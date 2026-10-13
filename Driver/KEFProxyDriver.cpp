// KEFProxy — CoreAudio HAL AudioServerPlugIn
//
// Presents a virtual output device "KEF LSX II" with a fine-grained volume control
// and forwards audio samples *unchanged* to the real KEF LSX II USB device, which
// stays pinned at 0 dB host volume (the helper daemon does the pinning). The
// virtual volume is applied by the helper via the speaker's network API, so the
// signal path through this driver is bit-transparent (apart from the mute).
//
// Latency design: the virtual device's clock is derived from the real device's
// IOProc timestamps (no resampling, no drift). The host writes the mix for virtual
// sample time t into a ring buffer; the real device's IOProc reads the same sample
// times. The only added latency is the virtual safety offset, which must cover two
// real IO buffers plus a jitter margin (see kDefaultRealBufferFrames).
//
// Design decision: Volume must be controllable with the Mac keys, shown in
// the standard macOS volume HUD, with bit-perfect transport; remote/app-only control is not enough.
// Design decision: Minimise additional latency as far as possible.

#include <Block.h>
#include <CoreAudio/AudioServerPlugIn.h>
#include <CoreAudio/CoreAudio.h>
#include <CoreFoundation/CoreFoundation.h>
#include <dispatch/dispatch.h>
#include <mach/mach_time.h>
#include <os/log.h>
#include <pthread.h>

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstring>
#include <vector>

// ---------------------------------------------------------------------------
// Configuration
// ---------------------------------------------------------------------------

static CFStringRef const kDeviceName = CFSTR("KEF LSX II");
static CFStringRef const kDeviceUID = CFSTR("io.github.roberdorfer.kefproxy.device");
static CFStringRef const kDeviceModelUID = CFSTR("io.github.roberdorfer.kefproxy.model");
static CFStringRef const kManufacturer = CFSTR("kef-lsx-volume");
// Substring of the real device's model UID ("LSX II:27AC:1060").
static const char* const kTargetModelMatch = "27AC:1060";

static const UInt32 kDefaultRealBufferFrames = 64;   // IO buffer requested on the real device
static const UInt32 kDefaultJitterUs = 2000;       // extra margin inside the virtual safety offset
static const UInt32 kRingFrames = 1 << 15;           // 32768 frames ring (power of two)
static const UInt32 kChannels = 2;
// Design decision: The proxy runs at the LSX II's maximum format only (192 kHz, 32-bit float
// on the virtual side, 24-bit on the USB side) and must not be switchable to anything lower — reason:
// the output has to stay at maximum quality for every user and app. The driver therefore publishes a
// single rate and pushes the real device back to it; the helper pins the real device's physical format.
static const Float64 kSupportedRates[] = {192000};
static const Float64 kFixedRate = 192000;

enum : AudioObjectID {
    kObjectID_PlugIn = kAudioObjectPlugInObject,  // 1
    kObjectID_Device = 2,
    kObjectID_Stream = 3,
    kObjectID_Volume = 4,
    kObjectID_Mute = 5,
};

enum : UInt64 { kChangeAction_Rate = 1, kChangeAction_Target = 2 };

// Custom properties (global scope, main element, CFString data):
//   'kstt' read-only runtime statistics; 'kcfg' settable "buffer=N;jitter=M" (persisted via host storage).
static const AudioObjectPropertySelector kCustomProperty_Stats = 'kstt';
static const AudioObjectPropertySelector kCustomProperty_Config = 'kcfg';
static const AudioObjectPropertySelector kCustomProperty_Trace = 'ktrc';   // read-only: last IO events as text

// Lock-free event trace for timing analysis (W = host WriteMix, R = real IOProc).
struct TraceEvent { UInt64 host; SInt64 a; SInt64 b; SInt64 c; char type; };
static const UInt32 kTraceSize = 1024;
static TraceEvent gTrace[kTraceSize];
static std::atomic<UInt32> gTraceIdx{0};
static inline void Trace(char type, SInt64 a, SInt64 b, SInt64 c) {
    UInt32 i = gTraceIdx.fetch_add(1, std::memory_order_relaxed) % kTraceSize;
    gTrace[i] = {mach_absolute_time(), a, b, c, type};
}

static const Float32 kVolumeMinDB = -60.0f;
static const Float32 kVolumeMaxDB = 0.0f;

// ---------------------------------------------------------------------------
// Globals
// ---------------------------------------------------------------------------

static os_log_t gLog = os_log_create("io.github.roberdorfer.kefproxy", "driver");
static AudioServerPlugInDriverInterface* gInterfacePtr = nullptr;  // set in the factory
static AudioServerPlugInDriverRef gDriverRef = &gInterfacePtr;
static AudioServerPlugInHostRef gHost = nullptr;
static pthread_mutex_t gStateMutex = PTHREAD_MUTEX_INITIALIZER;
static dispatch_queue_t gQueue = nullptr;  // serial queue for HAL client calls to the real device

static Float64 gSampleRate = kFixedRate;
static Float32 gVolumeScalar = 0.5f;
static UInt32 gMute = 0;
static UInt32 gIOStartCount = 0;  // number of StartIO without StopIO
static UInt32 gRealBufferFrames = kDefaultRealBufferFrames;
static std::atomic<UInt32> gJitterUs{kDefaultJitterUs};  // HAL IO-thread wake jitter allowance, in microseconds (rate independent)
static std::atomic<SInt64> gHostLead{0};      // virtual host: output sample time - current sample time in DoIOOperation
static std::atomic<UInt32> gHostIOBuffer{0};  // virtual host IO buffer frames

// Real device state (protected by gQueue for HAL calls, atomics for IO thread)
static std::atomic<AudioObjectID> gTargetDevice{kAudioObjectUnknown};
static AudioDeviceIOProcID gTargetIOProcID = nullptr;
static AudioObjectID gRunningDevice = kAudioObjectUnknown;  // device the IOProc is registered on
static bool gTargetRunning = false;
static std::atomic<UInt32> gTargetLatency{0};
static std::atomic<UInt32> gTargetSafety{50};  // pre-attach default = LSX II's value; the host caches the derived safety offset at device publication
static std::atomic<UInt32> gTargetActualBuffer{kDefaultRealBufferFrames};

// Clock / ring state (IO threads)
// Clock model: virtual sample v(h) = s0 + (h - h0) * slope, slope in frames per host tick.
// GetZeroTimeStamp derives the zero timestamp from this line on demand, so the host sees a
// perfectly regular clock. Publishing timestamps only when the real IOProc crossed a period
// boundary made the host IO thread stall for ~8 ms at every boundary.
struct ClockModel { Float64 s0; Float64 h0; Float64 slope; };
static ClockModel gModel = {0, 0, 1};
static std::atomic<UInt32> gModelSeq{0};  // seqlock, odd while a write is in progress
static void SetModel(Float64 s0, Float64 h0, Float64 slope) {
    gModelSeq.fetch_add(1, std::memory_order_release);
    gModel = {s0, h0, slope};
    gModelSeq.fetch_add(1, std::memory_order_release);
}
static ClockModel ReadModel() {
    for (;;) {
        UInt32 a = gModelSeq.load(std::memory_order_acquire);
        if (a & 1) continue;
        ClockModel m = gModel;
        std::atomic_thread_fence(std::memory_order_acquire);
        if (gModelSeq.load(std::memory_order_relaxed) == a) return m;
    }
}
static inline Float64 ModelSample(const ClockModel& m, Float64 h) { return m.s0 + (h - m.h0) * m.slope; }
static inline Float64 ModelHost(const ClockModel& m, Float64 v) { return m.h0 + (v - m.s0) / m.slope; }
static std::atomic<bool> gClockValid{false};  // model is fed by the real device
// The zero-timestamp seed marks timeline *discontinuities* only (SimpleAudio returns a constant).
// Bumping it per period made the host resynchronise every 4096 frames: ~8 ms IO-thread stall and
// one dropped 512-frame block each time.
static std::atomic<UInt64> gTimelineSeed{1};
static Float64 gRealBaseSampleTime = 0;  // real sample time that maps to virtual sample 0 (IOProc thread only)
static bool gRealBaseValid = false;
static Float64 gFirstV = 0, gFirstH = 0;  // first real timestamp in virtual/host units (model anchor)
static Float32* gRing = nullptr;  // interleaved Float32, kRingFrames * kChannels
static std::atomic<SInt64> gWrittenUpTo{0};  // virtual frames written (exclusive)
static std::atomic<UInt64> gUnderruns{0};
static std::atomic<UInt64> gCallbacks{0};
static std::atomic<SInt64> gMinMargin{1 << 30};  // smallest (written - needed) seen after warm-up
static std::atomic<SInt64> gOutputAhead{0};       // real device: output sample time - now sample time
static std::atomic<SInt64> gOutHostAheadUs{0};    // real device: output host time - mach_absolute_time at first callback, in us
static std::atomic<UInt32> gOutFlags{0};          // real device: inOutputTime->mFlags at first callback
static std::atomic<UInt64> gFirstUnderrunAt{0};   // callback index of first/last underrun (0 = none)
static std::atomic<UInt64> gLastUnderrunAt{0};
static std::atomic<SInt64> gMaxHostLate{0};       // virtual host IO thread: max (mach now - mCurrentTime.mHostTime) in frames
static std::atomic<SInt64> gNowSkew{0};           // virtual host: our clock's now - host's mCurrentTime.mSampleTime (frames), last cycle

