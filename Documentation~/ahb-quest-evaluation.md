# AHB receive-path prototype: local Quest evaluation

This is an experimental local spike, not a release candidate. It is based on the
`codex/webrtc-reliability` head `2431c8a` and lives on the separate
`codex/webrtc-ahb-evaluation` branch. The existing reliability branch and its
Android plugin were not changed by this spike.

## Source and approach

The donor is `velaboratory/com.unity.webrtc` main at
`d55fbbd543013f37c0fd9018f7137f96a2fd5b8e`. Its AHB decoder originated in
`dd7d6d8868b28cecc2661706b9f83bc714403935` and its direct-write path in
`c629b98ae2d7bdb7a90ffd299be0ca309aed1976`. The donor's native source,
shader, and relevant managed glue were adapted to the M116 reliability base;
the donor's whole branch and its prebuilt AAR were not adopted.

On Android Vulkan, opt-in mode 3 decodes to an `AHardwareBuffer`, imports it
as a Vulkan image, and writes converted RGBA into a Unity render texture using
a compute shader. It avoids the instrumented receive-side CPU I420 conversion
and Unity CPU-buffer upload. It still performs a GPU conversion/write; this is
not proof that hardware, drivers, or encoded media perform no copies.

Mode defaults to 0 (delegate/CPU display); `debug.ahb.mode=3` opts into the
prototype. This is an evaluation switch, not a supported product API. Decode
mode is latched on decoder configuration, so restart the app between modes.
The prototype requires Android API 26 or later and is targeted at Vulkan.

## Local evidence

- The ARM64 Android plugin compiled and linked with Unity 6000.6.7f1's NDK
  against the existing M116-20250805 dependency. Its ELF load segments have
  16 KiB alignment. The GLSL shader compiled to bytes identical to the checked-in
  SPIR-V using the installed NDK `glslc`.
- The isolated APK uses Unity 6000.6.7f1, IL2CPP, ARM64, Vulkan, two local
  peer connections, two 1280x720 H.264 streams capped at 30 fps, and synthetic
  Opus audio. Both modes used the same APK and explicitly negotiated H.264
  Constrained Baseline, profile-level-id 42e01f, packetization-mode 1.
- Standard decoder mode completed 3 media/disposal cycles and 2 reconnects.
  AHB mode completed the same 3 cycles and 2 reconnects. The AHB test checked
  actual decoder identity, random-write render textures, changing pixels,
  audio/video RTP progress, and pause/retain/resume.
- A subsequent AHB run completed **20 cycles and 19 reconnects** on Quest 3.
  All 20 cycle counter samples showed zero instrumented receive CPU conversions,
  CPU uploads and import failures; the captured log contained no fatal signals
  or fatal exceptions. This remains a short 2D local-loopback test, not a leak
  proof or an AROC/OpenXR validation. The device's prior USB stay-awake setting
  and the `debug.ahb.mode` property were restored after the run. The evidence is
  `artifacts~/ahb-validation/h264-ahb-stress-usb-awake-20261111-025155-utc.log`.
- During one five-second active window, the standard mode recorded 302 CPU
  conversions and 302 CPU uploads across both streams. AHB mode recorded 300
  decoded frames and 300 GPU writes, with zero instrumented receive CPU
  conversions or uploads. These counters do not instrument every possible
  driver-internal copy.
- The old receive-upload CPU marker measured about 1.47 ms per application
  frame in one baseline active window and was not entered in AHB mode. This
  excludes AHB command-recording and GPU execution; it is not an end-to-end
  frame-time, GPU-time, latency, or battery benchmark.
- A same-APK color investigation compared 11 solid colors on both peers.
  The Quest reported a BT.709 sampler suggestion, while the existing libyuv
  I420 receive path uses BT.601 limited range. The suggested-model AHB path
  differed from the baseline by up to 42 in an 8-bit channel; the diagnostic
  BT.601 limited-range override matched all 22 peer/color samples within 1.
  The comparison script rejects the old mismatch and accepts that override.
  Each diagnostic mode completed a one-cycle device run.
- The prototype now selects BT.601 limited range by default to match the
  existing CPU I420 path. On 2026-11-11, the final default-mode ARM64 APK
  passed a matched Quest test: 11 colors on both peers matched the CPU
  baseline within one 8-bit value per channel. Baseline and AHB each
  completed one H.264/audio media-and-disposal cycle; that final color test
  did not include a reconnect. The AHB active window recorded 302 decoded
  frames, 300 GPU writes, zero instrumented receive CPU conversions or
  uploads, and zero import failures. The final APK is under
  `artifacts~/ahb-validation/color-fixed-20261111-031044-1f825e5a`.
  Logs: `artifacts~/ahb-validation/final-color-baseline-20261111-225505-utc.log`
  and `artifacts~/ahb-validation/final-color-ahb-20261111-225547-utc.log`.
