using System.Security.Claims;
using DriveIn.Web.Authorization;
using DriveIn.Web.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace DriveIn.Web.Services;

// Id is null for a new option; existing options keep their ids so later sales can refer to them.
public sealed record PriceOptionInput(int? Id, string Name, string? Description, decimal Price);

public sealed record AddOnInput(string Name, string? Description, AddOnKind Kind, decimal Amount, bool IsActive);

public sealed record TheaterPricing(List<PriceSchedule> Schedules, List<AddOn> AddOns)
{
    public PriceSchedule? Default => Schedules.FirstOrDefault(s => s.IsDefault);
}

// What a showtime sells for: its schedule (the theater default unless overridden) and the active add-ons.
public sealed record ShowtimePricing(PriceSchedule Schedule, bool IsOverride, List<AddOn> AddOns);

// A theater's price schedules and add-ons. Any member may view them; changes require ManagePricing.
// Choosing a showtime's schedule is part of the schedule (ScheduleService, ManageSchedule).
public sealed class PricingService(IDbContextFactory<ApplicationDbContext> dbFactory, IAuthorizationService auth, TimeProvider time)
{
    public const string DefaultScheduleName = "Standard";

    public async Task<TheaterPricing> GetAsync(ClaimsPrincipal user, int theaterId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        Guard.RequireMember(user, await FindTheaterAsync(db, theaterId));
        var schedules = await db.PriceSchedules.AsNoTracking().Include(s => s.Options)
            .Where(s => s.TheaterId == theaterId).ToListAsync();
        foreach (var s in schedules)
            s.Options.Sort((a, b) => a.SortOrder.CompareTo(b.SortOrder));
        var addOns = await db.AddOns.AsNoTracking().Where(a => a.TheaterId == theaterId)
            .OrderBy(a => a.SortOrder).ThenBy(a => a.Id).ToListAsync();
        return new TheaterPricing(schedules.OrderByDescending(s => s.IsDefault).ThenBy(s => s.Name).ToList(), addOns);
    }

    public async Task<ShowtimePricing> GetForShowtimeAsync(ClaimsPrincipal user, int showtimeId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var showtime = await db.Showtimes.AsNoTracking().Include(s => s.Screen!.Theater)
            .FirstOrDefaultAsync(s => s.Id == showtimeId) ?? throw new NotFoundException("Showtime not found.");
        var theaterId = showtime.Screen!.TheaterId;
        Guard.RequireMember(user, showtime.Screen.Theater!);
        var schedules = db.PriceSchedules.AsNoTracking().Include(s => s.Options).Where(s => s.TheaterId == theaterId);
        var schedule = await (showtime.PriceScheduleId is int id ? schedules.Where(s => s.Id == id) : schedules.Where(s => s.IsDefault))
            .FirstOrDefaultAsync() ?? throw new NotFoundException("This theater has no default price schedule.");
        schedule.Options.Sort((a, b) => a.SortOrder.CompareTo(b.SortOrder));
        var addOns = await db.AddOns.AsNoTracking().Where(a => a.TheaterId == theaterId && a.IsActive)
            .OrderBy(a => a.SortOrder).ThenBy(a => a.Id).ToListAsync();
        return new ShowtimePricing(schedule, showtime.PriceScheduleId is not null, addOns);
    }

    // --- Schedules ---

    // Starts empty, or as a copy of another of the theater's schedules (e.g. copy Standard, then raise prices for 3D).
    public async Task<PriceSchedule> CreateScheduleAsync(ClaimsPrincipal user, int theaterId, string name, int? copyFromId = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await auth.RequireAsync(user, await FindTheaterAsync(db, theaterId), TheaterPermissions.ManagePricing);
        var trimmed = await ValidateScheduleNameAsync(db, theaterId, name, exceptId: null);
        var schedule = new PriceSchedule { TheaterId = theaterId, Name = trimmed };
        if (copyFromId is int sourceId)
        {
            var source = await db.PriceSchedules.AsNoTracking().Include(s => s.Options)
                .FirstOrDefaultAsync(s => s.Id == sourceId && s.TheaterId == theaterId)
                ?? throw new NotFoundException("Price schedule not found.");
            schedule.Options = source.Options.OrderBy(o => o.SortOrder)
                .Select((o, i) => new PriceOption { Name = o.Name, Description = o.Description, Price = o.Price, SortOrder = i })
                .ToList();
        }
        db.PriceSchedules.Add(schedule);
        await SaveNamedAsync(db, trimmed);
        return schedule;
    }

