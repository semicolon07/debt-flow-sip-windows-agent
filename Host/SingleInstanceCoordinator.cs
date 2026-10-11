using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using DebtFlow.SipAgent.Application;

namespace DebtFlow.SipAgent.Host;

public sealed class SingleInstanceCoordinator : IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _serverTask;

    private SingleInstanceCoordinator(Mutex mutex, string pipeName, bool isPrimary)
    {
        _mutex = mutex;
        _pipeName = pipeName;
        IsPrimary = isPrimary;
    }

    public bool IsPrimary { get; }
    public event Action? ActivationRequested;
    public event Func<CancellationToken, Task<AgentInstanceResponse>>? QueryMaintenanceRequested;
    public event Func<CancellationToken, Task<AgentInstanceResponse>>? PrepareUpdateRequested;

    public static SingleInstanceCoordinator Create()
    {
        string sid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("windows_user_sid_unavailable");
        string identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid)))[..24];
        string mutexName = $@"Global\DebtFlow.SipAgent.{identity}";
        string pipeName = $"DebtFlow.SipAgent.{identity}";
        var mutex = new Mutex(initiallyOwned: false, mutexName, out bool createdNew);
        return new SingleInstanceCoordinator(mutex, pipeName, createdNew);
    }

    public void StartActivationServer()
    {
        if (!IsPrimary || _serverTask != null)
        {
            return;
        }

        _serverTask = RunActivationServerAsync(_shutdown.Token);
    }

    public async Task SignalPrimaryAsync(CancellationToken cancellationToken)
    {
        if (IsPrimary)
        {
            return;
        }

        using var client = new NamedPipeClientStream(
            ".",
            _pipeName,
            PipeDirection.Out,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        await client.ConnectAsync((int)ConnectTimeout.TotalMilliseconds, cancellationToken);
        await client.WriteAsync(new byte[] { AgentInstanceProtocol.LegacyActivateSignal }, cancellationToken);
        await client.FlushAsync(cancellationToken);
    }

    public async Task<AgentInstanceResponse> RequestPrepareUpdateAsync(CancellationToken cancellationToken)
        => await RequestMaintenanceAsync(AgentInstanceCommand.PrepareUpdate, cancellationToken);

    public async Task<AgentInstanceResponse> RequestQueryMaintenanceAsync(CancellationToken cancellationToken)
        => await RequestMaintenanceAsync(AgentInstanceCommand.QueryMaintenance, cancellationToken);

    private async Task<AgentInstanceResponse> RequestMaintenanceAsync(
        AgentInstanceCommand command,
        CancellationToken cancellationToken)
    {
        if (IsPrimary)
        {
            return new AgentInstanceResponse(AgentInstanceResponseStatus.Ready);
        }

        using var client = new NamedPipeClientStream(
            ".",
            _pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        await client.ConnectAsync((int)ConnectTimeout.TotalMilliseconds, cancellationToken);
        byte[] request = AgentInstanceProtocol.EncodeRequest(command);
        await client.WriteAsync(request, cancellationToken);
        await client.FlushAsync(cancellationToken);

        var response = new byte[AgentInstanceProtocol.FrameLength];
        try
        {
            await client.ReadExactlyAsync(response, cancellationToken);
        }
        catch (EndOfStreamException)
        {
            return new AgentInstanceResponse(AgentInstanceResponseStatus.ProtocolUnsupported);
        }

        return AgentInstanceProtocol.TryDecodeResponse(response, out AgentInstanceResponseStatus status)
            ? new AgentInstanceResponse(status)
            : new AgentInstanceResponse(AgentInstanceResponseStatus.ProtocolUnsupported);
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        if (_serverTask != null)
        {
            try
            {
                await _serverTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _mutex.Dispose();
        _shutdown.Dispose();
    }

    private async Task RunActivationServerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellationToken);
                var firstByte = new byte[1];
                if (await server.ReadAsync(firstByte, cancellationToken) != 1)
                {
                    continue;
                }

                if (firstByte[0] == AgentInstanceProtocol.LegacyActivateSignal)
                {
                    InvokeActivation();
                    continue;
                }

                var request = new byte[AgentInstanceProtocol.FrameLength];
                request[0] = firstByte[0];
                try
                {
                    await server.ReadExactlyAsync(request.AsMemory(1), cancellationToken);
                }
                catch (EndOfStreamException)
                {
                    continue;
                }

                AgentInstanceResponseStatus status = AgentInstanceResponseStatus.ProtocolUnsupported;
                if (AgentInstanceProtocol.TryDecodeRequest(request, out AgentInstanceCommand command))
                {
                    status = command switch
                    {
                        AgentInstanceCommand.Activate => InvokeActivation(),
                        AgentInstanceCommand.QueryMaintenance => await InvokeMaintenanceAsync(
                            QueryMaintenanceRequested,
                            cancellationToken),
                        AgentInstanceCommand.PrepareUpdate => await InvokePrepareUpdateAsync(cancellationToken),
                        _ => AgentInstanceResponseStatus.ProtocolUnsupported
                    };
                }

                byte[] response = AgentInstanceProtocol.EncodeResponse(status);
                await server.WriteAsync(response, cancellationToken);
                await server.FlushAsync(cancellationToken);
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }
    }

    private AgentInstanceResponseStatus InvokeActivation()
    {
        try
        {
            ActivationRequested?.Invoke();
            return AgentInstanceResponseStatus.Ready;
        }
        catch (Exception exception) when (OperationalExceptionPolicy.IsNonFatal(exception))
        {
            return AgentInstanceResponseStatus.ShutdownFailed;
        }
    }

    private async Task<AgentInstanceResponseStatus> InvokePrepareUpdateAsync(CancellationToken cancellationToken)
        => await InvokeMaintenanceAsync(PrepareUpdateRequested, cancellationToken);

    private static async Task<AgentInstanceResponseStatus> InvokeMaintenanceAsync(
        Func<CancellationToken, Task<AgentInstanceResponse>>? handler,
        CancellationToken cancellationToken)
    {
        if (handler == null || handler.GetInvocationList().Length != 1)
        {
            return AgentInstanceResponseStatus.ProtocolUnsupported;
        }

        try
        {
            AgentInstanceResponse response = await handler(cancellationToken);
            return response.Status;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (OperationalExceptionPolicy.IsNonFatal(exception))
        {
            return AgentInstanceResponseStatus.ShutdownFailed;
        }
    }
}
