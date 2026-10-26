// Pure-function tests for the volume mapping in Helper\Config.cs (≙ Tests/ConfigTests.swift). Built and run by
// `make.cmd test`; no test framework so that the .NET SDK alone suffices. The tests never touch the registry:
// every call passes the range explicitly.
//
// The macOS tests are ported with the same names and assertions; they call the mappings with 16 steps
// (macOS) because Windows uses 50 (docs/DESIGN.md §11.1). The Addition tests below them cover the 50 steps, golden
// values of the macOS functions and Windows-only rules.

using System.Globalization;
using static System.FormattableString;

static class ConfigTests
{
    static int failures = 0;
    static void Check(bool ok, string what)
    {
        if (!ok) { failures += 1; Console.WriteLine($"FAIL {what}"); }
    }

    static int Main()
    {
        // Design decision: Equal measured-dB key steps between minimum and maximum — regression test.
        test_decision_keyStepsAreEqualInMeasuredDB();
        test_keyStepTable_endsAndMonotonic();
        test_keyStepTable_12to65_matchesDocumentedTable();
        test_approachFromAbove_landsOnFromAboveCurve();
        test_dB_curveShape();
        test_decision_speakerDiscoveryMatchRule();
        test_decision_identifiersUseGitHubReverseDNS();
        test_decision_outputFixedAt96kHz24bit();
        // Addition (Windows), docs/DESIGN.md §11.1.
        test_golden_keyStepTables();
        test_golden_speakerVolumeForScalar();
        test_golden_scalarForSpeakerVolume();
        test_golden_keyStepTable50();
        test_keyStepTable50_strictlyIncreasing();
        test_golden_approachFromAbove();
        test_edge_rangeClampNeverDegenerates();
        test_windowsIdentifiers();
        test_isConsoleDecision();
        test_uidPrefix_windows();
        test_settingsParsing();
        test_volumeKeys50();
        test_launcherThrottle();
        test_applyForAllUsersScript();
        test_installIdChange();
        test_defaultOutputPlanB();
        test_autoWakeReselect();
        test_reappearPush();
        if (failures == 0) { Console.WriteLine("ConfigTests: all passed"); return 0; }
        Console.WriteLine($"ConfigTests: {failures} failure(s)");
        return 1;
    }

    /// The macOS key steps (16) for the ported macOS tests.
    const int macSteps = 16;
    static readonly (int lo, int hi)[] ranges = [(12, 65), (12, 100), (20, 65), (30, 40), (60, 65), (12, 55), (8, 100)];

    // Swift's `\(array)` and `.first` / `.last`.
    static string S(IEnumerable<int> t) => "[" + string.Join(", ", t) + "]";
    static int? First(int[] t) => t.Length > 0 ? t[0] : null;
    static int? Last(int[] t) => t.Length > 0 ? t[^1] : null;
    static bool Monotone(int[] t) => t.Zip(t.Skip(1)).All(p => p.First <= p.Second);

    static void test_keyStepTable_endsAndMonotonic()
    {
        foreach (var (lo, hi) in ranges)
        {
            var t = Config.KeyStepTable(min: lo, max: hi, steps: macSteps);
            Check(t.Length == 16, $"{lo}-{hi}: 16 entries, got {t.Length}");
            Check(First(t) == lo, $"{lo}-{hi}: first step is the minimum");
            Check(Last(t) == hi, $"{lo}-{hi}: last step is the maximum");
            Check(Monotone(t), $"{lo}-{hi}: monotone {S(t)}");
        }
    }

    static void test_decision_keyStepsAreEqualInMeasuredDB()
    {
        foreach (var (lo, hi) in ranges.Where(r => r.hi - r.lo >= 15))
        {
            var t = Config.KeyStepTable(min: lo, max: hi, steps: macSteps);
            double stepDB = (Config.DB(hi) - Config.DB(lo)) / 15;
            // Each target is rounded to a whole speaker value (≤ 0.5 dB off), so two neighbouring steps
            // may differ from the ideal step by up to 1 dB; nothing larger is acceptable.
            for (int i = 1; i < 16; i++)
            {
                double d = Config.DB(t[i]) - Config.DB(t[i - 1]);
                Check(Math.Abs(d - stepDB) <= 1.0 + 1e-9, Invariant($"{lo}-{hi}: step {i} is {d} dB, ideal {stepDB}"));
            }
            // And each step sits at the nearest speaker value to its ideal dB position.
            for (int i = 1; i < 15; i++)
            {
                double ideal = Config.DB(lo) + stepDB * i;
                Check(Math.Abs(Config.DB(t[i]) - ideal) <= 0.5 + 1e-9, Invariant($"{lo}-{hi}: step {i}={t[i]} is {Config.DB(t[i]) - ideal} dB off its ideal"));
            }
        }
    }

    static void test_keyStepTable_12to65_matchesDocumentedTable()
    {
        int[] expected = [12, 15, 19, 22, 25, 29, 32, 36, 39, 42, 46, 49, 52, 56, 59, 65];   // README, docs/usb-volume-curve.md
        Check(Config.KeyStepTable(min: 12, max: 65, steps: macSteps).SequenceEqual(expected), $"12-65 table {S(Config.KeyStepTable(min: 12, max: 65, steps: macSteps))}");
    }

    static void test_approachFromAbove_landsOnFromAboveCurve()
    {
        for (int v = 8; v <= 100; v++)   // measured region only (below 8 both curves are extrapolated)
        {
            if (Config.ApproachFromAbove(v, cap: 100) is int via)
            {
                Check(via > v && via <= 100, $"via {via} for {v} is above the target and within the cap");
                Check(Config.DBFromBelow(via) >= Config.DB(v), $"via {via} from below is at least as loud as {v} from above");
                Check(Config.DBFromBelow(via - 1) < Config.DB(v) + 0.2 || via - 1 == v, $"via {via} for {v} is the smallest overshoot");
            }
            else
            {
                Check(Config.DBFromBelow(v) >= Config.DB(v) || v + 1 > 100, $"{v} needs no overshoot only when both curves agree");
            }
        }
        Check(Config.ApproachFromAbove(12, cap: 100) == 17, "12 -> via 17");
        Check(Config.ApproachFromAbove(17, cap: 100) == 20, "17 -> via 20");
        Check(Config.ApproachFromAbove(41, cap: 100) == null, "41 needs no overshoot");
        Check(Config.ApproachFromAbove(64, cap: 65) == null, "cap respected");
    }

    static void test_dB_curveShape()
    {
        Check(Config.DB(65) == 0, "65 is the 0 dB reference");
        Check(Config.DB(60) == -2.5 && Config.DB(12) == -50.5, "1 dB per unit to 60, 0.5 dB per unit above");
        Check(Enumerable.Range(1, 100).All(v => Config.DB(v) > Config.DB(v - 1)), "from-above curve strictly increasing");
        Check(Enumerable.Range(8, 93).All(v => Config.DBFromBelow(v) <= Config.DB(v) + 1e-9), "from below is never louder than from above (measured region)");
    }

