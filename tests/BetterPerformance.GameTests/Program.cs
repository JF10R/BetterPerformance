using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using BepInEx.Configuration;

if (args.Length != 2) throw new ArgumentException("Usage: verifier <game directory> <plugin DLL>");
string gameDirectory = Path.GetFullPath(args[0]);
string managedDirectory = Directory.Exists(Path.Combine(gameDirectory, "valheim_Data"))
    ? Path.Combine(gameDirectory, "valheim_Data", "Managed")
    : Path.Combine(gameDirectory, "valheim_server_Data", "Managed");
string[] roots = { managedDirectory, Path.Combine(gameDirectory, "BepInEx", "core") };
AppDomain.CurrentDomain.AssemblyResolve += (_, requested) => {
    var name = new AssemblyName(requested.Name);
    foreach (string root in roots) {
        string candidate = Path.Combine(root, name.Name + ".dll");
        if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
    }
    return null;
};
Assembly game = Assembly.LoadFrom(Path.Combine(managedDirectory, "assembly_valheim.dll"));
Assembly plugin = Assembly.LoadFrom(Path.GetFullPath(args[1]));
Type scene = game.GetType("ZNetScene", true)!;
Type patchType = plugin.GetType("BetterPerformance.ObjectCreationBudget", true)!;
MethodInfo transpile = patchType.GetMethod("Transpile", BindingFlags.Static | BindingFlags.NonPublic)!;
int checks = 0;
void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); checks++; }
List<CodeInstruction> Apply(List<CodeInstruction> original, MethodInfo method) =>
    ((IEnumerable<CodeInstruction>)transpile.Invoke(null, new object[] { original, method })!).ToList();
foreach (string methodName in new[] { "CreateObjectsSorted", "CreateDistantObjects" }) {
    MethodInfo method = scene.GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!;
    Console.WriteLine(method + "; body bytes=" + method.GetMethodBody()?.GetILAsByteArray()?.Length);
    List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(method);
    var snapshot = original.Select(i => (i.opcode, i.operand, labels: i.labels.ToArray(), blocks: i.blocks.ToArray())).ToArray();
    List<CodeInstruction> patched = Apply(original, method);
    bool near = methodName == "CreateObjectsSorted";
    var injected = patched.Where(i => i.operand is MethodInfo called && called.DeclaringType == patchType).ToList();
    Check(injected.Count == (near ? 3 : 2), methodName + ": expected number of injected calls");
    Check(patched.Count - original.Count == (near ? 5 : 2), methodName + ": no unexpected added instructions");
    Check(injected.Count(i => ((MethodInfo)i.operand).Name == "Created") == 1, methodName + ": result hook exists");
    Check(injected.Count(i => ((MethodInfo)i.operand).Name == "Continue") == 1, methodName + ": gate exists");
    var retained = patched.Where(original.Contains).ToList();
    Check(retained.SequenceEqual(original), methodName + ": original instruction order retained");
    for (int i = 0; i < original.Count; i++) {
        var before = snapshot[i]; var after = original[i];
        Check(before.opcode == after.opcode && Equals(before.operand, after.operand) &&
            before.labels.SequenceEqual(after.labels) && before.blocks.SequenceEqual(after.blocks),
            methodName + ": instruction/label/exception block changed at " + i);
    }
    var createdHook = injected.Single(i => ((MethodInfo)i.operand).Name == "Created");
    var gateHook = injected.Single(i => ((MethodInfo)i.operand).Name == "Continue");
    int createdIndex = patched.IndexOf(createdHook), gateIndex = patched.IndexOf(gateHook);
    Check(patched[createdIndex - 1].operand is MethodInfo creation && creation.Name == "CreateObject", methodName + ": hook immediately follows creation");
    Check(patched[gateIndex - 1].operand is MethodInfo move && move.Name == "MoveNext", methodName + ": gate follows MoveNext");
    Check(patched[gateIndex + 1].opcode == OpCodes.Brtrue || patched[gateIndex + 1].opcode == OpCodes.Brtrue_S, methodName + ": existing exit branch retained");
    Check(original.Any(i => i.blocks.Count > 0), methodName + ": actual exception blocks covered");
    if (near)
    {
        int priorityIndex = patched.FindIndex(i => i.operand is MethodInfo called && called.DeclaringType == patchType && called.Name == "Prioritize");
        Check(priorityIndex >= 3 && patched[priorityIndex - 3].operand is MethodInfo sort && sort.Name == "Sort" &&
            sort.DeclaringType!.IsGenericType && sort.DeclaringType.GetGenericTypeDefinition() == typeof(List<>), "Priority follows vanilla sorting.");
        Check(patched[priorityIndex - 2].opcode == OpCodes.Ldarg_0 && patched[priorityIndex - 1].opcode == OpCodes.Ldfld &&
            patched[priorityIndex - 1].operand is FieldInfo field && field.Name == "m_tempCurrentObjects2", "Priority receives the existing sorted candidate list.");
        var missingSort = original.Select(i => new CodeInstruction(i)).ToList();
        var sorting = missingSort.Single(i => i.operand is MethodInfo called && called.Name == "Sort");
        sorting.opcode = OpCodes.Nop;
        sorting.operand = null;
        bool sortRejected = false;
        try { Apply(missingSort, method); } catch (InvalidOperationException) { sortRejected = true; }
        Check(sortRejected, "Unsupported sorting layout must be rejected.");
    }
    var malformed = original.Select(i => new CodeInstruction(i)).ToList();
    int finalMove = malformed.FindLastIndex(i => i.operand is MethodInfo called && called.Name == "MoveNext");
    malformed[finalMove + 1].opcode = OpCodes.Brfalse;
    bool rejected = false;
    try { Apply(malformed, method); } catch (InvalidOperationException) { rejected = true; }
    Check(rejected, methodName + ": malformed branch rejected");
    Console.WriteLine("PASS " + methodName + ": original IL/labels/EH preserved, hooks placed, malformed layout rejected");
}
var config = new ConfigFile(Path.Combine(Path.GetTempPath(), "bp-verification-" + Guid.NewGuid().ToString("N") + ".cfg"), false) { SaveOnConfigSet = false };
BindingFlags privateStatic = BindingFlags.Static | BindingFlags.NonPublic;
patchType.GetMethod("Install", privateStatic)!.Invoke(null, new object[] { config, new BepInEx.Logging.ManualLogSource("Verification") });
Check(!(bool)patchType.GetProperty("Installed", privateStatic)!.GetValue(null)!, "default configuration installs no optimization");
Check(!(bool)patchType.GetProperty("Enabled", privateStatic)!.GetValue(null)!, "default configuration keeps optimization disabled");
Check(Harmony.GetPatchInfo(scene.GetMethod("CreateObjectsSorted", BindingFlags.Instance | BindingFlags.NonPublic)!) == null,
    "default configuration leaves scene methods unpatched");
