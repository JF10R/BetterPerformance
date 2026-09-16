using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace BetterPerformance.Core
{
    [DataContract]
    public sealed class ConfigurationChange
    {
        [DataMember(Name = "name")] public string Name { get; set; } = "";
        [DataMember(Name = "previous")] public string Previous { get; set; } = "";
        [DataMember(Name = "current")] public string Current { get; set; } = "";
        [DataMember(Name = "elapsedSeconds")] public double ElapsedSeconds { get; set; }
        [DataMember(Name = "source")] public string Source { get; set; } = "";
    }

    // Main-thread only. Store observations, not configuration commands or arbitrary mod values.
    public sealed class ConfigurationTracker
    {
        public const int MaxKeys = 96, MaxChanges = 128;
        private readonly Dictionary<string, string> values = new Dictionary<string, string>();
        private readonly List<ConfigurationChange> pending = new List<ConfigurationChange>();
        public long DroppedChanges { get; private set; }
        public long RejectedKeys { get; private set; }

        public void Observe(string name, string value, double elapsed, string source)
        {
            if (name.Length > 128 || value.Length > 128 || source.Length > 32 ||
                double.IsNaN(elapsed) || double.IsInfinity(elapsed) || elapsed < 0)
                throw new ArgumentException("Configuration observations must be bounded and timed.");
            if (!values.TryGetValue(name, out string previous))
            {
                if (values.Count == MaxKeys) { RejectedKeys++; return; }
                values.Add(name, value);
                return;
            }
            if (previous == value) return;
            values[name] = value;
            if (pending.Count == MaxChanges) { DroppedChanges++; return; }
            pending.Add(new ConfigurationChange { Name = name, Previous = previous, Current = value,
                ElapsedSeconds = elapsed, Source = source });
        }

        public void AppendSnapshot(List<TextValue> labels)
        {
            foreach (var pair in values) labels.Add(new TextValue("config." + pair.Key, pair.Value));
        }

        public ConfigurationChange[] Drain()
        {
            if (pending.Count == 0) return Array.Empty<ConfigurationChange>();
            var changes = pending.ToArray();
            pending.Clear();
            return changes;
        }
    }
}
