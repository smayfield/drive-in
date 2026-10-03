using System.Security.Claims;
using DriveIn.Web.Data;
using DriveIn.Web.Services;
using Microsoft.EntityFrameworkCore;
using static DriveIn.Web.Authorization.TheaterPermissions;
using static DriveIn.Web.Tests.TicketSalesTests;

namespace DriveIn.Web.Tests;

public class GiftCardTests
{
    // 5 PM in Chicago on the day of the 8 PM showing (see TicketSalesTests.SetUpAsync).
    private static readonly DateTimeOffset ShowDayAfternoon = new(2026, 9, 5, 22, 0, 0, TimeSpan.Zero);
    private const string Visa = "pm_test_visa_4242_0001"; // a test card token (see TestCardTokens)

    private static async Task<Setup> SetUpWithGiftCardsAsync()
    {
        var s = await SetUpAsync();
        await s.App.Get<TheaterService>().UpdateGiftCardSettingsAsync(s.OwnerPrincipal, s.Theater.Id, true);
        return s;
    }

    private static async Task<ClaimsPrincipal> EmployeeAsync(Setup s, params string[] permissions)
    {
        var employee = await s.App.CreateUserAsync($"emp{Guid.NewGuid():N}@example.com", employeeTheaterId: s.Theater.Id);
        if (permissions.Length > 0)
            await s.App.GrantAsync(employee, permissions);
        return Principals.For(employee);
    }

    private static async Task<GiftCard> BuyCardAsync(Setup s, ClaimsPrincipal buyer, decimal amount, string? recipientEmail = null)
    {
        var result = await s.Sales.PurchaseGiftCardAsync(buyer, s.Theater.Id,
            new GiftCardPurchaseInput(amount, "Sam", recipientEmail, "Enjoy!", Visa), TestApp.BaseUri);
        return result.Card;
    }

    private static async Task<Ticket> BuyTicketAsync(Setup s, ClaimsPrincipal buyer, PriceOption option, string? giftCode, int row = 1, int spot = 1,
        string? card = null)
    {
        var hold = await s.Sales.HoldAsync(buyer, s.Showing.Id, row, spot);
        var result = await s.Sales.PurchaseAsync(buyer, hold.TicketId, new PurchaseInput(option.Id, [], card, giftCode), TestApp.BaseUri);
        await using var db = s.App.Db();
        return await db.Tickets.SingleAsync(t => t.Code == result.Code);
    }

    private static async Task<GiftCard> ReloadAsync(Setup s, int id)
    {
        await using var db = s.App.Db();
        return await db.GiftCards.Include(g => g.Transactions).SingleAsync(g => g.Id == id);
    }

    // --- Selling ---

    [Fact]
    public async Task A_buyer_pays_by_card_and_gets_a_gift_card_whose_code_is_emailed()
    {
        await using var s = await SetUpWithGiftCardsAsync();
        var buyer = await BuyerAsync(s.App);

        var result = await s.Sales.PurchaseGiftCardAsync(buyer, s.Theater.Id,
            new GiftCardPurchaseInput(40m, "Sam", "sam@example.com", "Enjoy the show!", Visa), TestApp.BaseUri);

        var charge = Assert.Single(s.App.Payments.Charges);
        Assert.Equal(40m, charge.Amount);
        var card = await ReloadAsync(s, result.Card.Id);
        Assert.Equal((40m, 40m, s.Theater.Id), (card.InitialAmount, card.Balance, card.TheaterId));
        Assert.Matches($"^[{ShortCodes.Alphabet}]{{16}}$", card.Code);
        Assert.Equal(("Visa", "4242", "FAKE-1", false), (card.CardBrand, card.CardLast4, card.PaymentReference, card.IsTest));
        var purchase = Assert.Single(card.Transactions);
        Assert.Equal((GiftCardTransactionKind.Purchase, 40m, 40m), (purchase.Kind, purchase.Amount, purchase.BalanceAfter));
        Assert.True(result.BuyerEmailed);
        Assert.True(result.RecipientEmailed);

        Assert.Equal(2, s.App.Email.Sent.Count);
        var toBuyer = s.App.Email.Sent.Single(m => m.To == "buyer@example.com");
        var toRecipient = s.App.Email.Sent.Single(m => m.To == "sam@example.com");
        Assert.Contains(GiftCardCodes.Format(card.Code), toBuyer.Body);
        Assert.Contains(GiftCardCodes.Format(card.Code), toRecipient.Body);
        Assert.Contains("Enjoy the show!", toRecipient.Body);
        Assert.DoesNotContain("Enjoy the show!", toBuyer.Body);
        Assert.Contains("doesn't expire", toBuyer.Body);

        var mine = Assert.Single(await s.Sales.ListMyGiftCardsAsync(buyer));
        Assert.Equal(card.Code, mine.Card.Code);
        Assert.Empty(await s.Sales.ListMyGiftCardsAsync(await BuyerAsync(s.App, "other@example.com")));
    }

