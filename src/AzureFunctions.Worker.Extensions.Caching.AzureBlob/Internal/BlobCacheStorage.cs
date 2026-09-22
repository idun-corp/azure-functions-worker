using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Microsoft.Extensions.Caching.Distributed;

namespace AzureFunctions.Worker.Extensions.Caching.AzureBlob.Internal;

/// <summary>
/// The storage layer both cache faces sit on: one container, one key encoding, one metadata schema.
/// </summary>
internal sealed class BlobCacheStorage : IDisposable
{
    /// <summary>
    /// Subrequests the blob batch endpoint accepts in one call.
    /// </summary>
    private const int BatchSize = 256;

    /// <summary>
    /// Blobs the service returns per listing page, and its documented maximum.
    /// </summary>
    private const int PageSize = 5000;

    private readonly AzureBlobCacheOptions options;
    private readonly TimeProvider timeProvider;
    private readonly ILogger logger;
    private readonly BlobContainerClient container;
    private readonly BlobBatchClient batchClient;
    private readonly CancellationTokenSource backgroundCancellation = new();
    private readonly SemaphoreSlim containerGate = new(1, 1);

#if NET9_0_OR_GREATER
    private readonly Lock expirationLock = new();
#else
    private readonly object expirationLock = new();
#endif

    private readonly string namePrefix;

    private bool containerCreated;
    private DateTimeOffset? lastExpirationScan;

    public BlobCacheStorage(
        IOptions<AzureBlobCacheOptions> cacheOptions,
        string scope,
        TimeProvider timeProvider,
        ILogger logger)
    {
        this.options = cacheOptions.Value;

        // Each face gets its own segment under the application prefix. Without it a prefix purge
        // issued by the output cache store would take the distributed cache entries with it.
        this.namePrefix = string.Concat(this.options.ApplicationName, "/", scope);
        this.timeProvider = timeProvider;
        this.logger = logger;

        var retryPolicy = new ContainerCreatingRetryPolicy();

        var clientOptions = new BlobClientOptions
        {
            RetryPolicy = retryPolicy,
            Diagnostics =
            {
                IsDistributedTracingEnabled = this.options.EnableDependencyTracing,
            },
        };

        var serviceClient = new BlobServiceClient(this.options.ConnectionString, clientOptions);

        this.container = serviceClient.GetBlobContainerClient(this.options.ContainerName);
        this.batchClient = serviceClient.GetBlobBatchClient();

        if (this.options.CreateContainerIfNotExists)
        {
            retryPolicy.ErrorHandlers.Add(
                BlobErrorCode.ContainerNotFound.ToString(),
                cancellationToken => this.EnsureContainerAsync(serviceClient, cancellationToken));
        }
    }

    /// <summary>
    /// Current time, as the storage layer sees it.
    /// </summary>
    public DateTimeOffset UtcNow => this.timeProvider.GetUtcNow();

    /// <summary>
    /// Reads an entry, treating an expired or unreadable one as a miss.
    /// </summary>
    /// <param name="key">Cache key</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The entry and its payload, or null</returns>
    public async Task<BlobCacheReadResult?> ReadAsync(string key, CancellationToken cancellationToken)
    {
        this.ScheduleExpiredCleanup();

        var name = this.BlobName(key);

        try
        {
            var response = await this.container.GetBlobClient(name)
                .DownloadContentAsync(cancellationToken);

            return this.Interpret(key, name, response.Value.Content, response.Value.Details.Metadata);
        }
        catch (RequestFailedException exception) when (IsMissing(exception))
        {
            return null;
        }
    }

    /// <summary>
    /// Synchronous counterpart of <see cref="ReadAsync"/>.
    /// </summary>
    /// <param name="key">Cache key</param>
    /// <returns>The entry and its payload, or null</returns>
    public BlobCacheReadResult? Read(string key)
    {
        this.ScheduleExpiredCleanup();

        var name = this.BlobName(key);

        try
        {
            var response = this.container.GetBlobClient(name).DownloadContent();

            return this.Interpret(key, name, response.Value.Content, response.Value.Details.Metadata);
        }
        catch (RequestFailedException exception) when (IsMissing(exception))
        {
            return null;
        }
    }

