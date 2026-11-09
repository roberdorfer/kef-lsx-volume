import AppKit

final class AppDelegate: NSObject, NSApplicationDelegate {
    var statusMenu: StatusMenu?
    let controller = Controller()
    func applicationDidFinishLaunching(_ notification: Notification) {
        log("\(appName) \(appVersion) starting; speaker=\(Config.speakerHostOverride ?? "auto (Bonjour)") range=\(Config.minSpeakerVolume)-\(Config.maxSpeakerVolume)")
        if Config.speakerHostOverride == nil { SpeakerDiscovery.shared.start() }   // main thread: needs the run loop
        controller.start()
        statusMenu = StatusMenu(controller: controller)
    }
}

let app = NSApplication.shared
app.setActivationPolicy(.accessory)   // menu bar only, no Dock icon (also LSUIElement in Info.plist)
let delegate = AppDelegate()
app.delegate = delegate
app.run()
