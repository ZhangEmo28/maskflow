using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;

// Public OAuth endpoints (discovery, registration, authorize redirect, token). The consent itself
// happens in the web app (/oauth.html), which calls OAuthController with the user's session.
public static class OAuthEndpoints
{
    public const string Scope = "maskflow";

    // The address clients see. Behind nginx the API is reached through the web origin, so prefer
    // MASKFLOW_PUBLIC_URL; otherwise use the (forwarded) request host.
    public static string PublicBase(HttpContext context) =>
        Environment.GetEnvironmentVariable("MASKFLOW_PUBLIC_URL") is { Length: > 0 } configured
            ? configured.TrimEnd('/')
            : $"{context.Request.Scheme}://{context.Request.Host}";

    public sealed record RegistrationRequest(
        [property: JsonPropertyName("redirect_uris")] List<string>? RedirectUris,
        [property: JsonPropertyName("client_name")] string? ClientName,
        [property: JsonPropertyName("token_endpoint_auth_method")] string? TokenEndpointAuthMethod);

    public static WebApplication MapMaskFlowOAuth(this WebApplication app)
    {
        object ResourceMetadata(HttpContext context) => new
        {
            resource = $"{PublicBase(context)}{McpSetup.Path}",
            authorization_servers = new[] { PublicBase(context) },
            bearer_methods_supported = new[] { "header" },
            scopes_supported = new[] { Scope },
            resource_name = "MaskFlow",
            resource_documentation = $"{PublicBase(context)}/settings.html"
        };
        app.MapGet("/.well-known/oauth-protected-resource", (HttpContext context) => Results.Json(ResourceMetadata(context)));
        app.MapGet("/.well-known/oauth-protected-resource/mcp", (HttpContext context) => Results.Json(ResourceMetadata(context)));

        app.MapGet("/.well-known/oauth-authorization-server", (HttpContext context) =>
        {
            var issuer = PublicBase(context);
            return Results.Json(new
            {
                issuer,
                authorization_endpoint = $"{issuer}/oauth/authorize",
                token_endpoint = $"{issuer}/oauth/token",
                registration_endpoint = $"{issuer}/oauth/register",
                response_types_supported = new[] { "code" },
                grant_types_supported = new[] { "authorization_code", "refresh_token" },
                code_challenge_methods_supported = new[] { "S256" },
                token_endpoint_auth_methods_supported = new[] { "none" },
                scopes_supported = new[] { Scope }
            });
        });

        // RFC 7591 dynamic client registration; only public clients (PKCE, no secret).
        app.MapPost("/oauth/register", async ([FromBody] RegistrationRequest request, MaskFlowStore store) =>
        {
            if (request.TokenEndpointAuthMethod is not (null or "none"))
            {
                return Error("invalid_client_metadata", "Only public clients (token_endpoint_auth_method=none) are supported.");
            }

            try
            {
                var client = await store.RegisterOAuthClientAsync(request.ClientName, request.RedirectUris);
                return Results.Json(new
                {
                    client_id = client.Id,
                    client_id_issued_at = client.CreatedAt.ToUnixTimeSeconds(),
                    client_name = client.Name,
                    redirect_uris = client.RedirectUris,
                    token_endpoint_auth_method = "none",
                    grant_types = new[] { "authorization_code", "refresh_token" },
                    response_types = new[] { "code" },
                    scope = Scope
                }, statusCode: 201);
            }
            catch (BadHttpRequestException ex)
            {
                return ex.StatusCode == 503
                    ? Results.Json(new { error = "temporarily_unavailable" }, statusCode: 503)
                    : Error("invalid_redirect_uri", "redirect_uris must be https, http on localhost, or an app scheme.");
            }
        });

        // Validates the request, then hands over to the consent page in the web app.
        app.MapGet("/oauth/authorize", (HttpContext context, MaskFlowStore store) =>
        {
            var query = context.Request.Query;
            var problem = ValidateAuthorization(store, query["client_id"], query["redirect_uri"], query["response_type"], query["code_challenge"], query["code_challenge_method"]);
            if (problem is { Redirect: false }) return Results.BadRequest(new { error = problem.Value.Error, error_description = problem.Value.Description });
            if (problem is { } redirectable) return Results.Redirect(ErrorRedirect(query["redirect_uri"]!, redirectable.Error, query["state"]));
            return Results.Redirect($"{PublicBase(context)}/oauth.html{context.Request.QueryString}");
        });

        app.MapPost("/oauth/token", async (HttpContext context, MaskFlowStore store, OAuthCodeStore codes) =>
        {
            if (!context.Request.HasFormContentType) return Error("invalid_request", "Use application/x-www-form-urlencoded.");
            var form = await context.Request.ReadFormAsync();
            var clientId = form["client_id"].ToString();
            if (store.GetOAuthClient(clientId) is null) return Results.Json(new { error = "invalid_client" }, statusCode: 401);

            switch (form["grant_type"].ToString())
            {
                case "authorization_code":
                {
                    var pending = codes.Redeem(form["code"]);
                    if (pending is null || pending.ClientId != clientId || pending.RedirectUri != form["redirect_uri"].ToString())
                    {
                        return Error("invalid_grant", "The authorization code is invalid, expired or was already used.");
                    }

                    if (!OAuthCodeStore.VerifyPkce(form["code_verifier"].ToString(), pending.CodeChallenge))
                    {
                        return Error("invalid_grant", "PKCE verification failed.");
                    }

                    var (_, access, refresh) = await store.CreateOAuthGrantAsync(clientId, pending.UserId);
                    return Tokens(access, refresh);
                }
                case "refresh_token":
                {
                    var refreshed = await store.RefreshOAuthGrantAsync(clientId, form["refresh_token"].ToString());
                    return refreshed is { } value ? Tokens(value.AccessToken, value.RefreshToken) : Error("invalid_grant", "The refresh token is invalid or expired.");
                }
                default:
                    return Error("unsupported_grant_type", "Use authorization_code or refresh_token.");
            }
        });

        return app;
    }

