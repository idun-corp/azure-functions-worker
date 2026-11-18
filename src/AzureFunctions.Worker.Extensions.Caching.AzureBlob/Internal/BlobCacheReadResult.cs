using Azure;

namespace AzureFunctions.Worker.Extensions.Caching.AzureBlob.Internal;

/// <summary>
/// A successful read: the blob it came from, its bookkeeping and its decompressed payload.
/// </summary>
/// <param name="Name">Blob name</param>
/// <param name="Entry">Entry metadata</param>
/// <param name="Payload">Decompressed payload</param>
/// <param name="ETag">ETag the blob carried when it was read</param>
internal sealed record BlobCacheReadResult(string Name, BlobCacheEntry Entry, byte[] Payload, ETag ETag);
