using Microsoft.Extensions.Logging;
using Retakes.Database.Models;
using SqlSugar;

namespace Retakes.Database;

/// <summary>
/// Thin wrapper around <see cref="ISqlSugarClient"/> scoped to Retakes tables.
/// Phase A: skeleton only — no queries yet. AllocatorModule (Phase D) will call into this.
/// </summary>
public sealed class RetakesDatabase : IDisposable
{
    private readonly ISqlSugarClient             _db;
    private readonly ILogger<RetakesDatabase>    _logger;

    public RetakesDatabase(ISqlSugarClient db, ILogger<RetakesDatabase> logger)
    {
        _db     = db;
        _logger = logger;
    }

    /// <summary>Create / migrate the retakes tables against the connected database.</summary>
    public void EnsureSchema()
    {
        _db.CodeFirst.InitTables<UserSetting>();
        _logger.LogInformation("[Retakes.Database] Schema ensured.");
    }

    public void Dispose()
    {
        if (_db is IDisposable d)
            d.Dispose();
    }

    // ── Queries added in Phase D ─────────────────────────────────────────────
}
