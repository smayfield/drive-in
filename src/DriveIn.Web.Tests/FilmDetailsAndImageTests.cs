using DriveIn.Web.Authorization;
using DriveIn.Web.Data;
using DriveIn.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace DriveIn.Web.Tests;

public class FilmDetailsAndImageTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];
    private static readonly byte[] Gif = [.. "GIF89a"u8, 1, 2, 3];

    private static FilmInput JawsInput => new("Jaws", "PG", 124, 1975, "A giant shark terrorizes Amity Island.",
        "Steven Spielberg", "Roy Scheider, Robert Shaw, Richard Dreyfuss", "Horror, Thriller");

    // --- Film details ---

    [Fact]
    public async Task Films_keep_the_optional_details_they_are_given()
    {
        await using var app = new TestApp();
        var owner = await app.CreateUserAsync("owner@example.com");
        var theater = await app.CreateTheaterAsync("Starlight", owner.Id);
        var service = app.Get<ScheduleService>();

        var film = await service.AddFilmAsync(Principals.For(owner), theater.Id, JawsInput with { Directors = "  Steven Spielberg  ", Genres = " " });

        Assert.Equal((1975, "Steven Spielberg", null), (film.ReleaseYear, film.Directors, film.Genres));
        await service.UpdateFilmAsync(Principals.For(owner), film.Id, new FilmInput("Jaws", "PG", 124));
        await using var db = app.Db();
        var saved = await db.Films.SingleAsync();
        Assert.Equal((null, null), (saved.ReleaseYear, saved.Cast)); // the form sends every field, so blanks clear them
    }

    [Fact]
    public async Task Film_details_are_validated()
    {
        await using var app = new TestApp();
        var owner = await app.CreateUserAsync("owner@example.com");
        var theater = await app.CreateTheaterAsync("Starlight", owner.Id);
        var service = app.Get<ScheduleService>();
        var user = Principals.For(owner);

        await Assert.ThrowsAsync<AppValidationException>(() => service.AddFilmAsync(user, theater.Id, JawsInput with { ReleaseYear = 1700 }));
        await Assert.ThrowsAsync<AppValidationException>(() => service.AddFilmAsync(user, theater.Id, JawsInput with { ReleaseYear = 3000 }));
        await Assert.ThrowsAsync<AppValidationException>(() => service.AddFilmAsync(user, theater.Id, JawsInput with { Overview = new string('x', 2001) }));
        await Assert.ThrowsAsync<AppValidationException>(() => service.AddFilmAsync(user, theater.Id, JawsInput with { Cast = new string('x', 501) }));
    }

    [Fact]
    public async Task Showings_carry_the_film_details_for_display()
    {
        await using var app = new TestApp();
        var owner = await app.CreateUserAsync("owner@example.com");
        var theater = await app.CreateTheaterAsync("Starlight", owner.Id);
        var screen = await app.Get<ScreenService>().AddAsync(Principals.For(owner), theater.Id, "Main");
        var service = app.Get<ScheduleService>();
        var film = await service.AddFilmAsync(Principals.For(owner), theater.Id, JawsInput);
        await service.SetPosterAsync(Principals.For(owner), film.Id, Png);
        await service.AddShowtimeAsync(Principals.For(owner), screen.Id, film.Id, new DateOnly(2026, 9, 10), new TimeOnly(20, 0));

        var feature = (await service.ListUpcomingAsync(Principals.For(owner), theater.Id)).Single().Features.Single();

        Assert.Equal(("Steven Spielberg", 1975), (feature.Directors, feature.Year));
        Assert.StartsWith($"films/{film.Id}/poster?v=", feature.PosterUrl);
    }

    // --- Posters ---

    [Theory]
    [InlineData("png", "image/png")]
    [InlineData("jpeg", "image/jpeg")]
    [InlineData("gif", "image/gif")]
    public async Task Posters_can_be_uploaded_and_replaced(string kind, string contentType)
    {
        await using var app = new TestApp();
        var owner = await app.CreateUserAsync("owner@example.com");
        var theater = await app.CreateTheaterAsync("Starlight", owner.Id);
        var service = app.Get<ScheduleService>();
        var user = Principals.For(owner);
        var film = await service.AddFilmAsync(user, theater.Id, JawsInput);
        var bytes = kind switch { "png" => Png, "jpeg" => Jpeg, _ => Gif };

        await service.SetPosterAsync(user, film.Id, Png);
        await service.SetPosterAsync(user, film.Id, bytes); // replaces

        var poster = await service.GetPosterAsync(user, film.Id);
        Assert.Equal(contentType, poster!.ContentType);
        Assert.Equal(bytes, poster.Data);
        await using var db = app.Db();
        Assert.Equal(1, await db.FilmPosters.CountAsync());
        Assert.NotNull((await db.Films.SingleAsync()).PosterUrl);
    }

    [Fact]
    public async Task Posters_must_be_real_small_jpg_gif_or_png_images()
    {
        await using var app = new TestApp();
        var owner = await app.CreateUserAsync("owner@example.com");
        var theater = await app.CreateTheaterAsync("Starlight", owner.Id);
        var service = app.Get<ScheduleService>();
        var user = Principals.For(owner);
        var film = await service.AddFilmAsync(user, theater.Id, JawsInput);

        await Assert.ThrowsAsync<AppValidationException>(() => service.SetPosterAsync(user, film.Id, []));
        await Assert.ThrowsAsync<AppValidationException>(() => service.SetPosterAsync(user, film.Id, "<svg onload=alert(1)/>"u8.ToArray()));
        await Assert.ThrowsAsync<AppValidationException>(() => service.SetPosterAsync(user, film.Id, "GIF89a"u8.ToArray()[..3]));
        var tooBig = new byte[FilmPoster.MaxBytes + 1];
        Png.CopyTo(tooBig, 0);
        await Assert.ThrowsAsync<AppValidationException>(() => service.SetPosterAsync(user, film.Id, tooBig));
        await using var db = app.Db();
        Assert.Empty(await db.FilmPosters.ToListAsync());
    }

    [Fact]
    public async Task Changing_a_poster_needs_ManageSchedule_at_that_theater()
    {
        await using var app = new TestApp();
        var mine = await app.CreateTheaterAsync("Mine");
        var other = await app.CreateTheaterAsync("Other");
        var employee = await app.CreateUserAsync("emp@example.com", employeeTheaterId: mine.Id);
        var service = app.Get<ScheduleService>();
        await using (var db = app.Db())
        {
            db.Films.AddRange(new Film { TheaterId = mine.Id, Title = "A", RuntimeMinutes = 90 },
                new Film { TheaterId = other.Id, Title = "B", RuntimeMinutes = 90 });
            await db.SaveChangesAsync();
        }
        await using var films = app.Db();
        var a = await films.Films.SingleAsync(f => f.Title == "A");
        var b = await films.Films.SingleAsync(f => f.Title == "B");
        var user = Principals.For(employee);

        await Assert.ThrowsAsync<AccessDeniedException>(() => service.SetPosterAsync(user, a.Id, Png)); // no roles yet
        await app.GrantAsync(employee, TheaterPermissions.ManageSchedule);
        await service.SetPosterAsync(user, a.Id, Png);
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.SetPosterAsync(user, b.Id, Png));
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.RemovePosterAsync(user, b.Id));

        await service.RemovePosterAsync(user, a.Id);
        Assert.Null(await service.GetPosterAsync(user, a.Id));
        await using var check = app.Db();
        Assert.Empty(await check.FilmPosters.ToListAsync());
        Assert.Null((await check.Films.SingleAsync(f => f.Id == a.Id)).PosterUpdatedAt);
    }

    [Fact]
    public async Task A_demo_theaters_posters_are_only_served_to_its_members()
    {
        await using var app = new TestApp();
        var owner = await app.CreateUserAsync("owner@example.com");
        var stranger = await app.CreateUserAsync("stranger@example.com");
        var theater = await app.CreateTheaterAsync("Starlight", owner.Id);
        await using (var db = app.Db())
        {
            (await db.Theaters.SingleAsync()).Mode = TheaterMode.Demo;
            await db.SaveChangesAsync();
        }
        var service = app.Get<ScheduleService>();
        var film = await service.AddFilmAsync(Principals.For(owner), theater.Id, JawsInput);
        await service.SetPosterAsync(Principals.For(owner), film.Id, Png);

        Assert.NotNull(await service.GetPosterAsync(Principals.For(owner), film.Id));
        Assert.Null(await service.GetPosterAsync(Principals.For(stranger), film.Id));
    }

    [Fact]
    public async Task Deleting_a_film_or_theater_removes_its_posters()
    {
        await using var app = new TestApp();
        var admin = await app.CreateUserAsync("admin@example.com", admin: true);
        var user = Principals.For(admin, admin: true);
        var theater = await app.CreateTheaterAsync("Starlight");
        var service = app.Get<ScheduleService>();
        var one = await service.AddFilmAsync(user, theater.Id, JawsInput);
        var two = await service.AddFilmAsync(user, theater.Id, JawsInput with { Title = "Jaws 2" });
        await service.SetPosterAsync(user, one.Id, Png);
        await service.SetPosterAsync(user, two.Id, Png);

        await service.DeleteFilmAsync(user, one.Id);
        await using (var db = app.Db())
            Assert.Equal([two.Id], await db.FilmPosters.Select(p => p.FilmId).ToListAsync());

        await app.Get<TheaterService>().DeleteAsync(user, theater.Id);
        await using var check = app.Db();
        Assert.Empty(await check.FilmPosters.ToListAsync());
    }

    // --- Theater logos ---

    [Theory]
    [InlineData("png", "image/png")]
    [InlineData("jpeg", "image/jpeg")]
    [InlineData("gif", "image/gif")]
    public async Task Owners_upload_and_replace_a_logo(string kind, string contentType)
    {
        await using var app = new TestApp();
        var owner = await app.CreateUserAsync("owner@example.com");
        var theater = await app.CreateTheaterAsync("Starlight", owner.Id);
        var service = app.Get<TheaterService>();
        var user = Principals.For(owner);
        var bytes = kind switch { "png" => Png, "jpeg" => Jpeg, _ => Gif };

        await service.SetLogoAsync(user, theater.Id, Png);
        await service.SetLogoAsync(user, theater.Id, bytes); // replaces

        var logo = await service.GetLogoAsync(user, "starlight");
        Assert.Equal(contentType, logo!.ContentType);
        Assert.Equal(bytes, logo.Data);
        await using var db = app.Db();
        Assert.Equal(1, await db.TheaterLogos.CountAsync());
        Assert.NotNull((await db.Theaters.SingleAsync()).LogoUrl);
    }

    [Fact]
    public async Task Logos_must_be_real_small_jpg_gif_or_png_images()
    {
        await using var app = new TestApp();
        var owner = await app.CreateUserAsync("owner@example.com");
        var theater = await app.CreateTheaterAsync("Starlight", owner.Id);
        var service = app.Get<TheaterService>();
        var user = Principals.For(owner);

        await Assert.ThrowsAsync<AppValidationException>(() => service.SetLogoAsync(user, theater.Id, []));
        await Assert.ThrowsAsync<AppValidationException>(() => service.SetLogoAsync(user, theater.Id, "<svg onload=alert(1)/>"u8.ToArray()));
        await Assert.ThrowsAsync<AppValidationException>(() => service.SetLogoAsync(user, theater.Id, "GIF89a"u8.ToArray()[..3]));
        var tooBig = new byte[TheaterLogo.MaxBytes + 1];
        Png.CopyTo(tooBig, 0);
        await Assert.ThrowsAsync<AppValidationException>(() => service.SetLogoAsync(user, theater.Id, tooBig));
        await using var db = app.Db();
        Assert.Empty(await db.TheaterLogos.ToListAsync());
    }

    [Fact]
    public async Task Changing_a_logo_needs_EditProfile_at_that_theater()
    {
        await using var app = new TestApp();
        var mine = await app.CreateTheaterAsync("Mine");
        var other = await app.CreateTheaterAsync("Other");
        var employee = await app.CreateUserAsync("emp@example.com", employeeTheaterId: mine.Id);
        var service = app.Get<TheaterService>();
        var user = Principals.For(employee);

        await Assert.ThrowsAsync<AccessDeniedException>(() => service.SetLogoAsync(user, mine.Id, Png)); // no roles yet
        await app.GrantAsync(employee, TheaterPermissions.EditProfile);
        await service.SetLogoAsync(user, mine.Id, Png);
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.SetLogoAsync(user, other.Id, Png));
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.RemoveLogoAsync(user, other.Id));

        await service.RemoveLogoAsync(user, mine.Id);
        Assert.Null(await service.GetLogoAsync(user, "mine"));
        await using var db = app.Db();
        Assert.Empty(await db.TheaterLogos.ToListAsync());
        Assert.Null((await db.Theaters.SingleAsync(t => t.Id == mine.Id)).LogoUpdatedAt);
    }

    [Fact]
    public async Task A_demo_theaters_logo_is_only_served_to_its_members()
    {
        await using var app = new TestApp();
        var owner = await app.CreateUserAsync("owner@example.com");
        var stranger = await app.CreateUserAsync("stranger@example.com");
        var theater = await app.CreateTheaterAsync("Starlight", owner.Id);
        await using (var db = app.Db())
        {
            (await db.Theaters.SingleAsync()).Mode = TheaterMode.Demo;
            await db.SaveChangesAsync();
        }
        var service = app.Get<TheaterService>();
        await service.SetLogoAsync(Principals.For(owner), theater.Id, Png);

        Assert.NotNull(await service.GetLogoAsync(Principals.For(owner), "starlight"));
        Assert.Null(await service.GetLogoAsync(Principals.For(stranger), "starlight"));
    }

    [Fact]
    public async Task Deleting_a_theater_removes_its_logo()
    {
        await using var app = new TestApp();
        var admin = await app.CreateUserAsync("admin@example.com", admin: true);
        var theater = await app.CreateTheaterAsync("Starlight");
        var user = Principals.For(admin, admin: true);
        await app.Get<TheaterService>().SetLogoAsync(user, theater.Id, Png);

        await app.Get<TheaterService>().DeleteAsync(user, theater.Id);

        await using var db = app.Db();
        Assert.Empty(await db.TheaterLogos.ToListAsync());
    }
}