    // Design decision: Identifiers are io.github.roberdorfer.* — regression test.
    static void test_decision_identifiersUseGitHubReverseDNS()
    {
        Check(suiteName == "io.github.roberdorfer.lsxiivolumefixer", "defaults domain / bundle id");
        Check(proxyUID == "io.github.roberdorfer.kefproxy.device", "proxy device UID");
        Check(Config.userKeys.Contains("defaultOutputSetFor") && !Config.userKeys.Contains("firstRunDone"), "default-output marker is per proxy UID");
    }

    // Design decision: The speaker is found via Bonjour instead of a default hostname.
    static void test_decision_speakerDiscoveryMatchRule()
    {
        Check(SpeakerDiscovery.Matches(name: "LSX II", host: null), "service name LSX II matches before resolving");
        Check(SpeakerDiscovery.Matches(name: "lsx ii", host: null), "name match is case-insensitive");
        Check(SpeakerDiscovery.Matches(name: "Living room", host: "lsxii-xxxxxxxxxxxx.local."), "renamed speaker matches by host prefix");
        Check(SpeakerDiscovery.Matches(name: "Living room", host: "LSXII-0000.local"), "host prefix match is case-insensitive");
        Check(!SpeakerDiscovery.Matches(name: "Living room", host: null), "unrelated name without host does not match");
        Check(!SpeakerDiscovery.Matches(name: "Printer", host: "brother-1234.local."), "unrelated service does not match");
        Check(!SpeakerDiscovery.Matches(name: "LS50", host: "ls50w2-abcd.local."), "other KEF model does not match");
    }

    // Design decision: The USB device is pinned to 96 kHz / 24-bit, the LSX II's internal DSP rate.
    static void test_decision_outputFixedAt96kHz24bit()
    {
        Check(pinnedRealRate == 96000, "real device pinned to 96 kHz");
        Check(pinnedRealBits == 24, "real device pinned to 24-bit");
    }

    // MARK: - Addition (Windows)

    // Golden values below were computed with an independent re-implementation of Config.swift
    // (Float32 arithmetic emulated, `.rounded()` = half away from zero), not with this port.

    /// MAP-03: the macOS key-step tables of the seven test ranges (16 steps). The Windows rounding rule must
    /// give exactly these (docs/DESIGN.md §5.2: without equal neighbours in the macOS table both rules agree).
    static void test_golden_keyStepTables()
    {
        (int lo, int hi, int[] table)[] golden =
        [
            (12, 65, [12, 15, 19, 22, 25, 29, 32, 36, 39, 42, 46, 49, 52, 56, 59, 65]),
            (12, 100, [12, 17, 21, 26, 30, 35, 39, 44, 48, 53, 57, 64, 73, 82, 91, 100]),
            (20, 65, [20, 23, 26, 28, 31, 34, 37, 40, 43, 45, 48, 51, 54, 57, 60, 65]),
            (30, 40, [30, 31, 31, 32, 33, 33, 34, 35, 35, 36, 37, 37, 38, 39, 39, 40]),
            (60, 65, [60, 60, 61, 61, 61, 62, 62, 62, 63, 63, 63, 64, 64, 64, 65, 65]),
            (12, 55, [12, 15, 18, 21, 23, 26, 29, 32, 35, 38, 41, 44, 46, 49, 52, 55]),
            (8, 100, [8, 13, 18, 22, 27, 32, 37, 42, 46, 51, 56, 62, 71, 81, 90, 100]),
        ];
        foreach (var (lo, hi, table) in golden)
        {
            var t = Config.KeyStepTable(min: lo, max: hi, steps: macSteps);
            Check(t.SequenceEqual(table), $"{lo}-{hi} (16 steps): {S(t)}, macOS {S(table)}");
        }
        // docs/DESIGN.md §5.2, comparison row: 15–65 with 16 steps.
        int[] t1565 = [15, 18, 21, 24, 28, 31, 34, 37, 40, 43, 47, 50, 53, 56, 59, 65];
        Check(Config.KeyStepTable(min: 15, max: 65, steps: macSteps).SequenceEqual(t1565), $"15-65 (16 steps): {S(Config.KeyStepTable(min: 15, max: 65, steps: macSteps))}");
    }

    /// MAP-05: speakerVolume(forScalar:) of macOS for k/16 and k/64 over 12–65 (checks the interpolation and the
    /// Float32 arithmetic), and the rounding case: 1–40, 0.53125 → 20.5 → 21 (banker's rounding would give 20).
    static void test_golden_speakerVolumeForScalar()
    {
        int[] k16 = [0, 12, 15, 19, 22, 25, 29, 32, 36, 39, 42, 46, 49, 52, 56, 59, 65];
        int[] k64 = [0, 12, 12, 12, 12, 13, 14, 14, 15, 16, 17, 18, 19, 20, 21, 21, 22, 23, 24, 24, 25, 26, 27, 28, 29, 30,
                     31, 31, 32, 33, 34, 35, 36, 37, 38, 38, 39, 40, 40, 41, 42, 43, 44, 45, 46, 47, 47, 48, 49, 50, 50, 51,
                     52, 53, 54, 55, 56, 57, 57, 58, 59, 60, 62, 64, 65];
        for (int k = 0; k <= 16; k++)
        {
            int v = Config.SpeakerVolume(forScalar: (float)k / 16, min: 12, max: 65, steps: macSteps);
            Check(v == k16[k], $"12-65: {k}/16 -> {v}, macOS {k16[k]}");
        }
        for (int k = 0; k <= 64; k++)
        {
            int v = Config.SpeakerVolume(forScalar: (float)k / 64, min: 12, max: 65, steps: macSteps);
            Check(v == k64[k], $"12-65: {k}/64 -> {v}, macOS {k64[k]}");
        }
        int r = Config.SpeakerVolume(forScalar: 0.53125f, min: 1, max: 40, steps: macSteps);
        Check(r == 21, $"1-40: 0.53125 -> {r}, macOS 21 (20.5 rounded away from zero)");
        Check(Config.SpeakerVolume(forScalar: 0.0005f, min: 12, max: 65, steps: macSteps) == 0, "0.0005 (Float32) is still 0");
        Check(Config.SpeakerVolume(forScalar: float.NaN, min: 12, max: 65, steps: macSteps) == 65, "NaN ends at the maximum, as with Swift's min/max");
    }

