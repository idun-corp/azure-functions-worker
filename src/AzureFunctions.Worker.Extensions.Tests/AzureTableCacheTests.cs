using AzureFunctions.Worker.Extensions.Caching.AzureTable.Internal;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace AzureFunctions.Worker.Extensions.Tests;

public class AzureTableCacheTests
{
    private readonly AzureTableCache cache = new(
        Options.Create(new AzureTableCacheOptions { ConnectionString = "UseDevelopmentStorage=true", ApplicationName = "App" }),
        Options.Create(new System.Text.Json.JsonSerializerOptions()));

    [Fact]
    public void SingleKeyRead_TreatsTrailingWildcardAsPartOfTheKey()
    {
        var filter = this.cache.BuildFilter("__MSFT_HCT__*", exactKey: true);

        Assert.EndsWith(" and RowKey eq '__MSFT_HCT__*'", filter);
    }

    [Fact]
    public void PatternRead_TreatsTrailingWildcardAsPrefix()
    {
        var filter = this.cache.BuildFilter("__MSFT_HCT__*");

        Assert.EndsWith(" and RowKey ge '__MSFT_HCT__' and RowKey lt '__MSFT_HCT_`'", filter);
    }

    [Fact]
    public void LargeValue_IsSplitIntoPropertiesWithinTheTableLimit()
    {
        var value = Enumerable.Range(0, (Cache.MaxPropertySize * 2) + 1).Select(index => (byte) index).ToArray();
        var entity = new Cache { PartitionKey = "App", RowKey = "key", Data = value };

        Assert.Equal(["Data", "Data1", "Data2"], entity.Keys.Where(key => key.StartsWith("Data", StringComparison.Ordinal)).Order());
        Assert.All(entity.Values.OfType<byte[]>(), chunk => Assert.True(chunk.Length <= Cache.MaxPropertySize));
        Assert.Equal(value, entity.Data);
    }

    [Fact]
    public void SmallValue_ReplacingALargeOne_LeavesNoStaleChunks()
    {
        var entity = new Cache { PartitionKey = "App", RowKey = "key", Data = new byte[(Cache.MaxPropertySize * 2) + 1] };

        entity.Data = [1, 2, 3];

        Assert.DoesNotContain("Data1", entity.Keys);
        Assert.Equal([1, 2, 3], entity.Data);
    }
}
