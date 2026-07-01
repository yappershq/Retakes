using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Retakes.Config;
using Retakes.Plugins;
using Retakes.Shared;
using Retakes.Utils;
using Sharp.Modules.ClientPreferences.Shared;
using Sharp.Modules.CommandCenter.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEvents;
using Sharp.Shared.Listeners;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;

namespace Retakes.Announcement;

/// <summary>
/// Announces the selected bombsite to all players each round.
///
/// Three announce channels (mirrors b3none base + cs2-retakes-allocator RetakesAllocator.OnTick):
///   a. Chat line to everyone (b3none base).
///   b. Rich CENTER-HTML panel — site image + live per-team alive counts + DEFEND/RETAKE variant,
///      shown after a config delay for a config duration, refreshed on a repeating timer so the
///      counts stay live as players die. Gated on <c>EnableBombSiteAnnouncementCenter</c>.
///   c. Optional per-player voice cue + optional ASCII-art chat lines.
///
/// Provides !voices command to toggle the voice announcement per player.
/// </summary>
internal sealed class AnnouncementModule : IModule, IClientListener, IEventListener
{
    private readonly ILogger<AnnouncementModule> _logger;
    private readonly InterfaceBridge             _bridge;
    private readonly ConfigModule                _config;
    private readonly EventBus                    _bus;

    // Optional services — resolved in OAM, may remain null.
    private IClientPreference? _clientPreference;

    // In-memory mute set — used as fallback when no IClientPreference. Slot-indexed.
    private static readonly byte MaxSlots = PlayerSlot.MaxPlayerCount.AsPrimitive();
    private readonly bool[] _voicesMuted = new bool[MaxSlots];

    // ── center-HTML announce state ─────────────────────────────────────────
    // Non-null site → announce active; refresh timer re-renders while active.
    private Bombsite? _centerSite;
    private Guid?     _centerRefreshTimer;
    private Guid?     _centerStopTimer;

    // Stored so we can unsubscribe cleanly in Shutdown.
    private readonly Action<Bombsite>   _onBombsiteAnnounced;
    private readonly Func<TimerAction>  _refreshCenter;

