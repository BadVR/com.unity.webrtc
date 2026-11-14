using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Unity.Collections;
using Unity.WebRTC;
using UnityEngine;

public static class QuestRegressionChecks
{
    static int audioFrames, videoFrames, audioValidated, videoValidated, audioAttached, videoAttached;
    static Exception transformFailure;

    public static string TransformSummary => "audioFrames=" + Volatile.Read(ref audioFrames) +
        " videoFrames=" + Volatile.Read(ref videoFrames) + " audioValidated=" + Volatile.Read(ref audioValidated) +
        "/" + Volatile.Read(ref audioAttached) + " videoValidated=" + Volatile.Read(ref videoValidated) +
        "/" + Volatile.Read(ref videoAttached);

    public static void ResetTransformCounters()
    {
        // Call only before attaching any transforms, after previous peers are disposed.
        audioFrames = videoFrames = audioValidated = videoValidated = audioAttached = videoAttached = 0;
        transformFailure = null;
    }

    public static IEnumerator RunBeforeMediaChecks()
    {
        ResetTransformCounters();
        var field = typeof(WebRTC).GetField("s_syncContext", BindingFlags.NonPublic | BindingFlags.Static);
        var context = field?.GetValue(null) as ExecutableUnitySynchronizationContext;
        Require(context != null, "running executable synchronization context unavailable");
        bool sameBatch = false;
        context.Post(_ => throw new InvalidOperationException("QUEST_REGRESSION_EXPECTED_CALLBACK_EXCEPTION queued"), null);
        context.Post(_ => sameBatch = true, null);
        float until = Time.realtimeSinceStartup + 3f;
        while (!sameBatch && Time.realtimeSinceStartup < until) yield return null;
        Require(sameBatch, "throwing callback stopped current event batch");
        bool laterBatch = false;
        context.Post(_ => laterBatch = true, null);
        until = Time.realtimeSinceStartup + 3f;
        while (!laterBatch && Time.realtimeSinceStartup < until) yield return null;
        Require(laterBatch, "throwing callback stopped later event batches");

        int completed = 0;
        Exception sendError = null;
        var sender = new Thread(() =>
        {
            try { context.Send(_ => throw new InvalidOperationException("QUEST_REGRESSION_EXPECTED_CALLBACK_EXCEPTION waiter"), null); }
            catch (Exception exception) { sendError = exception; }
            finally { Volatile.Write(ref completed, 1); }
        }) { IsBackground = true };
        sender.Start();
        try
        {
            until = Time.realtimeSinceStartup + 3f;
            while (Volatile.Read(ref completed) == 0 && Time.realtimeSinceStartup < until) yield return null;
            Require(Volatile.Read(ref completed) != 0 && sender.Join(100), "throwing callback did not release waiting sender");
            Require(sendError == null, "waiting sender failed: " + sendError);
        }
        finally
        {
            context.ExecutePendingTasks(100);
            if (sender.IsAlive) sender.Join(1000);
        }
        Debug.Log("QUEST_REGRESSION PASS synchronization-pump continuity and waiter release (two expected exception logs)");

        yield return CheckAudioSinkTeardown();
        CheckPackedBoolArray();
        Debug.Log("QUEST_REGRESSION PASS pre-media checks; encoded frame ABI validation occurs during real media");
    }

    static IEnumerator CheckAudioSinkTeardown()
    {
        for (int cycle = 0; cycle < 8; cycle++)
        {
            var renderer = new AudioStreamTrack.AudioStreamRenderer((AudioStreamTrack)null);
            int started = 0, stop = 0, completed = 0;
            bool rejected = false;
            Exception drainError = null;
            var worker = new Thread(() =>
            {
                try
                {
                    var block = new float[960];
                    while (Volatile.Read(ref stop) == 0)
                    {
                        if (!renderer.ProcessAudio(block, block.Length, 2, 48000)) { rejected = true; break; }
                        Volatile.Write(ref started, 1);
                        Thread.Yield();
                    }
                }
                catch (Exception exception) { drainError = exception; }
                finally { Volatile.Write(ref completed, 1); }
            }) { IsBackground = true };
            worker.Start();
            try
            {
                float until = Time.realtimeSinceStartup + 3f;
                while (Volatile.Read(ref started) == 0 && Volatile.Read(ref completed) == 0 && Time.realtimeSinceStartup < until)
                    yield return null;
                Require(Volatile.Read(ref started) != 0, "native sink drain did not start: " + drainError);
                renderer.Dispose();
                until = Time.realtimeSinceStartup + 3f;
                while (Volatile.Read(ref completed) == 0 && Time.realtimeSinceStartup < until) yield return null;
                Require(Volatile.Read(ref completed) != 0 && worker.Join(100), "drain did not terminate after Dispose");
                Require(drainError == null && rejected, "sink drain failed to reject disposed sink: " + drainError);
                var untouched = new float[960];
                for (int i = 0; i < untouched.Length; i++) untouched[i] = 0.25f;
                int callbacks = 0;
                renderer.onReceived += (_, __, ___) => Interlocked.Increment(ref callbacks);
                Require(!renderer.ProcessAudio(untouched, untouched.Length, 2, 48000), "disposed sink accepted audio");
                renderer.SetData(untouched, 2, 48000);
                Require(callbacks == 0 && renderer.self == IntPtr.Zero, "disposed sink invoked callback or retained pointer");
                for (int i = 0; i < untouched.Length; i++) Require(untouched[i] == 0.25f, "disposed sink changed caller data");
            }
            finally
            {
                Volatile.Write(ref stop, 1);
                worker.Join(1000);
                renderer.Dispose();
            }
        }
        Debug.Log("QUEST_REGRESSION PASS native audio sink concurrent drain/dispose cycles=8 and post-dispose rejection");
    }

