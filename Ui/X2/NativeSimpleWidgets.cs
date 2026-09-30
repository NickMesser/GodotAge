#nullable enable
namespace AAEmu.GodotViewer.Ui.X2;

/// <summary>A styled name label with a retained cache query, ready for a host cache provider.</summary>
public sealed class CharacterNameLabelWidget : LabelWidget
{
    internal CharacterNameLabelWidget(UiRoot root, string type, string id, Widget? parent) : base(root, type, id, parent) { }

    public object? CacheQueryId { get; private set; }
    public object? CacheDataHandler { get; private set; }

    public void SetValue(object? queryId, string? name = null)
    {
        CacheQueryId = queryId;
        SetText(name ?? (queryId == null ? "" : $"name_cache_{queryId}"));
    }

    public void ClearValue() { CacheQueryId = null; SetText(""); }
    public void SetCharacterCacheDataHandler(object? callback) => CacheDataHandler = callback;
    public void RequestCharacterCacheData(object? queryId) => CacheQueryId = queryId;
    public void CancelRequestCharacterCacheData() => CacheQueryId = null;

    /// <summary>The host can supply a resolved character name without replacing the active query.</summary>
    public void ResolveName(object? queryId, string? name)
    {
        if (Equals(CacheQueryId, queryId)) SetText(name);
    }
}

/// <summary>Chat input that uses the ordinary edit box keyboard and focus behavior.</summary>
public sealed class MegaphoneChatEditWidget : EditBoxWidget
{
    internal MegaphoneChatEditWidget(UiRoot root, string type, string id, Widget? parent)
        : base(root, type, id, parent, false) { }

    public int HistoryLines { get; private set; } = 20;
    public object? Channel { get; private set; }
    private readonly List<string> _history = [];
    public IReadOnlyList<string> History => _history;

    public void SetHistoryLines(double count)
    {
        HistoryLines = Math.Max(0, (int)count);
        TrimHistory();
    }

    public void SetChannel(object? channel) => Channel = channel;
    public object? GetChannel() => Channel;
    public void AddHistory(string? text)
    {
        if (HistoryLines == 0 || string.IsNullOrEmpty(text)) return;
        _history.Add(text);
        TrimHistory();
    }

    private void TrimHistory()
    {
        if (_history.Count > HistoryLines) _history.RemoveRange(0, _history.Count - HistoryLines);
    }
}

/// <summary>Tooltip rows keep left and right text associated by the AddLine index.</summary>
public sealed class UnitFrameTooltipWidget : GameTooltipWidget
{
    internal UnitFrameTooltipWidget(UiRoot root, string type, string id, Widget? parent) : base(root, type, id, parent) { }

    public object? TooltipData { get; private set; }
    public IReadOnlyDictionary<int, SideLine> SideLines => _sides;
    private readonly Dictionary<int, SideLine> _sides = [];
    private int _lineCount;

    public readonly record struct SideLine(string Text, object? Font, double Size, UiAlign Align, double Indent);

    public void SetTooltipData(object? data) => TooltipData = data;
    public override void ClearLines() { base.ClearLines(); _sides.Clear(); _lineCount = 0; }

    public override int AddLine(string? text, object? font = null, double size = 0, object? align1 = null,
        object? align2 = null, double indent = 0)
    {
        _lineCount = base.AddLine(text, font, size, align1, align2, indent);
        return _lineCount;
    }

    public override void AddAnotherSideLine(double index, string? text, object? font = null, double size = 0,
        object? align = null, double indent = 0)
    {
        var i = (int)index;
        if (i < 1 || i > _lineCount) return;
        _sides[i] = new SideLine(text ?? "", font, size, WidgetStyle.ParseAlign(align, UiAlign.Right), indent);
        var rightWidth = style.GetTextWidth(text) + (float)Math.Max(0, indent);
        SetWidth(Math.Max(GetWidth(), GetLongestLineWidth() + rightWidth + TextInset.Left + TextInset.Right + 8));
    }
}

/// <summary>Video selection state; the client host may attach a decoder later.</summary>
public sealed class AviWidget : Widget
{
    internal AviWidget(UiRoot root, string type, string id, Widget? parent) : base(root, type, id, parent) { }
    public int AviNum { get; private set; } = -1;
    public void SetAviNum(double number) => AviNum = (int)number;
    public int GetAviNum() => AviNum;
}

/// <summary>Browser request state and fallback drawable, without embedded browser execution.</summary>
public sealed class WebViewWidget : Widget
{
    internal WebViewWidget(UiRoot root, string type, string id, Widget? parent) : base(root, type, id, parent) { }

    public string Url { get; private set; } = "";
    public string RequestKind { get; private set; } = "";
    public object? RequestArgument { get; private set; }
    public bool EscEvent { get; private set; }
    public Drawable? DefaultDrawable { get; private set; }
    public int WheelPosition { get; private set; }

    public void SetURL(string? url) { Url = url ?? ""; RequestKind = "url"; RequestArgument = null; }
    public string GetURL() => Url;
    public void SetEscEvent(bool enabled) => EscEvent = enabled;
    public void SetDefaultDrawable(Drawable? drawable)
    {
        DefaultDrawable = drawable;
        drawable?.SetVisible(true);
    }
    public void WheelUp(double delta = 1) => WheelPosition -= (int)Math.Max(1, delta);
    public void WheelDown(double delta = 1) => WheelPosition += (int)Math.Max(1, delta);
    public void RequestWiki() => Request("wiki");
    public void RequestMarket() => Request("market");
    public void RequestExpeditionBBS() => Request("expedition_bbs");
    public void RequestTGOS(object? page = null) => Request("tgos", page);
    public void RequestPlayDiary(object? data = null) => Request("play_diary", data);
    public void RequestMessenger(object? data = null) => Request("messenger", data);
    private void Request(string kind, object? argument = null) { RequestKind = kind; RequestArgument = argument; }
}
