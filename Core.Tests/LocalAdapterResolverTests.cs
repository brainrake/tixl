using System.Net;
using System.Net.NetworkInformation;
using Io.Utils;
using Xunit;

namespace Core.Tests;

public class LocalAdapterResolverTests
{
    [Fact]
    public void ExactLocalMatchWinsOverOtherSubnetCandidates()
    {
        var resolver = Resolver(Adapter("10.1.2.3"), Adapter("10.1.2.4"), Adapter("192.168.1.2"));
        resolver.Update("10.1.2.3", "192.168.1.50");
        Assert.Equal(IPAddress.Parse("10.1.2.3"), resolver.EffectiveAddress);
        Assert.Equal("10.1.2.3", resolver.DisplayValue);
        Assert.Null(resolver.ErrorMessage);
    }

    [Fact]
    public void UniqueConfiguredSubnetWinsOverTargetSubnetAndKeepsSavedValueVisible()
    {
        var resolver = Resolver(Adapter("10.1.2.3"), Adapter("192.168.1.2"));
        resolver.Update("10.1.2.99", "192.168.1.50");
        Assert.Equal(IPAddress.Parse("10.1.2.3"), resolver.EffectiveAddress);
        Assert.Equal("10.1.2.99 (effective: 10.1.2.3)", resolver.DisplayValue);
    }

    [Fact]
    public void UniqueTargetSubnetIsFallbackOnlyWhenConfiguredSubnetHasNoMatch()
    {
        var resolver = Resolver(Adapter("10.1.2.3"), Adapter("192.168.1.2"));
        resolver.Update("172.16.5.99", "192.168.1.50");
        Assert.Equal(IPAddress.Parse("192.168.1.2"), resolver.EffectiveAddress);
        Assert.Null(resolver.ErrorMessage);
    }

    [Theory]
    [InlineData("10.1.2.99", "192.168.1.50")]
    [InlineData("172.16.5.99", "10.1.2.50")]
    public void AmbiguousSubnetNeverChoosesFirstAdapter(string saved, string target)
    {
        var resolver = Resolver(Adapter("10.1.2.3"), Adapter("10.1.2.4"), Adapter("192.168.1.2"));
        resolver.Update(saved, target);
        Assert.Null(resolver.EffectiveAddress);
        Assert.NotNull(resolver.ErrorMessage);
    }

    [Theory]
    [InlineData("")]
    [InlineData("255.255.255.255")]
    [InlineData("239.255.0.1")]
    [InlineData("127.0.0.1")]
    [InlineData("192.168.1.255")]
    [InlineData("192.168.1.0")]
    public void MissingMatchDoesNotFallBackToOnlyAvailableNic(string target)
    {
        var resolver = Resolver(Adapter("192.168.1.2"));
        resolver.Update("10.1.2.99", target);
        Assert.Null(resolver.EffectiveAddress);
        Assert.NotNull(resolver.ErrorMessage);
    }

