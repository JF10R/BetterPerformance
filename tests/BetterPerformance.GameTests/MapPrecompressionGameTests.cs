using System.Collections;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Offline contract checks for speculative map pre-compression. No Unity object is created and
// no game method is invoked beyond the plain managed package writer and compressor.
internal static class MapPrecompressionGameTests
{
    internal static int Run(Assembly game, Assembly plugin)
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("MapPrecompression: " + message);
            checks++;
        }
        Type minimap = game.GetType("Minimap", true)!;
        Type pinData = game.GetType("Minimap+PinData", true)!;
        Type package = game.GetType("ZPackage", true)!;
        Type net = game.GetType("ZNet", true)!;
        Type module = plugin.GetType("BetterPerformance.MapPrecompression", true)!;
        Type cache = plugin.GetType("BetterPerformance.MapCompressionCache", true)!;

        // 1. The hooked mutators.
        var explore = AccessTools.DeclaredMethod(minimap, "Explore", new[] { typeof(int), typeof(int) });
        var exploreOthers = AccessTools.DeclaredMethod(minimap, "ExploreOthers", new[] { typeof(int), typeof(int) });
        var reset = AccessTools.DeclaredMethod(minimap, "ResetAndExplore", new[] { typeof(BitArray), typeof(BitArray) });
        Check(explore != null && explore.ReturnType == typeof(bool) && !explore.IsStatic && !explore.IsPublic,
            "Minimap.Explore(int, int) is a private instance bool");
        Check(exploreOthers != null && exploreOthers.ReturnType == typeof(bool) && !exploreOthers.IsStatic && !exploreOthers.IsPublic,
            "Minimap.ExploreOthers(int, int) is a private instance bool");
        Check(reset != null && reset.ReturnType == typeof(void) && !reset.IsStatic,
            "Minimap.ResetAndExplore(BitArray, BitArray) is the wholesale rewrite");
        Check(AccessTools.DeclaredMethod(minimap, "SetMapData", new[] { typeof(byte[]) }) != null,
            "Minimap.SetMapData(byte[]) exists and reaches ResetAndExplore");

        // 2. Field contracts the snapshot depends on.
        Check(AccessTools.DeclaredField(minimap, "m_explored")?.FieldType == typeof(BitArray), "m_explored is a BitArray");
        Check(AccessTools.DeclaredField(minimap, "m_exploredOthers")?.FieldType == typeof(BitArray), "m_exploredOthers is a BitArray");
        Check(AccessTools.DeclaredField(minimap, "m_textureSize")?.FieldType == typeof(int), "m_textureSize is an int");
        Check(AccessTools.DeclaredField(minimap, "m_pins")?.FieldType == typeof(List<>).MakeGenericType(pinData), "m_pins is a List<PinData>");
        // The verifier does not reference UnityEngine; the vector type comes from the game.
        Type vector3 = AccessTools.DeclaredField(pinData, "m_pos")!.FieldType;
        object Vector(float x, float y, float z) => Activator.CreateInstance(vector3, new object[] { x, y, z })!;
        Type ParameterType(string name) => name switch
        {
            "Int32" => typeof(int),
            "Int64" => typeof(long),
            "Boolean" => typeof(bool),
            "String" => typeof(string),
            "Vector3" => vector3,
            _ => throw new InvalidOperationException("Unexpected package writer parameter " + name),
        };
        Check(AccessTools.DeclaredField(pinData, "m_name")?.FieldType == typeof(string) &&
            vector3.FullName == "UnityEngine.Vector3" &&
            AccessTools.DeclaredField(pinData, "m_type")!.FieldType.IsEnum &&
            AccessTools.DeclaredField(pinData, "m_checked")?.FieldType == typeof(bool) &&
            AccessTools.DeclaredField(pinData, "m_ownerID")?.FieldType == typeof(long) &&
            AccessTools.DeclaredField(pinData, "m_save")?.FieldType == typeof(bool) &&
            AccessTools.DeclaredField(pinData, "m_author")!.FieldType.Name == "PlatformUserID",
            "saved pin fields have the types the snapshot flattens");
        Check(AccessTools.DeclaredMethod(net, "IsReferencePositionPublic", Type.EmptyTypes)?.ReturnType == typeof(bool),
            "ZNet.IsReferencePositionPublic() is the reference-position flag");

        // 3. The save path is the only consumer, so priming the cache reaches every map save.
        var callers = CallerCounts(game.Location, "Minimap", new[] { "SaveMapData", "GetMapData" });
        Check(callers["SaveMapData"] == 1, "Minimap.SaveMapData has exactly one caller (actual=" + callers["SaveMapData"] + ")");
        Check(callers["GetMapData"] == 1, "Minimap.GetMapData has exactly one caller (actual=" + callers["GetMapData"] + ")");

        // 4. The compressor the worker reuses.
        var writeCompressed = AccessTools.DeclaredMethod(package, "WriteCompressed", new[] { package });
        Check(writeCompressed != null && writeCompressed.ReturnType == typeof(void), "ZPackage.WriteCompressed(ZPackage) is the compression call site");
        var compressed = PatchProcessor.GetOriginalInstructions(writeCompressed!);
        var compress = compressed.Select(i => i.operand as MethodInfo)
            .FirstOrDefault(m => m != null && m.Name == "Compress" && m.IsStatic && m.ReturnType == typeof(byte[]));
        Check(compress != null && compress!.DeclaringType!.Name == "Utils" &&
            compress.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(byte[]) }),
            "WriteCompressed delegates to the static Utils.Compress(byte[])");
        Check(compressed.Count(i => i.Calls(compress!)) == 1, "WriteCompressed compresses exactly once");
        Check(compressed.Count(i => i.operand is MethodInfo m && m.DeclaringType == typeof(BinaryWriter) && m.Name == "Write") == 2,
            "WriteCompressed emits a length and a payload write and nothing else");

        // 5. The writer order the worker reproduces, read off the native method itself.
        var getMapData = AccessTools.DeclaredMethod(minimap, "GetMapData", Type.EmptyTypes)!;
        string[] order = PatchProcessor.GetOriginalInstructions(getMapData)
            .Where(i => i.operand is MethodInfo m && m.DeclaringType == package && m.Name == "Write")
            .Select(i => ((MethodInfo)i.operand).GetParameters()[0].ParameterType.Name).ToArray();
        // The first Write(int) is the outer package version; the rest is the inner payload.
        string[] expectedOrder = { "Int32", "Int32", "Boolean", "Boolean", "Int32",
            "String", "Vector3", "Int32", "Boolean", "Int64", "String", "Boolean" };
        Check(order.SequenceEqual(expectedOrder), "native inner-package writer order is [" + string.Join(",", order) + "]");

        // 6. Byte identity of the worker's encode against the native writer and compressor.
        var buildPayload = AccessTools.DeclaredMethod(module, "BuildPayload")!;
        Type snapshotType = module.GetNestedType("Snapshot", BindingFlags.NonPublic)!;
        Type pinType = module.GetNestedType("Pin", BindingFlags.NonPublic)!;
        var bits = new BitArray(100);
        var otherBits = new BitArray(100);
        for (int i = 0; i < 100; i++) { bits[i] = i % 3 == 0; otherBits[i] = i % 7 == 1; }
        int words = (100 + 31) / 32;
        int[] exploredWords = new int[words], othersWords = new int[words];
        bits.CopyTo(exploredWords, 0); otherBits.CopyTo(othersWords, 0);
        object MakePin(string name, float x, float y, float z, int type, bool ticked, long owner, string author)
        {
            object pin = Activator.CreateInstance(pinType)!;
            void Set(string field, object value) => pinType.GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(pin, value);
            Set("Name", name); Set("X", x); Set("Y", y); Set("Z", z);
            Set("Type", type); Set("Checked", ticked); Set("Owner", owner); Set("Author", author);
            return pin;
        }
        var pins = Array.CreateInstance(pinType, 2);
        pins.SetValue(MakePin("Home", 1.5f, -2.25f, 3f, 4, true, 76561198000000001L, "Steam_76561198000000001"), 0);
        pins.SetValue(MakePin("", float.NegativeInfinity, 0f, 1e30f, 0, false, 0L, ""), 1);
        object snapshot = Activator.CreateInstance(snapshotType)!;
        void SetSnapshot(string field, object value) => snapshotType.GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(snapshot, value);
        SetSnapshot("TextureSize", 10);
        SetSnapshot("Length", 100);
        SetSnapshot("Explored", exploredWords);
        SetSnapshot("Others", othersWords);
        SetSnapshot("Pins", pins);
        SetSnapshot("ReferencePositionPublic", true);

        object actualInner = buildPayload.Invoke(null, new[] { snapshot })!;
        // The reference encoding issues the same ZPackage calls the native method issues, in
        // the order asserted above, sourced from the BitArrays rather than from the snapshot.
        object expectedInner = Activator.CreateInstance(package)!;
        void Write(object target, string parameter, object value) =>
            AccessTools.DeclaredMethod(package, "Write", new[] { ParameterType(parameter) })!.Invoke(target, new[] { value });
        Write(expectedInner, "Int32", 10);
        for (int i = 0; i < 100; i++) Write(expectedInner, "Boolean", bits[i]);
        for (int j = 0; j < 100; j++) Write(expectedInner, "Boolean", otherBits[j]);
        Write(expectedInner, "Int32", 2);
        Write(expectedInner, "String", "Home");
        Write(expectedInner, "Vector3", Vector(1.5f, -2.25f, 3f));
        Write(expectedInner, "Int32", 4);
        Write(expectedInner, "Boolean", true);
        Write(expectedInner, "Int64", 76561198000000001L);
        Write(expectedInner, "String", "Steam_76561198000000001");
        Write(expectedInner, "String", "");
        Write(expectedInner, "Vector3", Vector(float.NegativeInfinity, 0f, 1e30f));
        Write(expectedInner, "Int32", 0);
        Write(expectedInner, "Boolean", false);
        Write(expectedInner, "Int64", 0L);
        Write(expectedInner, "String", "");
        Write(expectedInner, "Boolean", true);
        var getArray = AccessTools.DeclaredMethod(package, "GetArray", Type.EmptyTypes)!;
        byte[] Bytes(object value) => (byte[])getArray.Invoke(value, null)!;
        Check(Bytes(actualInner).SequenceEqual(Bytes(expectedInner)), "worker encode is byte-identical to the native writer order");
        SetSnapshot("BulkBitsAllowed", true);
        object bulkInner = buildPayload.Invoke(null, new[] { snapshot })!;
        Check(Bytes(bulkInner).SequenceEqual(Bytes(expectedInner)), "bulk snapshot encode preserves native explored, shared, pin and public flag bytes");
        SetSnapshot("ReferencePositionPublic", false);
        object privateBulk = buildPayload.Invoke(null, new[] { snapshot })!;
        SetSnapshot("BulkBitsAllowed", false);
        object privateNative = buildPayload.Invoke(null, new[] { snapshot })!;
        Check(Bytes(privateBulk).SequenceEqual(Bytes(privateNative)) && Bytes(privateBulk).Last() == 0,
            "bulk encode preserves a private reference position");
        SetSnapshot("ReferencePositionPublic", true);
        Check(Bytes(actualInner).Length == 4 + 100 + 100 + 4 + (1 + 4 + 12 + 4 + 1 + 8 + 24) + (1 + 12 + 4 + 1 + 8 + 1) + 1,
            "encoded payload has the native one-byte-per-bit size");

        // 7. The published pair is what the cache would have stored after a native save.
        object publishedOuter = Activator.CreateInstance(package)!;
        writeCompressed!.Invoke(publishedOuter, new[] { actualInner });
        byte[] publishedInput = Bytes(actualInner), publishedEncoded = Bytes(publishedOuter);

        var installed = AccessTools.DeclaredPropertySetter(cache, "Installed")!;
        var enabled = cache.GetProperty("Enabled", BindingFlags.NonPublic | BindingFlags.Static)!;
        var mainThread = AccessTools.DeclaredField(cache, "mainThread");
        var clear = AccessTools.DeclaredMethod(cache, "Clear")!;
        var adopt = AccessTools.DeclaredMethod(cache, "Adopt")!;
        var write = AccessTools.DeclaredMethod(cache, "Write")!;
        long Count(string field) => (long)AccessTools.DeclaredField(cache, field).GetValue(null)!;
        bool savedEnabled = (bool)enabled.GetValue(null)!;
        bool savedInstalled = (bool)AccessTools.DeclaredPropertyGetter(cache, "Installed")!.Invoke(null, null)!;
        int savedThread = (int)mainThread.GetValue(null)!;
        var moduleOption = AccessTools.DeclaredField(module, "option");
        object? savedModuleOption = moduleOption.GetValue(null);
        var moduleInstalled = AccessTools.DeclaredPropertySetter(module, "Installed")!;
        bool savedModuleInstalled = (bool)AccessTools.DeclaredPropertyGetter(module, "Installed")!.Invoke(null, null)!;
        try
        {
            clear.Invoke(null, null);
            installed.Invoke(null, new object[] { true });
            enabled.SetValue(null, true);
            mainThread.SetValue(null, Thread.CurrentThread.ManagedThreadId);
            Check((bool)adopt.Invoke(null, new object?[] { publishedInput, publishedEncoded, null })!, "publication is adopted by reference");
            Check((bool)AccessTools.DeclaredField(cache, "primed").GetValue(null)!, "the adopted entry is marked as speculative");

            object source = Activator.CreateInstance(package, new object[] { publishedInput })!;
            object expectedDestination = Activator.CreateInstance(package)!, actualDestination = Activator.CreateInstance(package)!;
            long hits = Count("hits"), primedHits = Count("primedHits");
            writeCompressed.Invoke(expectedDestination, new[] { source });
            write.Invoke(null, new[] { actualDestination, source });
            Check(Bytes(expectedDestination).SequenceEqual(Bytes(actualDestination)),
                "the speculative output equals the bytes native compression writes for the same input");
            Check(Count("hits") == hits + 1 && Count("primedHits") == primedHits + 1, "the primed entry serves exactly one counted hit");
            Check(Count("primedHits") == (long)AccessTools.DeclaredPropertyGetter(cache, "PrimedHits")!.Invoke(null, null)!,
                "primed hits are exposed without a second counter");
            long secondHits = Count("hits");
            write.Invoke(null, new[] { Activator.CreateInstance(package)!, source });
            Check(Count("hits") == secondHits + 1 && Count("primedHits") == primedHits + 1,
                "a later ordinary hit on the same entry is not counted as primed again");

            // A superseded speculation costs a miss and nothing else.
            clear.Invoke(null, null);
            byte[] staleInput = (byte[])publishedInput.Clone();
            staleInput[staleInput.Length - 1] ^= 0xFF;
            adopt.Invoke(null, new object?[] { staleInput, publishedEncoded, null });
            long stale = Count("primedStale");
            object staleExpected = Activator.CreateInstance(package)!, staleActual = Activator.CreateInstance(package)!;
            writeCompressed.Invoke(staleExpected, new[] { source });
            write.Invoke(null, new[] { staleActual, source });
            Check(Bytes(staleExpected).SequenceEqual(Bytes(staleActual)), "a stale speculation yields the native bytes");
            Check(Count("primedStale") == stale + 1, "a stale speculation is counted once");
            Check(!(bool)AccessTools.DeclaredField(cache, "primed").GetValue(null)!, "the stale mark is consumed");

            // A completed worker need not wait for the next pump when its exact input saves.
            var pendingInput = AccessTools.DeclaredField(module, "pendingInput");
            var pendingEncoded = AccessTools.DeclaredField(module, "pendingEncoded");
            var pendingWorld = AccessTools.DeclaredField(module, "pendingWorld");
            clear.Invoke(null, null);
            pendingInput.SetValue(null, publishedInput);
            pendingEncoded.SetValue(null, publishedEncoded);
            pendingWorld.SetValue(null, null);
            var config = new BepInEx.Configuration.ConfigFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".cfg"), false);
            config.SaveOnConfigSet = false;
            moduleOption.SetValue(null, config.Bind("Test", "Enabled", true));
            moduleInstalled.Invoke(null, new object[] { true });
            // Saving never waits for the worker's short publication section.
            var gate = AccessTools.DeclaredField(module, "Gate").GetValue(null)!;
            using (var entered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                bool holderTimedOut = false;
                var holder = new Thread(() => { lock (gate) { entered.Set(); holderTimedOut = !release.Wait(5000); } });
                holder.Start();
                Check(entered.Wait(5000), "worker fixture acquired publication gate");
                try
                {
                    object contendedActual = Activator.CreateInstance(package)!;
                    write.Invoke(null, new[] { contendedActual, source });
                    Check(!holderTimedOut && Bytes(contendedActual).SequenceEqual(Bytes(expectedDestination)),
                        "save falls back to native bytes without waiting for a publishing worker");
                    Check(pendingInput.GetValue(null) != null, "contended save leaves pending result for a later adoption");
                }
                finally { release.Set(); holder.Join(); }
            }
            long pendingHits = Count("primedHits");
            object pendingActual = Activator.CreateInstance(package)!;
            write.Invoke(null, new[] { pendingActual, source });
            Check(Bytes(pendingActual).SequenceEqual(Bytes(expectedDestination)) && Count("primedHits") == pendingHits + 1,
                "save adopts an exact completed worker without waiting for the pump");
            Check(pendingInput.GetValue(null) == null, "save consumes the matching pending result once");

            // A stale pending snapshot must not evict the current exact cache on a save.
            pendingInput.SetValue(null, staleInput);
            pendingEncoded.SetValue(null, publishedEncoded);
            long matchingHits = Count("hits"), matchingMisses = Count("misses");
            object preservedActual = Activator.CreateInstance(package)!;
            write.Invoke(null, new[] { preservedActual, source });
            Check(Bytes(preservedActual).SequenceEqual(Bytes(expectedDestination)) && Count("hits") == matchingHits + 1 &&
                Count("misses") == matchingMisses, "stale pending bytes cannot displace a matching saved cache entry");
            pendingInput.SetValue(null, publishedInput);
            moduleInstalled.Invoke(null, new object[] { false });
            long disabledAdoptions = Count("primedHits");
            write.Invoke(null, new[] { Activator.CreateInstance(package)!, source });
            Check(Count("primedHits") == disabledAdoptions && pendingInput.GetValue(null) != null,
                "disabled speculation cannot adopt a worker result during save");
            pendingInput.SetValue(null, null);
            pendingEncoded.SetValue(null, null);
        }
        finally
        {
            clear.Invoke(null, null);
            enabled.SetValue(null, savedEnabled);
            installed.Invoke(null, new object[] { savedInstalled });
            mainThread.SetValue(null, savedThread);
            moduleOption.SetValue(null, savedModuleOption);
            moduleInstalled.Invoke(null, new object[] { savedModuleInstalled });
            AccessTools.DeclaredField(module, "pendingInput").SetValue(null, null);
            AccessTools.DeclaredField(module, "pendingEncoded").SetValue(null, null);
            AccessTools.DeclaredField(module, "pendingWorld").SetValue(null, null);
        }

        // 8. The module ships off by default and reports why it is unavailable.
        Check(!(bool)AccessTools.DeclaredPropertyGetter(module, "Installed")!.Invoke(null, null)!, "module defaults to not installed");
        Check((string)AccessTools.DeclaredPropertyGetter(module, "Status")!.Invoke(null, null)! == "disabled", "module defaults to the disabled status");
        var pump = AccessTools.DeclaredMethod(module, "Pump");
        Check(pump != null && pump.GetParameters().Length == 0 && pump.ReturnType == typeof(void), "Pump() is the main-thread entry point");
        pump!.Invoke(null, null);
        Check((string)AccessTools.DeclaredPropertyGetter(module, "Status")!.Invoke(null, null)! == "disabled", "a pump while disabled does nothing");

        Console.WriteLine("Map pre-compression: " + checks + " offline contract checks; live trigger policy and hit rate remain unmeasured.");
        return checks;
    }

    // Counts call sites per method name on one declaring type, over the whole game assembly.
    private static Dictionary<string, int> CallerCounts(string assemblyPath, string declaringType, string[] methodNames)
    {
        var counts = methodNames.ToDictionary(name => name, _ => 0);
        using var module = ModuleDefinition.ReadModule(assemblyPath);
        foreach (TypeDefinition type in module.GetTypes())
            foreach (MethodDefinition method in type.Methods)
            {
                if (!method.HasBody) continue;
                foreach (Instruction instruction in method.Body.Instructions)
                {
                    if (instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt) continue;
                    if (!(instruction.Operand is MethodReference target)) continue;
                    if (target.DeclaringType.FullName != declaringType || !counts.ContainsKey(target.Name)) continue;
                    if (target.Parameters.Count != 0) continue;
                    counts[target.Name]++;
                }
            }
        return counts;
    }
}