// Provisional clock: host-time based timeline used until the real device's first
// callback (and permanently while no real device is present).
static std::atomic<bool> gIORunning{false};
static std::atomic<UInt64> gStartHostTime{0};
static std::atomic<bool> gSinkPresent{false};  // real IOProc registered; otherwise audio is discarded

static mach_timebase_info_data_t gTimebase;
static Float64 gHostTicksPerSecond = 1e9;

static inline UInt32 ZeroPeriod() { return 4096; }
static inline UInt32 VirtualSafetyOffset();
static inline UInt32 JitterFrames();

static void ApplyConfigString(CFStringRef cfg) {
    char buf[256] = {0};
    if (!cfg || !CFStringGetCString(cfg, buf, sizeof(buf), kCFStringEncodingUTF8)) return;
    char* save = nullptr;
    for (char* tok = strtok_r(buf, ";", &save); tok; tok = strtok_r(nullptr, ";", &save)) {
        char* eq = strchr(tok, '=');
        if (!eq) continue;
        *eq = 0;
        long v = strtol(eq + 1, nullptr, 10);
        if (!strcmp(tok, "buffer") && v >= 16 && v <= 4096) gRealBufferFrames = (UInt32)v;
        if (!strcmp(tok, "jitterUs") && v >= 0 && v <= 50000) gJitterUs.store((UInt32)v);
    }
}

static CFStringRef CopyConfigString() {
    return CFStringCreateWithFormat(nullptr, nullptr, CFSTR("buffer=%u;jitterUs=%u"), gRealBufferFrames, gJitterUs.load());
}

static CFStringRef CopyTraceString() {
    CFMutableStringRef out = CFStringCreateMutable(nullptr, 0);
    UInt32 end = gTraceIdx.load();
    UInt32 n = std::min<UInt32>(end, kTraceSize);
    UInt64 t0 = 0;
    for (UInt32 k = 0; k < n; ++k) {
        const TraceEvent& e = gTrace[(end - n + k) % kTraceSize];
        if (!t0) t0 = e.host;
        CFStringAppendFormat(out, nullptr, CFSTR("%c %.1f %lld %lld %lld\n"), e.type, (Float64)(e.host - t0) / gHostTicksPerSecond * 1e6, e.a, e.b, e.c);
    }
    return out;
}

static CFStringRef CopyStatsString() {
    pthread_mutex_lock(&gStateMutex);
    Float64 rate = gSampleRate;
    UInt32 running = gIOStartCount;
    pthread_mutex_unlock(&gStateMutex);
    return CFStringCreateWithFormat(nullptr, nullptr,
        CFSTR("rate=%.0f;ioClients=%u;realDevice=%u;sink=%d;clockValid=%d;realBuffer=%u;realLatency=%u;realSafety=%u;virtualSafety=%u;outputAhead=%lld;callbacks=%llu;underruns=%llu;minMargin=%lld;firstUnderrunAt=%llu;lastUnderrunAt=%llu;hostLead=%lld;hostIOBuffer=%u;maxHostLate=%lld;nowSkew=%lld;outHostAheadUs=%lld;outFlags=%u;requestedBuffer=%u;jitterUs=%u;jitterFrames=%u"),
        rate, running, gTargetDevice.load(), (int)gSinkPresent.load(), (int)gClockValid.load(), gTargetActualBuffer.load(),
        gTargetLatency.load(), gTargetSafety.load(), VirtualSafetyOffset(), gOutputAhead.load(), gCallbacks.load(),
        gUnderruns.load(), gMinMargin.load() == (1 << 30) ? (SInt64)-1 : gMinMargin.load(), gFirstUnderrunAt.load(), gLastUnderrunAt.load(), gHostLead.load(), gHostIOBuffer.load(), gMaxHostLate.load(), gNowSkew.load(), gOutHostAheadUs.load(), gOutFlags.load(), gRealBufferFrames, gJitterUs.load(), JitterFrames());
}
static inline UInt32 JitterFrames() { return (UInt32)std::ceil((Float64)gJitterUs.load() * gSampleRate / 1e6); }
// Why safety + 2*realBuffer + jitter: the real IOProc consumes a buffer that the real device plays
// realSafety + realBuffer frames after 'now', and it needs the whole buffer (another realBuffer) present
// in the ring; the jitter term covers the virtual HAL IO thread waking late.
static inline UInt32 VirtualSafetyOffset() { return gTargetSafety.load() + 2 * gTargetActualBuffer.load() + JitterFrames(); }

// ---------------------------------------------------------------------------
// Real device discovery and control (runs on gQueue)
// ---------------------------------------------------------------------------

static CFStringRef CopyStringProp(AudioObjectID obj, AudioObjectPropertySelector sel) {
    AudioObjectPropertyAddress addr = {sel, kAudioObjectPropertyScopeGlobal, kAudioObjectPropertyElementMain};
    CFStringRef s = nullptr;
    UInt32 size = sizeof(s);
    if (AudioObjectGetPropertyData(obj, &addr, 0, nullptr, &size, &s) != noErr) return nullptr;
    return s;
}

static UInt32 GetUInt32Prop(AudioObjectID obj, AudioObjectPropertySelector sel, AudioObjectPropertyScope scope, UInt32 def) {
    AudioObjectPropertyAddress addr = {sel, scope, kAudioObjectPropertyElementMain};
    UInt32 v = def, size = sizeof(v);
    if (AudioObjectGetPropertyData(obj, &addr, 0, nullptr, &size, &v) != noErr) return def;
    return v;
}

static Float64 GetRealRate(AudioObjectID dev) {
    AudioObjectPropertyAddress addr = {kAudioDevicePropertyNominalSampleRate, kAudioObjectPropertyScopeGlobal, kAudioObjectPropertyElementMain};
    Float64 r = 0;
    UInt32 size = sizeof(r);
    AudioObjectGetPropertyData(dev, &addr, 0, nullptr, &size, &r);
    return r;
}

static void NotifyHost(AudioObjectID obj, std::initializer_list<AudioObjectPropertySelector> sels, AudioObjectPropertyScope scope = kAudioObjectPropertyScopeGlobal) {
    if (!gHost) return;
    std::vector<AudioObjectPropertyAddress> addrs;
    for (auto s : sels) addrs.push_back({s, scope, kAudioObjectPropertyElementMain});
    gHost->PropertiesChanged(gHost, obj, (UInt32)addrs.size(), addrs.data());
}

static void RequestRateChange(Float64 newRate) {
    if (!gHost) return;
    UInt64 bits;
    memcpy(&bits, &newRate, sizeof(bits));
    gHost->RequestDeviceConfigurationChange(gHost, kObjectID_Device, kChangeAction_Rate, (void*)(uintptr_t)bits);
}

static OSStatus RealDeviceIOProc(AudioObjectID inDevice, const AudioTimeStamp* inNow, const AudioBufferList* inInputData,
                                 const AudioTimeStamp* inInputTime, AudioBufferList* outOutputData,
                                 const AudioTimeStamp* inOutputTime, void* inClientData);

static void SetRealRate(Float64 rate);
static void RealRateListener(AudioObjectID dev) {
    Float64 r = GetRealRate(dev);
    pthread_mutex_lock(&gStateMutex);
    Float64 ours = gSampleRate;
    pthread_mutex_unlock(&gStateMutex);
    if (r > 0 && std::fabs(r - ours) > 0.5) {
        os_log(gLog, "real device rate changed to %.0f, forcing back to %.0f", r, ours);
        SetRealRate(ours);
    }
}

// The one rate listener installed on the current real device (AttachTarget/FindTarget run on gQueue).
// Every USB re-enumeration gives a new device ID; the listener on the old ID is removed, not leaked.
static const AudioObjectPropertyAddress kRateAddr = {kAudioDevicePropertyNominalSampleRate, kAudioObjectPropertyScopeGlobal, kAudioObjectPropertyElementMain};
static AudioObjectPropertyListenerBlock gRateListener = nullptr;
static AudioObjectID gRateListenerDevice = kAudioObjectUnknown;

static void RemoveRateListener() {
    if (!gRateListener) return;
    AudioObjectRemovePropertyListenerBlock(gRateListenerDevice, &kRateAddr, gQueue, gRateListener);
    Block_release(gRateListener);
    gRateListener = nullptr;
    gRateListenerDevice = kAudioObjectUnknown;
}

