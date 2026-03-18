using SmartComponents.LocalEmbeddings;
using McpDocMind.Lite.Constants;

namespace McpDocMind.Lite.Embeddings;

/// <summary>
/// Provides local embedding generation using SmartComponents bge-micro-v2 (384 dims).
/// Model is lazy-loaded on first use to keep MCP startup instant.
/// </summary>
public sealed class EmbeddingService : IDisposable
{
    private readonly Lazy<LocalEmbedder> _embedder = new(() => new LocalEmbedder());

    public float[] GenerateEmbedding(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new float[EmbeddingConstants.VectorDimension];

        var embedding = _embedder.Value.Embed(text);
        return embedding.Values.ToArray();
    }

    /// <summary>
    /// Serializes a float[] embedding to a byte[] for sqlite-vec storage.
    /// sqlite-vec expects little-endian float32 arrays.
    /// </summary>
    public static byte[] ToBlob(float[] embedding)
    {
        var bytes = new byte[embedding.Length * sizeof(float)];
        Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    /// <summary>
    /// Deserializes a byte[] from sqlite-vec back to float[].
    /// </summary>
    public static float[] FromBlob(byte[] blob)
    {
        var floats = new float[blob.Length / sizeof(float)];
        Buffer.BlockCopy(blob, 0, floats, 0, blob.Length);
        return floats;
    }

    public void Dispose()
    {
        if (_embedder.IsValueCreated)
            _embedder.Value.Dispose();
    }
}
