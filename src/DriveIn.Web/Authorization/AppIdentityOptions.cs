using Microsoft.AspNetCore.Identity;

namespace DriveIn.Web.Authorization;

// Identity's options, shared by Program.cs and the tests' TestApp so both behave alike.
public static class AppIdentityOptions
{
    // Wrong passwords (or 2FA / recovery codes) in a row before an account is locked, and for how long. Per account,
    // so it also stops guessing spread over many addresses, which the per-IP rate limit can't.
    public const int MaxFailedSignIns = 10;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    public static void Configure(IdentityOptions options)
    {
        options.SignIn.RequireConfirmedAccount = true;
        options.User.RequireUniqueEmail = true;
        options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
        options.Lockout.AllowedForNewUsers = true;
        options.Lockout.MaxFailedAccessAttempts = MaxFailedSignIns;
        options.Lockout.DefaultLockoutTimeSpan = LockoutDuration;
    }
}
