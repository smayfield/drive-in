# Copilot instructions for drive-in

Project conventions are in `CLAUDE.md` and `README.md`. The choices below are deliberate and were
verified; please don't flag them again unless the code around them changes.

## Deliberate choices (verified)

- **Interactive Blazor forms using `<form @onsubmit="...">` with `@bind` inputs** (Admin/Manage pages).
  Blazor calls `preventDefault` automatically for `@onsubmit` handlers, and the input's `change` event
  is delivered before `submit`, so pressing Enter submits the current value without a page reload.
  Verified in Chrome by typing into the admin user search and pressing Enter.
- **`dotnet restore/publish -a $TARGETARCH` in `src/DriveIn.Web/Dockerfile`.** The .NET SDK accepts
  Docker's `amd64` as an alias for `x64`; this Dockerfile builds for `linux/amd64` locally, and
  production builds `linux/arm64`. (`Dockerfile.migrate` maps to a RID explicitly because
  `dotnet ef migrations bundle -r` needs a full RID.)
- **`&[open]` nesting in `Components/Layout/ReconnectModal.razor.css`** (unchanged .NET template code).
  This is native CSS nesting, supported by all current browsers. Blazor CSS isolation scopes the
  outer rule, so the compiled bundle contains `#components-reconnect-modal[b-…] { … &[open] { … } }`,
  which resolves to `#components-reconnect-modal[b-…][open]`.
- **Antiforgery on the Identity minimal-API endpoints** (`IdentityComponentsEndpointRouteBuilderExtensions.cs`).
  Endpoints that bind `[FromForm]` parameters are validated by `app.UseAntiforgery()` (400 before the
  handler runs), so they have no manual check. Endpoints that bind no form data validate manually.
  Verified with token-less POSTs in Development and Production.
- **Forwarded headers** are trusted only from the Docker `edge` network (Caddy), configured by
  `ForwardedHeaders__KnownNetworks__0` in `deploy/docker-compose.prod.yml`. `UseAuthentication` and
  `UseAuthorization` are called explicitly after `UseForwardedHeaders` so the Google callback sees https.
- **`Error.razor` and `NotFound.razor` use `[RequireAntiforgeryToken(required: false)]`.** They are
  re-executed for failed requests of any method, including POSTs whose antiforgery token was invalid;
  without this the error page itself throws and the user gets a raw 500.
- **Services use the injected `TimeProvider`**, not `DateTimeOffset.UtcNow`. Entity property
  initializers (`= DateTimeOffset.UtcNow`) are only defaults for rows created outside the services.