var milliseconds = config.Bind("Verification", "Budget", 4f);
patchType.GetField("milliseconds", privateStatic)!.SetValue(null, milliseconds);
var enabled = patchType.GetProperty("Enabled", privateStatic)!;
var current = patchType.GetField("current", privateStatic)!;
var begin = patchType.GetMethod("Begin", privateStatic)!;
var end = patchType.GetMethod("End", privateStatic)!;
Type batchType = current.FieldType;
var active = batchType.GetField("Active", BindingFlags.Instance | BindingFlags.NonPublic)!;
var budgetField = batchType.GetField("Budget", BindingFlags.Instance | BindingFlags.NonPublic)!;
object[] outerState = { Activator.CreateInstance(batchType)! };
enabled.SetValue(null, false);
begin.Invoke(null, outerState);
Check(!(bool)active.GetValue(current.GetValue(null))!, "disabled prefix leaves no active batch");
end.Invoke(null, outerState);
enabled.SetValue(null, true);
begin.Invoke(null, outerState);
Check((bool)active.GetValue(current.GetValue(null))!, "enabled prefix starts batch");
object[] nestedState = { Activator.CreateInstance(batchType)! };
begin.Invoke(null, nestedState);
object batch = current.GetValue(null)!;
object budget = budgetField.GetValue(batch)!;
budget.GetType().GetMethod("RecordCreation")!.Invoke(budget, new object[] { false });
budgetField.SetValue(batch, budget);
current.SetValue(null, batch);
end.Invoke(null, nestedState);
batch = current.GetValue(null)!;
budget = budgetField.GetValue(batch)!;
Check((bool)active.GetValue(batch)!, "nested finalizer retains parent active state");
Check((int)budget.GetType().GetProperty("Attempts")!.GetValue(budget)! == 1, "nested finalizer retains accumulated work");
end.Invoke(null, outerState);
Check(!(bool)active.GetValue(current.GetValue(null))!, "outer finalizer restores inactive state");
begin.Invoke(null, outerState);
budget = budgetField.GetValue(current.GetValue(null))!;
Check((int)budget.GetType().GetProperty("Attempts")!.GetValue(budget)! == 0, "new batch starts with fresh counters");
end.Invoke(null, outerState);
var quotaOption = config.Bind("ObjectLoading", "AdaptiveCreationQuota", false);
var quotaHook = patchType.GetMethod("ExpandQuota", privateStatic)!;
object[] allowance = { 10 };
quotaOption.Value = true;
quotaHook.Invoke(null, allowance);
Check((int)allowance[0] == 10, "Quota cannot expand outside an active batch.");
begin.Invoke(null, outerState);
quotaHook.Invoke(null, allowance);
Check((int)allowance[0] == 64, "Active budget can expand the native allowance.");
quotaOption.Value = false;
allowance[0] = 10;
quotaHook.Invoke(null, allowance);
Check((int)allowance[0] == 10, "Disabled adaptive quota preserves the native allowance.");
end.Invoke(null, outerState);
Console.WriteLine("PASS direct disabled/nested/finalizer state checks (no Unity creation invoked)");
Console.WriteLine("PASS " + checks + " checks; " + gameDirectory);
