import AppKit
import CoreAudio

/// Menu bar item: live status, volume range, settings, speaker source, all-users export.
final class StatusMenu: NSObject, NSMenuDelegate {
    let controller: Controller
    let item: NSStatusItem
    let menu = NSMenu()
    var refreshTimer: Timer?
    var rangeView: RangeView?

    init(controller: Controller) {
        self.controller = controller
        item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        super.init()
        if let b = item.button {
            b.image = StatusMenu.speakerIcon()
            b.image?.accessibilityDescription = appName
            b.imagePosition = .imageLeading
        }
        menu.delegate = self
        item.menu = menu
        refreshTimer = Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { [weak self] _ in self?.refreshButton() }
        refreshButton()
    }

    func refreshButton() {
        guard let b = item.button else { return }
        let st = controller.snapshot()
        if Config.showVolumeInMenuBar, st.realPresent, let v = st.speakerVolume {
            b.title = " \(v)"
        } else {
            b.title = ""
        }
        b.appearsDisabled = !st.realPresent
    }

    // MARK: menu

    func menuWillOpen(_ menu: NSMenu) {
        menu.removeAllItems()
        let st = controller.snapshot()

        // Status
        let head: String
        if !st.realPresent { head = L("LSX II not connected over USB", "LSX II nicht per USB verbunden") }
        else if let h = Config.speakerHost, !st.speakerReachable { head = L("Speaker not reachable (\(h))", "Lautsprecher nicht erreichbar (\(h))") }
        else if Config.speakerHost == nil { head = L("Searching for the LSX II on the network…", "Suche LSX II im Netzwerk…") }
        else if let v = st.speakerVolume { head = "LSX II: \(v)" + String(format: " (%.1f dB)", Config.dB(v)) }
        else { head = "LSX II" }
        addInfo(head, bold: true)
        addInfo(st.proxyIsDefault ? L("Output: KEF LSX II (proxy) ✓", "Ausgabe: KEF LSX II (Proxy) ✓")
                                  : L("Output: another device", "Ausgabe: anderes Gerät"))
        if let h = Config.speakerHost { addInfo(L("Speaker: ", "Lautsprecher: ") + h + (Config.speakerHostOverride == nil ? " (Bonjour)" : "")) }
        if let f = st.realFormat { addInfo(L("USB format: ", "USB-Format: ") + f) }
        if let s = st.speakerSource { addInfo(L("Speaker source: ", "Quelle am Lautsprecher: ") + s) }
        if Config.restoreInputUIDPrefix != nil {
            addInfo(L("Input: ", "Eingang: ") + (st.inputPresent ? (st.inputIsDefault ? "✓" : L("present, not default", "vorhanden, nicht Standard")) : L("not connected", "nicht verbunden")))
        }
        if !st.onConsole { addInfo(L("Inactive: another user owns the screen", "Inaktiv: anderer Benutzer am Bildschirm")) }
        menu.addItem(.separator())

        // Volume range
        addInfo(L("Volume range (Mac 0–100 % → speaker)", "Lautstärkebereich (Mac 0–100 % → Lautsprecher)"))
        let rv = RangeView { [weak self] in self?.controller.settingsChanged(pushVolume: true) }
        rangeView = rv
        let rvItem = NSMenuItem(); rvItem.view = rv
        menu.addItem(rvItem)
        menu.addItem(.separator())

        // Settings
        addToggle(L("Keep proxy as default output", "Proxy als Standardausgabe halten"), key: "autoSwitchDefault", on: Config.autoSwitchDefault)
        addToggle(L("Wake speaker automatically on playback", "Lautsprecher bei Wiedergabe automatisch aufwecken"), key: "autoWakeOnPlayback", on: Config.autoWakeOnPlayback)
        addToggle(L("Show volume in menu bar", "Lautstärke in der Menüleiste anzeigen"), key: "showVolumeInMenuBar", on: Config.showVolumeInMenuBar)
        menu.addItem(inputMenuItem())
        // Design decision: No "speaker address" item in the menu; the
        // `speakerHost` default remains settable via `defaults write` as an override of the Bonjour discovery;
        // the status shows the host in use.
        menu.addItem(.separator())

        // Speaker actions
        let wake = NSMenuItem(title: L("Turn speaker off and on (USB)", "Lautsprecher aus- und einschalten (USB)"), action: #selector(powerCycle), keyEquivalent: "")
        wake.target = self; menu.addItem(wake)
        // Design decision: No source submenu (usb/wifi/bluetooth/…) in the menu — reason: for the
        // Mac the source must be USB anyway; the status line still shows the current source.
        menu.addItem(.separator())

        let all = NSMenuItem(title: L("Apply these settings for all users…", "Diese Einstellungen für alle Benutzer übernehmen…"), action: #selector(applyForAllUsers), keyEquivalent: "")
        all.target = self; menu.addItem(all)
        let logItem = NSMenuItem(title: L("Open log", "Log öffnen"), action: #selector(openLog), keyEquivalent: "")
        logItem.target = self; menu.addItem(logItem)
        let restart = NSMenuItem(title: L("Restart \(appName)", "\(appName) neu starten"), action: #selector(restartApp), keyEquivalent: "")
        restart.target = self; menu.addItem(restart)
        addInfo("\(appName) \(appVersion)")
    }

    func menuDidClose(_ menu: NSMenu) { rangeView = nil; refreshButton() }

    // Design decision: Menu bar icon in the shape of an LSX II — reason: the generic
    // speaker symbol did not look like the speaker.
    /// Menu bar template image shaped like the LSX II: a slightly tall rounded cabinet with the single
    /// Uni-Q driver (ring + concentric tweeter) in its upper half. Drawn in code so it stays crisp at
    /// every scale and follows the menu bar's light/dark appearance.
    static func speakerIcon() -> NSImage {
        let img = NSImage(size: NSSize(width: 18, height: 18), flipped: false) { _ in
            NSColor.black.set()
            let box = NSBezierPath(roundedRect: NSRect(x: 4, y: 1.5, width: 10, height: 15), xRadius: 2.2, yRadius: 2.2)
            box.lineWidth = 1.5; box.stroke()
            let c = NSPoint(x: 9, y: 9.2)
            let ring = NSBezierPath(ovalIn: NSRect(x: c.x - 3.1, y: c.y - 3.1, width: 6.2, height: 6.2))
            ring.lineWidth = 1.5; ring.stroke()
            NSBezierPath(ovalIn: NSRect(x: c.x - 1.1, y: c.y - 1.1, width: 2.2, height: 2.2)).fill()
            return true
        }
        img.isTemplate = true
        return img
    }

    func addInfo(_ title: String, bold: Bool = false) {
        let i = NSMenuItem(title: title, action: nil, keyEquivalent: "")
        i.isEnabled = false
        if bold { i.attributedTitle = NSAttributedString(string: title, attributes: [.font: NSFont.boldSystemFont(ofSize: NSFont.systemFontSize)]) }
        menu.addItem(i)
    }

    func addToggle(_ title: String, key: String, on: Bool) {
        let i = NSMenuItem(title: title, action: #selector(toggle(_:)), keyEquivalent: "")
        i.target = self; i.representedObject = key; i.state = on ? .on : .off
        menu.addItem(i)
    }

    @objc func toggle(_ sender: NSMenuItem) {
        guard let key = sender.representedObject as? String else { return }
        Config.set(sender.state != .on, forKey: key)
        log("setting \(key) = \(sender.state != .on)")
        controller.settingsChanged(pushVolume: false)
        refreshButton()
    }

    // MARK: input device

    func inputMenuItem() -> NSMenuItem {
        let parent = NSMenuItem(title: L("Restore input after USB reconnect", "Eingang nach USB-Neuerkennung wiederherstellen"), action: nil, keyEquivalent: "")
        let sub = NSMenu()
        let off = NSMenuItem(title: L("Off", "Aus"), action: #selector(chooseInput(_:)), keyEquivalent: "")
        off.target = self; off.representedObject = ""; off.state = Config.restoreInputUIDPrefix == nil ? .on : .off
        sub.addItem(off)
        sub.addItem(.separator())
        var matched = Config.restoreInputUIDPrefix == nil
        for d in inputDevices() {
            let prefix = Self.uidPrefix(d.uid)
            let i = NSMenuItem(title: d.name, action: #selector(chooseInput(_:)), keyEquivalent: "")
            i.target = self; i.representedObject = prefix
            if let cur = Config.restoreInputUIDPrefix, d.uid.hasPrefix(cur) { i.state = .on; matched = true }
            sub.addItem(i)
        }
        if !matched, let cur = Config.restoreInputUIDPrefix {
            let i = NSMenuItem(title: cur + L(" (not connected)", " (nicht verbunden)"), action: nil, keyEquivalent: "")
            i.state = .on; i.isEnabled = false; sub.addItem(i)
        }
        parent.submenu = sub
        return parent
    }

    /// Stable part of a device UID: Apple USB UIDs end in location/interface numbers that change with the port.
    static func uidPrefix(_ uid: String) -> String {
        let parts = uid.split(separator: ":", omittingEmptySubsequences: false)
        if parts.first == "AppleUSBAudioEngine", parts.count >= 3 { return parts[0...2].joined(separator: ":") }
        return uid
    }

    func inputDevices() -> [(name: String, uid: String)] {
        allDevices().compactMap { id in
            var a = addr(kAudioDevicePropertyStreams, kAudioObjectPropertyScopeInput)
            var size: UInt32 = 0
            guard AudioObjectGetPropertyDataSize(id, &a, 0, nil, &size) == noErr, size > 0 else { return nil }
            let uid = getString(id, kAudioDevicePropertyDeviceUID)
            guard uid != proxyUID else { return nil }
            return (getString(id, kAudioObjectPropertyName), uid)
        }
    }

    @objc func chooseInput(_ sender: NSMenuItem) {
        let prefix = (sender.representedObject as? String) ?? ""
        Config.set(prefix.isEmpty ? nil : prefix, forKey: "restoreInputUIDPrefix")
        log("setting restoreInputUIDPrefix = \(prefix.isEmpty ? "off" : prefix)")
        controller.settingsChanged(pushVolume: false)
    }

    // MARK: speaker

    @objc func powerCycle() { controller.powerCycleSpeaker() }

    // MARK: all users / misc

    /// Per-user defaults live in each account; the system-wide fallback in /Library/Preferences
    /// needs root, hence one admin prompt. Users who set their own value keep it.
    @objc func applyForAllUsers() {
        var cmds: [String] = []
        for k in Config.settingKeys {
            guard let v = Config.defaults.object(forKey: k) else { cmds.append("/usr/bin/defaults delete '/Library/Preferences/\(suiteName)' '\(k)' 2>/dev/null"); continue }
            let flag: String; let val: String
            if Config.boolKeys.contains(k) { flag = "-bool"; val = (v as? Bool ?? true) ? "true" : "false" }
            else if let n = v as? Int { flag = "-int"; val = String(n) }
            else { flag = "-string"; val = String(describing: v).replacingOccurrences(of: "'", with: "") }
            cmds.append("/usr/bin/defaults write '/Library/Preferences/\(suiteName)' '\(k)' \(flag) '\(val)'")
        }
        cmds.append("/bin/chmod 644 '/Library/Preferences/\(suiteName).plist'")
        let shell = cmds.joined(separator: "; ")
        let script = "do shell script \"\(shell.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "\"", with: "\\\""))\" with administrator privileges with prompt \"\(appName): \(L("write these settings as the default for all users", "diese Einstellungen als Vorgabe für alle Benutzer speichern"))\""
        NSApp.activate(ignoringOtherApps: true)
        var err: NSDictionary?
        NSAppleScript(source: script)?.executeAndReturnError(&err)
        log("apply for all users: \(err == nil ? "ok" : "failed/cancelled \(err?[NSAppleScript.errorNumber] ?? "")")")
    }

    @objc func openLog() { NSWorkspace.shared.open(logURL) }
    @objc func restartApp() { log("restart requested from menu"); exit(0) }   // launchd (KeepAlive) relaunches
}

/// Two sliders for the speaker range the Mac scale maps onto, with dB and step size readout.
final class RangeView: NSView {
    let onChange: () -> Void
    let maxSlider = NSSlider(), minSlider = NSSlider()
    let maxLabel = NSTextField(labelWithString: ""), minLabel = NSTextField(labelWithString: ""), stepLabel = NSTextField(wrappingLabelWithString: "")

    init(onChange: @escaping () -> Void) {
        self.onChange = onChange
        super.init(frame: NSRect(x: 0, y: 0, width: 300, height: 120))
        for (slider, min, max, value, sel) in [(maxSlider, 20.0, 100.0, Double(Config.maxSpeakerVolume), #selector(maxChanged)),
                                              (minSlider, 1.0, 40.0, Double(Config.minSpeakerVolume), #selector(minChanged))] {
            slider.minValue = min; slider.maxValue = max; slider.doubleValue = value
            slider.isContinuous = true; slider.target = self; slider.action = sel
            slider.controlSize = .small
        }
        for l in [maxLabel, minLabel, stepLabel] { l.font = NSFont.menuFont(ofSize: NSFont.smallSystemFontSize); l.isEditable = false; l.isBordered = false; l.drawsBackground = false }
        stepLabel.textColor = .secondaryLabelColor
        maxLabel.frame = NSRect(x: 14, y: 98, width: 140, height: 16); maxSlider.frame = NSRect(x: 150, y: 96, width: 136, height: 20)
        minLabel.frame = NSRect(x: 14, y: 72, width: 140, height: 16); minSlider.frame = NSRect(x: 150, y: 70, width: 136, height: 20)
        stepLabel.frame = NSRect(x: 14, y: 4, width: 272, height: 58)   // 4 lines of small menu font need 52 pt
        for v in [maxLabel, maxSlider, minLabel, minSlider, stepLabel] as [NSView] { addSubview(v) }
        updateLabels()
    }
    required init?(coder: NSCoder) { fatalError() }

    @objc func maxChanged() {
        let v = max(Int(maxSlider.doubleValue.rounded()), Config.minSpeakerVolume + 1)
        maxSlider.doubleValue = Double(v)
        Config.set(v, forKey: "maxSpeakerVolume")
        changed("maxSpeakerVolume = \(v)")
    }
    @objc func minChanged() {
        let v = min(Int(minSlider.doubleValue.rounded()), Config.maxSpeakerVolume - 1)
        minSlider.doubleValue = Double(v)
        Config.set(v, forKey: "minSpeakerVolume")
        changed("minSpeakerVolume = \(v)")
    }
    var logPending: DispatchWorkItem?
    func changed(_ what: String) {
        updateLabels()
        // Dragging fires continuously: one log line and one push per pause, not per pixel.
        logPending?.cancel()
        let w = DispatchWorkItem { log("setting \(what)"); self.onChange() }
        logPending = w
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.15, execute: w)
    }

    func updateLabels() {
        minSlider.doubleValue = Double(Config.minSpeakerVolume)   // defaults may have changed elsewhere
        maxSlider.doubleValue = Double(Config.maxSpeakerVolume)
        let hi = Config.maxSpeakerVolume, lo = Config.minSpeakerVolume
        func dB(_ v: Int) -> String { String(format: "%.1f dB", Config.dB(v)) }
        maxLabel.stringValue = L("Maximum: \(hi) (\(dB(hi)))", "Maximum: \(hi) (\(dB(hi)))")
        minLabel.stringValue = L("Minimum: \(lo) (\(dB(lo)))", "Minimum: \(lo) (\(dB(lo)))")
        let t = Config.keyStepTable()
        let stepDB = (Config.dB(t[15]) - Config.dB(t[0])) / 15
        let steps = t.map(String.init).joined(separator: " ")
        // Short on purpose: the menu is 300 pt wide; the full rule is in the README and docs/usb-volume-curve.md.
        stepLabel.stringValue = L("Each key: %.1f dB (equal steps on the measured curve, dB re speaker 65)\nSteps: %@",
                                  "Pro Taste: %.1f dB (gleiche Schritte auf der gemessenen Kurve, dB rel. Lautsprecher 65)\nStufen: %@")
        stepLabel.stringValue = String(format: stepLabel.stringValue, stepDB, steps)
    }
}
