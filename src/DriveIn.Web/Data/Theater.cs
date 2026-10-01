using System.ComponentModel.DataAnnotations;

namespace DriveIn.Web.Data;

public class Theater
{
    public int Id { get; set; }

    [Required, MaxLength(200)]
    public string Name { get; set; } = "";

    [Required, MaxLength(100), RegularExpression("^[a-z0-9]+(-[a-z0-9]+)*$", ErrorMessage = "Use lowercase letters, digits and single hyphens.")]
    public string Slug { get; set; } = "";

    [MaxLength(200)] public string? AddressLine1 { get; set; }
    [MaxLength(200)] public string? AddressLine2 { get; set; }
    [MaxLength(100)] public string? City { get; set; }
    [MaxLength(100)] public string? State { get; set; }
    [MaxLength(20)] public string? PostalCode { get; set; }
    [MaxLength(100)] public string? Country { get; set; } = "US";
    [MaxLength(40), Phone] public string? Phone { get; set; }
    [MaxLength(300), Url] public string? Website { get; set; }
    [MaxLength(4000)] public string? Description { get; set; }
    [MaxLength(64)] public string? TimeZone { get; set; }

    // Where the theater is, for "near me" searches and weather. Looked up from the address when it's saved (see
    // TheaterService.LocateAsync); an editor can also enter them. Null when unknown.
    [Range(-90, 90)] public double? Latitude { get; set; }
    [Range(-180, 180)] public double? Longitude { get; set; }

    // Prefilled as the intermission between features when a double feature is scheduled.
    [Range(0, Showtime.MaxIntermissionMinutes)]
    public int DefaultIntermissionMinutes { get; set; } = 15;

    // The operating season, inclusive, as dates in the theater's time zone; either end may be left open. Showings
    // can only be scheduled within it, and it can't be changed to leave out upcoming showings.
    public DateOnly? SeasonOpensOn { get; set; }
    public DateOnly? SeasonClosesOn { get; set; }

    public bool IsInSeason(DateOnly date) =>
        (SeasonOpensOn is not DateOnly opens || date >= opens) && (SeasonClosesOn is not DateOnly closes || date <= closes);

    // Free admission: whether employees with the permission may reserve free spots for guests, and the rules. Saved
    // separately from the profile (TheaterService.UpdateFreeAdmissionSettingsAsync).
    public bool FreeAdmissionEnabled { get; set; }
    public bool FreeAdmissionRequiresApproval { get; set; } = true;
    public bool FreeAdmissionRequiresReason { get; set; } = true;
    [Range(1, 10000)] public int? FreeAdmissionMaxPerShowing { get; set; }
    [Range(1, 10000)] public int? FreeAdmissionMaxPerEmployeePerShowing { get; set; }

    // Whether the theater sells gift cards to the public (and so accepts them; cards already sold stay spendable if this
    // is turned off). Saved separately from the profile (TheaterService.UpdateGiftCardSettingsAsync).
    public bool GiftCardsEnabled { get; set; }

    // Admin switch: an inactive theater is hidden and sells nothing, whatever its mode.
    public bool IsActive { get; set; } = true;

    // Theaters that owners sign up themselves start in Demo: a private sandbox where the owner and staff can set
    // everything up and try test sales, until an admin approves their request to go live (and billing starts).
    public TheaterMode Mode { get; set; } = TheaterMode.Live;
    public DateTimeOffset? GoLiveRequestedAt { get; set; }
    public DateTimeOffset? LiveSince { get; set; }

    // When the owner accepted the Terms of Service to sign the theater up, and which version.
    public DateTimeOffset? TermsAcceptedAt { get; set; }
    [MaxLength(40)] public string? TermsVersion { get; set; }

    public bool IsDemo => Mode == TheaterMode.Demo;

    // Listed, and selling real tickets, to everyone. Demo theaters are only visible to their members.
    public bool IsPublic => IsActive && Mode == TheaterMode.Live;

    // When the logo was last uploaded; null when there is none. See TheaterLogo.
    public DateTimeOffset? LogoUpdatedAt { get; set; }

    // Served by the /theaters/{slug}/logo endpoint; the version keeps browsers from showing a replaced logo.
    public string? LogoUrl => LogoUpdatedAt is DateTimeOffset at ? $"theaters/{Slug}/logo?v={at.UtcTicks}" : null;

    // Nullable only so an admin can create a theater before its owner accepts an invite.
    public string? OwnerId { get; set; }
    public ApplicationUser? Owner { get; set; }

    public List<Screen> Screens { get; set; } = [];
    public List<ApplicationUser> Employees { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Detached copy of the editable fields, so a form can be cancelled without touching the original.
    public Theater CopyForEdit() => new()
    {
        Id = Id, Name = Name, Slug = Slug, IsActive = IsActive, OwnerId = OwnerId,
        AddressLine1 = AddressLine1, AddressLine2 = AddressLine2, City = City, State = State,
        PostalCode = PostalCode, Country = Country, Phone = Phone, Website = Website,
        Description = Description, TimeZone = TimeZone, SeasonOpensOn = SeasonOpensOn, SeasonClosesOn = SeasonClosesOn,
        Latitude = Latitude, Longitude = Longitude,
    };
}

public static class Seasons
{
    // e.g. "May 1, 2026 – Sep 30, 2026", "from May 1, 2026", "year-round".
    public static string Describe(Theater theater) => (theater.SeasonOpensOn, theater.SeasonClosesOn) switch
    {
        (DateOnly opens, DateOnly closes) => $"{opens:MMM d, yyyy} – {closes:MMM d, yyyy}",
        (DateOnly opens, null) => $"from {opens:MMM d, yyyy}",
        (null, DateOnly closes) => $"through {closes:MMM d, yyyy}",
        _ => "year-round",
    };

    // Whether the season is open on any day of the calendar month starting on monthStart. The plan bills these months.
    public static bool TouchesMonth(Theater theater, DateOnly monthStart) =>
        (theater.SeasonOpensOn is not DateOnly opens || opens <= monthStart.AddMonths(1).AddDays(-1))
        && (theater.SeasonClosesOn is not DateOnly closes || closes >= monthStart);
}

// Stored by name, so members can be added but not renamed.
public enum TheaterMode
{
    Demo,
    Live,
}
