using System.Text;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using AzureFunctions.Worker.Extensions.Caching.AzureBlob.Internal;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;

namespace AzureFunctions.Worker.Extensions.Tests.Caching;

/// <summary>
/// The blob cache against real storage.
/// </summary>
/// <remarks>
/// Traited as an integration suite because it needs an Azurite instance, which CI does not have - it
/// runs <c>--filter "Category!=Integration"</c>. Start one with
/// <c>azurite --silent --location &lt;dir&gt; --blobPort 10000 --queuePort 10001 --tablePort 10002</c>
/// and run these locally.
/// <para>
/// What they cover that a mock cannot: that the container is created from the error response rather
/// than eagerly, that a listing pages correctly past the service's maximum, that the blob batch
/// endpoint behaves, and that a concurrent writer never leaves a reader with a torn payload.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public class AzureBlobCacheIntegrationTests
{
    private const string ConnectionString = "UseDevelopmentStorage=true";

    [Fact]
    public async Task CreatesTheContainerFromTheFirstRequestThatFindsItMissing()
    {
        await using var fixture = new Fixture();

        // Concurrent first writes: the container does not exist yet, so every one of them races on
        // the ContainerNotFound handler.
        await Parallel.ForEachAsync(
            Enumerable.Range(0, 16),
            async (index, token) => await fixture.Cache.SetAsync(
                $"key-{index}",
                Encoding.UTF8.GetBytes($"value-{index}"),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10) },
                token));

        Assert.True(await fixture.Container.ExistsAsync());

        for (var index = 0; index < 16; index++)
        {
            Assert.Equal(
                Encoding.UTF8.GetBytes($"value-{index}"),
                await fixture.Cache.GetAsync($"key-{index}"));
        }
    }

    [Fact]
    public async Task RoundTripsAnEntryThroughRealStorage()
    {
        await using var fixture = new Fixture();

        var payload = Encoding.UTF8.GetBytes("round trip");

        await fixture.Cache.SetAsync(
            "round-trip",
            payload,
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10) });

        Assert.Equal(payload, await fixture.Cache.GetAsync("round-trip"));

        await fixture.Cache.RemoveAsync("round-trip");

        Assert.Null(await fixture.Cache.GetAsync("round-trip"));
    }

    [Fact]
    public async Task RoundTripsAMultiMegabytePayload()
    {
        await using var fixture = new Fixture();

        // The size that rules out the Azure Table cache, whose entities stop at 1 MiB.
        var payload = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("{\"value\":21.5},", 400_000)));

        Assert.True(payload.Length > 4 * 1024 * 1024);

        await fixture.Cache.SetAsync(
            "large",
            payload,
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10) });

        Assert.Equal(payload, await fixture.Cache.GetAsync("large"));
    }

    [Fact]
    public async Task DoesNotServeAnEntryPastItsExpiry()
    {
        await using var fixture = new Fixture();

        await fixture.Cache.SetAsync(
            "expiring",
            Encoding.UTF8.GetBytes("stale"),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) });

        Assert.NotNull(await fixture.Cache.GetAsync("expiring"));

        // No sweep has run, so the blob is still there. The read has to decide for itself.
        fixture.Time.UtcNow = fixture.Time.UtcNow.AddMinutes(6);

        Assert.Null(await fixture.Cache.GetAsync("expiring"));
    }

    [Fact]
    public async Task SearchesAndRemovesByPrefix()
    {
        await using var fixture = new Fixture();

        var options = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10) };

        foreach (var key in new[] { "owner|a|1", "owner|a|2", "owner|b|1" })
        {
            await fixture.Cache.SetJsonAsync(key, key, options);
        }

        var matches = await fixture.Cache.SearchJsonWithKeysAsync<string>("owner|a*");

        Assert.Equal(["owner|a|1", "owner|a|2"], matches.Keys.Order());

        await fixture.Cache.RemoveAllAsync("owner|a*");

        Assert.Null(await fixture.Cache.GetAsync("owner|a|1"));
        Assert.NotNull(await fixture.Cache.GetAsync("owner|b|1"));
    }

    [Fact]
    public async Task PurgesAcrossMoreThanOneListingPage()
    {
        await using var fixture = new Fixture();

        // The service returns at most 5000 blobs per page, so this is the smallest count that proves
        // the continuation is followed rather than the first page being mistaken for the whole set.
        const int Count = 5100;

        await Parallel.ForEachAsync(
            Enumerable.Range(0, Count),
            new ParallelOptions { MaxDegreeOfParallelism = 64 },
            async (index, token) => await fixture.Store.SetAsync(
                $"paged-{index}",
                Encoding.UTF8.GetBytes("v"),
                tags: null,
                TimeSpan.FromMinutes(10),
                token));

        await fixture.Store.EvictByTagAsync(fixture.PurgeAllTag, default);

        Assert.Empty(await fixture.RemainingOutputEntries());
    }

    [Fact]
    public async Task DeletesInBatchesWhenTheServiceSupportsIt()
    {
        await using var fixture = new Fixture(useBatchDelete: true);

        for (var index = 0; index < 10; index++)
        {
            await fixture.Store.SetAsync(
                $"batch-{index}",
                Encoding.UTF8.GetBytes("v"),
                tags: null,
                TimeSpan.FromMinutes(10),
                default);
        }

        // Azurite's support for the blob batch endpoint is the thing under test as much as ours: if
        // this fails, UseBatchDelete has to default to false and the readme has to say so.
        await fixture.Store.EvictByTagAsync(fixture.PurgeAllTag, default);

        Assert.Empty(await fixture.RemainingOutputEntries());
    }

    [Fact]
    public async Task ConcurrentWritersNeverLeaveAReaderWithATornPayload()
    {
        await using var fixture = new Fixture();

        var options = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10) };
        var first = Encoding.UTF8.GetBytes(new string('a', 512 * 1024));
        var second = Encoding.UTF8.GetBytes(new string('b', 512 * 1024));

        await fixture.Cache.SetAsync("contended", first, options);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var writer = Task.Run(
            async () =>
            {
                while (!cancellation.IsCancellationRequested)
                {
                    await fixture.Cache.SetAsync("contended", second, options, cancellation.Token);
                    await fixture.Cache.SetAsync("contended", first, options, cancellation.Token);
                }
            },
            cancellation.Token);

        while (!cancellation.IsCancellationRequested)
        {
            var read = await fixture.Cache.GetAsync("contended");

            if (read is null)
            {
                continue;
            }

            // An upload commits atomically, so a reader sees one version or the other, never a
            // splice of both.
            Assert.True(
                read.SequenceEqual(first) || read.SequenceEqual(second),
                "a reader observed a payload that was neither version");
        }

        await Task.WhenAny(writer);
    }

    [Fact]
    public async Task StoresAKeyLongEnoughToBeTruncated()
    {
        await using var fixture = new Fixture();

        var key = string.Concat(Enumerable.Repeat("å", 900));
        var payload = Encoding.UTF8.GetBytes("truncated");

        await fixture.Cache.SetAsync(
            key,
            payload,
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10) });

        // The name no longer carries the whole key, so this only works because the full key rides in
        // metadata and the read confirms it.
        Assert.Equal(payload, await fixture.Cache.GetAsync(key));
        Assert.Null(await fixture.Cache.GetAsync(key + "-different"));
    }

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => this.UtcNow;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly AzureBlobCacheOptions options;

        public Fixture(bool useBatchDelete = false)
        {
            this.options = new AzureBlobCacheOptions
            {
                ConnectionString = ConnectionString,

                // A container per test run, so a failure never leaves the next one reading debris.
                ContainerName = $"cache-{Guid.NewGuid():N}",
                ApplicationName = "Tests",
                UseBatchDelete = useBatchDelete,
                ExpiredItemsDeletionInterval = TimeSpan.Zero,
            };

            var cacheOptions = Options.Create(this.options);

            this.Cache = new AzureBlobCache(
                cacheOptions,
                Options.Create(new JsonSerializerOptions()),
                this.Time,
                NullLogger<AzureBlobCache>.Instance);

            this.Store = new AzureBlobOutputCacheStore(
                cacheOptions,
                this.Time,
                NullLogger<AzureBlobOutputCacheStore>.Instance);

            this.Container = new BlobServiceClient(ConnectionString)
                .GetBlobContainerClient(this.options.ContainerName);
        }

        public AzureBlobCache Cache { get; }

        public AzureBlobOutputCacheStore Store { get; }

        public BlobContainerClient Container { get; }

        public TestTimeProvider Time { get; } = new(DateTimeOffset.UtcNow);

        public string PurgeAllTag => this.options.PurgeAllTag;

        public async Task<List<string>> RemainingOutputEntries()
        {
            var names = new List<string>();

            var blobs = this.Container.GetBlobsAsync(
                BlobTraits.None,
                BlobStates.None,
                "Tests/output/",
                default);

            await foreach (var blob in blobs)
            {
                names.Add(blob.Name);
            }

            return names;
        }

        public async ValueTask DisposeAsync()
        {
            this.Cache.Dispose();
            this.Store.Dispose();

            await this.Container.DeleteIfExistsAsync();
        }
    }
}
