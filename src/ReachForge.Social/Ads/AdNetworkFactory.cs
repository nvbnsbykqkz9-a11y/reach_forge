using Microsoft.Extensions.Options;
using ReachForge.Application.Ads;
using ReachForge.Domain.Entities;

namespace ReachForge.Social.Ads;

/// <summary>広告の仕組みを選ぶ。お試しは「デモ接続」（Social:UseMock）がオンのときだけ使える。</summary>
public sealed class AdNetworkFactory(IEnumerable<IAdNetworkAdapter> adapters, IOptions<SocialOptions> social, TimeProvider clock) : IAdNetworkFactory
{
    public IAdNetworkAdapter? Get(AdNetwork network, bool demo)
    {
        if (demo) return social.Value.UseMock && network != AdNetwork.Demo ? new DemoAdAdapter(network, clock) : null;
        return adapters.FirstOrDefault(a => a.Network == network && a.IsConfigured);
    }
}
