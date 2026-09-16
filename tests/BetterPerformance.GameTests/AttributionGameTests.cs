using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

// Verifies the per-name attribution probes against the installed game assemblies.
// Nothing here starts the game: it reads metadata, exercises ZPackage (a plain
// MemoryStream wrapper) and installs/removes Harmony patches in this process.
internal static class AttributionGameTests
{
    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic |
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    internal static int Run(Assembly game, Assembly plugin)
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Attribution telemetry: " + message);
            checks++;
        }
        Type TypeOf(string name) => game.GetType(name, true)!;

        Type scene = TypeOf("ZNetScene"), zdo = TypeOf("ZDO"), package = TypeOf("ZPackage");
        Type routed = TypeOf("ZRoutedRpc"), rpc = TypeOf("ZRpc"), routedData = TypeOf("ZRoutedRpc+RoutedRPCData");

        MethodInfo create = Method(scene, "CreateObject", new[] { zdo });
        Check(!create.IsStatic && create.ReturnType.Name == "GameObject" && create.GetParameters().Length == 1,
            "ZNetScene.CreateObject(ZDO) is a one-argument instance method returning a GameObject");
        Check(Method(zdo, "GetPrefab", Type.EmptyTypes).ReturnType == typeof(int),
            "ZDO.GetPrefab() yields the int prefab hash");
        MethodInfo serialize = Method(zdo, "Serialize", new[] { package });
        Check(!serialize.IsStatic && serialize.ReturnType == typeof(void) && serialize.GetParameters().Length == 1,
            "ZDO.Serialize(ZPackage) is a one-argument instance method");
        MethodInfo handle = Method(routed, "HandleRoutedRPC", new[] { routedData });
        Check(!handle.IsStatic && handle.ReturnType == typeof(void) && handle.GetParameters().Length == 1,
            "ZRoutedRpc.HandleRoutedRPC(RoutedRPCData) is a one-argument instance method");
        FieldInfo methodHash = Field(routedData, "m_methodHash");
        Check(methodHash.IsPublic && methodHash.FieldType == typeof(int),
            "RoutedRPCData.m_methodHash is a public int field");

        // Target split: the routed data carries the target identity, the manager resolves
        // it with one dictionary lookup, and the ZDO yields the same prefab hash the
        // prefab_create group already keys by.
        Type zdoid = TypeOf("ZDOID"), manager = TypeOf("ZDOMan");
        FieldInfo targetZdo = Field(routedData, "m_targetZDO");
        Check(targetZdo.IsPublic && targetZdo.FieldType == zdoid,
            "RoutedRPCData.m_targetZDO is a public ZDOID field");
        Check(zdoid.IsValueType && Method(zdoid, "IsNone", Type.EmptyTypes).ReturnType == typeof(bool),
            "ZDOID is a value type exposing bool IsNone(), so an absent target costs no lookup");
        Check(zdoid.GetField("None", BindingFlags.Public | BindingFlags.Static) != null,
            "ZDOID.None is the sentinel an unrouted target compares to");
        MethodInfo getZdo = Method(manager, "GetZDO", new[] { zdoid });
        Check(!getZdo.IsStatic && getZdo.ReturnType == zdo,
            "ZDOMan.GetZDO(ZDOID) is a one-argument instance lookup returning a ZDO or null");
        Check(manager.GetProperty("instance", BindingFlags.Public | BindingFlags.Static) != null,
            "ZDOMan.instance is the dispatcher the hook reads the target through");
        Check(Method(scene, "GetPrefab", new[] { typeof(int) }).ReturnType.Name == "GameObject",
            "ZNetScene.GetPrefab(int) resolves a hash to a prefab object at export time");
        Check(scene.GetProperty("instance", BindingFlags.Public | BindingFlags.Static) != null,
            "ZNetScene.instance is reachable from the main thread");

        MethodInfo[] routedRegisters = Registers(routed), rpcRegisters = Registers(rpc);
        Check(routedRegisters.Length == 7, "ZRoutedRpc exposes the expected seven Register(string, ...) overloads");
        Check(rpcRegisters.Length == 5, "ZRpc exposes the expected five Register(string, ...) overloads");
        Type hashing = Assembly.Load(new AssemblyName("assembly_utils")).GetType("StringExtensionMethods", true)!;
        MethodInfo stableHash = Method(hashing, "GetStableHashCode", new[] { typeof(string) });
        Check(stableHash.IsStatic && stableHash.ReturnType == typeof(int),
            "StringExtensionMethods.GetStableHashCode(string) is the hash a registered name maps to");

        MethodInfo size = Method(package, "Size", Type.EmptyTypes), position = Method(package, "GetPos", Type.EmptyTypes);
        Check(size.ReturnType == typeof(int) && position.ReturnType == typeof(int),
            "ZPackage exposes int Size() and int GetPos()");
        object empty = Activator.CreateInstance(package)!;
        int Read(MethodInfo reader) => (int)(reader.Invoke(empty, null) ?? -1);
        Check(Read(size) == 0, "a fresh package has no written length");
        Method(package, "Write", new[] { typeof(int) }).Invoke(empty, new object[] { 7 });
        Check(Read(size) == 4 && Read(position) == 4, "writing advances both the written length and the cursor");
        Method(package, "SetPos", new[] { typeof(int) }).Invoke(empty, new object[] { 0 });
        Check(Read(size) == 4 && Read(position) == 0,
            "Size() is the written length and the correct byte source; GetPos() follows the cursor");

        Type telemetry = plugin.GetType("BetterPerformance.AttributionTelemetry", true)!;
        string[] forbidden = { "SetPos", "Write", "InvokeRoutedRPC", "InvokeRPC", "CreateObject", "Deserialize", "ReadPackage" };
        // Damage-number pressure gauge: a call count and the world-text list length.
        Type damageText = TypeOf("DamageText");
        MethodInfo addText = Method(damageText, "AddInworldText");
        Check(!addText.IsStatic && addText.ReturnType == typeof(void),
            "DamageText.AddInworldText is the instance method every damage number passes through");
        Check(damageText.GetMethods(Declared).Count(method => method.Name == "AddInworldText") == 1,
            "AddInworldText has one overload, so a name-only patch is unambiguous");
        FieldInfo worldTexts = Field(damageText, "m_worldTexts");
        Check(typeof(ICollection).IsAssignableFrom(worldTexts.FieldType),
            "DamageText.m_worldTexts exposes Count without enumerating the live texts");
        Check(damageText.GetProperty("instance", BindingFlags.Public | BindingFlags.Static) != null,
            "DamageText.instance is reachable at poll time");

        foreach (string name in new[] { "CreateBefore", "CreateAfter", "SerializeBefore", "SerializeAfter",
            "RoutedBefore", "RoutedAfter", "RegisterAfter", "DamageTextAdded" })
        {
            MethodInfo hook = Method(telemetry, name);
            Check(hook.ReturnType == typeof(void), name + " cannot replace a result or swallow a native exception");
            Check(hook.GetParameters().All(parameter => parameter.Name == "__state" || !parameter.ParameterType.IsByRef),
                name + " does not mutate game arguments");
            Check(!References(hook).OfType<MethodInfo>().Any(called =>
                called.DeclaringType?.Assembly == game && forbidden.Contains(called.Name)),
                name + " observes without writing to, repositioning or dispatching game state");
        }

        FieldInfo functions = Field(routed, "m_functions");
        Check(functions.FieldType.IsGenericType &&
            functions.FieldType.GetGenericTypeDefinition() == typeof(Dictionary<,>) &&
            functions.FieldType.GetGenericArguments()[0] == typeof(int),
            "ZRoutedRpc.m_functions is the int-keyed registry the handler fallback reads");
        Type registeredMethod = functions.FieldType.GetGenericArguments()[1];
        Type[] holders = new[] { "RoutedMethod", "RoutedMethod`1", "RoutedMethod`2", "RoutedMethod`3",
            "RoutedMethod`4", "RoutedMethod`5", "RoutedMethod`6" }
            .Select(name => game.GetType(name)).Where(candidate => candidate != null).Select(candidate => candidate!).ToArray();
        Check(holders.Length == routedRegisters.Length, "one concrete method holder exists per Register overload");
        Check(holders.All(candidate => candidate.GetInterfaces().Contains(registeredMethod) &&
            typeof(Delegate).IsAssignableFrom(Field(candidate, "m_action").FieldType)),
            "every registered routed method exposes an m_action delegate to name it by");
        Check(routed.GetProperty("instance", BindingFlags.Public | BindingFlags.Static) != null,
            "ZRoutedRpc.instance is reachable at export time");

        string pluginId = (string)(plugin.GetType("BetterPerformance.Plugin", true)!
            .GetField("PluginId", BindingFlags.Public | BindingFlags.Static)!.GetValue(null) ?? "");
        string owner = pluginId + ".AttributionTelemetry";
        MethodInfo[] timed = { create, serialize, handle };
        try
        {
            var options = new ConfigFile(Path.Combine(Path.GetTempPath(),
                "bp-attribution-" + Guid.NewGuid().ToString("N") + ".cfg"), false) { SaveOnConfigSet = false };
            Method(telemetry, "Install").Invoke(null, new object[] { options, new ManualLogSource("AttributionGameTests") });
            Check(telemetry.GetProperty("Installed", Declared)!.GetValue(null) is true,
                "installation reports success against the installed assemblies");
            Check(Harmony.GetPatchInfo(addText)?.Prefixes.Any(patch => patch.owner == owner) == true,
                "the damage-number gauge counts AddInworldText calls");
            Check(Harmony.GetPatchInfo(addText)!.Postfixes.All(patch => patch.owner != owner) &&
                Harmony.GetPatchInfo(addText)!.Transpilers.All(patch => patch.owner != owner),
                "the damage-number gauge observes without rewriting or post-processing the call");
            foreach (MethodInfo target in timed)
            {
                Patches info = Harmony.GetPatchInfo(target) ??
                    throw new InvalidOperationException("Attribution telemetry: " + target.Name + " is unpatched");
                Check(info.Prefixes.Any(patch => patch.owner == owner) && info.Finalizers.Any(patch => patch.owner == owner),
                    target.Name + " carries an observational prefix and finalizer");
                Check(!info.Postfixes.Any(patch => patch.owner == owner) &&
                    !info.Transpilers.Any(patch => patch.owner == owner),
                    target.Name + " is not rewritten or post-processed");
            }
            int named = routedRegisters.Concat(rpcRegisters).Count(method =>
                Harmony.GetPatchInfo(method)?.Postfixes.Any(patch => patch.owner == owner) == true);
            Check(named >= 1, "at least one Register overload yields registered RPC names");
            Check(routedRegisters.Concat(rpcRegisters).Where(method => method.IsGenericMethodDefinition)
                .All(method => Harmony.GetPatchInfo(method)?.Postfixes.Any(patch => patch.owner == owner) != true),
                "generic Register overloads are left untouched, so removal stays possible");

            Method(telemetry, "RegisterAfter").Invoke(null, new object[] { "RPC_AttributionProbe" });
            Method(telemetry, "StartCapture").Invoke(null, null);
            Check(((Array)Method(telemetry, "Drain").Invoke(null, null)!).Length == 0,
                "no capture activity produces no attribution rows");

            Type number = plugin.GetType("BetterPerformance.Core.NumberValue", true)!;
            Type text = plugin.GetType("BetterPerformance.Core.TextValue", true)!;
            var gauges = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(number))!;
            var labels = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(text))!;
            Method(telemetry, "Sample").Invoke(null, new object[] { gauges, labels });
            PropertyInfo gaugeName = number.GetProperty("Name")!, gaugeValue = number.GetProperty("Value")!;
            double Gauge(string name) => (double)(gaugeValue.GetValue(gauges.Cast<object>()
                .Single(entry => (string?)gaugeName.GetValue(entry) == name)) ?? -1);
            PropertyInfo labelName = text.GetProperty("Name")!, labelValue = text.GetProperty("Value")!;
            string Label(string name) => (string?)labelValue.GetValue(labels.Cast<object>()
                .Single(entry => (string?)labelName.GetValue(entry) == name)) ?? "";
            Check(Gauge("attribution_rpc_names_known") >= 1, "a registered name is retained for hash resolution");
            Check(Gauge("attribution_target_split_rpcs") == 4,
                "the default configuration splits exactly the four allow-listed routed RPCs");
            Check(Gauge("attribution_routed_rpc_target_keys") == 0 && Gauge("attribution_target_split_pairs") == 0 &&
                Gauge("attribution_target_split_key_collisions") == 0 && Gauge("attribution_target_unmapped_keys") == 0,
                "an idle capture resolves no target and keeps the split map empty");
            Check(Gauge("damage_text_added") == 0 && Gauge("damage_text_live_max") == 0,
                "an idle capture observes no damage numbers");
            Check(Label("attribution_damage_text_gauge") == "installed",
                "the damage-number gauge reports its own availability");
            Check(Label("attribution_target_split_scope").Contains("RPC_Damage"),
                "the split scope names the RPCs it applies to, inside the capture");
            Check(Gauge("attribution_probe_failures") == 0 && Gauge("attribution_other_thread_skips") == 0,
                "an idle capture records no probe failures");
            Console.WriteLine("Attribution telemetry: RPC name hooks on " + named + "/" +
                (routedRegisters.Length + rpcRegisters.Length) + " Register overloads; candidates=" +
                Gauge("attribution_register_hook_candidates") + "; failures=" +
                Gauge("attribution_register_hook_failures") + "; genericSkips=" +
                Gauge("attribution_register_generic_skips") + "; status=" +
                telemetry.GetProperty("Status", Declared)!.GetValue(null));
            Check(Gauge("attribution_register_hook_failures") == 0 && Gauge("attribution_register_generic_skips") == 10,
                "no Register patch is attempted where Harmony would refuse it");
            Check(Label("attribution_register_hook_failure") == "none", "no name hook failed during installation");
            foreach (string label in new[] { "attribution_telemetry_status", "attribution_rpc_name_resolution",
                "attribution_scope", "attribution_bytes_semantics", "attribution_key_semantics",
                "attribution_rpc_key_source", "attribution_register_hook_failure",
                "attribution_target_split_scope", "attribution_damage_text_gauge" })
                Check(labels.Cast<object>().Any(entry => (string?)labelName.GetValue(entry) == label),
                    label + " documents the export in the capture itself");
        }
        finally
        {
            Method(telemetry, "Uninstall").Invoke(null, null);
        }
        // Standalone .NET Framework cannot read bodies that carry Unity interface
        // defaults, so Harmony may refuse the removal here. The probe reports that
        // instead of throwing; removal itself requires Unity runtime validation.
        string cleanup = (string?)telemetry.GetProperty("Status", Declared)!.GetValue(null) ?? "";
        if (cleanup.StartsWith("unpatch_failed", StringComparison.Ordinal))
            Console.WriteLine("STATIC ONLY attribution cleanup: " + cleanup +
                "; Harmony rescans every patched method in this shared process, so removal requires Unity runtime validation");
        else
            foreach (MethodInfo target in timed)
                Check(Harmony.GetPatchInfo(target)?.Prefixes.Any(patch => patch.owner == owner) != true,
                    target.Name + " is released when the probe is removed");

        // Runs last and is never removed: a refused generic patch still leaves state
        // that blocks a later removal, which is exactly why the probe skips them.
        MethodInfo generic = routedRegisters.First(method => method.IsGenericMethodDefinition);
        try
        {
            new Harmony("jf10r.BetterPerformance.AttributionGameTests")
                .Patch(generic, postfix: new HarmonyMethod(typeof(AttributionGameTests), nameof(Ignore)));
            Console.WriteLine("Attribution telemetry: generic Register overloads accept a name hook here; " +
                "registry handler names remain the portable source");
        }
        catch (Exception exception)
        {
            Exception innermost = exception;
            while (innermost.InnerException != null) innermost = innermost.InnerException;
            Console.WriteLine("Attribution telemetry: generic Register name hook refused by " +
                innermost.GetType().Name + ": " + innermost.Message.Split('\n')[0]);
        }

        Console.WriteLine("Attribution telemetry: " + checks + " installed-game checks; per-name totals require Unity runtime validation.");
        return checks;
    }

    private static void Ignore() { }

    private static MethodInfo Method(Type type, string name, Type[]? parameters = null) =>
        (parameters == null ? type.GetMethod(name, Declared) : type.GetMethod(name, Declared, null, parameters, null))
        ?? throw new InvalidOperationException("Attribution telemetry: missing method " + type.Name + "." + name);

    private static FieldInfo Field(Type type, string name) => type.GetField(name, Declared)
        ?? throw new InvalidOperationException("Attribution telemetry: missing field " + type.Name + "." + name);

    private static MethodInfo[] Registers(Type type) => type.GetMethods(Declared)
        .Where(method => method.Name == "Register" && method.ReturnType == typeof(void) && !method.IsStatic &&
            method.GetParameters().Length == 2 && method.GetParameters()[0].ParameterType == typeof(string))
        .ToArray();

    // Metadata-only IL reader; no game method is invoked to inspect a hook body.
    private static IEnumerable<MemberInfo> References(MethodInfo method)
    {
        var opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(OpCode)).Select(field => (OpCode)field.GetValue(null)!)
            .ToDictionary(operation => operation.Value);
        byte[] bytes = method.GetMethodBody()!.GetILAsByteArray()!;
        for (int offset = 0; offset < bytes.Length;)
        {
            int raw = bytes[offset++];
            if (raw == 0xfe) raw = 0xfe00 | bytes[offset++];
            OpCode opcode = opcodes[unchecked((short)raw)];
            int length;
            switch (opcode.OperandType)
            {
                case OperandType.InlineNone: length = 0; break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar: length = 1; break;
                case OperandType.InlineVar: length = 2; break;
                case OperandType.InlineI8:
                case OperandType.InlineR: length = 8; break;
                case OperandType.InlineSwitch: length = 4 + 4 * BitConverter.ToInt32(bytes, offset); break;
                default: length = 4; break;
            }
            if (opcode.OperandType == OperandType.InlineMethod || opcode.OperandType == OperandType.InlineField)
                yield return method.Module.ResolveMember(BitConverter.ToInt32(bytes, offset),
                    method.DeclaringType!.GetGenericArguments(), method.GetGenericArguments())!;
            offset += length;
        }
    }
}
