#nullable enable
using AAEmu.GodotViewer.Client;
using AAEmu.GodotViewer.Net;
using AAEmu.GodotViewer.Ui.X2.Scripting.Api;

namespace AAEmu.GodotViewer.Ui.X2.Online;

public sealed partial class X2ProtocolEconomyData
{
    private const ushort SCICSMenuList = 587, SCICSGoodList = 588;
    private OnlineSession? _shopSession;
    private readonly Dictionary<int, int[]> _shopTabs = [];
    private readonly List<IReadOnlyDictionary<string, object?>> _shopGoods = [];
    private int _shopMain = 1, _shopSub = 1, _shopPage = 1;
    private bool _shopMenuReceived;
    private bool ShopEnabled { get; set; }
    private bool ShopReady { get; set; }
    private IReadOnlyList<IReadOnlyDictionary<string, object?>> ShopGoods => _shopGoods;

    private void HookShop(OnlineSession session)
    {
        if (ReferenceEquals(_shopSession, session)) return;
        if (_shopSession != null) _shopSession.RawPacket -= OnShopPacket;
        _shopSession = session;
        session.RawPacket += OnShopPacket;
    }

    private void OnShopPacket(RawPacketEvent packet)
    {
        try
        {
            var r = new ShopReader(packet.Body);
            if (packet.Opcode == SCICSMenuList)
            {
                ShopEnabled = r.Bool(); _shopTabs.Clear();
                _shopMenuReceived = true;
                if (ShopEnabled)
                    for (var main = 0; main < 9; main++)
                    {
                        var present = r.Bool(); var subs = new List<int>();
                        for (var sub = 0; sub < 8; sub++) if (r.U8() > 0) subs.Add(sub);
                        if (present && main > 0) _shopTabs[main] = subs.Where(x => x > 0).ToArray();
                    }
                ShopReady = true;
                _fireEvent?.Invoke("UPDATE_INGAME_SHOP", ["maintab"]);
            }
            else if (packet.Opcode == SCICSGoodList)
            {
                var count = Math.Min(r.U16(), (ushort)50);
                var received = new List<IReadOnlyDictionary<string, object?>>(count);
                for (var i = 0; i < count; i++) received.Add(ReadGood(r));
                if (received.FirstOrDefault() is { } first)
                {
                    var main = Convert.ToInt32(first["mainTab"]);
                    var sub = Convert.ToInt32(first["subTab"]);
                    _shopGoods.RemoveAll(x => Convert.ToInt32(x["mainTab"]) == main && Convert.ToInt32(x["subTab"]) == sub);
                    _shopGoods.AddRange(received);
                }
                var visibleCount = _shopGoods.Count(x => Convert.ToInt32(x["mainTab"]) == _shopMain && Convert.ToInt32(x["subTab"]) == _shopSub);
                _fireEvent?.Invoke("UPDATE_INGAME_SHOP", ["goods", _shopPage, visibleCount]);
            }
        }
        catch { /* malformed raw packets remain visible to the normal packet diagnostics */ }
    }

    private bool TryQueryShop(X2EconomyQuery query, out object? value)
    {
        value = null; var session = _session(); if (session != null) HookShop(session);
        switch (query.Method)
        {
            case "CheckReady" when _shopMenuReceived: value = true; return true;
            case "IsInGameShopEnable" when _shopMenuReceived: value = ShopEnabled; return true;
            case "GetMainTabs": value = _shopTabs.Keys.Order().ToArray(); return _shopTabs.Count > 0;
            case "GetFirstMainTab": value = _shopTabs.Keys.Order().FirstOrDefault(); return _shopTabs.Count > 0;
            case "GetSubTabs": value = _shopTabs.GetValueOrDefault(_shopMain) ?? []; return _shopTabs.Count > 0;
            case "GetFirstSubTab": value = _shopTabs.GetValueOrDefault(Int(query.Arguments, 0))?.FirstOrDefault() ?? 0; return _shopTabs.Count > 0;
            case "GetGoodsPerPage": value = _shopMain == 1 && _shopSub == 1 ? 4 : 8; return true;
            case "GetGoods":
                if (Math.Max(1, Int(query.Arguments, 0)) > 1) { value = null; return true; }
                var visible = _shopGoods.Where(x => Convert.ToInt32(x["mainTab"]) == _shopMain && Convert.ToInt32(x["subTab"]) == _shopSub).ToArray();
                var index = (Math.Max(1, Int(query.Arguments, 0)) - 1) * (_shopMain == 1 && _shopSub == 1 ? 4 : 8) + Math.Max(1, Int(query.Arguments, 1)) - 1;
                value = index < visible.Length ? visible[index] : null; return true;
            default: return false;
        }
    }

