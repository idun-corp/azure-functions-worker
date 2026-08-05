# Azure Functions Worker (isolated) extensions

Boost your productivity with better ASP.NET Core framework integration like in old good MVC days.

> **Consuming these packages:** they are published to GitHub Packages, which requires authentication
> even to restore, using a *classic* personal access token. See the
> [NuGet migration guide](https://github.com/idun-corp/shared-packages/blob/main/docs/nuget-migration.md)
> for the one-time setup. Versions **3.1.0 and later** come from GitHub Packages; earlier ones remain
> on the read-only Azure Artifacts feed.

## [Standalone Application Insights](src/AzureFunctions.Worker.Extensions.ApplicationInsights/readme.md)
- Completely opt-out worker host logging to Application insights
- Have control over `RequestTelemetry` item in your code
- Debloat worker host logs
- Keep Application insights metrics and perf counters

## [ASP.NET Core middleware support](src/AzureFunctions.Worker.Extensions.AspNetCore/readme.md)
- Use ASP.NET Core built-in middlewares: UseAuthentication, UseAuthorization, or any other custom ones
- Use ASP.NET Core built-in bindings: FromQuery, FromBody, IFormFile, or any other custom ones
- Have full IActionResult/IResult integration as in MVC

## [Swashbuckle API explorer for Azure Functions](src/AzureFunctions.Worker.Extensions.Swashbuckle/readme.md)
- Use Swashbuckle (Swagger UI) api explorer with zero midifications to your code
- Get full Swagger extensibility as in MVC

## [Distributed caching with Azure Table storage](src/AzureFunctions.Worker.Extensions.Caching.AzureTable/readme.md)
- Use Azure Table as distributed cache provider
- Full support for `IDistributedCache` interface
- JSON serialization extensions with host `System.Text.Json` serializer options as default
- Search cache entries or bulk set/delete operations support

# 🔴Migration to v2 Azure Functions SDK🔴

There are some changes in the API for bootstrapping worker, therefore extensions should be registered differently,
please reffer to migration block for each extension package separately 
