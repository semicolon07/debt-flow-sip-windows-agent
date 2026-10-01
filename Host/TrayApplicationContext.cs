using System.Diagnostics;
using System.Drawing;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using NAudio.Wave;
using DebtFlow.SipAgent.Application;
using DebtFlow.SipAgent.Protocol;

namespace DebtFlow.SipAgent.Host;

public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly AgentCoordinator _coordinator;
    private readonly LocalWebSocketServer _webSocketServer;
    private readonly IStartupRegistrationManager _startup;
    private readonly SingleInstanceCoordinator _singleInstance;
    private readonly AgentRuntimeOptions _options;
    private readonly ILogger<TrayApplicationContext> _logger;
    private readonly IAgentEventStore _eventStore;
    private readonly DiagnosticBundleExporter _diagnosticExporter;
    private readonly string _logDirectory;
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _agentStatus;
    private readonly ToolStripMenuItem _portalStatus;
    private readonly ToolStripMenuItem _registrationStatus;
    private readonly ToolStripMenuItem _callStatus;
    private readonly ToolStripMenuItem _audioStatus;
    private readonly ToolStripMenuItem _startupItem;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly SynchronizationContext _uiContext;
    private string? _lastNotificationCode;
    private bool _exitStarted;
    private int _refreshing;

    public TrayApplicationContext(
        AgentCoordinator coordinator,
        LocalWebSocketServer webSocketServer,
        IStartupRegistrationManager startup,
        SingleInstanceCoordinator singleInstance,
        AgentRuntimeOptions options,
        ILogger<TrayApplicationContext> logger,
        IAgentEventStore eventStore,
        DiagnosticBundleExporter diagnosticExporter,
        string logDirectory,
        bool startupRegistrationFailed)
    {
        _coordinator = coordinator;
        _webSocketServer = webSocketServer;
        _startup = startup;
        _singleInstance = singleInstance;
        _options = options;
        _logger = logger;
        _eventStore = eventStore;
        _diagnosticExporter = diagnosticExporter;
        _logDirectory = logDirectory;
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        _agentStatus = StatusItem("AgentStatus", TrayText.State("starting"));
        _portalStatus = StatusItem("PortalStatus", TrayText.Get("Disconnected"));
        _registrationStatus = StatusItem("RegistrationStatus", TrayText.State("unconfigured"));
        _callStatus = StatusItem("CallStatus", TrayText.Get("Idle"));
        _audioStatus = StatusItem("AudioStatus", TrayText.State("unknown"));
        _startupItem = new ToolStripMenuItem(TrayText.Get("StartWithWindows"))
        {
            CheckOnClick = true,
            Checked = SafeStartupState()
        };
        _startupItem.Click += (_, _) => UpdateStartup();

        var openLogs = new ToolStripMenuItem(TrayText.Get("OpenLogs"));
        openLogs.Click += (_, _) => OpenLogDirectory();
        var exportDiagnostics = new ToolStripMenuItem(TrayText.Get("ExportDiagnostics"));
        exportDiagnostics.Click += async (_, _) => await ExportDiagnosticsAsync();
        var exit = new ToolStripMenuItem(TrayText.Get("Exit"));
        exit.Click += async (_, _) => await ExitAsync(confirmActiveCall: true);

        var menu = new ContextMenuStrip();
        menu.Items.AddRange([
            _agentStatus,
            _portalStatus,
            _registrationStatus,
            _callStatus,
            _audioStatus,
            new ToolStripSeparator(),
            openLogs,
            exportDiagnostics,
            _startupItem,
            new ToolStripSeparator(),
            exit
        ]);

        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = TrayText.Get("AppName"),
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += async (_, _) => await RefreshStatusAsync();

        _timer = new System.Windows.Forms.Timer { Interval = 1000, Enabled = true };
        _timer.Tick += async (_, _) => await RefreshStatusAsync();
        _singleInstance.ActivationRequested += OnActivationRequested;
        SystemEvents.SessionEnding += OnSessionEnding;

        if (!_options.IsOperational)
        {
            Notify("origin_configuration_invalid", TrayText.Get("ConfigurationInvalid"));
        }

        if (startupRegistrationFailed)
        {
            Notify("startup_registration_failed", TrayText.Get("StartupFailed"));
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.SessionEnding -= OnSessionEnding;
            _singleInstance.ActivationRequested -= OnActivationRequested;
            _timer.Dispose();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }

        base.Dispose(disposing);
    }

    private static ToolStripMenuItem StatusItem(string resourceName, string status) => new(TrayText.Format(resourceName, status))
    {
        Enabled = false
    };

    private async Task RefreshStatusAsync()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) != 0)
        {
            return;
        }

        try
        {
            AgentSnapshotPayload snapshot = await _coordinator.GetSnapshotAsync(CancellationToken.None);
            string agentState = _options.IsOperational ? snapshot.AgentState : "degraded";
            string localizedAgentState = TrayText.State(agentState);
            string agentStatus = snapshot.AgentStateCode == null
                ? localizedAgentState
                : $"{localizedAgentState} ({snapshot.AgentStateCode})";
            _agentStatus.Text = TrayText.Format("AgentStatus", agentStatus);
            _portalStatus.Text = TrayText.Format(
                "PortalStatus",
                _webSocketServer.IsClientConnected ? TrayText.Get("Connected") : TrayText.Get("Disconnected"));
            _registrationStatus.Text = TrayText.Format("RegistrationStatus", TrayText.State(snapshot.RegistrationState));
            _callStatus.Text = TrayText.Format(
                "CallStatus",
                snapshot.ActiveCalls.Count == 0 ? TrayText.Get("Idle") : TrayText.State(snapshot.ActiveCalls[0].State));
            _audioStatus.Text = TrayText.Format("AudioStatus", TrayText.State(snapshot.AudioState));
            _notifyIcon.Icon = agentState == "degraded" ? SystemIcons.Warning : SystemIcons.Application;
            _notifyIcon.Text = TruncateTooltip($"{TrayText.Get("AppName")} — {localizedAgentState}");

            if (snapshot.RegistrationState == "failed")
            {
                Notify("registration_failed", TrayText.Get("RegistrationFailed"));
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning("Tray status refresh failed with {ErrorType}", exception.GetType().Name);
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    private async Task ExitAsync(bool confirmActiveCall)
    {
        if (_exitStarted)
        {
            return;
        }

        AgentSnapshotPayload snapshot = await _coordinator.GetSnapshotAsync(CancellationToken.None);
        if (confirmActiveCall && snapshot.ActiveCalls.Count > 0 &&
            MessageBox.Show(
                TrayText.Get("ExitActiveCall"),
                TrayText.Get("ExitTitle"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            return;
        }

        _exitStarted = true;
        _timer.Stop();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await _coordinator.ShutdownAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Graceful shutdown exceeded its time limit");
        }

        ExitThread();
    }

    private void OnSessionEnding(object sender, SessionEndingEventArgs eventArgs) =>
        _uiContext.Post(async _ => await ExitAsync(confirmActiveCall: false), null);

    private void OnActivationRequested() => _uiContext.Post(
        _ => Notify("second_instance", TrayText.Get("SecondInstance")),
        null);

    private void UpdateStartup()
    {
        try
        {
            _startup.SetEnabled(_startupItem.Checked);
            _startupItem.Checked = _startup.IsEnabled;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or InvalidOperationException)
        {
            _startupItem.Checked = SafeStartupState();
            _logger.LogWarning("Startup registration update failed with {ErrorType}", exception.GetType().Name);
            Notify("startup_update_failed", TrayText.Get("StartupFailed"));
        }
    }

    private bool SafeStartupState()
    {
        try
        {
            return _startup.IsEnabled;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or InvalidOperationException)
        {
            _logger.LogWarning("Startup registration state failed with {ErrorType}", exception.GetType().Name);
            return false;
        }
    }

    private void OpenLogDirectory()
    {
        Directory.CreateDirectory(_logDirectory);
        Process.Start(new ProcessStartInfo(_logDirectory) { UseShellExecute = true });
    }

    private async Task ExportDiagnosticsAsync()
    {
        try
        {
            AgentSnapshotPayload snapshot = await _coordinator.GetSnapshotAsync(CancellationToken.None);
            EventStoreHealth health = await _eventStore.GetHealthAsync(CancellationToken.None);
            string outputPath = Path.Combine(
                AgentStoragePaths.DiagnosticDirectory,
                $"debt-flow-sip-agent-diagnostics-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip");
            string exported = await _diagnosticExporter.ExportAsync(
                outputPath,
                snapshot,
                health,
                WaveIn.DeviceCount,
                WaveOut.DeviceCount,
                _options.IsOperational,
                CancellationToken.None);
            MessageBox.Show(
                TrayText.Format("DiagnosticExported", exported),
                TrayText.Get("AppName"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning("Diagnostic export failed with {ErrorType}", exception.GetType().Name);
            MessageBox.Show(
                TrayText.Get("DiagnosticExportFailed"),
                TrayText.Get("ActionRequired"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void Notify(string code, string message)
    {
        if (string.Equals(code, _lastNotificationCode, StringComparison.Ordinal))
        {
            return;
        }

        _lastNotificationCode = code;
        _notifyIcon.ShowBalloonTip(5000, TrayText.Get("ActionRequired"), message, ToolTipIcon.Warning);
    }

    private static string TruncateTooltip(string value) => value.Length <= 63 ? value : value[..63];
}
