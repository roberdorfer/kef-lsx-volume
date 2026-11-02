import CoreAudio
import Foundation
// usage: kefstats            -> print stats + config of proxy device
//        kefstats set "buffer=64;jitterUs=500"
//        kefstats trace   -> dump the driver ring trace (W = host write, R = real IOProc)
func devices() -> [AudioObjectID] {
    var a = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyDevices, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
    var size: UInt32 = 0
    AudioObjectGetPropertyDataSize(AudioObjectID(kAudioObjectSystemObject), &a, 0, nil, &size)
    var ids = [AudioObjectID](repeating: 0, count: Int(size) / 4)
    AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &a, 0, nil, &size, &ids)
    return ids
}
func str(_ id: AudioObjectID, _ sel: AudioObjectPropertySelector) -> String? {
    var a = AudioObjectPropertyAddress(mSelector: sel, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
    var v: Unmanaged<CFString>? = nil
    var size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
    let st = AudioObjectGetPropertyData(id, &a, 0, nil, &size, &v)
    guard st == noErr else { return "err \(st)" }
    return v?.takeRetainedValue() as String?
}
func fourcc(_ s: String) -> AudioObjectPropertySelector { s.utf8.reduce(0) { ($0 << 8) | UInt32($1) } }
guard let p = devices().first(where: { str($0, kAudioDevicePropertyDeviceUID) == "io.github.roberdorfer.kefproxy.device" }) else { print("no proxy"); exit(1) }
let args = CommandLine.arguments
if args.count >= 3 && args[1] == "set" {
    var a = AudioObjectPropertyAddress(mSelector: fourcc("kcfg"), mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
    var v: Unmanaged<CFString>? = Unmanaged.passRetained(args[2] as CFString)
    let st = AudioObjectSetPropertyData(p, &a, 0, nil, UInt32(MemoryLayout<Unmanaged<CFString>?>.size), &v)
    print("set -> \(st)")
}
if args.count >= 2 && args[1] == "trace" { print(str(p, fourcc("ktrc")) ?? "nil"); exit(0) }
print("id=\(p)")
print("config: \(str(p, fourcc("kcfg")) ?? "nil")")
print("stats:  \(str(p, fourcc("kstt")) ?? "nil")")
var sa = AudioObjectPropertyAddress(mSelector: kAudioDevicePropertySafetyOffset, mScope: kAudioObjectPropertyScopeOutput, mElement: kAudioObjectPropertyElementMain)
var so: UInt32 = 0; var sz = UInt32(4)
AudioObjectGetPropertyData(p, &sa, 0, nil, &sz, &so)
print("safetyOffset(out)=\(so)")
