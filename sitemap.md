# Site map

The important URLs on https://drive-in.online, grouped by who most likely needs them. Everything except the public
pages needs you to be signed in. `{id}` is a theater's number (in the address bar of its manage pages), `{slug}` its web
address (e.g. `starlight`). For rules, permissions and limits see [features.md](features.md).

## Everyone (no sign-in)

| URL | What |
|---|---|
| [/](https://drive-in.online/) | Home page (Sign in / Open the app at the top) |
| [/features](https://drive-in.online/features), [/pricing](https://drive-in.online/pricing), [/faq](https://drive-in.online/faq) | Marketing pages |
| [/legal](https://drive-in.online/legal) | Terms of Service, Privacy Policy, licenses (`/legal/terms`, `/legal/privacy`, `/legal/license`) |
| [/Account/Login](https://drive-in.online/Account/Login) | Sign in (password, passkey or Google) |
| [/Account/Register](https://drive-in.online/Account/Register) | Create an account |
| [/Account/ForgotPassword](https://drive-in.online/Account/ForgotPassword) | Email a password reset link |
| [/Account/ResendEmailConfirmation](https://drive-in.online/Account/ResendEmailConfirmation) | Resend the confirm-your-email link |
| `/invite/{token}` | Accept an owner or employee invitation (the link is emailed; valid 7 days) |
| [/theaters](https://drive-in.online/theaters) | All live theaters, and theaters near you (ZIP, city or your location) |
| `/theaters/{slug}` | A theater's showings, with weather, posters and film details |
| `/theaters/{slug}/pages/{page}`, `/theaters/{slug}/news` | A theater's own pages, and its news and events |

## Customers (any signed-in account)

| URL | What |
|---|---|
| `/theaters/{slug}/showings/{id}` | Choose a spot on the lot map and buy a ticket |
| `/theaters/{slug}/giftcards` | Buy a gift card for that theater (when it sells them) |
| [/tickets](https://drive-in.online/tickets) | My tickets (and gift cards bought or received); resend a receipt |
| `/tickets/{code}` | One ticket, with its QR code and gate code (the receipt's link) |
| [/Account/Manage](https://drive-in.online/Account/Manage) | Your account: who you're signed in as, name, email, password, passkeys, two-factor, Google link, your data |
| [/get-started](https://drive-in.online/get-started) | Sign a theater up (free demo); this is how an owner starts |

Sign out is at the top right of any app page (under ☰ on a phone).

## Theater owners

Owners can do everything at their own theaters. Start at [/manage](https://drive-in.online/manage), which lists them.

| URL | What |
|---|---|
| [/manage](https://drive-in.online/manage) | My theaters |
| `/manage/{id}` | Overview: profile, logo, season, time zone, setup checklist, free admission and gift card settings, go-live request |
| `/manage/{id}/screens/{screenId}` | A screen: name, spot layout, label scheme, large-vehicle spots |
| `/manage/{id}/lot` | Lot map of all screens |
| `/manage/{id}/schedule` | Films (details, posters) and showings |
| `/manage/{id}/pricing` | Price schedules and add-ons |
| `/manage/{id}/employees` | Employees and invitations |
| `/manage/{id}/roles` | Roles (named sets of actions) for employees |
| `/manage/{id}/reports` | Sales, attendance and gift card reports. Each table downloads as CSV from `/manage/{id}/reports/{kind}.csv?from=yyyy-MM-dd&to=yyyy-MM-dd`, where `{kind}` is days, films, showings or giftcards |
| `/manage/{id}/billing` | Plan, invoices and payments (owner only unless granted) |
| `/manage/{id}/billing/invoices/{invoiceId}` | One invoice (printable) |
| `/manage/{id}/payouts` | Payout account with the payment processor (owner only unless granted) |

The employee pages below are open to owners too.

## Employees

An employee can do what their roles allow. Their theater is under [/manage](https://drive-in.online/manage); with no role yet, its
page (`/manage/{id}`) opens but only says they have no permissions there until someone assigns them a role.

| URL | What | Needs |
|---|---|---|
| `/manage/{id}/gate` | The gate: check cars in (gate code or QR), sell a ticket at the gate, move a car to another spot | Admit guests / Sell tickets at the gate / Move tickets |
| `/manage/{id}/comps` | Free admission: reserve a spot for a guest, approve requests, the log | Offer / Approve / View free admission |
| `/manage/{id}/giftcards` | Gift card sales, balances and amounts owed | Manage / View gift cards |
| `/manage/{id}/schedule`, `/manage/{id}/pricing`, `/manage/{id}/screens/{screenId}`, `/manage/{id}/lot` | As for owners | Manage schedule / pricing / screens |
| `/manage/{id}/employees`, `/manage/{id}/roles` | As for owners | View, invite or manage employees / Manage roles |
| `/manage/{id}/reports` | As for owners | View reports |

## Site admins

Admins can also open every owner and employee page for any theater.

| URL | What |
|---|---|
| [/admin/theaters](https://drive-in.online/admin/theaters) | All theaters; go-live requests to activate or decline |
| [/admin/theaters/new](https://drive-in.online/admin/theaters/new) | Create a theater (live from the start) and assign its owner |
| `/admin/theaters/{id}` | Edit a theater, change its owner |
| [/admin/users](https://drive-in.online/admin/users) | Users: create, make or remove admin, delete |
| [/admin/billing](https://drive-in.online/admin/billing) | Invoices: draft, issue, void, record payments |
| [/admin/billing/subscriptions](https://drive-in.online/admin/billing/subscriptions) | Subscriptions and prices |
| [/admin/billing/reports](https://drive-in.online/admin/billing/reports) | Billing reports. CSV from `/admin/billing/{kind}.csv?from=yyyy-MM&to=yyyy-MM`, where `{kind}` is invoices, payments or aging |
| [/grafana/](https://drive-in.online/grafana/) | Metrics and alerts (Admin → Metrics). Dashboards: Business, Site performance, Server |
| [AWS Billing and Cost Management](https://us-east-1.console.aws.amazon.com/costmanagement/home#/home) | What hosting costs: this month's spend and forecast, by service (sign in to the AWS console). See also [Bills](https://us-east-1.console.aws.amazon.com/billing/home#/bills), [Cost Explorer](https://us-east-1.console.aws.amazon.com/costmanagement/home#/cost-explorer) and [Free Tier usage](https://us-east-1.console.aws.amazon.com/billing/home#/freetier) |

Who's an admin: the personal (not employee) account whose email is in the SSM parameter `/drive-in/admin-email`
becomes one when it signs in; others are made admin at `/admin/users`.
