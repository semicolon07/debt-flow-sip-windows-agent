using DebtFlow.SipAgent.Host;

namespace DebtFlow.SipAgent.Host.Tests;

public sealed class AgentStorageTests
{
    [Fact]
    public async Task EventStoreFactory_WhenDatabaseCannotBeOpened_ReturnsFailClosedStore()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"sip-agent-storage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string blockingFile = Path.Combine(directory, "not-a-directory");
        await File.WriteAllTextAsync(blockingFile, "block");

        try
        {
            var (store, available, failureCode) = await AgentEventStoreFactory.CreateAsync(
                Path.Combine(blockingFile, "agent.db"),
                CancellationToken.None);
            await using (store)
            {
                Assert.False(available);
                Assert.Equal("outbox_unavailable", failureCode);
                await Assert.ThrowsAsync<DebtFlow.SipAgent.Application.AgentStoreException>(
                    () => store.AppendAsync(
                        new(
                            Guid.NewGuid().ToString("D"),
                            Guid.NewGuid().ToString("D"),
                            Guid.NewGuid().ToString("D"),
                            null,
                            "call.created",
                            DateTimeOffset.UtcNow,
                            "created",
                            "{}"),
                        CancellationToken.None));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
