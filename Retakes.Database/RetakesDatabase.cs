using Microsoft.Extensions.Logging;
using Retakes.Database.Models;
using SqlSugar;

namespace Retakes.Database;

/// <summary>
/// Thin wrapper around <see cref="ISqlSugarClient"/> scoped to Retakes tables.
/// Exposes typed query methods so Core's AllocatorModule never touches SqlSugar directly.
/// ORM types (ISqlSugarClient) never cross into Retakes.Shared or Retakes.Core.
/// </summary>
public sealed class RetakesDatabase : IDisposable
{
    private readonly ISqlSugarClient          _db;
    private readonly ILogger<RetakesDatabase> _logger;

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

    // ── Queries ───────────────────────────────────────────────────────────

    /// <summary>
    /// Batch-load weapon preferences for multiple players.
    /// One <c>WHERE UserId IN (...)</c> query — never N separate queries.
    /// ulong→long at the CLR boundary.
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

    /// <summary>Get a single player's settings, or null if not found.</summary>
    public UserSetting? GetUserSettings(ulong steamId)
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
            _logger.LogError(ex, "[Retakes.Database] GetUserSettings({Id}) failed.", steamId);
            return null;
        }
    }

    /// <summary>
    /// Fire-and-forget upsert: store the new prefs JSON for a player.
    /// Caller provides the already-serialised JSON blob.
    /// </summary>
    public void SetWeaponPreference(ulong steamId, string prefsJson)
    {
        _ = Task.Run(() =>
        {
            var id = (long)steamId;
            try
            {
                var existing = _db.Queryable<UserSetting>()
                    .Where(u => u.UserId == id)
                    .First();

                if (existing is null)
                {
                    _db.Insertable(new UserSetting { UserId = id, WeaponPreferencesJson = prefsJson })
                       .ExecuteCommand();
                }
                else
                {
                    existing.WeaponPreferencesJson = prefsJson;
                    _db.Updateable(existing).ExecuteCommand();
                }
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
