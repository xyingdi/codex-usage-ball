using Microsoft.Win32;
using System.Windows;
using System.Windows.Media;

namespace CodexUsageBall.Services;

public static class ThemeService
{
    public static string Apply(string preference)
    {
        var actual = preference switch
        {
            "Light" => "Light",
            "Dark" => "Dark",
            _ => IsWindowsLightTheme() ? "Light" : "Dark"
        };

        if (actual == "Light")
        {
            ApplyLight();
        }
        else
        {
            ApplyDark();
        }

        return actual;
    }

    private static void ApplyDark()
    {
        SetBrush("InkBrush", "#F5F5F0");
        SetBrush("MutedBrush", "#A1A19B");
        SetBrush("SubtleBrush", "#73736F");
        SetBrush("SurfaceBrush", "#191918");
        SetBrush("RaisedBrush", "#222220");
        SetBrush("BorderBrush", "#363633");
        SetBrush("ControlBrush", "#282826");
        SetBrush("ControlHoverBrush", "#323230");
        SetBrush("DividerBrush", "#333330");
        SetBrush("ProgressTrackBrush", "#393936");
        SetBrush("AccentBrush", "#B9F5C8");
        SetBrush("WarningBrush", "#FFD65A");
        SetBrush("DangerBrush", "#F17B76");
        SetBrush("PrimaryButtonBrush", "#F1F1EC");
        SetBrush("PrimaryButtonTextBrush", "#161615");
        SetBrush("BallOuterBrush", "#141413");
        SetBrush("BallBorderBrush", "#5A5A55");
        SetBrush("BallTextBrush", "#F5F5F0");
        SetBrush("BallTrackBrush", "#42423E");
        SetBrush("BallInnerStrokeBrush", "#FFFFFF");
        SetBrush("SwitchThumbBrush", "#D7D7D1");
        SetBrush("SwitchOnTrackBrush", "#EDEDE8");
        SetBrush("SwitchOnThumbBrush", "#171716");
        SetBallBodyBrush(
            (0d, "#4D4D48"),
            (0.40d, "#30302D"),
            (0.76d, "#1C1C1A"),
            (1d, "#121211"));
    }

    private static void ApplyLight()
    {
        SetBrush("InkBrush", "#1B1B19");
        SetBrush("MutedBrush", "#696964");
        SetBrush("SubtleBrush", "#92928B");
        SetBrush("SurfaceBrush", "#F3F2ED");
        SetBrush("RaisedBrush", "#E9E8E2");
        SetBrush("BorderBrush", "#CFCEC7");
        SetBrush("ControlBrush", "#E4E3DD");
        SetBrush("ControlHoverBrush", "#DAD9D2");
        SetBrush("DividerBrush", "#D2D1CA");
        SetBrush("ProgressTrackBrush", "#D0CFC8");
        SetBrush("AccentBrush", "#238A4B");
        SetBrush("WarningBrush", "#C98A00");
        SetBrush("DangerBrush", "#C94B47");
        SetBrush("PrimaryButtonBrush", "#1D1D1B");
        SetBrush("PrimaryButtonTextBrush", "#F7F7F2");
        SetBrush("BallOuterBrush", "#D9D8D1");
        SetBrush("BallBorderBrush", "#ADACA5");
        SetBrush("BallTextBrush", "#171716");
        SetBrush("BallTrackBrush", "#CAC9C2");
        SetBrush("BallInnerStrokeBrush", "#FFFFFF");
        SetBrush("SwitchThumbBrush", "#8E8E87");
        SetBrush("SwitchOnTrackBrush", "#242422");
        SetBrush("SwitchOnThumbBrush", "#F5F5F0");
        SetBallBodyBrush(
            (0d, "#FFFFFF"),
            (0.42d, "#F6F5EF"),
            (0.78d, "#E7E6DF"),
            (1d, "#D7D6CF"));
    }

    private static void SetBrush(string key, string color)
    {
        System.Windows.Application.Current.Resources[key] = new SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
    }

    private static void SetBallBodyBrush(params (double Offset, string Color)[] stops)
    {
        var brush = new RadialGradientBrush
        {
            Center = new System.Windows.Point(0.31d, 0.24d),
            GradientOrigin = new System.Windows.Point(0.31d, 0.24d),
            RadiusX = 0.88d,
            RadiusY = 0.88d
        };
        foreach (var stop in stops)
        {
            brush.GradientStops.Add(new GradientStop(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(stop.Color),
                stop.Offset));
        }

        System.Windows.Application.Current.Resources["BallBodyBrush"] = brush;
    }

    private static bool IsWindowsLightTheme()
    {
        try
        {
            var value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme",
                0);
            return value is int enabled && enabled != 0;
        }
        catch
        {
            return false;
        }
    }
}
