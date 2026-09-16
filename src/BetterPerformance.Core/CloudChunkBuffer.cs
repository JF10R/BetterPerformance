namespace BetterPerformance.Core
{
    // Chunk arithmetic of the native Steam cloud writer. The scratch buffer only ever
    // receives a prefix of the payload, so a payload-sized buffer emits identical chunks.
    public static class CloudChunkBuffer
    {
        public const int NativeChunkSize = 104857600;

        // Degenerate inputs keep the native allocation size so the caller cannot diverge.
        public static int BufferLength(int chunkSize, int payloadLength) =>
            chunkSize <= 0 || payloadLength <= 0 ? chunkSize :
            payloadLength < chunkSize ? payloadLength : chunkSize;

        public static int ChunkCount(int chunkSize, int payloadLength) => payloadLength / chunkSize + 1;

        public static int ChunkLength(int chunkSize, int payloadLength, int index) =>
            index + 1 == ChunkCount(chunkSize, payloadLength) ? payloadLength % chunkSize : chunkSize;
    }
}
