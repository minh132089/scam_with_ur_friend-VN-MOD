// Vilao reply repair, JSON mode and Vietnamese call text (no Unity dependency except Debug).
using System;
using System.Globalization;
using UnityEngine;

namespace SwyfCompat
{
    // Vilao/DeepSeek often answers gameplay turns with plain text unless JSON mode is requested,
    // which makes the game send a second "repair" request. Ask for a JSON object up front.
    public static class VilaoBody
    {
        public const string DefaultEndpoint = "https://api.vilao.ai/v1/chat/completions";
        public const string DefaultModel = "chib/deepseek-v4.1-flash";
        private static readonly object ConfigGate = new object();
        private static DateTime configStamp = DateTime.MinValue;
        private static string configEndpoint = DefaultEndpoint;
        private static string configModel = DefaultModel;

        // <Game>_Data/StreamingAssets/swyf-vilao.json: {"endpoint": "...", "model": "..."}; re-read when the file changes.
        private static string ConfigPath
        {
            get
            {
                string managed = System.IO.Path.GetDirectoryName(typeof(VilaoBody).Assembly.Location) ?? ".";
                return System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.Combine(managed, ".."), System.IO.Path.Combine("StreamingAssets", "swyf-vilao.json")));
            }
        }

        private static void LoadConfig()
        {
            lock (ConfigGate)
            {
                try
                {
                    string path = ConfigPath;
                    if (!System.IO.File.Exists(path)) { configEndpoint = DefaultEndpoint; configModel = DefaultModel; configStamp = DateTime.MinValue; return; }
                    DateTime stamp = System.IO.File.GetLastWriteTimeUtc(path);
                    if (stamp == configStamp) return;
                    Newtonsoft.Json.Linq.JObject cfg = Newtonsoft.Json.Linq.JObject.Parse(System.IO.File.ReadAllText(path, System.Text.Encoding.UTF8));
                    string endpoint = ((string)cfg["endpoint"] ?? string.Empty).Trim();
                    string model = ((string)cfg["model"] ?? string.Empty).Trim();
                    configEndpoint = endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? endpoint : DefaultEndpoint;
                    configModel = model.Length > 0 && model.Length <= 256 && model.IndexOf(' ') < 0 ? model : DefaultModel;
                    configStamp = stamp;
                    Debug.Log("[SwyfCompat] AI config: model=" + configModel + " endpoint=" + configEndpoint);
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[SwyfCompat] swyf-vilao.json ignored (" + e.Message + "); using " + DefaultModel);
                    configEndpoint = DefaultEndpoint; configModel = DefaultModel;
                }
            }
        }

        public static string Endpoint() { LoadConfig(); return configEndpoint; }
        public static string Model() { LoadConfig(); return configModel; }

        public static Newtonsoft.Json.Linq.JObject EnsureJsonObjectFormat(Newtonsoft.Json.Linq.JObject body)
        {
            if (body == null) return null;
            Newtonsoft.Json.Linq.JToken existing = body["response_format"];
            if (existing == null || existing.Type == Newtonsoft.Json.Linq.JTokenType.Null)
            {
                Newtonsoft.Json.Linq.JObject format = new Newtonsoft.Json.Linq.JObject();
                format["type"] = "json_object";
                body["response_format"] = format;
            }
            return body;
        }

        private static readonly string[] Emotions = { "TRUSTING", "SUSPICIOUS", "ANGRY", "NEUTRAL" };

        // Repairs a caller reply locally so the game does not need a second "repair" request.
        // Handles: bare dialogue text, a quoted JSON string, JSON missing its leading {"dialogue":,
        // JSON wrapped in fences/extra text, and JSON missing trust_percent/emotion/objectives/comedy.
        // Missing trust/emotion are carried over from the caller's previous reply in the request history.
        public static void FixDialogueResponse(Newtonsoft.Json.Linq.JObject request, Newtonsoft.Json.Linq.JObject response)
        {
            try
            {
                Newtonsoft.Json.Linq.JArray choices = response == null ? null : response["choices"] as Newtonsoft.Json.Linq.JArray;
                Newtonsoft.Json.Linq.JObject message = (choices == null || choices.Count == 0) ? null : choices[0]["message"] as Newtonsoft.Json.Linq.JObject;
                Newtonsoft.Json.Linq.JToken contentToken = message == null ? null : message["content"];
                if (contentToken == null || contentToken.Type != Newtonsoft.Json.Linq.JTokenType.String) return;
                string original = (string)contentToken;
                string repaired = Repair(original, request);
                if (repaired == null || repaired == original) return;
                message["content"] = repaired;
                Debug.Log("[SwyfCompat] Repaired caller reply locally: " + Shorten(original) + " -> " + Shorten(repaired));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SwyfCompat] Local reply repair skipped: " + e.Message);
            }
        }

        private static string Repair(string content, Newtonsoft.Json.Linq.JObject request)
        {
            string text = StripFences(content.Trim());
            if (text.Length == 0) return null;

            Newtonsoft.Json.Linq.JObject obj = ParseObject(text);
            if (obj != null)
            {
                if (obj["dialogue"] == null) return null; // unknown shape: let the game's own repair handle it
                bool normalized = Normalize(obj, request);
                // Re-serialize when fields were fixed or when fences/whitespace had to be stripped.
                return (normalized || text != content) ? obj.ToString(Newtonsoft.Json.Formatting.None) : null;
            }
            if (text[0] == '"' && text.IndexOf("\"trust_percent\"", StringComparison.Ordinal) > 0)
            {
                obj = ParseObject("{\"dialogue\":" + text + (text.EndsWith("}", StringComparison.Ordinal) ? "" : "}"));
                if (obj != null) { Normalize(obj, request); return obj.ToString(Newtonsoft.Json.Formatting.None); }
            }
            int open = text.IndexOf('{');
            int close = text.LastIndexOf('}');
            if (open >= 0 && close > open)
            {
                obj = ParseObject(text.Substring(open, close - open + 1));
                if (obj != null && obj["dialogue"] != null) { Normalize(obj, request); return obj.ToString(Newtonsoft.Json.Formatting.None); }
                return null;
            }

            string dialogue = text;
            if (text.Length >= 2 && text[0] == '"' && text[text.Length - 1] == '"')
            {
                try { dialogue = Newtonsoft.Json.Linq.JToken.Parse(text).ToObject<string>(); }
                catch (Exception) { dialogue = text.Substring(1, text.Length - 2); }
            }
            if (string.IsNullOrWhiteSpace(dialogue) || dialogue.TrimStart().StartsWith("[", StringComparison.Ordinal)) return null;
            obj = new Newtonsoft.Json.Linq.JObject();
            obj["dialogue"] = dialogue.Trim();
            Normalize(obj, request);
            return obj.ToString(Newtonsoft.Json.Formatting.None);
        }

        // Ensures the four required fields plus comedy exist and are well-formed. Returns true if anything changed.
        private static bool Normalize(Newtonsoft.Json.Linq.JObject obj, Newtonsoft.Json.Linq.JObject request)
        {
            bool changed = false;
            int lastTrust; string lastEmotion;
            PreviousState(request, out lastTrust, out lastEmotion);

            Newtonsoft.Json.Linq.JToken trust = obj["trust_percent"];
            int trustValue;
            if (trust == null || !TryInt(trust, out trustValue)) { obj["trust_percent"] = lastTrust; changed = true; }
            else if (trust.Type != Newtonsoft.Json.Linq.JTokenType.Integer || trustValue < 0 || trustValue > 100)
            { obj["trust_percent"] = Math.Max(0, Math.Min(100, trustValue)); changed = true; }

            string emotion = obj["emotion"] != null && obj["emotion"].Type == Newtonsoft.Json.Linq.JTokenType.String ? ((string)obj["emotion"]).Trim().ToUpperInvariant() : null;
            if (emotion == null || Array.IndexOf(Emotions, emotion) < 0) { obj["emotion"] = lastEmotion; changed = true; }
            else if ((string)obj["emotion"] != emotion) { obj["emotion"] = emotion; changed = true; }

            if (!(obj["completed_objectives"] is Newtonsoft.Json.Linq.JArray)) { obj["completed_objectives"] = new Newtonsoft.Json.Linq.JArray(); changed = true; }

            Newtonsoft.Json.Linq.JToken comedy = obj["comedy"];
            if (comedy == null || (comedy.Type != Newtonsoft.Json.Linq.JTokenType.Float && comedy.Type != Newtonsoft.Json.Linq.JTokenType.Integer))
            {
                if (comedy != null) obj.Remove("comedy");
                obj["comedy"] = 0.0;
                changed = true;
            }
            return changed;
        }

        private static void PreviousState(Newtonsoft.Json.Linq.JObject request, out int trust, out string emotion)
        {
            trust = 50; emotion = "NEUTRAL";
            Newtonsoft.Json.Linq.JArray messages = request == null ? null : request["messages"] as Newtonsoft.Json.Linq.JArray;
            if (messages == null) return;
            for (int i = messages.Count - 1; i >= 0; i--)
            {
                Newtonsoft.Json.Linq.JObject m = messages[i] as Newtonsoft.Json.Linq.JObject;
                if (m == null || (string)m["role"] != "assistant" || m["content"] == null || m["content"].Type != Newtonsoft.Json.Linq.JTokenType.String) continue;
                Newtonsoft.Json.Linq.JObject prev = ParseObject(((string)m["content"]).Trim());
                if (prev == null) continue;
                int t;
                if (prev["trust_percent"] != null && TryInt(prev["trust_percent"], out t)) trust = Math.Max(0, Math.Min(100, t));
                string e = prev["emotion"] != null && prev["emotion"].Type == Newtonsoft.Json.Linq.JTokenType.String ? ((string)prev["emotion"]).Trim().ToUpperInvariant() : null;
                if (e != null && Array.IndexOf(Emotions, e) >= 0) emotion = e;
                return;
            }
        }

        private static bool TryInt(Newtonsoft.Json.Linq.JToken token, out int value)
        {
            value = 0;
            if (token.Type == Newtonsoft.Json.Linq.JTokenType.Integer) { value = (int)(long)token; return true; }
            double d;
            if (token.Type == Newtonsoft.Json.Linq.JTokenType.Float) { value = (int)Math.Round((double)token); return true; }
            if (token.Type == Newtonsoft.Json.Linq.JTokenType.String && double.TryParse(((string)token).Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out d))
            { value = (int)Math.Round(d); return true; }
            return false;
        }

        private static Newtonsoft.Json.Linq.JObject ParseObject(string text)
        {
            if (string.IsNullOrEmpty(text) || text[0] != '{') return null;
            try { return Newtonsoft.Json.Linq.JToken.Parse(text) as Newtonsoft.Json.Linq.JObject; }
            catch (Exception) { return null; }
        }

        private static string StripFences(string text)
        {
            if (!text.StartsWith("```", StringComparison.Ordinal)) return text;
            int firstNewline = text.IndexOf('\n');
            string inner = firstNewline >= 0 ? text.Substring(firstNewline + 1) : text.Substring(3);
            int end = inner.LastIndexOf("```", StringComparison.Ordinal);
            return (end >= 0 ? inner.Substring(0, end) : inner).Trim();
        }

        private static string Shorten(string s)
        {
            s = (s ?? string.Empty).Replace('\n', ' ');
            return s.Length <= 120 ? s : s.Substring(0, 120) + "...";
        }
    }

    // Vietnamese additions for phone calls.
    public static class VietnameseText
    {
        // Filler lines the caller says while the AI reply is pending. Returns null for other languages.
        public static string Filler(string languageCode, int variant)
        {
            if (string.IsNullOrEmpty(languageCode)) return null;
            string code = languageCode.Trim().ToLowerInvariant();
            if (code != "vi" && !code.StartsWith("vi-", StringComparison.Ordinal) && !code.StartsWith("vi_", StringComparison.Ordinal)
                && code != "vietnamese" && code != "tiếng việt") return null;
            switch (variant)
            {
                case 0: return "Ừm, được rồi.";
                case 1: return "Hừm, tôi hiểu.";
                default: return "À, vâng.";
            }
        }
    }

}
