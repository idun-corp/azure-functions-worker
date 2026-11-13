using AzureFunctions.Worker.Extensions.Caching.AzureBlob.Internal;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers Azure Blob Storage as a cache backend.
/// </summary>
public static class AzureBlobCacheDependencyInjectionExtensions
{
    /// <summary>
    /// Adds Azure Blob Storage as a distributed cache.
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="configureCacheOptions">Optional post configure options</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddAzureBlobCache(
        this IServiceCollection services,
        Action<AzureBlobCacheOptions>? configureCacheOptions = null)
    {
        return services.AddAzureBlobCache(configuration: null, configureCacheOptions);
    }

    /// <summary>
    /// Adds Azure Blob Storage as a distributed cache.
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="configuration">Configuration to bind <see cref="AzureBlobCacheOptions"/></param>
    /// <param name="configureCacheOptions">Optional post configure options</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddAzureBlobCache(
        this IServiceCollection services,
        IConfiguration? configuration,
        Action<AzureBlobCacheOptions>? configureCacheOptions = null)
    {
        services.AddBlobCacheOptions(configuration, configureCacheOptions);

        // Registered rather than tried, so it wins over any cache registered earlier, matching the
        // Azure Table cache. HybridCache then picks this up as its level two automatically.
        services.AddSingleton<IDistributedCache, AzureBlobCache>();

        return services;
    }

    /// <summary>
    /// Adds Azure Blob Storage as the store behind ASP.NET Core output caching.
    /// </summary>
    /// <remarks>
    /// Pair with <c>AddOutputCache</c> and <c>UseOutputCache</c>. Everything output caching provides
    /// on top of the store - named policies, vary-by rules, automatic revalidation and locking -
    /// applies unchanged.
    /// </remarks>
    /// <param name="services">Service collection</param>
    /// <param name="configureCacheOptions">Optional post configure options</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddAzureBlobOutputCache(
        this IServiceCollection services,
        Action<AzureBlobCacheOptions>? configureCacheOptions = null)
    {
        return services.AddAzureBlobOutputCache(configuration: null, configureCacheOptions);
    }

    /// <summary>
    /// Adds Azure Blob Storage as the store behind ASP.NET Core output caching.
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="configuration">Configuration to bind <see cref="AzureBlobCacheOptions"/></param>
    /// <param name="configureCacheOptions">Optional post configure options</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddAzureBlobOutputCache(
        this IServiceCollection services,
        IConfiguration? configuration,
        Action<AzureBlobCacheOptions>? configureCacheOptions = null)
    {
        services.AddBlobCacheOptions(configuration, configureCacheOptions);

        services.AddSingleton<IOutputCacheStore, AzureBlobOutputCacheStore>();

        return services;
    }

    private static void AddBlobCacheOptions(
        this IServiceCollection services,
        IConfiguration? configuration,
        Action<AzureBlobCacheOptions>? configureCacheOptions)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<AzureBlobCacheOptions>, AzureBlobCacheOptionsValidator>());

        services
            .AddOptions<AzureBlobCacheOptions>()
            .Configure(cacheOptions =>
            {
                configuration?.Bind(cacheOptions);

                configureCacheOptions?.Invoke(cacheOptions);

                // The storage a Functions app already has. Falling back to it means a host that has
                // configured AzureWebJobsStorage needs no cache configuration at all.
                cacheOptions.ConnectionString ??= configuration?.GetValue<string>("AzureWebJobsStorage")
                    ?? Environment.GetEnvironmentVariable("AzureWebJobsStorage");
            })
            .ValidateOnStart();
    }
}
