using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using BepInEx.Logging;
using HarmonyLib;

// No Unity scene or socket is started. Real ZRpc dispatches use in-memory packages
// and synthetic callbacks; only callback metadata enters attribution output.
internal static class DirectRpcAttributionGameTests
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
        BindingFlags.Static | BindingFlags.Instance;
    private static readonly Exception ExpectedFailure = new InvalidOperationException("synthetic RPC failure");
    private static Action? nestedCall;
    private static int receivedPayload;

    internal static int Run(Assembly game, Assembly plugin)
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Direct RPC attribution: " + message);
            checks++;
        }
        Type rpc = game.GetType("ZRpc", true)!, package = game.GetType("ZPackage", true)!;
        Type telemetry = plugin.GetType("BetterPerformance.AttributionTelemetry", true)!;
        Type timing = plugin.GetType("BetterPerformance.TimingHooks", true)!;
        MethodInfo Method(Type type, string name) => type.GetMethod(name, All)!;
        object? Call(string name, params object[] arguments) => Method(telemetry, name).Invoke(null, arguments);
        FieldInfo current = timing.GetField("Current", All)!;
        object? previousCapture = current.GetValue(null);
        MethodInfo handle = Method(rpc, "HandlePackage");
        MethodInfo write = package.GetMethod("Write", new[] { typeof(int) })!;
        MethodInfo rewind = package.GetMethod("SetPos")!;
        MethodInfo position = package.GetMethod("GetPos")!;
        MethodInfo stableHash = Assembly.Load(new AssemblyName("assembly_utils"))
            .GetType("StringExtensionMethods", true)!.GetMethod("GetStableHashCode")!;
        int Hash(string name) => (int)stableHash.Invoke(null, new object[] { name })!;
        object Packet(string name, int? payload = null)
        {
            object packet = Activator.CreateInstance(package)!;
            write.Invoke(packet, new object[] { Hash(name) });
            if (payload.HasValue) write.Invoke(packet, new object[] { payload.Value });
            rewind.Invoke(packet, new object[] { 0 });
            return packet;
        }
        long Counter(string name) => (long)telemetry.GetField(name, All)!.GetValue(null)!;
        object[] Drain() => ((IEnumerable)Call("Drain")!).Cast<object>()
            .Where(row => (string)row.GetType().GetProperty("Group")!.GetValue(row)! == "direct_rpc").ToArray();
        string Key(object row) => (string)row.GetType().GetProperty("Key")!.GetValue(row)!;
        double Sum(object row) => (double)row.GetType().GetProperty("SumMs")!.GetValue(row)!;
        long Count(object row) => (long)row.GetType().GetProperty("Count")!.GetValue(row)!;

        Check(!handle.IsStatic && handle.ReturnType == typeof(void), "native dispatch signature is intact");
        var native = PatchProcessor.GetOriginalInstructions(handle);
        var firstCall = native.First(instruction => instruction.operand is MethodInfo);
        Check(firstCall.operand is MethodInfo first && first.DeclaringType == package && first.Name == "ReadInt",
            "native dispatch first consumes its four-byte method identifier");
        foreach (string name in new[] { "DirectBefore", "DirectAfter" })
        {
            MethodInfo hook = Method(telemetry, name);
            Check(hook.ReturnType == typeof(void) && hook.GetParameters()
                .All(parameter => parameter.Name == "__state" || !parameter.ParameterType.IsByRef),
                name + " cannot skip native dispatch, change arguments or replace its exception");
        }
        try
        {
            Call("InstallDirectRpc", new ManualLogSource("DirectRpcAttributionGameTests"));
            Check((string?)telemetry.GetField("directRpcStatus", All)!.GetValue(null) == "installed",
                "the direct dispatcher patch installs against this game assembly");
            string owner = (string)plugin.GetType("BetterPerformance.Plugin", true)!
                .GetField("PluginId", All)!.GetValue(null)! + ".AttributionTelemetry";
            Patches patches = Harmony.GetPatchInfo(handle)!;
            Check(patches.Prefixes.Any(patch => patch.owner == owner) && patches.Finalizers.Any(patch => patch.owner == owner) &&
                patches.Transpilers.All(patch => patch.owner != owner) && patches.Postfixes.All(patch => patch.owner != owner),
                "the dispatcher has only observational prefix/finalizer hooks");
            current.SetValue(null, FormatterServices.GetUninitializedObject(current.FieldType));
            telemetry.GetProperty("Enabled", All)!.SetValue(null, true);
            Call("StartCapture");
            object dispatcher = Activator.CreateInstance(rpc, new object?[] { null })!;
            MethodInfo nongeneric = rpc.GetMethods(All).Single(method => method.Name == "Register" && !method.IsGenericMethod);
            MethodInfo generic = rpc.GetMethods(All).Single(method => method.Name == "Register" &&
                method.IsGenericMethodDefinition && method.GetGenericArguments().Length == 1).MakeGenericMethod(typeof(int));
            Delegate Callback(MethodInfo register, string name, string callback, bool payload)
            {
                var method = new DynamicMethod(name, typeof(void), payload ? new[] { rpc, typeof(int) } : new[] { rpc },
                    typeof(DirectRpcAttributionGameTests), true);
                ILGenerator il = method.GetILGenerator();
                if (payload) il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Call, Method(typeof(DirectRpcAttributionGameTests), callback));
                il.Emit(OpCodes.Ret);
                return method.CreateDelegate(register.GetParameters()[1].ParameterType);
            }
            void Register(MethodInfo registration, string name, string callback, bool payload = false) =>
                registration.Invoke(dispatcher, new object[] { name, Callback(registration, name + "Handler", callback, payload) });
            Register(generic, "BP_DirectGenericProbe", nameof(Receive), true);
            object packet = Packet("BP_DirectGenericProbe", 12345);
            handle.Invoke(dispatcher, new[] { packet });
            object[] rows = Drain();
            Check(receivedPayload == 12345 && (int)position.Invoke(packet, null)! == 8,
                "observation preserves generic payload decoding and final native cursor");
            Check(rows.Length == 1 && Key(rows[0]) == "BP_DirectGenericProbeHandler" && Count(rows[0]) == 1,
                "a generic registration resolves the callback name, without its wrapper or a hash fallback");

            Register(nongeneric, "BP_DirectInner", nameof(Inner));
            Register(nongeneric, "BP_DirectOuter", nameof(Outer));
            nestedCall = () => handle.Invoke(dispatcher, new[] { Packet("BP_DirectInner") });
            handle.Invoke(dispatcher, new[] { Packet("BP_DirectOuter") });
            rows = Drain();
            object inner = rows.Single(row => Key(row) == "BP_DirectInnerHandler");
            object outer = rows.Single(row => Key(row) == "BP_DirectOuterHandler");
            Check(rows.Length == 2 && Count(inner) == 1 && Count(outer) == 1 && Sum(outer) >= Sum(inner),
                "nested calls retain independent keys and inclusive outer duration");

            Register(nongeneric, "BP_DirectFailure", nameof(Fail));
            Exception? observed = null;
            try { handle.Invoke(dispatcher, new[] { Packet("BP_DirectFailure") }); }
            catch (TargetInvocationException exception) { observed = exception.InnerException; }
            rows = Drain();
            Check(ReferenceEquals(observed, ExpectedFailure) && Counter("directRpcFailures") == 1 &&
                rows.Length == 1 && Key(rows[0]) == "BP_DirectFailureHandler" && Count(rows[0]) == 1,
                "the exact native failure escapes while its call and failure count remain visible");

            object truncated = Activator.CreateInstance(package)!;
            try { handle.Invoke(dispatcher, new[] { truncated }); }
            catch (TargetInvocationException exception) { observed = exception.InnerException; }
            Check(observed is EndOfStreamException && Counter("directHeaderUnavailable") == 1 &&
                Counter("directRpcFailures") == 2 && Drain().Length == 0,
                "a truncated header keeps native failure semantics and creates no fabricated key");
            object ping = Activator.CreateInstance(package)!;
            write.Invoke(ping, new object[] { 0 });
            package.GetMethod("Write", new[] { typeof(bool) })!.Invoke(ping, new object[] { false });
            rewind.Invoke(ping, new object[] { 0 });
            handle.Invoke(dispatcher, new[] { ping });
            Check(Counter("directPingSkips") == 1 && Drain().Length == 0,
                "heartbeat replies are explicitly skipped and never become a bogus RPC bucket");
            handle.Invoke(dispatcher, new[] { Packet("BP_LateRegistration") });
            rows = Drain();
            Check(rows.Length == 1 && Key(rows[0]).StartsWith("hash:", StringComparison.Ordinal),
                "an unregistered identifier is explicit rather than given a guessed name");
            Register(generic, "BP_LateRegistration", nameof(Receive), true);
            handle.Invoke(dispatcher, new[] { Packet("BP_LateRegistration", 6789) });
            Check(Key(Drain().Single()) == "BP_LateRegistrationHandler" && receivedPayload == 6789,
                "a formerly unknown identifier resolves once its callback is registered");
            Check(Counter("probeFailures") == 0, "all exercised observation paths succeed");
            Call("Reset");
            Check(Counter("directRpcFailures") == 0 && Counter("directHeaderUnavailable") == 0 &&
                Counter("directPingSkips") == 0 && ((IDictionary)telemetry.GetField("DirectHandlers", All)!.GetValue(null)!).Count == 0,
                "capture reset clears direct counters and callback metadata");
            Call("StartCapture");
            Delegate boundedCallback = Callback(nongeneric, "BP_BoundedHandler", nameof(Inner), false);
            for (int i = 0; i < 600; i++)
            {
                string name = "BP_Bounded_" + i;
                nongeneric.Invoke(dispatcher, new object[] { name, boundedCallback });
                handle.Invoke(dispatcher, new[] { Packet(name) });
            }
            rows = Drain();
            Check(rows.Length == 25 && rows.Sum(Count) == 600 && rows.Any(row => Key(row) == "other"),
                "more than 256 identifiers conserve all calls in the top 24 plus other");
            Check(((IDictionary)telemetry.GetField("DirectHandlers", All)!.GetValue(null)!).Count == 512 &&
                Counter("directNameCapacitySkips") == 88,
                "callback metadata stays bounded at 512 keys and reports every capacity skip");
            Call("StartCapture");
            nestedCall = () => Call("StartCapture");
            handle.Invoke(dispatcher, new[] { Packet("BP_DirectOuter") });
            Check(Drain().Length == 0, "a reset and restart during dispatch cannot contaminate the next capture");
            nestedCall = () => current.SetValue(null, FormatterServices.GetUninitializedObject(current.FieldType));
            handle.Invoke(dispatcher, new[] { Packet("BP_DirectOuter") });
            Check(Drain().Length == 0, "a replaced capture session cannot receive an older dispatch scope");
            FieldInfo status = telemetry.GetField("directRpcStatus", All)!;
            status.SetValue(null, "unavailable:synthetic");
            handle.Invoke(dispatcher, new[] { Packet("BP_DirectGenericProbe", 9876) });
            Check(receivedPayload == 9876 && Drain().Length == 0,
                "hooks left by a partial install stay inert while the original callback still runs");
            status.SetValue(null, "installed");
        }
        finally
        {
            nestedCall = null;
            Call("Uninstall");
            current.SetValue(null, previousCapture);
        }
        Console.WriteLine("Direct RPC attribution: " + checks + " offline checks; in-game overhead remains unmeasured.");
        return checks;
    }

    private static void Receive(int payload) => receivedPayload = payload;
    private static void Inner() { }
    private static void Outer() => nestedCall!();
    private static void Fail() => throw ExpectedFailure;
}
