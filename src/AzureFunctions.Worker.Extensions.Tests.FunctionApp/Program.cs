using AzureFunctions.Worker.Extensions.Tests.FunctionApp.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

await Program.CreateApplicationBuilder(args).Build().RunAsync();

/// <summary>
/// Minimal function app exercising the AspNetCore integration extensions,
/// hosted in-memory by the tests via AzureFunctions.TestFramework.
/// </summary>
public partial class Program
{
    public static FunctionsApplicationBuilder CreateApplicationBuilder(string[] args)
    {
        var builder = FunctionsApplication.CreateBuilder(args);

        builder.ConfigureFunctionsWebApplication();

        builder.ConfigureAspNetCoreMvcIntegration();

        builder.UseAspNetCoreMiddleware(app =>
        {
            app.UseAuthentication();
            app.UseAuthorization();
        });

        builder.Services
            .AddAuthentication(HeaderAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, HeaderAuthenticationHandler>(HeaderAuthenticationHandler.SchemeName, configureOptions: null);

        builder.Services.AddAuthorization();

        return builder;
    }
}