    [Fact]
    public void MatchingUsesLocalAdapterMaskInsteadOfAssumingSlash24()
    {
        var resolver = Resolver(Adapter("10.1.2.3", "255.255.0.0"));
        resolver.Update("10.1.9.99", "");
        Assert.Equal(IPAddress.Parse("10.1.2.3"), resolver.EffectiveAddress);
        Assert.Equal(IPAddress.Parse("255.255.0.0"), resolver.SubnetMask);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("255.0.255.0")]
    public void InvalidOrDefaultRouteMasksDoNotJustifyGuessing(string mask)
    {
        var resolver = Resolver(Adapter("192.168.1.2", mask));
        resolver.Update("10.1.2.99", "192.168.1.50");
        Assert.Null(resolver.EffectiveAddress);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("not-an-ip")]
    [InlineData("::1")]
    public void EmptyAndInvalidInputsRemainUnbound(string? saved)
    {
        var resolver = Resolver(Adapter("10.1.2.3"));
        resolver.Update(saved, "10.1.2.50");
        Assert.Null(resolver.EffectiveAddress);
        Assert.NotNull(resolver.ErrorMessage);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.2")]
    public void WildcardAndExplicitLoopbackRemainUsableWithoutAdapterMatches(string saved)
    {
        var resolver = Resolver();
        resolver.Update(saved, "");
        Assert.Equal(IPAddress.Parse(saved), resolver.EffectiveAddress);
        Assert.Null(resolver.ErrorMessage);
    }

    [Fact]
    public void SnapshotChangesWaitForReconnectAndPropagateAddressAndMask()
    {
        LocalAdapterResolver.Adapter[] snapshot = [Adapter("10.1.2.3")];
        int enumerationCount = 0;
        var resolver = new LocalAdapterResolver(() =>
                                               {
                                                   enumerationCount++;
                                                   return snapshot;
                                               });
        Assert.True(resolver.Update("10.1.2.99", ""));
        string status = resolver.SendingMessage;
        snapshot = [Adapter("10.1.2.4", "255.255.0.0")];
        Assert.False(resolver.Update("10.1.2.99", ""));
        Assert.Equal(IPAddress.Parse("10.1.2.3"), resolver.EffectiveAddress);
        Assert.Equal(IPAddress.Parse("255.255.255.0"), resolver.SubnetMask);
        Assert.Same(status, resolver.SendingMessage);
        Assert.Equal(1, enumerationCount);

        var option = Assert.Single(resolver.GetOptions());
        Assert.Equal(IPAddress.Parse("10.1.2.4"), resolver.GetAdapter(option)?.Address);
        Assert.False(resolver.Update("10.1.2.99", ""));
        Assert.Equal(IPAddress.Parse("10.1.2.3"), resolver.EffectiveAddress);
        Assert.Equal(IPAddress.Parse("255.255.255.0"), resolver.SubnetMask);
        Assert.Same(status, resolver.SendingMessage);
        Assert.Equal(2, enumerationCount);

        Assert.True(resolver.Update("10.1.2.99", "", true));
        Assert.Equal(IPAddress.Parse("10.1.2.4"), resolver.EffectiveAddress);
        Assert.Equal(IPAddress.Parse("255.255.0.0"), resolver.SubnetMask);
        Assert.Equal(3, enumerationCount);

        snapshot = [Adapter("10.1.2.4", "255.255.255.0")];
        Assert.True(resolver.Update("10.1.2.99", "", true));
        Assert.Equal(IPAddress.Parse("10.1.2.4"), resolver.EffectiveAddress);
        Assert.Equal(IPAddress.Parse("255.255.255.0"), resolver.SubnetMask);
        Assert.False(resolver.Update("10.1.2.99", "", true));
    }

    [Fact]
    public void EnumerationFailureOnReconnectDiscardsCachedSelection()
    {
        bool fail = false;
        var resolver = new LocalAdapterResolver(() => fail ? throw new NetworkInformationException() : [Adapter("10.1.2.3")]);
        resolver.Update("10.1.2.3", "");
        fail = true;
        Assert.True(resolver.Update("10.1.2.3", "", true));
        Assert.Null(resolver.EffectiveAddress);
        Assert.NotNull(resolver.ErrorMessage);
        fail = false;
        Assert.True(resolver.Update("10.1.2.3", "", true));
        Assert.Equal(IPAddress.Parse("10.1.2.3"), resolver.EffectiveAddress);
        Assert.Null(resolver.ErrorMessage);
    }

    [Fact]
    public void ChangingTargetRefreshesAndResolvesSelection()
    {
        int enumerationCount = 0;
        var resolver = new LocalAdapterResolver(() =>
                                               {
                                                   enumerationCount++;
                                                   return [Adapter("10.1.2.3"), Adapter("192.168.1.2")];
                                               });
        resolver.Update("172.16.5.99", "10.1.2.50");
        Assert.Equal(IPAddress.Parse("10.1.2.3"), resolver.EffectiveAddress);
        Assert.True(resolver.Update("172.16.5.99", "192.168.1.50"));
        Assert.Equal(IPAddress.Parse("192.168.1.2"), resolver.EffectiveAddress);
        Assert.Equal(2, enumerationCount);
    }

    [Fact]
    public void ChangingSavedAddressRefreshesAndResolvesSelection()
    {
        LocalAdapterResolver.Adapter[] snapshot = [Adapter("10.1.2.3")];
        var resolver = new LocalAdapterResolver(() => snapshot);
        resolver.Update("10.1.2.3", "");
        snapshot = [Adapter("192.168.1.2")];
        Assert.True(resolver.Update("192.168.1.99", ""));
        Assert.Equal(IPAddress.Parse("192.168.1.2"), resolver.EffectiveAddress);
    }

    [Fact]
    public void NullAndEmptyInputsShareCachedResolution()
    {
        int enumerationCount = 0;
        var resolver = new LocalAdapterResolver(() =>
                                               {
                                                   enumerationCount++;
                                                   return [];
                                               });
        resolver.Update(null, null);
        Assert.False(resolver.Update("", ""));
        Assert.Equal(1, enumerationCount);
        Assert.Null(resolver.EffectiveAddress);
    }

    private static LocalAdapterResolver Resolver(params LocalAdapterResolver.Adapter[] adapters) => new(() => adapters);
    private static LocalAdapterResolver.Adapter Adapter(string ip, string mask = "255.255.255.0") => new(IPAddress.Parse(ip), IPAddress.Parse(mask), ip);
}