static void AttachTarget(AudioObjectID dev) {
    gTargetDevice.store(dev);
    gTargetLatency.store(GetUInt32Prop(dev, kAudioDevicePropertyLatency, kAudioObjectPropertyScopeOutput, 0));
    gTargetSafety.store(GetUInt32Prop(dev, kAudioDevicePropertySafetyOffset, kAudioObjectPropertyScopeOutput, 0));
    RemoveRateListener();
    gRateListener = Block_copy(^(UInt32, const AudioObjectPropertyAddress*) { RealRateListener(dev); });
    gRateListenerDevice = dev;
    AudioObjectAddPropertyListenerBlock(dev, &kRateAddr, gQueue, gRateListener);
    Float64 r = GetRealRate(dev);
    os_log(gLog, "attached real device %u rate=%.0f latency=%u safety=%u", dev, r, gTargetLatency.load(), gTargetSafety.load());
    pthread_mutex_lock(&gStateMutex);
    Float64 ours = gSampleRate;
    pthread_mutex_unlock(&gStateMutex);
    if (r > 0 && std::fabs(r - ours) > 0.5) SetRealRate(ours);
    NotifyHost(kObjectID_Device, {kAudioDevicePropertyLatency, kAudioDevicePropertySafetyOffset}, kAudioObjectPropertyScopeOutput);
}

static void StopRealIO();
static void StartRealIO();

static void FindTarget() {
    AudioObjectPropertyAddress addr = {kAudioHardwarePropertyDevices, kAudioObjectPropertyScopeGlobal, kAudioObjectPropertyElementMain};
    UInt32 size = 0;
    if (AudioObjectGetPropertyDataSize(kAudioObjectSystemObject, &addr, 0, nullptr, &size) != noErr) return;
    std::vector<AudioObjectID> devs(size / sizeof(AudioObjectID));
    if (AudioObjectGetPropertyData(kAudioObjectSystemObject, &addr, 0, nullptr, &size, devs.data()) != noErr) return;
    AudioObjectID found = kAudioObjectUnknown;
    for (AudioObjectID d : devs) {
        CFStringRef model = CopyStringProp(d, kAudioDevicePropertyModelUID);
        if (!model) continue;
        char buf[256];
        bool ok = CFStringGetCString(model, buf, sizeof(buf), kCFStringEncodingUTF8);
        CFRelease(model);
        if (ok && strstr(buf, kTargetModelMatch)) { found = d; break; }
    }
    AudioObjectID current = gTargetDevice.load();
    if (found == current) return;
    if (current != kAudioObjectUnknown) os_log(gLog, "real device %u disappeared", current);
    if (found != kAudioObjectUnknown) AttachTarget(found); else { RemoveRateListener(); gTargetDevice.store(kAudioObjectUnknown); }
    // The host stops IO (if running), calls PerformDeviceConfigurationChange, and restarts IO; that
    // moves the clock source cleanly between the real device and the fallback timer. Requested also when
    // not running: the host caches Latency/SafetyOffset and only re-reads them inside a configuration change.
    if (gHost) gHost->RequestDeviceConfigurationChange(gHost, kObjectID_Device, kChangeAction_Target, nullptr);
}

static void StartRealIO() {
    AudioObjectID dev = gTargetDevice.load();
    if (dev == kAudioObjectUnknown) { os_log(gLog, "no real device; provisional clock only"); return; }
    if (gTargetRunning) return;
    AudioObjectPropertyAddress bufAddr = {kAudioDevicePropertyBufferFrameSize, kAudioObjectPropertyScopeGlobal, kAudioObjectPropertyElementMain};
    UInt32 want = gRealBufferFrames;
    AudioObjectSetPropertyData(dev, &bufAddr, 0, nullptr, sizeof(want), &want);
    UInt32 got = want, size = sizeof(got);
    AudioObjectGetPropertyData(dev, &bufAddr, 0, nullptr, &size, &got);
    gTargetActualBuffer.store(got);
    gRealBaseValid = false;
    gClockValid.store(false);
    gWrittenUpTo.store(0);
    gUnderruns.store(0);
    gCallbacks.store(0);
    gMinMargin.store(1 << 30);
    gFirstUnderrunAt.store(0); gLastUnderrunAt.store(0); gMaxHostLate.store(0); gNowSkew.store(0);
    memset(gRing, 0, sizeof(Float32) * kRingFrames * kChannels);
    OSStatus err = AudioDeviceCreateIOProcID(dev, RealDeviceIOProc, nullptr, &gTargetIOProcID);
    if (err != noErr) { os_log_error(gLog, "CreateIOProcID failed %d", (int)err); return; }
    gTargetRunning = true;
    gRunningDevice = dev;
    gSinkPresent.store(true);
    err = AudioDeviceStart(dev, gTargetIOProcID);
    if (err != noErr) {
        os_log_error(gLog, "AudioDeviceStart failed %d", (int)err);
        AudioDeviceDestroyIOProcID(dev, gTargetIOProcID);
        gTargetIOProcID = nullptr;
        gTargetRunning = false;
        gRunningDevice = kAudioObjectUnknown;
        gSinkPresent.store(false);
        return;
    }
    os_log(gLog, "real IO started buffer=%u virtualSafety=%u", got, VirtualSafetyOffset());
}

static void StopRealIO() {
    gSinkPresent.store(false);
    if (gTargetRunning && gRunningDevice != kAudioObjectUnknown && gTargetIOProcID) {
        AudioDeviceStop(gRunningDevice, gTargetIOProcID);
        AudioDeviceDestroyIOProcID(gRunningDevice, gTargetIOProcID);
    }
    gTargetIOProcID = nullptr;
    gRunningDevice = kAudioObjectUnknown;
    gTargetRunning = false;
    gClockValid.store(false);
    os_log(gLog, "real IO stopped callbacks=%llu underruns=%llu minMargin=%lld frames", gCallbacks.load(), gUnderruns.load(), gMinMargin.load());
}

static void SetRealRate(Float64 rate) {
    AudioObjectID dev = gTargetDevice.load();
    if (dev == kAudioObjectUnknown) return;
    if (std::fabs(GetRealRate(dev) - rate) < 0.5) return;
    AudioObjectPropertyAddress addr = {kAudioDevicePropertyNominalSampleRate, kAudioObjectPropertyScopeGlobal, kAudioObjectPropertyElementMain};
    OSStatus err = AudioObjectSetPropertyData(dev, &addr, 0, nullptr, sizeof(rate), &rate);
    os_log(gLog, "set real device rate %.0f -> %d", rate, (int)err);
}

// ---------------------------------------------------------------------------
// Real device IOProc — the clock source and the ring reader (real-time thread)
// ---------------------------------------------------------------------------

static OSStatus RealDeviceIOProc(AudioObjectID, const AudioTimeStamp* inNow, const AudioBufferList*, const AudioTimeStamp*,
                                 AudioBufferList* outOutputData, const AudioTimeStamp* inOutputTime, void*) {
    if (!outOutputData || outOutputData->mNumberBuffers == 0) return noErr;
    const UInt32 bytesPerFrame = outOutputData->mBuffers[0].mNumberChannels * sizeof(Float32);
    const UInt32 frames = bytesPerFrame ? outOutputData->mBuffers[0].mDataByteSize / bytesPerFrame : 0;
    if (frames == 0) return noErr;

    if (!gIORunning.load(std::memory_order_acquire)) {
        for (UInt32 b = 0; b < outOutputData->mNumberBuffers; ++b)
            if (outOutputData->mBuffers[b].mData) memset(outOutputData->mBuffers[b].mData, 0, outOutputData->mBuffers[b].mDataByteSize);
        return noErr;
    }
    const Float64 rate = gSampleRate;
    const Float64 outHost = (Float64)inOutputTime->mHostTime;
    if (!gRealBaseValid) {
        // Continue the provisional (host-clock) timeline seamlessly and re-anchor the model here.
        const ClockModel m = ReadModel();
        const Float64 vFirst = ModelSample(m, outHost);
        gRealBaseSampleTime = inOutputTime->mSampleTime - vFirst;
        gFirstV = vFirst;
        gFirstH = outHost;
        gRealBaseValid = true;
        SetModel(vFirst, outHost, m.slope);
        gClockValid.store(true);
    }
    // Virtual sample time of this buffer.
    const Float64 vStart = inOutputTime->mSampleTime - gRealBaseSampleTime;
    // Track the real device's actual rate once the baseline is long enough (>= 200 ms).
    if (outHost - gFirstH > 0.2 * gHostTicksPerSecond) SetModel(gFirstV, gFirstH, (vStart - gFirstV) / (outHost - gFirstH));
    const UInt64 n = gCallbacks.fetch_add(1);
    if (n == 0) {
        gOutputAhead.store((SInt64)(inOutputTime->mSampleTime - inNow->mSampleTime));
        gOutHostAheadUs.store((SInt64)(((Float64)inOutputTime->mHostTime - (Float64)mach_absolute_time()) / gHostTicksPerSecond * 1e6));
        gOutFlags.store(inOutputTime->mFlags);
        os_log(gLog, "first callback: frames=%u outputAhead=%lld frames (output-now) base=%.0f", frames, gOutputAhead.load(), gRealBaseSampleTime);
    }

    const SInt64 start = (SInt64)vStart;
    const bool mute = gMute != 0;
    const SInt64 written = gWrittenUpTo.load(std::memory_order_acquire);
    const SInt64 margin = written - (start + (SInt64)frames);
    Trace('R', start, written, (SInt64)(((Float64)inOutputTime->mHostTime - (Float64)mach_absolute_time()) / gHostTicksPerSecond * rate));
    if (start > (SInt64)VirtualSafetyOffset() * 4) {  // past warm-up
        if (margin < 0) {
            gUnderruns.fetch_add(1);
            UInt64 none = 0;
            gFirstUnderrunAt.compare_exchange_strong(none, n + 1);
            gLastUnderrunAt.store(n + 1);
        }
        SInt64 prevMin = gMinMargin.load(std::memory_order_relaxed);
        while (margin < prevMin && !gMinMargin.compare_exchange_weak(prevMin, margin)) {}
        if ((n & 0x3fff) == 0) os_log_debug(gLog, "stats callbacks=%llu underruns=%llu minMargin=%lld", n, gUnderruns.load(), gMinMargin.load());
    }

    for (UInt32 b = 0; b < outOutputData->mNumberBuffers; ++b) {
        AudioBuffer& buf = outOutputData->mBuffers[b];
        Float32* out = (Float32*)buf.mData;
        const UInt32 ch = buf.mNumberChannels;
        if (mute || !out) { if (out) memset(out, 0, buf.mDataByteSize); continue; }
        for (UInt32 f = 0; f < frames; ++f) {
            const UInt32 idx = (UInt32)((start + f) & (kRingFrames - 1));
            Float32* src = gRing + (size_t)idx * kChannels;
            for (UInt32 c = 0; c < ch; ++c) {
                const UInt32 vc = (outOutputData->mNumberBuffers > 1) ? b : c;  // de-interleaved vs interleaved target
                out[f * ch + c] = (vc < kChannels) ? src[vc] : 0.0f;
            }
        }
    }
    // Clear what we consumed so a later underrun plays silence, not stale audio.
    for (UInt32 f = 0; f < frames; ++f) {
        const UInt32 idx = (UInt32)((start + f) & (kRingFrames - 1));
        memset(gRing + (size_t)idx * kChannels, 0, sizeof(Float32) * kChannels);
    }
    return noErr;
}

