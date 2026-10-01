using DebtFlow.SipAgent.Application;
using DebtFlow.SipAgent.Persistence;

namespace DebtFlow.SipAgent.Core.Tests;

public sealed class SqliteAgentEventStoreTests
{
    [Fact]
    public async Task Reopen_PreservesIdentitySequenceAndPendingReplay()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "agent.db");
        try
        {
            string instanceId;
            await using (var first = new SqliteAgentEventStore(path))
            {
                await first.InitializeAsync(CancellationToken.None);
                instanceId = first.AgentInstanceId;
                StoredDurableEvent stored = await first.AppendAsync(CreateDraft(), CancellationToken.None);
                Assert.Equal(1, stored.Sequence);
            }

            await using var reopened = new SqliteAgentEventStore(path);
            await reopened.InitializeAsync(CancellationToken.None);
            Assert.Equal(instanceId, reopened.AgentInstanceId);
            Assert.Equal(1, reopened.LastSequence);
            IReadOnlyList<StoredDurableEvent> pending = await reopened.LoadPendingAsync(0, 100, CancellationToken.None);
            Assert.Single(pending);
            Assert.Equal(1, pending[0].Sequence);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Acknowledge_PurgesContiguousEventsAndSurvivesReopen()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "agent.db");
        try
        {
            await using (var store = new SqliteAgentEventStore(path))
            {
                await store.InitializeAsync(CancellationToken.None);
                await store.AppendAsync(CreateDraft(), CancellationToken.None);
                await store.AppendAsync(CreateDraft(), CancellationToken.None);
                await store.AcknowledgeThroughAsync(2, CancellationToken.None);
                Assert.Equal(0, await store.CountPendingAsync(CancellationToken.None));
            }

            await using var reopened = new SqliteAgentEventStore(path);
            await reopened.InitializeAsync(CancellationToken.None);
            Assert.Equal(2, reopened.LastAcknowledgedSequence);
            StoredDurableEvent next = await reopened.AppendAsync(CreateDraft(), CancellationToken.None);
            Assert.Equal(3, next.Sequence);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ProcessedCommand_CanBeUpdatedWithoutCreatingDuplicate()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            await using var store = new SqliteAgentEventStore(Path.Combine(directory, "agent.db"));
            await store.InitializeAsync(CancellationToken.None);
            var provisional = new ProcessedCommand("command", "call.start", "hash", "unknown", DateTimeOffset.UtcNow);
            await store.SaveCommandAsync(provisional, CancellationToken.None);
            await store.SaveCommandAsync(provisional with { ResultJson = "accepted" }, CancellationToken.None);

            ProcessedCommand? result = await store.FindCommandAsync("command", CancellationToken.None);
            Assert.NotNull(result);
            Assert.Equal("accepted", result.ResultJson);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static DurableEventDraft CreateDraft() => new(
        Guid.NewGuid().ToString("D"),
        Guid.NewGuid().ToString("D"),
        Guid.NewGuid().ToString("D"),
        Guid.NewGuid().ToString("D"),
        "call.state_changed",
        DateTimeOffset.UtcNow,
        "ringing",
        "{}");

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"sip-agent-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
