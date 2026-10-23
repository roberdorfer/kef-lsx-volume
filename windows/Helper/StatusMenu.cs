// ≙ Helper/StatusMenu.swift.
//
// Tray icon and menu (docs/DESIGN.md §7): WinForms NotifyIcon + ContextMenuStrip with a Windows-11-style renderer, the
// RangeView (two TrackBars, three labels) in a ToolStripControlHost, and the icon drawn in code (the LSX II cabinet or
// the volume number). The menu is rebuilt from one controller snapshot every time it opens.
//
// Every status line, the RangeView, the three toggles and the input submenu (M2) write only the app's own settings
// (HKCU\Software\io.github.roberdorfer.lsxiivolumefixer) and only when the user clicks or drags. "Wake speaker / source
// USB" POSTs the speaker source (M4). M5: "Apply these settings for all users…" runs a generated admin script elevated once (as -EncodedCommand, no file)
// (§8.4); "Restart LSX II Volume Fixer" ends the agent with ExitApp(0) and the launcher starts it again (§10.9).

using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;

// MARK: - Model

/// What the menu shows and changes: the controller's snapshot, the settings (read live, §8.1), the capture endpoints
/// and the taskbar design. The app uses this class as it is. A render harness (a development tool, not published) overrides it with
/// fixed data, so that the real StatusMenu and RangeView can be drawn off screen without a controller, Core Audio or
/// the registry. (macOS reads Config and the controller directly; the indirection exists only for the harness.)
class MenuModel(Controller? controller)
{
    public virtual Controller.Status Snapshot() => controller!.Snapshot();
    public virtual string? SpeakerHost => Config.SpeakerHost;
    public virtual string? SpeakerHostOverride => Config.SpeakerHostOverride;
    public virtual string? RestoreInputUIDPrefix => Config.RestoreInputUIDPrefix;
    public virtual bool AutoSwitchDefault => Config.AutoSwitchDefault;
    public virtual bool AutoWakeOnPlayback => Config.AutoWakeOnPlayback;
    public virtual bool ShowVolumeInMenuBar => Config.ShowVolumeInMenuBar;
    public virtual int MinSpeakerVolume => Config.MinSpeakerVolume;
    public virtual int MaxSpeakerVolume => Config.MaxSpeakerVolume;
    /// ≙ Config.set: the user's value in HKCU (null deletes it). Only called from a click or a slider drag.
    public virtual void Set(object? value, string forKey) => Config.Set(value, forKey);
    public virtual void SettingsChanged(bool pushVolume) => controller!.SettingsChanged(pushVolume);
    public virtual (string name, string uid)[] InputDevices() => controller!.InputDevices();
    /// ≙ wakeUSB(): setSpeakerSource("usb"), a POST. Only called from a click.
    public virtual void WakeUSB() => controller!.SetSpeakerSource("usb");
    /// ≙ applyForAllUsers() (§8.4). Only called from a click.
    public virtual void ApplyForAllUsers() => StatusMenu.ApplyForAllUsers();
    /// ≙ restartApp(): log, then the orderly end with exit code 0; the launcher starts the agent again (§10.9).
    public virtual void Restart()
    {
        Log("restart requested from menu");
        ExitApp(0);
    }
    /// Light taskbar (`SystemUsesLightTheme` = 1): black icon and a light menu (§7.1, §7.3). Without the value: dark, the
    /// Windows 11 default.
    public virtual bool LightTaskbar =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) is int i && i == 1;
}

// MARK: - StatusMenu

/// Tray icon and menu: live status, volume range, settings, speaker source, all-users export.
sealed partial class StatusMenu : IDisposable
{
    readonly MenuModel model;
    readonly IWin32Window? owner;
    readonly NotifyIcon? item;
    internal readonly ContextMenuStrip menu = new TrayMenuStrip();
    readonly System.Windows.Forms.Timer? refreshTimer;
    internal RangeView? rangeView;
    readonly MenuRenderer renderer = new();
    Font? boldFont;
    DateTime menuClosedAt = DateTime.MinValue;
    /// MenuWillOpen has built the menu for the coming open (cleared when it closes).
    bool prepared;
    (string? number, bool dimmed, bool light, int px)? iconKey;
    Icon? icon;

    /// The tray app. `owner`: the agent's hidden window, made foreground before the menu opens.
    public StatusMenu(Controller controller, IWin32Window owner) : this(new MenuModel(controller), owner, tray: true)
    {
        // Addition (A81): redraw the icon as soon as the speaker value changes, not only on the 2-s timer.
        var ui = SynchronizationContext.Current;
        if (ui != null) controller.SpeakerVolumeChanged += () => ui.Post(_ => { if (!menu.Visible) RefreshButton(); }, null);
    }

    /// `tray` false: no NotifyIcon and no timer, only the menu (render harness).
    internal StatusMenu(MenuModel model, IWin32Window? owner, bool tray)
    {
        this.model = model;
        this.owner = owner;
        Configure(menu);
        // Every opening path goes through ShowMenu, which builds the menu before Show() (sizing, see there). Should the
        // menu ever open another way (e.g. the keyboard path of a later Shell_NotifyIcon v4 wrapper, O19), it is built
        // here, in `Opening` as §7.3 has it; Cancel is preset to true when the menu had no items.
        menu.Opening += (_, e) =>
        {
            if (prepared) return;
            refreshTimer?.Stop();
            MenuWillOpen(DpiAt(Cursor.Position));
            e.Cancel = false;
        };
        menu.Closed += (_, _) => MenuDidClose();
        if (!tray) return;
        item = new NotifyIcon { Text = appName };   // ≙ accessibilityDescription = appName
        // Deviation (Windows, A47): left and right click open the same menu, like an NSStatusItem with a menu.
        item.MouseUp += (_, e) => { if (e.Button is MouseButtons.Left or MouseButtons.Right) ShowMenu(); };
        refreshTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        refreshTimer.Tick += (_, _) => RefreshButton();
        refreshTimer.Start();
        RefreshButton();
        item.Visible = true;
    }

