using System.Net;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Windows;
using DebtFlow.SipAgent.Application;

namespace DebtFlow.SipAgent.Host;

public sealed class SipRuntime : ISipRuntime
{
    private static readonly TimeSpan OutboundCallTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(30);
    private readonly ILogger<SipRuntime> _logger;
    private readonly IAudioPreferencesStore _audioPreferencesStore;
    private readonly bool _acceptRtpFromAny;
    private readonly SIPTransport _transport;
    private readonly SIPUserAgent _userAgent;
    private readonly SemaphoreSlim _mediaGate = new(1, 1);
    private readonly object _callGate = new();
    private readonly object _audioNotificationGate = new();
    private readonly Channel<SipSignal> _signals = Channel.CreateBounded<SipSignal>(
        new BoundedChannelOptions(512)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
    private readonly ConcurrentDictionary<long, Task> _backgroundTasks = new();
    private readonly Dictionary<string, SipCallHandle> _callHandlesBySipCallId = new(StringComparer.Ordinal);
    private readonly Dictionary<ISIPServerUserAgent, SipCallHandle> _incomingHandles = new();
    private readonly AudioSessionVolumeController _volumeController = new();
    private readonly MMDeviceEnumerator? _audioDeviceEnumerator;
    private readonly MMDeviceNotificationClient? _audioNotificationClient;
    private SIPRegistrationUserAgent? _registrationAgent;
    private SIPServerUserAgent? _pendingIncomingCall;
    private SipCallHandle? _pendingIncomingHandle;
    private SipCallHandle? _activeCallHandle;
    private SipCallHandle? _unmappedOutboundHandle;
    private WindowsAudioEndPoint? _audioEndPoint;
    private VoIPMediaSession? _mediaSession;
    private AudioExtrasSource? _fallbackAudioSource;
    private SipConfiguration? _configuration;
    private CancellationTokenSource? _audioNotificationDebounce;
    private int? _cachedPlaybackDevice;
    private bool _playbackDeviceCacheValid;
    private long _audioDeviceGeneration;
    private bool _useTcp;
    private int _microphoneMuted;
    private int _outputVolume;
    private int _inputVolume;
    private long _registrationGeneration;
    private long _callGeneration;
    private long _backgroundTaskId;
    private int _signalOverflow;
    private bool _disposed;

    public SipRuntime(
        ILogger<SipRuntime> logger,
        IAudioPreferencesStore audioPreferencesStore,
        AgentRuntimeOptions options)
    {
        _logger = logger;
        _audioPreferencesStore = audioPreferencesStore;
        _acceptRtpFromAny = options.AcceptRtpFromAny;
        AudioPreferences preferences = audioPreferencesStore.Load();
        _outputVolume = preferences.OutputVolume;
        _inputVolume = preferences.InputVolume;
        _transport = new SIPTransport();
        _transport.AddSIPChannel(new SIPUDPChannel(new IPEndPoint(IPAddress.Any, 0)));
        _transport.AddSIPChannel(new SIPTCPChannel(new IPEndPoint(IPAddress.Any, 0)));
        _userAgent = new SIPUserAgent(_transport, null, true);
        WireUserAgentEvents();
        AudioState = GetAudioState();
        try
        {
            _audioDeviceEnumerator = new MMDeviceEnumerator();
            _audioNotificationClient = _audioDeviceEnumerator.CreateNotificationClient(false);
            _audioNotificationClient.DeviceStateChanged += (_, _) => ScheduleAudioInventoryRefresh();
            _audioNotificationClient.DeviceAdded += (_, _) => ScheduleAudioInventoryRefresh();
            _audioNotificationClient.DeviceRemoved += (_, _) => ScheduleAudioInventoryRefresh();
            _audioNotificationClient.DefaultDeviceChanged += (_, _) => ScheduleAudioInventoryRefresh();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Audio device notifications are unavailable because of {ErrorType}",
                exception.GetType().Name);
        }
        _logger.LogInformation(
            "Audio inventory: captureDevices={CaptureCount}, playbackDevices={PlaybackCount}",
            WaveIn.DeviceCount,
            WaveOut.DeviceCount);
    }

