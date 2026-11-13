using Azure;
using Azure.Data.Tables;

namespace AzureFunctions.Worker.Extensions.Caching.AzureTable.Internal;

/// <summary>
/// Cache inherited from <see cref="Dictionary{TKey, TValue}"/> will have better performance.
/// </summary>
internal sealed class Cache : Dictionary<string, object>, ITableEntity
{
    public required string PartitionKey
    {
        get => this.TryGetValue<string>(nameof(this.PartitionKey))!;
        set => this[nameof(this.PartitionKey)] = value;
    }

    public required string RowKey
    {
        get => this.TryGetValue<string>(nameof(this.RowKey))!;
        set => this[nameof(this.RowKey)] = value;
    }

    // Azure Table caps a binary property at 64 KiB, so larger values are split across Data, Data1, Data2, ... within the 1 MiB entity limit.
    internal const int MaxPropertySize = 64 * 1024;

    public byte[]? Data
    {
        get
        {
            var first = this.TryGetValue<byte[]>(nameof(this.Data));

            if (first is null || !this.ContainsKey(ChunkName(1)))
            {
                return first;
            }

            var chunks = new List<byte[]> { first };

            for (var index = 1; this.TryGetValue(ChunkName(index), out var chunk); index++)
            {
                chunks.Add((byte[]) chunk);
            }

            return [.. chunks.SelectMany(chunk => chunk)];
        }

        set
        {
            var staleChunk = 1;

            while (this.Remove(ChunkName(staleChunk)))
            {
                staleChunk++;
            }

            if (value is null || value.Length <= MaxPropertySize)
            {
                this[nameof(this.Data)] = value!;

                return;
            }

            var chunks = value.Chunk(MaxPropertySize).ToList();

            this[nameof(this.Data)] = chunks[0];

            for (var index = 1; index < chunks.Count; index++)
            {
                this[ChunkName(index)] = chunks[index];
            }
        }
    }

    public DateTimeOffset? AbsoluteExpiration
    {
        get => this.TryGetValue<DateTimeOffset?>(nameof(this.AbsoluteExpiration));
        set => this[nameof(this.AbsoluteExpiration)] = value!;
    }

    public DateTimeOffset ExpiresAtTime
    {
        get => this.TryGetValue<DateTimeOffset>(nameof(this.ExpiresAtTime));
        set => this[nameof(this.ExpiresAtTime)] = value;
    }

    public long? SlidingExpirationInSeconds
    {
        get => this.TryGetValue<long?>(nameof(this.SlidingExpirationInSeconds));
        set => this[nameof(this.SlidingExpirationInSeconds)] = value!;
    }

    DateTimeOffset? ITableEntity.Timestamp
    {
        get => this.TryGetValue<DateTimeOffset?>(nameof(ITableEntity.Timestamp));
        set => this[nameof(ITableEntity.Timestamp)] = value!;
    }

    ETag ITableEntity.ETag
    {
        get => this.TryGetValue<ETag>("odata.etag");
        set => this["odata.etag"] = value;
    }

    private static string ChunkName(int index) => $"{nameof(Data)}{index}";

    private T? TryGetValue<T>(string key)
    {
        return this.TryGetValue(key, out var value) ? (T) value : default;
    }
}
