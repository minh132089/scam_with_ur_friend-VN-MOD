// Speech patches:
//  Moss:         Kolkata.MossTtsNano worker -> if (SwyfCompat.VietnameseTts.TrySynthesize(...)) return; (VieNeu / Piper)
//  WhisperGpu:   com.whisper.unity -> GPU (Vulkan) on, flash attention off, models loaded natively from disk,
//                SwyfCompat.WhisperGpu.Prepare() before the context is created
//  SpeechTiming: ScriptsAssDef WhisperVcPipeline timing fields clamped through SwyfCompat.SpeechTuning
using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class MossFix
{
    public static int WhisperGpu(string input, string output, string managed, string compatPath)
    {
        using var r = CecilUtil.Resolver(managed);
        using var wasm = AssemblyDefinition.ReadAssembly(input, new ReaderParameters { AssemblyResolver = r });
        using var compat = AssemblyDefinition.ReadAssembly(compatPath, new ReaderParameters { AssemblyResolver = r });
        var manager = wasm.MainModule.Types.Single(t => t.Name == "WhisperManager");
        var create = manager.Methods.Single(m => m.Name == "CreateContextParams");
        var wil = create.Body.GetILProcessor();

        var load = create.Body.Instructions.Single(i => i.OpCode == OpCodes.Ldfld && i.Operand is FieldReference f && f.Name == "useGpu");
        if (load.Next.OpCode != OpCodes.Pop)
        {
            wil.InsertAfter(load, wil.Create(OpCodes.Ldc_I4_1));
            wil.InsertAfter(load, wil.Create(OpCodes.Pop));
        }
        Console.WriteLine("whisper gpu: on");
        // Flash attention on this whisper.cpp 1.7.5 Vulkan build is ~60x slower: force it off.
        var flash = create.Body.Instructions.Single(i => i.OpCode == OpCodes.Ldfld && i.Operand is FieldReference f2 && f2.Name == "flashAttention");
        if (flash.Next.OpCode != OpCodes.Pop)
        {
            wil.InsertAfter(flash, wil.Create(OpCodes.Ldc_I4_0));
            wil.InsertAfter(flash, wil.Create(OpCodes.Pop));
        }
        Console.WriteLine("whisper flash attention: off");

        var loaderType = compat.MainModule.GetType("SwyfCompat.WhisperFileLoader") ?? throw new InvalidOperationException("SwyfCompat.WhisperFileLoader missing");
        var loader = wasm.MainModule.ImportReference(loaderType.Methods.Single(m => m.Name == "InitFromFileAsync"));
        int redirected = 0, already = 0;
        foreach (var t in new[] { manager }.Concat(manager.NestedTypes))
        foreach (var m in t.Methods.Where(m => m.HasBody))
        foreach (var ins in m.Body.Instructions)
        {
            if (ins.OpCode != OpCodes.Call || !(ins.Operand is MethodReference mr)) continue;
            if (mr.Name == "InitFromFileAsync" && mr.DeclaringType.Name == "WhisperWrapper") { ins.Operand = loader; redirected++; }
            else if (mr.Name == "InitFromFileAsync" && mr.DeclaringType.Name == "WhisperFileLoader") already++;
        }
        if (redirected + already == 0) throw new InvalidOperationException("WhisperWrapper.InitFromFileAsync call not found");
        Console.WriteLine("whisper loader: " + redirected + " call(s) load natively from disk");

        var prepare = compat.MainModule.GetType("SwyfCompat.WhisperGpu").Methods.Single(m => m.Name == "Prepare");
        if (!create.Body.Instructions.Any(i => i.Operand is MethodReference mr && mr.Name == "Prepare" && mr.DeclaringType.Name == "WhisperGpu"))
            wil.InsertBefore(create.Body.Instructions[0], wil.Create(OpCodes.Call, wasm.MainModule.ImportReference(prepare)));
        Console.WriteLine("whisper prepare: present");
        wasm.Write(output);
        return 0;
    }

    public static int SpeechTiming(string input, string output, string managed, string compatPath)
    {
        using var r = CecilUtil.Resolver(managed);
        using var sasm = AssemblyDefinition.ReadAssembly(input, new ReaderParameters { AssemblyResolver = r });
        using var compat = AssemblyDefinition.ReadAssembly(compatPath, new ReaderParameters { AssemblyResolver = r });
        var tuning = compat.MainModule.GetType("SwyfCompat.SpeechTuning");
        var map = new Dictionary<string, MethodReference>
        {
            ["silenceToFinalizeSeconds"] = sasm.MainModule.ImportReference(tuning.Methods.Single(m => m.Name == "SilenceToFinalize")),
            ["partialIntervalSeconds"] = sasm.MainModule.ImportReference(tuning.Methods.Single(m => m.Name == "PartialInterval")),
        };
        var pipeline = sasm.MainModule.Types.Single(t => t.Name == "WhisperVcPipeline");
        int patched = 0, already = 0;
        foreach (var type in new[] { pipeline }.Concat(pipeline.NestedTypes))
        foreach (var method in type.Methods.Where(m => m.HasBody))
        {
            var til = method.Body.GetILProcessor();
            foreach (var ins in method.Body.Instructions.ToList())
            {
                if (ins.OpCode != OpCodes.Ldfld || !(ins.Operand is FieldReference f) || f.DeclaringType.Name != "WhisperVcPipeline" || !map.ContainsKey(f.Name)) continue;
                if (ins.Next != null && ins.Next.OpCode.Code == Code.Call && ins.Next.Operand is MethodReference mr && mr.DeclaringType.Name == "SpeechTuning") { already++; continue; }
                til.InsertAfter(ins, til.Create(OpCodes.Call, map[f.Name]));
                patched++;
            }
        }
        if (patched == 0 && already == 0) throw new InvalidOperationException("timing fields not found");
        sasm.Write(output);
        Console.WriteLine("speech timing: " + patched + " reads clamped");
        return 0;
    }

    public static int Moss(string input, string output, string managed, string compatPath)
    {
        using var resolver = CecilUtil.Resolver(managed);
        using var asm = AssemblyDefinition.ReadAssembly(input, new ReaderParameters { AssemblyResolver = resolver });
        using var compat = AssemblyDefinition.ReadAssembly(compatPath, new ReaderParameters { AssemblyResolver = resolver });
        var module = asm.MainModule;

        var singleton = module.Types.Single(t => t.FullName == "Kolkata.MossTtsNano.MossTtsSingleton");
        var closure = singleton.NestedTypes.Single(n => n.Methods.Any(m => m.Name == "<Prompt>b__0"));
        var worker = closure.Methods.Single(m => m.Name == "<Prompt>b__0" && m.Parameters.Count == 0);
        var callback = closure.Methods.Single(m => m.Name == "<Prompt>b__1" && m.Parameters.Count == 1);
        var fText = closure.Fields.Single(f => f.Name == "text");
        var fVoice = closure.Fields.Single(f => f.Name == "fullVoicePath");
        var fToken = closure.Fields.Single(f => f.Name == "token");
        var tryMethod = compat.MainModule.GetType("SwyfCompat.VietnameseTts").Methods.Single(m => m.Name == "TrySynthesize");

        if (worker.Body.Instructions.Any(i => i.Operand is MethodReference r && r.Name == "TrySynthesize"))
        {
            Console.WriteLine("moss: already patched");
            asm.Write(output);
            return 0;
        }
        // Reuse the Action<PocketTtsAudioResponse>(object, IntPtr) constructor reference the compiler emitted.
        var actionCtor = (MethodReference)worker.Body.Instructions
            .First(i => i.OpCode == OpCodes.Newobj && i.Operand is MethodReference r && r.DeclaringType.Name == "Action`1").Operand;

        var il = worker.Body.GetILProcessor();
        var first = worker.Body.Instructions[0];
        void Before(Instruction ins) => il.InsertBefore(first, ins);
        Before(il.Create(OpCodes.Ldarg_0));
        Before(il.Create(OpCodes.Ldfld, fText));
        Before(il.Create(OpCodes.Ldarg_0));
        Before(il.Create(OpCodes.Ldfld, fVoice));
        Before(il.Create(OpCodes.Ldarg_0));
        Before(il.Create(OpCodes.Ldftn, callback));
        Before(il.Create(OpCodes.Newobj, actionCtor));
        Before(il.Create(OpCodes.Ldarg_0));
        Before(il.Create(OpCodes.Ldfld, fToken));
        Before(il.Create(OpCodes.Call, module.ImportReference(tryMethod)));
        Before(il.Create(OpCodes.Brfalse, first));
        Before(il.Create(OpCodes.Ret));
        worker.Body.MaxStackSize = Math.Max(worker.Body.MaxStackSize, 8);

        asm.Write(output);
        Console.WriteLine("moss: Vietnamese lines routed to SwyfCompat.VietnameseTts");
        return 0;
    }
}
