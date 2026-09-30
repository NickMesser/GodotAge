#nullable enable

using AAEmu.GodotViewer.Net;

namespace AAEmu.GodotViewer.Client;

public partial class OnlineSession
{
    /// <summary>Raised with English-resolved local emote text so a chat UI can append it to its transcript.</summary>
    public event Action<string>? EmoteTextGenerated;

    /// <summary>
    /// Sends a native express_texts slash alias, such as <c>/bow</c> or <c>/emote bow</c>.
    /// The received SCEmotionExpressed event drives animation and chat presentation.
    /// </summary>
    public bool TrySendEmoteCommand(string command, uint targetUnitId = 0)
    {
        var emotes = _emotes;
        if (Client.Stage != ClientStage.InWorld || emotes is null || !emotes.TryResolveCommand(command, out var emotionId))
            return false;
        Client.SendGame(CombatPacketWriters.ExpressEmotion(Entered.UnitId, targetUnitId, emotionId));
        return true;
    }
}
