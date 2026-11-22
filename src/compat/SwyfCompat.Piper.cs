// Vietnamese caller voice via Piper (offline). Called from the patched MOSS worker
// (MossTtsSingleton <Prompt>b__0): when the call language is Vietnamese, Piper produces the audio and the
// game's own callback receives it; otherwise MOSS runs unchanged.
//
// Realism: each caller's reference voice (the WAV MOSS/Pocket would clone) is analysed once for its median
// pitch. Piper renders with length_scale = p and the result is played back at rate 22050*p, which shifts
// pitch (and formants) by p while keeping the speaking speed: low-pitched callers sound deeper.
//
// Layout (next to the game): <Game>_Data/StreamingAssets/piper/piper.exe (+ dlls, espeak-ng-data)
//                            <Game>_Data/StreamingAssets/piper/voices/vi_VN-vais1000-medium.onnx(.json)
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using PocketTTS;

namespace SwyfCompat
{
    public static class VietnameseTts
    {
        private const int TargetRate = 24000;          // MOSS / PocketTTS frame rate
        private const float Gain = 0.8f;               // Piper normalizes to full scale; leave headroom
        private const int RequestTimeoutMs = 20000;
        private const string VoiceFile = "vi_VN-vais1000-medium.onnx";
        private const string Expressiveness = "--noise_scale 0.6 --noise_w 0.6 --sentence_silence 0.15";
        public const float PiperBasePitchHz = 279f;     // measured median pitch of the vais1000 voice (young female)
        // The caller's absolute pitch is mapped linearly: ~120 Hz (deep male) -> x0.72, ~260 Hz (high/child) -> x1.05.
        // A plain ratio to 279 Hz would push almost every caller, including most women, to the lowest setting.
        public const float LowPitchHz = 120f, LowFactor = 0.72f;
        public const float HighPitchHz = 260f, HighFactor = 1.05f;
        public const float MinPitchFactor = 0.72f;
        public const float MaxPitchFactor = 1.08f;

        public static float MapPitch(float hz)
        {
            if (hz <= 0f) return 1f;
            float f = LowFactor + (hz - LowPitchHz) * (HighFactor - LowFactor) / (HighPitchHz - LowPitchHz);
            return Math.Max(MinPitchFactor, Math.Min(MaxPitchFactor, f));
        }
        private const int MaxWorkers = 2;

        private sealed class Worker
        {
            public Process Process;
            public BlockingCollection<string> Outputs;
            public int Bucket;
            public long LastUsed;
            public volatile string LastStderr = string.Empty;
            public bool Alive { get { try { return Process != null && !Process.HasExited; } catch (Exception) { return false; } } }
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<int, Worker> Workers = new Dictionary<int, Worker>();
        private static readonly Dictionary<string, float> PitchCache = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        private static string outputDir;
        private static long useCounter;
        private static bool exitHooked;
        private static string lastError = string.Empty;

        private static string PiperRoot
        {
            get
            {
                // Avoid Unity APIs here: this runs on the MOSS worker thread.
                string managed = Path.GetDirectoryName(typeof(VietnameseTts).Assembly.Location) ?? ".";
                return Path.GetFullPath(Path.Combine(Path.Combine(managed, ".."), Path.Combine("StreamingAssets", "piper")));
            }
        }

        private static string ExePath { get { return Path.Combine(PiperRoot, "piper.exe"); } }
        private static string ModelPath { get { return Path.Combine(Path.Combine(PiperRoot, "voices"), VoiceFile); } }

        public static bool Available { get { return File.Exists(ExePath) && File.Exists(ModelPath) && File.Exists(ModelPath + ".json"); } }

        // Returns true when Piper handled the request (audio already passed to emit); false to let MOSS run.
        public static bool TrySynthesize(string text, string voicePath, Action<PocketTtsAudioResponse> emit, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(text) || emit == null || !IsVietnamese(text)) return false;
            // Preferred engine: local VieNeu-TTS server (streams audio). Falls through to Piper when off/starting/failed.
            if (VieNeuTts.TryStream(text, voicePath, emit, ct)) return true;
            if (!Available) return false;
            List<PocketTtsAudioResponse> parts;
            try
            {
                parts = Build(text, PitchFactorFor(voicePath), ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning("[SwyfPiper] Vietnamese voice failed, using MOSS instead: " + e.Message + LastError());
                return false;
            }
            foreach (PocketTtsAudioResponse part in parts)
            {
                ct.ThrowIfCancellationRequested();
                emit(part);
            }
            return true;
        }

