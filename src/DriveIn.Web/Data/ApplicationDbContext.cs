using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace DriveIn.Web.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<Theater> Theaters => Set<Theater>();
    public DbSet<Screen> Screens => Set<Screen>();
    public DbSet<Film> Films => Set<Film>();
    public DbSet<Showtime> Showtimes => Set<Showtime>();
    public DbSet<ShowtimeFeature> ShowtimeFeatures => Set<ShowtimeFeature>();
    public DbSet<PriceSchedule> PriceSchedules => Set<PriceSchedule>();
    public DbSet<PriceOption> PriceOptions => Set<PriceOption>();
    public DbSet<AddOn> AddOns => Set<AddOn>();
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<TicketAddOn> TicketAddOns => Set<TicketAddOn>();
    public DbSet<Invitation> Invitations => Set<Invitation>();
    public DbSet<TheaterRole> TheaterRoles => Set<TheaterRole>();
    public DbSet<TheaterRolePermission> TheaterRolePermissions => Set<TheaterRolePermission>();
    public DbSet<EmployeeRole> EmployeeRoles => Set<EmployeeRole>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Plain table names instead of Identity's AspNet* prefix.
        builder.Entity<IdentityRole>().ToTable("roles");
        builder.Entity<IdentityRoleClaim<string>>().ToTable("role_claims");
        builder.Entity<IdentityUserRole<string>>().ToTable("user_roles");
        builder.Entity<IdentityUserClaim<string>>().ToTable("user_claims");
        builder.Entity<IdentityUserLogin<string>>().ToTable("user_logins");
        builder.Entity<IdentityUserToken<string>>().ToTable("user_tokens");
        builder.Entity<IdentityUserPasskey<string>>().ToTable("user_passkeys");

        builder.Entity<ApplicationUser>(u =>
        {
            u.ToTable("users");
            u.Property(x => x.DisplayName).HasMaxLength(100);
            // Deleting a theater deletes its employee accounts explicitly (TheaterService);
            // Restrict here so it can never happen by accident.
            u.HasOne(x => x.EmployeeTheater)
                .WithMany(t => t.Employees)
                .HasForeignKey(x => x.EmployeeTheaterId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Theater>(t =>
        {
            t.HasIndex(x => x.Slug).IsUnique();
            t.HasOne(x => x.Owner)
                .WithMany(u => u.OwnedTheaters)
                .HasForeignKey(x => x.OwnerId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Screen>(s =>
        {
            s.HasOne(x => x.Theater)
                .WithMany(t => t.Screens)
                .HasForeignKey(x => x.TheaterId)
                .OnDelete(DeleteBehavior.Cascade);
            s.Property(x => x.LabelScheme).HasConversion<string>().HasMaxLength(20);
        });

        builder.Entity<Film>(f =>
        {
            f.HasOne(x => x.Theater)
                .WithMany()
                .HasForeignKey(x => x.TheaterId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Showtime>(s =>
        {
            s.HasIndex(x => new { x.ScreenId, x.StartsAt });
            s.HasOne(x => x.Screen)
                .WithMany(x => x.Showtimes)
                .HasForeignKey(x => x.ScreenId)
                .OnDelete(DeleteBehavior.Cascade);
            s.HasOne(x => x.PriceSchedule)
                .WithMany()
                .HasForeignKey(x => x.PriceScheduleId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<ShowtimeFeature>(f =>
        {
            f.HasKey(x => new { x.ShowtimeId, x.Position });
            f.HasOne(x => x.Showtime)
                .WithMany(s => s.Features)
                .HasForeignKey(x => x.ShowtimeId)
                .OnDelete(DeleteBehavior.Cascade);
            // ScheduleService deletes a film's (past) showtimes before the film; Restrict keeps the DB from
            // quietly turning a double feature into a single one.
            f.HasOne(x => x.Film)
                .WithMany(x => x.Features)
                .HasForeignKey(x => x.FilmId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<PriceSchedule>(p =>
        {
            p.HasIndex(x => new { x.TheaterId, x.NormalizedName }).IsUnique();
            // One default per theater.
            p.HasIndex(x => x.TheaterId).IsUnique().HasFilter("is_default").HasDatabaseName("ix_price_schedules_one_default_per_theater");
            p.Property(x => x.NormalizedName).IsRequired();
            p.HasOne(x => x.Theater)
                .WithMany()
                .HasForeignKey(x => x.TheaterId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<PriceOption>(o =>
        {
            o.Property(x => x.Price).HasPrecision(8, 2);
            o.HasOne(x => x.Schedule)
                .WithMany(s => s.Options)
                .HasForeignKey(x => x.ScheduleId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<AddOn>(a =>
        {
            a.HasIndex(x => new { x.TheaterId, x.NormalizedName }).IsUnique();
            a.Property(x => x.NormalizedName).IsRequired();
            a.Property(x => x.Amount).HasPrecision(8, 2);
            a.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            a.HasOne(x => x.Theater)
                .WithMany()
                .HasForeignKey(x => x.TheaterId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Ticket>(t =>
        {
            // First to hold a spot wins: a second hold on it fails here.
            t.HasIndex(x => new { x.ShowtimeId, x.Row, x.Spot }).IsUnique();
            t.HasIndex(x => x.Code).IsUnique();
            t.HasIndex(x => new { x.Status, x.HeldUntil });
            t.HasIndex(x => x.UserId);
            t.HasIndex(x => x.ShortCode);
            t.HasOne(x => x.SoldBy)
                .WithMany()
                .HasForeignKey(x => x.SoldById)
                .OnDelete(DeleteBehavior.SetNull);
            t.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            t.Property(x => x.Stamp).IsConcurrencyToken();
            t.Property(x => x.OptionPrice).HasPrecision(8, 2);
            t.Property(x => x.Total).HasPrecision(8, 2);
            // Sales are records: a showing with sold tickets can't be deleted (ScheduleService, ScreenService).
            t.HasOne(x => x.Showtime)
                .WithMany()
                .HasForeignKey(x => x.ShowtimeId)
                .OnDelete(DeleteBehavior.Restrict);
            t.HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<TicketAddOn>(a =>
        {
            a.HasKey(x => new { x.TicketId, x.Position });
            a.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            a.Property(x => x.Amount).HasPrecision(8, 2);
            a.Property(x => x.Effect).HasPrecision(8, 2);
            a.HasOne(x => x.Ticket)
                .WithMany(t => t.AddOns)
                .HasForeignKey(x => x.TicketId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Invitation>(i =>
        {
            i.Property(x => x.Email).HasMaxLength(256).IsRequired();
            i.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            i.HasIndex(x => x.TokenHash).IsUnique();
            i.HasOne(x => x.Theater)
                .WithMany()
                .HasForeignKey(x => x.TheaterId)
                .OnDelete(DeleteBehavior.Cascade);
            i.HasOne(x => x.InvitedBy)
                .WithMany()
                .HasForeignKey(x => x.InvitedById)
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<TheaterRole>(r =>
        {
            r.HasIndex(x => new { x.TheaterId, x.NormalizedName }).IsUnique();
            r.Property(x => x.NormalizedName).IsRequired();
            r.HasOne(x => x.Theater)
                .WithMany()
                .HasForeignKey(x => x.TheaterId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<TheaterRolePermission>(p =>
        {
            p.HasKey(x => new { x.RoleId, x.Permission });
            p.Property(x => x.Permission).HasMaxLength(64);
            p.HasOne(x => x.Role)
                .WithMany(r => r.Permissions)
                .HasForeignKey(x => x.RoleId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<EmployeeRole>(m =>
        {
            m.HasKey(x => new { x.UserId, x.RoleId });
            m.HasOne(x => x.User)
                .WithMany(u => u.EmployeeRoles)
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            m.HasOne(x => x.Role)
                .WithMany(r => r.Members)
                .HasForeignKey(x => x.RoleId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
