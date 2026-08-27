using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace CodexUsageBall.Services;

public sealed class AppSettings
{
    public const double DefaultBallSize = 64d;
    public const double DefaultBallOpacity = 1d;
    public const string DefaultTheme = "System";
    public const double DefaultWarningUsedPercent = 70d;
    public const double DefaultDangerUsedPercent = 90d;
    public const double DefaultPanelWidth = 372d;
    public const double DefaultPanelHeight = 560d;

    public bool HasSavedPosition { get; set; }
    public double Left { get; set; }
    public double Top { get; set; }
    public bool AlwaysOnTop { get; set; } = true;
    public bool AutoShowWithCodex { get; set; } = true;
    public bool HideWhenCodexCloses { get; set; } = true;
    public double BallSize { get; set; } = DefaultBallSize;
    public double BallOpacity { get; set; } = DefaultBallOpacity;
    public string Theme { get; set; } = DefaultTheme;
    public double WarningUsedPercent { get; set; } = DefaultWarningUsedPercent;
    public double DangerUsedPercent { get; set; } = DefaultDangerUsedPercent;
    public double SettingsPanelWidth { get; set; } = DefaultPanelWidth;
    public double SettingsPanelHeight { get; set; } = DefaultPanelHeight;
    public string SelectedQuotaIdentity { get; set; } = string.Empty;
    public bool SingleClickCyclesQuota { get; set; }
    public bool AnimationsEnabled { get; set; } = true;

    public void ResetAll()
    {
        var defaults = new AppSettings();
        HasSavedPosition = defaults.HasSavedPosition;
        Left = defaults.Left;
        Top = defaults.Top;
        AlwaysOnTop = defaults.AlwaysOnTop;
        AutoShowWithCodex = defaults.AutoShowWithCodex;
        HideWhenCodexCloses = defaults.HideWhenCodexCloses;
        BallSize = defaults.BallSize;
        BallOpacity = defaults.BallOpacity;
        Theme = defaults.Theme;
        WarningUsedPercent = defaults.WarningUsedPercent;
        DangerUsedPercent = defaults.DangerUsedPercent;
        SettingsPanelWidth = defaults.SettingsPanelWidth;
        SettingsPanelHeight = defaults.SettingsPanelHeight;
        SelectedQuotaIdentity = defaults.SelectedQuotaIdentity;
        SingleClickCyclesQuota = defaults.SingleClickCyclesQuota;
        AnimationsEnabled = defaults.AnimationsEnabled;
    }
}

public sealed class SettingsService
{
    private const string RunValueName = "CodexUsageBall";
    private readonly string _settingsDirectory;
    private readonly string _settingsPath;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public SettingsService()
    {
        _settingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexUsageBall");
        _settingsPath = Path.Combine(_settingsDirectory, "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return new AppSettings();
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath), _jsonOptions)
                           ?? new AppSettings();
            settings.BallSize = Math.Clamp(settings.BallSize, 48d, 96d);
            settings.BallOpacity = Math.Clamp(settings.BallOpacity, 0.3d, 1d);
            settings.Theme = settings.Theme is "Light" or "Dark" ? settings.Theme : "System";
            settings.WarningUsedPercent = Math.Clamp(
                Math.Round(settings.WarningUsedPercent / 5d) * 5d,
                10d,
                90d);
            settings.DangerUsedPercent = Math.Clamp(
                Math.Round(settings.DangerUsedPercent / 5d) * 5d,
                20d,
                100d);
            if (settings.DangerUsedPercent < settings.WarningUsedPercent + 5d)
            {
                settings.DangerUsedPercent = Math.Min(100d, settings.WarningUsedPercent + 5d);
            }
            settings.SettingsPanelWidth = ClampFinite(settings.SettingsPanelWidth, 340d, 1200d, AppSettings.DefaultPanelWidth);
            settings.SettingsPanelHeight = ClampFinite(settings.SettingsPanelHeight, 430d, 1600d, AppSettings.DefaultPanelHeight);
            settings.SelectedQuotaIdentity = settings.SelectedQuotaIdentity?.Trim() ?? string.Empty;
            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(_settingsDirectory);
            var temporaryPath = _settingsPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, _jsonOptions));
            File.Move(temporaryPath, _settingsPath, true);
        }
        catch
        {
            // Settings must never interrupt the floating meter.
        }
    }

    private static double ClampFinite(double value, double minimum, double maximum, double fallback)
        => double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;

    public static bool SetStartWithWindows(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run",
                writable: true);
            if (key is null) return false;

            if (enabled)
            {
                var executable = Environment.ProcessPath
                    ?? throw new InvalidOperationException("无法定位当前程序路径。");
                key.SetValue(RunValueName, $"\"{executable}\" --startup", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

}
