using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace DebtFlow.SipAgent.Host;

public sealed class SafeJsonLoggerProvider : ILoggerProvider
{
    private const long MaximumFileBytes = 10 * 1024 * 1024;
    private const int RetainedFileCount = 7;
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly bool _writeConsole;
    private StreamWriter? _writer;
    private string? _currentPath;
    private static readonly Regex SensitiveAssignment = new(
        @"(?i)\b(password|authorization|username|credential|secret|token|destination|caller|remoteParty|dtmf|digit|sdp|sipHeader)\b\s*[:=]?\s*[^\s,;]+",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
    private static readonly Regex LongNumber = new(
        @"(?<!\d)\d{7,}(?!\d)",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    public SafeJsonLoggerProvider(string directory, bool writeConsole)
    {
        _directory = directory;
        _writeConsole = writeConsole;
        Directory.CreateDirectory(directory);
    }

    public ILogger CreateLogger(string categoryName) => new SafeJsonLogger(this, categoryName);

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private void Write<TState>(
        string category,
        LogLevel level,
        EventId eventId,
        TState state,
        Exception? exception)
    {
        Dictionary<string, object?> properties = ExtractSafeProperties(state);
        string template = properties.Remove("{OriginalFormat}", out object? original)
            ? Convert.ToString(original, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty
            : Convert.ToString(state, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("timestampUtc", DateTimeOffset.UtcNow);
            json.WriteString("level", level.ToString());
            json.WriteString("category", category);
            if (eventId.Id != 0)
            {
                json.WriteNumber("eventId", eventId.Id);
            }

            json.WriteString("messageTemplate", SanitizeText(template));
            if (exception != null)
            {
                json.WriteString("exceptionType", exception.GetType().Name);
            }

            if (properties.Count > 0)
            {
                json.WriteStartObject("properties");
                foreach ((string key, object? value) in properties)
                {
                    WriteSafeProperty(json, key, value);
                }

                json.WriteEndObject();
            }

            json.WriteEndObject();
        }

        string line = Encoding.UTF8.GetString(buffer.ToArray());
        lock (_gate)
        {
            EnsureWriter(Encoding.UTF8.GetByteCount(line) + Encoding.UTF8.GetByteCount(Environment.NewLine));
            _writer!.WriteLine(line);
            _writer.Flush();
            if (_writeConsole)
            {
                Console.WriteLine(line);
            }
        }
    }

    private void EnsureWriter(int nextBytes)
    {
        if (_writer != null && _writer.BaseStream.Length + nextBytes <= MaximumFileBytes)
        {
            return;
        }

        _writer?.Dispose();
        string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
        _currentPath = Path.Combine(_directory, $"agent-{stamp}.jsonl");
        _writer = new StreamWriter(new FileStream(
            _currentPath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            16 * 1024,
            FileOptions.WriteThrough),
            new UTF8Encoding(false));

        foreach (FileInfo oldFile in new DirectoryInfo(_directory)
                     .GetFiles("agent-*.jsonl")
                     .OrderByDescending(file => file.CreationTimeUtc)
                     .Skip(RetainedFileCount))
        {
            try
            {
                oldFile.Delete();
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static Dictionary<string, object?> ExtractSafeProperties<TState>(TState state)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (state is not IEnumerable<KeyValuePair<string, object?>> values)
        {
            return result;
        }

        foreach ((string key, object? value) in values)
        {
            result[key] = IsSensitiveKey(key) ? "[REDACTED]" : value;
        }

        return result;
    }

    private static void WriteSafeProperty(Utf8JsonWriter json, string key, object? value)
    {
        string safeKey = key.Trim('{', '}');
        if (value == null)
        {
            json.WriteNull(safeKey);
        }
        else if (value is bool boolean)
        {
            json.WriteBoolean(safeKey, boolean);
        }
        else if (value is int integer)
        {
            json.WriteNumber(safeKey, integer);
        }
        else if (value is long longValue)
        {
            json.WriteNumber(safeKey, longValue);
        }
        else
        {
            json.WriteString(safeKey, SanitizeText(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty));
        }
    }

    private static bool IsSensitiveKey(string key)
    {
        string normalized = key.Trim('{', '}').ToLowerInvariant();
        return normalized.Contains("password", StringComparison.Ordinal) ||
               normalized.Contains("authorization", StringComparison.Ordinal) ||
               normalized.Contains("username", StringComparison.Ordinal) ||
               normalized.Contains("credential", StringComparison.Ordinal) ||
               normalized.Contains("secret", StringComparison.Ordinal) ||
               normalized.Contains("token", StringComparison.Ordinal) ||
               normalized.Contains("destination", StringComparison.Ordinal) ||
               normalized.Contains("caller", StringComparison.Ordinal) ||
               normalized.Contains("remoteparty", StringComparison.Ordinal) ||
               normalized.Contains("dtmf", StringComparison.Ordinal) ||
               normalized.Contains("digit", StringComparison.Ordinal) ||
               normalized.Contains("sdp", StringComparison.Ordinal) ||
               normalized.Contains("sipheader", StringComparison.Ordinal);
    }

    private static string SanitizeText(string value)
    {
        string bounded = value.Length <= 1024 ? value : value[..1024];
        string assignmentsRemoved = SensitiveAssignment.Replace(bounded, "$1=[REDACTED]");
        return LongNumber.Replace(assignmentsRemoved, "[REDACTED]");
    }

    private sealed class SafeJsonLogger(SafeJsonLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                owner.Write(category, logLevel, eventId, state, exception);
            }
        }
    }
}
