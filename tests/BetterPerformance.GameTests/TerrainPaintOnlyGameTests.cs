using System.Reflection;
using Mono.Cecil;
using Cil = Mono.Cecil.Cil;

// Game contracts behind TerrainPaintOnlyReload; Mono.Cecil metadata only, never calls the game.
internal static class TerrainPaintOnlyGameTests
{
    internal static int Run(Assembly game, Assembly plugin)
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Terrain paint-only reload: " + message);
            checks++;
        }

        string managed = Path.GetDirectoryName(game.Location)!;
        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(managed);
        using var gameModule = ModuleDefinition.ReadModule(game.Location, new ReaderParameters { AssemblyResolver = resolver });
        TypeDefinition Require(string name) => gameModule.GetType(name)
            ?? throw new InvalidOperationException("Terrain paint-only reload: " + name + " is missing.");
        MethodDefinition Method(TypeDefinition type, string name, params string[] parameters) =>
            type.Methods.SingleOrDefault(m => m.Name == name && m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(parameters))
            ?? throw new InvalidOperationException("Terrain paint-only reload: " + type.Name + "." + name + " is missing.");
        List<Cil.Instruction> Body(MethodDefinition method) => method.Body.Instructions.ToList();
        bool Calls(MethodDefinition method, string declaring, string name) => Body(method).Any(i =>
            (i.OpCode == Cil.OpCodes.Call || i.OpCode == Cil.OpCodes.Callvirt) && i.Operand is MethodReference m && m.Name == name && m.DeclaringType.Name == declaring);
        bool Reads(MethodDefinition method, string field) => Body(method).Any(i => i.Operand is FieldReference f && f.Name == field);
        void Field(TypeDefinition type, string name, string fullType) =>
            Check(type.Fields.Any(f => f.Name == name && !f.IsStatic && f.FieldType.FullName == fullType), type.Name + "." + name + " is " + fullType);

        TypeDefinition comp = Require("TerrainComp"), heightmap = Require("Heightmap");
        Field(comp, "m_modifiedHeight", "System.Boolean[]");
        Field(comp, "m_levelDelta", "System.Single[]");
        Field(comp, "m_smoothDelta", "System.Single[]");
        Field(comp, "m_lastDataRevision", "System.UInt32");
        Field(comp, "m_nview", "ZNetView");
        Field(comp, "m_hmap", "Heightmap");

        // 1. A received edit: CheckLoad -> Load -> m_hmap.Poke() with both defaults (immediate, Full).
        MethodDefinition checkLoad = Method(comp, "CheckLoad");
        var body = Body(checkLoad);
        int load = body.FindIndex(i => i.Operand is MethodReference m && m.Name == "Load");
        int poke = body.FindIndex(i => i.Operand is MethodReference m && m.Name == "Poke" && m.DeclaringType.Name == "Heightmap");
        Check(load >= 0 && poke > load && Reads(checkLoad, "m_lastDataRevision"), "CheckLoad still loads, then pokes its heightmap");
        Check(poke >= 2 && body[poke - 1].OpCode == Cil.OpCodes.Ldc_I4_0 && body[poke - 2].OpCode == Cil.OpCodes.Ldc_I4_0, "CheckLoad's Poke is Poke(0, false)");
        // Load writes the three height arrays the snapshot compares.
        MethodDefinition loadMethod = Method(comp, "Load");
        Check(Reads(loadMethod, "m_modifiedHeight") && Reads(loadMethod, "m_levelDelta") && Reads(loadMethod, "m_smoothDelta"), "Load fills the three height arrays");

        // 2. The editing player already takes the paint-only path for the same kind of edit.
        MethodDefinition doOperation = comp.Methods.Single(m => m.Name == "DoOperation" && m.Parameters.Count == 3);
        Check(Calls(doOperation, "Heightmap", "Poke") && Reads(doOperation, "m_paintCleared") && Reads(doOperation, "m_level") &&
            Reads(doOperation, "m_raise") && Reads(doOperation, "m_smooth"), "DoOperation still pokes paint-only when only paint changed");

        // 3. Paint-only skips exactly the mesh rebuilds, and the render mesh does not depend on paint.
        MethodDefinition pokeMethod = Method(heightmap, "Poke", "System.Int32", "System.Boolean");
        Check(pokeMethod.IsPublic && pokeMethod.Parameters[0].Name == "delayed" && pokeMethod.Parameters[1].Name == "paintOnly", "Heightmap.Poke(int delayed, bool paintOnly)");
        MethodDefinition regenerate = Method(heightmap, "Regenerate");
        Check(Reads(regenerate, "m_regenRequest") && Calls(regenerate, "Heightmap", "RebuildCollisionMesh") && Calls(regenerate, "Heightmap", "RebuildRenderMesh") &&
            Reads(regenerate, "m_paintMask"), "Regenerate rebuilds meshes only for a Full request and always refreshes the paint");
        MethodDefinition render = Method(heightmap, "RebuildRenderMesh"), collision = Method(heightmap, "RebuildCollisionMesh");
        Check(!Reads(render, "m_paintMask") && !Reads(collision, "m_paintMask"), "neither mesh reads the paint mask");

        // 4. The pure comparison.
        Type snapshot = plugin.GetType("BetterPerformance.Core.TerrainHeightSnapshot", true)!;
        var equal = snapshot.GetMethod("Equal", BindingFlags.Public | BindingFlags.Static)!;
        var a = new object[] { new bool[4], new float[4], new float[4], new bool[4], new float[4], new float[4] };
        Check((bool)equal.Invoke(null, a)!, "equal arrays compare equal");
        ((float[])a[4])[2] = 0.5f;
        Check(!(bool)equal.Invoke(null, a)!, "a level change is seen");
        return checks;
    }
}
