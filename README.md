# drive-in.online

[drive-in.online](https://drive-in.online): turn-key software for drive-in theaters (ticket sales,
lot capacity, concessions, and more). A .NET 10 Blazor Web App (Interactive Server) with ASP.NET
Core Identity (local accounts + Google), PostgreSQL via EF Core, and SES for email, running in
Docker on one EC2 server. The setup mirrors LegoList.

For the full list of features and behaviors (rules, limits, routes, permission keys), see [features.md](features.md); for the important URLs by role, [sitemap.md](sitemap.md).

## Layout

| Path | What |
|---|---|
| `src/DriveIn.Web/` | The app: marketing home page, Identity account pages, theater browsing, owner/employee management, admin UI. |
| `src/DriveIn.Web/Data/Migrations/` | EF Core migrations (the schema's source of truth). |
| `src/DriveIn.Web.Tests/` | xUnit tests: authorization matrix and services against a real DI container with EF InMemory, and bUnit tests of the pages (`Pages/`). |
| `deploy/` | Production compose file, Caddyfiles, `deploy.sh`, `backup.sh` (copied to the server on each deploy). |
| `deploy/grafana/`, `deploy/victoriametrics/` | The metrics site: Grafana's data sources, dashboards and alert rules, and what VictoriaMetrics scrapes. |
| `infra/dns.yml` | CloudFormation: Route 53 hosted zone. |
| `infra/email.yml` | CloudFormation: SES domain identity (DKIM, MAIL FROM). |
| `infra/mail.yml` | CloudFormation: inbound mail. Any address at the domain is forwarded to one mailbox (SES receiving, S3, a small Lambda). |
| `infra/app.yml` | CloudFormation: VPC, EC2, EIP, ECR, ops bucket, snapshots, DNS records, alerts topic and alarms, GitHub deploy role. |
| `.github/workflows/deploy.yml` | On merge to `main`: test, build ARM64 images, deploy via SSM. |

## Accounts and permissions

- **Users** register with email + password (confirmed by email) or sign in with Google. Every signed-in
  user can browse all theaters. Google sign-in links automatically to an existing confirmed account
  with the same verified email; links can also be managed under Account → External logins.
- **Admin** is the only role. The account whose email matches `Seed:AdminEmail` (SSM
  `/drive-in/admin-email`) becomes admin when it signs in, so the first admin just registers.
  Admins manage all theaters (`/admin/theaters`) and users (`/admin/users`). Note: removing admin
  from that seeded account won't stick; it gets it back at next sign-in.
- **Owners**: each theater has one owner; one user may own several theaters. Owners either sign their
  theater up themselves (see Onboarding) or an admin creates it and assigns the owner by email on the
  theater's admin page: existing accounts become owner immediately; otherwise an invitation is emailed.
  Owners manage their theater at `/manage/{id}`, including employees.
- **Employees** are separate accounts bound to one theater, created only by invitation from its
  owner or anyone with "Invite employees". What they can do is set by **roles**.
- **Roles** are defined per theater by its owner (Manage → Roles). A role is a named set of actions
  (edit profile, manage screens, manage schedule, manage pricing, view/invite/manage employees, manage roles, admit guests, sell tickets at the gate, move tickets, offer / approve / view free admission, manage / view gift cards). Employees can have several
  roles and get the union of their actions; new employees have none, so they can't do anything until
  assigned a role. New theaters start with Manager (everything), Operations, Ticketing (admit guests, sell at the gate, move tickets) and Concessions,
  which owners can change or delete. Someone with "Manage roles" can only grant, change or remove
  actions they hold themselves, and can't delete an employee who has more authority than they do.

Invitations are single-use links valid for 7 days (only a SHA-256 of the token is stored); the
invitee sets a password or continues with Google using the invited address.

## Screens, spots and the schedule

- A theater has 1 to 4 **screens** (new theaters start with "Screen 1"). Each screen's page
  (Manage → a screen) sets its name and its **spot layout**: a list of rows, nearest the screen first,
  each with its own number of spots and centered on the screen. Spot labels follow one of three schemes
  chosen per screen: row letter + spot number (`B7`), row number + spot letter (`2G`), or one number
  (`207` = row 2, spot 7). Spots are numbered left to right as drivers face the screen.
- **Large vehicles** (full-size SUVs, pickups, vans) may only park in spots the screen's page marks **L**, so they don't block the
  view of the cars behind; cars can park anywhere. Buyers, gate staff and free-admission givers say what the guest is driving.
  New sample layouts, and screens that existed when this shipped, have the back half of their rows marked.
- The **lot map** (Manage → Lot map) draws every screen around the central concessions/restrooms/projection
  building: screens 1 and 2 face each other, screens 3 and 4 face each other at 90 degrees. Screen order
  on the theater page sets the positions.
- The **schedule** (Manage → Schedule) holds the theater's films and their showings. A showing is one
  ticket on one screen: a single film, or up to 4 back to back (a **double feature**) with an intermission
  between them. The theater sets a default intermission, which is prefilled for new showings and can be
  changed per showing; changing the default doesn't move showings already scheduled. Showings on a screen
  can't overlap at any point from the first film's start to the last film's end (intermissions included).
  Times are entered and shown in the theater's time zone (profile → Time zone, an IANA name such as
  `America/Chicago`) and stored in UTC.
