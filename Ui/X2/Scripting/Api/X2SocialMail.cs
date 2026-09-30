#nullable enable
using System.Globalization;
using AAEmu.GodotViewer.Lua;
using AAEmu.GodotViewer.Ui.X2.Scripting.World;

namespace AAEmu.GodotViewer.Ui.X2.Scripting.Api;

/// <summary>Identifies the two independent client mail stores.</summary>
public enum X2SocialMailboxKind
{
    Mail,
    GoodsMail,
}

/// <summary>An item table attached to a mail or staged in the compose window.</summary>
public sealed record X2SocialMailItem
{
    public uint TemplateId { get; init; }
    public ulong ItemId { get; init; }
    public string Name { get; init; } = "";
    public string IconPath { get; init; } = "";
    public int Grade { get; init; }
    public int Stack { get; init; } = 1;
    public bool IsStackable { get; init; }

    /// <summary>
    /// Additional client item-info fields supplied by the inventory adapter. This keeps mail independent of the
    /// inventory implementation while allowing item tooltips to receive subtype-specific values.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Fields { get; init; } =
        new Dictionary<string, object?>(StringComparer.Ordinal);
}

/// <summary>A mail body as cached by the client.</summary>
public sealed record X2SocialMailBody
{
    public string MailId { get; init; } = "0";
    public bool IsSent { get; init; }
    public int MailType { get; init; }
    public string Sender { get; init; } = "";
    public string Receiver { get; init; } = "";
    public string Title { get; init; } = "";
    public string Text { get; init; } = "";
    public DateTime SendDate { get; init; }
    public DateTime ReceiveDate { get; init; }
    public long Money { get; init; }
    public long AaPoints { get; init; }
    public long HonorPoints { get; init; }
    public long ChargeMoney { get; init; }
    public int SystemChargePermille { get; init; }
    public long Extra { get; init; }
    public string ZoneGroupName { get; init; } = "";
    public bool IsNation { get; init; }
    public bool IsReturned { get; init; }
    public bool IsUserMail { get; init; }
    public bool IsSystemMail { get; init; }
    public bool IsBillingMail { get; init; }
    public bool IsBalanceReceiptMail { get; init; }
    public bool IsTaxInKindReceiptMail { get; init; }
    public bool IsTaxRateChangedMail { get; init; }
    public bool IsHeroDropoutComebackRequestMail { get; init; }
    public IReadOnlyList<X2SocialMailItem?> Items { get; init; } = [];
}

/// <summary>A row in the received or sent mailbox list. Indices exposed to Lua are one based.</summary>
public sealed record X2SocialMailHeader
{
    public string MailId { get; init; } = "0";
    public int MailType { get; init; }
    public string Title { get; init; } = "";
    public string Sender { get; init; } = "";
    public string Receiver { get; init; } = "";
    public bool IsRead { get; init; }
    /// <summary>True when the header advertises items or any attached currency.</summary>
    public bool HasAttachments { get; init; }
    public int AttachedItemCount { get; init; }
    public bool FailedToLoadItems { get; init; }
    public long Extra { get; init; }
    public string ZoneGroupName { get; init; } = "";
    public bool IsNation { get; init; }
    public bool IsReturned { get; init; }
    public bool IsUserMail { get; init; }
    public bool IsSystemMail { get; init; }
    public bool IsBillingMail { get; init; }
    public bool IsBalanceReceiptMail { get; init; }
    public bool IsTaxInKindReceiptMail { get; init; }
    public bool IsTaxRateChangedMail { get; init; }
    public bool IsHeroDropoutComebackRequestMail { get; init; }
    public bool IsReportedSpam { get; init; }
    /// <summary>Optional body already present in the list cache (used by commercial mail).</summary>
    public X2SocialMailBody? CachedBody { get; init; }
}

/// <summary>Current query state and client-side compose slots for one mailbox.</summary>
public sealed record X2SocialMailboxState
{
    public bool CanSendMail { get; init; }
    public bool IsReading { get; init; }
    public bool IsWriting { get; init; }
    public int UnreadCount { get; init; }
    public int CurrencyForAttachment { get; init; }
    public int CurrencyForFee { get; init; }
    public long AttachmentMoneyLimit { get; init; }
    public long ExpressMailCost { get; init; }
    public long NormalMailCost { get; init; }
    public long ExpressAttachmentCost { get; init; }
    public long NormalAttachmentCost { get; init; }
    public IReadOnlyList<X2SocialMailHeader> Received { get; init; } = [];
    public IReadOnlyList<X2SocialMailHeader> Posted { get; init; } = [];
    /// <summary>Server-advertised totals can exceed the currently cached page.</summary>
    public int ReceivedTotal { get; init; } = -1;
    public int PostedTotal { get; init; } = -1;
    public X2SocialMailBody? Current { get; init; }
    /// <summary>Ten compose slots; null entries are empty.</summary>
    public IReadOnlyList<X2SocialMailItem?> ComposeItems { get; init; } = [];
}

