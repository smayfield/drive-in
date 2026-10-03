using System.Globalization;
using System.Security.Cryptography;

namespace DriveIn.Web.Data;

// Object keys for public theater images in the image CDN (see PublicImagePublisher). Each starts with the theater
// ("t/{theaterId}/") and ends with a hash of the bytes, so keys can't be guessed from ids. A logo's or poster's key also
// names the upload it was made from (LogoUpdatedAt / PosterUpdatedAt ticks): a key for an older upload is stale.
public static class CdnKeys
{
    public const int MaxLength = 200;

    public static string TheaterPrefix(int theaterId) => $"t/{theaterId}/";

    public static string LogoPrefix(int theaterId, DateTimeOffset version) =>
        $"{TheaterPrefix(theaterId)}logo-{version.UtcTicks.ToString(CultureInfo.InvariantCulture)}-";

    public static string PosterPrefix(int theaterId, int filmId, DateTimeOffset version) =>
        $"{TheaterPrefix(theaterId)}poster-{filmId}-{version.UtcTicks.ToString(CultureInfo.InvariantCulture)}-";

    public static string ImagePrefix(int theaterId, int imageId) => $"{TheaterPrefix(theaterId)}image-{imageId}-";

    // prefix + 128 bits of the bytes' SHA-256 + an extension for the type.
    public static string Make(string prefix, byte[] data, string contentType) =>
        prefix + Convert.ToHexStringLower(SHA256.HashData(data))[..32] + Extension(contentType);

    public static bool IsCurrent(string? key, string prefix) => key is not null && key.StartsWith(prefix, StringComparison.Ordinal);

    private static string Extension(string contentType) => contentType switch
    {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        _ => "",
    };
}