    public ChannelReader<SipSignal> Signals => _signals.Reader;

    public string AudioState { get; private set; }
    public bool IsMicrophoneMuted => Volatile.Read(ref _microphoneMuted) != 0;
    public int OutputVolume => Volatile.Read(ref _outputVolume);
    public int InputVolume => Volatile.Read(ref _inputVolume);

    public Task ConfigureAsync(SipConfiguration configuration, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        _configuration = configuration;
        _useTcp = false;
        _registrationGeneration++;
        return Task.CompletedTask;
    }

    public Task StartRegistrationAsync(long generation, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        SipConfiguration configuration = RequireConfiguration();
        _registrationAgent?.Stop();
        _registrationGeneration = generation;
        var registrationAgent = new SIPRegistrationUserAgent(
            _transport,
            configuration.Username,
            configuration.Password,
            SipEndpointFormatter.FormatRegistrar(configuration, _useTcp),
            180);
        _registrationAgent = registrationAgent;

        registrationAgent.RegistrationFailed += (_, _, _) =>
            EmitRegistrationSignal(
                generation,
                new SipSignal(SipSignalType.RegistrationFailed, SafeCode: "registration_failed"));
        registrationAgent.RegistrationTemporaryFailure += (_, _, _) =>
        {
            if (generation == _registrationGeneration && !_useTcp)
            {
                _useTcp = true;
                _logger.LogInformation("SIP registration transport changed to {Transport}", "tcp");
            }

            EmitRegistrationSignal(
                generation,
                new SipSignal(
                    SipSignalType.RegistrationFailed,
                    SafeCode: "registration_transport_failure",
                    Retryable: true));
        };
        registrationAgent.RegistrationRemoved += (_, _) =>
            EmitRegistrationSignal(
                generation,
                new SipSignal(
                    SipSignalType.RegistrationFailed,
                    SafeCode: "registration_transport_lost",
                    Retryable: true));
        registrationAgent.RegistrationSuccessful += (_, _) =>
        {
            _logger.LogInformation("SIP registration succeeded over {Transport}", _useTcp ? "tcp" : "udp");
            EmitRegistrationSignal(generation, new SipSignal(SipSignalType.RegistrationRegistered));
        };

        Emit(new SipSignal(SipSignalType.RegistrationRegistering, RegistrationGeneration: generation));
        _registrationAgent.Start();
        return Task.CompletedTask;
    }

    public Task StopRegistrationAsync(long generation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _registrationGeneration = generation;
        _registrationAgent?.Stop();
        _registrationAgent = null;
        _configuration = null;
        return Task.CompletedTask;
    }

