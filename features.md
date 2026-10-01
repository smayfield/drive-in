# Features and behaviors (technical reference)

What the app does, with the rules, limits, routes, permission keys and tables behind it. [README.md](README.md) covers setup,
infrastructure and deploys. **Keep this file in step with the code: any PR that adds, changes or removes a feature or behavior
updates it** (see CLAUDE.md).

Conventions used below: `Service.Method` names are in `src/DriveIn.Web/Services`; "needs `x.y`" means the service calls
`auth.RequireAsync(user, theater, TheaterPermissions.X)`; admins and the theater's owner pass every permission check.

## 1. Identity and accounts

- ASP.NET Core Identity, local accounts: email + password with **email confirmation required**; resend confirmation; forgot/reset
  password; change email (confirmed by link); change/set password. Account pages are static SSR under `Components/Account`.
- Google sign-in (`Authentication:Google:ClientId/ClientSecret`; callback `/signin-google`). Links automatically to an existing
  confirmed account with the same verified email; links manageable at Account → External logins.
- Two-factor (authenticator app, recovery codes), passkeys (add, rename, remove), download/delete personal data.
- Login uses `lockoutOnFailure: false`, so failed passwords don't lock an account out.
- `Admin` is the only Identity role. The user whose email equals `Seed:AdminEmail` is made admin at sign-in (re-granted if removed).
- Owner = `Theater.OwnerId`; Employee = `ApplicationUser.EmployeeTheaterId`, one theater per employee account. Not Identity roles.
  `AppClaimsPrincipalFactory` adds the employee-theater claim.
- Email: `Email:Provider` selects SES (production) or a console writer (local; links are logged, nothing is sent).
- Invitations (`invitations`): single-use token link, valid 7 days, only a SHA-256 of the token is stored. Invitee sets a
  password or continues with Google on the invited address. Used for employees and for theater owners without an account.

## 2. Authorization model

- Checks live in the services, not only the pages: `Guard.RequireAdmin` (site-admin work) and
  `auth.RequireAsync(user, theater, permission)` (theater work). `TheaterAccess` resolves permissions from the DB on every check.
  `AccessDeniedException` renders the AccessDenied page; `AppValidationException` shows an alert.
- Permissions are per-theater, stored by key (`theater_role_permissions`); keys are never renamed or reused. Employee permissions =
  union of their roles' (`employee_roles`); none by default.
- **Anti-escalation** (`Guard.RequireWithinAuthority`): a non-owner can only create/edit/delete/assign/remove roles, or delete
  employees, whose permissions are a subset of their own.
- Default roles for a *new* theater (`DefaultTheaterRoles`): Manager (all but `billing.*`, which stay with the owner unless granted), Operations (`theater.edit`, `screens.manage`,
  `schedule.manage`), Ticketing (`tickets.admit`, `tickets.sell`, `tickets.move`), Concessions (none). Changing defaults doesn't touch existing
  theaters; that needs a data migration.

| Key | Name | Gates |
|---|---|---|
| `theater.edit` | Edit theater profile | Profile fields, time zone, season, logo, free-admission settings |
| `screens.manage` | Manage screens | Screens and spot layouts |
| `schedule.manage` | Manage schedule | Films, posters, showings, default intermission, per-showing price schedule |
| `pricing.manage` | Manage pricing | Price schedules/options, add-ons |
| `employees.view` | View employees | Employee list, roles, pending invitations |
| `employees.invite` | Invite employees | Send, resend, revoke invitations |
| `employees.manage` | Manage employees | Password-reset emails, delete employee accounts |
| `roles.manage` | Manage roles | Role CRUD and assignment |
| `tickets.admit` | Admit guests | Gate check-in |
| `tickets.sell` | Sell tickets at the gate | Gate sales, gift-card redemption at the gate |
| `tickets.move` | Move tickets | Move a sold ticket to another available spot at the same showing (gate) |
| `comps.offer` | Offer free admission | Give or request a free ticket |
| `comps.approve` | Approve free admission | Approve/deny requests, withdraw free tickets |
| `comps.view` | View free admission log | `comp_events` log |
| `giftcards.manage` | Manage gift cards | Turn gift-card sales on/off |
| `giftcards.view` | View gift cards | Sales, balances, amount owed (last four chars of code only) |
| `reports.view` | View reports | Sales, attendance and gift card reports, CSV downloads |
| `billing.view` | View billing | The theater's subscription, issued invoices and payments (Billing tab) |
| `billing.manage` | Manage billing | Billing email, cancel the subscription (sees the subscription, not invoices, without `billing.view`) |

