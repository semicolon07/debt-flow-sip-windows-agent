using System.Net;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
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
using DebtFlow.SipAgent.Protocol;

namespace DebtFlow.SipAgent.Host;

public sealed class SipRuntime : ISipRuntime
{
    private static readonly TimeSpan OutboundCallTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PlaybackDeviceCacheLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MissingPlaybackDeviceCacheLifetime = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan AudioInventoryCacheLifetime = TimeSpan.FromSeconds(30);
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
    private long _playbackDeviceCacheExpiresAt;
    private AudioDevicesSnapshot _audioDevicesSnapshot = CreateFallbackAudioDevicesSnapshot();
    private long _audioInventoryCacheExpiresAt;
    private int _audioInventoryRefreshScheduled;
    private long _audioDeviceGeneration;
    private bool _useTcp;
    private int _microphoneMuted;
    private int _outputVolume;
    private int _inputVolume;
    private string _outputDeviceId;
    private string _inputDeviceId;
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
        _outputDeviceId = preferences.OutputDeviceId;
        _inputDeviceId = preferences.InputDeviceId;
        _transport = new SIPTransport();
        _transport.AddSIPChannel(new SIPUDPChannel(new IPEndPoint(IPAddress.Any, 0)));
        _transport.AddSIPChannel(new SIPTCPChannel(new IPEndPoint(IPAddress.Any, 0)));
        _userAgent = new SIPUserAgent(_transport, null, true);
        WireUserAgentEvents();
        AudioState = "degraded";
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
        AudioDevicesSnapshot inventory = RefreshAudioDevicesSnapshotCore();
        AudioState = GetAudioState(inventory);
        _logger.LogInformation(
            "Audio inventory: captureDevices={CaptureCount}, playbackDevices={PlaybackCount}",
            Math.Max(0, inventory.InputDevices.Count - 1),
            Math.Max(0, inventory.OutputDevices.Count - 1));
    }

    public ChannelReader<SipSignal> Signals => _signals.Reader;

    public string AudioState { get; private set; }
    public bool IsMicrophoneMuted => Volatile.Read(ref _microphoneMuted) != 0;
    public int OutputVolume => Volatile.Read(ref _outputVolume);
    public int InputVolume => Volatile.Read(ref _inputVolume);
    public AudioDevicesSnapshot AudioDevices
    {
        get
        {
            ScheduleAudioInventoryFallbackRefreshIfExpired();
            return Volatile.Read(ref _audioDevicesSnapshot);
        }
    }

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

    public Task SetOutputVolumePreferenceAsync(int volume, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        UpdateOutputVolumePreference(volume);
        return Task.CompletedTask;
    }

    public Task SetInputVolumePreferenceAsync(int volume, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        UpdateInputVolumePreference(volume);
        return Task.CompletedTask;
    }

    public Task SetAudioDevicePreferencesAsync(
        string outputDeviceId,
        string inputDeviceId,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        _ = ResolveOutputDeviceIndex(outputDeviceId, requireAvailable: true);
        _ = ResolveInputDeviceIndex(inputDeviceId, requireAvailable: true);
        bool changed = false;
        lock (_audioNotificationGate)
        {
            if (!string.Equals(_outputDeviceId, outputDeviceId, StringComparison.Ordinal))
            {
                _outputDeviceId = outputDeviceId;
                changed = true;
            }
            if (!string.Equals(_inputDeviceId, inputDeviceId, StringComparison.Ordinal))
            {
                _inputDeviceId = inputDeviceId;
                changed = true;
            }
            InvalidateAudioDeviceCachesCore();
            AudioDevicesSnapshot snapshot = Volatile.Read(ref _audioDevicesSnapshot);
            Volatile.Write(
                ref _audioDevicesSnapshot,
                snapshot with
                {
                    SelectedOutputDeviceId = outputDeviceId,
                    SelectedInputDeviceId = inputDeviceId
                });
        }
        if (changed) PersistAudioPreferences();
        return Task.CompletedTask;
    }

    public async Task TestOutputDeviceAsync(string deviceId, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int deviceIndex = ResolveOutputDeviceIndex(deviceId, requireAvailable: true)
            ?? throw new InvalidOperationException("Audio output device is unavailable.");
        var format = new WaveFormat(16_000, 16, 1);
        byte[] samples = CreateTestTone(format, TimeSpan.FromMilliseconds(650));
        using var stream = new RawSourceWaveStream(new MemoryStream(samples, writable: false), format);
        using var output = new WaveOut { DeviceNumber = deviceIndex };
        output.Init(stream);
        output.Play();
        await Task.Delay(TimeSpan.FromMilliseconds(700), cancellationToken);
        output.Stop();
    }

    public async Task<int> TestInputDeviceAsync(string deviceId, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int deviceIndex = ResolveInputDeviceIndex(deviceId, requireAvailable: true)
            ?? throw new InvalidOperationException("Audio input device is unavailable.");
        int peak = 0;
        using var input = new WaveIn
        {
            DeviceNumber = deviceIndex,
            WaveFormat = new WaveFormat(16_000, 16, 1),
            BufferMilliseconds = 80
        };
        input.DataAvailable += (_, eventArgs) =>
        {
            for (int offset = 0; offset + 1 < eventArgs.BytesRecorded; offset += 2)
            {
                int sample = Math.Abs(BitConverter.ToInt16(eventArgs.Buffer, offset));
                int observed;
                do
                {
                    observed = Volatile.Read(ref peak);
                    if (sample <= observed) break;
                }
                while (Interlocked.CompareExchange(ref peak, sample, observed) != observed);
            }
        };
        input.StartRecording();
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(900), cancellationToken);
        }
        finally
        {
            input.StopRecording();
        }
        return Math.Clamp((int)Math.Round(Volatile.Read(ref peak) / 32767d * 100d), 0, 100);
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
        string outputDeviceId;
        string inputDeviceId;
        lock (_audioNotificationGate)
        {
            outputDeviceId = _outputDeviceId;
            inputDeviceId = _inputDeviceId;
        }

        (
            WindowsAudioEndPoint endpoint,
            int? captureDevice,
            int? playbackDevice) = CreateResilientAudioEndPoint(
                encoder,
                inputDeviceId,
                outputDeviceId);
        _audioEndPoint = endpoint;
        bool hasCapture = captureDevice.HasValue;
        bool hasPlayback = playbackDevice.HasValue;
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

    private static WindowsAudioEndPoint CreateWindowsAudioEndPoint(
        AudioEncoder encoder,
        int? captureDevice,
        int? playbackDevice) =>
        new(
            encoder,
            audioOutDeviceIndex: playbackDevice ?? -1,
            audioInDeviceIndex: captureDevice ?? -1,
            disableSource: !captureDevice.HasValue,
            disableSink: !playbackDevice.HasValue);

    private (WindowsAudioEndPoint Endpoint, int? CaptureDevice, int? PlaybackDevice)
        CreateResilientAudioEndPoint(
            AudioEncoder encoder,
            string inputDeviceId,
            string outputDeviceId)
    {
        int? captureDevice = ResolveInputDeviceIndex(inputDeviceId, requireAvailable: false);
        int? playbackDevice = ResolveOutputDeviceIndex(outputDeviceId, requireAvailable: false);
        try
        {
            return (
                CreateWindowsAudioEndPoint(encoder, captureDevice, playbackDevice),
                captureDevice,
                playbackDevice);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Audio endpoint initialization is retrying after {ErrorType}",
                exception.GetType().Name);
        }

        InvalidatePlaybackDeviceCache();
        captureDevice = ResolveInputDeviceIndex(inputDeviceId, requireAvailable: false);
        playbackDevice = ResolveOutputDeviceIndex(outputDeviceId, requireAvailable: false);
        try
        {
            return (
                CreateWindowsAudioEndPoint(encoder, captureDevice, playbackDevice),
                captureDevice,
                playbackDevice);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Audio endpoint initialization is falling back to a single device after {ErrorType}",
                exception.GetType().Name);
        }

        if (captureDevice.HasValue)
        {
            try
            {
                return (
                    CreateWindowsAudioEndPoint(encoder, captureDevice, playbackDevice: null),
                    captureDevice,
                    null);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    "Audio capture fallback is unavailable because of {ErrorType}",
                    exception.GetType().Name);
            }
        }

        if (playbackDevice.HasValue)
        {
            try
            {
                return (
                    CreateWindowsAudioEndPoint(
                        encoder,
                        captureDevice: null,
                        playbackDevice: playbackDevice),
                    null,
                    playbackDevice);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    "Audio playback fallback is unavailable because of {ErrorType}",
                    exception.GetType().Name);
            }
        }

        return (CreateWindowsAudioEndPoint(encoder, null, null), null, null);
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
            string outputDeviceId;
            string inputDeviceId;
            lock (_audioNotificationGate)
            {
                outputDeviceId = _outputDeviceId;
                inputDeviceId = _inputDeviceId;
            }
            _audioPreferencesStore.Save(
                new AudioPreferences(OutputVolume, InputVolume, outputDeviceId, inputDeviceId));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogWarning(
                "Audio preferences could not be saved because of {ErrorType}",
                exception.GetType().Name);
        }
    }

    private void UpdateOutputVolumePreference(int volume)
    {
        if (volume is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(volume));
        }

        if (Interlocked.Exchange(ref _outputVolume, volume) != volume)
        {
            PersistAudioPreferences();
        }
    }

    private void UpdateInputVolumePreference(int volume)
    {
        if (volume is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(volume));
        }

        if (Interlocked.Exchange(ref _inputVolume, volume) != volume)
        {
            PersistAudioPreferences();
        }
    }

    private int? FindWorkingPlaybackDevice()
    {
        int deviceCount = GetOutputDeviceCount();
        if (deviceCount <= 0)
        {
            return null;
        }

        foreach (int deviceIndex in new[] { -1 }.Concat(Enumerable.Range(0, deviceCount)).Distinct())
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

    private AudioDevicesSnapshot CreateAudioDevicesSnapshot()
    {
        IReadOnlyList<WaveDeviceEntry> outputDevices = EnumerateOutputDevices();
        IReadOnlyList<WaveDeviceEntry> inputDevices = EnumerateInputDevices();
        string requestedOutput;
        string requestedInput;
        lock (_audioNotificationGate)
        {
            requestedOutput = _outputDeviceId;
            requestedInput = _inputDeviceId;
        }
        string selectedOutput = outputDevices.Any(device => device.DeviceId == requestedOutput)
            ? requestedOutput
            : AudioPreferences.SystemDefaultDeviceId;
        string selectedInput = inputDevices.Any(device => device.DeviceId == requestedInput)
            ? requestedInput
            : AudioPreferences.SystemDefaultDeviceId;
        return new AudioDevicesSnapshot(
            Array.AsReadOnly(outputDevices.Select(ToSnapshot).ToArray()),
            Array.AsReadOnly(inputDevices.Select(ToSnapshot).ToArray()),
            selectedOutput,
            selectedInput);
    }

    private static AudioDevicesSnapshot CreateFallbackAudioDevicesSnapshot()
    {
        var systemDefault = new AudioDeviceSnapshot(
            AudioPreferences.SystemDefaultDeviceId,
            "System default",
            true);
        return new AudioDevicesSnapshot(
            Array.AsReadOnly(new[] { systemDefault }),
            Array.AsReadOnly(new[] { systemDefault }),
            AudioPreferences.SystemDefaultDeviceId,
            AudioPreferences.SystemDefaultDeviceId);
    }

    private AudioDevicesSnapshot RefreshAudioDevicesSnapshotCore()
    {
        AudioDevicesSnapshot snapshot = CreateAudioDevicesSnapshot();
        Volatile.Write(ref _audioDevicesSnapshot, snapshot);
        Volatile.Write(
            ref _audioInventoryCacheExpiresAt,
            GetExpirationTimestamp(AudioInventoryCacheLifetime));
        return snapshot;
    }

    private void ScheduleAudioInventoryFallbackRefreshIfExpired()
    {
        if (_disposed || Stopwatch.GetTimestamp() < Volatile.Read(ref _audioInventoryCacheExpiresAt))
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _audioInventoryRefreshScheduled, 1, 0) != 0)
        {
            return;
        }

        TrackBackground(RefreshAudioInventoryFallbackAsync());
    }

    private async Task RefreshAudioInventoryFallbackAsync()
    {
        try
        {
            await Task.Yield();
            if (_disposed)
            {
                return;
            }

            AudioDevicesSnapshot previousInventory = Volatile.Read(ref _audioDevicesSnapshot);
            string previousState = AudioState;
            AudioDevicesSnapshot inventory = RefreshAudioDevicesSnapshotCore();
            AudioState = GetAudioState(inventory);
            if (!AudioInventoriesEqual(previousInventory, inventory) || previousState != AudioState)
            {
                Emit(new SipSignal(SipSignalType.AudioInventoryChanged, SafeCode: "audio_devices_changed"));
                if (AudioState == "degraded" && previousState != AudioState && _userAgent.IsCallActive)
                {
                    Emit(new SipSignal(
                        SipSignalType.MediaDegraded,
                        SafeCode: "audio_device_removed",
                        Call: GetActiveCall()));
                }
            }
        }
        catch (Exception exception)
        {
            Volatile.Write(
                ref _audioInventoryCacheExpiresAt,
                GetExpirationTimestamp(MissingPlaybackDeviceCacheLifetime));
            _logger.LogWarning(
                "Fallback audio inventory refresh failed because of {ErrorType}",
                exception.GetType().Name);
        }
        finally
        {
            Volatile.Write(ref _audioInventoryRefreshScheduled, 0);
        }
    }

    private static bool AudioInventoriesEqual(
        AudioDevicesSnapshot left,
        AudioDevicesSnapshot right) =>
        string.Equals(left.SelectedOutputDeviceId, right.SelectedOutputDeviceId, StringComparison.Ordinal) &&
        string.Equals(left.SelectedInputDeviceId, right.SelectedInputDeviceId, StringComparison.Ordinal) &&
        left.OutputDevices.SequenceEqual(right.OutputDevices) &&
        left.InputDevices.SequenceEqual(right.InputDevices);

    private int? ResolveOutputDeviceIndex(string deviceId, bool requireAvailable)
    {
        if (deviceId == AudioPreferences.SystemDefaultDeviceId)
        {
            int? defaultDevice = GetWorkingPlaybackDevice();
            if (defaultDevice.HasValue || !requireAvailable) return defaultDevice;
            throw new InvalidOperationException("Audio output device is unavailable.");
        }

        WaveDeviceEntry? selected = EnumerateOutputDevices()
            .FirstOrDefault(device => device.DeviceId == deviceId);
        if (selected is not null) return selected.DeviceIndex;
        if (!requireAvailable) return GetWorkingPlaybackDevice();
        throw new InvalidOperationException("Audio output device is unavailable.");
    }

    private int? ResolveInputDeviceIndex(string deviceId, bool requireAvailable)
    {
        if (deviceId == AudioPreferences.SystemDefaultDeviceId)
        {
            if (GetInputDeviceCount() > 0) return 0;
            if (!requireAvailable) return null;
            throw new InvalidOperationException("Audio input device is unavailable.");
        }

        WaveDeviceEntry? selected = EnumerateInputDevices()
            .FirstOrDefault(device => device.DeviceId == deviceId);
        if (selected is not null) return selected.DeviceIndex;
        if (!requireAvailable) return GetInputDeviceCount() > 0 ? 0 : null;
        throw new InvalidOperationException("Audio input device is unavailable.");
    }

    private IReadOnlyList<WaveDeviceEntry> EnumerateOutputDevices()
    {
        var devices = new List<WaveDeviceEntry>
        {
            new(AudioPreferences.SystemDefaultDeviceId, "System default", -1, true)
        };
        var identifierCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        int deviceCount = GetOutputDeviceCount();
        for (int index = 0; index < deviceCount; index++)
        {
            try
            {
                WaveOutCapabilities capabilities = WaveOut.GetCapabilities(index);
                string identifier = EnsureUniqueDeviceId(
                    CreateDeviceId("output", capabilities.ProductGuid, capabilities.NameGuid, capabilities.ProductName),
                    identifierCounts);
                devices.Add(new WaveDeviceEntry(
                    identifier,
                    capabilities.ProductName,
                    index,
                    false));
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    "Audio output device enumeration skipped index {DeviceIndex} because of {ErrorType}",
                    index,
                    exception.GetType().Name);
            }
        }
        return devices;
    }

    private IReadOnlyList<WaveDeviceEntry> EnumerateInputDevices()
    {
        var devices = new List<WaveDeviceEntry>
        {
            new(AudioPreferences.SystemDefaultDeviceId, "System default", 0, true)
        };
        var identifierCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        int deviceCount = GetInputDeviceCount();
        for (int index = 0; index < deviceCount; index++)
        {
            try
            {
                WaveInCapabilities capabilities = WaveIn.GetCapabilities(index);
                string identifier = EnsureUniqueDeviceId(
                    CreateDeviceId("input", capabilities.ProductGuid, capabilities.NameGuid, capabilities.ProductName),
                    identifierCounts);
                devices.Add(new WaveDeviceEntry(
                    identifier,
                    capabilities.ProductName,
                    index,
                    false));
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    "Audio input device enumeration skipped index {DeviceIndex} because of {ErrorType}",
                    index,
                    exception.GetType().Name);
            }
        }
        return devices;
    }

    private int GetOutputDeviceCount()
    {
        try
        {
            return Math.Max(0, WaveOut.DeviceCount);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Audio output device count is unavailable because of {ErrorType}",
                exception.GetType().Name);
            return 0;
        }
    }

    private int GetInputDeviceCount()
    {
        try
        {
            return Math.Max(0, WaveIn.DeviceCount);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Audio input device count is unavailable because of {ErrorType}",
                exception.GetType().Name);
            return 0;
        }
    }

    private static AudioDeviceSnapshot ToSnapshot(WaveDeviceEntry device) =>
        new(device.DeviceId, device.Label, device.IsSystemDefault);

    private static string CreateDeviceId(string kind, Guid productGuid, Guid nameGuid, string label)
    {
        byte[] hash = SHA256.HashData(
            Encoding.UTF8.GetBytes($"{kind}\n{productGuid:D}\n{nameGuid:D}\n{label}"));
        return $"{kind}-{Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant()}";
    }

    private static string EnsureUniqueDeviceId(
        string identifier,
        Dictionary<string, int> identifierCounts)
    {
        int occurrence = identifierCounts.GetValueOrDefault(identifier) + 1;
        identifierCounts[identifier] = occurrence;
        return occurrence == 1 ? identifier : $"{identifier}-{occurrence}";
    }

    private static byte[] CreateTestTone(WaveFormat format, TimeSpan duration)
    {
        int sampleCount = (int)(format.SampleRate * duration.TotalSeconds);
        var bytes = new byte[sampleCount * 2];
        const double frequency = 660d;
        for (int index = 0; index < sampleCount; index++)
        {
            double envelope = Math.Min(1d, Math.Min(index / 400d, (sampleCount - index) / 400d));
            short sample = (short)(Math.Sin(2d * Math.PI * frequency * index / format.SampleRate) *
                                   short.MaxValue * 0.18d * envelope);
            BitConverter.TryWriteBytes(bytes.AsSpan(index * 2, 2), sample);
        }
        return bytes;
    }

    private sealed record WaveDeviceEntry(
        string DeviceId,
        string Label,
        int DeviceIndex,
        bool IsSystemDefault);

    private int? GetWorkingPlaybackDevice()
    {
        long started = Stopwatch.GetTimestamp();
        long generation;
        long now = Stopwatch.GetTimestamp();
        lock (_audioNotificationGate)
        {
            if (_playbackDeviceCacheValid && now < _playbackDeviceCacheExpiresAt)
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
                _playbackDeviceCacheExpiresAt = GetExpirationTimestamp(
                    discovered.HasValue
                        ? PlaybackDeviceCacheLifetime
                        : MissingPlaybackDeviceCacheLifetime);
            }
        }

        AgentPerformanceTelemetry.RecordAudioProbe(
            Stopwatch.GetElapsedTime(started),
            cacheHit: false);
        return discovered;
    }

    private void InvalidatePlaybackDeviceCache()
    {
        lock (_audioNotificationGate)
        {
            _audioDeviceGeneration++;
            _cachedPlaybackDevice = null;
            _playbackDeviceCacheValid = false;
            _playbackDeviceCacheExpiresAt = 0;
            _volumeController.Invalidate();
        }
    }

    private void InvalidateAudioDeviceCachesCore()
    {
        _audioDeviceGeneration++;
        _cachedPlaybackDevice = null;
        _playbackDeviceCacheValid = false;
        _playbackDeviceCacheExpiresAt = 0;
        Volatile.Write(ref _audioInventoryCacheExpiresAt, 0);
        _volumeController.Invalidate();
    }

    private static long GetExpirationTimestamp(TimeSpan lifetime) =>
        Stopwatch.GetTimestamp() + (long)(lifetime.TotalSeconds * Stopwatch.Frequency);

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
            InvalidateAudioDeviceCachesCore();
            Volatile.Write(
                ref _audioInventoryCacheExpiresAt,
                GetExpirationTimestamp(TimeSpan.FromSeconds(1)));
            _audioNotificationDebounce?.Cancel();
            _audioNotificationDebounce?.Dispose();
            _audioNotificationDebounce = new CancellationTokenSource();
            token = _audioNotificationDebounce.Token;
        }

        TrackBackground(RefreshAudioInventoryAfterDebounceAsync(token));
    }

    private async Task RefreshAudioInventoryAfterDebounceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            if (_disposed)
            {
                return;
            }

            string previous = AudioState;
            AudioDevicesSnapshot inventory = RefreshAudioDevicesSnapshotCore();
            AudioState = GetAudioState(inventory);
            _logger.LogInformation(
                "Audio inventory changed: captureDevices={CaptureCount}, playbackDevices={PlaybackCount}, state={State}",
                Math.Max(0, inventory.InputDevices.Count - 1),
                Math.Max(0, inventory.OutputDevices.Count - 1),
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

    private static string GetAudioState(AudioDevicesSnapshot inventory) =>
        inventory.InputDevices.Count > 1 && inventory.OutputDevices.Count > 1
            ? "ready"
            : "degraded";

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
