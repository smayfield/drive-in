using System.Security.Claims;
using DriveIn.Web.Authorization;
using DriveIn.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace DriveIn.Web.Services;

// Moving a sold ticket to another spot at the same showing, at the gate: e.g. a front-row spot bought for a car, and
// the guest arrives in a large SUV. Needs MoveTickets. Only a spot that's available right now can be taken (never one
// sold, held or reserved for someone else), and it must fit the vehicle. The ticket keeps its codes, payment and
// check-in; every move is recorded in TicketMoves.
public sealed partial class TicketSalesService
{
    public async Task<TicketView> MoveAsync(ClaimsPrincipal user, string ticketCode, int row, int spot, VehicleSize vehicle)
    {
        var userId = Guard.RequireUserId(user);
        await using var db = await dbFactory.CreateDbContextAsync();
        var ticket = await db.Tickets.Include(t => t.Showtime!.Screen!.Theater)
            .FirstOrDefaultAsync(t => t.Code == ticketCode && t.Status == TicketStatus.Sold)
            ?? throw new NotFoundException("Ticket not found.");
        var screen = ticket.Showtime!.Screen!;
        await auth.RequireAsync(user, screen.Theater!, TheaterPermissions.MoveTickets);
        if (MoveProblem(ticket) is string problem)
            throw new AppValidationException(problem);
        var label = CheckSpot(screen, row, spot, vehicle);
        if (row == ticket.Row && spot == ticket.Spot && vehicle == ticket.VehicleSize)
            throw new AppValidationException($"The ticket is already for spot {label}. Choose a different spot.");
        var now = time.GetUtcNow();

        var move = new TicketMove
        {
            TicketId = ticket.Id, MovedById = userId, MovedAt = now,
            FromRow = ticket.Row, FromSpot = ticket.Spot, FromLabel = ticket.SpotLabel, FromVehicleSize = ticket.VehicleSize,
            ToRow = row, ToSpot = spot, ToLabel = label, ToVehicleSize = vehicle,
        };
        var sameSpot = row == ticket.Row && spot == ticket.Spot;
        var existing = sameSpot ? null
            : await db.Tickets.FirstOrDefaultAsync(t => t.ShowtimeId == ticket.ShowtimeId && t.Row == row && t.Spot == spot);
        if (existing is not null && !(existing.Status == TicketStatus.Held && existing.HeldUntil <= now))
            throw Taken(label);

        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            if (existing is not null)
            {
                db.Tickets.Remove(existing); // an expired hold
                try
                {
                    await db.SaveChangesAsync();
                }
                catch (DbUpdateConcurrencyException)
                {
                    throw Taken(label); // it was released and taken, or renewed, meanwhile
                }
            }
            ticket.Row = row;
            ticket.Spot = spot;
            ticket.SpotLabel = label;
            ticket.VehicleSize = vehicle;
            ticket.Stamp = Guid.NewGuid();
            db.TicketMoves.Add(move);
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                throw new AppValidationException("This ticket just changed. Look it up again.");
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                throw Taken(label); // someone else got there first
            }
            await tx.CommitAsync();
        }

        events.Publish(ticket.ShowtimeId);
        return await LoadViewAsync(db, ticket.Id);
    }

    // Showtime must be loaded.
    private string? MoveProblem(Ticket ticket) =>
        ticket.Showtime!.EndsAt <= time.GetUtcNow() ? "This showing has ended, so the ticket can't be moved." : null;
}
