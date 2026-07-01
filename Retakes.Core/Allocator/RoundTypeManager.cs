using Retakes.Config;
using Retakes.Shared;

namespace Retakes.Allocator;

/// <summary>
/// Faithful port of RetakesAllocatorCore/Managers/RoundTypeManager.cs.
/// Tracks round-type sequencing (Random / RandomFixedCounts / ManualOrdering) + admin one-shot override.
/// Registered as a singleton in Core's DI and injected into RoundFlowModule.
/// </summary>
internal sealed class RoundTypeManager
{
    private readonly ConfigModule _config;

    private string? _map;
    private RoundType? _nextRoundTypeOverride;
    private RoundType? _currentRoundType;

    private RoundTypeSelectionOption _selection;
    private readonly List<RoundType> _roundsOrder = [];
    private int _orderPosition;

    public RoundTypeManager(ConfigModule config)
    {
        _config = config;
    }

    /// <summary>
    /// (Re)initialise round-type sequencing from current config.
    /// Call from RoundFlowModule.OnPostInit() after ConfigModule.Init() has run.
    /// Also call on map change (Phase E) to reset ManualOrdering position.
    /// </summary>
    public void Initialize()
    {
        _nextRoundTypeOverride = null;
        _currentRoundType = null;
        _selection = _config.Config.Allocator.RoundTypeSelection;

        _roundsOrder.Clear();
        var allocCfg = _config.Config.Allocator;

        switch (_selection)
        {
            case RoundTypeSelectionOption.RandomFixedCounts:
                foreach (var (roundType, count) in allocCfg.RoundTypeRandomFixedCounts)
                    for (var i = 0; i < count; i++)
                        _roundsOrder.Add(roundType);
                CollectionUtils.Shuffle(_roundsOrder);
                break;

            case RoundTypeSelectionOption.ManualOrdering:
                foreach (var item in allocCfg.RoundTypeManualOrdering)
                    for (var i = 0; i < item.Count; i++)
                        _roundsOrder.Add(item.Type);
                break;
        }

        _orderPosition = 0;
    }

    public void SetMap(string map)
    {
        _map = map;
        // TODO Phase E: re-initialise on map change so map-specific nade configs take effect
    }

    public string? Map => _map;

    /// <summary>
    /// Get and advance to the next round type.
    /// If an admin override is set it is consumed (one-shot) and returned.
    /// </summary>
    public RoundType GetNextRoundType()
    {
        if (_nextRoundTypeOverride is not null)
        {
            var forced = _nextRoundTypeOverride.Value;
            _nextRoundTypeOverride = null;
            return forced;
        }

        return _selection switch
        {
            RoundTypeSelectionOption.Random           => GetRandomRoundType(),
            RoundTypeSelectionOption.ManualOrdering   => GetNextInOrder(),
            RoundTypeSelectionOption.RandomFixedCounts => GetNextInOrder(),
            _ => throw new InvalidOperationException($"Unknown RoundTypeSelectionOption: {_selection}"),
        };
    }

    public RoundType? CurrentRoundType => _currentRoundType;

    public void SetCurrentRoundType(RoundType? roundType) => _currentRoundType = roundType;

    /// <summary>Admin one-shot override — cleared after the next round type is consumed.</summary>
    public void SetNextRoundTypeOverride(RoundType? roundType) => _nextRoundTypeOverride = roundType;

    // ── Private ───────────────────────────────────────────────────────────────

    private RoundType GetNextInOrder()
    {
        if (_roundsOrder.Count == 0) return RoundType.FullBuy; // safety fallback
        if (_orderPosition >= _roundsOrder.Count) _orderPosition = 0;
        return _roundsOrder[_orderPosition++];
    }

    private RoundType GetRandomRoundType()
    {
        var roll = Random.Shared.NextDouble();
        var cfg = _config.Config.Allocator;

        var pistolPct = cfg.GetRoundTypePercentage(RoundType.Pistol);
        if (roll < pistolPct) return RoundType.Pistol;

        var halfPct = cfg.GetRoundTypePercentage(RoundType.HalfBuy);
        if (roll < pistolPct + halfPct) return RoundType.HalfBuy;

        return RoundType.FullBuy;
    }
}
