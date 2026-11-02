#!/usr/bin/env python3
"""Generate the README figures in docs/img/ from the measured curve (docs/usb-volume-curve.md).

Usage: python3 scripts/make_figures.py [output dir]   (default: docs/img)
"""
import sys, os
from html import escape
OUT = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "docs", "img")
os.makedirs(OUT, exist_ok=True)
FONT = "-apple-system, BlinkMacSystemFont, 'Segoe UI', Helvetica, Arial, sans-serif"
BLUE, ORANGE, GREY, INK, MUTED, GRID = "#1f6feb", "#d1661b", "#9aa4ae", "#1f2328", "#59636e", "#e6e9ec"

def above(v): return v - 62.5 if v <= 60 else -2.5 + 0.5 * (v - 60)
BELOW = {8:-60.7,9:-60.7,10:-60.7,11:-60.7,12:-60.7,13:-57.7,14:-55.4,15:-53.3,16:-51.4,17:-49.8,18:-48.2,19:-46.6,20:-45.1,
 21:-43.7,22:-42.3,23:-41.1,24:-39.8,25:-38.6,26:-37.6,27:-36.4,28:-35.3,29:-34.2,30:-33.0,31:-32.0,32:-31.0,33:-30.0,
 34:-28.9,35:-27.9,36:-26.8,37:-25.7,38:-24.6,39:-23.6,40:-22.7}
def below(v): return BELOW.get(v, above(v))

class Svg:
    def __init__(s, w, h): s.w, s.h, s.p = w, h, []
    def add(s, x): s.p.append(x)
    def text(s, x, y, t, size=13, fill=INK, anchor="start", weight="normal", italic=False):
        st = ' font-style="italic"' if italic else ''
        s.add(f'<text x="{x:.1f}" y="{y:.1f}" font-size="{size}" fill="{fill}" text-anchor="{anchor}" font-weight="{weight}"{st}>{escape(t, quote=False)}</text>')
    def line(s, x1, y1, x2, y2, stroke=GRID, w=1, dash=None, marker=None):
        d = f' stroke-dasharray="{dash}"' if dash else ''
        m = f' marker-end="url(#{marker})"' if marker else ''
        s.add(f'<line x1="{x1:.1f}" y1="{y1:.1f}" x2="{x2:.1f}" y2="{y2:.1f}" stroke="{stroke}" stroke-width="{w}"{d}{m}/>')
    def poly(s, pts, stroke, w=2.5, dash=None):
        d = f' stroke-dasharray="{dash}"' if dash else ''
        s.add(f'<polyline fill="none" stroke="{stroke}" stroke-width="{w}" stroke-linejoin="round"{d} points="' + " ".join(f"{x:.1f},{y:.1f}" for x, y in pts) + '"/>')
    def circle(s, x, y, r, fill, stroke=None, w=2):
        st = f' stroke="{stroke}" stroke-width="{w}"' if stroke else ''
        s.add(f'<circle cx="{x:.1f}" cy="{y:.1f}" r="{r}" fill="{fill}"{st}/>')
    def save(s, name, title):
        head = (f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {s.w} {s.h}" width="{s.w}" height="{s.h}" role="img" aria-label="{escape(title)}">'
                f'<title>{escape(title, quote=False)}</title><defs>'
                + "".join(f'<marker id="arr{c[1:]}" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse"><path d="M0,0 L10,5 L0,10 z" fill="{c}"/></marker>' for c in (INK, BLUE, ORANGE, MUTED))
                + f'</defs><rect width="100%" height="100%" rx="10" fill="#ffffff"/><g font-family="{FONT}">')
        open(os.path.join(OUT, name), "w").write(head + "".join(s.p) + "</g></svg>\n")

def axes(s, L, T, R, B, xr, yr, xticks, yticks, xlab, ylab, ylabfmt=lambda v: f"{v:g}"):
    X = lambda v: L + (v - xr[0]) / (xr[1] - xr[0]) * (R - L)
    Y = lambda v: B - (v - yr[0]) / (yr[1] - yr[0]) * (B - T)
    for v in yticks:
        s.line(L, Y(v), R, Y(v)); s.text(L - 8, Y(v) + 4, ylabfmt(v).replace("-", "−"), 12, MUTED, "end")
    for v in xticks:
        s.line(X(v), B, X(v), B + 5, MUTED); s.text(X(v), B + 20, f"{v}", 12, MUTED, "middle")
    s.line(L, B, R, B, MUTED)
    s.text((L + R) / 2, B + 42, xlab, 13, MUTED, "middle")
    s.add(f'<text transform="translate({L-46},{(T+B)/2}) rotate(-90)" font-size="13" fill="{MUTED}" text-anchor="middle">{ylab}</text>')
    return X, Y

