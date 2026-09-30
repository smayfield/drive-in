using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace DriveIn.Web.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<Theater> Theaters => Set<Theater>();
    public DbSet<TheaterLogo> TheaterLogos => Set<TheaterLogo>();
    public DbSet<Screen> Screens => Set<Screen>();
    public DbSet<Film> Films => Set<Film>();
    public DbSet<FilmPoster> FilmPosters => Set<FilmPoster>();
    public DbSet<Showtime> Showtimes => Set<Showtime>();
    public DbSet<ShowtimeFeature> ShowtimeFeatures => Set<ShowtimeFeature>();
    public DbSet<PriceSchedule> PriceSchedules => Set<PriceSchedule>();
    public DbSet<PriceOption> PriceOptions => Set<PriceOption>();
    public DbSet<AddOn> AddOns => Set<AddOn>();
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<TicketAddOn> TicketAddOns => Set<TicketAddOn>();
    public DbSet<TicketMove> TicketMoves => Set<TicketMove>();
    public DbSet<CompEvent> CompEvents => Set<CompEvent>();
    public DbSet<GiftCard> GiftCards => Set<GiftCard>();
    public DbSet<GiftCardTransaction> GiftCardTransactions => Set<GiftCardTransaction>();
    public DbSet<Invitation> Invitations => Set<Invitation>();
    public DbSet<TheaterRole> TheaterRoles => Set<TheaterRole>();
    public DbSet<TheaterRolePermission> TheaterRolePermissions => Set<TheaterRolePermission>();
    public DbSet<EmployeeRole> EmployeeRoles => Set<EmployeeRole>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvoiceLine> InvoiceLines => Set<InvoiceLine>();
    public DbSet<InvoicePayment> InvoicePayments => Set<InvoicePayment>();

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
            t.Property(x => x.Mode).HasConversion<string>().HasMaxLength(20);
            t.HasOne(x => x.Owner)
                .WithMany(u => u.OwnedTheaters)
                .HasForeignKey(x => x.OwnerId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<TheaterLogo>(l =>
        {
            l.HasKey(x => x.TheaterId);
            l.HasOne(x => x.Theater)
                .WithOne()
                .HasForeignKey<TheaterLogo>(x => x.TheaterId)
                .OnDelete(DeleteBehavior.Cascade);
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

        builder.Entity<FilmPoster>(p =>
        {
            p.HasKey(x => x.FilmId);
            p.HasOne(x => x.Film)
                .WithOne()
                .HasForeignKey<FilmPoster>(x => x.FilmId)
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
            t.Property(x => x.VehicleSize).HasConversion<string>().HasMaxLength(20);
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
            t.Property(x => x.GiftCardAmount).HasPrecision(8, 2);
            t.HasOne(x => x.GiftCard)
                .WithMany()
                .HasForeignKey(x => x.GiftCardId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<TicketMove>(m =>
        {
            m.HasIndex(x => x.TicketId);
            m.Property(x => x.FromVehicleSize).HasConversion<string>().HasMaxLength(20);
            m.Property(x => x.ToVehicleSize).HasConversion<string>().HasMaxLength(20);
            m.HasOne(x => x.Ticket)
                .WithMany()
                .HasForeignKey(x => x.TicketId)
                .OnDelete(DeleteBehavior.Cascade);
            m.HasOne(x => x.MovedBy)
                .WithMany()
                .HasForeignKey(x => x.MovedById)
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<GiftCard>(g =>
        {
            g.HasIndex(x => x.Code).IsUnique();
            g.HasIndex(x => x.TheaterId);
            g.HasIndex(x => x.PurchaserId);
            g.Property(x => x.InitialAmount).HasPrecision(8, 2);
            g.Property(x => x.Balance).HasPrecision(8, 2);
            g.Property(x => x.Stamp).IsConcurrencyToken();
            g.ToTable(t => t.HasCheckConstraint("ck_gift_cards_balance", "balance >= 0 AND balance <= initial_amount"));
            g.HasOne(x => x.Theater)
                .WithMany()
                .HasForeignKey(x => x.TheaterId)
                .OnDelete(DeleteBehavior.Cascade);
            g.HasOne(x => x.Purchaser)
                .WithMany()
                .HasForeignKey(x => x.PurchaserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<GiftCardTransaction>(x =>
        {
            x.Property(y => y.Kind).HasConversion<string>().HasMaxLength(20);
            x.Property(y => y.Amount).HasPrecision(8, 2);
            x.Property(y => y.BalanceAfter).HasPrecision(8, 2);
            x.HasIndex(y => y.GiftCardId);
            x.HasOne(y => y.GiftCard)
                .WithMany(g => g.Transactions)
                .HasForeignKey(y => y.GiftCardId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<CompEvent>(e =>
        {
            e.Property(x => x.Action).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => new { x.TheaterId, x.At });
            e.HasOne(x => x.Theater)
                .WithMany()
                .HasForeignKey(x => x.TheaterId)
                .OnDelete(DeleteBehavior.Cascade);
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

        builder.Entity<Subscription>(s =>
        {
            s.HasIndex(x => x.TheaterId).IsUnique();
            s.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            s.Property(x => x.PricePerScreenPerMonth).HasPrecision(10, 2);
            // Deleting a theater ends its subscription; its issued invoices are kept (Invoice.SubscriptionId goes null).
            s.HasOne(x => x.Theater)
                .WithMany()
                .HasForeignKey(x => x.TheaterId)
                .OnDelete(DeleteBehavior.Cascade);
            s.HasOne(x => x.CanceledBy)
                .WithMany()
                .HasForeignKey(x => x.CanceledById)
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<Invoice>(i =>
        {
            i.HasIndex(x => x.Number).IsUnique();
            // One live invoice per subscription and month; a voided one can be replaced.
            i.HasIndex(x => new { x.SubscriptionId, x.PeriodMonth }).IsUnique()
                .HasFilter("status <> 'Void'").HasDatabaseName("ix_invoices_one_per_subscription_month");
            i.HasIndex(x => new { x.Status, x.PeriodMonth });
            i.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            i.Property(x => x.Total).HasPrecision(10, 2);
            // Invoices are records: they outlive the theater and its subscription.
            i.HasOne(x => x.Subscription)
                .WithMany(s => s.Invoices)
                .HasForeignKey(x => x.SubscriptionId)
                .OnDelete(DeleteBehavior.SetNull);
            i.HasOne(x => x.Theater)
                .WithMany()
                .HasForeignKey(x => x.TheaterId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<InvoiceLine>(l =>
        {
            l.Property(x => x.UnitPrice).HasPrecision(10, 2);
            l.Property(x => x.Amount).HasPrecision(10, 2);
            l.HasOne(x => x.Invoice)
                .WithMany(i => i.Lines)
                .HasForeignKey(x => x.InvoiceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<InvoicePayment>(p =>
        {
            p.Property(x => x.Amount).HasPrecision(10, 2);
            p.Property(x => x.Method).HasConversion<string>().HasMaxLength(20);
            p.HasIndex(x => x.ReceivedOn);
            p.HasOne(x => x.Invoice)
                .WithMany(i => i.Payments)
                .HasForeignKey(x => x.InvoiceId)
                .OnDelete(DeleteBehavior.Cascade);
            p.HasOne(x => x.RecordedBy)
                .WithMany()
                .HasForeignKey(x => x.RecordedById)
                .OnDelete(DeleteBehavior.SetNull);
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
