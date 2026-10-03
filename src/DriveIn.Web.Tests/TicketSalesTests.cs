using System.Security.Claims;
using DriveIn.Web.Data;
using DriveIn.Web.Services;
using Microsoft.EntityFrameworkCore;
using static DriveIn.Web.Authorization.TheaterPermissions;

namespace DriveIn.Web.Tests;

public class TicketSalesTests
{
    private static readonly DateOnly Day = new(2026, 9, 5);
    private const string Visa = "pm_test_visa_4242_0001"; // a test card token (see TestCardTokens)

    internal sealed record Setup(
        TestApp App, ApplicationUser Owner, Theater Theater, Screen Screen, Showtime Showing,
        PriceOption Single, PriceOption CarLoad, AddOn OutsideFood, AddOn Veteran, AddOn Senior) : IAsyncDisposable
    {
        public TicketSalesService Sales => App.Get<TicketSalesService>();
        public ClaimsPrincipal OwnerPrincipal => Principals.For(Owner);
        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    // Chicago time; one screen of two rows (3 and 4 spots); Jaws at 8 PM on Sep 5 (01:00 UTC Sep 6); prices $10/$25;
    // a $5 fee, a $2 discount and a 10% discount. The clock starts at 2026-09-01 12:00 UTC.
    internal static async Task<Setup> SetUpAsync()
    {
        var app = new TestApp();
        var owner = await app.CreateUserAsync("owner@example.com");
        var theater = await app.CreateTheaterAsync("Starlight", owner.Id);
        PriceSchedule prices;
        await using (var db = app.Db())
        {
            (await db.Theaters.SingleAsync(t => t.Id == theater.Id)).TimeZone = "America/Chicago";
            prices = new PriceSchedule
            {
                TheaterId = theater.Id, Name = "Standard", IsDefault = true,
                Options = [new PriceOption { Name = "One occupant", Price = 10m, SortOrder = 0 }, new PriceOption { Name = "Car load", Price = 25m, SortOrder = 1 }],
            };
            db.PriceSchedules.Add(prices);
            await db.SaveChangesAsync();
        }
        var me = Principals.For(owner);
        var screen = await app.Get<ScreenService>().AddAsync(me, theater.Id, "North");
        await app.Get<ScreenService>().UpdateAsync(me, screen.Id, new ScreenLayoutInput("North", SpotLabelScheme.LetterNumber, [3, 4]));
        var film = await app.Get<ScheduleService>().AddFilmAsync(me, theater.Id, new FilmInput("Jaws", "PG", 124));
        var showing = await app.Get<ScheduleService>().AddShowtimeAsync(me, screen.Id, film.Id, Day, new TimeOnly(20, 0));
        var pricing = app.Get<PricingService>();
        var food = await pricing.AddAddOnAsync(me, theater.Id, new AddOnInput("Outside food", null, AddOnKind.Fee, 5m, true));
        var veteran = await pricing.AddAddOnAsync(me, theater.Id, new AddOnInput("Veteran", null, AddOnKind.Discount, 2m, true));
        var senior = await pricing.AddAddOnAsync(me, theater.Id, new AddOnInput("Senior", null, AddOnKind.PercentDiscount, 10m, true));
        return new Setup(app, owner, theater, screen, showing, prices.Options[0], prices.Options[1], food, veteran, senior);
    }

    internal static async Task<ClaimsPrincipal> BuyerAsync(TestApp app, string email = "buyer@example.com") =>
        Principals.For(await app.CreateUserAsync(email));

    internal static PurchaseInput Buy(PriceOption option, params AddOn[] addOns) => new(option.Id, addOns.Select(a => a.Id).ToList(), Visa);

    [Fact]
    public async Task A_buyer_holds_a_spot_pays_and_gets_an_emailed_ticket_with_a_qr_code()
    {
        await using var s = await SetUpAsync();
        var buyer = await BuyerAsync(s.App);
        var changes = new List<int>();
        s.App.Events.Changed += changes.Add;

        var hold = await s.Sales.HoldAsync(buyer, s.Showing.Id, 2, 3);
        Assert.Equal("B3", hold.SpotLabel);
        Assert.Equal(s.App.Time.GetUtcNow().AddMinutes(Ticket.HoldMinutes), hold.HeldUntil);

        var result = await s.Sales.PurchaseAsync(buyer, hold.TicketId, Buy(s.CarLoad, s.OutsideFood, s.Veteran, s.Senior), TestApp.BaseUri);

        // $25 + $5 − $2 − 10% of $25.
        var charge = Assert.Single(s.App.Payments.Charges);
        Assert.Equal(25.50m, charge.Amount);
        // Only the browser's token reaches the server, with an idempotency key per Paying attempt and the ticket's ids.
        Assert.Equal((2550L, "usd", Visa), (charge.AmountCents, charge.Currency, charge.PaymentMethodId));
        Assert.StartsWith($"ticket-{hold.TicketId}-", charge.IdempotencyKey);
        Assert.Equal(hold.TicketId.ToString(), charge.Metadata["ticket_id"]);
        Assert.Equal(s.Theater.Id.ToString(), charge.Metadata["theater_id"]);
        await using var db = s.App.Db();
        var ticket = await db.Tickets.Include(t => t.AddOns).SingleAsync();
        Assert.Equal(TicketStatus.Sold, ticket.Status);
        Assert.Equal(result.Code, ticket.Code);
        Assert.Equal(("Car load", 25m, 25.50m), (ticket.OptionName, ticket.OptionPrice, ticket.Total));
        Assert.Equal([5m, -2m, -2.50m], ticket.AddOns.OrderBy(a => a.Position).Select(a => a.Effect));
        Assert.Equal(("Visa", "4242", "FAKE-1"), (ticket.CardBrand, ticket.CardLast4, ticket.PaymentReference));
        Assert.Null(ticket.HeldUntil);

        Assert.True(result.ReceiptSent);
        var mail = Assert.Single(s.App.Email.Sent);
        Assert.Equal("buyer@example.com", mail.To);
        Assert.Contains("spot B3", mail.Subject);
        Assert.Contains($"{TestApp.BaseUri}tickets/{result.Code}", mail.Body);
        Assert.Contains("cid:ticket-qr", mail.Body);
        Assert.Contains("no refunds", mail.Body);
        var qr = Assert.Single(s.App.Email.Images.Single()!);
        Assert.Equal(("ticket-qr", "image/png"), (qr.ContentId, qr.ContentType));
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], qr.Content[..4]); // PNG

