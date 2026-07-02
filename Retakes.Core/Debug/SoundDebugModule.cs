using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Retakes.Plugins;
using Sharp.Shared.HookParams;
using Sharp.Shared.Listeners;
using Sharp.Shared.Types;

namespace Retakes.Debug;

// ponytail: TEMPORARY diagnostic — hooks every native EmitSound/soundevent to log its name once
// (deduped) so we can identify the real soundevent names for bomb-plant/freeze-beep/round-win
// audio. Remove this module + its DI registration once #1 (Sounds) is implemented for real.
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

        return current;
    }
}
