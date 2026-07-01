using Retakes.Shared;
using Sharp.Shared.Enums;

namespace Retakes.Allocator;

/// <summary>
/// Faithful port of RetakesAllocatorCore/NadeHelpers.cs.
/// Config is passed explicitly to avoid global state.
/// </summary>
public static class NadeHelpers
{
    public const string GlobalSettingName = "GLOBAL";

    /// <summary>
    /// Build the team's nade pool for this round as a stack.
    /// Players will be assigned nades from this pool in round-robin order.
    /// </summary>
    public static Stack<CsItem> GetUtilForTeam(
        string? map, RoundType roundType, CStrikeTeam team, int numPlayers, AllocatorSettings config)
    {
        map ??= GlobalSettingName;

        var maxNadesSetting = GetMaxTeamNades(map, team, roundType, config);
        if (maxNadesSetting == MaxTeamNadesSetting.None) return new();

        var multiplier = maxNadesSetting switch
        {
            MaxTeamNadesSetting.AveragePointFivePerPlayer    => 0.5,
            MaxTeamNadesSetting.AverageOnePerPlayer          => 1.0,
            MaxTeamNadesSetting.AverageOnePointFivePerPlayer => 1.5,
            MaxTeamNadesSetting.AverageTwoPerPlayer          => 2.0,
            _                                                => 0.0,
        };

        var maxTotal = maxNadesSetting switch
        {
            MaxTeamNadesSetting.One   => 1,
            MaxTeamNadesSetting.Two   => 2,
            MaxTeamNadesSetting.Three => 3,
            MaxTeamNadesSetting.Four  => 4,
            MaxTeamNadesSetting.Five  => 5,
            MaxTeamNadesSetting.Six   => 6,
            MaxTeamNadesSetting.Seven => 7,
            MaxTeamNadesSetting.Eight => 8,
            MaxTeamNadesSetting.Nine  => 9,
            MaxTeamNadesSetting.Ten   => 10,
            _                         => (int)Math.Ceiling(numPlayers * multiplier),
        };

        var molly = team == CStrikeTeam.TE ? CsItem.Molotov : CsItem.Incendiary;

        // Weighted distribution list — picked randomly; exhausted items are dropped
        var distribution = new List<CsItem>
        {
            CsItem.Flashbang, CsItem.Flashbang, CsItem.Flashbang, CsItem.Flashbang,
            CsItem.Smoke, CsItem.Smoke, CsItem.Smoke,
            CsItem.HE, CsItem.HE, CsItem.HE,
            molly, molly,
        };

        var caps = new Dictionary<CsItem, int>
        {
            { CsItem.Flashbang, GetMaxNades(map, team, CsItem.Flashbang, config) },
            { CsItem.Smoke,     GetMaxNades(map, team, CsItem.Smoke,     config) },
            { CsItem.HE,        GetMaxNades(map, team, CsItem.HE,        config) },
            { molly,            GetMaxNades(map, team, molly,             config) },
        };

        var nades = new Stack<CsItem>();
        while (caps.Count > 0 && maxTotal > 0)
        {
            var next = CollectionUtils.Choice(distribution);
            if (caps[next] <= 0)
            {
                distribution.RemoveAll(i => i == next);
                caps.Remove(next);
                continue;
            }
            nades.Push(next);
            caps[next]--;
            maxTotal--;
        }
        return nades;
    }

    /// <summary>
    /// Distribute the team's nade pool to individual players in round-robin fashion.
    /// Players who have reached their per-slot cap are skipped.
    /// </summary>
    public static void AllocateNadesToPlayers(
        Stack<CsItem> teamNades, ICollection<ulong> teamPlayers,
        Dictionary<ulong, List<CsItem>> nadesByPlayer)
    {
        if (teamPlayers.Count == 0 || teamNades.Count == 0) return;

        var shuffled = new List<ulong>(teamPlayers);
        CollectionUtils.Shuffle(shuffled);

        var idx = 0;
        while (teamNades.Count > 0 && shuffled.Count > 0)
        {
            var player = shuffled[idx];
            if (!nadesByPlayer.TryGetValue(player, out var playerNades))
            {
                playerNades = new();
                nadesByPlayer[player] = playerNades;
            }

            if (PlayerReachedMaxNades(playerNades))
            {
                shuffled.RemoveAt(idx);
                if (idx >= shuffled.Count) idx = 0;
                continue;
            }

            if (!teamNades.TryPop(out var nade)) break;
            playerNades.Add(nade);

            idx = (idx + 1) % shuffled.Count;
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static MaxTeamNadesSetting GetMaxTeamNades(
        string map, CStrikeTeam team, RoundType roundType, AllocatorSettings config)
    {
        var teamKey = team.ToString();
        if (config.MaxTeamNades.TryGetValue(map, out var mapNades)
            && mapNades.TryGetValue(teamKey, out var teamNades)
            && teamNades.TryGetValue(roundType, out var setting))
        {
            return setting;
        }

        if (map == GlobalSettingName) return MaxTeamNadesSetting.None;
        return GetMaxTeamNades(GlobalSettingName, team, roundType, config);
    }

    private static int GetMaxNades(string map, CStrikeTeam team, CsItem nade, AllocatorSettings config)
    {
        var teamKey = team.ToString();
        var nadeName = nade.GetName();

        if (config.MaxNades.TryGetValue(map, out var mapNades)
            && mapNades.TryGetValue(teamKey, out var teamNades))
        {
            if (teamNades.TryGetValue(nadeName, out var count)) return count;

            // Molotov/Incendiary fallback: try the other fire nade name
            if (nade is CsItem.Molotov or CsItem.Incendiary)
            {
                var otherName = nade == CsItem.Molotov
                    ? CsItem.Incendiary.GetName()
                    : CsItem.Molotov.GetName();
                if (teamNades.TryGetValue(otherName, out var otherCount)) return otherCount;
            }
        }

        if (map == GlobalSettingName) return 999999;
        return GetMaxNades(GlobalSettingName, team, nade, config);
    }

    private static bool PlayerReachedMaxNades(ICollection<CsItem> nades)
    {
        var allowance = new Dictionary<CsItem, int>
        {
            { CsItem.Flashbang,  2 },
            { CsItem.Smoke,      1 },
            { CsItem.HE,         1 },
            { CsItem.Molotov,    1 },
            { CsItem.Incendiary, 1 },
        };
        foreach (var n in nades)
        {
            if (!allowance.ContainsKey(n) || allowance[n] <= 0) return true;
            allowance[n]--;
        }
        return false;
    }
}
