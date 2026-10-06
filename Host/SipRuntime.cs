using System.Net;
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
    private readonly SIPTransport _transport;
    private readonly SIPUserAgent _userAgent;
    private readonly SemaphoreSlim _mediaGate = new(1, 1);
    private readonly object _audioNotificationGate = new();
    private readonly AudioSessionVolumeController _volumeController = new();
    private readonly MMDeviceEnumerator? _audioDeviceEnumerator;
    private readonly MMDeviceNotificationClient? _audioNotificationClient;
    private SIPRegistrationUserAgent? _registrationAgent;
    private SIPServerUserAgent? _pendingIncomingCall;
    private WindowsAudioEndPoint? _audioEndPoint;
    private VoIPMediaSession? _mediaSession;
    private AudioExtrasSource? _fallbackAudioSource;
    private SipConfiguration? _configuration;
    private CancellationTokenSource? _audioNotificationDebounce;
    private bool _useTcp;
    private int _microphoneMuted;
    private int _outputVolume;
    private int _inputVolume;
    private int _registrationGeneration;
    private bool _disposed;

    public SipRuntime(ILogger<SipRuntime> logger, IAudioPreferencesStore audioPreferencesStore)
    {
        _logger = logger;
        _audioPreferencesStore = audioPreferencesStore;
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

    public event Func<SipSignal, Task>? Signal;

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

    public Task StartRegistrationAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        SipConfiguration configuration = RequireConfiguration();
        _registrationAgent?.Stop();
        int generation = ++_registrationGeneration;
        var registrationAgent = new SIPRegistrationUserAgent(
            _transport,
            configuration.Username,
            configuration.Password,
            SipEndpointFormatter.FormatRegistrar(configuration, _useTcp),
            180);
        _registrationAgent = registrationAgent;

        registrationAgent.RegistrationFailed += async (_, _, _) =>
            await EmitRegistrationSignalAsync(
                generation,
                new SipSignal(SipSignalType.RegistrationFailed, SafeCode: "registration_failed"));
        registrationAgent.RegistrationTemporaryFailure += async (_, _, _) =>
        {
            if (generation == _registrationGeneration && !_useTcp)
            {
                _useTcp = true;
                _logger.LogInformation("SIP registration transport changed to {Transport}", "tcp");
            }

            await EmitRegistrationSignalAsync(
                generation,
                new SipSignal(
                    SipSignalType.RegistrationFailed,
                    SafeCode: "registration_transport_failure",
                    Retryable: true));
        };
        registrationAgent.RegistrationRemoved += async (_, _) =>
            await EmitRegistrationSignalAsync(
                generation,
                new SipSignal(
                    SipSignalType.RegistrationFailed,
                    SafeCode: "registration_transport_lost",
                    Retryable: true));
        registrationAgent.RegistrationSuccessful += async (_, _) =>
        {
            _logger.LogInformation("SIP registration succeeded over {Transport}", _useTcp ? "tcp" : "udp");
            await EmitRegistrationSignalAsync(generation, new SipSignal(SipSignalType.RegistrationRegistered));
        };

        _ = EmitAsync(new SipSignal(SipSignalType.RegistrationRegistering));
        _registrationAgent.Start();
        return Task.CompletedTask;
    }

    public Task StopRegistrationAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _registrationGeneration++;
        _registrationAgent?.Stop();
        _registrationAgent = null;
        _configuration = null;
        return Task.CompletedTask;
    }

    public async Task StartCallAsync(string destination, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SipConfiguration configuration = RequireConfiguration();
        VoIPMediaSession mediaSession = CreateMediaSession();
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
            await EmitAsync(new SipSignal(SipSignalType.CallFailed, SafeCode: "call_timeout"));
            return;
        }
        catch (OperationCanceledException)
        {
            _userAgent.Cancel();
            await CleanupMediaAsync();
            throw;
        }

        if (result)
        {
            await EmitAsync(new SipSignal(SipSignalType.CallConnected));
        }
        else
        {
            await CleanupMediaAsync();
            await EmitAsync(new SipSignal(SipSignalType.CallFailed, SafeCode: "call_unavailable"));
        }
    }

    public async Task AnswerAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        SIPServerUserAgent incoming = _pendingIncomingCall
            ?? throw new InvalidOperationException("No incoming call is pending.");
        _pendingIncomingCall = null;
        bool answered;
        try
        {
            answered = await _userAgent.Answer(incoming, CreateMediaSession())
                .WaitAsync(AnswerTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            await CleanupMediaAsync();
            await EmitAsync(new SipSignal(SipSignalType.CallFailed, SafeCode: "answer_timeout"));
            return;
        }
        await EmitAsync(answered
            ? new SipSignal(SipSignalType.CallConnected)
            : new SipSignal(SipSignalType.CallFailed, SafeCode: "answer_failed"));
    }

    public Task RejectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SIPServerUserAgent incoming = _pendingIncomingCall
            ?? throw new InvalidOperationException("No incoming call is pending.");
        incoming.Reject(SIPResponseStatusCodesEnum.BusyHere, "Rejected");
        _pendingIncomingCall = null;
        return Task.CompletedTask;
    }

    public Task RejectUnavailableAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SIPServerUserAgent incoming = _pendingIncomingCall
            ?? throw new InvalidOperationException("No incoming call is pending.");
        incoming.Reject(SIPResponseStatusCodesEnum.TemporarilyUnavailable, "Temporarily unavailable");
        _pendingIncomingCall = null;
        return Task.CompletedTask;
    }

    public async Task HangupAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_pendingIncomingCall != null)
        {
            _pendingIncomingCall.Reject(SIPResponseStatusCodesEnum.BusyHere, "Rejected");
            _pendingIncomingCall = null;
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
    }

    public Task SendDtmfAsync(char digit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
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

    public async Task SetMicrophoneMutedAsync(bool muted, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
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

    public async Task SetOutputVolumeAsync(int volume, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await SetSessionVolumeAsync(DataFlow.Render, volume, cancellationToken);
        Volatile.Write(ref _outputVolume, volume);
        PersistAudioPreferences();
    }

    public async Task SetInputVolumeAsync(int volume, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await SetSessionVolumeAsync(DataFlow.Capture, volume, cancellationToken);
        Volatile.Write(ref _inputVolume, volume);
        PersistAudioPreferences();
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
        _mediaGate.Dispose();
    }

    private void WireUserAgentEvents()
    {
        _userAgent.OnIncomingCall += async (agent, request) =>
        {
            if (_pendingIncomingCall != null || agent.IsCallActive)
            {
                SIPServerUserAgent busyCall = agent.AcceptCall(request);
                busyCall.Reject(SIPResponseStatusCodesEnum.BusyHere, "Busy");
                return;
            }

            _pendingIncomingCall = agent.AcceptCall(request);
            string caller = request.Header?.From?.FromURI?.User ?? "unknown";
            await EmitAsync(new SipSignal(SipSignalType.IncomingCall, Caller: caller));
        };

        _userAgent.ServerCallCancelled += async (_, _) =>
        {
            _pendingIncomingCall = null;
            await EmitAsync(new SipSignal(SipSignalType.IncomingCancelled));
        };
        _userAgent.OnDtmfTone += async (_, _) =>
            await EmitAsync(new SipSignal(SipSignalType.DtmfReceived));
        _userAgent.OnCallHungup += async _ =>
        {
            await CleanupMediaAsync();
            _pendingIncomingCall = null;
            await EmitAsync(new SipSignal(SipSignalType.CallRemoteEnded));
        };
        _userAgent.ClientCallTrying += async (_, response) =>
            await EmitAsync(new SipSignal(SipSignalType.CallTrying, (int)response.Status));
        _userAgent.ClientCallRinging += async (_, response) =>
            await EmitAsync(new SipSignal(SipSignalType.CallRinging, (int)response.Status));
        _userAgent.ClientCallAnswered += async (_, response) =>
            await EmitAsync(new SipSignal(SipSignalType.CallConnected, (int)response.Status));
        _userAgent.ClientCallFailed += async (_, _, response) =>
        {
            await CleanupMediaAsync();
            await EmitAsync(new SipSignal(
                SipSignalType.CallFailed,
                response == null ? null : (int)response.Status,
                "sip_call_failed"));
        };
    }

    private VoIPMediaSession CreateMediaSession()
    {
        var encoder = new AudioEncoder();
        bool hasCapture = WaveIn.DeviceCount > 0;
        int? playbackDevice = FindWorkingPlaybackDevice();
        bool hasPlayback = playbackDevice.HasValue;

        _audioEndPoint = new WindowsAudioEndPoint(
            encoder,
            audioOutDeviceIndex: hasPlayback ? playbackDevice!.Value : -1,
            audioInDeviceIndex: hasCapture ? 0 : -1,
            disableSource: !hasCapture,
            disableSink: !hasPlayback);
        Volatile.Write(ref _microphoneMuted, 0);

        _audioEndPoint.OnAudioSourceError += error =>
            _ = EmitAsync(new SipSignal(SipSignalType.MediaDegraded, SafeCode: "audio_source_error"));
        _audioEndPoint.OnAudioSinkError += error =>
            _ = EmitAsync(new SipSignal(SipSignalType.MediaDegraded, SafeCode: "audio_sink_error"));

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
            _ = EmitAsync(new SipSignal(SipSignalType.MediaDegraded, SafeCode: "audio_device_unavailable"));
        }

        _mediaSession = new VoIPMediaSession(new MediaEndPoints
        {
            AudioSource = source,
            AudioSink = sink
        })
        {
            AcceptRtpFromAny = true
        };

        _mediaSession.OnAudioFormatsNegotiated += formats =>
        {
            string codec = string.Join(",", formats.Select(format => $"{format.Codec}/{format.ClockRate}"));
            _ = ApplyRememberedVolumesAsync();
            _ = EmitAsync(new SipSignal(SipSignalType.MediaReady, Codec: codec));
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
            await EmitAsync(new SipSignal(SipSignalType.AudioInventoryChanged, SafeCode: "audio_devices_changed"));
            if (AudioState == "degraded" && previous != AudioState && _userAgent.IsCallActive)
            {
                await EmitAsync(new SipSignal(SipSignalType.MediaDegraded, SafeCode: "audio_device_removed"));
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

    private Task EmitRegistrationSignalAsync(int generation, SipSignal signal) =>
        generation == _registrationGeneration && !_disposed
            ? EmitAsync(signal)
            : Task.CompletedTask;

    private static string GetAudioState() =>
        WaveIn.DeviceCount > 0 && WaveOut.DeviceCount > 0 ? "ready" : "degraded";

    private async Task EmitAsync(SipSignal signal)
    {
        Func<SipSignal, Task>? handler = Signal;
        if (handler == null)
        {
            return;
        }

        try
        {
            await handler(signal);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "SIP signal {SignalType} could not be processed because of {ErrorType}",
                signal.Type,
                exception.GetType().Name);
        }
    }

    private SipConfiguration RequireConfiguration() =>
        _configuration ?? throw new InvalidOperationException("SIP runtime is not configured.");

}
