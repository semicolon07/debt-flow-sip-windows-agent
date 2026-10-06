using DebtFlow.SipAgent.Application;
using DebtFlow.SipAgent.Persistence;
using DebtFlow.SipAgent.Protocol;
using System.Threading.Channels;

namespace DebtFlow.SipAgent.Core.Tests;

public sealed class AgentCoordinatorTests
{
    [Fact]
    public async Task StartCall_WithCollectionBinding_PersistsRoutingAndCallContext()
    {
        await using CoordinatorFixture fixture = await CoordinatorFixture.CreateAsync();
        await fixture.RegisterAsync();
        string callId = NewId();
        string callContextId = $"phonectx_{Guid.NewGuid():N}";

        await fixture.Coordinator.StartCallAsync(
            new CallStartCommand(NewId(), callId, "0812345678", CallContextId: callContextId),
            CancellationToken.None);

        IReadOnlyList<StoredDurableEvent> events = await fixture.Store.LoadPendingAsync(
            0,
            100,
            CancellationToken.None);
        StoredDurableEvent created = Assert.Single(events, value => value.EventType == "call.created");
        Assert.Equal("collection-test", created.CollectionId);
        Assert.StartsWith("phonebind_", created.CollectionBindingId, StringComparison.Ordinal);
        Assert.Equal(callContextId, created.CallContextId);
        Assert.Contains("\"remoteParty\":\"0812345678\"", created.DataJson,
            StringComparison.Ordinal);

        CallSessionState active = Assert.IsType<CallSessionState>(
            await fixture.Store.LoadActiveCallAsync(CancellationToken.None));
        Assert.Equal(created.CollectionBindingId, active.CollectionBindingId);
        Assert.Equal(callContextId, active.CallContextId);
        Assert.Equal("0812345678", active.RemoteParty);
    }

    [Fact]
    public async Task IncomingCall_WithoutCollectionBinding_IsRejectedWithoutDurableEvent()
    {
        await using CoordinatorFixture fixture = await CoordinatorFixture.CreateAsync();
        await fixture.Coordinator.ConfigureAsync(
            new ConfigureCommand(NewId(), "192.0.2.10", 5060, "user", "password"),
            CancellationToken.None);
        await fixture.Coordinator.StartRegistrationAsync(CancellationToken.None);
        await fixture.Runtime.EmitAsync(new SipSignal(SipSignalType.RegistrationRegistered));
        await fixture.Coordinator.PortalConnectedAsync(CancellationToken.None);

        await fixture.Runtime.EmitAsync(new SipSignal(SipSignalType.IncomingCall, Caller: "0812345678"));

        Assert.Equal(1, fixture.Runtime.RejectUnavailableCount);
        Assert.Empty((await fixture.Coordinator.GetSnapshotAsync(CancellationToken.None)).ActiveCalls);
        Assert.Equal(0, await fixture.Store.CountPendingAsync(CancellationToken.None));
    }

    [Fact]
    public async Task StartRegistration_WhenAlreadyRegistered_IsRejectedWithoutRestart()
    {
        await using CoordinatorFixture fixture = await CoordinatorFixture.CreateAsync();
        await fixture.RegisterAsync();

        AgentCommandException exception = await Assert.ThrowsAsync<AgentCommandException>(
            () => fixture.Coordinator.StartRegistrationAsync(CancellationToken.None));

        Assert.Equal("registration_unavailable", exception.ErrorCode);
        Assert.Equal(1, fixture.Runtime.StartRegistrationCount);
    }

    [Fact]
    public async Task AnswerOrReject_AfterInboundCallConnected_IsRejectedWithoutSipSideEffect()
    {
        await using CoordinatorFixture fixture = await CoordinatorFixture.CreateAsync();
        await fixture.RegisterAsync();
        await fixture.Coordinator.PortalConnectedAsync(CancellationToken.None);
        await fixture.Runtime.EmitAsync(new SipSignal(SipSignalType.IncomingCall, Caller: "0812345678"));
        string callId = Assert.Single(
            (await fixture.Coordinator.GetSnapshotAsync(CancellationToken.None)).ActiveCalls).CallId;
        await fixture.Runtime.EmitAsync(new SipSignal(SipSignalType.CallConnected));

        foreach (Func<Task> command in new Func<Task>[]
                 {
                     () => fixture.Coordinator.AnswerAsync(new CallCommand(NewId(), callId), CancellationToken.None),
                     () => fixture.Coordinator.RejectAsync(new CallCommand(NewId(), callId), CancellationToken.None)
                 })
        {
            AgentCommandException exception = await Assert.ThrowsAsync<AgentCommandException>(command);
            Assert.Equal("call_invalid_state", exception.ErrorCode);
        }

        Assert.Equal(0, fixture.Runtime.AnswerCount);
        Assert.Equal(0, fixture.Runtime.RejectCount);
    }

