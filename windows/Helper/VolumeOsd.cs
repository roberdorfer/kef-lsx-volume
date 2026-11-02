// The own volume display (deviation v1, docs/DESIGN.md §6.5, A2): the Windows flyout shows the pinned endpoint (100 %), so
// the app brings a display in the style of the Windows 11 volume flyout. Display only, like the macOS HUD: it never takes
// the focus and lets clicks through. Shown only for the app's own keys; changes from the speaker (remote, KEF app) and
// foreign changes stay silent, as `applyToProxy` raises no HUD on macOS.
//
// - Content: speaker glyph (Segoe Fluent Icons E992–E995 by level, E74F for mute) and a horizontal bar with the scalar,
//   one key = 1/50; no number (§6.5).
// - Look: dark/light after `SystemUsesLightTheme`, accent colour through UISettings, high-contrast colours when that is
//   on; no acrylic (an inactive window falls back to a solid colour), so the transparency setting changes nothing. The
//   window is a layered window drawn with per-pixel alpha (UpdateLayeredWindow): rounded corners (8 px, like
//   DWMWCP_ROUND) and the soft shadow are drawn into the bitmap, because DWM does not round a layered window.
// - Behaviour: WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_LAYERED | WS_EX_TRANSPARENT, shown with
//   SW_SHOWNOACTIVATE; bottom centre of the monitor of the primary taskbar (Shell_TrayWnd), above the taskbar's place
//   (also when it hides itself); PerMonitorV2 DPI of that monitor; hidden after 2 s (A70, start value until O15), faded
//   out when Windows animations are on; not shown during an exclusive D3D full screen (QUNS_RUNNING_D3D_FULL_SCREEN).
//   Not on the lock screen or the secure desktop (§6.3): the window lives on the user's desktop.

using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;

/// Colours of one appearance (§6.5).
readonly record struct OsdTheme(Color Background, Color Border, Color Foreground, Color Rail, Color Accent, Color ThumbFill, bool HighContrast)
{
    /// Windows 11 flyout colours: solid fill (no acrylic), 1-px stroke, the slider rail and the accent fill
    /// (AccentLight2 on dark, AccentDark1 on light, as the WinUI slider).
    public static OsdTheme Make(bool light, Color accent) => light
        ? new(Color.FromArgb(0xF9, 0xF9, 0xF9), Color.FromArgb(0x1F, 0, 0, 0), Color.FromArgb(0xE4, 0, 0, 0), Color.FromArgb(0x72, 0, 0, 0), accent, Color.White, false)
        : new(Color.FromArgb(0x2C, 0x2C, 0x2C), Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF), Color.White, Color.FromArgb(0x8B, 0xFF, 0xFF, 0xFF), accent, Color.FromArgb(0x45, 0x45, 0x45), false);

    public static OsdTheme HighContrastColors() =>
        new(SystemColors.Window, SystemColors.WindowText, SystemColors.WindowText, SystemColors.GrayText, SystemColors.Highlight, SystemColors.Window, true);

    /// The current appearance: high contrast, else light/dark after `SystemUsesLightTheme` (taskbar) with the accent.
    public static OsdTheme Current()
    {
        if (SystemInformation.HighContrast) return HighContrastColors();
        bool light = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) is int i && i == 1;
        return Make(light, AccentColor(light));
    }

    /// UISettings accent: AccentDark1 on light, AccentLight2 on dark; the Windows default blue when unreadable.
    internal static Color AccentColor(bool light)
    {
        try
        {
            var c = new Windows.UI.ViewManagement.UISettings().GetColorValue(light
                ? Windows.UI.ViewManagement.UIColorType.AccentDark1
                : Windows.UI.ViewManagement.UIColorType.AccentLight2);
            return Color.FromArgb(c.A, c.R, c.G, c.B);
        }
        catch (Exception) { return light ? Color.FromArgb(0x00, 0x5F, 0xB8) : Color.FromArgb(0x60, 0xCD, 0xFF); }
    }
}

