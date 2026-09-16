using System;
using System.IO;

namespace BetterPerformance.Core
{
    public enum PackageCopyOutcome { Copied, UnsupportedStream, ClosedStream, HiddenBuffer, AliasedBuffer }

    public static class PackageCopy
    {
        // The caller must exclusively own both packages for this synchronous operation.
        // Returns a fallback reason BEFORE any write. Exceptions after acceptance propagate;
        // retrying native serialization after writing the length would duplicate bytes.
        public static PackageCopyOutcome TryWrite(BinaryWriter? destinationWriter, MemoryStream? destination,
            BinaryWriter? sourceWriter, MemoryStream? source, out int copiedBytes)
        {
            copiedBytes = 0;
            if (destinationWriter == null || sourceWriter == null || destination == null || source == null ||
                destinationWriter.GetType() != typeof(BinaryWriter) || sourceWriter.GetType() != typeof(BinaryWriter) ||
                destination.GetType() != typeof(MemoryStream) || source.GetType() != typeof(MemoryStream))
                return PackageCopyOutcome.UnsupportedStream;
            if (!source.CanRead || !destination.CanWrite) return PackageCopyOutcome.ClosedStream;
            if (!ReferenceEquals(destinationWriter.BaseStream, destination) || !ReferenceEquals(sourceWriter.BaseStream, source))
                return PackageCopyOutcome.UnsupportedStream;
            if (!source.TryGetBuffer(out var sourceBuffer) || !destination.TryGetBuffer(out var destinationBuffer))
                return PackageCopyOutcome.HiddenBuffer;
            if (ReferenceEquals(source, destination) || ReferenceEquals(sourceBuffer.Array, destinationBuffer.Array))
                return PackageCopyOutcome.AliasedBuffer;

            // This is the verified GetArray contract, except the final ToArray allocation.
            // Neither Flush nor TryGetBuffer changes MemoryStream.Position.
            sourceWriter.Flush();
            source.Flush();
            int length = sourceBuffer.Count;
            destinationWriter.Write(length);
            destinationWriter.Write(sourceBuffer.Array!, sourceBuffer.Offset, length);
            copiedBytes = length;
            return PackageCopyOutcome.Copied;
        }
    }
}
