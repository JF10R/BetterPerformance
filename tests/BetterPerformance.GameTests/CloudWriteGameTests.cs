using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Mono.Cecil;
using Cil = Mono.Cecil.Cil;

// Splatform.Steam.SteamCloud implements interfaces that carry default method bodies, which
// the .NET Framework verifier cannot load ("Non-abstract, non-.cctor method in an
// interface"). The real method is therefore read from the shipped assembly through
// Mono.Cecil metadata instead of reflection, and its instruction stream is converted into
// the exact CodeInstruction list Harmony would hand the transpiler in Unity's Mono.
internal static class CloudWriteGameTests
{
    internal static int Run(Assembly game, Assembly plugin, string managedDirectory)
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("CloudWrite: " + message);
            checks++;
        }

        Type module = plugin.GetType("BetterPerformance.CloudWriteOptimization", true)!;
        var validate = AccessTools.DeclaredMethod(module, "Validate");
        var transpile = AccessTools.DeclaredMethod(module, "Transpile");
        var allocate = AccessTools.DeclaredMethod(module, "AllocateChunkBuffer");
        var signature = AccessTools.DeclaredMethod(module, "Signature");
        var install = AccessTools.DeclaredMethod(module, "Install");
        var resolve = AccessTools.DeclaredMethod(module, "Resolve");
        var sample = AccessTools.DeclaredMethod(module, "Sample");
        var uninstall = AccessTools.DeclaredMethod(module, "Uninstall");
        var enabled = AccessTools.DeclaredPropertyGetter(module, "Enabled");
        var installed = AccessTools.DeclaredPropertyGetter(module, "Installed");
        var status = AccessTools.DeclaredPropertyGetter(module, "Status");
        Check(validate != null && transpile != null && allocate != null && signature != null && install != null &&
            resolve != null && sample != null && uninstall != null && enabled != null && installed != null && status != null,
            "module exposes the documented API");

        string steamPath = Path.Combine(managedDirectory, "Splatform.Steam.dll");
        if (!File.Exists(steamPath))
        {
            // The dedicated server ships no Steam cloud backend. Installation must be inert.
            object?[] arguments = { Configuration(), new ManualLogSource("CloudWriteVerification") };
            install!.Invoke(null, arguments);
            Check(!(bool)installed!.Invoke(null, null)!, "server install leaves the optimization uninstalled");
            Check(!(bool)enabled!.Invoke(null, null)!, "server install leaves the optimization disabled");
            Check((string)status!.Invoke(null, null)! == "type_unavailable", "server install reports type_unavailable");
            Check(resolve!.Invoke(null, new object?[] { null }) == null, "server has no Steam cloud writer to resolve");
            uninstall!.Invoke(null, null);
            string cleared = (string)status.Invoke(null, null)!;
            Check(cleared == "disabled" || cleared.StartsWith("unpatch_failed:"), "server uninstall reports its outcome (actual=" + cleared + ")");
            Console.WriteLine("Cloud write optimization: " + checks + " checks; no Steam cloud backend on this installation.");
            return checks;
        }

        Assembly steamworks = Assembly.LoadFrom(Path.Combine(managedDirectory, "com.rlabrecque.steamworks.net.dll"));
        MethodInfo chunkWrite = steamworks.GetType("Steamworks.SteamRemoteStorage", true)!
            .GetMethods(BindingFlags.Public | BindingFlags.Static).Single(m => m.Name == "FileWriteStreamWriteChunk");
        MethodInfo arrayCopy = AccessTools.Method(typeof(Array), "Copy",
            new[] { typeof(Array), typeof(int), typeof(Array), typeof(int), typeof(int) })!;

        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(managedDirectory);
        using var steam = ModuleDefinition.ReadModule(steamPath, new ReaderParameters { AssemblyResolver = resolver });
        TypeDefinition cloud = steam.GetType("Splatform.Steam.SteamCloud")
            ?? throw new InvalidOperationException("CloudWrite: Splatform.Steam.SteamCloud is missing.");
        MethodDefinition writeFile = cloud.Methods.Single(m => m.Name == "WriteFile");
        Check(!writeFile.IsStatic && writeFile.ReturnType.FullName == "System.Boolean" && writeFile.Parameters.Count == 4 &&
            writeFile.Parameters[0].ParameterType.FullName == "System.String" &&
            writeFile.Parameters[1].ParameterType.FullName == "System.Byte[]" && writeFile.Parameters[1].Name == "data" &&
            writeFile.Parameters[2].ParameterType.Resolve().IsEnum &&
            writeFile.Parameters[3].ParameterType.FullName == "System.Boolean",
            "installed assembly has the expected WriteFile signature");
        MethodInfo stand = typeof(Replica).GetMethod("WriteFile", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Check((bool)signature!.Invoke(null, new object[] { stand })!, "signature contract accepts the verified shape");
        Check(!(bool)signature.Invoke(null, new object[] { typeof(Replica).GetMethod("Static", BindingFlags.Static | BindingFlags.NonPublic)! })!,
            "signature contract rejects a static writer");

        var generator = new DynamicMethod("cloud-write-labels", typeof(void), Type.EmptyTypes).GetILGenerator();
        List<CodeInstruction> Original() => Convert(writeFile, generator, arrayCopy, chunkWrite);
        var original = Original();
        Check(original.Count(i => i.opcode == OpCodes.Newarr) == 1, "the installed writer allocates exactly one array");
        Check(original.Any(i => i.opcode == OpCodes.Ldc_I4 && Equals(i.operand, 104857600)), "the installed writer carries the 100 MiB constant");

        int allocation = (int)validate!.Invoke(null, new object[] { original, stand })!;
        Check(original[allocation].opcode == OpCodes.Newarr && Equals(original[allocation].operand, typeof(byte)),
            "contract validation points at the byte[] chunk allocation");

        List<CodeInstruction> Apply(List<CodeInstruction> code) =>
            ((IEnumerable<CodeInstruction>)transpile!.Invoke(null, new object[] { code, stand })!).ToList();
        var patched = Apply(original);
        Check(patched.Count == original.Count + 1, "transpiler adds exactly one instruction");
        for (int i = 0; i < allocation; i++) Check(ReferenceEquals(patched[i], original[i]), "instruction before the allocation retained " + i);
        for (int i = allocation + 1; i < original.Count; i++)
            Check(ReferenceEquals(patched[i + 1], original[i]), "instruction after the allocation retained " + i);
        Check(patched[allocation].opcode == OpCodes.Ldarg_2 && patched[allocation].labels.Count == 0 && patched[allocation].blocks.Count == 0,
            "the payload is pushed from the verified argument without branch or exception metadata");
        Check(patched[allocation + 1].opcode == OpCodes.Call && Equals(patched[allocation + 1].operand, allocate),
            "the allocation is replaced by the sized-buffer helper");
        Check(patched[allocation + 1].labels.SequenceEqual(original[allocation].labels) &&
            patched[allocation + 1].blocks.SequenceEqual(original[allocation].blocks),
            "the replacement keeps the original branch and exception metadata");
        Check(!patched.Any(i => i.opcode == OpCodes.Newarr), "no fixed-size allocation remains");
        Check(original.Count(i => i.opcode == OpCodes.Newarr) == 1 && original[allocation].opcode == OpCodes.Newarr,
            "the transpiler does not mutate the instructions it was given");

        void Rejects(string message, Action<List<CodeInstruction>> mutate)
        {
            var code = Original();
            mutate(code);
            try { validate.Invoke(null, new object[] { code, stand }); }
            catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException) { checks++; return; }
            throw new InvalidOperationException("CloudWrite: accepted " + message);
        }
        int Find(List<CodeInstruction> code, OpCode opcode) => code.FindIndex(i => i.opcode == opcode);
        Rejects("a changed chunk-size constant", code => code[Find(code, OpCodes.Ldc_I4)].operand = 104857601);
        Rejects("a missing chunk allocation", code => code.RemoveAt(Find(code, OpCodes.Newarr)));
        Rejects("a second chunk allocation", code => code.Insert(Find(code, OpCodes.Newarr), new CodeInstruction(OpCodes.Newarr, typeof(byte))));
        Rejects("a buffer that escapes the copy and write sites", code =>
            code.Insert(code.Count - 1, new CodeInstruction(OpCodes.Ldloc_2)));
        Rejects("an address taken of the chunk buffer", code =>
            code.Insert(code.Count - 1, new CodeInstruction(OpCodes.Ldloca_S, 2)));
        Rejects("a changed last-chunk length computation", code => code[Find(code, OpCodes.Rem)].opcode = OpCodes.Div);
        Rejects("a changed chunk-count computation", code => code[Find(code, OpCodes.Div)].opcode = OpCodes.Rem);
        Rejects("a chunk loop without its back edge", code => code[code.FindLastIndex(i => i.opcode == OpCodes.Blt_S)].opcode = OpCodes.Br_S);
        Rejects("a reassigned chunk-size local", code => code.Insert(code.Count - 1, new CodeInstruction(OpCodes.Stloc_0)));
        try
        {
            validate.Invoke(null, new object[] { Original(),
                typeof(Replica).GetMethod("Static", BindingFlags.Static | BindingFlags.NonPublic)! });
            throw new InvalidOperationException("CloudWrite: accepted an unverified writer signature");
        }
        catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException) { checks++; }

        // The helper is the only behavioural change; prove its sizing against the native rule.
        var savedEnabled = AccessTools.DeclaredPropertySetter(module, "Enabled")!;
        bool previous = (bool)enabled!.Invoke(null, null)!;
        try
        {
            savedEnabled.Invoke(null, new object[] { false });
            Check(((byte[])allocate!.Invoke(null, new object[] { 104857600, new byte[40658] })!).Length == 104857600,
                "the disabled helper keeps the native allocation size");
            savedEnabled.Invoke(null, new object[] { true });
            Check(((byte[])allocate.Invoke(null, new object[] { 104857600, new byte[40658] })!).Length == 40658,
                "the enabled helper sizes the buffer to the payload");
            Check(((byte[])allocate.Invoke(null, new object[] { 64, new byte[200] })!).Length == 64,
                "a payload above one chunk keeps a whole chunk");
            Check(((byte[])allocate.Invoke(null, new object?[] { 64, null })!).Length == 64,
                "a missing payload keeps the native allocation size");
        }
        finally { savedEnabled.Invoke(null, new object[] { previous }); }

        // Default configuration must install no IL transform. The Steam backend type cannot
        // be loaded offline, so this asserts the same inert outcome the server reports.
        object?[] installArguments = { Configuration(), new ManualLogSource("CloudWriteVerification") };
        install!.Invoke(null, installArguments);
        Check(!(bool)installed!.Invoke(null, null)!, "default configuration installs no optimization");
        Check(!(bool)enabled.Invoke(null, null)!, "default configuration keeps the optimization disabled");
        string reported = (string)status!.Invoke(null, null)!;
        Check(reported == "type_unavailable" || reported == "disabled", "default configuration reports an inert status (actual=" + reported + ")");
        var gauges = Collection(plugin, "BetterPerformance.Core.NumberValue");
        var labels = Collection(plugin, "BetterPerformance.Core.TextValue");
        sample!.Invoke(null, new object[] { gauges, labels });
        foreach (string name in new[] { "cloud_writes", "cloud_write_bytes", "cloud_write_max_bytes",
            "cloud_write_elapsed_sum", "cloud_write_elapsed_max" })
            Check(Names(gauges).Contains(name), "gauge " + name + " is exported");
        foreach (string name in new[] { "cloud_write_optimization_status", "cloud_write_scope" })
            Check(Names(labels).Contains(name), "label " + name + " is exported");
        Check(Text(labels, "cloud_write_scope") ==
            "steam_remote_storage_only; bytes_are_payload_not_quota; no_file_names_exported", "scope label states the measurement bounds");
        uninstall!.Invoke(null, null);
        Check(!(bool)installed.Invoke(null, null)! && !(bool)enabled.Invoke(null, null)!, "uninstall clears the module state");
        // A standalone CLR can fail to load a Unity type while Harmony walks its patches.
        // Uninstall must report that, not throw it.
        string removed = (string)status.Invoke(null, null)!;
        Check(removed == "disabled" || removed.StartsWith("unpatch_failed:"), "uninstall reports its outcome (actual=" + removed + ")");

        Console.WriteLine("Cloud write optimization: " + checks +
            " checks against the installed Splatform.Steam.dll; no Steam call, save or game method invoked.");
        return checks;
    }

    private static System.Collections.IList Collection(Assembly plugin, string typeName) =>
        (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(plugin.GetType(typeName, true)!))!;

    private static string[] Names(System.Collections.IList values) => values.Cast<object>()
        .Select(value => (string)value.GetType().GetProperty("Name")!.GetValue(value)!).ToArray();

    private static string Text(System.Collections.IList values, string name) => values.Cast<object>()
        .Where(value => (string)value.GetType().GetProperty("Name")!.GetValue(value)! == name)
        .Select(value => (string)value.GetType().GetProperty("Value")!.GetValue(value)!).Single();

    private static ConfigFile Configuration() =>
        new ConfigFile(Path.Combine(Path.GetTempPath(), "bp-cloud-write-" + Guid.NewGuid().ToString("N") + ".cfg"), false) { SaveOnConfigSet = false };

    // Cecil instruction stream to the CodeInstruction list Harmony produces at runtime.
    // Only the two call targets the contract inspects are resolved to reflection methods;
    // every other operand stays as metadata, which the contract never dereferences.
    private static List<CodeInstruction> Convert(MethodDefinition method, ILGenerator generator, MethodInfo copy, MethodInfo chunkWrite)
    {
        var opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(field => (OpCode)field.GetValue(null)!).ToDictionary(opcode => opcode.Name!, opcode => opcode);
        var source = method.Body.Instructions.ToList();
        var result = source.Select(instruction => new CodeInstruction(opcodes[instruction.OpCode.Name])).ToList();
        var labels = new Dictionary<int, Label>();
        for (int i = 0; i < source.Count; i++)
        {
            switch (source[i].Operand)
            {
                case Cil.Instruction target:
                    int at = source.IndexOf(target);
                    if (!labels.TryGetValue(at, out Label label))
                    {
                        label = generator.DefineLabel();
                        labels[at] = label;
                        result[at].labels.Add(label);
                    }
                    result[i].operand = label;
                    break;
                case Cil.VariableDefinition variable:
                    result[i].operand = variable.Index;
                    break;
                case TypeReference type:
                    result[i].operand = type.FullName == "System.Byte" ? typeof(byte) : null;
                    break;
                case MethodReference reference:
                    result[i].operand = reference.Name == "Copy" && reference.DeclaringType.FullName == "System.Array" ? copy
                        : reference.Name == "FileWriteStreamWriteChunk" ? chunkWrite : null;
                    break;
                case object operand:
                    result[i].operand = operand;
                    break;
            }
        }
        return result;
    }

    private enum Grouping { None }

    private sealed class Replica
    {
        private bool WriteFile(string filePath, byte[] data, Grouping grouping, bool allowOverwrite = false) =>
            filePath.Length + data.Length + (int)grouping > 0 && allowOverwrite;
        private static bool Static(string filePath, byte[] data, Grouping grouping, bool allowOverwrite = false) => false;
    }
}
