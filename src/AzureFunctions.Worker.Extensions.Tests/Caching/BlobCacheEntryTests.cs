using AzureFunctions.Worker.Extensions.Caching.AzureBlob.Internal;
using Microsoft.Extensions.Caching.Distributed;

namespace AzureFunctions.Worker.Extensions.Tests.Caching;

public class BlobCacheEntryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan DefaultSliding = TimeSpan.FromMinutes(20);

    /// <summary>
    /// The expiry rules, stated the way the Azure Table cache states them. The two caches are meant
    /// to behave identically, so this table is the thing that fails if either drifts.
    /// </summary>
    public static TheoryData<DistributedCacheEntryOptions, DateTimeOffset, DateTimeOffset?, long?> Expirations => new()
    {
        // Nothing supplied: the configured default sliding window applies.
        {
            new DistributedCacheEntryOptions(),
            Now.Add(DefaultSliding),
            null,
            (long) DefaultSliding.TotalSeconds
        },

        // Sliding only.
        {
            new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromMinutes(5) },
            Now.AddMinutes(5),
            null,
            300
        },

        // Absolute only, and crucially no default sliding is added on top of it.
        {
            new DistributedCacheEntryOptions { AbsoluteExpiration = Now.AddHours(1) },
            Now.AddHours(1),
            Now.AddHours(1),
            null
        },

        // Absolute expressed relative to now.
        {
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(2) },
            Now.AddHours(2),
            Now.AddHours(2),
            null
        },

        // Both, with the sliding window inside the ceiling.
        {
            new DistributedCacheEntryOptions
            {
                SlidingExpiration = TimeSpan.FromMinutes(5),
                AbsoluteExpiration = Now.AddHours(1),
            },
            Now.AddMinutes(5),
            Now.AddHours(1),
            300
        },

        // Both, with a sliding window that would overshoot the ceiling and is clamped to it.
        {
            new DistributedCacheEntryOptions
            {
                SlidingExpiration = TimeSpan.FromHours(3),
                AbsoluteExpiration = Now.AddHours(1),
            },
            Now.AddHours(1),
            Now.AddHours(1),
            10800
        },
    };

    [Theory]
    [MemberData(nameof(Expirations))]
    public void Create_MatchesTheTableCacheExpiryRules(
        DistributedCacheEntryOptions options,
        DateTimeOffset expiresAt,
        DateTimeOffset? absoluteExpiration,
        long? slidingSeconds)
    {
        var entry = BlobCacheEntry.Create("key", options, Now, DefaultSliding, length: 10, encoding: null);

        Assert.Equal(expiresAt, entry.ExpiresAt);
        Assert.Equal(absoluteExpiration, entry.AbsoluteExpiration);
        Assert.Equal(slidingSeconds, entry.SlidingExpirationSeconds);
    }

    [Fact]
    public void Create_ForOutputCache_ExpiresAfterTheGivenLifetime()
    {
        var entry = BlobCacheEntry.Create("key", TimeSpan.FromMinutes(30), Now, length: 10, encoding: "br");

        Assert.Equal(Now.AddMinutes(30), entry.ExpiresAt);
        Assert.Equal(Now.AddMinutes(30), entry.AbsoluteExpiration);
        Assert.Null(entry.SlidingExpirationSeconds);
        Assert.Equal("br", entry.Encoding);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("br")]
    [InlineData("gzip")]
    public void ToMetadata_RoundTrips(string? encoding)
    {
        var original = BlobCacheEntry.Create(
            "v2|owner|hvac|åäö",
            new DistributedCacheEntryOptions
            {
                SlidingExpiration = TimeSpan.FromMinutes(5),
                AbsoluteExpiration = Now.AddHours(1),
            },
            Now,
            DefaultSliding,
            length: 4096,
            encoding: encoding);

        var metadata = original.ToMetadata();

        Assert.True(BlobCacheEntry.TryParse(metadata, out var parsed));
        Assert.Equal(original, parsed);
    }

    [Fact]
    public void ToMetadata_ProducesNamesAndValuesStorageAccepts()
    {
        var entry = BlobCacheEntry.Create(
            "中文-\U0001F600",
            new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromMinutes(5) },
            Now,
            DefaultSliding,
            length: 1,
            encoding: "br");

        var metadata = entry.ToMetadata();

        foreach (var pair in metadata)
        {
            // Metadata names must be valid C# identifiers, and both names and values must be ASCII.
            Assert.Matches("^[A-Za-z_][A-Za-z0-9_]*$", pair.Key);
            Assert.All(pair.Value, character => Assert.True(char.IsAscii(character)));
        }

        var size = metadata.Sum(pair => pair.Key.Length + pair.Value.Length);

        Assert.True(size < 8 * 1024, $"metadata was {size} bytes, past the 8 KiB the service allows");
    }

    [Fact]
    public void TryParse_RejectsMetadataItDoesNotUnderstand()
    {
        Assert.False(BlobCacheEntry.TryParse(null, out _));
        Assert.False(BlobCacheEntry.TryParse(new Dictionary<string, string>(), out _));

        var entry = BlobCacheEntry.Create(
            "key",
            new DistributedCacheEntryOptions(),
            Now,
            DefaultSliding,
            length: 1,
            encoding: null);

        var future = new Dictionary<string, string>(entry.ToMetadata(), StringComparer.OrdinalIgnoreCase)
        {
            ["cacheschema"] = "2",
        };

        // A schema this version does not know is a miss, not a misreading.
        Assert.False(BlobCacheEntry.TryParse(future, out _));
    }

    [Fact]
    public void TryParse_ToleratesTheServiceReturningDifferentlyCasedNames()
    {
        var entry = BlobCacheEntry.Create(
            "key",
            new DistributedCacheEntryOptions(),
            Now,
            DefaultSliding,
            length: 1,
            encoding: null);

        var upper = entry.ToMetadata().ToDictionary(pair => pair.Key.ToUpperInvariant(), pair => pair.Value);

        Assert.True(BlobCacheEntry.TryParse(upper, out var parsed));
        Assert.Equal(entry.Key, parsed.Key);
    }

    [Fact]
    public void IsExpired_TreatsTheExpiryInstantAsPast()
    {
        var entry = BlobCacheEntry.Create("key", TimeSpan.FromMinutes(1), Now, length: 1, encoding: null);

        Assert.False(entry.IsExpired(Now));
        Assert.False(entry.IsExpired(Now.AddSeconds(59)));
        Assert.True(entry.IsExpired(Now.AddMinutes(1)));
        Assert.True(entry.IsExpired(Now.AddMinutes(2)));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(4, false)]
    [InlineData(5, true)]
    [InlineData(9, true)]
    public void ShouldRefresh_WaitsUntilTheWindowIsHalfSpent(int minutesElapsed, bool expected)
    {
        var entry = BlobCacheEntry.Create(
            "key",
            new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromMinutes(10) },
            Now,
            DefaultSliding,
            length: 1,
            encoding: null);

        Assert.Equal(expected, entry.ShouldRefresh(Now.AddMinutes(minutesElapsed), threshold: 0.5));
    }

    [Fact]
    public void ShouldRefresh_IsFalseWithoutASlidingWindow()
    {
        var entry = BlobCacheEntry.Create("key", TimeSpan.FromMinutes(10), Now, length: 1, encoding: null);

        Assert.False(entry.ShouldRefresh(Now.AddMinutes(9), threshold: 0.5));
    }

    [Fact]
    public void ShouldRefresh_IsFalseOnceTheEntryIsPinnedToItsCeiling()
    {
        // Sliding, but already clamped to the absolute expiration, so a refresh could not move it
        // and would be a pure cost.
        var entry = BlobCacheEntry.Create(
            "key",
            new DistributedCacheEntryOptions
            {
                SlidingExpiration = TimeSpan.FromHours(3),
                AbsoluteExpiration = Now.AddMinutes(10),
            },
            Now,
            DefaultSliding,
            length: 1,
            encoding: null);

        Assert.Equal(Now.AddMinutes(10), entry.ExpiresAt);
        Assert.False(entry.ShouldRefresh(Now.AddMinutes(9), threshold: 0.5));
    }

    [Fact]
    public void Refresh_RestartsTheWindowAndRespectsTheCeiling()
    {
        var entry = BlobCacheEntry.Create(
            "key",
            new DistributedCacheEntryOptions
            {
                SlidingExpiration = TimeSpan.FromMinutes(10),
                AbsoluteExpiration = Now.AddMinutes(25),
            },
            Now,
            DefaultSliding,
            length: 1,
            encoding: null);

        Assert.Equal(Now.AddMinutes(20), entry.Refresh(Now.AddMinutes(10)).ExpiresAt);

        // Past the point where a full window would overshoot, the ceiling wins.
        Assert.Equal(Now.AddMinutes(25), entry.Refresh(Now.AddMinutes(20)).ExpiresAt);
    }
}
