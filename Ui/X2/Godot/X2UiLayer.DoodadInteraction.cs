#nullable enable
using AAEmu.GodotViewer.Client;
using AAEmu.GodotViewer.Lua;
using AAEmu.GodotViewer.Ui.X2.Scripting.Api;

namespace AAEmu.GodotViewer.Ui.X2;

public partial class X2UiLayer
{
    private uint _craftDoodadObjectId;
    private uint _craftDoodadTemplateId;
    private const int CraftOrderContentId = 29; // UIC_CRAFT_ORDER in X2ApiData.g.cs
    private const int MailContentId = 79; // UIC_MAIL in X2ApiData.g.cs

    private void OnDoodadInteractionRequested(DoodadInteractionRequestedEvent interaction)
    {
        Emit($"[x2] doodad interaction {interaction.ObjectId} template {interaction.TemplateId} " +
             $"phase {interaction.PhaseId}: {interaction.UiKind}");
        switch (interaction.UiKind)
        {
            case DoodadUiInteractionKind.Mailbox:
                _session.WorldCore.ShowContent(MailContentId, true, [new LuaTable
                {
                    ["doodadId"] = (double)interaction.ObjectId,
                }]);
                // mailbox.alb waits for the first page when opened with a doodad id.
                _protocolSocialData?.RequestMailList(X2SocialMailboxKind.Mail, 1, 1, 14);
                break;
            case DoodadUiInteractionKind.Crafting:
                if (_protocolEconomyData?.BeginCraftInteraction(interaction.ObjectId, interaction.TemplateId, interaction.PhaseId) != true)
                    break;
                _craftDoodadObjectId = interaction.ObjectId;
                _craftDoodadTemplateId = interaction.TemplateId;
                _session.Root?.DispatchEvent("CRAFTING_START", (double)interaction.ObjectId, 1d);
                break;
            case DoodadUiInteractionKind.CraftOrderBoard:
                _session.WorldCore.ShowContent(CraftOrderContentId, true, []);
                break;
        }
    }
}
