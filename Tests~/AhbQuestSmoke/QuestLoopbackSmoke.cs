using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Unity.Profiling;
using Unity.WebRTC;
using UnityEngine;
using UnityEngine.Rendering;

// Disposable, synthetic package baseline. No AROC code, XR, camera or microphone input.
public sealed class QuestLoopbackSmoke : MonoBehaviour
{
    const int DefaultCycleCount = 3;
    static int CycleCount = DefaultCycleCount;
    const float MinimumMediaSeconds = 8f;
    const float CycleTimeoutSeconds = 50f;
    const int DefaultVideoWidth = 1280, DefaultVideoHeight = 720;
    static int VideoWidth = DefaultVideoWidth, VideoHeight = DefaultVideoHeight;
    static bool EnableTransforms;
    static int AhbMode;
    static int ExpectedPathMode;
    static bool UseVp8;
    const uint VideoMaximumFps = 30;
    const string UploadMarker = "UnityVideoRenderer.ConvertVideoFrameToTextureAndWriteToBuffer";
    const string Prefix = "QUEST_SMOKE ";

    sealed class Numbers
    {
        public uint Frames, VideoPackets, AudioPackets;
        public ulong VideoBytes, AudioBytes, AudioSamples;
        public string VideoCodec = "", AudioCodec = "", VideoFmtp = "", DecoderImplementation = "";
    }

    sealed class Peer
    {
        public int Index;
        public bool Active = true, RemoteReady;
        public RTCPeerConnection Connection;
        public readonly Queue<RTCIceCandidate> Ice = new Queue<RTCIceCandidate>();
        public GameObject CameraObject, InputObject, OutputObject;
        public Camera Camera;
        public RenderTexture SendTexture;
        public Texture ReceivedTexture;
        public AudioSource Input, Output;
        public AudioClip Clip;
        public VideoStreamTrack SendVideo, ReceivedVideo;
        public AudioStreamTrack SendAudio, ReceivedAudio;
        public RTCRtpSender VideoSender;
        public RTCRtpScriptTransform VideoTransform, AudioTransform;
        public Color? ForcedColor;
        public OnVideoReceived VideoHandler;
        public AudioReadEventHandler AudioHandler;
        public int VideoNotifications, AudioCallbacks, NonzeroAudioCallbacks;
        public int FirstNonzeroAudioCallbacks;
        public Numbers First, Last;
    }

    Peer[] peers;
    Coroutine updateRoutine;
    float cycleDeadline;
    int cycle;
    bool cleaning, finished;
    string failure, status = "Starting synthetic bidirectional media baseline";

    IEnumerator Start()
    {
        // Package initialization runs AfterSceneLoad; avoid creating tracks during Awake.
        yield return null;
        yield return Guarded(RunSmoke());
    }

    IEnumerator Guarded(IEnumerator root)
    {
        var stack = new Stack<IEnumerator>();
        stack.Push(root);
        try
        {
            while (stack.Count > 0)
            {
                if (!string.IsNullOrEmpty(failure))
                    break;
                if (cycleDeadline > 0 && Time.realtimeSinceStartup > cycleDeadline)
                {
                    Fail("cycle " + cycle + " exceeded " + CycleTimeoutSeconds + "s timeout");
                    break;
                }
                object next = null;
                bool moved = false;
                try
                {
                    moved = stack.Peek().MoveNext();
                    if (moved)
                        next = stack.Peek().Current;
                }
                catch (Exception ex)
                {
                    Fail("managed exception: " + ex);
                }
                if (!string.IsNullOrEmpty(failure))
                    break;
                if (!moved)
                {
                    (stack.Pop() as IDisposable)?.Dispose();
                    continue;
                }
                // Also drive CustomYieldInstruction/RTC operations ourselves, keeping
                // their waits under the same cycle timeout and exception handler.
                if (next is IEnumerator nested)
                {
                    stack.Push(nested);
                    continue;
                }
                yield return next;
            }
        }
        finally
        {
            cycleDeadline = 0;
            while (stack.Count > 0)
            {
                try { (stack.Pop() as IDisposable)?.Dispose(); }
                catch (Exception ex) { Fail("coroutine cleanup: " + ex.Message); }
            }
            Cleanup();
            if (updateRoutine != null)
            {
                StopCoroutine(updateRoutine);
                updateRoutine = null;
            }
            finished = true;
            if (!string.IsNullOrEmpty(failure))
            {
                status = "FAIL: " + failure;
                Debug.LogError(Prefix + "FINAL FAIL " + failure);
            }
        }
    }

