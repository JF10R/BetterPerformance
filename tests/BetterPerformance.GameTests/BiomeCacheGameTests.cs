using System.Reflection;
using Mono.Cecil;
using Cil = Mono.Cecil.Cil;

// Every contract here is read from Mono.Cecil metadata. Generating biome points walks
// 2048x2048 world positions through WorldGenerator, which a standalone CLR cannot run,
// so nothing below is invoked: only the shape the cache module depends on is checked.
internal static class BiomeCacheGameTests
{
    internal static int Run(Assembly game, Assembly plugin)
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Biome cache: " + message);
            checks++;
        }

        string managed = Path.GetDirectoryName(game.Location)!;
        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(managed);
        var parameters = new ReaderParameters { AssemblyResolver = resolver };
        using var gameModule = ModuleDefinition.ReadModule(game.Location, parameters);
        using var pluginModule = ModuleDefinition.ReadModule(plugin.Location, parameters);

        // 1. The one entry point the module skips or follows. Its body is the contract that
        //    makes a cached payload substitutable: same size constant, same two stores.
        TypeDefinition data = gameModule.GetType("AltBiomeWorldData")
            ?? throw new InvalidOperationException("Biome cache: AltBiomeWorldData is missing.");
        MethodDefinition generate = data.Methods.SingleOrDefault(m => m.Name == "GenerateBiomePoints" &&
            m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(new[] { "World" }))
            ?? throw new InvalidOperationException("Biome cache: AltBiomeWorldData.GenerateBiomePoints(World) is missing.");
        Check(generate.IsStatic && generate.IsPublic && generate.ReturnType.FullName == "System.Void",
            "AltBiomeWorldData.GenerateBiomePoints(World) is a public static void method");
        var body = generate.Body.Instructions;
        Check(body.Any(i => i.OpCode.Code == Cil.Code.Ldc_I4 && i.Operand is int size && size == 2048),
            "it still builds the grid with the 2048 constant, so a cached payload has the same extent");
        Check(body.Any(i => i.OpCode == Cil.OpCodes.Stfld && (i.Operand as FieldReference)?.Name == "PointsGenerated"),
            "it marks PointsGenerated, which is what a cached load has to reproduce");
        Check(body.Any(i => i.OpCode == Cil.OpCodes.Stfld && (i.Operand as FieldReference) is FieldReference biomeData &&
                biomeData.Name == "m_biomeData" && biomeData.DeclaringType.FullName == "World"),
            "it publishes the result on World.m_biomeData, the field the module reads back");
        Check(body.Any(i => i.OpCode == Cil.OpCodes.Stfld && (i.Operand as FieldReference) is FieldReference back &&
                back.Name == "m_world" && back.DeclaringType.FullName == "AltBiomeWorldData"),
            "it stores the back reference AltBiomeWorldData.m_world");

        // 2. The native serialization the payload is: the module never interprets it, so
        //    only its two halves being present and symmetric is contract.
        MethodDefinition save = data.Methods.SingleOrDefault(m => m.Name == "Save" &&
            m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(new[] { "System.IO.BinaryWriter" }))
            ?? throw new InvalidOperationException("Biome cache: AltBiomeWorldData.Save(BinaryWriter) is missing.");
        Check(!save.IsStatic && save.IsPublic && save.ReturnType.FullName == "System.Void",
            "AltBiomeWorldData.Save(BinaryWriter) is a public instance void method");
        MethodDefinition load = data.Methods.SingleOrDefault(m => m.Name == "Load" &&
            m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(new[] { "System.IO.BinaryReader", "Version/World" }))
            ?? throw new InvalidOperationException("Biome cache: AltBiomeWorldData.Load(BinaryReader, Version.World) is missing.");
        Check(load.IsStatic && load.IsPublic && load.ReturnType.FullName == "AltBiomeWorldData",
            "AltBiomeWorldData.Load(BinaryReader, Version.World) is a public static factory");
        Check(load.Body.Instructions.Any(i => i.OpCode == Cil.OpCodes.Stfld && (i.Operand as FieldReference)?.Name == "PointsGenerated"),
            "Load marks PointsGenerated itself, so a restored payload needs no second pass");
        Check(data.Fields.Any(f => f.Name == "Size" && f.FieldType.FullName == "System.Int32"),
            "AltBiomeWorldData.Size is the int the payload leads with");

        // 3. One point is one float and one byte. A wider point would change the payload
        //    size without changing any name, which is the drift a key cannot catch.
        TypeDefinition point = gameModule.GetType("BiomePoint")
            ?? throw new InvalidOperationException("Biome cache: BiomePoint is missing.");
        MethodDefinition pointSave = point.Methods.Single(m => m.Name == "Save" && m.Parameters.Count == 1);
        var written = pointSave.Body.Instructions
            .Select(i => (i.Operand as MethodReference))
            .Where(m => m != null && m.Name == "Write" && m.DeclaringType.FullName == "System.IO.BinaryWriter")
            .Select(m => m!.Parameters[0].ParameterType.FullName).ToList();
        Check(written.Count == 2 && written.Contains("System.Single") && written.Contains("System.Byte"),
            "BiomePoint.Save writes exactly one float and one byte");
        MethodDefinition pointLoad = point.Methods.Single(m => m.Name == "Load" && m.IsStatic);
        var read = pointLoad.Body.Instructions
            .Select(i => (i.Operand as MethodReference)?.Name)
            .Where(name => name == "ReadSingle" || name == "ReadByte").ToList();
        Check(pointLoad.ReturnType.FullName == "BiomePoint" && read.Count == 2 &&
            read.Contains("ReadSingle") && read.Contains("ReadByte"),
            "BiomePoint.Load reads back exactly that float and that byte");
        TypeDefinition biomeIndex = gameModule.GetType("Heightmap")!.NestedTypes.SingleOrDefault(t => t.Name == "BiomeIndex")
            ?? throw new InvalidOperationException("Biome cache: Heightmap.BiomeIndex is missing.");
        Check(biomeIndex.IsEnum && biomeIndex.Fields.Single(f => f.Name == "value__").FieldType.FullName == "System.Byte",
            "Heightmap.BiomeIndex is still a byte-wide enum, so one point stays five bytes");

        // 4. The fields the cache key is built from. A key that misses one of these would
        //    serve another world's biome data, which is worse than no cache at all.
        TypeDefinition world = gameModule.GetType("World")
            ?? throw new InvalidOperationException("Biome cache: World is missing.");
        foreach (var (field, type) in new[]
        {
            ("m_seed", "System.Int32"), ("m_uid", "System.Int64"), ("m_worldGenVersion", "System.Int32"),
            ("m_biomeData", "AltBiomeWorldData"), ("m_name", "System.String"),
        })
        {
            FieldDefinition definition = world.Fields.SingleOrDefault(f => f.Name == field)
                ?? throw new InvalidOperationException("Biome cache: World." + field + " is missing.");
            Check(!definition.IsStatic && definition.FieldType.FullName == type, "World." + field + " is an instance " + type);
        }

        // 5. The generator the skipped work would have called. Both are instance methods
        //    reached through the static accessor, so a cached run must leave them untouched.
        TypeDefinition generator = gameModule.GetType("WorldGenerator")
            ?? throw new InvalidOperationException("Biome cache: WorldGenerator is missing.");
        MethodDefinition biome = generator.Methods.SingleOrDefault(m => m.Name == "GetBiome" && m.Parameters.Count >= 2 &&
            m.Parameters[0].ParameterType.FullName == "System.Single" && m.Parameters[1].ParameterType.FullName == "System.Single" &&
            m.Parameters.Skip(2).All(p => p.HasDefault))
            ?? throw new InvalidOperationException("Biome cache: WorldGenerator.GetBiome(float, float, ...) is missing.");
        Check(!biome.IsStatic && biome.ReturnType.FullName == "Heightmap/Biome",
            "WorldGenerator.GetBiome(float, float) is an instance method returning Heightmap.Biome, callable with two arguments");
        MethodDefinition height = generator.Methods.SingleOrDefault(m => m.Name == "GetBiomeHeight" && m.Parameters.Count >= 4 &&
            m.Parameters[0].ParameterType.FullName == "Heightmap/Biome" &&
            m.Parameters[1].ParameterType.FullName == "System.Single" && m.Parameters[2].ParameterType.FullName == "System.Single" &&
            m.Parameters[3].ParameterType.IsByReference && m.Parameters.Skip(4).All(p => p.HasDefault))
            ?? throw new InvalidOperationException("Biome cache: WorldGenerator.GetBiomeHeight(Heightmap.Biome, float, float, out _, ...) is missing.");
        // The fourth argument is an out Color mask, not the out float the module brief
        // assumed; only its being an out parameter is contract for a cached run.
        Check(!height.IsStatic && height.ReturnType.FullName == "System.Single",
            "WorldGenerator.GetBiomeHeight is an instance method returning float, with an out parameter of " +
            height.Parameters[3].ParameterType.FullName);
        Check(generator.Properties.Any(p => p.Name == "instance" && p.GetMethod != null && p.GetMethod.IsStatic &&
                p.PropertyType.FullName == "WorldGenerator"),
            "WorldGenerator.instance is still the static accessor the generation path goes through");

        // 6. The plugin side. The prefix decides whether native generation runs at all, so
        //    it is the one hook here that returns a bool.
        TypeDefinition? module = pluginModule.GetType("BetterPerformance.BiomePointCache");
        if (module == null)
        {
            Console.WriteLine("Biome cache: BetterPerformance.BiomePointCache not yet present in the built plugin; hook shape unchecked.");
        }
        else
        {
            MethodDefinition? before = module.Methods.SingleOrDefault(m => m.Name == "BeforeGenerate" && m.Parameters.Count == 1);
            MethodDefinition? after = module.Methods.SingleOrDefault(m => m.Name == "AfterGenerate" && m.Parameters.Count == 1);
            if (before == null || after == null)
                Console.WriteLine("Biome cache: BiomePointCache.BeforeGenerate/AfterGenerate not yet present; hook shape unchecked.");
            else
            {
                Check(before.IsStatic && before.ReturnType.FullName == "System.Boolean" &&
                    before.Parameters[0].ParameterType.FullName == "World" && !before.Parameters[0].ParameterType.IsByReference,
                    "BiomePointCache.BeforeGenerate(World) is a static bool prefix taking the world by value");
                Check(after.IsStatic && after.ReturnType.FullName == "System.Void" &&
                    after.Parameters[0].ParameterType.FullName == "World" && !after.Parameters[0].ParameterType.IsByReference,
                    "BiomePointCache.AfterGenerate(World) is a static void postfix taking the world by value");
            }
        }

        // The module's own lookups, executed: a signature guessed wrong would otherwise show
        // only as a runtime "unavailable" or a permanent key_failed. A Unity type that cannot
        // load on this CLR is the one tolerated outcome, and it is named.
        Type cache = plugin.GetType("BetterPerformance.BiomePointCache", true)!;
        MethodInfo missing = cache.GetMethod("MissingContract", BindingFlags.Static | BindingFlags.NonPublic)!;
        try
        {
            string? reason = (string?)missing.Invoke(null, null);
            Check(reason == null, "BiomePointCache.MissingContract found every lookup it depends on: " + reason);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is TypeLoadException || exception.InnerException is FileNotFoundException)
        {
            Console.WriteLine("STATIC ONLY biome cache lookups: " + exception.InnerException!.GetType().Name + " on this CLR; the metadata checks above still ran");
        }

        Console.WriteLine("Biome cache: " + checks + " static game-contract checks from metadata; no generation was run.");
        return checks;
    }
}