    void Configure(ToolStripDropDownMenu m)
    {
        m.ShowCheckMargin = true;    // §7.3: check column on, image column off
        m.ShowImageMargin = false;
        m.Renderer = renderer;
        m.HandleCreated += (s, _) => RoundCorners(((Control)s!).Handle);
    }

    // MARK: icon

    /// What the icon shows for a snapshot (≙ refreshButton): the volume number with `showVolumeInMenuBar`, the USB device
    /// present and a value known; else the LSX II symbol. Dimmed without the USB device (≙ appearsDisabled, UI-04).
    internal static (string? number, bool dimmed) IconState(Controller.Status st, bool showVolume) =>
        (showVolume && st.RealPresent && st.SpeakerVolume is int v ? v.ToString(CultureInfo.InvariantCulture) : null, !st.RealPresent);

    /// ≙ refreshButton(): every 2 s, when the menu closes and after a toggle. The icon is redrawn only when its content
    /// changes (number, dimmed, design, size; §7.1).
    internal void RefreshButton()
    {
        if (item == null) return;
        var (number, dimmed) = IconState(model.Snapshot(), model.ShowVolumeInMenuBar);
        bool light = model.LightTaskbar;
        int px = GetSystemMetricsForDpi(SM_CXSMICON, TaskbarDpi());
        if (px <= 0) px = 16;
        var key = (number, dimmed, light, px);
        if (iconKey == key) return;
        iconKey = key;
        Icon next;
        using (Bitmap bmp = RenderIcon(px, number, dimmed, light)) next = Icon.FromHandle(bmp.GetHicon());
        Icon? prev = icon;
        item.Icon = next;
        icon = next;
        // Icon.FromHandle does not own the HICON: without DestroyIcon every new icon would leak a handle.
        if (prev != null) { _ = DestroyIcon(prev.Handle); prev.Dispose(); }
    }

    /// Design (§7.1) or DPI changed (WM_SETTINGCHANGE, WM_DPICHANGED, WM_DISPLAYCHANGE of the agent's window).
    internal void AppearanceChanged() => RefreshButton();

    /// The icon as a `px`×`px` 32-bit ARGB bitmap: white on a dark taskbar, black on a light one (A46: no template
    /// images); about 43 % opaque when dimmed.
    internal static Bitmap RenderIcon(int px, string? number, bool dimmed, bool lightTaskbar)
    {
        var bmp = new Bitmap(px, px, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            Color c = lightTaskbar ? Color.Black : Color.White;
            if (number == null) DrawSpeaker(g, px, c); else DrawNumber(g, px, number, c);
        }
        if (dimmed) ScaleAlpha(bmp, 0.43f);
        return bmp;
    }

    // Design decision: Tray icon in the shape of an LSX II — reason: the generic speaker symbol did not look like the
    // speaker.
    /// ≙ speakerIcon(): a slightly tall rounded cabinet (10×15, radius 2.2) with the single Uni-Q driver (ring radius 3.1
    /// and a concentric dot radius 1.1) in its upper half, stroke 1.5, in an 18×18 box (UI-02); scaled to `px` and put on
    /// whole pixels, so that it stays crisp from 16 to 32 px.
    static void DrawSpeaker(Graphics g, int px, Color c)
    {
        float s = px / 18f;
        // Whole-pixel stroke, rounded down rather than up (1 px at 16 and 20 px, 2 px at 24 and 32 px): a heavier stroke
        // would close the 0.4-pt gap between ring and cabinet (measured in the render harness).
        float sw = MathF.Max(1, MathF.Floor(1.5f * s + 0.25f));
        // Outer edges of the cabinet's stroke (macOS: rect x 4…14, y 1.5…16.5, stroke centred on it).
        float left = MathF.Round((4 - 0.75f) * s), right = MathF.Round((14 + 0.75f) * s);
        float top = MathF.Round((1.5f - 0.75f) * s), bottom = MathF.Round((16.5f + 0.75f) * s);
        using var pen = new Pen(c, sw);
        using (GraphicsPath box = RoundedRect(new RectangleF(left + sw / 2, top + sw / 2, right - left - sw, bottom - top - sw), 2.2f * s))
            g.DrawPath(pen, box);
        // The driver: macOS centre (9, 9.2) from the bottom of the 18-pt box, 8.8 from the top; ring radius 3.1, but at
        // least about 0.75 px clear of the cabinet's inner edge after rounding.
        float cx = (left + right) / 2, cy = 8.8f * s;
        float innerHalf = (right - left) / 2 - sw;
        float r = MathF.Min(3.1f * s, innerHalf - MathF.Max(0.75f, 0.75f * s) - sw / 2), d = 1.1f * s;
        g.DrawEllipse(pen, cx - r, cy - r, 2 * r, 2 * r);
        using var brush = new SolidBrush(c);
        g.FillEllipse(brush, cx - d, cy - d, 2 * d, 2 * d);
    }