    public async Task StartCallAsync(
        SipCallHandle call,
        string destination,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SipConfiguration configuration = RequireConfiguration();
        SetActiveCall(call);
        lock (_callGate)
        {
            _unmappedOutboundHandle = call;
        }
        VoIPMediaSession mediaSession = CreateMediaSession(call);
        string destinationUri = SipEndpointFormatter.FormatDestination(configuration, destination, _useTcp);

        bool result;
        try
        {
            result = await _userAgent.Call(
                    destinationUri,
                    configuration.Username,
                    configuration.Password,
                    mediaSession)
                .WaitAsync(OutboundCallTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            _userAgent.Cancel();
            await CleanupMediaAsync();
            ClearActiveCall(call);
            Emit(new SipSignal(SipSignalType.CallFailed, SafeCode: "call_timeout", Call: call));
            return;
        }
        catch (OperationCanceledException)
        {
            _userAgent.Cancel();
            await CleanupMediaAsync();
            ClearActiveCall(call);
            throw;
        }
        catch
        {
            await CleanupMediaAsync();
            ClearActiveCall(call);
            throw;
        }

        if (result)
        {
            Emit(new SipSignal(SipSignalType.CallConnected, Call: call));
        }
        else
        {
            await CleanupMediaAsync();
            ClearActiveCall(call);
            Emit(new SipSignal(SipSignalType.CallFailed, SafeCode: "call_unavailable", Call: call));
        }
    }

    public async Task AnswerAsync(SipCallHandle call, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        SIPServerUserAgent incoming = TakePendingIncoming(call);
        bool answered;
        try
        {
            answered = await _userAgent.Answer(incoming, CreateMediaSession(call))
                .WaitAsync(AnswerTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            await CleanupMediaAsync();
            ClearActiveCall(call);
            Emit(new SipSignal(SipSignalType.CallFailed, SafeCode: "answer_timeout", Call: call));
            return;
        }
        catch
        {
            await CleanupMediaAsync();
            ClearActiveCall(call);
            throw;
        }
        if (!answered)
        {
            await CleanupMediaAsync();
            ClearActiveCall(call);
        }
        Emit(answered
            ? new SipSignal(SipSignalType.CallConnected, Call: call)
            : new SipSignal(SipSignalType.CallFailed, SafeCode: "answer_failed", Call: call));
    }

    public Task RejectAsync(SipCallHandle call, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SIPServerUserAgent incoming = TakePendingIncoming(call);
        incoming.Reject(SIPResponseStatusCodesEnum.BusyHere, "Rejected");
        ClearActiveCall(call);
        return Task.CompletedTask;
    }

    public Task RejectUnavailableAsync(SipCallHandle call, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SIPServerUserAgent incoming = TakePendingIncoming(call);
        incoming.Reject(SIPResponseStatusCodesEnum.TemporarilyUnavailable, "Temporarily unavailable");
        ClearActiveCall(call);
        return Task.CompletedTask;
    }

    public async Task HangupAsync(SipCallHandle call, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureActiveCall(call);
        SIPServerUserAgent? pending;
        lock (_callGate)
        {
            pending = SameCall(_pendingIncomingHandle, call) ? _pendingIncomingCall : null;
            _pendingIncomingCall = null;
            _pendingIncomingHandle = null;
        }
        if (pending != null)
        {
            pending.Reject(SIPResponseStatusCodesEnum.BusyHere, "Rejected");
        }
        else if (_userAgent.IsCalling || _userAgent.IsRinging)
        {
            _userAgent.Cancel();
        }
        else if (_userAgent.IsCallActive)
        {
            _userAgent.Hangup();
        }

        await CleanupMediaAsync();
        ClearActiveCall(call);
    }

    public Task SendDtmfAsync(SipCallHandle call, char digit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureActiveCall(call);
        byte tone = digit switch
        {
            >= '0' and <= '9' => (byte)(digit - '0'),
            '*' => 10,
            '#' => 11,
            'A' or 'a' => 12,
            'B' or 'b' => 13,
            'C' or 'c' => 14,
            'D' or 'd' => 15,
            _ => throw new ArgumentOutOfRangeException(nameof(digit))
        };
        return _userAgent.SendDtmf(tone);
    }

    public async Task SetMicrophoneMutedAsync(
        SipCallHandle call,
        bool muted,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureActiveCall(call);
        await _mediaGate.WaitAsync(cancellationToken);
        try
        {
            WindowsAudioEndPoint endpoint = _audioEndPoint
                ?? throw new InvalidOperationException("Audio capture session is unavailable.");
            if (muted)
            {
                await endpoint.PauseAudio();
            }
            else
            {
                await endpoint.ResumeAudio();
            }

            Volatile.Write(ref _microphoneMuted, muted ? 1 : 0);
        }
        finally
        {
            _mediaGate.Release();
        }
    }

    public async Task SetOutputVolumeAsync(
        SipCallHandle call,
        int volume,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureActiveCall(call);
        await SetSessionVolumeAsync(DataFlow.Render, volume, cancellationToken);
        int previous = Interlocked.Exchange(ref _outputVolume, volume);
        if (previous != volume)
        {
            PersistAudioPreferences();
        }
    }

    public async Task SetInputVolumeAsync(
        SipCallHandle call,
        int volume,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureActiveCall(call);
        await SetSessionVolumeAsync(DataFlow.Capture, volume, cancellationToken);
        int previous = Interlocked.Exchange(ref _inputVolume, volume);
        if (previous != volume)
        {
            PersistAudioPreferences();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _registrationGeneration++;
        _registrationAgent?.Stop();
        if (_userAgent.IsCallActive)
        {
            _userAgent.Hangup();
        }

        await CleanupMediaAsync();
        SipCallHandle? active = GetActiveCall();
        if (active != null) ClearActiveCall(active);
        lock (_audioNotificationGate)
        {
            _audioNotificationDebounce?.Cancel();
            _audioNotificationDebounce?.Dispose();
            _audioNotificationDebounce = null;
        }
        _audioNotificationClient?.Dispose();
        _audioDeviceEnumerator?.Dispose();
        _transport.Shutdown();
        _configuration = null;
        Task[] background = _backgroundTasks.Values.ToArray();
        if (background.Length > 0)
        {
            try
            {
                await Task.WhenAll(background).WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
            {
            }
        }
        _signals.Writer.TryComplete();
        _mediaGate.Dispose();
    }

    private void WireUserAgentEvents()
    {
        _userAgent.OnIncomingCall += (agent, request) =>
        {
            try
            {
                if (!IsExpectedSipPeer(request))
                {
                    SIPServerUserAgent forbidden = agent.AcceptCall(request);
                    forbidden.Reject(SIPResponseStatusCodesEnum.Forbidden, "Unexpected SIP peer");
                    _logger.LogWarning("Rejected incoming SIP request from an unexpected peer");
                    return;
                }

                SIPServerUserAgent? accepted = null;
                SipCallHandle? call = null;
                lock (_callGate)
                {
                    if (_pendingIncomingCall == null &&
                        _activeCallHandle == null &&
                        !agent.IsCallActive &&
                        !agent.IsCalling &&
                        !agent.IsRinging)
                    {
                        accepted = agent.AcceptCall(request);
                        call = new SipCallHandle(
                            Guid.NewGuid().ToString("D").ToLowerInvariant(),
                            ++_callGeneration);
                        _pendingIncomingCall = accepted;
                        _pendingIncomingHandle = call;
                        _activeCallHandle = call;
                        _incomingHandles[accepted] = call;
                        RememberSipCallId(request.Header?.CallId, call);
                    }
                }

                if (accepted == null || call == null)
                {
                    SIPServerUserAgent busyCall = agent.AcceptCall(request);
                    busyCall.Reject(SIPResponseStatusCodesEnum.BusyHere, "Busy");
                    return;
                }

                string caller = request.Header?.From?.FromURI?.User ?? "unknown";
                Emit(new SipSignal(SipSignalType.IncomingCall, Caller: caller, Call: call));
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    "Incoming SIP call callback failed because of {ErrorType}",
                    exception.GetType().Name);
            }
        };

        _userAgent.ServerCallCancelled += (incoming, _) =>
        {
            SipCallHandle? call = ResolveIncomingHandle(incoming);
            if (call == null) return;
            ClearPendingIncoming(call);
            TrackBackground(CleanupMediaForCallAsync(call));
            Emit(new SipSignal(SipSignalType.IncomingCancelled, Call: call));
        };
        _userAgent.ServerCallRingTimeout += incoming =>
        {
            SipCallHandle? call = ResolveIncomingHandle(incoming);
            if (call == null) return;
            ClearPendingIncoming(call);
            TrackBackground(CleanupMediaForCallAsync(call));
            Emit(new SipSignal(SipSignalType.CallFailed, SafeCode: "answer_ack_timeout", Call: call));
        };
        _userAgent.OnDtmfTone += (_, _) =>
        {
            SipCallHandle? call = GetActiveCall();
            if (call != null) Emit(new SipSignal(SipSignalType.DtmfReceived, Call: call));
        };
        _userAgent.OnCallHungup += dialogue =>
        {
            SipCallHandle? call = ResolveSipCallId(dialogue.CallId);
            if (call == null) return;
            TrackBackground(CleanupMediaForCallAsync(call));
            Emit(new SipSignal(SipSignalType.CallRemoteEnded, Call: call));
        };
        _userAgent.ClientCallTrying += (_, response) =>
        {
            SipCallHandle? call = ResolveOrRememberOutboundHandle(response.Header?.CallId);
            if (call != null) Emit(new SipSignal(SipSignalType.CallTrying, (int)response.Status, Call: call));
        };
        _userAgent.ClientCallRinging += (_, response) =>
        {
            SipCallHandle? call = ResolveOrRememberOutboundHandle(response.Header?.CallId);
            if (call != null) Emit(new SipSignal(SipSignalType.CallRinging, (int)response.Status, Call: call));
        };
        _userAgent.ClientCallAnswered += (_, response) =>
        {
            SipCallHandle? call = ResolveOrRememberOutboundHandle(response.Header?.CallId);
            if (call != null) Emit(new SipSignal(SipSignalType.CallConnected, (int)response.Status, Call: call));
        };
        _userAgent.ClientCallFailed += (_, _, response) =>
        {
            SipCallHandle? call = response == null
                ? GetUnmappedOutboundCall()
                : ResolveOrRememberOutboundHandle(response.Header?.CallId);
            if (call == null) return;
            TrackBackground(CleanupMediaForCallAsync(call));
            Emit(new SipSignal(
                SipSignalType.CallFailed,
                response == null ? null : (int)response.Status,
                "sip_call_failed",
                Call: call));
        };
    }

    private VoIPMediaSession CreateMediaSession(SipCallHandle call)
    {
        var encoder = new AudioEncoder();
        bool hasCapture = WaveIn.DeviceCount > 0;
        int? playbackDevice = GetWorkingPlaybackDevice();
        bool hasPlayback = playbackDevice.HasValue;

        _audioEndPoint = new WindowsAudioEndPoint(
            encoder,
            audioOutDeviceIndex: hasPlayback ? playbackDevice!.Value : -1,
            audioInDeviceIndex: hasCapture ? 0 : -1,
            disableSource: !hasCapture,
            disableSink: !hasPlayback);
        Volatile.Write(ref _microphoneMuted, 0);

        _audioEndPoint.OnAudioSourceError += _ =>
            Emit(new SipSignal(SipSignalType.MediaDegraded, SafeCode: "audio_source_error", Call: call));
        _audioEndPoint.OnAudioSinkError += _ =>
            Emit(new SipSignal(SipSignalType.MediaDegraded, SafeCode: "audio_sink_error", Call: call));

        IAudioSource source;
        if (hasCapture)
        {
            source = _audioEndPoint;
            _fallbackAudioSource = null;
        }
        else
        {
            _fallbackAudioSource = new AudioExtrasSource(
                encoder,
                new AudioSourceOptions { AudioSource = AudioSourcesEnum.Silence });
            source = _fallbackAudioSource;
        }

        IAudioSink? sink = hasPlayback ? _audioEndPoint : null;
        if (!hasCapture || !hasPlayback)
        {
            AudioState = "degraded";
            Emit(new SipSignal(SipSignalType.MediaDegraded, SafeCode: "audio_device_unavailable", Call: call));
        }

        _mediaSession = new VoIPMediaSession(new MediaEndPoints
        {
            AudioSource = source,
            AudioSink = sink
        })
        {
            AcceptRtpFromAny = _acceptRtpFromAny
        };

        _mediaSession.OnAudioFormatsNegotiated += formats =>
        {
            string codec = string.Join(",", formats.Select(format => $"{format.Codec}/{format.ClockRate}"));
            TrackBackground(ApplyRememberedVolumesAsync());
            Emit(new SipSignal(SipSignalType.MediaReady, Codec: codec, Call: call));
        };
        return _mediaSession;
    }

    private async Task SetSessionVolumeAsync(
        DataFlow dataFlow,
        int volume,
        CancellationToken cancellationToken)
    {
        if (volume is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(volume));
        }

        for (int attempt = 0; attempt < 5; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_volumeController.TrySetProcessVolume(dataFlow, volume))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }

        throw new InvalidOperationException("Audio session is unavailable.");
    }

    private async Task ApplyRememberedVolumesAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await SetSessionVolumeAsync(DataFlow.Render, OutputVolume, timeout.Token);
            await SetSessionVolumeAsync(DataFlow.Capture, InputVolume, timeout.Token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(
                "Remembered audio volume could not be applied because of {ErrorType}",
                exception.GetType().Name);
        }
    }

