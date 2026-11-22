// Loads Whisper models directly from disk in native code. whisper.unity's InitFromFileAsync reads the whole model
// (574 MB for large-v3-turbo, 147 MB for base.en) into a managed byte[] first; Unity's Mono GC rarely returns that
// heap growth to Windows, so the game kept ~0.7 GB more committed memory for the whole session. Called instead of
// WhisperWrapper.InitFromFileAsync from the patched WhisperManager.InitModel.
using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Whisper;
using Whisper.Native;

namespace SwyfCompat
{
    public static class WhisperFileLoader
    {
        [DllImport("libwhisper", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern IntPtr whisper_init_from_file_with_params(string pathModel, WhisperNativeContextParams parameters);

        public static Task<WhisperWrapper> InitFromFileAsync(string modelPath, WhisperContextParams contextParams)
        {
            return Task.Factory.StartNew(() => InitFromFile(modelPath, contextParams));
        }

        private static WhisperWrapper InitFromFile(string modelPath, WhisperContextParams contextParams)
        {
            if (string.IsNullOrEmpty(modelPath) || !File.Exists(modelPath))
            {
                UnityEngine.Debug.LogError("[SwyfWhisper] Whisper model not found: " + modelPath);
                return null;
            }
            ConstructorInfo ctor = typeof(WhisperWrapper).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(IntPtr) }, null);
            if (ctor == null)
            {
                UnityEngine.Debug.LogWarning("[SwyfWhisper] WhisperWrapper(IntPtr) not found, using the game's buffer loader.");
                return WhisperWrapper.InitFromFileAsync(modelPath, contextParams).GetAwaiter().GetResult();
            }
            DateTime started = DateTime.UtcNow;
            IntPtr ctx;
            try { ctx = whisper_init_from_file_with_params(modelPath, contextParams.NativeParams); }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning("[SwyfWhisper] Native file loader unavailable (" + e.GetType().Name + "): " + e.Message);
                ctx = IntPtr.Zero;
            }
            if (ctx == IntPtr.Zero)
            {
                // e.g. a path native fopen cannot open: fall back to the game's own buffer loader (runs on this worker thread).
                UnityEngine.Debug.LogWarning("[SwyfWhisper] Native file load failed, using the game's buffer loader: " + modelPath);
                return WhisperWrapper.InitFromFileAsync(modelPath, contextParams).GetAwaiter().GetResult();
            }
            UnityEngine.Debug.Log("[SwyfWhisper] Loaded " + Path.GetFileName(modelPath) + " natively from disk in "
                + (int)(DateTime.UtcNow - started).TotalMilliseconds + " ms (no managed copy).");
            return (WhisperWrapper)ctor.Invoke(new object[] { ctx });
        }
    }
}