## 3. Theater modes and sign-up

- `Theater.Mode`: **Demo** or **Live**. Demo: private (only members see or buy, via `TheaterService.CanBrowse`); all sales use
  `DummyPaymentProcessor` regardless of `Payments:Provider`; tickets are `IsTest` (receipts say so). Anything shown or sold
  publicly must respect `CanBrowse` / `IsPublic`.
- Self-serve sign-up at `/get-started` (`OnboardingService`): any non-employee signed-in account; name, slug, location, time zone,
  1 to 4 screens; must accept Terms (version + time stored on the theater); max `Plans:MaxTheatersPerOwner` (3) per account.
  Seeds default roles, screens of 8 rows x 15 spots (rows 5 to 8 marked for large vehicles), and sample prices.
- Go-live: owner or admin requests (agreeing to Standard-plan billing); admins are emailed; admin activates or declines with a
  note at `/admin/theaters`. Activation sets Live and deletes test tickets (with their `ticket_moves`), `comp_events` and gift cards,
  and starts the theater's subscription at `Plans:PricePerScreenPerMonth` (none if that's unset), drafting the go-live month's
  invoice (see Billing). Admin-created theaters start Live without a subscription.
- Plan panel on the manage page: for a demo theater, the estimate (per screen per calendar month the season touches, every month if
  no season); for a live one, the subscription's locked-in price and a link to Billing.

## 4. Theater configuration (`/manage/{id}` and subpages)

| Route | Content |
|---|---|
| `/manage` | Theaters the user owns or works at |
| `/manage/{id}` | Overview: profile, time zone, season, logo, free-admission settings, setup checklist, plan panel |
| `/manage/{id}/screens/{screenId}` | Screen name, spot layout, large-vehicle spots |
| `/manage/{id}/lot` | Lot map |
| `/manage/{id}/schedule` | Films and showings |
| `/manage/{id}/pricing` | Price schedules, add-ons |
| `/manage/{id}/employees` | Employees and invitations |
| `/manage/{id}/roles` | Role editor (lists the permission catalog automatically) |
| `/manage/{id}/gate` | Check-in, gate sales, moving tickets |
| `/manage/{id}/comps` | Free admission |
| `/manage/{id}/giftcards` | Gift cards |
| `/manage/{id}/reports` | Sales, attendance and gift card reports |
| `/manage/{id}/billing` | Subscription, invoices, billing email, cancel (`billing.view` / `billing.manage`) |
| `/manage/{id}/billing/invoices/{invoiceId}` | One issued invoice, printable (`billing.view`) |

- **Profile:** name, unique slug (public URL), address/contact, description, IANA time zone. Times are entered/shown in that zone,
  stored in UTC.
- **Location** (`Theater.Latitude`/`Longitude`): looked up from the address (`IGeocoder`, `Geo.AddressQuery`) when a profile
  save changes the address or the theater has none, and at sign-up from the city and state; the editor can type them instead
  (both or neither, range-checked), which skips the lookup, or clear them to look them up again. A lookup that finds nothing leaves them blank without blocking
  the save, and the form warns the theater isn't on the map yet (so not in near-me searches). `TheaterGeocodingBackfill` looks up active theaters
  with an address but no coordinates once at startup.
- **Season:** optional opens/closes (either end optional). Showings must fall inside; can't be changed to exclude scheduled showings.
- **Logo** (`theater_logos`): JPG/GIF/PNG, max 2 MB, type sniffed from bytes (no SVG), stored in DB; served at
  `/theaters/{slug}/logo` to users who can browse the theater.
