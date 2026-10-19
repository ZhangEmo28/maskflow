using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

// OAuth 2.1 authorization server for MCP connectors (ChatGPT, claude.ai, …): dynamic client
// registration, authorization code + PKCE (S256), short-lived access tokens with rotating refresh tokens.
public sealed partial class MaskFlowStore
{
    public static readonly TimeSpan OAuthAccessLifetime = TimeSpan.FromHours(1);
    public static readonly TimeSpan OAuthRefreshLifetime = TimeSpan.FromDays(30);
    const int MaxOAuthClients = 2000;

    // https anywhere, http only on loopback (RFC 8252), or a private-use scheme of a native app.
    public static bool IsAllowedRedirectUri(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.Fragment)) return false;
        return uri.Scheme switch
        {
            "https" => true,
            "http" => uri.IsLoopback,
            "javascript" or "data" or "file" or "vbscript" or "about" or "blob" or "ftp" or "ws" or "wss" => false,
            _ => uri.Scheme.Contains('.') || uri.Scheme.Length >= 3
        };
    }

    public async Task<OAuthClient> RegisterOAuthClientAsync(string? name, IReadOnlyList<string>? redirectUris)
    {
        if (redirectUris is not { Count: > 0 } || redirectUris.Count > 10 || !redirectUris.All(IsAllowedRedirectUri))
        {
            throw new BadHttpRequestException("invalid_redirect_uri", 400);
        }

        var client = new OAuthClient("mcp_" + Util.Id() + Util.Id(), string.IsNullOrWhiteSpace(name) ? "MCP 客户端" : name.Trim()[..Math.Min(name.Trim().Length, 100)],
            redirectUris.Distinct().ToList(), DateTimeOffset.UtcNow);
        await SaveAsync(() =>
        {
            // Registration is open to anyone, so unused registrations are pruned and the total is capped.
            var used = State.OAuthGrants.Select(x => x.ClientId).ToHashSet();
            State.OAuthClients.RemoveAll(x => !used.Contains(x.Id) && x.CreatedAt < DateTimeOffset.UtcNow.AddDays(-1));
            if (State.OAuthClients.Count >= MaxOAuthClients) throw new BadHttpRequestException("too_many_clients", 503);
            State.OAuthClients.Add(client);
        });
        return client;
    }

    public OAuthClient? GetOAuthClient(string? clientId) => State.OAuthClients.FirstOrDefault(x => x.Id == clientId);

    public async Task<(OAuthGrant Grant, string AccessToken, string RefreshToken)> CreateOAuthGrantAsync(string clientId, int userId)
    {
        var (access, refresh) = (NewToken("mfo_"), NewToken("mfr_"));
        var now = DateTimeOffset.UtcNow;
        var grant = new OAuthGrant("grant_" + Util.Id(), clientId, userId, Util.Sha256(access), now + OAuthAccessLifetime,
            Util.Sha256(refresh), now + OAuthRefreshLifetime, now, null);
        await SaveAsync(() => State.OAuthGrants.Add(grant));
        return (grant, access, refresh);
    }

    // Rotation: the presented refresh token is replaced, so a stolen one stops working after the next refresh.
    public async Task<(OAuthGrant Grant, string AccessToken, string RefreshToken)?> RefreshOAuthGrantAsync(string clientId, string refreshToken)
    {
        var hash = Util.Sha256(refreshToken);
        var (access, refresh) = (NewToken("mfo_"), NewToken("mfr_"));
        OAuthGrant? updated = null;
        await SaveAsync(() =>
        {
            var index = State.OAuthGrants.FindIndex(x => x.RefreshTokenHash == hash && x.ClientId == clientId);
            if (index < 0) return;
            var grant = State.OAuthGrants[index];
            var now = DateTimeOffset.UtcNow;
            if (grant.RevokedAt is not null || grant.RefreshExpiresAt < now || GetUser(grant.UserId) is null) return;
            updated = grant with
            {
                AccessTokenHash = Util.Sha256(access), AccessExpiresAt = now + OAuthAccessLifetime,
                RefreshTokenHash = Util.Sha256(refresh), RefreshExpiresAt = now + OAuthRefreshLifetime
            };
            State.OAuthGrants[index] = updated;
        });
        return updated is null ? null : (updated, access, refresh);
    }

    public User? UserForOAuthAccessToken(string tokenHash)
    {
        var grant = State.OAuthGrants.FirstOrDefault(x => x.AccessTokenHash == tokenHash);
        return grant is { RevokedAt: null } && grant.AccessExpiresAt > DateTimeOffset.UtcNow ? GetUser(grant.UserId) : null;
    }

    public IEnumerable<object> ListOAuthGrants(int userId) =>
        State.OAuthGrants.Where(x => x.UserId == userId && x.RevokedAt is null && x.RefreshExpiresAt > DateTimeOffset.UtcNow)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x =>
            {
                var client = GetOAuthClient(x.ClientId);
                return (object)new
                {
                    x.Id,
                    clientName = client?.Name ?? "已删除的客户端",
                    redirectHost = client?.RedirectUris.Select(uri => Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ? parsed.Authority : uri).FirstOrDefault(),
                    x.CreatedAt,
                    expiresAt = x.RefreshExpiresAt
                };
            });

    public async Task<bool> RevokeOAuthGrantAsync(int userId, string grantId)
    {
        var revoked = false;
        await SaveAsync(() =>
        {
            var index = State.OAuthGrants.FindIndex(x => x.Id == grantId && x.UserId == userId && x.RevokedAt is null);
            if (index < 0) return;
            State.OAuthGrants[index] = State.OAuthGrants[index] with { RevokedAt = DateTimeOffset.UtcNow };
            revoked = true;
        });
        return revoked;
    }

    static string NewToken(string prefix) => prefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
}

public sealed record PendingAuthorization(string ClientId, string RedirectUri, string CodeChallenge, int UserId, string? Scope, DateTimeOffset ExpiresAt);

// Authorization codes live for five minutes and are used once, so memory is enough; a restart
// simply makes the user approve again.
public sealed class OAuthCodeStore
{
    static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    readonly ConcurrentDictionary<string, PendingAuthorization> codes = new();

    public string Issue(string clientId, string redirectUri, string codeChallenge, int userId, string? scope)
    {
        foreach (var (key, value) in codes)
        {
            if (value.ExpiresAt < DateTimeOffset.UtcNow) codes.TryRemove(key, out _);
        }

        var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        codes[Util.Sha256(code)] = new PendingAuthorization(clientId, redirectUri, codeChallenge, userId, scope, DateTimeOffset.UtcNow + Lifetime);
        return code;
    }

    public PendingAuthorization? Redeem(string? code) =>
        code is not null && codes.TryRemove(Util.Sha256(code), out var pending) && pending.ExpiresAt >= DateTimeOffset.UtcNow ? pending : null;

    public static bool VerifyPkce(string verifier, string challenge)
    {
        // RFC 7636: 43-128 unreserved characters; S256 = BASE64URL(SHA256(verifier)).
        if (verifier.Length is < 43 or > 128) return false;
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        var computed = Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(computed), Encoding.ASCII.GetBytes(challenge));
    }
}
