using System.IO;
using Microsoft.Extensions.Logging;
using Sharp.Modules.LocalizerManager.Shared;
using Sharp.Shared;
using Sharp.Shared.Managers;

namespace Retakes;

/// <summary>
/// Single cached gateway to every ModSharp manager the Core plugin needs.
/// Built once in the plugin ctor; optional external modules resolved in OnAllModulesLoaded.
/// </summary>
internal sealed class InterfaceBridge
{
    // === Paths ===
    internal string SharpPath  { get; }
    internal string ConfigPath { get; }
    internal string DataPath   { get; }

    // === Managers ===
    internal IEntityManager  EntityManager  { get; }
    internal IClientManager  ClientManager  { get; }
    internal IConVarManager  ConVarManager  { get; }
    internal IHookManager    HookManager    { get; }
    internal ISchemaManager  SchemaManager  { get; }
    internal IEventManager   EventManager   { get; }
    internal IFileManager    FileManager    { get; }

    // === Services ===
    internal IModSharp           ModSharp           { get; }
    internal ILoggerFactory      LoggerFactory      { get; }
    internal ISharpModuleManager SharpModuleManager { get; }
    internal IModSharpModule     Module             { get; }

    /// <summary>
    /// Optional localization service. Resolved in <c>OnAllModulesLoaded</c> (never Init/PostInit —
    /// the publishing plugin may not have registered it yet); null when the module isn't installed.
    /// </summary>
    internal ILocalizerManager?  LocalizerManager   { get; set; }

    public InterfaceBridge(IModSharpModule module, ISharedSystem sharedSystem, string sharpPath, ILoggerFactory loggerFactory)
    {
        Module   = module;

        SharpPath  = sharpPath;
        ConfigPath = Path.Combine(sharpPath, "configs", "retakes");
        DataPath   = Path.Combine(sharpPath, "data",    "retakes");

        Directory.CreateDirectory(ConfigPath);
        Directory.CreateDirectory(DataPath);

        EntityManager = sharedSystem.GetEntityManager();
        ClientManager = sharedSystem.GetClientManager();
        ConVarManager = sharedSystem.GetConVarManager();
        HookManager   = sharedSystem.GetHookManager();
        SchemaManager = sharedSystem.GetSchemaManager();
        EventManager  = sharedSystem.GetEventManager();
        FileManager   = sharedSystem.GetFileManager();

        ModSharp           = sharedSystem.GetModSharp();
        LoggerFactory      = loggerFactory;
        SharpModuleManager = sharedSystem.GetSharpModuleManager();
    }
}
