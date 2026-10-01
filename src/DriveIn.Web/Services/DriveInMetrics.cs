using System.Diagnostics;
using System.Diagnostics.Metrics;
using DriveIn.Web.Data;

namespace DriveIn.Web.Services;

// The app's business and activity counters (meter "DriveIn"), exported over OTLP to VictoriaMetrics when
// Metrics:OtlpEndpoint is set and graphed in Grafana (deploy/grafana). Each is recorded after the change it counts is
// saved. Tags stay low-cardinality (never a theater, user or showing id): per-theater detail comes from the database.
// VictoriaMetrics names them Prometheus-style, e.g. drivein.tickets.sold -> drivein_tickets_sold_total.
public sealed class DriveInMetrics
{
    public const string MeterName = "DriveIn";

    // Sales channels (the "channel" tag).
    public const string Online = "online";
    public const string Gate = "gate";
    public const string Comp = "comp";

    private readonly Counter<long> usersRegistered;
    private readonly Counter<long> theatersSignedUp;
    private readonly Counter<long> goLiveRequests;
    private readonly Counter<long> theatersActivated;
    private readonly Counter<long> ticketsSold;
    private readonly Counter<double> ticketRevenue;
    private readonly Counter<long> ticketsAdmitted;
    private readonly Counter<long> ticketsMoved;
    private readonly Counter<long> holdsExpired;
    private readonly Counter<long> payments;
    private readonly Counter<long> giftCardsSold;
    private readonly Counter<double> giftCardRevenue;
    private readonly Counter<long> emails;
    private readonly Counter<long> jobFailures;
    private readonly Counter<long> invoicesIssued;
    private readonly Counter<double> invoicePayments;
    private readonly Counter<long> errorsLogged;
    private readonly Counter<long> messagesSent;
    private readonly Counter<long> notificationsEmailed;
    private readonly Counter<long> contentPublished;

    public DriveInMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        usersRegistered = meter.CreateCounter<long>("drivein.users.registered", "{user}",
            "Accounts created, by how (method: password, google, invite, admin).");
        theatersSignedUp = meter.CreateCounter<long>("drivein.theaters.signed_up", "{theater}",
            "Theaters created by self sign-up (as demos).");
        goLiveRequests = meter.CreateCounter<long>("drivein.theaters.go_live_requested", "{request}",
            "Requests from demo theaters to go live.");
        theatersActivated = meter.CreateCounter<long>("drivein.theaters.activated", "{theater}",
            "Theaters made live (how: go_live, admin_created).");
        ticketsSold = meter.CreateCounter<long>("drivein.tickets.sold", "{ticket}",
            "Tickets sold or given (channel: online, gate, comp; test: demo-theater sales).");
        ticketRevenue = meter.CreateCounter<double>("drivein.tickets.revenue", "{USD}",
            "Ticket totals in dollars, gift card share included (channel, test).");
        ticketsAdmitted = meter.CreateCounter<long>("drivein.tickets.admitted", "{ticket}",
            "Cars checked in at the gate (how: scan, sold_at_gate).");
        ticketsMoved = meter.CreateCounter<long>("drivein.tickets.moved", "{ticket}", "Tickets moved to another spot.");
        holdsExpired = meter.CreateCounter<long>("drivein.holds.expired", "{hold}",
            "Spot holds that ran out before the buyer paid.");
        payments = meter.CreateCounter<long>("drivein.payments", "{payment}",
            "Card charges (for: ticket, gift_card; result: approved, declined, error; test).");
        giftCardsSold = meter.CreateCounter<long>("drivein.gift_cards.sold", "{card}", "Gift cards sold (test).");
        giftCardRevenue = meter.CreateCounter<double>("drivein.gift_cards.revenue", "{USD}",
            "Gift card face value sold, in dollars (test).");
        emails = meter.CreateCounter<long>("drivein.emails", "{email}", "Emails sent or failed (result: sent, failed).");
        jobFailures = meter.CreateCounter<long>("drivein.jobs.failures", "{failure}",
            "Background job runs that failed (job: hold_expiry, billing, geocoding, business_gauges, notification_email).");
        invoicesIssued = meter.CreateCounter<long>("drivein.invoices.issued", "{invoice}", "Invoices issued to owners.");
        invoicePayments = meter.CreateCounter<double>("drivein.invoices.payments", "{USD}",
            "Invoice payments recorded, in dollars.");
        errorsLogged = meter.CreateCounter<long>("drivein.errors.logged", "{message}",
            "Errors and critical messages logged (category: the logger, level: error, critical). See ErrorCountingLoggerProvider.");
        messagesSent = meter.CreateCounter<long>("drivein.messages.sent", "{message}",
            "In-app messages sent (kind: theater, support; side: customer, theater, support).");
        notificationsEmailed = meter.CreateCounter<long>("drivein.notifications.emailed", "{email}",
            "Notification digests emailed (one email per user per run, however many notifications it lists).");
        contentPublished = meter.CreateCounter<long>("drivein.content.published", "{item}",
            "Theater pages and posts published for the first time (kind: page, post).");
    }

    public void UserRegistered(string method) => usersRegistered.Add(1, new KeyValuePair<string, object?>("method", method));

    public void TheaterSignedUp() => theatersSignedUp.Add(1);

    public void GoLiveRequested() => goLiveRequests.Add(1);

    public void TheaterActivated(string how) => theatersActivated.Add(1, new KeyValuePair<string, object?>("how", how));

    public void TicketSold(string channel, bool test, decimal total)
    {
        var tags = new TagList { { "channel", channel }, { "test", test } };
        ticketsSold.Add(1, tags);
        ticketRevenue.Add((double)total, tags);
    }

    public void TicketAdmitted(string how) => ticketsAdmitted.Add(1, new KeyValuePair<string, object?>("how", how));

    public void TicketMoved() => ticketsMoved.Add(1);

    public void HoldsExpired(int count) => holdsExpired.Add(count);

    public void Payment(string forWhat, string result, bool test) =>
        payments.Add(1, new TagList { { "for", forWhat }, { "result", result }, { "test", test } });

    public void GiftCardSold(bool test, decimal amount)
    {
        var tag = new KeyValuePair<string, object?>("test", test);
        giftCardsSold.Add(1, tag);
        giftCardRevenue.Add((double)amount, tag);
    }

    public void Email(bool sent) => emails.Add(1, new KeyValuePair<string, object?>("result", sent ? "sent" : "failed"));

    public void JobFailed(string job) => jobFailures.Add(1, new KeyValuePair<string, object?>("job", job));

    public void InvoiceIssued() => invoicesIssued.Add(1);

    public void InvoicePaid(decimal amount) => invoicePayments.Add((double)amount);

    public void ErrorLogged(string category, LogLevel level) =>
        errorsLogged.Add(1, new TagList { { "category", category }, { "level", level == LogLevel.Critical ? "critical" : "error" } });

    public void MessageSent(ConversationKind kind, MessageSide side) =>
        messagesSent.Add(1, new TagList { { "kind", kind.ToString().ToLowerInvariant() }, { "side", side.ToString().ToLowerInvariant() } });

    public void NotificationEmailed() => notificationsEmailed.Add(1);

    public void ContentPublished(PageKind kind) =>
        contentPublished.Add(1, new KeyValuePair<string, object?>("kind", kind.ToString().ToLowerInvariant()));
}