    // Errors about the client or redirect URI must not redirect (the URI is untrusted); the rest go back to the client.
    public static (string Error, string Description, bool Redirect)? ValidateAuthorization(MaskFlowStore store, string? clientId, string? redirectUri,
        string? responseType, string? codeChallenge, string? codeChallengeMethod)
    {
        var client = store.GetOAuthClient(clientId);
        if (client is null) return ("invalid_client", "Unknown client_id; register the client first.", false);
        if (redirectUri is null || !client.RedirectUris.Contains(redirectUri)) return ("invalid_request", "redirect_uri does not match the registered URIs.", false);
        if (responseType != "code") return ("unsupported_response_type", "Only response_type=code is supported.", true);
        if (string.IsNullOrEmpty(codeChallenge) || codeChallengeMethod != "S256") return ("invalid_request", "PKCE with code_challenge_method=S256 is required.", true);
        return null;
    }

    public static string ErrorRedirect(string redirectUri, string error, string? state) =>
        QueryAppend(redirectUri, new() { ["error"] = error, ["state"] = state });

    public static string QueryAppend(string uri, Dictionary<string, string?> values)
    {
        var query = string.Join("&", values.Where(x => !string.IsNullOrEmpty(x.Value)).Select(x => $"{x.Key}={Uri.EscapeDataString(x.Value!)}"));
        return query.Length == 0 ? uri : $"{uri}{(uri.Contains('?') ? '&' : '?')}{query}";
    }

    static IResult Tokens(string access, string refresh) => Results.Json(new
    {
        access_token = access,
        token_type = "Bearer",
        expires_in = (int)MaskFlowStore.OAuthAccessLifetime.TotalSeconds,
        refresh_token = refresh,
        scope = Scope
    }, statusCode: 200);

    static IResult Error(string error, string description) => Results.Json(new { error, error_description = description }, statusCode: 400);
}