        private static bool IsVietnamese(string text)
        {
            string code = null;
            try
            {
                Kolkata.Speech.CallLanguageOption current = Kolkata.Speech.CallLanguage.Current;
                code = current == null ? null : current.Code;
            }
            catch (Exception) { }
            if (code == "vi") return true;
            if (code != null) return false;
            return LooksVietnamese(text);
        }

        private static bool LooksVietnamese(string text)
        {
            foreach (char c in text)
            {
                if ((c >= 'Ạ' && c <= 'ỹ') || c == 'ơ' || c == 'Ơ' || c == 'ư' || c == 'Ư' || c == 'đ' || c == 'Đ' || c == 'ă' || c == 'Ă')
                    return true;
            }
            return false;
        }

        // ---------- pitch matching ----------

        public static float PitchFactorFor(string voicePath)
        {
            if (string.IsNullOrEmpty(voicePath)) return 1f;
            lock (PitchCache)
            {
                float cached;
                if (PitchCache.TryGetValue(voicePath, out cached)) return cached;
            }
            float factor = 1f;
            float hz = 0f;
            try
            {
                hz = EstimateVoicePitchHz(voicePath);
                factor = MapPitch(hz);
            }
            catch (Exception) { factor = 1f; }
            lock (PitchCache) { PitchCache[voicePath] = factor; }
            UnityEngine.Debug.Log("[SwyfPiper] Voice " + Path.GetFileName(voicePath) + ": pitch " + hz.ToString("F0", CultureInfo.InvariantCulture)
                + " Hz -> factor " + factor.ToString("F2", CultureInfo.InvariantCulture));
            return factor;
        }

        // Median F0 of voiced frames (normalized autocorrelation, 70-400 Hz). Returns 0 when unknown.
        public static float EstimateVoicePitchHz(string wavPath)
        {
            if (!File.Exists(wavPath)) return 0f;
            int rate;
            float[] x = ReadWavMono(File.ReadAllBytes(wavPath), out rate);
            // Work at ~12 kHz on at most 12 s of audio.
            int decim = Math.Max(1, rate / 12000);
            int fs = rate / decim;
            int n = Math.Min(x.Length / decim, fs * 12);
            float[] y = new float[n];
            for (int i = 0; i < n; i++)
            {
                float acc = 0f;
                for (int k = 0; k < decim; k++) acc += x[i * decim + k];
                y[i] = acc / decim;
            }
            int frame = fs * 40 / 1000, hop = fs * 10 / 1000;
            int minLag = fs / 400, maxLag = fs / 70;
            if (n < frame + maxLag) return 0f;
            double maxRms = 0;
            int frames = (n - frame - maxLag) / hop;
            double[] rms = new double[frames];
            for (int f = 0; f < frames; f++)
            {
                double e = 0; int s = f * hop;
                for (int i = 0; i < frame; i++) e += y[s + i] * y[s + i];
                rms[f] = Math.Sqrt(e / frame);
                if (rms[f] > maxRms) maxRms = rms[f];
            }
            List<float> pitches = new List<float>();
            for (int f = 0; f < frames; f++)
            {
                if (rms[f] < maxRms * 0.15) continue;
                int s = f * hop;
                double e0 = 0;
                for (int i = 0; i < frame; i++) e0 += y[s + i] * y[s + i];
                double best = 0; int bestLag = 0;
                for (int lag = minLag; lag <= maxLag; lag++)
                {
                    double c = 0, e1 = 0;
                    for (int i = 0; i < frame; i++) { c += y[s + i] * y[s + i + lag]; e1 += y[s + i + lag] * y[s + i + lag]; }
                    double r = c / Math.Sqrt(e0 * e1 + 1e-12);
                    if (r > best) { best = r; bestLag = lag; }
                }
                if (best > 0.6 && bestLag > 0) pitches.Add((float)fs / bestLag);
            }
            if (pitches.Count < 10) return 0f;
            pitches.Sort();
            return pitches[pitches.Count / 2];
        }

