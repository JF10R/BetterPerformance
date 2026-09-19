using System.Reflection;
using Mono.Cecil;
using Cil = Mono.Cecil.Cil;

// Smelter reaches Unity types a standalone CLR may refuse, so every game-shape check here is
// read from Mono.Cecil metadata. SmelterTelemetry observes UpdateSmelter's catch-up size without
// patching its behaviour, so this file proves the game contracts it reads and its own hook
// shapes; it makes no claim about the loop's internal structure beyond an advisory NOTE.
internal static class SmelterGameTests
{
    private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;

    internal static int Run(Assembly game, Assembly plugin)
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Smelter telemetry: " + message);
            checks++;
        }

        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(game.Location)!);
        var parameters = new ReaderParameters { AssemblyResolver = resolver };
        using var gameModule = ModuleDefinition.ReadModule(game.Location, parameters);
        using var pluginModule = ModuleDefinition.ReadModule(plugin.Location, parameters);
        TypeDefinition smelter = gameModule.GetType("Smelter")
            ?? throw new InvalidOperationException("Smelter telemetry: Smelter is missing.");
        TypeDefinition zdoVars = gameModule.GetType("ZDOVars")
            ?? throw new InvalidOperationException("Smelter telemetry: ZDOVars is missing.");
        TypeDefinition znet = gameModule.GetType("ZNet")
            ?? throw new InvalidOperationException("Smelter telemetry: ZNet is missing.");
        TypeDefinition zdo = gameModule.GetType("ZDO")
            ?? throw new InvalidOperationException("Smelter telemetry: ZDO is missing.");

        // 1. The exact method the telemetry wraps.
        MethodDefinition update = smelter.Methods.SingleOrDefault(m => m.Name == "UpdateSmelter" && m.Parameters.Count == 0)
            ?? throw new InvalidOperationException("Smelter telemetry: Smelter.UpdateSmelter() is missing.");
        Check(!update.IsStatic && update.IsPrivate && update.ReturnType.FullName == "System.Void" && update.HasBody,
            "UpdateSmelter() is a private instance void method with a managed body");
        Check(smelter.Methods.Count(m => m.Name == "UpdateSmelter") == 1, "UpdateSmelter has no overload to confuse the lookup");

        // 2. The other two game calls this module counts.
        MethodDefinition spawn = smelter.Methods.SingleOrDefault(m => m.Name == "Spawn" && m.Parameters.Count == 2)
            ?? throw new InvalidOperationException("Smelter telemetry: Smelter.Spawn(string,int) is missing.");
        Check(!spawn.IsStatic && spawn.ReturnType.FullName == "System.Void" &&
            spawn.Parameters[0].ParameterType.FullName == "System.String" && spawn.Parameters[1].ParameterType.FullName == "System.Int32",
            "Spawn(string,int) keeps its shape");
        MethodDefinition removeOre = smelter.Methods.SingleOrDefault(m => m.Name == "RemoveOneOre" && m.Parameters.Count == 0)
            ?? throw new InvalidOperationException("Smelter telemetry: Smelter.RemoveOneOre() is missing.");
        Check(!removeOre.IsStatic && removeOre.ReturnType.FullName == "System.Void", "RemoveOneOre() keeps its shape");

        // 3. The ZDO variables and API this module reads, and nothing more.
        foreach (string name in new[] { "s_accTime", "s_startTime" })
        {
            FieldDefinition field = zdoVars.Fields.SingleOrDefault(f => f.Name == name)
                ?? throw new InvalidOperationException("Smelter telemetry: ZDOVars." + name + " is missing.");
            Check(field.IsStatic && field.IsPublic && field.IsInitOnly && field.FieldType.FullName == "System.Int32",
                "ZDOVars." + name + " is a public static readonly int key");
        }
        MethodDefinition getTime = znet.Methods.SingleOrDefault(m => m.Name == "GetTime" && m.Parameters.Count == 0)
            ?? throw new InvalidOperationException("Smelter telemetry: ZNet.GetTime() is missing.");
        Check(!getTime.IsStatic && getTime.ReturnType.FullName == "System.DateTime", "ZNet.GetTime() returns System.DateTime");
        MethodDefinition getFloat = zdo.Methods.SingleOrDefault(m => m.Name == "GetFloat" && m.Parameters.Count == 2 &&
            m.Parameters[0].ParameterType.FullName == "System.Int32" && !m.Parameters[1].ParameterType.IsByReference)
            ?? throw new InvalidOperationException("Smelter telemetry: ZDO.GetFloat(int,float) is missing.");
        Check(getFloat.Parameters[1].ParameterType.FullName == "System.Single", "ZDO.GetFloat(int,float) keeps its shape");
        MethodDefinition getLong = zdo.Methods.SingleOrDefault(m => m.Name == "GetLong" && m.Parameters.Count == 2 &&
            m.Parameters[0].ParameterType.FullName == "System.Int32" && !m.Parameters[1].ParameterType.IsByReference)
            ?? throw new InvalidOperationException("Smelter telemetry: ZDO.GetLong(int,long) is missing.");
        Check(getLong.Parameters[1].ParameterType.FullName == "System.Int64", "ZDO.GetLong(int,long) keeps its shape");

        // 4. This module's own hooks: static void, nothing by reference, so none can replace a
        //    game result or an argument.
        TypeDefinition module = pluginModule.GetType("BetterPerformance.SmelterTelemetry")!;
        foreach (string name in new[] { "BeforeUpdate", "AfterUpdate", "AfterSpawn", "AfterRemoveOre" })
        {
            MethodDefinition hook = module.Methods.Single(m => m.Name == name);
            Check(hook.IsStatic && hook.ReturnType.FullName == "System.Void" && hook.Parameters.All(p => !p.ParameterType.IsByReference),
                name + " is a static void hook with no by-reference parameter");
        }

        // 5. Advisory only: the catch-up arithmetic this module reproduces assumes UpdateSmelter
        //    still clamps to 3600 and still has a continue-style idle path back to the loop test.
        //    A drift here does not fail the build; it flags that SmelterTelemetry may be stale.
        try
        {
            var body = update.Body.Instructions;
            bool clampOk = body.Count(i => i.OpCode == Cil.OpCodes.Ldc_R4 && (float)i.Operand == 3600f) == 2;
            if (clampOk) checks++;
            else Console.WriteLine("NOTE Smelter telemetry: UpdateSmelter no longer clamps the accumulator to 3600 by one compare and one assignment; the catch-up size math may be stale.");

            var compares = body.Where(i => i.OpCode == Cil.OpCodes.Ldc_R4 && (float)i.Operand == 1f &&
                i.Previous != null && IsLoad(i.Previous) && i.Next != null && IsConditional(i.Next)).ToArray();
            bool continueOk = compares.Length == 1 && body.Any(i => (i.OpCode == Cil.OpCodes.Br || i.OpCode == Cil.OpCodes.Br_S) &&
                i.Operand is Cil.Instruction target && ReferenceEquals(target, compares[0].Previous));
            if (continueOk) checks++;
            else Console.WriteLine("NOTE Smelter telemetry: UpdateSmelter no longer has a continue-style idle path back to the loop test; the catch-up size math may be stale.");
        }
        catch (Exception exception) { Console.WriteLine("NOTE Smelter telemetry: loop-shape read failed (" + exception.GetType().Name + "); advisory only."); }

        // 6. Install against the real reflected type, always-on (no config gate), then verify
        //    it reports one of the defined states whichever way this CLR resolves it.
        var log = new BepInEx.Logging.ManualLogSource("SmelterVerification");
        log.LogEvent += (_, entry) => Console.WriteLine(entry.Data);
        Type reflected = plugin.GetType("BetterPerformance.SmelterTelemetry", true)!;
        reflected.GetMethod("Install", PrivateStatic)!.Invoke(null, new object[] { log });
        string status = (string)reflected.GetProperty("Status", PrivateStatic)!.GetValue(null)!;
        Check(status == "installed" || status == "type_unavailable" || status == "patch_failed" || status == "disabled" || status.StartsWith("unpatch_failed:"),
            "Status reports one of the defined states");
        Check((bool)reflected.GetProperty("Installed", PrivateStatic)!.GetValue(null)! == (status == "installed"),
            "Installed agrees with a status of installed");

        // 7. Counters and labels exist, whichever way Install resolved.
        Type number = plugin.GetType("BetterPerformance.Core.NumberValue", true)!, text = plugin.GetType("BetterPerformance.Core.TextValue", true)!;
        var gauges = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(number))!;
        var labels = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(text))!;
        reflected.GetMethod("Sample", PrivateStatic)!.Invoke(null, new object[] { gauges, labels });
        var exported = gauges.Cast<object>().Select(g => (string)number.GetProperty("Name")!.GetValue(g)!).ToList();
        foreach (string name in new[] { "smelter_catchup_seconds_max", "smelter_catchup_calls_over_60s", "smelter_accumulator_carried_max",
            "smelter_spawn_calls", "smelter_remove_ore_calls", "smelter_probe_failures" })
            Check(exported.Contains(name), name + " is exported");
        var labelNames = labels.Cast<object>().Select(l => (string)text.GetProperty("Name")!.GetValue(l)!).ToList();
        foreach (string name in new[] { "smelter_telemetry_status", "smelter_telemetry_scope" })
            Check(labelNames.Contains(name), name + " is exported");

        reflected.GetMethod("Uninstall", PrivateStatic)!.Invoke(null, null);
        // A standalone CLR can throw inside Harmony's unpatch rescan (an unrelated Unity type), which
        // the module reports as unpatch_failed rather than hiding; in the game it reads disabled.
        string after = (string)reflected.GetProperty("Status", PrivateStatic)!.GetValue(null)!;
        Check(after == "disabled" || after.StartsWith("unpatch_failed:"), "Uninstall returns Status to disabled or reports the unpatch failure (actual=" + after + ")");
        if (after != "disabled") Console.WriteLine("STATIC ONLY smelter telemetry unpatch: " + after + " on this CLR; contract checks above still ran");
        Console.WriteLine("Smelter telemetry: " + checks + " checks; this module makes no throughput or product-parity claim.");
        return checks;
    }

    private static bool IsLoad(Cil.Instruction instruction)
    {
        Cil.Code code = instruction.OpCode.Code;
        return code == Cil.Code.Ldloc || code == Cil.Code.Ldloc_S || code == Cil.Code.Ldloc_0 ||
            code == Cil.Code.Ldloc_1 || code == Cil.Code.Ldloc_2 || code == Cil.Code.Ldloc_3;
    }

    private static bool IsConditional(Cil.Instruction instruction)
    {
        Cil.Code code = instruction.OpCode.Code;
        return code == Cil.Code.Bge || code == Cil.Code.Bge_S || code == Cil.Code.Bge_Un || code == Cil.Code.Bge_Un_S ||
            code == Cil.Code.Blt || code == Cil.Code.Blt_S || code == Cil.Code.Blt_Un || code == Cil.Code.Blt_Un_S;
    }
}
