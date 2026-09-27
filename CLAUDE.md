# drive-in

See README.md for the full picture. Conventions worth knowing before changing code:

- **Branch + PR only.** Never commit to `main`; merging deploys to production.
- **One app**: `src/DriveIn.Web` (Blazor Web App). Identity account pages under `Components/Account`
  are static SSR (they need the HTTP response for cookies); app pages opt into
  `@rendermode InteractiveServer`.
- **Authorization lives in services, not just pages.** Every `Services/*` method takes the acting
  `ClaimsPrincipal` and checks it (`Guard.RequireAdmin`, or `IAuthorizationService` with
  `TheaterOperations.Operate` / `ManageEmployees` from `Authorization/TheaterAuthorization.cs`).
  Keep that when adding operations; pages derive from `AppComponentBase`, which turns
  `AccessDeniedException` into the AccessDenied page and `AppValidationException` into an alert.
- Owner and Employee are relationships (`Theater.OwnerId`, `ApplicationUser.EmployeeTheaterId`), not
  roles. `Admin` is the only role. The employee-theater claim is added by `AppClaimsPrincipalFactory`.
- **Data access:** interactive components must not hold a DbContext for the circuit's lifetime. Pure
  data services use `IDbContextFactory`; services that also use `UserManager` open a DI scope per call.
- **Schema:** EF Core migrations in `Data/Migrations` (snake_case via EFCore.NamingConventions). Add with
  `dotnet ef migrations add <Name> --project src/DriveIn.Web --output-dir Data/Migrations`.
  `DesignTimeDbContextFactory` is what `dotnet ef` and the migration bundle use.
- **Tests:** `dotnet test src/DriveIn.sln`. `TestApp` in `TestHelpers.cs` builds the real DI graph with EF
  InMemory, a fake email sender, and a fake clock; add service tests there.
- Local Postgres runs on port **5433** (`docker-compose.yml`); the app on http://localhost:5280.
