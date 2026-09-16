using System.Diagnostics;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;

internal static class BudgetPreparationGameTests
{
    internal static int Run(Assembly game, Assembly plugin)
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("BudgetPreparation: " + message);
            checks++;
        }
        Type module = plugin.GetType("BetterPerformance.ObjectCreationBudget", true)!;
        Type batchType = module.GetNestedType("Batch", BindingFlags.NonPublic)!;
        Type budgetType = plugin.GetType("BetterPerformance.Core.CreationBudget", true)!;
        FieldInfo Field(string name) => AccessTools.Field(module, name);
        FieldInfo Member(string name) => AccessTools.Field(batchType, name);
        object? Call(string name, params object?[] arguments) => AccessTools.Method(module, name).Invoke(null, arguments);
        object Current() => Field("current").GetValue(null)!;
        object Value(string name) => Member(name).GetValue(Current())!;
        var enabled = module.GetProperty("Enabled", BindingFlags.NonPublic | BindingFlags.Static)!;
        string[] savedFields = { "current", "nearCandidateProbe", "budgetAfterNearPreparation", "milliseconds", "yields", "batches", "attempts",
            "preparationBatches", "preparationUnavailable", "rebasedBatches", "serviceOvershoots",
            "preparationSumMs", "preparationMaxMs", "serviceSumMs", "serviceMaxMs" };
        object?[] saved = savedFields.Select(name => Field(name).GetValue(null)).ToArray();
        object originalEnabled = enabled.GetValue(null)!;
        var originalOption = (ConfigEntry<bool>)Field("budgetAfterNearPreparation").GetValue(null)!;
        Check(!(bool)originalOption.DefaultValue, "option must default off");
        var config = new ConfigFile(Path.Combine(Path.GetTempPath(), "BetterPerformance-preparation-" + Guid.NewGuid().ToString("N") + ".cfg"), false)
        { SaveOnConfigSet = false };
        var option = config.Bind("ObjectLoading", "BudgetAfterNearPreparation", false);
        Field("budgetAfterNearPreparation").SetValue(null, option);
        Field("milliseconds").SetValue(null, config.Bind("ObjectLoading", "BudgetMilliseconds", 4f));
        long frequency = Stopwatch.Frequency, start = frequency * 10, allowance = frequency / 250;
        long preparation = frequency / 10;

        void NewBatch(bool defer)
        {
            object batch = Activator.CreateInstance(batchType)!;
            Member("Active").SetValue(batch, true);
            Member("Started").SetValue(batch, start);
            Member("AllowanceMs").SetValue(batch, 4d);
            Member("DeferNearPreparation").SetValue(batch, defer);
            Member("Budget").SetValue(batch, Activator.CreateInstance(budgetType, start, allowance));
            Field("current").SetValue(null, batch);
        }
        void Created(bool success)
        {
            object batch = Current(), budget = Member("Budget").GetValue(batch)!;
            budgetType.GetMethod("RecordCreation")!.Invoke(budget, new object[] { success });
            Member("Budget").SetValue(batch, budget);
            Field("current").SetValue(null, batch);
        }
        bool Continue(long now, bool hasNext = true) => (bool)Call("ContinueAt", hasNext, now)!;
        try
        {
            // Exercise the actual installed game's near IL, not a hand-made shape.
            Type scene = game.GetType("ZNetScene", true)!;
            MethodInfo near = scene.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .Single(method => method.Name == "CreateObjectsSorted");
            var original = PatchProcessor.GetOriginalInstructions(near).ToList();
            var patched = ((IEnumerable<CodeInstruction>)Call("Transpile", original, near)!).ToList();
            Check((bool)Field("nearCandidateProbe").GetValue(null)!, "native near enumerator is verified");
            Check(patched.Count(instruction => instruction.Calls(AccessTools.Method(module, "ContinueWithTelemetry"))) == 1,
                "one near creation gate after native preparation");

            enabled.SetValue(null, true);
            Field("current").SetValue(null, Activator.CreateInstance(batchType));
            option.Value = true;
            object?[] begin = { null };
            Call("Begin", begin);
            Check((bool)Value("DeferNearPreparation"), "Begin snapshots enabled option with verified gate");
            option.Value = false;
            Check((bool)Value("DeferNearPreparation"), "live config changes cannot alter current batch policy");
            Call("End", begin[0]);
            Field("nearCandidateProbe").SetValue(null, false);
            option.Value = true;
            begin[0] = null;
            Call("Begin", begin);
            Check(!(bool)Value("DeferNearPreparation"), "unverified near gate retains whole-batch clock");
            Call("End", begin[0]);

            NewBatch(true);
            Call("NearPreparationComplete", start + preparation);
            Check((long)Value("AllowanceOffsetTicks") == preparation, "expensive preparation offset");
            Check(Continue(start + preparation), "first successful creation floor still available");
            Created(false);
            Check(Continue(start + preparation + allowance * 10), "failed creation retains native progress floor");
            Created(true);
            Check(Continue(start + preparation + allowance - 1), "one tick before service boundary allowed");
            Check(!Continue(start + preparation + allowance), "exact shared service boundary yields");
            object counts = Value("Budget");
            Check((int)budgetType.GetProperty("Attempts")!.GetValue(counts)! == 2 &&
                (int)budgetType.GetProperty("Successes")!.GetValue(counts)! == 1, "rebase preserves attempts and success floor");
            Call("NearPreparationComplete", start + preparation * 2);
            Check((long)Value("AllowanceOffsetTicks") == preparation, "later near gates do not renew allowance");

            NewBatch(false);
            Call("NearPreparationComplete", start + preparation);
            Check(Continue(start + preparation), "baseline allows success floor");
            Created(true);
            Check(!Continue(start + preparation), "default behavior charges preparation");
            Check((long)Value("AllowanceOffsetTicks") == 0, "disabled option never offsets");

            foreach (bool priorHasNext in new[] { false, true })
            {
                NewBatch(true);
                Continue(start + 1, priorHasNext);
                Call("NearPreparationComplete", start + preparation);
                Check(!(bool)Value("AllowanceRebased"), "prior distant gate prevents retroactive refund, hasNext=" + priorHasNext);
            }
            foreach (bool priorSuccess in new[] { false, true })
            {
                NewBatch(true);
                Created(priorSuccess);
                Call("NearPreparationComplete", start + preparation);
                Check(!(bool)Value("AllowanceRebased"), "prior creation attempt prevents refund, success=" + priorSuccess);
            }

            NewBatch(true);
            Call("NearPreparationComplete", start + preparation);
            Check(!Continue(start + preparation, false), "empty near loop still stops");
            Created(true);
            Check(!Continue(start + preparation + allowance), "distant gate shares clock even after empty near loop");
            object?[] nested = { null };
            Call("Begin", nested);
            Check((long)Value("AllowanceOffsetTicks") == preparation, "nested Begin preserves parent allowance");
            Call("End", nested[0]);
            Check((long)Value("AllowanceOffsetTicks") == preparation && !Continue(start + preparation + allowance),
                "nested End cannot renew allowance");

            object rebasedBatch = Current();
            NewBatch(true);
            nested[0] = null;
            Call("Begin", nested);
            Check(!(bool)Value("DeferNearPreparation"), "nested entry before first gate prevents a future rebase");
            Call("NearPreparationComplete", start + preparation);
            Check(!(bool)Value("AllowanceRebased"), "nested near gate cannot refund unfinished outer preparation");
            Call("End", nested[0]);
            Check(!(bool)Value("DeferNearPreparation"), "nested exit cannot restore permission to rebase");
            Field("current").SetValue(null, rebasedBatch);

            Call("ResetPreparationTelemetry");
            Call("RecordPreparationTelemetry", Current(), start + preparation + allowance);
            Check((long)Field("preparationBatches").GetValue(null)! == 1, "preparation observation count");
            Check((long)Field("rebasedBatches").GetValue(null)! == 1, "rebased observation count");
            Check(Math.Abs((double)Field("preparationSumMs").GetValue(null)! - 100d) < 0.001, "preparation elapsed includes original start");
            Check(Math.Abs((double)Field("serviceSumMs").GetValue(null)! - 4d) < 0.001, "service elapsed excludes preparation");
            Check((long)Field("serviceOvershoots").GetValue(null)! == 0, "exact boundary is not a telemetry overshoot");
            Call("RecordPreparationTelemetry", Current(), start + preparation + allowance * 2);
            Check((long)Field("serviceOvershoots").GetValue(null)! == 1 &&
                Math.Abs((double)Field("serviceMaxMs").GetValue(null)! - 8d) < 0.001, "overshoot counted without truncating elapsed");
            NewBatch(true);
            Call("RecordPreparationTelemetry", Current(), start + preparation);
            Check((long)Field("preparationUnavailable").GetValue(null)! == 1, "missing near gate explicitly unavailable");
            Call("NearPreparationComplete", start - 1);
            Check(!(bool)Value("AllowanceRebased"), "clock regression cannot rebase");
            Call("RecordPreparationTelemetry", Current(), start + preparation);
            Check((long)Field("preparationUnavailable").GetValue(null)! == 2, "negative interval omitted");
            Call("ResetPreparationTelemetry");
            Check((long)Field("preparationBatches").GetValue(null)! == 0 &&
                (double)Field("preparationSumMs").GetValue(null)! == 0 &&
                (double)Field("serviceMaxMs").GetValue(null)! == 0, "capture reset clears bounded counters");
        }
        finally
        {
            for (int i = 0; i < savedFields.Length; i++) Field(savedFields[i]).SetValue(null, saved[i]);
            enabled.SetValue(null, originalEnabled);
        }
        Console.WriteLine("Budget preparation: " + checks + " offline clock/guard/native-layout checks; no startup throughput claim.");
        return checks;
    }
}
