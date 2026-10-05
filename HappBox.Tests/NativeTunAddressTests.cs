using HappBox;
using Xunit;

namespace HappBox.Tests;

public sealed class NativeTunAddressTests
{
    [Theory]
    [InlineData("172.29.91.1/30", "172.29.91.2")]
    [InlineData("172.29.91.2/30", "172.29.91.1")]
    [InlineData("10.7.0.9/24", "10.7.0.1")]
    public void AdapterHostBitsAreValidAndDnsUsesAnotherHost(string address, string dns)
    {
        Assert.Equal(dns, NativeTunAddress.Parse(address).Dns.ToString());
    }
    [Theory]
    [InlineData("172.29.91.0/30")]
    [InlineData("172.29.91.3/30")]
    [InlineData("172.29.91.1/31")]
    [InlineData("127.0.0.1/24")]
    [InlineData("172.29.91.1/30'; bad")]
    [InlineData("::1/64")]
    public void InvalidAddressFailsBeforeAnyNetworkMutation(string address)
    {
        Assert.Throws<InvalidOperationException>(() => NativeTunAddress.Parse(address));
    }
}