/// <summary>The normalized send-mail request produced from the Lua compose table.</summary>
public sealed record X2SocialOutgoingMail
{
    public string Receiver { get; init; } = "";
    public IReadOnlyList<string> Receivers { get; init; } = [];
    public string Title { get; init; } = "";
    public string Text { get; init; } = "";
    public int Type { get; init; }
    public string Money { get; init; } = "0";
    public uint DoodadId { get; init; }
    public bool WithReceiver { get; init; }
    public bool GroupMail { get; init; }
}

public partial interface IX2SocialData
{
    /// <summary>Returns the latest locally cached state for ordinary or commercial mail.</summary>
    X2SocialMailboxState GetMailbox(X2SocialMailboxKind kind);

    /// <summary>Requests a one-based range of mailbox headers from the server.</summary>
    void RequestMailList(X2SocialMailboxKind kind, int mailListKind, int startIndex, int count);
    void CompleteMailList(X2SocialMailboxKind kind);
    void ReadMail(X2SocialMailboxKind kind, bool sent, string mailId);
    void DeleteMail(X2SocialMailboxKind kind, bool sent, string mailId);
    void ReturnMail(X2SocialMailboxKind kind, string mailId);
    void ReportSpamMail(X2SocialMailboxKind kind, string mailId);
    bool SendMail(X2SocialMailboxKind kind, X2SocialOutgoingMail mail);
    void SetMailReading(X2SocialMailboxKind kind, bool reading);
    void SetMailWriting(X2SocialMailboxKind kind, bool writing);
    void ClearCurrentMail(X2SocialMailboxKind kind);
    void ToggleMailbox(X2SocialMailboxKind kind);
    bool PlaceMailItem(X2SocialMailboxKind kind, int composeIndex);
    bool PlaceMailItemFromBag(X2SocialMailboxKind kind, int composeIndex, int bagId, int slotIndex);
    bool ClearMailItem(X2SocialMailboxKind kind, int composeIndex);
    void TakeCurrentMailItem(X2SocialMailboxKind kind, int itemIndex);
    void TakeAllCurrentMailItems(X2SocialMailboxKind kind);
    void TakeCurrentMailMoney(X2SocialMailboxKind kind);
    void TakeMailAttachmentsSequentially(X2SocialMailboxKind kind, string mailId);
    void PayCurrentMailCharge(X2SocialMailboxKind kind);
    void AcceptComebackRequest(X2SocialMailboxKind kind);

    /// <summary>Resolves the localized sender name for a quest template, or null when it is unknown.</summary>
    string? GetQuestMailSender(long questId);
}

public partial class NullSocialData
{
    private static readonly X2SocialMailboxState EmptyMailbox = new();

    public virtual X2SocialMailboxState GetMailbox(X2SocialMailboxKind kind) => EmptyMailbox;
    public virtual void RequestMailList(X2SocialMailboxKind kind, int mailListKind, int startIndex, int count) { }
    public virtual void CompleteMailList(X2SocialMailboxKind kind) { }
    public virtual void ReadMail(X2SocialMailboxKind kind, bool sent, string mailId) { }
    public virtual void DeleteMail(X2SocialMailboxKind kind, bool sent, string mailId) { }
    public virtual void ReturnMail(X2SocialMailboxKind kind, string mailId) { }
    public virtual void ReportSpamMail(X2SocialMailboxKind kind, string mailId) { }
    public virtual bool SendMail(X2SocialMailboxKind kind, X2SocialOutgoingMail mail) => false;
    public virtual void SetMailReading(X2SocialMailboxKind kind, bool reading) { }
    public virtual void SetMailWriting(X2SocialMailboxKind kind, bool writing) { }
    public virtual void ClearCurrentMail(X2SocialMailboxKind kind) { }
    public virtual void ToggleMailbox(X2SocialMailboxKind kind) { }
    public virtual bool PlaceMailItem(X2SocialMailboxKind kind, int composeIndex) => false;
    public virtual bool PlaceMailItemFromBag(X2SocialMailboxKind kind, int composeIndex, int bagId, int slotIndex) => false;
    public virtual bool ClearMailItem(X2SocialMailboxKind kind, int composeIndex) => false;
    public virtual void TakeCurrentMailItem(X2SocialMailboxKind kind, int itemIndex) { }
    public virtual void TakeAllCurrentMailItems(X2SocialMailboxKind kind) { }
    public virtual void TakeCurrentMailMoney(X2SocialMailboxKind kind) { }
    public virtual void TakeMailAttachmentsSequentially(X2SocialMailboxKind kind, string mailId) { }
    public virtual void PayCurrentMailCharge(X2SocialMailboxKind kind) { }
    public virtual void AcceptComebackRequest(X2SocialMailboxKind kind) { }
    public virtual string? GetQuestMailSender(long questId) => null;
}

