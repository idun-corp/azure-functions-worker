using System.IO;
using System.IO.Compression;
using Microsoft.Extensions.Caching.Distributed;

namespace AzureFunctions.Worker.Extensions.Caching.AzureBlob.Internal;

/// <summary>
/// Compresses and decompresses cache entry payloads.
/// </summary>
/// <remarks>
/// The encoding is recorded in private metadata rather than in the blob's Content-Encoding header.
/// That header invites transparent decompression by HttpClient, AzCopy, Storage Explorer or a CDN,
/// which would make the bytes an entry yields depend on how it was fetched; a private key is read by
/// exactly one piece of code.
/// </remarks>
internal static class BlobCachePayload
{
    /// <summary>
    /// Metadata value marking a Brotli compressed payload.
    /// </summary>
    public const string BrotliEncoding = "br";

    /// <summary>
    /// Metadata value marking a GZip compressed payload.
    /// </summary>
    public const string GZipEncoding = "gzip";

    /// <summary>
    /// Compresses a payload when the options call for it.
    /// </summary>
    /// <param name="payload">Payload to store</param>
    /// <param name="compression">Algorithm to apply</param>
    /// <param name="thresholdBytes">Size at which compression starts being worthwhile</param>
    /// <param name="encoding">Encoding to record in metadata, or null when stored verbatim</param>
    /// <returns>The bytes to upload</returns>
    public static byte[] Compress(
        byte[] payload,
        BlobCacheCompression compression,
        int thresholdBytes,
        out string? encoding)
    {
        if (compression == BlobCacheCompression.None || payload.Length < thresholdBytes)
        {
            encoding = null;

            return payload;
        }

        using var destination = new MemoryStream(payload.Length / 2);

        // Fastest rather than Optimal: the payloads this exists for are JSON, where the extra ratio
        // is small and the extra CPU is billed per GB-second on consumption plans.
        using (var compressor = Create(destination, compression))
        {
            compressor.Write(payload, 0, payload.Length);
        }

        var compressed = destination.ToArray();

        // Compression can inflate. Storing the larger form would cost both space and a decompression
        // on every read, so fall back to the original and record no encoding.
        if (compressed.Length >= payload.Length)
        {
            encoding = null;

            return payload;
        }

        encoding = compression == BlobCacheCompression.GZip ? GZipEncoding : BrotliEncoding;

        return compressed;
    }

    /// <summary>
    /// Restores a stored payload.
    /// </summary>
    /// <param name="stored">Bytes as they were uploaded</param>
    /// <param name="encoding">Encoding recorded in metadata</param>
    /// <param name="length">Payload length before compression, used to size the buffer</param>
    /// <param name="payload">The restored payload</param>
    /// <returns>
    /// Whether the encoding was one this version understands. An unrecognised encoding is a miss
    /// rather than an error, so the default can be changed without stranding existing entries.
    /// </returns>
    public static bool TryDecompress(byte[] stored, string? encoding, long length, out byte[] payload)
    {
        if (string.IsNullOrEmpty(encoding))
        {
            payload = stored;

            return true;
        }

        payload = [];

        if (encoding != BrotliEncoding && encoding != GZipEncoding)
        {
            return false;
        }

        using var source = new MemoryStream(stored, writable: false);
        using var decompressor = encoding == GZipEncoding
            ? new GZipStream(source, CompressionMode.Decompress)
            : (Stream) new BrotliStream(source, CompressionMode.Decompress);

        using var destination = new MemoryStream(length > 0 && length <= int.MaxValue ? (int) length : 0);

        decompressor.CopyTo(destination);

        payload = destination.ToArray();

        return true;
    }

    private static Stream Create(Stream destination, BlobCacheCompression compression)
    {
        return compression == BlobCacheCompression.GZip
            ? new GZipStream(destination, CompressionLevel.Fastest, leaveOpen: true)
            : new BrotliStream(destination, CompressionLevel.Fastest, leaveOpen: true);
    }
}
