using Microsoft.Extensions.Logging;
using Retakes.Plugins;
using Sharp.Shared.Enums;
using Sharp.Shared.HookParams;
using Sharp.Shared.Types;
using Sharp.Shared.Units;

namespace Retakes.RoundFlow;

/// <summary>
/// Stops the engine's own round_start/round_end game events from reaching clients — Retakes has
/// its own round-flow announcements (bombsite HUD, chat winner line, etc.) and the native
/// broadcast (announcer voice, default center text) competes with them.
/// </summary>
internal sealed class RoundEventSuppressModule : IModule
{
    private static readonly string[] SuppressedEvents = { "round_start", "round_end" };

    private readonly ILogger<RoundEventSuppressModule> _logger;
    private readonly InterfaceBridge                    _bridge;

    private readonly System.Func<IPostEventAbstractHookParams, HookReturnValue<NetworkReceiver>,
        HookReturnValue<NetworkReceiver>> _onPostEvent;

    public RoundEventSuppressModule(ILogger<RoundEventSuppressModule> logger, InterfaceBridge bridge)
    {
        _logger      = logger;
        _bridge      = bridge;
        _onPostEvent = OnPostEvent;
    }

    public bool Init() => true;

    public void OnPostInit()
    {
        _bridge.ModSharp.HookNetMessage(ProtobufNetMessageType.GE_Source1LegacyGameEvent);
        _bridge.HookManager.PostEventAbstract.InstallHookPre(_onPostEvent);
    }

    public void OnAllSharpModulesLoaded() { }

    public void Shutdown()
        => _bridge.HookManager.PostEventAbstract.RemoveHookPre(_onPostEvent);

    private HookReturnValue<NetworkReceiver> OnPostEvent(
        IPostEventAbstractHookParams p, HookReturnValue<NetworkReceiver> current)
    {
        if (p.MsgId != ProtobufNetMessageType.GE_Source1LegacyGameEvent)
            return new HookReturnValue<NetworkReceiver>(EHookAction.Ignored);

        var name = p.Data.ReadString("event_name");
        foreach (var suppressed in SuppressedEvents)
        {
            if (name == suppressed)
                return new HookReturnValue<NetworkReceiver>(EHookAction.SkipCallReturnOverride);
        }

        return new HookReturnValue<NetworkReceiver>(EHookAction.Ignored);
    }
}
