using System.ComponentModel.DataAnnotations;

namespace DriveIn.Web.Data;

public class Screen
{
    // A theater has 1 to MaxPerTheater screens (enforced by ScreenService).
    public const int MaxPerTheater = 4;
    public const int MaxRows = 40;
    public const int MaxSpotsPerRow = 99;

    public int Id { get; set; }

    public int TheaterId { get; set; }
    public Theater? Theater { get; set; }

    [Required, MaxLength(100)]
    public string Name { get; set; } = "";

    public int SortOrder { get; set; }

    // How spots are labeled, e.g. "B7", "2G" or "207".
    public SpotLabelScheme LabelScheme { get; set; } = SpotLabelScheme.LetterNumber;

    // Spots in each row, starting with the row nearest the screen. Rows are centered on the screen.
    public List<int> RowSpots { get; set; } = [];

    public int SpotCount => RowSpots.Sum();

    // Spots that take a large vehicle (full-size SUV, pickup, van), as SpotKey values; the rest are for standard
    // vehicles only, so tall vehicles park where they don't block the view. Standard vehicles may use any spot.
    public List<int> LargeSpots { get; set; } = [];

    public List<Showtime> Showtimes { get; set; } = [];

    // A spot as one int: row * 100 + spot (spots per row are at most 99).
    public static int SpotKey(int row, int spot) => row * 100 + spot;

    public static (int Row, int Spot) FromKey(int key) => (key / 100, key % 100);

    public bool Contains(int row, int spot) => row >= 1 && row <= RowSpots.Count && spot >= 1 && spot <= RowSpots[row - 1];

    public bool AllowsLarge(int row, int spot) => LargeSpots.Contains(SpotKey(row, spot));

    public bool Fits(int row, int spot, VehicleSize size) => size == VehicleSize.Standard || AllowsLarge(row, spot);

    // Some spots are for standard vehicles only, so buyers are asked what they're driving.
    public bool HasSizeLimits => LargeSpots.Count < SpotCount;

    // The default marking: the back half of the rows (all of a one-row screen) takes large vehicles.
    public static List<int> BackHalfLarge(IReadOnlyList<int> rowSpots) =>
        Enumerable.Range(1, rowSpots.Count)
            .Where(row => row > rowSpots.Count / 2)
            .SelectMany(row => Enumerable.Range(1, rowSpots[row - 1]).Select(spot => SpotKey(row, spot)))
            .ToList();
}

// Stored by name, so members can be added but not renamed.
public enum VehicleSize
{
    Standard, // cars, small SUVs, minivans: any spot
    Large,    // full-size SUVs, pickups, vans: only spots marked for large vehicles
}

public static class VehicleSizes
{
    public static string Describe(VehicleSize size) => size switch
    {
        VehicleSize.Large => "Large SUV, pickup or van",
        _ => "Car, small SUV or minivan",
    };

    public static string Short(VehicleSize size) => size == VehicleSize.Large ? "Large vehicle" : "Standard vehicle";
}

// Rows and spots are both numbered from 1: rows from the screen back, spots left to right facing the screen.
// Stored by name, so members can be added but not renamed.
public enum SpotLabelScheme
{
    LetterNumber, // row letter + spot number: A1, A2 … B1
    NumberLetter, // row number + spot letter: 1A, 1B … 2A
    Numeric,      // row number + two-digit spot number: 101, 102 … 201
}

public static class SpotLabels
{
    public static string Row(SpotLabelScheme scheme, int row) => scheme switch
    {
        SpotLabelScheme.LetterNumber => Letters(row),
        _ => row.ToString(),
    };

    public static string Spot(SpotLabelScheme scheme, int row, int spot) => scheme switch
    {
        SpotLabelScheme.LetterNumber => $"{Letters(row)}{spot}",
        SpotLabelScheme.NumberLetter => $"{row}{Letters(spot)}",
        SpotLabelScheme.Numeric => $"{row}{spot:00}",
        _ => throw new ArgumentOutOfRangeException(nameof(scheme)),
    };

    public static string Describe(SpotLabelScheme scheme) => scheme switch
    {
        SpotLabelScheme.LetterNumber => "Row letter, spot number",
        SpotLabelScheme.NumberLetter => "Row number, spot letter",
        SpotLabelScheme.Numeric => "Row and spot as one number",
        _ => scheme.ToString(),
    };

    public static string Example(SpotLabelScheme scheme) =>
        $"{Spot(scheme, 1, 1)}, {Spot(scheme, 1, 2)} … {Spot(scheme, 2, 1)}, {Spot(scheme, 2, 2)}";

    // Spreadsheet-style: 1 = A, 26 = Z, 27 = AA.
    public static string Letters(int n)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(n, 1);
        var s = "";
        for (; n > 0; n = (n - 1) / 26)
            s = (char)('A' + (n - 1) % 26) + s;
        return s;
    }
}
