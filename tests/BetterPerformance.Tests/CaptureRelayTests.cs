using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BetterPerformance.Core;

internal static class CaptureRelayTests
{
    private const string CaptureName = "20260919T001115449Z-client-3eab30e849974e23978803a5c849e611.jsonl";
    private const string LogName = "20260919T001115449Z-client-3eab30e849974e23978803a5c849e611.log";

    public static void NamesAndIds()
    {
        Check(RelayNames.IsValid(CaptureName, RelayStreamKind.Capture), "a capture file name is valid for the capture kind");
        Check(RelayNames.IsValid(LogName, RelayStreamKind.Log), "a log file name is valid for the log kind");
        Check(!RelayNames.IsValid(CaptureName, RelayStreamKind.Log) && !RelayNames.IsValid(LogName, RelayStreamKind.Capture), "the extension must match the kind");
        Check(RelayNames.IsValid(CaptureName.Replace(".jsonl", "-r3.jsonl"), RelayStreamKind.Capture), "a reconnect suffix is accepted");
        Check(RelayNames.Id(CaptureName) == "3eab30e849974e23978803a5c849e611", "the id is the 32-hex group");
        Check(RelayNames.Id(CaptureName.Replace(".jsonl", "-r12.jsonl")) == "3eab30e849974e23978803a5c849e611", "the id survives a reconnect suffix");
        foreach (string bad in new[] { "", "../x.jsonl", "a/b.jsonl", "20260919T001115449Z-client-3eab30e8.jsonl", CaptureName.ToUpperInvariant(),
            "20260919T001115449Z-Client-3eab30e849974e23978803a5c849e611.jsonl", CaptureName + " ", "C:" + CaptureName, new string('a', 200) })
            Check(!RelayNames.IsValid(bad, RelayStreamKind.Capture) && RelayNames.Id(bad) == "", "rejected: " + bad);
        Check(!RelayNames.IsValid(null, RelayStreamKind.Capture), "null is rejected");
    }

    public static void OutboxSplitsFramesAndBounds()
    {
        var outbox = new RelayOutbox(maxPendingBytes: 100_000, chunkBytes: 1024);
        byte[] record = Encoding.UTF8.GetBytes("{\"kind\":\"interval\",\"gauges\":[" + string.Join(",", Enumerable.Range(0, 400).Select(i => "{\"name\":\"g" + i + "\",\"value\":" + i + "}")) + "]}");
        Check(outbox.Enqueue(CaptureName, RelayStreamKind.Capture, record), "a record is accepted");
        byte[] framed = CompressionFrame.Encode(record);
        Check(!ReferenceEquals(framed, record), "the test record is compressible");
        int expectedChunks = (framed.Length + 1023) / 1024;
        Check(outbox.PendingChunks == expectedChunks && outbox.PendingBytes == framed.Length, "pending counts the framed bytes split at the chunk size");

        var chunks = new List<RelayChunk>();
        while (outbox.TryDequeue(out var chunk)) chunks.Add(chunk);
        Check(chunks.Count == expectedChunks, "every chunk dequeues in order");
        Check(chunks.Select(c => c.Sequence).SequenceEqual(Enumerable.Range(0, expectedChunks)), "sequences are 0..n-1");
        Check(chunks.Take(expectedChunks - 1).All(c => !c.Final && c.Payload.Length == 1024) && chunks.Last().Final, "only the last piece is Final");
        Check(chunks.All(c => c.Name == CaptureName && c.Kind == RelayStreamKind.Capture && !c.End), "each chunk names its stream and kind");
        Check(chunks.SelectMany(c => c.Payload).SequenceEqual(framed), "the pieces concatenate to the frame");
        Check(outbox.PendingBytes == 0, "dequeuing releases the pending bytes");

        // A second message continues the sequence; End follows it and forgets the stream.
        Check(outbox.Enqueue(CaptureName, RelayStreamKind.Capture, Encoding.UTF8.GetBytes("{\"kind\":\"writer_end\"}")), "a short message is accepted");
        outbox.EndStream(CaptureName, RelayStreamKind.Capture);
        Check(outbox.TryDequeue(out var second) && second.Sequence == expectedChunks && second.Final && !second.End, "the next message continues the sequence");
        Check(outbox.TryDequeue(out var end) && end.End && end.Final && end.Sequence == expectedChunks + 1 && end.Payload.Length == 0, "End carries the next sequence and no payload");
        Check(!outbox.TryDequeue(out _), "the queue is then empty");
        Check(outbox.Enqueue(CaptureName, RelayStreamKind.Capture, new byte[] { 1 }) && outbox.TryDequeue(out var restarted) && restarted.Sequence == 0,
            "a stream that ended starts again at 0");

        // Bound: a message that does not fit is dropped whole.
        var tight = new RelayOutbox(maxPendingBytes: 2000, chunkBytes: 1024);
        byte[] noise = new byte[1900];
        new Random(5).NextBytes(noise);
        Check(tight.Enqueue(CaptureName, RelayStreamKind.Capture, noise), "an incompressible message under the bound is queued raw");
        Check(!tight.Enqueue(CaptureName, RelayStreamKind.Capture, noise), "the second one does not fit and is refused");
        var summary = tight.Drain();
        Check(summary.Messages == 1 && summary.DroppedMessages == 1 && summary.DroppedBytes == 1900 && summary.PendingBytes == 1900 && summary.PendingChunks == 2,
            "the drain reports one queued and one dropped message");
        Check(tight.Drain().Messages == 0 && tight.Drain().PendingBytes == 1900, "drain resets counters but not the pending figure");
        Check(!tight.Enqueue("bad name.jsonl", RelayStreamKind.Capture, noise) && !tight.Enqueue(CaptureName, RelayStreamKind.Capture, Array.Empty<byte>()),
            "an invalid name or an empty message is refused");
        tight.Clear();
        Check(tight.PendingBytes == 0 && tight.PendingChunks == 0 && tight.Drain().DroppedBytes == 1900, "Clear drops the queue and counts the bytes");
    }