// ---------------------------------------------------------------------------
// Helpers for property code
// ---------------------------------------------------------------------------

static inline bool IsOutputScope(AudioObjectPropertyScope s) { return s == kAudioObjectPropertyScopeOutput || s == kAudioObjectPropertyScopeGlobal; }

static AudioStreamBasicDescription MakeASBD(Float64 rate) {
    AudioStreamBasicDescription d = {};
    d.mSampleRate = rate;
    d.mFormatID = kAudioFormatLinearPCM;
    d.mFormatFlags = kAudioFormatFlagIsFloat | kAudioFormatFlagsNativeEndian | kAudioFormatFlagIsPacked;
    d.mBytesPerPacket = kChannels * sizeof(Float32);
    d.mFramesPerPacket = 1;
    d.mBytesPerFrame = kChannels * sizeof(Float32);
    d.mChannelsPerFrame = kChannels;
    d.mBitsPerChannel = 32;
    return d;
}

static Float32 ScalarToDB(Float32 s) { s = std::clamp(s, 0.0f, 1.0f); return kVolumeMinDB + (kVolumeMaxDB - kVolumeMinDB) * s; }
static Float32 DBToScalar(Float32 db) { db = std::clamp(db, kVolumeMinDB, kVolumeMaxDB); return (db - kVolumeMinDB) / (kVolumeMaxDB - kVolumeMinDB); }

#define RET_CFSTRING(str)                                              \
    do {                                                               \
        if (inDataSize < sizeof(CFStringRef)) return kAudioHardwareBadPropertySizeError; \
        *((CFStringRef*)outData) = (CFStringRef)CFRetain(str);         \
        *outDataSize = sizeof(CFStringRef);                            \
        return noErr;                                                  \
    } while (0)

#define RET_U32(val)                                                   \
    do {                                                               \
        if (inDataSize < sizeof(UInt32)) return kAudioHardwareBadPropertySizeError; \
        *((UInt32*)outData) = (UInt32)(val);                           \
        *outDataSize = sizeof(UInt32);                                 \
        return noErr;                                                  \
    } while (0)

#define RET_F32(val)                                                   \
    do {                                                               \
        if (inDataSize < sizeof(Float32)) return kAudioHardwareBadPropertySizeError; \
        *((Float32*)outData) = (Float32)(val);                         \
        *outDataSize = sizeof(Float32);                                \
        return noErr;                                                  \
    } while (0)

#define RET_F64(val)                                                   \
    do {                                                               \
        if (inDataSize < sizeof(Float64)) return kAudioHardwareBadPropertySizeError; \
        *((Float64*)outData) = (Float64)(val);                         \
        *outDataSize = sizeof(Float64);                                \
        return noErr;                                                  \
    } while (0)

// ---------------------------------------------------------------------------
// Property implementation
// ---------------------------------------------------------------------------

static Boolean PlugIn_HasProperty(const AudioObjectPropertyAddress* a) {
    switch (a->mSelector) {
        case kAudioObjectPropertyBaseClass: case kAudioObjectPropertyClass: case kAudioObjectPropertyOwner:
        case kAudioObjectPropertyManufacturer: case kAudioObjectPropertyOwnedObjects:
        case kAudioPlugInPropertyDeviceList: case kAudioPlugInPropertyTranslateUIDToDevice:
        case kAudioPlugInPropertyResourceBundle:
            return true;
    }
    return false;
}

static Boolean Device_HasProperty(const AudioObjectPropertyAddress* a) {
    switch (a->mSelector) {
        case kAudioObjectPropertyBaseClass: case kAudioObjectPropertyClass: case kAudioObjectPropertyOwner:
        case kAudioObjectPropertyName: case kAudioObjectPropertyManufacturer: case kAudioObjectPropertyOwnedObjects:
        case kAudioDevicePropertyDeviceUID: case kAudioDevicePropertyModelUID: case kAudioDevicePropertyTransportType:
        case kAudioDevicePropertyRelatedDevices: case kAudioDevicePropertyClockDomain: case kAudioDevicePropertyDeviceIsAlive:
        case kAudioDevicePropertyDeviceIsRunning: case kAudioDevicePropertyDeviceCanBeDefaultDevice:
        case kAudioDevicePropertyDeviceCanBeDefaultSystemDevice: case kAudioDevicePropertyLatency:
        case kAudioDevicePropertyStreams: case kAudioObjectPropertyControlList: case kAudioDevicePropertySafetyOffset:
        case kAudioDevicePropertyNominalSampleRate: case kAudioDevicePropertyAvailableNominalSampleRates:
        case kAudioDevicePropertyIsHidden: case kAudioDevicePropertyZeroTimeStampPeriod:
        case kAudioObjectPropertyCustomPropertyInfoList:
            return true;
        case kCustomProperty_Stats: case kCustomProperty_Config: case kCustomProperty_Trace:
            return a->mScope == kAudioObjectPropertyScopeGlobal;
        case kAudioDevicePropertyPreferredChannelsForStereo: case kAudioDevicePropertyPreferredChannelLayout:
            return IsOutputScope(a->mScope);
    }
    return false;
}

static Boolean Stream_HasProperty(const AudioObjectPropertyAddress* a) {
    switch (a->mSelector) {
        case kAudioObjectPropertyBaseClass: case kAudioObjectPropertyClass: case kAudioObjectPropertyOwner:
        case kAudioStreamPropertyIsActive: case kAudioStreamPropertyDirection: case kAudioStreamPropertyTerminalType:
        case kAudioStreamPropertyStartingChannel: case kAudioStreamPropertyLatency: case kAudioStreamPropertyVirtualFormat:
        case kAudioStreamPropertyPhysicalFormat: case kAudioStreamPropertyAvailableVirtualFormats:
        case kAudioStreamPropertyAvailablePhysicalFormats:
            return true;
    }
    return false;
}

static Boolean Control_HasProperty(AudioObjectID obj, const AudioObjectPropertyAddress* a) {
    switch (a->mSelector) {
        case kAudioObjectPropertyBaseClass: case kAudioObjectPropertyClass: case kAudioObjectPropertyOwner:
        case kAudioControlPropertyScope: case kAudioControlPropertyElement:
            return true;
        case kAudioLevelControlPropertyScalarValue: case kAudioLevelControlPropertyDecibelValue:
        case kAudioLevelControlPropertyDecibelRange: case kAudioLevelControlPropertyConvertScalarToDecibels:
        case kAudioLevelControlPropertyConvertDecibelsToScalar:
            return obj == kObjectID_Volume;
        case kAudioBooleanControlPropertyValue:
            return obj == kObjectID_Mute;
    }
    return false;
}

