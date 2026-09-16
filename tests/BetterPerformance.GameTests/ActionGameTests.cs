using System.Reflection;
using System.Reflection.Emit;

internal static class ActionGameTests
{
    internal static int Run(Assembly game, Assembly plugin)
    {
        try { return Verify(game, plugin); }
        catch (TypeLoadException exception) when (exception.Message.Contains("Non-abstract, non-.cctor method in an interface"))
        {
            Console.WriteLine("UNAVAILABLE Action game-contract checks: standalone .NET Framework cannot load native action interfaces; use modern CLR metadata checks and Unity runtime validation.");
            return 0;
        }
    }

    private static int Verify(Assembly game, Assembly plugin)
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Action telemetry: " + message);
            checks++;
        }
        Type TypeOf(string name) => game.GetType(name, true)!;
        var humanoid = TypeOf("Humanoid"); var drop = TypeOf("ItemDrop"); var item = TypeOf("ItemDrop+ItemData");
        var inventory = TypeOf("Inventory"); var container = TypeOf("Container"); var gui = TypeOf("InventoryGui");
        var pickupMethod = humanoid.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(method => method.Name == "Pickup" && method.GetParameters().Length == 3);
        Check(pickupMethod.ReturnType == typeof(bool) && pickupMethod.GetParameters()[0].ParameterType.Name == "GameObject",
            "direct pickup signature matches installed game");
        var add = Method(inventory, "AddItem", new[] { item });
        Check(add.ReturnType == typeof(bool), "acceptance comes from bool AddItem(ItemData)");
        Check(References(pickupMethod).Count(member => Equals(member, add)) == 1,
            "native direct pickup uses this exact acceptance overload once");
        Check(Method(drop, "Pickup", new[] { humanoid }).ReturnType == typeof(void), "request signature");
        var requestOwn = Method(drop, "RequestOwn", Type.EmptyTypes);
        Check(requestOwn.ReturnType == typeof(void), "ownership request signature");
        Check(References(Method(drop, "Pickup", new[] { humanoid })).Any(member => Equals(member, requestOwn)),
            "ownership attribution hook is a real request-path call");
        Check(Method(container, "Interact", new[] { humanoid, typeof(bool), typeof(bool) }).ReturnType == typeof(bool), "interaction signature");
        var show = Method(gui, "Show", new[] { container, typeof(int) });
        var response = Method(container, "RPC_OpenResponse", new[] { typeof(long), typeof(bool) });
        Check(show.ReturnType == typeof(void) && response.ReturnType == typeof(void), "open confirmation/rejection signatures");
        var current = Field(gui, "m_currentContainer");
        Check(current.FieldType == container && References(show).Any(member => Equals(member, current)),
            "GUI confirmation field is present in native Show");
        Check(Field(humanoid, "m_inventory").FieldType == inventory && Field(drop, "m_itemData").FieldType == item,
            "bound inventory and item identity fields verified");
        Type module = plugin.GetType("BetterPerformance.ActionTelemetry", true)!;
        foreach (string name in new[] { "BeforeRequest", "AfterRequest", "BeforeOwnership", "BeforePickup", "AfterPickup", "AfterAddItem", "BeforeContainer", "AfterShow", "AfterOpenResponse" })
        {
            var observer = Method(module, name);
            Check(observer.ReturnType == typeof(void), name + " cannot skip original/change return or exception");
            Check(observer.GetParameters().All(parameter => parameter.Name == "__state" || !parameter.ParameterType.IsByRef),
                name + " does not mutate game arguments/results");
            var calls = References(observer).OfType<MethodInfo>();
            Check(!calls.Any(method => method.DeclaringType?.Assembly == game &&
                new[] { "InvokeRPC", "Pickup", "RequestOwn", "AddItem", "Show", "Interact" }.Contains(method.Name)),
                name + " observes without initiating game actions");
        }
        Console.WriteLine("Action telemetry: " + checks + " static game-contract checks; outcome hooks require Unity runtime validation.");
        return checks;
    }

    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    private static MethodInfo Method(Type type, string name, Type[]? parameters = null) => parameters == null
        ? type.GetMethod(name, Declared)! : type.GetMethod(name, Declared, null, parameters, null)!;
    private static FieldInfo Field(Type type, string name) => type.GetField(name, Declared)!;
    // Metadata-only reader also runs on modern CLR without initializing the game's
    // older Harmony runtime. No native game method is invoked by these checks.
    private static IEnumerable<MemberInfo> References(MethodInfo method)
    {
        var opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(OpCode)).Select(field => (OpCode)field.GetValue(null)!).ToDictionary(op => op.Value);
        var bytes = method.GetMethodBody()!.GetILAsByteArray()!;
        for (int offset = 0; offset < bytes.Length;)
        {
            int raw = bytes[offset++]; if (raw == 0xfe) raw = 0xfe00 | bytes[offset++];
            var opcode = opcodes[unchecked((short)raw)];
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
                yield return method.Module.ResolveMember(BitConverter.ToInt32(bytes, offset), method.DeclaringType!.GetGenericArguments(), method.GetGenericArguments())!;
            offset += length;
        }
    }
}
