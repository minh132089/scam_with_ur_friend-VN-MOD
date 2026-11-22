// Assembly-CSharp: the "OpenRouter (Bring Your Own Key)" route calls Vilao instead.
// 1) Endpoint literal (async state machine of CompleteOpenRouterAsync).
// 2) PrepareDirectOpenRouterBody: before each ret, set model and drop OpenRouter-only fields.
// (TypeFix later turns both literals into SwyfCompat.VilaoBody.Endpoint()/Model() calls.)
using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class VilaoPatch
{
    private const string OldEndpoint = "https://openrouter.ai/api/v1/chat/completions";
    private const string NewEndpoint = "https://api.vilao.ai/v1/chat/completions";
    private const string VilaoModel = "chib/deepseek-v4.1-flash";

    public static int Run(string input, string output, string managed)
    {
        using var resolver = CecilUtil.Resolver(managed);
        using var asm = AssemblyDefinition.ReadAssembly(input, new ReaderParameters { AssemblyResolver = resolver });
        var module = asm.MainModule;

        int endpointHits = 0;
        foreach (var type in CecilUtil.AllTypes(module.Types))
        foreach (var method in type.Methods.Where(m => m.HasBody))
        foreach (var ins in method.Body.Instructions)
        {
            if (ins.OpCode == OpCodes.Ldstr && (string)ins.Operand == OldEndpoint)
            {
                ins.Operand = NewEndpoint;
                endpointHits++;
                Console.WriteLine("endpoint patched in " + type.FullName + "::" + method.Name);
            }
        }
        if (endpointHits == 0) { Console.Error.WriteLine("endpoint literal not found"); return 1; }

        var api = module.Types.Single(t => t.Name == "KolkataApi");
        var prepare = api.Methods.Single(m => m.Name == "PrepareDirectOpenRouterBody");
        var newtonsoft = resolver.Resolve(AssemblyNameReference.Parse("Newtonsoft.Json"));
        var jObject = newtonsoft.MainModule.GetType("Newtonsoft.Json.Linq.JObject");
        var jToken = newtonsoft.MainModule.GetType("Newtonsoft.Json.Linq.JToken");
        var setItem = module.ImportReference(jObject.Methods.Single(m =>
            m.Name == "set_Item" && m.Parameters.Count == 2 && m.Parameters[0].ParameterType.FullName == "System.String"));
        var remove = module.ImportReference(jObject.Methods.Single(m =>
            m.Name == "Remove" && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.FullName == "System.String"));
        var fromString = module.ImportReference(jToken.Methods.Single(m =>
            m.Name == "op_Implicit" && m.Parameters.Count == 1 &&
            m.Parameters[0].ParameterType.FullName == "System.String" && m.ReturnType.FullName == jToken.FullName));

        var il = prepare.Body.GetILProcessor();
        var rets = prepare.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList();
        foreach (var ret in rets)
        {
            // Stack at ret: [JObject]. Keep it, mutate a dup'd reference.
            var injected = new List<Instruction>
            {
                il.Create(OpCodes.Dup),
                il.Create(OpCodes.Ldstr, "model"),
                il.Create(OpCodes.Ldstr, VilaoModel),
                il.Create(OpCodes.Call, fromString),
                il.Create(OpCodes.Callvirt, setItem),
            };
            foreach (var field in new[] { "provider", "session_id" })
            {
                injected.Add(il.Create(OpCodes.Dup));
                injected.Add(il.Create(OpCodes.Ldstr, field));
                injected.Add(il.Create(OpCodes.Callvirt, remove));
                injected.Add(il.Create(OpCodes.Pop));
            }
            // Retarget branches/handlers that jumped to ret so they run the injected code first.
            var first = injected[0];
            foreach (var ins in prepare.Body.Instructions)
            {
                if (ins.Operand == ret) ins.Operand = first;
                if (ins.Operand is Instruction[] targets)
                    for (int i = 0; i < targets.Length; i++) if (targets[i] == ret) targets[i] = first;
            }
            foreach (var h in prepare.Body.ExceptionHandlers)
            {
                if (h.HandlerEnd == ret) h.HandlerEnd = first;
                if (h.TryEnd == ret) h.TryEnd = first;
            }
            foreach (var ins in injected) il.InsertBefore(ret, ins);
        }
        prepare.Body.MaxStackSize += 4;
        Console.WriteLine("PrepareDirectOpenRouterBody patched at " + rets.Count + " return(s)");

        asm.Write(output);
        return 0;
    }
}
