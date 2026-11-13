using System.Text;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using AzureFunctions.Worker.Extensions.Caching.AzureBlob.Internal;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AzureFunctions.Worker.Extensions.Tests.Caching;

/// <summary>
/// The read, write and expiry behaviour, against a mocked container.
/// </summary>
/// <remarks>
/// These assert the things a storage account cannot show from the outside: that a refresh carries no
/// request conditions, that an expired entry is deleted rather than only hidden, and that an entry
/// belonging to another key is left alone.
/// </remarks>
public class BlobCacheStorageTests
{
    private const string Key = "v2|owner|hvac";

    private static readonly DateTimeOffset Now = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ReadAsync_ReturnsAStoredEntry()
    {
        var fixture = new Fixture();
        var payload = Encoding.UTF8.GetBytes("cached");

        fixture.Store(Key, payload, TimeSpan.FromMinutes(10));

        var result = await fixture.Storage.ReadAsync(Key, default);

        Assert.NotNull(result);
        Assert.Equal(payload, result.Payload);
        Assert.Equal(Key, result.Entry.Key);
    }

    [Fact]
    public async Task ReadAsync_TreatsAMissingBlobAsAMiss()
    {
        var fixture = new Fixture();

        fixture.BlobFor(Key)
            .Setup(blob => blob.DownloadContentAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(404, "missing", BlobErrorCode.BlobNotFound.ToString(), null));

        Assert.Null(await fixture.Storage.ReadAsync(Key, default));
    }

    [Fact]
    public async Task ReadAsync_LetsRealStorageFailuresSurface()
    {
        var fixture = new Fixture();

        fixture.BlobFor(Key)
            .Setup(blob => blob.DownloadContentAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(403, "forbidden", "AuthorizationFailure", null));

        // A miss and a permissions failure must not look the same, or an outage reads as a cold cache.
        await Assert.ThrowsAsync<RequestFailedException>(() => fixture.Storage.ReadAsync(Key, default));
    }

    [Fact]
    public async Task ReadAsync_TreatsAnExpiredEntryAsAMissAndDeletesIt()
    {
        var fixture = new Fixture();

        fixture.Store(Key, Encoding.UTF8.GetBytes("stale"), TimeSpan.FromMinutes(10));
        fixture.Time.UtcNow = Now.AddMinutes(11);

        Assert.Null(await fixture.Storage.ReadAsync(Key, default));

        await WaitUntil(() => fixture.Deleted.Contains(fixture.Name(Key)));
    }

    [Fact]
    public async Task ReadAsync_TreatsAnEntryBelongingToAnotherKeyAsAMissWithoutDeletingIt()
    {
        var fixture = new Fixture();

        // What a hash collision between two truncated keys would look like.
        fixture.Store(Key, Encoding.UTF8.GetBytes("someone else's"), TimeSpan.FromMinutes(10), storedKey: "other");

        Assert.Null(await fixture.Storage.ReadAsync(Key, default));

        await Task.Delay(200);

        // Deleting it would evict an entry that is perfectly valid for whichever key owns it.
        Assert.Empty(fixture.Deleted);
    }

    [Fact]
    public async Task ReadAsync_TreatsAnUnreadableEncodingAsAMissAndDeletesIt()
    {
        var fixture = new Fixture();

        fixture.Store(
            Key,
            Encoding.UTF8.GetBytes("payload"),
            TimeSpan.FromMinutes(10),
            encoding: "zstd");

        Assert.Null(await fixture.Storage.ReadAsync(Key, default));

        await WaitUntil(() => fixture.Deleted.Contains(fixture.Name(Key)));
    }

