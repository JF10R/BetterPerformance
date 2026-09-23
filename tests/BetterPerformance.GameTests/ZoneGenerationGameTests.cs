using System.Collections;
using System.Reflection;
using System.Runtime.Serialization;
using HarmonyLib;

// Runs entirely outside Unity. Invokes observation hooks with synthetic clocks and
// capture identities; never calls native SpawnZone or creates a Unity object.
internal static class ZoneGenerationGameTests
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    internal static int Run(Assembly game, Assembly plugin)
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Zone generation: " + message);
            checks++;
        }
        Type zoneSystem = game.GetType("ZoneSystem", true)!;
        Type spawnMode = game.GetType("ZoneSystem+SpawnMode", true)!;
        MethodInfo native = zoneSystem.GetMethods(Instance)
            .Single(method => method.Name == "SpawnZone" && method.GetParameters().Length == 3);
        ParameterInfo[] arguments = native.GetParameters();
        Check(native.ReturnType == typeof(bool) && arguments[0].ParameterType.Name == "Vector2s" &&
            arguments[1].ParameterType == spawnMode && arguments[2].IsOut && arguments[2].ParameterType.IsByRef &&
            arguments[2].ParameterType.GetElementType()!.Name == "GameObject",
            "SpawnZone retains the bool result, mode and out root signature");

        Type telemetry = plugin.GetType("BetterPerformance.ZoneGenerationTelemetry", true)!;
        MethodInfo before = telemetry.GetMethod("BeforeSpawn", Static)!;
        MethodInfo after = telemetry.GetMethod("AfterSpawn", Static)!;
        foreach (MethodInfo hook in new[] { before, after })
        {
            Check(hook.ReturnType == typeof(void), hook.Name + " cannot replace or suppress a native result or exception");
            Check(hook.GetParameters().All(argument => argument.Name == "__state" || !argument.ParameterType.IsByRef),
                hook.Name + " cannot mutate native arguments or exception");
        }
        Check(after.GetParameters().Single(argument => argument.Name == "__result").ParameterType == typeof(bool) &&
            after.GetParameters().Single(argument => argument.Name == "__exception").ParameterType == typeof(Exception),
            "Finalizer observes both the native outcome and exception");

        Type timing = plugin.GetType("BetterPerformance.TimingHooks", true)!;
        MethodInfo finalizer = timing.GetMethod("Finalizer", Static)!;
        Check(Calls(finalizer, telemetry.GetMethod("RecordPhase", Static)!),
            "Existing timings feed paired phases instead of leaving an unreachable zero gauge");
        Type metric = plugin.GetType("BetterPerformance.Core.Metric", true)!;
        FieldInfo currentSession = timing.GetField("Current", Static)!;
        FieldInfo installed = telemetry.GetField("<Installed>k__BackingField", Static)!;
        FieldInfo ownerThread = telemetry.GetField("ownerThread", Static)!;
        object? savedSession = currentSession.GetValue(null);
        object? savedInstalled = installed.GetValue(null);
        object? savedThread = ownerThread.GetValue(null);
        var availability = (IList)timing.GetField("Availability", Static)!.GetValue(null)!;
        object[] savedAvailability = availability.Cast<object>().ToArray();
        object session = FormatterServices.GetUninitializedObject(currentSession.FieldType);
        object window = telemetry.GetField("Window", Static)!.GetValue(null)!;
        MethodInfo drain = window.GetType().GetMethod("Drain")!;
        MethodInfo reset = telemetry.GetMethod("Reset", Static)!;
        MethodInfo phase = telemetry.GetMethod("RecordPhase", Static)!;
        object Begin(string mode)
        {
            object?[] call = { Enum.Parse(spawnMode, mode), null };
            before.Invoke(null, call);
            return call[1]!;
        }
        void Phase(string name, double ms) => phase.Invoke(null, new[] { Enum.Parse(metric, name), (object)ms, session });
        void End(object state, bool succeeded = true, Exception? exception = null) =>
            after.Invoke(null, new object?[] { state, succeeded, exception });
        long CallsAt(Array summaries, int mode) => (long)summaries.GetValue(mode)!.GetType().GetField("Calls")!.GetValue(summaries.GetValue(mode))!;
        object PeakAt(Array summaries, int mode) => summaries.GetValue(mode)!.GetType().GetField("Peak")!.GetValue(summaries.GetValue(mode))!;
        double PhaseMs(object sample, string field) => (double)sample.GetType().GetField(field)!.GetValue(sample)!;
        try
        {
            installed.SetValue(null, true);
            ownerThread.SetValue(null, Environment.CurrentManagedThreadId);
            currentSession.SetValue(null, session);
            reset.Invoke(null, null);

            object outer = Begin("Full");
            Phase("VegetationPlace", 10);
            object inner = Begin("Ghost");
            Phase("VegetationPlace", 20);
            End(inner);
            Phase("HeightmapRegenerate", 3);
            End(outer);
            Array nested = (Array)drain.Invoke(window, null)!;
            Check(CallsAt(nested, 1) == 1 && CallsAt(nested, 2) == 1,
                "Nested full and ghost calls keep separate outcomes");
            Check(PhaseMs(PeakAt(nested, 1), "VegetationMs") == 30 &&
                PhaseMs(PeakAt(nested, 1), "HeightmapMs") == 3 &&
                PhaseMs(PeakAt(nested, 2), "VegetationMs") == 20 &&
                PhaseMs(PeakAt(nested, 2), "HeightmapMs") == 0,
                "Nested phases restore the outer scope and remain tied to the calls that contain them");

            object failing = Begin("Full");
            Phase("ZonePlaceLocations", 8);
            End(failing, true, new InvalidOperationException("native failure"));
            Array failed = (Array)drain.Invoke(window, null)!;
            object failedSample = PeakAt(failed, 1);
            Check((bool)failedSample.GetType().GetField("Failed")!.GetValue(failedSample)! &&
                !(bool)failedSample.GetType().GetField("Succeeded")!.GetValue(failedSample)!,
                "An exception is reported without converting it to a successful spawn");
            Phase("VegetationPlace", 99);
            Check(CallsAt((Array)drain.Invoke(window, null)!, 1) == 0,
                "A finalizer releases the active scope even after a native failure");

            object resetDuringCall = Begin("Ghost");
            Phase("VegetationPlace", 77);
            reset.Invoke(null, null);
            End(resetDuringCall);
            Check(CallsAt((Array)drain.Invoke(window, null)!, 2) == 0,
                "Reset during a call cannot leak its completion into the next capture");

            object oldSessionCall = Begin("Ghost");
            currentSession.SetValue(null, FormatterServices.GetUninitializedObject(currentSession.FieldType));
            End(oldSessionCall);
            Check(CallsAt((Array)drain.Invoke(window, null)!, 2) == 0,
                "Capture identity changes reject in-flight samples even without a reset");
            currentSession.SetValue(null, session);

            // Disabled source phases must be absent, with explicit availability.
            object finalCall = Begin("Client");
            End(finalCall, false);
            Type number = plugin.GetType("BetterPerformance.Core.NumberValue", true)!;
            Type text = plugin.GetType("BetterPerformance.Core.TextValue", true)!;
            availability.Clear();
            availability.Add(Activator.CreateInstance(text, "probe.HeightmapRegenerate", "disabled"));
            var gauges = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(number))!;
            var labels = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(text))!;
            telemetry.GetMethod("Sample", Static)!.Invoke(null, new object[] { gauges, labels });
            Check(labels.Cast<object>().Any(label => (string)text.GetProperty("Name")!.GetValue(label)! == "zone_generation_heightmap_status" &&
                (string)text.GetProperty("Value")!.GetValue(label)! == "disabled") &&
                !gauges.Cast<object>().Any(gauge => ((string)number.GetProperty("Name")!.GetValue(gauge)!).EndsWith("_peak_heightmap_ms", StringComparison.Ordinal)),
                "A disabled source exports explicit availability and no false zero phase duration");
        }
        finally
        {
            reset.Invoke(null, null);
            currentSession.SetValue(null, savedSession);
            installed.SetValue(null, savedInstalled);
            ownerThread.SetValue(null, savedThread);
            availability.Clear();
            foreach (object entry in savedAvailability) availability.Add(entry);
        }
        Console.WriteLine("Zone generation: " + checks + " offline observation checks; native generation not invoked.");
        return checks;
    }

    private static bool Calls(MethodInfo caller, MethodInfo target) =>
        PatchProcessor.GetOriginalInstructions(caller).Any(instruction => instruction.operand is MethodInfo method && method == target);
}
