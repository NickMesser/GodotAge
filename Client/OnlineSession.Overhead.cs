#nullable enable

using AAEmu.GodotViewer.Net;
using AAEmu.GodotViewer.Ui.X2;
using Godot;

namespace AAEmu.GodotViewer.Client;

public partial class OnlineSession
{
    /// <summary>Scene node for configurable quest markers, speech bubbles, and resolved emotions.</summary>
    public OverheadPresentation? Overhead { get; private set; }
    private QuestMarkerCatalog? _questMarkers;
    private QuestChatBubbleCatalog? _questSpeech;
    private EmoteCatalog? _emotes;
    private readonly HashSet<(long QuestInstanceId, uint ComponentId)> _shownQuestSpeech = [];

    private void InitializeOverhead()
    {
        InitializeOverheadContent();
        Overhead = new OverheadPresentation
        {
            Name = "OverheadPresentation",
            TranslateText = UiTranslator.Shared.Translate,
            QuestMarkerResolver = ResolveQuestMarker,
            QuestMeshResolver = ResolveQuestMarkerMesh,
            EmotionResolver = ResolveEmotion,
            PlayAction = (unitId, action) => CharacterFor(unitId)?.PlayAction(action),
            WriteChat = text =>
            {
                EmoteTextGenerated?.Invoke(text);
                GD.Print($"[emote] {text}");
            },
        };
        AddChild(Overhead);
        ChatReceived += ShowOverheadChat;
        NpcDialogue += ShowOverheadNpcDialogue;
        QuestUpdated += ShowOverheadQuestSpeech;
        EmotionExpressed += emotion => Overhead?.ShowEmotion(
            emotion.SourceUnitId, emotion.TargetUnitId, emotion.EmotionId);
        RefreshOverheadUnits();
    }