    private void PersistAudioPreferences()
    {
        try
        {
            _audioPreferencesStore.Save(new AudioPreferences(OutputVolume, InputVolume));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogWarning(
                "Audio preferences could not be saved because of {ErrorType}",
                exception.GetType().Name);
        }
    }

    private int? FindWorkingPlaybackDevice()
    {
        if (WaveOut.DeviceCount <= 0)
        {
            return null;
        }

        foreach (int deviceIndex in new[] { -1 }.Concat(Enumerable.Range(0, WaveOut.DeviceCount)).Distinct())
        {
            try
            {
                using var output = new WaveOut { DeviceNumber = deviceIndex };
                output.Init(new BufferedWaveProvider(new WaveFormat(8000, 16, 1))
                {
                    DiscardOnBufferOverflow = true
                });
                output.Stop();
                return deviceIndex;
            }
            catch (Exception)
            {
                _logger.LogWarning("Audio playback probe failed for device index {DeviceIndex}", deviceIndex);
            }
        }

        return null;
    }

    private int? GetWorkingPlaybackDevice()
    {
        long started = Stopwatch.GetTimestamp();
        long generation;
        lock (_audioNotificationGate)
        {
            if (_playbackDeviceCacheValid)
            {
                AgentPerformanceTelemetry.RecordAudioProbe(
                    Stopwatch.GetElapsedTime(started),
                    cacheHit: true);
                return _cachedPlaybackDevice;
            }

            generation = _audioDeviceGeneration;
        }

        int? discovered = FindWorkingPlaybackDevice();
        lock (_audioNotificationGate)
        {
            if (generation == _audioDeviceGeneration)
            {
                _cachedPlaybackDevice = discovered;
                _playbackDeviceCacheValid = true;
            }
        }

        AgentPerformanceTelemetry.RecordAudioProbe(
            Stopwatch.GetElapsedTime(started),
            cacheHit: false);
        return discovered;
    }