    [Fact]
    public async Task ReadAsync_DoesNotRefreshBeforeTheThresholdIsReached()
    {
        var fixture = new Fixture();

        fixture.Store(Key, Encoding.UTF8.GetBytes("fresh"), sliding: TimeSpan.FromMinutes(10));
        fixture.Time.UtcNow = Now.AddMinutes(4);

        Assert.NotNull(await fixture.Cache.GetAsync(Key));

        await Task.Delay(200);

        // Each refresh is a billable write, so a hot key must not pay one on every read.
        fixture.BlobFor(Key).Verify(
            blob => blob.SetMetadataAsync(
                It.IsAny<IDictionary<string, string>>(),
                It.IsAny<BlobRequestConditions>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ReadAsync_RefreshesWithoutRequestConditionsOnceTheThresholdIsReached()
    {
        var fixture = new Fixture();

        fixture.Store(Key, Encoding.UTF8.GetBytes("fresh"), sliding: TimeSpan.FromMinutes(10));
        fixture.Time.UtcNow = Now.AddMinutes(6);

        Assert.NotNull(await fixture.Cache.GetAsync(Key));

        await WaitUntil(() => fixture.RefreshedMetadata.Count > 0);

        // The regression guard: a refresh under an ETag precondition loses races under concurrency
        // and reports each loss as a failed 412 dependency for something that is not a failure.
        Assert.All(fixture.RefreshConditions, conditions => Assert.Null(conditions));

        var refreshed = fixture.RefreshedMetadata.Single();

        Assert.True(BlobCacheEntry.TryParse(refreshed, out var entry));
        Assert.Equal(fixture.Time.UtcNow.AddMinutes(10), entry.ExpiresAt);
    }

    [Fact]
    public async Task WriteAsync_UploadsThePayloadWithItsBookkeeping()
    {
        var fixture = new Fixture();
        var payload = Encoding.UTF8.GetBytes("to store");

        await fixture.Storage.WriteAsync(
            Key,
            payload,
            (length, encoding) => BlobCacheEntry.Create(Key, TimeSpan.FromMinutes(15), Now, length, encoding),
            default);

        var options = Assert.Single(fixture.Uploads);

        Assert.True(BlobCacheEntry.TryParse(options.Metadata, out var entry));
        Assert.Equal(Key, entry.Key);
        Assert.Equal(Now.AddMinutes(15), entry.ExpiresAt);
        Assert.Equal(payload.Length, entry.Length);
    }

    [Fact]
    public async Task RemoveAsync_ToleratesAnEntryThatIsAlreadyGone()
    {
        var fixture = new Fixture();

        fixture.BlobFor(Key)
            .Setup(blob => blob.DeleteAsync(
                It.IsAny<DeleteSnapshotsOption>(),
                It.IsAny<BlobRequestConditions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(404, "missing", BlobErrorCode.BlobNotFound.ToString(), null));

        // Removing something that is not there is the caller's intent either way.
        var removal = fixture.Storage.RemoveAsync(Key, default);

        await removal;

        Assert.True(removal.IsCompletedSuccessfully);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        // Refreshes and deletes are deliberately fire and forget, so an assertion has to wait for
        // them rather than assume they have already run.
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
        {
            await Task.Delay(20);
        }

        Assert.True(condition(), "the background operation did not run");
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
            ContainerName = "cache",

            // The sweep is exercised separately; leaving it on would fire background listings that
            // the per-blob mocks here do not describe.
            ExpiredItemsDeletionInterval = TimeSpan.Zero,
            UseBatchDelete = false,
        };

        public Fixture()
        {
            this.Container
                .Setup(container => container.GetBlobClient(It.IsAny<string>()))
                .Returns<string>(name => this.Blob(name).Object);

            this.Storage = new BlobCacheStorage(
                Options.Create(this.options),
                "distributed",
                this.Time,
                NullLogger.Instance,
                this.Container.Object,
                batchClient: null);
        }

        public Mock<BlobContainerClient> Container { get; } = new();

        public TestTimeProvider Time { get; } = new(Now);

        public BlobCacheStorage Storage { get; }

        public AzureBlobCache Cache => new(
            Options.Create(this.options),
            Options.Create(new JsonSerializerOptions()),
            this.Storage);

        public List<string> Deleted { get; } = [];

        public List<IDictionary<string, string>> RefreshedMetadata { get; } = [];

        public List<BlobRequestConditions?> RefreshConditions { get; } = [];

        public List<BlobUploadOptions> Uploads { get; } = [];

        public string Name(string key)
        {
            return BlobCacheKeyEncoder.Encode(
                key,
                string.Concat(this.options.ApplicationName, "/distributed"),
                this.options.MaxEncodedKeyLength);
        }

        public Mock<BlobClient> BlobFor(string key)
        {
            return this.Blob(this.Name(key));
        }

        public Mock<BlobClient> Blob(string name)
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

            blob.Setup(client => client.SetMetadataAsync(
                    It.IsAny<IDictionary<string, string>>(),
                    It.IsAny<BlobRequestConditions>(),
                    It.IsAny<CancellationToken>()))
                .Callback<IDictionary<string, string>, BlobRequestConditions, CancellationToken>(
                    (metadata, conditions, _) =>
                    {
                        this.RefreshedMetadata.Add(metadata);
                        this.RefreshConditions.Add(conditions);
                    })
                .ReturnsAsync(Response.FromValue(BlobsModelFactory.BlobInfo(default, default), Mock.Of<Response>()));

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

        public void Store(
            string key,
            byte[] payload,
            TimeSpan? absolute = null,
            TimeSpan? sliding = null,
            string? storedKey = null,
            string? encoding = null)
        {
            var entry = sliding.HasValue
                ? BlobCacheEntry.Create(
                    storedKey ?? key,
                    new DistributedCacheEntryOptions { SlidingExpiration = sliding },
                    this.Time.UtcNow,
                    TimeSpan.FromMinutes(20),
                    payload.Length,
                    encoding)
                : BlobCacheEntry.Create(
                    storedKey ?? key,
                    absolute ?? TimeSpan.FromMinutes(10),
                    this.Time.UtcNow,
                    payload.Length,
                    encoding);

            var details = BlobsModelFactory.BlobDownloadDetails(metadata: entry.ToMetadata());

            var result = BlobsModelFactory.BlobDownloadResult(
                content: BinaryData.FromBytes(payload),
                details: details);

            this.Blob(this.Name(key))
                .Setup(blob => blob.DownloadContentAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(Response.FromValue(result, Mock.Of<Response>()));
        }
    }
}
