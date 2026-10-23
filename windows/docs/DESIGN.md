# LSX II Volume Fixer for Windows: design notes

Developer document for the Windows port. Basis: the macOS version in this repository.
The user-facing description is [../README.md](../README.md); this file explains how and why the
Windows version differs from macOS.

Section numbers are stable: code comments refer to them as `docs/DESIGN.md §5.2`, `§10.9` and so on,
and to the numbered items below (`A12` deviations, `O17` open points, `R4` decisions, `M3` milestones).

**Terms used here and in code comments:**

| Term | Meaning |
| --- | --- |
| *same* (`Same`) | behaviour, names, texts and values as on macOS |
| *adapted* (`Adapted`) | same behaviour, different Windows mechanism |
| *deviation (Windows)* (`Deviation (Windows)`) | Windows forces different behaviour |
| *deviation (v1)* (`Deviation (v1)`) | follows from "v1 without an own audio driver" |
| *deviation (user)* (`Deviation (user)`) | a deliberate design choice that differs from macOS, or a direct consequence |
| *addition* (`Addition`) | only needed on Windows |
| *dropped* / *reserved (v2)* (`Dropped`) | not applicable in v1, or kept for a later driver |
| *proxy role* | what the virtual "KEF LSX II" device does on macOS (the volume the keys change); in v1 the app's *virtual volume* plays it (§6.7) |

IDs such as `CTRL-38`, `DRV-37`, `MAP-03`, `EDGE-01`, `UI-02`, `DISC-06`, `INST-12`, `UNIN-02` refer to an
internal inventory of the macOS behaviour that was used for the port; they are kept in comments as
cross-references only.

---

## 1. Goal and principle

**Goal.** The PC's volume keys control the network volume (`player:volume`, 0–100) of a KEF LSX II
connected over USB, in 50 steps of roughly equal dB (about 1 dB; as many steps as the Windows volume
keys have; macOS: 16 steps of 3–4 dB). With 50 steps only the per-step bound is guaranteed: each key
press is at most 1 dB away from the ideal step; a step's absolute position may drift from its ideal
dB position (§5.2). The audio stays on the USB cable and is never scaled.

**Principle: as close to macOS as possible.** Unchanged:

- app name "LSX II Volume Fixer", slug `LSXIIVolumeFixer`, version 1.0, identifier
  `io.github.roberdorfer.lsxiivolumefixer`, reserved proxy UID `io.github.roberdorfer.kefproxy.device`;
- all setting keys and types, all defaults except `minSpeakerVolume` (15 instead of 12);
- the measured curve, the step-table principle (equal dB distances on the measured curve between
  minimum and maximum), approach-from-above, reset detection;
- all macOS time constants (0.03 s, 0.1 s, 0.15 s, 1 s, 2 s, 3 s, 10 s, 15 s, 30 s, 120 s, 600 s);
  Windows-only times are listed in A70;
