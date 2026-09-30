#nullable enable

using AAEmu.GodotViewer.Data;
using AAEmu.GodotViewer.Net;
using Godot;
using NVector3 = System.Numerics.Vector3;

namespace AAEmu.GodotViewer.Client;

public partial class OnlineSession
{
    private sealed record CastPlayback(uint SkillId, uint CasterUnitId, bool LoopActive);

    private void ApplyCombatVisualEvent(GameEvent ev)
    {
        switch (ev)
        {
            case TargetChangedEvent changed when changed.UnitId == Entered.UnitId:
                _pendingServerTargetId = changed.TargetUnitId;
                ApplyPendingServerTarget();
                break;
            case UnitDeathEvent death:
                ClearCasterSkillState(death.UnitId);
                if (_autoAttackTargetId == death.UnitId)
                    ClearAutoAttackIntent();
                CharacterFor(death.UnitId)?.PlayDeath("all_re_combat_dead_start");
                break;
            case CharacterResurrectedEvent revived:
                if (revived.UnitId == Entered.UnitId)
                    Player.Teleport(revived.Position, revived.Yaw);
                else if (_units.TryGetValue(revived.UnitId, out var unit))
                {
                    unit.Target = revived.Position;
                    unit.TargetYaw = revived.Yaw;
                }
                CharacterFor(revived.UnitId)?.StopAction();
                ApplyCombatVisualState(revived.UnitId);
                break;
            case CombatEngagedEvent engaged:
                ApplyCombatVisualState(engaged.UnitId);
                ApplyCombatVisualState(engaged.OtherUnitId);
                break;
            case CombatClearedEvent cleared:
                ApplyCombatVisualState(cleared.UnitId);
                break;
            case BuffCreatedEvent created:
                StartBuffEffects(created.UnitId, created.BuffIndex, created.BuffId, created.Caster.UnitId,
                    replaceExisting: true);
                break;
            case BuffRemovedEvent removed:
                RemoveBuffEffect(removed.UnitId, removed.BuffIndex);
                break;
            case SkillStartedEvent started:
                var castLoopActive = PlayCastStart(started);
                _combatCasts[started.TimelineId] = new CastPlayback(started.SkillId, started.Caster.UnitId,
                    castLoopActive);
                if (started.Caster.UnitId == Entered.UnitId && started.SkillId is 2 or 3 or 4)
                {
                    if (_autoAttackCancelPending && _autoAttackSkillId == started.SkillId)
                    {
                        if (Client != null && Client.Stage == ClientStage.InWorld)
                            Client.SendGame(CombatPacketWriters.StopAutoAttack(started.TimelineId, 0, Entered.UnitId));
                        ClearAutoAttackIntent();
                    }
                    else if (_autoAttackTargetId != 0 && _autoAttackSkillId == started.SkillId)
                        _autoAttackTimelineId = started.TimelineId;
                }
                PlaySkillEffects(started.SkillId, started.Caster.UnitId, started.Target, [0, 1]);
                break;
            case SkillFiredEvent fired:
                _combatCasts.TryGetValue(fired.TimelineId, out var previousCast);
                var resolvedFireLoop = PlayFireAnimation(fired);
                _combatCasts[fired.TimelineId] = new CastPlayback(fired.SkillId, fired.Caster.UnitId,
                    resolvedFireLoop ?? previousCast?.LoopActive ?? false);
                PlaySkillEffects(fired.SkillId, fired.Caster.UnitId, fired.Target, [2, 3, 4]);
                break;
            case SkillEndedEvent ended:
                if (_combatCasts.Remove(ended.TimelineId, out var endedCast))
                {
                    if (endedCast.LoopActive)
                        EndCastAnimation(endedCast);
                    PlaySkillEndEffects(endedCast);
                }
                if (_autoAttackTimelineId == ended.TimelineId)
                    _autoAttackTimelineId = 0;
                break;
            case SkillStoppedEvent stopped:
                foreach (var pair in _combatCasts.Where(pair => pair.Value.CasterUnitId == stopped.UnitId &&
                             (stopped.SkillId == 0 || pair.Value.SkillId == stopped.SkillId)).ToArray())
                    _combatCasts.Remove(pair.Key);
                StopInterruptedSkillAnimation(stopped.UnitId);
                if (stopped.UnitId == Entered.UnitId && (stopped.SkillId == 0 || stopped.SkillId is 2 or 3 or 4))
                    ClearAutoAttackIntent();
                break;
            case ForceAttackChangedEvent forceAttack when forceAttack.UnitId == Entered.UnitId && !forceAttack.Enabled:
                ClearAutoAttackIntent();
                break;
            case DamageEvent damage:
                ShowDamage(damage);
                break;
            case HealEvent heal:
                ShowHeal(heal);
                break;
            case EnvironmentDamageEvent environment:
                ShowEnvironmentDamage(environment);
                break;
        }
    }

