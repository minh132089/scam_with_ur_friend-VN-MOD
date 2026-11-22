// Vietnamese caller voices via a local VieNeu-TTS server (https://github.com/pnnbao97/VieNeu-TTS, Apache-2.0).
// Tried first by VietnameseTts.TrySynthesize; Piper (then MOSS) is the fallback.
//
// Config: <Game>_Data/StreamingAssets/swyf-voice.json
//   { "engine": "vieneu" | "piper", "vieneu_url": "http://127.0.0.1:8000", "vieneu_dir": "D:\\...\\VieNeu-TTS",
//     "autostart": true, "voices": { "<reference wav name>": "<VieNeu voice>" } }
// The server is started on the first Vietnamese line (python -m apps.openai_speech, CPU int8) and lines spoken
// while it loads use Piper. Audio is streamed as 24 kHz s16le PCM and handed to the game chunk by chunk.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using PocketTTS;

namespace SwyfCompat
{
    public static class VieNeuTts
    {
        private const int SampleRate = 24000;
        private const int ChunkSamples = 2400;               // 0.1 s per chunk handed to the game
        private const float Gain = 0.9f;
        private static readonly string[] FallbackMale = { "Hải Đăng", "Thiện Minh", "Quang Sơn", "Minh Đức", "Phạm Tuyên", "Quốc Tuấn", "Thái Sơn", "Minh Triết", "Đức Trí", "Adam" };
        private static readonly string[] FallbackFemale = { "Mai Anh", "Trúc Ly", "Thùy Dung", "Ngọc Huyền", "Ngọc Trân", "Ngọc Linh", "Đoan Trang", "Quỳnh Anh", "Mỹ Duyên", "Kim Thanh" };

        private enum State { Unknown, Starting, Ready, Disabled }

        private static readonly object Gate = new object();
        private static State state = State.Unknown;
        private static Process server;
        private static DateTime lastHealthCheck = DateTime.MinValue;
        private static List<string> maleVoices, femaleVoices;
        private static readonly Dictionary<string, string> VoiceCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static bool exitHooked;

        // ---------- config ----------
        private static string engine = "vieneu", baseUrl = "http://127.0.0.1:8000", vieneuDir = "";
        private static bool autostart = true;
        private static Dictionary<string, string> overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static DateTime configStamp = DateTime.MinValue;

        private static string ConfigPath
        {
            get
            {
                string managed = Path.GetDirectoryName(typeof(VieNeuTts).Assembly.Location) ?? ".";
                return Path.GetFullPath(Path.Combine(Path.Combine(managed, ".."), Path.Combine("StreamingAssets", "swyf-voice.json")));
            }
        }

        private static void LoadConfig()
        {
            try
            {
                string path = ConfigPath;
                if (!File.Exists(path)) { engine = "piper"; return; }
                DateTime stamp = File.GetLastWriteTimeUtc(path);
                if (stamp == configStamp) return;
                JObject cfg = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
                engine = ((string)cfg["engine"] ?? "vieneu").Trim().ToLowerInvariant();
                baseUrl = ((string)cfg["vieneu_url"] ?? "http://127.0.0.1:8000").Trim().TrimEnd('/');
                vieneuDir = ((string)cfg["vieneu_dir"] ?? "").Trim();
                // A relative vieneu_dir (mod installer default "SwyfMod\\VieNeu-TTS") is relative to the folder holding the game exe.
                if (vieneuDir.Length > 0 && !Path.IsPathRooted(vieneuDir))
                    vieneuDir = Path.GetFullPath(Path.Combine(Path.Combine(Path.GetDirectoryName(path) ?? ".", Path.Combine("..", "..")), vieneuDir));
                autostart = cfg["autostart"] == null || (bool)cfg["autostart"];
                Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                JObject voices = cfg["voices"] as JObject;
                if (voices != null) foreach (JProperty p in voices.Properties()) if (p.Value.Type == JTokenType.String) map[p.Name] = (string)p.Value;
                overrides = map;
                lock (VoiceCache) VoiceCache.Clear();
                configStamp = stamp;
                UnityEngine.Debug.Log("[SwyfVieNeu] config: engine=" + engine + " url=" + baseUrl + " autostart=" + autostart + " overrides=" + map.Count);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning("[SwyfVieNeu] swyf-voice.json ignored: " + e.Message);
            }
        }