    // Renames the schedule and replaces its options: options with an Id are updated, new ones added, missing ones removed.
    public async Task UpdateScheduleAsync(ClaimsPrincipal user, int scheduleId, string name, IReadOnlyList<PriceOptionInput> options)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var schedule = await LoadScheduleAuthorizedAsync(db, user, scheduleId);
        var trimmed = await ValidateScheduleNameAsync(db, schedule.TheaterId, name, exceptId: schedule.Id);
        if (options.Count > PriceOption.MaxPerSchedule)
            throw new AppValidationException($"A price schedule can have at most {PriceOption.MaxPerSchedule} options.");
        var cleaned = options.Select(o => (o.Id, Name: CleanName(o.Name, "option"), Description: CleanDescription(o.Description),
            Price: ValidateMoney(o.Price, "Prices"))).ToList();
        var duplicate = cleaned.GroupBy(o => o.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new AppValidationException($"\"{duplicate.Key}\" is listed more than once.");
        if (cleaned.Any(o => o.Id is int id && schedule.Options.All(x => x.Id != id)))
            throw new NotFoundException("Price option not found.");

        schedule.Name = trimmed;
        var keep = cleaned.Where(o => o.Id is not null).Select(o => o.Id!.Value).ToHashSet();
        db.PriceOptions.RemoveRange(schedule.Options.Where(o => !keep.Contains(o.Id)));
        for (var i = 0; i < cleaned.Count; i++)
        {
            var input = cleaned[i];
            var option = input.Id is int id ? schedule.Options.Single(o => o.Id == id) : new PriceOption { ScheduleId = schedule.Id };
            option.Name = input.Name;
            option.Description = input.Description;
            option.Price = input.Price;
            option.SortOrder = i;
            if (input.Id is null)
                db.PriceOptions.Add(option);
        }
        await SaveNamedAsync(db, trimmed);
    }

    public async Task SetDefaultScheduleAsync(ClaimsPrincipal user, int scheduleId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var schedule = await LoadScheduleAuthorizedAsync(db, user, scheduleId);
        if (schedule.IsDefault)
            return;
        await using var tx = await db.Database.BeginTransactionAsync();
        // Two saves: the one-default-per-theater index is checked row by row.
        foreach (var old in await db.PriceSchedules.Where(s => s.TheaterId == schedule.TheaterId && s.IsDefault).ToListAsync())
            old.IsDefault = false;
        await db.SaveChangesAsync();
        schedule.IsDefault = true;
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    // The default schedule can't be deleted, nor one that upcoming showtimes use. Past showtimes that
    // used it fall back to the default.
    public async Task DeleteScheduleAsync(ClaimsPrincipal user, int scheduleId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var schedule = await LoadScheduleAuthorizedAsync(db, user, scheduleId);
        if (schedule.IsDefault)
            throw new AppValidationException("Make another schedule the default before deleting this one.");
        var now = time.GetUtcNow();
        var showtimes = await db.Showtimes.Include(s => s.Film).Where(s => s.PriceScheduleId == scheduleId).ToListAsync();
        if (showtimes.Any(s => ScheduleService.EndsAt(s) > now))
            throw new AppValidationException($"Upcoming showtimes use \"{schedule.Name}\". Change their pricing first.");
        foreach (var s in showtimes)
            s.PriceScheduleId = null;
        db.PriceOptions.RemoveRange(schedule.Options);
        db.PriceSchedules.Remove(schedule);
        await db.SaveChangesAsync();
    }

    // --- Add-ons ---

    public async Task<AddOn> AddAddOnAsync(ClaimsPrincipal user, int theaterId, AddOnInput input)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await auth.RequireAsync(user, await FindTheaterAsync(db, theaterId), TheaterPermissions.ManagePricing);
        var addOn = new AddOn { TheaterId = theaterId };
        await ApplyAsync(db, input, addOn);
        addOn.SortOrder = (await db.AddOns.Where(a => a.TheaterId == theaterId).Select(a => (int?)a.SortOrder).MaxAsync() ?? -1) + 1;
        db.AddOns.Add(addOn);
        await db.SaveChangesAsync();
        return addOn;
    }