    /// UI-03 (deviation Windows, A45): the volume as the icon itself, in Bahnschrift SemiBold Condensed (fallback Segoe
    /// UI Bold). Two digits are 13/16 of the icon high (13 px at 16 px); wider numbers ("100") shrink to the icon's
    /// width. Height and left edge on whole pixels. Height and vertical place come from the ten digits together, not
    /// from the number's own outline: round digits overshoot, so "15" would otherwise sit 1 px below "37" at 16 px and
    /// the number would jump as the volume changes.
    static void DrawNumber(Graphics g, int px, string text, Color c)
    {
        FontFamily family = NumberFont(out FontStyle style);
        using var path = new GraphicsPath();
        path.AddString(text, family, (int)style, 100f, PointF.Empty, StringFormat.GenericTypographic);
        RectangleF b = path.GetBounds();
        if (b.Width <= 0 || b.Height <= 0) return;
        RectangleF d = DigitBounds(family, style);
        float k = MathF.Min(px * 13f / 16f / d.Height, px / b.Width);
        float h = MathF.Round(d.Height * k);
        if (b.Width * (h / d.Height) > px) h = MathF.Floor(d.Height * k);
        k = MathF.Max(1, h) / d.Height;
        float x = MathF.Round((px - b.Width * k) / 2), y = MathF.Round((px - d.Height * k) / 2);
        using var m = new Matrix();
        m.Translate(x, y);
        m.Scale(k, k);
        m.Translate(-b.X, -d.Y);
        path.Transform(m);
        using var brush = new SolidBrush(c);
        g.FillPath(brush, path);
    }

    static RectangleF? digitBounds;
    /// The outline bounds of "0123456789" at 100 em units: the common top and bottom of every number.
    static RectangleF DigitBounds(FontFamily family, FontStyle style)
    {
        if (digitBounds is RectangleF r) return r;
        using var path = new GraphicsPath();
        path.AddString("0123456789", family, (int)style, 100f, PointF.Empty, StringFormat.GenericTypographic);
        r = path.GetBounds();
        digitBounds = r;
        return r;
    }

    static FontFamily? numberFamily;
    static FontStyle numberStyle;
    internal static FontFamily NumberFont(out FontStyle style)
    {
        if (numberFamily == null)
        {
            try { numberFamily = new FontFamily("Bahnschrift SemiBold Condensed"); numberStyle = FontStyle.Regular; }
            catch (ArgumentException) { numberFamily = new FontFamily("Segoe UI"); numberStyle = FontStyle.Bold; }
        }
        style = numberStyle;
        return numberFamily;
    }

