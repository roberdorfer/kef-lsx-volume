# LSX II Volume Fixer for Windows

Fine-grained volume for a **KEF LSX II connected to a Windows PC over USB**. Out of the box, the
Windows volume keys move in 2 % steps, but on this speaker those steps fall on only 8 hardware
levels, 6 dB apart: roughly six key presses for each audible step, and most presses do nothing.
With this project every key press gives a real step: 50 levels, about 1 dB apart, with an on-screen
volume display. The audio stays on the USB cable with no added latency. It reaches the speaker's USB
input bit-perfect when a player uses exclusive mode, and very likely for 96 kHz sources under the
conditions in the [FAQ](#faq); after that, the LSX II's own DSP always processes the signal.


- [The problem](#the-problem)
- [What this project does](#what-this-project-does)
- [How the volume keys are mapped](#how-the-volume-keys-are-mapped)
- [Fixing the speaker's volume curve](#fixing-the-speakers-volume-curve)
- [Other things the app takes care of](#other-things-the-app-takes-care-of)
- [Requirements](#requirements) · [Install](#install) · [Settings](#settings) · [Uninstall](#uninstall)
- [FAQ](#faq) · [macOS and other speakers](#macos-and-other-speakers) · [Technical details](#technical-details)

## The problem

Connect an LSX II to a PC over USB and press the volume keys: the volume jumps in large steps, and
most key presses change nothing at all. The speaker's USB audio interface only offers 6 dB steps
(8 positions between −42 and 0 dB). Windows spreads its 51 key positions (0 to 100 % in 2 % steps)
over those, so roughly six presses in a row change nothing and then one jumps by 6 dB. At a desk,
the level you want often lies between two of them.

The limit sits in the speaker firmware, not in Windows. KEF's firmware updates are signed, so it
cannot be patched. KEF Connect's volume settings (limit, step size) do not change the USB behaviour
either.

## What this project does

The speaker has a second, much finer volume: its own volume value from 0 to 100, which the speaker
remote and the KEF Connect app use. It can be set over the local network. This project puts the
PC's volume keys on that control and leaves the audio on USB:


- There is no driver: Windows plays straight to the LSX II's own USB output. The app holds that
  output's Windows volume at 100 % (0 dB), so the speaker's USB volume stays at 0 dB and Windows
  never scales the signal.
- A tray app, **LSX II Volume Fixer**, catches the volume keys before Windows does, sends the volume
  to the speaker over the network and shows its own on-screen display. The tray icon shows the
  current speaker value; its menu shows the dB value, the output, the USB format and the connection.
- Because the USB volume is held at 100 %, Windows' own volume slider always shows 100 % for the
  LSX II (see the [FAQ](#faq)).
- Normal (shared-mode) output runs at 96 kHz / 24-bit, the rate KEF names for the cable link between
  the two speakers. 96 kHz sources pass without conversion; Windows converts other rates once.
  Players in exclusive mode keep their source rate and play bit-perfect to the USB input
  (conditions in the [FAQ](#faq)).
- Changes made with the speaker remote or KEF Connect show up on the PC, too.
- There is no added latency.

## How the volume keys are mapped

### Speaker value and loudness are not the same thing

The speaker's own volume is a number from 0 to 100, the one the remote and KEF Connect show. That
number is only a position on a scale. How much louder one more unit makes the speaker depends on
where on the scale you are.

Loudness differences are measured in decibels (dB). 3 dB is a small but clearly audible change;
10 dB sounds about twice as loud. A step of 3 dB sounds the same size whether you are at a quiet or
a loud level. A step of 3 speaker units does not: on the LSX II it is 3 dB in the middle of the
scale but only 1.5 dB near the top.

This project therefore places the 50 key steps in dB, not in speaker units. To do that it has to
know how many dB each speaker value really gives, and on this speaker that turned out to be the
hard part. It has [its own chapter](#fixing-the-speakers-volume-curve) below; the mapping itself
comes first.

### What the keys do, before and after

**Without this project:** The PC's volume keys never touch the speaker's 0–100 volume. They change
the volume of the speaker's USB input, which KEF's firmware only offers in 6 dB steps. You get at
most 8 levels, and most of the 50 key presses change nothing ([The problem](#the-problem)).

**With this project:** The keys set the speaker's 0–100 volume, and the app decides where each of
the 50 key steps lands. It spaces them so that every key press changes the loudness by the same
number of dB, based on how loud each value really is. With the default range that is 0.97 dB per
key (the menu shows 1.0 dB); the individual steps are 1 dB, and 0.5 dB for three keys above 60,
because they are rounded to whole speaker values.

For comparison: the first macOS version of this app spread its keys evenly over the speaker values
12–100. The lower keys then jumped about 6 dB each while the upper ones changed only 3 dB.

### Minimum and maximum

You set the range in the tray menu with two speaker values. The **minimum** is what the first key
press (key 1) gives. The **maximum** is what the last one (key 50) gives. The app's defaults are 15
and 65. PC volume 0 sets the speaker to 0 and mutes it.


The app then works out the step size:

> step = (loudness at maximum − loudness at minimum) ÷ 49

With the defaults, 15 is 47.5 dB quieter than 65. That gives 47.5 dB ÷ 49 = 0.97 dB per key, about
1 dB. Rounded to whole speaker values, the keys land on every value from 15 to 63, then 65 (the menu
writes this as `15–63 65`). Move either end and the app recalculates all 50 steps. The menu shows
the new values and the step size. A narrower range gives smaller steps.

As long as maximum − minimum is at least 49, the rounding never leaves a key without effect. With a
narrower range there are fewer speaker values than keys, so some values necessarily repeat, and
that many key presses change nothing.

Why 65 and not 100: the speaker gets very loud above that, so a higher maximum spends most key
steps on levels you never use at a desk. Raise it in the menu if you need more.

### Values outside the range

There are no finer steps: with 50 keys of about one speaker unit each, a smaller step would fall
below the speaker's resolution (whole values). Volume keys pressed with Alt, Shift, Ctrl or Win
count as normal steps. Values below the minimum or above the maximum can still be set with the
remote or KEF Connect; the PC then shows the nearest end of its scale.

## Fixing the speaker's volume curve

To space the key steps in dB, the app needs to know one thing: how loud is the speaker at each
volume value? We expected to take that from KEF. Instead we had to measure it, and the measurement
turned up three things about the LSX II on USB that the app has to work around. This chapter
explains them one by one and ends with a summary.

### How we measured

We put a measurement microphone at the listening position, played test tones from a Mac and
stepped through every volume value, up and down. Tones from low bass to high treble gave the same
result, so the curve does not depend on frequency. We then checked the rule described below with a
230-step test whose levels were predicted in advance; every step matched within the measurement
scatter. The full data is in [docs/usb-volume-curve.md](docs/usb-volume-curve.md).

The curve is a property of the speaker, measured with its USB volume at 0 dB. The Windows app holds
the USB volume at 0 dB as well, so the curve applies unchanged.

### 1. KEF's own volume table is wrong for USB

The speaker publishes a table that says how many dB each volume value gives. It claims 2 dB per
unit below 20 and 0.5 dB per unit above. The microphone says otherwise: on USB it is 1 dB per unit
from 8 to 60 and 0.5 dB per unit from 60 to 65. At value 20 the table is off by 20 dB, at
value 12 still by 12 dB.

**What the app does:** It does not use KEF's table at all. The key steps and the dB value shown in
the menu are based on the measured curve. Nothing on the speaker is changed: value 12 plays exactly
as loud as it always did, the app just knows how loud that is.

### 2. The same value can be louder or quieter

On USB the loudness of a volume value depends on how you got there. Set 20 by turning down from 30,
then set 20 by turning up from 10: the second time the speaker is 2.6 dB quieter, at the same
displayed value. At 12 the difference is 10 dB. From 41 up there is none. So below 41 the speaker
has two volume curves: a louder, straight one for values reached by turning down, and a quieter,
uneven one for values reached by turning up. This is how the speaker's firmware behaves; Windows and
this app play no part in it.

![Two volume curves and how the app reaches every value from above](docs/img/two-curves.svg)

Left alone, this breaks equal steps. In our measurement, going up from 12 to 17 changed the
loudness by less than 1 dB, and the next step, 17 to 23, by almost 9 dB. And the same key sounded
louder after turning down than after turning up.

**What the app does:** The measurements showed a simple pattern. Raising the value never makes the
speaker quieter, and lowering it never makes it louder. The app uses that. When the volume goes up,
it sets a value slightly above the target for 0.1 s and then the target itself. The speaker
arrives at the target from above, on the straight curve the key steps are based on. Example: key
up to 15 → the app sends 19, then 15 (the figure shows the same for 12, reached via 17). For those
0.1 s the speaker is up to about 1.4 dB louder than the target, depending on the value. Turning down
needs no trick, because it already comes from above. With 1 dB keys this brief overshoot can be
larger than the step itself; whether that is audible has not been checked yet.

Result: every key step sounds the same whichever direction you came from.

### 3. Above 60 the curve flattens

From 60 to 65 each unit adds only 0.5 dB, half as much as below 60.

**What the app does:** It spaces the keys by dB, not by units, and a key cannot move less than one
unit. So the keys 60 → 61 → 62 → 63 are 0.5 dB each, and the last key spans two units, 63 → 65,
which is 1 dB like the steps below 60.

### In short

| | KEF's firmware on USB | This project |
| --- | --- | --- |
| What the PC's keys change | The USB input volume: 6 dB steps, at most 8 levels | The speaker's 0–100 volume: 50 steps of about 1 dB |
| How loud each value is | A published table, up to 20 dB off | The curve we measured |
| Same value, same loudness? | No, up to 10 dB apart below 41 | Yes, every value is reached from above |
| Above 60 | Only 0.5 dB per unit | Three keys of 0.5 dB; the last key spans two units |

## Other things the app takes care of

### After a USB reconnect

Waking from standby or reconnecting the cable makes the LSX II disappear from USB and come back. On
Windows the LSX II also leaves USB by itself a few minutes after the sound stops, while it still
reports USB as its source. The app handles that:

- While the LSX II is gone, Windows moves the sound to another output, and it plays there until the
  LSX II is back (there is no always-present virtual device as on the Mac). When it returns, Windows
  usually switches back by itself; the app also restores the LSX II as default output, separately
  for Windows' default, multimedia and communications output, unless you deliberately picked
  something else, such as headphones. Apps that follow the default output move back with it; an app
  that opened the LSX II directly may stop and need to be started again.
- When you start playing while the LSX II is gone from USB, the app wakes it: after 2 s of playback
  on the substitute output it selects USB on the speaker again, which brings the LSX II back. It
  tries that once each time the LSX II has left; if it does not come back within 10 s, the app waits
  at least a minute before the next try. This happens only while the LSX II is still meant to be the
  default output: the app remembers that across restarts and sign-ins, and a device you picked
  deliberately while the LSX II was gone (headphones, for example) stays the default — the app then
  leaves the speaker alone. On a new installation the app brings the LSX II in on the first playback.
- When the LSX II comes back, the app checks the speaker's volume first. If it still has the PC's
  value, nothing is sent; otherwise the PC's value is sent again.
- The speaker sometimes resets its own volume. A jump of 25 or more without a change on the PC is
  treated as a reset, and the PC's value is sent again.
- The USB output could come back at a different sample rate. The app pins its shared-mode format to
  96 kHz / 24-bit and sets it back whenever it changes (playback pauses briefly when that happens).
  Players in exclusive mode use their own rate and are not reset.
- When the PC plays audio on the LSX II for 2 s while the speaker is in standby, the app wakes it
  and selects the USB input. It never switches away from another source that is playing.
- Windows' own mute (mute key, Quick Settings) mutes the speaker's USB input; the speaker value
  stays. Windows remembers that mute across restarts; the app clears it once after each Windows
  start, like the macOS version.

### Several users on one PC

The app is installed for all users. Each signed-in user gets their own instance, and only the one at
the screen talks to the speaker and handles the volume keys. The volume is shared by all users, as
on the Mac. After a user switch the app opens a fresh network connection, because on the Mac once
every request kept failing until the app was restarted; if the speaker is still unreachable two
minutes later, the app restarts itself.

## Requirements

- **The LSX II on USB *and* on the same network as the PC** (Wi-Fi or Ethernet). USB carries the
  audio; the network carries the volume.
- Windows 11 (developed and tested on build 26200, x64).
- The .NET 10 SDK: `winget install Microsoft.DotNet.SDK.10` (once, needs administrator rights). The
  first build also downloads one package (`Microsoft.Windows.SDK.NET.Ref`) from nuget.org.
- An administrator account: the app is installed system-wide.
- Smart App Control off: it blocks unsigned programs, and the app is built on your PC and not signed.
  It has to stay off while the app is installed. On Windows versions before the April 2026 update
  (KB5083769), turning it off can only be undone by resetting Windows.
- Recommended: both LSX II linked by cable and cable mode switched on in KEF Connect. The speakers
  then work at 96 kHz instead of 48 kHz internally, and the app's 96 kHz format is chosen for that,
  as on the Mac.

## Install

Copy this `windows` folder to your PC. If it came as a ZIP from a browser, unblock the files once
in PowerShell: `Get-ChildItem -Recurse | Unblock-File`. Then, in the `windows` folder:

```bat
winget install Microsoft.DotNet.SDK.10   :: once
make.cmd test
scripts\install.cmd                      :: or double-click it; one administrator (UAC) prompt
```

`install.cmd` builds the app, asks once for administrator rights for the system-wide part and then
starts the app in your session. Run it from a normal window or by double-click, not "as
administrator" (it refuses, because the app would then run elevated). Started by double-click, the
window stays open at the end so you can read the result.

After that, the LSX II is your sound output and the volume keys control the speaker. On its first
start the app sets the LSX II's Windows volume to 100 % (0 dB): if Windows was set lower before, the
speaker plays louder than before at the same speaker value, as loud as that value sounds on a Mac.
It also switches the output format to 96 kHz / 24-bit once, which interrupts running playback for a
moment. The tray icon may first appear under the ^ arrow next to the clock
(see the [FAQ](#faq)). There is no question about the local network as on the Mac.

There are no prebuilt binaries: `install.cmd` builds from source on your PC. The app is not signed;
files built locally carry no download mark, so SmartScreen does not check them.

More options:

```bat
scripts\install.cmd all --dry-run                    :: builds and prints the admin script; installs nothing
scripts\install.cmd all --pref maxSpeakerVolume=60   :: plus a system-wide default for all users
```

Installs `C:\Program Files\LSXIIVolumeFixer\LSXIIVolumeFixer.exe`, the value
`io.github.roberdorfer.lsxiivolumefixer` under `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run`
and the key `HKLM\SOFTWARE\io.github.roberdorfer.lsxiivolumefixer`. Running instances of other
signed-in users switch to the new version right away; everyone else gets it at their next sign-in.
A reinstall keeps the system-wide settings and the stored volume.

### Install with an AI coding agent

Paste this into Claude Code, Codex or a similar agent running on the PC:

```text
Install LSX II Volume Fixer for Windows (the `windows` folder of kef-lsx-volume) on this PC. It makes
the Windows volume keys control a KEF LSX II connected over USB in fine steps.

1. Check the prerequisites and stop with a clear explanation if one is missing:
   - Windows 11 (`[Environment]::OSVersion.Version`, build 22000 or later).
   - .NET 10 SDK (`dotnet --list-sdks` lists a 10.x version). If missing, ask me to run
     `winget install Microsoft.DotNet.SDK.10` and wait.
   - The LSX II is connected over USB: `Get-PnpDevice -Class AudioEndpoint -PresentOnly` lists an
     endpoint whose name contains "LSX II".
   - The LSX II is on the same network: a DNS-SD browse for `_http._tcp` in `local.` (for example
     with Windows.Devices.Enumeration, kind AssociationEndpointService; allow up to 30 s) shows a
     service named "LSX II" or a host starting with "lsxii-". If not, ask me to check that the
     speaker and the PC are on the same network.
2. Ask me where the project folder is (or where to put it), then run `make.cmd test` in its
   `windows` folder and show me the result.
3. Run `scripts\install.cmd all --dry-run` and summarise what will be installed.
4. Tell me that the next step may show a Windows administrator (UAC) prompt that I answer myself,
   then run `scripts\install.cmd all` from a shell that is not elevated. Never ask me for a
   password. If the administrator step fails (some sandboxed agent shells cannot write HKLM), ask me
   to double-click `scripts\install.cmd` myself instead.
5. Tell me that the tray icon may be hidden under the ^ arrow next to the clock and that I can pin
   it under Settings > Personalization > Taskbar > Other system tray icons.
6. Verify:
   - Two `LSXIIVolumeFixer` processes (launcher and agent) run from
     `C:\Program Files\LSXIIVolumeFixer\`.
   - `%LOCALAPPDATA%\LSXIIVolumeFixer\LSXIIVolumeFixer.log` shows "real device:" and no "no LSX II
     found via Bonjour" line (if there is one, ask me whether to set `speakerHost` as the README
     describes, then ask me to choose "Restart LSX II Volume Fixer" in the tray menu). If your shell
     cannot see that log (some app sandboxes redirect %LOCALAPPDATA%), ask me to choose "Open log"
     in the tray menu instead.
   - The LSX II is the default output in Settings > System > Sound.
7. Ask me to press the volume keys once and confirm that the speaker follows. Do not play test
   sounds or change the volume yourself.

Do not change other system or security settings. To remove everything later:
`scripts\uninstall.cmd` (add `--purge` to also delete my settings and log).
```

## Settings

All settings are in the tray menu (left or right click on the icon). They can also be set with
`reg add HKCU\Software\io.github.roberdorfer.lsxiivolumefixer /v <key> /t REG_DWORD /d <value> /f`
(numbers and on/off values as `REG_DWORD`, text as `REG_SZ`, for example
`/v speakerHost /d 192.168.1.x`).

Per-user values live in `HKCU\Software\io.github.roberdorfer.lsxiivolumefixer`. A system-wide
default for all users goes into `HKLM\SOFTWARE\io.github.roberdorfer.lsxiivolumefixer`; "Apply
these settings for all users…" in the menu writes the current user's values there (UAC prompt), as
does `scripts\install.cmd all --pref key=value`. A user's own value overrides the system-wide one.
`--pref` writes text values: numbers work that way, on/off values need `REG_DWORD` and are ignored
as text, as on the Mac.

| Key | Default | Meaning |
| --- | --- | --- |
| `speakerHost` | *(empty: Bonjour)* | Optional fixed hostname / IP of the speaker. By default the app finds the LSX II itself via mDNS/DNS-SD, called Bonjour in the menu and log (`_http._tcp` service "LSX II", host `lsxii-<mac>.local`) |
| `minSpeakerVolume` / `maxSpeakerVolume` | `15` / `65` | Speaker value of the first (1/50) and of the last key step; PC 0 = speaker 0 + mute |
| `autoSwitchDefault` | `1` (on) | Keep/restore the LSX II as default output |
| `autoWakeOnPlayback` | `1` (on) | When the PC plays audio on the LSX II for ≥ 2 s while the speaker is in standby, wake it and select USB (other active sources are never switched). Also when the LSX II has left USB while idle and is still meant to be the default output: ≥ 2 s of playback on the substitute output selects USB again (once each time it left, then at most every 70 s); a device picked deliberately in the meantime is respected |
| `showVolumeInMenuBar` | `1` (on) | Show the speaker volume as the number in the tray icon ("Show volume in the taskbar") |
| `resetJumpThreshold` | `25` | Speaker jump treated as a speaker-side reset |
| `restoreInputUIDPrefix` | unset (off) | Optional: device-ID prefix of an input device (e.g. a USB mic on the same hub, `USB\VID_…&PID_…&MI_…`) whose default-input role is restored after a USB re-enumeration. Unrelated to the speaker; opt-in. |
| `defaultOutputSetFor` | set by the app | ID of the LSX II USB output (`USB\VID_27AC&PID_1060&MI_01`) that was made the default output once for this user. Delete it to redo that step. |
| `restoreOutputOnReturn` | set by the app | Which of the three default outputs (bits 1 default, 2 multimedia, 4 communications) should go back to the LSX II when it returns to USB; also decides whether playback wakes it while it is gone. Not a setting. |

With the default 15–65 range the 50 key steps are the speaker values 15–63 65; the menu shows the
values for your range and the step size. Values below the minimum or above the maximum can still be
set with the remote or the KEF app; the PC then shows the nearest end of its scale.

Log: `%LOCALAPPDATA%\LSXIIVolumeFixer\LSXIIVolumeFixer.log` of the user the instance runs as ("Open
log" in the menu).

## Uninstall

```bat
scripts\uninstall.cmd            :: one administrator (UAC) prompt
scripts\uninstall.cmd --purge    :: also deletes your own settings and log
scripts\uninstall.cmd --dry-run  :: prints what would be removed; removes nothing
```

Stops the app in every signed-in session and removes the app, its Run entry, the system-wide
settings and the stored volume. The LSX II stays as it was set: Windows volume 100 %, format
96 kHz / 24-bit, default output. From then on the Windows keys control its USB volume in 6 dB steps
again; the speaker value is left as it was, so nothing gets suddenly louder. Other users keep their
own settings; for them the LSX II is not made the default output again after a reinstall (delete
`defaultOutputSetFor` in their `HKCU` key, or pick the LSX II once). Windows keeps the tray-icon
visibility setting per user; it has no effect once the app is gone. The .NET SDK and the `build`
folder stay as well.

## FAQ

**Does the music go over Wi-Fi?**
No. The audio goes over USB. Only the volume value uses the network. Between the two speakers the
signal goes wirelessly at 48 kHz / 24-bit, or over the cable at 96 kHz / 24-bit in cable mode.

**Is it still bit-perfect if the volume is set in the speaker?**
Up to the speaker's USB input, yes, in two cases:

- A player in **exclusive mode** (list below) sends the music unchanged at its own rate, past
  Windows' mixer, sample-rate converter and limiter, as long as it outputs 16- or 24-bit integer
  (the LSX II accepts no 32-bit or float in exclusive mode), its own volume is at 100 % and no player
  processing runs (volume normalisation or ReplayGain, equaliser). While it plays, other apps and
  system sounds are silent; that is how Windows' exclusive mode works.
- A **96 kHz source playing alone** in normal (shared) mode very likely passes unchanged, if its
  app and session volume are at 100 %, nothing ducks it, audio enhancements, spatial sound and mono
  audio are off, and its peaks stay clear of 0 dBFS: Windows' shared-mode engine has a limiter that
  can change peaks near full scale. Whether Windows dithers when it converts to 24-bit is not
  documented.

Sources at another rate (44.1 or 48 kHz, for example) without exclusive mode are converted to
96 kHz by Windows, as with any fixed-rate output; streams playing at the same time are mixed. The
app never scales samples and holds the USB volume at 0 dB; mute is the speaker's USB hardware mute.

After the USB input, the LSX II's DSP always runs (crossover, bass extension, EQ) and converts other
rates to 48 kHz, or to 96 kHz in cable mode; a 96 kHz input in cable mode is probably not converted
again, but KEF does not say. So end-to-end bit-perfect playback is not possible with this speaker,
with any computer.

Players with exclusive mode (as of October 2026, not yet checked with the LSX II): the Spotify
desktop app (Premium; lossless up to 24-bit / 44.1 kHz; Spotify shows the switch only for external
audio devices), Qobuz, TIDAL, Amazon Music, foobar2000 (WASAPI output), Audirvana. Without: Apple
Music for Windows, Deezer, browsers, games, VLC 3.x.

**Why 96 kHz and not 192 kHz?**
The LSX II's DSP converts every input to 96 kHz / 24-bit when the speakers are linked by cable
(48 kHz over the wireless link). At 192 kHz a 44.1 kHz track would be converted twice: up to
192 kHz by Windows, then down to 96 kHz in the speaker. At 96 kHz it is probably converted at most
once (whether the speaker converts a 96 kHz input again is not documented). 48 kHz would let 48 kHz
sources reach the USB input unconverted, to be converted once in the speaker in cable mode, but
44.1 kHz sources would then go through two conversions, and hi-res sources in shared mode would lose
everything above 24 kHz. Players in exclusive mode keep their source rate either way.

**How much latency does it add?**
None. Windows plays straight to the LSX II's USB output; there is no driver or extra buffer in
between.

**What happens without the network?**
Audio keeps playing over USB, but the volume keys cannot change the speaker volume until the
network is back.

**The keys do nothing and the menu has no "Speaker:" line.**
The app finds the speaker over the local network with mDNS/DNS-SD (Bonjour in the menu and log). If
mDNS is blocked on your network, the speaker is never found. Set its address by hand:
`reg add HKCU\Software\io.github.roberdorfer.lsxiivolumefixer /v speakerHost /d <hostname or IP> /f`,
then choose "Restart LSX II Volume Fixer" in the app's menu. "Open log" shows the reason: a line
saying that no LSX II was found via Bonjour. Note also that the keys only go to the speaker while
the LSX II is the default output; on headphones they control Windows' volume as usual.

**Why not just use the speaker remote or KEF Connect?**
You can, and the PC follows those changes. This project is for people who want the keyboard and an
on-screen display on the PC.

**Why not EarTrumpet, Equalizer APO or the volume mixer?**
They scale the audio signal on the PC, so playback is no longer bit-perfect, and you end up with
two volume controls. Here the signal stays untouched and the speaker sets the volume.

**What if a KEF firmware update changes the curve?**
Then the steps may become uneven again and the curve needs to be measured again. The measurement
method is in [docs/usb-volume-curve.md](docs/usb-volume-curve.md).

**How do I remove it?**
`scripts\uninstall.cmd`. See [Uninstall](#uninstall).

**Why does Windows show 100 % for the LSX II?**
Windows' volume for the LSX II is the speaker's USB volume, the one with only 6 dB steps. The app
keeps it at 100 % (0 dB), so the taskbar speaker icon, the volume flyout, Quick Settings and
Settings > Sound always show 100 % while the app runs. If you move Windows' slider anyway, the app
sets it back to 100 % a moment after you let go, and the speaker volume does not change. The real
volume is the number in the tray icon, the first line of the tray menu and the app's on-screen
display. Windows' mute button works: it is the same mute as the mute key.

**Why do the keys not work over admin windows or the lock screen?**
Windows does not show key presses to a normal program while a window running as administrator is
in front (Task Manager, an administrator terminal, installers), on the lock screen or during a UAC
prompt. There Windows handles the key itself: it moves the USB volume by 2 % and shows its own
flyout, and the app sets the USB volume back to 100 % shortly after, so the key has no effect.
Click another window and the keys work again.

**Where is the tray icon?**
New icons go to the hidden icons under the ^ arrow next to the clock. To keep it visible, turn on
"LSX II Volume Fixer" under Settings > Personalization > Taskbar > Other system tray icons. The icon
shows the speaker volume as a number, or a speaker symbol, dimmed while the LSX II is not on USB.
Left and right click open the same menu.

**Can I use it on a Mac and a PC with the same speaker?**
Not at the same time. Each running copy (this app or the macOS version) sends its own volume back
when the speaker jumps by 25 or more, so two computers with different volumes keep resetting each
other. Run it on one computer at a time.

## macOS and other speakers

This is the Windows version of LSX II Volume Fixer
([macOS version](https://github.com/roberdorfer/kef-lsx-volume)). The macOS version adds a virtual
audio device "KEF LSX II" with its own volume, so the macOS keys, HUD and menu bar slider control the
speaker directly. This Windows version does without a driver: a virtual Windows output needs a
signed kernel driver, and while such a driver held the LSX II, players could no longer use it in
exclusive mode. `Driver\` is reserved for such a driver ([Driver/README.md](Driver/README.md)).
Only the LSX II is tested. The measured curve, the step placement and the approach-from-above logic
in `Helper\Config.cs` are the same as in the macOS `Helper/Config.swift`, with 50 instead of 16
steps. If you want to adapt it to another KEF speaker, feel free to fork the project.

## Technical details

- `Helper\` is the tray app (`C:\Program Files\LSXIIVolumeFixer\LSXIIVolumeFixer.exe`, C# / .NET 10,
  WinForms). Windows starts it at every sign-in through the HKLM `Run` value. Without arguments it
  runs as a small launcher that starts the actual app (`--agent`) and restarts it after any exit (at most once every 5 s),
  like launchd's `KeepAlive` on the Mac; the launcher stops at sign-out, uninstall and update. Task
  Manager shows both processes under the same name: ending the launcher there ends the app as well,
  and it does not come back until the next sign-in (start the exe again to get it back sooner).
- A low-level keyboard hook (`WH_KEYBOARD_LL`, `Helper\KeyHook.cs`) swallows the volume up, down and
  mute keys while the LSX II is the default output and the session is at the screen. The keys move a
  stored volume from 0 to 1 in 1/50 steps (`HKLM\SOFTWARE\io.github.roberdorfer.lsxiivolumefixer\State`,
  shared by all users), which the controller maps to the speaker (`POST /api/setData
  player:volume`); a 1 s poll mirrors remote/app changes back. `Helper\VolumeOsd.cs` draws the
  on-screen display.
- The app pins the LSX II's endpoint volume at 0 dB (`IAudioEndpointVolume`; a change by Windows or
  another app is set back 0.3 s after it stops, and every 10 s), the shared-mode format at
  96 kHz / 24-bit and the default output for all three roles. Format and default output use the
  undocumented `IPolicyConfig`; exclusive-mode streams are left alone. The speaker is found with
  DNS-SD through the WinRT `DeviceWatcher`.
- `Helper\VolumeSurface.cs` (`IVolumeSurface`) is the seam for a later virtual output (v2): the
  controller only sees the interface, so a driver-backed volume can replace the keyboard hook
  without changing `Controller.cs` or `Config.cs`.
- Audio goes from the Windows audio engine (or an exclusive-mode player) straight to the LSX II's
  USB output; the app is not in the audio path and adds no latency.
- `make.cmd test` runs `Tests\ConfigTests.cs` and dry-run tests of both scripts.

The figure in this README is the macOS project's, generated by its `scripts/make_figures.py` from
the same measured data.

## Disclaimer

This is an independent project, not affiliated with or endorsed by KEF. KEF and LSX are
trademarks of their respective owner. The app changes Windows audio settings through an
undocumented interface (`IPolicyConfig`: default output, format) and uses a global keyboard hook;
after uninstalling, these settings stay as they are. Use it at your own risk; the software comes
without warranty (see the license).

## License

MIT, see [LICENSE](LICENSE).
