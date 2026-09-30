#nullable enable

namespace AAEmu.GodotViewer.Net;

public sealed record MailHeaderRecord(
    long MailId, byte Type, byte Status, string Title, string SenderName, byte AttachmentCount,
    string ReceiverName, long OpenDate, bool Returned, long Extra, bool FailedToLoadBody);

public sealed record MailUnreadCounts(
    int TotalSent, int TotalReceived, int TotalMiaReceived, int TotalCommercialReceived,
    int Sent, int Received, int MiaReceived, int CommercialReceived);

public sealed record MailBodyRecord(
    long MailId, byte Type, string ReceiverName, string Title, string Text,
    long Copper, long BillingAmount, long MoneyAmount2, uint Reserved,
    long SendDate, long ReceiveDate, long OpenDate, IReadOnlyList<ItemSnapshot?> Attachments);

public sealed record MailSlot(byte SlotType, byte SlotIndex);

public sealed record MailFailedEvent(byte Error, IReadOnlyList<MailSlot> Slots, bool Money) : GameEvent;
public sealed record MailCountsEvent(MailUnreadCounts Counts) : GameEvent;
public sealed record MailSentEvent(bool GroupSending, MailHeaderRecord Header, MailUnreadCounts Counts, IReadOnlyList<MailSlot> Slots) : GameEvent;
public sealed record MailReceivedEvent(MailHeaderRecord Header, MailUnreadCounts Counts, ulong Extra, MailBodyRecord? Body) : GameEvent;
public sealed record MailListEntryEvent(bool IsSent, uint Total, MailHeaderRecord Header, byte MailboxKind) : GameEvent;
public sealed record MailListEndEvent(byte MailboxKind, MailUnreadCounts Counts) : GameEvent;
public sealed record MailBodyEvent(bool IsPrepare, bool IsSent, MailBodyRecord Body, ulong Extra, bool OpenDateModified, MailUnreadCounts Counts) : GameEvent;
public sealed record MailReceiverOpenedEvent(long MailId, long OpenDate) : GameEvent;
public sealed record MailAttachmentTakenEvent(long MailId, bool Money, bool AaPoint, bool HonorPoint, bool Sequential, IReadOnlyList<ulong> ItemIds, IReadOnlyList<MailSlot> Slots) : GameEvent;
public sealed record MailChargePaidEvent(long MailId) : GameEvent;
public sealed record MailDeletedEvent(bool IsSent, long MailId, bool UnreadCountChanged, MailUnreadCounts Counts) : GameEvent;
public sealed record MailReturnedEvent(long MailId, MailHeaderRecord Header, MailUnreadCounts Counts) : GameEvent;
public sealed record MailStatusUpdatedEvent(bool IsSent, long MailId, byte Status) : GameEvent;
public sealed record MailRemovedEvent(bool IsSent, long MailId, bool FromAccountBox) : GameEvent;

/// <summary>Mail packet layouts for the 10.0.2.13 game protocol.</summary>
public static class MailProtocol
{
    public const ushort SCMailFailed = 0x15A;
    public const ushort SCCountTotalMail = 0x15B;
    public const ushort SCMailSent = 0x15C;
    public const ushort SCGotMail = 0x15D;
    public const ushort SCMailList = 0x15E;
    public const ushort SCMailListEnd = 0x15F;
    public const ushort SCMailBody = 0x160;
    public const ushort SCMailReceiverOpened = 0x161;
    public const ushort SCAttachmentTaken = 0x162;
    public const ushort SCChargeMoneyPaid = 0x163;
    public const ushort SCMailDeleted = 0x164;
    public const ushort SCMailReturned = 0x165;
    public const ushort SCMailStatusUpdated = 0x166;
    public const ushort SCMailRemoved = 0x167;
    public const ushort SCMailRemovedFromAccountBox = 0x168;

    public const ushort CSSendMail = 0x0DB;
    public const ushort CSListMail = 0x0DC;
    public const ushort CSListMailContinue = 0x0DD;
    public const ushort CSReadMail = 0x0DE;
    public const ushort CSTakeAttachmentItem = 0x0DF;
    public const ushort CSTakeAttachmentMoney = 0x0E0;
    public const ushort CSTakeAttachmentSequentially = 0x0E1;
    public const ushort CSPayChargeMoney = 0x0E2;
    public const ushort CSDeleteMail = 0x0E3;
    public const ushort CSReturnMail = 0x0E4;
    public const ushort CSHeroDropoutComebackAccept = 0x0EF;
    public const ushort CSTakeAllAttachmentItem = 0x194;
    public const ushort CSReportSpamMail = 0x1B5;