    public static void LineBatcher()
    {
        var batcher = new RelayLineBatcher(flushBytes: 64, flushSeconds: 1.0, maxBytes: 200);
        Check(batcher.TryTake(0) == null, "an empty batcher yields nothing");
        batcher.Append("[Info   :  BepInEx] one", 10);
        Check(batcher.TryTake(10.5) == null, "a young small batch is held");
        byte[]? aged = batcher.TryTake(11.0);
        Check(aged != null && Encoding.UTF8.GetString(aged) == "[Info   :  BepInEx] one\n", "an old batch is released with a newline appended");
        batcher.Append("line ending kept\n", 20);
        batcher.Append(new string('x', 60), 20);
        byte[]? big = batcher.TryTake(20);
        Check(big != null && Encoding.UTF8.GetString(big) == "line ending kept\n" + new string('x', 60) + "\n", "a batch over the flush size is released at once, lines kept intact");
        batcher.Append(new string('y', 150), 30);
        batcher.Append(new string('z', 100), 30);
        Check(batcher.DroppedLines == 1, "a line past the bound is dropped and counted");
        Check(batcher.TryTake(30, force: true)!.Length == 151, "force releases what fits");
        batcher.Append("", 40);
        Check(batcher.TryTake(40, force: true) == null, "an empty line is ignored");
    }

    public static void SinkReassemblesAndCloses()
    {
        var files = new Dictionary<string, MemoryStream>();
        var quota = new RelayQuota(1_000_000);
        Stream Open(string name, RelayStreamKind kind) { var s = new MemoryStream(); files[name] = s; return s; }
        using var sink = new RelaySink(Open, quota);
        var outbox = new RelayOutbox(1_000_000, chunkBytes: 512);
        byte[] first = Encoding.UTF8.GetBytes("{\"kind\":\"start\",\"pad\":\"" + new string('p', 3000) + "\"}");
        byte[] second = Encoding.UTF8.GetBytes("{\"kind\":\"interval\"}");
        outbox.Enqueue(CaptureName, RelayStreamKind.Capture, first);
        outbox.Enqueue(CaptureName, RelayStreamKind.Capture, second);
        outbox.EndStream(CaptureName, RelayStreamKind.Capture);
        var results = new List<RelayAccept>();
        while (outbox.TryDequeue(out var chunk)) results.Add(sink.Accept(chunk));
        Check(results[0] == RelayAccept.Opened && results.Last() == RelayAccept.Closed && results.Skip(1).Take(results.Count - 2).All(r => r == RelayAccept.Written),
            "open, write, close in order; got " + string.Join(",", results));
        string text = Encoding.UTF8.GetString(files[CaptureName].ToArray());
        Check(text == Encoding.UTF8.GetString(first) + "\n" + Encoding.UTF8.GetString(second) + "\n", "the file is the records, one per line, byte for byte");
        Check(sink.OpenStreams == 0, "the stream is closed after End");
        var summary = sink.Drain();
        Check(summary.Messages == 2 && summary.StreamsOpened == 1 && summary.StreamsClosed == 1 && summary.Rejected == 0 && summary.Failures == 0
            && summary.BytesWritten == first.Length + second.Length + 2, "the drain accounts for both messages");
        Check(quota.Used == first.Length + second.Length + 2, "the quota holds the bytes written");
        Check(sink.Accept(new RelayChunk { Name = CaptureName, Kind = RelayStreamKind.Capture, Sequence = 0, Final = true, Payload = new byte[] { 65 } }) == RelayAccept.RejectedName,
            "a closed stream is never reopened");

        // Log kind: no newline is appended, text lands as sent.
        outbox.Enqueue(LogName, RelayStreamKind.Log, Encoding.UTF8.GetBytes("[Info] a\n[Info] b\n"));
        while (outbox.TryDequeue(out var chunk)) sink.Accept(chunk);
        Check(Encoding.UTF8.GetString(files[LogName].ToArray()) == "[Info] a\n[Info] b\n", "a log batch is appended verbatim");
        Check(sink.OpenStreams == 1, "the log stream stays open until End or CloseAll");
        sink.CloseAll("peer_disconnected");
        string tail = Encoding.UTF8.GetString(files[LogName].ToArray());
        Check(tail.EndsWith("[relay_end reason=peer_disconnected]\n", StringComparison.Ordinal) && sink.OpenStreams == 0, "CloseAll writes the reason and closes: " + tail);
    }