    [Fact]
    public async Task A_new_gift_card_never_reuses_a_code_already_sold_at_any_theater()
    {
        await using var s = await SetUpWithGiftCardsAsync();
        var buyer = await BuyerAsync(s.App);
        var first = await BuyCardAsync(s, buyer, 25m);
        var codes = new Queue<string>([first.Code, first.Code, "ABCDEFGHJKMNPQRS"]);
        s.Sales.NewGiftCardCode = codes.Dequeue;

        var second = await BuyCardAsync(s, buyer, 25m);

        Assert.Equal("ABCDEFGHJKMNPQRS", second.Code);
        await using var db = s.App.Db();
        Assert.Equal(2, await db.GiftCards.Select(g => g.Code).Distinct().CountAsync());
        // And the database backs it up: the code is unique across all theaters, not per theater.
        var index = Assert.Single(db.Model.FindEntityType(typeof(GiftCard))!.GetIndexes(),
            i => i.Properties.Select(p => p.Name).SequenceEqual([nameof(GiftCard.Code)]));
        Assert.True(index.IsUnique);
    }

    [Fact]
    public async Task Gift_cards_are_only_sold_when_the_theater_turns_them_on()
    {
        await using var s = await SetUpAsync(); // off by default
        var buyer = await BuyerAsync(s.App);

        var offer = await s.Sales.GetGiftCardOfferAsync(buyer, s.Theater.Slug);
        Assert.False(offer.Available);
        await Assert.ThrowsAsync<AppValidationException>(() => BuyCardAsync(s, buyer, 25m));
        Assert.Empty(s.App.Payments.Charges);

        await s.App.Get<TheaterService>().UpdateGiftCardSettingsAsync(s.OwnerPrincipal, s.Theater.Id, true);
        Assert.True((await s.Sales.GetGiftCardOfferAsync(buyer, s.Theater.Slug)).Available);
        await BuyCardAsync(s, buyer, 25m);
    }

    [Fact]
    public async Task Gift_cards_can_not_be_sold_without_a_payment_processor()
    {
        await using var s = await SetUpWithGiftCardsAsync();
        s.App.Payments.IsAvailable = false;

        var offer = await s.Sales.GetGiftCardOfferAsync(await BuyerAsync(s.App), s.Theater.Slug);

        Assert.False(offer.Available);
    }

    [Theory]
    [InlineData(4.99)]
    [InlineData(500.01)]
    [InlineData(25.005)]
    [InlineData(0)]
    [InlineData(-10)]
    public async Task The_amount_must_be_within_the_limits_and_in_whole_cents(double amount)
    {
        await using var s = await SetUpWithGiftCardsAsync();
        var buyer = await BuyerAsync(s.App);

        await Assert.ThrowsAsync<AppValidationException>(() => BuyCardAsync(s, buyer, (decimal)amount));

        Assert.Empty(s.App.Payments.Charges);
        await using var db = s.App.Db();
        Assert.Empty(db.GiftCards);
    }

    [Fact]
    public async Task A_declined_card_buys_nothing()
    {
        await using var s = await SetUpWithGiftCardsAsync();
        s.App.Payments.DeclineWith = "insufficient funds";
        var buyer = await BuyerAsync(s.App);

        var ex = await Assert.ThrowsAsync<AppValidationException>(() => BuyCardAsync(s, buyer, 25m));

        Assert.Contains("insufficient funds", ex.Message);
        await using var db = s.App.Db();
        Assert.Empty(db.GiftCards);
        Assert.Empty(s.App.Email.Sent);
    }

    [Fact]
    public async Task A_bad_recipient_email_is_rejected_before_charging()
    {
        await using var s = await SetUpWithGiftCardsAsync();
        var buyer = await BuyerAsync(s.App);

        await Assert.ThrowsAsync<AppValidationException>(() => BuyCardAsync(s, buyer, 25m, recipientEmail: "not an email"));

        Assert.Empty(s.App.Payments.Charges);
    }

