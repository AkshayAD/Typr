using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Ava.Windows;

internal static class ThemeManager
{
    public static void Apply(string theme, string accent)
    {
        var application = Application.Current;
        if (application == null || application.Dispatcher.HasShutdownStarted) return;
        if (!application.Dispatcher.CheckAccess())
        {
            application.Dispatcher.BeginInvoke(new Action(() => Apply(theme, accent)));
            return;
        }

        var resources = application.Resources;
        if (SystemParameters.HighContrast)
        {
            // Reuse the user's guaranteed window/text pair. Accent surfaces are inverted
            // because the same accent token also appears as text on ordinary surfaces.
            foreach (string key in new[] { "CanvasBrush", "SurfaceBrush", "RailBrush", "InsetBrush", "AccentSoftBrush", "AccentTextBrush" })
                resources[key] = SystemColors.WindowBrush;
            foreach (string key in new[] { "TextBrush", "MutedBrush", "LineBrush", "AccentBrush", "SuccessBrush", "DangerBrush" })
                resources[key] = SystemColors.WindowTextBrush;
            return;
        }

        bool dark = string.Equals(theme, "Dark", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(theme, "System", StringComparison.OrdinalIgnoreCase) && WindowsUsesDarkApps();

        Set("CanvasBrush", dark ? "#1B1D1C" : "#F7F7F2");
        Set("SurfaceBrush", dark ? "#242725" : "#FFFFFF");
        Set("RailBrush", dark ? "#161917" : "#EFEEE8");
        Set("InsetBrush", dark ? "#2B2F2C" : "#F5F5F0");
        Set("TextBrush", dark ? "#F1F2EE" : "#262A28");
        Set("MutedBrush", dark ? "#BCC1B9" : "#626861");
        Set("LineBrush", dark ? "#444A45" : "#DDDFD5");
        Set("SuccessBrush", dark ? "#91CFAC" : "#276449");
        Set("DangerBrush", dark ? "#FFA6A1" : "#AD3737");

        var (accentColor, softColor) = accent?.ToUpperInvariant() switch
        {
            "FOREST" => dark ? ("#91CFAC", "#223C2E") : ("#2A674A", "#E3F1E8"),
            "OCEAN" => dark ? ("#91C9EC", "#243B49") : ("#226384", "#E5EFF6"),
            _ => dark ? ("#B6AAF1", "#393347") : ("#5D51A2", "#ECE9F8")
        };
        Set("AccentBrush", accentColor);
        Set("AccentSoftBrush", softColor);
        Set("AccentTextBrush", dark ? "#1B1D1C" : "#FFFFFF");

        void Set(string key, string color)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            brush.Freeze();
            resources[key] = brush;
        }
    }

    private static bool WindowsUsesDarkApps()
    {
        try
        {
            return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", null) is int value && value == 0;
        }
        catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}