public static partial class X2SocialApi
{
    internal static void InstallMail(X2LuaHost host, X2GameContext context, IX2SocialData data)
    {
        InstallSocialMailbox(host, data, "X2Mail", X2SocialMailboxKind.Mail, includeCachedBody: false);
        InstallSocialMailbox(host, data, "X2GoodsMail", X2SocialMailboxKind.GoodsMail, includeCachedBody: true);
    }

    private static void InstallSocialMailbox(
        X2LuaHost host,
        IX2SocialData data,
        string table,
        X2SocialMailboxKind kind,
        bool includeCachedBody)
    {
        X2SocialMailboxState State() => data.GetMailbox(kind);
        X2SocialMailHeader? Header(bool sent, int oneBasedIndex)
        {
            var list = sent ? State().Posted : State().Received;
            return oneBasedIndex > 0 && oneBasedIndex <= list.Count ? list[oneBasedIndex - 1] : null;
        }
        X2SocialMailHeader? HeaderById(bool sent, string id)
            => (sent ? State().Posted : State().Received).FirstOrDefault(x => x is not null && x.MailId == id);
        X2SocialMailItem? ComposeItem(int oneBasedIndex)
        {
            var items = State().ComposeItems;
            return oneBasedIndex > 0 && oneBasedIndex <= items.Count ? items[oneBasedIndex - 1] : null;
        }
        X2SocialMailItem? CurrentItem(int oneBasedIndex)
        {
            var items = State().Current?.Items;
            return items is not null && oneBasedIndex > 0 && oneBasedIndex <= items.Count
                ? items[oneBasedIndex - 1]
                : null;
        }

        host.Define(table, "CanSendMail", _ => State().CanSendMail);
        host.Define(table, "ClearCurrentMail", _ => { data.ClearCurrentMail(kind); return null; });
        host.Define(table, "ClearMailItem", a => data.ClearMailItem(kind, a.Int(0)));
        host.Define(table, "ComebackRequestAccept", _ => { data.AcceptComebackRequest(kind); return null; });
        host.Define(table, "CompleteMailList", _ => { data.CompleteMailList(kind); return null; });
        host.Define(table, "DeleteCurrentMail", _ =>
        {
            if (State().Current is { } current) data.DeleteMail(kind, current.IsSent, current.MailId);
            return null;
        });
        host.Define(table, "DeleteMailById", a =>
        {
            data.DeleteMail(kind, a.Bool(0), a.Str(1) ?? "0");
            return null;
        });
        host.Define(table, "GetAttachedItemCountById", a =>
            (double)(HeaderById(a.Bool(0), a.Str(1) ?? "0")?.AttachedItemCount ?? 0));
        // The scripts treat the limit as a money string (X2Util:StrNumericComp / SetCurrencyAmountLimit); an unbounded limit is the
        // money window's own ceiling (components/money.lua MONEY_LIMIT = "10000000000"), never a float such as 9.22e18.
        host.Define(table, "GetAttachmentMoneyLimit", _ =>
            Math.Clamp(State().AttachmentMoneyLimit, 0, 10_000_000_000L).ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (includeCachedBody)
        {
            host.Define(table, "GetCacheBodyInfo", a =>
                Header(a.Bool(0), a.Int(1))?.CachedBody is { } body ? SocialMailBodyTable(body) : null);
        }
        host.Define(table, "GetCountUnreadMail", _ => (double)State().UnreadCount);
        host.Define(table, "GetCurrencyForAttachment", _ => (double)State().CurrencyForAttachment);
        host.Define(table, "GetCurrencyForFee", _ => (double)State().CurrencyForFee);
        host.Define(table, "GetCurrentMailAAPointStr", _ => SocialMailNumberString(State().Current?.AaPoints ?? 0));
        host.Define(table, "GetCurrentMailAttachedItemCount", _ =>
            (double)(State().Current?.Items.Count(x => x is not null) ?? 0));
        host.Define(table, "GetCurrentMailBody", _ =>
            State().Current is { } body ? SocialMailBodyTable(body) : null);
        host.Define(table, "GetCurrentMailChargeMoneyStr", _ => SocialMailNumberString(State().Current?.ChargeMoney ?? 0));
        host.Define(table, "GetCurrentMailHonorPointStr", _ => SocialMailNumberString(State().Current?.HonorPoints ?? 0));
        host.Define(table, "GetCurrentMailItem", a =>
            CurrentItem(a.Int(0)) is { } item ? SocialMailItemTable(item) : null);
        host.Define(table, "GetCurrentMailMoneyStr", _ => SocialMailNumberString(State().Current?.Money ?? 0));
        host.Define(table, "GetCurrentMailType", _ =>
            State().Current is { } current ? (double)current.MailType : null);
        host.Define(table, "GetExpressMailAttachmentCost", _ => (double)State().ExpressAttachmentCost);
        host.Define(table, "GetExpressMailCost", a =>
            (double)(State().ExpressMailCost +
                Math.Max(0, State().ComposeItems.Count(x => x is not null) + (a.Bool(0) ? 1 : 0) - 1)
                * State().ExpressAttachmentCost));
        host.Define(table, "GetMailEmptyItemIndex", _ =>
        {
            var items = State().ComposeItems;
            for (var i = 0; i < 10; i++)
                if (i >= items.Count || items[i] is null) return (double)(i + 1);
            return null;
        });
        host.Define(table, "GetMailItem", a =>
            ComposeItem(a.Int(0)) is { } item ? SocialMailItemTable(item) : null);
        host.Define(table, "GetNormalMailAttachmentCost", _ => (double)State().NormalAttachmentCost);
        host.Define(table, "GetNormalMailCost", a =>
            (double)(State().NormalMailCost +
                Math.Max(0, State().ComposeItems.Count(x => x is not null) + (a.Bool(0) ? 1 : 0) - 1)
                * State().NormalAttachmentCost));
        host.Define(table, "GetPostedMailCount", _ =>
            (double)(State().PostedTotal >= 0 ? State().PostedTotal : State().Posted.Count));
        host.Define(table, "GetPostedMailTitleInfo", a =>
            Header(true, a.Int(0)) is { } header ? SocialMailHeaderTable(header) : null);
        host.Define(table, "GetQuestMailSender", a => data.GetQuestMailSender((long)a.Num(0)));
        host.Define(table, "GetReceivedMailCount", _ =>
            (double)(State().ReceivedTotal >= 0 ? State().ReceivedTotal : State().Received.Count));
        host.Define(table, "GetReceivedMailTitleInfo", a =>
            Header(false, a.Int(0)) is { } header ? SocialMailHeaderTable(header) : null);
        host.Define(table, "IsBalanceReceiptMail", a => SocialMailFlag(State(), a.Str(0), x => x.IsBalanceReceiptMail));
        host.Define(table, "IsBillingMail", a => SocialMailFlag(State(), a.Str(0), x => x.IsBillingMail));
        host.Define(table, "IsExistCurrentMailBody", _ => State().Current is not null);
        host.Define(table, "IsHeroDropoutComebackRequestMail", a =>
            SocialMailFlag(State(), a.Str(0), x => x.IsHeroDropoutComebackRequestMail));
        host.Define(table, "IsReportedSpamMail", a =>
            FindSocialMailHeader(State(), a.Str(0) ?? "0")?.IsReportedSpam ?? false);
        host.Define(table, "IsReturnedCurrentMail", _ => State().Current?.IsReturned ?? false);
        host.Define(table, "IsReturnedMail", a => SocialMailFlag(State(), a.Str(0), x => x.IsReturned));
        host.Define(table, "IsSystemMail", a => SocialMailFlag(State(), a.Str(0), x => x.IsSystemMail));
        host.Define(table, "IsTaxInKindReceiptMail", a => SocialMailFlag(State(), a.Str(0), x => x.IsTaxInKindReceiptMail));
        host.Define(table, "IsTaxRateChangedMail", a => SocialMailFlag(State(), a.Str(0), x => x.IsTaxRateChangedMail));
        host.Define(table, "IsUserMail", a => SocialMailFlag(State(), a.Str(0), x => x.IsUserMail));
        // The native call opens the publisher's external warning/refund page. There is no web-navigation contract here.
        host.Define(table, "OpenGoodMailWarringSite", _ => null);
        host.Define(table, "PayCurrentMailChargeMoney", _ => { data.PayCurrentMailCharge(kind); return null; });
        host.Define(table, "PlaceMailItem", a => data.PlaceMailItem(kind, a.Int(0)));
        host.Define(table, "PlaceMailItemFromBag", a =>
            data.PlaceMailItemFromBag(kind, a.Int(0), a.Int(1), a.Int(2)));
        host.Define(table, "ReadPostedMail", a =>
        {
            if (Header(true, a.Int(0)) is { } header) data.ReadMail(kind, true, header.MailId);
            return null;
        });
        host.Define(table, "ReadPostedMailById", a => { data.ReadMail(kind, true, a.Str(0) ?? "0"); return null; });
        host.Define(table, "ReadReceivedMail", a =>
        {
            if (Header(false, a.Int(0)) is { } header) data.ReadMail(kind, false, header.MailId);
            return null;
        });
        host.Define(table, "ReadReceivedMailById", a => { data.ReadMail(kind, false, a.Str(0) ?? "0"); return null; });
        host.Define(table, "ReportSpam", a => { data.ReportSpamMail(kind, a.Str(0) ?? "0"); return null; });
        host.Define(table, "RequestMailList", a =>
        {
            data.RequestMailList(kind, a.Int(0), a.Int(1), a.Int(2));
            return null;
        });
        host.Define(table, "ReturnMailById", a => { data.ReturnMail(kind, a.Str(0) ?? "0"); return null; });
        host.Define(table, "SendMail", a =>
        {
            return a.Table(0) is { } mail && data.SendMail(kind, ParseSocialOutgoingMail(mail));
        });
        host.Define(table, "SetReading", a => { data.SetMailReading(kind, a.Bool(0)); return null; });
        host.Define(table, "SetWriting", a => { data.SetMailWriting(kind, a.Bool(0)); return null; });
        host.Define(table, "TakeAllCurrentMailItem", _ => { data.TakeAllCurrentMailItems(kind); return null; });
        host.Define(table, "TakeAttachmentSequentially", a =>
        {
            data.TakeMailAttachmentsSequentially(kind, a.Str(0) ?? "0");
            return null;
        });
        host.Define(table, "TakeCurrentMailItem", a => { data.TakeCurrentMailItem(kind, a.Int(0)); return null; });
        host.Define(table, "TakeCurrentMailMoney", _ => { data.TakeCurrentMailMoney(kind); return null; });
        host.Define(table, "ToggleMailBox", _ => { data.ToggleMailbox(kind); return null; });
    }

    private static X2SocialMailHeader? FindSocialMailHeader(X2SocialMailboxState state, string id)
        => state.Received.Concat(state.Posted).FirstOrDefault(x => x is not null && x.MailId == id);

    private static bool SocialMailFlag(
        X2SocialMailboxState state,
        string? id,
        Func<X2SocialMailHeader, bool> headerFlag)
    {
        if (state.Current is { } body && body.MailId == id)
        {
            return headerFlag(new X2SocialMailHeader
            {
                MailId = body.MailId,
                IsReturned = body.IsReturned,
                IsUserMail = body.IsUserMail,
                IsSystemMail = body.IsSystemMail,
                IsBillingMail = body.IsBillingMail,
                IsBalanceReceiptMail = body.IsBalanceReceiptMail,
                IsTaxInKindReceiptMail = body.IsTaxInKindReceiptMail,
                IsTaxRateChangedMail = body.IsTaxRateChangedMail,
                IsHeroDropoutComebackRequestMail = body.IsHeroDropoutComebackRequestMail,
            });
        }
        return FindSocialMailHeader(state, id ?? "0") is { } header && headerFlag(header);
    }

    private static LuaTable SocialMailHeaderTable(X2SocialMailHeader header) => new()
    {
        ["mail_id"] = header.MailId,
        ["mail_type"] = (double)header.MailType,
        ["title"] = header.Title,
        ["sender"] = header.Sender,
        ["receiver"] = header.Receiver,
        ["is_read"] = header.IsRead,
        ["hasItem"] = header.HasAttachments || header.AttachedItemCount > 0,
        ["failToLoadItems"] = header.FailedToLoadItems,
        ["extra"] = (double)header.Extra,
        ["zone_group_name"] = header.ZoneGroupName,
        ["is_nation"] = header.IsNation,
    };

    private static LuaTable SocialMailBodyTable(X2SocialMailBody body) => new()
    {
        ["mail_id"] = body.MailId,
        ["mail_type"] = (double)body.MailType,
        ["sender"] = body.Sender,
        ["receiver"] = body.Receiver,
        ["title"] = body.Title,
        ["text"] = body.Text,
        ["sendDate"] = SocialMailDateTable(body.SendDate),
        ["recvDate"] = SocialMailDateTable(body.ReceiveDate),
        ["system_charge"] = (double)body.SystemChargePermille,
        ["extra"] = (double)body.Extra,
        ["zone_group_name"] = body.ZoneGroupName,
        ["is_nation"] = body.IsNation,
    };

    private static LuaTable SocialMailDateTable(DateTime value) => new()
    {
        ["year"] = (double)value.Year,
        ["month"] = (double)value.Month,
        ["day"] = (double)value.Day,
        ["hour"] = (double)value.Hour,
        ["minute"] = (double)value.Minute,
        ["second"] = (double)value.Second,
    };

    private static LuaTable SocialMailItemTable(X2SocialMailItem item)
    {
        var result = new LuaTable();
        foreach (var (key, value) in item.Fields) result[key] = value;
        result["type"] = (double)item.TemplateId;
        result["itemType"] = (double)item.TemplateId;
        result["itemId"] = item.ItemId.ToString(CultureInfo.InvariantCulture);
        result["name"] = item.Name;
        result["iconPath"] = item.IconPath;
        result["grade"] = (double)item.Grade;
        result["itemGrade"] = (double)item.Grade;
        result["stack"] = (double)item.Stack;
        result["isStackable"] = item.IsStackable;
        return result;
    }

    private static X2SocialOutgoingMail ParseSocialOutgoingMail(LuaTable table)
    {
        var receivers = new List<string>();
        if (SocialMailValue(table, "receivers") is LuaTable receiverTable)
        {
            foreach (var value in receiverTable
                         .Where(x => x.Key is double)
                         .OrderBy(x => (double)x.Key)
                         .Select(x => x.Value))
            {
                if (value is string name) receivers.Add(name);
                else if (value is LuaTable member && SocialMailStringValue(member, "name") is { Length: > 0 } memberName)
                    receivers.Add(memberName);
            }
        }

        return new X2SocialOutgoingMail
        {
            Receiver = SocialMailStringValue(table, "receiver") ?? "",
            Receivers = receivers,
            Title = SocialMailStringValue(table, "title") ?? "",
            Text = SocialMailStringValue(table, "text") ?? "",
            Type = SocialMailIntValue(table, "type"),
            Money = SocialMailStringValue(table, "money") ?? "0",
            DoodadId = SocialMailUIntValue(table, "doodadId"),
            WithReceiver = SocialMailBoolValue(table, "withReceiver"),
            GroupMail = SocialMailBoolValue(table, "groupMail"),
        };
    }

    private static object? SocialMailValue(LuaTable table, string key) => table.TryGetValue(key, out var value) ? value : null;
    private static string? SocialMailStringValue(LuaTable table, string key) => SocialMailValue(table, key) switch
    {
        null => null,
        string value => value,
        double value => value.ToString(CultureInfo.InvariantCulture),
        var value => value.ToString(),
    };
    private static int SocialMailIntValue(LuaTable table, string key)
        => SocialMailValue(table, key) is double value ? (int)value
            : int.TryParse(SocialMailStringValue(table, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0;
    private static uint SocialMailUIntValue(LuaTable table, string key)
        => uint.TryParse(SocialMailStringValue(table, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
    private static bool SocialMailBoolValue(LuaTable table, string key) => SocialMailValue(table, key) is not (null or false)
        && SocialMailStringValue(table, key) != "0";
    private static string SocialMailNumberString(long value) => value.ToString(CultureInfo.InvariantCulture);
}
