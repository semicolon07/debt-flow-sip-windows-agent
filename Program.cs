using System;
using System.Net;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using Microsoft.Extensions.Logging;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Windows;

class Program
{
    private static SIPTransport? _sipTransport;
    private static SIPUserAgent? _userAgent;
    private static SIPRegistrationUserAgent? _regUserAgent;
    private static WindowsAudioEndPoint? _audioEndPoint;
    private static VoIPMediaSession? _mediaSession;
    private static SIPServerUserAgent? _pendingIncomingCall;
    private static WebSocket? _webSocketClient;
    private static readonly SemaphoreSlim _webSocketSendLock = new(1, 1);
    private static Task? _outgoingCallTask;
    private static AudioExtrasSource? _fallbackAudioSource;
    private static ILoggerFactory? _loggerFactory;

    private static string _pbxHost = "192.168.1.100";
    private static string _sipUser = "1002";
    private static string _sipPass = "123456";

    static async Task Main(string[] args)
    {
        Console.WriteLine("=== Starting SIP Local WebSocket Agent ===");

        // Enable SIPSorcery internal logging. In v10.0.16 RTPSession.SetRemoteDescription
        // catches internal exceptions and returns SetDescriptionResultEnum.Error, so the
        // logger is essential for seeing the real failure.
        _loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "HH:mm:ss.fff ";
            });
        });
        SIPSorcery.LogFactory.Set(_loggerFactory);

        PrintAudioDevices();

        _sipTransport = new SIPTransport();
        _sipTransport.AddSIPChannel(
            new SIPUDPChannel(new IPEndPoint(IPAddress.Any, 0)));

        InitialiseUserAgent();

        var httpListener = new HttpListener();
        httpListener.Prefixes.Add("http://localhost:8443/");
        httpListener.Start();

        Console.WriteLine("WebSocket Agent listening on ws://localhost:8443/");

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            try { _userAgent?.Hangup(); } catch { }
            try { _regUserAgent?.Stop(); } catch { }
            try { _sipTransport?.Shutdown(); } catch { }
            Environment.Exit(0);
        };

        while (true)
        {
            var context = await httpListener.GetContextAsync();

            if (context.Request.IsWebSocketRequest)
            {
                _ = Task.Run(() => ProcessWebSocketRequestAsync(context));
            }
            else
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
            }
        }
    }

    private static void PrintAudioDevices()
    {
        Console.WriteLine($"[AUDIO] Capture devices (WaveIn): {WaveIn.DeviceCount}");
        for (int i = 0; i < WaveIn.DeviceCount; i++)
        {
            var caps = WaveIn.GetCapabilities(i);
            Console.WriteLine($"[AUDIO]   IN  [{i}] {caps.ProductName} / channels={caps.Channels}");
        }

        Console.WriteLine($"[AUDIO] Playback devices (WaveOut): {WaveOut.DeviceCount}");
        for (int i = 0; i < WaveOut.DeviceCount; i++)
        {
            var caps = WaveOut.GetCapabilities(i);
            Console.WriteLine($"[AUDIO]   OUT [{i}] {caps.ProductName} / channels={caps.Channels}");
        }

        if (WaveIn.DeviceCount == 0)
        {
            Console.WriteLine("[AUDIO WARNING] No microphone/capture device is visible to NAudio.");
            Console.WriteLine("[AUDIO WARNING] PoC will use a generated silence source so SIP/RTP signalling can still be tested.");
        }

        if (WaveOut.DeviceCount == 0)
        {
            Console.WriteLine("[AUDIO WARNING] No speaker/playback device is visible to NAudio.");
            Console.WriteLine("[AUDIO WARNING] PoC will continue without an AudioSink so the SIP call is not cancelled by media startup.");
        }
    }

    /// <summary>
    /// WindowsAudioEndPoint internally catches WaveOut.Init failures. If that happens
    /// the endpoint still has a WaveOut object and StartAudioSink later throws
    /// "Must call Init first". Probe the same 8 kHz mono format up front and only
    /// give SIPSorcery a playback device that NAudio can actually initialise.
    /// </summary>
    private static int? FindWorkingPlaybackDevice()
    {
        if (WaveOut.DeviceCount <= 0)
        {
            return null;
        }

        // Try the Windows default mapper first, then concrete device indexes.
        var candidates = new[] { -1 }
            .Concat(Enumerable.Range(0, WaveOut.DeviceCount))
            .Distinct();

        foreach (int deviceIndex in candidates)
        {
            try
            {
                using var output = new WaveOut
                {
                    DeviceNumber = deviceIndex
                };

                var provider = new BufferedWaveProvider(new WaveFormat(8000, 16, 1))
                {
                    DiscardOnBufferOverflow = true
                };

                output.Init(provider);
                output.Stop();

                string name;
                try
                {
                    name = WaveOut.GetCapabilities(deviceIndex).ProductName;
                }
                catch
                {
                    name = deviceIndex == -1 ? "Default Audio Mapper" : $"Device {deviceIndex}";
                }

                Console.WriteLine($"[AUDIO] Playback probe OK: index={deviceIndex}, name={name}");
                return deviceIndex;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AUDIO] Playback probe FAILED: index={deviceIndex}, error={ex.Message}");
            }
        }

        return null;
    }

    private static void InitialiseUserAgent()
    {
        if (_sipTransport == null)
            throw new InvalidOperationException("SIP transport has not been initialised.");

        _userAgent = new SIPUserAgent(_sipTransport, null, true);

        _userAgent.OnIncomingCall += async (ua, request) =>
        {
            try
            {
                if (_pendingIncomingCall != null || ua.IsCallActive)
                {
                    var busyCall = ua.AcceptCall(request);
                    busyCall.Reject(SIPResponseStatusCodesEnum.BusyHere, "Busy");
                    return;
                }

                _pendingIncomingCall = ua.AcceptCall(request);

                string callerId = request.Header?.From?.FromURI?.User
                                  ?? request.RemoteSIPEndPoint?.ToString()
                                  ?? "unknown";

                await SendEventToWebAsync("INCOMING_CALL", callerId);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SIP Incoming Error] {ex}");
                await SendEventToWebAsync("CALL_FAILED", ex.Message);
            }
        };

        _userAgent.ServerCallCancelled += async (_, _) =>
        {
            _pendingIncomingCall = null;
            await SendEventToWebAsync("CALL_CANCELLED", "Incoming call cancelled by remote party.");
        };

        _userAgent.OnDtmfTone += async (key, duration) =>
            await SendEventToWebAsync("DTMF_RECEIVED", $"Tone={key}, Duration={duration}ms");

        _userAgent.OnCallHungup += async _ =>
        {
            await CleanupMediaAsync();
            _pendingIncomingCall = null;
            await SendEventToWebAsync("CALL_ENDED", "Call hung up");
        };

        _userAgent.ClientCallTrying += async (_, response) =>
            await SendEventToWebAsync("CALL_TRYING", $"{(int)response.Status} {response.ReasonPhrase}");

        _userAgent.ClientCallRinging += async (_, response) =>
            await SendEventToWebAsync("CALL_RINGING", $"{(int)response.Status} {response.ReasonPhrase}");

        _userAgent.ClientCallAnswered += async (_, response) =>
        {
            Console.WriteLine($"[SIP] Answered: {(int)response.Status} {response.ReasonPhrase}");
            Console.WriteLine("[SIP] Remote SDP from 200 OK:");
            Console.WriteLine(string.IsNullOrWhiteSpace(response.Body) ? "<empty SDP>" : response.Body);
            await SendEventToWebAsync("CALL_ANSWERED", $"{(int)response.Status} {response.ReasonPhrase}");
        };

        _userAgent.ClientCallFailed += async (_, error, response) =>
        {
            await CleanupMediaAsync();
            string detail = response != null
                ? $"{error} ({(int)response.Status} {response.ReasonPhrase})"
                : error;
            await SendEventToWebAsync("CALL_FAILED", detail);
        };
    }

    private static VoIPMediaSession CreateMediaSession()
    {
        var encoder = new AudioEncoder();

        bool hasCaptureDevice = WaveIn.DeviceCount > 0;
        int captureDeviceIndex = hasCaptureDevice ? 0 : -1;

        int? playbackDeviceIndex = FindWorkingPlaybackDevice();
        bool hasPlaybackDevice = playbackDeviceIndex.HasValue;

        Console.WriteLine($"[AUDIO] Selected capture: {(hasCaptureDevice ? captureDeviceIndex.ToString() : "none")}");
        Console.WriteLine($"[AUDIO] Selected playback: {(hasPlaybackDevice ? playbackDeviceIndex!.Value.ToString() : "none")}");

        // Use explicit tested device indexes. If output cannot be initialised, disable
        // the Windows sink completely. This prevents SIPUserAgent.ClientCallRingingHandler
        // from cancelling the call when VoIPMediaSession.Start() starts early media.
        _audioEndPoint = new WindowsAudioEndPoint(
            encoder,
            audioOutDeviceIndex: hasPlaybackDevice ? playbackDeviceIndex!.Value : -1,
            audioInDeviceIndex: captureDeviceIndex,
            disableSource: !hasCaptureDevice,
            disableSink: !hasPlaybackDevice);

        _audioEndPoint.OnAudioSourceError += error =>
        {
            Console.WriteLine($"[AUDIO SOURCE ERROR] {error}");
            _ = SendEventToWebAsync("AUDIO_SOURCE_ERROR", error);
        };

        _audioEndPoint.OnAudioSinkError += error =>
        {
            Console.WriteLine($"[AUDIO SINK ERROR] {error}");
            _ = SendEventToWebAsync("AUDIO_SINK_ERROR", error);
        };

        IAudioSource audioSource;
        IAudioSink? audioSink;

        if (hasCaptureDevice)
        {
            Console.WriteLine("[AUDIO] Using Windows microphone source.");
            audioSource = _audioEndPoint;
            _fallbackAudioSource = null;
        }
        else
        {
            Console.WriteLine("[AUDIO] No microphone detected. Using generated silence source for PoC.");

            _fallbackAudioSource = new AudioExtrasSource(
                encoder,
                new AudioSourceOptions { AudioSource = AudioSourcesEnum.Silence });

            audioSource = _fallbackAudioSource;
        }

        if (hasPlaybackDevice)
        {
            Console.WriteLine("[AUDIO] Using Windows speaker sink.");
            audioSink = _audioEndPoint;
        }
        else
        {
            Console.WriteLine("[AUDIO WARNING] Playback unavailable. Call will continue without speaker output.");
            _ = SendEventToWebAsync(
                "AUDIO_SINK_UNAVAILABLE",
                "No NAudio WaveOut device could be initialised. SIP call will continue without local speaker playback.");
            audioSink = null;
        }

        var mediaEndPoints = new MediaEndPoints
        {
            AudioSource = audioSource,
            AudioSink = audioSink
        };

        _mediaSession = new DiagnosticVoIPMediaSession(mediaEndPoints)
        {
            AcceptRtpFromAny = true
        };

        _mediaSession.OnAudioFormatsNegotiated += formats =>
        {
            try
            {
                var negotiated = string.Join(", ", formats.Select(x => $"{x.Codec}/{x.ClockRate}"));
                Console.WriteLine($"[MEDIA] Negotiated audio codec(s): {negotiated}");
                _ = SendEventToWebAsync("MEDIA_NEGOTIATED", negotiated);
            }
            catch (Exception ex)
            {
                // Never allow PoC diagnostics to break SDP negotiation.
                Console.WriteLine($"[MEDIA DIAGNOSTIC ERROR] {ex}");
            }
        };

        return _mediaSession;
    }

    #region WebSocket

    private static async Task ProcessWebSocketRequestAsync(HttpListenerContext context)
    {
        var wsContext = await context.AcceptWebSocketAsync(subProtocol: null);
        _webSocketClient = wsContext.WebSocket;

        Console.WriteLine("[WS] Web Frontend Connected!");

        var buffer = new byte[16 * 1024];

        try
        {
            while (_webSocketClient.State == WebSocketState.Open)
            {
                using var messageBuffer = new System.IO.MemoryStream();
                WebSocketReceiveResult result;

                do
                {
                    result = await _webSocketClient.ReceiveAsync(
                        new ArraySegment<byte>(buffer),
                        CancellationToken.None);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await _webSocketClient.CloseAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "Closed",
                            CancellationToken.None);
                        return;
                    }

                    messageBuffer.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    string json = Encoding.UTF8.GetString(messageBuffer.ToArray());
                    await HandleWebCommandAsync(json);
                }
            }
        }
        catch (WebSocketException ex)
        {
            Console.WriteLine($"[WS Error] {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WS Error] {ex}");
        }
    }

    private static async Task HandleWebCommandAsync(string jsonMessage)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonMessage);
            var root = doc.RootElement;

            if (!root.TryGetProperty("action", out var actionElement))
            {
                await SendEventToWebAsync("ERROR", "Missing action.");
                return;
            }

            string? action = actionElement.GetString();

            switch (action)
            {
                case "register":
                    _pbxHost = GetRequiredString(root, "server");
                    _sipUser = GetRequiredString(root, "user");
                    _sipPass = GetRequiredString(root, "pass");
                    RegisterSipServer();
                    break;

                case "unregister":
                    UnregisterSipServer();
                    break;

                case "status":
                    await SendStatusAsync();
                    break;

                case "reject":
                    await RejectIncomingCallAsync();
                    break;

                case "call":
                    // SIPUserAgent.Call waits until the call is answered or fails.
                    // Do NOT await it on the WebSocket receive loop, otherwise commands
                    // such as hangup/cancel cannot be received while the phone is ringing.
                    if (_outgoingCallTask is { IsCompleted: false })
                    {
                        await SendEventToWebAsync("CALL_FAILED", "Another outgoing call command is already running.");
                        break;
                    }

                    string destination = GetRequiredString(root, "destination");
                    _outgoingCallTask = RunOutgoingCallAsync(destination);
                    break;

                case "answer":
                    await AnswerIncomingCallAsync();
                    break;

                case "hangup":
                    await HangupCallAsync();
                    break;

                case "dtmf":
                    await SendDtmfAsync(GetRequiredString(root, "digit"));
                    break;

                default:
                    await SendEventToWebAsync("ERROR", $"Unknown action: {action}");
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Command Error] {ex}");
            await SendEventToWebAsync("ERROR", ex.Message);
        }
    }

    private static string GetRequiredString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new ArgumentException($"Missing or invalid '{propertyName}'.");
        }

        return value.GetString()!;
    }

    #endregion

    #region SIP

    private static void RegisterSipServer()
    {
        if (_sipTransport == null)
            throw new InvalidOperationException("SIP transport has not been initialised.");

        _regUserAgent?.Stop();

        _regUserAgent = new SIPRegistrationUserAgent(
            _sipTransport,
            _sipUser,
            _sipPass,
            _pbxHost,
            180);

        _regUserAgent.RegistrationFailed += async (_, _, error) =>
            await SendEventToWebAsync("REGISTRATION_FAILED", error);

        _regUserAgent.RegistrationTemporaryFailure += async (_, _, error) =>
            await SendEventToWebAsync("REGISTRATION_TEMPORARY_FAILURE", error);

        _regUserAgent.RegistrationRemoved += async (uri, _) =>
            await SendEventToWebAsync("REGISTRATION_REMOVED", uri.ToString());

        _regUserAgent.RegistrationSuccessful += async (_, _) =>
            await SendEventToWebAsync(
                "REGISTRATION_SUCCESS",
                $"Registered as {_sipUser}@{_pbxHost}");

        _regUserAgent.Start();
    }

    private static void UnregisterSipServer()
    {
        _regUserAgent?.Stop();
        _regUserAgent = null;
        _ = SendEventToWebAsync("REGISTRATION_STOPPED", "Registration agent stopped.");
    }

    private static async Task RejectIncomingCallAsync()
    {
        if (_pendingIncomingCall == null)
        {
            await SendEventToWebAsync("ERROR", "There is no incoming call to reject.");
            return;
        }

        _pendingIncomingCall.Reject(SIPResponseStatusCodesEnum.BusyHere, "Rejected by user");
        _pendingIncomingCall = null;
        await SendEventToWebAsync("CALL_REJECTED", "Incoming call rejected.");
    }

    private static async Task SendStatusAsync()
    {
        string registration = _regUserAgent == null
            ? "stopped"
            : (_regUserAgent.IsRegistered ? "registered" : "not_registered");

        string call = _pendingIncomingCall != null ? "incoming"
            : _userAgent?.IsCallActive == true ? "connected"
            : _userAgent?.IsCalling == true ? "calling"
            : _userAgent?.IsRinging == true ? "ringing"
            : "idle";

        await SendEventToWebAsync("STATUS", $"registration={registration}; call={call}; sip={_sipUser}@{_pbxHost}");
    }


    private static async Task RunOutgoingCallAsync(string destination)
    {
        try
        {
            await MakeOutgoingCallAsync(destination);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Outgoing Call Error] {ex}");
            await CleanupMediaAsync();
            await SendEventToWebAsync("CALL_FAILED", ex.Message);
        }
    }

    private static async Task MakeOutgoingCallAsync(string destination)
    {
        if (_userAgent == null)
            throw new InvalidOperationException("SIP user agent has not been initialised.");

        if (_userAgent.IsCallActive || _userAgent.IsCalling || _userAgent.IsRinging)
        {
            await SendEventToWebAsync("CALL_FAILED", "Another call is already in progress.");
            return;
        }

        var mediaSession = CreateMediaSession();
        string destinationUri = $"sip:{destination}@{_pbxHost}";

        await SendEventToWebAsync("CALL_DIALING", $"Calling {destination}...");

        bool result = await _userAgent.Call(
            destinationUri,
            _sipUser,
            _sipPass,
            mediaSession);

        if (result)
        {
            await SendEventToWebAsync("CALL_CONNECTED", $"Connected to {destination}");
        }
        else
        {
            await CleanupMediaAsync();
            await SendEventToWebAsync("CALL_FAILED", "Unable to place call.");
        }
    }

    private static async Task AnswerIncomingCallAsync()
    {
        if (_userAgent == null)
            throw new InvalidOperationException("SIP user agent has not been initialised.");

        if (_pendingIncomingCall == null)
        {
            await SendEventToWebAsync("CALL_FAILED", "There is no incoming call to answer.");
            return;
        }

        var mediaSession = CreateMediaSession();
        var incomingCall = _pendingIncomingCall;
        _pendingIncomingCall = null;

        bool answered = await _userAgent.Answer(incomingCall, mediaSession);

        if (answered)
        {
            await SendEventToWebAsync("CALL_CONNECTED", "Call answered.");
        }
        else
        {
            await CleanupMediaAsync();
            await SendEventToWebAsync("CALL_FAILED", "Unable to answer incoming call.");
        }
    }

    private static async Task HangupCallAsync()
    {
        if (_userAgent == null)
            return;

        if (_pendingIncomingCall != null)
        {
            _pendingIncomingCall.Reject(
                SIPResponseStatusCodesEnum.BusyHere,
                "Rejected by user");
            _pendingIncomingCall = null;

            await CleanupMediaAsync();
            await SendEventToWebAsync("CALL_ENDED", "Incoming call rejected.");
            return;
        }

        if (_userAgent.IsCalling || _userAgent.IsRinging)
        {
            _userAgent.Cancel();
        }
        else if (_userAgent.IsCallActive)
        {
            _userAgent.Hangup();
        }

        await CleanupMediaAsync();
        await SendEventToWebAsync("CALL_ENDED", "Disconnected manually.");
    }

    private static async Task SendDtmfAsync(string digit)
    {
        if (_userAgent == null || !_userAgent.IsCallActive)
        {
            await SendEventToWebAsync("ERROR", "No active call for DTMF.");
            return;
        }

        if (digit.Length != 1)
            throw new ArgumentException("DTMF must contain exactly one digit.");

        byte tone = digit[0] switch
        {
            >= '0' and <= '9' => (byte)(digit[0] - '0'),
            '*' => 10,
            '#' => 11,
            'A' or 'a' => 12,
            'B' or 'b' => 13,
            'C' or 'c' => 14,
            'D' or 'd' => 15,
            _ => throw new ArgumentException($"Invalid DTMF digit '{digit}'.")
        };

        await _userAgent.SendDtmf(tone);
        await SendEventToWebAsync("DTMF_SENT", digit);
    }

    private static async Task CleanupMediaAsync()
    {
        try
        {
            if (_mediaSession != null)
            {
                _mediaSession.Close("call ended");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Media Cleanup] {ex.Message}");
        }
        finally
        {
            _mediaSession = null;
            _fallbackAudioSource = null;
            _audioEndPoint = null;
        }
    }

    #endregion

    private static async Task SendEventToWebAsync(string eventType, string message)
    {
        var ws = _webSocketClient;
        if (ws == null || ws.State != WebSocketState.Open)
            return;

        await _webSocketSendLock.WaitAsync();
        try
        {
            // Re-check after acquiring the lock because the socket may have closed
            // while another event was being sent.
            if (ws.State != WebSocketState.Open)
                return;

            var payload = JsonSerializer.Serialize(new { eventType, message });
            var bytes = Encoding.UTF8.GetBytes(payload);

            await ws.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                true,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WS Send Error] {ex.Message}");
        }
        finally
        {
            _webSocketSendLock.Release();
        }
    }
}


