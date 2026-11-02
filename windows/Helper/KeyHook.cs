// The volume keys (deviation v1, docs/DESIGN.md §6.2, A1): a low-level keyboard hook (WH_KEYBOARD_LL) on a thread of its own
// with a message loop, not on the UI thread. macOS has no counterpart: there the keys act on the proxy device's volume.
//
// - The callback reads only vkCode, flags, dwExtraInfo, the state of Alt and Win and the cached flag `interceptActive`.
//   For VK_VOLUME_UP/DOWN/MUTE while intercepting it hands every key-down (each auto-repeat is one step) to `sink`, which
//   only enqueues on the controller queue, and returns 1: key-down and key-up are swallowed. No logging, no COM, no
//   network in the callback (Windows removes a hook whose callback is slow).
// - interceptActive = onConsole && LSX II endpoint present && LSX II default for eConsole or eMultimedia; the controller
//   sets it (volatile) on every default-device, device and session event (§6.2). Otherwise the key goes to Windows.
// - Modifiers do not change the step (A7). Mask key (Addition): when a volume key is swallowed while Alt or Win is held,
//   Windows would see a lone press of the modifier (Win opens Start on release, Alt activates the menu bar); one vkE8
//   (unassigned) down/up is injected with SendInput and the own dwExtraInfo, and the hook lets it through.
// - Renewal (Addition, A66): Windows silently removes a hook whose callback took too long. The hook is installed again on
//   unlock, console change, resume and every 60 s: first the new one, then the old one is removed, without a gap. The log
//   line `keyboard hook installed (<reason>)` only for the first install and the event-driven renewals.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

static partial class KeyHook
{
    const int WH_KEYBOARD_LL = 13;
    const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
    const int WM_USER = 0x0400, WM_APP = 0x8000;
    const int WM_APP_REINSTALL = WM_APP + 1;
    const uint LLKHF_INJECTED = 0x10;
    const uint VK_VOLUME_MUTE = 0xAD, VK_VOLUME_DOWN = 0xAE, VK_VOLUME_UP = 0xAF;
    const int VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    const ushort VK_MASK = 0xE8;   // unassigned (§6.2)
    /// dwExtraInfo of the injected mask key ("LSX2"); injected events with it pass the hook.
    const nuint maskMarker = 0x4C535832;
    /// Renewal period (A66, A70).
    static readonly TimeSpan renewEvery = TimeSpan.FromSeconds(60);

    static volatile bool interceptActive;
    static volatile Action<VolumeKey>? sink;
    static nint hook;              // hook thread only
    static uint threadId;
    static Thread? thread;
    static System.Threading.Timer? renewTimer;
    /// Reasons of the queued renewals, one per WM_APP_REINSTALL; null = periodic (not logged).
    static readonly ConcurrentQueue<string?> reasons = new();

    /// Set by the controller (§6.2); read by the callback.
    public static bool InterceptActive
    {
        get => interceptActive;
        set => interceptActive = value;
    }

    /// Starts the hook thread and installs the hook (log `keyboard hook installed (start)`). `onKey` runs on the hook
    /// thread and must only enqueue.
    public static void Start(Action<VolumeKey> onKey)
    {
        if (thread != null) return;
        sink = onKey;
        using var ready = new ManualResetEventSlim();
        thread = new Thread(() => Run(ready)) { Name = "keyboard hook", IsBackground = true };
        thread.Start();
        ready.Wait();
        renewTimer = new System.Threading.Timer(_ => Reinstall(null), null, renewEvery, renewEvery);
    }

    /// Installs the hook again on the hook thread, new before old (A66). `reason` null: the 60-s renewal (no log line).
    public static void Reinstall(string? reason)
    {
        if (thread == null) return;
        reasons.Enqueue(reason);
        _ = PostThreadMessageW(threadId, WM_APP_REINSTALL, 0, 0);
    }

