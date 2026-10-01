using System.Reflection;
using System.Text;
using Mono.Cecil;
using Cil = Mono.Cecil.Cil;

// System resource telemetry reads Unity SystemInfo, Screen and Time, and asks the engine for
// graphics-memory profiler counters by name. All read from metadata and the shipped player binary.
internal static class SystemResourceGameTests
{
    // Counter names EngineTelemetry requests, and the graphics-memory names it cannot request.
    private static readonly string[] Requested = { "Video Memory Bytes", "Render Textures Bytes", "Render Textures Count",
        "Used Buffers Bytes", "Used Buffers Count" };
    private static readonly string[] AbsentFromRelease = { "Gfx Used Memory", "Gfx Reserved Memory", "Texture Memory", "Mesh Memory" };

    internal static int Run(Assembly game, Assembly plugin, string managed)
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("System resource telemetry: " + message);
            checks++;
        }

        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(managed);
        var parameters = new ReaderParameters { AssemblyResolver = resolver };
        using var core = ModuleDefinition.ReadModule(Path.Combine(managed, "UnityEngine.CoreModule.dll"), parameters);
        using var pluginModule = ModuleDefinition.ReadModule(plugin.Location, parameters);
        using var gameModule = ModuleDefinition.ReadModule(game.Location, parameters);

        TypeDefinition Require(ModuleDefinition module, string name) => module.GetType(name)
            ?? throw new InvalidOperationException("System resource telemetry: " + name + " is missing.");
        void Property(TypeDefinition type, string name, string returnType, bool isStatic)
        {
            PropertyDefinition? property = type.Properties.SingleOrDefault(p => p.Name == name);
            Check(property?.GetMethod != null && property.GetMethod.IsPublic && property.GetMethod.IsStatic == isStatic &&
                property.PropertyType.FullName == returnType, type.Name + "." + name + " is a public " +
                (isStatic ? "static " : "") + returnType + " getter");
        }

        // 1. Static hardware labels and the DXGI adapter match.
        TypeDefinition systemInfo = Require(core, "UnityEngine.SystemInfo");
        foreach (string name in new[] { "graphicsDeviceName", "graphicsDeviceVendor", "graphicsDeviceVersion", "processorType" })
            Property(systemInfo, name, "System.String", true);
        foreach (string name in new[] { "graphicsMemorySize", "graphicsDeviceVendorID", "graphicsDeviceID", "processorFrequency" })
            Property(systemInfo, name, "System.Int32", true);
        Property(systemInfo, "graphicsDeviceType", "UnityEngine.Rendering.GraphicsDeviceType", true);
        Check(Require(core, "UnityEngine.Rendering.GraphicsDeviceType").Fields.Any(f => f.Name == "Null"),
            "GraphicsDeviceType.Null marks a headless process");

        // 2. Display mode and uptime.
        Property(Require(core, "UnityEngine.Screen"), "currentResolution", "UnityEngine.Resolution", true);
        TypeDefinition resolution = Require(core, "UnityEngine.Resolution");
        Property(resolution, "width", "System.Int32", false);
        Property(resolution, "height", "System.Int32", false);
        Property(resolution, "refreshRateRatio", "UnityEngine.RefreshRate", false);
        Property(Require(core, "UnityEngine.RefreshRate"), "value", "System.Double", false);
        Property(Require(core, "UnityEngine.Time"), "realtimeSinceStartupAsDouble", "System.Double", true);
        TypeDefinition net = Require(gameModule, "ZNet");
        Check(net.Fields.Any(f => f.Name == "instance" && f.IsStatic && f.FieldType.FullName == "ZNet") ||
            net.Properties.Any(p => p.Name == "instance" && p.GetMethod?.IsStatic == true),
            "ZNet.instance identifies the world session");

        // 3. The plugin wires the module into install, capture start, poll and shutdown.
        TypeDefinition telemetry = Require(pluginModule, "BetterPerformance.SystemTelemetry");
        TypeDefinition pluginType = Require(pluginModule, "BetterPerformance.Plugin");
        bool Calls(string caller, string callee) => pluginType.Methods.Where(m => m.Name == caller && m.HasBody)
            .SelectMany(m => m.Body.Instructions).Any(i => i.OpCode == Cil.OpCodes.Call && i.Operand is MethodReference called &&
                called.DeclaringType.FullName == telemetry.FullName && called.Name == callee);
        Check(Calls("Awake", "Install"), "Plugin.Awake installs SystemTelemetry");
        Check(Calls("StartCapture", "StartLabels"), "Plugin.StartCapture records the start labels");
        Check(Calls("Sample", "Sample"), "Plugin.Sample polls SystemTelemetry");
        Check(Calls("OnDestroy", "Uninstall"), "Plugin.OnDestroy releases the DXGI adapter");

        // 4. Every requested counter name ships in the player; the Gfx family does not (the
        // release player omits it). If one appears after an update, request it in EngineTelemetry.
        string player = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(managed))!, "UnityPlayer.dll");
        Check(File.Exists(player), "UnityPlayer.dll sits beside the data directory");
        byte[] binary = File.ReadAllBytes(player);
        var literals = new HashSet<string>(Require(pluginModule, "BetterPerformance.EngineTelemetry").Methods
            .Where(m => m.HasBody).SelectMany(m => m.Body.Instructions)
            .Where(i => i.OpCode == Cil.OpCodes.Ldstr).Select(i => (string)i.Operand));
        foreach (string name in Requested)
        {
            Check(literals.Contains(name), "EngineTelemetry requests \"" + name + "\"");
            Check(Contains(binary, Encoding.ASCII.GetBytes(name)), "\"" + name + "\" is in the shipped UnityPlayer.dll");
        }
        foreach (string name in AbsentFromRelease)
            Check(!Contains(binary, Encoding.ASCII.GetBytes(name)),
                "\"" + name + "\" is now in UnityPlayer.dll: request it in EngineTelemetry and update docs/system-resource-telemetry.md");
        Console.WriteLine("PASS System resources: " + checks + " checks from metadata and UnityPlayer.dll; native reads are covered offline");
        return checks;
    }

    private static bool Contains(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && haystack[i + j] == needle[j]) j++;
            if (j == needle.Length) return true;
        }
        return false;
    }
}