        // ---------- synthesis ----------

        // Mirrors MossOnnxRuntime.Synthesize: pause tags become silence, transcript words follow the audio.
        private static List<PocketTtsAudioResponse> Build(string text, float pitch, CancellationToken ct)
        {
            List<PocketTtsAudioResponse> parts = new List<PocketTtsAudioResponse>();
            foreach (PromptSegment segment in PauseTagParser.Parse(text))
            {
                ct.ThrowIfCancellationRequested();
                if (segment.IsPause)
                {
                    if (segment.PauseSamples > 0) parts.Add(new PocketTtsAudioResponse(new float[segment.PauseSamples], segment.PauseSamples, null));
                    continue;
                }
                StringBuilder speech = new StringBuilder();
                Queue<string> words = new Queue<string>();
                foreach (PromptSegment span in TranscriptTagParser.ParseSpeechSpans(segment.Text))
                {
                    speech.Append(span.Text);
                    Queue<string> spanWords = span.Text != span.TranscriptText
                        ? TranscriptTagParser.CreateOverrideWords(span.Text, span.TranscriptText)
                        : TranscriptTagParser.CreateTranscriptWords(span.Text);
                    foreach (string w in spanWords) words.Enqueue(w);
                }
                string spoken = Normalize(speech.ToString());
                if (spoken.Length == 0) continue;
                float[] pcm = Speak(spoken, pitch, ct);
                if (pcm.Length == 0) continue;
                int until = 0;
                parts.Add(new PocketTtsAudioResponse(pcm, pcm.Length, SpeechTranscriptTiming.TakeTranscriptWords(words, ref until, pcm.Length, true)));
            }
            if (parts.Count == 0) throw new InvalidOperationException("Piper produced no audio.");
            return parts;
        }

        private static string Normalize(string s)
        {
            StringBuilder b = new StringBuilder(s.Length);
            bool space = false;
            foreach (char c in s)
            {
                if (char.IsWhiteSpace(c) || char.IsControl(c)) { space = b.Length > 0; continue; }
                if (space) { b.Append(' '); space = false; }
                b.Append(c);
            }
            return b.ToString();
        }

        // Measured on vais1000: duration ratio produced by a given --length_scale (the voice reacts sub-linearly).
        private static readonly float[] RatioPoints = { 0.731f, 0.783f, 0.870f, 0.945f, 1.000f, 1.040f, 1.145f };
        private static readonly float[] ScalePoints = { 0.40f, 0.55f, 0.70f, 0.85f, 1.00f, 1.15f, 1.30f };

        // length_scale whose duration ratio equals the pitch factor, so resampling by the factor restores the speed.
        public static float LengthScaleFor(float pitch)
        {
            if (pitch <= RatioPoints[0]) return Math.Max(0.45f, ScalePoints[0]);
            for (int i = 1; i < RatioPoints.Length; i++)
            {
                if (pitch <= RatioPoints[i])
                {
                    float t = (pitch - RatioPoints[i - 1]) / (RatioPoints[i] - RatioPoints[i - 1]);
                    return Math.Max(0.45f, ScalePoints[i - 1] + t * (ScalePoints[i] - ScalePoints[i - 1]));
                }
            }
            return ScalePoints[ScalePoints.Length - 1];
        }

