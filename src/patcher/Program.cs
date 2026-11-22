// SwyfPatcher: applies the Scam With Your Friends Vietnamese mod patches to a game's own DLLs.
// Built for .NET Framework 4.x (present on every Windows 10/11) so the friend needs nothing extra.
// Usage: SwyfPatcher all <Managed dir> <original suffix, e.g. .swyf-orig> <output dir> <SwyfCompat.dll>
//   Reads <Managed>\<name><suffix> (untouched originals), writes patched <output dir>\<name>.
//   The installer copies the outputs into Managed only after every step succeeded.
using System;
using System.Collections.Generic;
using System.IO;
using Mono.Cecil;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 5 && args[0] == "all") return All(args[1], args[2], args[3], args[4]);
            Console.Error.WriteLine("usage: SwyfPatcher all <managedDir> <originalSuffix> <outDir> <SwyfCompat.dll>");
            return 2;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("ERROR: " + e.Message);
            Console.Error.WriteLine(e.StackTrace);
            return 1;
        }
    }

    private static int All(string managed, string suffix, string outDir, string compat)
    {
        managed = Path.GetFullPath(managed);
        compat = Path.GetFullPath(compat);
        Directory.CreateDirectory(outDir);
        string Src(string name)
        {
            string p = Path.Combine(managed, name + suffix);
            if (!File.Exists(p)) throw new FileNotFoundException("Original file not found: " + p);
            return p;
        }
        string Out(string name) => Path.Combine(outDir, name);

        string tmp1 = Out("Assembly-CSharp.step1.tmp"), tmp2 = Out("Assembly-CSharp.step2.tmp");
        Step("Assembly-CSharp: AI endpoint (Vilao)", VilaoPatch.Run(Src("Assembly-CSharp.dll"), tmp1, managed));
        Step("Assembly-CSharp: PlayerPrefs / web compat", Redirect.Run(tmp1, tmp2, managed, compat));
        Step("Assembly-CSharp: game fixes + Vietnamese", TypeFix.Run(tmp2, Out("Assembly-CSharp.dll"), managed, compat));
        File.Delete(tmp1);
        File.Delete(tmp2);
        Step("Kolkata.Speech: Vietnamese profile", SpeechFix.Run(Src("Kolkata.Speech.dll"), Out("Kolkata.Speech.dll"), managed));
        Step("Kolkata.MossTtsNano: Vietnamese voices", MossFix.Moss(Src("Kolkata.MossTtsNano.dll"), Out("Kolkata.MossTtsNano.dll"), managed, compat));
        Step("com.whisper.unity: GPU + disk loader", MossFix.WhisperGpu(Src("com.whisper.unity.dll"), Out("com.whisper.unity.dll"), managed, compat));
        Step("ScriptsAssDef: faster speech timing", MossFix.SpeechTiming(Src("ScriptsAssDef.dll"), Out("ScriptsAssDef.dll"), managed, compat));
        Console.WriteLine("ALL PATCHES OK");
        return 0;
    }

    private static void Step(string name, int code)
    {
        if (code != 0) throw new InvalidOperationException(name + " failed (code " + code + ")");
        Console.WriteLine("[ok] " + name);
    }
}

internal static class CecilUtil
{
    // Search only the given folders: never the current directory or GAC copies of the game's assemblies.
    public static DefaultAssemblyResolver Resolver(params string[] dirs)
    {
        var r = new DefaultAssemblyResolver();
        r.RemoveSearchDirectory(".");
        r.RemoveSearchDirectory("bin");
        foreach (var d in dirs) r.AddSearchDirectory(d);
        return r;
    }

    public static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types)
    {
        foreach (var t in types)
        {
            yield return t;
            foreach (var n in AllTypes(t.NestedTypes)) yield return n;
        }
    }

    public static IEnumerable<TypeDefinition> AllNested(TypeDefinition t)
    {
        foreach (var n in t.NestedTypes) { yield return n; foreach (var nn in AllNested(n)) yield return nn; }
    }
}