    [Fact]
    public async Task A_failed_email_does_not_undo_the_sale()
    {
        await using var s = await SetUpWithGiftCardsAsync();
        s.App.Email.FailWith = new InvalidOperationException("SES down");

        var result = await s.Sales.PurchaseGiftCardAsync(await BuyerAsync(s.App), s.Theater.Id,
            new GiftCardPurchaseInput(25m, null, null, null, Visa), TestApp.BaseUri);

        Assert.False(result.BuyerEmailed);
        Assert.Null(result.RecipientEmailed);
        Assert.Equal(25m, (await ReloadAsync(s, result.Card.Id)).Balance);
    }

    // --- Paying online ---

    [Fact]
    public async Task A_gift_card_worth_less_than_the_ticket_pays_part_and_the_card_pays_the_rest()
    {
        await using var s = await SetUpWithGiftCardsAsync();
        var buyer = await BuyerAsync(s.App);
        var card = await BuyCardAsync(s, buyer, 20m);
        s.App.Payments.Charges.Clear();

        var ticket = await BuyTicketAsync(s, buyer, s.CarLoad, GiftCardCodes.Format(card.Code).ToLowerInvariant(), card: Visa);

        Assert.Equal(5m, Assert.Single(s.App.Payments.Charges).Amount); // $25 - $20
        Assert.Equal((25m, 20m, 5m), (ticket.Total, ticket.GiftCardAmount, ticket.CardAmount));
        Assert.Equal((card.Id, card.Last4), (ticket.GiftCardId, ticket.GiftCardLast4));
        Assert.Equal($"Paid with gift card ending {card.Last4} ($20.00) and Visa ending 4242 ($5.00)", TicketReceipt.PaidWith(ticket));
        var after = await ReloadAsync(s, card.Id);
        Assert.Equal(0m, after.Balance);
        var redeem = Assert.Single(after.Transactions, t => t.Kind == GiftCardTransactionKind.Redeem);
        Assert.Equal((-20m, 0m, ticket.Id), (redeem.Amount, redeem.BalanceAfter, redeem.TicketId));
        Assert.Contains("Gift card ending", s.App.Email.Sent.Last().Body);
    }

    [Fact]
    public async Task A_gift_card_worth_more_than_the_ticket_covers_it_and_keeps_the_rest()
    {
        await using var s = await SetUpWithGiftCardsAsync();
        var buyer = await BuyerAsync(s.App);
        var card = await BuyCardAsync(s, buyer, 50m);
        s.App.Payments.Charges.Clear();

        // No credit card at all: the gift card covers it.
        var first = await BuyTicketAsync(s, buyer, s.CarLoad, card.Code, spot: 1, card: null);

        Assert.Empty(s.App.Payments.Charges);
        Assert.Equal((25m, 25m, 0m), (first.Total, first.GiftCardAmount, first.CardAmount));
        Assert.Equal(TicketStatus.Sold, first.Status);
        Assert.Null(first.CardLast4);
        Assert.Equal($"Paid with gift card ending {card.Last4}", TicketReceipt.PaidWith(first));
        Assert.Equal(25m, (await ReloadAsync(s, card.Id)).Balance);

        // The balance carries over to the next ticket, and then some is left over again.
        var second = await BuyTicketAsync(s, buyer, s.Single, card.Code, spot: 2, card: null);
        Assert.Equal(10m, second.GiftCardAmount);
        Assert.Equal(15m, (await ReloadAsync(s, card.Id)).Balance);
    }

    [Fact]
    public async Task A_gift_card_that_runs_out_pays_what_is_left_and_then_is_spent()
    {
        await using var s = await SetUpWithGiftCardsAsync();
        var buyer = await BuyerAsync(s.App);
        var card = await BuyCardAsync(s, buyer, 15m);
        await BuyTicketAsync(s, buyer, s.Single, card.Code, spot: 1); // $10, leaving $5
        s.App.Payments.Charges.Clear();

        var second = await BuyTicketAsync(s, buyer, s.Single, card.Code, spot: 2, card: Visa);

        Assert.Equal((5m, 5m), (second.GiftCardAmount, second.CardAmount));
        Assert.Equal(5m, Assert.Single(s.App.Payments.Charges).Amount);
        Assert.Equal(0m, (await ReloadAsync(s, card.Id)).Balance);
        var hold = await s.Sales.HoldAsync(buyer, s.Showing.Id, 1, 3);
        var ex = await Assert.ThrowsAsync<AppValidationException>(() =>
            s.Sales.PurchaseAsync(buyer, hold.TicketId, new PurchaseInput(s.Single.Id, [], Visa, card.Code), TestApp.BaseUri));
        Assert.Contains("no balance left", ex.Message);
    }

