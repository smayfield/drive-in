using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace DriveIn.Web.Data;

// A named set of ticket prices, e.g. "Standard" or "3D". Each theater has exactly one default schedule,
// which showtimes use unless they're set to another of the theater's schedules.
public class PriceSchedule
{
    public int Id { get; set; }

    public int TheaterId { get; set; }
    public Theater? Theater { get; set; }

    [Required, MaxLength(60)]
    public string Name
    {
        get;
        set
        {
            field = value;
            NormalizedName = Normalize(value);
        }
    } = "";

    // Upper-cased Name; unique per theater (enforced by the DB).
    [MaxLength(60)]
    public string NormalizedName { get; private set; } = "";

    public static string Normalize(string name) => name.Trim().ToUpperInvariant();

    // At most one per theater (enforced by the DB).
    public bool IsDefault { get; set; }

    public List<PriceOption> Options { get; set; } = [];
}

// One way to buy a spot under a schedule, e.g. "1 occupant", "2 occupants" or "Car load".
public class PriceOption
{
    public const int MaxPerSchedule = 20;

    public int Id { get; set; }

    public int ScheduleId { get; set; }
    public PriceSchedule? Schedule { get; set; }

    [Required, MaxLength(60)]
    public string Name { get; set; } = "";

    [MaxLength(200)]
    public string? Description { get; set; }

    public decimal Price { get; set; }

    public int SortOrder { get; set; }
}

// An optional extra on a ticket: a fee (e.g. outside food) or a discount (e.g. veterans, seniors).
public class AddOn
{
    public int Id { get; set; }

    public int TheaterId { get; set; }
    public Theater? Theater { get; set; }

    [Required, MaxLength(60)]
    public string Name
    {
        get;
        set
        {
            field = value;
            NormalizedName = PriceSchedule.Normalize(value);
        }
    } = "";

    // Upper-cased Name; unique per theater (enforced by the DB).
    [MaxLength(60)]
    public string NormalizedName { get; private set; } = "";

    [MaxLength(200)]
    public string? Description { get; set; }

    public AddOnKind Kind { get; set; }

    // Dollars for Fee and Discount; percent off the ticket for PercentDiscount.
    public decimal Amount { get; set; }

    // Inactive add-ons are kept but not offered.
    public bool IsActive { get; set; } = true;

    public int SortOrder { get; set; }

    public string Display => Kind switch
    {
        AddOnKind.Fee => $"+{Money.Format(Amount)}",
        AddOnKind.Discount => $"−{Money.Format(Amount)}",
        AddOnKind.PercentDiscount => $"−{Amount.ToString("0.##", CultureInfo.InvariantCulture)}%",
        _ => Amount.ToString(CultureInfo.InvariantCulture),
    };
}

// Stored by name, so members can be added but not renamed.
public enum AddOnKind
{
    Fee,
    Discount,
    PercentDiscount,
}

public static class Money
{
    public const decimal Max = 9_999.99m;

    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-US");

    public static string Format(decimal amount) => amount.ToString("C", Culture);
}