    public static GameEvent Parse(ushort opcode, byte[] body)
    {
        var r = new WireReader(body);
        return opcode switch
        {
            SCMailFailed => ParseFailed(r),
            SCCountTotalMail => new MailCountsEvent(ReadCounts(r)),
            SCMailSent => ParseSent(r),
            SCGotMail => ParseReceived(r),
            SCMailList => new MailListEntryEvent(r.Bool(), r.U32(), ReadHeader(r), r.U8()),
            SCMailListEnd => new MailListEndEvent(r.U8(), ReadCounts(r)),
            SCMailBody => ParseBodyEvent(r),
            SCMailReceiverOpened => new MailReceiverOpenedEvent(r.S64(), r.S64()),
            SCAttachmentTaken => ParseAttachmentTaken(r),
            SCChargeMoneyPaid => new MailChargePaidEvent(r.S64()),
            SCMailDeleted => new MailDeletedEvent(r.Bool(), r.S64(), r.Bool(), ReadCounts(r)),
            SCMailReturned => new MailReturnedEvent(r.S64(), ReadHeader(r), ReadCounts(r)),
            SCMailStatusUpdated => new MailStatusUpdatedEvent(r.Bool(), r.S64(), r.U8()),
            SCMailRemoved => new MailRemovedEvent(r.Bool(), r.S64(), false),
            SCMailRemovedFromAccountBox => new MailRemovedEvent(r.Bool(), r.S64(), true),
            _ => throw new ArgumentOutOfRangeException(nameof(opcode), opcode, "not a mail opcode"),
        };
    }

    private static MailFailedEvent ParseFailed(WireReader r)
    {
        var error = r.U8();
        // AAEmu writes the returned attachment slots without a count, followed by the money flag.
        // Infer the repeated pair count from this packet's remaining bytes; the writer documents a max of 10.
        if (r.Remaining < 1 || (r.Remaining - 1) % 2 != 0)
            throw new WireException("mail failure attachment list has an invalid byte length");
        var slotCount = (r.Remaining - 1) / 2;
        if (slotCount > 10)
            throw new WireException($"mail failure attachment count {slotCount} exceeds 10");
        var slots = ReadSlots(r, slotCount);
        return new MailFailedEvent(error, slots, r.Bool());
    }

    private static MailSentEvent ParseSent(WireReader r) =>
        new(r.Bool(), ReadHeader(r), ReadCounts(r), ReadSlots(r, 10));

    private static MailReceivedEvent ParseReceived(WireReader r)
    {
        var header = ReadHeader(r);
        var counts = ReadCounts(r);
        var extra = r.U64();
        return new MailReceivedEvent(header, counts, extra, r.Bool() ? ReadBody(r) : null);
    }

    private static MailBodyEvent ParseBodyEvent(WireReader r)
    {
        var prepare = r.Bool();
        var sent = r.Bool();
        var body = ReadBody(r);
        return new MailBodyEvent(prepare, sent, body, r.U64(), r.Bool(), ReadCounts(r));
    }

    private static MailAttachmentTakenEvent ParseAttachmentTaken(WireReader r)
    {
        var mailId = r.S64();
        var money = r.Bool();
        var aaPoint = r.Bool();
        var honorPoint = r.Bool();
        var sequential = r.Bool();
        var count = r.U8();
        if (count > 10)
            throw new WireException($"invalid taken attachment count {count}");
        var ids = new List<ulong>(count);
        for (var i = 0; i < count; i++)
            ids.Add(r.U64());
        return new MailAttachmentTakenEvent(mailId, money, aaPoint, honorPoint, sequential, ids, ReadSlots(r, 10));
    }

    private static MailHeaderRecord ReadHeader(WireReader r) => new(
        r.S64(), r.U8(), r.U8(), r.Str(), r.Str(), r.U8(), r.Str(), r.S64(), r.Bool(), r.S64(), r.Bool());

    private static MailUnreadCounts ReadCounts(WireReader r) => new(
        r.S32(), r.S32(), r.S32(), r.S32(), r.S32(), r.S32(), r.S32(), r.S32());