static OSStatus PlugIn_GetPropertyData(const AudioObjectPropertyAddress* a, UInt32 qualSize, const void* qual, UInt32 inDataSize, UInt32* outDataSize, void* outData) {
    switch (a->mSelector) {
        case kAudioObjectPropertyBaseClass: RET_U32(kAudioObjectClassID);
        case kAudioObjectPropertyClass: RET_U32(kAudioPlugInClassID);
        case kAudioObjectPropertyOwner: RET_U32(kAudioObjectUnknown);
        case kAudioObjectPropertyManufacturer: RET_CFSTRING(kManufacturer);
        case kAudioObjectPropertyOwnedObjects:
        case kAudioPlugInPropertyDeviceList:
            if (inDataSize < sizeof(AudioObjectID)) { *outDataSize = 0; return noErr; }
            *((AudioObjectID*)outData) = kObjectID_Device;
            *outDataSize = sizeof(AudioObjectID);
            return noErr;
        case kAudioPlugInPropertyTranslateUIDToDevice: {
            if (qualSize != sizeof(CFStringRef) || !qual) return kAudioHardwareBadPropertySizeError;
            CFStringRef uid = *(const CFStringRef*)qual;
            RET_U32((uid && CFStringCompare(uid, kDeviceUID, 0) == kCFCompareEqualTo) ? kObjectID_Device : kAudioObjectUnknown);
        }
        case kAudioPlugInPropertyResourceBundle: RET_CFSTRING(CFSTR(""));
    }
    return kAudioHardwareUnknownPropertyError;
}

static OSStatus Device_GetPropertyData(const AudioObjectPropertyAddress* a, UInt32 qualSize, const void* qual, UInt32 inDataSize, UInt32* outDataSize, void* outData) {
    switch (a->mSelector) {
        case kAudioObjectPropertyBaseClass: RET_U32(kAudioObjectClassID);
        case kAudioObjectPropertyClass: RET_U32(kAudioDeviceClassID);
        case kAudioObjectPropertyOwner: RET_U32(kObjectID_PlugIn);
        case kAudioObjectPropertyName: RET_CFSTRING(kDeviceName);
        case kAudioObjectPropertyManufacturer: RET_CFSTRING(kManufacturer);
        case kAudioObjectPropertyOwnedObjects:
        case kAudioObjectPropertyControlList:
        case kAudioDevicePropertyStreams: {
            std::vector<AudioObjectID> ids;
            bool wantStreams = a->mSelector == kAudioDevicePropertyStreams;
            bool wantControls = a->mSelector == kAudioObjectPropertyControlList;
            if (a->mSelector == kAudioObjectPropertyOwnedObjects) {
                wantStreams = wantControls = true;
                if (qualSize >= sizeof(AudioClassID) && qual) {
                    UInt32 n = qualSize / sizeof(AudioClassID);
                    wantStreams = wantControls = false;
                    for (UInt32 i = 0; i < n; ++i) {
                        AudioClassID c = ((const AudioClassID*)qual)[i];
                        if (c == kAudioStreamClassID) wantStreams = true;
                        if (c == kAudioControlClassID || c == kAudioLevelControlClassID || c == kAudioVolumeControlClassID ||
                            c == kAudioBooleanControlClassID || c == kAudioMuteControlClassID) wantControls = true;
                    }
                }
            }
            if (wantStreams && IsOutputScope(a->mScope)) ids.push_back(kObjectID_Stream);
            if (wantControls && IsOutputScope(a->mScope)) { ids.push_back(kObjectID_Volume); ids.push_back(kObjectID_Mute); }
            UInt32 n = std::min<UInt32>((UInt32)ids.size(), inDataSize / sizeof(AudioObjectID));
            for (UInt32 i = 0; i < n; ++i) ((AudioObjectID*)outData)[i] = ids[i];
            *outDataSize = n * sizeof(AudioObjectID);
            return noErr;
        }
        case kAudioDevicePropertyDeviceUID: RET_CFSTRING(kDeviceUID);
        case kAudioDevicePropertyModelUID: RET_CFSTRING(kDeviceModelUID);
        case kAudioDevicePropertyTransportType: RET_U32(kAudioDeviceTransportTypeVirtual);
        case kAudioDevicePropertyRelatedDevices:
            if (inDataSize < sizeof(AudioObjectID)) { *outDataSize = 0; return noErr; }
            *((AudioObjectID*)outData) = kObjectID_Device;
            *outDataSize = sizeof(AudioObjectID);
            return noErr;
        case kAudioDevicePropertyClockDomain: RET_U32(0);
        case kAudioDevicePropertyDeviceIsAlive: RET_U32(1);
        case kAudioDevicePropertyDeviceIsRunning: {
            pthread_mutex_lock(&gStateMutex);
            UInt32 r = gIOStartCount > 0;
            pthread_mutex_unlock(&gStateMutex);
            RET_U32(r);
        }
        case kAudioDevicePropertyDeviceCanBeDefaultDevice: RET_U32(1);
        case kAudioDevicePropertyDeviceCanBeDefaultSystemDevice: RET_U32(1);
        case kAudioDevicePropertyLatency: RET_U32(gTargetLatency.load());
        case kAudioDevicePropertySafetyOffset: RET_U32(VirtualSafetyOffset());
        case kAudioDevicePropertyNominalSampleRate: {
            pthread_mutex_lock(&gStateMutex);
            Float64 r = gSampleRate;
            pthread_mutex_unlock(&gStateMutex);
            RET_F64(r);
        }
        case kAudioDevicePropertyAvailableNominalSampleRates: {
            UInt32 count = sizeof(kSupportedRates) / sizeof(kSupportedRates[0]);
            UInt32 n = std::min<UInt32>(count, inDataSize / sizeof(AudioValueRange));
            for (UInt32 i = 0; i < n; ++i) ((AudioValueRange*)outData)[i] = {kSupportedRates[i], kSupportedRates[i]};
            *outDataSize = n * sizeof(AudioValueRange);
            return noErr;
        }
        case kAudioDevicePropertyIsHidden: RET_U32(0);
        case kAudioDevicePropertyZeroTimeStampPeriod: RET_U32(ZeroPeriod());
        case kAudioObjectPropertyCustomPropertyInfoList: {
            AudioServerPlugInCustomPropertyInfo infos[3] = {
                {kCustomProperty_Stats, kAudioServerPlugInCustomPropertyDataTypeCFString, kAudioServerPlugInCustomPropertyDataTypeNone},
                {kCustomProperty_Config, kAudioServerPlugInCustomPropertyDataTypeCFString, kAudioServerPlugInCustomPropertyDataTypeNone},
                {kCustomProperty_Trace, kAudioServerPlugInCustomPropertyDataTypeCFString, kAudioServerPlugInCustomPropertyDataTypeNone},
            };
            UInt32 n = std::min<UInt32>(3, inDataSize / sizeof(AudioServerPlugInCustomPropertyInfo));
            memcpy(outData, infos, n * sizeof(AudioServerPlugInCustomPropertyInfo));
            *outDataSize = n * sizeof(AudioServerPlugInCustomPropertyInfo);
            return noErr;
        }
        case kCustomProperty_Stats: {
            if (inDataSize < sizeof(CFStringRef)) return kAudioHardwareBadPropertySizeError;
            *((CFStringRef*)outData) = CopyStatsString();
            *outDataSize = sizeof(CFStringRef);
            return noErr;
        }
        case kCustomProperty_Trace: {
            if (inDataSize < sizeof(CFStringRef)) return kAudioHardwareBadPropertySizeError;
            *((CFStringRef*)outData) = CopyTraceString();
            *outDataSize = sizeof(CFStringRef);
            return noErr;
        }
        case kCustomProperty_Config: {
            if (inDataSize < sizeof(CFStringRef)) return kAudioHardwareBadPropertySizeError;
            *((CFStringRef*)outData) = CopyConfigString();
            *outDataSize = sizeof(CFStringRef);
            return noErr;
        }
        case kAudioDevicePropertyPreferredChannelsForStereo:
            if (inDataSize < 2 * sizeof(UInt32)) return kAudioHardwareBadPropertySizeError;
            ((UInt32*)outData)[0] = 1; ((UInt32*)outData)[1] = 2;
            *outDataSize = 2 * sizeof(UInt32);
            return noErr;
        case kAudioDevicePropertyPreferredChannelLayout: {
            UInt32 size = offsetof(AudioChannelLayout, mChannelDescriptions) + kChannels * sizeof(AudioChannelDescription);
            if (inDataSize < size) return kAudioHardwareBadPropertySizeError;
            AudioChannelLayout* l = (AudioChannelLayout*)outData;
            l->mChannelLayoutTag = kAudioChannelLayoutTag_UseChannelDescriptions;
            l->mChannelBitmap = 0;
            l->mNumberChannelDescriptions = kChannels;
            for (UInt32 i = 0; i < kChannels; ++i) {
                l->mChannelDescriptions[i].mChannelLabel = (i == 0) ? kAudioChannelLabel_Left : kAudioChannelLabel_Right;
                l->mChannelDescriptions[i].mChannelFlags = 0;
                l->mChannelDescriptions[i].mCoordinates[0] = l->mChannelDescriptions[i].mCoordinates[1] = l->mChannelDescriptions[i].mCoordinates[2] = 0;
            }
            *outDataSize = size;
            return noErr;
        }
    }
    return kAudioHardwareUnknownPropertyError;
}

