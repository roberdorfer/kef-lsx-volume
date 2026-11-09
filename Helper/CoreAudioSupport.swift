import CoreAudio
import Foundation

// MARK: - CoreAudio helpers

func addr(_ sel: AudioObjectPropertySelector, _ scope: AudioObjectPropertyScope = kAudioObjectPropertyScopeGlobal, _ el: UInt32 = kAudioObjectPropertyElementMain) -> AudioObjectPropertyAddress {
    AudioObjectPropertyAddress(mSelector: sel, mScope: scope, mElement: el)
}

func getProp<T>(_ id: AudioObjectID, _ a: AudioObjectPropertyAddress, _ def: T) -> T? {
    var a = a
    var v = def
    var size = UInt32(MemoryLayout<T>.size)
    guard AudioObjectGetPropertyData(id, &a, 0, nil, &size, &v) == noErr else { return nil }
    return v
}

func setProp<T>(_ id: AudioObjectID, _ a: AudioObjectPropertyAddress, _ value: T) -> OSStatus {
    var a = a
    var v = value
    return AudioObjectSetPropertyData(id, &a, 0, nil, UInt32(MemoryLayout<T>.size), &v)
}

func getString(_ id: AudioObjectID, _ sel: AudioObjectPropertySelector) -> String {
    var a = addr(sel)
    var v: Unmanaged<CFString>? = nil
    var size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
    guard AudioObjectGetPropertyData(id, &a, 0, nil, &size, &v) == noErr else { return "" }
    return v?.takeRetainedValue() as String? ?? ""
}

func allDevices() -> [AudioObjectID] {
    var a = addr(kAudioHardwarePropertyDevices)
    var size: UInt32 = 0
    guard AudioObjectGetPropertyDataSize(AudioObjectID(kAudioObjectSystemObject), &a, 0, nil, &size) == noErr else { return [] }
    let n = Int(size) / MemoryLayout<AudioObjectID>.size
    var ids = [AudioObjectID](repeating: 0, count: n)
    guard AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &a, 0, nil, &size, &ids) == noErr else { return [] }
    return ids
}

func findProxy() -> AudioObjectID? { allDevices().first { getString($0, kAudioDevicePropertyDeviceUID) == proxyUID } }
func findInput() -> AudioObjectID? {
    guard let prefix = Config.restoreInputUIDPrefix else { return nil }
    return allDevices().first { getString($0, kAudioDevicePropertyDeviceUID).hasPrefix(prefix) }
}
func findReal() -> AudioObjectID? { allDevices().first { getString($0, kAudioDevicePropertyModelUID).contains(realModelMatch) } }
