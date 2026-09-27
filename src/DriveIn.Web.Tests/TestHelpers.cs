using System.Security.Claims;
using System.Text.RegularExpressions;
using DriveIn.Web.Authorization;
using DriveIn.Web.Data;
using DriveIn.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DriveIn.Web.Tests;

public sealed class FakeEmailSender : IAppEmailSender
{
    public List<(string To, string Subject, string Body)> Sent { get; } = [];

    public Task SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default)
    {
        Sent.Add((to, subject, htmlBody));
        return Task.CompletedTask;
    }

    // Pulls the invite token out of the most recent invitation email.
    public string LastInviteToken()
    {
        var body = Sent.Last(m => m.Subject.Contains("invited")).Body;
        return Regex.Match(body, @"/invite/([A-Za-z0-9_\-%]+)").Groups[1].Value;
    }
}

// A real DI container (Identity + EF InMemory) per test, mirroring Program.cs registrations.
public sealed class TestApp : IAsyncDisposable
{
    public const string BaseUri = "https://drive-in.test/";

    public ServiceProvider Services { get; }
    public FakeEmailSender Email { get; } = new();
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero));

    public TestApp(string? adminEmail = null)
    {
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Seed:AdminEmail"] = adminEmail })
            .Build());
        services.AddDbContextFactory<ApplicationDbContext>(o => o
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)), ServiceLifetime.Scoped);
        services.AddIdentityCore<ApplicationUser>(o =>
            {
                o.SignIn.RequireConfirmedAccount = true;
                o.User.RequireUniqueEmail = true;
                o.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
            })
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<ApplicationDbContext>()
            .AddClaimsPrincipalFactory<AppClaimsPrincipalFactory>()
            .AddDefaultTokenProviders();
        services.AddAuthorizationCore();
        services.AddSingleton<IAuthorizationHandler, TheaterAuthorizationHandler>();
        services.AddSingleton<IAppEmailSender>(Email);
        services.AddSingleton<IEmailSender<ApplicationUser>, IdentityEmailSender>();
        services.AddSingleton<TimeProvider>(Time);
        services.AddScoped<TheaterService>();
        services.AddScoped<ScreenService>();
        services.AddScoped<InvitationService>();
        services.AddScoped<EmployeeService>();
        services.AddScoped<UserAdminService>();
        Services = services.BuildServiceProvider();
        DbSeeder.SeedAsync(Services).GetAwaiter().GetResult();
    }

    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    public ApplicationDbContext Db() => Get<IDbContextFactory<ApplicationDbContext>>().CreateDbContext();

    public async Task<ApplicationUser> CreateUserAsync(string email, int? employeeTheaterId = null, bool admin = false)
    {
        await using var scope = Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, EmployeeTheaterId = employeeTheaterId };
        var result = await users.CreateAsync(user, "Password123!");
        Assert.True(result.Succeeded, string.Join(" ", result.Errors.Select(e => e.Description)));
        if (admin)
            await users.AddToRoleAsync(user, Roles.Admin);
        return user;
    }

    public async Task<Theater> CreateTheaterAsync(string name, string? ownerId = null)
    {
        await using var db = Db();
        var theater = new Theater { Name = name, Slug = name.ToLowerInvariant().Replace(' ', '-'), OwnerId = ownerId };
        db.Theaters.Add(theater);
        await db.SaveChangesAsync();
        return theater;
    }

    public async ValueTask DisposeAsync() => await Services.DisposeAsync();
}

public static class Principals
{
    public static ClaimsPrincipal For(ApplicationUser user, bool admin = false) =>
        Create(user.Id, admin, user.EmployeeTheaterId);

    public static ClaimsPrincipal Create(string userId, bool admin = false, int? employeeTheaterId = null)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        if (admin)
            claims.Add(new Claim(ClaimTypes.Role, Roles.Admin));
        if (employeeTheaterId is int id)
            claims.Add(new Claim(AppClaims.EmployeeTheater, id.ToString()));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    public static ClaimsPrincipal Anonymous => new(new ClaimsIdentity());
}
