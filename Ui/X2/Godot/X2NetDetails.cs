using AAEmu.GodotViewer.Client;
using AAEmu.GodotViewer.Net;
using AAEmu.GodotViewer.Ui.X2.Scripting;

namespace AAEmu.GodotViewer.Ui.X2;

/// <summary>Abilities and money from the raw lobby record (Character.WriteLobby), which LobbyCharacter keeps as Raw.</summary>
internal static class X2NetDetails
{
    public static X2CharacterDetails Read(NetLoginBackend backend, ulong characterId, SqliteUiTextSource data)
    {
        var c = backend.Characters.FirstOrDefault(x => x.Id == characterId);
        if (c == null) return null;
        var faction = c.FactionName is { Length: > 0 } f ? f : data?.FactionName(c.FactionId) ?? "";
        if (c.Raw.Length == 0)
            return new X2CharacterDetails([], 0, faction, c.FactionId, data?.ZoneName(c.ZoneId) ?? "");
        var r = new WireReader(c.Raw);
        r.S64(); r.Str(); r.U8(); r.U8(); r.U8(); // id, name, race, gender, level
        r.S64(); // heir exp
        r.U32(); r.U32(); r.U32(); r.U32(); // hp, mp, zone, faction
        r.Str(); r.U32(); r.U32(); // faction name, expedition, family
        PacketParsers.ReadEquipment(r, UnitKind.Character);
        int[] abilities = [r.U8(), r.U8(), r.U8()];
        r.S64(); r.S64(); r.F32(); // x, y, z
        PacketParsers.ReadAppearance(r);
        r.S16(); r.S64(); r.U32(); r.U32(); r.S64(); r.U32(); r.S64(); // death / rez / leave time
        var money = r.S64();
        int labor = 0, localLabor = 0;
        long bmPoint = 0;
        try
        {
            r.S64(); // aa point
            r.S16(); r.S32(); r.S16(); // crime point / record / score
            r.S64(); r.S64(); r.S64(); r.S64(); // delete/transfer requested, created, delete delay
            r.S64(); r.S64(); r.U8(); // bank money, bank aa point, auto-use aa point
            r.U32(); r.U32(); r.U32(); r.S64(); r.U8(); // prev point, point, gift, updated, force name change
            r.Blob(); // guid
            labor = (int)r.U32();
            localLabor = (int)r.U32();
            r.U32(); r.S64(); // consumed, lp updated
            bmPoint = r.S64();
        }
        catch (WireException)
        {
            // older record layout: labour and BM points stay 0
        }
        return new X2CharacterDetails(abilities, money, faction, c.FactionId, data?.ZoneName(c.ZoneId) ?? "", labor, bmPoint, localLabor);
    }
}
