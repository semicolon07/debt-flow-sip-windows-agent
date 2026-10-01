# Softphone Native Client - Full Function PoC

Baseline: .NET 10 Windows, SIPSorcery 10.0.16, SIPSorceryMedia.Windows 10.0.16, NAudio 3.1.0.

## Run
1. `dotnet restore`
2. `dotnet run`
3. Open `poc.html` in a browser.
4. Click **Connect Agent**, enter PBX credentials, then **Register**.
5. Test outgoing/incoming calls, Answer/Reject/Hangup and DTMF.

## WebSocket commands
`register`, `unregister`, `status`, `call`, `answer`, `reject`, `hangup`, `dtmf`.

## Important
This is intentionally a PoC. It binds the control socket only to localhost and supports one browser client. Credentials are sent from the local test page to the local agent in clear text; do not treat this protocol as production security design.

## Hangup/Cancel fix
The outgoing `SIPUserAgent.Call` operation is intentionally run outside the WebSocket receive loop. `Call` waits until answer/failure, so awaiting it directly in the command handler prevents `hangup` from being received while ringing. The WebSocket send path is also serialized because SIP events can be raised concurrently.


## SDP Error diagnostics / no microphone fallback

This build enables SIPSorcery internal Debug logging and prints NAudio WaveIn/WaveOut devices at startup.
If no WaveIn capture device is visible, the PoC uses `AudioExtrasSource` with `Silence` as the outgoing audio source so that SIP call setup can still complete instead of failing during `SetAudioSourceFormat`.
Actual two-way voice still requires a capture device visible to NAudio.

## Audio output fix (WaveOut Init)

This PoC now probes NAudio playback devices before creating `WindowsAudioEndPoint`.
If the default mapper (`-1`) cannot be initialised it tries concrete WaveOut device indexes.
If no playback device can be initialised, the SIP/RTP call is allowed to continue with no local speaker sink instead of being cancelled by `StartAudioSink()`.

Look for these lines when placing a call:

```text
[AUDIO] Playback probe OK: index=..., name=...
[AUDIO] Selected playback: ...
```

If all probes fail, the original Init exception is printed for each device. The call can still connect, but remote audio will not be rendered locally until a usable Windows playback device is available.
