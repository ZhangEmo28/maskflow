using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace MaskFlow.Api.Tests;

public class OAuthUnitTests
{
    [Theory]
    [InlineData("https://claude.ai/api/mcp/auth_callback", true)]
    [InlineData("https://chatgpt.com/connector_platform_oauth_redirect", true)]
    [InlineData("http://localhost:33418/callback", true)]
    [InlineData("http://127.0.0.1:8765/cb", true)]
    [InlineData("cursor://anysphere.cursor-retrieval/oauth/callback", true)]
    [InlineData("http://evil.example.com/cb", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("https://claude.ai/cb#fragment", false)]
    [InlineData("/relative", false)]
    public void RedirectUris_AreRestricted(string uri, bool allowed)
    {
        Assert.Equal(allowed, MaskFlowStore.IsAllowedRedirectUri(uri));
    }

    [Fact]
    public void Pkce_MatchesTheRfcExample()
    {
        Assert.True(OAuthCodeStore.VerifyPkce("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk", "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM"));
        Assert.False(OAuthCodeStore.VerifyPkce("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXX", "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM"));
        Assert.False(OAuthCodeStore.VerifyPkce("short", "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM"));
    }
}

public class OAuthFlowTests(MaskFlowApp app) : IClassFixture<MaskFlowApp>
{
    const string Redirect = "https://claude.ai/api/mcp/auth_callback";

    HttpClient NoRedirects() => app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    static (string Verifier, string Challenge) Pkce()
    {
        var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (verifier, challenge);
    }

    async Task<string> RegisterAsync()
    {
        var response = await app.CreateClient().PostAsJsonAsync("/oauth/register", new { client_name = "Claude", redirect_uris = new[] { Redirect } });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("client_id").GetString()!;
    }

    static async Task<string> ApproveAsync(Client user, string clientId, string challenge, bool approve = true)
    {
        var response = await user.Post("/api/oauth/approve", new
        {
            clientId, redirectUri = Redirect, responseType = "code", codeChallenge = challenge, codeChallengeMethod = "S256", state = "xyz", approve
        });
        response.EnsureSuccessStatusCode();
        return (await user.Json(response)).GetProperty("redirect").GetString()!;
    }

    Task<HttpResponseMessage> TokenAsync(Dictionary<string, string> form) => app.CreateClient().PostAsync("/oauth/token", new FormUrlEncodedContent(form));

    static string QueryValue(string url, string name) =>
        new Uri(url).Query.TrimStart('?').Split('&').Select(x => x.Split('=', 2)).First(x => x[0] == name)[1];

    [Fact]
    public async Task Discovery_PointsClientsAtTheAuthorizationServer()
    {
        var http = app.CreateClient();
        var resource = await http.GetFromJsonAsync<JsonElement>("/.well-known/oauth-protected-resource");
        Assert.EndsWith("/mcp", resource.GetProperty("resource").GetString());
        var issuer = resource.GetProperty("authorization_servers")[0].GetString()!;

        var server = await http.GetFromJsonAsync<JsonElement>("/.well-known/oauth-authorization-server");
        Assert.Equal(issuer, server.GetProperty("issuer").GetString());
        Assert.Equal(["S256"], server.GetProperty("code_challenge_methods_supported").EnumerateArray().Select(x => x.GetString()));
        Assert.EndsWith("/oauth/register", server.GetProperty("registration_endpoint").GetString());
    }

