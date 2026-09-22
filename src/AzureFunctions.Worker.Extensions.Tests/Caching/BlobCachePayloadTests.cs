using System.Text;
using AzureFunctions.Worker.Extensions.Caching.AzureBlob.Internal;
using Microsoft.Extensions.Caching.Distributed;

namespace AzureFunctions.Worker.Extensions.Tests.Caching;

public class BlobCachePayloadTests
{
    private const int Threshold = 4096;

    [Theory]
    [InlineData(BlobCacheCompression.Brotli, "br")]
    [InlineData(BlobCacheCompression.GZip, "gzip")]
    public void Compress_RoundTripsACompressiblePayload(BlobCacheCompression compression, string expected)
    {
        var payload = CompressibleJson(64 * 1024);

        var stored = BlobCachePayload.Compress(payload, compression, Threshold, out var encoding);

        Assert.Equal(expected, encoding);
        Assert.True(stored.Length < payload.Length, "the payload did not get smaller");
        Assert.True(BlobCachePayload.TryDecompress(stored, encoding, payload.Length, out var restored));
        Assert.Equal(payload, restored);
    }

    [Fact]
    public void Compress_LeavesPayloadsBelowTheThresholdAlone()
    {
        var payload = new byte[Threshold - 1];
        Array.Fill(payload, (byte) 'a');

        var stored = BlobCachePayload.Compress(payload, BlobCacheCompression.Brotli, Threshold, out var encoding);

        // Highly compressible, but below the threshold, so it is still stored verbatim.
        Assert.Null(encoding);
        Assert.Same(payload, stored);
    }

    [Fact]
    public void Compress_LeavesPayloadsAloneWhenCompressionIsOff()
    {
        var payload = CompressibleJson(64 * 1024);

        var stored = BlobCachePayload.Compress(payload, BlobCacheCompression.None, Threshold, out var encoding);

        Assert.Null(encoding);
        Assert.Same(payload, stored);
    }

    [Fact]
    public void Compress_KeepsTheOriginalWhenCompressionWouldInflateIt()
    {
        // Random bytes are incompressible, so the compressed form is larger than the input. Storing
        // that would cost space and a decompression on every read for nothing.
        var payload = new byte[16 * 1024];
        Random.Shared.NextBytes(payload);

        var stored = BlobCachePayload.Compress(payload, BlobCacheCompression.Brotli, Threshold, out var encoding);

        Assert.Null(encoding);
        Assert.Same(payload, stored);
    }

    [Fact]
    public void TryDecompress_PassesThroughAnUnencodedPayload()
    {
        var payload = Encoding.UTF8.GetBytes("stored verbatim");

        Assert.True(BlobCachePayload.TryDecompress(payload, null, payload.Length, out var restored));
        Assert.Same(payload, restored);
    }

    [Fact]
    public void TryDecompress_RejectsAnEncodingItDoesNotKnow()
    {
        var payload = Encoding.UTF8.GetBytes("stored verbatim");

        // An unrecognised encoding has to read as a miss rather than throw, so that changing the
        // default compression never strands entries written by an earlier version.
        Assert.False(BlobCachePayload.TryDecompress(payload, "zstd", payload.Length, out var restored));
        Assert.Empty(restored);
    }

    [Fact]
    public void Compress_HandlesAMultiMegabytePayload()
    {
        // The size this package exists for: past what an Azure Table entity can hold.
        var payload = CompressibleJson(8 * 1024 * 1024);

        var stored = BlobCachePayload.Compress(payload, BlobCacheCompression.Brotli, Threshold, out var encoding);

        Assert.Equal("br", encoding);
        Assert.True(BlobCachePayload.TryDecompress(stored, encoding, payload.Length, out var restored));
        Assert.Equal(payload.Length, restored.Length);
        Assert.Equal(payload, restored);
    }

    [Fact]
    public void TryDecompress_ToleratesAnUnknownUncompressedLength()
    {
        var payload = CompressibleJson(64 * 1024);
        var stored = BlobCachePayload.Compress(payload, BlobCacheCompression.Brotli, Threshold, out var encoding);

        // Length only sizes the buffer; a zero must not truncate the result.
        Assert.True(BlobCachePayload.TryDecompress(stored, encoding, length: 0, out var restored));
        Assert.Equal(payload, restored);
    }

    private static byte[] CompressibleJson(int approximateSize)
    {
        var builder = new StringBuilder(approximateSize);

        builder.Append("[[");

        while (builder.Length < approximateSize)
        {
            builder.Append("{\"sensorId\":\"3f2504e0-4f89-11d3-9a0c-0305e82c3301\",\"value\":21.5},");
        }

        builder.Append("]]");

        return Encoding.UTF8.GetBytes(builder.ToString());
    }
}
