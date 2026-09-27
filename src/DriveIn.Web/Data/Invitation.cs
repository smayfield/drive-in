namespace DriveIn.Web.Data;

public enum InvitationKind
{
    Owner = 0,
    Employee = 1,
}

public class Invitation
{
    public int Id { get; set; }

    public string Email { get; set; } = "";

    public int TheaterId { get; set; }
    public Theater? Theater { get; set; }

    public InvitationKind Kind { get; set; }

    // SHA-256 of the token in the invite link; the raw token is never stored.
    public string TokenHash { get; set; } = "";

    public string? InvitedById { get; set; }
    public ApplicationUser? InvitedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
}