    static unsafe void ScaleAlpha(Bitmap bmp, float factor)
    {
        BitmapData data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < data.Height; y++)
            {
                byte* row = (byte*)data.Scan0 + y * data.Stride;
                for (int x = 0; x < data.Width; x++) row[x * 4 + 3] = (byte)MathF.Round(row[x * 4 + 3] * factor);
            }
        }
        finally { bmp.UnlockBits(data); }
    }

    internal static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = MathF.Min(2 * radius, MathF.Min(r.Width, r.Height));
        var p = new GraphicsPath();
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // MARK: menu

    /// Opens the menu at the mouse pointer. Built here, right before Show(), rather than in `Opening`: Show() places the
    /// drop-down with the size it has before `Opening` runs. Every opening path should come here; the `Opening` handler
    /// (constructor) builds only a menu that did not.
    void ShowMenu()
    {
        // A click within 300 ms after the menu closed is the click that closed it (A70).
        if (menu.Visible || (DateTime.UtcNow - menuClosedAt).TotalMilliseconds < 300) return;
        Point at = Cursor.Position;
        refreshTimer?.Stop();   // while the menu is open the icon does not update (§7.1, O-M4)
        MenuWillOpen(DpiAt(at));
        menu.PerformLayout();
        Size size = menu.Size;
        // Deviation (Windows, A47): upward from the mouse pointer, as NotifyIcon's own menus open (above-left; below or
        // to the right only where the screen ends).
        Rectangle screen = Screen.FromPoint(at).Bounds;
        var b = new Rectangle(at.X - size.Width, at.Y - size.Height, size.Width, size.Height);
        if (b.Y < screen.Y) b.Y = at.Y;
        if (b.X < screen.X) b.X = at.X;
        b.X = Math.Clamp(b.X, screen.X, Math.Max(screen.X, screen.Right - b.Width));
        b.Y = Math.Clamp(b.Y, screen.Y, Math.Max(screen.Y, screen.Bottom - b.Height));
        // As NotifyIcon does it: our window in front, so that the menu closes when the user clicks elsewhere.
        if (owner != null) _ = SetForegroundWindow(owner.Handle);
        menu.Show(b.Location);
    }

    /// ≙ menuWillOpen(_:): removes every item (and frees the old RangeView, §7.3) and builds the menu from one snapshot.
    /// `dpi`: the menu's monitor; the RangeView and the renderer scale with it.
    internal void MenuWillOpen(int dpi)
    {
        ClearItems();
        prepared = true;
        Controller.Status st = model.Snapshot();
        float scale = dpi / 96f;
        MenuColors colors = model.LightTaskbar ? MenuColors.light : MenuColors.dark;   // §7.3: after the taskbar's design
        renderer.colors = colors;
        renderer.scale = scale;
        boldFont?.Dispose();
        boldFont = new Font(menu.Font, FontStyle.Bold);

        // Status
        string? host = model.SpeakerHost;
        string head;
        if (!st.RealPresent) head = L("LSX II not connected over USB", "LSX II nicht per USB verbunden");
        else if (host != null && !st.SpeakerReachable) head = L($"Speaker not reachable ({host})", $"Lautsprecher nicht erreichbar ({host})");
        else if (host == null) head = L("Searching for the LSX II on the network…", "Suche LSX II im Netzwerk…");
        else if (st.SpeakerVolume is int v) head = "LSX II: " + I(v) + " (" + Config.DB(v).ToString("F1", CultureInfo.InvariantCulture) + " dB)";
        else head = "LSX II";
        AddInfo(head, bold: true);
        // Deviation (v1, A49): the output is the LSX II's own USB endpoint, not a proxy device.
        AddInfo(st.ProxyIsDefault ? L("Output: LSX II (USB) ✓", "Ausgabe: LSX II (USB) ✓")
                                  : L("Output: another device", "Ausgabe: anderes Gerät"));
        if (host != null) AddInfo(L("Speaker: ", "Lautsprecher: ") + host + (model.SpeakerHostOverride == null ? " (Bonjour)" : ""));
        if (st.RealFormat is string f) AddInfo(L("USB format: ", "USB-Format: ") + f);
        if (st.SpeakerSource is string src) AddInfo(L("Speaker source: ", "Quelle am Lautsprecher: ") + src);
        if (model.RestoreInputUIDPrefix != null)
        {
            AddInfo(L("Input: ", "Eingang: ") + (st.InputPresent ? (st.InputIsDefault ? "✓" : L("present, not default", "vorhanden, nicht Standard")) : L("not connected", "nicht verbunden")));
        }
        if (!st.OnConsole) AddInfo(L("Inactive: another user owns the screen", "Inaktiv: anderer Benutzer am Bildschirm"));
        menu.Items.Add(new ToolStripSeparator());

        // Volume range (deviation Windows, A49: "PC" instead of "Mac")
        AddInfo(L("Volume range (PC 0–100 % → speaker)", "Lautstärkebereich (PC 0–100 % → Lautsprecher)"));
        var rv = new RangeView(model, scale, colors, onChange: () => model.SettingsChanged(pushVolume: true));
        rangeView = rv;
        // ToolStripDropDownMenu puts a hosted control in the text column (after the check column); inside the view the
        // macOS geometry holds.
        menu.Items.Add(new ToolStripControlHost(rv) { AutoSize = false, Size = rv.Size, Margin = Padding.Empty, Padding = Padding.Empty });
        menu.Items.Add(new ToolStripSeparator());

        // Settings (deviation v1/Windows, A49: "LSX II" instead of "proxy", "taskbar" instead of "menu bar"; the keys stay)
        AddToggle(L("Keep LSX II as default output", "LSX II als Standardausgabe halten"), "autoSwitchDefault", model.AutoSwitchDefault);
        AddToggle(L("Wake speaker automatically on playback", "Lautsprecher bei Wiedergabe automatisch aufwecken"), "autoWakeOnPlayback", model.AutoWakeOnPlayback);
        AddToggle(L("Show volume in the taskbar", "Lautstärke in der Taskleiste anzeigen"), "showVolumeInMenuBar", model.ShowVolumeInMenuBar);
        menu.Items.Add(InputMenuItem());
        // Design decision: No "speaker address" item in the menu; the `speakerHost` setting remains settable via `reg add`
        // as an override of the Bonjour discovery; the status shows the host in use.
        menu.Items.Add(new ToolStripSeparator());

        // Speaker actions
        // ≙ wakeUSB() → setSpeakerSource("usb"), a POST to the speaker (M4).
        var wake = new ToolStripMenuItem(Escape(L("Wake speaker / source USB", "Lautsprecher aufwecken / Quelle USB")));
        wake.Click += (_, _) => model.WakeUSB();
        menu.Items.Add(wake);
        // Design decision: No source submenu (usb/wifi/bluetooth/…) in the menu — reason: for the PC the source must be
        // USB anyway; the status line still shows the current source.
        menu.Items.Add(new ToolStripSeparator());

        // ≙ applyForAllUsers(): the elevated admin script of §8.4 (-EncodedCommand, §10.3).
        var all = new ToolStripMenuItem(Escape(L("Apply these settings for all users…", "Diese Einstellungen für alle Benutzer übernehmen…")));
        all.Click += (_, _) => model.ApplyForAllUsers();
        menu.Items.Add(all);
        var logItem = new ToolStripMenuItem(Escape(L("Open log", "Log öffnen")));
        logItem.Click += (_, _) => OpenLog();
        menu.Items.Add(logItem);
        // ≙ restartApp(): log, ExitApp(0); the launcher starts the agent again (§10.9).
        var restart = new ToolStripMenuItem(Escape(L($"Restart {appName}", $"{appName} neu starten")));
        restart.Click += (_, _) => model.Restart();
        menu.Items.Add(restart);
        AddInfo($"{appName} {appVersion}");
    }

    /// ≙ menuDidClose(_:).
    void MenuDidClose()
    {
        prepared = false;
        rangeView = null;
        menuClosedAt = DateTime.UtcNow;
        refreshTimer?.Start();
        RefreshButton();
    }

    /// Items.Clear() disposes nothing: every item is disposed here; the host disposes the RangeView, which first runs a
    /// pending range change (≙ the DispatchWorkItem that outlives the menu on macOS).
    void ClearItems()
    {
        ToolStripItem[] old = [.. menu.Items.Cast<ToolStripItem>()];
        menu.Items.Clear();
        foreach (ToolStripItem i in old) i.Dispose();
        rangeView = null;
    }

    /// Info lines are disabled items (UI-15, ≙ isEnabled = false); the renderer draws no hover mark for them (A48).
    void AddInfo(string title, bool bold = false)
    {
        var i = new ToolStripMenuItem(Escape(title)) { Enabled = false };
        if (bold) i.Font = boldFont;
        menu.Items.Add(i);
    }

    void AddToggle(string title, string key, bool on)
    {
        var i = new ToolStripMenuItem(Escape(title)) { Checked = on };
        i.Click += (_, _) => Toggle(key, on);
        menu.Items.Add(i);
    }

    /// ≙ toggle(_:): new value = !current → Config.set → log `setting <key> = true|false` → settingsChanged(false) →
    /// icon.
    void Toggle(string key, bool wasOn)
    {
        model.Set(!wasOn, key);
        Log($"setting {key} = {(!wasOn ? "true" : "false")}");
        model.SettingsChanged(pushVolume: false);
        RefreshButton();
    }

    // MARK: input device

    ToolStripMenuItem InputMenuItem()
    {
        var parent = new ToolStripMenuItem(Escape(L("Restore input after USB reconnect", "Eingang nach USB-Neuerkennung wiederherstellen")));
        Configure((ToolStripDropDownMenu)parent.DropDown);
        string? cur = model.RestoreInputUIDPrefix;
        var off = new ToolStripMenuItem(Escape(L("Off", "Aus"))) { Checked = cur == null };
        off.Click += (_, _) => ChooseInput("");
        parent.DropDownItems.Add(off);
        parent.DropDownItems.Add(new ToolStripSeparator());
        bool matched = cur == null;
        foreach (var d in model.InputDevices())
        {
            string prefix = UidPrefix(d.uid);
            var i = new ToolStripMenuItem(Escape(d.name));
            i.Click += (_, _) => ChooseInput(prefix);
            if (cur != null && d.uid.StartsWith(cur, StringComparison.Ordinal)) { i.Checked = true; matched = true; }
            parent.DropDownItems.Add(i);
        }
        if (!matched && cur != null)
        {
            parent.DropDownItems.Add(new ToolStripMenuItem(Escape(cur + L(" (not connected)", " (nicht verbunden)"))) { Checked = true, Enabled = false });
        }
        return parent;
    }

    /// ≙ chooseInput(_:).
    void ChooseInput(string prefix)
    {
        model.Set(prefix.Length == 0 ? null : prefix, "restoreInputUIDPrefix");
        Log($"setting restoreInputUIDPrefix = {(prefix.Length == 0 ? "off" : prefix)}");
        model.SettingsChanged(pushVolume: false);
    }

    // MARK: misc

    /// ≙ openLog(): the shell's default program for .log (§7.3).
    static void OpenLog()
    {
        try { Process.Start(new ProcessStartInfo(logURL) { UseShellExecute = true })?.Dispose(); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { }
    }

    /// ≙ applyForAllUsers() (§8.4): the per-user values live in each account's HKCU; the machine-wide fallback in HKLM
    /// needs an administrator, hence one elevation. Users who set their own value keep it. The script (Config.cs,
    /// AdminScriptForAllUsers) runs once elevated on a background thread (macOS runs the AppleScript synchronously). It
    /// goes on the command line as -EncodedCommand, never through a file: a file in the user-writable %TEMP% could be
    /// swapped while the UAC prompt is open, and the elevated PowerShell would run the swapped one.
    /// Log `apply for all users: ok` or `failed/cancelled <code>` (1223: UAC cancelled; else the script's exit code).
    internal static void ApplyForAllUsers()
    {
        string script = AdminScriptForAllUsers(k => Config.defaults.Object(forKey: k));
        string encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        var worker = new Thread(() =>
        {
            string result;
            try
            {
                string ps = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
                using Process? p = Process.Start(new ProcessStartInfo(ps, "-NoProfile -EncodedCommand " + encoded)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden,
                });
                if (p == null) result = "failed/cancelled -1";
                else
                {
                    p.WaitForExit();
                    result = p.ExitCode == 0 ? "ok" : "failed/cancelled " + p.ExitCode.ToString(CultureInfo.InvariantCulture);
                }
            }
            catch (System.ComponentModel.Win32Exception e)
            {
                result = "failed/cancelled " + e.NativeErrorCode.ToString(CultureInfo.InvariantCulture);
            }
            Log($"apply for all users: {result}");
        }) { Name = "apply for all users", IsBackground = true };
        worker.Start();
    }

    /// For ExitApp (§10.9): NIM_DELETE now, so that no ghost icon stays in the notification area.
    internal void RemoveIcon()
    {
        refreshTimer?.Stop();
        if (item == null) return;
        item.Visible = false;
        item.Dispose();
    }

    public void Dispose()
    {
        RemoveIcon();
        refreshTimer?.Dispose();
        if (icon != null) { _ = DestroyIcon(icon.Handle); icon.Dispose(); icon = null; }
        ClearItems();
        menu.Dispose();
        boldFont?.Dispose();
    }

    /// `&` is a mnemonic prefix in ToolStrip texts; device names and UIDs (`…&PID_…`) must show it as it is.
    static string Escape(string s) => s.Replace("&", "&&");
    static string I(int v) => v.ToString(CultureInfo.InvariantCulture);

    static void RoundCorners(IntPtr hwnd)
    {
        // O21: rounded popup corners like the Windows 11 menus.
        int pref = DWMWCP_ROUNDSMALL;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
    }

    static int DpiAt(Point p)
    {
        IntPtr mon = MonitorFromPoint(new POINT { x = p.X, y = p.Y }, MONITOR_DEFAULTTONEAREST);
        return GetDpiForMonitor(mon, MDT_EFFECTIVE_DPI, out uint dx, out _) == 0 && dx > 0 ? (int)dx : 96;
    }

    /// The DPI of the taskbar's monitor (the icon size follows it, SM_CXSMICON; e.g. 24 px at 144 DPI).
    static uint TaskbarDpi()
    {
        IntPtr tray = FindWindowW("Shell_TrayWnd", null);
        IntPtr mon = tray != IntPtr.Zero ? MonitorFromWindow(tray, MONITOR_DEFAULTTOPRIMARY) : MonitorFromPoint(default, MONITOR_DEFAULTTOPRIMARY);
        return GetDpiForMonitor(mon, MDT_EFFECTIVE_DPI, out uint dx, out _) == 0 && dx > 0 ? dx : 96;
    }

    // MARK: Win32 (docs/DESIGN.md §4: DWM and Shell in StatusMenu.cs)

    const int SM_CXSMICON = 49;
    const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUNDSMALL = 3;
    const uint MONITOR_DEFAULTTOPRIMARY = 1, MONITOR_DEFAULTTONEAREST = 2;
    const int MDT_EFFECTIVE_DPI = 0;

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int x, y; }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr hIcon);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetricsForDpi(int nIndex, uint dpi);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr FindWindowW(string lpClassName, string? lpWindowName);

    [LibraryImport("user32.dll")]
    private static partial IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [LibraryImport("user32.dll")]
    private static partial IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [LibraryImport("shcore.dll")]
    private static partial int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);
}

