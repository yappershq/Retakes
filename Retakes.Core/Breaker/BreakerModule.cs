using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Retakes.Config;
using Retakes.Plugins;
using Sharp.Shared.GameEntities;
using Sharp.Shared.GameEvents;
using Sharp.Shared.Listeners;
using Sharp.Shared.Objects;
using Sharp.Shared.Units;

namespace Retakes.Breaker;

/// <summary>
/// Breaks/opens map entities at round start.
/// Respects ShouldBreakBreakables and ShouldOpenDoors config flags.
/// Also rebuilds the entity action list on game_newmap.
/// </summary>
internal sealed class BreakerModule : IModule, IEventListener
{
    private readonly ILogger<BreakerModule> _logger;
    private readonly InterfaceBridge         _bridge;
    private readonly ConfigModule            _config;

    private readonly List<(string classname, string action)> _entityActions = new();

    // ── IEventListener ─────────────────────────────────────────────────────
    int IEventListener.ListenerVersion  => IEventListener.ApiVersion;
    int IEventListener.ListenerPriority => 0;

    public BreakerModule(
        ILogger<BreakerModule> logger,
        InterfaceBridge         bridge,
        ConfigModule            config)
    {
        _logger = logger;
        _bridge = bridge;
        _config = config;
    }

    // ── IModule lifecycle ──────────────────────────────────────────────────

    public bool Init() => true;

    public void OnPostInit()
    {
        _bridge.EventManager.HookEvent("game_newmap");
        _bridge.EventManager.InstallEventListener(this);
    }

    public void OnAllSharpModulesLoaded()
        => BuildEntityActions();

    public void Shutdown()
        => _bridge.EventManager.RemoveEventListener(this);

    // ── IEventListener impl ────────────────────────────────────────────────

    void IEventListener.FireGameEvent(IGameEvent @event)
    {
        if (@event.Name.Equals("round_poststart", StringComparison.Ordinal))
            BreakEntities();
        else if (@event.Name.Equals("game_newmap", StringComparison.Ordinal))
            BuildEntityActions();
    }

    // ── internals ─────────────────────────────────────────────────────────

    private void BuildEntityActions()
    {
        _entityActions.Clear();

        var mapName = _bridge.ModSharp.GetMapName() ?? "";

        if (_config.Config.Game.ShouldBreakBreakables)
        {
            _entityActions.Add(("func_breakable",      "Break"));
            _entityActions.Add(("func_breakable_surf", "Break"));
            _entityActions.Add(("prop.breakable.01",   "Break"));
            _entityActions.Add(("prop.breakable.02",   "Break"));

            if (mapName is "de_vertigo" or "de_nuke" or "de_mirage")
                _entityActions.Add(("prop_dynamic", "Break"));

            if (mapName is "de_nuke")
                _entityActions.Add(("func_button", "Kill"));
        }

        if (_config.Config.Game.ShouldOpenDoors)
            _entityActions.Add(("prop_door_rotating", "open"));

        _logger.LogDebug("[Retakes] BreakerModule: rebuilt {Count} entity actions for map '{Map}'",
            _entityActions.Count, mapName);
    }

    private void BreakEntities()
    {
        if (_entityActions.Count == 0) return;

        var broken = 0;
        // ponytail: collect indices first, then act — avoids stale cursor when Kill frees the entity
        var indices = new List<EntityIndex>();
        foreach (var (classname, action) in _entityActions)
        {
            indices.Clear();
            IBaseEntity? ent = null;
            while ((ent = _bridge.EntityManager.FindEntityByClassname(ent, classname)) is not null)
            {
                if (ent.IsValid())
                    indices.Add(ent.Index);
            }

            foreach (var idx in indices)
            {
                var e = _bridge.EntityManager.FindEntityByIndex(idx);
                if (e is { IsValidEntity: true })
                {
                    e.AcceptInput(action);
                    broken++;
                }
            }
        }

        if (broken > 0)
            _logger.LogDebug("[Retakes] BreakerModule: sent input to {Count} entities", broken);
    }
}
