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
// Name the build every contract below was checked against; a client and a dedicated
// server can sit on different builds while one of them is still updating.
try
{
    object? gameVersion = game.GetType("Version", true)!.GetProperty("CurrentVersion", BindingFlags.Public | BindingFlags.Static)!.GetValue(null);
    Console.WriteLine("Installed game version: " + gameVersion + " (" + gameDirectory + ")");
}
catch (Exception exception) { Console.WriteLine("Installed game version: unavailable offline (" + exception.GetType().Name + ")"); }
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
    Check(patched.Count - original.Count == (near ? 6 : 3), methodName + ": no unexpected added instructions");
    Check(injected.Count(i => ((MethodInfo)i.operand).Name == "Created") == 1, methodName + ": result hook exists");
    string gateName = near ? "ContinueWithTelemetry" : "ContinueDistantWithTelemetry";
    Check(injected.Count(i => ((MethodInfo)i.operand).Name == gateName) == 1, methodName + ": observed gate exists");
    var retained = patched.Where(original.Contains).ToList();
    Check(retained.SequenceEqual(original), methodName + ": original instruction order retained");
    for (int i = 0; i < original.Count; i++) {
        var before = snapshot[i]; var after = original[i];
        Check(before.opcode == after.opcode && Equals(before.operand, after.operand) &&
            before.labels.SequenceEqual(after.labels) && before.blocks.SequenceEqual(after.blocks),
            methodName + ": instruction/label/exception block changed at " + i);
    }
    var createdHook = injected.Single(i => ((MethodInfo)i.operand).Name == "Created");
    var gateHook = injected.Single(i => ((MethodInfo)i.operand).Name == gateName);
    int createdIndex = patched.IndexOf(createdHook), gateIndex = patched.IndexOf(gateHook);
    Check(patched[createdIndex - 1].operand is MethodInfo creation && creation.Name == "CreateObject", methodName + ": hook immediately follows creation");
    Check(patched[gateIndex - 2].operand is MethodInfo move && move.Name == "MoveNext", methodName + ": observed gate follows MoveNext");
    var receiver = patched[gateIndex - 1];
    Check((receiver.opcode == OpCodes.Ldloca || receiver.opcode == OpCodes.Ldloca_S) &&
        receiver.opcode == patched[gateIndex - 3].opcode && Equals(receiver.operand, patched[gateIndex - 3].operand) &&
        receiver.labels.Count == 0 && receiver.blocks.Count == 0,
        methodName + ": observed gate receives the same enumerator by reference without duplicated labels/EH");
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
    var unknownReceiver = original.Select(i => new CodeInstruction(i)).ToList();
    unknownReceiver[finalMove - 1].opcode = OpCodes.Ldnull;
    unknownReceiver[finalMove - 1].operand = null;
    var fallback = Apply(unknownReceiver, method);
    Check(fallback.Any(i => i.operand is MethodInfo called && called.DeclaringType == patchType && called.Name == "Continue") &&
        !fallback.Any(i => i.operand is MethodInfo called && called.DeclaringType == patchType && called.Name == gateName),
        methodName + ": unproven enumerator receiver retains the original decision-only gate");
    malformed[finalMove + 1].opcode = OpCodes.Brfalse;
    bool rejected = false;
    try { Apply(malformed, method); } catch (InvalidOperationException) { rejected = true; }
    Check(rejected, methodName + ": malformed branch rejected");
    Console.WriteLine("PASS " + methodName + ": original IL/labels/EH preserved, hooks placed, malformed layout rejected");
}
Type telemetryType = plugin.GetType("BetterPerformance.LootQueueTelemetry", true)!;
var observeTranspiler = telemetryType.GetMethod("Transpile", BindingFlags.Static | BindingFlags.NonPublic)!;
var nearMethod = scene.GetMethod("CreateObjectsSorted", BindingFlags.Instance | BindingFlags.NonPublic)!;
foreach (bool withBudget in new[] { false, true })
{
    var original = PatchProcessor.GetOriginalInstructions(nearMethod);
    var input = withBudget ? Apply(original, nearMethod) : original;
    var snapshot = input.Select(i => (i.opcode, i.operand, labels: i.labels.ToArray(), blocks: i.blocks.ToArray())).ToArray();
    var observed = ((IEnumerable<CodeInstruction>)observeTranspiler.Invoke(null, new object[] { input })!).ToList();
    Check(observed.Count == input.Count + 3, "Loot observation adds exactly one bounded observer call.");
    Check(observed.Where(input.Contains).SequenceEqual(input), "Loot observer retains all original and budget instructions in order.");
    for (int i = 0; i < input.Count; i++)
        Check(input[i].opcode == snapshot[i].opcode && Equals(input[i].operand, snapshot[i].operand) &&
            input[i].labels.SequenceEqual(snapshot[i].labels) && input[i].blocks.SequenceEqual(snapshot[i].blocks),
            "Loot observer preserves instructions, labels and exception blocks.");
    int observer = observed.FindIndex(i => i.operand is MethodInfo call && call.DeclaringType == telemetryType && call.Name == "Observe");
    Check(observer >= 3 && observed[observer - 3].operand is MethodInfo sorting && sorting.Name == "Sort", "Loot observer immediately follows native sorting.");
    if (withBudget)
        Check(observer < observed.FindIndex(i => i.operand is MethodInfo call && call.DeclaringType == patchType && call.Name == "Prioritize"), "Loot observations precede optional priority changes.");
    var malformed = input.Where(i => !(i.operand is MethodInfo call && call.Name == "Sort")).ToList();
    bool rejected = false;
    try { ((IEnumerable<CodeInstruction>)observeTranspiler.Invoke(null, new object[] { malformed })!).ToList(); }
    catch (InvalidOperationException) { rejected = true; }
    Check(rejected, "Loot observation rejects an unsupported queue layout.");
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
// Resolve and install observational patches against the real managed assemblies,
// but never invoke a save, network handler, or any Unity game method.
Type timingType = plugin.GetType("BetterPerformance.TimingHooks", true)!;
Type metricType = plugin.GetType("BetterPerformance.Core.Metric", true)!;
Type zdo = game.GetType("ZDO", true)!, zdoPeer = game.GetType("ZDOMan+ZDOPeer", true)!;
Type package = game.GetType("ZPackage", true)!, rpc = game.GetType("ZRpc", true)!;
Type zdoList = typeof(List<>).MakeGenericType(zdo);
Type chunkList = typeof(List<>).MakeGenericType(typeof(Tuple<,>).MakeGenericType(game.GetType("ZoneSystem+ChunkIndex", true)!, zdoList));
var probeContracts = new[] {
    ("Game", "SavePlayerProfile", "CharacterSave", new[] { typeof(bool), typeof(bool) }, typeof(void)),
    ("Minimap", "GetMapData", "MapSerialization", Type.EmptyTypes, typeof(byte[])),
    ("PlayerProfile", "SavePlayerToDisk", "CharacterSaveToDisk", Type.EmptyTypes, typeof(bool)),
    ("ZDOMan", "GetSaveClonePerChunk", "SaveClone", Type.EmptyTypes, chunkList),
    ("ZRpc", "HandlePackage", "RpcDispatch", new[] { package }, typeof(void)),
    ("ZDOMan", "RPC_ZDOData", "IncomingZdoData", new[] { rpc, package }, typeof(void)),
    ("ZDOMan", "CreateSyncList", "SyncListBuild", new[] { zdoPeer, zdoList }, typeof(void)),
    ("ZDOMan", "SendZDOs", "SendZdos", new[] { zdoPeer, typeof(bool) }, typeof(bool))
};
MethodInfo installTiming = timingType.GetMethod("Install", privateStatic)!;
var timingHarmony = new Harmony("BetterPerformance.offline.timing.verification");
var timingLog = new BepInEx.Logging.ManualLogSource("TimingVerification");
timingLog.LogEvent += (_, entry) => Console.WriteLine(entry.Data);
int offlineJitLimitations = 0;
bool OfflineUnityFailure(HarmonyException exception) =>
    (exception.InnerException is TypeLoadException load && load.Message.Contains("Non-abstract, non-.cctor method in an interface")) ||
    (exception.InnerException is System.Security.SecurityException security && security.Message.Contains("ECall methods must be packaged into a system module"));
try
{
    installTiming.Invoke(null, new object[] { timingHarmony, timingLog, true });
    var availability = (System.Collections.IEnumerable)timingType.GetField("Availability", privateStatic)!.GetValue(null)!;
    var states = availability.Cast<object>().ToDictionary(
        value => (string)value.GetType().GetProperty("Name")!.GetValue(value)!,
        value => (string)value.GetType().GetProperty("Value")!.GetValue(value)!);
    var registered = (System.Collections.IDictionary)timingType.GetField("Metrics", privateStatic)!.GetValue(null)!;
    foreach (var (typeName, methodName, metricName, parameters, returnType) in probeContracts)
    {
        Check(Enum.IsDefined(metricType, metricName), metricName + ": metric is exportable");
        var method = AccessTools.DeclaredMethod(game.GetType(typeName, true)!, methodName, parameters);
        Check(method != null && method.ReturnType == returnType && method.GetMethodBody() != null,
            metricName + ": current game has the exact managed signature");
        Check(registered.Contains(method!) && registered[method!]!.ToString() == metricName,
            metricName + ": production installer maps the exact game method to this metric");
        if (states.TryGetValue("probe." + metricName, out var failedState) && failedState == "patch_failed")
        {
            // Unity's Mono supports these game interfaces; the .NET Framework
            // verifier cannot JIT their default implementations. Fail every other
            // patch error, and never report these as runtime-verified patches.
            try { timingHarmony.Patch(method!, prefix: new HarmonyMethod(timingType, "Prefix"), finalizer: new HarmonyMethod(timingType, "Finalizer")); }
            catch (HarmonyException exception) when (OfflineUnityFailure(exception))
            {
                offlineJitLimitations++;
                Console.WriteLine("STATIC ONLY " + metricName + ": exact registration verified; offline .NET Framework lacks Unity interface/native support");
                continue;
            }
        }
        Check(states.TryGetValue("probe." + metricName, out var state) && state == "enabled",
            metricName + ": production install reports success (actual=" + state + ")");
        var patches = Harmony.GetPatchInfo(method!);
        var prefixes = patches!.Prefixes.Where(p => p.owner == timingHarmony.Id).ToArray();
        var finalizers = patches.Finalizers.Where(p => p.owner == timingHarmony.Id).ToArray();
        Check(prefixes.Length == 1 && prefixes[0].PatchMethod.ReturnType == typeof(void),
            metricName + ": prefix cannot skip the original method");
        Check(finalizers.Length == 1 && finalizers[0].PatchMethod.ReturnType == typeof(void),
            metricName + ": finalizer cannot replace a result or exception");
        Check(!patches.Transpilers.Any(p => p.owner == timingHarmony.Id) && !patches.Postfixes.Any(p => p.owner == timingHarmony.Id),
            metricName + ": observational patch only");
    }
}
finally
{
    try { timingHarmony.UnpatchSelf(); }
    catch (HarmonyException exception) when (OfflineUnityFailure(exception))
    { Console.WriteLine("STATIC ONLY cleanup: offline Unity JIT limitation; verification process exits without invoking game methods"); }
}
Console.WriteLine("PASS eight exact save/RPC/replication timing registrations; offline JIT limitations=" + offlineJitLimitations + "; no game methods invoked");
Console.WriteLine("PASS " + checks + " checks; " + gameDirectory);
// One drifted contract used to abort the process and silently skip every module after it,
// which is the opposite of what a post-update check is for. Each module now runs, reports,
// and a single non-zero exit at the end names all of them.
var contractFailures = new List<string>();
void Module(string name, Action run)
{
    try { run(); }
    catch (Exception exception)
    {
        contractFailures.Add(name);
        Console.WriteLine("FAIL " + name + ": " + exception.Message);
    }
}
Module("Ai", () => AiGameTests.Run(game, plugin));
Module("FastMapSerialization", () => FastMapSerializationTests.Run(game, plugin));
Module("PackageCopy", () => PackageCopyGameTests.Run(game, plugin));
Module("MapCompressionCache", () => MapCompressionCacheGameTests.Run(game, plugin));
Module("MinimapCache", () => MinimapCacheGameTests.Run(game, plugin));
Module("Action", () => ActionGameTests.Run(game, plugin));
Module("BudgetPreparation", () => BudgetPreparationGameTests.Run(game, plugin));
Module("Loading", () => LoadingGameTests.Run(game, plugin));
Module("LoadingDetails", () => LoadingDetailsGameTests.Run(game, plugin));
Module("InitialLoading", () => InitialLoadingGameTests.Run(game, plugin));
Module("Ownership", () => OwnershipGameTests.Run(game, plugin));
Module("OwnershipExpedite", () => OwnershipExpediteGameTests.Run(game, plugin));
Module("Simulation", () => SimulationGameTests.Run(game, plugin));
Module("Attribution", () => AttributionGameTests.Run(game, plugin));
Module("CloudWrite", () => CloudWriteGameTests.Run(game, plugin, managedDirectory));
Module("Replication", () => ReplicationGameTests.Run(game, plugin));
Module("Terrain", () => TerrainGameTests.Run(game, plugin));
Module("Gameplay", () => GameplayGameTests.Run(game, plugin));
Module("GameplayProbes", () => GameplayProbesGameTests.Run(game, plugin));
Module("GuiSound", () => GuiSoundGameTests.Run(game, plugin));
Module("Mining", () => MiningGameTests.Run(game, plugin));
Module("LootVisibility", () => LootVisibilityGameTests.Run(game, plugin));
Module("BiomeCache", () => BiomeCacheGameTests.Run(game, plugin));
Module("Smelter", () => SmelterGameTests.Run(game, plugin));
Module("Dungeon", () => DungeonGameTests.Run(game, plugin));
Module("MapPrecompression", () => MapPrecompressionGameTests.Run(game, plugin));
if (contractFailures.Count > 0)
{
    Console.WriteLine("FAILED game contracts (" + contractFailures.Count + "): " + string.Join(", ", contractFailures));
    Environment.Exit(1);
}
Console.WriteLine("PASS all module game contracts; " + gameDirectory);
