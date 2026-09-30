# drive-in

See README.md for the full picture. Conventions worth knowing before changing code:

- **Branch + PR only.** Never commit to `main`; merging deploys to production.
- **One app**: `src/DriveIn.Web` (Blazor Web App). `Routes` is interactive by default; pages marked
  `[ExcludeFromInteractiveRouting]` stay static SSR: the Identity account pages under `Components/Account` (they need
  the HTTP response for cookies), marketing and legal pages, Invite, Error and NotFound. So don't put `@rendermode` on pages.
- **UI:** MudBlazor (MIT) for every interactive page, in `AppLayout`. Static pages use `AccountLayout` /
  `MarketingLayout` and plain CSS instead, since MudBlazor needs an interactive circuit. Light and dark follow the
  OS: `wwwroot/theme.js` sets `data-bs-theme` and a `di-scheme` cookie (so the server prerenders the right palette),
  and `MudThemeProvider` follows the system. Keep `Layout/DriveInTheme.cs`, the tokens at the top of `wwwroot/app.css`
  and `marketing.css` in step. Public pages (theaters, showings, tickets) are the flashy ones (`wwwroot/public.css`: the
  marquee header and the ticket `Stub`); manage and admin pages stay plain, dense MudBlazor. Gotchas: use
  `Class="muted"` for muted text (`Color.Secondary` is the teal accent); copy a `@for` variable into a local before using
  it inside a component's child content (it's rendered after the loop moves on); MudBlazor's `lg` breakpoint is 1280px,
  not Bootstrap's 992px. Bootstrap is still loaded for pages not yet moved to MudBlazor.
- **Authorization lives in services, not just pages.** Every `Services/*` method takes the acting
  `ClaimsPrincipal` and checks it: `Guard.RequireAdmin` for site-admin work, or
  `auth.RequireAsync(user, theater, TheaterPermissions.X)` for anything done at a theater. Pages derive
  from `AppComponentBase`, which turns `AccessDeniedException` into the AccessDenied page and
  `AppValidationException` into an alert. Pages may hide UI by permission, but the service is the check.
- **Every new theater feature must be protected by a permission (action).** Add a key to
  `Authorization/TheaterPermissions.cs` (never rename or reuse a key; they're stored in the DB), require
  it in the service, gate the UI with `TheaterAccess.GetPermissionsAsync`, and add a test. The Roles UI
  lists the catalog automatically. Decide whether an existing default role should get it:
  `DefaultTheaterRoles` only applies to new theaters, so existing theaters need a data migration if so.
- **Theater roles** are owner-defined per theater (`TheaterRole` + `TheaterRolePermission`), assigned to
  employees via `EmployeeRole`. Admins and the owner have every permission; an employee has the union of
  their roles' permissions (none by default). `TheaterAccess` resolves this from the DB on every check.
  **Anti-escalation:** a non-owner can only create/edit/delete/assign/remove roles, or delete employees,
  whose permissions are a subset of their own (`Guard.RequireWithinAuthority`).
- **Theater modes:** self-signed-up theaters start in `Demo` (private: only members see or buy, via
  `TheaterService.CanBrowse`; sales go through `DummyPaymentProcessor` and tickets are `IsTest`). Admin activation
  makes them `Live`. Anything new that's shown or sold publicly must respect `CanBrowse` / `IsPublic`.
- Owner and Employee are relationships (`Theater.OwnerId`, `ApplicationUser.EmployeeTheaterId`), not
  Identity roles. `Admin` is the only Identity role. The employee-theater claim is added by
  `AppClaimsPrincipalFactory`.
- **Data access:** interactive components must not hold a DbContext for the circuit's lifetime. Pure
  data services use `IDbContextFactory`; services that also use `UserManager` open a DI scope per call.
- **Schema:** EF Core migrations in `Data/Migrations` (snake_case via EFCore.NamingConventions). Add with
  `dotnet ef migrations add <Name> --project src/DriveIn.Web --output-dir Data/Migrations`.
  `DesignTimeDbContextFactory` is what `dotnet ef` and the migration bundle use.
- **Tests:** `dotnet test src/DriveIn.sln`. `TestApp` in `TestHelpers.cs` builds the real DI graph with EF
  InMemory, a fake email sender, and a fake clock; add service tests there.
- Local Postgres runs on port **5433** (`docker-compose.yml`); the app on http://localhost:5280.
