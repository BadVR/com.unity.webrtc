using System;
using System.Runtime.InteropServices;
using UnityEngine;

// Evaluation-only API: both A/B modes require the same instrumented native binary.
public static class AhbNativeProbe
{
    public static bool InTimingWindow;
    [DllImport("webrtc", CallingConvention = CallingConvention.Cdecl)]
    static extern int GetAhbDisplayMode();
    [DllImport("webrtc", CallingConvention = CallingConvention.Cdecl)]
    static extern ulong GetReceivePathCounter(int index);
    [DllImport("webrtc", CallingConvention = CallingConvention.Cdecl)]
    static extern void ResetReceivePathCounters();

    public static int DisplayMode() => GetAhbDisplayMode();
    public static void Reset() => ResetReceivePathCounters();
    public static ulong[] Counters()
    {
        var values = new ulong[6];
        for (int i = 0; i < values.Length; i++) values[i] = GetReceivePathCounter(i);
        return values;
    }

    public static int IntOption(string name, int fallback)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
        using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
        using (var intent = activity.Call<AndroidJavaObject>("getIntent"))
            return intent.Call<int>("getIntExtra", name, fallback);
#else
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i + 1 < args.Length; i++)
            if (args[i] == "-" + name && int.TryParse(args[i + 1], out int value)) return value;
        return fallback;
#endif
    }

    public static void RequireTexture(Texture texture, int mode, string label)
    {
        Require(texture != null, label + " missing receive texture");
        if (mode >= 2)
        {
            var rt = texture as RenderTexture;
            Require(rt != null && rt.enableRandomWrite && rt.IsCreated(),
                label + " AHB requires a created random-write RenderTexture; actual=" + texture.GetType().Name);
        }
        else Require(texture is Texture2D, label + " delegate requires CPU Texture2D; actual=" + texture.GetType().Name);
        Debug.Log("QUEST_SMOKE RECEIVE_TEXTURE " + label + " ahbMode=" + mode + " type=" + texture.GetType().Name +
            " dimensions=" + texture.width + "x" + texture.height + " randomWrite=" +
            (texture is RenderTexture renderTexture && renderTexture.enableRandomWrite));
    }

    public static void ReportDelta(string phase, ulong[] before, int mode, bool requireActive)
    {
        ulong[] after = Counters(), delta = new ulong[6];
        for (int i = 0; i < delta.Length; i++)
        {
            Require(after[i] >= before[i], "native counter reset/wrapped during " + phase + " index=" + i);
            delta[i] = after[i] - before[i];
        }
        Debug.Log("QUEST_SMOKE PATH_COUNTERS phase=" + phase + " ahbMode=" + mode +
            " ahbDecoded=" + delta[0] + " gpuWrites=" + delta[1] + " cpuI420=" + delta[2] +
            " cpuUploads=" + delta[3] + " importFailures=" + delta[4] + " skippedAhb=" + delta[5] +
            " activeRequired=" + requireActive);
        Require(delta[4] == 0, "AHB import failure during " + phase);
        if (mode >= 2)
        {
            Require(delta[2] == 0 && delta[3] == 0, "receive CPU pixel path used in AHB mode during " + phase);
            if (requireActive) Require(delta[0] > 0 && delta[1] > 0, "AHB decode/GPU write did not progress during " + phase);
        }
        else
        {
            Require(delta[0] == 0 && delta[1] == 0, "AHB path unexpectedly active in delegate mode during " + phase);
            if (requireActive) Require(delta[2] > 0 && delta[3] > 0, "delegate receive CPU conversion/upload did not progress during " + phase);
        }
    }

    static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException("AHB_PROBE " + message);
    }
}
