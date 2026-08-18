# ScarletRadioControl.Device — WebRTC device side (SIPSorcery + FFmpeg)

## Context

`ScarletRadioControl.Device` is currently a stub worker. It must become the **device side** of the WebRTC system: a headless app running on a Raspberry Pi 4 (linux-arm64) with a USB webcam, streaming live H264 video to the browser viewer (`/device/:deviceId/control`). The signaling contract already exists and is fixed by [WebRtcHub.cs](src/ScarletRadioControl.Web/Hubs/WebRtcHub.cs) and the browser device simulator [ControlTest.tsx](src/ScarletRadioControl.Web.Frontend/src/pages/device/ControlTest.tsx): the **device is the WebRTC offerer**, joins the hub group via `JoinAsDevice`, offers on `ClientJoined`, and heartbeats every 1 s.

**Decisions:** in-process `SIPSorceryMedia.FFmpeg` bindings (not an external ffmpeg process); video only, no audio; publish stays portable/framework-dependent (no CI/RID changes).

All SIPSorcery APIs below were verified against the `v10.0.15` tag (SIPSorceryMedia.FFmpeg lives in the main sipsorcery repo at that tag, version 10.0.15, targets net10.0, binds native **FFmpeg 8.1** via FFmpeg.AutoGen 8.1.0; both packages confirmed on NuGet at 10.0.15).

## Changes

### 1. Packages

- [Directory.Packages.props](Directory.Packages.props): add three entries (indented like neighbors):
  - `<PackageVersion Include="Microsoft.AspNetCore.SignalR.Client" Version="10.0.11" />`
  - `<PackageVersion Include="SIPSorcery" Version="10.0.15" />`
  - `<PackageVersion Include="SIPSorceryMedia.FFmpeg" Version="10.0.15" />`
- [ScarletRadioControl.Device.csproj](src/ScarletRadioControl.Device/ScarletRadioControl.Device.csproj): add version-less references (CPM): `Microsoft.AspNetCore.SignalR.Client`, `SIPSorcery` (needed directly — `RTCPeerConnection` lives in core, SIPSorceryMedia.FFmpeg only depends on the abstractions), `SIPSorceryMedia.FFmpeg`. (`SIPSorceryMedia.Abstractions` + `FFmpeg.AutoGen` come transitively.)
- Run `dotnet restore` to validate the pins resolve.

### 2. Configuration

`appsettings.json` (Pi defaults) — new `Device` section:

```json
"Device": {
	"DeviceId": "5454d100-26bf-4c9b-bec7-0289aad847d4",
	"HubUrl": "https://scarlet-radio-control-web.azurewebsites.net/hubs/web-rtc-hub",
	"Camera": { "LinuxPath": "/dev/video0", "WindowsPath": "video=Integrated Camera", "Width": 1280, "Height": 720, "Framerate": 30 },
	"Ffmpeg": { "LibraryPath": null, "LogLevel": "AV_LOG_WARNING" }
}
```

`appsettings.Development.json`: `DeviceId: "test"`, `HubUrl: "https://web.scarlet-radio-control.dev.localhost:7001/hubs/web-rtc-hub"`, dev-machine `WindowsPath`.

New `Options/` classes (mutable `get; set;` for the config binder — the `required`/`init` record style stays reserved for wire DTOs):
- `Options/DeviceOptions.cs` — `const string SectionName = "Device"`, `DeviceId`, `HubUrl`, `Camera`, `Ffmpeg`.
- `Options/CameraOptions.cs` — `LinuxPath`, `WindowsPath`, `Width`, `Height`, `Framerate`; helper `GetPath()` → `OperatingSystem.IsWindows() ? WindowsPath : LinuxPath`.
- `Options/FfmpegOptions.cs` — `string? LibraryPath`, `FfmpegLogLevelEnum LogLevel`.

[Startup.cs](src/ScarletRadioControl.Device/Startup.cs): change signature to `ConfigureServices(HostBuilderContext hostBuilderContext, IServiceCollection serviceCollection)` (Program.cs unchanged — the method group binds to the two-arg `ConfigureServices` overload). Register: `Configure<DeviceOptions>(...GetSection(DeviceOptions.SectionName))`, `AddSingleton<CameraVideoSource>()`, `AddSingleton<WebRtcSessionManager>()`, `AddHostedService<WebRtcSignalingBackgroundService>()`. Delete the stub `BackgroundServices/WorkerBackgroundService.cs` and its registration.

### 3. Signaling DTOs — `Signaling/` (deliberate duplicates of the hub contract; Device must not reference Web)