static OSStatus Stream_GetPropertyData(const AudioObjectPropertyAddress* a, UInt32 inDataSize, UInt32* outDataSize, void* outData) {
    switch (a->mSelector) {
        case kAudioObjectPropertyBaseClass: RET_U32(kAudioObjectClassID);
        case kAudioObjectPropertyClass: RET_U32(kAudioStreamClassID);
        case kAudioObjectPropertyOwner: RET_U32(kObjectID_Device);
        case kAudioStreamPropertyIsActive: RET_U32(1);
        case kAudioStreamPropertyDirection: RET_U32(0);
        case kAudioStreamPropertyTerminalType: RET_U32(kAudioStreamTerminalTypeSpeaker);
        case kAudioStreamPropertyStartingChannel: RET_U32(1);
        case kAudioStreamPropertyLatency: RET_U32(0);
        case kAudioStreamPropertyVirtualFormat:
        case kAudioStreamPropertyPhysicalFormat: {
            if (inDataSize < sizeof(AudioStreamBasicDescription)) return kAudioHardwareBadPropertySizeError;
            pthread_mutex_lock(&gStateMutex);
            *((AudioStreamBasicDescription*)outData) = MakeASBD(gSampleRate);
            pthread_mutex_unlock(&gStateMutex);
            *outDataSize = sizeof(AudioStreamBasicDescription);
            return noErr;
        }
        case kAudioStreamPropertyAvailableVirtualFormats:
        case kAudioStreamPropertyAvailablePhysicalFormats: {
            UInt32 count = sizeof(kSupportedRates) / sizeof(kSupportedRates[0]);
            UInt32 n = std::min<UInt32>(count, inDataSize / sizeof(AudioStreamRangedDescription));
            for (UInt32 i = 0; i < n; ++i) {
                AudioStreamRangedDescription* d = ((AudioStreamRangedDescription*)outData) + i;
                d->mFormat = MakeASBD(kSupportedRates[i]);
                d->mSampleRateRange = {kSupportedRates[i], kSupportedRates[i]};
            }
            *outDataSize = n * sizeof(AudioStreamRangedDescription);
            return noErr;
        }
    }
    return kAudioHardwareUnknownPropertyError;
}

static OSStatus Control_GetPropertyData(AudioObjectID obj, const AudioObjectPropertyAddress* a, UInt32 qualSize, const void* qual, UInt32 inDataSize, UInt32* outDataSize, void* outData) {
    switch (a->mSelector) {
        case kAudioObjectPropertyBaseClass: RET_U32(obj == kObjectID_Volume ? kAudioLevelControlClassID : kAudioBooleanControlClassID);
        case kAudioObjectPropertyClass: RET_U32(obj == kObjectID_Volume ? kAudioVolumeControlClassID : kAudioMuteControlClassID);
        case kAudioObjectPropertyOwner: RET_U32(kObjectID_Device);
        case kAudioControlPropertyScope: RET_U32(kAudioObjectPropertyScopeOutput);
        case kAudioControlPropertyElement: RET_U32(kAudioObjectPropertyElementMain);
        case kAudioLevelControlPropertyScalarValue: {
            pthread_mutex_lock(&gStateMutex); Float32 v = gVolumeScalar; pthread_mutex_unlock(&gStateMutex);
            RET_F32(v);
        }
        case kAudioLevelControlPropertyDecibelValue: {
            pthread_mutex_lock(&gStateMutex); Float32 v = gVolumeScalar; pthread_mutex_unlock(&gStateMutex);
            RET_F32(ScalarToDB(v));
        }
        case kAudioLevelControlPropertyDecibelRange:
            if (inDataSize < sizeof(AudioValueRange)) return kAudioHardwareBadPropertySizeError;
            *((AudioValueRange*)outData) = {kVolumeMinDB, kVolumeMaxDB};
            *outDataSize = sizeof(AudioValueRange);
            return noErr;
        case kAudioLevelControlPropertyConvertScalarToDecibels:
            if (inDataSize < sizeof(Float32)) return kAudioHardwareBadPropertySizeError;
            *((Float32*)outData) = ScalarToDB(*((Float32*)outData));
            *outDataSize = sizeof(Float32);
            return noErr;
        case kAudioLevelControlPropertyConvertDecibelsToScalar:
            if (inDataSize < sizeof(Float32)) return kAudioHardwareBadPropertySizeError;
            *((Float32*)outData) = DBToScalar(*((Float32*)outData));
            *outDataSize = sizeof(Float32);
            return noErr;
        case kAudioBooleanControlPropertyValue: {
            pthread_mutex_lock(&gStateMutex); UInt32 m = gMute; pthread_mutex_unlock(&gStateMutex);
            RET_U32(m);
        }
    }
    return kAudioHardwareUnknownPropertyError;
}

// ---------------------------------------------------------------------------
// COM plumbing
// ---------------------------------------------------------------------------

static std::atomic<ULONG> gRefCount{0};

static HRESULT Drv_QueryInterface(void*, REFIID inUUID, LPVOID* outInterface) {
    if (!outInterface) return E_POINTER;
    CFUUIDRef req = CFUUIDCreateFromUUIDBytes(nullptr, inUUID);
    bool ok = CFEqual(req, IUnknownUUID) || CFEqual(req, kAudioServerPlugInDriverInterfaceUUID);
    CFRelease(req);
    if (!ok) { *outInterface = nullptr; return E_NOINTERFACE; }
    gRefCount.fetch_add(1);
    *outInterface = gDriverRef;
    return S_OK;
}
static ULONG Drv_AddRef(void*) { return gRefCount.fetch_add(1) + 1; }
static ULONG Drv_Release(void*) { ULONG v = gRefCount.load(); if (v > 0) v = gRefCount.fetch_sub(1) - 1; return v; }

static OSStatus Drv_Initialize(AudioServerPlugInDriverRef, AudioServerPlugInHostRef inHost) {
    gHost = inHost;
    mach_timebase_info(&gTimebase);
    gHostTicksPerSecond = 1e9 * (Float64)gTimebase.denom / (Float64)gTimebase.numer;
    gQueue = dispatch_queue_create("io.github.roberdorfer.kefproxy.queue", DISPATCH_QUEUE_SERIAL);
    gRing = (Float32*)calloc((size_t)kRingFrames * kChannels, sizeof(Float32));

    // Optional tuning from the bundle's Info.plist.
    CFBundleRef bundle = CFBundleGetBundleWithIdentifier(CFSTR("io.github.roberdorfer.kefproxy"));
    if (bundle) {
        CFNumberRef n;
        SInt32 v;
        if ((n = (CFNumberRef)CFBundleGetValueForInfoDictionaryKey(bundle, CFSTR("KEFProxyRealBufferFrames"))) && CFNumberGetValue(n, kCFNumberSInt32Type, &v) && v >= 16 && v <= 4096) gRealBufferFrames = (UInt32)v;
        if ((n = (CFNumberRef)CFBundleGetValueForInfoDictionaryKey(bundle, CFSTR("KEFProxyJitterMicroseconds"))) && CFNumberGetValue(n, kCFNumberSInt32Type, &v) && v >= 0 && v <= 50000) gJitterUs.store((UInt32)v);
    }
    CFPropertyListRef stored = nullptr;
    if (gHost->CopyFromStorage(gHost, CFSTR("config"), &stored) == noErr && stored) {
        if (CFGetTypeID(stored) == CFStringGetTypeID()) ApplyConfigString((CFStringRef)stored);
        CFRelease(stored);
    }
    // Restore the last volume so a coreaudiod restart does not reset the proxy (and thus the
    // speaker) to the 0.5 default. Mute is deliberately not persisted.
    CFPropertyListRef storedVol = nullptr;
    if (gHost->CopyFromStorage(gHost, CFSTR("volume"), &storedVol) == noErr && storedVol) {
        if (CFGetTypeID(storedVol) == CFNumberGetTypeID()) {
            Float32 v = 0.5f;
            if (CFNumberGetValue((CFNumberRef)storedVol, kCFNumberFloat32Type, &v)) gVolumeScalar = std::clamp(v, 0.0f, 1.0f);
        }
        CFRelease(storedVol);
    }
    gTargetActualBuffer.store(gRealBufferFrames);
    os_log(gLog, "initialize realBuffer=%u jitterUs=%u volume=%.3f", gRealBufferFrames, gJitterUs.load(), gVolumeScalar);

    // Defer HAL client calls: never call back into the HAL synchronously from Initialize.
    dispatch_after(dispatch_time(DISPATCH_TIME_NOW, 500 * NSEC_PER_MSEC), gQueue, ^{
        AudioObjectPropertyAddress addr = {kAudioHardwarePropertyDevices, kAudioObjectPropertyScopeGlobal, kAudioObjectPropertyElementMain};
        AudioObjectAddPropertyListenerBlock(kAudioObjectSystemObject, &addr, gQueue, ^(UInt32, const AudioObjectPropertyAddress*) { FindTarget(); });
        FindTarget();
    });
    return noErr;
}

