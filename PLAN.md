# Gamepad → WebRTC control channel → RC car actuators

## Context

The repo already has a working WebRTC video path (Pi → browser) but **no control path**. Both ends
already agree on a pre-negotiated `control` data channel (id 0, unordered, `maxRetransmits: 0`) —
it is created on both sides and then dropped on the floor:

- `useRtcPeerConnection.tsx:18` creates `controlRtcDataChannel`, but `Control.tsx:24` destructures
  only `rtcPeerConnection` and discards the channels. Nothing ever calls `.send()`.
- `WebRtcSessionManager.cs:82-97` creates the mirror set and logs any inbound message at
  `LogDebug`, but the local `rtcDataChannels` array is never stored on the session.
- `useGamepad.tsx` exists but is imported by nothing, and is broken: it stores a one-shot `Gamepad`
  snapshot and never re-polls, so axis/button values are never read.
- `src/ScarletRadioControl.Device/DataChannels/` is an empty directory clearly reserved for this.

The goal: an Xbox controller in the browser drives a real 1/10 RC car on the Pi 4 — **RT forward,
LT reverse/brake, left stick X steering** — over `controlRtcDataChannel` as JSON, with a hard
failsafe so loss of signal stops the car.

### Hardware / host facts verified on this Pi

| Fact | Value |
|---|---|
| Board | Raspberry Pi 4 Model B Rev 1.1, 4 cores |
| OS | Raspberry Pi OS Trixie (Debian 13), .NET SDK 10.0.400 |
| Hardware PWM | **not enabled** — no `dtoverlay=pwm-2chan`, `/sys/class/pwm` is empty |
| `/dev/gpiomem` | `crw-rw---- root gpio` — user `deinok` is in group `gpio`, **no root needed** |
| GPIO12 / GPIO13 | currently `ip pd` (input, pull-down) — free |
| libgpiod | v2.2.1 (`libgpiod3`) installed |

### Decisions taken

1. **Software PWM** (`SoftwarePwmChannel`) on GPIO12 (throttle) and GPIO13 (steering) — no
   `config.txt` change, no reboot.
2. **Double-tap reverse** ESC state machine.
3. Wire payload = **normalized intent** (`throttle` / `steer` floats), not raw gamepad arrays.
4. Safety = **failsafe on stale input only**. No deadman button.
5. **The ESC has fixed endpoints and no SET button**, so it cannot be taught a pulse range → we emit
   the standard 1000 / 1500 / 2000 µs and correct any neutral offset with `TrimMicroseconds`.

Scope note: this plan covers software only. Wiring, power distribution, and connector layout are
out of scope and handled separately.

> **One flagged concern, then proceeding as specified.** `SoftwarePwmChannel` generates edges from a
> user-space thread, so pulse width jitters by roughly ±20-100 µs under load — and this Pi runs
> ffmpeg + x264 on the same 4 cores. Expect visible servo twitch and occasional ESC misreads. The
> design below therefore puts the PWM backend behind a factory (`Software` | `Hardware`) selected by
> `appsettings.json`, so switching to jitter-free hardware PWM later is a config edit plus a reboot,
> not a rewrite. Migration path is documented at the end.

---

## Wire protocol

Sent on `controlRtcDataChannel` at 50 Hz as UTF-8 JSON, ~55 bytes (~2.8 kB/s):

```json
{ "seq": 1482, "throttle": 0.62, "steer": -0.31 }
```

- `seq` — monotonic counter. The channel is **unordered with zero retransmits**, so the receiver
  must drop any frame whose `seq` is not greater than the last accepted one.
- `throttle` — `-1.0` (full reverse) … `0.0` (neutral) … `+1.0` (full forward). Computed browser-side
  as `RT.value - LT.value`.
- `steer` — `-1.0` (full left) … `+1.0` (full right), from `axes[0]`.

No timestamp field: browser and Pi clocks are unsynchronised, so the failsafe is measured against
the **Pi's own arrival time**, not a sender stamp.

---

## Frontend: `src/ScarletRadioControl.Web.Frontend`

### 1. `src/models/controlState.ts` (new)

```ts
export interface ControlState {
	seq: number;
	steer: number;
	throttle: number;
}
```

### 2. `src/hooks/useGamepad.tsx` (rewrite)