    [Fact]
    public async Task A_declined_card_puts_the_gift_card_money_back_and_keeps_the_hold()
    {
        await using var s = await SetUpWithGiftCardsAsync();
        var buyer = await BuyerAsync(s.App);
        var card = await BuyCardAsync(s, buyer, 10m);
        s.App.Payments.DeclineWith = "card declined";
        var hold = await s.Sales.HoldAsync(buyer, s.Showing.Id, 1, 1);

        var ex = await Assert.ThrowsAsync<AppValidationException>(() =>
            s.Sales.PurchaseAsync(buyer, hold.TicketId, new PurchaseInput(s.CarLoad.Id, [], Visa, card.Code), TestApp.BaseUri));

        Assert.Contains("declined", ex.Message);
        var after = await ReloadAsync(s, card.Id);
        Assert.Equal(10m, after.Balance);
        Assert.Equal([GiftCardTransactionKind.Purchase, GiftCardTransactionKind.Redeem, GiftCardTransactionKind.Restore],
            after.Transactions.OrderBy(t => t.Id).Select(t => t.Kind));
        await using var db = s.App.Db();
        var ticket = await db.Tickets.SingleAsync();
        Assert.Equal(TicketStatus.Held, ticket.Status);
        Assert.Equal((null, 0m, null), (ticket.GiftCardId, ticket.GiftCardAmount, ticket.GiftCardLast4));

        // Trying again with a good card works and takes the gift card money once.
        s.App.Payments.DeclineWith = null;
        await s.Sales.PurchaseAsync(buyer, hold.TicketId, new PurchaseInput(s.CarLoad.Id, [], Visa, card.Code), TestApp.BaseUri);
        Assert.Equal(0m, (await ReloadAsync(s, card.Id)).Balance);
    }

    [Fact]
    public async Task A_gift_card_covering_the_whole_ticket_needs_no_card_so_nothing_can_be_declined()
    {
        await using var s = await SetUpWithGiftCardsAsync();
        var buyer = await BuyerAsync(s.App);
        var card = await BuyCardAsync(s, buyer, 100m);
        s.App.Payments.DeclineWith = "should not be asked";

        var ticket = await BuyTicketAsync(s, buyer, s.CarLoad, card.Code);

        Assert.Equal(TicketStatus.Sold, ticket.Status);
        Assert.Equal(75m, (await ReloadAsync(s, card.Id)).Balance);
    }

    [Fact]
    public async Task Without_a_gift_card_a_paid_ticket_still_needs_card_details()
    {
        await using var s = await SetUpWithGiftCardsAsync();
        var buyer = await BuyerAsync(s.App);
        var hold = await s.Sales.HoldAsync(buyer, s.Showing.Id, 1, 1);

        await Assert.ThrowsAsync<AppValidationException>(() =>
            s.Sales.PurchaseAsync(buyer, hold.TicketId, new PurchaseInput(s.CarLoad.Id, [], null), TestApp.BaseUri));

        Assert.Empty(s.App.Payments.Charges);
    }

    // --- Only real gift cards from this theater are accepted ---

    [Fact]
    public async Task A_code_that_was_never_sold_is_rejected_and_nothing_is_charged()
    {
        await using var s = await SetUpWithGiftCardsAsync();
        var buyer = await BuyerAsync(s.App);
        var hold = await s.Sales.HoldAsync(buyer, s.Showing.Id, 1, 1);

        foreach (var code in new[] { "ABCD-EFGH-JKMN-PQRT", "not a code", "K7QM", "" + new string('A', 40) })
        {
            var ex = await Assert.ThrowsAsync<AppValidationException>(() =>
                s.Sales.PurchaseAsync(buyer, hold.TicketId, new PurchaseInput(s.CarLoad.Id, [], Visa, code), TestApp.BaseUri));
            Assert.Contains("isn't valid at this theater", ex.Message);
        }

        Assert.Empty(s.App.Payments.Charges);
        await using var db = s.App.Db();
        Assert.Equal(TicketStatus.Held, (await db.Tickets.SingleAsync()).Status); // still theirs to pay for
    }