    /// MAP-06: scalar(forSpeakerVolume:) of macOS over 12–65 with 16 steps, compared exactly: every golden value is
    /// a Float32, and computing in Double instead shifts some results by 1 ulp (20 -> 0x3E555555 instead of
    /// 0x3E555556), which a later key press can turn into another speaker value. Also a table with equal
    /// neighbours (60–65, same table under both rules) and the maximum of a narrow range.
    static void test_golden_scalarForSpeakerVolume()
    {
        (int v, double s)[] golden =
        [
            (0, 0), (1, 0.0625), (5, 0.0625), (12, 0.0625), (13, 0.0833333358168602), (15, 0.125), (19, 0.1875),
            (20, 0.2083333432674408), (30, 0.3958333432674408), (40, 0.5833333730697632), (50, 0.7708333730697632),
            (59, 0.9375), (60, 0.9479166269302368), (64, 0.9895833134651184), (65, 1), (80, 1),
        ];
        foreach (var (v, s) in golden)
        {
            float got = Config.Scalar(forSpeakerVolume: v, min: 12, max: 65, steps: macSteps);
            Check(got == (float)s, Invariant($"12-65: {v} -> {got} (0x{BitConverter.SingleToInt32Bits(got):X8}), macOS {s}"));
        }
        // 60–65: [60 60 61 61 61 62 62 62 63 63 63 64 64 64 65 65]; a value maps to the first of its equal entries.
        (int v, float s)[] narrow = [(60, 0.0625f), (61, 0.1875f), (62, 0.375f), (63, 0.5625f), (64, 0.75f), (65, 1f)];
        foreach (var (v, s) in narrow)
        {
            float got = Config.Scalar(forSpeakerVolume: v, min: 60, max: 65, steps: macSteps);
            Check(got == s, Invariant($"60-65: {v} -> {got}, macOS {s}"));
        }
        Check(Config.Scalar(forSpeakerVolume: 8, min: 1, max: 8, steps: macSteps) == 1, "1-8: the maximum -> 1");
    }

    /// 50 steps over the default range 15–65 (docs/DESIGN.md §5.1, §5.2, §5.4); then exact Float32 scalars and exact
    /// tables of further ranges, recomputed with the independent re-implementation (the structural test below
    /// cannot see where an equal neighbour sits).
    static void test_golden_keyStepTable50()
    {
        int[] expected = [.. Enumerable.Range(15, 49), 65];   // 15 16 … 62 63 65
        var t = Config.KeyStepTable(min: 15, max: 65, steps: 50);
        Check(t.SequenceEqual(expected), $"15-65 (50 steps): {S(t)}");
        Check(First(t) == 15 && Last(t) == 65, "15-65 (50 steps): first 15, last 65");
        Check(Config.KeyStepTable(min: 15, max: 65).SequenceEqual(t), "50 steps is the default");
        Check(Config.keyStep == 1f / 50 && Config.keySteps == 50, "key step 1/50");
        for (int k = 1; k <= 50; k++)
        {
            float s = (float)k / 50;
            int v = Config.SpeakerVolume(forScalar: s, min: 15, max: 65);
            Check(v == t[k - 1], $"15-65: {k}/50 -> {v}, table {t[k - 1]}");
            // Float32: about half of the k come back 1–2 ulp off k/50, so not compared exactly.
            float back = Config.Scalar(forSpeakerVolume: t[k - 1], min: 15, max: 65);
            Check(Math.Abs(back - s) <= 1e-6, Invariant($"15-65: scalar({t[k - 1]}) = {back}, expected {k}/50 = {s}"));
        }
        Check(Math.Abs(Config.Scalar(forSpeakerVolume: 64, min: 15, max: 65) - 0.99) <= 1e-6, "15-65: 64 -> 0.99");
        Check(Config.SpeakerVolume(forScalar: 0.99f, min: 15, max: 65) == 64, "15-65: 0.99 -> 64");
        Check(Config.Scalar(forSpeakerVolume: 10, min: 15, max: 65) == 1f / 50, "15-65: 10 (below the minimum) -> 1/50");
        Check(Config.Scalar(forSpeakerVolume: 80, min: 15, max: 65) == 1, "15-65: 80 (above the maximum) -> 1");
        Check(Config.SpeakerVolume(forScalar: 0, min: 15, max: 65) == 0 && Config.Scalar(forSpeakerVolume: 0, min: 15, max: 65) == 0, "0 <-> 0 (speaker 0 + mute)");

        // Exact Float32 results of the macOS expression with 50 steps (1 ulp off the decimal value is correct).
        (int lo, int hi, int v, int bits)[] scalars =
        [
            (15, 65, 19, 0x3DCCCCCC),   // 0.099999994f, 0.1f is 0x3DCCCCCD
            (15, 65, 29, 0x3E99999B),   // 0.30000004f, 0.3f is 0x3E99999A
            (20, 60, 22, 0x3D75C28F),   // 0.06f: first of the two 22s (index 2), not the second
            (20, 60, 27, 0x3E3851EB),   // 0.17999999f, 0.18f is 0x3E3851EC
        ];
        foreach (var (lo, hi, v, bits) in scalars)
        {
            float got = Config.Scalar(forSpeakerVolume: v, min: lo, max: hi);
            Check(BitConverter.SingleToInt32Bits(got) == bits, Invariant($"{lo}-{hi} (50 steps): scalar({v}) = {got} (0x{BitConverter.SingleToInt32Bits(got):X8}), expected 0x{bits:X8}"));
        }
        Check(Config.Scalar(forSpeakerVolume: 5, min: 1, max: 5) == 1, "1-5 (50 steps): the maximum -> 1");

        // Exact tables where the rounding rule decides (docs/DESIGN.md §5.2, A75): the wide/narrow boundary
        // (max − min = 49 / 48), narrow ranges, and exact dB ties (15–61: the smaller value wins).
        static int[] Run(int from, int to) => [.. Enumerable.Range(from, to - from + 1)];
        (int lo, int hi, int[] table)[] golden50 =
        [
            (17, 65, [.. Run(17, 23), 23, .. Run(24, 65)]),   // §5.2: one equal neighbour, no value skipped
            (15, 64, Run(15, 64)),
            (15, 63, [.. Run(15, 24), 24, .. Run(25, 63)]),
            (15, 61, [.. Run(15, 21), 21, .. Run(22, 34), 34, .. Run(35, 47), 47, .. Run(48, 61)]),
            (20, 60, [20, 21, 22, 22, 23, 24, 25, 26, 27, 27, 28, 29, 30, 31, 31, 32, 33, 34, 35, 36, 36, 37, 38, 39, 40,
                      40, 41, 42, 43, 44, 44, 45, 46, 47, 48, 49, 49, 50, 51, 52, 53, 53, 54, 55, 56, 57, 58, 58, 59, 60]),
        ];
        foreach (var (lo, hi, table) in golden50)
        {
            var t50 = Config.KeyStepTable(min: lo, max: hi, steps: 50);
            Check(table.Length == 50 && t50.SequenceEqual(table), $"{lo}-{hi} (50 steps): {S(t50)}, expected {S(table)}");
        }
    }

    /// Every range 1 ≤ min < max ≤ 100 with 50 steps (docs/DESIGN.md §5.2, A75): with max − min ≥ 49 strictly
    /// increasing (no key press without effect); narrower ranges hit every value from min to max and have
    /// exactly 49 − (max − min) equal neighbours.
    static void test_keyStepTable50_strictlyIncreasing()
    {
        for (int lo = 1; lo < 100; lo++)
        {
            for (int hi = lo + 1; hi <= 100; hi++)
            {
                var t = Config.KeyStepTable(min: lo, max: hi, steps: 50);
                bool ends = t.Length == 50 && t[0] == lo && t[^1] == hi;
                var pairs = t.Zip(t.Skip(1)).ToArray();
                if (hi - lo >= 49)
                {
                    Check(ends && pairs.All(p => p.First < p.Second), $"{lo}-{hi} (50 steps): strictly increasing {S(t)}");
                }
                else
                {
                    bool steps01 = pairs.All(p => p.Second - p.First is 0 or 1);
                    int equal = pairs.Count(p => p.First == p.Second);
                    Check(ends && steps01 && equal == 49 - (hi - lo), $"{lo}-{hi} (50 steps): every value, {equal} equal neighbours (expected {49 - (hi - lo)}) {S(t)}");
                }
            }
        }
    }

