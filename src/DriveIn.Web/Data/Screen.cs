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

    public List<Showtime> Showtimes { get; set; } = [];
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
