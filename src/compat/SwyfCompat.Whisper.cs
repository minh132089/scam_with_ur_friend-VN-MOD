// Whisper GPU helper, called at the start of the patched WhisperManager.CreateContextParams (main thread,
// right before the native context is created):
//  1. forwards whisper.cpp / ggml native log lines into the Unity log ("[whisper.cpp] ...") so GPU
//     initialisation success or failure is visible in Player.log;
//  2. preloads ggml-vulkan.dll from <Game>_Data/Plugins/x86_64 with the same safe search flags the game
//     uses for libwhisper.dll, so ggml's own lookup finds it already loaded;
//  3. logs whether the game's CPU-only switch (KOLKATA_WHISPER_CPU_ONLY) is set.
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace SwyfCompat
{
    // Snappier voice input once Whisper runs on the GPU (~0.5 s per pass): applied where WhisperVcPipeline
    // reads its serialized timing fields. Smaller scene values are kept.
    public static class SpeechTuning
    {
        public const float MaxSilenceToFinalizeSeconds = 0.8f;   // game default 1.25 s
        public const float MaxPartialIntervalSeconds = 0.5f;     // game default 1 s

        public static float SilenceToFinalize(float sceneValue) { return sceneValue > MaxSilenceToFinalizeSeconds ? MaxSilenceToFinalizeSeconds : sceneValue; }
        public static float PartialInterval(float sceneValue) { return sceneValue > MaxPartialIntervalSeconds ? MaxPartialIntervalSeconds : sceneValue; }
    }

    public static class WhisperGpu
    {
        private const uint SearchFlags = 0x100 | 0x200 | 0x800; // DLL_LOAD_DIR | APPLICATION_DIR | SYSTEM32

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void NativeLog(int level, IntPtr text, IntPtr userData);

        [DllImport("libwhisper", CallingConvention = CallingConvention.Cdecl)]
        private static extern void whisper_log_set(NativeLog callback, IntPtr userData);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandleW(string name);

        private static NativeLog keepAlive;   // the native side keeps the pointer; never let it be collected
        private static bool prepared;
        private static readonly object LineGate = new object();
        private static string pending = string.Empty;

        public static void Prepare()
        {
            VieNeuTts.Prewarm();   // every model init (e.g. switching calls to Vietnamese); idempotent
            if (prepared) return;
            prepared = true;
            try
            {
                keepAlive = OnNativeLog;
                whisper_log_set(keepAlive, IntPtr.Zero);
            }
            catch (Exception e) { UnityEngine.Debug.LogWarning("[SwyfWhisper] Could not hook native whisper log: " + e.Message); }

            string cpuOnly = Environment.GetEnvironmentVariable("KOLKATA_WHISPER_CPU_ONLY");
            if (!string.IsNullOrEmpty(cpuOnly))
                UnityEngine.Debug.LogWarning("[SwyfWhisper] KOLKATA_WHISPER_CPU_ONLY=" + cpuOnly + " is set: the game forces Whisper onto the CPU.");

            try
            {
                string managed = Path.GetDirectoryName(typeof(WhisperGpu).Assembly.Location) ?? ".";
                string plugins = Path.GetFullPath(Path.Combine(Path.Combine(managed, ".."), Path.Combine("Plugins", "x86_64")));
                string vulkan = Path.Combine(plugins, "ggml-vulkan.dll");
                if (GetModuleHandleW("ggml-vulkan.dll") != IntPtr.Zero)
                {
                    UnityEngine.Debug.Log("[SwyfWhisper] ggml-vulkan.dll already loaded.");
                }
                else if (!File.Exists(vulkan))
                {
                    UnityEngine.Debug.LogWarning("[SwyfWhisper] ggml-vulkan.dll not found at " + vulkan);
                }
                else
                {
                    IntPtr h = LoadLibraryExW(vulkan, IntPtr.Zero, SearchFlags);
                    if (h == IntPtr.Zero)
                        UnityEngine.Debug.LogWarning("[SwyfWhisper] Preloading ggml-vulkan.dll failed (Win32 " + Marshal.GetLastWin32Error() + "): " + vulkan);
                    else
                        UnityEngine.Debug.Log("[SwyfWhisper] Preloaded ggml-vulkan.dll for GPU speech recognition.");
                }
            }
            catch (Exception e) { UnityEngine.Debug.LogWarning("[SwyfWhisper] Vulkan preload skipped: " + e.Message); }
        }

        // ggml sends partial fragments; emit complete lines only.
        private static void OnNativeLog(int level, IntPtr text, IntPtr userData)
        {
            try
            {
                string s = Marshal.PtrToStringAnsi(text);
                if (string.IsNullOrEmpty(s)) return;
                lock (LineGate)
                {
                    pending += s;
                    int nl;
                    while ((nl = pending.IndexOf('\n')) >= 0)
                    {
                        string line = pending.Substring(0, nl).TrimEnd('\r');
                        pending = pending.Substring(nl + 1);
                        if (line.Trim().Length > 0) UnityEngine.Debug.Log("[whisper.cpp] " + line);
                    }
                    if (pending.Length > 4000) { UnityEngine.Debug.Log("[whisper.cpp] " + pending); pending = string.Empty; }
                }
            }
            catch (Exception) { }
        }
    }
}