- `RtcIceServer.cs` — `required string? Credential`, `required ICollection<string>? Urls`, `required string? Username` (mirror of `WebRtcHub.RtcIceServer`).
- `RtcSessionDescriptionInit.cs` — `required string Sdp`, `required string Type` (string `"offer"`/`"answer"`, exactly what the browser expects; SignalR JSON camelCases on the wire).
- `RtcIceCandidateInit.cs` — all nullable to tolerate browser `candidate.toJSON()`: `string? Candidate`, `string? SdpMid`, `int? SdpMLineIndex`, `string? UsernameFragment`.

### 4. `Video/CameraVideoSource.cs` — camera lifecycle (singleton, `IAsyncDisposable`)

Primary ctor `(IOptions<DeviceOptions>, ILogger<CameraVideoSource>)`.
- Lazy one-time init (lock-guarded), called before first offer: `FFmpegInit.Initialise(logLevel, libraryPath, logger)`; `new FFmpegCameraSource(path)` (dshow on Windows, v4l2 on Linux); `RestrictCameraFormats(f => f.Width == W && f.Height == H && f.FPS >= fps)` — if false, log available formats and continue with source default; `RestrictFormats(f => f.Codec == VideoCodecsEnum.H264)`; `OnVideoSourceError` → log + tear down so the next peer attempt re-creates the source.
- Pass-throughs: `GetVideoSourceFormats()`, `SetVideoSourceFormat(...)`, `ForceKeyFrame()`.
- `AddConsumer/RemoveConsumer(EncodedSampleDelegate)` — subscribe/unsubscribe on `OnVideoSourceEncodedSample` (multicast delegate is the fan-out; each peer's handler is literally `pc.SendVideo`). 0→1 consumers: `StartVideo()`/`ResumeVideo()`; 1→0: `PauseVideo()` (don't encode for nobody on the Pi).
- `DisposeAsync()` → `CloseVideo()` + `Dispose()`.

### 5. `WebRtc/WebRtcPeerSession.cs` + `WebRtc/WebRtcSessionManager.cs` (singleton)

`WebRtcPeerSession`: client connectionId, `RTCPeerConnection`, buffered `List<RtcIceCandidateInit>`, `remoteDescriptionSet` flag, the subscribed `EncodedSampleDelegate` (for unsubscribe), per-session lock.

`WebRtcSessionManager` (`ConcurrentDictionary<string, WebRtcPeerSession>` — supports client page reloads; one client per device is the modeled scenario, no per-peer encoders or renegotiation):
- `SetIceServers(ICollection<RtcIceServer>)` — SIPSorcery `RTCIceServer.urls` is a **single string**: flatten one entry per URL, set `username`/`credential` + `credentialType = password` when present, skip `turns:` URLs (SIPSorcery 10.0.15 has no TURN-over-TCP/TLS), cap at 10 (SIPSorcery max).
- `CreateOfferAsync(clientConnectionId)`:
  1. Existing session for id → close/replace.
  2. `cameraVideoSource` ensure-initialised (throw → caller logs, no offer).
  3. `new RTCPeerConnection(rtcConfiguration)`.
  4. Create the **identical negotiated data-channel set** as [useRtcPeerConnection.tsx](src/ScarletRadioControl.Web.Frontend/src/hooks/useRtcPeerConnection.tsx), before `createOffer`: `control` `{id=0, negotiated=true, ordered=false, maxRetransmits=0}`, `commands` `{id=1, negotiated=true}`, `telemetry` `{id=2, negotiated=true, ordered=false, maxRetransmits=0}`, `events` `{id=3, negotiated=true}` (unused for now; log `onopen` at debug).
  5. `pc.addTrack(new MediaStreamTrack(camera.GetVideoSourceFormats(), MediaStreamStatusEnum.SendOnly))`.
  6. `pc.OnVideoFormatsNegotiated += formats => camera.SetVideoSourceFormat(formats.First())`.
  7. `pc.onicecandidate += c =>` raise manager event `OnIceCandidate(connectionId, dto)` with `Candidate = "candidate:" + c.ToString()` (SIPSorcery `ToString()` omits the prefix browsers expect), `SdpMid = c.sdpMid ?? "0"`, `SdpMLineIndex`; null-guard.
  8. `pc.onconnectionstatechange`: `connected` → `camera.AddConsumer(pc.SendVideo)` + `camera.ForceKeyFrame()`; `failed`/`closed`/`disconnected` → `ClosePeer(id)`.
  9. `createOffer` → `await setLocalDescription` (starts trickle; browser buffers early candidates) → return own DTO `{Sdp, Type="offer"}`.