    [Fact]
    public async Task A_gift_card_from_another_theater_is_not_accepted_and_looks_like_any_bad_code()
    {
        await using var s = await SetUpWithGiftCardsAsync();
        var buyer = await BuyerAsync(s.App);
        var elsewhere = await s.App.CreateTheaterAsync("Moonlight");
        await s.App.Get<TheaterService>().UpdateGiftCardSettingsAsync(Principals.Create("x", admin: true), elsewhere.Id, true);
        var moonCard = (await s.Sales.PurchaseGiftCardAsync(buyer, elsewhere.Id,
            new GiftCardPurchaseInput(50m, null, null, null, Visa), TestApp.BaseUri)).Card;
        var hold = await s.Sales.HoldAsync(buyer, s.Showing.Id, 1, 1);

        var ex = await Assert.ThrowsAsync<AppValidationException>(() =>
            s.Sales.PurchaseAsync(buyer, hold.TicketId, new PurchaseInput(s.CarLoad.Id, [], Visa, moonCard.Code), TestApp.BaseUri));
        var check = await Assert.ThrowsAsync<AppValidationException>(() => s.Sales.CheckGiftCardAsync(buyer, s.Showing.Id, moonCard.Code));
        var bad = await Assert.ThrowsAsync<AppValidationException>(() => s.Sales.CheckGiftCardAsync(buyer, s.Showing.Id, "ABCD-EFGH-JKMN-PQRT"));

        Assert.Equal(bad.Message, ex.Message);
        Assert.Equal(bad.Message, check.Message);
        Assert.Equal(50m, (await ReloadAsync(s, moonCard.Id)).Balance);
    }

    // --- Anyone with the code can spend it ---

    [Fact]
    public async Task The_recipient_spends_a_gift_card_on_their_own_account()
    {
        await using var s = await SetUpWithGiftCardsAsync();
        var buyer = await BuyerAsync(s.App);
        var card = await BuyCardAsync(s, buyer, 50m, recipientEmail: "Sam@Example.com");
        var recipient = await BuyerAsync(s.App, "sam@example.com");
        s.App.Payments.Charges.Clear();

        Assert.Contains("you don't need the buyer's account", s.App.Email.Sent.Single(m => m.To == "Sam@Example.com").Body);
        var theirs = Assert.Single(await s.Sales.ListMyGiftCardsAsync(recipient));
        Assert.Equal((card.Code, true), (theirs.Card.Code, theirs.Received));
        Assert.False(Assert.Single(await s.Sales.ListMyGiftCardsAsync(buyer)).Received);

        var ticket = await BuyTicketAsync(s, recipient, s.CarLoad, theirs.Card.Code);

        Assert.Empty(s.App.Payments.Charges);
        Assert.Equal(25m, ticket.GiftCardAmount);
        Assert.Equal(25m, (await ReloadAsync(s, card.Id)).Balance);
        await using var db = s.App.Db();
        Assert.Equal(recipient.FindFirstValue(ClaimTypes.NameIdentifier), (await db.Tickets.SingleAsync()).UserId);
    }

    [Fact]
    public async Task A_gift_card_passed_on_to_anyone_can_be_spent_online_and_at_the_gate()
    {
        await using var s = await SetUpWithGiftCardsAsync();
        s.App.Time.SetUtcNow(ShowDayAfternoon);
        var card = await BuyCardAsync(s, await BuyerAsync(s.App), 40m); // no recipient named
        var stranger = await BuyerAsync(s.App, "friend-of-a-friend@example.com");

        Assert.Empty(await s.Sales.ListMyGiftCardsAsync(stranger)); // not theirs to look up, but theirs to spend
        Assert.Equal(40m, (await s.Sales.CheckGiftCardAsync(stranger, s.Showing.Id, card.Code)).Balance);
        await BuyTicketAsync(s, stranger, s.Single, card.Code);
        var attendant = await EmployeeAsync(s, SellAtGate);
        var hold = await s.Sales.HoldAtGateAsync(attendant, s.Showing.Id, 1, 2);
        await s.Sales.SellAtGateAsync(attendant, hold.TicketId, s.CarLoad.Id, [], card.Code);

        Assert.Equal(5m, (await ReloadAsync(s, card.Id)).Balance); // $40 - $10 - $25
    }