/// The OSD window. Create on the UI thread; Show may be called from any thread (it posts to the UI thread).
sealed partial class VolumeOsd : IDisposable
{
    /// Hidden after this long without a new key (A70; start value, O15).
    public static readonly TimeSpan showFor = TimeSpan.FromSeconds(2);
    // Geometry in device-independent pixels.
    public const int width = 200, height = 48, shadow = 16, corner = 8;

    readonly OsdWindow window = new();
    readonly SynchronizationContext ui;
    readonly System.Windows.Forms.Timer hideTimer = new() { Interval = (int)showFor.TotalMilliseconds };
    readonly System.Windows.Forms.Timer fadeTimer = new() { Interval = 16 };
    Bitmap? frame;
    Point framePos;
    int alpha;
    bool visible;

    public VolumeOsd()
    {
        ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        window.CreateHandle(new CreateParams
        {
            Caption = appName,
            Style = unchecked((int)0x80000000),   // WS_POPUP
            ExStyle = WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
        });
        hideTimer.Tick += (_, _) => { hideTimer.Stop(); Hide(); };
        fadeTimer.Tick += (_, _) => FadeStep();
    }

    /// Shows the scalar `s` (0…1) and the mute state; any thread.
    public void Show(float s, bool muted) => ui.Post(_ => ShowNow(s, muted), null);

