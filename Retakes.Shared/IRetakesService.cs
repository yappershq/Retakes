namespace Retakes.Shared;

/// <summary>
/// Public event-bus and round-state surface published by Retakes.Core in PostInit via
/// ISharpModuleManager.RegisterSharpModuleInterface&lt;IRetakesService&gt;(this, Identity, impl).
///
/// External plugins look it up in THEIR OnAllModulesLoaded (guaranteed published by then — ModSharp
/// finishes all PostInits before any OAM) and subscribe to events or read round state:
/// <code>
/// var iface   = manager.GetOptionalSharpModuleInterface&lt;IRetakesService&gt;(IRetakesService.Identity);
/// var retakes = iface?.Instance;
/// retakes?.OnAnnounceBombsite += site => { /* ... */ };
/// </code>
/// </summary>
public interface IRetakesService
{
    static string Identity => typeof(IRetakesService).FullName!;

    // ── Events ──────────────────────────────────────────────────────────────

    /// <summary>Fired during round pre-start when the bombsite for this round is selected.</summary>
    event Action<Bombsite> OnAnnounceBombsite;

    /// <summary>Fired post-round-start when weapons / equipment should be allocated to players.</summary>
    event Action OnAllocate;

    // ── Round state (read-only) ──────────────────────────────────────────────

    /// <summary>Bombsite selected for the current round.</summary>
    Bombsite CurrentBombsite { get; }

    /// <summary>Economy tier selected for the current round.</summary>
    RoundType CurrentRoundType { get; }
}