- log and menu texts EN/DE wherever the behaviour exists;
- flags, messages and test cases of the install/uninstall scripts;
- file and class structure (`Helper\`, `Driver\`, `scripts\`, `Tests\`, `docs\`; `Config`,
  `Controller`, `Speaker`, `SpeakerDiscovery`, `StatusMenu`/`RangeView`, `CoreAudioSupport`).

Deviations exist only where Windows or the "no driver" decision forces them, or where a
deliberate design choice differs. Each one is named where it applies and listed in §14.

**Fixed decisions:**

1. **v1 has no audio driver.** A virtual Windows endpoint needs a kernel driver with attestation
   signing (EV certificate) or test signing with Secure Boot off. v1 sends audio straight to the real
   USB endpoint, holds its Windows volume at 100 % (0 dB) and maps the volume keys to the network
   volume. A clean seam for a later driver stays (§13). For lossless playback v1 is also the better
   design: a virtual endpoint (v2) would lose the exclusive-mode path at source rate while the driver
   bridge holds the LSX II, and Spotify hides its exclusive switch for virtual devices (§13.2).
2. **C# / .NET 10**, WinForms, no third-party packages. The build does not depend on git.
3. **50 key steps instead of 16** (§5.1, §5.2), with a stricter rounding rule and no fine steps.
4. **Default range 15–65** (`minSpeakerVolume` 15 instead of 12).
5. **Lossless for everything that reaches the LSX II:** shared format fixed at **96 kHz / 24-bit** as on
   macOS (R4); exclusive mode stays allowed so players can play bit-perfect at source rate (R3, §5.9).
6. **First pin as on macOS**, without loudness protection (R5, §5.7.2).

**What v1 therefore cannot do 1:1** (details §6, §14):

- Windows always shows 100 % for the LSX II; the app brings its own volume OSD. There is no mouse
  slider for the speaker volume (macOS: menu bar, Control Center).
- The keys are taken with a low-level keyboard hook. Over elevated windows and on the lock screen
  this does not work; there Windows changes the USB endpoint and the app sets it back (§6.4).
- There is no always-present output device. When the LSX II briefly leaves USB (standby, cable),
  Windows moves audio to another device until it is back.
- The Windows shared-mode engine has a limiter that cannot be switched off (undocumented, known from
  community measurements); shared mode is bit-perfect only under conditions. Exclusive-mode players
  bypass mixer, resampler and limiter (§5.8, §5.9).

---

## 2. Starting point on Windows

Measured read-only on a test PC (Windows 11 build 26200); only general findings are listed.

| Topic | Finding |
| --- | --- |
| LSX II on USB | Composite device `USB\VID_27AC&PID_1060`. MI_00 = HID consumer control (8 one-bit inputs, all usage 0, no function driver). MI_01 = audio "LSX II" with the inbox driver `usbaudio2.sys`. No USB serial number: devnode, ContainerId and endpoint ID depend on the USB port. |
| Render endpoint | Shown as "Speakers (n- LSX II)", ID `{0.0.0.00000000}.{endpoint-id}`, parent `USB\VID_27AC&PID_1060&MI_01\<instance>`. A different USB port creates a new endpoint with a new ID and its own stored format. Core Audio returns endpoint IDs in lower case; that is how they appear in the log. |
| Endpoint volume | `IAudioEndpointVolume`: hardware volume and mute (mask 0x3, no hardware meter); range −42…0 dB in 6 dB increments; `GetVolumeStepInfo` = 51 steps (one Windows key = 2 %, so about six presses per audible 6 dB step: the same problem as on macOS). 2 channels. |
| Format | Shared-mode device format (`PKEY_AudioEngine_DeviceFormat`) was 48 kHz / 2 ch / 24-bit before the first pin; mix format float32. Exclusive mode accepts 44.1/48/88.2/96/176.4/192 kHz at 16/16 and 24/24 packed; not 24-in-32, not float. Engine period 10 ms. |
| Speaker internals | In cable mode (both speakers linked by cable, enabled in KEF Connect) the LSX II converts every source to 96 kHz / 24-bit internally, otherwise to 48 kHz / 24-bit (KEF manual). Whether the app can read this setting over the HTTP API is open (O28). |
| Effects | No enhancement APO on the endpoint; spatial sound off; Windows default ducking (other sounds reduced by 80 % during communication). |
| Network | DNS-SD via the WinRT `DeviceWatcher` (AQS with the DNS-SD protocol ID, `_http._tcp`, domain `local`) finds "LSX II" within tens of ms, already resolved (`HostName=lsxii-xxxxxxxxxxxx.local`, IPv4 and IPv6 addresses, port 80). The first enumeration completes only after ≈ 30 s. Windows resolves `.local` names via mDNS, but unreliably: `getaddrinfo` fails in bursts of ≈ 10 s (`SocketException NoData (11004)`); IPv6 address lists may contain stale prefixes that time out (O17). `GET /api/getData?path=player:volume&roles=value` → `[{"type":"i32_","i32_":<v>}]`. |

Handled explicitly: the **+24 dB jump** at the first pin when Windows was set low (§5.7.2), the
one-time **format switch to 96 kHz** (§5.8), the **LSX II not being the default output** at install
(§5.12), the **unreliable `.local` resolution** (§5.15).

---

## 3. Architecture

### 3.1 Signal paths

```
macOS
  Apps -> CoreAudio HAL -> KEFProxy "KEF LSX II" (virtual, 96 kHz) -> ring buffer -> LSX II (USB, 96 kHz / 24-bit)
  Keys, HUD, menu bar -> proxy volume (0..1) -> helper: curve, 16 steps, approach-from-above -> HTTP POST player:volume

Windows v1 (no driver)
  AUDIO
    Apps -> Windows audio engine (shared: mix float32, SRC -> 96 kHz, limiter)
         -> endpoint "LSX II" (usbaudio2, format 96 kHz / 24-bit pinned, endpoint volume 0 dB pinned)
    Exclusive-mode player -> same endpoint at source rate, no mix / SRC / limiter
  VOLUME
    volume keys -> app's keyboard hook (key swallowed)
                -> virtual volume 0..1 (proxy role, stored)  <- 1 s poll of player:volume (remote, KEF app)
                -> OSD, number in the tray icon
                -> controller: curve, 50 steps, approach-from-above -> HTTP POST player:volume
    Windows flyout, Quick Settings, apps -> endpoint volume -> "foreign change" (§6.4):
                never taken over; re-pinned to 0 dB after 300 ms of quiet
```

### 3.2 Roles compared

| Role | macOS | Windows v1 | Windows v2 (driver, §13) |
| --- | --- | --- | --- |
| Output device apps see | "KEF LSX II" (proxy) | the real LSX II endpoint | virtual endpoint "KEF LSX II" |
| Receiver of the volume keys | proxy volume | the app's keyboard hook | proxy endpoint volume |
| Volume display | macOS HUD | own OSD | Windows flyout |
| USB volume at 0 dB | helper | app | app |
| Fixed format | 96 kHz / 24-bit: driver + helper | 96 kHz / 24-bit shared format pinned by the app; exclusive mode allowed | bridge opens USB exclusively at 96 kHz / 24-bit |
| Key steps | 16 | 50 | 50 |
| Mute | driver writes zeros | USB hardware mute | driver writes zeros |
| Added latency | about 4–5 ms | 0 ms | a few ms |

### 3.3 Processes and threads

- **One program, two modes** (`Helper\main.cs`): no arguments = *launcher* (replacement for launchd
  `KeepAlive`, §10.9); `--agent` = the tray app.
- **Agent threads:**
  - **UI thread** (STA, WinForms): tray icon, menu, OSD, a hidden top-level window for system messages.
  - **Controller thread** (MTA) with a serial work queue (≙ `DispatchQueue(label: suiteName)`). It owns
    all controller state and all Core Audio objects. Delayed work runs on timers that only enqueue;
    cancellable work (send, auto-wake) uses a cancellation token per job.
  - **Hook thread** with its own message loop, only for `WH_KEYBOARD_LL` (§6.2).
  - **Thread pool**: `HttpClient` continuations and `DeviceWatcher` callbacks. Speaker replies are posted
    to the controller queue; discovery state sits behind a lock.
- `snapshot()` runs synchronously on the controller queue (≙ `queue.sync`). The controller never waits
  for the UI thread.
- **Rule for COM callbacks** (`IMMNotificationClient`, `IAudioEndpointVolumeCallback`, session events):
  only enqueue, never block, never call `IPolicyConfig`, register/unregister or format changes from
  inside a callback (the Windows form of DRV-37 "never call back into the HAL synchronously").

---

## 4. File mapping macOS → Windows

| macOS | Purpose | Windows | Status |
| --- | --- | --- | --- |
| `Helper/main.swift` (`AppDelegate`) | start line, discovery, controller, status menu | `Helper\main.cs` (`AppDelegate`, launcher/agent switch, WinForms `ApplicationContext` without a window) | adapted |
| launchd `KeepAlive`, `ThrottleInterval 5` | restart after any exit | `Helper\Launcher.cs` (launcher mode with a job object) | new (Windows) |
| `Helper/Config.swift` | constants, settings, curve, table, mapping, log, console, `L()` | `Helper\Config.cs` (`static partial class Config`, free functions in `static class Globals`) | same; storage adapted |
| `Helper/Controller.swift` | state machine | `Helper\Controller.cs` | same; audio calls adapted |
| `Helper/CoreAudioSupport.swift` | device helpers, `findProxy`, `findReal`, `findInput` | `Helper\CoreAudioSupport.cs` (endpoint enumeration, property store, `DeviceUid`, endpoint volume, sessions, `IPolicyConfig` wrapper, COM declarations) | adapted |
| `Helper/Speaker.swift` | HTTP API, failure counting, session reset | `Helper\Speaker.cs` (`HttpClient`) | same |
| `Helper/SpeakerDiscovery.swift` | Bonjour browse, match rule | `Helper\SpeakerDiscovery.cs` (DNS-SD via `DeviceWatcher`) | adapted |
| `Helper/StatusMenu.swift` (`StatusMenu`, `RangeView`) | menu bar item and menu | `Helper\StatusMenu.cs` (`NotifyIcon`, `ContextMenuStrip`, `RangeView`, icon drawing, renderer) | adapted |
| (macOS key handling, HUD) | – | `Helper\KeyHook.cs`, `Helper\VolumeOsd.cs` | new (deviation v1) |
| (proxy volume control in the driver) | – | `Helper\VolumeSurface.cs` (`IVolumeSurface`, `IRealOutput`, `KeyHookVolumeSurface`) | new (seam, §13) |
| `Helper/Info.plist` | bundle metadata | `Helper\LSXIIVolumeFixer.csproj` + `Helper\app.manifest` | adapted |
| `Driver/KEFProxyDriver.cpp` | virtual device | `Driver\README.md` (reserved) | dropped in v1, reserved (v2) |
| `scripts/install.sh`, `scripts/uninstall.sh` | install | `scripts\install.ps1`, `scripts\uninstall.ps1` (+ `.cmd` starters) | adapted |
| `scripts/kefstats.swift` | driver diagnostics | name reserved | dropped in v1 |
| `scripts/make_figures.py` | figures | not ported yet (the README uses the macOS figure) | open |
| `Makefile` | build, tests | `make.ps1` + `make.cmd`, same target names | adapted |
| `Tests/ConfigTests.swift` | pure-function tests | `Tests\ConfigTests.cs` + `.csproj` (macOS tests with 16 steps, plus 50-step tests) | same |
| `Tests/*_dry_run.sh` | script tests | `Tests\install_dry_run.ps1`, `Tests\uninstall_dry_run.ps1` | adapted |
| `docs/usb-volume-curve.md` | measured data | copy, "16 key steps" → "50 key steps", overshoot table for targets 15–40 | same / adapted |

`CoreAudioSupport` details: `allDevices()` → `IMMDeviceEnumerator::EnumAudioEndpoints(DEVICE_STATE_ACTIVE)`;
device UID → `DeviceUid(endpoint)` = instance ID of the parent devnode, e.g.
`USB\VID_27AC&PID_1060&MI_01\<instance>` (endpoint property `{b3f8fa53-0004-438e-9003-51a46e139bfc},2`,
fallback `IDeviceTopology` → connector 0, fallback 2 the endpoint ID); `findReal()` → first active
render endpoint whose `DeviceUid` contains `VID_27AC&PID_1060`, case-insensitive (A23); `findInput()` →
first active capture endpoint whose `DeviceUid` starts with `restoreInputUIDPrefix`; `AudioObjectID` in
logs → endpoint ID string; `OSStatus` → HRESULT (`0` or `0x%08X`).

**Naming rule.** Swift identifiers are carried over mechanically: types, methods and properties get a
capital first letter (`rescan` → `Rescan`), fields and locals stay (`lastSentSpeaker`), test methods keep
the Swift spelling (`test_…`), `static let` constants keep their names, free functions live in `Globals`
(`global using static`). Log texts, menu texts and setting keys are unchanged. P/Invoke and COM
declarations live in the file that uses them; there is no `Interop\` folder.

---

## 5. Behaviour in detail

### 5.1 Volume keys → 50 steps

The hook (§6.2) hands the key to the controller. One key = 1/50 (`keyStep`), also with modifiers;
every auto-repeat of a held key is a step.

```
Up / Down:  if muted: unmute;  s = clamp(s ± 1/50, 0, 1)
            Down and s <= 0.0005: s = 0, mute         (0 = speaker 0 + mute)
Mute:       toggle (USB hardware mute, §5.10)
then:       store s (§5.14), show OSD (§6.5), proxyVolumeChanged() -> scheduleSend(speakerVolume(forScalar: s))
```

From `proxyVolumeChanged()` on the flow is the macOS one. `speakerVolume(forScalar:)` keeps the macOS
structure, generalised from 15 to N − 1 = 49 intervals: `s ≤ 0.0005` → 0; otherwise
`pos = clamp((s − 1/50) / (1 − 1/50) · 49, 0…49)`, `i = min(48, Int(pos))`, linear between
`keyStepTable[i]` and `[i+1]`, rounded. So k/50 → table value k−1, 1/50 → minimum, 1 → maximum.
Division in `float`, rounding `MidpointRounding.AwayFromZero` like Swift `.rounded()` (.NET's default
banker's rounding differs at exactly .5). Pure function: `Config.ApplyVolumeKey`.

Deviations: keys are captured by the app (v1); 50 instead of 16 steps (user, A73); no fine steps
(user, A7: a quarter step of ~1 speaker unit is below the speaker's integer resolution).

### 5.2 Curve, step table, approach-from-above

Ported literally (C# `double`, same loop order):

- `dB(v)`: `v ≤ 0` → −120; `v ≥ 60` → −2.5 + 0.5·(v − 60); else v − 62.5 (relative to speaker 65, reached
  from above). `fromBelowCurve` 12…40 and `dBFromBelow(v)` as on macOS.
- `keyStepTable(min, max, steps)`: targets `dB(lo) + stepDB·i`, `stepDB = (dB(hi) − dB(lo)) / (steps − 1)`;
  first entry `lo`, last `hi`. For each step in between the nearest speaker value by measured dB, ties
  to the smaller, searched
  - **among values strictly above the previous step and at most `hi − (steps − 1 − i)`** when
    `max − min ≥ steps − 1` (A75);
  - otherwise only among `prev` and `prev + 1` (at most `hi`), never below `hi − (steps − 1 − i)`. Equal
    neighbours are then unavoidable, exactly `(steps − 1) − (max − min)` of them, and no value is skipped.
- Why: the macOS rule with 50 steps over 15–65 gives `… 30 31 31 32 …`, a key press with no effect.
  Without equal neighbours both rules give the same table, so the ported 16-step tests pass unchanged.
- Default 15–65 with 50 steps: `15 16 17 … 62 63 65` (1.0 dB per key, 0.5 dB between 60 and 63).
- Guaranteed with 50 steps: each step within 1 dB of `stepDB`; positions may drift (15–65: value 60 is
  1.4 dB above its ideal position; some ranges up to ≈ 6 dB). The 0.5 dB position bound holds only
  with 16 steps.
- `approachFromAbove(v, cap)`: nothing for `v ≤ 0` or where both curves agree (≥ 41 and 1); otherwise
  the smallest overshoot k = 1…8 with `v + k ≤ cap` and `dBFromBelow(v + k) ≥ dB(v) + 0.2`. Hold time
  0.1 s. With 1 dB steps the brief overshoot can be larger than the step (O27).
- The curve is a property of the speaker, measured with USB volume at 0 dB; v1 keeps the endpoint at
  0 dB, so it applies unchanged.
- **Bugfix EDGE-01 (A29):** macOS crashes when `maxSpeakerVolume` ≤ 11 and the minimum is unset; Windows
  uses `min(15, max − 1)` as the unset minimum.

### 5.3 Sending to the speaker (same)

`scheduleSend(v)` stores the value, cancels the pending job and flushes after 0.03 s (last value wins).
`flushSend()` skips while a send is in flight or when `v == lastSentSpeaker == lastKnownSpeaker`. Going up
(or unknown state) uses `via = approachFromAbove(v)`: send `via`, wait 0.1 s, send `v`; one retry of the
target if it fails after `via` succeeded. Log `proxy -> speaker: <v>[ (via <via>)]` or
`speaker setVolume <v> failed`. Measured (O20): batching 29–45 ms, hold 100–115 ms (timer resolution).

### 5.4 Speaker → PC sync (same)

Poll every `pollInterval` (1 s) on the console session only. A changed value is mirrored into the virtual
volume via `IVolumeSurface.SetFromSpeaker(s)` (no key event, no OSD, but stored), unless a send is in
progress or the value equals `lastSentSpeaker`. `scalar(forSpeakerVolume:)` with 50 steps. Start rule as on
macOS: the first set-up pulls the speaker value; afterwards the PC value is pushed (USB reappeared,
console regained, settings changed, reset). The tray number is redrawn immediately (A81). CTRL-38 kept 1:1.

### 5.5 Reset detection (same)

A jump of ≥ `resetJumpThreshold` (25) between two polls without a PC change counts as a speaker reset;
the PC value is pushed back: `push proxy -> speaker: <v> (speaker jumped <a> -> <b>, treating as reset)`.

### 5.6 USB reconnect

`IMMNotificationClient` added/removed/state-changed all enqueue `rescan()`. A new endpoint instance gets a
fresh `IAudioEndpointVolume` (with callback) and `IAudioSessionManager2`, then the volume pin, format pin
and format watch, then the macOS logic (push only after the first set-up, first-run default output,
restores). Windows keeps the endpoint ID on the same port, so a remove/state change of the real ID marks
`realInstanceGone` and the next `rescan()` treats it as a new instance. Another USB port is a new device.

**A85:** before `push proxy -> speaker (real device reappeared)` the app reads the speaker value; if the
speaker already has the PC value nothing is sent (`push proxy -> speaker: skipped, speaker already at <v>
(real device reappeared)`). Reason: the LSX II leaves and rejoins USB after every idle phase on Windows,
and a push from "unknown" always starts with the overshoot. Pure function `Config.ReappearPushNeeded`.

**Deviation (v1), DRV-14:** without a driver there is no persistent device. Apps get
`AUDCLNT_E_DEVICE_INVALIDATED`; Windows moves the default to another device. Only streams that follow the
default device (Media Foundation, `ActivateAudioInterfaceAsync(DEVINTERFACE_AUDIO_RENDER)`) come back
automatically. The default-output restore (§5.12) helps; Windows also restores by itself.

### 5.7 Endpoint volume pin

**5.7.1 Running.** Like `pinRealVolume()`: each channel (`GetChannelVolumeLevelScalar` < 0.999 →
`SetChannelVolumeLevelScalar(ch, 1.0f, ownGuid)`, logged as `element ch+1`), then the master (`element 0`).
Triggers: new device, pin timer (2 s, then 10 s), console only; mute untouched. `ownGuid` is a fixed
UUID v5 of `suiteName`, used to recognise own writes in callbacks. Addition: an
`IAudioEndpointVolumeCallback` re-pins foreign changes after 300 ms of quiet (§6.4).

**5.7.2 First pin and the +24 dB jump.** The first pin sets the endpoint from whatever Windows had (on
the test PC ≈ −24 dB) to 0 dB, immediately, without touching the speaker. The speaker then sounds
as loud at its value as on a Mac. macOS does the same (R5). Rejected: leaving the endpoint attenuated;
pinning on the first key press (then even "down" would be up to 23 dB louder).

### 5.8 Format pin 96 kHz / 24-bit

Target as on macOS (`pinnedRealRate = 96000`, `pinnedRealBits = 24`).

- **Read:** `PKEY_AudioEngine_DeviceFormat`. Matches when 96000 Hz, 24 valid bits, PCM; then nothing is
  written or logged.
- **Write:** `IPolicyConfig::SetDeviceFormat(id, devFmt, mixFmt)`: `devFmt` = WAVEFORMATEXTENSIBLE PCM
  96 kHz 24/24, channels and mask from the current format; `mixFmt` = the same as float32.
- **Triggers:** as on macOS plus `OnPropertyValueChanged` for `PKEY_AudioEngine_DeviceFormat` of this
  endpoint. Log `pin real format: <rate> Hz/<bits>-bit[ float] -> 96000 Hz/24-bit (<err>)`.
- The first pin per endpoint (e.g. 48 → 96 kHz) briefly invalidates running shared streams. Windows stores
  the format per endpoint, so another USB port is pinned once more. A failure (exclusive app holds the
  device) is logged and retried on the next 10 s tick.
- **Why 96 kHz (R4):** in cable mode the speaker works at 96 kHz; every source is then converted at most
  once (44.1/48 kHz once by Windows, presumably not again in the speaker; 96 kHz sources not at all;
  hi-res sources down to 96 kHz without losing anything the speaker could use). 48 kHz would keep 48 kHz
  sources bit-perfect but convert 44.1 kHz twice; 192 kHz converts everything twice. Assumption shared
  with macOS: the speaker skips its SRC for a 96 kHz input (not documented).
- **Exclusive mode stays allowed** (R3, A15): the endpoint settings "Allow applications to take exclusive
  control" and "Give exclusive mode applications priority" stay at the Windows defaults; the app neither
  reads nor writes them. Exclusive players bypass mixer, SRC and limiter and send their source rate (16 or
  24-bit integer packed only). Cost: other apps are silent meanwhile; exclusive streams may not appear as
  sessions (O7). The hardware volume pin and mute also apply in exclusive mode.
- If `IPolicyConfig` is unavailable: log once `IPolicyConfig unavailable (<err>): default output and format
  pins disabled`, only display the format.

### 5.9 Lossless: bit accuracy, conversions, latency

The LSX II converts every source internally (48 kHz wireless, 96 kHz cable mode); its DSP (crossover,
bass extension, EQ) always runs, there is no bypass. End-to-end lossless is impossible with this speaker;
reachable is **bit-perfect up to the USB input** with **as few conversions as possible**.

| Source | Path | Bit-perfect at the USB input? |
| --- | --- | --- |
| Player in exclusive mode | WASAPI exclusive at source rate, no mixer/SRC/limiter | yes, if 16/24-bit integer, player volume 100 %, no player DSP |
| 96 kHz source in shared mode | engine without SRC: float mix, limiter, 24-bit conversion | very likely, if alone, peaks below the limiter, no dithering (unknown, O24) |
| 44.1 / 48 kHz and other rates in shared mode | one Windows SRC to 96 kHz | no |
| several streams at once | engine mixes | no |

Shared-mode conditions: source at 96 kHz, alone, session/stream volume 100 %, no ducking, enhancements,
spatial sound and mono audio off, peaks below the limiter threshold. Players with exclusive mode (as of
2026-10, to be verified with the device, O29): Spotify desktop (Premium), Qobuz, TIDAL, Amazon Music,
foobar2000, Audirvana; without: Apple Music for Windows, Deezer, browsers, games, VLC 3.x. Windows has no
"follow the source rate" mode for shared mode; the app cannot switch formats per source either.

What the app does: holds endpoint volume (hardware) at 1.0, pins the shared format, never scales samples
or sessions, does not touch enhancements/ducking. Added latency: **0 ms** (A51). Deviations: the shared
engine's `CAudioLimiter` cannot be disabled (A11); dithering on float → int24 is undocumented (O24).

### 5.10 Mute

Mute is the USB endpoint's **hardware mute** (`SetMute(…, ownGuid)`): silence from the PC, speaker value
unchanged. Mute from Quick Settings or the mixer is the same mute. The volume pin never touches mute.
**Mute across restarts:** macOS resets the proxy mute at every driver start; Windows stores the endpoint
mute. The console instance therefore lifts the endpoint mute once per system boot as soon as the LSX II
endpoint is present, then stores the boot time as `State\muteResetBoot` (boot time = now −
`GetTickCount64`, same boot within 120 s tolerance). Limit: shutdown with Fast Startup does not reset
uptime, so the mute survives it (A10, R12).

### 5.11 Auto-wake and Plan B

"Running" = at least one `AudioSessionStateActive` session on the target endpoint (≙
`DeviceIsRunningSomewhere`), via `IAudioSessionManager2` notifications plus a 1 s session scan as a safety
net (reports only changes). After 2 s of playback (console, `autoWakeOnPlayback`): `getSource`, and on
`standby` log `playback started while speaker in standby: waking it on USB` and `setSpeakerSource("usb")`.

**Plan B (A84).** On Windows the LSX II leaves USB after a few idle minutes while still reporting
`physicalSource = usb`; with no endpoint nothing can "run" on it. So while the LSX II is gone and its
return as default output is pending, sessions on the substitute default output (eMultimedia) count as
running; after 2 s the app wakes from `standby` and also, when the source is `usb` without a USB device,
selects `usb` again (`playback started while LSX II is gone from USB: selecting USB again`). Limited to once
per loss episode; if the endpoint is not back within 10 s, log once `LSX II did not return to USB within
10 s after selecting USB again; next attempt in 60 s at the earliest`. The pending return per role is
stored in HKCU `restoreOutputOnReturn` (bitmask 1 console, 2 multimedia, 4 communications) and read at
start; a fresh install (no `defaultOutputSetFor`) without the LSX II on USB counts as pending for all
roles; a deliberate user choice stores 0. Pure functions: `Config.AutoWakeAction`, `Config.PlanBActive`,
`Config.RestorePendingAtStart`. To be verified on the device (O25).

### 5.12 Default output

macOS state machine: (a) default == real device → switch to proxy; (b) default == target → remember;
(c) target was default and the device is gone → restore on return; (d) anything else is the user's choice.
Windows:

- Target `surface.OutputEndpointId` = the LSX II endpoint in v1 (`real ?? findReal()`), the proxy in v2.
  Branch (a) is inactive in v1 and stays for v2.
- **Three roles** (eConsole, eMultimedia, eCommunications), one unchanged state machine per role;
  `OnDefaultDeviceChanged(eRender, role, id)` enqueues `checkDefaultOutput(role)`. Log lines get the
  suffix ` [console|multimedia|communications]`.
- Extra branch between (c) and (d): pending, device absent and the default is still the substitute that
  applied at the loss (or first evaluation after start) → stays pending, no log line (A84).
- **First run** (`firstRunDefaultOutput`): all three roles; marker `defaultOutputSetFor` per user =
  port-independent ID `USB\VID_27AC&PID_1060&MI_01` (v2: `proxyUID`), set only when all roles succeed.
- **Set:** undocumented `IPolicyConfig::SetDefaultEndpoint(id, role)`; failures retried on next `rescan()`.
- Windows itself restores a returning default device unless the user picked another one; the state
  machine stays as an idempotent safety net.

### 5.13 Optional input restore

Same state machine per capture role, set with `SetDefaultEndpoint`. Prefix rule (A25): a `DeviceUid`
starting with `USB\` with at least three segments loses its last segment (port/serial), e.g.
`USB\VID_1234&PID_5678&MI_00\<instance>` → `USB\VID_1234&PID_5678&MI_00`; other IDs (HDAUDIO, Bluetooth,
SWD) stay complete. The submenu lists active capture endpoints.

### 5.14 Multiple users

- HKLM `Run` starts one instance per sign-in with the Explorer shell; mutex
  `Local\io.github.roberdorfer.lsxiivolumefixer` prevents doubles.
- Console: `WTSGetActiveConsoleSessionId() == own SessionId`, polled every second; `0xFFFFFFFF` keeps the
  last state (A57). Same gating list as macOS, plus the endpoint-volume callback handling, the boot mute
  reset and the hook.
- **Virtual volume machine-wide** like the driver's host storage: `HKLM\SOFTWARE\io.github.roberdorfer.lsxiivolumefixer\State`,
  value `volume` (REG_SZ, invariant, default 0.5). The installer grants Users (S-1-5-32-545) only
  `SetValue` on that subkey (no `KEY_WRITE`, which includes `KEY_CREATE_SUB_KEY`). The app opens `State`
  only with `QueryValues | SetValue`. Re-read before the push on console gain, since another session may
  have changed it.

### 5.15 Network errors and self-relaunch

- `HttpClient` with `SocketsHttpHandler`, `UseCookies = false`, `UseProxy = false` (Windows' `<local>`
  bypass does not cover `.local` names), 3 s budget (`Timeout` and `ConnectTimeout`).
- **ConnectCallback (A43)** replaces Happy Eyeballs (.NET has none): candidates are the last good address,
  then the DNS-SD addresses of the chosen service, then the resolver result (running in parallel);
  attempts staggered every 250 ms (RFC 8305), first success wins; re-resolve every 250 ms within the budget.
  URL and `Host` header keep the host name. Measured (O17): the callback keeps the budget, and with DNS-SD
  candidates all requests succeed even during resolver bursts.
- Counting, session reset and escalation as on macOS (3 failures → new session; 3 more → escalation; only
  within 120 s of console gain and at most every 600 s via `lastSelfRelaunch`). Log `…: exiting so the
  launcher relaunches the helper`, then `ExitApp(0)`.

### 5.16 Speaker discovery

`DeviceInformation.CreateWatcher(aqs, props, AssociationEndpointService)` for `_http._tcp` in `local`.
Match rule unchanged: name contains "LSX II" (case-insensitive) or host starts with `lsxii-`. No resolve
step (host already resolved in `Added`; else evaluated on `Updated`). Counting per
`InstanceName.Domain`; an `Updated` that changes host/name/domain counts as remove + add. `Stopped` with
`Aborted` (A67): log `speaker discovery: watcher aborted, restarting`, restart immediately, then after
10 s doubling up to 600 s; the previously chosen speaker is taken back silently, others wait for
`EnumerationCompleted` (DISC-06). The watcher lives in a static field (GC). The `.local` host name is
stored, addresses only serve as connection candidates. The 30 s hint names
`reg add HKCU\Software\io.github.roberdorfer.lsxiivolumefixer /v speakerHost /d <host or IP> /f`. The word
"Bonjour" stays in logs and menu (R1). No firewall rule is needed.

### 5.17 Speaker HTTP API (same)

`GET /api/getData?path=player:volume&roles=value` (query built literally, `:` not escaped);
`path=settings:/kef/play/physicalSource` for the source; `POST /api/setData` with exactly
`{"path":…,"roles":"value","value":…}` and `Content-Type: application/json` (no charset). Success rules as
on macOS. Headers: `Accept: */*`, `Accept-Encoding: gzip, deflate, br`; no User-Agent (A78).

### 5.18 Start and controller functions (same)

Agent start: log `LSX II Volume Fixer 1.0 starting; speaker=<override|auto (Bonjour)> range=<min>-<max>`,
discovery (only without override), `controller.Start()`, `StatusMenu`, message loop. `Start()` registers
`IMMNotificationClient`, runs the first evaluation, logs `console session: <bool>`, starts timers (poll 1 s,
pin 2 s/10 s, source 3 s/15 s). `snapshot()`, `settingsChanged(pushVolume)` and `setSpeakerSource(src)` as
on macOS. Controller state keeps the macOS names (CTRL-02); `proxy` is the surface, `real`/`input` are
endpoint ID strings, `isDefault`/`restoreOnReturn` are per role.

---

## 6. Volume keys and display

### 6.1 Why different

On macOS the proxy device has its own fine volume that the OS routes keys, HUD and sliders to. In v1 the
default device is the real endpoint, whose Windows volume must stay at 100 %; every Windows UI would move
exactly that, in 6 dB steps. So the app must take the keys before Windows does and show its own display.

### 6.2 Taking the keys: `WH_KEYBOARD_LL`

- Low-level hook on its own thread with a message loop; callback as an `UnmanagedCallersOnly` function
  pointer that only reads `vkCode`, flags, extra info and Alt/Win state, enqueues a key event and returns 1.
  No logging, COM or network inside the callback.
- Swallowed: `VK_VOLUME_UP` (0xAF), `VK_VOLUME_DOWN` (0xAE), `VK_VOLUME_MUTE` (0xAD), down and up.
- `interceptActive = onConsole && LSX II endpoint present && LSX II is default for eConsole or
  eMultimedia` (read live, independent of `autoSwitchDefault`). Otherwise keys go to Windows (headphones
  work as usual).
- Speaker unreachable: keys are still swallowed; virtual volume and OSD change, the send fails and is logged.
- **Mask key (addition):** when a volume key is swallowed while Alt or Win is held, the callback injects
  one `vkE8` down/up with its own extra info, so Windows does not open the Start menu or a menu bar.
- **Robustness (A66):** Windows silently removes slow hooks, so the hook is reinstalled on unlock, console
  switch, resume and every 60 s (new first, then old removed). Log `keyboard hook installed (<reason>)`
  only on the first install and event-driven reinstalls.
- Rejected: Raw Input (cannot block), `RegisterHotKey` (per combination, racy), shell hook /
  `WM_APPCOMMAND` (observe only), a filter driver, hiding the native flyout.

### 6.3 Capture limits (deviation Windows, A5/A6)

Elevated foreground windows (UIPI), the secure desktop, UAC prompts and the lock screen do not reach the
hook; Windows changes the endpoint by 2 % and shows its flyout, and §6.4 re-pins it. `uiAccess=true`
would need a signed binary with a root certificate (R7: not in v1). The OSD cannot appear there.

### 6.4 Other volume sources (addition)

Flyout, Quick Settings, mouse wheel on the speaker icon, Settings, volume mixer, `mmsys.cpl`, apps using
`IAudioEndpointVolume`, missed keys. Detection: `IAudioEndpointVolumeCallback`; events with
`guidEventContext == ownGuid` are own writes. **Rule A (as macOS):** a foreign mute change is the same
mute and needs nothing; a foreign level is never taken over and never sent; after 300 ms without further
foreign changes `pinRealVolume()` runs (no own log line, no OSD); console only. Alternative rule D
("take over quieter values") was rejected (R11). Consequence: Windows UI always shows 100 % for the LSX II;
the real value is in the tray icon, menu header and OSD.

### 6.5 Own OSD (deviation v1, A2)

Only for the app's own keys (speaker-side changes stay silent, like macOS `applyToProxy`). Speaker glyph
(Segoe Fluent Icons `E992`–`E995`, `E74F` muted) and a bar of `s`, no number. Windows 11 flyout look:
layered window with per-pixel alpha (rounded corners and shadow drawn into the bitmap), light/dark,
accent colour, honours transparency/animation/contrast settings. Never takes focus, click-through
(`WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_LAYERED | WS_EX_TRANSPARENT`), bottom
centre above the primary taskbar, PerMonitorV2, hidden after 2 s (O15), not shown over exclusive D3D
full screen.

### 6.6 The LSX II's HID interface (MI_00)

Consumer control with 8 inputs, all usage 0: Windows cannot turn it into `VK_VOLUME_*`, so there is no
feedback loop between speaker and hook. Whether the KEF remote triggers anything on the PC is O8.

### 6.7 The seam: `IVolumeSurface`

The proxy role sits behind an interface (code in §13.1). v1 `KeyHookVolumeSurface`: hook, OSD, stored
scalar, endpoint mute (with boot reset), sessions of the LSX II endpoint as "running",
`OutputEndpointId` = LSX II endpoint. v2 `VirtualEndpointVolumeSurface`: endpoint volume and sessions of
the driver endpoint; hook and OSD off. `Controller` and `Config` see only the interface.

---

## 7. Tray menu

### 7.1 Icon

WinForms `NotifyIcon` + `ContextMenuStrip` (WPF and WinUI 3 rejected). Same geometry as macOS
`speakerIcon()`, drawn with GDI+ at `SM_CXSMICON`, white/black by taskbar theme (A46), dimmed (≈ 43 %
alpha) without the USB device. With `showVolumeInMenuBar` the icon shows the number instead (A45;
Bahnschrift SemiBold Condensed, fallback Segoe UI Bold). Old HICONs are destroyed. Refresh every 2 s and
on menu close; paused while the menu is open. Left and right click open the same menu (A47); clicks within
300 ms after closing are ignored. New icons land in the overflow; only the user can pin them (A50).

### 7.2 Menu items

Order, separators and texts as in `StatusMenu.swift`: header (not connected / not reachable / searching /
`LSX II: <v> (<dB> dB)`), `Output: LSX II (USB) ✓` or `another device` (A21), `Speaker: <host> (Bonjour)`,
`USB format: 96 kHz / 24-bit`, `Speaker source: …`, `Input: …`, `Inactive: another user owns the screen`;
`Volume range (PC 0–100 % → speaker)` with the RangeView; checkmarks `Keep LSX II as default output`,
`Wake speaker automatically on playback`, `Show volume in the taskbar`; submenu `Restore input after USB
reconnect`; `Wake speaker / source USB`; `Apply these settings for all users…`, `Open log`, `Restart LSX II
Volume Fixer`, `LSX II Volume Fixer 1.0`. All EN/DE via `L()`. No quit item (as on macOS).

### 7.3 Details

The menu is rebuilt from one `snapshot()` right before `Show()`. Info lines are disabled items without
hover highlight (A48). Own Windows-11-style renderer, rounded popup corners via DWM, menu scaled once to
its DPI. Checkmarks log `setting <key> = true|false`. **RangeView:** 300×120 DIP, max slider 20–100, min
slider 1–40, reacts only to user moves, 0.15 s debounce, then `settingsChanged(pushVolume: true)`. Labels
`Maximum: 65 (0.0 dB)`, `Minimum: 15 (-47.5 dB)`, step line `Each key: 1.0 dB …`, step list with runs
collapsed (`15–63 65`) or only ends and count (`1 … 100 (50 values)`, A76). Numbers formatted invariant.
Arrow/page keys and the mouse wheel move the sliders too (A79). Language: `L(en, de)` by UI culture.

---

## 8. Settings

### 8.1 Location and precedence

| macOS | Windows |
| --- | --- |
| user defaults domain `io.github.roberdorfer.lsxiivolumefixer` | `HKCU\Software\io.github.roberdorfer.lsxiivolumefixer` |
| `/Library/Preferences/…plist` | `HKLM\SOFTWARE\io.github.roberdorfer.lsxiivolumefixer` (64-bit view) |
| user > system-wide > built-in default; read live | same, no cache |

`Software\Policies` is not used (HKLM would win over HKCU there).

### 8.2 Keys

| Key | Type | Default | Rule |
| --- | --- | --- | --- |
| `speakerHost` | REG_SZ | empty (Bonjour) | trimmed; empty → discovery |
| `minSpeakerVolume` | REG_DWORD (REG_SZ number accepted) | 15 (macOS 12) | > 0 → `min(v, max − 1)`; unset → `min(15, max − 1)` |
| `maxSpeakerVolume` | REG_DWORD | 65 | > 0 → `min(v, 100)` |
| `autoSwitchDefault` | REG_DWORD 0/1 | 1 | keep/restore LSX II as default output |
| `autoWakeOnPlayback` | REG_DWORD 0/1 | 1 | wake after ≥ 2 s of playback (§5.11) |
| `showVolumeInMenuBar` | REG_DWORD 0/1 | 1 | number in the tray icon |
| `restoreInputUIDPrefix` | REG_SZ | unset | Windows prefix format (§5.13) |
| `defaultOutputSetFor` | REG_SZ | set by the app | first-run marker |
| `pollInterval` | REG_SZ (invariant) or DWORD | 1.0 | hidden |
| `resetJumpThreshold` | REG_DWORD | 25 | hidden |
| `lastSelfRelaunch` | REG_SZ ISO 8601 UTC | set by the app | 600 s lock |

`settingKeys`, `userKeys` (+ `defaultOutputSetFor`) and `boolKeys` are identical to macOS, in order.
Bools are read only from REG_DWORD, as macOS ignores string bools (R13). State, not settings:
`HKLM\…\State\volume`, `HKLM\…\State\muteResetBoot`, `HKLM\…\installId`, `HKCU\…\restoreOutputOnReturn`.

### 8.3 Command line instead of `defaults write`

`reg add HKCU\Software\io.github.roberdorfer.lsxiivolumefixer /v maxSpeakerVolume /t REG_DWORD /d 60 /f`;
`reg query …`; `reg delete … /v defaultOutputSetFor /f`; system-wide via HKLM (admin) or
`scripts\install.cmd all --pref key=value`.

### 8.4 "Apply these settings for all users…"

As on macOS an admin script, held in memory and passed as `-EncodedCommand` (no file: one in the
user-writable `%TEMP%` could be swapped while the UAC prompt is open, see §10.3), with one
`Set-ItemProperty` per `settingKeys` entry (effective value; DWord for bools/numbers, String otherwise) or
`Remove-ItemProperty` for unset ones; the key is created only if missing (never `New-Item -Force`, which
would wipe the key including `State` and `installId`). Quotes, including typographic ones, are stripped.
Run once elevated (`runas`, hidden window, full path to `powershell.exe`); log `apply for all users: ok`
or `failed/cancelled <code>` (1223 = UAC cancelled). Generated by the pure function
`StatusMenu.AdminScriptForAllUsers` in `Config.cs`.

---

## 9. Logging

File `%LOCALAPPDATA%\LSXIIVolumeFixer\LSXIIVolumeFixer.log`, per user (A39). Launcher and agent both write
to it, so it is opened with `FILE_APPEND_DATA | SYNCHRONIZE` (no `FILE_WRITE_DATA`), one `WriteFile` per
line under a lock; fallback stderr; no rotation. Line format `<UTC timestamp yyyy-MM-ddTHH:mm:ss.fffZ>
<message>`, UTF-8 without BOM, LF. Numbers invariant, float32 round-trip, bools lower case, device IDs as
endpoint ID strings (`none` = no device), error codes `0` or HRESULT `0x%08X` (A40). All log texts are
English and match macOS except where §14 says otherwise.

Windows-only lines: `keyboard hook installed (<reason>)`, `keyboard hook install failed (<err>)`,
`IPolicyConfig unavailable …`, the two Plan B lines (§5.11), `push proxy -> speaker: skipped …` (A85),
`speaker discovery: watcher aborted, restarting`, `speaker discovery: watcher restart failed …` (A67),
`launcher: agent exited (<code>), relaunching in <s> s` (only for exit code ≠ 0),
`launcher: WM_CLOSE received: ending the agent and exiting`, `launcher: starting the agent failed …`,
`update installed: handing over to the new version`, `uninstalled: exiting`, `WM_CLOSE received: exiting`,
`WM_ENDSESSION received: exiting`. Driver lines taken over by v1: `volume set <%.4f>`,
`initialize volume=<%.3f>`. Foreign endpoint changes are visible only through the re-pin lines.

---

## 10. Install, update, uninstall

### 10.1 What goes where

| macOS | Windows v1 |
| --- | --- |
| `/Library/Audio/Plug-Ins/HAL/KEFProxy.driver` | – (target `driver` reserved) |
| `/Applications/LSXIIVolumeFixer.app` | `C:\Program Files\LSXIIVolumeFixer\LSXIIVolumeFixer.exe` |
| LaunchAgent plist (`RunAtLoad`, `KeepAlive`, `ThrottleInterval 5`) | value `io.github.roberdorfer.lsxiivolumefixer` under `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run`; `KeepAlive` by the launcher (§10.9) |
| `/Library/Preferences/…` | `HKLM\SOFTWARE\io.github.roberdorfer.lsxiivolumefixer` (+ `State`, `installId`) |
| `~/Library/Preferences/…`, `~/Library/Logs/…` | `HKCU\Software\io.github.roberdorfer.lsxiivolumefixer`, `%LOCALAPPDATA%\LSXIIVolumeFixer\` |

### 10.2 `scripts\install.ps1`

Grammar as `install.sh`: `install.ps1 [driver|helper|all] [--pref key=value ...] [--dry-run]`; unknown
argument → exit 2; `driver` → `driver: not part of this version`, exit 2. Started through
`install.cmd` (`-ExecutionPolicy Bypass` for this process only; `Set-ExecutionPolicy` is never called).

User part (not elevated): checks for 64-bit PowerShell and, without `--dry-run`, for not being elevated
(exit 1); `make.ps1 -s all`; staging folder `%TEMP%\kefproxy.XXXXXX` (removed in `finally`); `admin.ps1`
written UTF-8 **with BOM** (PowerShell 5.1 reads BOM-less files as ANSI); `--dry-run` prints the script and
exits 0; one elevation; then in the own session wait up to 5 s for the hand-over, else start the exe via
`explorer.exe` (detached from the terminal, up to 5 tries); verify a process from the fixed path started
after the install began, else `helper agent failed to load`, exit 1.

Admin part: `$ErrorActionPreference = 'Stop'`, everything in `try`, its log lines kept in memory and sent back through a pipe (§10.3). Rename a
running old exe to `.old` (renaming works while running, overwriting does not), copy the new exe, create
HKLM key and `State` only if missing, set the Users `SetValue` rule on `State` (by SID), write the Run value,
write a new `installId` (triggers the hand-over), wait up to 15 s for old processes, then `Stop-Process`,
delete `.old`; finally `--pref` values as REG_SZ (escaped with
`CodeGeneration.EscapeSingleQuotedStringContent`). The admin part writes only HKLM and Program Files,
never HKCU.

### 10.3 Elevation

`Start-Process powershell.exe -Verb RunAs -WindowStyle Hidden -PassThru …` then `WaitForExit()` (not
`-Wait`, which waits for the whole process tree). Values go through the file, never the command line.
The staging folder is in the user-writable `%TEMP%`, so the file could be swapped while the UAC prompt is
open. The elevated PowerShell therefore never runs `admin.ps1` by path: it gets a fixed bootstrap as
`-EncodedCommand` (no `-ExecutionPolicy Bypass`) that reads the file once, compares its SHA-256 with the hash
taken by the user part and runs exactly those bytes from memory; a mismatch exits 3 without running anything
(`admin script changed after it was written`). The install's admin script also checks the SHA-256 of the
exe after copying it to Program Files and removes it on a mismatch. The admin part writes nothing into the
staging folder either (a junction there could send an elevated write to any folder): it keeps its log lines in
memory and, in `finally`, creates a named pipe `kefproxy.<GUID>` itself, waits up to 10 s for the user part to
connect and writes the lines; the user part connects while the process runs and prints them. The elevated side
only writes, so a process that grabs the pipe first only gets status text and the log is missing. Cancel (1223) → `cancelled`, exit 1. The UAC dialog cannot show custom text (A35), so the text is printed
on the console.

### 10.4 Update of running instances

An admin script can only hard-kill processes in other sessions (leaving ghost tray icons). So instances
hand over themselves: every agent checks `HKLM\…\installId` each second (pure function
`Launcher.InstallIdChange`). New value → log `update installed: handing over to the new version`,
`ExitApp`; its launcher starts the fixed path with `--relaunched` and exits. Value gone → `uninstalled:
exiting`. A registry read error counts as unchanged. Instances that hang are killed after 15 s (A34).
Other signed-in users without a running instance get the app at their next sign-in (A63).

### 10.5 Prerequisites and building

.NET 10 SDK (`winget install Microsoft.DotNet.SDK.10`, LTS until 2028-11); `global.json` pins
`10.0.100` with `rollForward: latestFeature`. `make.cmd` / `make.ps1` targets: `all`, `helper`, `test`,
`test-install`, `test-uninstall`, `clean`, `driver` (reserved). Output is a framework-dependent single
file `build\LSXIIVolumeFixer.exe`. The first restore downloads the WinRT projection
`Microsoft.Windows.SDK.NET.Ref` from nuget.org (pinned to `10.0.22000.57`). The build sends nothing
else (A72): telemetry, first-run certificate, workload checks and NuGet audit are switched off for the
build process; no runtime-pack download, no single-file analyzer. Scripts follow Windows PowerShell 5.1
rules: no `&&`/`||`, no ternary, no `??`, files that need it saved with a BOM.

### 10.6 MAX_PATH

All build output stays under `build\`, intermediate files without TFM and RID folders
(`AppendTargetFrameworkToOutputPath` / `AppendRuntimeIdentifierToOutputPath` false), so even a deep
checkout stays below 260 characters (A38). PowerShell 5.1 and Explorer fail beyond that when long paths
are disabled (the Windows default).

### 10.7 `scripts\uninstall.ps1`

`uninstall.ps1 [--purge] [--dry-run]`; 64-bit check; no build. Admin part: remove the Run value and its
`StartupApproved\Run` entry, delete `State` non-recursively, then the HKLM key with `installId` (all
instances see this and exit cleanly), wait at most 10 s and kill the rest, delete the Program Files
folder. User part: `--purge` deletes the HKCU key and log folder; otherwise only `defaultOutputSetFor`.
Double-clicked starters keep the window open at the end (never with `--dry-run`, A83).

### 10.8 What stays after uninstall

Other users' settings and logs; the device pins (endpoint 100 %, format 96 kHz / 24-bit, default output);
from then on the Windows keys control the USB volume in 6 dB steps again, the speaker value is unchanged,
so nothing gets suddenly louder. Also the per-user tray visibility setting and the .NET SDK / build folder.

### 10.9 Autostart and `KeepAlive`: the launcher

HKLM `Run` starts the exe at every sign-in (Explorer shell only), never elevated (`asInvoker`). Launcher
mode: take the mutex (`--relaunched` waits up to 10 s); create a job object with
`KILL_ON_JOB_CLOSE | SILENT_BREAKAWAY_OK` (the agent ends with the launcher, its own child processes such
as the editor or the elevated admin script do not); start `--agent`; after **any** exit restart at the
earliest 5 s after the last start (pure function `Launcher.ThrottleDelay`). Stop conditions: exit 1115
(sign-out), `installId` or the own exe missing (uninstalled), `installId` changed (update).

Both processes have a hidden top-level window titled `LSX II Volume Fixer`. Agent: `WM_CLOSE` → `ExitApp(0)`,
`WM_ENDSESSION` → `ExitApp(1115)` (A80). Launcher: `WM_CLOSE` → forwards `WM_CLOSE` to the agent every
second until it ends, then exits without restart (A82). `ExitApp` hides and disposes the `NotifyIcon`
before `Environment.Exit` so no ghost icon remains. Ending the launcher with Task Manager kills the
agent through the job and nothing restarts until the next sign-in (A31, A60). Minimum version: build
< 22000 exits with 1150 (A71). Rejected: Task Scheduler, a service with `CreateProcessAsUser`,
`RegisterApplicationRestart`, MSIX/MSI per user.

---

## 11. Tests

### 11.1 `Tests\ConfigTests.cs`

Console program without a test framework (no NuGet downloads); `ConfigTests.csproj` links
`Helper\Config.cs` and `Helper\SpeakerDiscovery.cs`, like the macOS Makefile. Output `FAIL <what>` and
finally `ConfigTests: all passed` or `ConfigTests: <n> failure(s)` with exit 1. Tests never touch the
registry.

- **Ported from macOS (same names, called with 16 steps):** `test_decision_keyStepsAreEqualInMeasuredDB`,
  `test_keyStepTable_endsAndMonotonic`, `test_keyStepTable_12to65_matchesDocumentedTable`,
  `test_approachFromAbove_landsOnFromAboveCurve`, `test_dB_curveShape`,
  `test_decision_speakerDiscoveryMatchRule`, `test_decision_identifiersUseGitHubReverseDNS`,
  `test_decision_outputFixedAt96kHz24bit`.
- **Additions:** golden tables and mappings (`test_golden_*`), `test_golden_keyStepTable50`,
  `test_keyStepTable50_strictlyIncreasing` (all ranges), `test_edge_rangeClampNeverDegenerates`,
  `test_windowsIdentifiers`, `test_isConsoleDecision`, `test_launcherThrottle`,
  `test_applyForAllUsersScript`, `test_installIdChange`, `test_defaultOutputPlanB`,
  `test_autoWakeReselect`, `test_reappearPush`, `test_uidPrefix_windows`, `test_settingsParsing`,
  `test_volumeKeys50`.

### 11.2 Script tests

`Tests\install_dry_run.ps1`: runs `install.ps1 helper --dry-run --pref "speakerHost=<value>"` with a value
containing `"`, `\`, `'`, `$x`, a backtick, `’` and `ä`; parses the generated `Set-ItemProperty` line with
the PowerShell parser and requires the exact value (this also proves the BOM); checks the copy line, the
`SetValue`-only rule, no `New-Item -Force` on a registry path, Run value and `installId`, a removed staging
folder, and exit 2 for `driver` and unknown arguments. `Tests\uninstall_dry_run.ps1`: `--dry-run --purge`
must remove Run and StartupApproved values, Program Files folder, HKLM key, instances, HKCU key and log
folder; without `--purge` only `defaultOutputSetFor`.

### 11.3 Device tests

Manual, per milestone (§16), with the checks in §15. Steps that change the system or the speaker were run
only deliberately, never as a side effect of a read-only check.

---

## 12. Project structure

```
windows\
├─ README.md, LICENSE, global.json, Directory.Build.props, make.ps1, make.cmd
├─ Helper\     LSXIIVolumeFixer.csproj (WinExe, net10.0-windows10.0.22000.0, UseWindowsForms), app.manifest,
│              main.cs, Launcher.cs, Config.cs, Controller.cs, CoreAudioSupport.cs, VolumeSurface.cs,
│              KeyHook.cs, VolumeOsd.cs, Speaker.cs, SpeakerDiscovery.cs, StatusMenu.cs
├─ Driver\     README.md (reserved for v2)
├─ scripts\    install.ps1/.cmd, uninstall.ps1/.cmd, dev-setup.ps1/.cmd (development only)
├─ Tests\      ConfigTests.cs/.csproj, install_dry_run.ps1, uninstall_dry_run.ps1
├─ docs\       DESIGN.md, usb-volume-curve.md, img\
└─ build\      output (not in the repository)
```

The measurement tools used during development (discovery, name resolution, menu rendering, key and
lifecycle probes) are not part of the published tree.

csproj essentials: `AssemblyName` `LSXIIVolumeFixer`, version 1.0, `OutputType` WinExe (≙
`LSUIElement`), `SupportedOSPlatformVersion` 10.0.22000.0, PerMonitorV2 via `ApplicationHighDpiMode`,
`AllowUnsafeBlocks`. `app.manifest`: only `asInvoker` and the Windows 10/11 supportedOS GUID.

---

## 13. Later driver option (v2)

### 13.1 The seam

```csharp
public interface IVolumeSurface : IDisposable {
    string Kind { get; }                 // "keyhook" (v1) | "virtual-endpoint" (v2)
    bool IsPresent { get; }
    float? Scalar { get; }               // 0..1, 50 steps
    bool Muted { get; }
    int SetFromSpeaker(float scalar);    // applyToProxy; no user event; 0 or error
    bool IsRunningSomewhere { get; }     // auto-wake
    string? OutputEndpointId { get; }    // target of autoSwitchDefault / first run
    string TargetName { get; }           // "LSX II" (v1) | "proxy" (v2)
    event Action UserScalarChanged, UserMuteChanged, RunningChanged, PresenceChanged;
}
public interface IRealOutput {
    string? EndpointId { get; }          // DeviceUid contains "VID_27AC&PID_1060"
    void PinVolume();                    // channels + master -> 1.0, mute untouched
    void PinFormat();                    // v1: IPolicyConfig; v2: bridge opens exclusively
    string? FormatText { get; }
    event Action Changed;
}
```

`Controller` and `Config` stay unchanged in v2; branch (a) of `checkDefaultOutput` becomes active once
`OutputEndpointId` differs from the real endpoint.

### 13.2 What v2 changes

`VirtualEndpointVolumeSurface` replaces the hook; the Windows flyout becomes the HUD; reserved names
become active (device "KEF LSX II", UID `io.github.roberdorfer.kefproxy.device`, `kcfg`/`kstt`/`ktrc`,
`kefstats`, install target `driver`); mute writes zeros again; the driver stores the volume
(`HKLM\…\State` goes away); audio stays on the always-present proxy across USB re-enumeration.
**Loss compared to v1:** while the bridge holds the LSX II exclusively, exclusive-mode players only reach
the virtual endpoint, and Spotify shows its exclusive switch only for external devices. For lossless
playback v1 is therefore the better design; v2 buys comfort at the cost of quality.

### 13.3 Driver sketch

PortCls WaveRT miniport based on `simpleaudiosample` (or ACX), one 2-channel 96 kHz float32 format with
`KSNODETYPE_VOLUME` (−60…0 dB) and `KSNODETYPE_MUTE`; a user-mode bridge opens the LSX II WASAPI-exclusive,
event-driven, 24/24 packed, on an MMCSS "Pro Audio" thread, reads the driver's cyclic buffer from shared
memory and fits a clock model from `IAudioClock::GetPosition` (no SRC, no drift). Lessons from the macOS
driver apply (ring buffer 32768 frames, underrun plays silence, latency values fixed at publication,
2000 µs jitter reserve). Signing needs attestation with an EV certificate, or test signing for own use;
HVCI compatibility required. A driver bug is a blue screen, so the disclaimer gets stricter for v2.

---

## 14. Full deviation list

Kind: **W** = Windows forces it; **v1** = follows from "no driver"; **U** = deliberate design choice
or a direct consequence; **Z** = addition; **B** = bugfix compared to macOS; **opt** = only on request.
Numbers are stable; A17, A30 and A62 were dropped (now 1:1 as macOS); A77 is now the same as macOS and
kept for history.

| # | Area | macOS | Windows v1 | Kind | Reason |
| --- | --- | --- | --- | --- | --- |
| A1 | Keys | act on the proxy device volume | low-level hook, key swallowed | v1 | without a driver no device with fine volume |
| A2 | Display | macOS HUD | own OSD in Windows 11 style | v1 | the Windows flyout shows the pinned endpoint |
| A3 | Windows volume UI | – | always 100 % for the LSX II | v1 | endpoint must stay at 0 dB |
| A4 | Foreign endpoint changes | never taken over; the 10 s pin resets | never taken over; re-pin after 300 ms of quiet via callback; mute is the same mute | Z | in v1 Windows UI, apps and missed keys write to the real endpoint |
| A5 | Keys over elevated windows, secure desktop, lock screen | work | not captured; Windows moves the endpoint, A4 pins back | W | UIPI and desktop boundaries; `uiAccess` would need a root certificate |
| A6 | Display on the lock screen | HUD appears | no OSD | W | private window bands |
| A7 | Fine steps | Option+Shift = 1/64 | none; modified volume keys count as normal steps (mask key `vkE8` with Alt/Win) | U | with 50 steps a quarter step is below the speaker's integer resolution |
| A8 | USB re-enumeration | apps keep playing to the proxy | endpoint disappears, Windows moves audio elsewhere until the LSX II returns | v1 | no always-present device |
| A9 | Mute | driver writes zeros | USB hardware mute | v1 | no driver in the sample path |
| A10 | Mute across restarts | off after driver start | Windows stores endpoint mute; the console instance lifts it once per boot when the LSX II appears (120 s tolerance); survives Fast Startup | v1/W | emulates the driver initialisation |
| A11 | Limiter | none | shared-mode `CAudioLimiter` cannot be disabled; exclusive players bypass it | W | shared-mode engine |
| A12 | Resampling | macOS SRC to 96 kHz | Windows engine SRC to 96 kHz in shared mode; exclusive players unconverted | W | engine not replaceable |
| A13 | Format pin | documented CoreAudio property + driver | undocumented `IPolicyConfig::SetDeviceFormat` | W | no public API |
| A14 | Format choice | only 96 kHz offered | Control Panel lists all; the app sets 96 kHz / 24-bit back | W/v1 | no own device |
| A15 | Exclusive mode | (any format change reset) | allowed; exclusive players play bit-perfect at source rate; other apps silent meanwhile | U | lossless playback via exclusive-mode players (R3) |
| A16 | Bit-perfect conditions | – | README/FAQ only (enhancements, spatial sound, ducking, limiter); no check | W | settings that exist only on Windows |
| A18 | Default output | one | three roles, one state machine per role; first run sets all three | W | Windows role model |
| A19 | Setting the default | documented property | undocumented `IPolicyConfig::SetDefaultEndpoint` | W | no public API |
| A20 | "Default was real device → proxy" | active | inactive (code kept) | v1 | target is the real device |
| A21 | Wording | "proxy" in logs and menu | "LSX II", role suffix; `proxy device: keyhook` | v1/W | no proxy device; roles |
| A22 | Auto-wake | `DeviceIsRunningSomewhere` of the proxy | active session on the LSX II endpoint; without it Plan B (A84) | v1 | no proxy |
| A23 | Device detection | ModelUID contains `27AC:1060` (case-sensitive) | `DeviceUid` contains `VID_27AC&PID_1060`, case-insensitive; active endpoints only | W | ID format; Windows IDs are case-insensitive; disabled devices exist |
| A24 | `defaultOutputSetFor` | proxy UID | `DeviceUid` without instance segment (`USB\VID_27AC&PID_1060&MI_01`), v2 proxy UID | W/v1 | port-independent like the proxy UID |
| A25 | `restoreInputUIDPrefix` | `AppleUSBAudioEngine:<V>:<M>` | `USB\VID_…&PID_…&MI_…` | W | ID format |
| A26 | Proxy volume storage | driver host storage (machine-wide) | `HKLM\…\State\volume`; users may only set values; re-read before the console push | v1 | no driver; another session may have changed it |
| A27 | Settings | UserDefaults, `defaults write` | registry HKCU over HKLM, `reg add` | W | platform |
| A28 | Bool settings as strings | ignored | same (REG_DWORD only) | opt | 1:1 |
| A29 | Unset minimum with max ≤ default minimum | crash | `min(15, max − 1)` | B | EDGE-01 |
| A31 | Restart after exit | launchd `KeepAlive`, throttle 5 s | launcher + job object, same 5 s; killing the launcher hard also kills the agent and nothing restarts until next sign-in | W | no launchd; a watchdog would need a third process |
| A32 | Self-relaunch log text | "launchd relaunches" | "the launcher relaunches" | W | mechanism |
| A33 | Autostart | LaunchAgent, immediate | HKLM `Run`, possibly delayed, not in safe mode | W | platform |
| A34 | Updating running instances | bootout/bootstrap per session | hand-over via `installId`, leftovers killed after 15 s; running old exe renamed to `.old` and deleted | W | other sessions can only be hard-killed; running exe cannot be overwritten |
| A35 | Admin prompt | one prompt with custom text | one UAC dialog without custom text; text on the console | W | UAC |
| A36 | Signing | ad hoc `codesign` | none | W | built locally, no Mark-of-the-Web, self-signing useless |
| A37 | Toolchain | Xcode CLT, swiftc, clang, Makefile | .NET 10 SDK, `dotnet publish`, `make.ps1`; first restore needs nuget.org | W | C# / .NET |
| A38 | Build folder | `build/` | results and intermediates under `build\`, no TFM/RID folders | W | MAX_PATH in PowerShell 5.1 and Explorer |
| A39 | Log path | `~/Library/Logs/LSXIIVolumeFixer.log` | `%LOCALAPPDATA%\LSXIIVolumeFixer\LSXIIVolumeFixer.log` | W | platform |
| A40 | Codes and IDs in the log | OSStatus, AudioObjectID | HRESULT, endpoint ID string, `none` for "no device" | W | platform |
| A41 | Discovery | NetServiceBrowser with resolve | `DeviceWatcher` without resolve; address changes via `Updated` | W | API |
| A42 | 30 s hint | names `defaults write` | names `reg add` | W | platform |
| A43 | HTTP | URLSession: Happy Eyeballs, `waitsForConnectivity`, system proxy with `*.local` exception | `ConnectCallback` with candidates (last good, DNS-SD, resolver), staggered 250 ms; re-resolve within 3 s; `UseProxy = false` | W | .NET has no Happy Eyeballs; `getaddrinfo` fails in bursts; `<local>` excludes `.local` names |
| A44 | Network permission | macOS asks for "local network" | no prompt | W | does not exist on Windows |
| A45 | Volume number | text next to the icon | number instead of the icon | W | tray icons have no text |
| A46 | Icon colour | template image | colour by taskbar theme | W | no template images |
| A47 | Click and position | menu below the menu bar | left and right click open the same menu, above/left of the cursor | W | notification area |
| A48 | Info lines | disabled NSMenuItems | disabled items, renderer without hover highlight | W | ToolStrip highlights disabled items |
| A49 | Menu and console texts | "Mac 0–100 %", "menu bar", "(proxy)" | "PC 0–100 %", "taskbar", "LSX II (USB)"; install line "(tray)" | W/v1 | device, no menu bar, no proxy |
| A50 | Visibility | always in the menu bar | new icons in the overflow, the user pins them | W | Windows rule |
| A51 | Latency | about 4–5 ms | 0 ms | v1 | no intermediate device (improvement) |
| A52 | Driver diagnostics | driver logs, `kcfg`, `kstt`, `ktrc`, `kefstats` | dropped, reserved | v1 | no driver |
| A53 | `install … driver` | installs the driver | exit 2 "not part of this version" | v1 | no driver |
| A54 | Uninstall | coreaudiod restart; local network note | no restart; tray visibility note; StartupApproved removed; `State` deleted non-recursively | W/v1 | platform |
| A55 | Interactive slider | menu bar / Control Center | none; the Windows slider is pinned back | v1 (opt) | the Windows slider is the USB endpoint in v1 |
| A56 | One instance per session | launchd job per `gui/<uid>` | mutex `Local\…` | W | platform |
| A57 | Console state while switching | – | `0xFFFFFFFF` keeps the last value | W | API behaviour |
| A58 | USB format line | physical stream format | shared-mode device format | W | API |
| A59 | Tests | swiftc, sh | C# console project, PowerShell dry runs, additional tests | W/Z | language, Windows pitfalls |
| A60 | Processes | one per session | two (launcher + agent), same name in Task Manager; ending the launcher ends both until next sign-in | W | `KeepAlive` replacement |
| A61 | Start from the installer | `launchctl bootstrap` | start via `explorer.exe`, detached from the terminal | W | job objects of some terminals |
| A63 | Other signed-in users at first install or without a running instance | `bootstrap` starts the agent in every session | app starts at their next sign-in | W | starting in other sessions needs LocalSystem |
| A64 | Install checks | none | 64-bit PowerShell always; "not elevated" without `--dry-run`; exit 1 | W | WOW64 redirection; the app must not start elevated |
| A65 | Own instance during install | stopped before the prompt | handed over in the admin part; started afterwards by the user part; cancelling keeps the old one running | W | same hand-over as other sessions (A34) |
| A66 | Hook renewal | – | on unlock, console switch, resume and every 60 s | Z | Windows removes LL hooks silently |
| A67 | Discovery abort | – | restart `DeviceWatcher` on `Aborted`, then 10 s doubling to 600 s; host kept, previous speaker re-taken silently | Z | WinRT watchers can abort; DISC-06; no tight restart loop |
| A68 | Log | one writer; driver lines in the unified log | launcher and agent append; `volume set`, `initialize volume=` in the app log | W/v1 | launcher; no driver channel |
| A69 | Names and files | Swift names | capitalised methods/properties; new files `Launcher.cs`, `KeyHook.cs`, `VolumeOsd.cs`, `VolumeSurface.cs` | W/v1 | C# convention |
| A70 | Additional time constants | – | 300 ms re-pin quiet; 250 ms stagger/re-resolve; 60 s hook renewal; 300 ms click lock; 2 s OSD; 15 s update wait; 5 s hand-over wait; 10 s uninstall and `--relaunched` mutex wait; 1 s `WM_CLOSE` repeat; 120 s boot-time tolerance; 10–600 s watcher restart; 10 s return window and 60 s pause for re-selecting `usb` | Z | Windows mechanisms without macOS counterpart |
| A71 | Minimum version | `LSMinimumSystemVersion` 14.0, system dialog | start check build ≥ 22000 in launcher and agent, exit without dialog | W | Windows does not enforce a minimum version |
| A72 | Build environment | swiftc sends nothing | `make.ps1` disables .NET telemetry, first-run certificate, workload notifications; `Directory.Build.props` disables runtime-pack download, single-file analyzer, NuGet audit, pins the projection | Z | the build should send and install nothing beyond what it needs |
| A73 | Key steps | 16, scalar 1/16 | 50, scalar 1/50; mapping as macOS with 49 instead of 15 intervals | U | same principle: as many steps as the OS keys |
| A74 | Default `minSpeakerVolume` | 12 | 15 | U | chosen range |
| A75 | Step-table rounding | nearest by dB, never below the previous step | nearest strictly above the previous step, at most `hi − (steps − 1 − i)`; narrow ranges only `prev`/`prev + 1`; only the per-step bound is guaranteed with 50 steps | U | consequence of A73: the macOS rule leaves dead key presses |
| A76 | Step list in the menu | all 16 values | runs collapsed (`15–63 65`); otherwise ends and count | U | display only |
| A77 | Format pin (now same) | fixed 96 kHz / 24-bit | same | – | history: 48 kHz chosen first, changed to 96 kHz like macOS (R4) |
| A78 | HTTP headers | URLSession defaults incl. User-Agent, Accept-Language | `Accept: */*`, `Accept-Encoding: gzip, deflate, br`; no User-Agent, no Accept-Language | W | .NET sets no default headers |
| A79 | RangeView | 300×120 from the menu edge, fixed 58 pt step label; `NSSlider` | in the text column after the check column; step label grows to four measured lines where needed; track click like `NSSlider`; arrow, page keys and wheel | W/Z | ToolStrip layout; Segoe UI metrics; keyboard access |
| A80 | Orderly end from outside | `SIGTERM` or sign-out end the process | `WM_CLOSE`/`WM_ENDSESSION` to a hidden top-level window + message filter → `ExitApp` (exit 0 / 1115) | Z | without `NIM_DELETE` a ghost icon would remain |
| A81 | Tray number after a speaker change | 2 s timer, up to ≈ 3 s delay | redrawn immediately on new poll value, successful send or reappear check | Z | the delay felt too long |
| A82 | Orderly end of the launcher | launchd job without own process | hidden window: `WM_CLOSE` → forward to the agent each second until it ends, then exit without restart; `WM_ENDSESSION` → no restart | Z | `taskkill` without /F needs a window; /F would kill the agent via the job |
| A83 | Install script windows | terminal stays open | double-clicked `.cmd` waits for a key at the end (not with `--dry-run`); `uninstall.ps1` checks 64-bit too; agent logs `uninstalled: exiting` | Z | a double-clicked console would close with the result |
| A84 | Auto-wake without USB device (Plan B) | proxy always default and "running"; wake only from `standby`; restore flag only in memory | while the LSX II is gone and its return pending, sessions on the substitute output count; wake from `standby` and re-select `usb` (once per loss episode, then at most every 70 s); pending return per role stored in HKCU `restoreOutputOnReturn`; a deliberate choice drops it permanently | v1/W | the LSX II leaves USB when idle but keeps reporting `usb` |
| A85 | Push on reappear | always, with overshoot | GET first; skip if the speaker already has the PC value | W | the LSX II reappears after every idle phase; the overshoot would be audible each time |

---

## 15. Open points and device checks

### 15.1 To verify on a Windows PC

"Read-only" = only read and log; "consent" = changes state, only run deliberately on a test system.

| # | Question | Status / test | Kind |
| --- | --- | --- | --- |
| O1 | Does `IPolicyConfig::SetDefaultEndpoint` work without elevation and behave like a UI choice? | **confirmed** on build 26200 (all three roles set, not elevated) | consent |
| O2 | Does `SetDeviceFormat` set 96 kHz / 24-bit shared without elevation? | **confirmed** (48 → 96 kHz, log `(0)`) | consent |
| O4 | Does `OnPropertyValueChanged` fire for `DeviceFormat` when the format is changed in `mmsys.cpl`? | open: change the format in the Control Panel and watch the log | consent |
| O5 | Does the USB endpoint stay while the speaker is in standby? Do apps follow a re-enumeration? | **answered:** the LSX II leaves USB after a few idle minutes but keeps reporting `usb` → Plan B (A84). App behaviour after re-enumeration (browser, Spotify, a game) still open | read-only |
| O6 | Does the speaker reset its volume on a Windows re-enumeration? | open: poll log while unplugging/replugging | read-only |
| O7 | Do exclusive and loopback streams appear as active sessions on the endpoint? | open (false wake by OBS/Discord loopback; no wake for exclusive players) | read-only |
| O8 | Does the KEF remote trigger anything on the PC (HID MI_00, keys, endpoint)? | open | read-only |
| O9 | Does the USB hardware mute have side effects in the speaker (own mute, standby timer, clicks)? | open | consent |
| O11 | Which role do the Windows volume keys follow (eConsole or eMultimedia)? | low priority | consent |
| O12 | How often does Windows drop the hook; do vendor tools swallow volume keys first? | open: hook log over a working day | read-only |
| O14 | Which `guidEventContext` do Quick Settings, Settings, wheel and the Windows key path use? Does own `SetMute` show the native flyout? | open; rule A re-pin itself confirmed (≈ 310 ms) | read-only / consent |
| O15 | Geometry and duration of the native flyout; where 25H2 stores "volume popup position" | open (OSD uses 2 s) | read-only |
| O16 | Does `DeviceWatcher.Removed` arrive when the speaker leaves the network? | open (only synthetic scenarios tested) | read-only |
| O17 | Frequency/length of `.local` resolution failures under .NET 10; address order; connect times; does the callback keep the 3 s budget? | **done:** bursts of ≈ 10 s; with DNS-SD candidates 100 % success; callback within budget | read-only |
| O18 | Does the .NET 10 SDK bring the desktop runtime; does the build cope with long paths? | **done** | – |
| O19 | Is the tray icon keyboard-reachable (Win+B, Enter, Shift+F10)? | checked during M2 | read-only |
| O20 | Does EcoQoS stretch the 30 ms / 100 ms timers? | **done:** only timer resolution (≈ 15.6 ms) | read-only |
| O21 | Does DWM round the dropdown; does the menu follow theme changes live? | look checked in M2; live theme and DPI changes still open | read-only |
| O22 | Does a pinned tray icon survive an update (same path)? | open: pin, then install again | consent |
| O23 | Does the app started by the installer via `explorer.exe` survive closing the terminal? | open | consent |
| O24 | Does Windows dither float → int24; where is the limiter threshold? | not testable without capturing the USB stream | – |
| O25 | Plan B on the device: does re-selecting `usb` bring the LSX II back to USB, and within 10 s? False triggers (system sounds ≥ 2 s, VoIP, loopback)? Does a deliberate choice survive a restart? Does the reappear push stay silent (A85)? | open | read-only / consent |
| O26 | What do the Windows keys do to the LSX II today (dB per press over 50 presses)? | open; needed for the "before" figure | consent |
| O27 | Is the brief overshoot audible with 1 dB steps (up to ≈ 1.4 dB above target below 41)? | open: listening test 15–41 | consent |
| O28 | Can the app read cable mode or the USB input rate over the HTTP API? | open (check pykefcontrol / aiokef paths) | read-only |
| O29 | Exclusive players on the device: does Spotify show the switch for the LSX II; do Qobuz, TIDAL, foobar2000 deliver the source rate at 16/24-bit; do they switch per track? | open | consent |
| O30 | Does an agent started from a sandboxed (packaged) app shell see the same registry and files as the installed app? | **answered: no.** Packaged-app shells virtualise HKCU and `%LOCALAPPDATA%`; HKLM is real. Check from a process started via `explorer.exe`; the README's agent install prompt accounts for this | read-only |

O3, O10 and O13 were dropped (exclusive mode stays allowed, no loudness protection, no fine steps).

### 15.2 To verify on a Mac (macOS behaviour not visible in the source)

| # | Question |
| --- | --- |
| O-M1 | Does a normal key press snap to the 1/16 grid when the proxy scalar lies between steps? (Windows: no snapping.) |
| O-M2 | Does up/down while muted unmute, and also move one step? Does reaching 0 mute the proxy? |
| O-M3 | Does `install.sh --pref autoSwitchDefault=false` (string, not bool) have any effect? |
| O-M4 | Does the 2 s status-item timer fire while the menu is open? |

### 15.3 Decisions

| # | Question | Decision |
| --- | --- | --- |
| R1 | Keep the word "Bonjour" although Windows uses mDNS/DNS-SD? | keep |
| R2 | Call the virtual volume "proxy" in logs? | yes (correct again in v2) |
| R3 | Disable exclusive mode (macOS "fixed format" principle)? | no, stays allowed for bit-perfect playback |
| R4 | Shared format 96 kHz like the Mac, or 48 kHz? | 96 kHz / 24-bit |
| R5 | First pin like macOS or with loudness protection? | like macOS |
| R6 | Keep the macOS quirks CTRL-38 and CTRL-44? | keep (1:1) |
| R7 | Optional `uiAccess` install (keys over admin windows)? | not in v1 |
| R8 | Draggable OSD bar as a slider replacement? | no |
| R9 | .NET 10 SDK via `winget`? | yes, installed by the user |
| R10 | Machine-wide volume store with users allowed to set values only? | yes |
| R11 | Foreign endpoint changes: re-pin (rule A) or take over quieter ones (rule D)? | rule A |
| R12 | Lift the endpoint mute once per boot like the macOS driver? | yes (not after Fast Startup) |
| R13 | Accept bools from REG_SZ? Fallback for EDGE-01? | bools 1:1 ignored; fallback `min(15, max − 1)` |

---

## 16. Milestones

Each milestone ended with something checkable; write steps on the system or the speaker only with
consent.

| M | Content | Status |
| --- | --- | --- |
| M0 | Skeleton and pure logic: `global.json`, `Directory.Build.props`, `make.ps1`, `Config.cs`, `ConfigTests` | done |
| M1 | Network, settings, log (read-only on the speaker): `Speaker.cs`, `SpeakerDiscovery.cs` | done |
| M2 | Core Audio read-only + tray: `CoreAudioSupport.cs`, read side of `VolumeSurface.cs`, `StatusMenu.cs`, `ExitApp` | done |
| M3 | Pins and default output (with consent); first a development setup (`scripts\dev-setup.cmd`, elevated once) that creates exactly the HKLM keys, `State` ACL and `installId` the installer writes, so the agent can run from `build\` | done, verified on the device |
| M4 | Keys and sending: `KeyHook.cs`, write side of the surface, `State\volume`, send path, wake item, `VolumeOsd.cs` | done, accepted |
| M5 | Lifecycle and multi-user: `Launcher.cs`, console gating, self-relaunch, auto-wake, apply for all users, restart | done |
| M6 | Installation: `install.ps1`, `uninstall.ps1`, starters, dry-run tests | done; O22/O23 open |
| M7 | Documentation: Windows README, `docs\usb-volume-curve.md`, `Driver\README.md` | done except the Windows figures (`make_figures.py`, needs O26) |

Not yet tested on the device: listening test O27, Plan B O25, the reappear push (A85), O22/O23.