# 1) key steps before / after. "before" is the measured macOS key -> USB volume mapping.
def fig_steps():
    s = Svg(760, 430); L, T, R, B = 78, 78, 730, 360
    s.text(24, 32, "What the 16 volume keys do", 18, weight="600")
    s.text(24, 54, "Loudness after each key press, relative to the loudest step", 13, MUTED)
    X, Y = axes(s, L, T, R, B, (0.5, 16.5), (-54, 2), range(1, 17), range(-50, 1, 10), "Volume key step (1 = quietest, 16 = loudest)", "dB")
    before = {1:-30,2:-30,3:-24,4:-18,5:-18,6:-18,7:-12,8:-12,9:-12,10:-6,11:-6,12:-6,13:-6,14:-6,15:0,16:0}
    after = [-50.5 + i * 50.5 / 15 for i in range(16)]
    # before as stairs
    pts = []
    for k in range(1, 17):
        pts += [(X(k - 0.5), Y(before[k])), (X(k + 0.5), Y(before[k]))]
    s.poly(pts, ORANGE, 2, "5 4")
    for k in range(1, 17):
        changed = k == 1 or before[k] != before[k - 1]
        if changed: s.circle(X(k), Y(before[k]), 5.5, ORANGE)
        else: s.circle(X(k), Y(before[k]), 5, "#ffffff", ORANGE, 2)
    s.poly([(X(i + 1), Y(v)) for i, v in enumerate(after)], BLUE, 2.5)
    for i, v in enumerate(after): s.circle(X(i + 1), Y(v), 5.5, BLUE)
    # legend
    lx, ly = X(8.6), Y(-38)
    s.circle(lx, ly, 5.5, ORANGE); s.text(lx + 12, ly + 4, "USB volume before: 6 levels, 6 dB apart", 13)
    s.circle(lx, ly + 22, 5, "#ffffff", ORANGE, 2); s.text(lx + 12, ly + 26, "key press that changed nothing (10 of 16)", 13)
    s.circle(lx, ly + 44, 5.5, BLUE); s.text(lx + 12, ly + 48, "with this project: 16 levels, 3.4 dB apart", 13)
    s.text(L, B + 62, "Before: measured on macOS with the speaker's USB volume (−42 … 0 dB in 6 dB steps). After: default range, speaker values 12 … 65.", 11.5, MUTED)
    s.save("key-steps.svg", "Volume key steps before and after")

# 2) two curves + approach from above
def fig_curves():
    s = Svg(760, 470); L, T, R, B = 78, 78, 730, 390
    s.text(24, 32, "One speaker value, two loudness levels", 18, weight="600")
    s.text(24, 54, "Speaker volume over USB, measured with a microphone at the listening position", 13, MUTED)
    X, Y = axes(s, L, T, R, B, (8, 45), (-66, -18), range(10, 46, 5), range(-60, -19, 10), "Speaker volume value", "dB (relative to value 65)")
    vs = list(range(8, 46))
    # shaded gap
    s.add('<polygon fill="#d1661b" fill-opacity="0.10" points="' + " ".join(f"{X(v):.1f},{Y(above(v)):.1f}" for v in vs) + " " + " ".join(f"{X(v):.1f},{Y(below(v)):.1f}" for v in reversed(vs)) + '"/>')
    s.poly([(X(v), Y(below(v))) for v in vs], ORANGE, 2.5)
    s.poly([(X(v), Y(above(v))) for v in vs], BLUE, 2.5)
    s.text(X(36), Y(above(36)) - 16, "reached by turning down: 1 dB per step", 13, BLUE, "end", "600")
    s.text(X(25), Y(below(25)) + 22, "reached by turning up: quieter, uneven", 13, ORANGE, "start", "600")
    s.text(X(41) - 6, Y(-35), "from 41 up both are the same", 12, MUTED, "end")
    s.line(X(41), Y(above(41)) + 8, X(41), B, GREY, 1, "3 3")
    # example: target 12 via 17
    x12, y12 = X(12), Y(above(12)); x17, y17 = X(17), Y(below(17)); y12b = Y(below(12))
    s.circle(x12, y12b, 5, "#ffffff", ORANGE, 2)
    s.circle(x17, y17, 5.5, INK)
    s.circle(x12, y12, 6, INK)
    s.text(L + 6, Y(-63.6), "12 after turning up: −60.7 dB", 12, MUTED)
    s.add(f'<path d="M{x12+6:.1f},{y12b-8:.1f} Q{x17-30:.1f},{y12b-40:.1f} {x17-2:.1f},{y17+9:.1f}" fill="none" stroke="{INK}" stroke-width="1.6" marker-end="url(#arr1f2328)"/>')
    s.add(f'<path d="M{x17-6:.1f},{y17-6:.1f} Q{(x12+x17)/2:.1f},{y12-34:.1f} {x12+7:.1f},{y12-5:.1f}" fill="none" stroke="{INK}" stroke-width="1.6" marker-end="url(#arr1f2328)"/>')
    s.text(x17 + 12, y17 + 20, "① briefly set 17", 12.5, INK, weight="600")
    s.text(L + 8, Y(-31), "② then 12: lands on the upper curve, −50.5 dB", 12.5, INK, weight="600")
    s.text(L, B + 62, "The app reaches every value from above, so each key step lands on the straight upper curve, whichever direction you came from.", 11.5, MUTED)
    s.save("two-curves.svg", "Two volume curves and how the app reaches every value from above")

