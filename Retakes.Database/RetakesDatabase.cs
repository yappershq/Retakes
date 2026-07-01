using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Retakes.Database.Models;
using SqlSugar;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Retakes")]

namespace Retakes.Database;

/// <summary>
/// Thin wrapper around <see cref="ISqlSugarClient"/> scoped to Retakes tables.
/// Exposes typed query methods so Core's AllocatorModule never touches SqlSugar directly.
/// ORM types (ISqlSugarClient) never cross into Retakes.Shared or Retakes.Core.
///
/// <para>
/// Write-through pref cache: populated async on <c>PrefetchUserAsync</c> (called from
/// OnClientPutInServer on the game thread), kept in sync on every <c>SetCachedWeaponPreference</c>
/// write, evicted on disconnect via <c>EvictUser</c>. Game-thread reads never touch the DB.
/// </para>
/// </summary>
internal sealed class RetakesDatabase : IDisposable
{
    private readonly ISqlSugarClient          _db;
    private readonly ILogger<RetakesDatabase> _logger;

    // Write-through pref cache: game-thread reads, bg-task initial load, game-thread writes.
    private readonly ConcurrentDictionary<ulong, UserSetting> _prefCache = new();

    public RetakesDatabase(ISqlSugarClient db, ILogger<RetakesDatabase> logger)
    {
        _db     = db;
        _logger = logger;
    }

    /// <summary>
    /// Factory: create a <see cref="RetakesDatabase"/> backed by a MySQL connection.
    /// Called from Core's DI setup so Core never imports SqlSugar types.
    /// </summary>
    public static RetakesDatabase Create(string connectionString, ILogger<RetakesDatabase> logger)
    {
        var client = new SqlSugarScope(new ConnectionConfig
        {
            DbType                = DbType.MySql,
            ConnectionString      = connectionString,
            IsAutoCloseConnection = true,
        });
        return new RetakesDatabase(client, logger);
    }

    // ── Schema management ─────────────────────────────────────────────────

    /// <summary>Create / migrate the retakes tables against the connected database.</summary>
    public void EnsureSchema()
    {
        _db.CodeFirst.InitTables<UserSetting>();
        _logger.LogInformation("[Retakes.Database] Schema ensured.");
    }

    // ── Pref cache (game-thread safe) ──────────────────────────────────────

    /// <summary>
    /// Seed the cache with a default entry then load from DB on a background task.
    /// Call from <c>OnClientPutInServer</c> (game thread). On cache miss, callers
    /// always get a default — no DB call on the game thread.
    /// </summary>
    public void PrefetchUserAsync(ulong steamId)
    {
        // Seed immediately so any game-thread read before load completes returns a default, not a miss.
        _prefCache.TryAdd(steamId, new UserSetting { UserId = (long)steamId });

        _ = Task.Run(() =>
        {
            var loaded = GetUserSettingsFromDb(steamId);
            if (loaded is null) return;

            // Only populate from DB if player hasn't already set prefs since connect
            // (TryAdd with loaded row; if key already exists keep the in-game write).
            // ponytail: AddOrUpdate ensures DB prefs win only when no in-game pref has been written yet.
            _prefCache.AddOrUpdate(
                steamId,
                loaded,
                (_, existing) => string.IsNullOrEmpty(existing.WeaponPreferencesJson) || existing.WeaponPreferencesJson == "{}"
                    ? loaded
                    : existing);
        });
    }

    /// <summary>
    /// Get cached settings for a player. Returns a default if not yet loaded — no DB call.
    /// On a cache miss (before prefetch completes), seeds the cache with a default entry.
    /// </summary>
    public UserSetting GetCachedUserSettings(ulong steamId)
        => _prefCache.GetOrAdd(steamId, id => new UserSetting { UserId = (long)id });

    /// <summary>
    /// Update the in-memory cache immediately, then queue an async DB write (fire-and-forget).
    /// Always call this instead of the raw <see cref="SetWeaponPreference"/> from game-thread code.
    /// </summary>
    public void SetCachedWeaponPreference(ulong steamId, string prefsJson)
    {
        var entry = _prefCache.GetOrAdd(steamId, id => new UserSetting { UserId = (long)id });
        entry.WeaponPreferencesJson = prefsJson;
        SetWeaponPreference(steamId, prefsJson);
    }

    /// <summary>Remove the player's entry from the pref cache on disconnect.</summary>
    public void EvictUser(ulong steamId) => _prefCache.TryRemove(steamId, out _);

    // ── Direct DB queries (for background tasks and schema tools only) ─────

    /// <summary>
    /// Batch-load weapon preferences for multiple players.
    /// One <c>WHERE UserId IN (...)</c> query — never N separate queries.
    /// ulong→long at the CLR boundary.
    /// NOTE: Not safe to call on the game thread — use <see cref="GetCachedUserSettings"/> instead.
    /// </summary>
    public Dictionary<ulong, UserSetting> GetUsersSettings(IEnumerable<ulong> steamIds)
    {
        var ids = steamIds.Select(id => (long)id).ToList();
        if (ids.Count == 0) return new();

        try
        {
            var rows = _db.Queryable<UserSetting>()
                .Where(u => ids.Contains(u.UserId))
                .ToList();
            return rows.ToDictionary(u => (ulong)u.UserId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Retakes.Database] GetUsersSettings failed.");
            return new();
        }
    }

    /// <summary>Get a single player's settings from the DB, or null if not found.</summary>
    private UserSetting? GetUserSettingsFromDb(ulong steamId)
    {
        var id = (long)steamId;
        try
        {
            return _db.Queryable<UserSetting>()
                .Where(u => u.UserId == id)
                .First();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Retakes.Database] GetUserSettingsFromDb({Id}) failed.", steamId);
            return null;
        }
    }

    /// <summary>
    /// Fire-and-forget atomic upsert: store the new prefs JSON for a player.
    /// Uses <c>Storageable</c> (INSERT … ON DUPLICATE KEY UPDATE) — no read-then-insert race.
    /// </summary>
    private void SetWeaponPreference(ulong steamId, string prefsJson)
    {
        _ = Task.Run(() =>
        {
            try
            {
                _db.Storageable(new UserSetting { UserId = (long)steamId, WeaponPreferencesJson = prefsJson })
                   .ExecuteCommand();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Retakes.Database] SetWeaponPreference({Id}) failed.", steamId);
            }
        });
    }

    // ── IDisposable ───────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_db is IDisposable d) d.Dispose();
    }
}
