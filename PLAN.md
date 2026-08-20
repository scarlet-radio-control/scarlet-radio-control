# ScarletRadioControl.Device — external ffmpeg + RTP over loopback (replaces the in-process bindings)

## Context

The device streams H264 via in-process `SIPSorceryMedia.FFmpeg` bindings ([CameraVideoSource.cs](src/ScarletRadioControl.Device/Services/CameraVideoSource.cs)). That choice carried two accepted risks: **FFmpeg 8.1 shared libs required on the Pi** (no apt package at that version) and **software x264 encoding**. Both now bite — and on the dev machine the winget `Gyan.FFmpeg.Shared` package has been upgraded to **9.0.1**, removing the 8.1.2 build that the FFmpeg.AutoGen 8.1 ABI requires, so the in-process path can no longer load its native libs there either.

This change replaces the in-process bindings **outright** with the RTP-over-loopback approach: spawn the external **ffmpeg CLI** to capture the camera and encode H264 (hardware `h264_v4l2m2m` on the Pi 4, `libx264` on Windows dev), output RTP to a loopback UDP port; the app receives those packets, depacketizes them into H264 Annex-B access units, and invokes the existing `EncodedSampleDelegate` consumers (each peer's `pc.SendVideo`). Any distro/winget ffmpeg version works, the FFmpeg-8.1 pin disappears, and the Pi gets hardware encoding.

The `AddConsumerAsync`/`RemoveConsumerAsync` façade contract of `CameraVideoSource` is preserved, so [WebRtcSessionManager.cs](src/ScarletRadioControl.Device/Services/WebRtcSessionManager.cs), `WebRtcPeerSession.cs`, `WebRtcSignalingBackgroundService.cs`, `Startup.cs`, and the `Signaling/` DTOs are all untouched.

All SIPSorcery APIs below were verified against `sipsorcery-org/sipsorcery` tag **v10.0.15** (paths under `src/SIPSorcery/`).

## Status

**Implemented and building clean** (`dotnet build`, 0 warnings under TreatWarningsAsErrors + style enforcement): all five change sections landed — the `SIPSorceryMedia.FFmpeg` package reference is gone from both [Directory.Packages.props](Directory.Packages.props) and the [csproj](src/ScarletRadioControl.Device/ScarletRadioControl.Device.csproj), [FfmpegOptions.cs](src/ScarletRadioControl.Device/Options/FfmpegOptions.cs) and the [appsettings.json](src/ScarletRadioControl.Device/appsettings.json) `Ffmpeg` section match the shapes below, and [CameraVideoSource.cs](src/ScarletRadioControl.Device/Services/CameraVideoSource.cs) is rewritten behind the unchanged façade. No reference to the removed bindings survives anywhere outside this document.

The ffmpeg command line itself lives in [appsettings.json](src/ScarletRadioControl.Device/appsettings.json) as a per-platform template (section 2), so the invoked command is readable where it is configured and no encoder knowledge is left in code.

**Verified on the Raspberry Pi 4** (see Verification for the measurements). **Not yet verified on the Windows dev machine** — that leg needs the C920 plugged into a Windows host and a browser, so it remains open.

## Decisions

1. **Depacketize + `SendVideo`, not raw RTP forwarding.**
	- `src/SIPSorcery/net/RTP/Packetisation/H264Depacketiser.cs`: `public virtual MemoryStream ProcessRTPPayload(byte[] rtpPayload, ushort seqNum, uint timestamp, int markbit, out bool isKeyFrame)` — accumulates payloads per timestamp (single NAL types 1–23, STAP-A 24, FU-A 28, seq-reordered within a frame), returns one **Annex-B access unit** on `markbit == 1`, else `null`. Stateful, not thread-safe — only ever touched from the single receive loop.
	- `RTPSession.SendVideo(uint durationRtpUnits, byte[] sample)` — duration is in **90 kHz RTP units** (3000 at 30 fps). `VideoStream.SendVideo` resolves the **negotiated** format per connection (`GetSendingFormat().ToVideoFormat(); payloadID = sendingFormat.FormatID`) and re-packetizes Annex-B H264 into single-NAL/FU-A itself (packetization-mode=1). **ffmpeg's RTP payload type (96) is therefore decoupled from the negotiated payload type.**
	- `EncodedSampleDelegate` (`SIPSorceryMedia.Abstractions`) is `void (uint durationRtpUnits, byte[] sample)` — exactly `pc.SendVideo`'s shape, which is why the façade survives unchanged.
	- **The latency cost is ≈ 0.** ffmpeg emits each encoded access unit as a back-to-back packet burst on loopback (no pacing), and the depacketiser releases the frame on the **marker-bit packet** — it never waits for the next frame's timestamp. The hold is the burst duration (well under 1 ms) against a 33 ms frame interval; `SendVideo` then sends all of that frame's packets immediately. The real latency levers are the encoder settings and the browser jitter buffer (see Risks / notes).
	- `SendRtpRaw` rejected: it would change the consumer delegate type (breaking the façade and `WebRtcPeerSession.EncodedSampleDelegate`), require per-peer negotiated-payload-type plumbing, forfeit the access-unit patch point that decision 4 depends on, and its RTCP sender-report counter behavior is unverified.
2. **Ephemeral loopback port**: bind a `UdpClient` on `127.0.0.1:0` **before** spawning ffmpeg, read `((IPEndPoint)Client.LocalEndPoint).Port` into the ffmpeg args. Keep an `RtpPort` option (default `0` = ephemeral) as a debugging escape hatch — a fixed port allows wireshark/ffplay inspection.
3. **Keyframes**: fixed short GOP, `-g {Framerate}` in the configured command (so one keyframe per second at any framerate; edit the template for a different GOP). `ForceKeyFrame()` becomes a Debug-log no-op (an external encoder cannot service it); nothing reacts to RTCP PLI (SIPSorcery 10.0.15 exposes no typed PLI event on `RTCPeerConnection`, and there would be nothing to do with it). Late joiners and post-loss recovery are bounded at ~1 s.
4. **SPS/PPS before every IDR, guaranteed app-side**: the receive loop caches the latest in-band SPS (NAL 7) / PPS (NAL 8) and prepends them (4-byte start codes) to any access unit containing an IDR (NAL 5) that lacks them. Needed because ffmpeg's `v4l2_m2m_enc.c` never sets `V4L2_CID_MPEG_VIDEO_REPEAT_SEQ_HEADER` (verified at ffmpeg tag n8.1.2), and the RTP muxer's global-header flag moves x264's SPS/PPS out-of-band; `-x264-params repeat-headers=1` is belt-and-suspenders on the libx264 path. Do **our own NAL-type scan** — `H264Depacketiser`'s `isKeyFrame` out-param is unreliable (its source swaps the constants: `IDR_SLICE = 1; NON_IDR_SLICE = 5;`).
5. **Keep the class name `CameraVideoSource`** (it names the role, not the mechanism); rewrite the internals only. Close-on-idle semantics preserved: 0→1 consumers binds the socket, spawns ffmpeg and starts the receive loop; 1→0 kills ffmpeg and closes the socket.
6. **Supervision**: on unexpected ffmpeg exit while consumers > 0 → log exit code + stderr tail at Error, wait 1 s, respawn against the **same still-bound socket** (the receive loop is untouched). A generation counter guards a respawn racing a deliberate stop.
7. **Packages**: remove `SIPSorceryMedia.FFmpeg`. `SIPSorcery` 10.0.15 depends on `SIPSorceryMedia.Abstractions >= 10.0.15` (verified on nuget.org), which provides `EncodedSampleDelegate`/`VideoFormat`; `H264Depacketiser`/`RTPPacket` live in SIPSorcery core. No new package references needed.

## Changes

### 1. Packages

- [Directory.Packages.props](Directory.Packages.props): delete `<PackageVersion Include="SIPSorceryMedia.FFmpeg" Version="10.0.15" />`.
- [ScarletRadioControl.Device.csproj](src/ScarletRadioControl.Device/ScarletRadioControl.Device.csproj): delete `<PackageReference Include="SIPSorceryMedia.FFmpeg" />`.
- Run `dotnet restore` to validate.

### 2. [Options/FfmpegOptions.cs](src/ScarletRadioControl.Device/Options/FfmpegOptions.cs) — full rewrite

Drop `LibraryPath`/`LogLevel` and the `using SIPSorceryMedia.FFmpeg;` (both binding-specific). **The whole ffmpeg command line moves into configuration** as a per-platform template, mirroring the `LinuxPath`/`WindowsPath` pattern in `CameraOptions`. That leaves no encoder knowledge in code at all — no `BitrateKbps`, `GopSeconds`, `LinuxEncoder`/`WindowsEncoder`/`GetEncoder()`, and no `ExtraArgs` escape hatch, because the command itself is now the escape hatch. Mutable `get; set;` for the config binder, and deliberately no default value on either template:

```csharp
using System;

namespace ScarletRadioControl.Device.Options;

public class FfmpegOptions
{

	public string ExecutablePath { get; set; } = "ffmpeg";

	public string LinuxArguments { get; set; }

	public int RtpPort { get; set; }

	public string WindowsArguments { get; set; }

	public string GetArguments()
	{
		return OperatingSystem.IsWindows() ? this.WindowsArguments : this.LinuxArguments;
	}

}
```

**No in-code default for the arguments.** Neither template is initialised here, so [appsettings.json](src/ScarletRadioControl.Device/appsettings.json) is the single place the invoked command is written down and the two can never drift. `EnsureInitialised()` covers the consequence: a missing, empty or whitespace template throws `InvalidOperationException` alongside the existing camera-path checks, so an unconfigured platform fails as "no video offer" rather than as a `NullReferenceException` inside the tokeniser.

**Placeholders.** The template is split on whitespace and each token then has its `{Placeholder}` tokens substituted. Substituting *after* the split is what makes quoting unnecessary: `{CameraPath}` expands to `video=HD Pro Webcam C920` **inside an already-separated token**, so it reaches `ProcessStartInfo.ArgumentList` as one argument despite the spaces. Double quotes in the template are still honoured for grouping a literal value, and are stripped.

| Placeholder | Source | Notes |
| --- | --- | --- |
| `{RtpPort}` | the bound socket | The one placeholder that cannot be dropped — the port is ephemeral and unknown until bind time. |
| `{CameraPath}` | `CameraOptions.GetPath()` | Keeps the camera configured in one place. |
| `{Framerate}` | `CameraOptions.Framerate` | Falls back to 30 when unset; `-g {Framerate}` is what gives the 1 s GOP that `GopSeconds` used to compute. |
| `{Width}`, `{Height}` | `CameraOptions` | Used as `{Width}x{Height}`. |

An unrecognised placeholder throws `InvalidOperationException` naming the offender and listing the supported set, rather than passing a literal brace to ffmpeg and surfacing as confusing stderr.

### 3. [appsettings.json](src/ScarletRadioControl.Device/appsettings.json) — replace the `Ffmpeg` section

The command that will actually be invoked is visible here, which is the point of the template: tuning bitrate, GOP, preset or even the input format is a config edit, not a code change.

```json
"Ffmpeg": {
	"ExecutablePath": "ffmpeg",
	"LinuxArguments": "-hide_banner -nostats -loglevel warning -fflags nobuffer -f v4l2 -input_format mjpeg -video_size {Width}x{Height} -framerate {Framerate} -i {CameraPath} -an -c:v h264_v4l2m2m -pix_fmt yuv420p -profile:v 66 -b:v 2000k -g {Framerate} -keyint_min {Framerate} -f rtp -payload_type 96 rtp://127.0.0.1:{RtpPort}?pkt_size=1200",
	"RtpPort": 0,
	"WindowsArguments": "-hide_banner -nostats -loglevel warning -fflags nobuffer -f dshow -rtbufsize 64M -video_size {Width}x{Height} -framerate {Framerate} -vcodec mjpeg -i {CameraPath} -an -c:v libx264 -pix_fmt yuv420p -profile:v baseline -preset ultrafast -tune zerolatency -sc_threshold 0 -x264-params repeat-headers=1 -b:v 2000k -maxrate 2000k -bufsize 1000k -g {Framerate} -keyint_min {Framerate} -f rtp -payload_type 96 rtp://127.0.0.1:{RtpPort}?pkt_size=1200"
}
```

`-payload_type 96` must stay in step with the `RtpPayloadType` constant the receive loop filters on (see section 4); it is the only value in the template that code also knows about.

[appsettings.Development.json](src/ScarletRadioControl.Device/appsettings.Development.json) needs **no change** (it has no `Ffmpeg` section, and `HubUrl` is already `https://localhost:7001/hubs/web-rtc-hub` — .NET clients on the dev machine cannot resolve `*.dev.localhost`). Stale `Device__Ffmpeg__LibraryPath`/`LogLevel` env overrides on a deployed Pi bind to nothing and are harmless.

### 4. [Services/CameraVideoSource.cs](src/ScarletRadioControl.Device/Services/CameraVideoSource.cs) — rewrite the internals, same façade

Primary ctor unchanged: `(IOptions<DeviceOptions>, ILogger<CameraVideoSource>)`.

State was consolidated in a later simplification pass. Everything sharing the capture's lifetime — socket, receive task and its `CancellationTokenSource`, bound port, current `Process` — lives in one `CameraCapture` object, so start/stop is a single reference swap under the `Lock` and the supervisor recognises a superseded respawn by `ReferenceEquals` rather than a generation counter. `consumerCount` is gone: the multicast `EncodedSampleDelegate` is null exactly when the last consumer leaves, and the receive loop reads it with `Volatile.Read` instead of taking the lock, so a spawn holding the lock across ffmpeg's fork/exec (10–40 ms on a Pi) cannot stall the 30 Hz path. The per-stream fields (cached SPS/PPS, previous timestamp, the once-only warning flag) moved into a `CaptureStreamState` owned by the receive loop on the same terms as the depacketiser, which deleted the hand-written reset block at teardown. Only the stderr ring buffer remains a locked field, since the stderr callback runs on its own thread.

- **`EnsureInitialised()`** — cheap validation only, preserving the manager's "throw → no offer" behavior: throw `InvalidOperationException` if `Camera.GetPath()` is null/empty; on Linux also if `!File.Exists(LinuxPath)`.
- **`GetVideoSourceFormats()`** — static, no init required:
  `return new List<VideoFormat> { new VideoFormat(VideoCodecsEnum.H264, 96, 90000, "packetization-mode=1;profile-level-id=42e01f;level-asymmetry-allowed=1") };`
  (verified ctor: `VideoFormat(VideoCodecsEnum codec, int formatID, int clockRate = 90000, string parameters = null)`).
  **`profile-level-id` is load-bearing** — see the fmtp note in Risks. The advertised id and the loopback filter are now separate constants (`OfferedVideoPayloadType` / `LoopbackRtpPayloadType`); the latter reaches the ffmpeg command line through a `{PayloadType}` placeholder, so config and code can no longer disagree.
- **`SetVideoSourceFormat(VideoFormat)`** — no-op with a Debug log; the negotiated payload id is applied per-connection inside `VideoStream.SendVideo`.
- **`ForceKeyFrame()`** — no-op with a Debug log ("external encoder; viewers recover on the next GOP").
- **`AddConsumerAsync`** — under the lock: `encodedSampleConsumers += encodedSampleDelegate`, `consumerCount++`; on 0→1 start capture: bind the `UdpClient` on `IPEndPoint(IPAddress.Loopback, RtpPort)` with `Client.ReceiveBufferSize = 2 * 1024 * 1024`, read the bound port, spawn ffmpeg (`ArgumentList`, `RedirectStandardError`/`RedirectStandardOutput`, `CreateNoWindow`, `EnableRaisingEvents`, `Exited` → supervision), start the receive-loop `Task`.
- **`RemoveConsumerAsync`** — under the lock: `encodedSampleConsumers -= encodedSampleDelegate`, decrement; on 1→0 stop capture: bump the generation, `ffmpegProcess.Kill(entireProcessTree: true)` in try/catch + `WaitForExitAsync`, cancel and await the receive task outside the lock, dispose the socket, clear the SPS/PPS/timestamp state. Mirrors today's full close-on-idle.
- **Supervision** (`Exited` handler): if the generation is current and `consumerCount > 0` → log Error with the exit code + stderr tail, `Task.Delay(1s)`, respawn on the same port. stderr arrives via `ErrorDataReceived`, each line logged at Warning (the command runs `-loglevel warning`, so it is quiet in the steady state) and kept in the ring buffer; stdout (ffmpeg prints the RTP SDP there) logged at Debug.
- **Receive loop** (single `Task.Run`; one `H264Depacketiser` per loop lifetime):

```csharp
var result = await udpClient.ReceiveAsync(cancellationToken);
if (!RTPPacket.TryParse(result.Buffer, out var rtpPacket, out _)) { continue; }
if (rtpPacket.Header.PayloadType != RtpPayloadType) { continue; }
var annexBStream = h264Depacketiser.ProcessRTPPayload(rtpPacket.GetPayloadBytes(),
	rtpPacket.Header.SequenceNumber, rtpPacket.Header.Timestamp, rtpPacket.Header.MarkerBit, out _);
if (annexBStream == null) { continue; }
var accessUnit = this.EnsureParameterSets(annexBStream.ToArray());
var durationRtpUnits = this.ComputeDurationRtpUnits(rtpPacket.Header.Timestamp);
EncodedSampleDelegate? encodedSampleConsumers;
lock (this.lockObject) { encodedSampleConsumers = this.encodedSampleConsumers; }
encodedSampleConsumers?.Invoke(durationRtpUnits, accessUnit);
```

Verified APIs: `public static bool TryParse(ReadOnlySpan<byte> buffer, out RTPPacket packet, out int consumed)` and `public byte[] GetPayloadBytes()`; `RTPHeader` exposes `int MarkerBit`, `ushort SequenceNumber`, `uint Timestamp`, `int PayloadType`.

`ComputeDurationRtpUnits`: unchecked `timestamp - previousRtpTimestamp` (uint wrap-safe), with a `90000 / Framerate` fallback for the first frame or absurd deltas (0 or > 90000). `EnsureParameterSets`: Annex-B start-code scan of NAL types; cache types 7/8; if the access unit contains type 5 but no type 7, prepend the cached SPS+PPS (log once if none are cached yet).

**RTCP**: bind only the RTP port. ffmpeg sends RTCP SRs to port+1, where nobody listens; our socket never sends, so Windows' `WSAECONNRESET`-on-receive quirk cannot trigger. No `?rtcpport=` needed.

**`DisposeAsync`** — same as the 1→0 stop path.

### 5. ffmpeg argument lists (the configured defaults, expanded)

These are the command lines the section 3 templates expand to at 1280x720@30 — not something code assembles. Code's only jobs are to split the template, substitute the placeholders, and hand the tokens to `ProcessStartInfo.ArgumentList`. Common prefix: `-hide_banner -nostats -loglevel warning`.

**Windows dev** — C920 via dshow, MJPEG input (the C920 tops out at 10 fps for raw yuyv at 720p; 720p30 is MJPEG-only):

```
ffmpeg -hide_banner -nostats -loglevel warning
  -fflags nobuffer -f dshow -rtbufsize 64M -video_size 1280x720 -framerate 30 -vcodec mjpeg -i "video=HD Pro Webcam C920"
  -an -c:v libx264 -pix_fmt yuv420p -profile:v baseline
  -preset ultrafast -tune zerolatency -sc_threshold 0 -x264-params repeat-headers=1
  -b:v 2000k -maxrate 2000k -bufsize 1000k -g 30 -keyint_min 30
  -f rtp -payload_type 96 "rtp://127.0.0.1:<port>?pkt_size=1200"
```

The `-i` value is exactly `CameraOptions.WindowsPath` (already `video=`-prefixed), substituted into the `{CameraPath}` token so its spaces need no quoting. `-pix_fmt yuv420p` is load-bearing: C920 MJPEG decodes to yuvj422p and libx264 would otherwise emit 4:2:2 High, which browsers reject. `-tune zerolatency` disables B-frames, lookahead, and frame-threading delay; the tight `-bufsize` (0.5 s VBV) caps IDR size spikes so no single frame stalls the WebRTC leg — trade VBV size against quality if 2000k starts breathing visibly.

**Linux / Pi 4** — v4l2 with hardware encode:

```
ffmpeg -hide_banner -nostats -loglevel warning
  -fflags nobuffer -f v4l2 -input_format mjpeg -video_size 1280x720 -framerate 30 -i /dev/video0
  -an -c:v h264_v4l2m2m -pix_fmt yuv420p -profile:v 66
  -b:v 2000k -g 30 -keyint_min 30
  -f rtp -payload_type 96 "rtp://127.0.0.1:<port>?pkt_size=1200"
```

`-input_format mjpeg` is needed for 720p30 over UVC bandwidth; MJPEG decode is cheap on the Pi 4. Verified against `v4l2_m2m_enc.c`: `-b:v` → the `BITRATE` ctrl, `-g` → `GOP_SIZE`. **`-profile:v` must be the numeric `profile_idc` (`66` = baseline), not a name**: only libx264 registers the named profile strings, so `-profile:v baseline` is a fatal argument-parse error on `h264_v4l2m2m` rather than the non-fatal warning assumed during planning. `pkt_size=1200` keeps every datagram below MTU (no IP fragmentation).

Because each platform has its own template, the per-encoder branching that code used to do — named `baseline` and the x264-only `-preset`/`-tune`/`-sc_threshold`/`-x264-params`/`-maxrate`/`-bufsize` on Windows, numeric `66` on Linux — is simply the difference between the two strings above. `BuildFfmpegArguments` is now platform-agnostic.

### 6. Explicitly untouched

`WebRtcSessionManager.cs`, `WebRtcPeerSession.cs`, `WebRtcSignalingBackgroundService.cs`, `Startup.cs`, `Program.cs`, `Signaling/*`, `CameraOptions.cs`, `DeviceOptions.cs`.

## Verification

**Windows dev machine — not yet run** (needs the C920 on a Windows host plus a browser):

1. ffmpeg CLI: winget's `Gyan.FFmpeg.Shared` is now **9.0.1** (the 8.1.2 build was removed by the upgrade) and its `bin` directory is on the user PATH — fresh terminals resolve `ffmpeg`; any 6.x+ works. If the spawned process cannot resolve it, set `Device:Ffmpeg:ExecutablePath` to the full path.
2. Plug in the C920 (it was unplugged when checked during planning) and confirm the dshow name: `ffmpeg -list_devices true -f dshow -i dummy` must show `HD Pro Webcam C920`, matching `appsettings.Development.json`.
3. Standalone smoke test without the app: run the Windows command above with a fixed port (e.g. 5600) plus `-sdp_file test.sdp`, then `ffplay -protocol_whitelist file,udp,rtp -i test.sdp` → live video (1–2 s of ffplay buffering is normal and is ffplay's, not the pipeline's).
4. `dotnet build` (TreatWarningsAsErrors + style enforcement catch convention slips). Run `ScarletRadioControl.Web`, browse `https://web.scarlet-radio-control.dev.localhost:7001/device/test/control`, then run the Device app → expect the spawn log with the full args, the SDP on stdout at Debug, and live video ~1 s after `connected`. Then check: reload the client page (session replaced); close the tab → last consumer removed → ffmpeg disappears from Task Manager; kill `ffmpeg.exe` mid-stream → respawn within ~1 s and video resumes; `chrome://webrtc-internals` shows H264 with sane framesReceived and PLI counts.

**Raspberry Pi 4 — done:**

1. ✅ apt ffmpeg is **7.1.5** and registers `h264_v4l2m2m`; `/dev/video0` is the `HD Pro Webcam C920` and offers `MJPG` at both 1280x720 and 1920x1080.
2. ✅ The exact Linux argument list the app builds was run against a fixed port with a loopback listener in place. Over a ~6 s capture: **1597 packets, all payload type 96, largest datagram 1200 bytes** (so nothing is IP-fragmented), **183 marker bits ≈ 30 fps**, **7 IDRs ≈ one per 30 frames**, matching `-g 30`, and **zero sequence-number gaps**. `-profile:v 66` is accepted; ffmpeg's only stderr output is the benign swscaler range warning.
3. ✅ SPS **and** PPS appear in-band on **every one of the 183 frames** — bcm2835-codec does repeat the sequence header, which was the open question in Risks. Decision 4's app-side cache is therefore belt-and-suspenders on this platform, and the `repeat_sequence_header` driver fallback below is not needed.
4. ✅ CPU during steady-state encode is **0.65 of one core out of four** (~16 % of the machine), and that is dominated by the *software MJPEG decode*, not the hardware H264 encode.
5. ✅ The Device app starts with no native-library dependency at all, connects to the signalling hub, and spawns **no** ffmpeg process while no consumer is attached — the 0→1 half of the close-on-idle contract.
6. ✅ The configured templates expand correctly through the real code path: the Linux one is byte-identical to the command measured above, and the Windows one keeps `video=HD Pro Webcam C920` as a **single** argument — substitution happens after the split, so spaces never need quoting. Framerate falls back to 30 when unset, and an unknown placeholder throws a message naming it and listing the supported set.
7. Still open: a real browser peer against the deployed control page, which exercises fan-out, the 1→0 teardown, and respawn-after-kill.

If late joiners ever do show black video despite the app-side SPS/PPS injection, the driver-level fallback is `v4l2-ctl -d /dev/video11 --set-ctrl=repeat_sequence_header=1`.

## Risks / notes

- **`h264_v4l2m2m` quirks (Pi 4)**: loose bitrate control and no on-demand keyframes remain. Two planning assumptions were wrong in opposite directions, both now settled on-device: the profile option is **fatal** on a name rather than warning (hence the numeric `66`), while SPS/PPS turned out to be repeated in-band on **every** frame, not merely once — so the app-side cache never has to fire here. Fallback if the hardware encoder ever misbehaves: `"LinuxEncoder": "libx264"` at reduced resolution.
- **The fmtp must carry `profile-level-id`.** Omitting it is not a harmless default: RFC 6184 §8.1 requires a receiver to imply **baseline level 1.0**, whose limits are 99 macroblocks per frame (176x144), 1485 macroblocks/s and 64 kbps. A 1280x720@30 2000 kbps stream is 36x, 73x and 31x past those, and a browser that configures its decoder from the negotiated level renders the first keyframe off the in-band SPS and then freezes permanently. Measured on the Pi, the encoder's own SPS declares `profile_idc 66` / `level_idc 40` (baseline 4.0), so the advertised `42e01f` (constrained baseline 3.1, the profile every browser accepts, and exactly sized for 720p30) is paired with `level-asymmetry-allowed=1` to permit that difference.
- **Keyframes are large and unprotected.** Measured on the Pi: 11 keyframes averaging 48 kB, peaking at 59.5 kB, which `RTPSession.RTP_MAX_PAYLOAD = 1200` turns into ~52 FU-A packets sent back to back with no pacing. SIPSorcery's offer carries only `a=rtcp-fb:96 transport-cc` — no `nack` — so a single lost packet costs the whole keyframe, and the next chance is a similarly sized one a GOP later. Levers if this bites: lower the bitrate in the configured command, or drop the resolution.
- **Keyframe-on-PLI is lost** (the bindings honored it): packet-loss corruption on the WebRTC leg now persists up to the GOP length (1 s). Accepted.
- **Loopback UDP loss under load**: mitigated by the 2 MB `SO_RCVBUF` and `pkt_size=1200`; a lost marker packet costs one frame (the depacketiser discards partial access units on timestamp change). Optional: a Debug log on sequence gaps.
- **Fan-out is synchronous** on the receive loop (each peer's `SendVideo` does SRTP inline) — fine for the modeled few-peers scenario.
- **Input defaults to MJPEG** on both platforms; a camera lacking MJPEG at the configured mode fails visibly in the stderr log. Since the whole command including the input block is configuration, switching to `yuyv422` or another resolution is now an `appsettings` edit rather than a code change.
- **End-to-end latency budget** (what actually dominates): camera capture ≤ 1 frame (33 ms) + MJPEG decode + encode (x264 zerolatency has no lookahead; `h264_v4l2m2m` carries ~1–2 frames of inherent hardware pipeline) + loopback/depacketize/repacketize < 1 ms + network + **the browser's adaptive jitter buffer, typically 30–100 ms and the dominant receiver-side term**. Optional frontend follow-up, out of scope here: set `jitterBufferTarget = 0` (with `playoutDelayHint = 0` as the legacy fallback) on the video `RTCRtpReceiver` in [Control.tsx](src/ScarletRadioControl.Web.Frontend/src/pages/device/Control.tsx).
- Optional follow-up (not in this change): restore `docs/webrtc-signaling.md` from commit `624cce1` and update it with the real device implementation.
