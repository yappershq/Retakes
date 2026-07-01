namespace Retakes.Player;

/// <summary>Shared SteamID validation — consolidated from file-local duplicates in player + round-flow modules.</summary>
internal static class SteamIdExtensions
{
    internal static bool IsValidSteamId(this ulong steamId)
        => steamId > 76561197960265728UL;
}
