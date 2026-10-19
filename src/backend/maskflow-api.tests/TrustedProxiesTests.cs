using System.Net;
using MaskFlow.Api.Infrastructure.Security;

namespace MaskFlow.Api.Tests;

public class TrustedProxiesTests
{
    [Fact]
    public void Default_CoversLoopbackAndDockerNetworks()
    {
        var networks = TrustedProxies.Parse(null);
        Assert.Contains(networks, x => x.Contains(IPAddress.Parse("172.18.0.5")));
        Assert.Contains(networks, x => x.Contains(IPAddress.Loopback));
        Assert.DoesNotContain(networks, x => x.Contains(IPAddress.Parse("8.8.8.8")));
    }

    [Fact]
    public void Custom_ReplacesDefaultsAndAcceptsSingleAddresses()
    {
        var networks = TrustedProxies.Parse("10.1.2.3, 192.168.5.0/24");
        Assert.Equal(2, networks.Count);
        Assert.True(networks[0].Contains(IPAddress.Parse("10.1.2.3")));
        Assert.False(networks[0].Contains(IPAddress.Parse("10.1.2.4")));
        Assert.DoesNotContain(networks, x => x.Contains(IPAddress.Parse("172.18.0.5")));
    }

    [Theory]
    [InlineData("not-an-ip")]
    [InlineData("10.0.0.0/40")]
    public void Invalid_Throws(string value)
    {
        Assert.Throws<InvalidOperationException>(() => TrustedProxies.Parse(value));
    }
}
