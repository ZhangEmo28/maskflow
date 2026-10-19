using Microsoft.AspNetCore.Mvc;

public sealed record OAuthApproveRequest(string ClientId, string RedirectUri, string ResponseType, string CodeChallenge, string CodeChallengeMethod,
    string? State, string? Scope, bool Approve);

// Consent for MCP connectors, called by the web app's /oauth.html with the signed-in user's session,
// plus the "已连接的应用" list in account settings.
[Tags("OAuth")]
public sealed class OAuthController(MaskFlowStore store, OAuthCodeStore codes) : MaskFlowControllerBase(store)
{
    [HttpGet("/api/oauth/authorize-info")]
    public IActionResult Info([FromQuery(Name = "client_id")] string? clientId, [FromQuery(Name = "redirect_uri")] string? redirectUri,
        [FromQuery(Name = "response_type")] string? responseType, [FromQuery(Name = "code_challenge")] string? codeChallenge,
        [FromQuery(Name = "code_challenge_method")] string? codeChallengeMethod)
    {
        CurrentUser();
        var problem = OAuthEndpoints.ValidateAuthorization(Store, clientId, redirectUri, responseType, codeChallenge, codeChallengeMethod);
        if (problem is { } error) return BadRequest(new { detail = $"授权请求无效：{error.Description}" });
        var client = Store.GetOAuthClient(clientId)!;
        return Ok(new { clientName = client.Name, redirectHost = new Uri(redirectUri!).Authority, redirectUri });
    }

    [HttpPost("/api/oauth/approve")]
    public IActionResult Approve([FromBody] OAuthApproveRequest request)
    {
        var user = CurrentUser();
        var problem = OAuthEndpoints.ValidateAuthorization(Store, request.ClientId, request.RedirectUri, request.ResponseType, request.CodeChallenge, request.CodeChallengeMethod);
        if (problem is { } error) return BadRequest(new { detail = $"授权请求无效：{error.Description}" });
        if (!request.Approve) return Ok(new { redirect = OAuthEndpoints.ErrorRedirect(request.RedirectUri, "access_denied", request.State) });
        var code = codes.Issue(request.ClientId, request.RedirectUri, request.CodeChallenge, user.Id, request.Scope);
        return Ok(new { redirect = OAuthEndpoints.QueryAppend(request.RedirectUri, new() { ["code"] = code, ["state"] = request.State }) });
    }

    [HttpGet("/api/account/oauth-grants")]
    public IActionResult Grants() => Ok(new { grants = Store.ListOAuthGrants(CurrentUser().Id) });

    [HttpDelete("/api/account/oauth-grants/{grantId}")]
    public async Task<IActionResult> Revoke(string grantId) =>
        await Store.RevokeOAuthGrantAsync(CurrentUser().Id, grantId) ? Ok(new { ok = true }) : NotFound(new { detail = "Grant not found." });
}
