// Assembly-CSharp fixes applied on top of the Vilao + compat patches:
// 1) DelayedEnableImmediateDisable gets the TargetCamera property the newer IImmediateCameraTargetDisabler requires.
// 2) CallerAiPipeline's per-turn AI budget 15 s -> 45 s (slow upstream models).
// 3) Gameplay dialogue requests get response_format json_object (SwyfCompat.VilaoBody).
// 4) KolkataOsCallerQueueApp's UQueryExtensions.Q calls go through SwyfCompat.UiCompat.
// 5) Malformed caller replies are repaired locally (SwyfCompat.VilaoBody.FixDialogueResponse).
// 6) Vietnamese filler lines (SwyfCompat.VietnameseText.Filler).
// 7) Endpoint and model are read from StreamingAssets/swyf-vilao.json.
using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class TypeFix
{
    private const double OldBudget = 15.0;
    private const double NewBudget = 45.0;

    public static int Run(string input, string output, string managed, string compatPath)
    {
        using var resolver = CecilUtil.Resolver(managed);
        using var asm = AssemblyDefinition.ReadAssembly(input, new ReaderParameters { AssemblyResolver = resolver });
        using var compat = AssemblyDefinition.ReadAssembly(compatPath, new ReaderParameters { AssemblyResolver = resolver });
        var module = asm.MainModule;

        AddTargetCamera(module, resolver);
        RaiseTurnBudget(module);
        AddJsonMode(module, compat.MainModule);
        GuardCallerQueueQueries(module, compat.MainModule);
        AddLocalReplyRepair(module, compat.MainModule);
        AddVietnameseFillers(module, compat.MainModule);
        UseConfigurableModel(module, compat.MainModule);

        asm.Write(output);
        return 0;
    }

    private static void AddLocalReplyRepair(ModuleDefinition module, ModuleDefinition compat)
    {
        var api = module.Types.Single(t => t.FullName == "KolkataApi");
        var networkedCall = module.Types.Single(t => t.FullName == "NetworkedCall");
        var record = networkedCall.Methods.Single(m => m.Name == "RecordGameplayResponse" && m.Parameters.Count == 3);
        var isDialogue = api.Methods.Single(m => m.Name == "IsGameplayDialogueRequest" && m.Parameters.Count == 1);
        var fix = module.ImportReference(compat.GetType("SwyfCompat.VilaoBody").Methods.Single(m => m.Name == "FixDialogueResponse"));

        var wrapper = api.Methods.SingleOrDefault(m => m.Name == "SwyfRecordGameplayResponse");
        if (wrapper == null)
        {
            wrapper = new MethodDefinition("SwyfRecordGameplayResponse",
                MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, module.TypeSystem.Void);
            foreach (var p in record.Parameters) wrapper.Parameters.Add(new ParameterDefinition(p.Name, ParameterAttributes.None, p.ParameterType));
            var il = wrapper.Body.GetILProcessor();
            var recordStart = il.Create(OpCodes.Ldarg_0);
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Call, isDialogue));
            il.Append(il.Create(OpCodes.Brfalse_S, recordStart));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldarg_1));
            il.Append(il.Create(OpCodes.Call, fix));
            il.Append(recordStart);
            il.Append(il.Create(OpCodes.Ldarg_1));
            il.Append(il.Create(OpCodes.Ldarg_2));
            il.Append(il.Create(OpCodes.Call, record));
            il.Append(il.Create(OpCodes.Ret));
            api.Methods.Add(wrapper);
            Console.WriteLine("reply repair: wrapper added");
        }

        int redirected = 0;
        foreach (var type in CecilUtil.AllNested(api))
        foreach (var method in type.Methods.Where(m => m.HasBody))
        foreach (var ins in method.Body.Instructions)
        {
            if (ins.OpCode == OpCodes.Call && ins.Operand is MethodReference r
                && r.DeclaringType.FullName == "NetworkedCall" && r.Name == "RecordGameplayResponse")
            {
                ins.Operand = wrapper;
                redirected++;
            }
        }
        if (redirected == 0 && !CecilUtil.AllNested(api).SelectMany(t => t.Methods).Where(m => m.HasBody)
                .Any(m => m.Body.Instructions.Any(i => i.Operand is MethodReference r && r.Name == "SwyfRecordGameplayResponse")))
            throw new InvalidOperationException("RecordGameplayResponse call site not found");
        Console.WriteLine("reply repair: " + redirected + " call site(s)");
    }

    private static void UseConfigurableModel(ModuleDefinition module, ModuleDefinition compat)
    {
        var body = compat.GetType("SwyfCompat.VilaoBody");
        var endpoint = module.ImportReference(body.Methods.Single(m => m.Name == "Endpoint" && m.Parameters.Count == 0));
        var model = module.ImportReference(body.Methods.Single(m => m.Name == "Model" && m.Parameters.Count == 0));
        var api = module.Types.Single(t => t.FullName == "KolkataApi");
        int e = 0, md = 0, already = 0;
        foreach (var type in new[] { api }.Concat(CecilUtil.AllNested(api)))
        foreach (var method in type.Methods.Where(m => m.HasBody))
        foreach (var ins in method.Body.Instructions)
        {
            if (ins.OpCode == OpCodes.Ldstr && (string)ins.Operand == "https://api.vilao.ai/v1/chat/completions") { ins.OpCode = OpCodes.Call; ins.Operand = endpoint; e++; }
            else if (ins.OpCode == OpCodes.Ldstr && (string)ins.Operand == "chib/deepseek-v4.1-flash") { ins.OpCode = OpCodes.Call; ins.Operand = model; md++; }
            else if (ins.OpCode == OpCodes.Call && ins.Operand is MethodReference r && r.DeclaringType.Name == "VilaoBody" && (r.Name == "Endpoint" || r.Name == "Model")) already++;
        }
        if (e + md == 0 && already == 0) throw new InvalidOperationException("vilao literals not found");
        Console.WriteLine("config: endpoint x" + e + ", model x" + md + " read from swyf-vilao.json");
    }

    private static void AddVietnameseFillers(ModuleDefinition module, ModuleDefinition compat)
    {
        var fillers = module.Types.Single(t => t.FullName == "CallerResponseFillers");
        var get = fillers.Methods.Single(m => m.Name == "Get" && m.Parameters.Count == 2);
        var vi = compat.GetType("SwyfCompat.VietnameseText").Methods.Single(m => m.Name == "Filler");
        if (get.Body.Instructions.Any(i => i.Operand is MethodReference r && r.Name == vi.Name && r.DeclaringType.Name == "VietnameseText"))
        { Console.WriteLine("vi fillers: already present"); return; }
        var il = get.Body.GetILProcessor();
        var first = get.Body.Instructions[0];
        var pop = il.Create(OpCodes.Pop);
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_1));
        il.InsertBefore(first, il.Create(OpCodes.Call, module.ImportReference(vi)));
        il.InsertBefore(first, il.Create(OpCodes.Dup));
        il.InsertBefore(first, il.Create(OpCodes.Brfalse_S, pop));
        il.InsertBefore(first, il.Create(OpCodes.Ret));
        il.InsertBefore(first, pop);
        get.Body.MaxStackSize = Math.Max(get.Body.MaxStackSize, 4);
        Console.WriteLine("vi fillers: added");
    }

    private static void AddJsonMode(ModuleDefinition module, ModuleDefinition compat)
    {
        var api = module.Types.Single(t => t.FullName == "KolkataApi");
        var prepare = api.Methods.Single(m => m.Name == "PrepareDirectOpenRouterBody");
        var isDialogue = api.Methods.Single(m => m.Name == "IsGameplayDialogueRequest" && m.Parameters.Count == 1);
        var ensure = compat.GetType("SwyfCompat.VilaoBody").Methods.Single(m => m.Name == "EnsureJsonObjectFormat");
        if (prepare.Body.Instructions.Any(i => i.Operand is MethodReference r && r.Name == ensure.Name))
        { Console.WriteLine("json mode: already present"); return; }

        var ensureRef = module.ImportReference(ensure);
        var il = prepare.Body.GetILProcessor();
        var rets = prepare.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList();
        if (rets.Count != 1) throw new InvalidOperationException("expected one ret in PrepareDirectOpenRouterBody, found " + rets.Count);
        var ret = rets[0];
        // stack at ret: [JObject]  ->  if (IsGameplayDialogueRequest(j)) j = EnsureJsonObjectFormat(j);
        il.InsertBefore(ret, il.Create(OpCodes.Dup));
        il.InsertBefore(ret, il.Create(OpCodes.Call, isDialogue));
        il.InsertBefore(ret, il.Create(OpCodes.Brfalse, ret));
        il.InsertBefore(ret, il.Create(OpCodes.Call, ensureRef));
        prepare.Body.MaxStackSize = Math.Max(prepare.Body.MaxStackSize, 8);
        Console.WriteLine("json mode: added");
    }

    private static void GuardCallerQueueQueries(ModuleDefinition module, ModuleDefinition compat)
    {
        var app = module.Types.Single(t => t.FullName == "Kolkata.UI.KolkataOsCallerQueueApp");
        var ui = compat.GetType("SwyfCompat.UiCompat");
        var genericQ = module.ImportReference(ui.Methods.Single(m => m.Name == "Q" && m.HasGenericParameters));
        var plainQ = module.ImportReference(ui.Methods.Single(m => m.Name == "Q" && !m.HasGenericParameters));
        const string Sig3 = "UnityEngine.UIElements.VisualElement,System.String,System.String";

        int redirected = 0, already = 0;
        var unhandled = new SortedSet<string>();
        foreach (var type in new[] { app }.Concat(CecilUtil.AllNested(app)))
        foreach (var method in type.Methods.Where(m => m.HasBody))
        foreach (var ins in method.Body.Instructions)
        {
            if (ins.OpCode != OpCodes.Call || !(ins.Operand is MethodReference target)) continue;
            var owner = target.DeclaringType.FullName;
            if (owner == "SwyfCompat.UiCompat") { already++; continue; }
            if (owner != "UnityEngine.UIElements.UQueryExtensions" || target.Name != "Q") continue;
            var sig = string.Join(",", target.Parameters.Select(p => p.ParameterType.FullName.Replace("!!0", "T")));
            var elementSig = target is GenericInstanceMethod gim
                ? string.Join(",", gim.ElementMethod.Parameters.Select(p => p.ParameterType.FullName))
                : sig;
            if (elementSig != Sig3) { unhandled.Add(target.FullName); continue; }
            if (target is GenericInstanceMethod g)
            {
                var inst = new GenericInstanceMethod(genericQ);
                inst.GenericArguments.Add(g.GenericArguments[0]);
                ins.Operand = inst;
            }
            else ins.Operand = plainQ;
            redirected++;
        }
        foreach (var u in unhandled) Console.WriteLine("caller queue: UNHANDLED " + u);
        if (unhandled.Count > 0) throw new InvalidOperationException("unhandled Q overloads");
        Console.WriteLine("caller queue: " + redirected + " Q calls guarded (" + already + " already)");
    }

    private static void AddTargetCamera(ModuleDefinition module, IAssemblyResolver resolver)
    {
        var type = module.Types.Single(t => t.FullName == "DelayedEnableImmediateDisable");
        if (type.Methods.Any(m => m.Name == "get_TargetCamera")) { Console.WriteLine("TargetCamera: already present"); return; }

        var targetField = type.Fields.Single(f => f.Name == "targetObject");
        var cinemachine = resolver.Resolve(AssemblyNameReference.Parse("Unity.Cinemachine"));
        var camType = module.ImportReference(cinemachine.MainModule.GetType("Unity.Cinemachine.CinemachineCamera"));
        var core = resolver.Resolve(AssemblyNameReference.Parse("UnityEngine.CoreModule"));
        var opEquality = module.ImportReference(core.MainModule.GetType("UnityEngine.Object").Methods
            .Single(m => m.Name == "op_Equality" && m.Parameters.Count == 2));
        var getComponentGeneric = core.MainModule.GetType("UnityEngine.GameObject").Methods
            .Single(m => m.Name == "GetComponent" && m.HasGenericParameters && m.Parameters.Count == 0);
        var getComponent = new GenericInstanceMethod(module.ImportReference(getComponentGeneric));
        getComponent.GenericArguments.Add(camType);

        var getter = new MethodDefinition("get_TargetCamera",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName |
            MethodAttributes.NewSlot | MethodAttributes.Virtual | MethodAttributes.Final, camType);
        var il = getter.Body.GetILProcessor();
        var notNull = il.Create(OpCodes.Ldarg_0);
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldfld, targetField));
        il.Append(il.Create(OpCodes.Ldnull));
        il.Append(il.Create(OpCodes.Call, opEquality));
        il.Append(il.Create(OpCodes.Brfalse_S, notNull));
        il.Append(il.Create(OpCodes.Ldnull));
        il.Append(il.Create(OpCodes.Ret));
        il.Append(notNull);
        il.Append(il.Create(OpCodes.Ldfld, targetField));
        il.Append(il.Create(OpCodes.Callvirt, getComponent));
        il.Append(il.Create(OpCodes.Ret));
        type.Methods.Add(getter);
        type.Properties.Add(new PropertyDefinition("TargetCamera", PropertyAttributes.None, camType) { GetMethod = getter });
        Console.WriteLine("TargetCamera: added");
    }

    private static void RaiseTurnBudget(ModuleDefinition module)
    {
        var pipeline = module.Types.Single(t => t.FullName == "CallerAiPipeline");
        var budgetConst = pipeline.Fields.SingleOrDefault(f => f.Name == "TurnBudgetSeconds" && f.HasConstant);
        if (budgetConst != null) budgetConst.Constant = NewBudget;

        int hits = 0, already = 0;
        foreach (var type in new[] { pipeline }.Concat(pipeline.NestedTypes))
        foreach (var method in type.Methods.Where(m => m.HasBody))
        foreach (var ins in method.Body.Instructions)
        {
            if (ins.OpCode != OpCodes.Ldc_R8 || ins.Next == null || ins.Next.OpCode != OpCodes.Call) continue;
            if (!(ins.Next.Operand is MethodReference callee) || callee.FullName != "System.TimeSpan System.TimeSpan::FromSeconds(System.Double)") continue;
            var value = (double)ins.Operand;
            if (value == OldBudget) { ins.Operand = NewBudget; hits++; }
            else if (value == NewBudget) already++;
        }
        if (hits == 0 && already == 0) throw new InvalidOperationException("turn budget literal not found");
        Console.WriteLine("budget: " + hits + " site(s) " + OldBudget + "s -> " + NewBudget + "s");
    }
}
