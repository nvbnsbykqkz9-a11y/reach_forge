using MudBlazor;

namespace ReachForge.Web.Theme;

/// <summary>デザイントークンを MudTheme に集約する（RF-UX-001 4章 / 14.2）。画面のコードに色コードを直接書かない。</summary>
public static class RfTheme
{
    public static readonly MudTheme Default = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#0A5BD6",        // color.primary（対白 6.03:1）
            Secondary = "#5443D1",      // color.ai
            Success = "#00775F",
            Warning = "#B26A00",        // 文字色には使わない
            Error = "#C0392B",
            Info = "#0A5BD6",
            TextPrimary = "#1B2333",
            TextSecondary = "#4A5568",
            Background = "#F3F6FB",
            Surface = "#FFFFFF",
            AppbarBackground = "#FFFFFF",
            AppbarText = "#1B2333",
            DrawerBackground = "#FFFFFF",
            LinesDefault = "#D7DDE8",
            LinesInputs = "#8C96A8",
        },
        PaletteDark = new PaletteDark
        {
            Primary = "#8AB4FF",
            Secondary = "#B9A8FF",
            Success = "#6EE7C5",
            Error = "#FF8A7A",
            TextPrimary = "#E6EDF7",
            TextSecondary = "#A9B4C6",
            Background = "#0F1726",
            Surface = "#162033",
            AppbarBackground = "#162033",
            DrawerBackground = "#162033",
        },
        Typography = new Typography
        {
            Default = new DefaultTypography
            {
                FontFamily = ["Noto Sans JP", "Hiragino Sans", "Yu Gothic UI", "Meiryo", "sans-serif"],
                FontSize = "0.875rem",
                LineHeight = "1.7",
            },
            H4 = new H4Typography { FontSize = "1.5rem", FontWeight = "700", LineHeight = "1.4" },
            H5 = new H5Typography { FontSize = "1.25rem", FontWeight = "700", LineHeight = "1.4" },
            H6 = new H6Typography { FontSize = "1rem", FontWeight = "700", LineHeight = "1.5" },
            Caption = new CaptionTypography { FontSize = "0.75rem", LineHeight = "1.6" },
            Button = new ButtonTypography { FontSize = "0.8125rem", FontWeight = "500", TextTransform = "none" },
        },
        LayoutProperties = new LayoutProperties { DefaultBorderRadius = "8px", DrawerWidthLeft = "240px", AppbarHeight = "56px" },
    };
}
