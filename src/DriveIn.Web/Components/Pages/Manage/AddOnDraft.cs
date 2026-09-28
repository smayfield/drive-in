using DriveIn.Web.Data;
using DriveIn.Web.Services;

namespace DriveIn.Web.Components.Pages.Manage;

// Form state for adding or editing an add-on on the pricing page.
public sealed class AddOnDraft
{
    public int Id { get; init; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public AddOnKind Kind { get; set; } = AddOnKind.Fee;
    public decimal Amount { get; set; }
    public bool IsActive { get; set; } = true;

    public static AddOnDraft From(AddOn a) => new()
    {
        Id = a.Id, Name = a.Name, Description = a.Description, Kind = a.Kind, Amount = a.Amount, IsActive = a.IsActive,
    };

    public AddOnInput ToInput() => new(Name, Description, Kind, Amount, IsActive);
}
