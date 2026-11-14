# Isolated Quest H.264 AHB spike

This disposable fixture mirrors the evaluation worktree's Runtime/Editor package into a short temporary Unity project. It uses application ID `com.badvr.webrtcahb.spike`; it does not replace the archived baseline app or AROC. The original archive is unchanged.

Requires PowerShell7, Unity6000.6.7f1 with Android modules, and the instrumented evaluation ARM64 AAR. The APK is ARM64/IL2CPP, Vulkan, Linear color space, Android minSDK32/target36. This is a 2D package test, not AROC/OpenXR validation.

Build once from this evaluation worktree:

```powershell
& '.\Tests~\AhbQuestSmoke\Build-QuestSmoke.ps1' `
  -UnityEditor 'C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Unity.exe' `
  -NativeAar '.\artifacts~\native\libwebrtc-ahb-arm64.aar' -RunLabel ahb-h264
```

The build script prints `APK=` and `FINGERPRINTS=`. The fingerprint identifies the actual mirrored package, selected AAR, fixture, source commit and working-tree status. No package reference points at the main checkout; the project uses its temporary mirror of this evaluation worktree.

Run the same APK in delegate mode0 and AHB mode3:

```powershell
$adb = 'C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe'
& '.\Tests~\AhbQuestSmoke\Run-QuestSmoke.ps1' -Adb $adb -Serial '<serial>' `
  -Apk '<APK printed by build>' -AhbMode 0 -Cycles 3 -VideoWidth 1280 -VideoHeight 720 -RunLabel h264-delegate
& '.\Tests~\AhbQuestSmoke\Run-QuestSmoke.ps1' -Adb $adb -Serial '<serial>' `
  -AhbMode 3 -Cycles 3 -VideoWidth 1280 -VideoHeight 720 -RunLabel h264-ahb
```

The runner force-stops only this spike app, records/restores the prior `debug.ahb.mode` property, and passes mode/cycles/resolution in Android intent extras. It captures fresh logs, memory samples and run settings. Omit APK to reuse the installed spike. Use `-Cycles 20 -TimeoutSeconds 600` for reconnect stress, or 1920x1080 for a matched 1080p test without rebuilding. Both widths/heights must be even. Runtime options override build defaults; defaults are3cycles/1280x720. The native binary caches mode at configure, so the runner restarts the app for every mode change.

Checks include:

- Two local peers send H.264 video capped at30fps and distinct Opus audio tones. Shared sender/receiver H.264 Constrained Baseline profile-level-id42e01f and packetization-mode1 are required; High profiles and codec/profile fallback are rejected; actual receive codec, fmtp and decoder implementation are logged. VP8 fallback cannot pass.
- Mode0 requires Texture2D receive output and progressing native CPU conversion/upload counters. Mode3 requires a created random-write RenderTexture, progressing AHB decode/GPU conversion counters, zero receive CPU conversion/uploads and zero import failures.
- Required native exports: `int GetAhbDisplayMode()`, `uint64_t GetReceivePathCounter(int)` and `void ResetReceivePathCounters()`. Counters0..5 mean successful AHB frames, GPU dispatches, receive CPU I420 conversion, receive CPU texture upload, import failures, skipped AHB conversions. Missing exports fail the run.
- Correctness-only pixel probes verify opposite-peer red/green colors, progress to white, pause retention and resumed pixel changes. ReadPixels is forbidden inside the three timed windows.
- Each cycle disposes peers/tracks, then reconnects. Existing callback/audio-lifetime/packed-bool/source-array checks remain outside timing windows.
- Encoded513-byte payload transforms are disabled by default to avoid measurement overhead. `-EnableTransforms` enables them as a separate correctness run.

Timing logs report the existing receive CPU upload marker and appFPS. AHB legitimately has no samples for this CPU-only path; zero marker time is not whole-frame/GPU timing. AHB still performs a GPU YCbCr-to-RGBA write. These counters establish the instrumented receive path, not an absence of CPU copies inside proprietary codec/driver internals. Sender readback, encoded packet copies and explicit correctness ReadPixels are outside that claim.

Remaining validation: OpenXR/AROC integration, multiple video tracks on one connection (this fixture has one per peer), live resolution renegotiation within one connection (width/height options are fixed per run), remote-network interoperability/loss recovery, and longer thermal/memory soak. Do not infer these from a 2D loopback pass.