static OSStatus Drv_CreateDevice(AudioServerPlugInDriverRef, CFDictionaryRef, const AudioServerPlugInClientInfo*, AudioObjectID*) { return kAudioHardwareUnsupportedOperationError; }
static OSStatus Drv_DestroyDevice(AudioServerPlugInDriverRef, AudioObjectID) { return kAudioHardwareUnsupportedOperationError; }
static OSStatus Drv_AddDeviceClient(AudioServerPlugInDriverRef, AudioObjectID, const AudioServerPlugInClientInfo*) { return noErr; }
static OSStatus Drv_RemoveDeviceClient(AudioServerPlugInDriverRef, AudioObjectID, const AudioServerPlugInClientInfo*) { return noErr; }

static OSStatus Drv_PerformDeviceConfigurationChange(AudioServerPlugInDriverRef, AudioObjectID inDeviceObjectID, UInt64 inChangeAction, void* inChangeInfo) {
    if (inDeviceObjectID != kObjectID_Device) return kAudioHardwareBadObjectError;
    if (inChangeAction == kChangeAction_Rate) {
        UInt64 bits = (UInt64)(uintptr_t)inChangeInfo;
        Float64 rate;
        memcpy(&rate, &bits, sizeof(rate));
        pthread_mutex_lock(&gStateMutex);
        gSampleRate = rate;
        pthread_mutex_unlock(&gStateMutex);
        os_log(gLog, "virtual rate now %.0f", rate);
        dispatch_async(gQueue, ^{ SetRealRate(rate); });
    }
    // kChangeAction_Target needs no state change here; StopIO/StartIO around this call re-bind the clock.
    return noErr;
}
static OSStatus Drv_AbortDeviceConfigurationChange(AudioServerPlugInDriverRef, AudioObjectID, UInt64, void*) { return noErr; }

static Boolean Drv_HasProperty(AudioServerPlugInDriverRef, AudioObjectID obj, pid_t, const AudioObjectPropertyAddress* a) {
    switch (obj) {
        case kObjectID_PlugIn: return PlugIn_HasProperty(a);
        case kObjectID_Device: return Device_HasProperty(a);
        case kObjectID_Stream: return Stream_HasProperty(a);
        case kObjectID_Volume: case kObjectID_Mute: return Control_HasProperty(obj, a);
    }
    return false;
}

static OSStatus Drv_IsPropertySettable(AudioServerPlugInDriverRef, AudioObjectID obj, pid_t, const AudioObjectPropertyAddress* a, Boolean* out) {
    if (!out) return kAudioHardwareIllegalOperationError;
    *out = false;
    if (!Drv_HasProperty(nullptr, obj, 0, a)) return kAudioHardwareUnknownPropertyError;
    if (obj == kObjectID_Device && (a->mSelector == kAudioDevicePropertyNominalSampleRate || a->mSelector == kCustomProperty_Config)) *out = true;
    if (obj == kObjectID_Stream && (a->mSelector == kAudioStreamPropertyVirtualFormat || a->mSelector == kAudioStreamPropertyPhysicalFormat || a->mSelector == kAudioStreamPropertyIsActive)) *out = true;
    if (obj == kObjectID_Volume && (a->mSelector == kAudioLevelControlPropertyScalarValue || a->mSelector == kAudioLevelControlPropertyDecibelValue)) *out = true;
    if (obj == kObjectID_Mute && a->mSelector == kAudioBooleanControlPropertyValue) *out = true;
    return noErr;
}

static OSStatus Drv_GetPropertyData(AudioServerPlugInDriverRef, AudioObjectID obj, pid_t, const AudioObjectPropertyAddress* a, UInt32 qualSize, const void* qual, UInt32 inDataSize, UInt32* outDataSize, void* outData) {
    if (!outDataSize || !outData) return kAudioHardwareIllegalOperationError;
    switch (obj) {
        case kObjectID_PlugIn: return PlugIn_GetPropertyData(a, qualSize, qual, inDataSize, outDataSize, outData);
        case kObjectID_Device: return Device_GetPropertyData(a, qualSize, qual, inDataSize, outDataSize, outData);
        case kObjectID_Stream: return Stream_GetPropertyData(a, inDataSize, outDataSize, outData);
        case kObjectID_Volume: case kObjectID_Mute: return Control_GetPropertyData(obj, a, qualSize, qual, inDataSize, outDataSize, outData);
    }
    return kAudioHardwareBadObjectError;
}

static OSStatus Drv_GetPropertyDataSize(AudioServerPlugInDriverRef ref, AudioObjectID obj, pid_t pid, const AudioObjectPropertyAddress* a, UInt32 qualSize, const void* qual, UInt32* outDataSize) {
    if (!outDataSize) return kAudioHardwareIllegalOperationError;
    // Generic approach: ask the getter with a generous buffer and report the size it would use.
    UInt8 buf[1024];
    UInt32 size = 0;
    OSStatus err = Drv_GetPropertyData(ref, obj, pid, a, qualSize, qual, sizeof(buf), &size, buf);
    if (err != noErr) return err;
    // Release CFTypes we just created.
    switch (a->mSelector) {
        case kAudioObjectPropertyName: case kAudioObjectPropertyManufacturer: case kAudioDevicePropertyDeviceUID:
        case kAudioDevicePropertyModelUID: case kAudioPlugInPropertyResourceBundle:
        case kCustomProperty_Stats: case kCustomProperty_Config: case kCustomProperty_Trace:
            if (size == sizeof(CFStringRef)) CFRelease(*(CFStringRef*)buf);
            break;
    }
    *outDataSize = size;
    return noErr;
}

static OSStatus Drv_SetPropertyData(AudioServerPlugInDriverRef, AudioObjectID obj, pid_t, const AudioObjectPropertyAddress* a, UInt32, const void*, UInt32 inDataSize, const void* inData) {
    if (!inData) return kAudioHardwareIllegalOperationError;
    if (obj == kObjectID_Device && a->mSelector == kAudioDevicePropertyNominalSampleRate) {
        if (inDataSize != sizeof(Float64)) return kAudioHardwareBadPropertySizeError;
        Float64 r = *(const Float64*)inData;
        bool ok = false;
        for (Float64 s : kSupportedRates) if (std::fabs(s - r) < 0.5) ok = true;
        if (!ok) return kAudioHardwareIllegalOperationError;
        pthread_mutex_lock(&gStateMutex); Float64 cur = gSampleRate; pthread_mutex_unlock(&gStateMutex);
        if (std::fabs(cur - r) > 0.5) RequestRateChange(r);
        return noErr;
    }
    if (obj == kObjectID_Stream && (a->mSelector == kAudioStreamPropertyVirtualFormat || a->mSelector == kAudioStreamPropertyPhysicalFormat)) {
        if (inDataSize != sizeof(AudioStreamBasicDescription)) return kAudioHardwareBadPropertySizeError;
        const AudioStreamBasicDescription* d = (const AudioStreamBasicDescription*)inData;
        if (d->mFormatID != kAudioFormatLinearPCM || d->mChannelsPerFrame != kChannels || d->mBitsPerChannel != 32) return kAudioDeviceUnsupportedFormatError;
        bool ok = false;
        for (Float64 s : kSupportedRates) if (std::fabs(s - d->mSampleRate) < 0.5) ok = true;
        if (!ok) return kAudioDeviceUnsupportedFormatError;
        pthread_mutex_lock(&gStateMutex); Float64 cur = gSampleRate; pthread_mutex_unlock(&gStateMutex);
        if (std::fabs(cur - d->mSampleRate) > 0.5) RequestRateChange(d->mSampleRate);
        return noErr;
    }
    if (obj == kObjectID_Stream && a->mSelector == kAudioStreamPropertyIsActive) return noErr;
    if (obj == kObjectID_Device && a->mSelector == kCustomProperty_Config) {
        if (inDataSize != sizeof(CFStringRef)) return kAudioHardwareBadPropertySizeError;
        CFStringRef cfg = *(const CFStringRef*)inData;
        ApplyConfigString(cfg);
        CFStringRef stored = CopyConfigString();
        if (gHost) gHost->WriteToStorage(gHost, CFSTR("config"), stored);
        os_log(gLog, "config now buffer=%u jitterUs=%u (buffer takes effect at next IO start)", gRealBufferFrames, gJitterUs.load());
        CFRelease(stored);
        NotifyHost(kObjectID_Device, {kCustomProperty_Config});
        // The HAL caches SafetyOffset/Latency when the device is published; a plain property
        // notification does not refresh that cache. A configuration change republishes the device.
        if (gHost) gHost->RequestDeviceConfigurationChange(gHost, kObjectID_Device, kChangeAction_Target, nullptr);
        return noErr;
    }
    if (obj == kObjectID_Volume && (a->mSelector == kAudioLevelControlPropertyScalarValue || a->mSelector == kAudioLevelControlPropertyDecibelValue)) {
        if (inDataSize != sizeof(Float32)) return kAudioHardwareBadPropertySizeError;
        Float32 v = *(const Float32*)inData;
        if (a->mSelector == kAudioLevelControlPropertyDecibelValue) v = DBToScalar(v);
        v = std::clamp(v, 0.0f, 1.0f);
        pthread_mutex_lock(&gStateMutex);
        bool changed = std::fabs(gVolumeScalar - v) > 1e-6f;
        gVolumeScalar = v;
        pthread_mutex_unlock(&gStateMutex);
        if (changed) {
            os_log(gLog, "volume set %.4f", v);
            if (gHost) {
                CFNumberRef n = CFNumberCreate(nullptr, kCFNumberFloat32Type, &v);
                gHost->WriteToStorage(gHost, CFSTR("volume"), n);
                CFRelease(n);
            }
            NotifyHost(kObjectID_Volume, {kAudioLevelControlPropertyScalarValue, kAudioLevelControlPropertyDecibelValue});
        }
        return noErr;
    }
    if (obj == kObjectID_Mute && a->mSelector == kAudioBooleanControlPropertyValue) {
        if (inDataSize != sizeof(UInt32)) return kAudioHardwareBadPropertySizeError;
        UInt32 m = *(const UInt32*)inData ? 1 : 0;
        pthread_mutex_lock(&gStateMutex);
        bool changed = gMute != m;
        gMute = m;
        pthread_mutex_unlock(&gStateMutex);
        if (changed) NotifyHost(kObjectID_Mute, {kAudioBooleanControlPropertyValue});
        return noErr;
    }
    return kAudioHardwareUnknownPropertyError;
}

