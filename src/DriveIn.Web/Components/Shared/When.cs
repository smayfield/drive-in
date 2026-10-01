namespace DriveIn.Web.Components.Shared;

// Short relative times for messages and notifications ("just now", "5 min ago", "3 h ago", "Sep 28"). The server
// doesn't know the viewer's time zone, so older times are dates (UTC) rather than clock times.
public static class When
{
    public static string Ago(DateTimeOffset at, DateTimeOffset now)
    {
        var age = now - at;
        if (age < TimeSpan.FromMinutes(1))
            return "just now";
        if (age < TimeSpan.FromHours(1))
            return $"{(int)age.TotalMinutes} min ago";
        if (age < TimeSpan.FromDays(1))
            return $"{(int)age.TotalHours} h ago";
        return at.Year == now.Year ? at.ToString("MMM d") : at.ToString("MMM d, yyyy");
    }
}