    private CharacterNode? CharacterFor(uint unitId)
    {
        if (unitId == Entered.UnitId)
            return _playerCharacter;
        return _units.TryGetValue(unitId, out var unit) ? unit.Character : null;
    }

    private void ApplyCombatVisualState(uint unitId)
    {
        var character = CharacterFor(unitId);
        if (character == null || !CombatState.TryGetUnit(unitId, out var state))
            return;
        if (state!.IsDead)
            character.PlayDeath("all_re_combat_dead_start");
        else
            character.SetCombatIdle(state.IsInCombat ? "fist_ba_combat_idle" : "");
        foreach (var buff in state.Buffs.Values)
            StartBuffEffects(unitId, buff.Index, buff.BuffId, buff.CasterUnitId);
    }

    private bool PlayCastStart(SkillStartedEvent started)
    {
        var skill = _combatData?.GetSkill(started.SkillId);
        var animation = skill?.StartAnimationId is { } animId ? _combatData?.GetAnimation(animId) : null;
        if (animation == null || CharacterFor(started.Caster.UnitId) is not { } character)
            return false;
        if (animation.Loop)
        {
            character.PlayCastLoop(animation.Name, animation.Name);
            return true;
        }
        else
            character.PlayAction(animation.Name);
        return false;
    }

    /// <returns>Null when no fire clip was resolved; otherwise whether the resolved clip loops.</returns>
    private bool? PlayFireAnimation(SkillFiredEvent fired)
    {
        var animation = fired.FireAnimationId != 0 ? _combatData?.GetAnimation(checked((int)fired.FireAnimationId)) : null;
        var skill = _combatData?.GetSkill(fired.SkillId);
        animation ??= skill?.FireAnimationId is { } animId ? _combatData?.GetAnimation(animId) : null;
        var character = CharacterFor(fired.Caster.UnitId);
        if (animation == null || character == null)
            return null;
        character.PlayAction(animation.Name, loop: animation.Loop);
        return animation.Loop;
    }

    private void StopInterruptedSkillAnimation(uint casterUnitId)
    {
        if (CharacterFor(casterUnitId) is not { } character ||
            CombatState.TryGetUnit(casterUnitId, out var state) && state!.IsDead)
            return;
        character.StopAction();
    }

    private void EndCastAnimation(CastPlayback cast)
    {
        var character = CharacterFor(cast.CasterUnitId);
        var endAnimation = _combatData?.GetSkill(cast.SkillId)?.ChannelingAnimationId is { } animationId
            ? _combatData?.GetAnimation(animationId)
            : null;
        if (character == null || CombatState.TryGetUnit(cast.CasterUnitId, out var state) && state!.IsDead)
            return;
        if (endAnimation != null)
            character.EndCastLoop(endAnimation.Name);
        else
            character.StopAction();
    }

    private void PlaySkillEffects(uint skillId, uint casterUnitId, SkillTarget target, int[] eventIds)
    {
        var groupId = _combatData?.GetSkill(skillId)?.FxGroupId;
        if (groupId is not > 0 || _combatData == null)
            return;
        foreach (var item in _combatData.GetFxGroupItems(groupId.Value))
        {
            if (!eventIds.Contains(item.StartEventId))
                continue;
            PlayFxItem(item, casterUnitId, target.UnitId, target.Position, loop: false,
                projectile: item.StartEventId is 2 or 3 && item.AssetName.Contains("proj", StringComparison.OrdinalIgnoreCase));
        }
    }