- **Screens:** 1 to 4 per theater (new theaters start with "Screen 1"); order sets lot-map position. Spot layout = list of rows,
  nearest the screen first, each with its own spot count, centered. Label schemes per screen: row letter + spot number (`B7`),
  row number + spot letter (`2G`), single number (`207`). Spots numbered left to right facing the screen. A screen with sales can't
  be deleted, and can't drop or relabel spots sold for upcoming showings.
- **Vehicle sizes:** two, `VehicleSize.Standard` (cars, small SUVs, minivans) and `Large` (full-size SUVs, pickups, vans), so tall
  vehicles park where they don't block the view. Each screen marks which spots take large vehicles (`Screen.LargeSpots`, an
  `integer[]` of spot keys `row * 100 + spot`); a large vehicle may only have a marked spot, a standard one may have any. The
  screen editor (`screens.manage`) marks a whole row by checkbox, single spots by clicking the preview, or presets (back half of
  rows, all, none); **Fill** marks the back half, and new rows added behind a fully marked row are marked. Marks must be inside the
  layout; saving without marks keeps the current ones minus spots the layout drops. A spot a Large ticket holds for an upcoming
  showing can't be unmarked. New sample layouts, and existing screens at migration, have the back half of their rows marked
  (`Screen.BackHalfLarge`: rows after `rowCount / 2`). Maps show an **L** on marked spots when a screen has any unmarked ones
  (`Screen.HasSizeLimits`); buyers are only asked their vehicle on such screens.
- **Lot map:** screens 1 and 2 face each other; 3 and 4 face each other at 90 degrees; drawn around the central building.
- **Films** (`films`): title, rating, runtime, year, genres, director, cast, description. Poster (`film_posters`): JPG/GIF/PNG, max
  2 MB, served at `/films/{id}/poster` to users who can browse the theater. No external movie database. A film with sales can't be deleted.
- **Showings** (`showtimes`, `showtime_features`): one ticket on one screen; 1 to 4 films back to back (double feature) with an
  intermission between them. Theater default intermission prefills new showings and can be overridden per showing; changing the
  default doesn't move existing ones. No overlap on a screen from first film's start to last film's end (intermissions included).
  A showing with sales can't be removed or moved to another screen.
- **Pricing:** `price_schedules` (named; exactly one default, "Standard" at creation) with `price_options` (e.g. "1 occupant $10").
  A showing uses the default schedule unless set otherwise. Schedules can be created as a copy. A schedule used by upcoming
  showings can't be deleted; past showings fall back to the default. `add_ons`: fees or discounts, amount in dollars or percent,
  active flag (inactive kept, not offered).
- **Employees:** invite, resend, revoke; list with roles; send password reset; delete. Role assignment needs `roles.manage`.

## 5. Buying online (`TicketSalesService`)

- **Near me** (`/theaters`): a ZIP code or city (geocoded by `TheaterService.FindPlaceAsync`), or **Use my location**
  (`wwwroot/geo.js`, browser geolocation rounded to 2 decimals), within 25/50/100 (default)/250 miles or any distance.
  `TheaterService.ListNearAsync` applies the same visibility as the list (`CanBrowse`: demo theaters only for members),
  leaves out theaters without coordinates, and sorts nearest first (haversine, `Geo.DistanceMiles`). The search is in the
  query string (`near` or `lat`+`lon`, `radius`). Without a search the list is alphabetical.
- **Geocoding** (`Geocoding:Provider`, case-insensitive; an unknown value fails at startup): `Nominatim` (OpenStreetMap, default; `NominatimGeocoder`) or `None`. Nominatim allows one
  request a second, so lookups are serialized and spaced, and results (misses too) are cached in memory for a day (up to 10,000 places); a bare
  5-digit query is looked up as a US postcode. Failures return "not found" and are logged.
