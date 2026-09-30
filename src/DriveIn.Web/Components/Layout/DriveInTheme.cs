using MudBlazor;

namespace DriveIn.Web.Components.Layout;

// One theme for the app: a cool daylight lot in light mode, a night lot in dark mode. The marquee red and the
// projector cyan are the only strong colors; bulb gold is reserved for highlights. wwwroot/app.css carries the same
// values for the static pages, so keep the two in step.
public static class DriveInTheme
{
    public static MudTheme Theme { get; } = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#c8283b",
            Secondary = "#0b7a8a",
            Tertiary = "#7a5c00",
            Success = "#1f7a4d", SuccessContrastText = "#ffffff",
            Error = "#c62828", ErrorContrastText = "#ffffff",
            Warning = "#ffb300", WarningContrastText = "#14172b",
            Info = "#1565c0", InfoContrastText = "#ffffff",
            Background = "#eceff6",
            Surface = "#ffffff",
            AppbarBackground = "#0a0d1f",
            AppbarText = "#f3f1fa",
            DrawerBackground = "#ffffff",
            DrawerText = "#14172b",
            DrawerIcon = "#4b5070",
            TextPrimary = "#14172b",
            TextSecondary = "#4b5070",
            ActionDefault = "#4b5070",
            Divider = "#d5d9e6",
            DividerLight = "#e3e6f0",
            LinesDefault = "#d5d9e6",
            TableLines = "#d5d9e6",
            TableStriped = "#f5f6fa",
            TableHover = "#eef0f7",
        },
        PaletteDark = new PaletteDark
        {
            Primary = "#ff5468",
            Secondary = "#5ef2ff",
            Tertiary = "#ffd66b",
            Success = "#3fbf7f", SuccessContrastText = "#0a0d1f",
            Error = "#ff6b6b", ErrorContrastText = "#0a0d1f",
            Warning = "#ffd66b", WarningContrastText = "#0a0d1f",
            Info = "#5ec8ff", InfoContrastText = "#0a0d1f",
            PrimaryContrastText = "#0a0d1f",
            SecondaryContrastText = "#0a0d1f",
            Background = "#0a0d1f",
            Surface = "#151a38",
            AppbarBackground = "#070918",
            AppbarText = "#efedf6",
            DrawerBackground = "#0f1229",
            DrawerText = "#efedf6",
            DrawerIcon = "#a8a6bd",
            TextPrimary = "#efedf6",
            TextSecondary = "#a8a6bd",
            ActionDefault = "#a8a6bd",
            Divider = "#2a2f52",
            DividerLight = "#20254a",
            LinesDefault = "#2a2f52",
            TableLines = "#2a2f52",
            TableStriped = "#12163a",
            TableHover = "#1b2148",
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "6px",
            AppbarHeight = "52px",
        },
        Typography = new Typography
        {
            Default = new DefaultTypography
            {
                FontFamily = ["Barlow", "system-ui", "-apple-system", "Segoe UI", "sans-serif"],
                FontSize = "1rem",
                LineHeight = "1.5",
            },
            H1 = new H1Typography { FontSize = "2rem", FontWeight = "700", LineHeight = "1.15", LetterSpacing = "-0.01em" },
            H2 = new H2Typography { FontSize = "1.65rem", FontWeight = "700", LineHeight = "1.2" },
            H3 = new H3Typography { FontSize = "1.4rem", FontWeight = "600", LineHeight = "1.25" },
            H4 = new H4Typography { FontSize = "1.25rem", FontWeight = "600", LineHeight = "1.3" },
            H5 = new H5Typography { FontSize = "1.125rem", FontWeight = "600", LineHeight = "1.35" },
            H6 = new H6Typography { FontSize = "1rem", FontWeight = "600", LineHeight = "1.35" },
            Button = new ButtonTypography { FontWeight = "600", TextTransform = "none", LetterSpacing = "0.01em" },
        },
    };
}
