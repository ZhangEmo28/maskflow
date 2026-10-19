using System.Net;
using ProxyNetwork = Microsoft.AspNetCore.HttpOverrides.IPNetwork;

namespace MaskFlow.Api.Infrastructure.Security;

public static class TrustedProxies
{
    // Loopback plus private ranges cover the bundled nginx container on Docker networks.
    // Tighten with MASKFLOW_TRUSTED_PROXIES (comma-separated CIDRs) when the API is reachable from a LAN.
    public const string Default = "127.0.0.0/8,::1/128,10.0.0.0/8,172.16.0.0/12,192.168.0.0/16,fc00::/7";

    public static IReadOnlyList<ProxyNetwork> Parse(string? value)
    {
        var raw = string.IsNullOrWhiteSpace(value) ? Default : value;
        var networks = new List<ProxyNetwork>();
        foreach (var entry in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var slash = entry.IndexOf('/');
            var addressText = slash < 0 ? entry : entry[..slash];
            if (!IPAddress.TryParse(addressText, out var address))
            {
                throw new InvalidOperationException($"MASKFLOW_TRUSTED_PROXIES contains an invalid address: {entry}");
            }

            var maxPrefix = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;
            var prefix = maxPrefix;
            if (slash >= 0 && (!int.TryParse(entry[(slash + 1)..], out prefix) || prefix < 0 || prefix > maxPrefix))
            {
                throw new InvalidOperationException($"MASKFLOW_TRUSTED_PROXIES contains an invalid prefix: {entry}");
            }

            networks.Add(new ProxyNetwork(address, prefix));
        }

        return networks;
    }
}