- A theater may set an operating **season** (profile → Season opens / closes, either end optional). Showings
  can only be scheduled within it, and it can't be changed to leave out showings already scheduled.
- **Film details and posters** (Schedule → Films → Edit): besides title, rating and runtime, a film can have a year, genres,
  director, cast and a description, and an uploaded poster (JPG, GIF or PNG, up to 2 MB; "Manage schedule"). There's no
  external movie database: IMDb has no free API, and TMDB's commercial license is $150 a month, so theaters enter these
  by hand and upload artwork they have the right to show (e.g. the distributor's promotional poster). Posters are stored in the
  database (`film_posters`) and served from `/films/{id}/poster` to signed-in users who can browse the theater. They show
  on the theater's page beside each showing.
- **Logo**: with "Edit profile", a theater uploads a JPG, GIF or PNG (up to 2 MB) on its manage page. The type is checked
  from the file's bytes (no SVG). It's stored in the database (`theater_logos`, so backups include it) and served from
  `/theaters/{slug}/logo` to signed-in users who can browse that theater (demo theaters: members only). It shows on the
  theaters list and the theater's page.

## Pricing

- **Price schedules** (Manage → Pricing) are per-theater named lists of ticket options, e.g. "1 occupant $10",
  "2 occupants $15", "Car load $25". Each theater has one default schedule ("Standard" for new theaters).
- A showtime uses the default schedule unless it's set to another one on the schedule page (e.g. "3D" or a
  special event). New schedules can start as a copy of an existing one. A schedule used by upcoming
  showtimes can't be deleted; past showtimes that used it fall back to the default.
- **Add-ons** are per-theater fees (e.g. outside food) and discounts, either a dollar amount or a percent
  (e.g. veterans, seniors). Inactive add-ons are kept but not offered.
- "Manage pricing" controls schedules and add-ons; choosing a showtime's schedule is part of "Manage schedule".

## Ticket sales

- Any signed-in user can buy tickets online: Theaters → a theater → a showing → **choose a spot** on the screen's
  map. A ticket is one spot (one car) at one showing.
- Customers can find **theaters near them** by ZIP code, city or their browser's location. Theater coordinates are
  looked up from the address with OpenStreetMap's Nominatim (`Geocoding:Provider`, no key needed; set `None` to turn
  lookups off). Its usage policy asks for a contact address in the User-Agent: `Geocoding:ContactEmail` (in production, SSM `/drive-in/geocoding-contact-email`), falling back
  to `Company:ContactEmail`. The public server allows about one lookup a second, which is fine at this scale; a busier
  site would switch `IGeocoder` to a paid provider.
- Showings show the **weather forecast** for the theater's location over the showing's hours, from Open-Meteo
  (`Weather:Provider`, free and keyless, up to 16 days ahead; `None` turns it off), on the theater, showing and
  ticket pages.
- Choosing a spot **holds** it for 10 minutes while the buyer picks a ticket option and add-ons and pays; the first
  to hold a spot gets it (a unique index on showing + spot), and anyone else who tries is told to choose another.
  A buyer holds one spot at a time. Expired holds are released every 10 seconds (`HoldExpiryService`).
- Seat maps update live: every hold, release and sale is published in-process (`SpotEvents`) to open maps. That
  works because the app is a single server; running several would need a shared bus such as Postgres LISTEN/NOTIFY.
