using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Retakes.Database.Models;
using Sharp.Shared;
using SqlSugar;

namespace Retakes.Database;

/// <summary>
/// Standalone ModSharp plugin that owns the Retakes MySQL/SqlSugar connection.
/// Phase A: plugin skeleton only — no published interface yet.
/// Phase D (AllocatorModule) will publish a query service via ISharpModuleManager.
/// </summary>
public sealed class RetakesDatabasePlugin : IModSharpModule
{
    public string DisplayName   => "Retakes.Database";
    public string DisplayAuthor => "yappershq";

    private readonly ServiceProvider              _services;
    private readonly ILogger<RetakesDatabasePlugin> _logger;

    public RetakesDatabasePlugin(
        ISharedSystem   sharedSystem,
        string?         dllPath,
        string?         sharpPath,
        Version?        version,
        IConfiguration? coreConfiguration,
        bool            hotReload)
    {
        ArgumentNullException.ThrowIfNull(sharedSystem);
        ArgumentNullException.ThrowIfNull(sharpPath);

        var loggerFactory = sharedSystem.GetLoggerFactory();

        var svc = new ServiceCollection();
        svc.AddSingleton<ILoggerFactory>(loggerFactory);
        svc.AddSingleton(typeof(ILogger<>), typeof(LoggerFactoryLogger<>));

        // Phase A: register a stub SqlSugar client so the project compiles.
        // Phase D: replace with real connection string loaded from retakes.database.jsonc.
        svc.AddSingleton<ISqlSugarClient>(_ => new SqlSugarScope(new ConnectionConfig
        {
            DbType            = DbType.MySql,
            ConnectionString  = "Server=127.0.0.1;Database=retakes;Uid=root;Pwd=;",
            IsAutoCloseConnection = true,
        }));

        svc.AddSingleton<RetakesDatabase>();
        _services = svc.BuildServiceProvider();
        _logger   = _services.GetRequiredService<ILogger<RetakesDatabasePlugin>>();
    }

    public bool Init()
    {
        _logger.LogInformation("[Retakes.Database] Database plugin loaded (Phase A stub).");
        return true;
    }

    public void PostInit()    { }
    public void OnAllModulesLoaded() { }

    public void Shutdown()
    {
        _services.Dispose();
    }
}

/// <summary>Generic logger adapter bridging ILogger&lt;T&gt; onto ModSharp's factory.</summary>
internal sealed class LoggerFactoryLogger<T>(ILoggerFactory factory) : ILogger<T>
{
    private readonly ILogger _inner = factory.CreateLogger(typeof(T).Name);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _inner.BeginScope(state);
    public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => _inner.Log(logLevel, eventId, state, exception, formatter);
}
