using System.Globalization;
using DriveIn.Web.Data;

namespace DriveIn.Web.Components.Shared;

// Geometry for drawing spot layouts (LotMap). Units are SVG user units.
//
// Each screen's field is laid out in local coordinates with the screen along the top (y = 0 is the top of
// the field), rows below it nearest-first, each row centered on x = 0, and spot 1 on the left as seen by
// a driver facing the screen. The theater view places up to four fields around a central building,
// screens outward and rows toward the building: screens 1 and 2 north and south facing each other,
// screens 3 and 4 east and west (rotated 90 degrees) facing each other.
public sealed class LotPlan
{
    public const double SpotWidth = 26, SpotDepth = 38, SpotGap = 4, LaneWidth = 22;
    public const double NameHeight = 18, ScreenThickness = 8, ScreenToFirstRow = 44, BottomPad = 10;
    public const double RowLabelWidth = 22, RowLabelGap = 6, MinScreenWidth = 140;
    public const double BuildingHalfWidth = 90, BuildingHalfHeight = 55, Clearance = 36, Pad = 20;

    // Rotation (degrees clockwise) of the field for each screen position in the theater view.
    public static readonly int[] Angles = [0, 180, 90, 270];

    public required IReadOnlyList<PlacedField> Fields { get; init; }
    public LotRect? Building { get; init; }
    public required LotRect ViewBox { get; init; }

    public static LotPlan ForScreen(Screen screen)
    {
        var field = LotField.For(screen);
        return new LotPlan
        {
            Fields = [new PlacedField(field, 0, 0, 0)],
            ViewBox = new LotRect(-field.HalfWidth, 0, field.HalfWidth * 2, field.Depth).Inflate(Pad),
        };
    }

    public static LotPlan ForTheater(IEnumerable<Screen> screens)
    {
        var fields = screens.OrderBy(s => s.SortOrder).ThenBy(s => s.Id).Take(Screen.MaxPerTheater).Select(LotField.For).ToList();
        var building = new LotRect(-BuildingHalfWidth, -BuildingHalfHeight, BuildingHalfWidth * 2, BuildingHalfHeight * 2);

        // North and south fields sit just beyond the building. East and west fields do too, unless they're
        // wide enough to reach the north/south fields' rows, in which case they move out past them.
        var northSouthGap = BuildingHalfHeight + Clearance;
        var northSouthHalfWidth = fields.Take(2).Select(f => f.HalfWidth).DefaultIfEmpty(0).Max();
        double EastWestGap(LotField f) => f.HalfWidth + Clearance > northSouthGap
            ? Math.Max(BuildingHalfWidth, northSouthHalfWidth) + Clearance
            : BuildingHalfWidth + Clearance;

        var placed = new List<PlacedField>();
        var bounds = building;
        for (var i = 0; i < fields.Count; i++)
        {
            var f = fields[i];
            var (x, y, box) = Angles[i] switch
            {
                0 => (0.0, -(northSouthGap + f.Depth),
                    new LotRect(-f.HalfWidth, -(northSouthGap + f.Depth), f.HalfWidth * 2, f.Depth)),
                180 => (0.0, northSouthGap + f.Depth,
                    new LotRect(-f.HalfWidth, northSouthGap, f.HalfWidth * 2, f.Depth)),
                90 => (EastWestGap(f) + f.Depth, 0.0,
                    new LotRect(EastWestGap(f), -f.HalfWidth, f.Depth, f.HalfWidth * 2)),
                _ => (-(EastWestGap(f) + f.Depth), 0.0,
                    new LotRect(-(EastWestGap(f) + f.Depth), -f.HalfWidth, f.Depth, f.HalfWidth * 2)),
            };
            placed.Add(new PlacedField(f, Angles[i], x, y) { Bounds = box });
            bounds = bounds.Union(box);
        }
        return new LotPlan { Fields = placed, Building = building, ViewBox = bounds.Inflate(Pad) };
    }

    // Invariant formatting for SVG attributes.
    public static string N(double value) => Math.Round(value, 2).ToString(CultureInfo.InvariantCulture);
}

public sealed record LotSpot(double X, double Y, string Label);

public sealed record LotRow(double Y, double HalfWidth, string Label, IReadOnlyList<LotSpot> Spots);

public sealed class LotField
{
    public required Screen Screen { get; init; }
    public required double HalfWidth { get; init; }
    public required double Depth { get; init; }
    public required double ScreenWidth { get; init; }
    public required IReadOnlyList<LotRow> Rows { get; init; }

    public static double RowWidth(int spots) => spots * LotPlan.SpotWidth + Math.Max(0, spots - 1) * LotPlan.SpotGap;

    public static double RowTop(int row) =>
        LotPlan.NameHeight + LotPlan.ScreenThickness + LotPlan.ScreenToFirstRow + (row - 1) * (LotPlan.SpotDepth + LotPlan.LaneWidth);

    public static LotField For(Screen screen)
    {
        var rows = screen.RowSpots.Select((count, i) =>
        {
            var row = i + 1;
            var top = RowTop(row);
            var left = -RowWidth(count) / 2;
            var spots = Enumerable.Range(1, count)
                .Select(s => new LotSpot(left + (s - 1) * (LotPlan.SpotWidth + LotPlan.SpotGap), top,
                    SpotLabels.Spot(screen.LabelScheme, row, s)))
                .ToList();
            return new LotRow(top, RowWidth(count) / 2, SpotLabels.Row(screen.LabelScheme, row), spots);
        }).ToList();

        var widest = rows.Select(r => r.HalfWidth * 2).DefaultIfEmpty(0).Max();
        var screenWidth = Math.Max(LotPlan.MinScreenWidth, widest * 0.7);
        var halfWidth = Math.Max(screenWidth / 2, widest / 2 + LotPlan.RowLabelGap + LotPlan.RowLabelWidth);
        var depth = rows.Count == 0
            ? RowTop(1) + LotPlan.BottomPad
            : RowTop(rows.Count) + LotPlan.SpotDepth + LotPlan.BottomPad;
        return new LotField { Screen = screen, HalfWidth = halfWidth, Depth = depth, ScreenWidth = screenWidth, Rows = rows };
    }
}

// A field drawn with transform "translate(X Y) rotate(Angle)"; Bounds is where it lands.
public sealed record PlacedField(LotField Field, int Angle, double X, double Y)
{
    public LotRect Bounds { get; init; } = new(-Field.HalfWidth, 0, Field.HalfWidth * 2, Field.Depth);

    public string Transform => $"translate({LotPlan.N(X)} {LotPlan.N(Y)}) rotate({Angle})";

    // Keeps text upright however the field is rotated.
    public string? Upright(double x, double y) => Angle == 0 ? null : $"rotate({-Angle} {LotPlan.N(x)} {LotPlan.N(y)})";
}

public readonly record struct LotRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;

    public LotRect Inflate(double by) => new(X - by, Y - by, Width + by * 2, Height + by * 2);

    public LotRect Union(LotRect other)
    {
        var x = Math.Min(X, other.X);
        var y = Math.Min(Y, other.Y);
        return new LotRect(x, y, Math.Max(Right, other.Right) - x, Math.Max(Bottom, other.Bottom) - y);
    }

    public bool Intersects(LotRect other) => X < other.Right && other.X < Right && Y < other.Bottom && other.Y < Bottom;

    public string ViewBox => $"{LotPlan.N(X)} {LotPlan.N(Y)} {LotPlan.N(Width)} {LotPlan.N(Height)}";
}
