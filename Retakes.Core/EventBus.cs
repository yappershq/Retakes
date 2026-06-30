using Retakes.Shared;

namespace Retakes;

/// <summary>
/// Implements <see cref="IRetakesService"/> — the public event bus published to external plugins.
/// Internal modules fire <see cref="FireAnnounceBombsite"/> / <see cref="FireAllocate"/> on this bus;
/// external consumers subscribe in their OnAllModulesLoaded.
/// Published via RegisterSharpModuleInterface in <see cref="RetakesPlugin.PostInit"/>.
/// </summary>
internal sealed class EventBus : IRetakesService
{
    public event Action<Bombsite>? OnAnnounceBombsite;
    public event Action?           OnAllocate;

    public Bombsite   CurrentBombsite   { get; private set; } = Bombsite.A;
    public RoundType  CurrentRoundType  { get; private set; } = RoundType.FullBuy;

    // ── Internal fire methods (called by RoundFlowModule once implemented) ───

    internal void FireAnnounceBombsite(Bombsite site)
    {
        CurrentBombsite = site;
        OnAnnounceBombsite?.Invoke(site);
    }

    internal void FireAllocate(RoundType roundType)
    {
        CurrentRoundType = roundType;
        OnAllocate?.Invoke();
    }
}