    private void InitializeOverheadContent()
    {
        try
        {
            if (!File.Exists(GameDatabasePath)) return;
            _questMarkers = new QuestMarkerCatalog(GameDatabasePath);
            _questSpeech = new QuestChatBubbleCatalog(GameDatabasePath);
            _emotes = new EmoteCatalog(GameDatabasePath);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"Could not load overhead quest/emote content: {exception.Message}");
        }
    }

    private OverheadQuestMarker ResolveQuestMarker(OverheadUnit unit)
    {
        if (_questMarkers is null) return OverheadQuestMarker.None;
        var level = CombatState.TryGetUnit(Entered.UnitId, out var player) ? Math.Max(0, (int)player!.Level) : 0;
        return _questMarkers.Resolve(unit.TemplateId, level, QuestState);
    }

    private Mesh? ResolveQuestMarkerMesh(OverheadQuestMarker marker)
    {
        if (Models is null || OverheadPresentation.NativeQuestModel(marker.State) is not { } asset) return null;
        var reference = Models.Request(asset.ModelPath, asset.MaterialPath, mirrored: false);
        return Models.GetMesh(reference);
    }

    private OverheadEmotion? ResolveEmotion(uint sourceUnitId, uint targetUnitId, uint emotionId)
    {
        if (_emotes?.TryGet(emotionId, out var entry) != true || _combatData is null) return null;
        var isNpc = UnitRegistry?.TryGet(sourceUnitId, out var source) == true && source!.Kind == TargetKind.Npc;
        var animationId = isNpc && entry.NpcAnimationId > 0 ? entry.NpcAnimationId : entry.AnimationId;
        var animation = _combatData.GetAnimation(animationId)?.Name;
        var textColumn = sourceUnitId == Entered.UnitId
            ? targetUnitId == 0 ? "me" : "me_target"
            : targetUnitId == Entered.UnitId ? "other_me" : targetUnitId == 0 ? "other" : "other_target";
        var text = _combatData.GetText("express_texts", textColumn, emotionId);
        if (string.IsNullOrWhiteSpace(text))
            text = _combatData.GetText("express_texts", sourceUnitId == Entered.UnitId ? "me" : "other", emotionId);
        return new OverheadEmotion(animation, text);
    }

    private void ShowOverheadChat(ChatEvent chat)
    {
        var kind = chat.ChatType switch
        {
            SocialChatTypes.Say => OverheadChatKind.Say,
            SocialChatTypes.Shout => OverheadChatKind.Shout,
            _ => (OverheadChatKind?)null,
        };
        if (kind is { } value)
            Overhead?.ShowChat(chat.SenderUnitId, chat.SenderName, chat.Message, value,
                showSpeakerName: chat.SenderUnitId != Entered.UnitId);
    }

    private void ShowOverheadNpcDialogue(NpcDialogueEvent dialogue)
    {
        if (!string.IsNullOrWhiteSpace(dialogue.Message))
            Overhead?.ShowChat(dialogue.NpcUnitId, dialogue.NpcName, dialogue.Message, OverheadChatKind.Npc);
    }

    private void ShowOverheadQuestSpeech(IQuestProtocolEvent value)
    {
        QuestWireRecord? quest;
        uint componentId;
        switch (value)
        {
            case QuestContextStartedEvent started: quest = started.Quest; componentId = started.ComponentId; break;
            case QuestContextUpdatedEvent updated: quest = updated.Quest; componentId = updated.ComponentId; break;
            default: return;
        }
        if (componentId == 0 || _questSpeech?.TryGetStart(componentId, out var row) != true ||
            _shownQuestSpeech.Contains((quest.InstanceId, componentId))) return;
        var npc = _units.Values.FirstOrDefault(u => u.Snapshot.Kind == UnitKind.Npc &&
            u.Snapshot.TemplateId == row.NpcTemplateId);
        if (npc is null) return;

        var text = _combatData?.GetText("quest_chat_bubbles", "speech", row.Id) ?? "";
        if (string.IsNullOrWhiteSpace(text)) return;
        var commandEnd = text.StartsWith('/') ? text.IndexOf(' ') : -1;
        if (commandEnd > 1)
        {
            var emotionCommand = text[..commandEnd];
            if (_emotes?.TryResolveCommand(emotionCommand, out var emotionId) == true &&
                _emotes.TryGet(emotionId, out var emote))
            {
                var animationId = emote.NpcAnimationId > 0 ? emote.NpcAnimationId : emote.AnimationId;
                if (_combatData?.GetAnimation(animationId) is { } animation)
                    npc.Character?.PlayAction(animation.Name, loop: animation.Loop);
            }
            text = text[(commandEnd + 1)..].TrimStart();
        }
        text = ExpandNpcNameMacros(UiTranslator.Shared.Translate(text));
        _shownQuestSpeech.Add((quest.InstanceId, componentId));
        Overhead?.ShowChat(npc.Snapshot.UnitId, npc.Snapshot.Name, text, OverheadChatKind.Npc);
    }

    private string ExpandNpcNameMacros(string text)
    {
        if (_combatData is null) return text;
        return System.Text.RegularExpressions.Regex.Replace(text, "@NPC_NAME\\((\\d+)\\)", match =>
        {
            return uint.TryParse(match.Groups[1].Value, out var id) && _combatData.GetNpc(id) is { } npc
                ? npc.Name : match.Value;
        }, System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }

    private void RefreshOverheadUnits()
    {
        if (Overhead is null || UnitRegistry is null)
            return;
        Overhead.SetUnits(UnitRegistry.All.Select(target => new OverheadUnit(
            target.Id,
            target.Node,
            target.Kind switch
            {
                TargetKind.Player => OverheadUnitKind.Player,
                TargetKind.Npc => OverheadUnitKind.Npc,
                TargetKind.Mate => OverheadUnitKind.Mate,
                TargetKind.Vehicle => OverheadUnitKind.Vehicle,
                _ => OverheadUnitKind.Other,
            },
            _units.TryGetValue(target.Id, out var unit) ? unit.Snapshot.TemplateId
                : target.Id == Entered.UnitId ? Entered.Self?.TemplateId ?? 0 : 0,
            target.Height)));
    }
}
