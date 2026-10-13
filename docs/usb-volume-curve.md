# Measured USB volume curve of the LSX II

Measured 2026-10-12 on a MacBook Pro with the LSX II on USB (proxy 192 kHz, USB host volume
0 dB, driver passes samples unchanged). Signal: five simultaneous sine tones (150, 420, 1000, 3100,
7300 Hz) played from the Mac; level at the listening position taken with a USB measurement microphone (Beyerdynamic FOX) and a
Goertzel filter per tone every 0.5 s (room noise rejected, noise floor ≈ −95 dBFS, lowest signal
−75 dBFS). All values are relative to speaker 65 within one run; absolute SPL was not measured.
All five tones give the same steps within 0.3 dB, so the curves are frequency-independent.

## Two curves and a switching rule

`player:volume` on the USB path does not have one output level per value but two, depending on the
direction the value is reached from:

| Speaker | from above (dB) | from below (dB) | difference |
| --- | --- | --- | --- |
| 8–12 | v − 62.5 (−54.5 … −50.5) | −60.7 (flat) | 6–10 |
| 13 | −49.5 | −57.7 | 8.2 |
| 14 | −48.5 | −55.4 | 6.9 |
| 15 | −47.5 | −53.3 | 5.8 |
| 16 | −46.5 | −51.4 | 4.9 |
| 17 | −45.5 | −49.8 | 4.3 |
| 18 | −44.5 | −48.2 | 3.7 |
| 19 | −43.5 | −46.6 | 3.1 |
| 20 | −42.5 | −45.1 | 2.6 |
| 21 | −41.5 | −43.7 | 2.2 |
| 22 | −40.5 | −42.3 | 1.8 |
| 23 | −39.5 | −41.1 | 1.6 |
| 24 | −38.5 | −39.8 | 1.3 |
| 25 | −37.5 | −38.6 | 1.1 |
| 26 | −36.5 | −37.6 | 1.1 |
| 27 | −35.5 | −36.4 | 0.9 |
| 28 | −34.5 | −35.3 | 0.8 |
| 29 | −33.5 | −34.2 | 0.7 |
| 30 | −32.5 | −33.0 | 0.5 |
| 31 | −31.5 | −32.0 | 0.5 |
| 32 | −30.5 | −31.0 | 0.5 |
| 33 | −29.5 | −30.0 | 0.5 |
| 34 | −28.5 | −28.9 | 0.4 |
| 35 | −27.5 | −27.9 | 0.4 |
| 36 | −26.5 | −26.8 | 0.3 |
| 37 | −25.5 | −25.7 | 0.2 |
| 38 | −24.5 | −24.6 | 0.1 |
| 39 | −23.5 | −23.6 | 0.1 |
| 40 | −22.5 | −22.7 | 0.2 |
| 41–60 | v − 62.5 | same | 0 |
| 60–65 | −2.5 + 0.5 (v − 60) | same | 0 |

- **From above** (`Config.dB`): straight, 1.0 dB per unit from 8 to 60 and 0.5 dB per unit from 60
  to 65. Below 8 not measured.
- **From below** (`Config.dBFromBelow`): identical from 41 up, steeper below (≈2 dB per unit around
  13–16) and flat below 13: raising the value within 8–12 changes nothing.
- **Switching rule**: raising the value gives max(current level, from-below(v)); lowering it gives
  min(current level, from-above(v)). Raising never makes the output quieter, lowering never louder.
  Dwell time does not matter (0.1 s behaves like 4 s). The rule was derived from blocks A–C
  (sweeps up/down, 1-unit steps, interleaved jumps) and then confirmed by block D, a 230-step plan
  with predicted levels (every prediction within the ±0.5 dB measurement scatter).

Practical consequences without compensation: 12 → 17 from below changed the output by under 1 dB,
17 → 23 by 8.7 dB; a small step down from an up-state changed nothing at all.

## Compensation: reach every value from above

Because raising never lowers the output, a value `v` can be forced onto the from-above curve from
any state by briefly setting a higher value `v + k` whose from-below level is at least
from-above(v), then `v`. The helper does this (`Config.approachFromAbove`, 0.2 dB margin,
`Controller.flushSend`) whenever the value goes up or the speaker state is unknown:

| target | via | blip above target |
| --- | --- | --- |
| 12 | 17 | 0.7 dB |
| 14 | 18 | 0.3 dB |
| 17 | 20 | 0.4 dB |
| 20 | 22 | 0.2 dB |
| 23 | 25 | 0.9 dB |
| 25 | 27 | 1.1 dB |
| 28–40 | +1 | ≤ 1 dB |
| 41 and up | none | |

Going down from a known value already arrives from above and needs no overshoot. The 16 key steps
(`Config.keyStepTable`) are placed on the from-above curve only, at equal dB distances between the
minimum and the maximum (rounded to whole speaker values).

## KEF's own table

`settings:/mediaPlayer/volumeMap` does not describe the USB path: it claims 2 dB per unit below 20
and 0.5 dB per unit above. Do not use it for the USB output.
