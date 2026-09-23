using System;
using System.Collections.Generic;
using BetterPerformance.Core;

// MinimapTextureCache and BiomePointCache used to append "plugin=<version>" to their cache
// key, so every BetterPerformance release invalidated every entry before a verified hit could
// ever be served. This is the pure half of the fix: the composition CacheKeyMaterial uses to
// describe Harmony patches and the mod set instead, with no Harmony or game dependency.
internal static class CacheKeyMaterialTests
{
    public static void Run()
    {
        SamePatchesProduceTheSameMaterial();
        PatchMaterialIsOrderIndependent();
        AChangedPatchFingerprintChangesTheMaterial();
        AChangedOwnerOrPriorityChangesTheMaterial();
        NoPatchesProduceEmptyMaterial();
        ModsExcludeTheNamedGuid();
        ModsAreOrderIndependentAndSorted();
        ExcludingAnAbsentGuidChangesNothing();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static PatchDescriptor Patch(string owner = "other.mod", string kind = "prefix", int priority = 400,
        string declaringType = "WorldGenerator", string name = "GetBiome", string fingerprint = "abc123") =>
        new PatchDescriptor(owner, kind, priority, declaringType, name, fingerprint);

    private static void SamePatchesProduceTheSameMaterial()
    {
        var patches = new[] { Patch(), Patch(owner: "jf10r.BetterPerformance", kind: "postfix") };
        Check(CacheKeyMaterial.DescribePatches(patches) == CacheKeyMaterial.DescribePatches(patches),
            "Identical input must produce identical material.");
    }

    private static void PatchMaterialIsOrderIndependent()
    {
        var a = Patch(owner: "mod.a", priority: 100);
        var b = Patch(owner: "mod.b", priority: 200, kind: "postfix");
        var c = Patch(owner: "mod.a", priority: 100, name: "GetBiomeHeight");
        string forward = CacheKeyMaterial.DescribePatches(new[] { a, b, c });
        string shuffled = CacheKeyMaterial.DescribePatches(new[] { c, a, b });
        Check(forward == shuffled, "The set of patches on a method must key the same regardless of enumeration order.");
    }

    private static void AChangedPatchFingerprintChangesTheMaterial()
    {
        string before = CacheKeyMaterial.DescribePatches(new[] { Patch(fingerprint: "abc123") });
        string after = CacheKeyMaterial.DescribePatches(new[] { Patch(fingerprint: "def456") });
        Check(before != after, "A patch method's changed IL fingerprint must change the material.");
    }

    private static void AChangedOwnerOrPriorityChangesTheMaterial()
    {
        string baseline = CacheKeyMaterial.DescribePatches(new[] { Patch() });
        Check(baseline != CacheKeyMaterial.DescribePatches(new[] { Patch(owner: "different.mod") }),
            "A different owner must change the material.");
        Check(baseline != CacheKeyMaterial.DescribePatches(new[] { Patch(priority: 500) }),
            "A different priority must change the material.");
        Check(baseline != CacheKeyMaterial.DescribePatches(new[] { Patch(kind: "postfix") }),
            "A different patch kind (prefix vs postfix) must change the material.");
    }

    private static void NoPatchesProduceEmptyMaterial()
    {
        Check(CacheKeyMaterial.DescribePatches(null!) == "", "A null patch list must yield empty material, not throw.");
        Check(CacheKeyMaterial.DescribePatches(Array.Empty<PatchDescriptor>()) == "", "No patches must yield empty material.");
    }

    private static void ModsExcludeTheNamedGuid()
    {
        var mods = new[]
        {
            new KeyValuePair<string, string>("jf10r.BetterPerformance", "0.4.13"),
            new KeyValuePair<string, string>("other.mod", "1.0.0")
        };
        string excluded = CacheKeyMaterial.DescribeMods(mods, "jf10r.BetterPerformance");
        Check(!excluded.Contains("jf10r.BetterPerformance"), "The named GUID must not appear in the material.");
        Check(excluded.Contains("other.mod@1.0.0"), "Every other mod's GUID and version must remain.");
        var bumped = new[]
        {
            new KeyValuePair<string, string>("jf10r.BetterPerformance", "0.4.14"),
            new KeyValuePair<string, string>("other.mod", "1.0.0")
        };
        Check(excluded == CacheKeyMaterial.DescribeMods(bumped, "jf10r.BetterPerformance"),
            "A version bump of the excluded plugin alone must not change the mod material.");
        var otherModChanged = new[]
        {
            new KeyValuePair<string, string>("jf10r.BetterPerformance", "0.4.13"),
            new KeyValuePair<string, string>("other.mod", "1.0.1")
        };
        Check(excluded != CacheKeyMaterial.DescribeMods(otherModChanged, "jf10r.BetterPerformance"),
            "A version change on a mod that is not excluded must still change the material.");
    }

    private static void ModsAreOrderIndependentAndSorted()
    {
        var forward = new[]
        {
            new KeyValuePair<string, string>("alpha.mod", "1.0"),
            new KeyValuePair<string, string>("beta.mod", "2.0")
        };
        var reversed = new[]
        {
            new KeyValuePair<string, string>("beta.mod", "2.0"),
            new KeyValuePair<string, string>("alpha.mod", "1.0")
        };
        Check(CacheKeyMaterial.DescribeMods(forward, null!) == CacheKeyMaterial.DescribeMods(reversed, null!),
            "The mod list must key the same regardless of enumeration order.");
    }

    private static void ExcludingAnAbsentGuidChangesNothing()
    {
        var mods = new[] { new KeyValuePair<string, string>("other.mod", "1.0.0") };
        Check(CacheKeyMaterial.DescribeMods(mods, null!) == CacheKeyMaterial.DescribeMods(mods, "jf10r.BetterPerformance"),
            "Excluding a GUID that is not present must not change the material.");
    }
}