        // Called on the main thread when Whisper is being set up (WhisperGpu.Prepare): if calls are in Vietnamese,
        // start the server in the background so it is usually ready before the first caller line.
        public static void Prewarm()
        {
            try
            {
                string code = null;
                try { Kolkata.Speech.CallLanguageOption current = Kolkata.Speech.CallLanguage.Current; code = current == null ? null : current.Code; } catch (Exception) { }
                if (code != "vi") return;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { lock (Gate) LoadConfig(); if (engine == "vieneu") EnsureServer(); }
                    catch (Exception e) { UnityEngine.Debug.LogWarning("[SwyfVieNeu] prewarm failed: " + e.Message); }
                });
            }
            catch (Exception) { }
        }

        // ---------- entry point ----------

        // Streams the line through VieNeu. Returns false (nothing emitted) when VieNeu is off, still starting,
        // or failed before producing audio, so the caller can fall back to Piper.
        public static bool TryStream(string text, string voicePath, Action<PocketTtsAudioResponse> emit, CancellationToken ct)
        {
            lock (Gate) LoadConfig();
            if (engine != "vieneu" || !EnsureServer()) return false;
            string voice = VoiceFor(voicePath);
            bool emittedAny = false;
            try
            {
                foreach (PromptSegment segment in PauseTagParser.Parse(text))
                {
                    ct.ThrowIfCancellationRequested();
                    if (segment.IsPause)
                    {
                        if (segment.PauseSamples > 0) { emit(new PocketTtsAudioResponse(new float[segment.PauseSamples], segment.PauseSamples, null)); emittedAny = true; }
                        continue;
                    }
                    StringBuilder speech = new StringBuilder();
                    Queue<string> words = new Queue<string>();
                    foreach (PromptSegment span in TranscriptTagParser.ParseSpeechSpans(segment.Text))
                    {
                        speech.Append(span.Text);
                        Queue<string> w = span.Text != span.TranscriptText
                            ? TranscriptTagParser.CreateOverrideWords(span.Text, span.TranscriptText)
                            : TranscriptTagParser.CreateTranscriptWords(span.Text);
                        foreach (string x in w) words.Enqueue(x);
                    }
                    string spoken = speech.ToString().Trim();
                    if (spoken.Length == 0) continue;
                    if (StreamSpeech(spoken, voice, words, emit, ct)) emittedAny = true;
                }
                return emittedAny;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning("[SwyfVieNeu] " + (emittedAny ? "stream interrupted: " : "failed, using Piper: ") + e.Message);
                MarkUnhealthy();
                return emittedAny;
            }
        }

        // ---------- HTTP streaming ----------

        private static bool StreamSpeech(string input, string voice, Queue<string> words, Action<PocketTtsAudioResponse> emit, CancellationToken ct)
        {
            JObject body = new JObject();
            body["model"] = "vieneu-v3-turbo";
            body["input"] = input;
            body["voice"] = voice;
            body["response_format"] = "pcm";
            body["sample_rate"] = SampleRate;
            byte[] payload = Encoding.UTF8.GetBytes(body.ToString(Newtonsoft.Json.Formatting.None));

            using (LocalHttpResponse resp = LocalHttp.Send("POST", new Uri(baseUrl + "/v1/audio/speech"), payload, "application/json", 30000, ct))
            {
                if (resp.Status != 200)
                {
                    string detail = string.Empty;
                    try { detail = resp.ReadAllText(); } catch (Exception) { }
                    throw new IOException("HTTP " + resp.Status + " " + (detail.Length > 200 ? detail.Substring(0, 200) : detail));
                }
                Stream s = resp.Body;
                {
                    byte[] buf = new byte[8192];
                    float[] chunk = new float[ChunkSamples];
                    int filled = 0, until = 0;
                    int carry = -1;                  // odd byte left over between reads
                    float[] pending = null;
                    bool any = false;
                    int n;
                    while ((n = s.Read(buf, 0, buf.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        int i = 0;
                        if (carry >= 0 && n > 0)
                        {
                            chunk[filled++] = (short)(carry | (buf[0] << 8)) / 32768f * Gain;
                            carry = -1; i = 1;
                            if (filled == ChunkSamples) { Flush(ref pending, chunk, filled, words, ref until, emit); chunk = new float[ChunkSamples]; filled = 0; any = true; }
                        }
                        for (; i + 1 < n; i += 2)
                        {
                            chunk[filled++] = (short)(buf[i] | (buf[i + 1] << 8)) / 32768f * Gain;
                            if (filled == ChunkSamples) { Flush(ref pending, chunk, filled, words, ref until, emit); chunk = new float[ChunkSamples]; filled = 0; any = true; }
                        }
                        if (i < n) carry = buf[i];
                    }
                    // Last chunk carries the remaining transcript words.
                    if (filled > 0) { Flush(ref pending, chunk, filled, words, ref until, emit); any = true; }
                    if (pending != null)
                    {
                        emit(new PocketTtsAudioResponse(pending, pending.Length, SpeechTranscriptTiming.TakeTranscriptWords(words, ref until, pending.Length, true)));
                        any = true;
                    }
                    return any;
                }
            }
        }

        // Emits the previously buffered chunk (not final) and keeps the new one pending, so the final chunk can
        // be marked isFinalChunk once the stream ends (same pattern as MossOnnxRuntime).
        private static void Flush(ref float[] pending, float[] chunk, int length, Queue<string> words, ref int until, Action<PocketTtsAudioResponse> emit)
        {
            float[] data = chunk;
            if (length != chunk.Length) { data = new float[length]; Array.Copy(chunk, data, length); }
            if (pending != null)
                emit(new PocketTtsAudioResponse(pending, pending.Length, SpeechTranscriptTiming.TakeTranscriptWords(words, ref until, pending.Length, false)));
            pending = data;
        }

        // ---------- server lifecycle ----------

        private static bool EnsureServer()
        {
            lock (Gate)
            {
                if (state == State.Disabled) return false;
                if (state == State.Ready && (DateTime.UtcNow - lastHealthCheck).TotalSeconds < 30) return true;
                bool healthy = Healthy(state == State.Ready ? 1500 : 400);
                lastHealthCheck = DateTime.UtcNow;
                if (healthy)
                {
                    if (state != State.Ready) UnityEngine.Debug.Log("[SwyfVieNeu] Server ready at " + baseUrl);
                    state = State.Ready;
                    return true;
                }
                if (state == State.Starting)
                {
                    if (server != null && server.HasExitedSafe()) { state = State.Disabled; UnityEngine.Debug.LogWarning("[SwyfVieNeu] Server process exited; using Piper. See " + LogPath); }
                    return false;
                }
                if (!autostart) { state = State.Unknown; return false; }
                StartServer();
                return false;
            }
        }

        private static void MarkUnhealthy()
        {
            lock (Gate) { if (state == State.Ready) state = State.Unknown; lastHealthCheck = DateTime.MinValue; }
        }

        private static bool Healthy(int timeoutMs)
        {
            try
            {
                using (LocalHttpResponse resp = LocalHttp.Send("GET", new Uri(baseUrl + "/health"), null, null, timeoutMs, CancellationToken.None))
                    return resp.Status == 200;
            }
            catch (Exception) { return false; }
        }

        private static string LogPath { get { return Path.Combine(Path.GetTempPath(), "swyf-vieneu-server.log"); } }

        private static void StartServer()
        {
            string scripts = Path.Combine(Path.Combine(vieneuDir, ".venv"), "Scripts");
            string python = Path.Combine(scripts, "pythonw.exe");
            if (!File.Exists(python)) python = Path.Combine(scripts, "python.exe");
            string launcher = Path.Combine(vieneuDir, "swyf_launch.py");
            if (string.IsNullOrEmpty(vieneuDir) || !File.Exists(python) || !File.Exists(launcher))
            {
                state = State.Disabled;
                UnityEngine.Debug.LogWarning("[SwyfVieNeu] VieNeu not installed at '" + vieneuDir + "' (need .venv and swyf_launch.py); using Piper.");
                return;
            }
            Uri uri = new Uri(baseUrl);
            int gamePid = 0;
            try { gamePid = Process.GetCurrentProcess().Id; } catch (Exception) { }
            // ShellExecute: the server must not inherit the game's handles (e.g. Player.log). swyf_launch.py sets
            // the environment, writes its own log and exits when the game process ends.
            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = python,
                Arguments = "\"" + launcher + "\" \"" + LogPath + "\" " + gamePid + " " + uri.Port,
                WorkingDirectory = vieneuDir,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            try
            {
                Process p = Process.Start(psi);
                if (p == null) throw new IOException("Process.Start returned null");
                server = p;
                state = State.Starting;
                if (!exitHooked) { exitHooked = true; try { UnityEngine.Application.quitting += StopServer; } catch (Exception) { } }
                UnityEngine.Debug.Log("[SwyfVieNeu] Starting VieNeu server (CPU int8); Piper speaks until it is ready. Log: " + LogPath);
            }
            catch (Exception e)
            {
                state = State.Disabled;
                UnityEngine.Debug.LogWarning("[SwyfVieNeu] Could not start VieNeu server: " + e.Message + "; using Piper.");
            }
        }

        // The venv launcher spawns the real interpreter as a child, so stop the whole tree. (The server also exits
        // on its own when the game process ends.)
        private static void StopServer()
        {
            Process p = server;
            server = null;
            if (p == null) return;
            try
            {
                if (!p.HasExited)
                {
                    ProcessStartInfo kill = new ProcessStartInfo("taskkill", "/T /F /PID " + p.Id) { UseShellExecute = false, CreateNoWindow = true };
                    using (Process k = Process.Start(kill)) { if (k != null) k.WaitForExit(2000); }
                }
            }
            catch (Exception) { }
            try { if (!p.HasExited) p.Kill(); } catch (Exception) { }
        }

        private static bool HasExitedSafe(this Process p)
        {
            try { return p.HasExited; } catch (Exception) { return true; }
        }

        // ---------- voices ----------

        private static string VoiceFor(string voicePath)
        {
            string key = IdentityKey(voicePath);
            lock (VoiceCache)
            {
                string cached;
                if (VoiceCache.TryGetValue(key, out cached)) return cached;
            }
            string voice;
            if (!overrides.TryGetValue(key, out voice))
            {
                LoadVoiceLists();
                // Prefer the caller's declared gender (kolkata-json/caller-identities); otherwise guess from pitch.
                string identityKey = IdentityKey(voicePath);
                string declared = CallerGender(identityKey);
                bool male;
                string reason;
                if (declared == "male" || declared == "female") { male = declared == "male"; reason = "gender " + declared; }
                else
                {
                    float hz = 0f;
                    try { hz = string.IsNullOrEmpty(voicePath) ? 0f : VietnameseTts.EstimateVoicePitchHz(voicePath); } catch (Exception) { }
                    male = hz > 0f && hz < 165f;
                    reason = "pitch " + hz.ToString("F0") + " Hz";
                }
                List<string> pool = male ? maleVoices : femaleVoices;
                voice = pool[(int)(StableHash(identityKey) % (uint)pool.Count)];
                UnityEngine.Debug.Log("[SwyfVieNeu] Caller voice " + identityKey + " (" + reason + ") -> " + voice);
            }
            lock (VoiceCache) VoiceCache[key] = voice;
            return voice;
        }

        private static void LoadVoiceLists()
        {
            if (maleVoices != null) return;
            List<string> male = new List<string>(), female = new List<string>();
            try
            {
                using (LocalHttpResponse resp = LocalHttp.Send("GET", new Uri(baseUrl + "/v1/voices"), null, null, 3000, CancellationToken.None))
                {
                    if (resp.Status != 200) throw new IOException("HTTP " + resp.Status);
                    JToken root = JToken.Parse(resp.ReadAllText());
                    JArray list = root as JArray ?? root["data"] as JArray ?? root["voices"] as JArray;
                    if (list != null)
                        foreach (JToken v in list)
                        {
                            string name = (string)v["id"] ?? (string)v["name"];
                            string gender = ((string)v["gender"] ?? "").ToLowerInvariant();
                            if (string.IsNullOrEmpty(name)) continue;
                            if (gender.StartsWith("m") || gender.Contains("nam")) male.Add(name);
                            else if (gender.StartsWith("f") || gender.Contains("nữ") || gender.Contains("nu")) female.Add(name);
                        }
                }
            }
            catch (Exception e) { UnityEngine.Debug.LogWarning("[SwyfVieNeu] Voice list unavailable (" + e.Message + "); using built-in names."); }
            maleVoices = male.Count > 0 ? male : new List<string>(FallbackMale);
            femaleVoices = female.Count > 0 ? female : new List<string>(FallbackFemale);
            UnityEngine.Debug.Log("[SwyfVieNeu] Voices: " + maleVoices.Count + " male, " + femaleVoices.Count + " female.");
        }

        // Key shared by voice paths and identity voiceIds: file name without extension, or the parent folder
        // for the generic ".../<id>/voice.wav" layout used by default callers.
        private static string IdentityKey(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            string p = path.Replace('\\', '/').TrimEnd('/');
            string[] parts = p.Split('/');
            string last = parts[parts.Length - 1];
            int dot = last.LastIndexOf('.');
            if (dot > 0) last = last.Substring(0, dot);
            if (last.Equals("voice", StringComparison.OrdinalIgnoreCase) && parts.Length > 1) last = parts[parts.Length - 2];
            return last.ToLowerInvariant();
        }

        private static Dictionary<string, string> genders;

        private static string CallerGender(string identityKey)
        {
            if (genders == null)
            {
                Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    string managed = Path.GetDirectoryName(typeof(VieNeuTts).Assembly.Location) ?? ".";
                    string dir = Path.GetFullPath(Path.Combine(Path.Combine(managed, ".."), Path.Combine("StreamingAssets", Path.Combine("kolkata-json", "caller-identities"))));
                    if (Directory.Exists(dir))
                    {
                        foreach (string file in Directory.GetFiles(dir, "*.json"))
                        {
                            try
                            {
                                JToken root = JToken.Parse(File.ReadAllText(file, Encoding.UTF8));
                                IEnumerable<JToken> items = root is JArray ? (IEnumerable<JToken>)root : new[] { root };
                                foreach (JToken id in items)
                                {
                                    string gender = ((string)id["gender"] ?? "").Trim().ToLowerInvariant();
                                    foreach (string field in new[] { "voiceId", "revealedVoiceId" })
                                    {
                                        string v = (string)id[field];
                                        if (!string.IsNullOrEmpty(v)) map[IdentityKey(v)] = gender;
                                    }
                                }
                            }
                            catch (Exception) { }
                        }
                    }
                }
                catch (Exception) { }
                genders = map;
                UnityEngine.Debug.Log("[SwyfVieNeu] Loaded gender for " + map.Count + " caller voices.");
            }
            string g;
            return genders.TryGetValue(identityKey ?? "", out g) ? g : null;
        }

        private static uint StableHash(string s)
        {
            uint h = 2166136261;
            foreach (char c in s ?? "") { h ^= c; h *= 16777619; }
            return h;
        }
    }
}