    // ── IEventListener identity ────────────────────────────────────────────
    int IEventListener.ListenerVersion  => IEventListener.ApiVersion;
    int IEventListener.ListenerPriority => 0;

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
        _refreshCenter       = RefreshCenter;
    }

    // ── IModule lifecycle ──────────────────────────────────────────────────

    public bool Init() => true;

    public void OnPostInit()
    {
        _bridge.ClientManager.InstallClientListener(this);
        // bomb_planted → optional force-close of the center announce loop.
        _bridge.EventManager.HookEvent("bomb_planted");
        _bridge.EventManager.InstallEventListener(this);
    }

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
        StopCenterAnnounce();
        _bridge.EventManager.RemoveEventListener(this);
        _bridge.ClientManager.RemoveClientListener(this);
    }

    // ── bombsite announcement ─────────────────────────────────────────────

    private void OnBombsiteAnnounced(Bombsite site)
    {
        var mapCfg   = _config.Config.MapConfig;
        var allocCfg = _config.Config.Allocator;
        var siteStr  = site.ToString();

        // a. Chat announcement to everyone (each in their own locale).
        Loc.ChatAll(_bridge.LocalizerManager, _bridge.ClientManager, "Retakes_Bombsite_Announce", siteStr);

        // a2. Optional ASCII-art chat lines (allocator EnableBombSiteAnnouncementChat).
        if (allocCfg.EnableBombSiteAnnouncementChat)
        {
            var prefix = site == Bombsite.A ? "Retakes_ChatAsite_Line" : "Retakes_ChatBsite_Line";
            for (var i = 1; i <= 6; i++)
                Loc.ChatAll(_bridge.LocalizerManager, _bridge.ClientManager, prefix + i);
        }

        // b. Rich center-HTML announce (allocator EnableBombSiteAnnouncementCenter).
        //    Legacy b3none plain CT-only center is superseded by this when enabled.
        if (allocCfg.EnableBombSiteAnnouncementCenter)
        {
            ScheduleCenterAnnounce(site);
        }
        else if (mapCfg.EnableBombsiteAnnouncementCenter)
        {
            // Fallback: legacy plain CT-only center line (no chat color escapes on Center channel).
            foreach (var controller in _bridge.EntityManager.FindPlayerControllers(true))
            {
                if (controller is null || !controller.IsValid() || controller.IsFakeClient) continue;
                if (controller.Team != CStrikeTeam.CT) continue;
                var client = controller.GetGameClient();
                if (client is { IsInGame: true })
                    Loc.Center(_bridge.LocalizerManager, client, "Retakes_Bombsite_Center", siteStr);
            }
        }

        // c. Voice cue (if not muted).
        if (mapCfg.EnableBombsiteAnnouncementVoices)
        {
            var siteLower = siteStr.ToLowerInvariant();
            foreach (var controller in _bridge.EntityManager.FindPlayerControllers(true))
            {
                if (controller is null || !controller.IsValid() || controller.IsFakeClient) continue;
                var client = controller.GetGameClient();
                if (client is not { IsInGame: true }) continue;
                if (IsVoiceMuted(client.SteamId, client.Slot)) continue;

                // ponytail: EXPERIMENTAL, opt-in (config default off). The `play <path>` client
                // command + this VO asset path are UNVERIFIED — a server-issued `play` may be
                // client-blocked and the exact CS2 agent-VO soundevent/path needs live confirmation.
                // If it turns out silent, switch to a real soundevent emit. Do not treat as working.
                var announcer = Announcers[Random.Shared.Next(Announcers.Length)];
                client.Command($"play sounds/vo/agents/{announcer}/loc_{siteLower}_01");
            }
        }
    }

    // ── center-HTML announce loop ──────────────────────────────────────────

    private void ScheduleCenterAnnounce(Bombsite site)
    {
        StopCenterAnnounce();

        var allocCfg = _config.Config.Allocator;
        var delay    = Math.Max(0f, allocCfg.BombSiteAnnouncementCenterDelay);
        var show     = Math.Max(0.1f, allocCfg.BombSiteAnnouncementCenterShowTimer);

        // After the delay, start the refresh loop (re-render each tick to keep counts live)
        // and schedule a stop after the show duration. All timers stop on round/map end.
        _bridge.ModSharp.PushTimer(
            () =>
            {
                _centerSite = site;
                RenderCenter(); // immediate first paint
                // Refresh at ~1s cadence — PrintCenterHtml duration keeps it up between refreshes.
                _centerRefreshTimer = _bridge.ModSharp.PushTimer(
                    _refreshCenter, 1.0, GameTimerFlags.StopOnMapEnd | GameTimerFlags.StopOnRoundEnd);
                _centerStopTimer = _bridge.ModSharp.PushTimer(
                    StopCenterAnnounce, show, GameTimerFlags.StopOnMapEnd | GameTimerFlags.StopOnRoundEnd);
            },
            delay,
            GameTimerFlags.StopOnMapEnd | GameTimerFlags.StopOnRoundEnd);
    }

    private TimerAction RefreshCenter()
    {
        if (_centerSite is null) return TimerAction.Stop;
        RenderCenter();
        return TimerAction.Continue;
    }

    private void RenderCenter()
    {
        if (_centerSite is not { } site) return;

        var allocCfg = _config.Config.Allocator;
        var lm       = _bridge.LocalizerManager;
        if (lm is null) return;

        // Live per-team alive counts (re-resolved every render — never cached across ticks).
        var countT  = 0;
        var countCt = 0;
        var alive   = new List<IGameClient>();
        foreach (var controller in _bridge.EntityManager.FindPlayerControllers(true))
        {
            if (controller is null || !controller.IsValid() || controller.IsFakeClient) continue;
            var pawn = controller.GetPlayerPawn();
            if (pawn is null || !pawn.IsAlive) continue;
            if      (controller.Team == CStrikeTeam.TE) countT++;
            else if (controller.Team == CStrikeTeam.CT) countCt++;
            else continue;

            var client = controller.GetGameClient();
            if (client is { IsInGame: true }) alive.Add(client);
        }

        var siteImage = lm.Format("en-US", site == Bombsite.A ? "Retakes_BombSite_A" : "Retakes_BombSite_B");
        var siteStr   = site.ToString();
        var duration  = 2; // > refresh cadence so panel never flickers off between refreshes

        foreach (var client in alive)
        {
            var controller = client.GetPlayerController();
            if (controller is null) continue;
            var team = controller.Team;

            if (team == CStrikeTeam.CT)
            {
                // RETAKE (CT.Message): {0}=site {1}=image {2}=countT {3}=countCt
                client.PrintCenterHtml(
                    lm.For(client).Text("Retakes_Center_CtMessage", siteStr, siteImage, countT, countCt), duration);
            }
            else if (team == CStrikeTeam.TE && !allocCfg.BombSiteAnnouncementCenterToCtOnly)
            {
                // DEFEND (T.Message)
                client.PrintCenterHtml(
                    lm.For(client).Text("Retakes_Center_TMessage", siteStr, siteImage, countT, countCt), duration);
            }
        }
    }

    private void StopCenterAnnounce()
    {
        _centerSite = null;
        if (_centerRefreshTimer is { } r) { _bridge.ModSharp.StopTimer(r); _centerRefreshTimer = null; }
        if (_centerStopTimer    is { } s) { _bridge.ModSharp.StopTimer(s); _centerStopTimer    = null; }
    }

    // ── IEventListener impl ────────────────────────────────────────────────

    void IEventListener.FireGameEvent(IGameEvent @event)
    {
        if (!@event.Name.Equals("bomb_planted", StringComparison.Ordinal)) return;

        // Force-close the center announce loop when the bomb is planted (allocator config).
        if (_config.Config.Allocator.ForceCloseBombSiteAnnouncementCenterOnPlant)
            StopCenterAnnounce();
    }

    // ── !voices command ───────────────────────────────────────────────────

    private void OnVoicesCommand(IGameClient client, StringCommand _)
    {
        if (!client.IsInGame) return;

        var steamId  = client.SteamId;
        var isMuted  = IsVoiceMuted(steamId, client.Slot);

        if (_clientPreference is not null)
        {
            _clientPreference.SetCookie(steamId, "retakes.voices", isMuted ? 0L : 1L);
        }
        else
        {
            _voicesMuted[client.Slot.AsPrimitive()] = !isMuted;
        }

        // isMuted reflects the PRE-toggle state; if it was muted we've just enabled voices.
        Loc.Chat(_bridge.LocalizerManager, client,
            isMuted ? "Retakes_Voices_Enabled" : "Retakes_Voices_Disabled");
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private bool IsVoiceMuted(SteamID steamId, PlayerSlot slot)
    {
        if (_clientPreference is not null)
        {
            var cookie = _clientPreference.GetCookie(steamId, "retakes.voices");
            return cookie?.GetNumber() == 1L;
        }

        return slot.IsValid() && _voicesMuted[slot.AsPrimitive()];
    }

    // ── IClientListener impl ──────────────────────────────────────────────

    void IClientListener.OnClientDisconnected(IGameClient client, Sharp.Shared.Enums.NetworkDisconnectionReason reason)
    {
        if (client.IsFakeClient) return;
        _voicesMuted[client.Slot.AsPrimitive()] = false;
    }

    void IClientListener.OnClientConnected(IGameClient client)    { }
    void IClientListener.OnClientPutInServer(IGameClient client)  { }
}
