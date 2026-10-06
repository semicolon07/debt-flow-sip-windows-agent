using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SIPSorcery;
using DebtFlow.SipAgent.Application;
using DebtFlow.SipAgent.Persistence;

namespace DebtFlow.SipAgent.Host;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        AgentLaunchCommand command;
        try
        {
            command = AgentCommandLine.Parse(args);
        }
        catch (AgentConfigurationException exception)
        {
            ConsoleSession.EnsureAttached();
            Console.Error.WriteLine(exception.Code);
            return 2;
        }

        bool consoleRequested = args.Contains("--console", StringComparer.Ordinal) || command != AgentLaunchCommand.Run;
        if (consoleRequested)
        {
            ConsoleSession.EnsureAttached();
        }
        else
        {
            ApplicationConfiguration.Initialize();
            _ = AgentSettingsProvisioner.EnsureFromPackagedExample();
        }

        AgentRuntimeOptions? options = null;
        try
        {
            if (command == AgentLaunchCommand.Run) options = AgentRuntimeOptions.Load(args);
        }
        catch (AgentConfigurationException exception)
        {
            if (consoleRequested)
            {
                Console.Error.WriteLine(exception.Code);
            }
            else
            {
                MessageBox.Show(exception.Code, "Debt Flow SIP Agent", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            return 2;
        }

        if (options is { ConsoleMode: false, IsOperational: false })
        {
            using var dialog = new OriginConfigurationDialog();
            if (dialog.ShowDialog() != DialogResult.OK ||
                !AgentSettingsProvisioner.TrySave(
                    AgentRuntimeOptions.GetConfigurationPath(),
                    dialog.IsAllowAllOrigins,
                    dialog.AllowedOrigins))
            {
                return 2;
            }

            options = AgentRuntimeOptions.Load(args);
            if (!options.IsOperational) return 2;
        }

        try
        {
            return RunAsync(command, options).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            if (consoleRequested)
            {
                Console.Error.WriteLine($"agent_start_failed:{exception.GetType().Name}");
            }
            else
            {
                MessageBox.Show(
                    TrayText.Get("AgentStartFailed"),
                    TrayText.Get("ActionRequired"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }

            return 1;
        }
    }

    private static async Task<int> RunAsync(AgentLaunchCommand command, AgentRuntimeOptions? options)
    {
        await using SingleInstanceCoordinator singleInstance = SingleInstanceCoordinator.Create();
        if (!singleInstance.IsPrimary)
        {
            if (command != AgentLaunchCommand.Run)
            {
                Console.Error.WriteLine("agent_already_running");
                return 5;
            }

            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await singleInstance.SignalPrimaryAsync(timeout.Token);
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException)
            {
            }

            return 0;
        }

        singleInstance.StartActivationServer();
        ILocalTlsCertificateManager certificateManager = new LocalTlsCertificateManager(
            new WindowsLocalTlsCertificatePlatform(),
            new FileLocalTlsCertificateMetadataStore(AgentStoragePaths.TlsCertificateMetadataPath),
            new SystemLocalTlsClock(),
            new FileLocalTlsConsentStore(AgentStoragePaths.TlsCertificateConsentPath));

        if (command != AgentLaunchCommand.Run)
        {
            LocalTlsCertificateResult maintenanceResult = command == AgentLaunchCommand.RepairLocalCertificate
                ? await certificateManager.RepairAsync(CancellationToken.None)
                : await certificateManager.RemoveAsync(CancellationToken.None);
            maintenanceResult.Certificate?.Dispose();
            if (maintenanceResult.ErrorCode != null)
            {
                Console.Error.WriteLine(maintenanceResult.ErrorCode);
                return 4;
            }

            Console.WriteLine(command == AgentLaunchCommand.RepairLocalCertificate
                ? "local_certificate_repaired"
                : "local_certificate_removed");
            return 0;
        }

        if (options == null) throw new InvalidOperationException("runtime_options_unavailable");
        while (true)
        {
            LocalTlsCertificateResult tls = await certificateManager.EnsureReadyAsync(
                () => RequestTlsConsent(options.ConsoleMode),
                CancellationToken.None);
            if (!tls.IsReady || tls.Certificate == null)
            {
                tls.Certificate?.Dispose();
                if (options.ConsoleMode)
                {
                    Console.Error.WriteLine(tls.ErrorCode ?? "tls_certificate_invalid");
                    return 4;
                }

                using var recovery = new TlsRecoveryApplicationContext(
                    certificateManager,
                    singleInstance,
                    tls,
                    new TlsDiagnosticExporter(),
                    AgentStoragePaths.LogDirectory);
                System.Windows.Forms.Application.Run(recovery);
                if (recovery.RetryRequested) continue;
                return 4;
            }

            RuntimeExecutionResult runtime;
            using (tls.Certificate)
            {
                runtime = await RunRuntimeAsync(
                    options,
                    tls.Certificate,
                    singleInstance,
                    tls.DaysRemaining,
                    tls.IsDegraded ? tls.ErrorCode : null);
            }

            if (runtime.ExitCode != 0) return runtime.ExitCode;
            AgentMaintenanceRequest request = runtime.MaintenanceRequest;
            if (request == AgentMaintenanceRequest.None) return 0;
            LocalTlsCertificateResult result = request == AgentMaintenanceRequest.RepairCertificate
                ? await certificateManager.RepairAsync(CancellationToken.None)
                : await certificateManager.RemoveAsync(CancellationToken.None);
            result.Certificate?.Dispose();
            if (result.ErrorCode != null)
            {
                using var recovery = new TlsRecoveryApplicationContext(
                    certificateManager,
                    singleInstance,
                    result,
                    new TlsDiagnosticExporter(),
                    AgentStoragePaths.LogDirectory);
                System.Windows.Forms.Application.Run(recovery);
                if (recovery.RetryRequested) continue;
                return 4;
            }

            if (request == AgentMaintenanceRequest.RemoveCertificate) return 0;
        }
    }

    private static bool RequestTlsConsent(bool consoleMode)
    {
        if (!consoleMode)
        {
            return MessageBox.Show(
                TrayText.Get("TlsConsent"),
                TrayText.Get("TlsConsentTitle"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information,
                MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }

        Console.Error.WriteLine(TrayText.Get("TlsConsent"));
        Console.Error.Write("Continue? [y/N]: ");
        string? response = Console.ReadLine()?.Trim();
        return string.Equals(response, "y", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(response, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<RuntimeExecutionResult> RunRuntimeAsync(
        AgentRuntimeOptions options,
        System.Security.Cryptography.X509Certificates.X509Certificate2 certificate,
        SingleInstanceCoordinator singleInstance,
        int? tlsDaysRemaining,
        string? tlsWarningCode)
    {
        AgentMaintenanceRequest maintenanceRequest = AgentMaintenanceRequest.None;
        string logDirectory = AgentStoragePaths.LogDirectory;
        var logProvider = new SafeJsonLoggerProvider(logDirectory, options.ConsoleMode);
        (IAgentEventStore eventStore, bool durableStoreAvailable, string? storageFailureCode) = await AgentEventStoreFactory.CreateAsync(
            AgentStoragePaths.DatabasePath,
            CancellationToken.None);

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            ApplicationName = typeof(Program).Assembly.GetName().Name
        });
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Logging.AddProvider(logProvider);
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.AddServerHeader = false;
            server.ListenLocalhost(AgentRuntimeOptions.Port, listen => listen.UseHttps(certificate));
        });

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<IStartupRegistrationManager, StartupRegistrationManager>();
        builder.Services.AddSingleton<IAgentEventStore>(eventStore);
        builder.Services.AddSingleton(new DiagnosticBundleExporter(logDirectory));
        builder.Services.AddSingleton<WebSocketEventPublisher>();
        builder.Services.AddSingleton<IAgentEventPublisher>(provider => provider.GetRequiredService<WebSocketEventPublisher>());
        builder.Services.AddSingleton<IAgentClock, SystemAgentClock>();
        builder.Services.AddSingleton<IAgentIdGenerator, GuidAgentIdGenerator>();
        builder.Services.AddSingleton<IAgentDelay, SystemAgentDelay>();
        builder.Services.AddSingleton<IAudioPreferencesStore>(
            new FileAudioPreferencesStore(AgentStoragePaths.AudioPreferencesPath));
        builder.Services.AddSingleton<SipRuntime>();
        builder.Services.AddSingleton<ISipRuntime>(provider => provider.GetRequiredService<SipRuntime>());
        builder.Services.AddSingleton(provider => new AgentCoordinator(
            provider.GetRequiredService<ISipRuntime>(),
            provider.GetRequiredService<IAgentEventStore>(),
            provider.GetRequiredService<IAgentEventPublisher>(),
            provider.GetRequiredService<IAgentClock>(),
            provider.GetRequiredService<IAgentIdGenerator>(),
            durableStoreAvailable,
            provider.GetRequiredService<IAgentDelay>(),
            AgentRuntimeOptions.OwnerDisconnectGrace,
            storageFailureCode ?? options.ConfigurationError));
        builder.Services.AddSingleton<V1CommandDispatcher>();
        builder.Services.AddSingleton<LocalWebSocketServer>();

        await using WebApplication app = builder.Build();
        ILoggerFactory loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
        ILogger logger = loggerFactory.CreateLogger("DebtFlow.SipAgent");
        using ILoggerFactory sipLoggerFactory = LoggerFactory.Create(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Warning);
            logging.AddProvider(new RedactingSipLoggerProvider(loggerFactory));
        });
        SIPSorcery.LogFactory.Set(sipLoggerFactory);

        AgentCoordinator coordinator = app.Services.GetRequiredService<AgentCoordinator>();
        await coordinator.InitializeAsync(CancellationToken.None);
        LocalWebSocketServer webSocketServer = app.Services.GetRequiredService<LocalWebSocketServer>();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = Protocol.ProtocolConstants.HeartbeatInterval });
        app.Map("/agent/v1", webSocketServer.HandleAsync);
        app.MapFallback(context =>
        {
            context.Response.StatusCode = (int)HttpStatusCode.NotFound;
            return Task.CompletedTask;
        });

        logger.LogInformation(
            "Starting Debt Flow SIP Agent V1 in {Mode} mode with allow-all={AllowAllOrigins} and {OriginCount} allowed origins",
            options.ConsoleMode ? "console" : "tray",
            options.IsAllowAllOrigins,
            options.AllowedOrigins.Count);
        if (!durableStoreAvailable)
        {
            logger.LogError(
                "Durable event store is unavailable with code {Code}; registration and calls are blocked",
                storageFailureCode ?? "outbox_unavailable");
        }

        if (options.ConfigurationError != null)
        {
            logger.LogWarning("Agent configuration is degraded with code {Code}", options.ConfigurationError);
        }

        if (tlsWarningCode != null)
        {
            logger.LogWarning("Local TLS certificate is operating with warning code {Code}", tlsWarningCode);
        }

        try
        {
            await app.StartAsync();
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            logger.LogError("Local WebSocket listener failed with {ErrorType}", exception.GetType().Name);
            if (!options.ConsoleMode)
            {
                MessageBox.Show(
                    TrayText.Get("PortUnavailable"),
                    TrayText.Get("ActionRequired"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }

            return new RuntimeExecutionResult(3, AgentMaintenanceRequest.None);
        }

        if (options.ConsoleMode)
        {
            await app.WaitForShutdownAsync();
        }
        else
        {
            IStartupRegistrationManager startup = app.Services.GetRequiredService<IStartupRegistrationManager>();
            bool startupRegistrationFailed = false;
            try
            {
                startup.InitializeDefault();
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or InvalidOperationException)
            {
                startupRegistrationFailed = true;
                logger.LogWarning("Automatic startup registration failed with {ErrorType}", exception.GetType().Name);
            }

            using var tray = new TrayApplicationContext(
                app.Services.GetRequiredService<AgentCoordinator>(),
                webSocketServer,
                startup,
                singleInstance,
                options,
                app.Services.GetRequiredService<ILogger<TrayApplicationContext>>(),
                eventStore,
                app.Services.GetRequiredService<DiagnosticBundleExporter>(),
                logDirectory,
                startupRegistrationFailed,
                tlsDaysRemaining,
                tlsWarningCode);
            System.Windows.Forms.Application.Run(tray);
            maintenanceRequest = tray.MaintenanceRequest;
        }

        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await coordinator.ShutdownAsync(shutdown.Token);
        await app.StopAsync(shutdown.Token);
        logger.LogInformation("Debt Flow SIP Agent stopped");
        return new RuntimeExecutionResult(0, maintenanceRequest);
    }

    private sealed record RuntimeExecutionResult(int ExitCode, AgentMaintenanceRequest MaintenanceRequest);

}
