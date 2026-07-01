using System.Collections.Generic;
using System.Text.Json.Serialization;
using Retakes.Allocator;

namespace Retakes.Config;

public class QueuePriorityFlagConfig
{
    [JsonPropertyName("display_name")] public string DisplayName { get; set; } = "VIP";
    [JsonPropertyName("flag")]         public string Flag        { get; set; } = "@css/vip";
    [JsonPropertyName("priority")]     public int    Priority    { get; set; } = 0;
}

public class GameSettings
{
    [JsonPropertyName("max_players")]                public int  MaxPlayers               { get; set; } = 9;
    [JsonPropertyName("should_break_breakables")]    public bool ShouldBreakBreakables    { get; set; } = false;
    [JsonPropertyName("should_open_doors")]          public bool ShouldOpenDoors          { get; set; } = false;
    /// <summary>
    /// Fallback allocator active only when Allocator.Enabled is false.
    /// Kept for backwards-compat; overridden by Allocator.Enabled check at runtime.
    /// </summary>
    [JsonPropertyName("enable_fallback_allocation")] public bool EnableFallbackAllocation { get; set; } = false;
}

public class QueueSettings
{
    [JsonPropertyName("priority_flags")]              public List<QueuePriorityFlagConfig> PriorityFlags          { get; set; } = [new QueuePriorityFlagConfig()];
    [JsonPropertyName("immunity_flags")]              public List<QueuePriorityFlagConfig> ImmunityFlags          { get; set; } = [];
    [JsonPropertyName("should_remove_spectators")]    public bool ShouldRemoveSpectators    { get; set; } = true;
    [JsonPropertyName("should_auto_join_spectators")] public bool ShouldAutoJoinSpectators  { get; set; } = true;

    [JsonPropertyName("should_force_even_teams_when_player_count_is_multiple_of_10")]
    public bool ShouldForceEvenTeamsWhenPlayerCountIsMultipleOf10 { get; set; } = true;
}

public class TeamSettings
{
    [JsonPropertyName("terrorist_ratio")]                       public float TerroristRatio                   { get; set; } = 0.45f;
    [JsonPropertyName("rounds_to_scramble")]                    public int   RoundsToScramble                 { get; set; } = 5;
    [JsonPropertyName("is_scramble_enabled")]                   public bool  IsScrambleEnabled                { get; set; } = true;
    [JsonPropertyName("is_balance_enabled")]                    public bool  IsBalanceEnabled                 { get; set; } = true;
    [JsonPropertyName("should_prevent_team_changes_mid_round")] public bool ShouldPreventTeamChangesMidRound { get; set; } = true;
}

public class MapConfigSettings
{
    [JsonPropertyName("enable_bombsite_announcement_center")] public bool EnableBombsiteAnnouncementCenter { get; set; } = true;
    [JsonPropertyName("enable_bombsite_announcement_voices")] public bool EnableBombsiteAnnouncementVoices { get; set; } = true;
}

public class BombSettings
{
    [JsonPropertyName("is_auto_plant_enabled")] public bool IsAutoPlantEnabled { get; set; } = true;
}

public class CommandsSettings
{
    /// <summary>Permission flag required for spawn-editor commands (!showspawns/!add/!remove/!nearest/!done).</summary>
    [JsonPropertyName("spawn_editor_flag")] public string SpawnEditor { get; set; } = "@css/root";

    /// <summary>Permission flag required for map-config commands (!mapconfig/!mapconfigs).</summary>
    [JsonPropertyName("map_config_flag")] public string MapConfig { get; set; } = "@css/root";

    /// <summary>Permission flag required for admin commands (!forcebombsite/!scramble/!debugqueues).</summary>
    [JsonPropertyName("admin_flag")] public string Admin { get; set; } = "@css/admin";
}

public class RetakesConfig
{
    [JsonPropertyName("game")]       public GameSettings       Game       { get; set; } = new();
    [JsonPropertyName("queue")]      public QueueSettings      Queue      { get; set; } = new();
    [JsonPropertyName("teams")]      public TeamSettings       Teams      { get; set; } = new();
    [JsonPropertyName("map_config")] public MapConfigSettings  MapConfig  { get; set; } = new();
    [JsonPropertyName("bomb")]       public BombSettings       Bomb       { get; set; } = new();
    [JsonPropertyName("allocator")]  public AllocatorSettings  Allocator  { get; set; } = new();
    [JsonPropertyName("commands")]   public CommandsSettings   Commands   { get; set; } = new();
}
