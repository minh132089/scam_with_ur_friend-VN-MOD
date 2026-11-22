// Assembly-CSharp: calls to UnityEngine.PlayerPrefs.* and UnityWebRequest.Get(Uri) go to SwyfCompat.dll
// (both are stripped from this build's engine modules).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class Redirect
{
    public static int Run(string input, string output, string managed, string compatPath)
    {
        using var resolver = CecilUtil.Resolver(managed, Path.GetDirectoryName(compatPath));
        using var asm = AssemblyDefinition.ReadAssembly(input, new ReaderParameters { AssemblyResolver = resolver });
        using var compat = AssemblyDefinition.ReadAssembly(compatPath, new ReaderParameters { AssemblyResolver = resolver });
        var module = asm.MainModule;
        var prefsCompat = compat.MainModule.GetType("SwyfCompat.PlayerPrefsCompat");
        var webCompat = compat.MainModule.GetType("SwyfCompat.UnityWebRequestCompat");

        string Sig(MethodReference m) => m.Name + "(" + string.Join(",", m.Parameters.Select(p => p.ParameterType.FullName)) + ")";

        MethodDefinition FindReplacement(MethodReference target)
        {
            var owner = target.DeclaringType.FullName;
            TypeDefinition shim = null;
            if (owner == "UnityEngine.PlayerPrefs") shim = prefsCompat;
            else if (owner == "UnityEngine.Networking.UnityWebRequest" && target.Name == "Get"
                     && target.Parameters.Count == 1 && target.Parameters[0].ParameterType.FullName == "System.Uri") shim = webCompat;
            if (shim == null || target.HasThis) return null;
            var sig = Sig(target);
            return shim.Methods.SingleOrDefault(m => m.IsStatic && m.IsPublic && Sig(m) == sig);
        }

        var counts = new SortedDictionary<string, int>();
        var unhandled = new SortedSet<string>();
        foreach (var type in CecilUtil.AllTypes(module.Types))
        foreach (var method in type.Methods.Where(m => m.HasBody))
        foreach (var ins in method.Body.Instructions)
        {
            if ((ins.OpCode != OpCodes.Call && ins.OpCode != OpCodes.Callvirt) || !(ins.Operand is MethodReference target)) continue;
            var owner = target.DeclaringType.FullName;
            if (owner != "UnityEngine.PlayerPrefs" && !(owner == "UnityEngine.Networking.UnityWebRequest" && target.Name == "Get")) continue;
            var replacement = FindReplacement(target);
            if (replacement == null)
            {
                if (owner == "UnityEngine.PlayerPrefs") unhandled.Add(target.FullName);
                continue;
            }
            ins.OpCode = OpCodes.Call;
            ins.Operand = module.ImportReference(replacement);
            var key = owner + "::" + Sig(target);
            counts.TryGetValue(key, out int n);
            counts[key] = n + 1;
        }

        int total = counts.Values.Sum();
        Console.WriteLine("redirected " + total + " call(s) in " + counts.Count + " signature(s)");
        foreach (var u in unhandled) Console.WriteLine("UNHANDLED " + u);
        if (unhandled.Count > 0) return 1;
        if (total == 0) { Console.Error.WriteLine("no PlayerPrefs calls found"); return 1; }

        asm.Write(output);
        return 0;
    }
}
