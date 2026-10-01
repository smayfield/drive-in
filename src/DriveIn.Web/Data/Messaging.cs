using System.ComponentModel.DataAnnotations;

namespace DriveIn.Web.Data;

// A thread of in-app messages (MessagingService): a signed-in customer and a theater's staff, or someone (often a
// current or future theater owner) and the site admins ("support"). Messages stay in the app; email only says there's
// something new (NotificationService).
public class Conversation
{
    public const int MaxSubjectLength = 200;

    public int Id { get; set; }

    public ConversationKind Kind { get; set; }

    // Kind Theater: the theater being messaged. Deleting the theater deletes its conversations.
    public int? TheaterId { get; set; }
    public Theater? Theater { get; set; }

    // Kind Support: the theater it's about, if any. Kept (as null) when that theater is deleted.
    public int? AboutTheaterId { get; set; }
    public Theater? AboutTheater { get; set; }

    // Who started it: the customer, or whoever contacted support. Null once their account is deleted.
    public string? CustomerId { get; set; }
    public ApplicationUser? Customer { get; set; }

    [Required, MaxLength(MaxSubjectLength)]
    public string Subject { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastMessageAt { get; set; }

    // Closed conversations take no new messages until the theater (or, for support, an admin) reopens them.
    public DateTimeOffset? ClosedAt { get; set; }

    public bool IsClosed => ClosedAt is not null;

    public List<Message> Messages { get; set; } = [];
}

// Stored by name, so members can be added but not renamed.
public enum ConversationKind
{
    Theater,
    Support,
}

public class Message
{
    public const int MaxBodyLength = 4000;

    public long Id { get; set; }

    public int ConversationId { get; set; }
    public Conversation? Conversation { get; set; }

    // Null once the sender's account is deleted ("Deleted account").
    public string? SenderId { get; set; }
    public ApplicationUser? Sender { get; set; }

    // Which side of the conversation wrote it, so a theater reply reads as the theater whoever on staff sent it.
    public MessageSide Side { get; set; }

    [Required, MaxLength(MaxBodyLength)]
    public string Body { get; set; } = "";

    public DateTimeOffset SentAt { get; set; }
}

// Stored by name, so members can be added but not renamed.
public enum MessageSide
{
    Customer,
    Theater,
    Support,
}

// How far one person has read a conversation. Each staff member and admin has their own, so one reading it doesn't
// mark it read for the others.
public class ConversationRead
{
    public int ConversationId { get; set; }
    public Conversation? Conversation { get; set; }

    public string UserId { get; set; } = "";
    public ApplicationUser? User { get; set; }

    public DateTimeOffset ReadAt { get; set; }
}
