using DebtFlow.SipAgent.Host;

namespace DebtFlow.SipAgent.Host.Tests;

public sealed class SingleInstanceCoordinatorTests
{
    [Fact]
    public async Task Primary_handles_legacy_activation_and_prepare_update_request()
    {
        await using SingleInstanceCoordinator primary = SingleInstanceCoordinator.Create();
        Assert.True(primary.IsPrimary);
        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.ActivationRequested += () => activated.TrySetResult();
        primary.QueryMaintenanceRequested += _ => Task.FromResult(
            new AgentInstanceResponse(AgentInstanceResponseStatus.Ready));
        primary.PrepareUpdateRequested += _ => Task.FromResult(
            new AgentInstanceResponse(AgentInstanceResponseStatus.ActiveCall));
        primary.StartActivationServer();

        await using (SingleInstanceCoordinator activationClient = SingleInstanceCoordinator.Create())
        {
            Assert.False(activationClient.IsPrimary);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await activationClient.SignalPrimaryAsync(timeout.Token);
            await activated.Task.WaitAsync(timeout.Token);
        }

        await using (SingleInstanceCoordinator queryClient = SingleInstanceCoordinator.Create())
        {
            Assert.False(queryClient.IsPrimary);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            AgentInstanceResponse response = await queryClient.RequestQueryMaintenanceAsync(timeout.Token);
            Assert.Equal(AgentInstanceResponseStatus.Ready, response.Status);
        }

        await using (SingleInstanceCoordinator updateClient = SingleInstanceCoordinator.Create())
        {
            Assert.False(updateClient.IsPrimary);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            AgentInstanceResponse response = await updateClient.RequestPrepareUpdateAsync(timeout.Token);
            Assert.Equal(AgentInstanceResponseStatus.ActiveCall, response.Status);
        }
    }

    [Fact]
    public async Task Prepare_update_without_handler_fails_closed()
    {
        await using SingleInstanceCoordinator primary = SingleInstanceCoordinator.Create();
        Assert.True(primary.IsPrimary);
        primary.StartActivationServer();

        await using SingleInstanceCoordinator client = SingleInstanceCoordinator.Create();
        Assert.False(client.IsPrimary);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        AgentInstanceResponse response = await client.RequestPrepareUpdateAsync(timeout.Token);

        Assert.Equal(AgentInstanceResponseStatus.ProtocolUnsupported, response.Status);
    }
}
