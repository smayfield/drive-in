using DriveIn.Web.Services;

namespace DriveIn.Web.Tests;

public class HtmlContentTests
{
    private static readonly Dictionary<int, LibraryImage> Library = new()
    {
        [7] = new LibraryImage(7, 800, 600, "Our screen at dusk"),
        [8] = new LibraryImage(8, 400, 400, null),
    };

    private static string Clean(string html) => new HtmlContent().Sanitize(html, "starlight", Library);

    [Fact]
    public void Keeps_the_editors_formatting()
    {
        var html = "<h2>Rules</h2><p>Be <strong>kind</strong>, <em>quiet</em>, <u>on</u> <s>time</s>.</p>" +
                   "<ul><li>One</li></ul><ol><li>Two</li></ol><blockquote>Quote</blockquote><hr>" +
                   "<p class=\"ql-align-center\">Centered</p><p class=\"ql-indent-2\">Indented</p>";

        Assert.Equal(html, Clean(html));
    }

    [Theory]
    [InlineData("<script>alert(1)</script><p>Hi</p>", "<p>Hi</p>")]
    [InlineData("<p onclick=\"alert(1)\" style=\"color:red\">Hi</p>", "<p>Hi</p>")]
    [InlineData("<iframe src=\"https://evil.example\"></iframe><p>Hi</p>", "<p>Hi</p>")]
    [InlineData("<p class=\"evil ql-align-right\">Hi</p>", "<p class=\"ql-align-right\">Hi</p>")]
    [InlineData("<div><span>Hi</span></div>", "Hi")]
    [InlineData("<h1>Big</h1>", "Big")]
    [InlineData("<a href=\"javascript:alert(1)\">x</a>", "<a>x</a>")]
    [InlineData("<form action=\"/x\"><input name=\"q\"></form><p>Hi</p>", "<p>Hi</p>")]
    public void Strips_anything_that_could_run_or_restyle_the_site(string input, string expected)
    {
        Assert.Equal(expected, Clean(input));
    }

    [Fact]
    public void Links_never_reach_back_and_external_ones_open_in_a_new_tab()
    {
        Assert.Equal("<a href=\"https://example.com/\" rel=\"noopener noreferrer nofollow\" target=\"_blank\">site</a>",
            Clean("<a href=\"https://example.com/\" target=\"_self\">site</a>"));
        Assert.Equal("<a href=\"mailto:hi@example.com\" rel=\"noopener noreferrer nofollow\">mail</a>",
            Clean("<a href=\"mailto:hi@example.com\">mail</a>"));
        Assert.Equal("<a href=\"theaters/starlight/news\" rel=\"noopener noreferrer nofollow\">news</a>",
            Clean("<a href=\"theaters/starlight/news\">news</a>"));
        // An absolute link back to this site stays in the same tab, like a relative one.
        Assert.Equal("<a href=\"https://drive-in.online/theaters/starlight\" rel=\"noopener noreferrer nofollow\">us</a>",
            Clean("<a href=\"https://drive-in.online/theaters/starlight\">us</a>"));
        Assert.Contains("target=\"_blank\"", new HtmlContent("http://localhost:5280/").Sanitize("<a href=\"https://drive-in.online/\">x</a>", "s", Library));
    }

    [Fact]
    public void Images_must_come_from_the_theaters_library_and_take_its_size()
    {
        var kept = Clean("<p><img src=\"theaters/starlight/images/7\" width=\"9999\" class=\"img-small img-left\" onerror=\"x()\"></p>");
        Assert.Equal("<p><img src=\"theaters/starlight/images/7\" width=\"800\" class=\"img-small img-left\" height=\"600\" alt=\"Our screen at dusk\" loading=\"lazy\" decoding=\"async\"></p>", kept);

        // An empty description is kept (a decorative image), not replaced by the library's.
        Assert.Contains("alt=\"\"", Clean("<img src=\"theaters/starlight/images/7\" alt=\"\">"));
        // Its own description wins over the library's; an absolute link to this site is made relative.
        Assert.Contains("alt=\"Popcorn\"", Clean("<img src=\"https://drive-in.online/theaters/starlight/images/8\" alt=\"Popcorn\">"));
        Assert.Contains("src=\"theaters/starlight/images/8\"", Clean("<img src=\"/theaters/starlight/images/8\">"));

        Assert.Equal("", Clean("<img src=\"theaters/starlight/images/99\">"));       // not in the library
        // In the library under an older address (the theater was renamed): kept, at the current address.
        Assert.Contains("src=\"theaters/starlight/images/7\"", Clean("<img src=\"theaters/old-name/images/7\">"));
        Assert.Equal("", Clean("<img src=\"https://evil.example/x.png\">"));
        Assert.Equal("", Clean("<img src=\"data:image/png;base64,AAAA\">"));
    }

    [Fact]
    public void Plain_text_excerpts_and_emptiness()
    {
        Assert.Equal("Rules Be kind. One Two", HtmlContent.ToPlainText("<h2>Rules</h2><p>Be <strong>kind</strong>.</p><ul><li>One</li><li>Two</li></ul>"));
        Assert.Equal("", HtmlContent.ToPlainText(null));
        Assert.Equal("Hello there…", HtmlContent.Excerpt("Hello there, general Kenobi", 15));
        Assert.Equal("Short", HtmlContent.Excerpt("Short", 15));
        Assert.True(HtmlContent.IsEffectivelyEmpty("<p><br></p>"));
        Assert.False(HtmlContent.IsEffectivelyEmpty("<p><img src=\"theaters/x/images/1\"></p>"));
        Assert.Equal(new HashSet<int> { 7, 8 }, HtmlContent.ImageIds("<img src=\"theaters/x/images/7\"><img alt=\"a\" src=\"theaters/x/images/8?v=1\">"));
        Assert.Equal(12, HtmlContent.FirstImageId("<p>x</p><img src=\"theaters/x/images/12\"><img src=\"theaters/x/images/3\">"));
        Assert.Null(HtmlContent.FirstImageId("<p>No images</p>"));
        // Links and text that mention an image address don't count as using it.
        Assert.Empty(HtmlContent.ImageIds("<a href=\"https://example.com/theaters/x/images/9\">see /images/9</a>"));
        Assert.Equal("", Clean("   "));
    }
}
