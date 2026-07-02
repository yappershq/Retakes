using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Retakes.Plugins;
using Sharp.Shared.Enums;
using Sharp.Shared.HookParams;
using Sharp.Shared.Listeners;
using Sharp.Shared.Types;

namespace Retakes.Debug;

// ponytail: keeps the dedup-log diagnostic from #1 (still useful while more sounds get wired up)
// and additionally mutes bot/agent radio chatter voice lines, confirmed live 2026-07-02: any
// soundevent name containing "radio" (sas.radio.locknload05, phoenix.radio_letsgo03/04, etc).
internal sealed class SoundDebugModule : IModule
{
    private readonly ILogger<SoundDebugModule> _logger;
    private readonly InterfaceBridge           _bridge;
    private readonly HashSet<string>           _seen = new();

    private readonly System.Func<ISoundEventHookParams, HookReturnValue<SoundOpEventGuid>,
        HookReturnValue<SoundOpEventGuid>> _onSoundEvent;

    public SoundDebugModule(ILogger<SoundDebugModule> logger, InterfaceBridge bridge)
    {
        _logger       = logger;
        _bridge       = bridge;
        _onSoundEvent = OnSoundEvent;
    }

    public bool Init() => true;

    public void OnPostInit()
        => _bridge.HookManager.SoundEvent.InstallHookPre(_onSoundEvent);

    public void OnAllSharpModulesLoaded() { }

    public void Shutdown()
        => _bridge.HookManager.SoundEvent.RemoveHookPre(_onSoundEvent);

    private HookReturnValue<SoundOpEventGuid> OnSoundEvent(
        ISoundEventHookParams p, HookReturnValue<SoundOpEventGuid> current)
    {
        if (_seen.Add(p.SoundName))
            _logger.LogInformation("[Retakes][SoundDebug] {Sound} (entity {Entity}, dur {Dur})",
                p.SoundName, p.EntityIndex, p.SoundDuration);

        if (p.SoundName.Contains("radio", System.StringComparison.OrdinalIgnoreCase))
            return new HookReturnValue<SoundOpEventGuid>(EHookAction.SkipCallReturnOverride);

        return current;
    }
}
