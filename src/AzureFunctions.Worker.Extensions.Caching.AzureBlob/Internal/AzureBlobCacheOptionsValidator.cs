using System.Text.RegularExpressions;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Caching.Distributed;

namespace AzureFunctions.Worker.Extensions.Caching.AzureBlob.Internal;

/// <summary>
/// Rejects configurations that would fail later as opaque storage errors.
/// </summary>
/// <remarks>
/// Written by hand rather than generated: the interesting rules are relationships between options,
/// above all that a blob name has to fit in 1024 characters once the application name, the scope
/// segment and an encoded key are concatenated. A validation attribute cannot express that, and the
/// failure it prevents would otherwise appear as a rejected request from the service.
/// </remarks>
internal sealed partial class AzureBlobCacheOptionsValidator : IValidateOptions<AzureBlobCacheOptions>
{
    /// <summary>
    /// Longest blob name the service accepts, in characters.
    /// </summary>
    private const int MaxBlobNameLength = 1024;

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, AzureBlobCacheOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            failures.Add(
                $"{nameof(AzureBlobCacheOptions.ConnectionString)} is required. Set it explicitly, or " +
                "provide an AzureWebJobsStorage configuration value or environment variable.");
        }

        if (!ContainerName().IsMatch(options.ContainerName ?? string.Empty))
        {
            failures.Add(
                $"{nameof(AzureBlobCacheOptions.ContainerName)} '{options.ContainerName}' is not a valid " +
                "container name: 3 to 63 lowercase letters, digits and non-consecutive hyphens.");
        }

        if (!ApplicationName().IsMatch(options.ApplicationName ?? string.Empty))
        {
            failures.Add(
                $"{nameof(AzureBlobCacheOptions.ApplicationName)} '{options.ApplicationName}' is not a valid " +
                "blob name segment: 1 to 64 letters, digits, hyphens and underscores.");
        }

        if (options.MaxEncodedKeyLength <= BlobCacheKeyEncoder.HashSuffixLength)
        {
            failures.Add(
                $"{nameof(AzureBlobCacheOptions.MaxEncodedKeyLength)} must exceed " +
                $"{BlobCacheKeyEncoder.HashSuffixLength}, the length of the hash a truncated key is " +
                "replaced by, or no part of the key itself would survive.");
        }
        else
        {
            // The longest scope wins: the same options serve both faces, and only one of them needs
            // to overflow for the configuration to be wrong.
            var scope = Math.Max(AzureBlobCache.Scope.Length, AzureBlobOutputCacheStore.Scope.Length);
            var longest = (options.ApplicationName?.Length ?? 0) + 1 + scope + 1 + options.MaxEncodedKeyLength;

            if (longest > MaxBlobNameLength)
            {
                failures.Add(
                    $"{nameof(AzureBlobCacheOptions.ApplicationName)} and " +
                    $"{nameof(AzureBlobCacheOptions.MaxEncodedKeyLength)} together allow a blob name of " +
                    $"{longest} characters, past the {MaxBlobNameLength} the service accepts.");
            }
        }

        if (options.SlidingExpirationRefreshThreshold is < 0 or > 1)
        {
            failures.Add(
                $"{nameof(AzureBlobCacheOptions.SlidingExpirationRefreshThreshold)} must be a fraction " +
                "between 0 and 1.");
        }

        if (options.DefaultSlidingExpiration <= TimeSpan.Zero)
        {
            failures.Add($"{nameof(AzureBlobCacheOptions.DefaultSlidingExpiration)} must be positive.");
        }

        if (options.ExpiredItemsDeletionInterval < TimeSpan.Zero)
        {
            failures.Add(
                $"{nameof(AzureBlobCacheOptions.ExpiredItemsDeletionInterval)} cannot be negative. Use " +
                "TimeSpan.Zero to disable the sweep.");
        }

        if (options.CompressionThresholdBytes < 0)
        {
            failures.Add($"{nameof(AzureBlobCacheOptions.CompressionThresholdBytes)} cannot be negative.");
        }

        if (string.IsNullOrWhiteSpace(options.PurgeAllTag))
        {
            failures.Add(
                $"{nameof(AzureBlobCacheOptions.PurgeAllTag)} is required: without it no tag would evict " +
                "anything.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9]|-(?=[a-z0-9])){1,61}[a-z0-9]$")]
    private static partial Regex ContainerName();

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex ApplicationName();
}
