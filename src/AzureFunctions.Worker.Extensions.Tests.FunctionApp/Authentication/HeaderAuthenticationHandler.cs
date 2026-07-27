using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;

namespace AzureFunctions.Worker.Extensions.Tests.FunctionApp.Authentication;

/// <summary>
/// Deterministic authentication scheme for tests: a request is authenticated when it carries
/// the "X-Test-Auth: valid" header; anything else produces the default 401 challenge.
/// </summary>
public class HeaderAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "TestHeader";

    public const string HeaderName = "X-Test-Auth";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (this.Request.Headers.TryGetValue(HeaderName, out var headerValue) && headerValue == "valid")
        {
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "test-user")], this.Scheme.Name);
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), this.Scheme.Name);

            return Task.FromResult(AuthenticateResult.Success(ticket));
        }

        return Task.FromResult(AuthenticateResult.NoResult());
    }
}