    static void CheckPackedBoolArray()
    {
        byte[] packed = { 0, 1, 0, 1, 2, 0, 1 };
        IntPtr ptr = Marshal.AllocCoTaskMem(packed.Length);
        try
        {
            Marshal.Copy(packed, 0, ptr, packed.Length);
            bool[] values = ptr.AsArray<bool>(packed.Length, false);
            for (int i = 0; i < packed.Length; i++) Require(values[i] == (packed[i] != 0), "packed native bool mismatch at " + i);
        }
        finally { Marshal.FreeCoTaskMem(ptr); }
        Debug.Log("QUEST_REGRESSION PASS packed native bool-byte conversion");
    }

    // Caller owns the returned transform; dispose it AFTER its peer has closed/disposed.
    public static RTCRtpScriptTransform AttachTransform(RTCRtpSender sender, TrackKind kind)
    {
        Require(kind == TrackKind.Video || kind == TrackKind.Audio, "unexpected transform kind");
        int validated = 0;
        RTCRtpScriptTransform transform = null;
        transform = new RTCRtpScriptTransform(kind, e =>
        {
            // libwebrtc thread: inspect/copy frame memory and update atomic counters only.
            // Never throw a managed exception across the native callback boundary.
            try
            {
                Require(kind == TrackKind.Video ? e.Frame is RTCEncodedVideoFrame : e.Frame is RTCEncodedAudioFrame,
                    "unexpected encoded frame type for " + kind);
                if (Interlocked.Exchange(ref validated, 1) == 0)
                {
                    CheckFrameDataRoundTrip(e.Frame);
                    if (kind == TrackKind.Video) Interlocked.Increment(ref videoValidated);
                    else Interlocked.Increment(ref audioValidated);
                }
                if (kind == TrackKind.Video) Interlocked.Increment(ref videoFrames);
                else Interlocked.Increment(ref audioFrames);
            }
            catch (Exception exception) { RecordTransformFailure(exception); }
            try { transform.Write(e.Frame); }
            catch (Exception exception) { RecordTransformFailure(exception); }
        });
        try { sender.Transform = transform; }
        catch { transform.Dispose(); throw; }
        if (kind == TrackKind.Video) Interlocked.Increment(ref videoAttached);
        else Interlocked.Increment(ref audioAttached);
        return transform;
    }

    static void CheckFrameDataRoundTrip(RTCEncodedFrame frame)
    {
        byte[] originalData = frame.GetData().ToArray();
        Require(originalData.Length > 0, "encoded frame payload was empty");
        byte[] replacementData = new byte[513];
        for (int i = 0; i < replacementData.Length; i++) replacementData[i] = (byte)(i % 251);
        // An arbitrary libwebrtc thread has no Allocator.Temp scope.
        using (var original = new NativeArray<byte>(originalData, Allocator.Persistent))
        using (var replacement = new NativeArray<byte>(replacementData, Allocator.Persistent))
        {
            try
            {
                frame.SetData(replacement.AsReadOnly());
                var returned = frame.GetData();
                Require(returned.Length == replacementData.Length, "native frame size_t length roundtrip failed");
                for (int i = 0; i < replacementData.Length; i++)
                    Require(returned[i] == replacementData[i], "native encoded payload byte mismatch");
            }
            finally { frame.SetData(original.AsReadOnly()); }
        }
    }

    static void RecordTransformFailure(Exception exception)
    {
        if (Interlocked.CompareExchange(ref transformFailure, exception, null) == null)
            Debug.LogError("QUEST_REGRESSION FAIL encoded frame callback: " + exception);
    }