    [Fact]
    public async Task Cards_sent_to_an_unconfirmed_address_are_not_listed_for_it()
    {
        await using var s = await SetUpWithGiftCardsAsync();
        await BuyCardAsync(s, await BuyerAsync(s.App), 20m, recipientEmail: "sam@example.com");
        var user = await s.App.CreateUserAsync("sam@example.com");
        await using (var db = s.App.Db())
        {
            (await db.Users.SingleAsync(u => u.Id == user.Id)).EmailConfirmed = false;
            await db.SaveChangesAsync();
        }

        Assert.Empty(await s.Sales.ListMyGiftCardsAsync(Principals.For(user)));
    }

    [Fact]
    public async Task Checking_a_gift_card_shows_its_balance_without_spending_it()
    {
        await using var s = await SetUpWithGiftCardsAsync();
        var buyer = await BuyerAsync(s.App);
        var card = await BuyCardAsync(s, buyer, 30m);

        var balance = await s.Sales.CheckGiftCardAsync(await BuyerAsync(s.App, "friend@example.com"), s.Showing.Id, card.Code);

        Assert.Equal((card.Last4, 30m), (balance.Last4, balance.Balance));
        Assert.Equal(30m, (await ReloadAsync(s, card.Id)).Balance);
    }

    // --- At the gate ---

    [Fact]
    public async Task An_attendant_takes_a_gift_card_at_the_gate_and_the_terminal_pays_the_rest()
    {
        await using var s = await SetUpWithGiftCardsAsync();
        s.App.Time.SetUtcNow(ShowDayAfternoon);
        var card = await BuyCardAsync(s, await BuyerAsync(s.App), 20m);
        s.App.Payments.Charges.Clear();
        var attendant = await EmployeeAsync(s, SellAtGate);

        var balance = await s.Sales.CheckGiftCardAtGateAsync(attendant, s.Showing.Id, card.Code);
        var hold = await s.Sales.HoldAtGateAsync(attendant, s.Showing.Id, 1, 1);
        var sold = await s.Sales.SellAtGateAsync(attendant, hold.TicketId, s.CarLoad.Id, [], card.Code);

        Assert.Equal(20m, balance.Balance);
        var charge = Assert.Single(s.App.Payments.Charges);
        Assert.Equal(5m, charge.Amount);
        Assert.True(charge.CardPresent);
        Assert.Equal((25m, 20m, 5m), (sold.Ticket.Total, sold.Ticket.GiftCardAmount, sold.Ticket.CardAmount));
        Assert.Equal($"Paid with gift card ending {card.Last4} ($20.00) and card at the gate ($5.00)", TicketReceipt.PaidWith(sold.Ticket));
        Assert.NotNull(sold.Ticket.AdmittedAt);
        Assert.Equal(0m, (await ReloadAsync(s, card.Id)).Balance);
    }

    [Fact]
    public async Task At_the_gate_a_gift_card_that_covers_the_ticket_leaves_the_terminal_alone()
    {
        await using var s = await SetUpWithGiftCardsAsync();
        s.App.Time.SetUtcNow(ShowDayAfternoon);
        var card = await BuyCardAsync(s, await BuyerAsync(s.App), 100m);
        s.App.Payments.Charges.Clear();
        var attendant = await EmployeeAsync(s, SellAtGate);

        var hold = await s.Sales.HoldAtGateAsync(attendant, s.Showing.Id, 1, 1);
        var sold = await s.Sales.SellAtGateAsync(attendant, hold.TicketId, s.CarLoad.Id, [], card.Code);

        Assert.Empty(s.App.Payments.Charges);
        Assert.Equal($"Paid with gift card ending {card.Last4}", TicketReceipt.PaidWith(sold.Ticket));
        Assert.Equal(75m, (await ReloadAsync(s, card.Id)).Balance);
    }

    [Fact]
    public async Task At_the_gate_a_declined_terminal_puts_the_gift_card_money_back()
    {
        await using var s = await SetUpWithGiftCardsAsync();
        s.App.Time.SetUtcNow(ShowDayAfternoon);
        var card = await BuyCardAsync(s, await BuyerAsync(s.App), 10m);
        var attendant = await EmployeeAsync(s, SellAtGate);
        var hold = await s.Sales.HoldAtGateAsync(attendant, s.Showing.Id, 1, 1);
        s.App.Payments.DeclineWith = "declined";

        await Assert.ThrowsAsync<AppValidationException>(() => s.Sales.SellAtGateAsync(attendant, hold.TicketId, s.CarLoad.Id, [], card.Code));

        Assert.Equal(10m, (await ReloadAsync(s, card.Id)).Balance);
    }