    /// <summary>
    /// Reads an entry's bookkeeping without transferring its payload.
    /// </summary>
    /// <param name="key">Cache key</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The blob name and entry, or null</returns>
    public async Task<(string Name, BlobCacheEntry Entry)?> ReadMetadataAsync(
        string key,
        CancellationToken cancellationToken)
    {
        this.ScheduleExpiredCleanup();

        var name = this.BlobName(key);

        try
        {
            var properties = await this.container.GetBlobClient(name)
                .GetPropertiesAsync(cancellationToken: cancellationToken);

            if (!BlobCacheEntry.TryParse(properties.Value.Metadata, out var entry) ||
                !string.Equals(entry.Key, key, StringComparison.Ordinal) ||
                entry.IsExpired(this.UtcNow))
            {
                return null;
            }

            return (name, entry);
        }
        catch (RequestFailedException exception) when (IsMissing(exception))
        {
            return null;
        }
    }

    /// <summary>
    /// Writes an entry, replacing any existing one.
    /// </summary>
    /// <param name="key">Cache key</param>
    /// <param name="payload">Payload to store</param>
    /// <param name="describe">Builds the entry from the compressed length and encoding</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A task that completes when the entry is stored</returns>
    public async Task WriteAsync(
        string key,
        byte[] payload,
        Func<long, string?, BlobCacheEntry> describe,
        CancellationToken cancellationToken)
    {
        this.ScheduleExpiredCleanup();

        var (content, entry) = this.Prepare(payload, describe);

        await this.container.GetBlobClient(this.BlobName(key)).UploadAsync(
            content,
            new BlobUploadOptions { Metadata = entry.ToMetadata() },
            cancellationToken);
    }

    /// <summary>
    /// Synchronous counterpart of <see cref="WriteAsync"/>.
    /// </summary>
    /// <param name="key">Cache key</param>
    /// <param name="payload">Payload to store</param>
    /// <param name="describe">Builds the entry from the compressed length and encoding</param>
    public void Write(string key, byte[] payload, Func<long, string?, BlobCacheEntry> describe)
    {
        this.ScheduleExpiredCleanup();

        var (content, entry) = this.Prepare(payload, describe);

        this.container.GetBlobClient(this.BlobName(key)).Upload(
            content,
            new BlobUploadOptions { Metadata = entry.ToMetadata() });
    }

    /// <summary>
    /// Removes an entry, tolerating one that is already gone.
    /// </summary>
    /// <param name="key">Cache key</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A task that completes when the entry is removed</returns>
    public async Task RemoveAsync(string key, CancellationToken cancellationToken)
    {
        this.ScheduleExpiredCleanup();

        try
        {
            await this.container.GetBlobClient(this.BlobName(key))
                .DeleteAsync(cancellationToken: cancellationToken);
        }
        catch (RequestFailedException exception) when (IsMissing(exception))
        {
            // Removing an entry that is not there is the caller's intent either way.
        }
    }

    /// <summary>
    /// Synchronous counterpart of <see cref="RemoveAsync"/>.
    /// </summary>
    /// <param name="key">Cache key</param>
    public void Remove(string key)
    {
        this.ScheduleExpiredCleanup();

        try
        {
            this.container.GetBlobClient(this.BlobName(key)).Delete();
        }
        catch (RequestFailedException exception) when (IsMissing(exception))
        {
            // Removing an entry that is not there is the caller's intent either way.
        }
    }