    public async Task UpdateAddOnAsync(ClaimsPrincipal user, int addOnId, AddOnInput input)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var addOn = await LoadAddOnAuthorizedAsync(db, user, addOnId);
        await ApplyAsync(db, input, addOn);
        await db.SaveChangesAsync();
    }

    public async Task DeleteAddOnAsync(ClaimsPrincipal user, int addOnId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        db.AddOns.Remove(await LoadAddOnAuthorizedAsync(db, user, addOnId));
        await db.SaveChangesAsync();
    }

    // --- Helpers ---

    private static async Task<Theater> FindTheaterAsync(ApplicationDbContext db, int theaterId) =>
        await db.Theaters.AsNoTracking().FirstOrDefaultAsync(t => t.Id == theaterId)
            ?? throw new NotFoundException("Theater not found.");

    private async Task<PriceSchedule> LoadScheduleAuthorizedAsync(ApplicationDbContext db, ClaimsPrincipal user, int scheduleId)
    {
        var schedule = await db.PriceSchedules.Include(s => s.Theater).Include(s => s.Options)
            .FirstOrDefaultAsync(s => s.Id == scheduleId) ?? throw new NotFoundException("Price schedule not found.");
        await auth.RequireAsync(user, schedule.Theater!, TheaterPermissions.ManagePricing);
        return schedule;
    }

    private async Task<AddOn> LoadAddOnAuthorizedAsync(ApplicationDbContext db, ClaimsPrincipal user, int addOnId)
    {
        var addOn = await db.AddOns.Include(a => a.Theater).FirstOrDefaultAsync(a => a.Id == addOnId)
            ?? throw new NotFoundException("Add-on not found.");
        await auth.RequireAsync(user, addOn.Theater!, TheaterPermissions.ManagePricing);
        return addOn;
    }

    private static async Task ApplyAsync(ApplicationDbContext db, AddOnInput input, AddOn addOn)
    {
        var name = CleanName(input.Name, "add-on");
        if (!Enum.IsDefined(input.Kind))
            throw new AppValidationException("Choose whether the add-on is a fee or a discount.");
        var amount = input.Kind == AddOnKind.PercentDiscount
            ? input.Amount is > 0 and <= 100 && input.Amount == Math.Round(input.Amount, 2)
                ? input.Amount
                : throw new AppValidationException("A percent discount must be more than 0 and at most 100.")
            : ValidateMoney(input.Amount, "Amounts");
        if (input.Kind != AddOnKind.PercentDiscount && amount == 0)
            throw new AppValidationException("Give the add-on an amount.");
        var existing = await db.AddOns.Where(a => a.TheaterId == addOn.TheaterId && a.Id != addOn.Id).Select(a => a.Name).ToListAsync();
        if (existing.Contains(name, StringComparer.OrdinalIgnoreCase))
            throw new AppValidationException($"There's already an add-on named \"{name}\".");
        addOn.Name = name;
        addOn.Description = CleanDescription(input.Description);
        addOn.Kind = input.Kind;
        addOn.Amount = amount;
        addOn.IsActive = input.IsActive;
    }

    private static async Task<string> ValidateScheduleNameAsync(ApplicationDbContext db, int theaterId, string name, int? exceptId)
    {
        var trimmed = CleanName(name, "price schedule");
        var normalized = PriceSchedule.Normalize(trimmed);
        if (await db.PriceSchedules.AnyAsync(s => s.TheaterId == theaterId && s.Id != exceptId && s.NormalizedName == normalized))
            throw new AppValidationException($"There's already a price schedule named \"{trimmed}\".");
        return trimmed;
    }

    // The app-level name check gives the friendly error; the unique index catches concurrent duplicates.
    private static async Task SaveNamedAsync(ApplicationDbContext db, string name)
    {
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            throw new AppValidationException($"There's already a price schedule named \"{name}\".");
        }
    }

    private static string CleanName(string? name, string what)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0)
            throw new AppValidationException($"Give the {what} a name.");
        if (trimmed.Length > 60)
            throw new AppValidationException("Names can be at most 60 characters.");
        return trimmed;
    }

    private static string? CleanDescription(string? description)
    {
        var trimmed = description?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;
        return trimmed.Length <= 200 ? trimmed : throw new AppValidationException("Descriptions can be at most 200 characters.");
    }

    private static decimal ValidateMoney(decimal amount, string what)
    {
        if (amount is < 0 or > Money.Max)
            throw new AppValidationException($"{what} must be between $0.00 and {Money.Format(Money.Max)}.");
        if (amount != Math.Round(amount, 2))
            throw new AppValidationException($"{what} can't have fractions of a cent.");
        return amount;
    }
}
