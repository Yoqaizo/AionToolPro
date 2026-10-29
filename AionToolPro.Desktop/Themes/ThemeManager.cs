using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AionToolPro.Desktop.Models;

namespace AionToolPro.Desktop.Themes
{
    public static class ThemeManager
    {
        public static void ApplyTheme(ThemeDefinition theme, bool isDarkMode)
        {
            var res = Application.Current.Resources;
            bool useLight = theme.IsLightSurface || !isDarkMode;

            Color canvasColor    = (Color)ColorConverter.ConvertFromString(useLight ? "#F1F5F9" : "#0B0E14");
            Color sidebarColor   = (Color)ColorConverter.ConvertFromString(useLight ? "#FFFFFF" : "#11151F");
            Color cardColor      = (Color)ColorConverter.ConvertFromString(useLight ? "#FFFFFF" : "#171C28");
            Color cardHoverColor = (Color)ColorConverter.ConvertFromString(useLight ? "#F8FAFC" : "#1F2637");
            Color inputBgColor   = (Color)ColorConverter.ConvertFromString(useLight ? "#F8FAFC" : "#0D111A");
            Color borderColor    = (Color)ColorConverter.ConvertFromString(useLight ? "#CBD5E1" : "#263044");
            Color primaryText    = (Color)ColorConverter.ConvertFromString(useLight ? "#0F172A" : "#F8FAFC");
            Color mutedText      = (Color)ColorConverter.ConvertFromString(useLight ? "#475569" : "#94A3B8");

            var accentColor    = (Color)ColorConverter.ConvertFromString(theme.AccentHex);
            var secondaryColor = (Color)ColorConverter.ConvertFromString(theme.SecondaryHex);

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
            res["AccentGradientBrush"] = grad;

            SetAnimatedBrush(res, "InfoBlueBrush",       (Color)ColorConverter.ConvertFromString("#38BDF8"));
            SetAnimatedBrush(res, "SettingsYellowBrush", (Color)ColorConverter.ConvertFromString("#FACC15"));
            SetAnimatedBrush(res, "FixesOrangeBrush",    (Color)ColorConverter.ConvertFromString("#FB923C"));
            SetAnimatedBrush(res, "DangerRedBrush",      (Color)ColorConverter.ConvertFromString("#F43F5E"));
            SetAnimatedBrush(res, "SuccessGreenBrush",   (Color)ColorConverter.ConvertFromString("#10B981"));
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
