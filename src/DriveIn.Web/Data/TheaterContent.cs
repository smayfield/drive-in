using System.ComponentModel.DataAnnotations;

namespace DriveIn.Web.Data;

// A theater's own customer-facing content (ContentService): pages in its menu (Rules, Concessions...) and posts
// (announcements and special events) in its "News & events". The body is HTML from the editor, sanitized before it's
// saved and again when it's shown (HtmlContent).
public class TheaterPage
{
    public const int MaxTitleLength = 200;
    public const int MaxSlugLength = 100;
    public const int MaxSummaryLength = 300;
    public const int MaxBodyLength = 200_000;

    public int Id { get; set; }

    public int TheaterId { get; set; }
    public Theater? Theater { get; set; }

    public PageKind Kind { get; set; }

    [Required, MaxLength(MaxTitleLength)]
    public string Title { get; set; } = "";

    // Its address under the theater: theaters/{slug}/pages/{Slug} or theaters/{slug}/news/{Slug}.
    [Required, MaxLength(MaxSlugLength)]
    public string Slug { get; set; } = "";

    // Shown on news cards and as the page's description for search engines and link previews.
    [MaxLength(MaxSummaryLength)]
    public string? Summary { get; set; }

    [Required]
    public string BodyHtml { get; set; } = "";

    // The hero image on the page and the thumbnail on news cards, from the theater's image library.
    public int? CoverImageId { get; set; }
    public TheaterImage? CoverImage { get; set; }

    // Overrides the cover image's own alt text here; empty means decorative (no description).
    [MaxLength(TheaterImage.MaxAltLength)]
    public string? CoverAlt { get; set; }

    // Null: a draft. Otherwise shown from this time (now or scheduled) until UnpublishAt, if set.
    public DateTimeOffset? PublishAt { get; set; }
    public DateTimeOffset? UnpublishAt { get; set; }

    // Pages: listed in the theater's menu, in this order.
    public bool ShowInMenu { get; set; } = true;
    public int SortOrder { get; set; }

    // Posts: pinned ones come first; an event's date and time is shown on it.
    public bool IsPinned { get; set; }
    public DateTimeOffset? EventStartsAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public string? UpdatedById { get; set; }
    public ApplicationUser? UpdatedBy { get; set; }

    public bool IsLive(DateTimeOffset now) =>
        PublishAt is DateTimeOffset from && from <= now && (UnpublishAt is not DateTimeOffset until || until > now);

    public PageStatus StatusAt(DateTimeOffset now) => Status(PublishAt, UnpublishAt, now);

    public static PageStatus Status(DateTimeOffset? publishAt, DateTimeOffset? unpublishAt, DateTimeOffset now) => publishAt switch
    {
        null => PageStatus.Draft,
        DateTimeOffset from when from > now => PageStatus.Scheduled,
        _ when unpublishAt is DateTimeOffset until && until <= now => PageStatus.Ended,
        _ => PageStatus.Live,
    };
}

// Stored by name, so members can be added but not renamed.
public enum PageKind
{
    Page,
    Post,
}

public enum PageStatus
{
    Draft,
    Scheduled,
    Live,
    Ended,
}

// An image in a theater's library, used as a cover or inline in its pages and posts. Kept as uploaded (no resizing);
// the type is taken from the file's bytes, never from what the browser claimed.
public class TheaterImage
{
    public const int MaxBytes = 5 * 1024 * 1024;
    public const int MaxPerTheater = 250;
    public const int MaxAltLength = 300;
    public const int MaxFileNameLength = 200;

    public int Id { get; set; }

    public int TheaterId { get; set; }
    public Theater? Theater { get; set; }

    [Required, MaxLength(MaxFileNameLength)]
    public string FileName { get; set; } = "";

    // The default description, used wherever the image is placed unless the page gives its own.
    [MaxLength(MaxAltLength)]
    public string? AltText { get; set; }

    [Required, MaxLength(30)]
    public string ContentType { get; set; } = "";

    public int Width { get; set; }
    public int Height { get; set; }
    public int ByteSize { get; set; }

    public byte[] Data { get; set; } = [];

    public DateTimeOffset UploadedAt { get; set; }

    public string? UploadedById { get; set; }
    public ApplicationUser? UploadedBy { get; set; }

    public string Url(string theaterSlug) => ImageUrl(theaterSlug, Id);

    public static string ImageUrl(string theaterSlug, int id) => $"theaters/{theaterSlug}/images/{id}";
}
