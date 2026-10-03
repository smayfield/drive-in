using DriveIn.Web.Components.Pages.Manage;
using DriveIn.Web.Components.Pages.Theaters;
using DriveIn.Web.Components.Shared;
using DriveIn.Web.Data;
using DriveIn.Web.Services;

namespace DriveIn.Web.Tests.Pages;

// The seat map for keyboard and screen reader users: its roles and labels, the marks that don't rest on color, "Best
// available", the list of spots, and the live region for the viewer's hold. (Arrow-key focus is lot-map.js, in the browser.)
public class SeatMapAccessibilityTests
{
    private const string Visa = "pm_test_visa_4242_0001"; // a test card token (see TestCardTokens)

    private static async Task<(TicketSalesTests.Setup S, PageHost Host)> BuyerAsync()
    {
        var s = await TicketSalesTests.SetUpAsync();
        return (s, new PageHost(s.App).SignIn(await TicketSalesTests.BuyerAsync(s.App)));
    }

    private static IRenderedComponent<Showing> OpenShowing(PageHost host, TicketSalesTests.Setup s)
    {
        var page = host.Render<Showing>(p => p.Add(x => x.Slug, "starlight").Add(x => x.ShowtimeId, s.Showing.Id));
        page.WaitForText("Choose your spot");
        return page;
    }

    // Another buyer holds A2 and has bought B1.
    private static async Task OthersTakeSpotsAsync(TicketSalesTests.Setup s)
    {
        var other = await TicketSalesTests.BuyerAsync(s.App, "other@example.com");
        var sold = await s.Sales.HoldAsync(other, s.Showing.Id, 2, 1);
        await s.Sales.PurchaseAsync(other, sold.TicketId, new PurchaseInput(s.Single.Id, [], Visa), TestApp.BaseUri);
        var third = await TicketSalesTests.BuyerAsync(s.App, "third@example.com");
        await s.Sales.HoldAsync(third, s.Showing.Id, 1, 2);
    }

    [Fact]
    public async Task The_map_is_a_labelled_group_of_spot_buttons_with_keyboard_help()
    {
        var (s, host) = await BuyerAsync();
        await using var _ = host;
        await using var __ = s;
        var page = OpenShowing(host, s);

        var map = page.Find("svg.lot-map");
        Assert.Equal("group", map.GetAttribute("role"));
        Assert.Equal("Spots at North: 7 of 7 available", map.GetAttribute("aria-label"));
        Assert.True(map.HasAttribute("data-roving"));
        var help = page.Find($"#{map.GetAttribute("aria-describedby")}");
        Assert.Contains("arrow keys", help.TextContent);

        var spots = map.QuerySelectorAll("[data-spot]");
        Assert.Equal(7, spots.Length);
        Assert.All(spots, spot =>
        {
            Assert.Equal("button", spot.GetAttribute("role"));
            Assert.Equal("-1", spot.GetAttribute("tabindex")); // lot-map.js gives one of them the tab stop
            Assert.False(spot.HasAttribute("aria-disabled"));
            // The aria-label is the whole announcement: no <title> to be read again as a description, drawn text hidden.
            Assert.Null(spot.QuerySelector("title"));
            Assert.Equal("true", spot.QuerySelector("text.lot-spot-label")!.GetAttribute("aria-hidden"));
        });
        Assert.Equal("1", page.Find("g[aria-label='Spot A3: available']").GetAttribute("data-row"));
        Assert.NotEmpty(page.FindAll("[data-lot-live][aria-live=polite]"));
        Assert.Contains("Map key", page.Find("ul.spot-legend").GetAttribute("aria-label"));
    }

    [Fact]
    public async Task Held_and_sold_spots_stay_focusable_but_are_disabled_and_marked()
    {
        var (s, host) = await BuyerAsync();
        await using var _ = host;
        await using var __ = s;
        await OthersTakeSpotsAsync(s);
        var page = OpenShowing(host, s);

        Assert.Equal("Spots at North: 5 of 7 available", page.Find("svg.lot-map").GetAttribute("aria-label"));
        var held = page.Find("g[aria-label='Spot A2: on hold for another buyer']");
        Assert.Equal("true", held.GetAttribute("aria-disabled"));
        Assert.Equal("-1", held.GetAttribute("tabindex"));
        Assert.NotNull(held.QuerySelector("path.lot-mark-hatch"));
        var sold = page.Find("g[aria-label='Spot B1: sold']");
        Assert.Equal("true", sold.GetAttribute("aria-disabled"));
        Assert.NotNull(sold.QuerySelector("path.lot-mark-cross"));
        Assert.Null(page.Find("g[aria-label='Spot A1: available']").QuerySelector("path.lot-mark"));

        // Clicking a disabled spot does nothing.
        sold.Click();
        Assert.Contains("Choose your spot", page.Text());
        Assert.DoesNotContain("is yours for", page.Text());
    }