        private static float[] Speak(string line, float pitch, CancellationToken ct)
        {
            int bucket = (int)Math.Round(pitch * 20f);        // 0.05 steps
            float pitchStep = bucket / 20f;
            float lengthScale = LengthScaleFor(pitchStep);
            lock (Gate)
            {
                for (int attempt = 1; ; attempt++)
                {
                    Worker worker = GetWorker(bucket, lengthScale, pitchStep);
                    string file = Path.Combine(outputDir, "swyf_" + Guid.NewGuid().ToString("N") + ".wav");
                    try
                    {
                        string json = "{\"text\": \"" + JsonEscape(line) + "\", \"output_file\": \"" + JsonEscape(file) + "\"}\n";
                        byte[] bytes = Encoding.UTF8.GetBytes(json);
                        worker.Process.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
                        worker.Process.StandardInput.BaseStream.Flush();
                        WaitFor(worker, Path.GetFileName(file), ct);
                        int rate;
                        float[] mono = ReadWavMono(File.ReadAllBytes(file), out rate);
                        for (int i = 0; i < mono.Length; i++) mono[i] *= Gain;
                        // Rendered shorter/longer by ~p; playing it back at rate*p restores the speed and shifts pitch by p.
                        return Resample(mono, rate * (double)pitchStep, TargetRate);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception e) when (attempt == 1)
                    {
                        lastError = e.Message + (string.IsNullOrEmpty(worker.LastStderr) ? "" : " / " + worker.LastStderr);
                        StopWorker(worker); // restart once (process died or got stuck)
                    }
                    finally
                    {
                        TryDelete(file);
                    }
                }
            }
        }

        private static void WaitFor(Worker worker, string fileName, CancellationToken ct)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(RequestTimeoutMs);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (remaining <= 0) throw new TimeoutException("Piper did not answer in time.");
                string line;
                if (!worker.Outputs.TryTake(out line, Math.Min(remaining, 200), ct))
                {
                    if (!worker.Alive) throw new IOException("Piper exited.");
                    continue;
                }
                if (line.IndexOf(fileName, StringComparison.OrdinalIgnoreCase) >= 0) return;
                // Output of an earlier, cancelled request: delete it and keep waiting.
                TryDelete(line.Trim().Replace("\\\\", "\\"));
            }
        }

        private static Worker GetWorker(int bucket, float lengthScale, float pitchStep)
        {
            Worker worker;
            if (Workers.TryGetValue(bucket, out worker) && worker.Alive)
            {
                worker.LastUsed = ++useCounter;
                return worker;
            }
            if (worker != null) StopWorker(worker);
            while (Workers.Count >= MaxWorkers)
            {
                Worker oldest = null;
                foreach (Worker w in Workers.Values) if (oldest == null || w.LastUsed < oldest.LastUsed) oldest = w;
                StopWorker(oldest);
            }
            worker = StartWorker(bucket, lengthScale, pitchStep);
            Workers[bucket] = worker;
            return worker;
        }

        private static Worker StartWorker(int bucket, float lengthScale, float pitchStep)
        {
            outputDir = Path.Combine(Path.GetTempPath(), "swyf-piper");
            Directory.CreateDirectory(outputDir);
            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = ExePath,
                Arguments = "--model \"" + ModelPath + "\" --json-input --output_dir \"" + outputDir + "\" --length_scale "
                    + lengthScale.ToString("0.00", CultureInfo.InvariantCulture) + " " + Expressiveness,
                WorkingDirectory = PiperRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            Process p = Process.Start(psi);
            if (p == null) throw new IOException("Could not start piper.exe.");
            Worker worker = new Worker { Process = p, Outputs = new BlockingCollection<string>(), Bucket = bucket, LastUsed = ++useCounter };
            Thread stdout = new Thread(() =>
            {
                try { string l; while ((l = p.StandardOutput.ReadLine()) != null) worker.Outputs.Add(l); }
                catch (Exception) { }
                finally { try { worker.Outputs.CompleteAdding(); } catch (Exception) { } }
            }) { IsBackground = true, Name = "SwyfPiper stdout" };
            Thread stderr = new Thread(() =>
            {
                try { string l; while ((l = p.StandardError.ReadLine()) != null) worker.LastStderr = l; }
                catch (Exception) { }
            }) { IsBackground = true, Name = "SwyfPiper stderr" };
            stdout.Start();
            stderr.Start();
            if (!exitHooked)
            {
                exitHooked = true;
                // piper.exe also exits on its own when the game closes (its stdin pipe closes).
                try { UnityEngine.Application.quitting += StopAll; } catch (Exception) { }
            }
            UnityEngine.Debug.Log("[SwyfPiper] Started Vietnamese voice (" + VoiceFile + ", pitch x"
                + pitchStep.ToString("0.00", CultureInfo.InvariantCulture) + ", length_scale "
                + lengthScale.ToString("0.00", CultureInfo.InvariantCulture) + ").");
            return worker;
        }