    IEnumerator RunSmoke()
    {
        Log("START platform=" + Application.platform + " stage=configure ahbSpike=true");
        CycleCount = AhbNativeProbe.IntOption("ahb_cycles", DefaultCycleCount);
        VideoWidth = AhbNativeProbe.IntOption("ahb_width", DefaultVideoWidth);
        VideoHeight = AhbNativeProbe.IntOption("ahb_height", DefaultVideoHeight);
        EnableTransforms = AhbNativeProbe.IntOption("ahb_transforms", 0) != 0;
        UseVp8 = AhbNativeProbe.IntOption("ahb_vp8", 0) != 0;
        Require(CycleCount >= 1 && CycleCount <= 100, "cycles outside 1..100");
        Require(VideoWidth >= 64 && VideoWidth <= 4096 && VideoHeight >= 64 && VideoHeight <= 4096 &&
            VideoWidth % 2 == 0 && VideoHeight % 2 == 0, "video dimensions must be even, 64..4096");
        AhbMode = AhbNativeProbe.DisplayMode();
        int expectedMode = AhbNativeProbe.IntOption("ahb_mode", AhbMode);
        ExpectedPathMode = AhbNativeProbe.IntOption("ahb_path_mode", AhbMode);
        Require(AhbMode == expectedMode && (AhbMode == 0 || AhbMode == 3),
            "expected explicit mode0 delegate or mode3 AHB; actual=" + AhbMode + " expected=" + expectedMode);
        AhbNativeProbe.Reset(); // Missing telemetry fails before media starts.
        Application.runInBackground = true;
        Application.targetFrameRate = 90;
        Screen.sleepTimeout = SleepTimeout.NeverSleep;
        Log("DEVICE platform=" + Application.platform + " unity=" + Application.unityVersion +
            " graphics=" + SystemInfo.graphicsDeviceType + " device=" + SystemInfo.graphicsDeviceName +
            " model=" + SystemInfo.deviceModel + " audioHz=" + AudioSettings.outputSampleRate +
            " ahbMode=" + AhbMode + " cycles=" + CycleCount + " resolution=" + VideoWidth + "x" + VideoHeight +
            " transforms=" + EnableTransforms);
        Log("SCOPE synthetic 2D Vulkan/Adreno package loopback; excludes AROC/XR, external network, hardware camera/microphone and Vision Pro");
        Require(SystemInfo.graphicsDeviceType == GraphicsDeviceType.Vulkan,
            "expected Vulkan; actual=" + SystemInfo.graphicsDeviceType);
        if (FindFirstObjectByType<AudioListener>() == null)
            gameObject.AddComponent<AudioListener>();
        updateRoutine = StartCoroutine(WebRTC.Update());
        yield return QuestRegressionChecks.RunBeforeMediaChecks();
        Log("CONFIG codec=" + (UseVp8 ? "VP8" : "H264") + " actualCodecRequired=true ahbMode=" + AhbMode + " transforms=" + EnableTransforms +
            " timingReadPixels=false telemetryRequired=true");
        Log("PERF_CONFIG targetAppFps=90 targetVideoMaxFps=" + VideoMaximumFps +
            " sendResolution=" + VideoWidth + "x" + VideoHeight + " sendStreams=2 nativeMarker=" + UploadMarker +
            " markerCategory=Render timing=CPU-elapsed-nanoseconds-including-conversion-and-upload-not-GPU-timestamps");

        for (cycle = 1; cycle <= CycleCount; cycle++)
        {
            status = "Cycle " + cycle + "/" + CycleCount + ": negotiating";
            cycleDeadline = Time.realtimeSinceStartup + CycleTimeoutSeconds;
            Log("CYCLE " + cycle + " BEGIN (fresh peers/tracks)");
            ulong[] cycleCounters = AhbNativeProbe.Counters();
            QuestRegressionChecks.ResetTransformCounters();
            peers = new[] { new Peer { Index = 0 }, new Peer { Index = 1 } };
            foreach (Peer p in peers)
                CreatePeer(p);
            WireCallbacks(peers[0], peers[1]);
            WireCallbacks(peers[1], peers[0]);
            foreach (Peer p in peers)
                AddSyntheticTracks(p);

            yield return Negotiate(peers[0], peers[1]);
            foreach (Peer p in peers)
                SetVideoEncoding(p, true);
            while (peers.Any(p => p.Connection.ConnectionState != RTCPeerConnectionState.Connected))
                yield return null;
            Log("CYCLE " + cycle + " CONNECTED both peers");
            float mediaStart = Time.realtimeSinceStartup;
            bool ready = false;
            while (!ready || Time.realtimeSinceStartup - mediaStart < MinimumMediaSeconds)
            {
                foreach (Peer p in peers)
                    yield return Snapshot(p);
                ready = peers.All(MediaReady);
                status = "Cycle " + cycle + "/" + CycleCount + ": media " +
                    (Time.realtimeSinceStartup - mediaStart).ToString("F1") + "s; " +
                    string.Join(" | ", peers.Select(p => "P" + p.Index + " decoded=" + p.Last.Frames +
                        " audioNonzero=" + Volatile.Read(ref p.NonzeroAudioCallbacks)));
                float next = Time.realtimeSinceStartup + 1f;
                while (Time.realtimeSinceStartup < next)
                    yield return null;
            }
            foreach (Peer p in peers)
                Require(MediaReady(p), "incomplete inbound media on P" + p.Index);
            Log("CYCLE " + cycle + " MEDIA PASS both peers have " + (UseVp8 ? "VP8" : "H264") + " frames, growing RTP counters and nonzero decoded audio");
            if (EnableTransforms)
            {
                QuestRegressionChecks.RequireTransformsObserved();
                Log("CYCLE " + cycle + " TRANSFORMS " + QuestRegressionChecks.TransformSummary);
            }
            foreach (Peer p in peers) AhbNativeProbe.RequireTexture(p.ReceivedTexture, ExpectedPathMode, "P" + p.Index);
            yield return ValidateReceiverPixels();
            if (cycle == 1)
            {
                yield return ProbeColorPalette();
                yield return ProbeUploadPerformanceAndRetention();
            }
            yield return QuestRegressionChecks.ValidateReceiverSources(peers.SelectMany(p => p.Connection.GetReceivers()));
            AhbNativeProbe.ReportDelta("cycle-" + cycle, cycleCounters, ExpectedPathMode, true);
            Cleanup();
            cycleDeadline = 0;
            Log("CYCLE " + cycle + " DISPOSED");
            // Let deferred Unity destruction and native render cleanup drain.
            float pauseUntil = Time.realtimeSinceStartup + 0.75f;
            while (Time.realtimeSinceStartup < pauseUntil)
                yield return null;
        }
        Require(string.IsNullOrEmpty(failure), failure);
        status = "PASS: " + CycleCount + " bidirectional video/audio cycles, disposal and reconnect";
        Log("FINAL PASS cycles=" + CycleCount + " freshConnections=" + CycleCount + " reconnects=" + (CycleCount - 1) +
            " inboundVideoAndAudioPeers=2 codec=" + (UseVp8 ? "VP8" : "H264") + " scope=2D-package-loopback");
    }

