using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DriveIn.Web.Data;

// Used by `dotnet ef` and the migration bundle instead of booting the web app, so migrations
// don't depend on app configuration. The bundle's --connection argument overrides this string;
// otherwise it falls back to ConnectionStrings__DefaultConnection, then the local dev database.
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? "Host=localhost;Port=5433;Database=drivein;Username=drivein;Password=drivein";

        // IdentityDbContext reads the schema version (v3 adds passkeys) from IdentityOptions in the
        // application service provider. Supply the same setting as Program.cs so the model matches.
        var appServices = new ServiceCollection()
            .Configure<IdentityOptions>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3)
            .BuildServiceProvider();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .UseApplicationServiceProvider(appServices)
            .Options;
        return new ApplicationDbContext(options);
    }
}
