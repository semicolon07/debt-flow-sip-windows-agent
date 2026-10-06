using System.Text.Json;
using System.Text.Json.Serialization;

namespace DebtFlow.SipAgent.Host;

public sealed record AgentRuntimeOptions(
    bool ConsoleMode,
    bool BackgroundMode,
    bool IsAllowAllOrigins,
    IReadOnlySet<string> AllowedOrigins,
    string? ConfigurationError,
    bool AcceptRtpFromAny = false)
{
    public const int Port = 8443;
    public static readonly TimeSpan OwnerDisconnectGrace = TimeSpan.FromSeconds(60);

    public bool IsOperational => ConfigurationError == null;

    public static AgentRuntimeOptions Load(string[] args, string? configurationPath = null)
    {
        bool consoleMode = args.Contains("--console", StringComparer.Ordinal);
        bool backgroundMode = args.Contains("--background", StringComparer.Ordinal);
        var cliOrigins = new List<string>();

        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            if (argument is "--console" or "--background")
            {
                continue;
            }

            if (argument == "--allowed-origin" && index + 1 < args.Length)
            {
                cliOrigins.Add(args[++index]);
                continue;
            }

            throw new AgentConfigurationException("unsupported_argument");
        }

        if (!consoleMode && cliOrigins.Count > 0)
        {
            throw new AgentConfigurationException("allowed_origin_requires_console");
        }

        var origins = new HashSet<string>(StringComparer.Ordinal);
        bool isAllowAllOrigins = false;
        bool acceptRtpFromAny = false;
        string? configurationError = null;
        if (consoleMode)
        {
            origins.Add("http://localhost:8765");
            origins.Add("http://127.0.0.1:8765");
        }
        else
        {
            string resolvedConfigurationPath = configurationPath ?? GetConfigurationPath();
            if (!File.Exists(resolvedConfigurationPath))
            {
                configurationError = "origin_configuration_missing";
            }
            else
            {
                try
                {
                    AgentSettingsDocument document = JsonSerializer.Deserialize<AgentSettingsDocument>(
                        File.ReadAllBytes(resolvedConfigurationPath),
                        JsonOptions) ?? throw new JsonException();
                    if (document.Agent == null)
                    {
                        throw new JsonException();
                    }

                    IReadOnlyList<string> configuredOrigins = document.Agent.AllowedOrigins ?? [];
                    isAllowAllOrigins = document.Agent.IsAllowAllOrigins ?? configuredOrigins.Count == 0;
                    acceptRtpFromAny = document.Agent.AcceptRtpFromAny ?? false;
                    foreach (string origin in configuredOrigins)
                    {
                        origins.Add(NormalizeOrigin(origin));
                    }

                    if (!isAllowAllOrigins && origins.Count == 0)
                    {
                        throw new AgentConfigurationException("origin_configuration_invalid");
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or AgentConfigurationException)
                {
                    origins.Clear();
                    configurationError = "origin_configuration_invalid";
                }
            }
        }

        foreach (string origin in cliOrigins)
        {
            origins.Add(NormalizeOrigin(origin));
        }

        if (!isAllowAllOrigins && origins.Count == 0)
        {
            configurationError ??= "origin_configuration_missing";
        }

        return new AgentRuntimeOptions(
            consoleMode,
            backgroundMode,
            isAllowAllOrigins,
            origins,
            configurationError,
            acceptRtpFromAny);
    }

    public static string GetConfigurationPath()
    {
        return AgentStoragePaths.ConfigurationPath;
    }

    private static string NormalizeOrigin(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
            (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) &&
             !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new AgentConfigurationException("origin_configuration_invalid");
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private sealed record AgentSettingsDocument(AgentSettings Agent);
    private sealed record AgentSettings(
        bool? IsAllowAllOrigins,
        IReadOnlyList<string>? AllowedOrigins,
        bool? AcceptRtpFromAny);
}

public sealed class AgentConfigurationException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