        var other = await BuyerAsync(s.App, "other@example.com");
        Assert.Equal(SpotState.Sold, (await s.Sales.GetAvailabilityAsync(other, s.Showing.Id))[2, 3]);
        Assert.Equal([s.Showing.Id, s.Showing.Id], changes); // held, then sold
        var mine = Assert.Single(await s.Sales.ListMyTicketsAsync(buyer));
        Assert.Equal("B3", mine.Ticket.SpotLabel);
    }

    [Fact]
    public async Task The_first_to_hold_a_spot_wins_and_the_second_buyer_must_choose_again()
    {
        await using var s = await SetUpAsync();
        var first = await BuyerAsync(s.App, "first@example.com");
        var second = await BuyerAsync(s.App, "second@example.com");

        await s.Sales.HoldAsync(first, s.Showing.Id, 1, 2);
        var ex = await Assert.ThrowsAsync<AppValidationException>(() => s.Sales.HoldAsync(second, s.Showing.Id, 1, 2));
        Assert.Contains("someone else just took spot A2", ex.Message);
        Assert.Contains("choose another spot", ex.Message);

        var seen = await s.Sales.GetAvailabilityAsync(second, s.Showing.Id);
        Assert.Equal(SpotState.Held, seen[1, 2]);
        Assert.Null(seen.MyHold);
        Assert.Equal(SpotState.Mine, (await s.Sales.GetAvailabilityAsync(first, s.Showing.Id))[1, 2]);

        await s.Sales.HoldAsync(second, s.Showing.Id, 1, 3); // another spot is fine
    }

    [Fact]
    public async Task A_sold_spot_can_never_be_held_again()
    {
        await using var s = await SetUpAsync();
        var buyer = await BuyerAsync(s.App);
        var hold = await s.Sales.HoldAsync(buyer, s.Showing.Id, 1, 1);
        await s.Sales.PurchaseAsync(buyer, hold.TicketId, Buy(s.Single), TestApp.BaseUri);
        s.App.Time.Advance(TimeSpan.FromHours(1));
        await s.Sales.ReleaseExpiredHoldsAsync();

        var other = await BuyerAsync(s.App, "other@example.com");
        await Assert.ThrowsAsync<AppValidationException>(() => s.Sales.HoldAsync(other, s.Showing.Id, 1, 1));
        await Assert.ThrowsAsync<AppValidationException>(() => s.Sales.HoldAsync(buyer, s.Showing.Id, 1, 1));
    }

    [Fact]
    public async Task An_expired_hold_goes_back_on_sale_and_can_no_longer_be_paid_for()
    {
        await using var s = await SetUpAsync();
        var slow = await BuyerAsync(s.App, "slow@example.com");
        var hold = await s.Sales.HoldAsync(slow, s.Showing.Id, 2, 1);

        s.App.Time.Advance(TimeSpan.FromMinutes(Ticket.HoldMinutes));
        var fast = await BuyerAsync(s.App, "fast@example.com");
        Assert.Equal(SpotState.Available, (await s.Sales.GetAvailabilityAsync(fast, s.Showing.Id))[2, 1]);
        Assert.Null((await s.Sales.GetAvailabilityAsync(slow, s.Showing.Id)).MyHold);

        var ex = await Assert.ThrowsAsync<AppValidationException>(() =>
            s.Sales.PurchaseAsync(slow, hold.TicketId, Buy(s.Single), TestApp.BaseUri));
        Assert.Contains("ran out", ex.Message);
        Assert.Empty(s.App.Payments.Charges);

        await s.Sales.HoldAsync(fast, s.Showing.Id, 2, 1); // replaces the expired hold
        await using var db = s.App.Db();
        Assert.Equal("fast@example.com", (await db.Tickets.Include(t => t.User).SingleAsync()).User!.Email);
    }

    [Fact]
    public async Task Expired_holds_are_released_and_their_showings_notified()
    {
        await using var s = await SetUpAsync();
        var buyer = await BuyerAsync(s.App);
        await s.Sales.HoldAsync(buyer, s.Showing.Id, 1, 1);
        var changes = new List<int>();
        s.App.Events.Changed += changes.Add;

        Assert.Equal(0, await s.Sales.ReleaseExpiredHoldsAsync());
        s.App.Time.Advance(TimeSpan.FromMinutes(Ticket.HoldMinutes) + TimeSpan.FromSeconds(1));
        Assert.Equal(1, await s.Sales.ReleaseExpiredHoldsAsync());

        await using var db = s.App.Db();
        Assert.Empty(db.Tickets);
        Assert.Equal([s.Showing.Id], changes);
    }

    [Fact]
    public async Task Holding_another_spot_lets_go_of_the_first_and_holding_again_does_not_extend_it()
    {
        await using var s = await SetUpAsync();
        var buyer = await BuyerAsync(s.App);
        var first = await s.Sales.HoldAsync(buyer, s.Showing.Id, 1, 1);
        s.App.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(first, await s.Sales.HoldAsync(buyer, s.Showing.Id, 1, 1));

        await s.Sales.HoldAsync(buyer, s.Showing.Id, 1, 2);

        var seen = await s.Sales.GetAvailabilityAsync(buyer, s.Showing.Id);
        Assert.Equal(SpotState.Available, seen[1, 1]);
        Assert.Equal("A2", seen.MyHold!.SpotLabel);

        await s.Sales.ReleaseHoldAsync(buyer, seen.MyHold.TicketId);
        Assert.Empty((await s.Sales.GetAvailabilityAsync(buyer, s.Showing.Id)).Spots);
    }

    [Fact]
    public async Task A_buyer_cannot_hold_another_spot_while_paying_for_one()
    {
        await using var s = await SetUpAsync();
        var buyer = await BuyerAsync(s.App);
        var hold = await s.Sales.HoldAsync(buyer, s.Showing.Id, 1, 1);
        await using (var db = s.App.Db())
        {
            (await db.Tickets.SingleAsync()).Status = TicketStatus.Paying; // mid-charge, e.g. in another tab
            await db.SaveChangesAsync();
        }

        var ex = await Assert.ThrowsAsync<AppValidationException>(() => s.Sales.HoldAsync(buyer, s.Showing.Id, 1, 2));
        Assert.Contains("Finish that checkout", ex.Message);

        // A payment stuck past its hold window (e.g. the server crashed mid-charge) doesn't block them forever.
        s.App.Time.Advance(TimeSpan.FromMinutes(Ticket.HoldMinutes));
        var next = await s.Sales.HoldAsync(buyer, s.Showing.Id, 1, 2);
        Assert.NotEqual(hold.TicketId, next.TicketId);
    }

    [Fact]
    public async Task A_declined_card_keeps_the_hold_and_sells_nothing()
    {
        await using var s = await SetUpAsync();
        var buyer = await BuyerAsync(s.App);
        var hold = await s.Sales.HoldAsync(buyer, s.Showing.Id, 1, 1);
        s.App.Payments.DeclineWith = "insufficient funds";

        var ex = await Assert.ThrowsAsync<AppValidationException>(() =>
            s.Sales.PurchaseAsync(buyer, hold.TicketId, Buy(s.Single), TestApp.BaseUri));
        Assert.Contains("insufficient funds", ex.Message);

        await using (var db = s.App.Db())
            Assert.Equal(TicketStatus.Held, (await db.Tickets.SingleAsync()).Status);
        Assert.Empty(s.App.Email.Sent);

        s.App.Payments.DeclineWith = null;
        await s.Sales.PurchaseAsync(buyer, hold.TicketId, Buy(s.Single), TestApp.BaseUri);
        await using (var db = s.App.Db())
            Assert.Equal(TicketStatus.Sold, (await db.Tickets.SingleAsync()).Status);
    }

    // The server takes only a payment method token; anything else (missing, or not shaped like a token, such as a card
    // number) is refused before charging.
    [Theory]
    [InlineData(null, "Enter your card details")]
    [InlineData("  ", "Enter your card details")]
    [InlineData("4242424242424242", "didn't come through")]
    [InlineData("pm_<script>", "didn't come through")]
    [InlineData("pm_4242424242424242", "didn't come through")] // a card number dressed up as a token
    [InlineData("pm_test_visa_4242_4242424242424", "didn't come through")]
    public async Task Missing_or_malformed_card_tokens_are_rejected_before_charging(string? token, string message)
    {
        await using var s = await SetUpAsync();
        var buyer = await BuyerAsync(s.App);
        var hold = await s.Sales.HoldAsync(buyer, s.Showing.Id, 1, 1);

        var ex = await Assert.ThrowsAsync<AppValidationException>(() => s.Sales.PurchaseAsync(buyer, hold.TicketId,
            new PurchaseInput(s.Single.Id, [], token), TestApp.BaseUri));

        Assert.Contains(message, ex.Message);
        Assert.Empty(s.App.Payments.Charges);
    }

    [Fact]
    public async Task A_ticket_discounted_to_nothing_needs_no_card()
    {
        await using var s = await SetUpAsync();
        var free = await s.App.Get<PricingService>().AddAddOnAsync(s.OwnerPrincipal, s.Theater.Id,
            new AddOnInput("Comp", null, AddOnKind.PercentDiscount, 100m, true));
        var buyer = await BuyerAsync(s.App);
        var hold = await s.Sales.HoldAsync(buyer, s.Showing.Id, 1, 1);

        await s.Sales.PurchaseAsync(buyer, hold.TicketId, new PurchaseInput(s.Single.Id, [free.Id], null), TestApp.BaseUri);

        Assert.Empty(s.App.Payments.Charges);
        await using var db = s.App.Db();
        var ticket = await db.Tickets.SingleAsync();
        Assert.Equal((TicketStatus.Sold, 0m, null), (ticket.Status, ticket.Total, ticket.CardLast4));
    }

    [Fact]
    public async Task Only_offered_choices_can_be_bought()
    {
        await using var s = await SetUpAsync();
        var buyer = await BuyerAsync(s.App);
        var hold = await s.Sales.HoldAsync(buyer, s.Showing.Id, 1, 1);
        await s.App.Get<PricingService>().UpdateAddOnAsync(s.OwnerPrincipal, s.Veteran.Id,
            new AddOnInput("Veteran", null, AddOnKind.Discount, 2m, IsActive: false));

        await Assert.ThrowsAsync<AppValidationException>(() =>
            s.Sales.PurchaseAsync(buyer, hold.TicketId, Buy(s.Single, s.Veteran), TestApp.BaseUri));
        await Assert.ThrowsAsync<AppValidationException>(() =>
            s.Sales.PurchaseAsync(buyer, hold.TicketId, new PurchaseInput(-1, [], Visa), TestApp.BaseUri));
        Assert.Empty(s.App.Payments.Charges);
    }

    [Fact]
    public async Task Someone_else_cannot_pay_for_or_release_your_hold()
    {
        await using var s = await SetUpAsync();
        var buyer = await BuyerAsync(s.App);
        var hold = await s.Sales.HoldAsync(buyer, s.Showing.Id, 1, 1);
        var other = await BuyerAsync(s.App, "other@example.com");

        await Assert.ThrowsAsync<AppValidationException>(() =>
            s.Sales.PurchaseAsync(other, hold.TicketId, Buy(s.Single), TestApp.BaseUri));
        await s.Sales.ReleaseHoldAsync(other, hold.TicketId);

        Assert.Equal(SpotState.Mine, (await s.Sales.GetAvailabilityAsync(buyer, s.Showing.Id))[1, 1]);
    }

    [Fact]
    public async Task Spots_off_the_layout_and_showings_that_started_are_not_sold()
    {
        await using var s = await SetUpAsync();
        var buyer = await BuyerAsync(s.App);

        await Assert.ThrowsAsync<NotFoundException>(() => s.Sales.HoldAsync(buyer, s.Showing.Id, 1, 4)); // row 1 has 3
        await Assert.ThrowsAsync<NotFoundException>(() => s.Sales.HoldAsync(buyer, s.Showing.Id, 3, 1));

        s.App.Time.Advance(TimeSpan.FromDays(5)); // Sep 6 12:00 UTC: started
        var ex = await Assert.ThrowsAsync<AppValidationException>(() => s.Sales.HoldAsync(buyer, s.Showing.Id, 1, 1));
        Assert.Contains("started", ex.Message);
        Assert.Empty((await s.Sales.ListOnSaleAsync(buyer, s.Theater.Slug)).Showings);
    }

    [Fact]
    public async Task Nothing_is_sold_without_a_payment_processor()
    {
        await using var s = await SetUpAsync();
        s.App.Payments.IsAvailable = false;
        var buyer = await BuyerAsync(s.App);

        Assert.False((await s.Sales.GetShowingAsync(buyer, s.Showing.Id)).OnSale);
        await Assert.ThrowsAsync<AppValidationException>(() => s.Sales.HoldAsync(buyer, s.Showing.Id, 1, 1));
    }

    [Fact]
    public async Task Signed_out_visitors_cannot_buy()
    {
        await using var s = await SetUpAsync();
        await Assert.ThrowsAsync<AccessDeniedException>(() => s.Sales.HoldAsync(Principals.Anonymous, s.Showing.Id, 1, 1));
        await Assert.ThrowsAsync<AccessDeniedException>(() => s.Sales.GetAvailabilityAsync(Principals.Anonymous, s.Showing.Id));
    }

    // --- The gate ---

    internal static async Task<string> SellAsync(Setup s, ClaimsPrincipal buyer, int row = 1, int spot = 1)
    {
        var hold = await s.Sales.HoldAsync(buyer, s.Showing.Id, row, spot);
        return (await s.Sales.PurchaseAsync(buyer, hold.TicketId, Buy(s.Single), TestApp.BaseUri)).Code;
    }

    [Fact]
    public async Task Only_the_buyer_and_staff_who_can_admit_guests_can_look_up_a_ticket()
    {
        await using var s = await SetUpAsync();
        var buyer = await BuyerAsync(s.App);
        var code = await SellAsync(s, buyer);
        var gate = await s.App.CreateUserAsync("gate@example.com", employeeTheaterId: s.Theater.Id);
        var usher = await s.App.CreateUserAsync("usher@example.com", employeeTheaterId: s.Theater.Id);
        await s.App.GrantAsync(gate, AdmitGuests);

        var own = await s.Sales.GetByCodeAsync(buyer, code);
        Assert.True(own.IsBuyer);
        Assert.False(own.CanAdmit);
        var staff = await s.Sales.GetByCodeAsync(Principals.For(gate), code);
        Assert.True(staff.CanAdmit);
        Assert.False(staff.IsBuyer);

        await Assert.ThrowsAsync<AccessDeniedException>(() => s.Sales.GetByCodeAsync(Principals.For(usher), code));
        var stranger = await BuyerAsync(s.App, "x@example.com");
        await Assert.ThrowsAsync<AccessDeniedException>(() => s.Sales.GetByCodeAsync(stranger, code));
        await Assert.ThrowsAsync<AccessDeniedException>(() => s.Sales.AdmitAsync(Principals.For(usher), code));
        await Assert.ThrowsAsync<AccessDeniedException>(() => s.Sales.AdmitAsync(buyer, code));
        await Assert.ThrowsAsync<NotFoundException>(() => s.Sales.GetByCodeAsync(buyer, "not-a-code"));
    }

    [Fact]
    public async Task A_ticket_admits_once_and_only_on_the_day_of_its_showing()
    {
        await using var s = await SetUpAsync();
        var buyer = await BuyerAsync(s.App);
        var code = await SellAsync(s, buyer);
        var gate = await s.App.CreateUserAsync("gate@example.com", employeeTheaterId: s.Theater.Id);
        await s.App.GrantAsync(gate, AdmitGuests);
        var staff = Principals.For(gate);

        // Showing starts 01:00 UTC Sep 6; gates open 3 hours before.
        Assert.Contains("Not valid today: this ticket is for Sat, Sep 5", (await s.Sales.GetByCodeAsync(staff, code)).AdmitProblem);
        await Assert.ThrowsAsync<AppValidationException>(() => s.Sales.AdmitAsync(staff, code));

        s.App.Time.SetUtcNow(new DateTimeOffset(2026, 9, 5, 22, 30, 0, TimeSpan.Zero));
        Assert.Null((await s.Sales.GetByCodeAsync(staff, code)).AdmitProblem);
        await s.Sales.AdmitAsync(staff, code);

        var used = await Assert.ThrowsAsync<AppValidationException>(() => s.Sales.AdmitAsync(staff, code));
        Assert.Contains("Already used", used.Message);
        Assert.NotNull((await s.Sales.GetByCodeAsync(buyer, code)).View.AdmittedLocal);
    }

    [Fact]
    public async Task A_ticket_cannot_be_used_after_its_showing()
    {
        await using var s = await SetUpAsync();
        var code = await SellAsync(s, await BuyerAsync(s.App));
        s.App.Time.SetUtcNow(new DateTimeOffset(2026, 9, 6, 20, 0, 0, TimeSpan.Zero)); // the next evening

        var ex = await Assert.ThrowsAsync<AppValidationException>(() => s.Sales.AdmitAsync(s.OwnerPrincipal, code));
        Assert.Contains("can't be used on a later date", ex.Message);
    }

    [Fact]
    public async Task The_receipt_can_be_sent_again_by_the_buyer_only()
    {
        await using var s = await SetUpAsync();
        var buyer = await BuyerAsync(s.App);
        var code = await SellAsync(s, buyer);

        Assert.True(await s.Sales.ResendReceiptAsync(buyer, code, TestApp.BaseUri));
        Assert.Equal(2, s.App.Email.Sent.Count);
        await Assert.ThrowsAsync<NotFoundException>(() => s.Sales.ResendReceiptAsync(s.OwnerPrincipal, code, TestApp.BaseUri));
    }

    // --- Sales are records ---

    [Fact]
    public async Task A_showing_with_sold_tickets_cannot_be_removed_or_moved_to_another_screen()
    {
        await using var s = await SetUpAsync();
        await SellAsync(s, await BuyerAsync(s.App));
        var schedule = s.App.Get<ScheduleService>();
        var south = await s.App.Get<ScreenService>().AddAsync(s.OwnerPrincipal, s.Theater.Id, "South");
        var film = (await schedule.ListFilmsAsync(s.OwnerPrincipal, s.Theater.Id)).Single();

        await Assert.ThrowsAsync<AppValidationException>(() => schedule.DeleteShowtimeAsync(s.OwnerPrincipal, s.Showing.Id));
        await Assert.ThrowsAsync<AppValidationException>(() => schedule.UpdateShowtimeAsync(s.OwnerPrincipal, s.Showing.Id,
            new ShowtimeInput(south.Id, [film.Id], Day, new TimeOnly(20, 0))));
        // Same screen, later time: fine; the ticket follows its showing.
        await schedule.UpdateShowtimeAsync(s.OwnerPrincipal, s.Showing.Id, new ShowtimeInput(s.Screen.Id, [film.Id], Day, new TimeOnly(20, 30)));

        s.App.Time.Advance(TimeSpan.FromDays(10));
        await Assert.ThrowsAsync<AppValidationException>(() => schedule.DeleteFilmAsync(s.OwnerPrincipal, film.Id));
    }

    [Fact]
    public async Task A_showing_with_only_holds_can_be_removed()
    {
        await using var s = await SetUpAsync();
        await s.Sales.HoldAsync(await BuyerAsync(s.App), s.Showing.Id, 1, 1);

        await s.App.Get<ScheduleService>().DeleteShowtimeAsync(s.OwnerPrincipal, s.Showing.Id);

        await using var db = s.App.Db();
        Assert.Empty(db.Tickets);
        Assert.Empty(db.Showtimes);
    }

    [Fact]
    public async Task A_screen_keeps_spots_and_labels_that_upcoming_tickets_use()
    {
        await using var s = await SetUpAsync();
        await SellAsync(s, await BuyerAsync(s.App), row: 2, spot: 4);
        var screens = s.App.Get<ScreenService>();

        var removed = await Assert.ThrowsAsync<AppValidationException>(() =>
            screens.UpdateAsync(s.OwnerPrincipal, s.Screen.Id, new ScreenLayoutInput("North", SpotLabelScheme.LetterNumber, [3, 3])));
        Assert.Contains("B4", removed.Message);
        await Assert.ThrowsAsync<AppValidationException>(() =>
            screens.UpdateAsync(s.OwnerPrincipal, s.Screen.Id, new ScreenLayoutInput("North", SpotLabelScheme.Numeric, [3, 4])));
        await screens.UpdateAsync(s.OwnerPrincipal, s.Screen.Id, new ScreenLayoutInput("North field", SpotLabelScheme.LetterNumber, [5, 4, 6]));

        s.App.Time.Advance(TimeSpan.FromDays(10)); // the showing is past
        await screens.UpdateAsync(s.OwnerPrincipal, s.Screen.Id, new ScreenLayoutInput("North", SpotLabelScheme.Numeric, [2]));
    }

    [Fact]
    public async Task Deleting_a_theater_deletes_its_ticket_sales()
    {
        await using var s = await SetUpAsync();
        await SellAsync(s, await BuyerAsync(s.App));
        var admin = Principals.For(await s.App.CreateUserAsync("admin@example.com", admin: true), admin: true);

        await s.App.Get<TheaterService>().DeleteAsync(admin, s.Theater.Id);

        await using var db = s.App.Db();
        Assert.Empty(db.Tickets);
        Assert.Empty(db.TicketAddOns);
    }

    [Theory]
    [InlineData("abc_DEF-123", "abc_DEF-123")]
    [InlineData("  abc_DEF-123 ", "abc_DEF-123")]
    [InlineData("https://drive-in.online/tickets/abc_DEF-123", "abc_DEF-123")]
    [InlineData("https://drive-in.online/tickets/abc_DEF-123?new=sent", "abc_DEF-123")]
    [InlineData("https://drive-in.online/tickets/abc_DEF-123/#top", "abc_DEF-123")]
    [InlineData("", "")]
    [InlineData("https://drive-in.online/tickets/abc%ZZ%", "abc%ZZ%")] // malformed escapes are left as typed, not thrown
    [InlineData("https://drive-in.online/tickets/abc%2Dx", "abc-x")]
    public void Gate_staff_can_type_a_code_or_paste_a_ticket_link(string input, string code) =>
        Assert.Equal(code, TicketLinks.CodeFrom(input));

    [Fact]
    public async Task A_buyer_without_an_email_address_is_not_charged()
    {
        await using var s = await SetUpAsync();
        var user = await s.App.CreateUserAsync("blank@example.com");
        var buyer = Principals.For(user);
        var hold = await s.Sales.HoldAsync(buyer, s.Showing.Id, 1, 1);
        await using (var db = s.App.Db())
        {
            (await db.Users.SingleAsync(u => u.Id == user.Id)).Email = "  ";
            await db.SaveChangesAsync();
        }

        var ex = await Assert.ThrowsAsync<AppValidationException>(() =>
            s.Sales.PurchaseAsync(buyer, hold.TicketId, Buy(s.Single), TestApp.BaseUri));

        Assert.Contains("email address", ex.Message);
        Assert.Empty(s.App.Payments.Charges);
    }

    // --- Pricing and cards ---

    [Fact]
    public void Quotes_add_fees_subtract_discounts_and_never_go_below_zero()
    {
        var option = new PriceOption { Name = "Car", Price = 15m };
        AddOn Make(AddOnKind kind, decimal amount) => new() { Name = kind.ToString(), Kind = kind, Amount = amount };

        Assert.Equal(15m, TicketQuote.For(option, []).Total);
        Assert.Equal(13.33m, TicketQuote.For(option, [Make(AddOnKind.PercentDiscount, 11.15m)]).Total); // 1.6725 rounds to 1.67
        Assert.Equal(18m, TicketQuote.For(option, [Make(AddOnKind.Fee, 5m), Make(AddOnKind.Discount, 2m)]).Total);
        Assert.Equal(0m, TicketQuote.For(option, [Make(AddOnKind.Discount, 20m)]).Total);
    }

    [Theory]
    [InlineData("pm_test_visa_4242_ab12", "Visa", "4242", false)]
    [InlineData("pm_test_mastercard_4444_ab12", "Mastercard", "4444", false)]
    [InlineData("pm_test_amex_0005_ab12", "Amex", "0005", false)]
    [InlineData("pm_test_decline_0002_ab12", "Card", "0002", true)]
    public void Test_card_tokens_carry_the_brand_and_last_four(string token, string brand, string last4, bool declines)
    {
        Assert.Equal(new TestCardTokens.TestCard(brand, last4, declines), TestCardTokens.Parse(token));
    }

    [Theory]
    [InlineData("pm_1QabcXYZ")] // a real Stripe token isn't a test card
    [InlineData("pm_test_visa_42_ab")]
    [InlineData(null)]
    public void Other_tokens_are_not_test_cards(string? token) => Assert.Null(TestCardTokens.Parse(token));

    [Fact]
    public async Task The_dummy_processor_approves_test_cards_and_card_present_charges_and_declines_the_rest()
    {
        var dummy = new DummyPaymentProcessor(Microsoft.Extensions.Logging.Abstractions.NullLogger<DummyPaymentProcessor>.Instance);
        PaymentRequest Charge(string? pm) => new(1000, "usd", "test", pm, "key", new Dictionary<string, string>());

        var approved = await dummy.ChargeAsync(Charge("pm_test_visa_4242_ab12"));
        Assert.Equal((true, "Visa", "4242"), (approved.Approved, approved.CardBrand, approved.CardLast4));
        Assert.True((await dummy.ChargeAsync(Charge(null))).Approved); // at the gate
        Assert.False((await dummy.ChargeAsync(Charge("pm_test_decline_0002_ab12"))).Approved);
        Assert.False((await dummy.ChargeAsync(Charge("pm_1QabcXYZ"))).Approved);
        Assert.Equal(PaymentClientKind.Test, dummy.Client.Kind);
    }

    [Theory]
    [InlineData(" pk_test_a ", " sk_test_b\n", true)] // pasted with whitespace
    [InlineData(null, null, false)]
    [InlineData("pk_test_a", "sk_test_b", true)]
    [InlineData("pk_live_a", "sk_live_b", false)] // no real money until the integration has been tried
    [InlineData("pk_test_a", "", false)]
    [InlineData("pk_test_", "sk_test_", false)] // placeholders
    [InlineData("", "", false)]
    public void Stripe_accepts_only_test_mode_keys(string? publishable, string? secret, bool ok)
    {
        // Configuration binding can set null (e.g. "PublishableKey": null in a JSON file).
        var options = new StripeOptions { PublishableKey = publishable!, SecretKey = secret! };
        if (ok)
            StripeOptions.Validate(options);
        else
            Assert.Throws<InvalidOperationException>(() => StripeOptions.Validate(options));
    }

    [Fact]
    public async Task Stripe_declines_charges_under_its_minimum_without_calling_it()
    {
        var stripe = new StripePaymentProcessor(new Stripe.StripeClient("sk_test_x"), new StripeTerminalReader(),
            Microsoft.Extensions.Options.Options.Create(new StripeOptions()), Microsoft.Extensions.Options.Options.Create(new PaymentOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<StripePaymentProcessor>.Instance);

        var result = await stripe.ChargeAsync(new PaymentRequest(30, "usd", "x", "pm_1Qabc", "k", new Dictionary<string, string>()));

        Assert.False(result.Approved);
        Assert.Contains("at least 0.50 USD", result.DeclineReason);
    }

    [Theory]
    [InlineData("USD ", "usd")]
    [InlineData("cad", "cad")]
    [InlineData("", "usd")]
    public void The_currency_is_kept_as_stripe_wants_it(string configured, string used) =>
        Assert.Equal(used, new PaymentOptions { Currency = configured }.Currency);

    [Fact]
    public void Stripes_card_element_uses_the_configured_currency()
    {
        var stripe = new StripePaymentProcessor(new Stripe.StripeClient("sk_test_x"), new StripeTerminalReader(),
            Microsoft.Extensions.Options.Options.Create(new StripeOptions { PublishableKey = "pk_test_x", SecretKey = "sk_test_x" }),
            Microsoft.Extensions.Options.Options.Create(new PaymentOptions { Currency = "cad" }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<StripePaymentProcessor>.Instance);

        Assert.Equal(new PaymentClient(PaymentClientKind.Stripe, "pk_test_x", "cad"), stripe.Client);
    }

    [Theory]
    [InlineData(25.50, 2550)]
    [InlineData(0.005, 1)]
    [InlineData(500, 50000)]
    public void Amounts_are_charged_in_cents(decimal amount, long cents) => Assert.Equal(cents, PaymentRequest.ToCents(amount));
}