    /// <summary>
    /// Enumerates live entries whose key starts with the given prefix.
    /// </summary>
    /// <remarks>
    /// The listing prefix is only a filter. A key long enough to have been truncated does not carry
    /// its tail in the blob name, so every candidate is confirmed against the full key in metadata.
    /// </remarks>
    /// <param name="keyPrefix">Literal key prefix</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Matching entries and their blob names</returns>
    public async IAsyncEnumerable<(string Name, BlobCacheEntry Entry)> ListAsync(
        string keyPrefix,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var now = this.UtcNow;

        var prefix = BlobCacheKeyEncoder.EncodePrefix(
            keyPrefix,
            this.namePrefix,
            this.options.MaxEncodedKeyLength);

        var blobs = this.container.GetBlobsAsync(
            BlobTraits.Metadata,
            BlobStates.None,
            prefix,
            cancellationToken);

        await foreach (var blob in blobs)
        {
            if (!BlobCacheEntry.TryParse(blob.Metadata, out var entry) ||
                entry.IsExpired(now) ||
                !entry.Key.StartsWith(keyPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            yield return (blob.Name, entry);
        }
    }

    /// <summary>
    /// Deletes every blob under a blob name prefix, live or expired.
    /// </summary>
    /// <param name="prefix">Blob name prefix</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of blobs deleted</returns>
    public async Task<int> DeleteByNamePrefixAsync(string prefix, CancellationToken cancellationToken)
    {
        var deleted = 0;
        var page = new List<string>(BatchSize);

        var blobs = this.container.GetBlobsAsync(
            BlobTraits.None,
            BlobStates.None,
            prefix,
            cancellationToken);

        await foreach (var blob in blobs)
        {
            page.Add(blob.Name);

            if (page.Count == BatchSize)
            {
                deleted += await this.DeleteAsync(page, cancellationToken);
                page.Clear();
            }
        }

        if (page.Count > 0)
        {
            deleted += await this.DeleteAsync(page, cancellationToken);
        }

        return deleted;
    }

    /// <summary>
    /// The blob name prefix covering every entry this face owns.
    /// </summary>
    /// <returns>Blob name prefix</returns>
    public string ScopePrefix()
    {
        return string.Concat(this.namePrefix, "/");
    }

    /// <summary>
    /// Deletes the named blobs, tolerating ones that are already gone.
    /// </summary>
    /// <param name="names">Blob names</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of blobs deleted</returns>
    public async Task<int> DeleteAsync(IReadOnlyCollection<string> names, CancellationToken cancellationToken)
    {
        if (names.Count == 0)
        {
            return 0;
        }

        if (!this.options.UseBatchDelete || names.Count == 1)
        {
            foreach (var name in names)
            {
                try
                {
                    await this.container.GetBlobClient(name)
                        .DeleteAsync(cancellationToken: cancellationToken);
                }
                catch (RequestFailedException exception) when (IsMissing(exception))
                {
                    // Another instance got there first.
                }
            }

            return names.Count;
        }

        var deleted = 0;

        foreach (var chunk in names.Chunk(BatchSize))
        {
            var batch = this.batchClient.CreateBatch();

            foreach (var name in chunk)
            {
                batch.DeleteBlob(this.container.GetBlobClient(name).Uri);
            }

            // A blob batch is not atomic and reports per subrequest. Another instance deleting the
            // same entry concurrently is expected, so a failed subrequest must not fail the batch.
            await this.batchClient.SubmitBatchAsync(batch, throwOnAnyFailure: false, cancellationToken);

            deleted += chunk.Length;
        }

        return deleted;
    }

    /// <summary>
    /// Restarts an entry's sliding window with a metadata-only write, in the background.
    /// </summary>
    /// <remarks>
    /// Written unconditionally, with no ETag precondition. A refresh that lost a race would return
    /// 412 and be recorded as a failed dependency for something that is not a failure; last writer
    /// wins costs at most one sliding period of accuracy on an entry that was just rewritten.
    /// </remarks>
    /// <param name="name">Blob name</param>
    /// <param name="entry">Entry to refresh</param>
    public void ScheduleRefresh(string name, BlobCacheEntry entry)
    {
        this.RunInBackground(
            async cancellationToken =>
            {
                var refreshed = entry.Refresh(this.UtcNow);

                await this.container.GetBlobClient(name).SetMetadataAsync(
                    refreshed.ToMetadata(),
                    conditions: null,
                    cancellationToken);
            },
            "refreshing the sliding expiration of {BlobName}",
            name);
    }

    /// <summary>
    /// Deletes an entry in the background, for one found expired or unreadable during a read.
    /// </summary>
    /// <param name="name">Blob name</param>
    public void ScheduleDelete(string name)
    {
        this.RunInBackground(
            async cancellationToken =>
            {
                try
                {
                    await this.container.GetBlobClient(name)
                        .DeleteAsync(cancellationToken: cancellationToken);
                }
                catch (RequestFailedException exception) when (IsMissing(exception))
                {
                    // Already swept.
                }
            },
            "deleting the expired entry {BlobName}",
            name);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Background refreshes and sweeps must not outlive the host. On a consumption plan an
        // instance can be reclaimed at any time, and an orphaned task would log into a disposed
        // provider.
        if (!this.backgroundCancellation.IsCancellationRequested)
        {
            this.backgroundCancellation.Cancel();
        }

        this.backgroundCancellation.Dispose();
        this.containerGate.Dispose();
    }

    private static bool IsMissing(RequestFailedException exception)
    {
        return exception.Status == 404;
    }

    private (BinaryData Content, BlobCacheEntry Entry) Prepare(
        byte[] payload,
        Func<long, string?, BlobCacheEntry> describe)
    {
        var stored = BlobCachePayload.Compress(
            payload,
            this.options.Compression,
            this.options.CompressionThresholdBytes,
            out var encoding);

        return (new BinaryData(stored), describe(payload.Length, encoding));
    }

    private BlobCacheReadResult? Interpret(
        string key,
        string name,
        BinaryData content,
        IDictionary<string, string> metadata)
    {
        if (!BlobCacheEntry.TryParse(metadata, out var entry))
        {
            // Written by a schema this version does not understand, so it cannot be served. Dropping
            // it keeps a rollback from leaving unreadable entries behind forever.
            this.ScheduleDelete(name);

            return null;
        }

        if (!string.Equals(entry.Key, key, StringComparison.Ordinal))
        {
            // Two keys truncated onto one name. Deliberately not deleted: the blob belongs to
            // whichever key wrote it, and removing it would evict an entry that is perfectly valid
            // for its owner.
            this.logger.LogWarning(
                "Cache entry {BlobName} holds a different key than the one requested, so it was treated as a miss",
                name);

            return null;
        }

        if (entry.IsExpired(this.UtcNow))
        {
            this.ScheduleDelete(name);

            return null;
        }

        if (!BlobCachePayload.TryDecompress(content.ToArray(), entry.Encoding, entry.Length, out var payload))
        {
            this.ScheduleDelete(name);

            return null;
        }

        return new BlobCacheReadResult(name, entry, payload);
    }

    private string BlobName(string key)
    {
        return BlobCacheKeyEncoder.Encode(
            key,
            this.namePrefix,
            this.options.MaxEncodedKeyLength);
    }

    private async Task EnsureContainerAsync(BlobServiceClient serviceClient, CancellationToken cancellationToken)
    {
        if (this.containerCreated)
        {
            return;
        }

        await this.containerGate.WaitAsync(cancellationToken);

        try
        {
            if (this.containerCreated)
            {
                return;
            }

            await serviceClient.GetBlobContainerClient(this.options.ContainerName)
                .CreateIfNotExistsAsync(cancellationToken: cancellationToken);

            this.containerCreated = true;
        }
        finally
        {
            this.containerGate.Release();
        }
    }

    private void ScheduleExpiredCleanup()
    {
        if (this.options.ExpiredItemsDeletionInterval <= TimeSpan.Zero)
        {
            return;
        }

        var now = this.UtcNow;

        if (!this.ShouldScan(now))
        {
            return;
        }

        lock (this.expirationLock)
        {
            if (!this.ShouldScan(now))
            {
                return;
            }

            this.lastExpirationScan = now;
        }

        this.RunInBackground(this.DeleteExpiredEntriesAsync, "sweeping expired entries");
    }

    private bool ShouldScan(DateTimeOffset now)
    {
        return this.lastExpirationScan is not { } last ||
            (now - last) > this.options.ExpiredItemsDeletionInterval;
    }

    private async Task DeleteExpiredEntriesAsync(CancellationToken cancellationToken)
    {
        var now = this.UtcNow;
        var expired = new List<string>(BatchSize);

        var blobs = this.container.GetBlobsAsync(
            BlobTraits.Metadata,
            BlobStates.None,
            this.ScopePrefix(),
            cancellationToken);

        await foreach (var blob in blobs.AsPages(pageSizeHint: PageSize)
            .WithCancellation(cancellationToken))
        {
            foreach (var item in blob.Values)
            {
                // An entry whose metadata will not parse is unreadable, so it is swept too rather
                // than accumulating forever.
                if (BlobCacheEntry.TryParse(item.Metadata, out var entry) && !entry.IsExpired(now))
                {
                    continue;
                }

                expired.Add(item.Name);

                if (expired.Count == BatchSize)
                {
                    await this.DeleteAsync(expired, cancellationToken);
                    expired.Clear();
                }
            }
        }

        if (expired.Count > 0)
        {
            await this.DeleteAsync(expired, cancellationToken);
        }
    }

    private void RunInBackground(
        Func<CancellationToken, Task> work,
        string operation,
        string? target = null)
    {
        var cancellationToken = this.backgroundCancellation.Token;

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await work(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // The host is shutting down; the entry keeps its current expiry.
                }
                catch (RequestFailedException exception)
                {
                    // Cache maintenance is best effort by design. Failing it must never surface to
                    // the caller, whose request has already been served.
                    this.logger.LogWarning(
                        exception,
                        "Cache maintenance failed during {Operation} for {Target}",
                        operation,
                        target);
                }
            },
            cancellationToken);
    }
}
