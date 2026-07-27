using System.Net;
using AzureFunctions.TestFramework.Core;
using AzureFunctions.TestFramework.Http;
using AzureFunctions.Worker.Extensions.Tests.FunctionApp.Authentication;
using AzureFunctions.Worker.Extensions.Tests.FunctionApp.Functions;

namespace AzureFunctions.Worker.Extensions.Tests;

/// <summary>
/// In-memory end-to-end tests for the AspNetCore integration worker middleware, hosted via
/// AzureFunctions.TestFramework (the real worker pipeline over an in-memory TestServer —
/// no Core Tools, no Docker, no TCP ports).
/// </summary>
public sealed class AspNetCoreIntegrationMiddlewareTests : IAsyncLifetime
{
    private IFunctionsTestHost testHost = null!;
    private HttpClient client = null!;

    public async Task InitializeAsync()
    {
        this.testHost = new FunctionsTestHostBuilder()
            .WithFunctionsAssembly(typeof(VoidFunctions).Assembly)
            .WithHostApplicationBuilderFactory(Program.CreateApplicationBuilder)
            .Build();

        await this.testHost.StartAsync();

        this.client = this.testHost.CreateHttpClient();
    }

    public async Task DisposeAsync()
    {
        this.client.Dispose();

        await this.testHost.DisposeAsync();
    }

    /// <summary>
    /// A Task-returning (void) HTTP function behind [Authorize] must surface the 401
    /// authentication challenge — the middleware's NoContent fallback used to overwrite
    /// the unstarted challenge response with 204.
    /// </summary>
    [Fact]
    public async Task VoidFunction_FailedAuthentication_Returns401()
    {
        var response = await this.client.GetAsync("/api/void/authorized");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// The same function with valid authentication completes as a genuine void invocation:
    /// the NoContent fallback still applies.
    /// </summary>
    [Fact]
    public async Task VoidFunction_SuccessfulAuthentication_Returns204()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/void/authorized");

        request.Headers.Add(HeaderAuthenticationHandler.HeaderName, "valid");

        var response = await this.client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    /// <summary>
    /// Anonymous void functions keep the 204 fallback untouched.
    /// </summary>
    [Fact]
    public async Task AnonymousVoidFunction_Returns204()
    {
        var response = await this.client.GetAsync("/api/void/anonymous");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    /// <summary>
    /// Result-returning functions are unaffected by the fallback.
    /// </summary>
    [Fact]
    public async Task ResultFunction_ReturnsPayload()
    {
        var response = await this.client.GetAsync("/api/result/anonymous");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("ok", await response.Content.ReadAsStringAsync());
    }
}
