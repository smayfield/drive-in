using DriveIn.Web.Services;

namespace DriveIn.Web.Components.Shared;

// What ImagePickerDialog chose: a library image (Id/Url) with its description and inline layout, or Removed.
public sealed record ImageChoice(int? Id, string? Url, string Alt, int Width, int Height, string Size, string Align)
{
    public static readonly ImageChoice Removed = new(null, null, "", 0, 0, "", "");

    public bool IsRemoved => ReferenceEquals(this, Removed);

    // The classes that lay the image out on the page (see public.css .theater-content).
    public string ClassName => $"img-{Size} img-{Align}";

    // Reads the size and position back from an image's classes, defaulting to medium and centered.
    public static (string Size, string Align) Layout(string? className)
    {
        var classes = (className ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var size = HtmlContent.ImageSizes.FirstOrDefault(s => classes.Contains($"img-{s}")) ?? "medium";
        var align = HtmlContent.ImageAligns.FirstOrDefault(a => classes.Contains($"img-{a}")) ?? "center";
        return (size, align);
    }
}