    public static void SinkRefusesBrokenStreams()
    {
        var files = new Dictionary<string, MemoryStream>();
        Stream Open(string name, RelayStreamKind kind) { var s = new MemoryStream(); files[name] = s; return s; }
        RelayChunk Chunk(string name, int seq, byte[] payload, bool final = true, bool end = false) =>
            new RelayChunk { Name = name, Kind = RelayStreamKind.Capture, Sequence = seq, Final = final, End = end, Payload = payload };
        byte[] line = Encoding.UTF8.GetBytes("{\"kind\":\"interval\"}");
        string other = CaptureName.Replace("3eab30e8", "aaaaaaaa");
        string third = CaptureName.Replace("3eab30e8", "bbbbbbbb");

        using (var sink = new RelaySink(Open, new RelayQuota(1_000_000)))
        {
            Check(sink.Accept(Chunk("../evil.jsonl", 0, line)) == RelayAccept.RejectedName && files.Count == 0, "an invalid name opens nothing");
            Check(sink.Accept(Chunk(CaptureName, 3, line)) == RelayAccept.RejectedSequence && files.Count == 0, "a stream must start at 0");
            Check(sink.Accept(Chunk(CaptureName, 0, line)) == RelayAccept.RejectedName, "a name refused once stays refused");
            Check(sink.Accept(Chunk(other, 0, line)) == RelayAccept.Opened, "another stream opens");
            Check(sink.Accept(Chunk(other, 2, line)) == RelayAccept.RejectedSequence, "a gap breaks the stream");
            string text = Encoding.UTF8.GetString(files[other].ToArray());
            Check(text.StartsWith(Encoding.UTF8.GetString(line) + "\n", StringComparison.Ordinal) && text.Contains("\"kind\":\"relay_end\"") && text.Contains("RejectedSequence")
                && text.Contains("\"captureId\":\"aaaaaaaa49974e23978803a5c849e611\"") && text.EndsWith("\n", StringComparison.Ordinal),
                "the broken capture gets a relay_end record with its id and the reason: " + text);
            Check(sink.Accept(Chunk(other, 3, line)) == RelayAccept.RejectedName && sink.OpenStreams == 0, "a broken stream is closed and refused");

            // A corrupt frame: valid header, garbage body.
            byte[] corrupt = CompressionFrame.Encode(Encoding.UTF8.GetBytes(new string('c', 4000)));
            for (int i = 8; i < corrupt.Length; i++) corrupt[i] ^= 0x5A;
            Check(sink.Accept(Chunk(third, 0, corrupt)) == RelayAccept.RejectedCorrupt, "a frame that will not decode breaks the stream");
            Check(Encoding.UTF8.GetString(files[third].ToArray()).Contains("RejectedCorrupt"), "the trailer names the corruption");
            var summary = sink.Drain();
            Check(summary.Rejected == 6 && summary.StreamsOpened == 2 && summary.StreamsClosed == 2, "rejections and closes are counted: " + summary.Rejected + "/" + summary.StreamsOpened + "/" + summary.StreamsClosed);
        }

        // Message size, stream size, stream count and quota bounds.
        files.Clear();
        using (var small = new RelaySink(Open, new RelayQuota(1_000_000), maxMessageBytes: 100, maxStreamBytes: 150, maxStreams: 1))
        {
            Check(small.Accept(Chunk(CaptureName, 0, new byte[60], final: false)) == RelayAccept.Opened, "a partial message is held");
            Check(small.Accept(Chunk(CaptureName, 1, new byte[60], final: true)) == RelayAccept.RejectedSize, "a message over the bound breaks the stream before decode");
            Check(small.Accept(Chunk(other, 0, line)) == RelayAccept.Opened, "the stream slot is free again after the break");
            Check(small.Accept(Chunk(third, 0, line)) == RelayAccept.RejectedStreams, "a second concurrent stream is refused");
            byte[] big = new byte[140];
            new Random(9).NextBytes(big);
            Check(small.Accept(Chunk(other, 1, big)) == RelayAccept.RejectedSize, "a stream over its byte bound is closed");
        }
        files.Clear();
        using (var quotaed = new RelaySink(Open, new RelayQuota(30)))
        {
            Check(quotaed.Accept(Chunk(CaptureName, 0, line)) == RelayAccept.Opened, "the first message fits the quota");
            Check(quotaed.Accept(Chunk(CaptureName, 1, line)) == RelayAccept.RejectedQuota, "the second one exceeds it and closes the stream");
            Check(Encoding.UTF8.GetString(files[CaptureName].ToArray()) == Encoding.UTF8.GetString(line) + "\n", "no trailer is written once the quota is spent");
        }
        files.Clear();
        using (var failing = new RelaySink((n, k) => throw new IOException("disk"), new RelayQuota(1_000_000)))
        {
            Check(failing.Accept(Chunk(CaptureName, 0, line)) == RelayAccept.Failed && failing.Drain().Failures == 1, "an open failure is a Failed result and a counted failure");
        }
    }

