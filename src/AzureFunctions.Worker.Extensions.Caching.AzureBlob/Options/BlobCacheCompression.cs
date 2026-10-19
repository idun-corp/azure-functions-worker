namespace Microsoft.Extensions.Caching.Distributed;

/// <summary>
/// Compression applied to cache entry payloads before they are uploaded.
/// </summary>
public enum BlobCacheCompression
{
    /// <summary>
    /// Payloads are stored verbatim.
    /// </summary>
    None = 0,

    /// <summary>
    /// Payloads at or above the configured threshold are compressed with Brotli.
    /// </summary>
    Brotli = 1,

    /// <summary>
    /// Payloads at or above the configured threshold are compressed with GZip.
    /// </summary>
    GZip = 2,
}
