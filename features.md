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
- **Lockout** (`AppIdentityOptions`, shared by `Program.cs` and the tests): 10 wrong passwords (or 2FA / recovery codes) in a row
  lock the account for 15 minutes (`lockoutOnFailure: true`); a successful sign-in resets the count. A locked account can't sign
  in by any method (password, passkey, Google) and is sent to `/Account/Lockout`, which says how long and links to Forgot password:
  resetting the password lifts a lockout from wrong passwords, but not an admin's lock (`LockoutEnd` = max), which never ends.
- **Rate limits** (`Services/RateLimiting.cs`, config section `RateLimits`, each rule `PermitLimit` per `WindowSeconds` in a
  fixed window). Per signed-in user, or per client IP when signed out (an IPv6 address by its /64), except where noted:
  - Endpoint limits (ASP.NET Core's limiter; `UseRateLimiter` after forwarded headers and authentication): `Account`, 10 a
    minute, for form posts to every account page (`[EnableRateLimiting]` in `Components/Account/Pages/_Imports.razor`, and on
    `Invite`) and the `/Account/PerformExternalLogin` and passkey options endpoints. One budget across those pages; viewing a
    page (GET/HEAD) never counts. Over it: `429` with `Retry-After` and a small "Too many attempts" page.
  - Circuit actions (`ActionRateLimiter`, a singleton the services call, since endpoint limits never see what happens over a
    circuit's WebSocket; in memory on the app's clock, so one server, reset on restart; signed-out callers share one budget).
    Over a limit the service throws `AppValidationException` ("Too many attempts. Please wait ... and try again."):
    `Messages` 10 a minute (new conversations and replies), `PlaceSearch` 20 a minute (`FindPlaceAsync`; the theater list is
    a static page, so it passes the request's own partition, `HttpRateLimiting.PartitionKey`, and signed-out visitors are
    limited per IP rather than together), and, counting only
    codes that match nothing, `GiftCardMissesPerUser` 10 per 10 minutes and `GiftCardMissesPerTheater` 100 an hour across
    everyone at a theater (checkout and gate gift card checks; past either, no code is looked up) and `GateCodeMisses` 30 a
    minute (`FindAtGateAsync`; real scans never count).
  - Every refusal counts `drivein.rate_limited{policy}` (section 14).
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
- Default roles for a *new* theater (`DefaultTheaterRoles`): Manager (all but `billing.*`, which stay with the owner unless granted;
  migration `AddMessaging` gave existing Manager roles `messages.*`), Operations (`theater.edit`, `screens.manage`,
  `schedule.manage`, `content.manage`; migration `AddTheaterContent` gave existing Manager and Operations roles `content.manage`), Ticketing (`tickets.admit`, `tickets.sell`, `tickets.move`), Concessions (none). Changing defaults doesn't touch existing
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
| `payouts.manage` | Manage payouts | Set up and check the theater's payout account (Payouts tab). Like billing, no default role gets it |
| `messages.view` | View messages | The theater's inbox: read customers' conversations, be notified of new ones |
| `messages.reply` | Reply to messages | Reply, close and reopen conversations (needs `messages.view` to see them) |
| `content.manage` | Manage pages and posts | The theater's own pages and posts (write, publish, schedule, delete, preview drafts) and its image library |

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
| `/manage/{id}/gate/offline` | Offline check-in (static page + JS, installable; `tickets.admit`) |
| `/manage/{id}/comps` | Free admission |
| `/manage/{id}/giftcards` | Gift cards |
| `/manage/{id}/reports` | Sales, attendance and gift card reports |
| `/manage/{id}/billing` | Subscription, invoices, billing email, cancel (`billing.view` / `billing.manage`) |
| `/manage/{id}/billing/invoices/{invoiceId}` | One issued invoice, printable (`billing.view`) |
| `/manage/{id}/payouts` | The theater's payout account: set up (the processor's hosted onboarding), status (`payouts.manage`) |

- **Profile:** name, unique slug (public URL), address/contact, description, IANA time zone. Times are entered/shown in that zone,
  stored in UTC.
- **Location** (`Theater.Latitude`/`Longitude`): looked up from the address (`IGeocoder`, `Geo.AddressQuery`) when a profile
  save changes the address or the theater has none, and at sign-up from the city and state; the editor can type them instead
  (both or neither, range-checked), which skips the lookup, or clear them to look them up again. A lookup that finds nothing leaves them blank without blocking
  the save, and the form warns the theater isn't on the map yet (so not in near-me searches). `TheaterGeocodingBackfill` looks up active theaters
  with an address but no coordinates once at startup.
- **Season:** optional opens/closes (either end optional). Showings must fall inside; can't be changed to exclude scheduled showings.
- **Logo** (`theater_logos`): JPG/GIF/PNG, max 2 MB, type sniffed from bytes (no SVG), stored in DB; served at
  `/theaters/{slug}/logo` to anyone who can browse the theater (signed in or not).
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
  2 MB, served at `/films/{id}/poster` to anyone who can browse the theater (signed in or not). No external movie database. A film with sales can't be deleted.
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

