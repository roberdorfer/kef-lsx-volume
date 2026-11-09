# Driver (reserved for v2)

This version of LSX II Volume Fixer for Windows (v1) has no driver. Windows plays straight to the
LSX II's own USB output, and the tray app catches the volume keys with a keyboard hook. This folder
is kept free for a possible later version (v2) with a virtual output, the Windows counterpart of the
macOS `Driver/KEFProxyDriver.cpp`.

What v2 would be:

- A virtual output "KEF LSX II" (UID `io.github.roberdorfer.kefproxy.device`) with its own volume
  control, so the Windows volume keys, the Windows volume flyout and per-app routing work on it as
  on the Mac. The keyboard hook and the app's own on-screen display would then be off.
- A kernel-mode audio driver (PortCls WaveRT miniport, or ACX), one fixed format of 96 kHz, plus a
  user-mode bridge that opens the LSX II in exclusive mode at the same rate and copies the samples
  to it.
- The app would use it through the existing seam `IVolumeSurface` in `Helper\VolumeSurface.cs`;
  `Controller.cs` and `Config.cs` stay unchanged. The reserved names (install target `driver`,
  tuning property `kcfg`, `scripts\kefstats`) become active.

Why v1 does without it:

- A Windows kernel driver must be signed through Microsoft's attestation service with an EV
  code-signing certificate, or run with test signing and Secure Boot off. A bug in it crashes
  Windows, not just the audio service.
- While the bridge holds the LSX II in exclusive mode, players can no longer use it in exclusive
  mode with their source rate, and Spotify offers its exclusive mode only for external, not
  virtual, devices. For bit-perfect playback v1 is the better design; v2 would add convenience
  (Windows flyout, an output that stays present across USB reconnects) at the cost of that path.

`scripts\install.cmd driver` and `make.cmd driver` answer `driver: not part of this version`.
