#nullable enable
using AAEmu.GodotViewer.Data;
using Godot;

namespace AAEmu.GodotViewer.Ui.X2;

// The skillset page's skill preview: after the class cutscene (or straight away for the page's own first pick) the actor performs the
// skillset's three preview skills one after the other, with each skill's particle effects (the skill's fx group, phased by the
// "!skill_effect" animation events of its clips), then rests in the class idle.
public partial class X2UiLayer
{
    private sealed record PreviewSkillPlan(long SkillId, string StartClip, bool StartLoops, string FireClip, float CastSeconds,
        IReadOnlyList<FxGroupItemData> Items);

    private sealed class SkillPreviewRun
    {
        public required CharacterNode Character;
        public int AbilityId;
        public required List<PreviewSkillPlan> Plans;
        public int Index;
        public double NextAt;
        /// <summary>0 waiting for NextAt, 1 start clip, 2 fire clip, 3 effects only.</summary>
        public int Phase;
        public double PhaseEndsAt = double.MaxValue, PhaseGuard;
        public SkillFxRun? Fx;
        public ClipStream? Stream;
        public PreviewSkillPlan? Plan;
    }

    private List<PreviewSkillPlan> BuildPreviewPlans(LoginStageAnimationCatalog.AbilityVisual ability)
    {
        var plans = new List<PreviewSkillPlan>();
        if (_previewGameData == null) return plans;
        foreach (var id in ability.PreviewSkillIds)
        {
            if (id <= 0 || _previewGameData.GetSkill(id) is not { } skill) continue;
            var start = skill.StartAnimationId is { } startId ? _previewGameData.GetAnimation(startId) : null;
            var fire = skill.FireAnimationId is { } fireId ? _previewGameData.GetAnimation(fireId) : null;
            var items = skill.FxGroupId is > 0 ? _previewGameData.GetFxGroupItems(skill.FxGroupId.Value) : [];
            if (start == null && fire == null && items.Count == 0) continue;
            plans.Add(new PreviewSkillPlan(id, start?.Name ?? "", start?.Loop ?? false, fire?.Name ?? "", skill.CastingMilliseconds / 1000f, items));
        }
        return plans;
    }

    /// <summary>Schedules the preview skills of the current skillset to begin <paramref name="delay"/> seconds from now.</summary>
    private void SchedulePreviewSkills(ModelViewScene scene, LoginStageAnimationCatalog.AbilityVisual ability, CharacterNode character, double delay)
    {
        CancelPreviewSkills(scene);
        if (_session.Engine.SkipSkillAction || _loginStage == null) return;
        var plans = BuildPreviewPlans(ability);
        AnimEventDatabase.Prepare(character.AnimationListPath);
        foreach (var plan in plans)
            foreach (var item in plan.Items) _loginStage.PreloadFx(item.AssetName);
        if (System.Environment.GetEnvironmentVariable("X2_TRACE_PREVIEW") == "1")
            Emit($"[x2] preview skills of ability {ability.Id}: {string.Join("; ", plans.Select(p => $"{p.SkillId} start '{p.StartClip}' fire '{p.FireClip}' cast {p.CastSeconds:F1}s fx {p.Items.Count}"))}");
        if (plans.Count == 0) return;
        scene.PreviewRun = new SkillPreviewRun
        {
            Character = character, AbilityId = ability.Id, Plans = plans, NextAt = Time.GetTicksMsec() / 1000.0 + delay,
        };
    }

    private void CancelPreviewSkills(ModelViewScene scene)
    {
        if (scene.PreviewRun is not { } run) return;
        scene.PreviewRun = null;
        var now = Time.GetTicksMsec() / 1000.0;
        _loginStage?.StopStream(run.Stream, now, killEffects: true);
        if (run.Fx != null) _loginStage?.FireSkillPhase(run.Fx, 5, null, now);
        if (run.Phase is 1 or 2 && GodotObject.IsInstanceValid(run.Character)) run.Character.StopAction(0.2f);
    }

    private void TickPreviewSkills(ModelViewScene scene, Scripting.X2Engine engine, CharacterNode character, double now)
    {
        if (scene.PreviewRun is not { } run || _loginStage == null) return;
        if (run.Character != character || engine.Stage != Scripting.X2Engine.StageAbility ||
            engine.LoginAnimationAbility != run.AbilityId || engine.SkipSkillAction)
        {
            CancelPreviewSkills(scene);
            return;
        }
        if (engine.PreviewFrozen) return;
        if (run.Phase == 0)
        {
            if (now < run.NextAt) return;
            if (run.Index >= run.Plans.Count) { scene.PreviewRun = null; scene.PreviewWanted = false; return; }
            BeginPreviewSkill(scene, run, now);
        }
        else if (now >= run.PhaseEndsAt || now >= run.PhaseGuard) AdvancePreviewSkill(scene, run, now);
    }