    void CreatePeer(Peer p)
    {
        var config = new RTCConfiguration { iceServers = new RTCIceServer[0] };
        p.Connection = new RTCPeerConnection(ref config);
        p.Connection.OnConnectionStateChange = state =>
        {
            if (!p.Active) return;
            Log("CYCLE " + cycle + " P" + p.Index + " state=" + state);
            if (state == RTCPeerConnectionState.Failed)
                Fail("P" + p.Index + " connection failed");
        };
    }

    void WireCallbacks(Peer p, Peer other)
    {
        p.Connection.OnIceCandidate = candidate =>
        {
            if (candidate == null) return;
            try
            {
                if (!other.Active) { candidate.Dispose(); return; }
                other.Ice.Enqueue(candidate);
                FlushIce(other);
            }
            catch (Exception ex) { Fail("ICE: " + ex); }
        };
        p.Connection.OnTrack = e =>
        {
            if (!p.Active) return;
            try
            {
                if (e.Track is VideoStreamTrack video)
                {
                    Require(p.ReceivedVideo == null, "duplicate received video on P" + p.Index);
                    p.ReceivedVideo = video;
                    p.VideoHandler = texture =>
                    {
                        if (!p.Active) return;
                        p.ReceivedTexture = texture;
                        Interlocked.Increment(ref p.VideoNotifications);
                    };
                    video.OnVideoReceived += p.VideoHandler;
                }
                else if (e.Track is AudioStreamTrack audio)
                {
                    Require(p.ReceivedAudio == null, "duplicate received audio on P" + p.Index);
                    p.ReceivedAudio = audio;
                    p.OutputObject = new GameObject("ReceivedAudio-P" + p.Index);
                    p.Output = p.OutputObject.AddComponent<AudioSource>();
                    p.Output.spatialBlend = 0;
                    p.Output.volume = 1f; // Synthetic source samples already have amplitude 0.03.
                    p.AudioHandler = (data, channels, sampleRate) =>
                    {
                        // Audio thread: only sample inspection and atomic counters.
                        Interlocked.Increment(ref p.AudioCallbacks);
                        for (int i = 0; i < data.Length; i++)
                        {
                            if (Math.Abs(data[i]) > 0.0001f)
                            {
                                Interlocked.Increment(ref p.NonzeroAudioCallbacks);
                                break;
                            }
                        }
                    };
                    audio.onReceived += p.AudioHandler;
                    p.Output.SetTrack(audio); // Starts receiver audio processing/playout.
                }
                Log("CYCLE " + cycle + " P" + p.Index + " receivedTrack=" + e.Track.Kind);
            }
            catch (Exception ex) { Fail("OnTrack: " + ex); }
        };
    }

