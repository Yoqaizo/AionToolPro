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
        public static (bool IsDarkSurface, Color SolidCanvasColor) ApplyTheme(
            ThemeDefinition theme,
            bool isDarkMode,
            int surfacePaletteId = 0,
            bool useFrostedAlpha = false,
            int transparencyPercent = 100,
            double brightness = 1.0,
            double contrast = 1.0,
            bool animate = true,
            string? customAccentHex = null,
            string? customSecondaryHex = null)
        {
            var res = Application.Current.Resources;
            var surface = AioCatalog.SurfacePalettes.FirstOrDefault(s => s.Id == surfacePaletteId) ?? AioCatalog.SurfacePalettes[0];

            bool useLight = theme.IsLightSurface || surface.IsLightSurface || !isDarkMode;

            brightness = Math.Clamp(brightness, 0.75, 1.30);
            contrast = Math.Clamp(contrast, 0.80, 1.35);
            transparencyPercent = Math.Clamp(transparencyPercent, 35, 100);

            string canvasHex, sidebarHex, cardHex, cardHoverHex, inputHex, borderHex;

            if (useLight)
            {
                canvasHex    = "#EEF2F7";
                sidebarHex   = "#F8FAFC";
                cardHex      = "#FFFFFF";
                cardHoverHex = "#F1F5F9";
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

            Color canvasColor    = CalibrateColor(ParseColor(canvasHex), brightness, contrast);
            Color sidebarColor   = CalibrateColor(ParseColor(sidebarHex), brightness, contrast);
            Color cardColor      = CalibrateColor(ParseColor(cardHex), brightness, contrast);
            Color cardHoverColor = CalibrateColor(ParseColor(cardHoverHex), brightness, contrast);
            Color inputBgColor   = CalibrateColor(ParseColor(inputHex), brightness, contrast);
            Color borderColor    = CalibrateColor(ParseColor(borderHex), brightness, contrast);
            Color solidCanvas    = Color.FromRgb(canvasColor.R, canvasColor.G, canvasColor.B);
            Color popupColor     = Color.FromArgb(248, cardColor.R, cardColor.G, cardColor.B);

            double textContrast = 1.0 + (contrast - 1.0) * 0.45;
            Color primaryText    = CalibrateColor(ParseColor(useLight ? "#0F172A" : "#F8FAFC"), 1.0, textContrast);
            Color mutedText      = CalibrateColor(ParseColor(useLight ? "#475569" : "#9AA8BE"), Math.Clamp(brightness, 0.96, 1.15), Math.Clamp(textContrast, 0.95, 1.15));

            string effectiveAccentHex = theme.IsCustom && !string.IsNullOrWhiteSpace(customAccentHex) ? customAccentHex : theme.AccentHex;
            string effectiveSecondaryHex = theme.IsCustom && !string.IsNullOrWhiteSpace(customSecondaryHex) ? customSecondaryHex : theme.SecondaryHex;

            Color accentColor    = CalibrateColor(ParseColor(effectiveAccentHex), Math.Clamp(brightness, 0.85, 1.20), Math.Clamp(contrast, 0.85, 1.25));
            Color secondaryColor = CalibrateColor(ParseColor(effectiveSecondaryHex), Math.Clamp(brightness, 0.85, 1.20), Math.Clamp(contrast, 0.85, 1.25));

            // Real Frosted Glass Alpha: keeps nested layers light so DWM Acrylic blur shines unmistakably through!
            if (useFrostedAlpha)
            {
                // Map slider 35..100 -> t in 0.0..1.0
                double t = (transparencyPercent - 35.0) / 65.0;
                byte canvasAlpha    = ToAlpha(0.06 + t * 0.50); // 6% .. 56% (at 68%: ~31% -> crystal-clear glass!)
                byte sidebarAlpha   = ToAlpha(0.16 + t * 0.50); // 16% .. 66%
                byte cardAlpha      = ToAlpha(0.22 + t * 0.50); // 22% .. 72%
                byte cardHoverAlpha = ToAlpha(0.34 + t * 0.46); // 34% .. 80%
                byte inputAlpha     = ToAlpha(0.28 + t * 0.48); // 28% .. 76%
                byte borderAlpha    = useLight ? (byte)165 : (byte)110;

                canvasColor    = Color.FromArgb(canvasAlpha, canvasColor.R, canvasColor.G, canvasColor.B);
                sidebarColor   = Color.FromArgb(sidebarAlpha, sidebarColor.R, sidebarColor.G, sidebarColor.B);
                cardColor      = Color.FromArgb(cardAlpha, cardColor.R, cardColor.G, cardColor.B);
                cardHoverColor = Color.FromArgb(cardHoverAlpha, cardHoverColor.R, cardHoverColor.G, cardHoverColor.B);
                inputBgColor   = Color.FromArgb(inputAlpha, inputBgColor.R, inputBgColor.G, inputBgColor.B);
                borderColor    = useLight
                    ? Color.FromArgb(borderAlpha, borderColor.R, borderColor.G, borderColor.B)
                    : Color.FromArgb(borderAlpha, (byte)Math.Min(255, borderColor.R + 55), (byte)Math.Min(255, borderColor.G + 65), (byte)Math.Min(255, borderColor.B + 85));
            }

            SetBrush(res, "WindowCanvasBrush", canvasColor, animate);
            SetBrush(res, "SidebarSurfaceBrush", sidebarColor, animate);
            SetBrush(res, "CardSurfaceBrush", cardColor, animate);
            SetBrush(res, "CardHoverBrush", cardHoverColor, animate);
            SetBrush(res, "InputSurfaceBrush", inputBgColor, animate);
            SetBrush(res, "BorderHairlineBrush", borderColor, animate);
            SetBrush(res, "PrimaryTextBrush", primaryText, animate);
            SetBrush(res, "MutedTextBrush", mutedText, animate);
            SetBrush(res, "AccentBrush", accentColor, animate);
            SetBrush(res, "SecondaryAccentBrush", secondaryColor, animate);

            var softAccent = Color.FromArgb(useLight ? (byte)36 : (byte)52, accentColor.R, accentColor.G, accentColor.B);
            SetBrush(res, "AccentSoftBrush", softAccent, animate);

            // Crisp specular glass highlight border
            var glassEdge = useFrostedAlpha
                ? (useLight ? Color.FromArgb(200, 255, 255, 255) : Color.FromArgb(72, 255, 255, 255))
                : borderColor;
            SetBrush(res, "GlassEdgeBrush", glassEdge, animate);
            SetBrush(res, "PopupSurfaceBrush", popupColor, animate);

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

            return (!useLight, solidCanvas);
        }

        private static byte ToAlpha(double ratio) => (byte)Math.Clamp((int)Math.Round(ratio * 255.0), 14, 255);

        private static Color CalibrateColor(Color src, double brightness, double contrast)
        {
            if (Math.Abs(brightness - 1.0) < 0.005 && Math.Abs(contrast - 1.0) < 0.005)
                return src;

            byte Adjust(byte ch)
            {
                double n = ch / 255.0;
                n = (n - 0.5) * contrast + 0.5;
                n = n * brightness + (brightness - 1.0) * 0.14;
                return (byte)Math.Clamp((int)Math.Round(n * 255.0), 0, 255);
            }

            return Color.FromArgb(src.A, Adjust(src.R), Adjust(src.G), Adjust(src.B));
        }

        private static Color ParseColor(string hex) => ColorConverter.ConvertFromString(hex) is Color c ? c : Colors.White;

        private static void SetFrozenBrush(ResourceDictionary res, string key, Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            res[key] = brush;
        }

        private static void SetBrush(ResourceDictionary res, string key, Color targetColor, bool animate)
        {
            if (res[key] is SolidColorBrush existing && !existing.IsFrozen)
            {
                if (animate)
                {
                    var anim = new ColorAnimation(targetColor, TimeSpan.FromMilliseconds(180))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    };
                    existing.BeginAnimation(SolidColorBrush.ColorProperty, anim);
                }
                else
                {
                    existing.BeginAnimation(SolidColorBrush.ColorProperty, null);
                    existing.Color = targetColor;
                }
            }
            else
            {
                res[key] = new SolidColorBrush(targetColor);
            }
        }
    }
}
