using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AionToolPro.Desktop.Models;

namespace AionToolPro.Desktop.Themes
{
    public static class ThemeManager
    {
        public static void ApplyTheme(ThemeDefinition theme, bool isDarkMode, int surfacePaletteId = 0)
        {
            var res = Application.Current.Resources;
            var surface = AioCatalog.SurfacePalettes.FirstOrDefault(s => s.Id == surfacePaletteId) ?? AioCatalog.SurfacePalettes[0];

            bool useLight = theme.IsLightSurface || surface.IsLightSurface || !isDarkMode;

            string canvasHex, sidebarHex, cardHex, cardHoverHex, inputHex, borderHex;

            if (useLight)
            {
                canvasHex    = "#F1F5F9";
                sidebarHex   = "#FFFFFF";
                cardHex      = "#FFFFFF";
                cardHoverHex = "#F8FAFC";
                inputHex     = "#F8FAFC";
                borderHex    = "#CBD5E1";
            }
            else if (!surface.UseThemeDefault)
            {
                canvasHex    = surface.CanvasHex;
                sidebarHex   = surface.SidebarHex;
                cardHex      = surface.CardHex;
                cardHoverHex = surface.CardHoverHex;
                inputHex     = surface.InputHex;
                borderHex    = surface.BorderHex;
            }
            else
            {
                canvasHex    = theme.CanvasHex;
                sidebarHex   = theme.SidebarHex;
                cardHex      = theme.CardHex;
                cardHoverHex = theme.CardHoverHex;
                inputHex     = theme.InputHex;
                borderHex    = theme.BorderHex;
            }

            Color canvasColor    = ParseColor(canvasHex);
            Color sidebarColor   = ParseColor(sidebarHex);
            Color cardColor      = ParseColor(cardHex);
            Color cardHoverColor = ParseColor(cardHoverHex);
            Color inputBgColor   = ParseColor(inputHex);
            Color borderColor    = ParseColor(borderHex);
            Color primaryText    = ParseColor(useLight ? "#0F172A" : "#F8FAFC");
            Color mutedText      = ParseColor(useLight ? "#475569" : "#94A3B8");

            Color accentColor    = ParseColor(theme.AccentHex);
            Color secondaryColor = ParseColor(theme.SecondaryHex);

            SetAnimatedBrush(res, "WindowCanvasBrush", canvasColor);
            SetAnimatedBrush(res, "SidebarSurfaceBrush", sidebarColor);
            SetAnimatedBrush(res, "CardSurfaceBrush", cardColor);
            SetAnimatedBrush(res, "CardHoverBrush", cardHoverColor);
            SetAnimatedBrush(res, "InputSurfaceBrush", inputBgColor);
            SetAnimatedBrush(res, "BorderHairlineBrush", borderColor);
            SetAnimatedBrush(res, "PrimaryTextBrush", primaryText);
            SetAnimatedBrush(res, "MutedTextBrush", mutedText);
            SetAnimatedBrush(res, "AccentBrush", accentColor);
            SetAnimatedBrush(res, "SecondaryAccentBrush", secondaryColor);

            var softAccent = Color.FromArgb(useLight ? (byte)35 : (byte)45, accentColor.R, accentColor.G, accentColor.B);
            SetAnimatedBrush(res, "AccentSoftBrush", softAccent);

            var grad = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            grad.GradientStops.Add(new GradientStop(accentColor, 0.0));
            grad.GradientStops.Add(new GradientStop(secondaryColor, 1.0));
            grad.Freeze();
            res["AccentGradientBrush"] = grad;

            SetFrozenBrush(res, "InfoBlueBrush",       ParseColor("#38BDF8"));
            SetFrozenBrush(res, "SettingsYellowBrush", ParseColor("#FACC15"));
            SetFrozenBrush(res, "FixesOrangeBrush",    ParseColor("#FB923C"));
            SetFrozenBrush(res, "DangerRedBrush",      ParseColor("#F43F5E"));
            SetFrozenBrush(res, "SuccessGreenBrush",   ParseColor("#10B981"));
        }

        private static Color ParseColor(string hex) => ColorConverter.ConvertFromString(hex) is Color c ? c : Colors.White;

        private static void SetFrozenBrush(ResourceDictionary res, string key, Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            res[key] = brush;
        }

        private static void SetAnimatedBrush(ResourceDictionary res, string key, Color targetColor)
        {
            if (res[key] is SolidColorBrush existing && !existing.IsFrozen)
            {
                var anim = new ColorAnimation(targetColor, TimeSpan.FromMilliseconds(220))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                existing.BeginAnimation(SolidColorBrush.ColorProperty, anim);
            }
            else
            {
                res[key] = new SolidColorBrush(targetColor);
            }
        }
    }
}