    private async Task CleanupMediaAsync()
    {
        await _mediaGate.WaitAsync();
        VoIPMediaSession? mediaSession = _mediaSession;
        _mediaSession = null;
        _fallbackAudioSource = null;
        _audioEndPoint = null;
        Volatile.Write(ref _microphoneMuted, 0);
        try
        {
            mediaSession?.Close("call ended");
        }
        catch (Exception exception)
        {
            _logger.LogWarning("Media cleanup failed with {ErrorType}", exception.GetType().Name);
        }
        finally
        {
            try
            {
                mediaSession?.Dispose();
            }
            catch (Exception exception)
            {
                _logger.LogWarning("Media disposal failed with {ErrorType}", exception.GetType().Name);
            }

            _mediaGate.Release();
        }
    }

    private void ScheduleAudioInventoryRefresh()
    {
        if (_disposed)
        {
            return;
        }

        CancellationToken token;
        lock (_audioNotificationGate)
        {
            _audioDeviceGeneration++;
            _cachedPlaybackDevice = null;
            _playbackDeviceCacheValid = false;
            _volumeController.Invalidate();
            _audioNotificationDebounce?.Cancel();
            _audioNotificationDebounce?.Dispose();
            _audioNotificationDebounce = new CancellationTokenSource();
            token = _audioNotificationDebounce.Token;
        }

        _ = RefreshAudioInventoryAfterDebounceAsync(token);
    }