    static void Run(ManualResetEventSlim ready)
    {
        // Creates the thread's message queue before anybody posts to it.
        _ = PeekMessageW(out MSG msg, 0, WM_USER, WM_USER, 0);
        threadId = GetCurrentThreadId();
        Install("start");
        ready.Set();
        while (GetMessageW(out msg, 0, 0, 0) > 0)
        {
            if (msg.message == WM_APP_REINSTALL && msg.hwnd == 0)
            {
                _ = reasons.TryDequeue(out string? reason);
                Install(reason);
                continue;
            }
            _ = TranslateMessage(msg);
            _ = DispatchMessageW(msg);
        }
    }

    /// Hook thread: install a new hook, then remove the old one.
    static unsafe void Install(string? reason)
    {
        nint h = SetWindowsHookExW(WH_KEYBOARD_LL, &HookProc, GetModuleHandleW(null), 0);
        if (h == 0)
        {
            // Addition (§9): the old hook (if any) stays; the next renewal tries again.
            Log($"keyboard hook install failed ({ErrText(Marshal.GetHRForLastWin32Error())})");
            return;
        }
        if (hook != 0) _ = UnhookWindowsHookEx(hook);
        hook = h;
        if (reason != null) Log($"keyboard hook installed ({reason})");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static unsafe nint HookProc(int nCode, nint wParam, nint lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                var k = (KBDLLHOOKSTRUCT*)lParam;
                bool ownMask = (k->flags & LLKHF_INJECTED) != 0 && k->dwExtraInfo == maskMarker;
                uint vk = k->vkCode;
                if (!ownMask && interceptActive && (vk == VK_VOLUME_UP || vk == VK_VOLUME_DOWN || vk == VK_VOLUME_MUTE))
                {
                    if (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN)
                    {
                        if (Held(VK_MENU) || Held(VK_LWIN) || Held(VK_RWIN)) SendMask();
                        sink?.Invoke(vk == VK_VOLUME_UP ? VolumeKey.Up : vk == VK_VOLUME_DOWN ? VolumeKey.Down : VolumeKey.Mute);
                    }
                    return 1;   // swallowed (key-down and key-up)
                }
            }
        }
        catch (Exception) { }   // nothing may escape into user32; the key then goes on to Windows
        return CallNextHookEx(0, nCode, wParam, lParam);
    }

    static bool Held(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// One vkE8 down/up with the own marker (§6.2).
    static unsafe void SendMask()
    {
        INPUT* inputs = stackalloc INPUT[2];
        inputs[0] = new INPUT { type = 1, ki = new KEYBDINPUT { wVk = VK_MASK, dwExtraInfo = maskMarker } };
        inputs[1] = new INPUT { type = 1, ki = new KEYBDINPUT { wVk = VK_MASK, dwFlags = 2 /* KEYEVENTF_KEYUP */, dwExtraInfo = maskMarker } };
        _ = SendInput(2, inputs, sizeof(INPUT));
    }

    // MARK: Win32 (docs/DESIGN.md §4: the hook's declarations live here)

    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public nuint dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public nuint dwExtraInfo; }

    /// INPUT with the union sized for MOUSEINPUT, the largest member (40 bytes on x64 and ARM64, 28 on x86).
    [StructLayout(LayoutKind.Sequential)]
    struct INPUT
    {
        public uint type;
        public KEYBDINPUT ki;
        readonly long padding;   // MOUSEINPUT is 8 bytes larger than KEYBDINPUT
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public nint hwnd; public uint message; public nint wParam, lParam; public uint time; public int x, y; }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static unsafe partial nint SetWindowsHookExW(int idHook, delegate* unmanaged[Stdcall]<int, nint, nint, nint> lpfn, nint hmod, uint dwThreadId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWindowsHookEx(nint hhk);

    [LibraryImport("user32.dll")]
    private static partial nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int vKey);

    [LibraryImport("user32.dll")]
    private static unsafe partial uint SendInput(uint cInputs, INPUT* pInputs, int cbSize);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string? lpModuleName);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostThreadMessageW(uint idThread, int msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekMessageW(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [LibraryImport("user32.dll")]
    private static partial int GetMessageW(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(in MSG lpMsg);

    [LibraryImport("user32.dll")]
    private static partial nint DispatchMessageW(in MSG lpMsg);
}
