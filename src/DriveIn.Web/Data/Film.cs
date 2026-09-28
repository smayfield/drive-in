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

    public List<Showtime> Showtimes { get; set; } = [];
}

// One screening of a film on a screen. Showtimes on a screen never overlap (checked by ScheduleService).
public class Showtime
{
    public int Id { get; set; }

    public int ScreenId { get; set; }
    public Screen? Screen { get; set; }

    public int FilmId { get; set; }
    public Film? Film { get; set; }

    // The theater's default schedule when null.
    public int? PriceScheduleId { get; set; }
    public PriceSchedule? PriceSchedule { get; set; }

    // UTC. Entered and shown in the theater's time zone.
    public DateTimeOffset StartsAt { get; set; }
}