    private bool ExecuteShop(X2EconomyCommand command, OnlineSession session, ClientActions actions)
    {
        HookShop(session);
        switch (command.Method)
        {
            case "RequestMenuList": actions.RequestIcsMenuList(); return true;
            case "SelectMainTab": _shopMain = Int(command.Arguments, 0); _shopSub = Int(command.Arguments, 1); _shopPage = 1; actions.RequestIcsMenuList(); return true;
            case "SelectSubTab": _shopSub = Int(command.Arguments, 0); _shopPage = 1; actions.RequestIcsMenuList(); return true;
            case "SelectPage":
                _shopPage = Math.Max(1, Int(command.Arguments, 0));
                if (_shopPage == 1) actions.RequestIcsMenuList();
                else _fireEvent?.Invoke("UPDATE_INGAME_SHOP", ["goods", _shopPage, 0]);
                return true;
            default: return false;
        }
    }

    private static IReadOnlyDictionary<string, object?> ReadGood(ShopReader r)
    {
        var id = r.U32(); var name = r.Str(); var main = r.U8(); var sub = r.U8();
        var minLevel = r.U8(); var maxLevel = r.U8(); var itemType = r.S32(); var displayMode = r.U8();
        var limitType = r.U8(); var buyCount = r.U16(); var buyType = r.U8(); var buyId = r.U32();
        var start = r.S64(); var end = r.S64(); var currency = r.U8(); var price = r.U32();
        var remaining = r.S32(); var bonusType = r.U32(); var bonusCount = r.U32(); var cmdUi = r.U8();
        var payItemType = r.U32(); var discountPrice = r.U32();
        return new Dictionary<string, object?>
        {
            ["cashShopId"] = id, ["mainTab"] = main, ["subTab"] = sub,
            ["name"] = name, ["itemType"] = itemType, ["count"] = 1, ["isKnown"] = true,
            ["isBuyable"] = displayMode != 0, ["isLimitTabGoods"] = limitType != 0,
            ["level"] = minLevel, ["levelMax"] = maxLevel, ["buyType"] = "level", ["buyId"] = buyId,
            ["limitType"] = limitType, ["buyCount"] = (int)buyCount, ["remainBuyCount"] = remaining,
            ["sdate"] = start, ["edate"] = end, ["priceType"] = currency, ["price"] = price,
            ["bonusType"] = bonusType, ["bonusCount"] = bonusCount, ["eventType"] = displayMode,
            ["cmdUi"] = cmdUi, ["payItemType"] = payItemType, ["disPrice"] = discountPrice,
        };
    }

    private sealed class ShopReader(byte[] body)
    {
        private int p; private void Need(int n) { if (p + n > body.Length) throw new InvalidDataException(); }
        public byte U8() { Need(1); return body[p++]; } public bool Bool() => U8() != 0;
        public ushort U16() { Need(2); var v = BitConverter.ToUInt16(body, p); p += 2; return v; }
        public uint U32() { Need(4); var v = BitConverter.ToUInt32(body, p); p += 4; return v; }
        public int S32() { Need(4); var v = BitConverter.ToInt32(body, p); p += 4; return v; }
        public long S64() { Need(8); var v = BitConverter.ToInt64(body, p); p += 8; return v; }
        public string Str() { var n = U16(); Need(n); var v = System.Text.Encoding.UTF8.GetString(body, p, n).TrimEnd('\0'); p += n; return v; }
    }
}
