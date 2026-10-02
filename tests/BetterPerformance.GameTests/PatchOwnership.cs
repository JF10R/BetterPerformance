using System.Reflection;
using HarmonyLib;

// What a module's Uninstall must leave behind: no original in the process still patched by its
// Harmony id. Reads the patch registry only, so it holds on a CLR that cannot load the bodies.
internal static class PatchOwnership
{
    internal static string IdOf(Type module) =>
        ((Harmony)module.GetField("Patches", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!).Id;

    // "none", or the originals that still carry a patch owned by id.
    internal static string Retained(string id)
    {
        var retained = Harmony.GetAllPatchedMethods()
            .Where(method => Harmony.GetPatchInfo(method)?.Owners.Contains(id) == true)
            .Select(method => method.DeclaringType?.FullName + "." + method.Name)
            .ToList();
        return retained.Count == 0 ? "none" : string.Join(", ", retained);
    }
}
