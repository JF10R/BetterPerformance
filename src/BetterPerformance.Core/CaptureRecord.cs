using System;
using System.Runtime.Serialization;

namespace BetterPerformance.Core
{
    [DataContract]
    public sealed class CaptureRecord
    {
        [DataMember(Name = "schemaVersion", Order = 0)] public int SchemaVersion { get; set; } = 1;
        [DataMember(Name = "kind", Order = 1)] public string Kind { get; set; } = "";
        [DataMember(Name = "captureId", Order = 2)] public string CaptureId { get; set; } = "";
        [DataMember(Name = "utc", Order = 3)] public string Utc { get; set; } = "";
        [DataMember(Name = "elapsedSeconds", Order = 4)] public double ElapsedSeconds { get; set; }
        [DataMember(Name = "intervalSeconds", Order = 5)] public double IntervalSeconds { get; set; }
        [DataMember(Name = "labels", Order = 6)] public TextValue[] Labels { get; set; } = Array.Empty<TextValue>();
        [DataMember(Name = "gauges", Order = 7)] public NumberValue[] Gauges { get; set; } = Array.Empty<NumberValue>();
        [DataMember(Name = "timings", Order = 8)] public TimingSummary[] Timings { get; set; } = Array.Empty<TimingSummary>();
        [DataMember(Name = "slowOperations", Order = 9)] public SlowOperationSummary[] SlowOperations { get; set; } = Array.Empty<SlowOperationSummary>();
        [DataMember(Name = "configurationChanges", Order = 10)] public ConfigurationChange[] ConfigurationChanges { get; set; } = Array.Empty<ConfigurationChange>();
        [DataMember(Name = "attributions", Order = 11)] public AttributionSummary[] Attributions { get; set; } = Array.Empty<AttributionSummary>();
    }

    // One aggregated row of a named attribution group. Key is a prefab or RPC
    // identifier, a "prefab:<hash>"/"hash:<int>" fallback, or the synthetic "other".
    [DataContract]
    public sealed class AttributionSummary
    {
        [DataMember(Name = "group", Order = 0)] public string Group { get; set; } = "";
        [DataMember(Name = "key", Order = 1)] public string Key { get; set; } = "";
        [DataMember(Name = "count", Order = 2)] public long Count { get; set; }
        [DataMember(Name = "sumMs", Order = 3)] public double SumMs { get; set; }
        [DataMember(Name = "maxMs", Order = 4)] public double MaxMs { get; set; }
        [DataMember(Name = "bytes", Order = 5)] public long Bytes { get; set; }
    }

    [DataContract]
    public sealed class TextValue
    {
        public TextValue(string name, string value) { Name = name; Value = value; }
        [DataMember(Name = "name")] public string Name { get; private set; }
        [DataMember(Name = "value")] public string Value { get; private set; }
    }

    [DataContract]
    public sealed class NumberValue
    {
        public NumberValue(string name, double value, string unit) { Name = name; Value = value; Unit = unit; }
        [DataMember(Name = "name")] public string Name { get; private set; }
        [DataMember(Name = "value")] public double Value { get; private set; }
        [DataMember(Name = "unit")] public string Unit { get; private set; }
    }

    public sealed class CaptureClock
    {
        private readonly long origin;
        private readonly long frequency;
        public DateTime StartedUtc { get; }
        public CaptureClock(DateTime startedUtc, long origin, long frequency)
        {
            if (frequency <= 0) throw new ArgumentOutOfRangeException(nameof(frequency));
            StartedUtc = startedUtc;
            this.origin = origin;
            this.frequency = frequency;
        }
        public double ElapsedSeconds(long timestamp) => Math.Max(0, (timestamp - origin) / (double)frequency);
    }
}
