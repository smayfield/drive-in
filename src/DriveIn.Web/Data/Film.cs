using System.ComponentModel.DataAnnotations;

namespace DriveIn.Web.Data;

// A film in a theater's catalog, scheduled onto its screens as showtimes.
public class Film
{
    public const int MaxRuntimeMinutes = 600;

    public int Id { get; set; }

    public int TheaterId { get; set; }
    public Theater? Theater { get; set; }

    [Required, MaxLength(200)]
    public string Title { get; set; } = "";

    // e.g. "PG-13".
    [MaxLength(10)]
    public string? Rating { get; set; }

    [Range(1, MaxRuntimeMinutes)]
    public int RuntimeMinutes { get; set; }

    public List<ShowtimeFeature> Features { get; set; } = [];
}

// One ticketed showing on a screen: one film, or several back to back (a double feature) with an intermission
// between them. Showings on a screen never overlap, intermissions included (checked by ScheduleService).
public class Showtime
{
    public const int MaxFeatures = 4;
    public const int MaxIntermissionMinutes = 120;

    public int Id { get; set; }

    public int ScreenId { get; set; }
    public Screen? Screen { get; set; }

    // The films in the order they're shown.
    public List<ShowtimeFeature> Features { get; set; } = [];

    // Between consecutive features. Set from the theater's default when scheduled; can be overridden.
    public int IntermissionMinutes { get; set; }

    // The theater's default schedule when null.
    public int? PriceScheduleId { get; set; }
    public PriceSchedule? PriceSchedule { get; set; }

    // UTC. Entered and shown in the theater's time zone.
    public DateTimeOffset StartsAt { get; set; }

    // UTC. When the last feature ends: stored so overlaps can be checked in the database. Kept up to date by
    // ScheduleService whenever the features, intermission or a film's runtime change (see ComputeEndsAt).
    public DateTimeOffset EndsAt { get; set; }

    public static DateTimeOffset ComputeEndsAt(DateTimeOffset startsAt, IReadOnlyList<int> runtimes, int intermissionMinutes) =>
        startsAt.AddMinutes(runtimes.Sum() + intermissionMinutes * Math.Max(0, runtimes.Count - 1));

    // Start of each feature, in order.
    public static IEnumerable<DateTimeOffset> FeatureStarts(DateTimeOffset startsAt, IReadOnlyList<int> runtimes, int intermissionMinutes)
    {
        var at = startsAt;
        foreach (var runtime in runtimes)
        {
            yield return at;
            at = at.AddMinutes(runtime + intermissionMinutes);
        }
    }
}

public class ShowtimeFeature
{
    public int ShowtimeId { get; set; }
    public Showtime? Showtime { get; set; }

    // 1 for the first film shown.
    public int Position { get; set; }

    public int FilmId { get; set; }
    public Film? Film { get; set; }
}
