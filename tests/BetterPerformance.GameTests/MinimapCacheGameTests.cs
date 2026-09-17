using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;

internal static class MinimapCacheGameTests
{
    internal static int Run(Assembly game, Assembly plugin)
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("MinimapCache: " + message);
            checks++;
        }
        Type minimap = game.GetType("Minimap", true)!;
        Type generator = game.GetType("WorldGenerator", true)!;
        Type module = plugin.GetType("BetterPerformance.MinimapTextureCache", true)!;
        BindingFlags privateStatic = BindingFlags.Static | BindingFlags.NonPublic;

        // The exact native members the cache depends on.
        MethodInfo generate = AccessTools.DeclaredMethod(minimap, "GenerateWorldMap")!;
        Check(generate != null && !generate.IsStatic && generate.ReturnType == typeof(void) &&
            generate.GetParameters().Length == 0, "GenerateWorldMap has the expected signature.");
        foreach (var (name, typeName) in new[]
                 { ("m_mapTexture", "UnityEngine.Texture2D"), ("m_forestMaskTexture", "UnityEngine.Texture2D"),
                   ("m_heightTexture", "UnityEngine.Texture2D"), ("m_textureSize", "System.Int32"),
                   ("m_pixelSize", "System.Single"), ("m_hasGenerated", "System.Boolean") })
        {
            var field = AccessTools.DeclaredField(minimap, name);
            Check(field != null && field.FieldType.FullName == typeName, name + " exists with type " + typeName + ".");
        }
        Check(AccessTools.DeclaredMethod(minimap, "GetPixelColor") != null, "GetPixelColor exists.");
        Check(AccessTools.DeclaredMethod(minimap, "GetMaskColor") != null, "GetMaskColor exists.");
        Check(AccessTools.DeclaredMethod(generator, "GetBiome", new[] { typeof(float), typeof(float), typeof(float), typeof(bool) }) != null,
            "The generator still exposes the sampled GetBiome overload.");
        Check(AccessTools.DeclaredMethod(generator, "GetBiomeHeight") != null, "The generator still exposes GetBiomeHeight.");
        Check(AccessTools.DeclaredField(game.GetType("World", true)!, "m_worldGenVersion") != null,
            "The world carries the generator version the key uses.");

        // The shipped IL satisfies the contract, so the feature is available on this build.
        Type stepType = module.GetNestedType("ILStep", BindingFlags.NonPublic)!;
        FieldInfo codeField = stepType.GetField("Code")!, operandField = stepType.GetField("Operand")!;
        var readBody = module.GetMethod("ReadBody", privateStatic)!;
        var validate = module.GetMethod("ValidateGenerateShape", privateStatic)!;
        System.Collections.IList Original() =>
            (System.Collections.IList)readBody.Invoke(null, new object[] { generate! })!;
        var original = Original();
        Check(original.Count > 50, "The shipped GenerateWorldMap body decodes: " + original.Count + " instructions.");
        object Step(OpCode code, object? operand) => Activator.CreateInstance(stepType, new object?[] { code, operand })!;
        OpCode CodeOf(object step) => (OpCode)codeField.GetValue(step)!;
        object? OperandOf(object step) => operandField.GetValue(step);
        int Find(Func<object, bool> predicate)
        {
            for (int i = 0; i < original.Count; i++) if (predicate(original[i]!)) return i;
            throw new InvalidOperationException("MinimapCache: expected instruction not found in GenerateWorldMap.");
        }
        validate.Invoke(null, new object[] { original });
        checks++;

        bool Rejects(Action<System.Collections.IList> mutate, string what)
        {
            var copy = Original();
            mutate(copy);
            try { validate.Invoke(null, new object[] { copy }); }
            catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException) { return true; }
            throw new InvalidOperationException("MinimapCache: a modified layout was accepted (" + what + ").");
        }
        int applyIndex = Find(step => OperandOf(step) is MethodInfo m && m.Name == "Apply" && m.DeclaringType!.Name == "Texture2D");
        var heightIndexes = new List<int>();
        for (int i = 0; i < original.Count; i++)
            if (CodeOf(original[i]!) == OpCodes.Ldfld && (OperandOf(original[i]!) as FieldInfo)?.Name == "m_heightTexture")
                heightIndexes.Add(i);
        Check(heightIndexes.Count > 0, "The height texture is read by the shipped body.");
        Check(Rejects(code => code.RemoveAt(applyIndex), "missing texture upload"),
            "Dropping one texture upload is rejected.");
        Check(Rejects(code => code[applyIndex] = Step(OpCodes.Call,
                AccessTools.DeclaredMethod(typeof(Console), "WriteLine", Type.EmptyTypes)), "foreign call"),
            "A call to an unexpected type is rejected.");
        Check(Rejects(code => code.Insert(0, Step(OpCodes.Stsfld, AccessTools.DeclaredField(minimap, "m_textureSize"))),
                "extra state write"),
            "A write to game state beyond the three textures is rejected.");
        Check(Rejects(code => heightIndexes.ForEach(i => code[i] = Step(OpCodes.Ldfld, AccessTools.DeclaredField(minimap, "m_mapTexture"))),
                "missing height texture"),
            "Losing a cached texture field is rejected.");

        // The reader the key depends on decodes the generation code itself.
        foreach (var sampled in new[]
                 {
                     AccessTools.DeclaredMethod(minimap, "GetPixelColor"), AccessTools.DeclaredMethod(minimap, "GetMaskColor"),
                     AccessTools.DeclaredMethod(generator, "GetBiome", new[] { typeof(float), typeof(float), typeof(float), typeof(bool) }),
                     AccessTools.DeclaredMethod(generator, "GetBiomeHeight")
                 })
        {
            var decoded = (System.Collections.IList?)readBody.Invoke(null, new object[] { sampled! });
            Check(decoded != null && decoded.Count > 5, "The key reader decodes " + sampled!.Name + ".");
        }

        // The IL closure the key hashes must actually reach the generation code.
        var closure = module.GetMethod("Closure", privateStatic)!;
        var methods = (System.Collections.IEnumerable?)closure.Invoke(null, Array.Empty<object>());
        if (methods == null)
        {
            // The game targets netstandard2.1; this .NET Framework verifier cannot describe
            // every reachable body (ReadOnlySpan). Unity's runtime can, and the production
            // code fails closed when it cannot.
            Console.WriteLine("STATIC ONLY minimap cache closure: offline CLR cannot read every reachable body; key computation unverified here");
        }
        else
        {
            var names = methods.Cast<MethodBase>()
                .Select(m => (m.DeclaringType?.FullName ?? "?") + "::" + m.Name).ToList();
            Check(names.Count > 10 && names.Count <= 4096, "The closure is non-trivial and bounded: " + names.Count + " methods.");
            foreach (string required in new[] { "Minimap::GenerateWorldMap", "Minimap::GetPixelColor", "Minimap::GetMaskColor",
                         "WorldGenerator::GetBiome", "WorldGenerator::GetBiomeHeight" })
                Check(names.Contains(required), "The key hashes " + required + ".");
            Check(names.Count == methods.Cast<MethodBase>().Distinct().Count(), "The closure visits each method once.");
            Console.WriteLine("PASS minimap cache closure covers " + names.Count + " generation methods");
        }

        // Default configuration installs nothing and patches nothing.
        var config = new ConfigFile(Path.Combine(Path.GetTempPath(), "bp-minimap-" + Guid.NewGuid().ToString("N") + ".cfg"), false)
        { SaveOnConfigSet = false };
        module.GetMethod("Install", privateStatic)!.Invoke(null, new object[] { config, new BepInEx.Logging.ManualLogSource("MinimapVerification") });
        Check(!(bool)module.GetProperty("Installed", privateStatic)!.GetValue(null)!, "Default configuration installs no patch.");
        Check(!(bool)module.GetProperty("Enabled", privateStatic)!.GetValue(null)!, "Default configuration stays disabled.");
        // The shared verifier process leaves residual Harmony ownership from earlier
        // suites, so assert on our own identifier rather than on an empty owner set.
        var patchInfo = Harmony.GetPatchInfo(generate!);
        Check(patchInfo == null || !patchInfo.Owners.Any(owner => owner.Contains("MinimapTextureCache")),
            "Default configuration leaves GenerateWorldMap unpatched by this module.");
        Check((string)module.GetProperty("Status", privateStatic)!.GetValue(null)! == "disabled_at_startup",
            "Status reports the default off state.");
        // The plugin's own WorldMapGenerate timing probe patches the same method under the
        // main plugin id; it must be accepted, and nothing else may be.
        var accepted = (string[])module.GetField("AcceptedOwners", privateStatic)!.GetValue(null)!;
        Check(accepted.Length == 2 && accepted.Contains("jf10r.BetterPerformance.MinimapTextureCache") &&
            accepted.Contains("jf10r.BetterPerformance"), "The accepted GenerateWorldMap owners are exactly the module and the plugin itself.");

        var enabledEntry = config.Bind("MinimapCache", "Enabled", false);
        var modeEntry = config.Bind("MinimapCache", "Mode", "shadow");
        Check(!enabledEntry.Value && modeEntry.Value == "shadow", "Repository defaults are off and shadow-only.");

        Console.WriteLine("PASS " + checks + " minimap texture cache checks; no Unity object created, no game method invoked");
        return checks;
    }
}