// MARK: - Menu strip

/// The tray menu's ContextMenuStrip, with the check column and paddings for its DPI. WinForms 10 lays out a
/// ContextMenuStrip made with the public constructor for 96 DPI whatever the DPI (ToolStripDropDownMenu() calls
/// ScaleConstants(96)) and rescales only on a DPI change; the drop-downs of menu items (the input submenu) start at the
/// system DPI. Measured in M2 at 144 DPI: check column 25 instead of 37 px, text column at 33 instead of 49 px, the
/// 18-px check glyph pushed against the border. RescaleConstantsForDpi brings the constants to the strip's DPI once,
/// before any item exists; later DPI changes WinForms handles itself (it recreates the handle on another monitor).
sealed class TrayMenuStrip : ContextMenuStrip
{
    public TrayMenuStrip()
    {
        if (DeviceDpi != 96) RescaleConstantsForDpi(96, DeviceDpi);
    }
}

// MARK: - RangeView

/// ≙ RangeView: two sliders for the speaker range the PC scale maps onto, with dB and step size readout (§7.3).
/// 300×120 device-independent pixels in the macOS layout, scaled to the menu's DPI.
sealed class RangeView : Panel
{
    readonly MenuModel model;
    readonly Action onChange;
    readonly TrackBar maxSlider = new MenuSlider(), minSlider = new MenuSlider();
    readonly Label maxLabel = new(), minLabel = new(), stepLabel = new();
    readonly System.Windows.Forms.Timer logPending = new() { Interval = 150 };
    readonly Font font;
    string? pendingWhat;