- `ApplyAnswer(id, dto)` — `setRemoteDescription({type = RTCSdpType.answer, sdp})`; result != `SetDescriptionResultEnum.OK` → log + `ClosePeer`; else flag set + flush buffered candidates (mirrors ControlTest.tsx).
- `AddIceCandidate(id, dto)` — ignore null/empty `Candidate` (end-of-candidates); buffer until remote description set; else map to SIPSorcery `RTCIceCandidateInit` (its `Parse` strips the `candidate:` prefix; `sdpMLineIndex` is `ushort`).
- `ClosePeer(id)` (idempotent: RemoveConsumer, `pc.close()`, remove) and `CloseAll()`.

### 6. `BackgroundServices/WebRtcSignalingBackgroundService.cs` (hosted service)

Primary ctor `(IOptions<DeviceOptions>, WebRtcSessionManager, ILogger<...>)`. In `ExecuteAsync`:
1. `new HubConnectionBuilder().WithUrl(hubUrl).WithAutomaticReconnect().Build()`.
2. Handlers (registered before start): `On<string>("ClientJoined")` → `CreateOfferAsync` + `InvokeAsync("SendOffer", deviceId, connectionId, offer)` (try/catch — camera may be absent); `On<string, RtcSessionDescriptionInit>("ReceiveAnswer")` → `ApplyAnswer`; `On<string, RtcIceCandidateInit>("ReceiveIceCandidate")` → `AddIceCandidate`.
3. Manager `OnIceCandidate` → `InvokeAsync("SendIceCandidate", deviceId, targetConnectionId, dto)` only when `State == Connected` (try/catch + log).
4. `Reconnected += ` re-invoke `JoinAsDevice(deviceId, null)` + `SetIceServers` — **mandatory**: reconnect means new connection id, group membership lost. Established peers keep streaming (media is P2P); leave them alone.
5. Main loop `PeriodicTimer(1s)` until cancelled: if `Disconnected` → try `StartAsync` + `JoinAsDevice` + `SetIceServers` (retry every ~5th tick; covers Pi booting before network and `WithAutomaticReconnect` giving up); if `Connected` → `InvokeAsync("DeviceHeartbeat", deviceId)` in try/catch (1 s cadence per ControlTest.tsx).
6. `finally`: `CloseAll()` + `DisposeAsync()` the hub connection.

## Verification

**Windows dev machine:**
1. Install FFmpeg 8.x **shared** build (`winget install Gyan.FFmpeg.Shared`); ensure its `bin` on `PATH` or set `Device:Ffmpeg:LibraryPath`.
2. Get webcam DirectShow name: `ffmpeg -list_devices true -f dshow -i dummy` → `WindowsPath: "video=<name>"`.
3. `dotnet build` (TreatWarningsAsErrors + style enforcement will catch convention slips), then run `ScarletRadioControl.Web`, browse `https://web.scarlet-radio-control.dev.localhost:7001/device/test/control`.
4. `dotnet run --project src/ScarletRadioControl.Device` — expect heartbeat indicator on the page, offer/answer in logs, live webcam video, candidate types in the page stats. Also test: client page reload (new `ClientJoined` replaces session), Web restart (device reconnect + rejoin).

**Raspberry Pi 4:** install .NET 10 arm64 runtime; FFmpeg **8.1 shared libs** (Raspberry Pi OS apt ships older — needs a source/third-party build; validate early with a minimal `FFmpegInit.Initialise` run, or point `LibraryPath` at the custom lib dir); `usermod -aG video`, check formats via `v4l2-ctl --list-formats-ext`; copy portable publish output and run with `DOTNET_ENVIRONMENT=Production` (per-unit overrides via env vars like `Device__DeviceId`).

## Risks / notes

- **FFmpeg 8.1 on the Pi** is the main deployment risk (no apt package at that version) — accepted trade-off of the in-process choice; code unaffected.
- **TURN over TCP/TLS unsupported** in SIPSorcery 10.0.15 (verified TODO in `IceServer.cs`) — the hub's `transport=tcp`/`turns:` entries are inert for the device; UDP-blocking NATs would break relay.
- **Device restart with an open client page** produces no new offer (client never re-triggers `ClientJoined`) — pre-existing behavior identical to ControlTest.tsx; fixing is a frontend change, out of scope.
- Encoding likely software (x264) at first — if 720p30 strains the Pi, drop config to 640x480 or lower framerate; hardware v4l2m2m via bindings is a later optimization.
- Prod hub URL (`scarlet-radio-control-web.azurewebsites.net`) is inferred from the Azure app name — confirm before flashing the Pi.
- Optional follow-up (not in this change): restore `docs/webrtc-signaling.md` from commit `624cce1` and update it with the real device implementation.
