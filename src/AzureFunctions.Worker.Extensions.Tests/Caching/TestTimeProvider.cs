namespace AzureFunctions.Worker.Extensions.Tests.Caching;

/// <summary>
/// A clock the cache tests move by hand, so expiry is exercised without waiting for it.
/// </summary>
/// <param name="now">Time the clock starts at</param>
internal sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => this.UtcNow;
}