    void AddSyntheticTracks(Peer p)
    {
        p.CameraObject = new GameObject("SyntheticCamera-P" + p.Index);
        p.Camera = p.CameraObject.AddComponent<Camera>();
        p.Camera.clearFlags = CameraClearFlags.SolidColor;
        p.Camera.cullingMask = 0;
        p.Camera.depth = -10 - p.Index;
        p.SendVideo = p.Camera.CaptureStreamTrack(VideoWidth, VideoHeight);
        p.SendTexture = p.Camera.targetTexture;
        var videoSender = p.Connection.AddTrack(p.SendVideo);
        p.VideoSender = videoSender;
        if (EnableTransforms) p.VideoTransform = QuestRegressionChecks.AttachTransform(videoSender, TrackKind.Video);
        var receivers = RTCRtpReceiver.GetCapabilities(TrackKind.Video).codecs;
        // Use a stock hardware-decodable common format in BOTH modes. The donor
        // advertises High profiles that its delegate decoder cannot necessarily create.
        var receiverBaseline = receivers.Where(c => UseVp8 ? IsVp8(c) : IsRequiredH264Format(c)).ToArray();
        var h264 = RTCRtpSender.GetCapabilities(TrackKind.Video).codecs
            .Where(c => (UseVp8 ? IsVp8(c) : IsRequiredH264Format(c)) && receiverBaseline.Length > 0).ToArray();
        Log("H264_CAPABILITIES P" + p.Index + " requested=" + (UseVp8 ? "VP8" : "H264 42e01f packetizationMode=1") +
            " receiverFormats=" + string.Join(" | ", receiverBaseline.Select(c => c.sdpFmtpLine)) +
            " selectedSenderFormats=" + string.Join(" | ", h264.Select(c => c.sdpFmtpLine)));
        Require(h264.Length > 0, "no common requested sender/receiver video codec; refuse codec/profile fallback");
        var transceiver = p.Connection.GetTransceivers().First(t => t.Sender == videoSender);
        Require(transceiver.SetCodecPreferences(h264) == RTCErrorType.None, "SetCodecPreferences(video) failed");

        p.InputObject = new GameObject("SyntheticTone-P" + p.Index);
        p.Input = p.InputObject.AddComponent<AudioSource>();
        p.Input.spatialBlend = 0;
        p.Input.loop = true;
        p.Input.volume = 1f;
        const int sampleRate = 48000;
        float frequency = p.Index == 0 ? 440f : 660f;
        var samples = new float[sampleRate];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = 0.03f * (float)Math.Sin(2.0 * Math.PI * frequency * i / sampleRate);
        p.Clip = AudioClip.Create("Synthetic-" + frequency + "Hz", sampleRate, 1, sampleRate, false);
        Require(p.Clip.SetData(samples, 0), "AudioClip.SetData failed");
        p.Input.clip = p.Clip;
        p.SendAudio = new AudioStreamTrack(p.Input) { Loopback = false };
        var audioSender = p.Connection.AddTrack(p.SendAudio);
        if (EnableTransforms) p.AudioTransform = QuestRegressionChecks.AttachTransform(audioSender, TrackKind.Audio);
        p.Input.Play();
        Log("CYCLE " + cycle + " P" + p.Index + " SEND video=" + VideoWidth + "x" + VideoHeight +
            " maxFramerate=" + VideoMaximumFps + " codec=" + (UseVp8 ? "VP8" : "H264") + " tone=" + frequency + "Hz amplitude=0.03 localLoopback=false");
    }

    static bool IsVp8(RTCRtpCodecCapability codec)
    {
        return string.Equals(codec.mimeType, "video/VP8", StringComparison.OrdinalIgnoreCase);
    }

    static bool IsRequiredH264Format(RTCRtpCodecCapability codec)
    {
        if (!string.Equals(codec.mimeType, "video/H264", StringComparison.OrdinalIgnoreCase)) return false;
        return string.Equals(FmtpValue(codec.sdpFmtpLine, "profile-level-id"), "42e01f", StringComparison.OrdinalIgnoreCase) &&
            FmtpValue(codec.sdpFmtpLine, "packetization-mode") == "1";
    }

    static string FmtpValue(string fmtp, string key)
    {
        foreach (string parameter in (fmtp ?? "").Split(';'))
        {
            string[] pair = parameter.Split(new[] { '=' }, 2);
            if (pair.Length == 2 && string.Equals(pair[0].Trim(), key, StringComparison.OrdinalIgnoreCase))
                return pair[1].Trim();
        }
        return "";
    }

    IEnumerator Negotiate(Peer offerer, Peer answerer)
    {
        var offerOp = offerer.Connection.CreateOffer();
        yield return offerOp;
        Require(!offerOp.IsError, "CreateOffer: " + offerOp.Error.message);
        var offer = offerOp.Desc;
        var op1 = offerer.Connection.SetLocalDescription(ref offer);
        yield return op1;
        Require(!op1.IsError, "offer SetLocalDescription: " + op1.Error.message);
        var op2 = answerer.Connection.SetRemoteDescription(ref offer);
        yield return op2;
        Require(!op2.IsError, "offer SetRemoteDescription: " + op2.Error.message);
        answerer.RemoteReady = true;
        FlushIce(answerer);
        var answerOp = answerer.Connection.CreateAnswer();
        yield return answerOp;
        Require(!answerOp.IsError, "CreateAnswer: " + answerOp.Error.message);
        var answer = answerOp.Desc;
        var op3 = answerer.Connection.SetLocalDescription(ref answer);
        yield return op3;
        Require(!op3.IsError, "answer SetLocalDescription: " + op3.Error.message);
        var op4 = offerer.Connection.SetRemoteDescription(ref answer);
        yield return op4;
        Require(!op4.IsError, "answer SetRemoteDescription: " + op4.Error.message);
        offerer.RemoteReady = true;
        FlushIce(offerer);
    }