    [Fact]
    public async Task Checking_a_gift_card_at_the_gate_needs_permission_to_sell_there()
    {
        await using var s = await SetUpWithGiftCardsAsync();
        var card = await BuyCardAsync(s, await BuyerAsync(s.App), 10m);

        foreach (var who in new[] { await EmployeeAsync(s), await EmployeeAsync(s, AdmitGuests), await EmployeeAsync(s, ViewGiftCards) })
            await Assert.ThrowsAsync<AccessDeniedException>(() => s.Sales.CheckGiftCardAtGateAsync(who, s.Showing.Id, card.Code));
    }

    // --- Staff ---

    [Fact]
    public async Task Turning_gift_cards_on_needs_the_manage_permission()
    {
        await using var s = await SetUpAsync();
        var theaters = s.App.Get<TheaterService>();
        foreach (var who in new[] { await EmployeeAsync(s), await EmployeeAsync(s, EditProfile, ViewGiftCards, SellAtGate) })
            await Assert.ThrowsAsync<AccessDeniedException>(() => theaters.UpdateGiftCardSettingsAsync(who, s.Theater.Id, true));
        var stranger = await BuyerAsync(s.App);
        await Assert.ThrowsAsync<AccessDeniedException>(() => theaters.UpdateGiftCardSettingsAsync(stranger, s.Theater.Id, true));

        await theaters.UpdateGiftCardSettingsAsync(await EmployeeAsync(s, ManageGiftCards), s.Theater.Id, true);

        await using var db = s.App.Db();
        Assert.True((await db.Theaters.SingleAsync(t => t.Id == s.Theater.Id)).GiftCardsEnabled);
    }

    [Fact]
    public async Task Turning_gift_cards_off_stops_sales_but_cards_already_sold_still_work()
    {
        await using var s = await SetUpWithGiftCardsAsync();
        var buyer = await BuyerAsync(s.App);
        var card = await BuyCardAsync(s, buyer, 30m);

        await s.App.Get<TheaterService>().UpdateGiftCardSettingsAsync(s.OwnerPrincipal, s.Theater.Id, false);

        await Assert.ThrowsAsync<AppValidationException>(() => BuyCardAsync(s, buyer, 30m));
        var ticket = await BuyTicketAsync(s, buyer, s.Single, card.Code);
        Assert.Equal(10m, ticket.GiftCardAmount);
    }

    [Fact]
    public async Task Staff_with_the_view_permission_see_sales_and_balances_but_never_the_codes()
    {
        await using var s = await SetUpWithGiftCardsAsync();
        var buyer = await BuyerAsync(s.App);
        var card = await BuyCardAsync(s, buyer, 30m);
        await BuyCardAsync(s, buyer, 20m);
        await BuyTicketAsync(s, buyer, s.Single, card.Code);

        foreach (var who in new[] { await EmployeeAsync(s), await EmployeeAsync(s, ManageGiftCards, SellAtGate) })
            await Assert.ThrowsAsync<AccessDeniedException>(() => s.Sales.ListGiftCardsAsync(who, s.Theater.Id));
        var summary = await s.Sales.ListGiftCardsAsync(await EmployeeAsync(s, ViewGiftCards), s.Theater.Id);

        Assert.Equal((2, 50m, 40m), (summary.Count, summary.TotalSold, summary.Outstanding));
        Assert.Equal([20m, 20m], summary.Rows.Select(r => r.Balance)); // $30 less a $10 ticket, and a $20 card
        Assert.Equal("buyer@example.com", summary.Rows[0].PurchaserEmail);
        Assert.All(summary.Rows, r => Assert.Equal(4, r.Last4.Length));
        Assert.DoesNotContain(card.Code, System.Text.Json.JsonSerializer.Serialize(summary));
    }

    [Fact]
    public void Default_roles_and_the_catalog_include_the_gift_card_actions()
    {
        Assert.Contains(ManageGiftCards, AllKeys);
        Assert.Contains(ViewGiftCards, AllKeys);
        Assert.Contains(ManageGiftCards, DriveIn.Web.Authorization.DefaultTheaterRoles.All.Single(r => r.Name == "Manager").Permissions);
        Assert.DoesNotContain(ManageGiftCards, DriveIn.Web.Authorization.DefaultTheaterRoles.All.Single(r => r.Name == "Ticketing").Permissions);
    }

