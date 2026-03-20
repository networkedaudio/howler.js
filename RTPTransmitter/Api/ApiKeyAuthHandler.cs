using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace RTPTransmitter.Api;

/// <summary>
/// Simple API-key authentication handler.
/// Clients pass the key via the <c>X-Api-Key</c> header.
/// The expected key is read from <c>Api:ApiKey</c> in configuration.
/// </summary>
public sealed class ApiKeyAuthHandler : AuthenticationHandler<ApiKeyAuthOptions>
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";

    private readonly IConfiguration _configuration;

    public ApiKeyAuthHandler(
        IOptionsMonitor<ApiKeyAuthOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IConfiguration configuration)
        : base(options, logger, encoder)
    {
        _configuration = configuration;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var headerValue))
            return Task.FromResult(AuthenticateResult.Fail("Missing X-Api-Key header."));

        var expectedKey = _configuration["Api:ApiKey"];
        if (string.IsNullOrWhiteSpace(expectedKey) || expectedKey == "CHANGE-ME-TO-A-SECURE-KEY")
            return Task.FromResult(AuthenticateResult.Fail("API key is not configured on the server."));

        if (!string.Equals(headerValue, expectedKey, StringComparison.Ordinal))
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));

        var identity = new ClaimsIdentity(SchemeName);
        identity.AddClaim(new Claim(ClaimTypes.Name, "ApiClient"));
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

/// <summary>
/// Options placeholder for the API key auth scheme (no additional options needed).
/// </summary>
public sealed class ApiKeyAuthOptions : AuthenticationSchemeOptions
{
}