    void FlushIce(Peer p)
    {
        if (!p.RemoteReady || !p.Active) return;
        while (p.Ice.Count > 0)
        {
            using (var candidate = p.Ice.Dequeue())
                Require(p.Connection.AddIceCandidate(candidate), "AddIceCandidate failed P" + p.Index);
        }
    }

    IEnumerator Snapshot(Peer p)
    {
        var op = p.Connection.GetStats();
        yield return op;
        Require(!op.IsError, "GetStats failed P" + p.Index);
        using (var report = op.Value)
        {
            var n = new Numbers();
            foreach (var inbound in report.Stats.Values.OfType<RTCInboundRTPStreamStats>())
            {
                string codec = "unknown", fmtp = "";
                if (!string.IsNullOrEmpty(inbound.codecId) && report.Stats.TryGetValue(inbound.codecId, out var found) && found is RTCCodecStats c)
                {
                    codec = c.mimeType;
                    fmtp = c.sdpFmtpLine;
                }
                if (inbound.kind == "video")
                {
                    n.Frames += inbound.framesDecoded;
                    n.VideoPackets += inbound.packetsReceived;
                    n.VideoBytes += inbound.bytesReceived;
                    n.VideoCodec = codec;
                    n.VideoFmtp = fmtp;
                    n.DecoderImplementation = inbound.decoderImplementation;
                }
                else if (inbound.kind == "audio")
                {
                    n.AudioPackets += inbound.packetsReceived;
                    n.AudioBytes += inbound.bytesReceived;
                    n.AudioSamples += inbound.totalSamplesReceived;
                    n.AudioCodec = codec;
                }
            }
            p.Last = n;
            if (p.First == null)
            {
                p.First = n;
                p.FirstNonzeroAudioCallbacks = Volatile.Read(ref p.NonzeroAudioCallbacks);
            }
            Log("CYCLE " + cycle + " P" + p.Index + " STATS videoCodec=" + n.VideoCodec +
                " fmtp=" + n.VideoFmtp + " decoderImplementation=" + n.DecoderImplementation +
                " framesDecoded=" + n.Frames + " videoPackets=" + n.VideoPackets + " videoBytes=" + n.VideoBytes +
                " audioCodec=" + n.AudioCodec + " audioPackets=" + n.AudioPackets + " audioBytes=" + n.AudioBytes +
                " audioSamples=" + n.AudioSamples + " audioCallbacks=" + Volatile.Read(ref p.AudioCallbacks) +
                " nonzeroAudioCallbacks=" + Volatile.Read(ref p.NonzeroAudioCallbacks) +
                " receivedTexture=" + (p.ReceivedTexture != null));
        }
    }

    bool MediaReady(Peer p)
    {
        if (p.First == null || p.Last == null) return false;
        Numbers a = p.First, b = p.Last;
        return p.Connection.ConnectionState == RTCPeerConnectionState.Connected &&
            p.ReceivedTexture != null && Volatile.Read(ref p.VideoNotifications) > 0 &&
            b.Frames > a.Frames + 1 && b.VideoPackets > a.VideoPackets && b.VideoBytes > a.VideoBytes &&
            b.AudioPackets > a.AudioPackets && b.AudioBytes > a.AudioBytes && b.AudioSamples > a.AudioSamples &&
            Volatile.Read(ref p.NonzeroAudioCallbacks) > p.FirstNonzeroAudioCallbacks &&
            string.Equals(b.VideoCodec, UseVp8 ? "video/VP8" : "video/H264", StringComparison.OrdinalIgnoreCase);
    }

    void SetVideoEncoding(Peer peer, bool active)
    {
        var parameters = peer.VideoSender.GetParameters();
        Require(parameters.encodings != null && parameters.encodings.Length > 0,
            "no video encodings on P" + peer.Index);
        foreach (var encoding in parameters.encodings)
        {
            encoding.active = active;
            encoding.maxFramerate = VideoMaximumFps;
        }
        var error = peer.VideoSender.SetParameters(parameters);
        Require(error.errorType == RTCErrorType.None,
            "SetParameters active=" + active + " P" + peer.Index + ": " + error.errorType);
    }

