using System.Diagnostics;
using System.Drawing;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using DebtFlow.SipAgent.Application;
using DebtFlow.SipAgent.Protocol;

namespace DebtFlow.SipAgent.Host;

public sealed class TrayApplicationContext : ApplicationContext
{
    internal const int StatusRefreshIntervalMilliseconds = 5_000;
    private readonly AgentCoordinator _coordinator;
    private readonly LocalWebSocketServer _webSocketServer;
    private readonly IStartupRegistrationManager _startup;
    private readonly SingleInstanceCoordinator _singleInstance;
    private readonly AgentRuntimeOptions _options;
    private readonly ILogger<TrayApplicationContext> _logger;
    private readonly IAgentEventStore _eventStore;
    private readonly DiagnosticBundleExporter _diagnosticExporter;
    private readonly SafeJsonLoggerProvider _logProvider;
    private readonly string _logDirectory;
    private readonly int? _tlsDaysRemaining;
    private readonly string? _tlsWarningCode;
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

    public AgentMaintenanceRequest MaintenanceRequest { get; private set; }

    public TrayApplicationContext(
        AgentCoordinator coordinator,
        LocalWebSocketServer webSocketServer,
        IStartupRegistrationManager startup,
        SingleInstanceCoordinator singleInstance,
        AgentRuntimeOptions options,
        ILogger<TrayApplicationContext> logger,
        IAgentEventStore eventStore,
        DiagnosticBundleExporter diagnosticExporter,
        SafeJsonLoggerProvider logProvider,
        string logDirectory,
        bool startupRegistrationFailed,
        int? tlsDaysRemaining = null,
        string? tlsWarningCode = null)
    {
        _coordinator = coordinator;
        _webSocketServer = webSocketServer;
        _startup = startup;
        _singleInstance = singleInstance;
        _options = options;
        _logger = logger;
        _eventStore = eventStore;
        _diagnosticExporter = diagnosticExporter;
        _logProvider = logProvider;
        _logDirectory = logDirectory;
        _tlsDaysRemaining = tlsDaysRemaining;
        _tlsWarningCode = tlsWarningCode;
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
        exportDiagnostics.Click += (_, _) => RunUiTask(ExportDiagnosticsAsync, "export_diagnostics");
        var repairCertificate = new ToolStripMenuItem(TrayText.Get("RepairCertificate"));
        repairCertificate.Click += (_, _) => RunUiTask(
            () => RequestCertificateMaintenanceAsync(AgentMaintenanceRequest.RepairCertificate),
            "repair_certificate");
        var removeCertificate = new ToolStripMenuItem(TrayText.Get("RemoveCertificate"));
        removeCertificate.Click += (_, _) => RunUiTask(
            () => RequestCertificateMaintenanceAsync(AgentMaintenanceRequest.RemoveCertificate),
            "remove_certificate");
        var exit = new ToolStripMenuItem(TrayText.Get("Exit"));
        exit.Click += (_, _) => RunUiTask(() => ExitAsync(confirmActiveCall: true), "exit");

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
            repairCertificate,
            removeCertificate,
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
        _notifyIcon.DoubleClick += (_, _) => RunUiTask(RefreshStatusAsync, "status_refresh");

        _timer = new System.Windows.Forms.Timer
        {
            Interval = StatusRefreshIntervalMilliseconds,
            Enabled = true
        };
        _timer.Tick += (_, _) => RunUiTask(RefreshStatusAsync, "status_refresh");
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

        if (tlsWarningCode != null)
        {
            Notify(tlsWarningCode, TrayText.Format("TlsWarning", tlsWarningCode));
        }

        RunUiTask(RefreshStatusAsync, "initial_status_refresh");
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
            AgentSnapshotPayload snapshot = await GetSnapshotWithTimeoutAsync();
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
        catch (Exception exception)
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

        AgentSnapshotPayload? snapshot = null;
        try
        {
            snapshot = await GetSnapshotWithTimeoutAsync();
        }
        catch (Exception exception)
        {
            _logger.LogWarning("Tray exit status check failed with {ErrorType}; shutdown will continue", exception.GetType().Name);
        }
        if (confirmActiveCall && snapshot?.ActiveCalls.Count > 0 &&
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
        catch (Exception exception)
        {
            _logger.LogWarning("Graceful shutdown failed with {ErrorType}", exception.GetType().Name);
        }
        finally
        {
            ExitThread();
        }
    }

    private async Task RequestCertificateMaintenanceAsync(AgentMaintenanceRequest request)
    {
        if (_exitStarted) return;

        AgentSnapshotPayload snapshot;
        try
        {
            snapshot = await GetSnapshotWithTimeoutAsync();
        }
        catch (Exception exception)
        {
            _logger.LogWarning("Certificate maintenance status check failed with {ErrorType}", exception.GetType().Name);
            return;
        }
        if (snapshot.ActiveCalls.Count > 0)
        {
            MessageBox.Show(
                TrayText.Get("ActiveCallBlocksCertificateMaintenance"),
                TrayText.Get("ActionRequired"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        if (request == AgentMaintenanceRequest.RemoveCertificate && MessageBox.Show(
                TrayText.Get("RemoveCertificateConfirm"),
                TrayText.Get("ActionRequired"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            return;
        }

        MaintenanceRequest = request;
        await ExitAsync(confirmActiveCall: false);
    }

    private void OnSessionEnding(object sender, SessionEndingEventArgs eventArgs) =>
        _uiContext.Post(_ => RunUiTask(() => ExitAsync(confirmActiveCall: false), "session_ending"), null);

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
        try
        {
            Directory.CreateDirectory(_logDirectory);
            Process.Start(new ProcessStartInfo(_logDirectory) { UseShellExecute = true });
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogWarning("Opening the log directory failed with {ErrorType}", exception.GetType().Name);
        }
    }

    private async Task ExportDiagnosticsAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            AgentSnapshotPayload snapshot = await _coordinator.GetSnapshotAsync(timeout.Token);
            EventStoreHealth health = await _eventStore.GetHealthAsync(timeout.Token);
            AudioDevicesSnapshot? audioDevices = snapshot.AudioDevices;
            string outputPath = Path.Combine(
                AgentStoragePaths.DiagnosticDirectory,
                $"debt-flow-sip-agent-diagnostics-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip");
            string exported = await _diagnosticExporter.ExportAsync(
                outputPath,
                snapshot,
                health,
                Math.Max(0, (audioDevices?.InputDevices.Count ?? 1) - 1),
                Math.Max(0, (audioDevices?.OutputDevices.Count ?? 1) - 1),
                _options.IsOperational,
                timeout.Token,
                LocalTlsCertificateProfile.Version,
                _tlsDaysRemaining,
                _tlsWarningCode,
                _coordinator.GetOperationalHealth(),
                _webSocketServer.GetOperationalHealth(),
                _logProvider.GetOperationalHealth());
            MessageBox.Show(
                TrayText.Format("DiagnosticExported", exported),
                TrayText.Get("AppName"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception exception)
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

    private async Task<AgentSnapshotPayload> GetSnapshotWithTimeoutAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        return await _coordinator.GetSnapshotAsync(timeout.Token);
    }

    private async void RunUiTask(Func<Task> action, string operation)
    {
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Tray operation {Operation} failed with {ErrorType}",
                operation,
                exception.GetType().Name);
        }
    }

    private static string TruncateTooltip(string value) => value.Length <= 63 ? value : value[..63];
}
