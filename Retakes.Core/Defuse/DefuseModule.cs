using Microsoft.Extensions.Logging;
using Retakes.Plugins;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEvents;
using Sharp.Shared.Listeners;
using Sharp.Shared.Objects;

namespace Retakes.Defuse;

/// <summary>
/// Gives every alive CT player a defuser kit when the bomb is planted.
/// </summary>
internal sealed class DefuseModule : IModule, IEventListener
{
    private readonly ILogger<DefuseModule> _logger;
    private readonly InterfaceBridge        _bridge;

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
        => _bridge.EventManager.InstallEventListener(this);

    public void OnAllSharpModulesLoaded() { }

    public void Shutdown()
        => _bridge.EventManager.RemoveEventListener(this);

    // ── IEventListener impl ────────────────────────────────────────────────

    void IEventListener.FireGameEvent(IGameEvent @event)
    {
        if (@event is not IEventBombPlanted) return;
        GiveDefusers();
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