/// <summary>
/// PoC diagnostics: exposes the exact SDP negotiation result returned by SIPSorcery.
/// Remove this wrapper once interoperability with the PBX is confirmed.
/// </summary>
sealed class DiagnosticVoIPMediaSession : VoIPMediaSession
{
    public DiagnosticVoIPMediaSession(MediaEndPoints mediaEndPoints)
        : base(mediaEndPoints)
    {
    }

    public override SDP CreateOffer(IPAddress connectionAddress)
    {
        var offer = base.CreateOffer(connectionAddress);
        Console.WriteLine("\n========== LOCAL SDP OFFER ==========");
        Console.WriteLine(offer?.ToString() ?? "<null>");
        Console.WriteLine("========== END LOCAL SDP OFFER ======\n");
        return offer;
    }

    public override SetDescriptionResultEnum SetRemoteDescription(SdpType sdpType, SDP sessionDescription)
    {
        Console.WriteLine($"\n========== SET REMOTE SDP ({sdpType}) ==========");
        Console.WriteLine(sessionDescription?.ToString() ?? "<null>");

        var result = base.SetRemoteDescription(sdpType, sessionDescription);

        Console.WriteLine($"[SDP] SetRemoteDescription result: {result}");
        Console.WriteLine("========== END SET REMOTE SDP ================\n");
        return result;
    }
}
