using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

internal static class AiGameTests
{
    internal static void Run(Assembly game, Assembly plugin)
    {
        const BindingFlags StaticPrivate = BindingFlags.Static | BindingFlags.NonPublic;
        Type telemetry = plugin.GetType("BetterPerformance.AiTelemetry", true)!;
        Type updater = game.GetType("MonoUpdatersExtra", true)!;
        Type list = typeof(List<>).MakeGenericType(game.GetType("IUpdateAI", true)!);
        MethodInfo batch = AccessTools.DeclaredMethod(updater, "UpdateAI", new[] { list, list, typeof(string), typeof(float) });
        Type pathfinding = game.GetType("Pathfinding", true)!;
        Type[] pathArguments = (Type[])telemetry.GetField("PathArguments", StaticPrivate)!.GetValue(null)!;
        MethodInfo path = AccessTools.DeclaredMethod(pathfinding, "GetPath", pathArguments);
        if (batch == null || !batch.IsStatic || batch.ReturnType != typeof(void) || path == null || path.ReturnType != typeof(bool))
            throw new InvalidOperationException("Current AI/path signatures do not match.");
        Type spawn = game.GetType("SpawnSystem", true)!;
        Type spawnData = AccessTools.Inner(spawn, "SpawnData");
        if (spawnData == null || AccessTools.DeclaredMethod(spawn, "UpdateSpawnList",
            new[] { typeof(List<>).MakeGenericType(spawnData), typeof(DateTime), typeof(bool), typeof(string) })?.ReturnType != typeof(void) ||
            AccessTools.DeclaredMethod(spawn, "Spawn", new[] { spawnData, pathArguments[0], typeof(bool) })?.ReturnType != typeof(void) ||
            AccessTools.DeclaredMethod(pathfinding, "UpdatePathfinding", Type.EmptyTypes)?.ReturnType != typeof(void))
            throw new InvalidOperationException("Current spawn/path-maintenance signatures do not match.");

        var harmony = new Harmony("jf10r.BetterPerformance.AiGameTests");
        var availability = (IList)plugin.GetType("BetterPerformance.TimingHooks", true)!
            .GetField("Availability", StaticPrivate)!.GetValue(null)!;
        int initialAvailabilityCount = availability.Count;
        try
        {
            telemetry.GetMethod("Install", StaticPrivate)!.Invoke(null,
                new object[] { harmony, new ManualLogSource("AiGameTests"), true });
            var batchPatches = Harmony.GetPatchInfo(batch);
            var pathPatches = Harmony.GetPatchInfo(path);
            if (batchPatches == null || !batchPatches.Prefixes.Any(p => p.owner == harmony.Id) ||
                !batchPatches.Finalizers.Any(p => p.owner == harmony.Id) || pathPatches == null ||
                !pathPatches.Postfixes.Any(p => p.owner == harmony.Id))
                throw new InvalidOperationException("AI cadence/path-result hooks did not install.");
            // The cadence prefix references native Unity Time and cannot be invoked
            // by standalone CLR even with no capture. Only inspect its patch above.
            telemetry.GetMethod("PathResult", StaticPrivate)!.Invoke(null, new object[] { false });
            var resultParameter = telemetry.GetMethod("PathResult", StaticPrivate)!.GetParameters().Single();
            if (resultParameter.ParameterType != typeof(bool) || telemetry.GetMethod("BatchFinalizer", StaticPrivate)!.ReturnType != typeof(void))
                throw new InvalidOperationException("Observation hooks must not replace results/exceptions.");
            Console.WriteLine("PASS AI/spawn native signatures, cadence/result patch installation, inactive path-result hook");
        }
        finally
        {
            try { harmony.UnpatchSelf(); }
            catch (Exception exception) when (OfflineInterfaceLimitation(exception))
            { Console.WriteLine("STATIC ONLY AI cleanup: standalone CLR cannot JIT Unity interface defaults; no game method invoked"); }
            telemetry.GetMethod("Reset", StaticPrivate)!.Invoke(null, null);
            while (availability.Count > initialAvailabilityCount) availability.RemoveAt(availability.Count - 1);
        }
    }

    private static bool OfflineInterfaceLimitation(Exception exception)
    {
        for (Exception? current = exception; current != null; current = current.InnerException)
            if (current is TypeLoadException && current.Message.IndexOf("non-abstract", StringComparison.OrdinalIgnoreCase) >= 0
                && current.Message.IndexOf("interface", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }
}