    IEnumerator ProbeUploadPerformanceAndRetention()
    {
        Log("UPLOAD_PROBE BEGIN targetAppFps=90 videoMaxFps=" + VideoMaximumFps +
            " streams=2 resolution=" + VideoWidth + "x" + VideoHeight);
        yield return MeasureUploadWindow("active", 5f);
        foreach (Peer p in peers)
            SetVideoEncoding(p, false);

        // Drain encoded/network/decoder work before asserting there is no new source frame.
        uint[] previousFrames = peers.Select(p => p.Last.Frames).ToArray();
        int stableIntervals = 0;
        for (int i = 0; i < 10 && stableIntervals < 3; i++)
        {
            float until = Time.realtimeSinceStartup + 0.5f;
            while (Time.realtimeSinceStartup < until)
                yield return null;
            foreach (Peer p in peers)
                yield return Snapshot(p);
            stableIntervals = peers.All(p => p.Last.Frames == previousFrames[p.Index]) ? stableIntervals + 1 : 0;
            previousFrames = peers.Select(p => p.Last.Frames).ToArray();
        }
        Require(stableIntervals >= 3, "video did not stop decoding after encodings were paused");

        Texture[] pausedTextures = peers.Select(p => p.ReceivedTexture).ToArray();
        Color32[] pausedColors = peers.Select(p => ReadCenterPixel(p.ReceivedTexture)).ToArray();
        foreach (Peer p in peers)
        {
            Color32 color = pausedColors[p.Index];
            Require(Math.Max(color.r, Math.Max(color.g, color.b)) > 30,
                "expected visible synthetic video before pause on P" + p.Index);
            Log("UPLOAD_RETAIN P" + p.Index + " phase=beforePauseWindow pixel=" + PixelText(color) +
                " framesDecoded=" + previousFrames[p.Index]);
        }
        yield return MeasureUploadWindow("paused", 4f);
        foreach (Peer p in peers)
        {
            Require(p.Last.Frames == previousFrames[p.Index], "new decoded video during paused upload probe P" + p.Index);
            Require(p.ReceivedTexture == pausedTextures[p.Index], "received texture replaced while paused P" + p.Index);
            Color32 retained = ReadCenterPixel(p.ReceivedTexture);
            Require(PixelDistance(pausedColors[p.Index], retained) <= 2,
                "last received texture pixels lost while no new frame arrived P" + p.Index);
            Log("UPLOAD_RETAIN P" + p.Index + " phase=afterPauseWindow pixel=" + PixelText(retained) +
                " preserved=true framesDecoded=" + p.Last.Frames);
        }

        // Force a visibly different source so resumed RTP alone cannot hide a stalled texture.
        foreach (Peer p in peers)
        {
            p.ForcedColor = Color.white;
            SetVideoEncoding(p, true);
        }
        yield return MeasureUploadWindow("resumed", 5f);
        foreach (Peer p in peers)
        {
            Require(p.Last.Frames > previousFrames[p.Index], "video did not resume decoding P" + p.Index);
            Color32 resumed = ReadCenterPixel(p.ReceivedTexture);
            Require(PixelDistance(pausedColors[p.Index], resumed) > 20,
                "new decoded video did not change received texture pixels P" + p.Index);
            Log("UPLOAD_RESUME P" + p.Index + " pixel=" + PixelText(resumed) +
                " changed=true framesDecoded=" + p.Last.Frames);
        }
        Log("UPLOAD_PROBE PASS pausedFramesStable=true lastPixelsPreserved=true resumedPixelsChanged=true");
    }

