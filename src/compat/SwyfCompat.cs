// Replacements for Unity APIs that Assembly-CSharp calls but this build's Unity modules stripped
// (PlayerPrefs.SetInt/SetFloat/DeleteKey/Save, UnityWebRequest.Get(Uri)).
// Built only on the natives that still exist: SetString, GetString, GetInt, GetFloat, HasKey.
using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.Networking;

namespace SwyfCompat
{
    public static class PlayerPrefsCompat
    {
        private const string Prefix = "§SWYF:";
        private const string IntTag = Prefix + "i:";
        private const string FloatTag = Prefix + "f:";
        private const string Deleted = Prefix + "del";
        private const string Absent = Prefix + "absent";

        // Returns the stored string, or Absent when the key is missing, deleted, or natively typed (int/float).
        private static string Raw(string key)
        {
            if (!PlayerPrefs.HasKey(key)) return Absent;
            string s = PlayerPrefs.GetString(key, Absent);
            return s == Deleted ? null : s;
        }

        public static bool HasKey(string key)
        {
            return PlayerPrefs.HasKey(key) && PlayerPrefs.GetString(key, Absent) != Deleted;
        }

        public static void DeleteKey(string key)
        {
            if (PlayerPrefs.HasKey(key)) PlayerPrefs.SetString(key, Deleted);
        }

        public static void DeleteAll() { }

        // Unity writes PlayerPrefs to disk on application quit; the explicit flush was stripped.
        public static void Save() { }

        public static void SetString(string key, string value) { PlayerPrefs.SetString(key, value ?? string.Empty); }

        public static void SetInt(string key, int value)
        {
            PlayerPrefs.SetString(key, IntTag + value.ToString(CultureInfo.InvariantCulture));
        }

        public static void SetFloat(string key, float value)
        {
            PlayerPrefs.SetString(key, FloatTag + value.ToString("R", CultureInfo.InvariantCulture));
        }

        public static string GetString(string key) { return GetString(key, string.Empty); }

        public static string GetString(string key, string defaultValue)
        {
            string s = Raw(key);
            if (s == null || s == Absent || s.StartsWith(Prefix, StringComparison.Ordinal)) return defaultValue;
            return s;
        }

        public static int GetInt(string key) { return GetInt(key, 0); }

        public static int GetInt(string key, int defaultValue)
        {
            string s = Raw(key);
            if (s == null) return defaultValue;
            if (s == Absent) return PlayerPrefs.GetInt(key, defaultValue);
            int v;
            string body = s.StartsWith(IntTag, StringComparison.Ordinal) ? s.Substring(IntTag.Length) : s;
            return int.TryParse(body, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : defaultValue;
        }

        public static float GetFloat(string key) { return GetFloat(key, 0f); }

        public static float GetFloat(string key, float defaultValue)
        {
            string s = Raw(key);
            if (s == null) return defaultValue;
            if (s == Absent) return PlayerPrefs.GetFloat(key, defaultValue);
            float v;
            string body = s.StartsWith(FloatTag, StringComparison.Ordinal) ? s.Substring(FloatTag.Length)
                : s.StartsWith(IntTag, StringComparison.Ordinal) ? s.Substring(IntTag.Length) : s;
            return float.TryParse(body, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : defaultValue;
        }
    }

    public static class UnityWebRequestCompat
    {
        public static UnityWebRequest Get(Uri uri)
        {
            if (uri == null) throw new ArgumentNullException("uri");
            return UnityWebRequest.Get(uri.AbsoluteUri);
        }
    }

    // UXML from the newer build lacks some elements the older code queries (e.g. "workshop-page").
    // Return a detached placeholder instead of null so the rest of the window still builds.
    public static class UiCompat
    {
        private static readonly System.Collections.Generic.HashSet<string> Reported = new System.Collections.Generic.HashSet<string>();

        private static void Report(string kind, string name, string className)
        {
            string key = kind + "|" + name + "|" + className;
            lock (Reported)
            {
                if (!Reported.Add(key)) return;
            }
            Debug.LogWarning("[SwyfCompat] UI element missing, using placeholder: " + kind + " name=" + (name ?? "<null>") + " class=" + (className ?? "<null>"));
        }

        public static T Q<T>(UnityEngine.UIElements.VisualElement e, string name, string className) where T : UnityEngine.UIElements.VisualElement
        {
            T found = e == null ? null : UnityEngine.UIElements.UQueryExtensions.Q<T>(e, name, className);
            if (found != null) return found;
            Report(typeof(T).Name, name, className);
            try { return (T)Activator.CreateInstance(typeof(T)); }
            catch (Exception) { return null; }
        }

        public static UnityEngine.UIElements.VisualElement Q(UnityEngine.UIElements.VisualElement e, string name, string className)
        {
            UnityEngine.UIElements.VisualElement found = e == null ? null : UnityEngine.UIElements.UQueryExtensions.Q(e, name, className);
            if (found != null) return found;
            Report("VisualElement", name, className);
            return new UnityEngine.UIElements.VisualElement();
        }
    }
}
