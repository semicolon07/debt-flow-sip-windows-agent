namespace DebtFlow.SipAgent.Application;

/// <summary>
/// Owns fire-and-forget work so faults are always observed and shutdown can drain it.
/// Operation names must be low-cardinality constants because they are used as metric tags.
/// </summary>
public sealed class BackgroundTaskSupervisor
{
    private readonly object _sync = new();
    private readonly HashSet<Task> _tasks = [];
    private long _faultCount;
    private string? _lastFaultOperation;

    public int ActiveCount
    {
        get
        {
            lock (_sync)
            {
                return _tasks.Count;
            }
        }
    }

    public long FaultCount => Volatile.Read(ref _faultCount);
    public string? LastFaultOperation => Volatile.Read(ref _lastFaultOperation);

    public void Track(string operation, Task task)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(task);
        lock (_sync)
        {
            _tasks.Add(task);
        }

        _ = task.ContinueWith(
            completed =>
            {
                lock (_sync)
                {
                    _tasks.Remove(completed);
                }

                if (completed.IsFaulted)
                {
                    _ = completed.Exception;
                    Interlocked.Increment(ref _faultCount);
                    Volatile.Write(ref _lastFaultOperation, operation);
                    AgentPerformanceTelemetry.RecordBackgroundTaskFault(operation);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public async Task DrainAsync(TimeSpan timeout)
    {
        Task[] snapshot;
        lock (_sync)
        {
            snapshot = [.. _tasks];
        }

        if (snapshot.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(snapshot).WaitAsync(timeout);
        }
        catch (Exception exception) when (
            exception is TimeoutException or OperationCanceledException ||
            exception is AggregateException ||
            snapshot.Any(static task => task.IsFaulted))
        {
            foreach (Task task in snapshot)
            {
                _ = task.Exception;
            }
        }
    }
}
