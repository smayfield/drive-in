using System.ComponentModel.DataAnnotations;

namespace DriveIn.Web.Data;

// The log of free admission (see Ticket.IsComp): one row each time a comp is requested, issued, approved, denied or
// withdrawn. The ticket, showing and people are copied in, so the record survives a denied request's ticket being
// deleted and employees' accounts going away.
public class CompEvent
{
    public long Id { get; set; }

    public int TheaterId { get; set; }
    public Theater? Theater { get; set; }

    public int? TicketId { get; set; }

    public CompAction Action { get; set; }
    public DateTimeOffset At { get; set; }

    // Who did it (the employee, or the approver for Approved / Denied / Cancelled).
    public string? ActorId { get; set; }
    [MaxLength(256)] public string ActorName { get; set; } = "";

    // Who asked for the spot, when someone else did (an approver acting on a request).
    [MaxLength(256)] public string? OfferedByName { get; set; }

    [MaxLength(200)] public string ShowingTitle { get; set; } = "";
    public DateTimeOffset ShowingStartsAt { get; set; }
    [MaxLength(10)] public string SpotLabel { get; set; } = "";
    [MaxLength(100)] public string GuestName { get; set; } = "";
    [MaxLength(500)] public string? Reason { get; set; }
    [MaxLength(500)] public string? Note { get; set; }

    // From a demo theater; deleted when it goes live, like its test tickets.
    public bool IsTest { get; set; }
}

// Stored by name, so members can be added but not renamed.
public enum CompAction
{
    Requested,
    Issued,
    Approved,
    Denied,
    Cancelled,
}
