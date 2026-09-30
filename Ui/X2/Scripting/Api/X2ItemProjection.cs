#nullable enable
using System.Globalization;

namespace AAEmu.GodotViewer.Ui.X2.Scripting.Api;

/// <summary>Protocol instance values that supplement a read-only item template for Lua.</summary>
public sealed record X2ItemInstance(uint TemplateId, ulong ItemId, byte Grade, byte Flags, int Count,
    byte DetailType, long CreateTime, int LifespanMinutes, ulong MadeUnitId, long UnsecureTime,
    long UnpackTime, long ChargeUseSkillTime, byte? Durability = null, ushort? EnchantScale = null,
    uint? ImageTemplateId = null, uint? DyeColor = null);

public static class X2ItemProjection
{
    /// <summary>The same item projection is used by the online inventory and offline Lua hover harness.</summary>
    public static X2InventoryItem ToX2Item(X2InventoryItem? definition, X2ItemInstance snapshot,
        int? equipSlot = null)
    {
        var fields = definition is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(definition.Fields);
        fields["flags"] = (double)snapshot.Flags;
        fields["detailType"] = (double)snapshot.DetailType;
        fields["createTime"] = snapshot.CreateTime.ToString(CultureInfo.InvariantCulture);
        fields["lifespanMinutes"] = (double)snapshot.LifespanMinutes;
        fields["madeUnitId"] = (double)snapshot.MadeUnitId;
        fields["unsecureTime"] = snapshot.UnsecureTime.ToString(CultureInfo.InvariantCulture);
        fields["unpackTime"] = snapshot.UnpackTime.ToString(CultureInfo.InvariantCulture);
        fields["chargeUseSkillTime"] = snapshot.ChargeUseSkillTime.ToString(CultureInfo.InvariantCulture);
        var template = definition is null ? null : definition with { Grade = snapshot.Grade };
        var maxDurability = X2ItemsApi.MaxDurability(template);
        if (maxDurability is not null)
            fields["maxDurability"] = maxDurability;
        // Detail type 1 is equipment. SCUnitState carries its current durability, but a
        // partial equipment update may omit it; use the template maximum in that case.
        // The Lua icon needs a numeric durability whenever maxDurability is present.
        if (snapshot.DetailType == 1)
            fields["durability"] = (double?)snapshot.Durability ?? maxDurability ?? 0d;
        if (snapshot.EnchantScale is { } enchantScale) fields["enchantScale"] = (double)enchantScale;
        if (snapshot.ImageTemplateId is { } imageTemplate) fields["imageTemplateId"] = (double)imageTemplate;
        if (snapshot.DyeColor is { } dyeColor) fields["dyeColor"] = (double)dyeColor;
        if (equipSlot is { } slot) fields["equipSlot"] = (double)slot;
        return new X2InventoryItem
        {
            Id = snapshot.ItemId, ItemType = snapshot.TemplateId, Grade = snapshot.Grade,
            Count = snapshot.Count, Name = definition?.Name ?? $"Item {snapshot.TemplateId}",
            Icon = definition?.Icon ?? "", Fields = fields,
        };
    }
}