    IEnumerator MeasureUploadWindow(string phase, float seconds)
    {
        foreach (Peer p in peers)
            yield return Snapshot(p);
        uint[] decodedBefore = peers.Select(p => p.Last.Frames).ToArray();
        ulong[] pathBefore = AhbNativeProbe.Counters();
        int appFrameBefore = Time.frameCount;
        double start = Time.realtimeSinceStartupAsDouble;
        using (var recorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, UploadMarker, 2048,
                   ProfilerRecorderOptions.SumAllSamplesInFrame))
        {
            Require(recorder.Valid || ExpectedPathMode >= 2, "native upload profiler marker unavailable: " + UploadMarker);
            AhbNativeProbe.InTimingWindow = true;
            while (Time.realtimeSinceStartupAsDouble - start < seconds)
                yield return null;
            AhbNativeProbe.InTimingWindow = false;
            if (recorder.Valid) recorder.Stop();
            double elapsed = Time.realtimeSinceStartupAsDouble - start;
            int appFrames = Time.frameCount - appFrameBefore;
            var samples = new List<ProfilerRecorderSample>(recorder.Valid ? recorder.Count : 0);
            if (recorder.Valid) recorder.CopyTo(samples);
            long totalCpuNs = 0, calls = 0;
            foreach (var sample in samples)
            {
                totalCpuNs += sample.Value;
                calls += sample.Count;
            }
            Require(ExpectedPathMode >= 2 || (samples.Count > 0 && calls > 0), "no native upload profiler samples for phase=" + phase);
            Require(!recorder.Valid || recorder.UnitType.ToString() == "TimeNanoseconds",
                "unexpected upload profiler unit: " + recorder.UnitType);
            Require(!recorder.Valid || (!recorder.WrappedAround && samples.Count < 2048), "upload recorder exceeded measurement capacity");
            Log("UPLOAD_PERF phase=" + phase + " seconds=" + Number(elapsed) +
                " appFrames=" + appFrames + " appFps=" + Number(appFrames / elapsed) +
                " sampleFrames=" + samples.Count + " markerCalls=" + calls + " totalCpuNs=" + totalCpuNs +
                " meanCpuNsPerAppFrame=" + Number((double)totalCpuNs / Math.Max(1, appFrames)) +
                " meanCpuNsPerCall=" + Number((double)totalCpuNs / Math.Max(1, calls)) +
                " meanCpuNsPerRecordedFrame=" + Number((double)totalCpuNs / Math.Max(1, samples.Count)) +
                " markerValid=" + recorder.Valid + " scope=CPU-upload-marker-only-excludes-GPU-execution-and-AHB-command-recording");
            AhbNativeProbe.ReportDelta(phase, pathBefore, ExpectedPathMode, phase == "active" || phase == "resumed");
            foreach (Peer p in peers)
            {
                yield return Snapshot(p);
                Log("UPLOAD_VIDEO phase=" + phase + " P" + p.Index +
                    " framesDecodedDelta=" + (p.Last.Frames - decodedBefore[p.Index]) +
                    " approxDecodedFps=" + Number((p.Last.Frames - decodedBefore[p.Index]) / elapsed) +
                    " receivedTexture=" + p.ReceivedTexture.width + "x" + p.ReceivedTexture.height +
                    " configuredSource=" + VideoWidth + "x" + VideoHeight + " configuredMaxFps=" + VideoMaximumFps);
            }
        }
    }

    IEnumerator ValidateReceiverPixels()
    {
        // Correctness-only readbacks, outside timed windows.
        foreach (Peer p in peers) p.ForcedColor = p.Index == 0 ? new Color(0.8f, 0.1f, 0.1f) : new Color(0.1f, 0.8f, 0.1f);
        float until = Time.realtimeSinceStartup + 1f;
        while (Time.realtimeSinceStartup < until) yield return null;
        Color32[] before = new Color32[peers.Length];
        foreach (Peer p in peers)
        {
            Color32 pixel = ReadCenterPixel(p.ReceivedTexture);
            Require(p.Index == 0 ? pixel.g > pixel.r + 30 && pixel.g > pixel.b + 30 :
                pixel.r > pixel.g + 30 && pixel.r > pixel.b + 30,
                "received source color wrong/stale on P" + p.Index + " pixel=" + PixelText(pixel));
            before[p.Index] = pixel;
            Log("PIXEL_COLOR P" + p.Index + " pixel=" + PixelText(pixel) + " expected=" + (p.Index == 0 ? "green" : "red"));
        }
        foreach (Peer p in peers) p.ForcedColor = Color.white;
        until = Time.realtimeSinceStartup + 1f;
        while (Time.realtimeSinceStartup < until) yield return null;
        foreach (Peer p in peers)
        {
            Color32 pixel = ReadCenterPixel(p.ReceivedTexture);
            Require(pixel.r > 180 && pixel.g > 180 && pixel.b > 180 && PixelDistance(before[p.Index], pixel) > 30,
                "received pixels failed to progress to white on P" + p.Index + " pixel=" + PixelText(pixel));
            Log("PIXEL_PROGRESS P" + p.Index + " pixel=" + PixelText(pixel) + " changedToWhite=true");
            p.ForcedColor = null;
        }
    }

    IEnumerator ProbeColorPalette()
    {
        // Both peers send identical solid colors so the two receive paths can
        // be compared without a second client. Readback stays outside timing.
        var names = new[] { "black", "gray25", "gray50", "gray75", "red", "green", "blue", "cyan", "magenta", "yellow", "skin" };
        var colors = new[] { Color.black, new Color(.25f,.25f,.25f), new Color(.5f,.5f,.5f),
            new Color(.75f,.75f,.75f), new Color(.8f,.1f,.1f), new Color(.1f,.8f,.1f),
            new Color(.1f,.1f,.8f), new Color(.1f,.8f,.8f), new Color(.8f,.1f,.8f),
            new Color(.8f,.8f,.1f), new Color(.65f,.38f,.25f) };
        for (int i = 0; i < colors.Length; i++)
        {
            foreach (Peer p in peers) p.ForcedColor = colors[i];
            float until = Time.realtimeSinceStartup + .5f;
            while (Time.realtimeSinceStartup < until) yield return null;
            foreach (Peer p in peers)
                Log("COLOR_SAMPLE name=" + names[i] + " P" + p.Index + " pixel=" + PixelText(ReadCenterPixel(p.ReceivedTexture)));
        }
        foreach (Peer p in peers) p.ForcedColor = null;
    }

    static Color32 ReadCenterPixel(Texture texture)
    {
        Require(!AhbNativeProbe.InTimingWindow, "ReadPixels forbidden inside timing window");
        Require(texture != null, "received texture is missing during pixel readback");
        var target = RenderTexture.GetTemporary(1, 1, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
        var probe = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
        var previous = RenderTexture.active;
        try
        {
            Graphics.Blit(texture, target);
            RenderTexture.active = target;
            probe.ReadPixels(new Rect(0, 0, 1, 1), 0, 0, false);
            return (Color32)probe.GetPixel(0, 0);
        }
        finally
        {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(target);
            Destroy(probe);
        }
    }

    static int PixelDistance(Color32 a, Color32 b)
    {
        return Math.Max(Math.Abs(a.r - b.r), Math.Max(Math.Abs(a.g - b.g), Math.Abs(a.b - b.b)));
    }

    static string PixelText(Color32 color) { return color.r + "," + color.g + "," + color.b + "," + color.a; }
    static string Number(double value) { return value.ToString("F3", CultureInfo.InvariantCulture); }

    void Update()
    {
        if (peers == null) return;
        foreach (Peer p in peers)
            if (p.Active && p.Camera != null)
                p.Camera.backgroundColor = p.ForcedColor ??
                    Color.HSVToRGB(Mathf.Repeat(Time.unscaledTime * 0.17f + p.Index * 0.5f, 1f), 0.8f, 0.8f);
    }

    void Cleanup()
    {
        if (cleaning || peers == null) return;
        cleaning = true;
        foreach (Peer p in peers) p.Active = false;
        foreach (Peer p in peers)
        {
            Safe(() => { if (p.Input != null) p.Input.Stop(); });
            Safe(() => { if (p.Output != null) p.Output.Stop(); });
            Safe(() => { if (p.Camera != null) p.Camera.enabled = false; });
            Safe(() => { if (p.Connection != null) { p.Connection.OnIceCandidate = null; p.Connection.OnTrack = null; p.Connection.OnConnectionStateChange = null; p.Connection.Close(); } });
            Safe(() => { if (p.ReceivedAudio != null && p.AudioHandler != null) p.ReceivedAudio.onReceived -= p.AudioHandler; });
            Safe(() => { if (p.ReceivedVideo != null && p.VideoHandler != null) p.ReceivedVideo.OnVideoReceived -= p.VideoHandler; });
            Safe(() => p.ReceivedAudio?.Dispose());
            Safe(() => p.ReceivedVideo?.Dispose());
            Safe(() => p.SendAudio?.Dispose());
            Safe(() => p.SendVideo?.Dispose());
            Safe(() => p.Connection?.Dispose());
            Safe(() => p.VideoTransform?.Dispose());
            Safe(() => p.AudioTransform?.Dispose());
            while (p.Ice.Count > 0) Safe(() => p.Ice.Dequeue().Dispose());
            p.ReceivedTexture = null;
            if (p.Camera != null) p.Camera.targetTexture = null;
            // VideoTrack disposal queues native render work; release the source RT
            // after that grace period rather than destroying it immediately.
            if (p.SendTexture != null) Destroy(p.SendTexture, 0.5f);
            if (p.Clip != null) Destroy(p.Clip);
            if (p.CameraObject != null) Destroy(p.CameraObject);
            if (p.InputObject != null) Destroy(p.InputObject);
            if (p.OutputObject != null) Destroy(p.OutputObject);
        }
        peers = null;
        cleaning = false;
    }

    void Safe(Action action)
    {
        try { action(); }
        catch (Exception ex) { Fail("cleanup: " + ex); }
    }

    void Fail(string reason)
    {
        if (string.IsNullOrEmpty(failure)) failure = reason;
        Debug.LogError(Prefix + reason);
    }

    static void Require(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
    }

    static void Log(string text) { Debug.Log(Prefix + text); }

    void OnGUI()
    {
        GUI.Label(new Rect(15, 15, Screen.width - 30, 70), "QUEST_SMOKE\n" + status);
        GUI.Label(new Rect(15, 90, Screen.width - 30, 50),
            (UseVp8 ? "VP8" : "H264") + " AHB spike mode=" + AhbMode + "; synthetic 2D Vulkan. No AROC/XR, external network, camera or mic.");
        if (peers == null) return;
        float width = Mathf.Min(320, (Screen.width - 50) / 2f);
        foreach (Peer p in peers)
        {
            float x = 15 + p.Index * (width + 15);
            GUI.Label(new Rect(x, 145, width, 25), "P" + p.Index + " received video");
            if (p.ReceivedTexture != null)
                GUI.DrawTexture(new Rect(x, 175, width, width * 0.75f), p.ReceivedTexture, ScaleMode.ScaleToFit);
        }
    }

    void OnDestroy()
    {
        if (!finished) Log("STOPPED before final result");
        Cleanup();
        if (updateRoutine != null) StopCoroutine(updateRoutine);
    }
}