    private static MailBodyRecord ReadBody(WireReader r)
    {
        var id = r.S64();
        var type = r.U8();
        var receiver = r.Str();
        var title = r.Str();
        var text = r.Str();
        var copper = r.S64();
        var billing = r.S64();
        var money2 = r.S64();
        var reserved = r.U32();
        var sent = r.S64();
        var received = r.S64();
        var opened = r.S64();
        var attachments = new List<ItemSnapshot?>(10);
        for (var i = 0; i < 10; i++)
            attachments.Add(InventoryProtocol.ReadItem(r));
        return new MailBodyRecord(id, type, receiver, title, text, copper, billing, money2, reserved, sent, received, opened, attachments);
    }

    private static IReadOnlyList<MailSlot> ReadSlots(WireReader r, int count)
    {
        var slots = new List<MailSlot>(count);
        for (var i = 0; i < count; i++)
            slots.Add(new MailSlot(r.U8(), r.U8()));
        return slots;
    }

    public static byte[] WriteList(byte mailboxKind, uint startIndex, uint sentCount, bool recover = false, bool test = false) =>
        new WireWriter().U8(mailboxKind).U32(startIndex).U32(sentCount).Bool(recover).Bool(test).ToArray();

    public static byte[] WriteListContinue(byte mailboxKind) => new WireWriter().U8(mailboxKind).ToArray();

    public static byte[] WriteRead(bool isSent, long mailId) =>
        new WireWriter().Bool(isSent).S64(mailId).ToArray();

    /// <summary>The recovered client sends mail id, slot type, and attachment index.</summary>
    public static byte[] WriteTakeAttachmentItem(long mailId, byte slotType, byte attachmentIndex) =>
        new WireWriter().S64(mailId).U8(slotType).U8(attachmentIndex).ToArray();

    public static byte[] WriteTakeAttachmentMoney(long mailId) => new WireWriter().S64(mailId).ToArray();
    public static byte[] WriteTakeAttachmentSequentially(long mailId) => new WireWriter().S64(mailId).ToArray();
    public static byte[] WritePayChargeMoney(long mailId, bool autoUseAaPoint) =>
        new WireWriter().S64(mailId).Bool(autoUseAaPoint).ToArray();
    public static byte[] WriteDelete(long mailId, bool isSent) => new WireWriter().S64(mailId).Bool(isSent).ToArray();
    public static byte[] WriteReturn(long mailId) => new WireWriter().S64(mailId).ToArray();
    public static byte[] WriteTakeAllAttachmentItems(long mailId) => new WireWriter().S64(mailId).ToArray();
    public static byte[] WriteReportSpamMail(ulong type, string sender) =>
        new WireWriter().U64(type).Str(sender).ToArray();
    public static byte[] WriteHeroDropoutComebackAccept(ulong type) => new WireWriter().U64(type).ToArray();

    public static byte[] WriteSend(
        byte kind, string receiver, ulong receiverReference, string title, string text,
        byte attachmentCount, ulong money0, ulong money1, ulong money2, uint money3, long extra,
        bool groupMail, IReadOnlyList<MailSlot> attachmentSlots, uint mailboxDoodadObjectId,
        ulong groupMoney = 0, IReadOnlyList<ulong>? groupRecipients = null)
    {
        if (attachmentCount > 10 || attachmentSlots.Count > 10)
            throw new ArgumentOutOfRangeException(nameof(attachmentCount), "mail supports at most 10 attachments");
        var recipients = groupRecipients ?? Array.Empty<ulong>();
        if (recipients.Count > 100)
            throw new ArgumentOutOfRangeException(nameof(groupRecipients), "group mail supports at most 100 recipients");

        var w = new WireWriter().U8(kind).Str(receiver).U64(receiverReference).Str(title).Str(text)
            .U8(attachmentCount).U64(money0).U64(money1).U64(money2).U32(money3).S64(extra).Bool(groupMail);
        for (var i = 0; i < 10; i++)
        {
            var slot = i < attachmentSlots.Count ? attachmentSlots[i] : new MailSlot(0, 0);
            w.U8(slot.SlotType).U8(slot.SlotIndex);
        }
        w.Bc(mailboxDoodadObjectId).U64(groupMoney).U32((uint)recipients.Count);
        foreach (var recipient in recipients)
            w.U64(recipient);
        return w.ToArray();
    }
}
