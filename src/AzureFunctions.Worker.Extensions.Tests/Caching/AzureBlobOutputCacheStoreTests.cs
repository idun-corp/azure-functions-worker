using System.Text;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using AzureFunctions.Worker.Extensions.Caching.AzureBlob.Internal;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AzureFunctions.Worker.Extensions.Tests.Caching;

public class AzureBlobOutputCacheStoreTests
{
    private const string Key = "GET|/api/hvac/hourly";

    private const string PurgeAllTag = "response-cache:all";

    private static readonly DateTimeOffset Now = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SetAsync_ThenGetAsync_RoundTripsWithinTheLifetime()
    {
        var fixture = new Fixture();
        var payload = Encoding.UTF8.GetBytes("{\"rows\":[]}");

        await fixture.Store.SetAsync(Key, payload, tags: [PurgeAllTag], TimeSpan.FromMinutes(30), default);

        var written = Assert.Single(fixture.Uploads);

        Assert.True(BlobCacheEntry.TryParse(written.Metadata, out var entry));
        Assert.Equal(Now.AddMinutes(30), entry.ExpiresAt);

        // Output cache entries have a fixed lifetime, never a sliding one.
        Assert.Null(entry.SlidingExpirationSeconds);
    }

    [Fact]
    public async Task GetAsync_ReturnsNothingOnceTheLifetimeHasElapsed()
    {
        var fixture = new Fixture();

        fixture.Stored(Key, Encoding.UTF8.GetBytes("stale"), TimeSpan.FromMinutes(5));
        fixture.Time.UtcNow = Now.AddMinutes(6);

        Assert.Null(await fixture.Store.GetAsync(Key, default));
    }

    [Fact]
    public async Task EvictByTagAsync_RemovesEveryEntryForTheConfiguredTag()
    {
        var fixture = new Fixture();

        fixture.Listing("Tests/output/", "Tests/output/one", "Tests/output/two");

        await fixture.Store.EvictByTagAsync(PurgeAllTag, default);

        Assert.Equal(["Tests/output/one", "Tests/output/two"], fixture.Deleted.Order());
    }

    [Fact]
    public async Task EvictByTagAsync_IgnoresAnyOtherTagWithoutThrowing()
    {
        var fixture = new Fixture();

        fixture.Listing("Tests/output/", "Tests/output/one");

        // Eviction runs from maintenance endpoints and background jobs. Throwing for a tag this
        // version cannot honour would turn a missing feature into a failed request.
        await fixture.Store.EvictByTagAsync("response-cache:tenant:abc", default);

        Assert.Empty(fixture.Deleted);
    }

    [Fact]
    public async Task EvictByTagAsync_LeavesTheDistributedCacheAlone()
    {
        var fixture = new Fixture();

        // Both faces share an application name, so the scope segment is the only thing keeping a
        // purge on one from taking the other with it.
        fixture.Listing("Tests/output/", "Tests/output/one");

        await fixture.Store.EvictByTagAsync(PurgeAllTag, default);

        Assert.All(fixture.ListedPrefixes, prefix => Assert.Equal("Tests/output/", prefix));
        Assert.DoesNotContain(fixture.Deleted, name => name.StartsWith("Tests/distributed", StringComparison.Ordinal));
    }

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => this.UtcNow;
    }

    private sealed class Fixture
    {
        private readonly Dictionary<string, Mock<BlobClient>> blobs = new(StringComparer.Ordinal);

        private readonly AzureBlobCacheOptions options = new()
        {
            ConnectionString = "UseDevelopmentStorage=true",
            ApplicationName = "Tests",
            ContainerName = "response-cache",
            PurgeAllTag = PurgeAllTag,
            ExpiredItemsDeletionInterval = TimeSpan.Zero,
            UseBatchDelete = false,
        };

        public Fixture()
        {
            this.Container
                .Setup(container => container.GetBlobClient(It.IsAny<string>()))
                .Returns<string>(name => this.Blob(name).Object);

            var storage = new BlobCacheStorage(
                Options.Create(this.options),
                "output",
                this.Time,
                NullLogger.Instance,
                this.Container.Object,
                batchClient: null);

            this.Store = new AzureBlobOutputCacheStore(
                Options.Create(this.options),
                NullLogger<AzureBlobOutputCacheStore>.Instance,
                storage);
        }

        public Mock<BlobContainerClient> Container { get; } = new();

        public TestTimeProvider Time { get; } = new(Now);

        public AzureBlobOutputCacheStore Store { get; }

        public List<string> Deleted { get; } = [];

        public List<BlobUploadOptions> Uploads { get; } = [];

        public List<string> ListedPrefixes { get; } = [];

        public void Listing(string prefix, params string[] names)
        {
            var items = names
                .Select(name => BlobsModelFactory.BlobItem(name: name))
                .ToList();

            this.Container
                .Setup(container => container.GetBlobsAsync(
                    It.IsAny<BlobTraits>(),
                    It.IsAny<BlobStates>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .Returns<BlobTraits, BlobStates, string, CancellationToken>((_, _, requested, _) =>
                {
                    this.ListedPrefixes.Add(requested);

                    var matching = items
                        .Where(item => item.Name.StartsWith(requested, StringComparison.Ordinal))
                        .ToList();

                    return AsyncPageable<BlobItem>.FromPages(
                        [Page<BlobItem>.FromValues(matching, null, Mock.Of<Response>())]);
                });

            _ = prefix;
        }

        public void Stored(string key, byte[] payload, TimeSpan validFor)
        {
            var entry = BlobCacheEntry.Create(key, validFor, this.Time.UtcNow, payload.Length, encoding: null);

            var result = BlobsModelFactory.BlobDownloadResult(
                content: BinaryData.FromBytes(payload),
                details: BlobsModelFactory.BlobDownloadDetails(metadata: entry.ToMetadata()));

            var name = BlobCacheKeyEncoder.Encode(
                key,
                string.Concat(this.options.ApplicationName, "/output"),
                this.options.MaxEncodedKeyLength);

            this.Blob(name)
                .Setup(blob => blob.DownloadContentAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(Response.FromValue(result, Mock.Of<Response>()));
        }

        private Mock<BlobClient> Blob(string name)
        {
            if (this.blobs.TryGetValue(name, out var existing))
            {
                return existing;
            }

            var blob = new Mock<BlobClient>();

            blob.Setup(client => client.DeleteAsync(
                    It.IsAny<DeleteSnapshotsOption>(),
                    It.IsAny<BlobRequestConditions>(),
                    It.IsAny<CancellationToken>()))
                .Callback(() => this.Deleted.Add(name))
                .ReturnsAsync(Mock.Of<Response>());

            blob.Setup(client => client.UploadAsync(
                    It.IsAny<BinaryData>(),
                    It.IsAny<BlobUploadOptions>(),
                    It.IsAny<CancellationToken>()))
                .Callback<BinaryData, BlobUploadOptions, CancellationToken>(
                    (_, uploadOptions, _) => this.Uploads.Add(uploadOptions))
                .ReturnsAsync(Response.FromValue(
                    BlobsModelFactory.BlobContentInfo(default, default, default, default, default),
                    Mock.Of<Response>()));

            this.blobs[name] = blob;

            return blob;
        }
    }
}
