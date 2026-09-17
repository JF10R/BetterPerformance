using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using Mono.Cecil;
using Cil = Mono.Cecil.Cil;

// Smelter reaches Unity types a standalone CLR may refuse, so every game-shape check here is
// read from Mono.Cecil metadata. The transpiler itself is exercised against real reflected IL
// when this runtime can read the body, and reported as unread when it cannot.
internal static class SmelterGameTests
{
    private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;

    internal static int Run(Assembly game, Assembly plugin)
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Smelter catch-up budget: " + message);
            checks++;
        }

        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(game.Location)!);
        var parameters = new ReaderParameters { AssemblyResolver = resolver };
        using var gameModule = ModuleDefinition.ReadModule(game.Location, parameters);
        using var pluginModule = ModuleDefinition.ReadModule(plugin.Location, parameters);
        TypeDefinition smelter = gameModule.GetType("Smelter")
            ?? throw new InvalidOperationException("Smelter catch-up budget: Smelter is missing.");
        TypeDefinition zdoVars = gameModule.GetType("ZDOVars")
            ?? throw new InvalidOperationException("Smelter catch-up budget: ZDOVars is missing.");

        // 1. The exact method the transpiler rewrites.
        MethodDefinition update = smelter.Methods.SingleOrDefault(m => m.Name == "UpdateSmelter" && m.Parameters.Count == 0)
            ?? throw new InvalidOperationException("Smelter catch-up budget: Smelter.UpdateSmelter() is missing.");
        Check(!update.IsStatic && update.IsPrivate && update.ReturnType.FullName == "System.Void" && update.HasBody,
            "UpdateSmelter() is a private instance void method with a managed body");
        Check(smelter.Methods.Count(m => m.Name == "UpdateSmelter") == 1, "UpdateSmelter has no overload to confuse the lookup");

        // 2. The ZDO variables that hold the whole per-iteration state, so a deferred second
        //    resumes from the ZDO rather than from plugin memory.
        foreach (string name in new[] { "s_accTime", "s_startTime", "s_fuel", "s_bakeTimer", "s_queued", "s_spawnOre", "s_spawnAmount" })
        {
            FieldDefinition field = zdoVars.Fields.SingleOrDefault(f => f.Name == name)
                ?? throw new InvalidOperationException("Smelter catch-up budget: ZDOVars." + name + " is missing.");
            Check(field.IsStatic && field.IsPublic && field.IsInitOnly && field.FieldType.FullName == "System.Int32",
                "ZDOVars." + name + " is a public static readonly int key");
        }

        // 3. The loop shape the transpiler relies on: one accumulator read, one clamp to 3600,
        //    one compare against 1 in the loop head, one decrement, one persist after the loop.
        var body = update.Body.Instructions;
        int Calls(string name) => body.Count(i => (i.Operand as MethodReference)?.Name == name);
        foreach (string name in new[] { "GetDeltaTime", "GetAccumulator", "SetAccumulator", "RemoveOneOre", "QueueProcessed", "SpawnProcessed" })
            Check(Calls(name) == 1, "UpdateSmelter calls " + name + " exactly once");
        Check(Calls("IsOwner") == 1, "UpdateSmelter keeps exactly one owner gate, which the module never touches");
        Check(body.Count(i => i.OpCode == Cil.OpCodes.Ldc_R4 && (float)i.Operand == 3600f) == 2,
            "the accumulator is clamped to 3600 by exactly one compare and one assignment");
        var compares = body.Where(i => i.OpCode == Cil.OpCodes.Ldc_R4 && (float)i.Operand == 1f &&
            i.Previous != null && IsLoad(i.Previous) && i.Next != null && IsConditional(i.Next)).ToArray();
        Check(compares.Length == 1, "exactly one compare against 1 sits in the loop head");
        var steps = body.Where(i => i.OpCode == Cil.OpCodes.Ldc_R4 && (float)i.Operand == 1f &&
            i.Previous != null && IsLoad(i.Previous) && i.Next != null && i.Next.OpCode == Cil.OpCodes.Sub).ToArray();
        Check(steps.Length == 1 && steps[0] != compares[0], "exactly one decrement by one sits inside the loop, distinct from the compare");
        Check(compares[0].Previous.Operand == steps[0].Previous.Operand ||
            LocalOf(compares[0].Previous) == LocalOf(steps[0].Previous),
            "the compare and the decrement read the same accumulator local");

        // 4. The module's own shape: the injected hook is a single parameterless static float.
        TypeDefinition module = pluginModule.GetType("BetterPerformance.SmelterCatchupBudget")!;
        MethodDefinition threshold = module.Methods.Single(m => m.Name == "Threshold");
        Check(threshold.IsStatic && threshold.Parameters.Count == 0 && threshold.ReturnType.FullName == "System.Single",
            "Threshold is a static parameterless float, so it can replace the loop constant in place");
        MethodDefinition after = module.Methods.Single(m => m.Name == "AfterUpdate");
        Check(after.IsStatic && after.ReturnType.FullName == "System.Void" &&
            after.Parameters.All(p => !p.ParameterType.IsByReference),
            "AfterUpdate is a static void postfix that cannot replace a result");

        // 5. The transpiler against real IL: one instruction changes, nothing moves.
        Type reflected = plugin.GetType("BetterPerformance.SmelterCatchupBudget", true)!;
        MethodInfo transpile = reflected.GetMethod("Transpile", PrivateStatic)!;
        MethodInfo target = game.GetType("Smelter", true)!.GetMethod("UpdateSmelter",
            BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null)!;
        List<CodeInstruction>? original = null;
        try { original = PatchProcessor.GetOriginalInstructions(target); }
        catch (TypeLoadException) { Console.WriteLine("STATIC ONLY Smelter catch-up budget: offline CLR cannot read UpdateSmelter's body; metadata checks stand alone."); }
        if (original != null)
        {
            List<CodeInstruction> Apply(List<CodeInstruction> input) =>
                ((IEnumerable<CodeInstruction>)transpile.Invoke(null, new object[] { input, target })!).ToList();
            var snapshot = original.Select(i => (i.opcode, i.operand, labels: i.labels.ToArray(), blocks: i.blocks.ToArray())).ToArray();
            List<CodeInstruction> patched = Apply(original);
            Check(patched.Count == original.Count, "the transpiler adds and removes no instruction");
            int changed = Enumerable.Range(0, patched.Count)
                .Count(i => patched[i].opcode != snapshot[i].opcode || !Equals(patched[i].operand, snapshot[i].operand));
            Check(changed == 1, "exactly one instruction differs from the original");
            int head = Enumerable.Range(0, patched.Count).Single(i => patched[i].opcode != snapshot[i].opcode);
            Check(snapshot[head].opcode == OpCodes.Ldc_R4 && Equals(snapshot[head].operand, 1f),
                "the replaced instruction is the loop-head constant");
            Check(patched[head].opcode == OpCodes.Call && patched[head].operand is MethodInfo hook &&
                hook.DeclaringType == reflected && hook.Name == "Threshold",
                "the loop-head constant is replaced by the module's own gate");
            Check(patched[head].labels.SequenceEqual(snapshot[head].labels) && patched[head].blocks.SequenceEqual(snapshot[head].blocks),
                "the replaced site keeps its loop-test label and exception blocks");
            for (int i = 0; i < patched.Count; i++)
                Check(i == head || (patched[i].opcode == snapshot[i].opcode && Equals(patched[i].operand, snapshot[i].operand) &&
                    patched[i].labels.SequenceEqual(snapshot[i].labels) && patched[i].blocks.SequenceEqual(snapshot[i].blocks)),
                    "instruction, label and exception block preserved at " + i);
            Check(Enumerable.Range(0, patched.Count).All(i => i == head || ReferenceEquals(patched[i], original[i])),
                "every other instruction is the original object, in the original order");

            // 6. A mutated loop head is refused and vanilla is kept.
            var mutated = PatchProcessor.GetOriginalInstructions(target);
            mutated[head] = new CodeInstruction(OpCodes.Ldc_R4, 2f);
            bool rejected = false;
            try { Apply(mutated); }
            catch (TargetInvocationException exception) when (exception.InnerException is InvalidOperationException) { rejected = true; }
            Check(rejected, "a changed compare constant is refused instead of patched");
            var noClamp = PatchProcessor.GetOriginalInstructions(target);
            int clamp = noClamp.FindIndex(i => i.opcode == OpCodes.Ldc_R4 && Equals(i.operand, 3600f));
            noClamp[clamp] = new CodeInstruction(OpCodes.Ldc_R4, 7200f);
            rejected = false;
            try { Apply(noClamp); }
            catch (TargetInvocationException exception) when (exception.InnerException is InvalidOperationException) { rejected = true; }
            Check(rejected, "a changed accumulator clamp is refused instead of patched");
        }

        // 7. Default configuration installs no transpiler and keeps the researched budget.
        var config = new ConfigFile(Path.Combine(Path.GetTempPath(), "bp-smelter-" + Guid.NewGuid().ToString("N") + ".cfg"), false)
        { SaveOnConfigSet = false };
        var log = new BepInEx.Logging.ManualLogSource("SmelterVerification");
        log.LogEvent += (_, entry) => Console.WriteLine(entry.Data);
        reflected.GetMethod("Install", PrivateStatic)!.Invoke(null, new object[] { config, log });
        var option = (ConfigEntry<bool>)config[new ConfigDefinition("Stations", "SmelterCatchupBudgetEnabled")];
        Check(!option.Value && !(bool)option.DefaultValue, "SmelterCatchupBudgetEnabled defaults to false");
        var count = (ConfigEntry<int>)config[new ConfigDefinition("Stations", "SmelterCatchupIterationsPerCall")];
        Check((int)count.DefaultValue == 8, "SmelterCatchupIterationsPerCall defaults to eight simulated seconds");
        var range = (AcceptableValueRange<int>)count.Description.AcceptableValues;
        Check(range.MinValue == 2 && range.MaxValue == 3600,
            "the budget cannot be set to one or below, so the drain always outpaces the one second per second that accrues");
        Check(!(bool)reflected.GetProperty("Installed", PrivateStatic)!.GetValue(null)!, "default configuration installs no transpiler");
        Check(!(bool)reflected.GetProperty("Enabled", PrivateStatic)!.GetValue(null)!, "default configuration stays disabled");
        Check((string)reflected.GetProperty("Status", PrivateStatic)!.GetValue(null)! == "disabled", "default status reports disabled");
        Check((int)reflected.GetProperty("Iterations", PrivateStatic)!.GetValue(null)! == 8, "the exported budget matches the configured default");
        Check(Harmony.GetAllPatchedMethods().SelectMany(m => Harmony.GetPatchInfo(m)!.Owners)
                .All(owner => !owner.EndsWith("SmelterCatchupBudget")),
            "default configuration installs no budget transpiler on any method");

        // 8. Counters and labels exist whether or not the budget installed.
        Type number = plugin.GetType("BetterPerformance.Core.NumberValue", true)!, text = plugin.GetType("BetterPerformance.Core.TextValue", true)!;
        var gauges = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(number))!;
        var labels = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(text))!;
        reflected.GetMethod("Sample", PrivateStatic)!.Invoke(null, new object[] { gauges, labels });
        var exported = gauges.Cast<object>().Select(g => (string)number.GetProperty("Name")!.GetValue(g)!).ToList();
        foreach (string name in new[] { "smelter_loop_iterations_sum", "smelter_loop_iterations_max", "smelter_accumulator_carried_max",
            "smelter_budget_truncations", "smelter_spawn_calls", "smelter_remove_ore_calls", "smelter_probe_failures" })
            Check(exported.Contains(name), name + " is exported");
        var labelNames = labels.Cast<object>().Select(l => (string)text.GetProperty("Name")!.GetValue(l)!).ToList();
        foreach (string name in new[] { "smelter_budget_status", "smelter_budget_enabled", "smelter_budget_iterations" })
            Check(labelNames.Contains(name), name + " is exported");
        var status = labels.Cast<object>().First(l => (string)text.GetProperty("Name")!.GetValue(l)! == "smelter_budget_status");
        Check(new[] { "disabled", "installed", "unavailable", "unavailable_unexpected_shape", "type_unavailable" }
                .Contains((string)text.GetProperty("Value")!.GetValue(status)!),
            "smelter_budget_status reports one of the defined states");

        reflected.GetMethod("Uninstall", PrivateStatic)!.Invoke(null, null);
        Console.WriteLine("Smelter catch-up budget: " + checks + " checks; the throughput claim needs an A/B on one world with the module off and on.");
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

    private static int LocalOf(Cil.Instruction instruction)
    {
        if (instruction.Operand is Cil.VariableReference variable) return variable.Index;
        return instruction.OpCode.Code switch
        {
            Cil.Code.Ldloc_0 => 0,
            Cil.Code.Ldloc_1 => 1,
            Cil.Code.Ldloc_2 => 2,
            Cil.Code.Ldloc_3 => 3,
            _ => -1
        };
    }
}
