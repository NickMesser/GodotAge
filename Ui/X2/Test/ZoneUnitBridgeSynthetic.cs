#nullable enable
using System.Numerics;
using System.Reflection;
using AAEmu.GodotViewer.Net;
using AAEmu.GodotViewer.Ui.X2.Online;
using AAEmu.GodotViewer.Ui.X2.Scripting.World;

namespace AAEmu.GodotViewer.Ui.X2.Test;

/// <summary>Offline handoff regression; call Run from a console runner without connecting to a server.</summary>
public static class ZoneUnitBridgeSynthetic
{
    private sealed class Events : IX2Events
    {
        public readonly List<(string Name, object?[] Args)> Fired = [];
        public void Fire(string eventName, params object?[] args) => Fired.Add((eventName, args));
    }

    public static void Run()
    {
        var player = new UnitSnapshot { UnitId = 1, Name = "Player", Kind = UnitKind.Character };
        var a = new UnitSnapshot { UnitId = 2, Name = "Old zone", Kind = UnitKind.Npc };
        var b = new UnitSnapshot { UnitId = 3, Name = "Auctioneer", Kind = UnitKind.Npc, TemplateId = 10857 };
        var entered = new EnteredWorldEvent(1, 1, "Player", Vector3.Zero, 0, 179, 0, 1, player);
        using var client = new GameClient(new GameClientOptions { UserName = "offline", UserToken = "offline" });
        var events = new Events();
        using var bridge = new X2WorldBridge(client, entered, events, _ => { });
        var scene = new List<(UnitSnapshot Snapshot, Vector3 Position)> { (a, Vector3.Zero) };

        // Event-only startup, then the session attaches after A was already received.
        Send(bridge, new UnitAppearedEvent(a));
        bridge.Pump();
        Expect(bridge, 1, 2);
        bridge.SessionUnitsProvider = () => scene;
        bridge.Pump();
        Expect(bridge, 1, 2);

        // The combat reducer may retain an old unit or have a state before its scene model exists.
        var combat = new CombatState(1);
        combat.Apply(new UnitAppearedEvent(a));
        combat.Apply(new UnitAppearedEvent(new UnitSnapshot { UnitId = 9, Name = "Ghost", Kind = UnitKind.Npc }));
        bridge.CombatStateProvider = () => combat;
        if (bridge.Get(9) != null) throw new InvalidOperationException("Combat-only ghost leaked into the bridge");

        Send(bridge, new TeleportedEvent(0, new Vector3(100, 200, 0)));
        var moved = new Vector3(14369, 15492, 153);
        scene = [(a, Vector3.Zero), (b, moved)];
        Send(bridge, new UnitAppearedEvent(b));
        Send(bridge, new UnitMovedEvent(new UnitMovement { UnitId = b.UnitId, Position = moved }));
        bridge.Pump();
        Expect(bridge, 1, 2, 3);
        if (bridge.Get(3)?.Name != "Auctioneer") throw new InvalidOperationException("New zone unit not resolved");
        if (bridge.Get(3) is not { X: 14369, Y: 15492, Z: 153 })
            throw new InvalidOperationException("New zone unit lost its same-batch movement");

        bridge.SetTarget(2);
        bridge.SetTargetOfTarget(2);
        scene = [(b, moved)];
        Send(bridge, new UnitsRemovedEvent([2], false));
        bridge.Pump();
        Expect(bridge, 1, 3);
        if (bridge.Get(2) != null || bridge.TargetId != 0 || bridge.TargetOfTargetId != 0)
            throw new InvalidOperationException("Removed unit or target survived handoff");
        if (!events.Fired.Any(e => e.Name == "UNIT_LEAVED_SIGHT" && Equals(e.Args[0], 2d)))
            throw new InvalidOperationException("Removed unit did not notify nameplates");

        // A reused object id must adopt the new snapshot rather than retain the prior template.
        var replacement = new UnitSnapshot { UnitId = 3, Name = "Replacement", Kind = UnitKind.Character };
        scene = [(replacement, moved)];
        bridge.Pump();
        if (bridge.Get(3) is not { Name: "Replacement", Type: "character" })
            throw new InvalidOperationException("Reused id retained old identity");

        // Recreating the UI bridge after all packets arrived must recover current scene units.
        using var late = new X2WorldBridge(client, entered, new Events(), _ => { });
        late.SessionUnitsProvider = () => scene;
        late.Pump();
        Expect(late, 1, 3);
    }

    private static void Send(X2WorldBridge bridge, GameEvent e) =>
        typeof(X2WorldBridge).GetMethod("OnNetworkEvent", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(bridge, [e]);

    private static void Expect(X2WorldBridge bridge, params uint[] ids)
    {
        var actual = bridge.All.Select(unit => unit.Id).Order().ToArray();
        if (!actual.SequenceEqual(ids.Order()))
            throw new InvalidOperationException($"Expected [{string.Join(",", ids)}], got [{string.Join(",", actual)}]");
    }
}