    private void PlaySkillEndEffects(CastPlayback cast)
    {
        var groupId = _combatData?.GetSkill(cast.SkillId)?.FxGroupId;
        if (groupId is not > 0 || _combatData == null)
            return;
        foreach (var item in _combatData.GetFxGroupItems(groupId.Value).Where(item => item.StartEventId == 5))
            PlayFxItem(item, cast.CasterUnitId, 0, NVector3.Zero, loop: false, projectile: false);
    }

    private void StartBuffEffects(uint ownerId, uint buffIndex, uint buffId, uint casterId,
        bool replaceExisting = false)
    {
        var key = (ownerId, buffIndex);
        if (_buffEffectNodes.TryGetValue(key, out var existing))
        {
            if (!replaceExisting && existing.All(GodotObject.IsInstanceValid))
                return;
            RemoveBuffEffect(ownerId, buffIndex);
        }
        var groupId = _combatData?.GetBuff(buffId)?.FxGroupId;
        if (groupId is not > 0)
            return;
        var nodes = new List<Node3D>();
        foreach (var item in _combatData!.GetFxGroupItems(groupId!.Value).Where(item => item.StartEventId is 0 or 1))
            if (PlayFxItem(item, casterId, ownerId, NVector3.Zero, loop: true, projectile: false) is { } node)
                nodes.Add(node);
        if (nodes.Count > 0)
            _buffEffectNodes[key] = nodes;
    }

    private void ReattachBuffEffects(uint unitId)
    {
        if (!CombatState.TryGetUnit(unitId, out var state))
            return;
        foreach (var buff in state!.Buffs.Values)
            StartBuffEffects(unitId, buff.Index, buff.BuffId, buff.CasterUnitId, replaceExisting: true);
    }

    private Node3D? PlayFxItem(FxGroupItemData item, uint casterId, uint targetId,
        NVector3 targetPosition, bool loop, bool projectile)
    {
        var attachId = item.LocationId switch
        {
            2 => targetId,
            1 => casterId,
            _ => casterId,
        };
        var anchor = UnitRegistry?.TryGet(attachId, out var target) == true ? target!.Node : null;
        var offset = CryAxes.Point(item.OffsetX, item.OffsetY, item.OffsetZ);

        // FxSound rows live in the same FX groups as particle rows. Attach them to the same unit/bone;
        // positional-only effects use a short-lived anchor at the event position.
        var audio = AudioDirector.Shared;
        if (audio?.Content?.TryResolveSkillFx((uint)item.Id, out _) == true)
        {
            Node3D? soundAnchor = anchor == null ? null : FindFxBone(anchor, item.Bone) ?? anchor;
            var temporaryAnchor = false;
            if (soundAnchor == null && targetPosition != NVector3.Zero)
            {
                soundAnchor = new Node3D { Name = "FxSoundAnchor" };
                AddChild(soundAnchor);
                soundAnchor.GlobalPosition = World.ToGodot(targetPosition.X, targetPosition.Y, targetPosition.Z);
                temporaryAnchor = true;
            }
            var player = soundAnchor == null ? null : audio.PlaySkillFx(item.Id, soundAnchor);
            if (temporaryAnchor)
            {
                if (player != null) player.Finished += soundAnchor!.QueueFree;
                else soundAnchor!.QueueFree();
            }
            return null;
        }

        if (string.IsNullOrWhiteSpace(item.AssetName))
            return null;
        if (_effectPlayer == null || !EnsureParticleLibrary(item.AssetName) || !_effectPlayer.Effects.ContainsKey(item.AssetName))
            return null;

        Node3D? playback;
        if (projectile)
        {
            var start = anchor?.GlobalPosition ?? World.ToGodot(targetPosition.X, targetPosition.Y, targetPosition.Z);
            var destination = targetId != 0 && UnitRegistry?.TryGet(targetId, out var receiver) == true
                ? receiver!.Node.GlobalPosition
                : World.ToGodot(targetPosition.X, targetPosition.Y, targetPosition.Z);
            playback = _effectPlayer.PlayAt(item.AssetName, start + offset);
            var duration = Mathf.Clamp(start.DistanceTo(destination) / 22f, 0.12f, 0.8f);
            CreateTween().TweenProperty(playback, "global_position", destination, duration);
        }
        else if (anchor != null)
            playback = _effectPlayer.Play(item.AssetName, anchor, EmptyToNull(item.Bone), offset, loop);
        else if (targetPosition != NVector3.Zero)
            playback = _effectPlayer.PlayAt(item.AssetName, World.ToGodot(targetPosition.X, targetPosition.Y, targetPosition.Z));
        else
            return null;

        audio?.PlayParticleFx(item.AssetName, playback);
        return playback;
    }