    public static void RoundTripMirrorsWriterOutput()
    {
        // The writer's tee feeds the outbox; the sink rebuilds the file; the bytes must match.
        var local = new MemoryStream();
        var outbox = new RelayOutbox(50_000_000);
        var writer = new CaptureWriter(() => local, capacity: 16, maxBytes: 64L * 1024 * 1024, leaveOpen: true,
            onLine: line => outbox.Enqueue(CaptureName, RelayStreamKind.Capture, line));
        for (int i = 0; i < 20; i++)
            writer.TryWrite(new CaptureRecord { Kind = "interval", CaptureId = "3eab30e849974e23978803a5c849e611", Utc = "2026-09-19T00:00:0" + (i % 10) + "Z", ElapsedSeconds = i,
                Gauges = Enumerable.Range(0, 300).Select(g => new NumberValue("gauge_" + g, g * i, "count")).ToArray() });
        Check(writer.Finish(5000), "the writer finishes");
        outbox.EndStream(CaptureName, RelayStreamKind.Capture);
        Check(writer.TeeFailures == 0, "the tee never failed");

        var remote = new MemoryStream();
        using var sink = new RelaySink((n, k) => remote, new RelayQuota(100_000_000));
        long wire = 0;
        while (outbox.TryDequeue(out var chunk))
        {
            wire += chunk.Payload.Length;
            var result = sink.Accept(chunk);
            Check(result == RelayAccept.Opened || result == RelayAccept.Written || result == RelayAccept.Closed, "every chunk is accepted, got " + result);
        }
        byte[] localBytes = local.ToArray(), remoteBytes = remote.ToArray();
        int firstDiff = Enumerable.Range(0, Math.Min(localBytes.Length, remoteBytes.Length)).FirstOrDefault(i => localBytes[i] != remoteBytes[i], -1);
        Check(localBytes.SequenceEqual(remoteBytes), "the mirrored file is byte-identical to the local one, footer included: local=" + localBytes.Length + " remote=" + remoteBytes.Length + " firstDiff=" + firstDiff
            + " localTail=" + Encoding.UTF8.GetString(localBytes, Math.Max(0, localBytes.Length - 120), Math.Min(120, localBytes.Length)).Replace('\n', '|'));
        Check(wire < local.Length / 2, "the wire carried less than half the raw bytes: " + wire + " of " + local.Length);
        Check(Encoding.UTF8.GetString(remote.ToArray()).Contains("\"kind\":\"writer_end\""), "the writer_end record travelled through the tee");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
