#nullable enable

using AAEmu.GodotViewer.Net;

namespace AAEmu.GodotViewer.Client;

public sealed record DoodadInteractionRequestedEvent(
    uint ObjectId, uint TemplateId, uint PhaseId, DoodadUiInteractionKind UiKind);

public partial class OnlineSession
{
    // anims.id=149 is named fist_ac_talk_01 in the content database.  The player CAL has no _01 alias:
    // nu_m_base.cal includes pc/nu_m_animations.cal, whose neutral-talk CAF aliases start at fist_ac_talk_11.
    private const int PlayerTalkAnimationId = 149;
    private const string PlayerTalkAnimationAlias = "fist_ac_talk_11";

    /// <summary>The corpse whose loot bag was last opened (the loot window's Take All / single loot target it).</summary>
    public uint LootBagOwnerId { get; private set; }

    /// <summary>Raised after a live doodad click resolves its current authored client UI action.</summary>
    public event Action<DoodadInteractionRequestedEvent>? DoodadInteractionRequested;

    /// <summary>Interacts with a unit or doodad by object id as a right click on it would (scenario driver / UI use).</summary>
    public bool InteractWith(uint objectId) =>
        UnitRegistry?.TryGet(objectId, out var target) == true && target != null && BeginWorldInteraction(target, pickedWithRightMouse: true);

    private void OnTargetRightClicked(ITargetable target) =>
        BeginWorldInteraction(target, pickedWithRightMouse: true);

    private bool BeginWorldInteraction(ITargetable target, bool pickedWithRightMouse) => target.Kind switch
    {
        TargetKind.Npc => BeginNpcInteraction(target, pickedWithRightMouse),
        TargetKind.Doodad => BeginDoodadInteraction(target),
        TargetKind.Vehicle => BeginVehicleInteraction(target),
        // Houses: the real client sent CSStartInteraction for a right click and nothing for F (captured 2026-09-30).
        TargetKind.Other when HouseTimelineOf(target.Id) is not null => pickedWithRightMouse && BeginHouseInteraction(target),
        _ => false,
    };

    private bool BeginDoodadInteraction(ITargetable target)
    {
        if (Client.Stage != ClientStage.InWorld ||
            !_units.TryGetValue(target.Id, out var live) || live.Snapshot.Kind != UnitKind.Doodad)
            return false;

        // A house's DoodadFuncParentInfo nameplate (func skill 15212 "Check Construction Info") opens the house
        // window in the client itself (x2game FUN_3987b670 -> FUN_398861b0).
        if (Doodads.HasParentInfoFunc(live.Snapshot.TemplateId, live.Snapshot.PhaseId) &&
            OpenHouseFromNameplate(target.Id, live.Snapshot.ParentUnitId))
        {
            FaceTarget(target);
            return true;
        }

        var interaction = Doodads.ResolveInteraction(live.Snapshot.TemplateId, live.Snapshot.PhaseId);
        if (interaction.UiKind == DoodadUiInteractionKind.None && interaction.SkillId == 0)
            return false;

        if (interaction.SkillId != 0)
            Client.SendGame(CombatPacketWriters.StartSkillOnDoodad(
                interaction.SkillId, Entered.UnitId, target.Id));

        FaceTarget(target);
        DoodadInteractionRequested?.Invoke(new DoodadInteractionRequestedEvent(
            target.Id, live.Snapshot.TemplateId, live.Snapshot.PhaseId, interaction.UiKind));
        return true;
    }

    private bool BeginNpcInteraction(ITargetable target, bool pickedWithRightMouse)
    {
        if (Client.Stage != ClientStage.InWorld || target.Kind != TargetKind.Npc)
            return false;
        if (target.Dead)
        {
            // A lootable corpse: right-click or F opens its loot bag (CSLootOpenBag, lootAll=false); the server answers
            // with the bag contents (SCLootBagData) that the loot window shows, or a loot failure.
            if (target is not Targeting.ITargetableHoverInfo { CanLoot: true })
                return false;
            var loot = ItemClientWriters.OpenLoot(target.Id, 0, lootAll: false);
            LootBagOwnerId = target.Id;
            Client.SendGame(loot.Opcode, loot.Body);
            FaceTarget(target);
            return true;
        }

        if (pickedWithRightMouse)
        {
            // The real client's right click on a unit sent CSStartInteraction(unit, 0, extraInfo 1, pickId -1,
            // mouse 2, modifiers 0) (captured 2026-09-30 on a house; the same packet serves NPCs).
            Client.SendGame(CombatPacketWriters.StartNpcInteraction(
                target.Id, objectId: 0, extraInfo: 1, pickId: -1, mouseButton: 2, modifierKeys: 0));
        }
        else
        {
            // The original F/do_interaction_1 path sends only CSInteractNPC. The target is already
            // selected by the time this action runs, so the packet's isTargetChanged value is false.
            Client.SendGame(CombatPacketWriters.InteractNpc(target.Id, targetChanged: false));
        }

        FaceTarget(target);
        var talk = _combatData?.GetAnimation(PlayerTalkAnimationId);
        if (talk != null)
            CharacterFor(Entered.UnitId)?.PlayAction(
                string.Equals(talk.Name, "fist_ac_talk_01", StringComparison.OrdinalIgnoreCase)
                    ? PlayerTalkAnimationAlias
                    : talk.Name,
                loop: talk.Loop);
        return true;
    }

    private void FaceTarget(ITargetable target)
    {
        var targetPosition = World.ToCry(target.Node.GlobalPosition);
        var dx = targetPosition.X - Player.CryPosition.X;
        var dy = targetPosition.Y - Player.CryPosition.Y;
        if (dx * dx + dy * dy > 0.0001f)
            Player.Heading = WorldCoords.YawFromDirection(dx, dy);
    }
}