    [Fact]
    public async Task Answer_IncomingCall_InvokesSipRuntimeExactlyOnce()
    {
        await using CoordinatorFixture fixture = await CoordinatorFixture.CreateAsync();
        await fixture.RegisterAsync();
        await fixture.Coordinator.PortalConnectedAsync(CancellationToken.None);
        await fixture.Runtime.EmitAsync(new SipSignal(SipSignalType.IncomingCall, Caller: "0812345678"));
        string callId = Assert.Single(
            (await fixture.Coordinator.GetSnapshotAsync(CancellationToken.None)).ActiveCalls).CallId;

        await fixture.Coordinator.AnswerAsync(
            new CallCommand(NewId(), callId),
            CancellationToken.None);

        Assert.Equal(1, fixture.Runtime.AnswerCount);
        AgentSnapshotPayload snapshot = await fixture.Coordinator.GetSnapshotAsync(CancellationToken.None);
        Assert.Equal("answering", Assert.Single(snapshot.ActiveCalls).State);
        IReadOnlyList<StoredDurableEvent> events = await fixture.Store.LoadPendingAsync(0, 100, CancellationToken.None);
        StoredDurableEvent answering = Assert.Single(
            events,
            item => item.EventType == "call.state_changed" && item.State == "answering");
        Assert.Contains("inbound", answering.DataJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Answer_IncomingCall_CompletesWhenRuntimeEmitsConnectedBeforeReturning()
    {
        await using CoordinatorFixture fixture = await CoordinatorFixture.CreateAsync();
        await fixture.RegisterAsync();
        await fixture.Coordinator.PortalConnectedAsync(CancellationToken.None);
        await fixture.Runtime.EmitAsync(new SipSignal(SipSignalType.IncomingCall, Caller: "0812345678"));
        string callId = Assert.Single(
            (await fixture.Coordinator.GetSnapshotAsync(CancellationToken.None)).ActiveCalls).CallId;
        fixture.Runtime.AnswerBehavior = call =>
            fixture.Runtime.EmitAsync(new SipSignal(SipSignalType.CallConnected, Call: call));

        await fixture.Coordinator.AnswerAsync(
                new CallCommand(NewId(), callId),
                CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(1));

        AgentSnapshotPayload snapshot = await fixture.Coordinator.GetSnapshotAsync(CancellationToken.None);
        Assert.Equal("connected", Assert.Single(snapshot.ActiveCalls).State);
    }

    [Fact]
    public async Task StaleTerminalSignal_FromPreviousCall_DoesNotEndCurrentCall()
    {
        await using CoordinatorFixture fixture = await CoordinatorFixture.CreateAsync();
        await fixture.RegisterAsync();
        await fixture.Coordinator.PortalConnectedAsync(CancellationToken.None);
        await fixture.Runtime.EmitAsync(new SipSignal(SipSignalType.IncomingCall, Caller: "0812345678"));
        SipCallHandle firstCall = Assert.IsType<SipCallHandle>(fixture.Runtime.ActiveCall);
        await fixture.Runtime.EmitAsync(new SipSignal(SipSignalType.CallRemoteEnded, Call: firstCall));

        await fixture.Runtime.EmitAsync(new SipSignal(SipSignalType.IncomingCall, Caller: "0899999999"));
        SipCallHandle secondCall = Assert.IsType<SipCallHandle>(fixture.Runtime.ActiveCall);
        string currentCallId = Assert.Single(
            (await fixture.Coordinator.GetSnapshotAsync(CancellationToken.None)).ActiveCalls).CallId;

        await fixture.Runtime.EmitAsync(new SipSignal(
            SipSignalType.CallFailed,
            SafeCode: "stale_failure",
            Call: firstCall));

        AgentSnapshotPayload snapshot = await fixture.Coordinator.GetSnapshotAsync(CancellationToken.None);
        Assert.Equal(currentCallId, Assert.Single(snapshot.ActiveCalls).CallId);
        Assert.Equal("incoming", Assert.Single(snapshot.ActiveCalls).State);
        Assert.NotEqual(firstCall.RuntimeCallId, secondCall.RuntimeCallId);
    }

    [Fact]
    public async Task StartCall_RequestCancelledAfterDequeue_CompletesStateTransitionOnce()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"sip-agent-cancel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var store = new SqliteAgentEventStore(Path.Combine(directory, "agent.db"));
        await store.InitializeAsync(CancellationToken.None);
        var runtime = new FakeSipRuntime();
        var publisher = new BlockingPublisher();
        await using var coordinator = new AgentCoordinator(
            runtime,
            store,
            publisher,
            new FixedClock(),
            new GuidAgentIdGenerator());

        try
        {
            await coordinator.ConfigureAsync(
                new ConfigureCommand(NewId(), "192.0.2.10", 5060, "user", "password",
                    "collection-test", BindingId()),
                CancellationToken.None);
            await coordinator.StartRegistrationAsync(CancellationToken.None);
            await runtime.EmitAsync(new SipSignal(SipSignalType.RegistrationRegistered));
            publisher.BlockDurable = true;
            using var request = new CancellationTokenSource();

            Task command = coordinator.StartCallAsync(
                new CallStartCommand(NewId(), NewId(), "0812345678", "diagnostic-only"),
                request.Token);
            await publisher.WaitUntilBlockedAsync();
            request.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command);

            publisher.Release();
            await WaitUntilAsync(() => runtime.StartCallCount == 1);
            AgentSnapshotPayload snapshot = await coordinator.GetSnapshotAsync(CancellationToken.None);
            Assert.Single(snapshot.ActiveCalls);
            Assert.Equal(2, await store.CountPendingAsync(CancellationToken.None));
        }
        finally
        {
            publisher.Release();
            await coordinator.DisposeAsync();
            await store.DisposeAsync();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Configure_InvalidSipHostIp_IsRejectedBeforeRuntime()
    {
        await using CoordinatorFixture fixture = await CoordinatorFixture.CreateAsync();
        string[] invalidHosts = ["sip.example.test", "192.0.2.10:5060", "[2001:db8::10]", "fe80::1%12"];

        foreach (string host in invalidHosts)
        {
            AgentCommandException exception = await Assert.ThrowsAsync<AgentCommandException>(
                () => fixture.Coordinator.ConfigureAsync(
                    new ConfigureCommand(NewId(), host, 5060, "user", "password"),
                    CancellationToken.None));
            Assert.Equal("invalid_message", exception.ErrorCode);
        }

        Assert.Equal(0, fixture.Runtime.ConfigureCount);
    }

    [Fact]
    public async Task StartCall_BeforeRegistration_IsRejectedWithoutDurableEvent()
    {
        await using CoordinatorFixture fixture = await CoordinatorFixture.CreateAsync();
        var command = new CallStartCommand(NewId(), NewId(), "0812345678", "diagnostic-only");

        AgentCommandException exception = await Assert.ThrowsAsync<AgentCommandException>(
            () => fixture.Coordinator.StartCallAsync(command, CancellationToken.None));

        Assert.Equal("registration_unavailable", exception.ErrorCode);
        Assert.Equal(0, await fixture.Store.CountPendingAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DuplicateTerminalSignal_CreatesOneCallEndedEvent()
    {
        await using CoordinatorFixture fixture = await CoordinatorFixture.CreateAsync();
        await fixture.RegisterAsync();
        string callId = NewId();
        await fixture.Coordinator.StartCallAsync(
            new CallStartCommand(NewId(), callId, "0812345678", "diagnostic-only"),
            CancellationToken.None);
        await fixture.Runtime.EmitAsync(new SipSignal(SipSignalType.CallConnected, 200));
        await fixture.Coordinator.HangupAsync(new CallCommand(NewId(), callId), CancellationToken.None);
        await fixture.Runtime.EmitAsync(new SipSignal(SipSignalType.CallRemoteEnded));

        IReadOnlyList<StoredDurableEvent> events = await fixture.Store.LoadPendingAsync(0, 100, CancellationToken.None);
        StoredDurableEvent ended = Assert.Single(events, item => item.EventType == "call.ended");
        Assert.Contains("completed", ended.DataJson, StringComparison.Ordinal);
        Assert.Contains("0812345678",
            Assert.Single(events, item => item.EventType == "call.created").DataJson,
            StringComparison.Ordinal);
        Assert.DoesNotContain("0812345678",
            string.Join(string.Empty, events.Where(item => item.EventType != "call.created")
                .Select(item => item.DataJson)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DtmfEvent_DoesNotPersistDigit()
    {
        await using CoordinatorFixture fixture = await CoordinatorFixture.CreateAsync();
        await fixture.RegisterAsync();
        string callId = NewId();
        await fixture.Coordinator.StartCallAsync(
            new CallStartCommand(NewId(), callId, "0812345678", "diagnostic-only"),
            CancellationToken.None);
        await fixture.Runtime.EmitAsync(new SipSignal(SipSignalType.CallConnected, 200));

        await fixture.Coordinator.SendDtmfAsync(
            new DtmfCommand(NewId(), callId, "9"),
            CancellationToken.None);

        IReadOnlyList<StoredDurableEvent> events = await fixture.Store.LoadPendingAsync(0, 100, CancellationToken.None);
        StoredDurableEvent dtmf = events.Single(item => item.EventType == "call.dtmf_sent");
        Assert.DoesNotContain("9", dtmf.DataJson, StringComparison.Ordinal);
        Assert.Equal('9', fixture.Runtime.LastDtmf);
    }

    [Fact]
    public async Task AudioControls_UpdateRuntimeAndSnapshotWithoutDurableEvents()
    {
        await using CoordinatorFixture fixture = await CoordinatorFixture.CreateAsync();
        await fixture.RegisterAsync();
        string callId = NewId();
        await fixture.Coordinator.StartCallAsync(
            new CallStartCommand(NewId(), callId, "0812345678", "diagnostic-only"),
            CancellationToken.None);
        await fixture.Runtime.EmitAsync(new SipSignal(SipSignalType.CallConnected, 200));
        long eventsBeforeControls = await fixture.Store.CountPendingAsync(CancellationToken.None);

        await fixture.Coordinator.SetMicrophoneMutedAsync(
            new CallMuteCommand(NewId(), callId, true),
            CancellationToken.None);
        await fixture.Coordinator.SetOutputVolumeAsync(
            new CallVolumeCommand(NewId(), callId, 65),
            CancellationToken.None);
        await fixture.Coordinator.SetInputVolumeAsync(
            new CallVolumeCommand(NewId(), callId, 40),
            CancellationToken.None);

        AgentSnapshotPayload snapshot = await fixture.Coordinator.GetSnapshotAsync(CancellationToken.None);
        AudioControlsSnapshot controls = Assert.IsType<AudioControlsSnapshot>(snapshot.AudioControls);
        Assert.True(controls.MicrophoneMuted);
        Assert.Equal(65, controls.OutputVolume);
        Assert.Equal(40, controls.InputVolume);
        Assert.Equal(eventsBeforeControls, await fixture.Store.CountPendingAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AudioControls_RejectInvalidStateAndOutOfRangeVolume()
    {
        await using CoordinatorFixture fixture = await CoordinatorFixture.CreateAsync();
        await fixture.RegisterAsync();
        string callId = NewId();
        await fixture.Coordinator.StartCallAsync(
            new CallStartCommand(NewId(), callId, "0812345678", "diagnostic-only"),
            CancellationToken.None);

        AgentCommandException invalidState = await Assert.ThrowsAsync<AgentCommandException>(
            () => fixture.Coordinator.SetMicrophoneMutedAsync(
                new CallMuteCommand(NewId(), callId, true),
                CancellationToken.None));
        Assert.Equal("call_invalid_state", invalidState.ErrorCode);

        await fixture.Runtime.EmitAsync(new SipSignal(SipSignalType.CallConnected, 200));
        AgentCommandException invalidVolume = await Assert.ThrowsAsync<AgentCommandException>(
            () => fixture.Coordinator.SetOutputVolumeAsync(
                new CallVolumeCommand(NewId(), callId, 101),
                CancellationToken.None));
        Assert.Equal("invalid_message", invalidVolume.ErrorCode);
    }

    [Fact]
    public async Task ConcurrentTerminalSignals_AreSerializedToOneCallEndedEvent()
    {
        await using CoordinatorFixture fixture = await CoordinatorFixture.CreateAsync();
        await fixture.RegisterAsync();
        string callId = NewId();
        await fixture.Coordinator.StartCallAsync(
            new CallStartCommand(NewId(), callId, "0812345678", "diagnostic-only"),
            CancellationToken.None);

        await Task.WhenAll(
            fixture.Runtime.EmitAsync(new SipSignal(SipSignalType.CallFailed, 486, "sip_call_failed")),
            fixture.Runtime.EmitAsync(new SipSignal(SipSignalType.CallRemoteEnded)));

        IReadOnlyList<StoredDurableEvent> events = await fixture.Store.LoadPendingAsync(0, 100, CancellationToken.None);
        Assert.Single(events, item => item.EventType == "call.ended");
    }

    [Fact]
    public async Task DisconnectLeaseExpiry_UnregistersAndClearsConfiguration()
    {
        var delay = new ControlledDelay();
        await using CoordinatorFixture fixture = await CoordinatorFixture.CreateAsync(delay);
        await fixture.RegisterAsync();
        await fixture.Coordinator.PortalConnectedAsync(CancellationToken.None);
        await fixture.Coordinator.PortalDisconnectedAsync(CancellationToken.None);

        await delay.WaitUntilRequestedAsync();
        delay.Release();
        await WaitUntilAsync(() => fixture.Runtime.StopRegistrationCount == 1);

        AgentSnapshotPayload snapshot = await fixture.Coordinator.GetSnapshotAsync(CancellationToken.None);
        Assert.Equal("unconfigured", snapshot.RegistrationState);
    }

    [Fact]
    public async Task ReconnectBeforeLeaseExpiry_CancelsPendingCredentialCleanup()
    {
        var delay = new ControlledDelay();
        await using CoordinatorFixture fixture = await CoordinatorFixture.CreateAsync(delay);
        await fixture.RegisterAsync();
        await fixture.Coordinator.PortalConnectedAsync(CancellationToken.None);
        await fixture.Coordinator.PortalDisconnectedAsync(CancellationToken.None);
        await delay.WaitUntilRequestedAsync();
        await fixture.Coordinator.PortalConnectedAsync(CancellationToken.None);

        delay.Release();
        await fixture.Coordinator.WaitForCurrentOwnerLeaseExpirationAsync();

        Assert.Equal(0, fixture.Runtime.StopRegistrationCount);
        Assert.Equal("registered", (await fixture.Coordinator.GetSnapshotAsync(CancellationToken.None)).RegistrationState);
    }

    [Fact]
    public async Task DisconnectLeaseExpiry_DuringActiveCall_DefersCleanupUntilTerminalEvent()
    {
        var delay = new ControlledDelay();
        await using CoordinatorFixture fixture = await CoordinatorFixture.CreateAsync(delay);
        await fixture.RegisterAsync();
        await fixture.Coordinator.PortalConnectedAsync(CancellationToken.None);
        string callId = NewId();
        await fixture.Coordinator.StartCallAsync(
            new CallStartCommand(NewId(), callId, "0812345678", "diagnostic-only"),
            CancellationToken.None);
        await fixture.Runtime.EmitAsync(new SipSignal(SipSignalType.CallConnected, 200));
        await fixture.Coordinator.PortalDisconnectedAsync(CancellationToken.None);

        await delay.WaitUntilRequestedAsync();
        delay.Release();
        await fixture.Coordinator.WaitForCurrentOwnerLeaseExpirationAsync();

        Assert.Equal(0, fixture.Runtime.HangupCount);
        Assert.Equal(0, fixture.Runtime.StopRegistrationCount);
        Assert.Single((await fixture.Coordinator.GetSnapshotAsync(CancellationToken.None)).ActiveCalls);

        await fixture.Runtime.EmitAsync(new SipSignal(SipSignalType.CallRemoteEnded));

        Assert.Equal(0, fixture.Runtime.HangupCount);
        Assert.Equal(1, fixture.Runtime.StopRegistrationCount);
        AgentSnapshotPayload terminal = await fixture.Coordinator.GetSnapshotAsync(CancellationToken.None);
        Assert.Empty(terminal.ActiveCalls);
        Assert.Equal("unconfigured", terminal.RegistrationState);
        IReadOnlyList<StoredDurableEvent> events = await fixture.Store.LoadPendingAsync(0, 100, CancellationToken.None);
        Assert.Single(events, item => item.EventType == "call.ended");
    }

    [Fact]
    public async Task ReconnectAfterLeaseExpiry_DuringActiveCall_CancelsDeferredCleanup()
    {
        var delay = new ControlledDelay();
        await using CoordinatorFixture fixture = await CoordinatorFixture.CreateAsync(delay);
        await fixture.RegisterAsync();
        await fixture.Coordinator.PortalConnectedAsync(CancellationToken.None);
        string callId = NewId();
        await fixture.Coordinator.StartCallAsync(
            new CallStartCommand(NewId(), callId, "0812345678", "diagnostic-only"),
            CancellationToken.None);
        await fixture.Runtime.EmitAsync(new SipSignal(SipSignalType.CallConnected, 200));
        await fixture.Coordinator.PortalDisconnectedAsync(CancellationToken.None);

        await delay.WaitUntilRequestedAsync();
        delay.Release();
        await fixture.Coordinator.WaitForCurrentOwnerLeaseExpirationAsync();
        await fixture.Coordinator.PortalConnectedAsync(CancellationToken.None);
        Assert.Single((await fixture.Coordinator.GetSnapshotAsync(CancellationToken.None)).ActiveCalls);

        await fixture.Runtime.EmitAsync(new SipSignal(SipSignalType.CallRemoteEnded));

        Assert.Equal(0, fixture.Runtime.HangupCount);
        Assert.Equal(0, fixture.Runtime.StopRegistrationCount);
        AgentSnapshotPayload terminal = await fixture.Coordinator.GetSnapshotAsync(CancellationToken.None);
        Assert.Empty(terminal.ActiveCalls);
        Assert.Equal("registered", terminal.RegistrationState);
        IReadOnlyList<StoredDurableEvent> events = await fixture.Store.LoadPendingAsync(0, 100, CancellationToken.None);
        Assert.Single(events, item => item.EventType == "call.ended");
    }

    [Fact]
    public async Task IncomingCallWithoutPortal_IsRejectedAndDurablyEnded()
    {
        await using CoordinatorFixture fixture = await CoordinatorFixture.CreateAsync();
        await fixture.RegisterAsync();

        await fixture.Runtime.EmitAsync(new SipSignal(SipSignalType.IncomingCall, Caller: "0812345678"));

        Assert.Equal(1, fixture.Runtime.RejectUnavailableCount);
        IReadOnlyList<StoredDurableEvent> events = await fixture.Store.LoadPendingAsync(0, 100, CancellationToken.None);
        StoredDurableEvent ended = Assert.Single(events, item => item.EventType == "call.ended");
        Assert.Contains("portal_unavailable", ended.DataJson, StringComparison.Ordinal);
        Assert.Contains("0812345678",
            Assert.Single(events, item => item.EventType == "call.created").DataJson,
            StringComparison.Ordinal);
        Assert.DoesNotContain("0812345678",
            string.Join(string.Empty, events.Where(item => item.EventType != "call.created")
                .Select(item => item.DataJson)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShutdownWithActiveCall_HangsUpAndProducesOneTerminalEvent()
    {
        await using CoordinatorFixture fixture = await CoordinatorFixture.CreateAsync();
        await fixture.RegisterAsync();
        string callId = NewId();
        await fixture.Coordinator.StartCallAsync(
            new CallStartCommand(NewId(), callId, "0812345678", "diagnostic-only"),
            CancellationToken.None);
        await fixture.Runtime.EmitAsync(new SipSignal(SipSignalType.CallConnected, 200));

        await fixture.Coordinator.ShutdownAsync(CancellationToken.None);

        Assert.Equal(1, fixture.Runtime.HangupCount);
        Assert.Equal(1, fixture.Runtime.StopRegistrationCount);
        IReadOnlyList<StoredDurableEvent> events = await fixture.Store.LoadPendingAsync(0, 100, CancellationToken.None);
        Assert.Single(events, item => item.EventType == "call.ended");
    }

    [Fact]
    public async Task OutboxFailureBeforeOutboundCall_DegradesWithoutStartingSipOrLeavingActiveCall()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"sip-agent-outbox-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var innerStore = new SqliteAgentEventStore(Path.Combine(directory, "agent.db"));
        await innerStore.InitializeAsync(CancellationToken.None);
        var store = new FailingAppendEventStore(innerStore);
        var runtime = new FakeSipRuntime();
        await using var coordinator = new AgentCoordinator(
            runtime,
            store,
            new NullPublisher(),
            new FixedClock(),
            new GuidAgentIdGenerator());

        try
        {
            await coordinator.ConfigureAsync(
                new ConfigureCommand(NewId(), "192.0.2.10", 5060, "user", "password",
                    "collection-test", BindingId()),
                CancellationToken.None);
            await coordinator.StartRegistrationAsync(CancellationToken.None);
            await runtime.EmitAsync(new SipSignal(SipSignalType.RegistrationRegistered));
            store.FailAppends = true;

            AgentCommandException exception = await Assert.ThrowsAsync<AgentCommandException>(
                () => coordinator.StartCallAsync(
                    new CallStartCommand(NewId(), NewId(), "0812345678", "diagnostic-only"),
                    CancellationToken.None));

            AgentSnapshotPayload snapshot = await coordinator.GetSnapshotAsync(CancellationToken.None);
            Assert.Equal("outbox_unavailable", exception.ErrorCode);
            Assert.Equal("degraded", snapshot.AgentState);
            Assert.Empty(snapshot.ActiveCalls);
            Assert.Equal(0, runtime.StartCallCount);
        }
        finally
        {
            await coordinator.DisposeAsync();
            await store.DisposeAsync();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ShutdownWithActiveCall_WhenOutboxFails_StillHangsUpAndUnregisters()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"sip-agent-shutdown-outbox-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var innerStore = new SqliteAgentEventStore(Path.Combine(directory, "agent.db"));
        await innerStore.InitializeAsync(CancellationToken.None);
        var store = new FailingAppendEventStore(innerStore);
        var runtime = new FakeSipRuntime();
        await using var coordinator = new AgentCoordinator(
            runtime,
            store,
            new NullPublisher(),
            new FixedClock(),
            new GuidAgentIdGenerator());

        try
        {
            await coordinator.ConfigureAsync(
                new ConfigureCommand(NewId(), "192.0.2.10", 5060, "user", "password"),
                CancellationToken.None);
            await coordinator.StartRegistrationAsync(CancellationToken.None);
            await runtime.EmitAsync(new SipSignal(SipSignalType.RegistrationRegistered));
            await coordinator.StartCallAsync(
                new CallStartCommand(NewId(), NewId(), "0812345678", "diagnostic-only"),
                CancellationToken.None);
            await runtime.EmitAsync(new SipSignal(SipSignalType.CallConnected));
            store.FailAppends = true;

            await coordinator.ShutdownAsync(CancellationToken.None);

            Assert.Equal(1, runtime.HangupCount);
            Assert.Equal(1, runtime.StopRegistrationCount);
        }
        finally
        {
            await coordinator.DisposeAsync();
            await store.DisposeAsync();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task OutboxFailureOnDialTransition_DoesNotStartSipOrLeaveActiveCall()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"sip-agent-dial-outbox-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var innerStore = new SqliteAgentEventStore(Path.Combine(directory, "agent.db"));
        await innerStore.InitializeAsync(CancellationToken.None);
        var store = new FailingAppendEventStore(innerStore) { FailOnAppendAttempt = 2 };
        var runtime = new FakeSipRuntime();
        await using var coordinator = new AgentCoordinator(
            runtime,
            store,
            new NullPublisher(),
            new FixedClock(),
            new GuidAgentIdGenerator());

        try
        {
            await coordinator.ConfigureAsync(
                new ConfigureCommand(NewId(), "192.0.2.10", 5060, "user", "password"),
                CancellationToken.None);
            await coordinator.StartRegistrationAsync(CancellationToken.None);
            await runtime.EmitAsync(new SipSignal(SipSignalType.RegistrationRegistered));

            AgentCommandException exception = await Assert.ThrowsAsync<AgentCommandException>(
                () => coordinator.StartCallAsync(
                    new CallStartCommand(NewId(), NewId(), "0812345678", "diagnostic-only"),
                    CancellationToken.None));

            AgentSnapshotPayload snapshot = await coordinator.GetSnapshotAsync(CancellationToken.None);
            Assert.Equal("outbox_unavailable", exception.ErrorCode);
            Assert.Equal("degraded", snapshot.AgentState);
            Assert.Empty(snapshot.ActiveCalls);
            Assert.Equal(0, runtime.StartCallCount);
        }
        finally
        {
            await coordinator.DisposeAsync();
            await store.DisposeAsync();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task OutboxFailureOnIncomingTransition_RejectsUnavailableAndClearsActiveCall()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"sip-agent-incoming-outbox-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var innerStore = new SqliteAgentEventStore(Path.Combine(directory, "agent.db"));
        await innerStore.InitializeAsync(CancellationToken.None);
        var store = new FailingAppendEventStore(innerStore) { FailOnAppendAttempt = 2 };
        var runtime = new FakeSipRuntime();
        await using var coordinator = new AgentCoordinator(
            runtime,
            store,
            new NullPublisher(),
            new FixedClock(),
            new GuidAgentIdGenerator());

        try
        {
            await coordinator.ConfigureAsync(
                new ConfigureCommand(NewId(), "192.0.2.10", 5060, "user", "password",
                    "collection-test", BindingId()),
                CancellationToken.None);
            await coordinator.StartRegistrationAsync(CancellationToken.None);
            await runtime.EmitAsync(new SipSignal(SipSignalType.RegistrationRegistered));
            await coordinator.PortalConnectedAsync(CancellationToken.None);

            await runtime.EmitAsync(new SipSignal(SipSignalType.IncomingCall, Caller: "0812345678"));

            AgentSnapshotPayload snapshot = await coordinator.GetSnapshotAsync(CancellationToken.None);
            Assert.Equal("degraded", snapshot.AgentState);
            Assert.Empty(snapshot.ActiveCalls);
            Assert.Equal(1, runtime.RejectUnavailableCount);
        }
        finally
        {
            await coordinator.DisposeAsync();
            await store.DisposeAsync();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Configure_WhenAgentStartsDegraded_DoesNotPassCredentialToSipRuntime()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"sip-agent-degraded-configure-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var store = new SqliteAgentEventStore(Path.Combine(directory, "agent.db"));
        await store.InitializeAsync(CancellationToken.None);
        var runtime = new FakeSipRuntime();
        await using var coordinator = new AgentCoordinator(
            runtime,
            store,
            new NullPublisher(),
            new FixedClock(),
            new GuidAgentIdGenerator(),
            durableStoreAvailable: false);

        try
        {
            AgentCommandException exception = await Assert.ThrowsAsync<AgentCommandException>(
                () => coordinator.ConfigureAsync(
                    new ConfigureCommand(NewId(), "192.0.2.10", 5060, "user", "password"),
                    CancellationToken.None));

            Assert.Equal("outbox_unavailable", exception.ErrorCode);
            Assert.Equal(0, runtime.ConfigureCount);
        }
        finally
        {
            await coordinator.DisposeAsync();
            await store.DisposeAsync();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Configure_WhenDurableStoreHasSpecificFailure_PreservesSafeFailureCode()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"sip-agent-corrupt-configure-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var store = new SqliteAgentEventStore(Path.Combine(directory, "agent.db"));
        await store.InitializeAsync(CancellationToken.None);
        var runtime = new FakeSipRuntime();
        await using var coordinator = new AgentCoordinator(
            runtime,
            store,
            new NullPublisher(),
            new FixedClock(),
            new GuidAgentIdGenerator(),
            durableStoreAvailable: false,
            initialDegradedCode: "outbox_corrupt");

        try
        {
            AgentCommandException exception = await Assert.ThrowsAsync<AgentCommandException>(
                () => coordinator.ConfigureAsync(
                    new ConfigureCommand(NewId(), "192.0.2.10", 5060, "user", "password"),
                    CancellationToken.None));

            Assert.Equal("outbox_corrupt", exception.ErrorCode);
            Assert.Equal(0, runtime.ConfigureCount);
            Assert.Equal(
                "outbox_corrupt",
                (await coordinator.GetSnapshotAsync(CancellationToken.None)).AgentStateCode);
        }
        finally
        {
            await coordinator.DisposeAsync();
            await store.DisposeAsync();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task RegistrationRetry_RunsSingleSerializedAttemptAfterRetryableFailure()
    {
        var retryDelay = new ControlledDelay();
        var retryPolicy = new FixedRetryPolicy(TimeSpan.FromSeconds(7));
        await using CoordinatorFixture fixture = await CoordinatorFixture.CreateAsync(
            retryDelay: retryDelay,
            retryPolicy: retryPolicy);
        await fixture.Coordinator.ConfigureAsync(
            new ConfigureCommand(NewId(), "192.0.2.10", 5060, "user", "password"),
            CancellationToken.None);
        await fixture.Coordinator.StartRegistrationAsync(CancellationToken.None);

        await fixture.Runtime.EmitAsync(new SipSignal(
            SipSignalType.RegistrationFailed,
            SafeCode: "registration_transport_failure",
            Retryable: true));

        Assert.Equal("retrying", (await fixture.Coordinator.GetSnapshotAsync(CancellationToken.None)).RegistrationState);
        Assert.Equal(TimeSpan.FromSeconds(7), await retryDelay.WaitUntilRequestedAsync());
        Assert.Equal([1], retryPolicy.Attempts);
        retryDelay.Release();
        await WaitUntilAsync(() => fixture.Runtime.StartRegistrationCount == 2);
        Assert.Equal("registering", (await fixture.Coordinator.GetSnapshotAsync(CancellationToken.None)).RegistrationState);
    }

    [Fact]
    public async Task RegistrationRetry_IsInvalidatedByReconfigure()
    {
        var retryDelay = new ControlledDelay();
        await using CoordinatorFixture fixture = await CoordinatorFixture.CreateAsync(
            retryDelay: retryDelay,
            retryPolicy: new FixedRetryPolicy(TimeSpan.FromSeconds(2)));
        await fixture.Coordinator.ConfigureAsync(
            new ConfigureCommand(NewId(), "192.0.2.10", 5060, "user", "password"),
            CancellationToken.None);
        await fixture.Coordinator.StartRegistrationAsync(CancellationToken.None);
        await fixture.Runtime.EmitAsync(new SipSignal(
            SipSignalType.RegistrationFailed,
            SafeCode: "registration_transport_failure",
            Retryable: true));
        await retryDelay.WaitUntilRequestedAsync();

        await fixture.Coordinator.ConfigureAsync(
            new ConfigureCommand(NewId(), "192.0.2.11", 5060, "user2", "password2"),
            CancellationToken.None);
        retryDelay.Release();
        await Task.Delay(25);

        Assert.Equal(1, fixture.Runtime.StartRegistrationCount);
        Assert.Equal("unregistered", (await fixture.Coordinator.GetSnapshotAsync(CancellationToken.None)).RegistrationState);
    }

    [Fact]
    public async Task Initialize_ReconcilesRecoveredCallExactlyOnce()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"sip-agent-recovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var store = new SqliteAgentEventStore(Path.Combine(directory, "agent.db"));
        await store.InitializeAsync(CancellationToken.None);
        string callId = NewId();
        var active = new CallSessionState(
            callId,
            NewId(),
            CallDirection.Outbound,
            CallState.Ringing,
            new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
            null,
            null,
            null,
            null,
            "***5678");
        await store.AppendCallEventAsync(
            new DurableEventDraft(NewId(), NewId(), callId, active.CommandId, "call.created", active.StartedAtUtc, "ringing", "{}"),
            active,
            false,
            CancellationToken.None);

        try
        {
            await using (var first = new AgentCoordinator(
                             new FakeSipRuntime(),
                             store,
                             new NullPublisher(),
                             new FixedClock(),
                             new GuidAgentIdGenerator()))
            {
                await first.InitializeAsync(CancellationToken.None);
            }

            Assert.Null(await store.LoadActiveCallAsync(CancellationToken.None));
            IReadOnlyList<StoredDurableEvent> afterFirst = await store.LoadPendingAsync(0, 100, CancellationToken.None);
            Assert.Single(afterFirst, item => item.EventType == "call.ended");
            Assert.Contains("agent_restarted", afterFirst.Single(item => item.EventType == "call.ended").DataJson);

            await using (var second = new AgentCoordinator(
                             new FakeSipRuntime(),
                             store,
                             new NullPublisher(),
                             new FixedClock(),
                             new GuidAgentIdGenerator()))
            {
                await second.InitializeAsync(CancellationToken.None);
            }

            IReadOnlyList<StoredDurableEvent> afterSecond = await store.LoadPendingAsync(0, 100, CancellationToken.None);
            Assert.Single(afterSecond, item => item.EventType == "call.ended");
        }
        finally
        {
            await store.DisposeAsync();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task StartCall_WhenOutboxBecomesCritical_IsBlockedBeforeSipSideEffect()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"sip-agent-capacity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var store = new SqliteAgentEventStore(
            Path.Combine(directory, "agent.db"),
            new EventStoreLimits(10, 1024L * 1024 * 1024));
        await store.InitializeAsync(CancellationToken.None);
        for (int index = 0; index < 8; index++)
        {
            await store.AppendAsync(
                new DurableEventDraft(NewId(), NewId(), NewId(), null, "test.event", DateTimeOffset.UtcNow, null, "{}"),
                CancellationToken.None);
        }

        var runtime = new FakeSipRuntime();
        await using var coordinator = new AgentCoordinator(
            runtime,
            store,
            new NullPublisher(),
            new FixedClock(),
            new GuidAgentIdGenerator());
        try
        {
            await coordinator.ConfigureAsync(
                new ConfigureCommand(NewId(), "192.0.2.10", 5060, "user", "password"),
                CancellationToken.None);
            await coordinator.StartRegistrationAsync(CancellationToken.None);
            await runtime.EmitAsync(new SipSignal(SipSignalType.RegistrationRegistered));
            await store.AppendAsync(
                new DurableEventDraft(NewId(), NewId(), NewId(), null, "test.event", DateTimeOffset.UtcNow, null, "{}"),
                CancellationToken.None);

            AgentCommandException exception = await Assert.ThrowsAsync<AgentCommandException>(
                () => coordinator.StartCallAsync(
                    new CallStartCommand(NewId(), NewId(), "1001", "context"),
                    CancellationToken.None));
            Assert.Equal("outbox_capacity_critical", exception.ErrorCode);
            Assert.Equal(0, runtime.StartCallCount);
        }
        finally
        {
            await coordinator.DisposeAsync();
            await store.DisposeAsync();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task FakeLifecycle_OneThousandSequentialCalls_HasOneTerminalEventPerCall()
    {
        await using CoordinatorFixture fixture = await CoordinatorFixture.CreateAsync();
        await fixture.RegisterAsync();

        for (int index = 0; index < 1_000; index++)
        {
            await fixture.Coordinator.StartCallAsync(
                new CallStartCommand(NewId(), NewId(), "1001", "soak"),
                CancellationToken.None);
            await fixture.Runtime.EmitAsync(new SipSignal(SipSignalType.CallConnected, 200));
            await fixture.Runtime.EmitAsync(new SipSignal(SipSignalType.CallRemoteEnded));
            await fixture.Runtime.EmitAsync(new SipSignal(SipSignalType.CallRemoteEnded));
        }

        await WaitUntilAsync(() => fixture.Runtime.StartCallCount == 1_000);
        Assert.Equal(5_000, await fixture.Store.CountPendingAsync(CancellationToken.None));
        int terminalCount = 0;
        long afterSequence = 0;
        while (true)
        {
            IReadOnlyList<StoredDurableEvent> batch = await fixture.Store.LoadPendingAsync(
                afterSequence,
                1_000,
                CancellationToken.None);
            if (batch.Count == 0)
            {
                break;
            }

            terminalCount += batch.Count(item => item.EventType == "call.ended");
            afterSequence = batch[^1].Sequence;
        }

        Assert.Equal(1_000, terminalCount);
        Assert.Empty((await fixture.Coordinator.GetSnapshotAsync(CancellationToken.None)).ActiveCalls);
    }

    private static string NewId() => Guid.NewGuid().ToString("D").ToLowerInvariant();

    private sealed class CoordinatorFixture : IAsyncDisposable
    {
        private readonly string _directory;

        private CoordinatorFixture(
            string directory,
            SqliteAgentEventStore store,
            FakeSipRuntime runtime,
            AgentCoordinator coordinator)
        {
            _directory = directory;
            Store = store;
            Runtime = runtime;
            Coordinator = coordinator;
        }

        public SqliteAgentEventStore Store { get; }
        public FakeSipRuntime Runtime { get; }
        public AgentCoordinator Coordinator { get; }

        public static async Task<CoordinatorFixture> CreateAsync(
            IAgentDelay? delay = null,
            IAgentDelay? retryDelay = null,
            IRegistrationRetryPolicy? retryPolicy = null)
        {
            string directory = Path.Combine(Path.GetTempPath(), $"sip-agent-coordinator-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var store = new SqliteAgentEventStore(Path.Combine(directory, "agent.db"));
            await store.InitializeAsync(CancellationToken.None);
            var runtime = new FakeSipRuntime();
            var coordinator = new AgentCoordinator(
                runtime,
                store,
                new NullPublisher(),
                new FixedClock(),
                new GuidAgentIdGenerator(),
                delay: delay,
                ownerDisconnectGrace: TimeSpan.FromSeconds(60),
                retryDelay: retryDelay,
                retryPolicy: retryPolicy);
            return new CoordinatorFixture(directory, store, runtime, coordinator);
        }

        public async Task RegisterAsync()
        {
            await Coordinator.ConfigureAsync(
                new ConfigureCommand(NewId(), "192.0.2.10", 5060, "user", "password",
                    "collection-test", BindingId()),
                CancellationToken.None);
            await Coordinator.StartRegistrationAsync(CancellationToken.None);
            await Runtime.EmitAsync(new SipSignal(SipSignalType.RegistrationRegistered));
        }

        public async ValueTask DisposeAsync()
        {
            await Coordinator.DisposeAsync();
            await Store.DisposeAsync();
            Directory.Delete(_directory, true);
        }
    }

    private static string BindingId() => $"phonebind_{Guid.NewGuid():N}";

    private sealed class FakeSipRuntime : ISipRuntime
    {
        private readonly Channel<SipSignal> _signals = Channel.CreateUnbounded<SipSignal>();
        private SipCallHandle? _activeCall;
        private long _generation;
        public ChannelReader<SipSignal> Signals => _signals.Reader;
        public SipCallHandle? ActiveCall => _activeCall;
        public Func<SipCallHandle, Task>? AnswerBehavior { get; set; }
        public string AudioState => "ready";
        public bool IsMicrophoneMuted { get; private set; }
        public int OutputVolume { get; private set; } = 100;
        public int InputVolume { get; private set; } = 100;
        public char? LastDtmf { get; private set; }
        public int StopRegistrationCount { get; private set; }
        public int RejectUnavailableCount { get; private set; }
        public int HangupCount { get; private set; }
        public int StartCallCount { get; private set; }
        public int ConfigureCount { get; private set; }
        public int StartRegistrationCount { get; private set; }
        public int AnswerCount { get; private set; }
        public int RejectCount { get; private set; }

        public async Task EmitAsync(SipSignal signal)
        {
            if (signal.Type == SipSignalType.IncomingCall && signal.Call == null)
            {
                _activeCall = new SipCallHandle(NewId(), ++_generation);
                signal = signal with { Call = _activeCall };
            }
            else if (signal.Call == null && signal.Type is
                     SipSignalType.CallTrying or
                     SipSignalType.CallRinging or
                     SipSignalType.CallConnected or
                     SipSignalType.CallFailed or
                     SipSignalType.CallRemoteEnded or
                     SipSignalType.IncomingCancelled or
                     SipSignalType.MediaReady or
                     SipSignalType.MediaDegraded or
                     SipSignalType.DtmfReceived)
            {
                signal = signal with { Call = _activeCall };
            }
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await _signals.Writer.WriteAsync(signal with { ProcessingCompletion = completion });
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        public Task ConfigureAsync(SipConfiguration configuration, CancellationToken cancellationToken)
        {
            ConfigureCount++;
            return Task.CompletedTask;
        }
        public Task StartRegistrationAsync(long generation, CancellationToken cancellationToken)
        {
            StartRegistrationCount++;
            return Task.CompletedTask;
        }
        public Task StopRegistrationAsync(long generation, CancellationToken cancellationToken)
        {
            StopRegistrationCount++;
            return Task.CompletedTask;
        }
        public Task StartCallAsync(
            SipCallHandle call,
            string destination,
            CancellationToken cancellationToken)
        {
            StartCallCount++;
            _activeCall = call;
            return Task.CompletedTask;
        }
        public Task AnswerAsync(SipCallHandle call, CancellationToken cancellationToken)
        {
            AnswerCount++;
            return AnswerBehavior?.Invoke(call) ?? Task.CompletedTask;
        }

        public Task RejectAsync(SipCallHandle call, CancellationToken cancellationToken)
        {
            RejectCount++;
            return Task.CompletedTask;
        }
        public Task RejectUnavailableAsync(SipCallHandle call, CancellationToken cancellationToken)
        {
            RejectUnavailableCount++;
            return Task.CompletedTask;
        }

        public Task HangupAsync(SipCallHandle call, CancellationToken cancellationToken)
        {
            HangupCount++;
            return Task.CompletedTask;
        }
        public Task SendDtmfAsync(SipCallHandle call, char digit, CancellationToken cancellationToken)
        {
            LastDtmf = digit;
            return Task.CompletedTask;
        }

        public Task SetMicrophoneMutedAsync(
            SipCallHandle call,
            bool muted,
            CancellationToken cancellationToken)
        {
            IsMicrophoneMuted = muted;
            return Task.CompletedTask;
        }

        public Task SetOutputVolumeAsync(
            SipCallHandle call,
            int volume,
            CancellationToken cancellationToken)
        {
            OutputVolume = volume;
            return Task.CompletedTask;
        }

        public Task SetInputVolumeAsync(
            SipCallHandle call,
            int volume,
            CancellationToken cancellationToken)
        {
            InputVolume = volume;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _signals.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingAppendEventStore(IAgentEventStore inner) : IAgentEventStore
    {
        private int _appendAttempts;
        public bool FailAppends { get; set; }
        public int? FailOnAppendAttempt { get; init; }
        public string AgentInstanceId => inner.AgentInstanceId;
        public long LastSequence => inner.LastSequence;
        public long LastAcknowledgedSequence => inner.LastAcknowledgedSequence;
        public Task InitializeAsync(CancellationToken cancellationToken) => inner.InitializeAsync(cancellationToken);

        public Task<StoredDurableEvent> AppendAsync(
            DurableEventDraft draft,
            CancellationToken cancellationToken)
        {
            int attempt = Interlocked.Increment(ref _appendAttempts);
            return FailAppends || attempt == FailOnAppendAttempt
                ? Task.FromException<StoredDurableEvent>(new IOException("simulated_outbox_failure"))
                : inner.AppendAsync(draft, cancellationToken);
        }

        public Task<StoredDurableEvent> AppendCallEventAsync(
            DurableEventDraft draft,
            CallSessionState callState,
            bool terminal,
            CancellationToken cancellationToken)
        {
            int attempt = Interlocked.Increment(ref _appendAttempts);
            return FailAppends || attempt == FailOnAppendAttempt
                ? Task.FromException<StoredDurableEvent>(new IOException("simulated_outbox_failure"))
                : inner.AppendCallEventAsync(draft, callState, terminal, cancellationToken);
        }

        public Task<IReadOnlyList<StoredDurableEvent>> LoadPendingAsync(
            long afterSequence,
            int maximumCount,
            CancellationToken cancellationToken) =>
            inner.LoadPendingAsync(afterSequence, maximumCount, cancellationToken);

        public Task<long> CountPendingAsync(CancellationToken cancellationToken) =>
            inner.CountPendingAsync(cancellationToken);

        public Task<EventStoreHealth> GetHealthAsync(CancellationToken cancellationToken) =>
            inner.GetHealthAsync(cancellationToken);

        public Task<CallSessionState?> LoadActiveCallAsync(CancellationToken cancellationToken) =>
            inner.LoadActiveCallAsync(cancellationToken);

        public Task AcknowledgeThroughAsync(long sequence, CancellationToken cancellationToken) =>
            inner.AcknowledgeThroughAsync(sequence, cancellationToken);

        public Task<ProcessedCommand?> FindCommandAsync(string commandId, CancellationToken cancellationToken) =>
            inner.FindCommandAsync(commandId, cancellationToken);

        public Task<bool> HasCommandRequestHashPrefixAsync(
            string prefix,
            CancellationToken cancellationToken) =>
            inner.HasCommandRequestHashPrefixAsync(prefix, cancellationToken);

        public Task SaveCommandAsync(ProcessedCommand command, CancellationToken cancellationToken) =>
            inner.SaveCommandAsync(command, cancellationToken);

        public Task RecoverExecutingCommandsAsync(
            DateTimeOffset recoveredAtUtc,
            CancellationToken cancellationToken) =>
            inner.RecoverExecutingCommandsAsync(recoveredAtUtc, cancellationToken);

        public Task PruneCommandsAsync(
            DateTimeOffset olderThanUtc,
            int maximumRetained,
            CancellationToken cancellationToken) =>
            inner.PruneCommandsAsync(olderThanUtc, maximumRetained, cancellationToken);

        public Task CheckpointAsync(CancellationToken cancellationToken) =>
            inner.CheckpointAsync(cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class NullPublisher : IAgentEventPublisher
    {
        public Task PublishDurableAsync(StoredDurableEvent storedEvent, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task PublishRealtimeAsync(
            string eventType,
            RealtimeEventPayload payload,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class BlockingPublisher : IAgentEventPublisher
    {
        private readonly TaskCompletionSource _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool BlockDurable { get; set; }

        public async Task PublishDurableAsync(
            StoredDurableEvent storedEvent,
            CancellationToken cancellationToken)
        {
            if (!BlockDurable)
            {
                return;
            }

            _blocked.TrySetResult();
            await _released.Task.WaitAsync(cancellationToken);
        }

        public Task PublishRealtimeAsync(
            string eventType,
            RealtimeEventPayload payload,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task WaitUntilBlockedAsync() => _blocked.Task;
        public void Release() => _released.TrySetResult();
    }

    private sealed class FixedClock : IAgentClock
    {
        private long _ticks;
        public DateTimeOffset UtcNow =>
            new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero)
                .AddSeconds(Interlocked.Increment(ref _ticks));
    }

    private sealed class ControlledDelay : IAgentDelay
    {
        private readonly TaskCompletionSource<TimeSpan> _requested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            _requested.TrySetResult(delay);
            await _released.Task.WaitAsync(cancellationToken);
        }

        public Task<TimeSpan> WaitUntilRequestedAsync() => _requested.Task;
        public void Release() => _released.TrySetResult();
    }

    private sealed class FixedRetryPolicy(TimeSpan delay) : IRegistrationRetryPolicy
    {
        public List<int> Attempts { get; } = [];

        public TimeSpan GetDelay(int attempt)
        {
            Attempts.Add(attempt);
            return delay;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(5, timeout.Token);
        }
    }
}