- **Weather** (`WeatherService`, `WeatherLine`): the forecast over each showing, from the hour it starts through the hour it
  ends: the worst WMO condition, the temperature at start and end, the highest chance of rain, and wind when it's 25 km/h or
  more. It appears on the theater page's showing stubs, the showing page (with a "check the forecast" chip when rain is 50%+
  likely, there are thunderstorms, or wind reaches 40 km/h), and the ticket page until the car is admitted. It's only for
  showings that haven't ended, at theaters with coordinates that the user may browse, with weather turned on (otherwise
  nothing is shown, not even the "available later" note). Showings that end past the forecast range
  (`WeatherService.ForecastDays`, 16 whole UTC days from today) say when the forecast becomes available. A showing's hours
  are those it overlaps (an end on the hour doesn't take in the next). °F/mph when the theater's country is US or blank,
  otherwise °C/km/h. It loads after the first interactive render, so a slow provider never delays the page.
  `Weather:Provider` (case-insensitive; an unknown value fails at startup): `OpenMeteo` (default; `OpenMeteoForecaster`, no
  key, one request per place cached for an hour, 5 s timeout, failures logged and shown as no forecast) or `None`.
- Routes: `/theaters` (list), `/theaters/{slug}` (details, showings), `/theaters/{slug}/showings/{showtimeId}` (spot map +
  checkout), `/tickets` (My tickets), `/tickets/{code}`.
- A ticket = one spot (one car) at one showing (`tickets`; unique index on showing + spot).
- **Hold:** choosing a spot sets the ticket `Held` until `Ticket.HoldMinutes` (10) while the buyer picks option/add-ons and pays.
  First to hold wins; others get an error to pick again. A buyer holds one spot at a time (a new hold releases the old one).
  Re-holding your own spot doesn't extend it. `HoldExpiryService` releases expired holds every 10 seconds.
- **Vehicle:** on screens with size limits the buyer says what they're driving before picking; with Large, spots not marked L
  can't be picked (map and service both check). The choice is locked while holding (choose a different spot to change it) and is
  stored on the ticket (`Ticket.VehicleSize`), shown on the receipt, ticket page, My tickets and at the gate.
- **Live maps:** holds/releases/sales are published through in-process `SpotEvents` to open maps. Single-server only; multiple
  servers would need a shared bus (e.g. Postgres LISTEN/NOTIFY).
- **Payment** (`IPaymentProcessor`, `Payments.cs`): credit card only. `Payments:Provider=Dummy` approves everything without
  charging (set in `appsettings.Development.json`); unset (production) means nothing can be sold online or at the gate. Only card
  brand and last four are stored. Total $0 after discounts needs no card.
- **Ticket states** (`TicketStatus`): Held (expires), Paying (being charged; never swept, so a crash mid-charge leaves the spot
  off sale rather than risk a double sale), Pending (free-admission request; never swept), Sold. On approval: sold to buyer, receipt emailed
  (`TicketReceipt`): QR code (inline image) linking to `tickets/{code}` (random 128-bit code) and a 4-character gate code
  (no look-alike characters, e.g. `K7QM`). Receipts can be resent from My tickets.
- **Gate codes** are unique among a theater's upcoming tickets, enforced when issued (not by a DB constraint).
- **No refunds or cancellations**, including weather.
- **Test data:** demo-theater tickets have `IsTest`.

## 6. The gate (`TicketSalesService.Gate.cs`, `/manage/{id}/gate`)

- **Check in** (`tickets.admit`): by gate code or QR/`tickets/{code}` link. Shows showing, screen, spot and validity. Admits once,
  only at the issuing theater, from 3 hours before the showing starts until it ends. Lookup matches the theater's tickets from
  yesterday on and lists duplicates if a gate code collides.
- **Sell** (`tickets.sell`): choose one of today's showings (selling continues after start, until end), a spot on the live map
  (held like online), ticket option and add-ons, then charge. Card-present (`PaymentRequest.Card` null); the ticket has no buyer
  account, `SoldById` is the attendant, and the car is checked in on sale. Competes with online buyers for the same spots. The
  attendant picks the car's vehicle size, with the same large-spot rule as online.
- **Move** (`tickets.move`, `TicketSalesService.Moves.cs`): from a looked-up ticket, choose the car's vehicle size and a new spot on
  the live map, e.g. a front-row ticket bought for a car when the guest arrives in a large SUV. Works before or after check-in,
  until the showing ends; same showing only. The new spot must be available right now (not sold, held, paying or pending; an
  expired hold is fine) and fit the vehicle. The ticket keeps its codes, payment and check-in; spot, label and vehicle change.
  Each move is a `ticket_moves` row (from/to spot, label and vehicle, who, when). Moving alone lets staff look tickets up at the
  gate but not check them in. Manager and Ticketing roles get `tickets.move` by default (a migration added it to existing ones).

## 7. Free admission (`TicketSalesService.Comps.cs`, `/manage/{id}/comps`)

- Off by default. Theater settings (Overview, `theater.edit`): enabled, require approval, require reason, optional max per
  showing, optional max per employee per showing.
- `comps.offer`: reserve a spot for a named guest (and optional email, and their vehicle size) at $0; with approval required, creates a `Pending` ticket
  that holds the spot with no expiry. `comps.approve`: approve (becomes sold) or deny; withdraw unused free tickets; approvers'
  own offers skip approval. `comps.view`: the log.
- A free ticket is a normal sold ticket: `Ticket.IsComp`, $0, no buyer account, `SoldById` = giver, QR + gate code, checked in
  like any other.
- Every event (requested, given, approved, denied, withdrawn) goes to `comp_events` with people, showing and spot copied in,
  so the log survives deleted tickets and accounts.
- Only Manager gets the three keys by default; a migration added them to existing Managers.

## 8. Gift cards (`TicketSalesService.GiftCards.cs`, `GiftCardEmail`)

- `giftcards.manage` turns sales on per theater; `giftcards.view` lists sales, balances, amount owed. Only Manager gets both by
  default (migration added to existing Managers).
- Purchase at `/theaters/{slug}/giftcards`: any user who can browse the theater, $5 to $500, by card. A 16-character code
  (about 78 random bits) is emailed to the buyer and an optional recipient. Staff see only the last four characters.
  Codes are unique across all theaters (unique index on `gift_cards.code`); a new code is checked against existing ones, and
  if the save still clashes (charged by then) it retries with a fresh code rather than failing the paid purchase.
- My tickets lists the user's gift cards with their codes: ones they bought, and ones sent to their confirmed email address
  (recipient email compared case-insensitively), marked as a gift.
- Redemption: a bearer instrument, so anyone with the code can spend it (buyer, recipient, or whoever it's passed on to); the
  purchaser is never checked. At online checkout, or at the gate (needs `tickets.sell`). Must match a card sold by that theater (a wrong code and
  another theater's code give the same error). One card per ticket. It pays first up to its balance; the card/terminal is charged
  the remainder; no charge if fully covered. Remaining balance stays on the card. No expiry, refund or cash-out.
- Integrity: the balance is debited in the same save that moves the ticket to `Paying`, guarded by a concurrency stamp and a DB
  check `0 <= balance <= initial_amount`. A declined card restores the balance (`Restore` transaction). Every change is a row in
  `gift_card_transactions`. Turning sales off leaves existing cards spendable.

## 9. Reports (`ReportService`, `/manage/{id}/reports`)

- Needs `reports.view`. Manager gets it by default; a migration added it to existing Managers.
- A date range in the theater's local dates, both ends included, at most `ReportService.MaxDays` (366). Presets: today, last
  7 days, this month (the default, to date), last month, this season (when it has an opening date), this year.
- **Tickets** count by the showing's start date, sold tickets only (not Held, Paying or Pending). Cars include free tickets.
  Totals, by channel (online, at the gate, free admission), paid by card vs gift card, per day, per showing (capacity from the
  screen's *current* layout, occupancy, admitted, no-shows once the showing has ended), per film (a double feature counts
  toward each of its films, so film rows don't add up), ticket options (paid tickets) and add-ons (count and total effect).
  Attendance % = admitted / cars, over ended showings only. Demo theaters show a "test sales" notice.
- **Gift cards**, from `gift_card_transactions` by when they happened: owed at the start of the range, sold, spent (redemptions
  net of restores), owed at the end; start + sold − spent = end. Plus every card with a balance right now, oldest first:
  last four, bought, buyer, recipient, value, balance, last used. Codes are never shown.
- **CSV** at `/manage/{id}/reports/{showings|days|films|giftcards}.csv?from=yyyy-MM-dd&to=yyyy-MM-dd` (signed in; the service
  checks `reports.view`, 403 otherwise): UTF-8 with BOM, RFC 4180 quoting, invariant numbers, and text starting with
  `= + - @` (or tab/CR) prefixed with `'` so spreadsheets don't run it as a formula (`ReportCsv`).

## 10. Billing (`BillingService`, `BillingReportService`, `BillingEmails`)

- **Subscriptions** (`subscriptions`, one per theater): Standard plan, `PricePerScreenPerMonth` locked in when it starts (admins can
  change it; applies to invoices drafted afterwards, and the Terms promise 30 days' notice), `StartedOn` (theater-local date),
  Active or Canceled, optional `BillingEmail` (else the owner's email). Started by go-live activation, or by an admin at
  `/admin/billing/subscriptions` for live theaters without one (admin-created, or activated with no price configured).
- **Billed months:** screens × price for each calendar month (theater time zone) that the subscription covers and the season touches
  (`Seasons.TouchesMonth`; every month with no season), in full: no proration for the start or cancel month. Not billed while the
  theater is inactive or has no screens (`BillingService.IsBillable`). Screen count is taken when the draft is made.
- **Drafting:** `BillingJobService` runs at startup then hourly and drafts this month's and last month's invoice (catching up after
  downtime) where due and missing; admins get one email when drafts are created. Unique index on subscription + month for invoices
  that aren't void, so concurrent runs can't double-bill. Admins can run it from `/admin/billing`.
- **Invoices** (`invoices`, `invoice_lines`, `invoice_payments`): Draft → Issued → Paid, or Void.
  - Drafts: admins add adjustment or credit lines (negative price) or remove lines; owners don't see drafts.
  - Issue (one, or all drafts): assigns the next number `INV-000001` (sequential, retried on a clash; voided drafts use none), due
    `Billing:PaymentTermsDays` (15) later, refreshes the bill-to snapshot, and emails the invoice. Total can't be below $0; a $0
    invoice is issued already paid. A failed email doesn't undo the issue; the admin is told and can resend.
  - Payments are recorded by an admin (no processor yet): amount up to the balance (partial allowed), method (check, bank transfer,
    card, other), reference, date received (not in the future). Each emails a receipt; the invoice is Paid at zero balance.
  - Void: only with no payments (no refunds); emails the bill-to if it had been issued; the month can then be drafted again.
  - Invoices snapshot theater name/address and bill-to name/email. Deleting a theater deletes its subscription and drafts and keeps
    issued invoices with no theater.
  - Dates (issued, due, overdue, received) are UTC dates. Overdue = issued and past due.
- **Cancel:** the owner (`billing.manage`) or an admin. The current month is the last billed (`EndsAfterMonth`, billed if in season
  and not yet drafted); issued invoices stand. Emails the bill-to, and the admins when the owner cancels. Admin reactivation resumes
  from the current month if it had lapsed (the gap isn't billed).
- **Emails** (`BillingEmails`): invoice, receipt and void notice, with `Company:*` name/address/contact and a link to the invoice on
  the theater's Billing page.
- **Reports** (`/admin/billing/reports`): for a range of months (at most 60): active/cancelled subscriptions, billed screens,
  projected billing this and next month; invoiced (by month billed) and collected (by date received) per month; per theater
  invoiced/paid/owed; receivables aging today (not yet due, 1–30, 31–60, 61–90, over 90 days past due). CSV at
  `/admin/billing/{invoices|payments|aging}.csv?from=yyyy-MM&to=yyyy-MM` (admins only), same format as the theater reports' CSV.

## 11. Admin (`Policies.Admin`)

- `/admin/theaters`, `/admin/theaters/new`, `/admin/theaters/{id}`: list, create (owner by email: immediate if the account exists,
  else invitation), edit, review go-live requests (activate or decline with note).
- `/admin/users` (`UserAdminService`): list; create a confirmed account (optionally admin) that is emailed a link to set a
  password; make/remove admin (not on self); delete (not self). The seeded admin regains admin at sign-in.
- `/admin/billing` (invoices: filter, draft now, issue all), `/admin/billing/invoices/{id}` (lines, issue, void, record payment,
  resend, print), `/admin/billing/subscriptions` (start, price, cancel, reactivate), `/admin/billing/reports`. See Billing.

## 12. Static and marketing pages

- `[ExcludeFromInteractiveRouting]` static SSR with plain CSS (`static.css`, `marketing.css`): `/`, `/features`, `/pricing`,
  `/faq`, `/legal`, `/legal/terms`, `/legal/privacy`, `/legal/license`, `/invite/{token}`, `/Error`, `/not-found`, account pages.
- Config: `Plans:PricePerScreenPerMonth`, `Billing:PaymentTermsDays`, `Company:*` (legal name, mailing address, governing state, contact email, effective date).
  Unset values render as placeholders; legal pages show a "draft, not in effect" banner until `Company:LegalName` is set.

## 13. UI and platform

- Blazor Web App, interactive server by default (`Routes`); MudBlazor for interactive pages in `AppLayout`; no Bootstrap.
- Light/dark follow OS via `wwwroot/theme.js` (`data-theme`, `di-scheme` cookie so the server prerenders the right palette);
  palettes in `Layout/DriveInTheme.cs`, `app.css`, `marketing.css`.
- Public pages use the marquee header and ticket `Stub` (`public.css`); manage/admin pages are plain dense MudBlazor with
  `ManageHeader` / `AdminHeader`.
- Data: PostgreSQL via EF Core, snake_case, migrations in `Data/Migrations`. Interactive components don't hold a DbContext;
  data services use `IDbContextFactory`, services that use `UserManager` open a DI scope per call.
- Deploy: merge to `main` runs tests, builds ARM64 images, runs an EF migration bundle, then deploys via SSM; Caddy fronts the app.
  Nightly `pg_dump` (30 days) plus daily EBS snapshots (7). Metrics and alerts: see section 14.

## 14. Metrics and alerts (`deploy/grafana`, `deploy/victoriametrics`)

- **Grafana** at `/grafana/` (`https://drive-in.online/grafana/`, linked as Metrics on `AdminHeader`), **site admins only**.
  Caddy's `forward_auth` asks `/ops/grafana-auth` (`GrafanaAuth.Check`) before every Grafana request: an admin gets 200 with
  their lowercased email (else user id) in `X-WEBAUTH-USER`, which Grafana's auth proxy signs them in as (auto sign-up, org
  Admin; the seeded admin is also Grafana server admin); signed out redirects to `/Account/Login?ReturnUrl=` (only ever back into
  `/grafana/`); anyone else gets 403. Caddy drops a client-sent `X-WEBAUTH-USER`, and Grafana only trusts the header from
  Caddy's fixed IP (172.30.0.10). No login form, basic auth or anonymous access; Grafana publishes no port.
- **VictoriaMetrics** (13 months) scrapes node-exporter (host CPU, memory, swap, disk, PSI), Caddy (`:2020`, request
  counts, latency, status codes), postgres-exporter and itself every 30 s (`deploy/victoriametrics/scrape.yml`). `backup.sh`
  pushes `drivein_backup_last_success_timestamp_seconds` after each nightly backup.
- **Business data** comes from SQL: the "Drive-In DB" data source connects as `grafana_ro` (`deploy/grafana-ro.sql`, re-run on
  every deploy): read-only sessions, 30 s statement timeout, `pg_monitor`, and column-level SELECT on every table except
  `user_claims`, `user_logins`, `user_passkeys` and `user_tokens`, leaving out bearer codes (`code`, `short_code`), hashes,
  `security_stamp`, image bytes (`data`), `payment_reference` and any `*email*` column; on `users` only `id`, `created_at`,
  `email_confirmed`, `employee_theater_id`, `lockout_end` and `two_factor_enabled`.
- **The app's metrics** go over OTLP (http/protobuf, every 15 s) to VictoriaMetrics when `Metrics:OtlpEndpoint` is set
  (production compose; locally `appsettings.Development.json` points at the `monitoring` compose profile). Built-in meters:
  ASP.NET Core hosting (requests by route and status), Kestrel, SignalR, Blazor circuits, diagnostics (unhandled exceptions),
  Identity, authentication/authorization, `System.Runtime`, `System.Net.Http` (SES, Nominatim, Open-Meteo), Npgsql and EF Core.
- **`DriveInMetrics`** (meter `DriveIn`; Prometheus names add `_total`): counters recorded after the change is saved, with
  low-cardinality tags only (never a theater, user or showing id):
  `drivein.users.registered{method=password|google|invite|admin}`, `drivein.theaters.signed_up`,
  `drivein.theaters.go_live_requested`, `drivein.theaters.activated{how=go_live|admin_created}`,
  `drivein.tickets.sold` and `drivein.tickets.revenue` (dollars) `{channel=online|gate|comp, test}`,
  `drivein.tickets.admitted{how=scan|sold_at_gate}`, `drivein.tickets.moved`, `drivein.holds.expired`,
  `drivein.payments{for=ticket|gift_card, result=approved|declined|error, test}`, `drivein.gift_cards.sold` and
  `drivein.gift_cards.revenue {test}`, `drivein.emails{result=sent|failed}` (every sender is wrapped in `MeteredEmailSender`),
  `drivein.jobs.failures{job=hold_expiry|billing|geocoding|business_gauges}`, `drivein.invoices.issued`,
  `drivein.invoices.payments` (dollars), and `drivein.errors.logged{category, level}` (every Error/Critical log message,
  `ErrorCountingLoggerProvider`: failures inside Blazor circuits never become 5xx responses).
- **`BusinessGauges`** (hosted service) reads totals every minute and reports them as gauges: `drivein.users{kind=customer|employee}`,
  `drivein.theaters{mode}` (active), `drivein.screens.live`, `drivein.showings.upcoming` (next 7 days, live theaters),
  `drivein.theaters.go_live_pending`, `drivein.free_admission.pending`, `drivein.invoices.outstanding` (dollars). Nothing is
  reported before the first read.
- Everything in Grafana is provisioned from the repo (data sources, dashboards in a read-only "Drive-In" folder, alert rules,
  contact point, notification policy); UI edits aren't kept. Dashboards:
  - **Drive-In: Business**: totals (customers, new accounts, live/demo theaters, go-live requests, live screens, tickets, revenue,
    cars admitted, gift cards, invoiced, owed) and daily trends from SQL (real sales only; test tickets and gift cards left out),
    top theaters and pending go-lives, plus live activity from the counters (sales, payments, sign-ups, abandoned holds, open
    sessions, emails).
  - **Drive-In: Site performance**: requests, 5xx, latency (p50/95/99, leaving out the Blazor circuit's connection), busiest and
    slowest routes, errors logged by category, unhandled exceptions, job failures, payments, emails, circuits and connections,
    sign-ins, outbound calls, database time and pool, EF Core, and the .NET runtime (memory, CPU, GC, thread pool).
  - **Drive-In: Server**: host, edge, PostgreSQL, backups, monitoring targets.
- **Alerts** email through the `drive-in-alerts` SNS topic (`infra/app.yml`, `AlertEmail`), which Grafana publishes to with the
  instance role. Grafana rules (folder Drive-In, group Server): disk over 80% (10 min), memory available under 10% (10 min),
  swap over 1 GB (15 min), Caddy 502/503/504 above 0.02/s (5 min), PostgreSQL down (3 min), a scrape target down (10 min),
  last backup over 26 h old. Group App: the app stopped reporting (5 min), 5xx over 5% of at least 20 requests in 10 min,
  p95 over 2 s (10 min), more than 10 errors logged in 5 min, any critical error, any email failure (15 min), any background
  job failure (15 min), any payment processor error (15 min), and a go-live request waiting over 24 h (SQL). Repeats every
  12 h while firing. CloudWatch alarms on the same topic cover what Grafana can't see
  from the box: EC2 system status (also auto-recovers the instance), instance status, CPU over 90% for 15 min, and any
  surplus CPU credits charged (t4g "unlimited" billing).

## 15. Not built

- A real payment processor (production can't sell until `Payments:Provider` is set to one).
- Concessions ordering, announcements (the Concessions role has no permissions yet).
- Paying invoices online (payments are recorded by an admin), sales tax on invoices, and overdue reminders or suspension for
  non-payment.
- Multi-server deployment (in-process `SpotEvents`).