    /// MAP-12: the complete overshoot list (cap 100), a cap that blocks the overshoot, and the cap boundary
    /// (an overshoot equal to the cap is allowed; reached with maxSpeakerVolume ≤ 41).
    static void test_golden_approachFromAbove()
    {
        var expected = new Dictionary<int, int>
        {
            [6] = 14, [7] = 15, [8] = 15, [9] = 15, [10] = 16, [11] = 17, [12] = 17, [13] = 18, [14] = 18, [15] = 19,
            [16] = 20, [17] = 20, [18] = 21, [19] = 22, [20] = 22, [21] = 23, [22] = 24, [23] = 25, [24] = 26, [25] = 27,
            [26] = 28, [27] = 28, [28] = 29, [29] = 30, [30] = 31, [31] = 32, [32] = 33, [33] = 34, [34] = 35, [35] = 36,
            [36] = 37, [37] = 38, [38] = 39, [39] = 40, [40] = 41,
        };
        for (int v = -1; v <= 100; v++)
        {
            int? want = expected.TryGetValue(v, out int via) ? via : null;
            int? got = Config.ApproachFromAbove(v, cap: 100);
            Check(got == want, $"approachFromAbove({v}) = {got?.ToString() ?? "none"}, expected {want?.ToString() ?? "none"}");
        }
        Check(Config.ApproachFromAbove(12, cap: 16) == null, "12 with cap 16: no overshoot (17 is above the cap)");
        Check(Config.ApproachFromAbove(15) == 19, "default cap 100");
        foreach (var (v, via) in expected)
        {
            Check(Config.ApproachFromAbove(v, cap: via) == via, $"approachFromAbove({v}, cap: {via}) = {Config.ApproachFromAbove(v, cap: via)?.ToString() ?? "none"}, expected {via} (v + k <= cap)");
            Check(Config.ApproachFromAbove(v, cap: via - 1) == null, $"approachFromAbove({v}, cap: {via - 1}) = {Config.ApproachFromAbove(v, cap: via - 1)?.ToString() ?? "none"}, expected none");
        }
    }

    /// EDGE-01 (A29): a stored maximum of 1…15 without a stored minimum must still give a usable range:
    /// min(15, max − 1). macOS crashes for max 1…11 (minimum 12 above the maximum). Stored values are used and
    /// clamped as on macOS (§8.2): max `v > 0 ? min(v, 100) : 65`, min `v > 0 ? min(v, max − 1) : …`.
    static void test_edge_rangeClampNeverDegenerates()
    {
        Check(Config.MaxSpeakerVolumeFor(0) == 65 && Config.MinSpeakerVolumeFor(0, max: 65) == 15, "defaults 15-65 (A74)");
        Check(Config.MinSpeakerVolumeFor(0, max: 11) == 10 && Config.MinSpeakerVolumeFor(0, max: 1) == 0, "unset minimum below the maximum");
        Check(Config.MaxSpeakerVolumeFor(80) == 80 && Config.MaxSpeakerVolumeFor(100) == 100 && Config.MaxSpeakerVolumeFor(150) == 100 && Config.MaxSpeakerVolumeFor(1) == 1, "stored maximum used, at most 100");
        Check(Config.MinSpeakerVolumeFor(20, max: 65) == 20 && Config.MinSpeakerVolumeFor(70, max: 65) == 64 && Config.MinSpeakerVolumeFor(1, max: 1) == 0, "stored minimum used, below the maximum");
        int[] raws = [.. Enumerable.Range(-2, 125), int.MinValue, int.MaxValue];
        foreach (int rawMax in raws)
        {
            int max = Config.MaxSpeakerVolumeFor(rawMax);
            Check(max >= 1 && max <= 100 && max == (rawMax > 0 ? Math.Min(rawMax, 100) : 65), $"max {rawMax} -> {max}");
            foreach (int rawMin in raws)
            {
                int min = Config.MinSpeakerVolumeFor(rawMin, max: max);
                if (!(min >= 0 && min < max && min == (rawMin > 0 ? Math.Min(rawMin, max - 1) : Math.Min(15, max - 1))))
                {
                    Check(false, $"min {rawMin} with max {max} -> {min}");
                    continue;
                }
                var t = Config.KeyStepTable(min: min, max: max);
                Check(t.Length == 50 && t[0] == min && t[^1] == max && Monotone(t), $"{min}-{max}: table {S(t)}");
            }
        }
    }