    public static void RequireTransformsObserved()
    {
        Exception failure = Volatile.Read(ref transformFailure);
        Require(failure == null, "encoded frame ABI callback failed: " + failure);
        Require(Volatile.Read(ref audioFrames) > 0 && Volatile.Read(ref videoFrames) > 0,
            "both encoded frame kinds were not observed: " + TransformSummary);
        Require(Volatile.Read(ref audioValidated) == Volatile.Read(ref audioAttached) && audioAttached > 0 &&
            Volatile.Read(ref videoValidated) == Volatile.Read(ref videoAttached) && videoAttached > 0,
            "not every attached sender passed the513-byte frame ABI roundtrip: " + TransformSummary);
        Debug.Log("QUEST_REGRESSION PASS encoded frame ABI " + TransformSummary);
    }

    sealed class ReceiverSourceProbe
    {
        public RTCRtpReceiver Receiver;
        public uint Ssrc;
        public long TimestampMs, RtpTimestamp;
        public float ObservedAt;
        public string Kind;
    }

    public static IEnumerator ValidateReceiverSources(IEnumerable<RTCRtpReceiver> receivers)
    {
        int checkedReceivers = 0;
        var probes = new List<ReceiverSourceProbe>();
        foreach (var receiver in receivers)
        {
            var operation = receiver.GetStats();
            yield return operation;
            Require(!operation.IsError, "receiver source stats failed");
            using (var report = operation.Value)
            {
                var inboundBySsrc = new Dictionary<uint, RTCInboundRTPStreamStats>();
                foreach (var value in report.Stats.Values)
                    if (value is RTCInboundRTPStreamStats inbound && inbound.packetsReceived > 0)
                        inboundBySsrc[inbound.ssrc] = inbound;
                Require(inboundBySsrc.Count > 0, "receiver has no active inbound RTP stats");
                var sources = receiver.GetSynchronizationSources();
                Require(sources != null && sources.Length > 0, "receiver returned no synchronization sources");
                int matched = 0;
                foreach (var source in sources)
                {
                    // This API also returns CSRC entries with a null SSRC; only
                    // entries classified as synchronization sources should match.
                    if (!source.source.HasValue) continue;
                    Require(source.source.Value != 0, "synchronization source SSRC is zero");
                    bool found = inboundBySsrc.TryGetValue(source.source.Value, out var inbound);
                    Require(found, "synchronization source SSRC does not match receiver RTP stats");
                    Require(source.timestamp.HasValue && source.timestamp.Value > 0 &&
                        source.rtpTimestamp.HasValue,
                        "synchronization source timestamps are missing/invalid");
                    probes.Add(new ReceiverSourceProbe {
                        Receiver = receiver, Ssrc = source.source.Value, Kind = inbound.kind,
                        TimestampMs = source.timestamp.Value, RtpTimestamp = source.rtpTimestamp.Value,
                        ObservedAt = Time.realtimeSinceStartup
                    });
                    matched++;
                }
                Require(matched > 0, "receiver has no matching synchronization source");
                checkedReceivers++;
            }
        }
        Require(checkedReceivers > 0, "no RTP receivers supplied for native array validation");
        float until = Time.realtimeSinceStartup + 1f;
        while (Time.realtimeSinceStartup < until) yield return null;
        foreach (var probe in probes)
        {
            RTCRtpContributingSource latest = null;
            foreach (var source in probe.Receiver.GetSynchronizationSources())
                if (source.source == probe.Ssrc) { latest = source; break; }
            Require(latest != null && latest.timestamp.HasValue && latest.rtpTimestamp.HasValue,
                "matching source disappeared during active media");
            // Native RtpSource is 24 bytes on ARM64: timestamp at offset 16
            // must survive the export. Compare elapsed times, never epochs:
            // M116 stats.lastPacketReceivedTimestamp uses Unix epoch while
            // SourceTracker's timestamp uses the monotonic Clock::CurrentTime.
            long deltaMs = latest.timestamp.Value - probe.TimestampMs;
            double elapsedMs = (Time.realtimeSinceStartup - probe.ObservedAt) * 1000.0;
            Require(deltaMs > 0 && Math.Abs(deltaMs - elapsedMs) < 750,
                "native RtpSource timestamp tail did not advance with media; deltaMs=" + deltaMs + " elapsedMs=" + elapsedMs);
            uint rtpDelta = unchecked((uint)latest.rtpTimestamp.Value - (uint)probe.RtpTimestamp);
            Require(rtpDelta > 0 && rtpDelta < 0x80000000u, "native source RTP timestamp did not advance");
            Debug.Log("QUEST_REGRESSION source kind=" + probe.Kind + " ssrc=" + probe.Ssrc +
                " timestampDeltaMs=" + deltaMs + " elapsedMs=" + elapsedMs + " rtpTimestampDelta=" + rtpDelta);
        }
        Debug.Log("QUEST_REGRESSION PASS native RtpSource array receivers=" + checkedReceivers +
            " sources=" + probes.Count + " managedStructBytes=" + Marshal.SizeOf<RTCRtpContributingSourceInternal>());
    }

    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("QUEST_REGRESSION " + message);
    }
}
