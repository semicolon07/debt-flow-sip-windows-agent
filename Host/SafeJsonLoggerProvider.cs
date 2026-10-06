using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using DebtFlow.SipAgent.Application;

namespace DebtFlow.SipAgent.Host;

public sealed class SafeJsonLoggerProvider : ILoggerProvider
{
    private const long MaximumFileBytes = 10 * 1024 * 1024;
    private const int RetainedFileCount = 7;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);
    private static readonly byte[] NewLine = Encoding.UTF8.GetBytes(Environment.NewLine);
    private readonly string _directory;
    private readonly bool _writeConsole;
    private readonly Channel<byte[]> _queue;
    private readonly Task _writerTask;
    private FileStream? _writer;
    private int _disposed;
    private long _droppedLines;

    public SafeJsonLoggerProvider(string directory, bool writeConsole)
    {
        _directory = directory;
        _writeConsole = writeConsole;
        Directory.CreateDirectory(directory);
        _queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(2048)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        _writerTask = Task.Run(WriteLoopAsync);
    }

    public ILogger CreateLogger(string categoryName) => new SafeJsonLogger(this, categoryName);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _queue.Writer.TryComplete();
        try
        {
            _writerTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
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

        var buffer = new ArrayBufferWriter<byte>(512);
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

            json.WriteString("messageTemplate", SafeLogSanitizer.Sanitize(template));
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

        byte[] line = buffer.WrittenSpan.ToArray();
        if (!_queue.Writer.TryWrite(line))
        {
            Interlocked.Increment(ref _droppedLines);
        }
    }

    private async Task WriteLoopAsync()
    {
        long lastFlush = Stopwatch.GetTimestamp();
        Task<bool>? pendingRead = null;
        bool dirty = false;
        try
        {
            while (true)
            {
                int count = 0;
                while (count < 64 && _queue.Reader.TryRead(out byte[]? line))
                {
                    WriteLineCore(line);
                    dirty = true;
                    count++;
                }

                long dropped = Interlocked.Exchange(ref _droppedLines, 0);
                if (dropped > 0)
                {
                    AgentPerformanceTelemetry.RecordDroppedLogs(dropped);
                    WriteLineCore(Encoding.UTF8.GetBytes(
                        $"{{\"timestampUtc\":\"{DateTimeOffset.UtcNow:O}\",\"level\":\"Warning\",\"category\":\"DebtFlow.SipAgent.Logging\",\"messageTemplate\":\"log_queue_overflow\",\"properties\":{{\"droppedLines\":{dropped}}}}}"));
                    dirty = true;
                }

                TimeSpan sinceFlush = Stopwatch.GetElapsedTime(lastFlush);
                if (_writer != null && dirty && sinceFlush >= FlushInterval)
                {
                    await _writer.FlushAsync();
                    lastFlush = Stopwatch.GetTimestamp();
                    sinceFlush = TimeSpan.Zero;
                    dirty = false;
                }

                if (_queue.Reader.Completion.IsCompleted && !_queue.Reader.TryPeek(out _))
                {
                    break;
                }

                pendingRead ??= _queue.Reader.WaitToReadAsync().AsTask();
                if (_writer == null || !dirty)
                {
                    bool canRead = await pendingRead;
                    pendingRead = null;
                    if (!canRead)
                    {
                        break;
                    }
                    continue;
                }

                TimeSpan untilFlush = FlushInterval - sinceFlush;
                Task flushDelay = Task.Delay(untilFlush > TimeSpan.Zero ? untilFlush : TimeSpan.Zero);
                Task completed = await Task.WhenAny(pendingRead, flushDelay);
                if (completed == flushDelay)
                {
                    await _writer.FlushAsync();
                    lastFlush = Stopwatch.GetTimestamp();
                }
                else
                {
                    bool canRead = await pendingRead;
                    pendingRead = null;
                    if (!canRead)
                    {
                        break;
                    }
                }
            }
        }
        catch (Exception)
        {
        }
        finally
        {
            try
            {
                if (_writer != null)
                {
                    await _writer.FlushAsync();
                }
                _writer?.Dispose();
            }
            catch (IOException)
            {
            }

            _writer = null;
        }
    }

    private void WriteLineCore(byte[] line)
    {
        EnsureWriter(line.Length + NewLine.Length);
        _writer!.Write(line);
        _writer.Write(NewLine);
        if (_writeConsole)
        {
            Console.WriteLine(Encoding.UTF8.GetString(line));
        }
    }

    private void EnsureWriter(int nextBytes)
    {
        if (_writer != null && _writer.Length + nextBytes <= MaximumFileBytes)
        {
            return;
        }

        _writer?.Dispose();
        string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
        string currentPath = Path.Combine(_directory, $"agent-{stamp}.jsonl");
        _writer = new FileStream(
            currentPath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            16 * 1024,
            FileOptions.Asynchronous);

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
            json.WriteString(safeKey, SafeLogSanitizer.Sanitize(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty));
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
               normalized.Contains("sipheader", StringComparison.Ordinal) ||
               normalized.Contains("origin", StringComparison.Ordinal);
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

public static class SafeLogSanitizer
{
    private static readonly Regex SensitiveAssignment = new(
        @"(?i)\b(password|authorization|username|credential|secret|token|destination|caller|remoteParty|dtmf|digit|sdp|sipHeader)\b\s*[:=]?\s*[^\s,;]+",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
    private static readonly Regex LongNumber = new(
        @"(?<!\d)\d{7,}(?!\d)",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
    private static readonly Regex WebOrigin = new(
        @"(?i)https?://[^\s\"",]+",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    public static string Sanitize(string value)
    {
        string bounded = value.Length <= 4096 ? value : value[..4096];
        string assignmentsRemoved = SensitiveAssignment.Replace(bounded, "$1=[REDACTED]");
        string originsRemoved = WebOrigin.Replace(assignmentsRemoved, "[ORIGIN_REDACTED]");
        return LongNumber.Replace(originsRemoved, "[REDACTED]");
    }
}