- **Public theater pages**: `/theaters/{slug}` (showings, news, the theater's menu) and its pages and posts (section 16) need no
  sign-in and are statically rendered (section 12); `CanBrowse` still hides demo and inactive theaters from everyone but their
  members, and an unknown or hidden theater is a 404. Signed-out visitors see "Sign in to buy" on showings. The theater list
  and near-me search are public too (below). The showing (spot map) and gift-card pages, and buying, need sign-in.
- **Theater list and near me** (`/theaters`, public, static SSR in `PublicLayout`; "Theaters" is in every top bar and "Find a
  theater" in the marketing nav for signed-out visitors): signed-out visitors see live theaters, members also see their demo
  ones (`ListActiveAsync`, the `CanBrowse` rule as a query). The search is a plain GET form, so it works without JavaScript:
  a ZIP code or city (`near`, geocoded by `TheaterService.FindPlaceAsync`, open to anyone) within 25/50/100 (default)/250
  miles or any distance (`radius`). **Use my location** (`wwwroot/geo.js`) ships hidden and is shown by the script when the
  browser has geolocation; it asks for the position and opens `?lat=&lon=&radius=` (rounded to 2 decimals, about a
  kilometer, so the exact spot stays out of the URL), or explains a refusal inline. `TheaterService.ListNearAsync` applies the
  same visibility, leaves out theaters without coordinates, and sorts nearest first (haversine, `Geo.DistanceMiles`). Without
  a search the list is alphabetical.
- **Geocoding** (`Geocoding:Provider`, case-insensitive; an unknown value fails at startup): `Nominatim` (OpenStreetMap, default; `NominatimGeocoder`) or `None`. Nominatim allows one
  request a second, so lookups are serialized and spaced, and results (misses too) are cached in memory for a day (up to 10,000 places); a bare
  5-digit query is looked up as a US postcode. Since anyone can search, a lookup that can't get its turn within 10 s
  (`QueueTimeout`) gives up as "not found" (not cached) rather than queue. Failures return "not found" and are logged.
  Searches are also limited per signed-in user or per client IP (`PlaceSearch`, section 1).
- **Weather** (`WeatherService`, `WeatherLine`): the forecast over each showing, from the hour it starts through the hour it
  ends: the worst WMO condition, the temperature at start and end, the highest chance of rain, and wind when it's 25 km/h or
  more. It appears on the theater page's showing stubs, the showing page (with a "check the forecast" chip when rain is 50%+
  likely, there are thunderstorms, or wind reaches 40 km/h), and the ticket page until the car is admitted. It's only for
  showings that haven't ended, at theaters with coordinates that the user may browse, with weather turned on (otherwise
  nothing is shown, not even the "available later" note). Showings that end past the forecast range
  (`WeatherService.ForecastDays`, 16 whole UTC days from today) say when the forecast becomes available. A showing's hours
  are those it overlaps (an end on the hour doesn't take in the next). °F/mph when the theater's country is US or blank,
  otherwise °C/km/h. A slow provider never delays the page: on the (static) theater page each stub's line is streamed in
  (`StreamedWeatherLine`, `[StreamRendering]`) once the forecast arrives, and a failing forecast just leaves it out; the
  interactive showing and ticket pages load it after their first render.
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
  servers would need a shared bus (e.g. Postgres LISTEN/NOTIFY). During a deploy's minute of overlap, a map open on the old
  copy misses changes made through the new one until it's refreshed; the database still allows one hold per spot.
- **Accessible seat maps** (`SeatMap` / `LotMap`, `lot-map.js`; online checkout, the gate's sale and move, free admission, and the
  screen page's large-vehicle marking):
  - An interactive map is a labelled `role="group"` (e.g. "Spots at North: 42 of 120 available", plus how many fit a large
    vehicle when one is chosen) of spot buttons with **one tab stop**. The arrow keys move between spots (up is toward the screen;
    up/down go to the nearest spot across, as rows are centered), Home/End go to the ends of the row, Enter or Space picks.
    Focus moves in the browser (`lot-map.js`), with no round trip; picking goes through Blazor as a click.
  - Every spot stays a button: unavailable ones (held, sold, too small for the vehicle) are `aria-disabled`, so focus and the arrow
    keys keep their place when a spot changes under them. When the focused spot changes state, the map's polite live region says so;
    `SeatMap` also announces when the viewer's own hold starts or ends.
  - States don't rest on color: held spots are hatched, sold ones crossed out, cars-only ones dashed; the key (a list, read by
    screen readers) shows the same marks. Spot labels are at least 4.5:1 against their spot. Instructions say "available", not "green".
  - **Best available** picks the free spot that fits the vehicle in the row nearest the screen, as near that row's middle as
    possible (the left one of two equally central; `SpotChoice.Best`). **Or choose a spot** lists every free, fitting spot by label
    and row. Both pick exactly as clicking the map would.
  - Maps that only show a layout (the lot map, a read-only screen layout) stay a single `role="img"` with a summary label.
- **Payment** (`IPaymentProcessor`, `Payments.cs`, `StripePayments.cs`): credit card only. The card never reaches the server: the
  checkout (`CardFields`, `wwwroot/payments.js`) turns it into a payment method token in the browser and the services take only
  the token (`PurchaseInput.PaymentMethodId`; `PaymentTokens.Require` refuses anything not shaped like `pm_...`). The processor's
  `PaymentClient` says how: Stripe's Payment Element (`stripe.createPaymentMethod`), the test card form (plain inputs Blazor never
  binds; `tokenizeTest` checks the number, expiry and code and makes `pm_test_{brand}_{last4}_{random}`), or none.
  `PaymentRequest` carries the amount in cents, currency (`Payments:Currency`, `usd`), description, the token (null = card-present
  at the gate), an idempotency key (`ticket-{id}-{Paying stamp}` or `giftcard-{random}`) and metadata (kind, ticket, theater,
  showing ids). Providers: `Dummy` approves test tokens and card-present charges, declines `pm_test_decline_...` (test number
  4000 0000 0000 0002) and anything else (set in `appsettings.Development.json`; demo theaters always use it); `Stripe`
  (untested, test-mode keys only, see README); unset (production) means nothing can be sold online or at the gate. Only the
  brand and last four the processor reports are stored. Total $0 after discounts needs no card.
