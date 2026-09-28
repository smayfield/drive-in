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

    // Prefilled as the intermission between features when a double feature is scheduled.
    [Range(0, Showtime.MaxIntermissionMinutes)]
    public int DefaultIntermissionMinutes { get; set; } = 15;

    public bool IsActive { get; set; } = true;

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
        Description = Description, TimeZone = TimeZone,
    };
}