The current implementation is unusable — it caches a snapshot object, and its `gamepad !== undefined`
guard closes over a stale value under an empty dep array. Replace with a connection-tracking hook
that re-renders only on connect/disconnect and hands out an **index**, so the consumer can re-read a
fresh snapshot each frame via `navigator.getGamepads()` (required by spec — Chrome returns new
objects every poll).

```tsx
export default function useGamepad(): { gamepadId: string | null; gamepadIndex: number | null }
```

- Listens to `gamepadconnected` / `gamepaddisconnected`, plus an initial `navigator.getGamepads()`
  sweep for a pad already connected before mount (Chrome only surfaces pads after a button press).
- Warn once to the console if `gamepad.mapping !== "standard"`.
- Convert from named export to **default export**, matching `useEffectAsync` / `useRtcPeerConnection`.
  Nothing imports it today, so this is free.

### 3. `src/hooks/useControlStateSender.tsx` (new)

```tsx
export default function useControlStateSender(
	controlRtcDataChannel: RTCDataChannel | undefined,
): { controlState: ControlState | undefined; gamepadId: string | null }
```

Owns a `requestAnimationFrame` loop with a time gate at `sendIntervalMilliseconds = 20` (50 Hz):

- Re-reads `navigator.getGamepads()[gamepadIndex]` each tick.
- **Xbox standard-gamepad mapping**: `buttons[7].value` = RT, `buttons[6].value` = LT,
  `axes[0]` = left stick X.
- `throttle = clamp(applyDeadzone(rt, 0.03) - applyDeadzone(lt, 0.03), -1, 1)`;
  `steer = applyDeadzone(axes[0], 0.08)`. `applyDeadzone` **rescales** the remaining range to
  `0..1` so there is no step at the deadzone edge.
- Skips the send when `readyState !== "open"` or `bufferedAmount > 65536`.
- Increments `seq` in a ref; state is committed to React at ~10 Hz only (a separate time gate) so the
  page does not re-render 50×/s.
- Cleanup cancels the rAF handle.

> rAF stops in a backgrounded tab. That is *desirable* here: the device-side failsafe fires ~300 ms
> later and the car stops. Call this out in a code comment.

### 4. `src/pages/device/Control.tsx` (modify)