- An earlier AHB run failed a 750 ms source-timestamp lag assertion after
  pause/resume; an unchanged repeat passed the assertion. Another repeat was
  interrupted by the headset losing focus and timed out after waking. Preserve
  those results when interpreting the short successful run.
- An earlier planned 20-cycle AHB run completed five cycles before the headset
  again lost focus during cycle six. The suspended run was stopped and its
  debug property restored. A second attempt completed seven cycles and timed
  out after another focus loss. The later successful run is recorded above.

Logs, APKs, binaries, and fingerprints are local under ignored
`artifacts~/ahb-validation` and `artifacts~/native`. Earlier test AARs replaced
only ARM64. The newer API 24 builds contain freshly built ARM64 and x86-64
slices; x86-64 and API 24 still need device coverage.

## Opt-in and fallback work, 2026-11-11

The experimental decoder now advertises only stock-supported H.264 formats,
and only when explicitly enabled before Vulkan initialization using the
developer property `debug.ahb.mode=3`. This property is a test mechanism, not
a production application opt-in API. Missing AHB/Vulkan capabilities and
decoder initialization or decode failures select the stock H.264 decoder;
decode failures request a keyframe. The normal path tolerates older native
binaries that lack the new diagnostic exports. AHB-specific Vulkan functions
are optional, and an AHB device-creation failure retries without AHB.

On Quest 3, a test-hook build passed default, AHB, and injected decoder
initialization and decode failure tests. A subsequent build without test hooks
passed one cycle each for default H.264, AHB H.264, and VP8 with AHB enabled.
The two H.264 runs matched 22 color samples within one channel value. VP8
negotiated VP8 and used the CPU path. These are synthetic, two-peer local
loopback tests, not AROC or OpenXR tests. The test-hook and release-style
logs are retained under `artifacts~/ahb-validation`. The separate Vulkan
device-creation retry has compiled, but its failure branch has not been
forced on a device.

The rebuilt API 24, no-test-hooks APK subsequently passed 20 consecutive
H.264 local-loopback cycles and 19 reconnects on Quest 3, including decoded
audio and video on both peers and disposal after each cycle. Its 22 first-cycle
color samples matched the default-path baseline within one channel value.
The last cycle recorded 683 AHB decoded frames, 682 GPU writes, no instrumented
CPU I420 conversions or uploads, and no import failures. Log:
`artifacts~/ahb-validation/release-soak-20261111-232547-utc.log`; APK and
fingerprints: `artifacts~/ahb-validation/api24-retry-20261111-232340-c4f1ef32`.
This does not exercise the failure-only Vulkan retry branch.

Both Android ABIs compiled with API 24 as the native minimum and weakly linked
API 26 AHB entry points; the generated AAR needs testing on API 24 and x86-64
devices. Guarded AHB use requires API 26 or later. This is not evidence that
the rest of an application supports every Android version.

## Known blockers before adopting this path

1. **Color fidelity and metadata:** the same solid-color probe produced approximately
   RGB `(3,153,3)` / `(153,3,3)` in the baseline and `(1,111,2)` / `(181,6,2)`
   in the original AHB mode. An explicit BT.601 limited-range diagnostic
   matched 11 colors within 1 per channel on both peers, and the final
   default-mode APK reproduced that result. Test real streams with their
   color metadata, additional reference color bars and orientation. The
   compatibility choice matches the current CPU path; it does not propagate
   per-frame color metadata. The shader assumes Linear color space and flips Y.
2. **Lifecycle and GPU synchronization:** the prototype adds device shutdown
   gating, foreign queue ownership barriers, and deferred retirement of readers,
   imported images and per-slot views. These changes need independent review,
   Vulkan validation and device tests for resize, output-surface changes,
   device loss, teardown while work is in flight, and prolonged reuse. A short
   loopback run is not proof that every lifetime edge is safe.
3. **Scope and fallback:** opt-in and injected early fallback are tested as
   described above. A production-accessible opt-in must be designed around
   Unity's Vulkan initialization; a developer system property alone is not
   sufficient. Test failure after frames are already in flight, inability to
   configure the fallback, and Vulkan device-creation retry; review extension
   and feature handling on non-Quest and non-Android platforms.
4. **Device and product coverage:** run an OpenXR scene and AROC itself, a real
   remote sender/network, multi-track-on-one-peer, real resolution adaptation,
   packet loss/keyframe recovery, background/foreground, and a longer soak.
   Verify VP8 and other existing codecs still use their normal paths. No Vision
   Pro zero-copy path was built or tested.
5. **Packaging:** both ABI slices compile against API 24, with API 26 calls
   guarded and weakly linked, and have 16 KiB alignment. Actual API 24 and
   x86-64 device tests, final artifact provenance, and exported API review
   remain necessary before distributing an Android AAR.

Suggested next step: test real streams' color metadata and orientation, then
run a matched A/B benchmark with GPU timing,
latency, frame drops, memory and thermal
measurements on a representative Quest/OpenXR workload. Estimate and schedule
productionization after those results; device/driver issues can dominate the
remaining effort.
