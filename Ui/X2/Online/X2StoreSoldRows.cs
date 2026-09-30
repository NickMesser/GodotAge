#nullable enable

using System.Globalization;
using AAEmu.GodotViewer.Lua;
using AAEmu.GodotViewer.Ui.X2.Scripting.Api;

namespace AAEmu.GodotViewer.Ui.X2.Online;

/// <summary>Projects a server buyback item into the exact row consumed by store/rebuy.lua.</summary>
internal static class X2StoreSoldRows
{
    internal static LuaTable Build(X2StoreItem item)
    {
        var row = new LuaTable();
        if (item.Fields is not null)
            foreach (var (key, value) in item.Fields)
                row[key] = LuaValue(value);
        row["goodIndex"] = (double)item.GoodIndex;
        row["itemType"] = (double)item.ItemType;
        row["name"] = item.Name;
        row["icon"] = item.Icon;
        row["stack"] = (double)item.Stack;
        row["maxStack"] = (double)item.MaxStack;
        row["cost"] = item.Cost.ToString(CultureInfo.InvariantCulture);
        row["currency"] = (double)item.Currency;
        row["currencyStr"] = item.CurrencyName;
        row["soldout"] = item.SoldOut;
        row["singless"] = item.SinglePurchase;
        row["isStackable"] = item.IsStackable;
        row["purchaseType"] = (double)item.PurchaseType;
        row["purchaseLimit"] = (double)item.PurchaseLimit;
        row["purchaseBuyCount"] = (double)item.PurchaseBuyCount;
        return row;
    }

    private static object? LuaValue(object? value)
    {
        if (value is null or string || value.GetType().IsPrimitive || value is decimal)
            return value;
        if (value is System.Collections.IEnumerable values)
        {
            var table = new LuaTable();
            var index = 1;
            foreach (var entry in values)
                table[(double)index++] = LuaValue(entry);
            return table;
        }
        return value;
    }
}