    private static Node3D? FindFxBone(Node3D anchor, string bone)
    {
        if (string.IsNullOrWhiteSpace(bone) || bone.Equals("none", StringComparison.OrdinalIgnoreCase)) return null;
        return anchor.FindChild(bone, recursive: true, owned: false) as Node3D;
    }

    private bool EnsureParticleLibrary(string assetName)
    {
        if (_effectPlayer == null)
            return false;
        var separator = assetName.IndexOf('.');
        var libraryName = (separator < 0 ? assetName : assetName[..separator]).ToLowerInvariant();
        var path = $"game/libs/particles/{libraryName}.xml";
        if (_particleLibrariesAttempted.Add(path) && !_effectPlayer.LoadLibrary(path))
            GD.PrintErr($"FX particle library not found for '{assetName}': {path}");
        return _effectPlayer.Effects.ContainsKey(assetName);
    }

    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) || value.Equals("none", StringComparison.OrdinalIgnoreCase)
        ? null : value;

    private void RemoveBuffEffect(uint unitId, uint buffIndex)
    {
        if (!_buffEffectNodes.Remove((unitId, buffIndex), out var nodes))
            return;
        foreach (var node in nodes)
            if (GodotObject.IsInstanceValid(node)) node.QueueFree();
    }

    private void RemoveBuffEffects(uint unitId)
    {
        foreach (var key in _buffEffectNodes.Keys.Where(key => key.UnitId == unitId).ToArray())
            RemoveBuffEffect(key.UnitId, key.BuffIndex);
    }

    private void ShowDamage(DamageEvent damage)
    {
        var miss = damage.HitType switch
        {
            CombatHitType.MeleeMiss or CombatHitType.RangedMiss or CombatHitType.SpellMiss => "MISS",
            CombatHitType.MeleeDodge or CombatHitType.RangedDodge => "DODGE",
            CombatHitType.MeleeBlock or CombatHitType.RangedBlock => "BLOCK",
            CombatHitType.MeleeParry or CombatHitType.RangedParry => "PARRY",
            CombatHitType.Immune => "IMMUNE",
            CombatHitType.SpellResist => "RESIST",
            _ => null,
        };
        var text = miss ?? (damage.Amount == 0 ? "" : $"-{damage.Amount:N0}");
        if (text.Length == 0)
            return;
        var incoming = damage.TargetUnitId == Entered.UnitId;
        var outgoing = damage.CasterUnitId == Entered.UnitId;
        var color = miss != null ? new Color(0.86f, 0.88f, 0.94f)
            : incoming ? new Color(1f, 0.38f, 0.34f)
            : outgoing ? new Color(1f, 0.82f, 0.48f)
            : new Color(0.94f, 0.84f, 0.76f);
        ShowCombatText(damage.TargetUnitId, text, color);
    }

    private void ShowHeal(HealEvent heal)
    {
        if (heal.Amount == 0)
            return;
        ShowCombatText(heal.TargetUnitId, $"+{Math.Abs(heal.Amount):N0}", new Color(0.4f, 1f, 0.58f));
    }

    private void ShowEnvironmentDamage(EnvironmentDamageEvent environment)
    {
        if (environment.Amount > 0)
            ShowCombatText(environment.TargetUnitId, $"-{environment.Amount:N0}", new Color(1f, 0.38f, 0.34f));
    }

    private void ShowCombatText(uint unitId, string text, Color color)
    {
        if (UnitRegistry?.TryGet(unitId, out var target) != true || target == null)
            return;
        _floatingText?.Show(UnitRegistry, unitId, target.Height, text, color);
    }
}