    /// How a Label draws its text (WordBreak, TextBoxControl, no mnemonics); the fit test measures the same way.
    internal static readonly TextFormatFlags stepFlags = TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix;

    public RangeView(MenuModel model, float scale, MenuColors colors, Action onChange)
    {
        this.model = model;
        this.onChange = onChange;
        int S(double v) => (int)Math.Round(v * scale);
        // ≙ NSFont.menuFont(ofSize: smallSystemFontSize): the menu font's family at 11 px, DPI-scaled.
        font = new Font(SystemFonts.MenuFont?.FontFamily ?? FontFamily.GenericSansSerif, 11f * scale, GraphicsUnit.Pixel);
        BackColor = colors.Background;
        foreach (var (slider, min, max, value, changed) in new (TrackBar, int, int, int, Action)[]
                 { (maxSlider, 20, 100, model.MaxSpeakerVolume, MaxChanged), (minSlider, 1, 40, model.MinSpeakerVolume, MinChanged) })
        {
            // ≙ NSSlider, .small (RV-02, RV-03); only user movement counts (Scroll, not ValueChanged). A click on the track
            // jumps to the pointer (MenuSlider); LargeChange is left for PageUp/PageDown.
            slider.Minimum = min;
            slider.Maximum = max;
            slider.Value = Math.Clamp(value, min, max);
            slider.TickStyle = TickStyle.None;
            slider.AutoSize = false;
            slider.SmallChange = 1;
            slider.LargeChange = 5;
            slider.BackColor = colors.Background;
            slider.Scroll += (_, _) => changed();
        }
        foreach (Label l in new[] { maxLabel, minLabel, stepLabel })
        {
            l.Font = font;
            l.AutoSize = false;
            l.UseMnemonic = false;
            l.BackColor = colors.Background;
            l.ForeColor = colors.Text;
        }
        stepLabel.ForeColor = colors.Secondary;   // ≙ secondaryLabelColor
        // The macOS frames, whose origin is the bottom left of the 300×120 view, measured from the top.
        maxLabel.Bounds = new Rectangle(S(14), S(6), S(140), S(16));
        maxSlider.Bounds = new Rectangle(S(150), S(4), S(136), S(20));
        minLabel.Bounds = new Rectangle(S(14), S(32), S(140), S(16));
        minSlider.Bounds = new Rectangle(S(150), S(30), S(136), S(20));
        // macOS reserves 58 pt for 4 lines of the small font (52 needed). Segoe UI needs about 4 × 14.6 px (§7.3), so the
        // label grows to four measured lines where 58 is too little, and the view with it.
        int fourLines = TextRenderer.MeasureText("0\n0\n0\n0", font, new Size(S(272), int.MaxValue), stepFlags).Height;
        stepLabel.Bounds = new Rectangle(S(14), S(58), S(272), Math.Max(S(58), fourLines));
        Size = new Size(S(300), stepLabel.Bottom + S(4));
        Controls.AddRange([maxLabel, maxSlider, minLabel, minSlider, stepLabel]);
        logPending.Tick += (_, _) => Flush();
        UpdateLabels();
    }

    /// ≙ maxChanged(): at least min + 1; the slider jumps to the value, which is stored at once.
    void MaxChanged()
    {
        int v = Math.Max(maxSlider.Value, model.MinSpeakerVolume + 1);
        maxSlider.Value = Math.Clamp(v, maxSlider.Minimum, maxSlider.Maximum);
        model.Set(v, "maxSpeakerVolume");
        Changed("maxSpeakerVolume = " + v.ToString(CultureInfo.InvariantCulture));
    }

