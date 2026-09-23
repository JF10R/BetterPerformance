using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace BetterPerformance.Core
{
    // One Harmony patch attached to a method inside a cache key's closure, as the caller
    // resolved it. Never a Harmony type itself, so this stays testable off a game process.
    public readonly struct PatchDescriptor
    {
        public PatchDescriptor(string owner, string kind, int priority, string declaringType, string name, string fingerprint)
        {
            Owner = owner ?? "";
            Kind = kind ?? "";
            Priority = priority;
            DeclaringType = declaringType ?? "";
            Name = name ?? "";
            Fingerprint = fingerprint ?? "";
        }

        public string Owner { get; }
        public string Kind { get; }
        public int Priority { get; }
        public string DeclaringType { get; }
        public string Name { get; }
        public string Fingerprint { get; }
    }

    // Pure composition of the two cache-key fragments MinimapTextureCache and BiomePointCache
    // used a bare plugin version for: the set of Harmony patches on a method, and the loaded
    // mod list with one GUID excluded. Callers resolve identity and fingerprints (Harmony,
    // IlFingerprint) and hand the results here, so ordering and formatting live in one place
    // that does not need a game runtime to test.
    public static class CacheKeyMaterial
    {
        public static string DescribePatches(IEnumerable<PatchDescriptor> patches)
        {
            var sorted = new List<PatchDescriptor>(patches ?? Array.Empty<PatchDescriptor>());
            sorted.Sort((left, right) =>
            {
                int result = string.CompareOrdinal(left.Owner, right.Owner);
                if (result != 0) return result;
                result = string.CompareOrdinal(left.Kind, right.Kind);
                if (result != 0) return result;
                result = string.CompareOrdinal(left.DeclaringType, right.DeclaringType);
                if (result != 0) return result;
                result = string.CompareOrdinal(left.Name, right.Name);
                if (result != 0) return result;
                result = left.Priority.CompareTo(right.Priority);
                return result != 0 ? result : string.CompareOrdinal(left.Fingerprint, right.Fingerprint);
            });
            var text = new StringBuilder();
            foreach (var patch in sorted)
                text.Append(patch.Owner).Append(':').Append(patch.Kind).Append(':').Append(patch.DeclaringType)
                    .Append("::").Append(patch.Name).Append(':')
                    .Append(patch.Priority.ToString(CultureInfo.InvariantCulture)).Append(':')
                    .Append(patch.Fingerprint).Append(';');
            return text.ToString();
        }

        // excludeGuid drops one plugin (normally the caller's own) from the mod set: another
        // mod's GUID@version must still move the key, but a release of this plugin must not.
        public static string DescribeMods(IEnumerable<KeyValuePair<string, string>> guidVersions, string excludeGuid)
        {
            var names = new List<string>();
            if (guidVersions != null)
                foreach (var mod in guidVersions)
                {
                    if (excludeGuid != null && string.Equals(mod.Key, excludeGuid, StringComparison.Ordinal)) continue;
                    names.Add(mod.Key + "@" + (mod.Value ?? "?"));
                }
            names.Sort(StringComparer.Ordinal);
            return string.Join("\n", names.ToArray());
        }
    }
}