    private void BeginPreviewSkill(ModelViewScene scene, SkillPreviewRun run, double now)
    {
        var plan = run.Plans[run.Index];
        run.Plan = plan;
        run.Fx = plan.Items.Count > 0 ? new SkillFxRun { Items = plan.Items, Character = run.Character } : null;
        if (run.Fx != null) _loginStage!.FireSkillPhase(run.Fx, 0, null, now);
        if (plan.StartClip.Length > 0) PlayPreviewClip(scene, run, plan.StartClip, plan.StartLoops, 1, now);
        else if (plan.FireClip.Length > 0) PlayPreviewClip(scene, run, plan.FireClip, false, 2, now);
        else
        {
            // Skills without a clip of their own (the client picks those by weapon): only their particle effects play.
            run.Phase = 3;
            run.PhaseGuard = now + 6;
            run.PhaseEndsAt = now + 0.45;
            if (run.Fx != null) _loginStage!.FireSkillPhase(run.Fx, 1, null, now);
        }
    }

    private void PlayPreviewClip(ModelViewScene scene, SkillPreviewRun run, string alias, bool loop, int phase, double now)
    {
        run.Phase = phase;
        run.PhaseEndsAt = double.MaxValue;
        run.PhaseGuard = now + 10;
        var character = run.Character;
        var graph = character.AnimationGraph;
        var before = graph?.ActionSerial ?? 0;
        character.PlayPresentationAction(alias, 0.12f, loop, started: () =>
        {
            if (scene.PreviewRun != run || !GodotObject.IsInstanceValid(character)) return;
            var t = Time.GetTicksMsec() / 1000.0;
            var began = graph != null && graph.ActionSerial != before;
            var length = began ? graph!.LastActionLength : 0.4f;
            _loginStage?.StopStream(run.Stream, t, killEffects: false);
            run.Stream = _loginStage?.StartClipEvents(character, alias, t, length, loop, run.Fx);
            var driven = run.Stream?.Events.Any(e => e.IsSkillEffect) == true;
            if (run.Fx != null && !driven)
            {
                // A clip with no skill-effect events of its own: the cast clip forms the skill, the fire clip throws and launches it.
                if (phase == 1) _loginStage!.FireSkillPhase(run.Fx, 1, null, t);
                else { _loginStage!.FireSkillPhase(run.Fx, 2, null, t); _loginStage.FireSkillPhase(run.Fx, 3, null, t); }
            }
            run.PhaseEndsAt = t + (loop ? Math.Clamp(run.Plan?.CastSeconds ?? 1f, 0.7f, 2.2f) : Math.Max(0.35f, length));
            if (System.Environment.GetEnvironmentVariable("X2_TRACE_PREVIEW") == "1")
                Emit($"[x2] preview skill {run.Plan?.SkillId} clip {alias} {(began ? $"{length:F2} s" : "missing")} phase {phase}, events {run.Stream?.Events.Count ?? 0}");
        });
    }

    private void AdvancePreviewSkill(ModelViewScene scene, SkillPreviewRun run, double now)
    {
        var plan = run.Plan!;
        if (run.Phase == 1 && plan.FireClip.Length > 0)
        {
            PlayPreviewClip(scene, run, plan.FireClip, false, 2, now);
            return;
        }
        if (run.Phase == 3 && run.Fx != null && !run.Fx.Fired.Contains(3))
        {
            _loginStage!.FireSkillPhase(run.Fx, 2, null, now);
            _loginStage.FireSkillPhase(run.Fx, 3, null, now);
            run.PhaseEndsAt = now + 1.4;
            return;
        }
        _loginStage!.StopStream(run.Stream, now, killEffects: false);
        run.Stream = null;
        if (run.Fx != null) _loginStage.FireSkillPhase(run.Fx, 5, null, now);
        if (run.Phase is 1 or 2 && plan.StartLoops && run.Phase == 1) run.Character.StopAction(0.25f);
        run.Fx = null;
        run.Index++;
        run.Phase = 0;
        run.PhaseEndsAt = double.MaxValue;
        run.NextAt = now + 0.8;
    }
}