- **Payment** is by credit card only, through `IPaymentProcessor`. **Card numbers never reach the server**: the checkout
  page turns the card into a payment method token in the browser (`wwwroot/payments.js`, `CardFields`) and sends only that.
  With Stripe the card fields are Stripe's Payment Element, in Stripe's own iframe, which keeps the site in PCI DSS's
  **SAQ A** (the lightest level: no card data is stored, processed or sent by our servers). Only the brand and last four
  digits the processor reports are stored. A ticket whose total is $0 after discounts needs no card. Providers (`Payments:Provider`):
  - `Dummy` (on in `appsettings.Development.json`, and always used by demo theaters): approves everything without taking money.
    Its checkout shows a test card form (plain inputs only `payments.js` reads) that makes fake `pm_test_...` tokens; use
    4242 4242 4242 4242, or 4000 0000 0000 0002 to see a decline.
  - `Stripe`: `StripePaymentProcessor`. **Not tried against Stripe yet** (there's no Stripe account), so it accepts only
    test-mode keys (`pk_test_`/`sk_test_`) and production doesn't enable it. Keys: `Payments:Stripe:PublishableKey` and
    `Payments:Stripe:SecretKey` (user-secrets locally; in production, SSM `/drive-in/stripe-publishable-key` and
    `/drive-in/stripe-secret-key`, which `deploy.sh` doesn't read yet). Before turning it on: try a test-mode sale, a
    decline and a gift card end to end; add the keys to `deploy.sh` and the compose file; allow Stripe in the site's
    Content-Security-Policy (`Services/SecurityHeaders.cs`): `https://js.stripe.com` in `script-src` and `frame-src`, and
    `https://api.stripe.com` in `connect-src`; then lift the test-key check in `StripeOptions.Validate`.
    Known gaps: cards that ask for 3-D Secure are declined for now, and gate sales need Stripe Terminal readers
    (`StripeTerminalReader` is a stub that declines).
  - Unset (as in production): nothing is sold, online or at the gate.
- **Charges can't be lost or doubled.** Every charge carries an idempotency key (`ticket-{id}-{stamp}`, `giftcard-{id}`), and
  everything the sale needs is saved before charging. If checkout never hears the outcome (a timeout, or the server dying
  mid-charge), the ticket or gift card purchase stays Paying and `PaymentReconcileService` asks the processor about it after
  5 minutes: it's sold (receipt emailed) if the charge went through, and undone if it failed or the processor still has no
  record of it after 30 minutes. With Stripe, a signed webhook at `/payments/stripe/webhook` settles it right away (set
  `Payments:Stripe:WebhookSecret`, SSM `/drive-in/stripe-webhook-secret`, and subscribe it to payment_intent.succeeded and
  payment_intent.payment_failed). Grafana alerts if one is still Paying after 45 minutes.
- On approval the spot is sold to the buyer and a **receipt** is emailed with a QR code (an inline image) linking to
  `tickets/{code}` (a random 128-bit code) and a 4-character **gate code** (e.g. `K7QM`; no look-alike characters)
  the guest can read out instead. Buyers see their tickets under My tickets and can resend the receipt.
- **The gate** (Manage → Gate) has two panels, each shown to staff with its permission:
  - **Check in** ("Admit guests"): type the gate code, or scan the QR code (a handheld scanner types into the box;
    a phone's camera app opens `tickets/{code}` directly). It shows the showing, screen and spot, and whether the
    ticket is valid: it admits once, only at this theater, from 3 hours before its showing until it ends, so a
    ticket for another date is refused. Gate codes are unique among a theater's upcoming tickets (checked when
    issued, not by the DB), so a lookup matches the theater's tickets from yesterday on and lists any duplicates.
  - **Sell a ticket** ("Sell tickets at the gate"): pick one of today's showings (the gate keeps selling after a
    showing starts, until it ends), pick a spot on the live map (held for the car as online), choose the ticket
    option and add-ons, and charge the card on the terminal. The charge is card-present (`PaymentRequest.Card` is
    null; the dummy processor approves it), the ticket has no buyer account (`SoldById` is the attendant), and the
    car is checked in as it's sold. Online buyers and the gate compete for the same spots; first to hold wins.
  - **Move** ("Move tickets"): send a car to another spot at the same showing, e.g. a front-row ticket when the guest arrives
    in a large SUV, at check-in or after the car is in. Only spots available right now that fit the vehicle; each move is logged
    in `ticket_moves`.
- **All sales are final**: there are no refunds or cancellations, including for weather. Sold tickets are records,
  so a showing with sales can't be removed or moved to another screen, a film or screen with sales can't be
  deleted, and a screen can't drop or relabel spots sold for upcoming showings.
- **Free admission** (Manage → Free admission): employees reserve a spot for a named guest (a friend or family
  member) at no charge. A theater turns it on under Overview → Free admission, and sets whether it **needs approval**,
  whether a **reason** is required, and optional caps per showing and per employee per showing (off by default). Three
  actions govern it: *Offer free admission* (reserve a spot, or request one when approval is required), *Approve free
  admission* (approve/deny requests, withdraw unused free tickets; approvers' own offers skip approval) and *View free
  admission log*. Only the Manager role gets them by default (a migration adds them to existing Managers).
  - A free ticket is a normal sold ticket (`Ticket.IsComp`, $0, no buyer account, `SoldById` = the giver) with a QR
    code and gate code, optionally emailed to the guest, and checked in at the gate like any other. A request waiting
    for approval is a `Pending` ticket: it holds the spot with no expiry until approved (becomes sold) or denied.
  - Everything (requested, given, approved, denied, withdrawn) is written to `comp_events`, with the people, showing
    and spot copied in so the log outlives deleted tickets and accounts. Demo theaters' test entries are cleared on
    go-live along with their test tickets.
- **Gift cards** (Manage → Gift cards): a theater turns sales on there (*Manage gift cards*); *View gift cards* lists
  sales, balances and the amount still owed (codes are never shown to staff, only the last four characters). Any
  signed-in user who can browse the theater buys one at `/theaters/{slug}/giftcards` for $5 to $500 by card, and the
  16-character code (about 78 random bits) is emailed to them and an optional recipient. Only Managers get the actions
  by default (a migration adds them to existing Managers).
  - Spending: enter the code at online checkout, or at the Gate (needs *Sell tickets at the gate*). The code must match
    a card sold by **that theater**; a wrong code and another theater's code give the same error. One card per ticket.
    It pays first (up to its balance) and the card or terminal is charged only for the rest; if nothing is left to
    pay, no card is needed. What remains stays on the gift card. Cards have no expiry, and no refund or cash-out.
  - The balance is taken off in the same save that moves the ticket to `Paying`, guarded by a concurrency stamp and a
    DB check (`0 <= balance <= initial_amount`), so it can't be spent twice. If the card is then declined, the money
    is put back (a `Restore` transaction). Every change is a row in `gift_card_transactions`. Turning sales off leaves
    cards already sold spendable. Demo theaters' test cards are cleared on go-live with their test tickets.
- **Reports** (Manage → Reports, *View reports*): pick a date range (or a preset) to see ticket sales and attendance
  by channel, payment, day, showing and film, ticket options and add-ons, and gift cards sold, spent and still owed
  (with every card that has a balance left). Each table downloads as CSV. Only Managers get it by default (a migration
  adds it to existing Managers). Details in [features.md](features.md).
- **Messages** stay in the app (stored in Postgres, never emailed): customers write to a theater from its page (staff
  with *View messages* / *Reply to messages* answer at Manage → Messages), anyone signed in can contact us (admins answer at
  `/admin/messages`, where they can also read, but not join, every theater's conversations). New messages show under a
  bell in the app bar; if one is still unread after `Notifications:EmailDelayMinutes` (10) the person is emailed a link
  (never the text) unless they turned that off at Account → Notifications. Links point at `Notifications:SiteUrl`
  (production's address by default; `appsettings.Development.json` uses localhost). Details in [features.md](features.md).
- **Theater pages and posts**: theaters write their own pages (in the theater's menu) and posts (news and special events) in a
  rich-text editor (Quill, vendored in `wwwroot/lib/quill`), with images from their own library. Drafts can be previewed by staff,
  publishing can be scheduled, and the HTML is sanitized (HtmlSanitizer) when saved and when shown. A theater's page and its content
  are public (no sign-in) for live theaters; buying still needs an account. Details in [features.md](features.md).

## Marketing site and onboarding

- **Public pages** (MarketingLayout, statically rendered): home, `/features`, `/pricing`, `/faq`, and `/legal` (Terms of
  Service, Privacy Policy, License & open-source notices). Prices come from `Plans:PricePerScreenPerMonth` and the
  provider's details from `Company:*` (legal name, mailing address, governing state, contact email, effective date).
  Unset values show as placeholders (`$__`, `[Company legal name]`), and the legal pages show a "draft, not in effect"
  banner until `Company:LegalName` is set. The legal text is a starting draft for review by a lawyer. These values
  aren't secret, so set them in `src/DriveIn.Web/appsettings.json` (through a PR, like any change).
- **Sign-up** (`/get-started`, "Start free demo"): any signed-in personal account (not an employee account) creates a
  theater with a name, web address, location, time zone and 1–4 screens, accepting the Terms (version and time are
  recorded on the theater). At most `Plans:MaxTheatersPerOwner` (3) per account. The theater starts with the default
  roles, screens laid out 8 rows × 15 spots, and sample prices, so a sale can be tried right away.
- **Demo mode**: a signed-up theater is private (only its members can see it or buy from it) and its sales are test
  sales: they go through the dummy payment processor whatever `Payments:Provider` says, and tickets are marked
  test (receipts say so). The manage page shows a setup checklist.
- **Going live**: the owner (or an admin) asks from the manage page, agreeing to the Standard plan's billing; admins
  are emailed. An admin activates or declines it (with a note to the owner) on `/admin/theaters`. Activating makes the
  theater public and selling for real, and deletes its test tickets. Theaters created by an admin are live from the start.
- **Billing** is per screen for each calendar month the operating season touches (every month without a season).
  Activation starts the theater's subscription at the current price. An hourly job drafts each month's invoice; an admin
  reviews and issues them at `/admin/billing` (emailed to the owner, due `Billing:PaymentTermsDays` later) and records
  payments as they arrive (each emails a receipt). There's no card processor for this yet. Owners see their plan and
  invoices under Manage → Billing (owner-only unless granted). Details in [features.md](features.md).
- **Payouts**: ticket and gift card money belongs to each theater, so with Stripe every charge is a destination charge to the
  theater's own Stripe Connect (Express) account, which the owner sets up under Manage → Payouts (owner-only unless granted
  "Manage payouts") through Stripe's hosted onboarding; Stripe collects and verifies the business and bank details. A live
  theater can't take cards until its account is enabled. The platform's fee per charge is `Payments:ApplicationFeePercent` (0
  for now). Untested against Stripe, like the rest of the Stripe code; the subscription billing above stays in-house rather than
  moving to Stripe Billing, which can be revisited once there's a Stripe account.

## Look and feel

- **MudBlazor** (MIT, free for commercial use) provides the components for every signed-in page; the app shell is
  `Components/Layout/AppLayout.razor`. Marketing pages, legal pages and the Identity account pages stay statically
  rendered (fast, indexable, and Identity needs the HTTP response) with plain CSS (`wwwroot/static.css`); they are marked
  `[ExcludeFromInteractiveRouting]`.
- **Light and dark** follow the visitor's browser or OS setting, with no toggle. `wwwroot/theme.js` runs before first paint and
  remembers the choice in a `di-scheme` cookie, so the server prerenders the right palette next time (the first-ever visit
  from a dark device may flash light for a moment). The palettes live in `Layout/DriveInTheme.cs` (MudBlazor) and at the
  top of `wwwroot/app.css`; `marketing.css` has its own copy.
- **Public pages** (theaters, showings, tickets) are the showy ones: a bulb-lit marquee header per theater and
  ticket-stub showings and tickets (`wwwroot/public.css`, `Components/Shared/Stub.razor`). **Manage and admin pages** are meant to stay plain and dense.
- Fonts: Bungee for display headings, Barlow for everything else (both from Google Fonts).

## Local development

Needs Docker and the .NET 10 SDK.

```powershell
./setup.ps1   # Postgres on localhost:5433 + migrations
./start.ps1   # build, test, run at http://localhost:5280
```

Emails aren't sent locally; confirmation, reset, and invite links are written to the console.
To see metrics locally, `docker compose --profile monitoring up -d` starts VictoriaMetrics and Grafana
(http://localhost:3000/grafana/, no sign-in) with the production dashboards; the app pushes to it in Development.
To make yourself admin locally: `dotnet user-secrets set Seed:AdminEmail you@example.com --project src/DriveIn.Web`.
Google sign-in is optional locally; to enable it, set `Authentication:Google:ClientId` and
`Authentication:Google:ClientSecret` with user-secrets (redirect URI `http://localhost:5280/signin-google`).

**Schema changes:** edit the entities in `src/DriveIn.Web/Data/`, then

```sh
dotnet ef migrations add <Name> --project src/DriveIn.Web --output-dir Data/Migrations
dotnet ef database update --project src/DriveIn.Web
```

Production applies migrations during deploy with an EF migration bundle (`Dockerfile.migrate`),
before the new app version starts; if a migration fails, the old version keeps running.

## Workflow

All changes go through a feature branch and a pull request; nothing is committed to `main` directly.
Merging to `main` runs `.github/workflows/deploy.yml`, which assumes the IAM role
`drive-in-app-deploy` via OIDC (no stored AWS keys) and reads these repo **variables**:
`AWS_ROLE_ARN`, `ECR_REGISTRY`, `OPS_BUCKET`, `INSTANCE_ID` (from the `drive-in-app` stack outputs).

## Infrastructure

All stacks are in `us-east-1`. The domain is registered at GoDaddy with nameservers pointing to Route 53.

### One-time setup

1. **Email.** `aws cloudformation deploy --stack-name drive-in-email --template-file infra/email.yml`.
   Then request SES production access (Account dashboard → Request production access); until it's
   granted, SES only delivers to verified addresses.

   **Inbound mail** (optional): every address at the domain (`info@`, `tickets@`, `anything@`) forwards to one
   mailbox. The address is a parameter, since this repo is public:
   ```sh
   aws cloudformation deploy --stack-name drive-in-mail --template-file infra/mail.yml \
     --capabilities CAPABILITY_IAM --parameter-overrides ForwardTo=you@example.com
   aws ses set-active-receipt-rule-set --rule-set-name drive-in-inbound   # once; CloudFormation can't activate it
   ```
   It adds the domain's MX record (pointing at SES). SES only sends from our own domain, so forwards come from
   "*Sender name* (*sender address*) via drive-in.online" `<forwarder@drive-in.online>`; Reply goes to the original sender (Reply-To). SES's
   spam and virus scan runs first and flagged mail is dropped. Messages are kept 30 days in the stack's bucket, and
   the forwarder logs to CloudWatch (`/aws/lambda/drive-in-mail-Forwarder-…`). Cost: SES receiving is about $0.10 per
   1,000 messages; the Lambda and bucket stay in the free tier. Tests: `python infra/test_mail_forwarder.py`.
2. **Google OAuth client** (Google Cloud console → Credentials → OAuth client ID, Web application).
   Authorized redirect URIs: `https://drive-in.online/signin-google`,
   `https://app.drive-in.online/signin-google`, `http://localhost:5280/signin-google`.
3. **Secrets** in SSM Parameter Store (SecureString). The DB password is generated by the AWS CLI
   (no OpenSSL needed; nothing is stored in Secrets Manager). It has no punctuation because it goes
   into a connection string and `.env`. PowerShell:
   ```powershell
   $pw = aws secretsmanager get-random-password --password-length 40 --exclude-punctuation --query RandomPassword --output text
   aws ssm put-parameter --type SecureString --name /drive-in/db-password --value $pw
   Remove-Variable pw
   ```
   Then:
   ```sh
   aws ssm put-parameter --type SecureString --name /drive-in/google-client-id     --value ...
   aws ssm put-parameter --type SecureString --name /drive-in/google-client-secret --value ...
   aws ssm put-parameter --type SecureString --name /drive-in/admin-email          --value you@example.com
   aws ssm put-parameter --type SecureString --name /drive-in/geocoding-contact-email --value you@example.com
   ```
   And the password of `grafana_ro`, the read-only database role the metrics site uses (PowerShell, like the DB password):
   ```powershell
   $pw = aws secretsmanager get-random-password --password-length 40 --exclude-punctuation --query RandomPassword --output text
   aws ssm put-parameter --type SecureString --name /drive-in/grafana-db-password --value $pw
   Remove-Variable pw
   ```
   (`/drive-in/serve-apex` and `/drive-in/alerts-topic-arn` are managed by the app stack. `geocoding-contact-email` is optional: it's the contact
   address Nominatim's usage policy asks for, and without it the app falls back to `Company:ContactEmail`.)
4. **Server**, serving `drive-in.online` (`www` and `app` redirect to it). Run from the repo root:
   ```sh
   aws cloudformation deploy --stack-name drive-in-app --template-file infra/app.yml \
     --capabilities CAPABILITY_NAMED_IAM
   ```
   To stage a server on `app.drive-in.online` only, without taking over the domain, add
   `--parameter-overrides ServeApex=false`; switch it to `true` later and re-run the Deploy workflow
   so Caddy picks up `Caddyfile.live`. The stack keeps its current `ServeApex` on later deploys.
5. **Repo variables** for the Deploy workflow, from the stack outputs (PowerShell):
   ```powershell
   $out = aws cloudformation describe-stacks --stack-name drive-in-app --region us-east-1 --query "Stacks[0].Outputs" --output json | ConvertFrom-Json
   function Out($key) { ($out | Where-Object OutputKey -eq $key).OutputValue }
   gh variable set AWS_ROLE_ARN --body (Out DeployRoleArn)
   gh variable set ECR_REGISTRY --body (Out EcrRegistry)
   gh variable set OPS_BUCKET   --body (Out OpsBucket)
   gh variable set INSTANCE_ID  --body (Out InstanceId)
   gh variable list
   ```
   Confirm the server is reachable by SSM (should print `Online` a few minutes after the stack finishes):
   ```powershell
   aws ssm describe-instance-information --region us-east-1 --filters "Key=InstanceIds,Values=$(Out InstanceId)" --query "InstanceInformationList[].PingStatus" --output text
   ```
6. **Deploy**: merge to `main` (or run the Deploy workflow manually) and watch it with `gh run watch`.
   Then check `https://drive-in.online`: registration email, Google sign-in, admin, invites.

The domain used to serve a static site (S3 + CloudFront, `drive-in-site` stack). It was retired on
2026-09-28 by deleting that stack, deploying the app stack with `ServeApex=true`, and re-running the
Deploy workflow. The apex didn't resolve between the first two steps, and resolvers cache that
"no such name" for up to 15 minutes, so do such moves back to back. (Negative answers are cached for
the lesser of the SOA record's TTL, 900 s on Route 53, and its MINIMUM field, 86400 s; RFC 2308.)

### Changing the app stack

**Preview every change to `drive-in-app` before applying it.** Some changes replace the server, and a replacement
starts from a fresh, empty disk: the site is down until the data is moved over. The old root volume is kept
(`DeleteOnTermination: false`), and there are nightly backups and daily snapshots, but a replacement still means manual
recovery. Preview:

```sh
aws cloudformation deploy --stack-name drive-in-app --template-file infra/app.yml \
  --capabilities CAPABILITY_NAMED_IAM --no-execute-changeset
aws cloudformation describe-change-set --change-set-name <name from the output> --stack-name drive-in-app \
  --query "Changes[].ResourceChange.[LogicalResourceId,Action,Replacement]" --output table
```

If `Server` shows `Replacement: True`, don't execute it as is. The server's image is pinned (`ImageId` on `Server` in
`infra/app.yml`, not a parameter, since `cloudformation deploy` keeps a parameter's previous value) so updates don't
pick up a new one. The template used to follow the "latest" SSM parameter, and on 2026-10-01 an unrelated stack update
replaced the server that way. The first update after that change drops the old `AmiId` parameter; its preview should
show `Server` unchanged, since the pinned image is the one it runs. To upgrade the OS image, do it deliberately: take a
backup, change `ImageId` in the template (through a PR), apply, then move the Docker volumes (`drive-in_pgdata`, `drive-in_dpkeys`,
`drive-in_caddy_data`, `drive-in_caddy_config`) from the old root volume, and update the `INSTANCE_ID` repo variable
before redeploying. Patches within the image come from `dnf upgrade` on the server instead.

### Operations

- **Metrics:** `https://drive-in.online/grafana/` (Admin → Metrics), for site admins: sign in to the app as an admin and
  Grafana signs you in. Dashboards and alert rules live in `deploy/grafana/`; Grafana's UI can't save changes to them, so
  edit them there (or export a changed dashboard as JSON into that folder). Alerts are emailed through the
  `drive-in-alerts` SNS topic; to check delivery, open Alerting → Contact points → SNS email → Test.

- Shell on the server: `aws ssm start-session --target <InstanceId>`; the stack lives in `/opt/drive-in`.
- Logs: `docker compose logs web` there (lost when the container is recreated on deploy). Production logs include
  scopes, so a request ID from the error page (`00-<trace id>-<span id>-00`) can be found by grepping for its trace id.
- Backups: nightly `pg_dump` to `s3://<OpsBucket>/backups/` (30 days), plus daily EBS snapshots (7).
  Run one now with `sudo drive-in-backup <OpsBucket>`.
