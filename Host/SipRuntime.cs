using System.Net;
using Microsoft.Extensions.Logging;
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
    private readonly ILogger<SipRuntime> _logger;
    private readonly SIPTransport _transport;
    private readonly SIPUserAgent _userAgent;
    private SIPRegistrationUserAgent? _registrationAgent;
    private SIPServerUserAgent? _pendingIncomingCall;
    private WindowsAudioEndPoint? _audioEndPoint;
    private VoIPMediaSession? _mediaSession;
    private AudioExtrasSource? _fallbackAudioSource;
    private SipConfiguration? _configuration;
    private bool _disposed;

    public SipRuntime(ILogger<SipRuntime> logger)
    {
        _logger = logger;
        _transport = new SIPTransport();
        _transport.AddSIPChannel(new SIPUDPChannel(new IPEndPoint(IPAddress.Any, 0)));
        _userAgent = new SIPUserAgent(_transport, null, true);
        WireUserAgentEvents();
        AudioState = WaveIn.DeviceCount > 0 && WaveOut.DeviceCount > 0 ? "ready" : "degraded";
        _logger.LogInformation(
            "Audio inventory: captureDevices={CaptureCount}, playbackDevices={PlaybackCount}",
            WaveIn.DeviceCount,
            WaveOut.DeviceCount);
    }

    public event Func<SipSignal, Task>? Signal;

    public string AudioState { get; private set; }

    public Task ConfigureAsync(SipConfiguration configuration, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        _configuration = configuration;
        return Task.CompletedTask;
    }

    public Task StartRegistrationAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        SipConfiguration configuration = RequireConfiguration();
        _registrationAgent?.Stop();
        _registrationAgent = new SIPRegistrationUserAgent(
            _transport,
            configuration.Username,
            configuration.Password,
            Registrar(configuration),
            180);

        _registrationAgent.RegistrationFailed += async (_, _, _) =>
            await EmitAsync(new SipSignal(SipSignalType.RegistrationFailed, SafeCode: "registration_failed"));
        _registrationAgent.RegistrationTemporaryFailure += async (_, _, _) =>
            await EmitAsync(new SipSignal(SipSignalType.RegistrationFailed, SafeCode: "registration_temporary_failure"));
        _registrationAgent.RegistrationRemoved += async (_, _) =>
            await EmitAsync(new SipSignal(SipSignalType.RegistrationUnregistered, SafeCode: "registration_removed"));
        _registrationAgent.RegistrationSuccessful += async (_, _) =>
            await EmitAsync(new SipSignal(SipSignalType.RegistrationRegistered));

        _ = EmitAsync(new SipSignal(SipSignalType.RegistrationRegistering));
        _registrationAgent.Start();
        return Task.CompletedTask;
    }

    public Task StopRegistrationAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _registrationAgent?.Stop();
        _registrationAgent = null;
        _configuration = null;
        return EmitAsync(new SipSignal(SipSignalType.RegistrationUnregistered, SafeCode: "registration_stopped"));
    }

    public async Task StartCallAsync(string destination, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SipConfiguration configuration = RequireConfiguration();
        VoIPMediaSession mediaSession = CreateMediaSession();
        string destinationUri = $"sip:{destination}@{Registrar(configuration)}";

        bool result = await _userAgent.Call(
            destinationUri,
            configuration.Username,
            configuration.Password,
            mediaSession);

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
        bool answered = await _userAgent.Answer(incoming, CreateMediaSession());
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

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _registrationAgent?.Stop();
        if (_userAgent.IsCallActive)
        {
            _userAgent.Hangup();
        }

        await CleanupMediaAsync();
        _transport.Shutdown();
        _configuration = null;
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
            _ = EmitAsync(new SipSignal(SipSignalType.MediaReady, Codec: codec));
        };
        return _mediaSession;
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
        try
        {
            _mediaSession?.Close("call ended");
        }
        catch (Exception exception)
        {
            _logger.LogWarning("Media cleanup failed with {ErrorType}", exception.GetType().Name);
        }
        finally
        {
            _mediaSession = null;
            _fallbackAudioSource = null;
            _audioEndPoint = null;
        }

        await Task.CompletedTask;
    }

    private Task EmitAsync(SipSignal signal)
    {
        Func<SipSignal, Task>? handler = Signal;
        return handler == null ? Task.CompletedTask : handler(signal);
    }

    private SipConfiguration RequireConfiguration() =>
        _configuration ?? throw new InvalidOperationException("SIP runtime is not configured.");

    private static string Registrar(SipConfiguration configuration) =>
        configuration.Port == 5060 ? configuration.Host : $"{configuration.Host}:{configuration.Port}";
}