// ---------------------------------------------------------------------------
// IO
// ---------------------------------------------------------------------------

static OSStatus Drv_StartIO(AudioServerPlugInDriverRef, AudioObjectID inDeviceObjectID, UInt32) {
    if (inDeviceObjectID != kObjectID_Device) return kAudioHardwareBadObjectError;
    pthread_mutex_lock(&gStateMutex);
    bool first = gIOStartCount == 0;
    gIOStartCount++;
    pthread_mutex_unlock(&gStateMutex);
    if (first) {
        // Never call back into the HAL from the host's thread: start the real device asynchronously.
        gRealBaseValid = false;
        gClockValid.store(false);
        const UInt64 now = mach_absolute_time();
        gStartHostTime.store(now);
        pthread_mutex_lock(&gStateMutex);
        const Float64 rate = gSampleRate;
        pthread_mutex_unlock(&gStateMutex);
        SetModel(0, (Float64)now, rate / gHostTicksPerSecond);  // provisional host-clock timeline
        gTimelineSeed.fetch_add(1);  // new timeline for this IO session
        gIORunning.store(true, std::memory_order_release);
        dispatch_async(gQueue, ^{ StartRealIO(); });
        NotifyHost(kObjectID_Device, {kAudioDevicePropertyDeviceIsRunning});
    }
    return noErr;
}

static OSStatus Drv_StopIO(AudioServerPlugInDriverRef, AudioObjectID inDeviceObjectID, UInt32) {
    if (inDeviceObjectID != kObjectID_Device) return kAudioHardwareBadObjectError;
    pthread_mutex_lock(&gStateMutex);
    bool last = gIOStartCount == 1;
    if (gIOStartCount > 0) gIOStartCount--;
    pthread_mutex_unlock(&gStateMutex);
    if (last) {
        gIORunning.store(false, std::memory_order_release);
        gClockValid.store(false);
        dispatch_async(gQueue, ^{ StopRealIO(); });
        NotifyHost(kObjectID_Device, {kAudioDevicePropertyDeviceIsRunning});
    }
    return noErr;
}

static OSStatus Drv_GetZeroTimeStamp(AudioServerPlugInDriverRef, AudioObjectID inDeviceObjectID, UInt32, Float64* outSampleTime, UInt64* outHostTime, UInt64* outSeed) {
    if (inDeviceObjectID != kObjectID_Device) return kAudioHardwareBadObjectError;
    // Latest period boundary at or before now, on the model's line.
    const ClockModel m = ReadModel();
    const Float64 period = ZeroPeriod();
    Float64 v = ModelSample(m, (Float64)mach_absolute_time());
    if (v < 0) v = 0;
    const UInt64 k = (UInt64)std::floor(v / period);
    const Float64 sample = (Float64)k * period;
    *outSampleTime = sample;
    *outHostTime = (UInt64)ModelHost(m, sample);
    *outSeed = gTimelineSeed.load(std::memory_order_relaxed);
    return noErr;
}

static OSStatus Drv_WillDoIOOperation(AudioServerPlugInDriverRef, AudioObjectID inDeviceObjectID, UInt32, UInt32 inOperationID, Boolean* outWillDo, Boolean* outWillDoInPlace) {
    if (inDeviceObjectID != kObjectID_Device) return kAudioHardwareBadObjectError;
    *outWillDo = inOperationID == kAudioServerPlugInIOOperationWriteMix;
    *outWillDoInPlace = true;
    return noErr;
}

static OSStatus Drv_BeginIOOperation(AudioServerPlugInDriverRef, AudioObjectID, UInt32, UInt32, UInt32, const AudioServerPlugInIOCycleInfo*) { return noErr; }

static OSStatus Drv_DoIOOperation(AudioServerPlugInDriverRef, AudioObjectID inDeviceObjectID, AudioObjectID inStreamObjectID, UInt32, UInt32 inOperationID, UInt32 inIOBufferFrameSize, const AudioServerPlugInIOCycleInfo* inIOCycleInfo, void* ioMainBuffer, void*) {
    if (inDeviceObjectID != kObjectID_Device || inStreamObjectID != kObjectID_Stream) return kAudioHardwareBadObjectError;
    if (inOperationID != kAudioServerPlugInIOOperationWriteMix || !ioMainBuffer) return noErr;
    if (!gSinkPresent.load()) return noErr;  // no real device: discard
    const SInt64 start = (SInt64)inIOCycleInfo->mOutputTime.mSampleTime;
    gHostLead.store((SInt64)(inIOCycleInfo->mOutputTime.mSampleTime - inIOCycleInfo->mCurrentTime.mSampleTime), std::memory_order_relaxed);
    gHostIOBuffer.store(inIOBufferFrameSize, std::memory_order_relaxed);
    Trace('W', start, (SInt64)inIOBufferFrameSize, (SInt64)inIOCycleInfo->mCurrentTime.mSampleTime);
    {
        const UInt64 machNow = mach_absolute_time();
        const Float64 rate = gSampleRate;
        if (inIOCycleInfo->mCurrentTime.mHostTime && machNow > inIOCycleInfo->mCurrentTime.mHostTime) {
            const SInt64 late = (SInt64)((Float64)(machNow - inIOCycleInfo->mCurrentTime.mHostTime) / gHostTicksPerSecond * rate);
            SInt64 prev = gMaxHostLate.load(std::memory_order_relaxed);
            while (late > prev && !gMaxHostLate.compare_exchange_weak(prev, late)) {}
        }
        if (gClockValid.load()) {
            const Float64 ours = ModelSample(ReadModel(), (Float64)machNow);
            gNowSkew.store((SInt64)(ours - inIOCycleInfo->mCurrentTime.mSampleTime), std::memory_order_relaxed);
        }
    }
    const Float32* src = (const Float32*)ioMainBuffer;
    for (UInt32 f = 0; f < inIOBufferFrameSize; ++f) {
        const UInt32 idx = (UInt32)((start + f) & (kRingFrames - 1));
        memcpy(gRing + (size_t)idx * kChannels, src + (size_t)f * kChannels, sizeof(Float32) * kChannels);
    }
    SInt64 end = start + inIOBufferFrameSize;
    SInt64 prev = gWrittenUpTo.load(std::memory_order_relaxed);
    while (end > prev && !gWrittenUpTo.compare_exchange_weak(prev, end, std::memory_order_release, std::memory_order_relaxed)) {}
    return noErr;
}

static OSStatus Drv_EndIOOperation(AudioServerPlugInDriverRef, AudioObjectID, UInt32, UInt32, UInt32, const AudioServerPlugInIOCycleInfo*) { return noErr; }

static AudioServerPlugInDriverInterface gInterface = {
    nullptr,
    Drv_QueryInterface, Drv_AddRef, Drv_Release,
    Drv_Initialize, Drv_CreateDevice, Drv_DestroyDevice, Drv_AddDeviceClient, Drv_RemoveDeviceClient,
    Drv_PerformDeviceConfigurationChange, Drv_AbortDeviceConfigurationChange,
    Drv_HasProperty, Drv_IsPropertySettable, Drv_GetPropertyDataSize, Drv_GetPropertyData, Drv_SetPropertyData,
    Drv_StartIO, Drv_StopIO, Drv_GetZeroTimeStamp, Drv_WillDoIOOperation, Drv_BeginIOOperation, Drv_DoIOOperation, Drv_EndIOOperation,
};

extern "C" void* KEFProxy_Create(CFAllocatorRef, CFUUIDRef inRequestedTypeUUID) {
    if (!CFEqual(inRequestedTypeUUID, kAudioServerPlugInTypeUUID)) return nullptr;
    gInterfacePtr = &gInterface;
    gRefCount.fetch_add(1);
    return gDriverRef;
}
