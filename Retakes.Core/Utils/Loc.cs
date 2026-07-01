using System;
using Sharp.Modules.LocalizerManager.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;

namespace Retakes.Utils;

/// <summary>
/// Thin localization helper over <see cref="ILocalizerManager"/>. Every user-facing chat/center
/// line routes through here so a missing LocalizerManager degrades to a silent no-op (matching the
/// port's other optional-service handling) instead of a hardcoded English string. Culture is fixed
/// to <c>en-US</c> for server-side renders; per-client lines use the client's own locale.
/// </summary>
internal static class Loc
{
    private const string ServerCulture = "en-US";

    /// <summary>Localized chat line to one client.</summary>
    public static void Chat(ILocalizerManager? lm, IGameClient client, string key, params object?[] args)
        => lm?.For(client).Localized(key, args).Prefix(null)
              .Transform(ChatFormat.ProcessColorCodes).Print(HudPrintChannel.Chat);

    /// <summary>
    /// Localized center-text line to one client. The Center HUD channel renders plain text / HTML —
    /// NOT chat <c>\x</c> color escapes — so we deliberately do NOT run <see cref="ChatFormat.ProcessColorCodes"/>
    /// here (chat escapes would leak as literal garbage on the center channel).
    /// </summary>
    public static void Center(ILocalizerManager? lm, IGameClient client, string key, params object?[] args)
        => lm?.For(client).Localized(key, args).Prefix(null).Print(HudPrintChannel.Center);

    /// <summary>
    /// Localized center-HTML to one client via <see cref="IGameClient.PrintCenterHtml"/>. The rendered
    /// value is an HTML fragment (<c>&lt;font&gt;</c>/<c>&lt;img&gt;</c>) so it must NOT go through chat
    /// color processing. Duration is refreshed by the caller's loop to keep the panel visible.
    /// </summary>
    public static void CenterHtml(ILocalizerManager? lm, IGameClient client, int duration, string key, params object?[] args)
    {
        if (lm is null) return;
        client.PrintCenterHtml(lm.For(client).Text(key, args), duration);
    }

    /// <summary>Localized chat line to every in-game human (each rendered in their own locale).</summary>
    public static void ChatAll(ILocalizerManager? lm, IClientManager clients, string key, params object?[] args)
    {
        if (lm is null) return;
        foreach (var client in clients.GetGameClients(inGame: true))
        {
            if (client.IsFakeClient) continue;
            lm.For(client).Localized(key, args).Prefix(null)
              .Transform(ChatFormat.ProcessColorCodes).Print(HudPrintChannel.Chat);
        }
    }

    /// <summary>Per-client localized string (menu titles/items). Falls back to the key if absent.</summary>
    public static string Str(ILocalizerManager? lm, IGameClient client, string key, params object?[] args)
        => lm is null ? key : lm.For(client).Text(key, args);

    /// <summary>Server-side localized string (worldtext / shared world entities). Falls back to the key.</summary>
    public static string Format(ILocalizerManager? lm, string key, params object?[] args)
        => lm is null ? key : ChatFormat.ProcessColorCodes(lm.Format(ServerCulture, key, args));
}
