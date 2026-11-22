// Kolkata.Speech: adds a Vietnamese ("vi") pronunciation profile to SentinelSpeechLanguage. Without it, adding
// "vi" to kolkata-speech-languages.json makes SentinelSpeechLanguage.Resolve throw for every language option.
using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class SpeechFix
{
    private const string Code = "vi";
    private const string Digits = "không|một|hai|ba|bốn|năm|sáu|bảy|tám|chín";
    private const string Letters = "a|bê|xê|đê|e|ép|giê|hát|i|gi|ca|e-lờ|em-mờ|en-nờ|o|pê|quy|e-rờ|ét|tê|u|vê|vê kép|ích|i dài|dét";
    private const string Symbols = "a còng|chấm|gạch ngang|gạch dưới|cộng|gạch chéo";               // @ . - _ + /
    private const string Questions = "họ thời con gái của mẹ|tên thú cưng đầu tiên|màu yêu thích";  // maiden name, first pet, favorite color
    private const string Colors = "đỏ|xanh dương|xanh lá|tím|vàng|cam|hồng|đen|trắng|cầu vồng|xanh mòng két|vàng kim|bạc|nâu|chàm";

    public static int Run(string input, string output, string managed)
    {
        Check("digits", Digits, 10); Check("letters", Letters, 26); Check("symbols", Symbols, 6);
        Check("questions", Questions, 3); Check("colors", Colors, 15);

        using var resolver = CecilUtil.Resolver(managed);
        using var asm = AssemblyDefinition.ReadAssembly(input, new ReaderParameters { AssemblyResolver = resolver });
        var type = asm.MainModule.Types.Single(t => t.FullName == "Kolkata.Speech.SentinelSpeechLanguage");
        var create = type.Methods.Single(m => m.Name == "CreateProfiles");
        if (create.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == Digits))
        {
            Console.WriteLine("vi profile: already present");
            asm.Write(output);
            return 0;
        }

        var ctor = type.Methods.Single(m => m.IsConstructor && !m.IsStatic && m.Parameters.Count == 6);
        // Reuse the Dictionary<string, SentinelSpeechLanguage>.Add reference the compiler already emitted.
        var add = type.Methods.Concat(type.NestedTypes.SelectMany(n => n.Methods)).Where(m => m.HasBody)
            .SelectMany(m => m.Body.Instructions)
            .Where(i => (i.OpCode == OpCodes.Callvirt || i.OpCode == OpCodes.Call) && i.Operand is MethodReference r
                        && r.Name == "Add" && r.DeclaringType.FullName.StartsWith("System.Collections.Generic.Dictionary`2"))
            .Select(i => (MethodReference)i.Operand).First();

        var rets = create.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList();
        if (rets.Count != 1) throw new InvalidOperationException("expected one ret, found " + rets.Count);
        var ret = rets[0];
        var il = create.Body.GetILProcessor();
        // stack at ret: [profiles]  ->  profiles.Add("vi", new SentinelSpeechLanguage("vi", ...));
        il.InsertBefore(ret, il.Create(OpCodes.Dup));
        il.InsertBefore(ret, il.Create(OpCodes.Ldstr, Code));
        il.InsertBefore(ret, il.Create(OpCodes.Ldstr, Code));
        il.InsertBefore(ret, il.Create(OpCodes.Ldstr, Digits));
        il.InsertBefore(ret, il.Create(OpCodes.Ldstr, Letters));
        il.InsertBefore(ret, il.Create(OpCodes.Ldstr, Symbols));
        il.InsertBefore(ret, il.Create(OpCodes.Ldstr, Questions));
        il.InsertBefore(ret, il.Create(OpCodes.Ldstr, Colors));
        il.InsertBefore(ret, il.Create(OpCodes.Newobj, ctor));
        il.InsertBefore(ret, il.Create(OpCodes.Callvirt, add));
        create.Body.MaxStackSize = Math.Max(create.Body.MaxStackSize, 10);
        asm.Write(output);
        Console.WriteLine("vi profile: added");
        return 0;
    }

    private static void Check(string name, string value, int expected)
    {
        int n = value.Split('|').Length;
        if (n != expected) throw new InvalidOperationException(name + ": " + n + " entries, expected " + expected);
    }
}
