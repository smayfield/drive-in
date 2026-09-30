using System.ComponentModel.DataAnnotations;

namespace DriveIn.Web.Data;

// A sold ticket moved to another spot at the gate (TicketSalesService.Moves), e.g. because the car is too large for
// the spot bought. Kept as the ticket's history; deleted with the ticket.
public class TicketMove
{
    public int Id { get; set; }

    public int TicketId { get; set; }
    public Ticket? Ticket { get; set; }

    public int FromRow { get; set; }
    public int FromSpot { get; set; }
    [MaxLength(10)] public string FromLabel { get; set; } = "";
    public VehicleSize FromVehicleSize { get; set; }

    public int ToRow { get; set; }
    public int ToSpot { get; set; }
    [MaxLength(10)] public string ToLabel { get; set; } = "";
    public VehicleSize ToVehicleSize { get; set; }

    // The employee who moved it, while their account exists.
    public string? MovedById { get; set; }
    public ApplicationUser? MovedBy { get; set; }

    public DateTimeOffset MovedAt { get; set; }
}
