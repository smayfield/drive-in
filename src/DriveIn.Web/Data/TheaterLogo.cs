using System.ComponentModel.DataAnnotations;

namespace DriveIn.Web.Data;

// A theater's uploaded logo (JPEG, GIF or PNG), kept apart from Theater so listing theaters never loads the bytes.
// Theater.LogoUpdatedAt says whether one exists and doubles as the cache-busting version.
public class TheaterLogo
{
    public const int MaxBytes = 2 * 1024 * 1024;

    public int TheaterId { get; set; }
    public Theater? Theater { get; set; }

    // Set from the file's own bytes, never from what the browser claimed.
    [Required, MaxLength(30)]
    public string ContentType { get; set; } = "";

    public byte[] Data { get; set; } = [];

    // Where its public copy is in the image CDN, while the theater is public (PublicImagePublisher). It names the
    // Theater.LogoUpdatedAt version it was made from, so a replaced logo's old copy is never served.
    [MaxLength(CdnKeys.MaxLength)]
    public string? CdnKey { get; set; }
}