    /// ≙ minChanged(): at most max − 1.
    void MinChanged()
    {
        int v = Math.Min(minSlider.Value, model.MaxSpeakerVolume - 1);
        minSlider.Value = Math.Clamp(v, minSlider.Minimum, minSlider.Maximum);
        model.Set(v, "minSpeakerVolume");
        Changed("minSpeakerVolume = " + v.ToString(CultureInfo.InvariantCulture));
    }

    /// Dragging fires continuously: one log line and one push per pause (0.15 s), not per pixel (RV-05).
    void Changed(string what)
    {
        UpdateLabels();
        pendingWhat = what;
        logPending.Stop();
        logPending.Start();
    }

    /// Runs the pending change now: after the pause, or when the view goes away before it.
    void Flush()
    {
        logPending.Stop();
        if (pendingWhat is not string what) return;
        pendingWhat = null;
        Log("setting " + what);
        onChange();
    }

    void UpdateLabels()
    {
        int hi = model.MaxSpeakerVolume, lo = model.MinSpeakerVolume;   // the settings may have changed elsewhere
        minSlider.Value = Math.Clamp(lo, minSlider.Minimum, minSlider.Maximum);
        maxSlider.Value = Math.Clamp(hi, maxSlider.Minimum, maxSlider.Maximum);
        static string DB(int v) => Config.DB(v).ToString("F1", CultureInfo.InvariantCulture) + " dB";
        string h = hi.ToString(CultureInfo.InvariantCulture), l = lo.ToString(CultureInfo.InvariantCulture);
        maxLabel.Text = L($"Maximum: {h} ({DB(hi)})", $"Maximum: {h} ({DB(hi)})");
        minLabel.Text = L($"Minimum: {l} ({DB(lo)})", $"Minimum: {l} ({DB(lo)})");
        stepLabel.Text = StepText(Config.KeyStepTable(min: lo, max: hi), Fits);
    }

    bool Fits(string text) =>
        TextRenderer.MeasureText(text, font, new Size(stepLabel.Width, int.MaxValue), stepFlags).Height <= stepLabel.Height;

    /// The step line (RV-07): `Each key: %.1f dB (…)\nSteps: …` with the step (dB(t[49]) − dB(t[0])) / 49, invariant
    /// numbers. Short on purpose: the menu is 300 px wide; the full rule is in the README and docs\usb-volume-curve.md.
    /// When the list does not fit the label: only the ends and the number of distinct values (A76).
    internal static string StepText(int[] t, Func<string, bool> fits)
    {
        double stepDB = (Config.DB(t[^1]) - Config.DB(t[0])) / (t.Length - 1);
        string head = string.Format(CultureInfo.InvariantCulture,
            L("Each key: {0:F1} dB (equal steps on the measured curve, dB re speaker 65)",
              "Pro Taste: {0:F1} dB (gleiche Schritte auf der gemessenen Kurve, dB rel. Lautsprecher 65)"), stepDB);
        string steps = L("Steps: ", "Stufen: ");
        string full = head + "\n" + steps + StepRuns(t);
        if (fits(full)) return full;
        string n = t.Distinct().Count().ToString(CultureInfo.InvariantCulture);
        return head + "\n" + steps + t[0].ToString(CultureInfo.InvariantCulture) + " … " + t[^1].ToString(CultureInfo.InvariantCulture)
            + " (" + n + " " + L("values", "Werte") + ")";
    }

    /// Deviation (user, A76; display only): runs of at least three consecutive values (each +1) as "a–b", other
    /// values one by one; an equal neighbour starts a new run, so it stays visible (17–65: "17–23 23–65").
    internal static string StepRuns(int[] t)
    {
        var parts = new List<string>();
        int i = 0;
        while (i < t.Length)
        {
            int j = i;
            while (j + 1 < t.Length && t[j + 1] == t[j] + 1) j++;
            if (j - i >= 2)
            {
                parts.Add(t[i].ToString(CultureInfo.InvariantCulture) + "–" + t[j].ToString(CultureInfo.InvariantCulture));
                i = j + 1;
            }
            else
            {
                parts.Add(t[i].ToString(CultureInfo.InvariantCulture));
                i++;
            }
        }
        return string.Join(" ", parts);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Flush();
            logPending.Dispose();
            font.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// ≙ NSSlider's mouse behaviour: a click on the track puts the value at the pointer and goes on as a drag of the
/// thumb. The standard TrackBar would instead page by LargeChange towards the pointer and repeat while the button is
/// held. The click raises Scroll like a drag step, so the RangeView sees user movement only.
sealed partial class MenuSlider : TrackBar
{
    const int WM_LBUTTONDOWN = 0x0201;
    const int TBM_GETTHUMBRECT = 0x0419, TBM_GETCHANNELRECT = 0x041A;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_LBUTTONDOWN && Orientation == Orientation.Horizontal && Maximum > Minimum && IsHandleCreated)
        {
            int x = (short)((long)m.LParam & 0xFFFF), y = (short)(((long)m.LParam >> 16) & 0xFFFF);
            RECT thumb = Thumb();
            if (x < thumb.left || x >= thumb.right || y < thumb.top || y >= thumb.bottom)
            {
                RECT channel = default;
                _ = SendMessageW(Handle, TBM_GETCHANNELRECT, 0, ref channel);
                int v = ValueAt(x, channel.left, channel.right, thumb.right - thumb.left, Minimum, Maximum);
                if (v != Value)
                {
                    Value = v;
                    OnScroll(EventArgs.Empty);
                }
                // The button-down goes on to the thumb, now under the pointer: the native control starts a drag.
                thumb = Thumb();
                int cx = (thumb.left + thumb.right) / 2, cy = (thumb.top + thumb.bottom) / 2;
                m.LParam = (IntPtr)((cy << 16) | (cx & 0xFFFF));
            }
        }
        base.WndProc(ref m);
    }