    void ShowNow(float s, bool muted)
    {
        if (SHQueryUserNotificationState(out int state) == 0 && state == QUNS_RUNNING_D3D_FULL_SCREEN) return;
        Rectangle area = Placement(out float scale);
        frame?.Dispose();
        frame = Render(s, muted, OsdTheme.Current(), scale);
        framePos = new Point(area.Left + (area.Width - frame.Width) / 2, area.Bottom - frame.Height);
        fadeTimer.Stop();
        alpha = 255;
        Apply();
        if (!visible)
        {
            _ = ShowWindow(window.Handle, SW_SHOWNOACTIVATE);
            visible = true;
        }
        _ = SetWindowPos(window.Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        hideTimer.Stop();
        hideTimer.Start();
    }

    void Hide()
    {
        if (!visible) return;
        if (AnimationsOn()) { fadeTimer.Start(); return; }
        HideNow();
    }

    void FadeStep()
    {
        alpha -= 255 / 9;   // ≈ 150 ms
        if (alpha <= 0) { fadeTimer.Stop(); HideNow(); return; }
        Apply();
    }

    void HideNow()
    {
        _ = ShowWindow(window.Handle, SW_HIDE);
        visible = false;
    }

    static bool AnimationsOn() => !SystemParametersInfoW(SPI_GETCLIENTAREAANIMATION, 0, out int on, 0) || on != 0;

    /// The window's place: the band above the primary taskbar on its monitor (also when the taskbar hides itself:
    /// above its nominal 48 DIP), with a 12-DIP gap; `scale` = that monitor's DPI / 96.
    static Rectangle Placement(out float scale)
    {
        nint tray = FindWindowW("Shell_TrayWnd", null);
        nint mon = tray != 0 ? MonitorFromWindow(tray, MONITOR_DEFAULTTOPRIMARY) : MonitorFromPoint(default, MONITOR_DEFAULTTOPRIMARY);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW(mon, ref mi)) { scale = 1; return Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080); }
        scale = GetDpiForMonitor(mon, 0 /* MDT_EFFECTIVE_DPI */, out uint dpi, out _) == 0 ? dpi / 96f : 1f;
        Rectangle work = mi.rcWork.ToRectangle(), monitor = mi.rcMonitor.ToRectangle();
        int bottom = work.Bottom;
        if (work.Bottom >= monitor.Bottom) bottom = monitor.Bottom - (int)Math.Round(48 * scale);   // auto-hide or no bottom taskbar
        bottom -= (int)Math.Round((12 - shadow) * scale);   // the shadow margin hangs into the gap
        return Rectangle.FromLTRB(work.Left, work.Top, work.Right, bottom);
    }

    unsafe void Apply()
    {
        if (frame == null) return;
        nint screen = GetDC(0);
        nint mem = CreateCompatibleDC(screen);
        // UpdateLayeredWindow wants a premultiplied, top-down 32-bit DIB: GDI+ converts to PARGB, the rows are copied.
        var bi = new BITMAPINFOHEADER { biSize = sizeof(BITMAPINFOHEADER), biWidth = frame.Width, biHeight = -frame.Height, biPlanes = 1, biBitCount = 32 };
        nint bmp = CreateDIBSection(screen, ref bi, 0, out nint bits, 0, 0);
        using (var p = frame.Clone(new Rectangle(Point.Empty, frame.Size), PixelFormat.Format32bppPArgb))
        {
            BitmapData d = p.LockBits(new Rectangle(Point.Empty, p.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                for (int y = 0; y < p.Height; y++)
                    Buffer.MemoryCopy((byte*)d.Scan0 + (long)y * d.Stride, (byte*)bits + (long)y * p.Width * 4, p.Width * 4, p.Width * 4);
            }
            finally { p.UnlockBits(d); }
        }
        nint old = SelectObject(mem, bmp);
        try
        {
            var size = new SIZE { cx = frame.Width, cy = frame.Height };
            var pos = new POINT { x = framePos.X, y = framePos.Y };
            var src = new POINT();
            var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = (byte)Math.Clamp(alpha, 0, 255), AlphaFormat = 1 /* AC_SRC_ALPHA */ };
            _ = UpdateLayeredWindow(window.Handle, screen, ref pos, ref size, mem, ref src, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            _ = SelectObject(mem, old);
            _ = DeleteObject(bmp);
            _ = DeleteDC(mem);
            _ = ReleaseDC(0, screen);
        }
    }

    /// The OSD as a 32-bit ARGB bitmap with its shadow margin, at `scale` (DPI / 96). Also used by the render harness
    /// (render harness, `osd`; a development tool, not published).
    public static Bitmap Render(float s, bool muted, OsdTheme theme, float scale)
    {
        int S(double v) => (int)Math.Round(v * scale);
        float F(double v) => (float)(v * scale);
        int m = S(shadow), w = S(width), h = S(height);
        var bmp = new Bitmap(w + 2 * m, h + 2 * m, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        var body = new RectangleF(m, m, w, h);

        // Shadow: soft, a little lower than the body (the flyout's elevation); none in high contrast.
        if (!theme.HighContrast)
        {
            const int layers = 10;
            for (int i = layers; i >= 1; i--)
            {
                float grow = F(i * shadow / (double)layers * 0.9);
                var r = RectangleF.Inflate(body, grow, grow);
                r.Offset(0, F(4));
                using var p = Rounded(r, F(corner) + grow);
                using var b = new SolidBrush(Color.FromArgb((int)(10 * (1 - (i - 1) / (double)layers)), 0, 0, 0));
                g.FillPath(b, p);
            }
        }
        using (var p = Rounded(body, F(corner)))
        {
            using var b = new SolidBrush(theme.Background);
            g.FillPath(b, p);
            using var pen = new Pen(theme.Border, Math.Max(1, F(1)));
            g.DrawPath(pen, p);
        }

        // Glyph: E74F mute, else E992 (0) … E995 (loud), 16 DIP, at the flyout's left padding.
        char glyph = muted ? '' : s <= 0 ? '' : s < 1 / 3f ? '' : s < 2 / 3f ? '' : '';
        using (var font = new Font(IconFontFamily(), F(16), GraphicsUnit.Pixel))
        using (var b = new SolidBrush(theme.Foreground))
        {
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            var fmt = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(glyph.ToString(), font, b, new RectangleF(m + F(12), m, F(24), h), fmt);
        }

        // Bar: rail 4 DIP, accent fill up to s, thumb (20 DIP ring, 12 DIP accent dot) like the WinUI slider.
        float x0 = m + F(52), x1 = m + w - F(20), cy = m + h / 2f;
        float frac = Math.Clamp(s, 0, 1), xv = x0 + frac * (x1 - x0);
        using (var rail = new Pen(theme.Rail, F(4)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(rail, x0, cy, x1, cy);
        if (xv > x0)
        {
            using var fill = new Pen(muted && !theme.HighContrast ? theme.Rail : theme.Accent, F(4)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(fill, x0, cy, xv, cy);
        }
        float ro = F(10), ri = F(6);
        using (var ring = new SolidBrush(theme.ThumbFill)) g.FillEllipse(ring, xv - ro, cy - ro, 2 * ro, 2 * ro);
        using (var edge = new Pen(theme.Border, Math.Max(1, F(1)))) g.DrawEllipse(edge, xv - ro, cy - ro, 2 * ro, 2 * ro);
        using (var dot = new SolidBrush(muted && !theme.HighContrast ? theme.Rail : theme.Accent)) g.FillEllipse(dot, xv - ri, cy - ri, 2 * ri, 2 * ri);
        return bmp;
    }

    /// Segoe Fluent Icons (Windows 11), else Segoe MDL2 Assets (same code points).
    static FontFamily IconFontFamily()
    {
        using var installed = new InstalledFontCollection();
        foreach (string name in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
            if (installed.Families.Any(f => f.Name == name)) return new FontFamily(name);
        return SystemFonts.MessageBoxFont?.FontFamily ?? FontFamily.GenericSansSerif;
    }

    static GraphicsPath Rounded(RectangleF r, float radius)
    {
        float d = Math.Min(2 * radius, Math.Min(r.Width, r.Height));
        var p = new GraphicsPath();
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// The window's extended styles, for the render harness's check.
    internal int ExStyle => GetWindowLongW(window.Handle, GWL_EXSTYLE);
    internal nint Handle => window.Handle;

    public void Dispose()
    {
        hideTimer.Dispose();
        fadeTimer.Dispose();
        frame?.Dispose();
        window.DestroyHandle();
    }

    /// The bare window: no painting of its own (UpdateLayeredWindow), never activated.
    sealed class OsdWindow : NativeWindow
    {
        const int WM_MOUSEACTIVATE = 0x0021, MA_NOACTIVATE = 3, WM_NCHITTEST = 0x0084, HTTRANSPARENT = -1;
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEACTIVATE) { m.Result = MA_NOACTIVATE; return; }
            if (m.Msg == WM_NCHITTEST) { m.Result = HTTRANSPARENT; return; }
            base.WndProc(ref m);
        }
    }

    // MARK: Win32 (docs/DESIGN.md §4: DWM/shell declarations of the OSD live here)

    internal const int WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x8000000;
    const int GWL_EXSTYLE = -20;
    const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;
    const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    static readonly nint HWND_TOPMOST = -1;
    const uint ULW_ALPHA = 2;
    const uint MONITOR_DEFAULTTOPRIMARY = 1;
    const uint SPI_GETCLIENTAREAANIMATION = 0x1042;
    const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)]
    struct RECT
    {
        public int left, top, right, bottom;
        public readonly Rectangle ToRectangle() => Rectangle.FromLTRB(left, top, right, bottom);
    }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateDIBSection(nint hdc, ref BITMAPINFOHEADER pbmi, uint usage, out nint ppvBits, nint hSection, uint offset);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateLayeredWindow(nint hwnd, nint hdcDst, ref POINT pptDst, ref SIZE psize, nint hdcSrc, ref POINT pptSrc, uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint hWnd, int nCmdShow);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [LibraryImport("user32.dll")]
    private static partial int GetWindowLongW(nint hWnd, int nIndex);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindowW(string lpClassName, string? lpWindowName);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint hwnd, uint dwFlags);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromPoint(POINT pt, uint dwFlags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfoW(nint hMonitor, ref MONITORINFO lpmi);

    [LibraryImport("shcore.dll")]
    private static partial int GetDpiForMonitor(nint hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfoW(uint uiAction, uint uiParam, out int pvParam, uint fWinIni);

    [LibraryImport("shell32.dll")]
    private static partial int SHQueryUserNotificationState(out int pquns);

    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint hWnd);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint hWnd, nint hDC);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    private static partial nint SelectObject(nint hdc, nint h);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint ho);
}