    /// Windows names of the identifiers (docs/DESIGN.md §8.1, §9, §10.1, §10.9, §4).
    static void test_windowsIdentifiers()
    {
        Check(appName == "LSX II Volume Fixer" && appSlug == "LSXIIVolumeFixer" && appVersion == "1.0", "app name, slug, version");
        Check(runKeyPath == @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run" && runValueName == "io.github.roberdorfer.lsxiivolumefixer", "Run value");
        Check(mutexName == @"Local\io.github.roberdorfer.lsxiivolumefixer", "per-session mutex");
        Check(userKeyPath == @"Software\io.github.roberdorfer.lsxiivolumefixer", "HKCU settings key");
        Check(machineKeyPath == @"SOFTWARE\io.github.roberdorfer.lsxiivolumefixer", "HKLM settings key");
        Check(stateKeyPath == @"SOFTWARE\io.github.roberdorfer.lsxiivolumefixer\State", "HKLM state key");
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Check(logURL == local + @"\LSXIIVolumeFixer\LSXIIVolumeFixer.log", $"log file {logURL}");
        Check(realModelMatch == "VID_27AC&PID_1060", "real device match");
        Check(Config.settingKeys.SequenceEqual(Config.userKeys.Take(7)) && Config.userKeys.Length == 8 && Config.userKeys[7] == "defaultOutputSetFor", "userKeys = settingKeys + defaultOutputSetFor");
        Check(Config.boolKeys.SequenceEqual(["autoSwitchDefault", "autoWakeOnPlayback", "showVolumeInMenuBar"]), "boolKeys");
    }

    /// The pure decision behind IsConsoleSession() (docs/DESIGN.md §5.14, A57).
    static void test_isConsoleDecision()
    {
        Check(IsConsoleDecision(1, 1, last: false), "own session owns the console");
        Check(!IsConsoleDecision(2, 1, last: true), "another session owns the console");
        Check(IsConsoleDecision(0xFFFFFFFF, 1, last: true), "console being reattached keeps the last state (true)");
        Check(!IsConsoleDecision(0xFFFFFFFF, 1, last: false), "console being reattached keeps the last state (false)");
        Check(!IsConsoleDecision(1, 0, last: true), "session 0 (services) does not own console session 1");
    }

    /// The stable UID prefix of the input restore (docs/DESIGN.md §5.13, A25; ≙ StatusMenu.uidPrefix): USB instance IDs lose
    /// their last segment (port or serial number), every other ID stays whole.
    static void test_uidPrefix_windows()
    {
        Check(StatusMenu.UidPrefix(@"USB\VID_1234&PID_5678&MI_00\A&5E6F7A8B&0&0000") == @"USB\VID_1234&PID_5678&MI_00", "USB: the port segment is cut off (a USB microphone)");
        Check(StatusMenu.UidPrefix(@"USB\VID_27AC&PID_1060&MI_01\8&1A2B3C4D&0&0001") == @"USB\VID_27AC&PID_1060&MI_01", "USB: the LSX II (later the defaultOutputSetFor marker, 5.12)");
        Check(StatusMenu.UidPrefix(@"USB\VID_ABCD&PID_0001\00000000ABCD") == @"USB\VID_ABCD&PID_0001", "USB without interface number: the serial number is cut off");
        Check(StatusMenu.UidPrefix(@"usb\vid_1234&pid_5678&mi_00\a&5e6f7a8b&0&0000") == @"usb\vid_1234&pid_5678&mi_00", "the USB enumerator ignores case");
        string[] whole =
        [
            @"HDAUDIO\FUNC_01&VEN_1234&DEV_5678&SUBSYS_00000000&REV_1000\4&11111111&0&0001",
            @"BTHENUM\{0000111E-0000-1000-8000-00805F9B34FB}_VID&0001ABCD_PID&1234\7&22222222&0&A0B1C2D3E4F5_C00000000",
            @"BTHHFENUM\BthHFPAudio\8&33333333&0&97",
            @"SWD\MMDEVAPI\{0.0.1.00000000}.{0D3E2C1B-1234-4ABC-9DEF-0123456789AB}",
            @"{0.0.1.00000000}.{0d3e2c1b-1234-4abc-9def-0123456789ab}",   // fallback 2 of DeviceUid: the endpoint ID
            @"USB\VID_1234&PID_5678",                                      // two segments (≙ parts.count >= 3)
            @"USBSTOR\DISK&VEN_X\1",                                       // another enumerator that starts with "USB"
            "",
        ];
        foreach (string uid in whole) Check(StatusMenu.UidPrefix(uid) == uid, $"stays whole: {uid}");
        string mic = @"USB\VID_1234&PID_5678&MI_00\A&5E6F7A8B&0&0000";
        Check(mic.StartsWith(StatusMenu.UidPrefix(mic), StringComparison.Ordinal), "the prefix matches its own device (findInput, submenu check mark)");
        Check(!@"USB\VID_1234&PID_5678&MI_01\A&5E6F7A8B&0&0001".StartsWith(StatusMenu.UidPrefix(mic), StringComparison.Ordinal), "another interface of the same USB device does not match");
    }

    /// Reading rules for registry values (docs/DESIGN.md §8.2, R13).
    static void test_settingsParsing()
    {
        Check(RegistryDefaults.AsBool(1) == true && RegistryDefaults.AsBool(0) == false, "REG_DWORD 1/0 are bools");
        Check(RegistryDefaults.AsBool(2) == null && RegistryDefaults.AsBool(-1) == null, "other REG_DWORD values are no bools");
        Check(RegistryDefaults.AsBool("true") == null && RegistryDefaults.AsBool("1") == null && RegistryDefaults.AsBool("false") == null, "REG_SZ is never a bool (as on macOS)");
        Check(RegistryDefaults.AsBool(1L) == null && RegistryDefaults.AsBool(null) == null, "REG_QWORD and missing are no bools");

        Check(RegistryDefaults.IntegerValue(60) == 60 && RegistryDefaults.IntegerValue(-1) == -1, "REG_DWORD numbers");
        Check(RegistryDefaults.IntegerValue("60") == 60 && RegistryDefaults.IntegerValue(" 60 ") == 60 && RegistryDefaults.IntegerValue("+7") == 7, "REG_SZ numbers are parsed");
        Check(RegistryDefaults.IntegerValue("12.7") == 12 && RegistryDefaults.IntegerValue("65abc") == 65, "REG_SZ: leading integer, like NSString.integerValue");
        Check(RegistryDefaults.IntegerValue("abc") == 0 && RegistryDefaults.IntegerValue("") == 0 && RegistryDefaults.IntegerValue(null) == 0, "unreadable or missing -> 0 (default)");
        Check(RegistryDefaults.IntegerValue(new byte[] { 60 }) == 0 && RegistryDefaults.IntegerValue(new[] { "60" }) == 0, "REG_BINARY / REG_MULTI_SZ -> 0");
        Check(RegistryDefaults.IntegerValue(5_000_000_000L) == int.MaxValue && RegistryDefaults.IntegerValue("99999999999") == int.MaxValue, "too large numbers saturate");

        var saved = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");   // decimal comma: the parse must not depend on it
        try
        {
            Check(RegistryDefaults.DoubleValue("1.5") == 1.5 && RegistryDefaults.DoubleValue(" 0.25") == 0.25, "pollInterval REG_SZ is invariant");
            Check(RegistryDefaults.DoubleValue("1,5") == 1, "a decimal comma ends the number");
            Check(RegistryDefaults.DoubleValue(2) == 2 && RegistryDefaults.DoubleValue("abc") == 0 && RegistryDefaults.DoubleValue(null) == 0, "REG_DWORD, unreadable, missing");
            Check(RegistryDefaults.StringValue(5) == "5" && RegistryDefaults.StringValue("x") == "x" && RegistryDefaults.StringValue(new[] { "x" }) == null, "strings, numbers as text");
            var d = new DateTime(2026, 10, 17, 9, 41, 7, 123, DateTimeKind.Utc);
            Check(RegistryDefaults.DateString(d) == "2026-10-17T09:41:07.123Z" && RegistryDefaults.DateValue(RegistryDefaults.DateString(d)) == d, "dates as ISO 8601 UTC");
            Check(DateTime.UtcNow.ToString(iso8601Format, CultureInfo.InvariantCulture).Length == 24, "log timestamp yyyy-MM-ddTHH:mm:ss.fffZ");
        }
        finally { CultureInfo.CurrentCulture = saved; }
    }

    /// The launcher's KeepAlive throttle (docs/DESIGN.md §10.9, ≙ ThrottleInterval 5): the next start comes at the earliest
    /// 5 s after the last one, whatever the exit code; the log line only for exit codes other than 0.
    static void test_launcherThrottle()
    {
        Check(Launcher.throttleInterval == TimeSpan.FromSeconds(5), "throttle interval 5 s");
        Check(Launcher.ThrottleDelay(TimeSpan.Zero) == TimeSpan.FromSeconds(5), "immediate end: wait 5 s");
        Check(Launcher.ThrottleDelay(TimeSpan.FromMilliseconds(300)) == TimeSpan.FromMilliseconds(4700), "end after 0.3 s: wait 4.7 s");
        Check(Launcher.ThrottleDelay(TimeSpan.FromSeconds(4.999)) == TimeSpan.FromMilliseconds(1), "end after 4.999 s: wait 1 ms");
        Check(Launcher.ThrottleDelay(TimeSpan.FromSeconds(5)) == TimeSpan.Zero, "end after 5 s: start at once");
        Check(Launcher.ThrottleDelay(TimeSpan.FromMinutes(10)) == TimeSpan.Zero, "slow end (10 min): start at once");
        Check(Launcher.ThrottleDelay(TimeSpan.FromSeconds(-1)) == TimeSpan.FromSeconds(5), "negative run time: wait 5 s, never longer");
        Check(Launcher.ExitLogLine(0, TimeSpan.FromSeconds(4)) == null, "exit 0 (Restart, self-relaunch, WM_CLOSE): no launcher log line");
        Check(Launcher.ExitLogLine(-1, TimeSpan.FromMilliseconds(4730)) == "launcher: agent exited (-1), relaunching in 4.7 s", "exit -1 (Stop-Process) log line");
        Check(Launcher.ExitLogLine(1, TimeSpan.Zero) == "launcher: agent exited (1), relaunching in 0.0 s", "exit 1 (Task Manager) after a slow end");
    }

    /// (M6) The installId check of agent and launcher (docs/DESIGN.md §10.4, §10.7, §10.9): a new value hands over, a missing
    /// one ends, no value at start and now (a run from build\ without the setup) changes nothing; the handover starts the
    /// fixed install path when it exists.
    static void test_installIdChange()
    {
        const string a = "0f1e2d3c-0000-4000-8000-000000000001", b = "0f1e2d3c-0000-4000-8000-000000000002";
        Check(Launcher.InstallIdChange(a, a) == Launcher.InstallChange.None, "same installId: nothing");
        Check(Launcher.InstallIdChange(a, b) == Launcher.InstallChange.Updated, "new installId: update handover");
        Check(Launcher.InstallIdChange(a, null) == Launcher.InstallChange.Uninstalled, "installId gone: uninstalled");
        Check(Launcher.InstallIdChange(null, a) == Launcher.InstallChange.Updated, "installId appears (install over a build\\ run): handover");
        Check(Launcher.InstallIdChange(null, null) == Launcher.InstallChange.None, "no installId at start and now: nothing");
        Check(Launcher.InstallIdChange(a, a.ToUpperInvariant()) == Launcher.InstallChange.Updated, "comparison is ordinal");
        Check(installedExePath.EndsWith(@"\LSXIIVolumeFixer\LSXIIVolumeFixer.exe", StringComparison.Ordinal)
              && Path.IsPathFullyQualified(installedExePath), "installed exe path: <Program Files>\\LSXIIVolumeFixer\\LSXIIVolumeFixer.exe");
        Check(Launcher.HandoverTarget(@"C:\src\build\LSXIIVolumeFixer.exe", true) == installedExePath, "handover: installed exe when present");
        Check(Launcher.HandoverTarget(@"C:\src\build\LSXIIVolumeFixer.exe", false) == @"C:\src\build\LSXIIVolumeFixer.exe", "handover: own exe without an installation");
    }

    /// The default-output state machine with the persisted restore flag and Plan B (docs/DESIGN.md §5.11, §5.12, A84): the
    /// start rule, a deliberate choice cancelling the restore, re-evaluations without a new choice, the restore on
    /// return with and without IPolicyConfig.
    static void test_defaultOutputPlanB()
    {
        const string lsx = "{0.0.0.00000000}.{lsx}", builtin = "{0.0.0.00000000}.{builtin}", phones = "{0.0.0.00000000}.{phones}";
        const string marker = @"USB\VID_27AC&PID_1060&MI_01";
        static string P(bool[] p) => string.Concat(p.Select(x => x ? '1' : '0'));

        // Start rule.
        Check(P(Config.RestorePendingAtStart(null, null, realPresent: false)) == "111", "fresh install without USB: pending for every role (first run can bring the LSX II in)");
        Check(P(Config.RestorePendingAtStart(null, "", realPresent: false)) == "111", "empty marker counts as none");
        Check(P(Config.RestorePendingAtStart(null, null, realPresent: true)) == "000", "fresh install with the LSX II on USB: nothing pending (first run sets it)");
        Check(P(Config.RestorePendingAtStart(null, marker, realPresent: false)) == "000", "no stored flag but marker: nothing pending (no forced restore)");
        Check(P(Config.RestorePendingAtStart(0, null, realPresent: false)) == "000", "stored 0 (deliberate choice earlier) wins over the fresh-install rule");
        Check(P(Config.RestorePendingAtStart(7, marker, realPresent: false)) == "111", "stored 7: pending for every role");
        Check(P(Config.RestorePendingAtStart(5, marker, realPresent: true)) == "101", "stored 5: console and communications");
        Check(Config.RestorePendingMask([true, false, true]) == 5 && Config.RestorePendingMask([false, false, false]) == 0
              && Config.RestorePendingMask(Config.RestorePendingAtStart(6, null, false)) == 6, "mask round-trip");
        Check(Config.restorePendingKey == "restoreOutputOnReturn" && !Config.userKeys.Contains(Config.restorePendingKey)
              && !Config.settingKeys.Contains(Config.restorePendingKey), "the flag is state, not a setting");

        // Start without USB, flag stored: the first evaluation keeps it, without a "left" line; Plan B is active.
        var r = Config.DefaultOutputStep(builtin, null, realPresent: false, isDefault: false, restoreOnReturn: true, substituteKnown: false, substitute: null);
        Check(r.Branch == Config.DefaultOutputBranch.StillPending && r.RestoreOnReturn && !r.IsDefault && r.SubstituteKnown && r.Substitute == builtin,
              "start without USB, flag stored: first evaluation keeps the restore, no 'left' line");
        Check(Config.PlanBActive(realPresent: false, autoSwitchDefault: true, [false, r.RestoreOnReturn, false]), "Plan B active while pending");
        Check(!Config.PlanBActive(realPresent: false, autoSwitchDefault: false, [true, true, true]), "Plan B off with autoSwitchDefault = false");
        Check(!Config.PlanBActive(realPresent: true, autoSwitchDefault: true, [true, true, true]), "Plan B off while the LSX II is on USB");
        // Start without USB, no flag (nothing pending): the first evaluation is the user's state, nothing forced.
        var n = Config.DefaultOutputStep(phones, null, realPresent: false, isDefault: false, restoreOnReturn: false, substituteKnown: false, substitute: null);
        Check(n.Branch == Config.DefaultOutputBranch.UserChoice && !n.RestoreOnReturn, "start without USB, nothing pending: the default stays the user's");
        Check(!Config.PlanBActive(realPresent: false, autoSwitchDefault: true, [false, false, false]), "Plan B off when nothing is pending");

        // A re-evaluation with the same substitute (settings change, console regained) keeps it; a new device cancels it.
        var again = Config.DefaultOutputStep(builtin, null, false, r.IsDefault, r.RestoreOnReturn, r.SubstituteKnown, r.Substitute);
        Check(again.Branch == Config.DefaultOutputBranch.StillPending && again.RestoreOnReturn, "re-evaluation without a new choice keeps the restore");
        var choice = Config.DefaultOutputStep(phones, null, false, again.IsDefault, again.RestoreOnReturn, again.SubstituteKnown, again.Substitute);
        Check(choice.Branch == Config.DefaultOutputBranch.UserChoice && !choice.RestoreOnReturn && !choice.SubstituteKnown,
              "deliberate choice (headphones) while the LSX II is away cancels the restore");
        Check(Config.RestorePendingMask([choice.RestoreOnReturn, choice.RestoreOnReturn, choice.RestoreOnReturn]) == 0
              && P(Config.RestorePendingAtStart(0, marker, false)) == "000", "the cancelled restore is stored as 0 and not revived at the next start");

        // The macOS sequence: default (b), USB gone (c, logged), choice (d).
        var b = Config.DefaultOutputStep(lsx, lsx, realPresent: true, isDefault: false, restoreOnReturn: true, substituteKnown: true, substitute: builtin);
        Check(b.Branch == Config.DefaultOutputBranch.IsTarget && b.IsDefault && !b.RestoreOnReturn && !b.SubstituteKnown, "(b) LSX II is the default: restore cleared");
        var c = Config.DefaultOutputStep(builtin, null, realPresent: false, b.IsDefault, b.RestoreOnReturn, b.SubstituteKnown, b.Substitute);
        Check(c.Branch == Config.DefaultOutputBranch.LeftWhileAbsent && c.RestoreOnReturn && c.Substitute == builtin, "(c) LSX II gone: restore pending, logged");
        var cNone = Config.DefaultOutputStep(null, null, realPresent: false, isDefault: true, restoreOnReturn: false, substituteKnown: false, substitute: null);
        Check(cNone.Branch == Config.DefaultOutputBranch.LeftWhileAbsent && cNone.SubstituteKnown && cNone.Substitute == null, "(c) with no endpoint left (none)");
        Check(Config.DefaultOutputStep(null, null, false, cNone.IsDefault, cNone.RestoreOnReturn, cNone.SubstituteKnown, cNone.Substitute).Branch == Config.DefaultOutputBranch.StillPending,
              "still none: pending");
        var d = Config.DefaultOutputStep(phones, lsx, realPresent: true, isDefault: false, restoreOnReturn: true, substituteKnown: true, substitute: builtin);
        Check(d.Branch == Config.DefaultOutputBranch.UserChoice && !d.RestoreOnReturn, "(d) LSX II present but another default: the user's choice");
        var notChosen = Config.DefaultOutputStep(phones, null, realPresent: false, isDefault: false, restoreOnReturn: false, substituteKnown: false, substitute: null);
        Check(notChosen.Branch == Config.DefaultOutputBranch.UserChoice, "(d) nothing pending, LSX II away: stays the user's choice");

        // Restore on return.
        Check(Config.RestoreOnReturn(pending: true, policyConfigAvailable: true, alreadyDefault: false) == (true, false), "pending: set the LSX II as default, clear");
        Check(Config.RestoreOnReturn(true, true, alreadyDefault: true) == (false, false), "pending but already default (Windows did it): clear only");
        Check(Config.RestoreOnReturn(false, true, false) == (false, false), "not pending (deliberate choice): nothing");
        Check(Config.RestoreOnReturn(true, policyConfigAvailable: false, alreadyDefault: false) == (false, true), "IPolicyConfig unavailable: no set, flag kept");
        var windowsRestored = Config.DefaultOutputStep(lsx, lsx, realPresent: true, isDefault: false, restoreOnReturn: true, substituteKnown: true, substitute: builtin);
        Check(windowsRestored.Branch == Config.DefaultOutputBranch.IsTarget && !windowsRestored.RestoreOnReturn, "IPolicyConfig unavailable: Windows' own restore clears the flag via (b)");
    }

    /// The auto-wake with the limited re-select of `usb` (docs/DESIGN.md §5.11, A84): standby as on macOS; `usb` without the
    /// LSX II once per loss episode, then at the earliest 10 s + 60 s after a failed attempt; other sources never.
    static void test_autoWakeReselect()
    {
        DateTime t0 = new(2026, 10, 23, 18, 0, 0, DateTimeKind.Utc);
        TimeSpan S(double s) => TimeSpan.FromSeconds(s);
        Check(Config.usbReselectVerdict == S(10) && Config.usbReselectBackoff >= S(60), "10 s verdict, back-off at least 60 s");
        Check(Config.AutoWakeAction("standby", realPresent: false, null, t0) == Config.WakeAction.WakeFromStandby, "standby wakes (macOS)");
        Check(Config.AutoWakeAction("standby", realPresent: true, t0, t0 + S(1)) == Config.WakeAction.WakeFromStandby, "standby wake is never limited");
        Check(Config.AutoWakeAction("usb", realPresent: false, null, t0) == Config.WakeAction.ReselectUsb, "usb without the LSX II: re-select (first in the episode)");
        Check(Config.AutoWakeAction("usb", realPresent: true, null, t0) == Config.WakeAction.None, "usb with the LSX II on USB: nothing");
        foreach (string src in new[] { "wifi", "bluetooth", "tv", "optical", "analog" })
            Check(Config.AutoWakeAction(src, realPresent: false, null, t0) == Config.WakeAction.None, $"{src}: never switched");
        Check(Config.AutoWakeAction(null, realPresent: false, null, t0) == Config.WakeAction.None, "unknown source: nothing");
        Check(Config.AutoWakeAction("usb", false, t0, t0 + S(5)) == Config.WakeAction.None, "second playback start within the episode (5 s): no re-select");
        Check(Config.AutoWakeAction("usb", false, t0, t0 + S(30)) == Config.WakeAction.None, "after a failed attempt (30 s): backing off");
        Check(Config.AutoWakeAction("usb", false, t0, t0 + S(69.9)) == Config.WakeAction.None, "69.9 s: still backing off");
        Check(Config.AutoWakeAction("usb", false, t0, t0 + S(70)) == Config.WakeAction.ReselectUsb, "70 s (10 s verdict + 60 s): next attempt");
        // The endpoint came back: the controller clears lastReselect, so the next loss episode re-selects at once.
        Check(Config.AutoWakeAction("usb", false, null, t0 + S(20)) == Config.WakeAction.ReselectUsb, "re-armed after the endpoint returned");
    }

    /// The push on "real device reappeared" (docs/DESIGN.md §5.6, A85): skipped when the speaker already has the PC's value.
    static void test_reappearPush()
    {
        Check(!Config.ReappearPushNeeded(20, 20), "speaker already at the target: no push (no overshoot blip)");
        Check(Config.ReappearPushNeeded(53, 20), "speaker reset to another value: push");
        Check(Config.ReappearPushNeeded(21, 20), "one off: push");
        Check(Config.ReappearPushNeeded(null, 20), "GET failed: push as on macOS");
        Check(!Config.ReappearPushNeeded(0, 0), "PC at 0, speaker at 0: no push");
    }

    /// The admin script of "Apply these settings for all users…" (docs/DESIGN.md §8.4): the effective value of every
    /// settingKeys entry, types like macOS, quotes removed, unset values deleted; the key created only when missing, never
    /// with -Force; State and installId never named.
    static void test_applyForAllUsersScript()
    {
        var values = new Dictionary<string, object?>
        {
            ["minSpeakerVolume"] = 20,                       // REG_DWORD → DWord
            ["maxSpeakerVolume"] = "60",                     // REG_SZ number → string (≙ `v as? Int` fails for a string)
            ["autoSwitchDefault"] = 0,                       // bool false
            ["showVolumeInMenuBar"] = "false",               // no REG_DWORD 0/1 → true (≙ `v as? Bool ?? true`)
            ["restoreInputUIDPrefix"] = "USB\\VID_1234&PID_5678&MI_00 it's ‘x’ ’y‚ ‛z",
            ["defaultOutputSetFor"] = "USB\\VID_27AC&PID_1060&MI_01",   // userKeys only: not copied
        };
        string s = StatusMenu.AdminScriptForAllUsers(k => values.GetValueOrDefault(k));
        string[] lines = s.Split("\r\n");
        Check(s.Contains("$k = 'HKLM:\\SOFTWARE\\io.github.roberdorfer.lsxiivolumefixer'\r\n"), "target key HKLM 64-bit path");
        Check(s.Contains("if (-not (Test-Path -LiteralPath $k)) { New-Item -Path $k | Out-Null }"), "key created only when missing");
        Check(!s.Contains("-Force") && !s.Contains("State") && !s.Contains("installId") && !s.Contains("Remove-Item ") && !s.Contains("defaultOutputSetFor"), "never -Force, State, installId, Remove-Item or defaultOutputSetFor");
        Check(lines.Contains("    Remove-ItemProperty -LiteralPath $k -Name 'speakerHost' -ErrorAction SilentlyContinue"), "unset speakerHost deleted");
        Check(lines.Contains("    Remove-ItemProperty -LiteralPath $k -Name 'autoWakeOnPlayback' -ErrorAction SilentlyContinue"), "unset autoWakeOnPlayback deleted");
        Check(lines.Contains("    Set-ItemProperty -LiteralPath $k -Name 'minSpeakerVolume' -Value 20 -Type DWord"), "number as DWord");
        Check(lines.Contains("    Set-ItemProperty -LiteralPath $k -Name 'maxSpeakerVolume' -Value '60' -Type String"), "string number as String");
        Check(lines.Contains("    Set-ItemProperty -LiteralPath $k -Name 'autoSwitchDefault' -Value 0 -Type DWord"), "bool false as DWord 0");
        Check(lines.Contains("    Set-ItemProperty -LiteralPath $k -Name 'showVolumeInMenuBar' -Value 1 -Type DWord"), "non-bool value of a bool key as true");
        Check(lines.Contains("    Set-ItemProperty -LiteralPath $k -Name 'restoreInputUIDPrefix' -Value 'USB\\VID_1234&PID_5678&MI_00 its x y z' -Type String"), "quotes (also typographic) removed");
        // Order of settingKeys, one line each.
        int[] at = [.. Config.settingKeys.Select(k => Array.FindIndex(lines, l => l.Contains($"-Name '{k}'")))];
        Check(at.All(i => i > 0) && at.Zip(at.Skip(1)).All(p => p.First < p.Second), "one line per settingKeys entry, in order");
        Check(s.StartsWith("# LSX II Volume Fixer: write these settings as the default for all users\r\n$ErrorActionPreference = 'Stop'\r\ntry {\r\n"), "header");
        Check(s.EndsWith("    exit 0\r\n} catch {\r\n    exit 1\r\n}\r\n"), "exit 0, or 1 on any error");
    }

    /// The key rule of docs/DESIGN.md §5.1 (M4) with the default range 15–65: 50 presses up from 0 + mute give every table
    /// value once (15 16 … 63 65, §16 M4), down to 0 mutes, the mute key only toggles; plus the State\volume format.
    static void test_volumeKeys50()
    {
        int[] t = Config.KeyStepTable(min: 15, max: 65);
        float s = 0; bool muted = true;
        var seen = new List<int>();
        for (int k = 1; k <= 50; k++)
        {
            (s, muted) = Config.ApplyVolumeKey(VolumeKey.Up, s, muted);
            seen.Add(Config.SpeakerVolume(forScalar: s, min: 15, max: 65));
            Check(!muted, $"up {k} lifts the mute");
        }
        Check(seen.SequenceEqual(t), $"50 presses up from mute give the table {S(t)}, got {S(seen)}");
        Check(seen[0] == 15 && seen[^1] == 65 && seen[^2] == 63, "first press 15, last two 63 65");
        (s, muted) = Config.ApplyVolumeKey(VolumeKey.Up, s, muted);
        Check(s == 1 && Config.SpeakerVolume(forScalar: s, min: 15, max: 65) == 65, "up at the top stays at 1 (65)");
        for (int k = 49; k >= 1; k--)
        {
            (s, muted) = Config.ApplyVolumeKey(VolumeKey.Down, s, muted);
            Check(Config.SpeakerVolume(forScalar: s, min: 15, max: 65) == t[k - 1] && !muted, $"down to step {k} = {t[k - 1]}");
        }
        (s, muted) = Config.ApplyVolumeKey(VolumeKey.Down, s, muted);
        Check(s == 0 && muted && Config.SpeakerVolume(forScalar: s, min: 15, max: 65) == 0, "down from the first step: 0 + mute (speaker 0)");
        (s, muted) = Config.ApplyVolumeKey(VolumeKey.Down, s, muted);
        Check(s == 0 && muted, "down at 0 stays 0 + mute");
        Check(Config.ApplyVolumeKey(VolumeKey.Mute, 0.5f, false) == (0.5f, true) && Config.ApplyVolumeKey(VolumeKey.Mute, 0.5f, true) == (0.5f, false), "the mute key toggles the mute and keeps s");
        Check(Config.ApplyVolumeKey(VolumeKey.Up, 0.5f, true) == (0.52f, false), "up while muted lifts the mute and steps");
        Check(Config.ApplyVolumeKey(VolumeKey.Down, 0.5f, true) == (0.48f, false), "down while muted lifts the mute and steps");

        Check(Config.StoredVolume(null) == 0.5f && Config.StoredVolume("abc") == 0.5f && Config.StoredVolume(5) == 0.5f, "State\\volume: missing, unreadable or not REG_SZ -> 0.5");
        Check(Config.StoredVolume("0.25") == 0.25f && Config.StoredVolume("1.5") == 1f && Config.StoredVolume("-1") == 0f, "State\\volume is parsed and clamped to 0...1");
        var saved = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            float x = 0.02f * 7;
            Check(Config.StoredVolumeText(x) == x.ToString("R", CultureInfo.InvariantCulture) && !Config.StoredVolumeText(x).Contains(','), "State\\volume is written invariant");
            Check(Config.StoredVolume(Config.StoredVolumeText(x)) == x, "State\\volume round-trips the Float32");
        }
        finally { CultureInfo.CurrentCulture = saved; }
    }
}
