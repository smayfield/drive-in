using DriveIn.Web.Data;

namespace DriveIn.Web.Services;

// Showtimes are stored in UTC and entered/shown in the theater's time zone (an IANA id such as
// "America/Chicago"). A theater without a valid time zone uses UTC.
public static class TheaterTime
{
    public static bool IsValidZone(string? id) => TimeZoneInfo.TryFindSystemTimeZoneById(id?.Trim() ?? "", out _);

    public static TimeZoneInfo ZoneOf(Theater theater) =>
        TimeZoneInfo.TryFindSystemTimeZoneById(theater.TimeZone?.Trim() ?? "", out var zone) ? zone : TimeZoneInfo.Utc;

    public static DateTime ToLocal(Theater theater, DateTimeOffset utc) =>
        TimeZoneInfo.ConvertTime(utc, ZoneOf(theater)).DateTime;

    public static DateTimeOffset ToUtc(Theater theater, DateOnly date, TimeOnly time)
    {
        var zone = ZoneOf(theater);
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
            throw new AppValidationException($"{local:g} doesn't exist in {zone.Id} (the clocks skip it for daylight saving time).");
        // An ambiguous time (clocks falling back) is taken as the first occurrence, i.e. daylight time.
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}
