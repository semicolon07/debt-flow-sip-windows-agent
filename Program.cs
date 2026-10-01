using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SIPSorcery;
using DebtFlow.SipAgent.Application;

namespace DebtFlow.SipAgent.Host;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        AgentRuntimeOptions options;
        try
        {
            options = AgentRuntimeOptions.Load(args);
        }
        catch (AgentConfigurationException exception)
        {
            bool consoleRequested = args.Contains("--console", StringComparer.Ordinal);
            if (consoleRequested)
            {
                ConsoleSession.EnsureAttached();
                Console.Error.WriteLine(exception.Code);
            }
            else
            {
                MessageBox.Show(exception.Code, "Debt Flow SIP Agent", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            return 2;
        }

        if (options.ConsoleMode)
        {
            ConsoleSession.EnsureAttached();
        }
        else
        {
            ApplicationConfiguration.Initialize();
        }

        try
        {
            return RunAsync(options).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            if (options.ConsoleMode)
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

    private static async Task<int> RunAsync(AgentRuntimeOptions options)
    {
        await using SingleInstanceCoordinator singleInstance = SingleInstanceCoordinator.Create();
        if (!singleInstance.IsPrimary)
        {
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
            server.ListenLocalhost(AgentRuntimeOptions.Port);
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
            "Starting Debt Flow SIP Agent V1 in {Mode} mode with {OriginCount} allowed origins",
            options.ConsoleMode ? "console" : "tray",
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

            return 3;
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
                startupRegistrationFailed);
            System.Windows.Forms.Application.Run(tray);
        }

        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await coordinator.ShutdownAsync(shutdown.Token);
        await app.StopAsync(shutdown.Token);
        logger.LogInformation("Debt Flow SIP Agent stopped");
        return 0;
    }

}