- Line 24 → `const { rtcDataChannels, rtcPeerConnection } = useRtcPeerConnection();`
- `const { controlState, gamepadId } = useControlStateSender(rtcDataChannels.controlRtcDataChannel);`
- Add a compact HUD overlay under the existing status `<p>`: gamepad id (or "no gamepad — press a
  button"), `controlRtcDataChannel.readyState`, and two horizontal bars for throttle and steer.
  Match the file's existing inline-style approach; do not introduce Tailwind here.

**Conventions to honour:** tabs (`.editorconfig`), **double quotes** (the one custom eslint rule in
`eslint.config.ts`), `import { type X }` for type-only imports (`verbatimModuleSyntax` is on),
`noUnusedLocals` / `noUnusedParameters` are on. Run `npm run lint` and `npm run build`.

---

## Device: `src/ScarletRadioControl.Device`

### 5. `Directory.Packages.props` (modify)

Add, keeping alphabetical order and the file's 2-space indent (it is the one file not using tabs):

```xml
<PackageVersion Include="System.Device.Gpio" Version="4.2.0" />
```

and a versionless `<PackageReference Include="System.Device.Gpio" />` in
`ScarletRadioControl.Device.csproj`. `4.2.0` verified as current stable on nuget.org.

### 6. `Options/` (new files, following the `CameraOptions`/`FfmpegOptions` pattern)

- **`ServoChannelOptions.cs`** — shared base for both actuators:
  `GpioPin` (software backend), `PwmChip` + `PwmChannel` (hardware backend),
  `NeutralPulseWidthMicroseconds` (1500), `MinimumPulseWidthMicroseconds` (1000),
  `MaximumPulseWidthMicroseconds` (2000), `Deadband` (0.05), `Invert`, `TrimMicroseconds`,
  `MaximumForwardScale` / `MaximumReverseScale` (both default `1.0`, i.e. inert — present so the car
  can be tamed for bench runs without a rebuild).
- **`ThrottleOptions.cs` : `ServoChannelOptions`** — adds `ReverseMode`
  (`DoubleTap` | `Direct` | `BrakeOnly`, default `DoubleTap`), `BrakeToReverseMilliseconds` (400),
  `NeutralHoldMilliseconds` (150), `ArmingHoldMilliseconds` (2000).
- **`SteeringOptions.cs` : `ServoChannelOptions`** — no extra members today; exists for symmetry and
  future trim/expo.
- **`PwmOptions.cs`** — `Backend` (`Software` | `Hardware`, default `Software`),
  `FrequencyHertz` (50), `UsePrecisionTimer` (true), `GpioDriver` (`RaspberryPi` | `LibGpiod` |
  `Default`, default `RaspberryPi`).
- **`ControlOptions.cs`** — `Enabled`, `FailsafeTimeoutMilliseconds` (300),
  `UpdateIntervalMilliseconds` (20), plus `Pwm`, `Steering`, `Throttle`.
- **`DeviceOptions.cs`** (modify) — add `public ControlOptions Control { get; set; } = new ControlOptions();`

### 7. `DataChannels/ControlState.cs` (new)

```csharp
public record ControlState
{
	public long Seq { get; init; }
	public double Steer { get; init; }
	public double Throttle { get; init; }
}
```

Deserialised with a `static readonly JsonSerializerOptions` using
`PropertyNamingPolicy = JsonNamingPolicy.CamelCase` and `PropertyNameCaseInsensitive = true`.

### 8. `DataChannels/ControlDataChannelHandler.cs` (new)

The parse/ownership layer, so `WebRtcSessionManager` never touches JSON.

- `Handle(string clientConnectionId, byte[] bytes)` — decode UTF-8, deserialise, validate
  (`double.IsFinite`, clamp to `[-1, 1]`), then forward to `VehicleControlService`.
- **Stale-frame rejection:** drop when `Seq <= lastAcceptedSeq`, except when
  `Seq < lastAcceptedSeq - 100` (a page refresh restarting from 0) — then reset the baseline.
- **Ownership:** the **most recently connected** peer owns control; frames from any other peer are
  dropped with a rate-limited `LogWarning`. Last-wins (not first-wins) so a browser refresh regains
  control immediately instead of waiting for the stale peer's ICE to time out.
- `ReleaseOwner(string clientConnectionId)` — if the departing peer was the owner, trigger failsafe.
- Malformed JSON is caught and logged at `LogWarning` with rate limiting; a 50 Hz stream of bad
  frames must not flood the journal.

### 9. `Services/PwmChannelFactory.cs` (new)

Single place that knows the backend split, mirroring how `CameraOptions.GetPath()` handles the
Windows/Linux divergence:

- `Software` → `new SoftwarePwmChannel(gpioPin, frequencyHertz, dutyCycle, usePrecisionTimer, gpioController, shouldDispose: false)`
  over a shared `GpioController`. Driver chosen by `PwmOptions.GpioDriver`; `RaspberryPi` maps to
  `new GpioController(new RaspberryPi3Driver())` (memory-mapped via `/dev/gpiomem`, far fewer
  syscalls per edge than libgpiod — meaningful for software PWM), falling back to the default
  controller with a `LogWarning` if construction throws.
- `Hardware` → `PwmChannel.Create(pwmChip, pwmChannel, frequencyHertz, dutyCycle)`.

### 10. `Services/VehicleControlService.cs` (new) — the core

Singleton owning both `PwmChannel`s, thread-safe via `System.Threading.Lock`
(`.editorconfig` sets `csharp_prefer_system_threading_lock = true:error`).

**Pulse mapping** (`SoftwarePwmChannel` exposes `DutyCycle`, not pulse width — convert):

```
scaled     = value >= 0 ? value * MaximumForwardScale : value * MaximumReverseScale
scaled     = Invert ? -scaled : scaled
span       = scaled >= 0 ? (Maximum - Neutral) : (Neutral - Minimum)
pulseUs    = Neutral + Trim + scaled * span            // then clamp to [Minimum, Maximum]
DutyCycle  = pulseUs / (1_000_000.0 / FrequencyHertz)  // 1500µs @ 50Hz -> 0.075
```

**Double-tap reverse state machine.** The ESC itself already does brake-then-reverse when a
transmitter passes through neutral; what this adds is *automating the neutral gap* so the driver can
simply hold LT. States: `Arming` → `Neutral` → `Forward` → `Braking` → `NeutralGap` → `Reverse`.

- `Arming` — held for `ArmingHoldMilliseconds` (2000) at neutral on startup so the ESC arms; input
  is ignored until it expires.
- `Forward` → LT pressed → `Braking` (output goes below neutral; the ESC reads this as brake).
- After `BrakeToReverseMilliseconds` (400) of continuous braking → `NeutralGap`: force neutral for
  `NeutralHoldMilliseconds` (150) regardless of stick, then → `Reverse` and pass LT through as real
  reverse.
- Releasing LT to within the deadband for ≥ `NeutralHoldMilliseconds` clears the "was forward" flag,
  so the *next* LT press from a standstill reverses directly with no brake phase.
- `Direct` mode skips the machine (linear map); `BrakeOnly` clamps output to `>= Neutral` for
  forward and treats LT as proportional braking without ever entering `Reverse`.

**Public surface:** `ApplyControlState(ControlState)`, `Tick()`, `Failsafe(string reason)`,
`IDisposable`. `ApplyControlState` records `lastControlStateReceivedAt` from `Stopwatch`/
`Environment.TickCount64` (monotonic — **not** `DateTime.UtcNow`). Writes to the PWM channels happen
only when the computed pulse width changes, since `SoftwarePwmChannel` sustains the waveform on its
own thread.

**When `ControlOptions.Enabled == false`** the service allocates no PWM channels and only logs the
control state at `LogDebug` — a dry-run mode for validating the wire protocol on a dev box with no
hardware attached. This is what `appsettings.Development.json` will set.

### 11. `BackgroundServices/VehicleControlBackgroundService.cs` (new)

Modelled on `WebRtcSignalingBackgroundService`'s `PeriodicTimer` loop:

- `PeriodicTimer(UpdateIntervalMilliseconds)` → `VehicleControlService.Tick()`.
- `Tick()` fires `Failsafe("stale input")` when `now - lastControlStateReceivedAt > FailsafeTimeoutMilliseconds`,
  logging **once** per failsafe entry rather than every tick.
- `StopAsync` → neutral both channels, then dispose. Covers SIGTERM/Ctrl-C so the car does not run
  away when the service stops.

Failsafe triggers, all converging on the same method: stale input (300 ms), `control` channel
`onclose`, peer disconnect via `ClosePeer`, process start (neutral before arming), process stop.

### 12. `WebRtc/WebRtcPeerSession.cs` (modify)

Add `public Dictionary<string, RTCDataChannel> RtcDataChannels { get; } = new Dictionary<string, RTCDataChannel>();`
so channels survive past `CreateOfferAsync` (today the local array is discarded, which also blocks any
future telemetry/events send path).

### 13. `Services/WebRtcSessionManager.cs` (modify)

At lines 81-97:

- Store each created channel into `webRtcPeerSession.RtcDataChannels[label]`.
- Route the `control` channel: `onmessage` → `controlDataChannelHandler.Handle(clientConnectionId, bytes)`;
  `onclose` → `controlDataChannelHandler.ReleaseOwner(clientConnectionId)`. Keep the existing
  debug logging for the other three channels.
- Update the stale comment on line 81.
- Fix line 95's `LogDebug($"…")` string interpolation to structured logging — it is the only place in
  the codebase deviating from the convention, and we are editing that block anyway.
- In `ClosePeer` (line 208), call `controlDataChannelHandler.ReleaseOwner(clientConnectionId)`.
- Inject `ControlDataChannelHandler` as a new primary-constructor parameter with a matching
  `private readonly` field, per the file's existing style.

### 14. `Startup.cs` (modify)

Three additions, one chained `serviceCollection` statement each, keeping the existing rough
alphabetical grouping:

```csharp
serviceCollection.AddHostedService<BackgroundServices.VehicleControlBackgroundService>();
serviceCollection.AddSingleton<DataChannels.ControlDataChannelHandler>();
serviceCollection.AddSingleton<Services.VehicleControlService>();
```

`AddHostedService` does not share the singleton instance, so `VehicleControlService` is registered
separately and injected into the background service.

### 15. `appsettings.json` / `appsettings.Development.json` (modify)

Add a `Device:Control` block (tab-indented, matching the file):

```jsonc
"Control": {
	"Enabled": true,
	"FailsafeTimeoutMilliseconds": 300,
	"UpdateIntervalMilliseconds": 20,
	"Pwm": { "Backend": "Software", "FrequencyHertz": 50, "GpioDriver": "RaspberryPi", "UsePrecisionTimer": true },
	"Throttle": { "GpioPin": 12, "PwmChip": 0, "PwmChannel": 0, "ReverseMode": "DoubleTap", "NeutralPulseWidthMicroseconds": 1500, "MinimumPulseWidthMicroseconds": 1000, "MaximumPulseWidthMicroseconds": 2000, "Deadband": 0.05, "ArmingHoldMilliseconds": 2000, "BrakeToReverseMilliseconds": 400, "NeutralHoldMilliseconds": 150, "MaximumForwardScale": 1.0, "MaximumReverseScale": 1.0 },
	"Steering": { "GpioPin": 13, "PwmChip": 0, "PwmChannel": 1, "NeutralPulseWidthMicroseconds": 1500, "MinimumPulseWidthMicroseconds": 1000, "MaximumPulseWidthMicroseconds": 2000, "Deadband": 0.03, "Invert": false, "TrimMicroseconds": 0 }
}
```

`appsettings.Development.json` overrides `"Enabled": false` so a dev machine runs dry.

---

## Verification

1. **Build**: `dotnet build` at the repo root. `TreatWarningsAsErrors=true` +
   `EnforceCodeStyleInBuild=true` means `.editorconfig` violations break the build — file-scoped
   namespaces, mandatory `this.`, braces, no target-typed `new()`, no collection expressions.
2. **Frontend**: `npm run lint && npm run build` in `src/ScarletRadioControl.Web.Frontend`
   (the double-quote rule and `noUnusedLocals` are the usual trip hazards).
3. **Dry run, no hardware** — `Control:Enabled: false`, run Web + Device + frontend, open
   `/device/{deviceId}/control`, press a button on the Xbox pad. Expect: HUD shows the pad id, bars
   track RT/LT/stick, and the Device log emits parsed `ControlState` at ~50 Hz. This proves the wire
   format end to end with nothing connected to the GPIO header.
4. **Signal-only, no motor** — `Enabled: true`, ESC unplugged, scope or logic-analyse GPIO12: expect
   50 Hz, 1500 µs at rest, 2000 µs at full RT, 1000 µs at full LT (after the double-tap sequence).
   `pinctrl get 12,13` should report the pins as outputs while the service runs. Watch the jitter
   here — this is where the software-PWM tradeoff shows up.
5. **Servo only** — with just the steering servo attached, confirm the stick centres and sweeps the
   full range; set `Steering:Invert` / `TrimMicroseconds` if reversed or off-centre.
6. **Neutral / trim check, wheels up.** The ESC cannot be taught endpoints, so verify ours match it.
   With the stick and triggers released, the motor must be *completely* still — any creep means the
   ESC's true neutral is not 1500 µs; adjust `Throttle:TrimMicroseconds` in ±10 µs steps until it
   stops.
7. **ESC on the bench, wheels up** — confirm arming (neutral held 2 s at startup), forward on RT,
   brake on LT, and that holding LT for ~550 ms transitions to reverse. Tune
   `BrakeToReverseMilliseconds` / `NeutralHoldMilliseconds` to the ESC's actual behaviour.
8. **Failsafe drills**, each must neutralise within ~300 ms and log exactly once:
   close the browser tab · unplug the controller · background the tab · `kill` the Device process
   (SIGTERM path) · pull the network.

---

## If software PWM jitter proves unusable

No code changes needed — the factory already handles it:

```
# /boot/firmware/config.txt
dtoverlay=pwm-2chan,pin=12,func=4,pin2=13,func2=4
dtparam=audio=off        # PWM peripheral contends with the 3.5mm jack on Pi 4
```

Reboot, then set `Device:Control:Pwm:Backend` to `"Hardware"`. `/sys/class/pwm/pwmchip0` appears with
two channels; the existing udev rule at `/lib/udev/rules.d/99-com.rules:12` chgrps it to group `gpio`,
and `deinok` is already in that group, so it still runs unprivileged. Same GPIO pins either way.