        private static void StopWorker(Worker worker)
        {
            if (worker == null) return;
            Workers.Remove(worker.Bucket);
            Process p = worker.Process;
            worker.Process = null;
            if (p == null) return;
            try { if (!p.HasExited) { p.StandardInput.Close(); if (!p.WaitForExit(500)) p.Kill(); } } catch (Exception) { }
            try { p.Dispose(); } catch (Exception) { }
        }

        private static void StopAll()
        {
            lock (Gate)
            {
                foreach (Worker w in new List<Worker>(Workers.Values)) StopWorker(w);
            }
        }

        // ---------- audio helpers ----------

        private static float[] ReadWavMono(byte[] b, out int rate)
        {
            if (b.Length < 44 || Encoding.ASCII.GetString(b, 0, 4) != "RIFF") throw new InvalidDataException("Not a WAV file.");
            int channels = 1, bits = 16, format = 1, pos = 12, dataStart = -1, dataLen = 0;
            rate = 22050;
            while (pos + 8 <= b.Length)
            {
                string id = Encoding.ASCII.GetString(b, pos, 4);
                int size = BitConverter.ToInt32(b, pos + 4);
                if (id == "fmt ")
                {
                    format = BitConverter.ToInt16(b, pos + 8);
                    channels = BitConverter.ToInt16(b, pos + 10);
                    rate = BitConverter.ToInt32(b, pos + 12);
                    bits = BitConverter.ToInt16(b, pos + 22);
                }
                else if (id == "data")
                {
                    dataStart = pos + 8;
                    dataLen = Math.Min(size, b.Length - dataStart);
                    break;
                }
                if (size < 0) break;
                pos += 8 + size + (size & 1);
            }
            if (dataStart < 0 || channels < 1) throw new InvalidDataException("Unsupported WAV layout.");
            int bytesPerSample = bits / 8;
            int frames = dataLen / (bytesPerSample * channels);
            float[] mono = new float[frames];
            for (int i = 0; i < frames; i++)
            {
                float acc = 0f;
                for (int ch = 0; ch < channels; ch++)
                {
                    int o = dataStart + (i * channels + ch) * bytesPerSample;
                    if (bits == 16) acc += BitConverter.ToInt16(b, o) / 32768f;
                    else if (bits == 32 && format == 3) throw new InvalidDataException("Float WAV not supported."); // BitConverter.ToSingle is stripped in the game's mscorlib
                    else if (bits == 32) acc += BitConverter.ToInt32(b, o) / 2147483648f;
                    else if (bits == 24) acc += ((b[o] | (b[o + 1] << 8) | ((sbyte)b[o + 2] << 16))) / 8388608f;
                    else if (bits == 8) acc += (b[o] - 128) / 128f;
                    else throw new InvalidDataException("Unsupported WAV bit depth.");
                }
                mono[i] = acc / channels;
            }
            return mono;
        }

        private static float[] Resample(float[] input, double from, int to)
        {
            if (Math.Abs(from - to) < 0.5 || input.Length == 0) return input;
            int outLength = (int)((double)input.Length * to / from);
            float[] output = new float[outLength];
            double step = from / to;
            for (int i = 0; i < outLength; i++)
            {
                double src = i * step;
                int i0 = (int)src;
                if (i0 >= input.Length - 1) { output[i] = input[input.Length - 1]; continue; }
                double t = src - i0;
                output[i] = (float)(input[i0] * (1 - t) + input[i0 + 1] * t);
            }
            return output;
        }

        private static string JsonEscape(string s)
        {
            StringBuilder b = new StringBuilder(s.Length + 16);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': b.Append("\\\""); break;
                    case '\\': b.Append("\\\\"); break;
                    default:
                        if (c < 0x20 || c > 0x7E) b.Append("\\u").Append(((int)c).ToString("x4"));
                        else b.Append(c);
                        break;
                }
            }
            return b.ToString();
        }

        private static void TryDelete(string path)
        {
            try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); } catch (Exception) { }
        }

        private static string LastError()
        {
            string s = lastError;
            return string.IsNullOrEmpty(s) ? string.Empty : " (" + s + ")";
        }
    }
}
