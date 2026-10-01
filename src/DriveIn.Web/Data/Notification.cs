using System.ComponentModel.DataAnnotations;

namespace DriveIn.Web.Data;

// Something new for one user, shown under the bell (NotificationService). If it's still unread a while later it's
// emailed, as a link only (NotificationService.SendDueEmailsAsync), unless the user turned email off.
public class Notification
{
    public const int MaxTitleLength = 200;
    public const int MaxLinkLength = 300;

    public long Id { get; set; }

    public string UserId { get; set; } = "";
    public ApplicationUser? User { get; set; }

    public NotificationKind Kind { get; set; }

    [Required, MaxLength(MaxTitleLength)]
    public string Title { get; set; } = "";

    // App-relative, e.g. "messages/12".
    [Required, MaxLength(MaxLinkLength)]
    public string Link { get; set; } = "";

    // Message notifications: one unread notification per user and conversation, updated as more messages arrive.
    public int? ConversationId { get; set; }
    public Conversation? Conversation { get; set; }

    // How many things it stands for (e.g. new messages in the conversation since it was last read).
    public int Count { get; set; } = 1;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
    public DateTimeOffset? EmailedAt { get; set; }

    public bool IsRead => ReadAt is not null;
}

// Stored by name, so members can be added but not renamed.
public enum NotificationKind
{
    Message,
}