    // --- Demo theaters ---

    [Fact]
    public async Task Demo_theaters_sell_test_gift_cards_that_are_cleared_when_the_theater_goes_live()
    {
        await using var s = await SetUpWithGiftCardsAsync();
        await using (var db = s.App.Db())
        {
            (await db.Theaters.SingleAsync(t => t.Id == s.Theater.Id)).Mode = TheaterMode.Demo;
            await db.SaveChangesAsync();
        }
        var buyer = s.OwnerPrincipal; // demo theaters are private: only members can buy
        var card = await BuyCardAsync(s, buyer, 30m);
        var ticket = await BuyTicketAsync(s, buyer, s.Single, card.Code);

        Assert.True(card.IsTest);
        Assert.True(ticket.IsTest);
        Assert.Empty(s.App.Payments.Charges); // the dummy processor, not the real one
        Assert.Contains("[TEST]", s.App.Email.Sent.First().Subject);

        var admin = Principals.For(await s.App.CreateUserAsync("admin@example.com", admin: true), admin: true);
        await s.App.Get<OnboardingService>().ActivateAsync(admin, s.Theater.Id, TestApp.BaseUri);

        await using var check = s.App.Db();
        Assert.Empty(check.GiftCards);
        Assert.Empty(check.GiftCardTransactions);
        Assert.Empty(check.Tickets);
    }

    // --- Guessing ---

    [Fact]
    public async Task Wrong_codes_past_the_limit_stop_all_lookups_for_that_person_until_the_window_ends()
    {
        await using var s = await SetUpWithGiftCardsAsync();
        var card = await BuyCardAsync(s, await BuyerAsync(s.App), 50m);
        var guesser = await BuyerAsync(s.App, "guesser@example.com");
        var limit = new RateLimitOptions().GiftCardMissesPerUser;

        for (var i = 0; i < limit.PermitLimit; i++)
        {
            var miss = await Assert.ThrowsAsync<AppValidationException>(() => s.Sales.CheckGiftCardAsync(guesser, s.Showing.Id, "ABCD-EFGH-JKMN-PQRT"));
            Assert.StartsWith("That gift card isn't valid", miss.Message);
        }
        // Even a real code is refused now, so guessing can't go on.
        var refused = await Assert.ThrowsAsync<AppValidationException>(() => s.Sales.CheckGiftCardAsync(guesser, s.Showing.Id, card.Code));
        Assert.StartsWith("Too many attempts", refused.Message);
        // Checkout too.
        var buy = await Assert.ThrowsAsync<AppValidationException>(() => BuyTicketAsync(s, guesser, s.CarLoad, card.Code));
        Assert.StartsWith("Too many attempts", buy.Message);
        // Someone else isn't affected.
        Assert.Equal(50m, (await s.Sales.CheckGiftCardAsync(await BuyerAsync(s.App, "friend@example.com"), s.Showing.Id, card.Code)).Balance);

        s.App.Time.Advance(limit.Window);
        Assert.Equal(50m, (await s.Sales.CheckGiftCardAsync(guesser, s.Showing.Id, card.Code)).Balance);
    }

    [Fact]
    public async Task Wrong_codes_from_many_people_at_one_theater_hit_the_theater_limit()
    {
        await using var s = await SetUpWithGiftCardsAsync();
        var card = await BuyCardAsync(s, await BuyerAsync(s.App), 50m);
        var options = new RateLimitOptions();
        var perUser = options.GiftCardMissesPerUser.PermitLimit;
        var misses = 0;
        for (var person = 0; misses < options.GiftCardMissesPerTheater.PermitLimit; person++)
        {
            var guesser = await BuyerAsync(s.App, $"guesser{person}@example.com");
            for (var i = 0; i < perUser && misses < options.GiftCardMissesPerTheater.PermitLimit; i++, misses++)
                await Assert.ThrowsAsync<AppValidationException>(() => s.Sales.CheckGiftCardAsync(guesser, s.Showing.Id, "ABCD-EFGH-JKMN-PQRT"));
        }

        var newcomer = await BuyerAsync(s.App, "new@example.com");
        var refused = await Assert.ThrowsAsync<AppValidationException>(() => s.Sales.CheckGiftCardAsync(newcomer, s.Showing.Id, card.Code));
        Assert.StartsWith("Too many attempts", refused.Message);
    }
}