    RECT Thumb()
    {
        RECT r = default;
        _ = SendMessageW(Handle, TBM_GETTHUMBRECT, 0, ref r);
        return r;
    }

    /// The value whose thumb centre is nearest to `x`: the centre runs from channelLeft + thumb/2 (minimum) to
    /// channelRight − thumb/2 (maximum).
    internal static int ValueAt(int x, int channelLeft, int channelRight, int thumbWidth, int min, int max)
    {
        double span = channelRight - channelLeft - thumbWidth;
        double f = span > 0 ? (x - channelLeft - thumbWidth / 2.0) / span : 0;
        return Math.Clamp(min + (int)Math.Round(f * (max - min), MidpointRounding.AwayFromZero), min, max);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int left, top, right, bottom; }

    [LibraryImport("user32.dll")]
    private static partial IntPtr SendMessageW(IntPtr hWnd, int msg, nint wParam, ref RECT lParam);
}

// MARK: - Renderer

/// Colours of the Windows 11 menus, light and dark (approximated; Windows has no API for them).
sealed record MenuColors(Color Background, Color Border, Color Text, Color DisabledText, Color Secondary, Color Highlight, Color Separator)
{
    public static readonly MenuColors dark = new(Rgb(0x2C2C2C), Rgb(0x404040), Rgb(0xFFFFFF), Rgb(0x8F8F8F), Rgb(0xA8A8A8), Rgb(0x3D3D3D), Rgb(0x454545));
    public static readonly MenuColors light = new(Rgb(0xF9F9F9), Rgb(0xD5D5D5), Rgb(0x1A1A1A), Rgb(0x7A7A7A), Rgb(0x5F5F5F), Rgb(0xEAEAEA), Rgb(0xE0E0E0));
    static Color Rgb(int rgb) => Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
}

/// Windows-11-style menu (§7.3): flat background after the taskbar's design, rounded hover mark, check mark and
/// chevron from Segoe Fluent Icons (E73E, E76C), no hover mark on disabled items (A48).
sealed class MenuRenderer : ToolStripProfessionalRenderer
{
    internal MenuColors colors = MenuColors.dark;
    internal float scale = 1;
    Font? glyphFont, chevronFont;
    float glyphScale;

    public MenuRenderer() { RoundedEdges = false; }

    int S(float v) => (int)MathF.Round(v * scale);

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var b = new SolidBrush(colors.Background);
        e.Graphics.FillRectangle(b, e.AffectedBounds);
        // Separators over the whole width, as in the Windows 11 menus: ToolStripDropDownMenu places separator items (and
        // hosted controls) after the check column, so they are drawn here instead of in OnRenderSeparator.
        using var p = new Pen(colors.Separator);
        foreach (ToolStripItem i in e.ToolStrip.Items)
        {
            if (i is not ToolStripSeparator || !i.Available) continue;
            int y = i.Bounds.Y + i.Bounds.Height / 2;
            e.Graphics.DrawLine(p, S(4), y, e.ToolStrip.Width - 1 - S(4), y);
        }
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var p = new Pen(colors.Border);
        e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        // A48: info lines are disabled items; ToolStrip would mark them on hover, the system menus do not.
        if (!e.Item.Enabled || !e.Item.Selected) return;
        var r = new RectangleF(S(4), S(1), e.Item.Width - S(8), e.Item.Height - S(2));
        using GraphicsPath path = StatusMenu.RoundedRect(r, S(4));
        using var b = new SolidBrush(colors.Highlight);
        SmoothingMode old = e.Graphics.SmoothingMode;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.FillPath(b, path);
        e.Graphics.SmoothingMode = old;
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        Color c = e.Item.Enabled ? colors.Text : colors.DisabledText;
        TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, e.TextRectangle, c, e.TextFormat);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        EnsureFonts();
        Color c = e.Item.Enabled ? colors.Text : colors.DisabledText;
        DrawGlyph(e.Graphics, "", glyphFont!, e.ImageRectangle, c);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        EnsureFonts();
        Color c = e.Item?.Enabled != false ? colors.Text : colors.DisabledText;
        DrawGlyph(e.Graphics, "", chevronFont!, e.ArrowRectangle, c);
    }

    /// Drawn with the background (OnRenderToolStripBackground).
    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e) { }

    /// An icon glyph centred in `r`, with grayscale antialiasing like the glyphs of the Windows 11 menus: ClearType (the
    /// TextRenderer default) gave the 1-px strokes of the check mark coloured fringes (measured in M2).
    static void DrawGlyph(Graphics g, string glyph, Font font, Rectangle r, Color c)
    {
        TextRenderingHint old = g.TextRenderingHint;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var brush = new SolidBrush(c);
        g.DrawString(glyph, font, brush, r, centered);
        g.TextRenderingHint = old;
    }

    static readonly StringFormat centered = new(StringFormat.GenericTypographic)
    {
        Alignment = StringAlignment.Center,
        LineAlignment = StringAlignment.Center,
        FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip,
    };

    void EnsureFonts()
    {
        if (glyphFont != null && glyphScale == scale) return;
        glyphFont?.Dispose();
        chevronFont?.Dispose();
        string family = IconFontFamily();
        glyphFont = new Font(family, 12f * scale, GraphicsUnit.Pixel);
        chevronFont = new Font(family, 10f * scale, GraphicsUnit.Pixel);
        glyphScale = scale;
    }

    static string? iconFontFamily;
    /// Segoe Fluent Icons (Windows 11); Segoe MDL2 Assets has the same code points.
    static string IconFontFamily()
    {
        if (iconFontFamily != null) return iconFontFamily;
        try { using var f = new FontFamily("Segoe Fluent Icons"); iconFontFamily = f.Name; }
        catch (ArgumentException) { iconFontFamily = "Segoe MDL2 Assets"; }
        return iconFontFamily;
    }
}
