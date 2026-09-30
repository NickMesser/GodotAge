#nullable enable
using System.Collections.ObjectModel;

namespace AAEmu.GodotViewer.Ui.X2.Scripting.Api;

/// <summary>A query made by one of the economy Lua namespaces.</summary>
/// <param name="Table">The X2 namespace, for example <c>X2Auction</c>.</param>
/// <param name="Method">The native binding name.</param>
/// <param name="Arguments">Arguments after the implicit Lua colon-call receiver.</param>
public sealed record X2EconomyQuery(string Table, string Method, IReadOnlyList<object?> Arguments);

/// <summary>A command requested by one of the economy Lua namespaces.</summary>
/// <param name="Table">The X2 namespace, for example <c>X2Trade</c>.</param>
/// <param name="Method">The native binding name.</param>
/// <param name="Arguments">Arguments after the implicit Lua colon-call receiver.</param>
public sealed record X2EconomyCommand(string Table, string Method, IReadOnlyList<object?> Arguments);

/// <summary>Several values returned by a query. Values become separate Lua return values.</summary>
public sealed record X2EconomyValues(IReadOnlyList<object?> Values);

/// <summary>A craft recipe known to the client.</summary>
public sealed record X2CraftRecipe(
    int CraftType,
    int ProductItemType,
    string Name,
    int ExecutableCount = 0,
    bool Favorite = false,
    IReadOnlyDictionary<string, object?>? BaseInfo = null,
    IReadOnlyList<IReadOnlyDictionary<string, object?>>? Products = null,
    IReadOnlyList<IReadOnlyDictionary<string, object?>>? Materials = null);

/// <summary>An auction article from the most recent result page.</summary>
public sealed record X2AuctionArticle(string ArticleId, IReadOnlyDictionary<string, object?> Info, bool IsMine = false);

/// <summary>Live direct trade state.</summary>
public sealed record X2TradeState(
    bool Active = false,
    bool MineLocked = false,
    bool OtherLocked = false,
    long OfferedMoney = 0,
    long MoneyLimit = 0,
    int Currency = 0);

/// <summary>Live pet or mount state for the economy UI.</summary>
public sealed record X2MateState(
    int MateType,
    string UnitId = "",
    bool Exists = false,
    bool IsMine = false,
    bool Mountable = false,
    bool Mounted = false,
    bool Attackable = false,
    bool HasPassengerSeat = false,
    bool HasPassenger = false,
    int Speed = 0,
    uint TemplateId = 0,
    long Experience = 0,
    long PreviousLevelExperience = 0,
    long ExpToNextLevel = 0,
    IReadOnlyList<IReadOnlyDictionary<string, object?>>? Skills = null,
    string State = "");

/// <summary>Server supplied state displayed by the economy windows.</summary>
public sealed record X2EconomySnapshot
{
    public long Money { get; init; }
    public IReadOnlyDictionary<int, long> Currencies { get; init; } = EmptyInt64Map;
    public IReadOnlyList<X2CraftRecipe> CraftRecipes { get; init; } = [];
    public bool CraftOrderMode { get; init; }
    public bool Crafting { get; init; }
    public int CraftInteractionTargetId { get; init; }
    public int CraftInteractionDoodadType { get; init; }
    public int CraftBatchRemaining { get; init; }
    public IReadOnlyList<X2AuctionArticle> AuctionArticles { get; init; } = [];
    public int AuctionPage { get; init; }
    public int AuctionTotal { get; init; }
    public IReadOnlyDictionary<string, object?>? AuctionAttachedItem { get; init; }
    public X2TradeState Trade { get; init; } = new();
    public IReadOnlyDictionary<string, object?>? Butler { get; init; }
    public IReadOnlyList<X2MateState> Mates { get; init; } = [];
    public bool ShopEnabled { get; init; }
    public bool ShopReady { get; init; }
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> ShopGoods { get; init; } = [];
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> ShopCart { get; init; } = [];
    public IReadOnlyDictionary<string, object?>? ShopBuyResult { get; init; }
    public bool Premium { get; init; }
    public long PremiumEndTime { get; init; }
    public int PremiumPoint { get; init; }
    public int PremiumGradePoint { get; init; }
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> SavedCustomData { get; init; } = [];
    public bool BeautyShopEntered { get; init; }
    public IReadOnlyDictionary<string, object?>? BlessUthstin { get; init; }
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> UccPatterns { get; init; } = [];
    public IReadOnlyList<string> UccForegroundPaths { get; init; } = [];
    public bool BotActive { get; init; }
    public IReadOnlySet<int> VisibleBotData { get; init; } = EmptyIntSet;

    private static readonly IReadOnlyDictionary<int, long> EmptyInt64Map =
        new ReadOnlyDictionary<int, long>(new Dictionary<int, long>());
    private static readonly IReadOnlySet<int> EmptyIntSet = new HashSet<int>();
}

/// <summary>
/// State and requests needed by X2Craft, X2Auction, X2Trade, X2Butler, X2Mate, X2InGameShop,
/// X2PremiumService, X2Customizer, X2CustomizingUnit, X2BlessUthstin, X2Ucc and X2Bot.
/// A host may expose common state through <see cref="Snapshot"/> and answer binding-specific shapes through
/// <see cref="TryQuery"/>. Commands are requests; authoritative changes arrive later in a new snapshot and UI event.
/// </summary>
public interface IX2EconomyData
{
    X2EconomySnapshot Snapshot { get; }

    /// <summary>
    /// Supplies a binding-specific value when it is known. Tables are plain dictionaries/lists and are converted to
    /// 1-based Lua tables. Return <see langword="false"/> to use the binding's unavailable value.
    /// </summary>
    bool TryQuery(X2EconomyQuery query, out object? value);

    /// <summary>Sends an economy request. False means the feature or request is unavailable.</summary>
    bool Execute(X2EconomyCommand command);
}

/// <summary>Fresh-character economy state with every server-backed feature empty or unavailable.</summary>
public class NullEconomyData : IX2EconomyData
{
    public static NullEconomyData Instance { get; } = new();
    public virtual X2EconomySnapshot Snapshot { get; } = new();

    public virtual bool TryQuery(X2EconomyQuery query, out object? value)
    {
        value = null;
        return false;
    }

    public virtual bool Execute(X2EconomyCommand command) => false;
}
