using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace DriveIn.Web.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<Theater> Theaters => Set<Theater>();
    public DbSet<Screen> Screens => Set<Screen>();
    public DbSet<Invitation> Invitations => Set<Invitation>();

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
    }
}
