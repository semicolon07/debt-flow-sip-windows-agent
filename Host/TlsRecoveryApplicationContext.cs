using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;

namespace DebtFlow.SipAgent.Host;

public enum AgentMaintenanceRequest
{
    None,
    RepairCertificate,
    RemoveCertificate
}

public sealed class TlsDiagnosticExporter
{
    public async Task<string> ExportAsync(
        string outputPath,
        LocalTlsCertificateResult result,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)
            ?? throw new InvalidOperationException("diagnostic_path_invalid"));
        await using FileStream stream = File.Create(outputPath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false);
        ZipArchiveEntry entry = archive.CreateEntry("tls-summary.json", CompressionLevel.Optimal);
        await using Stream entryStream = entry.Open();
        await JsonSerializer.SerializeAsync(entryStream, new
        {
            state = result.State,
            profileVersion = result.ProfileVersion,
            daysRemaining = result.DaysRemaining,
            errorCode = result.ErrorCode,
            degraded = result.IsDegraded
        }, cancellationToken: cancellationToken);
        return outputPath;
    }
}

public sealed class TlsRecoveryApplicationContext : ApplicationContext
{
    private readonly ILocalTlsCertificateManager _certificateManager;
    private readonly SingleInstanceCoordinator _singleInstance;
    private readonly TlsDiagnosticExporter _diagnosticExporter;
    private readonly string _logDirectory;
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _status;
    private readonly SynchronizationContext _uiContext;
    private LocalTlsCertificateResult _result;
    private bool _operationRunning;

    public TlsRecoveryApplicationContext(
        ILocalTlsCertificateManager certificateManager,
        SingleInstanceCoordinator singleInstance,
        LocalTlsCertificateResult result,
        TlsDiagnosticExporter diagnosticExporter,
        string logDirectory)
    {
        _certificateManager = certificateManager;
        _singleInstance = singleInstance;
        _result = result;
        _diagnosticExporter = diagnosticExporter;
        _logDirectory = logDirectory;
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        _status = new ToolStripMenuItem(TrayText.Format("AgentStatus", TrayText.State("degraded"))) { Enabled = false };
        var repair = new ToolStripMenuItem(TrayText.Get("RepairCertificate"));
        repair.Click += async (_, _) => await RepairAsync();
        var remove = new ToolStripMenuItem(TrayText.Get("RemoveCertificate"));
        remove.Click += async (_, _) => await RemoveAsync();
        var logs = new ToolStripMenuItem(TrayText.Get("OpenLogs"));
        logs.Click += (_, _) => OpenLogs();
        var diagnostics = new ToolStripMenuItem(TrayText.Get("ExportDiagnostics"));
        diagnostics.Click += async (_, _) => await ExportAsync();
        var exit = new ToolStripMenuItem(TrayText.Get("Exit"));
        exit.Click += (_, _) => ExitThread();

        var menu = new ContextMenuStrip();
        menu.Items.AddRange([
            _status,
            new ToolStripSeparator(),
            repair,
            remove,
            logs,
            diagnostics,
            new ToolStripSeparator(),
            exit
        ]);
        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Warning,
            Text = TrayText.Get("AppName"),
            ContextMenuStrip = menu,
            Visible = true
        };
        _singleInstance.ActivationRequested += OnActivationRequested;
        ShowFailure();
    }

    public bool RetryRequested { get; private set; }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _singleInstance.ActivationRequested -= OnActivationRequested;
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }

        base.Dispose(disposing);
    }

    private async Task RepairAsync()
    {
        if (_operationRunning) return;
        _operationRunning = true;
        try
        {
            _result = await _certificateManager.RepairAsync(CancellationToken.None);
            if (_result.IsReady)
            {
                RetryRequested = true;
                ExitThread();
                return;
            }

            ShowFailure();
            MessageBox.Show(
                TrayText.Get("CertificateRepairFailed"),
                TrayText.Get("ActionRequired"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        finally
        {
            _operationRunning = false;
        }
    }

    private async Task RemoveAsync()
    {
        if (_operationRunning || MessageBox.Show(
                TrayText.Get("RemoveCertificateConfirm"),
                TrayText.Get("ActionRequired"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            return;
        }

        _operationRunning = true;
        try
        {
            LocalTlsCertificateResult removed = await _certificateManager.RemoveAsync(CancellationToken.None);
            if (removed.ErrorCode == null)
            {
                ExitThread();
                return;
            }

            _result = removed;
            ShowFailure();
            MessageBox.Show(
                TrayText.Get("CertificateRemoveFailed"),
                TrayText.Get("ActionRequired"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        finally
        {
            _operationRunning = false;
        }
    }

    private async Task ExportAsync()
    {
        try
        {
            string path = Path.Combine(
                AgentStoragePaths.DiagnosticDirectory,
                $"debt-flow-sip-agent-tls-diagnostics-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip");
            string exported = await _diagnosticExporter.ExportAsync(path, _result, CancellationToken.None);
            MessageBox.Show(
                TrayText.Format("DiagnosticExported", exported),
                TrayText.Get("AppName"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show(
                TrayText.Get("DiagnosticExportFailed"),
                TrayText.Get("ActionRequired"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void OpenLogs()
    {
        Directory.CreateDirectory(_logDirectory);
        Process.Start(new ProcessStartInfo(_logDirectory) { UseShellExecute = true });
    }

    private void ShowFailure()
    {
        string code = _result.ErrorCode ?? "tls_certificate_invalid";
        _status.Text = TrayText.Format("AgentStatus", $"{TrayText.State("degraded")} ({code})");
        _notifyIcon.ShowBalloonTip(
            5000,
            TrayText.Get("ActionRequired"),
            TrayText.Format("TlsUnavailable", code),
            ToolTipIcon.Warning);
    }

    private void OnActivationRequested() => _uiContext.Post(_ => ShowFailure(), null);
}
