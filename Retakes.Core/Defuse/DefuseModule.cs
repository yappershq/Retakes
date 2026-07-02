using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Retakes.Plugins;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEvents;
using Sharp.Shared.Listeners;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;

namespace Retakes.Defuse;

/// <summary>
/// Gives every alive CT player a defuser kit when the bomb is planted, and — port of
/// Souplax1/InstaDefuse (with the molotov tracking approach cross-checked against
/// a2Labs-cc/SwiftlyS2-Retakes) — instantly completes a defuse when the last T is dead and
/// there's enough time left to have finished normally, or lets the bomb go off if there isn't.
/// Skips instant-defuse (falls back to a manual defuse) if a molotov/incendiary is burning within
/// <see cref="MolotovExclusionRadius"/> units of the bomb.
/// </summary>
internal sealed class DefuseModule : IModule, IEventListener
{
    private const float MolotovExclusionRadius = 300f;

    private readonly ILogger<DefuseModule> _logger;
    private readonly InterfaceBridge        _bridge;

    // Tracked via inferno_startburn/inferno_expire (entityid → position) rather than scanning
    // entities by classname at defuse-time — SwiftlyS2-Retakes' proven approach, and it sidesteps
    // relying on an unverified "inferno" entity classname for FindEntityByClassname.
    private readonly Dictionary<int, Vector> _activeInfernos = new();

    // ── IEventListener ─────────────────────────────────────────────────────
    int IEventListener.ListenerVersion  => IEventListener.ApiVersion;
    int IEventListener.ListenerPriority => 0;

    public DefuseModule(
        ILogger<DefuseModule> logger,
        InterfaceBridge        bridge)
    {
        _logger = logger;
        _bridge = bridge;
    }

    // ── IModule lifecycle ──────────────────────────────────────────────────

    public bool Init() => true;

    public void OnPostInit()
    {
        // FireGameEvent only fires for hooked events.
        _bridge.EventManager.HookEvent("bomb_planted");
        _bridge.EventManager.HookEvent("bomb_begindefuse");
        _bridge.EventManager.HookEvent("inferno_startburn");
        _bridge.EventManager.HookEvent("inferno_expire");
        _bridge.EventManager.InstallEventListener(this);
    }

    public void OnAllSharpModulesLoaded() { }

    public void Shutdown()
        => _bridge.EventManager.RemoveEventListener(this);

    // ── IEventListener impl ────────────────────────────────────────────────

    void IEventListener.FireGameEvent(IGameEvent @event)
    {
        switch (@event)
        {
            case IEventBombPlanted:
                GiveDefusers();
                break;

            case IEventBombBeginDefuse begin:
                TryInstantDefuse(begin);
                break;

            default:
                switch (@event.Name)
                {
                    case "inferno_startburn":
                        _activeInfernos[@event.GetInt("entityid")] =
                            new Vector(@event.GetFloat("x"), @event.GetFloat("y"), @event.GetFloat("z"));
                        break;

                    case "inferno_expire":
                        _activeInfernos.Remove(@event.GetInt("entityid"));
                        break;
                }
                break;
        }
    }

    // ── instant defuse ────────────────────────────────────────────────────

    /// <summary>
    /// Mirrors Souplax1/InstaDefuse's AttemptInstadefuse: on defuse-start, if 0 T are alive and
    /// there's enough time left to finish a normal defuse, zero the countdown so the ALREADY
    /// in-progress native defuse completes on the next tick (no manual bomb_defused event / netvar
    /// juggling needed — the engine's own defuse-completion logic does the rest). If there isn't
    /// enough time, nudge the timer to blow ~now instead of leaving a doomed defuse attempt
    /// dragging out. A molotov within <see cref="MolotovExclusionRadius"/>u of the bomb disables
    /// this entirely — defuse manually.
    /// </summary>
    private void TryInstantDefuse(IEventBombBeginDefuse @event)
    {
        var pawn = @event.Pawn;
        if (pawn is not { IsAlive: true }) return;

        var c4 = _bridge.EntityManager.FindEntityByClassname(null, "planted_c4");
        if (c4 is null || !c4.IsValidEntity) return;
        if (c4.GetNetVar<bool>("m_bCannotBeDefused")) return;

        if (IsMolotovNearby(c4.GetAbsOrigin()))
            return; // fire nearby — force a manual defuse regardless of time/team state

        var curTime       = _bridge.ModSharp.GetGlobals().CurTime;
        var timeRemaining = c4.GetNetVar<float>("m_flC4Blow") - curTime;
        var defuseLength  = c4.GetNetVar<float>("m_flDefuseLength");

        if (timeRemaining >= defuseLength)
        {
            if (AnyAliveOnTeam(CStrikeTeam.TE)) return; // Ts still alive — normal defuse plays out

            // 0.0f (GameTime_t epoch) is always in the past by the time the engine next checks
            // it — matches the proven Cola-Ace/cs2-retakes-instantdefuse ModSharp implementation.
            _bridge.ModSharp.InvokeFrameAction(() =>
            {
                if (!c4.IsValidEntity) return;
                c4.SetNetVar("m_flDefuseCountDown", 0.0f); // completes the in-progress defuse now
            });
        }
        else
        {
            // Can't finish in time even uncontested — let it blow now instead of dragging out an
            // attempt that was always going to fail. Same 1.0f trick as the reference plugin.
            _bridge.ModSharp.InvokeFrameAction(() =>
            {
                if (!c4.IsValidEntity) return;
                c4.SetNetVar("m_flC4Blow", 1.0f);
            });
        }
    }

    private bool IsMolotovNearby(Vector bombPos)
    {
        foreach (var fire in _activeInfernos.Values)
        {
            // 2D distance (ignore Z) — matches SwiftlyS2-Retakes: fire spreads across the floor
            // plane, and a molotov thrown from a different height above the bombsite still
            // counts as "near" for this check.
            var dx = fire.X - bombPos.X;
            var dy = fire.Y - bombPos.Y;
            if (dx * dx + dy * dy <= MolotovExclusionRadius * MolotovExclusionRadius)
                return true;
        }
        return false;
    }

    private bool AnyAliveOnTeam(CStrikeTeam team)
    {
        foreach (var controller in _bridge.EntityManager.FindPlayerControllers(true))
        {
            if (controller is null || !controller.IsValid()) continue;
            if (controller.Team != team) continue;

            var pawn = controller.GetPlayerPawn();
            if (pawn is { IsAlive: true }) return true;
        }
        return false;
    }

    // ── defuser distribution ───────────────────────────────────────────────

    private void GiveDefusers()
    {
        var count = 0;
        foreach (var controller in _bridge.EntityManager.FindPlayerControllers(true))
        {
            if (controller is null || !controller.IsValid()) continue;
            if (controller.Team != CStrikeTeam.CT)           continue;

            var pawn = controller.GetPlayerPawn();
            if (pawn is null || !pawn.IsAlive)               continue;

            var svc = pawn.GetItemService();
            if (svc is null)                                  continue;
            if (svc.HasDefuser)                               continue;

            svc.HasDefuser = true;
            count++;
        }

        if (count > 0)
            _logger.LogDebug("[Retakes] DefuseModule: gave defuser to {Count} CT(s)", count);
    }
}
