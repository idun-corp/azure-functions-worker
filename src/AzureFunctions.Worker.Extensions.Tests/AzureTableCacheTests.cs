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
}