    private async Task RefreshAudioInventoryAfterDebounceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            string previous = AudioState;
            AudioState = GetAudioState();
            _logger.LogInformation(
                "Audio inventory changed: captureDevices={CaptureCount}, playbackDevices={PlaybackCount}, state={State}",
                WaveIn.DeviceCount,
                WaveOut.DeviceCount,
                AudioState);
            Emit(new SipSignal(SipSignalType.AudioInventoryChanged, SafeCode: "audio_devices_changed"));
            if (AudioState == "degraded" && previous != AudioState && _userAgent.IsCallActive)
            {
                Emit(new SipSignal(
                    SipSignalType.MediaDegraded,
                    SafeCode: "audio_device_removed",
                    Call: GetActiveCall()));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Audio inventory refresh failed because of {ErrorType}",
                exception.GetType().Name);
        }
    }

    private void EmitRegistrationSignal(long generation, SipSignal signal)
    {
        if (generation == _registrationGeneration && !_disposed)
        {
            Emit(signal with { RegistrationGeneration = generation });
        }
    }

    private static string GetAudioState() =>
        WaveIn.DeviceCount > 0 && WaveOut.DeviceCount > 0 ? "ready" : "degraded";

    private void Emit(SipSignal signal)
    {
        if (_disposed)
        {
            return;
        }

        if (_signals.Writer.TryWrite(signal))
        {
            return;
        }

        if (Interlocked.Exchange(ref _signalOverflow, 1) == 0)
        {
            _logger.LogError("SIP signal channel overflowed; runtime is entering a degraded state");
            TrackBackground(ReportSignalOverflowAsync());
        }
    }

    private async Task ReportSignalOverflowAsync()
    {
        try
        {
            await _signals.Writer.WriteAsync(new SipSignal(
                SipSignalType.RuntimeFailed,
                SafeCode: "sip_signal_overflow"));
        }
        catch (ChannelClosedException)
        {
        }
    }

    private bool IsExpectedSipPeer(SIPRequest request)
    {
        SipConfiguration? configuration = _configuration;
        IPAddress? remote = request.RemoteSIPEndPoint?.Address;
        if (configuration == null || remote == null || !IPAddress.TryParse(configuration.Host, out IPAddress? expected))
        {
            return false;
        }

        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
        if (expected.IsIPv4MappedToIPv6) expected = expected.MapToIPv4();
        return remote.Equals(expected);
    }

    private void SetActiveCall(SipCallHandle call)
    {
        lock (_callGate)
        {
            if (_activeCallHandle != null && !SameCall(_activeCallHandle, call))
            {
                throw new InvalidOperationException("Another SIP call is active.");
            }

            _activeCallHandle = call;
            _callGeneration = Math.Max(_callGeneration, call.Generation);
        }
    }

    private SipCallHandle? GetActiveCall()
    {
        lock (_callGate)
        {
            return _activeCallHandle;
        }
    }

    private void EnsureActiveCall(SipCallHandle call)
    {
        lock (_callGate)
        {
            if (!SameCall(_activeCallHandle, call))
            {
                throw new InvalidOperationException("The SIP call handle is stale.");
            }
        }
    }

    private SIPServerUserAgent TakePendingIncoming(SipCallHandle call)
    {
        lock (_callGate)
        {
            if (_pendingIncomingCall == null ||
                !SameCall(_pendingIncomingHandle, call) ||
                !SameCall(_activeCallHandle, call))
            {
                throw new InvalidOperationException("No matching incoming call is pending.");
            }

            SIPServerUserAgent incoming = _pendingIncomingCall;
            _pendingIncomingCall = null;
            _pendingIncomingHandle = null;
            return incoming;
        }
    }

    private SipCallHandle? ResolveIncomingHandle(ISIPServerUserAgent incoming)
    {
        lock (_callGate)
        {
            return _incomingHandles.GetValueOrDefault(incoming);
        }
    }

    private void ClearPendingIncoming(SipCallHandle call)
    {
        lock (_callGate)
        {
            if (SameCall(_pendingIncomingHandle, call))
            {
                _pendingIncomingCall = null;
                _pendingIncomingHandle = null;
            }
        }
    }

    private void ClearActiveCall(SipCallHandle call)
    {
        lock (_callGate)
        {
            if (!SameCall(_activeCallHandle, call)) return;
            _activeCallHandle = null;
            if (SameCall(_unmappedOutboundHandle, call))
            {
                _unmappedOutboundHandle = null;
            }
            if (SameCall(_pendingIncomingHandle, call))
            {
                _pendingIncomingCall = null;
                _pendingIncomingHandle = null;
            }

            foreach (string key in _callHandlesBySipCallId
                         .Where(pair => SameCall(pair.Value, call))
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                _callHandlesBySipCallId.Remove(key);
            }

            foreach (ISIPServerUserAgent key in _incomingHandles
                         .Where(pair => SameCall(pair.Value, call))
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                _incomingHandles.Remove(key);
            }
        }
    }

    private void RememberSipCallId(string? sipCallId, SipCallHandle call)
    {
        if (!string.IsNullOrWhiteSpace(sipCallId))
        {
            _callHandlesBySipCallId[sipCallId] = call;
        }
    }

    private SipCallHandle? ResolveSipCallId(string? sipCallId)
    {
        if (string.IsNullOrWhiteSpace(sipCallId)) return null;
        lock (_callGate)
        {
            return _callHandlesBySipCallId.GetValueOrDefault(sipCallId);
        }
    }

    private SipCallHandle? ResolveOrRememberOutboundHandle(string? sipCallId)
    {
        lock (_callGate)
        {
            if (!string.IsNullOrWhiteSpace(sipCallId) &&
                _callHandlesBySipCallId.TryGetValue(sipCallId, out SipCallHandle? known))
            {
                return known;
            }

            if (string.IsNullOrWhiteSpace(sipCallId)) return null;

            SipCallHandle? outbound = _unmappedOutboundHandle;
            if (outbound == null || !SameCall(outbound, _activeCallHandle)) return null;
            RememberSipCallId(sipCallId, outbound);
            _unmappedOutboundHandle = null;
            return outbound;
        }
    }

    private SipCallHandle? GetUnmappedOutboundCall()
    {
        lock (_callGate)
        {
            return SameCall(_unmappedOutboundHandle, _activeCallHandle)
                ? _unmappedOutboundHandle
                : null;
        }
    }

    private async Task CleanupMediaForCallAsync(SipCallHandle call)
    {
        try
        {
            EnsureActiveCall(call);
            await CleanupMediaAsync();
            ClearActiveCall(call);
        }
        catch (InvalidOperationException)
        {
        }
        catch (Exception exception)
        {
            _logger.LogWarning("Media cleanup callback failed because of {ErrorType}", exception.GetType().Name);
        }
    }

    private void TrackBackground(Task task)
    {
        long id = Interlocked.Increment(ref _backgroundTaskId);
        _backgroundTasks[id] = task;
        _ = task.ContinueWith(
            completed => _backgroundTasks.TryRemove(id, out _),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static bool SameCall(SipCallHandle? left, SipCallHandle? right) =>
        left != null && right != null &&
        left.Generation == right.Generation &&
        string.Equals(left.RuntimeCallId, right.RuntimeCallId, StringComparison.Ordinal);

    private SipConfiguration RequireConfiguration() =>
        _configuration ?? throw new InvalidOperationException("SIP runtime is not configured.");

}
