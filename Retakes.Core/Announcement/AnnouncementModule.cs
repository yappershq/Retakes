using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Retakes.Config;
using Retakes.Plugins;
using Retakes.Shared;
using Sharp.Modules.ClientPreferences.Shared;
using Sharp.Modules.CommandCenter.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.Listeners;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;

namespace Retakes.Announcement;

/// <summary>
/// Announces the selected bombsite to all players each round.
/// Sends chat, optional CT center-text, and optional per-player voice cues.
/// Provides !voices command to toggle the voice announcement per player.
/// </summary>
internal sealed class AnnouncementModule : IModule, IClientListener
{
    private readonly ILogger<AnnouncementModule> _logger;
    private readonly InterfaceBridge             _bridge;
    private readonly ConfigModule                _config;
    private readonly EventBus                    _bus;

    // Optional services — resolved in OAM, may remain null.
    private IClientPreference? _clientPreference;

    // In-memory mute set — used as fallback when no IClientPreference.
    private readonly HashSet<ulong> _voicesMuted = new();

    // Stored so we can unsubscribe cleanly in Shutdown.
    private readonly Action<Bombsite> _onBombsiteAnnounced;

    private static readonly string[] Announcers =
    [
        "balkan_epic",
        "leet_epic",
        "professional_epic",
        "professional_fem",
        "seal_epic",
        "swat_epic",
        "swat_fem",
    ];

    // ── IClientListener ────────────────────────────────────────────────────
    int IClientListener.ListenerVersion  => IClientListener.ApiVersion;
    int IClientListener.ListenerPriority => 0;

    public AnnouncementModule(
        ILogger<AnnouncementModule> logger,
        InterfaceBridge             bridge,
        ConfigModule                config,
        EventBus                    bus)
    {
        _logger = logger;
        _bridge = bridge;
        _config = config;
        _bus    = bus;

        _onBombsiteAnnounced = OnBombsiteAnnounced;
    }

    // ── IModule lifecycle ──────────────────────────────────────────────────

    public bool Init() => true;

    public void OnPostInit()
        => _bridge.ClientManager.InstallClientListener(this);

    public void OnAllSharpModulesLoaded()
    {
        _bus.OnAnnounceBombsite += _onBombsiteAnnounced;

        // Optional: client preference cookie service.
        _clientPreference = _bridge.SharpModuleManager
            .GetOptionalSharpModuleInterface<IClientPreference>(IClientPreference.Identity)
            ?.Instance;

        if (_clientPreference is null)
            _logger.LogInformation("[Retakes] AnnouncementModule: IClientPreference not available, using in-memory voice-mute fallback.");

        // Optional: command center for !voices toggle.
        var commandCenter = _bridge.SharpModuleManager
            .GetOptionalSharpModuleInterface<ICommandCenter>(ICommandCenter.Identity)
            ?.Instance;

        if (commandCenter is not null)
            commandCenter.GetRegistry("retakes").RegisterClientCommand("voices", OnVoicesCommand);
        else
            _logger.LogInformation("[Retakes] AnnouncementModule: ICommandCenter not available, !voices command not registered.");
    }

    public void Shutdown()
    {
        _bus.OnAnnounceBombsite -= _onBombsiteAnnounced;
        _bridge.ClientManager.RemoveClientListener(this);
    }

    // ── bombsite announcement ─────────────────────────────────────────────

    private void OnBombsiteAnnounced(Bombsite site)
    {
        var controllers = _bridge.EntityManager.FindPlayerControllers(true);
        var siteStr     = site.ToString();
        var siteLower   = siteStr.ToLowerInvariant();

        foreach (var controller in controllers)
        {
            if (controller is null || !controller.IsValid()) continue;
            if (controller.IsFakeClient)                     continue;

            var client = controller.GetGameClient();
            if (client is not { IsInGame: true })            continue;

            // a. Chat announcement to everyone.
            client.Print(HudPrintChannel.Chat, $"[Retakes] Bombsite {siteStr}!");

            // b. Center text for CTs only.
            if (_config.Config.MapConfig.EnableBombsiteAnnouncementCenter
                && controller.Team == CStrikeTeam.CT)
            {
                client.Print(HudPrintChannel.Center, $"Bombsite {siteStr}");
            }

            // c. Voice cue (if not muted).
            if (_config.Config.MapConfig.EnableBombsiteAnnouncementVoices
                && !IsVoiceMuted(client.SteamId))
            {
                var announcer = Announcers[Random.Shared.Next(Announcers.Length)];
                client.Command($"play sounds/vo/agents/{announcer}/loc_{siteLower}_01");
            }
        }
    }

    // ── !voices command ───────────────────────────────────────────────────

    private void OnVoicesCommand(IGameClient client, StringCommand _)
    {
        if (!client.IsInGame) return;

        var steamId  = client.SteamId;
        var isMuted  = IsVoiceMuted(steamId);

        if (_clientPreference is not null)
        {
            _clientPreference.SetCookie(steamId, "retakes.voices", isMuted ? 0L : 1L);
        }
        else
        {
            if (isMuted)
                _voicesMuted.Remove((ulong)steamId);
            else
                _voicesMuted.Add((ulong)steamId);
        }

        var state = isMuted ? "enabled" : "disabled";
        client.Print(HudPrintChannel.Chat, $"Voices [{state}]");
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private bool IsVoiceMuted(SteamID steamId)
    {
        if (_clientPreference is not null)
        {
            var cookie = _clientPreference.GetCookie(steamId, "retakes.voices");
            return cookie?.GetNumber() == 1L;
        }

        return _voicesMuted.Contains((ulong)steamId);
    }

    // ── IClientListener impl ──────────────────────────────────────────────

    void IClientListener.OnClientDisconnected(IGameClient client, Sharp.Shared.Enums.NetworkDisconnectionReason reason)
    {
        if (client.IsFakeClient) return;
        _voicesMuted.Remove((ulong)client.SteamId);
    }

    void IClientListener.OnClientConnected(IGameClient client)    { }
    void IClientListener.OnClientPutInServer(IGameClient client)  { }
}