- **Ticket states** (`TicketStatus`): Held (expires), Paying (being charged; never swept as a hold, see below), Pending
  (free-admission request; never swept), Sold. On approval: sold to buyer, receipt emailed
  (`TicketReceipt`): QR code (inline image) linking to `tickets/{code}` (random 128-bit code) and a 4-character gate code
  (no look-alike characters, e.g. `K7QM`). Receipts can be resent from My tickets.
- **Payout accounts** (`Payouts.cs`, `PayoutService`, Manage → Payouts, `payouts.manage`): the theater's money goes to its own
  account at the processor (`theaters.payout_account_id`, `payout_status` None / Pending / Enabled, `payout_status_checked_at`).
  With Stripe that's a Connect Express account (`StripeConnectAccounts`): "Set up payouts" creates it (idempotent per theater) and
  sends the owner to Stripe's hosted onboarding (an account link), which returns to `/manage/{id}/payouts?done=1` (status is
  checked then, or with "Check status"; `?expired=1` asks for a new link). Enabled = Stripe's charges_enabled and payouts_enabled.
  Charges are destination charges on the theater's behalf (`on_behalf_of` + `transfer_data.destination`, `PaymentRequest.PayoutAccountId`)
  with `application_fee_amount` from `Payments:ApplicationFeePercent` (0: a placeholder until fees are decided). When the processor
  needs one (`IPaymentProcessor.RequiresPayoutAccount`: Stripe), a live theater sells nothing by card (online, gift cards, the
  gate) until it's Enabled; demo theaters (dummy processor) never need one. `Payments:Provider=Dummy` uses `DummyPayoutAccounts`
  (onboarding finishes at once); unset uses none (the page says there's nothing to set up). Admins see each theater's status on
  `/admin/theaters` and its admin page. The platform's own billing of theaters stays in-house (`BillingService`), not Stripe Billing.
- **Settling payments** (`TicketSalesService.Payments.cs`): going to Paying saves everything the sale needs (option, add-ons,
  total, gate code, buyer email, gate seller) plus `tickets.payment_key` (the idempotency key) and `payment_started_at`. Only a
  definite decline undoes it at checkout (back to Held, details cleared, gift card money restored); a processor error leaves it
  Paying and tells the buyer (or attendant) they won't be charged twice. Finishing (`CompletePaidTicketAsync`) and undoing
  (`AbortTicketPaymentAsync`) are the same code for checkout, webhooks and reconciliation, idempotent and keyed on the payment
  key, so they can race safely. `PaymentReconcileService` runs every minute: Paying tickets and gift card purchases at least
  5 minutes old are looked up with the processor by key (`IPaymentProcessor.GetStatusAsync`). Succeeded: sold (receipt or gift
  card emailed; gate sales checked in). Failed, or unknown to the processor 30 minutes after starting: undone (the spot is swept
  once its hold has run out). Pending: waits. A ticket paid entirely by gift card is simply finished. Stripe also calls
  `POST /payments/stripe/webhook` (signed with `Payments:Stripe:WebhookSecret`; 404 without it) on payment_intent.succeeded /
  payment_failed, which settles that key at once by asking Stripe for the intent. A success for a sale that's no longer waiting
  is logged as an error for support (refund or sell by hand).
- **Gate codes** are unique among a theater's upcoming tickets, enforced when issued (not by a DB constraint).
- **No refunds or cancellations**, including weather.
- **Test data:** demo-theater tickets have `IsTest`.

## 6. The gate (`TicketSalesService.Gate.cs`, `/manage/{id}/gate`)

- **Check in** (`tickets.admit`): by gate code or QR/`tickets/{code}` link. Shows showing, screen, spot and validity. Admits once,
  only at the issuing theater, from 3 hours before the showing starts until it ends. Lookup matches the theater's tickets from
  yesterday on and lists duplicates if a gate code collides.
- **Sell** (`tickets.sell`): choose one of today's showings (selling continues after start, until end), a spot on the live map
  (held like online), ticket option and add-ons, then charge. Card-present (`PaymentRequest.PaymentMethodId` null; with Stripe it goes to `ICardReader`, a Stripe Terminal stub that
  declines for now); the ticket has no buyer
  account, `SoldById` is the attendant, and the car is checked in on sale. Competes with online buyers for the same spots. The
  attendant picks the car's vehicle size, with the same large-spot rule as online.
- **Move** (`tickets.move`, `TicketSalesService.Moves.cs`): from a looked-up ticket, choose the car's vehicle size and a new spot on
  the live map, e.g. a front-row ticket bought for a car when the guest arrives in a large SUV. Works before or after check-in,
  until the showing ends; same showing only. The new spot must be available right now (not sold, held, paying or pending; an
  expired hold is fine) and fit the vehicle. The ticket keeps its codes, payment and check-in; spot, label and vehicle change.
  Each move is a `ticket_moves` row (from/to spot, label and vehicle, who, when). Moving alone lets staff look tickets up at the
  gate but not check them in. Manager and Ticketing roles get `tickets.move` by default (a migration added it to existing ones).
- **Offline check-in** (`tickets.admit`, `/manage/{id}/gate/offline`, linked from the Check in panel): for when the lot's signal
  drops. A static page plus plain JS (`wwwroot/gate-offline/`, `Endpoints/GateOfflineEndpoints.cs`,
  `TicketSalesService.Offline.cs`), not a Blazor circuit, installable to a home screen (web manifest; a service worker scoped to
  the page keeps the page, scripts and styles, network first for the page with a 5-second fallback to the copy).
  - **Admit list** (`GET .../gate/offline/data`, JSON, `no-store`, ETag so an unchanged list is a 304): the showings the gate can
    admit to today (as for gate sales) and every sold ticket for them, used or not: id, showing, spot, large vehicle, kind
    (online / gate / comp), test, gate code, check-in time, and the ticket code only as a SHA-256 hash, with no names or emails, so a lost
    device holds no usable ticket links. Gate codes are kept as they are (they're read out at the gate and only admit at this
    theater today). Kept in IndexedDB; fetched on open and every 30 s while reachable, which also picks up new sales.
  - **Checking in** works like the online panel (gate code, ticket code, or a scanned ticket link; then Check in) with the same
    rules judged on the device's clock: once, from 3 hours before the showing until it ends. Tickets for other days aren't on the
    device, so they aren't found. Check-ins are queued on the device (each with a random id) and shown as used straight away.
  - **Sync** (`POST .../gate/offline/sync`, up to 200 check-ins; the antiforgery token from the list's `X-Gate-Token` header goes
    back in `RequestVerificationToken`): applied with the same rules as of when the car came in (a device clock ahead of the
    server counts as now), so a check-in synced after the showing ended still counts. Idempotent: the ticket's `Stamp` is set to the
    check-in's id, so a retried sync answers `already_synced` (also matched by the exact check-in time, since a later move gives
    the ticket a new `Stamp`). Conflicts (`already_used` by another check-in, `not_found` for a
    withdrawn free ticket or another theater's, `not_valid` at the time) are listed on the page under "Needs a look" until
    cleared, since the car is already in; a ticket moved since is admitted and reports its new spot.
  - The page shows Online / Offline / Signed out / No access, when the list was last updated (and that later sales aren't on it
    while offline), and how many check-ins are waiting to sync.

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
- Purchase at `/theaters/{slug}/giftcards`: any user who can browse the theater, $5 to $500, by card. Each attempt is a
  `gift_card_purchases` row (Paying → Completed or Failed) saved before charging, keyed `giftcard-{id}`, so an unheard charge
  is settled like a ticket's (above) and the card issued then. A 16-character code
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
- `/admin/messages` (`MessagingService`): the support inbox (reply, close, reopen) and every theater's customer conversations,
  read-only, filterable by theater (also linked as "Customer messages" from a theater's admin page). See section 15.
- `/admin/billing` (invoices: filter, draft now, issue all), `/admin/billing/invoices/{id}` (lines, issue, void, record payment,
  resend, print), `/admin/billing/subscriptions` (start, price, cancel, reactivate), `/admin/billing/reports`. See Billing.

## 12. Static and marketing pages

- `[ExcludeFromInteractiveRouting]` static SSR with plain CSS (`static.css`, `marketing.css`, `public.css`): `/`, `/features`,
  `/pricing`, `/faq`, `/legal`, `/legal/terms`, `/legal/privacy`, `/legal/license`, `/invite/{token}`, `/Error`, `/not-found`,
  account pages, and the public theater pages: `/theaters` (the list and near-me search, section 5), `/theaters/{slug}`, `/theaters/{slug}/pages/{page}`, `/theaters/{slug}/news` and
  `/theaters/{slug}/news/{post}` (section 16). Signed-out visitors and crawlers read those without opening a Blazor circuit.
  They use `PublicLayout` (the static top bar over a 1280px page, like the app's); `public.css` is written against the
  `app.css` tokens rather than MudBlazor's palette variables, so the marquee and stubs look the same there as on the
  interactive showing and ticket pages. "Choose a spot" links into the interactive showing page (enhanced navigation starts the
  circuit only then). The static top bar's notifications are a link with the unread count, not the live bell.
- Config: `Plans:PricePerScreenPerMonth`, `Billing:PaymentTermsDays`, `Company:*` (legal name, mailing address, governing state, contact email, effective date).
  Unset values render as placeholders; legal pages show a "draft, not in effect" banner until `Company:LegalName` is set.

## 13. UI and platform

- Blazor Web App, interactive server by default (`Routes`); MudBlazor for interactive pages in `AppLayout`; no Bootstrap.
- Light/dark follow OS via `wwwroot/theme.js` (`data-theme`, `di-scheme` cookie so the server prerenders the right palette);
  palettes in `Layout/DriveInTheme.cs`, `app.css`, `marketing.css`.
- Fonts are self-hosted (`wwwroot/fonts/fonts.css`, OFL): Bungee and Bungee Shade (display) and Barlow 400–700 (text),
  Latin and Latin Extended subsets. Nothing is loaded from Google Fonts or another font CDN.
- Production only answers to its own host names (`AllowedHosts` in `appsettings.Production.json`: the apex, `app.`, `www.`
  and `localhost`); a request for any other Host gets a 400. Anything new that calls the app by another name (e.g. a
  container health check on `web:8080`) has to be added there.
- Public pages use the marquee header (its bulbs chase around the sign; still under `prefers-reduced-motion`) and the
  ticket `Stub` (`public.css`); manage/admin pages are plain dense MudBlazor with `ManageHeader` / `AdminHeader`.
- Data: PostgreSQL via EF Core, snake_case, migrations in `Data/Migrations`. Interactive components don't hold a DbContext;
  data services use `IDbContextFactory`, services that use `UserManager` open a DI scope per call.
- Security headers (`Services/SecurityHeaders.cs`, after the exception handler so error pages get them too) on every app
  response: a CSP (`script-src 'self'` plus a per-request nonce on Blazor's import map, the only inline script; inline
  styles allowed for MudBlazor and Quill; images `self`, `data:`, `blob:`; `connect-src 'self'` for the circuit;
  `frame-src 'none'`; only when `Payments:Provider=Stripe`, Stripe's hosts for its card form: js.stripe.com in `script-src`,
  api.stripe.com in `connect-src`, and js.stripe.com and hooks.stripe.com as the only `frame-src`;
  `form-action` adds accounts.google.com for Google sign-in's redirect; `frame-ancestors 'none'`, `object-src 'none'`,
  `base-uri 'self'`), `X-Frame-Options: DENY`, `nosniff`, `Referrer-Policy: strict-origin-when-cross-origin`,
  `Cross-Origin-Opener-Policy: same-origin` and a `Permissions-Policy` (camera, geolocation and payment for this site only;
  microphone and USB off). A new third-party script, style, font, frame or API host must be added to the CSP there.
  Grafana (`/grafana/`) is proxied by Caddy and keeps its own headers. HSTS comes from `UseHsts` in production.
- Deploy: merge to `main` runs tests, builds ARM64 images, runs an EF migration bundle, then deploys via SSM; Caddy fronts the app.
  Zero downtime, blue/green on the one server (`deploy/rollout.sh`): compose services `web-blue` / `web-green` (one
  definition, profiles `blue` / `green`, 768 MB each). The idle color starts with the new image, `/readyz` must pass
  (asked from a throwaway container on the edge network, `Host: localhost`), then Caddy's `upstream` file and the staged
  `Caddyfile.next` are switched together and Caddy reloads; the old color drains 60 s, then gets a graceful stop. If
  the new copy isn't ready in 180 s, or Caddy rejects the switch, the old one keeps serving and the deploy fails.
  Caddy health-checks the upstream (`/healthz` every 5 s, 3 failures) and keeps WebSockets open across reloads
  (`stream_close_delay` 5m). Migrations must work with the previous release (CLAUDE.md).
- Health: `/healthz` (process up, no checks) and `/readyz` (database reachable, `DatabaseHealthCheck`), anonymous.
- Background jobs (`HoldExpiryService`, `NotificationEmailService`, `BillingJobService`, `BusinessGauges`) run only in the
  copy holding the Postgres session advisory lock `PostgresJobLeadership.LockKey` (unpooled connection, checked before each
  run; a stopped or disconnected copy releases it). `TheaterGeocodingBackfill` runs once per start in every copy; it's
  idempotent.
  Metrics and alerts: see section 14.
- Backups (README "Backups and restores"): point-in-time recovery with pgBackRest (`deploy/postgres/Dockerfile`, image
  `drive-in-postgres:pg-<Dockerfile hash>`, rebuilt only when that file changes): WAL archived to `s3://<OpsBucket>/pitr/`
  continuously (`archive_timeout` 60 s), nightly base backups (full Sundays, else differential; four fulls kept). Also a
  nightly `pg_dump` and Data Protection keys tarball (`backups/`, 30 days), and daily EBS snapshots (7). The bucket is
  versioned (old versions kept 14 days). `deploy/restore.sh`: `list`, `drill` (scratch restore beside the live database),
  `pitr <time>|latest`, `dump`, `dpkeys`. Alerts: WAL archiving failing, pg_dump or base backup overdue.
- Logs: every container's console output goes to CloudWatch Logs (`/drive-in/containers`, 30 days; Docker's `awslogs`
  driver, non-blocking, one stream per container). In production the web app logs JSON with scopes (trace id, request path).

## 14. Metrics and alerts (`deploy/grafana`, `deploy/victoriametrics`)

- **Grafana** at `/grafana/` (`https://drive-in.online/grafana/`, linked as Metrics on `AdminHeader`), **site admins only**.
  Caddy's `forward_auth` asks `/ops/grafana-auth` (`GrafanaAuth.Check`) before every Grafana request: an admin gets 200 with
  their lowercased email (else user id) in `X-WEBAUTH-USER`, which Grafana's auth proxy signs them in as (auto sign-up, org
  Admin; the seeded admin is also Grafana server admin); signed out redirects to `/Account/Login?ReturnUrl=` (only ever back into
  `/grafana/`); anyone else gets 403. Caddy drops a client-sent `X-WEBAUTH-USER`, and Grafana only trusts the header from
  Caddy's fixed IP (172.30.0.10). No login form, basic auth or anonymous access; Grafana publishes no port.
- **VictoriaMetrics** (13 months) scrapes node-exporter (host CPU, memory, swap, disk, PSI), Caddy (`:2020`, request
  counts, latency, status codes), postgres-exporter and itself every 30 s (`deploy/victoriametrics/scrape.yml`). `backup.sh`
  pushes `drivein_backup_last_success_timestamp_seconds` (pg_dump) and `drivein_pitr_backup_last_success_timestamp_seconds`
  (pgBackRest base backup) after each success; postgres-exporter's `pg_stat_archiver_*` covers WAL archiving.
- **Business data** comes from SQL: the "Drive-In DB" data source connects as `grafana_ro` (`deploy/grafana-ro.sql`, re-run on
  every deploy): read-only sessions, 30 s statement timeout, `pg_monitor`, and column-level SELECT on every table except
  `user_claims`, `user_logins`, `user_passkeys` and `user_tokens`, leaving out bearer codes (`code`, `short_code`), hashes,
  `security_stamp`, image bytes (`data`), `payment_reference`, any `*email*` column and what people write in the app
  (`messages.body`, `conversations.subject`, `notifications.title`) and theaters' page text (`theater_pages.body_html`,
  `theater_pages.summary`); on `users` only `id`, `created_at`,
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
  `drivein.tickets.admitted{how=scan|sold_at_gate|offline}`, `drivein.tickets.moved`, `drivein.holds.expired`,
  `drivein.gate.offline_admissions{outcome=synced|conflict}` (offline check-ins as they sync; a retried one isn't counted again),
  `drivein.payments{for=ticket|gift_card, result=approved|declined|error, test}`,
  `drivein.payments.reconciled{for=ticket|gift_card, outcome=completed|released, via=job|webhook}`, `drivein.gift_cards.sold{test}` and
  `drivein.gift_cards.revenue{test}` (dollars), `drivein.emails{result=sent|failed}` (every sender is wrapped in `MeteredEmailSender`; a send the caller
  cancels isn't counted),
  `drivein.jobs.failures{job=hold_expiry|billing|geocoding|business_gauges|notification_email|payment_reconcile}`, `drivein.invoices.issued`,
  `drivein.invoices.payments` (dollars), and `drivein.errors.logged{category, level}` (every Error/Critical log message,
  `ErrorCountingLoggerProvider`: failures inside Blazor circuits never become 5xx responses),
  `drivein.messages.sent{kind=theater|support, side=customer|theater|support}`, `drivein.notifications.emailed` (digests),
  `drivein.content.published{kind=page|post}` (a page or post's first publish), and
  `drivein.rate_limited{policy=account|messages|place_search|gift_card_misses_user|gift_card_misses_theater|gate_code_misses}`
  (requests and actions refused by a rate limit, section 1).
- **`BusinessGauges`** (hosted service, only when `Metrics:OtlpEndpoint` is set) reads totals every minute and reports them as gauges: `drivein.users{kind=customer|employee}`,
  `drivein.theaters{mode}` (active), `drivein.screens.live`, `drivein.showings.upcoming` (next 7 days, live theaters),
  `drivein.theaters.go_live_pending`, `drivein.free_admission.pending`, `drivein.invoices.outstanding` (dollars). Nothing is
  reported before the first read.
- Everything in Grafana is provisioned from the repo (data sources, dashboards in a read-only "Drive-In" folder, alert rules,
  contact point, notification policy); UI edits aren't kept. Dashboards:
  - **Drive-In: Business**: totals (customers, new accounts, live/demo theaters, go-live requests, live screens, tickets, revenue,
    cars admitted, gift cards, invoiced, owed) and daily trends from SQL (real sales only; test tickets and gift cards left out),
    top theaters and pending go-lives, plus live activity from the counters (sales, payments, sign-ups, abandoned holds, open
    sessions, emails, messages and notification emails, content published per day, offline gate check-ins synced vs conflicts).
  - **Drive-In: Site performance**: requests, 5xx, latency (p50/95/99, leaving out the Blazor circuit's connection), busiest and
    slowest routes, errors logged by category, unhandled exceptions, job failures, payments, emails, circuits and connections,
    sign-ins, rate-limited requests by policy, outbound calls, database time and pool, EF Core, and the .NET runtime (memory, CPU, GC, thread pool).
  - **Drive-In: Server**: host, edge, PostgreSQL, backups, monitoring targets.
- **Alerts** email through the `drive-in-alerts` SNS topic (`infra/app.yml`, `AlertEmail`), which Grafana publishes to with the
  instance role. Grafana rules (folder Drive-In, group Server): disk over 80% (10 min), memory available under 10% (10 min),
  swap over 1 GB (15 min), Caddy 502/503/504 above 0.02/s (5 min), PostgreSQL down (3 min), a scrape target down (10 min),
  last pg_dump or base backup over 26 h old, WAL archive failures for 10 min (critical). Group App: the app stopped reporting (5 min), 5xx over 5% of at least 20 requests in 10 min,
  p95 over 2 s (10 min), more than 10 errors logged in 5 min, any critical error, any email failure (15 min), any background
  job failure (15 min), any payment processor error (15 min), a ticket or gift card purchase still Paying 45 minutes after
  payment started (SQL), and a go-live request waiting over 24 h (SQL). Repeats every
  12 h while firing. CloudWatch alarms on the same topic cover what Grafana can't see
  from the box: EC2 system status (also auto-recovers the instance), instance status, CPU over 90% for 15 min, and any
  surplus CPU credits charged (t4g "unlimited" billing).

## 15. Messages and notifications (`MessagingService`, `NotificationService`)

- **In the app only.** Conversations and messages live in the database (`conversations`, `messages`, `conversation_reads`);
  they are never emailed. Recipients get a notification instead (below). Signed-in users only.
- **Customer ↔ theater** (`ConversationKind.Theater`): "Message the theater" on `/theaters/{slug}` opens
  `/messages/new?theater={slug}` (subject up to 200 characters, message up to 4000). Only theaters the person can browse
  (`TheaterService.CanBrowse`: not demo or inactive ones to outsiders); the owner and employees can't message their own
  theater. The theater's inbox is `/manage/{id}/messages` (Messages tab; open/closed lists, the thread beside them):
  `messages.view` reads, `messages.reply` replies, closes and reopens. Customers see replies as from the theater; staff and
  admins also see which staff member wrote them. Staff see the customer's display name (or "Customer"), never their email.
- **Support** (`ConversationKind.Support`): anyone signed in, typically a current or future owner, writes from `/messages/new`
  ("Contact Drive-In Online": linked from `/messages`, the manage list, the plan panel, `/get-started`, FAQ and Pricing),
  optionally about one of their theaters. Every admin is notified; admins reply, close and reopen at `/admin/messages`.
- **Admins** read every theater conversation (`/admin/messages?view=theaters`, filter by theater) but never post in them,
  even though they pass every theater permission check (unless they own the theater).
- **The customer's side**: `/messages` lists their conversations (theaters and support), `/messages/{id}` is the thread. Only
  the theater (or, for support, an admin) closes a conversation; nobody can post in a closed one until it's reopened.
- Limits: 10 new conversations per person per 24 hours, and at most 10 messages (new conversations and replies) a minute
  (`RateLimits:Messages`, section 1). Unread state is per person
  (`conversation_reads`): opening a thread marks it read for you only. Deleting an account keeps its conversations and
  messages, shown as "Deleted account". The personal-data download includes the messages you wrote.
- Threads, inboxes, the bell and `/notifications` update live through in-process `MessageEvents` / `NotificationEvents`
  (like `SpotEvents`).
- **Notifications** (`notifications`; `NotificationKind.Message` for now): one unread notification per person and
  conversation, counting new messages ("3 new messages from Starlight"); reading the conversation reads it. The bell in the app
  bar shows the unread count and the latest 10 (mark all read, see all); the static pages' top bar shows
  "Notifications (n)"; `/notifications` lists the latest 100 (unread first).
- **Email** (`NotificationEmailService`, every minute): a notification still unread `Notifications:EmailDelayMinutes` (10)
  after its last update, and not yet emailed, is sent in one digest per person: titles and links
  (`Notifications:SiteUrl`) only, never the message or subject. More messages before it's read don't send another; a
  failed send is retried next minute; nothing older than `Notifications:EmailMaxAgeHours` (48) is sent. Only to confirmed
  addresses, and not to anyone who turned it off at Account → Notifications (`ApplicationUser.EmailNotifications`, on by
  default).

## 16. Theater pages and posts (`ContentService`, `HtmlContent`)

- **What**: each theater writes its own customer-facing content (`theater_pages`):
  - **Pages** (`PageKind.Page`, e.g. Rules, Concessions, FAQ) at `/theaters/{slug}/pages/{page}`. Live pages with "Show in menu" are
    listed in the theater's menu, in the order set on the manage list (up/down).
  - **Posts** (`PageKind.Post`: announcements and special events) at `/theaters/{slug}/news/{post}`, listed under "News & events" on
    the theater page (3 cards, "All news & events" when there are more) and at `/theaters/{slug}/news` (20 a page). Order: pinned,
    then upcoming events (soonest first), then everything else newest first. A post can have an event date and time, shown on it.
- **The theater's menu** (`TheaterMasthead`): under the marquee on the theater page and its content pages: Showings, each live menu
  page, News & events (when there are live posts), Gift cards (when sold). New pages appear there as soon as they're live.
- **Managing** (`content.manage`; tab "Pages & posts" on `ManageHeader`): `/manage/{id}/content` (list with status, publish,
  unpublish, delete with confirmation, reorder pages), `/manage/{id}/content/new?kind=page|post` and `/manage/{id}/content/{pageId}`
  (title, web address made from the title if left blank, summary, cover image, content, menu or pin and event fields, publishing).
  Web addresses: lowercase letters, digits and hyphens, unique per theater and kind; pages can't use `news`, `pages`, `showings`,
  `giftcards`, `logo`, `images`, `new` or `edit`.
- **Publishing**: a draft until published (`PublishAt` null). Publish now, or schedule a start (`PublishAt`) and optional end
  (`UnpublishAt`) in the theater's time zone; shown only between them. Status: Draft, Scheduled, Live, Ended. Unpublish returns it
  to a draft. Publishing needs some text or a cover image. Staff with `content.manage` see drafts and scheduled items at their public
  address with a "Preview: only staff can see this" banner (and `noindex`); everyone else gets "Page not found" (404).
- **Editor** (`RichTextEditor`: Quill 2, vendored in `wwwroot/lib/quill`, via `wwwroot/rich-text.js`): headings (H2 to H4), bold,
  italic, underline, strike, lists, indent, quote, link, alignment, images. Pasted HTML keeps only this theater's library images.
- **Images**: the theater's library (`theater_images`, `/manage/{id}/content/images`): JPG, PNG, GIF or WebP, up to 5 MB each and
  250 per theater, type sniffed from the bytes (no SVG), width and height read from the header, kept as uploaded (no resizing).
  Each has a default description (alt text). The editor's image button opens `ImagePickerDialog` (Library or Upload tab), which asks
  for a description (or "decorative"), a size (small 33%, medium 60%, full width) and a position (left or right with text wrapping,
  or centered); pasted or dropped image files are uploaded to the library and placed medium and centered; clicking a placed image
  changes those or removes it. A page or post can have a cover image (hero on the page, thumbnail on news cards, and the link
  preview image; its alt text can be overridden per page). The library lists where each image is used; an image in use can't be
  deleted. Images are served at `/theaters/{slug}/images/{id}` to anyone who can browse the theater (public theaters' cached for a
  week: an image never changes, a new upload gets a new id).
- **Safety** (`HtmlContent.Sanitize`, HtmlSanitizer): applied when content is saved and again when it's shown. Allowed: `p br h2 h3
  h4 strong em u s a ul ol li blockquote hr img`; `href` only http, https, mailto or tel (links get `rel="noopener noreferrer
  nofollow"`, external ones open in a new tab); `img` only from this theater's library, with width, height and `loading="lazy"`
  written from the library; classes only `ql-align-*`, `ql-indent-1..8`, `img-small|medium|full`, `img-left|center|right`. No styles,
  scripts, frames, forms or event attributes (a script's or frame's contents are dropped too).
- **Sharing**: pages and posts set a meta description (the summary, or the start of the text) and Open Graph title, description and
  image (cover, else first image, else the theater's logo).

## 17. Not built

- A tried-and-enabled payment processor: `StripePaymentProcessor` exists but hasn't been run against Stripe (test keys only), and
  production can't sell until `Payments:Provider` is set. 3-D Secure, and Stripe Terminal readers at the gate.
- Concessions ordering (the Concessions role has no permissions yet).
- Paying invoices online (payments are recorded by an admin), sales tax on invoices, and overdue reminders or suspension for
  non-payment.
- Multi-server deployment (in-process `SpotEvents`, `MessageEvents`, `NotificationEvents`).
- Theater pages: image resizing or thumbnails (images are served as uploaded), revision history, a sitemap, and the theater's menu
  on the showing and gift-card pages (they keep a link back to the theater).
- Message attachments, theater-initiated conversations (a theater writing to a customer first), blocking a sender, and
  notification kinds other than messages (e.g. go-live decisions, free-admission requests).
