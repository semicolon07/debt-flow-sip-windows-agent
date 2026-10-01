using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace DebtFlow.SipAgent.Host;

public sealed class SingleInstanceCoordinator : IAsyncDisposable
{
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
        await client.ConnectAsync(2000, cancellationToken);
        await client.WriteAsync(new byte[] { 1 }, cancellationToken);
        await client.FlushAsync(cancellationToken);
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
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellationToken);
                var signal = new byte[1];
                if (await server.ReadAsync(signal, cancellationToken) == 1)
                {
                    try
                    {
                        ActivationRequested?.Invoke();
                    }
                    catch
                    {
                    }
                }
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }
    }
}
