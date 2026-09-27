using System.ComponentModel.DataAnnotations;

namespace DriveIn.Web.Data;

public class Screen
{
    public int Id { get; set; }

    public int TheaterId { get; set; }
    public Theater? Theater { get; set; }

    [Required, MaxLength(100)]
    public string Name { get; set; } = "";

    [Range(0, 10000)]
    public int CarCapacity { get; set; }

    public int SortOrder { get; set; }
}