    [Fact]
    public async Task Register_RejectsUnsafeRedirects()
    {
        var response = await app.CreateClient().PostAsJsonAsync("/oauth/register", new { client_name = "x", redirect_uris = new[] { "http://evil.example.com/cb" } });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_redirect_uri", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Authorize_ValidatesBeforeShowingConsent()
    {
        var clientId = await RegisterAsync();
        var (_, challenge) = Pkce();
        var http = NoRedirects();

        var ok = await http.GetAsync($"/oauth/authorize?response_type=code&client_id={clientId}&redirect_uri={Uri.EscapeDataString(Redirect)}&code_challenge={challenge}&code_challenge_method=S256&state=s1");
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        Assert.Contains("/oauth.html?response_type=code", ok.Headers.Location!.ToString());

        // An unknown client or unregistered redirect never redirects anywhere.
        Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync($"/oauth/authorize?response_type=code&client_id=nope&redirect_uri={Uri.EscapeDataString(Redirect)}&code_challenge={challenge}&code_challenge_method=S256")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync($"/oauth/authorize?response_type=code&client_id={clientId}&redirect_uri={Uri.EscapeDataString("https://evil.example.com/cb")}&code_challenge={challenge}&code_challenge_method=S256")).StatusCode);

        // Without PKCE the error goes back to the (registered) client.
        var noPkce = await http.GetAsync($"/oauth/authorize?response_type=code&client_id={clientId}&redirect_uri={Uri.EscapeDataString(Redirect)}&state=s2");
        Assert.Equal(HttpStatusCode.Redirect, noPkce.StatusCode);
        Assert.StartsWith($"{Redirect}?error=invalid_request&state=s2", noPkce.Headers.Location!.ToString());
    }

    [Fact]
    public async Task FullFlow_IssuesTokensThatWorkForMcpRefreshAndRevoke()
    {
        var clientId = await RegisterAsync();
        var user = await app.SignUpAsync("oauth");
        var (verifier, challenge) = Pkce();

        var info = await user.Json(await user.Get($"/api/oauth/authorize-info?client_id={clientId}&redirect_uri={Uri.EscapeDataString(Redirect)}&response_type=code&code_challenge={challenge}&code_challenge_method=S256"));
        Assert.Equal(("Claude", "claude.ai"), (info.GetProperty("clientName").GetString(), info.GetProperty("redirectHost").GetString()));

        var redirect = await ApproveAsync(user, clientId, challenge);
        Assert.StartsWith(Redirect + "?code=", redirect);
        Assert.Equal("xyz", QueryValue(redirect, "state"));
        var code = QueryValue(redirect, "code");

        var wrongVerifier = await TokenAsync(new() { ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = Redirect, ["client_id"] = clientId, ["code_verifier"] = Pkce().Verifier });
        Assert.Equal(HttpStatusCode.BadRequest, wrongVerifier.StatusCode);

        // A failed attempt burns the code, so approve again.
        code = QueryValue(await ApproveAsync(user, clientId, challenge), "code");
        var tokenResponse = await TokenAsync(new() { ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = Redirect, ["client_id"] = clientId, ["code_verifier"] = verifier });
        tokenResponse.EnsureSuccessStatusCode();
        var tokens = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>();
        var access = tokens.GetProperty("access_token").GetString()!;
        Assert.StartsWith("mfo_", access);
        Assert.Equal(3600, tokens.GetProperty("expires_in").GetInt32());

        var reuse = await TokenAsync(new() { ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = Redirect, ["client_id"] = clientId, ["code_verifier"] = verifier });
        Assert.Equal("invalid_grant", (await reuse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

        Assert.Equal(HttpStatusCode.OK, (await McpToolsList(access)).StatusCode);

        var refreshed = await (await TokenAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = tokens.GetProperty("refresh_token").GetString()!, ["client_id"] = clientId }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var newAccess = refreshed.GetProperty("access_token").GetString()!;
        Assert.Equal(HttpStatusCode.Unauthorized, (await McpToolsList(access)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await McpToolsList(newAccess)).StatusCode);
        var replay = await TokenAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = tokens.GetProperty("refresh_token").GetString()!, ["client_id"] = clientId });
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);

        var grants = (await user.Json(await user.Get("/api/account/oauth-grants"))).GetProperty("grants");
        var grant = Assert.Single(grants.EnumerateArray());
        Assert.Equal("Claude", grant.GetProperty("clientName").GetString());
        (await user.Delete($"/api/account/oauth-grants/{grant.GetProperty("id").GetString()}")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await McpToolsList(newAccess)).StatusCode);
    }

    [Fact]
    public async Task Deny_ReturnsAccessDenied()
    {
        var clientId = await RegisterAsync();
        var user = await app.SignUpAsync("deny");
        var redirect = await ApproveAsync(user, clientId, Pkce().Challenge, approve: false);
        Assert.Equal($"{Redirect}?error=access_denied&state=xyz", redirect);
    }

    async Task<HttpResponseMessage> McpToolsList(string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""", Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.Add("MCP-Protocol-Version", "2025-06-18");
        return await app.CreateClient().SendAsync(request);
    }
}