# 3) min/max and equal steps
def fig_range():
    s = Svg(760, 486); L, T, R, B = 78, 78, 690, 390
    s.text(24, 32, "How the 16 key steps are placed", 18, weight="600")
    s.text(24, 54, "This app spaces the keys by loudness (dB) on the measured curve, between the minimum and maximum you choose", 13, MUTED)
    X, Y = axes(s, L, T, R, B, (8, 70), (-56, 3), range(10, 71, 10), range(-50, 1, 10), "Speaker volume value", "dB (relative to value 65)")
    vs = [v / 2 for v in range(16, 141)]
    s.poly([(X(v), Y(above(v))) for v in vs], GREY, 2)
    # KEF's published table (2 dB per unit below 20, 0.5 above), anchored at 65 like the measured curve
    kef = lambda v: (v - 65) * 0.5 if v >= 20 else -22.5 - 2 * (20 - v)
    s.poly([(X(v), Y(kef(v))) for v in vs], ORANGE, 2, "6 4")
    s.text(X(10), Y(-7), "KEF's own table (wrong on USB, not used)", 12, ORANGE, "start", "600")
    steps = [12, 15, 19, 22, 25, 29, 32, 36, 39, 42, 46, 49, 52, 56, 59, 65]
    for i, v in enumerate(steps):
        y = Y(above(v))
        s.line(X(v), y, R, y, "#c9d6ea", 1, "2 3")
        s.circle(X(v), y, 5.5, BLUE)
        if i in (0, 15) or i % 3 == 2: s.text(R + 8, y + 4, f"key {i+1}", 11.5, BLUE)
    s.text(X(12) + 10, Y(above(12)) + 4, "Minimum = key 1 (app default 12)", 12.5, INK, weight="600")
    s.text(X(65) + 4, Y(above(65)) - 14, "Maximum = key 16 (app default 65)", 12.5, INK, "end", "600")
    s.text(X(57), Y(-1.5), "measured: 0.5 dB per value above 60", 12, MUTED, "end")
    s.text(X(31) + 8, Y(above(31)) + 24, "measured: 1 dB per value (8 … 60)", 12, MUTED)
    s.text(L, B + 62, "Gap per key = (loudness at maximum − loudness at minimum) ÷ 15. Default: 50.5 dB ÷ 15 = 3.4 dB.", 11.5, MUTED)
    s.text(L, B + 78, "Change either end in the menu and all 16 steps are recalculated.", 11.5, MUTED)
    s.save("key-step-range.svg", "Key steps placed at equal dB between minimum and maximum")

# 4) signal paths
def fig_paths():
    s = Svg(760, 250)
    def box(x, y, w, h, title, sub, fill="#f6f8fa", stroke="#d0d7de"):
        s.add(f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="8" fill="{fill}" stroke="{stroke}"/>')
        s.text(x + w / 2, y + h / 2 - 3, title, 13.5, INK, "middle", "600"); s.text(x + w / 2, y + h / 2 + 15, sub, 11.5, MUTED, "middle")
    s.text(24, 32, "Audio over USB, volume over the network", 18, weight="600")
    s.add('<rect x="20" y="52" width="384" height="182" rx="10" fill="none" stroke="#d0d7de" stroke-dasharray="4 4"/>')
    s.text(34, 72, "Mac", 13, MUTED, weight="600")
    box(36, 86, 132, 54, "Apps, volume keys", "normal macOS output")
    box(214, 86, 172, 54, "“KEF LSX II” output", "driver, samples unchanged")
    box(214, 166, 172, 54, "LSX II Volume Fixer", "menu bar app")
    box(560, 106, 170, 94, "KEF LSX II", "speaker", "#eef4ff", "#9cbcf0")
    s.line(168, 113, 210, 113, MUTED, 1.6, marker="arr59636e")
    s.line(300, 140, 300, 162, MUTED, 1.6, marker="arr59636e"); s.text(306, 156, "volume", 11, MUTED)
    s.line(386, 113, 556, 132, BLUE, 3, marker="arr1f6feb"); s.text(470, 106, "USB cable: audio, bit-perfect", 12, BLUE, "middle", "600")
    s.line(386, 193, 556, 176, ORANGE, 2.2, "6 4", marker="arrd1661b"); s.text(470, 208, "Wi-Fi / LAN: volume value only", 12, ORANGE, "middle", "600")
    s.save("signal-paths.svg", "Audio goes over USB, only the volume value goes over the network")

fig_steps(); fig_curves(); fig_range(); fig_paths()