    [Fact]
    public async Task Best_available_holds_the_middle_of_the_front_row()
    {
        var (s, host) = await BuyerAsync();
        await using var _ = host;
        await using var __ = s;
        var page = OpenShowing(host, s);

        page.ClickButton("Best available");

        page.WaitForText("Spot A2 is yours for");
        page.WaitForAssertion(() => Assert.Equal("Spot A2 is on hold for you.", page.Find("[role=status]").TextContent.Trim()));
    }

    [Fact]
    public async Task Best_available_skips_taken_spots_and_takes_the_left_of_two_equally_central()
    {
        var (s, host) = await BuyerAsync();
        await using var _ = host;
        await using var __ = s;
        await OthersTakeSpotsAsync(s);
        var page = OpenShowing(host, s);

        page.ClickButton("Best available");

        page.WaitForText("Spot A1 is yours for");
    }

    [Fact]
    public async Task A_spot_can_be_chosen_from_the_list_and_the_live_region_says_when_the_hold_ends()
    {
        var (s, host) = await BuyerAsync();
        await using var _ = host;
        await using var __ = s;
        await OthersTakeSpotsAsync(s);
        var page = OpenShowing(host, s);

        host.Select(page, "Or choose a spot", "B3 (row B)");

        page.WaitForText("Spot B3 is yours for");
        page.ClickButton("Choose a different spot");
        page.WaitForText("Choose your spot");
        page.WaitForAssertion(() =>
            Assert.Equal("Spot B3 is no longer on hold for you.", page.Find("[role=status]").TextContent.Trim()));
    }

    [Fact]
    public void The_list_offers_only_free_spots_that_fit_the_vehicle()
    {
        var screen = new Screen { Name = "North", RowSpots = [3, 4], LargeSpots = [201, 202, 203, 204] };
        var availability = new SpotAvailability(new Dictionary<(int, int), SpotState>
        {
            [(1, 2)] = SpotState.Held, [(2, 2)] = SpotState.Sold, [(2, 3)] = SpotState.Mine,
        }, null);

        Assert.Equal([(1, 1), (1, 3), (2, 1), (2, 4)], SpotChoice.Available(screen, availability, VehicleSize.Standard));
        Assert.Equal([(2, 1), (2, 4)], SpotChoice.Available(screen, availability, VehicleSize.Large));
        Assert.Equal((1, 1), SpotChoice.Best(screen, availability, VehicleSize.Standard));
        Assert.Equal((2, 1), SpotChoice.Best(screen, availability, VehicleSize.Large));
        Assert.Null(SpotChoice.Best(screen, new SpotAvailability(new Dictionary<(int, int), SpotState>
        {
            [(2, 1)] = SpotState.Sold, [(2, 2)] = SpotState.Sold, [(2, 3)] = SpotState.Sold, [(2, 4)] = SpotState.Sold,
        }, null), VehicleSize.Large));
    }

    [Fact]
    public async Task A_map_that_only_shows_a_layout_stays_one_image()
    {
        var s = await TicketSalesTests.SetUpAsync();
        await using var __ = s;
        await using var host = new PageHost(s.App).SignIn(s.OwnerPrincipal);
        var page = host.Render<ManageLot>(p => p.Add(x => x.Id, s.Theater.Id));
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("svg.lot-map")));

        var map = page.Find("svg.lot-map");
        Assert.Equal("img", map.GetAttribute("role"));
        Assert.False(map.HasAttribute("data-roving"));
        Assert.Empty(page.FindAll("[role=button][data-spot]"));
        Assert.NotEmpty(page.FindAll("g.lot-spot-group > title"));
        Assert.Empty(page.FindAll("[data-lot-live]"));
    }

    [Fact]
    public void Hatching_stays_inside_the_spot()
    {
        var lines = LotMarks.Hatch(10, 20, 26, 38).ToList();
        Assert.NotEmpty(lines);
        Assert.All(lines, l =>
        {
            Assert.InRange(l.X1, 10, 36);
            Assert.InRange(l.X2, 10, 36);
            Assert.InRange(l.Y1, 20, 58);
            Assert.InRange(l.Y2, 20, 58);
        });
    }
}
