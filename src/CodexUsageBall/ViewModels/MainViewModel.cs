using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CodexUsageBall.Models;
using CodexUsageBall.Services;

namespace CodexUsageBall.ViewModels;

using MediaBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly CodexAppServerClient _client;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private UsageSnapshot? _snapshot;
    private bool _isLoading = true;
    private bool _hasError;
    private string _remainingText = "--";
    private double _remainingPercent;
    private string _planLabel = "CODEX";
    private string _lastUpdatedText = "等待数据";
    private string _errorText = string.Empty;
    private MediaBrush _usageBrush = new SolidColorBrush(MediaColor.FromRgb(161, 161, 155));
    private double _warningUsedPercent = 70d;
    private double _dangerUsedPercent = 90d;
    private bool _isLightTheme;
    private bool _animationsEnabled = true;
    private string? _selectedQuotaIdentity;

    public MainViewModel(CodexAppServerClient client)
    {
        _client = client;
        _client.RateLimitsChanged += (_, _) => _ = RefreshAsync();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<string?>? QuotaSelectionChanged;

    public ObservableCollection<QuotaItemViewModel> Quotas { get; } = new();
    public DateTimeOffset? LastRefreshAt { get; private set; }
    public bool IsLoading { get => _isLoading; private set => SetField(ref _isLoading, value); }
    public bool HasError { get => _hasError; private set => SetField(ref _hasError, value); }
    public string RemainingText { get => _remainingText; private set => SetField(ref _remainingText, value); }
    public double RemainingPercent { get => _remainingPercent; private set => SetField(ref _remainingPercent, value); }
    public string PlanLabel { get => _planLabel; private set => SetField(ref _planLabel, value); }
    public string LastUpdatedText { get => _lastUpdatedText; private set => SetField(ref _lastUpdatedText, value); }
    public string ErrorText { get => _errorText; private set => SetField(ref _errorText, value); }
    public MediaBrush UsageBrush { get => _usageBrush; private set => SetField(ref _usageBrush, value); }
    public string? SelectedQuotaIdentity => _selectedQuotaIdentity;
    public bool IsSmartQuotaSelection => string.IsNullOrEmpty(_selectedQuotaIdentity);
    public QuotaItemViewModel? DisplayedQuota => ResolveDisplayedQuota();

    public bool IsStale(TimeSpan age)
        => !LastRefreshAt.HasValue || DateTimeOffset.Now - LastRefreshAt.Value >= age;

    public void ConfigureUsageColors(
        double warningUsedPercent,
        double dangerUsedPercent,
        bool isLightTheme,
        bool animationsEnabled)
    {
        _warningUsedPercent = Math.Clamp(warningUsedPercent, 10d, 90d);
        _dangerUsedPercent = Math.Clamp(
            Math.Max(dangerUsedPercent, _warningUsedPercent + 5d),
            20d,
            100d);
        _isLightTheme = isLightTheme;
        _animationsEnabled = animationsEnabled;

        foreach (var quota in Quotas)
        {
            quota.UpdateUsageColor(
                _warningUsedPercent,
                _dangerUsedPercent,
                _isLightTheme,
                animationsEnabled);
        }

        if (!HasError) UpdateDisplayedQuota(animationsEnabled);
    }

    public void ConfigureQuotaSelection(string? identity)
    {
        _selectedQuotaIdentity = NormalizeQuotaIdentity(identity);
        if (Quotas.Count > 0 && _selectedQuotaIdentity is not null
                             && Quotas.All(quota => quota.Identity != _selectedQuotaIdentity))
        {
            _selectedQuotaIdentity = null;
        }
        UpdateDisplayedQuota(false);
        OnQuotaSelectionPropertiesChanged();
    }

    public bool SelectQuota(string? identity)
    {
        var normalized = NormalizeQuotaIdentity(identity);
        if (normalized is not null && Quotas.All(quota => quota.Identity != normalized)) normalized = null;
        if (string.Equals(_selectedQuotaIdentity, normalized, StringComparison.Ordinal)) return false;
        _selectedQuotaIdentity = normalized;
        UpdateDisplayedQuota(_animationsEnabled);
        OnQuotaSelectionPropertiesChanged();
        QuotaSelectionChanged?.Invoke(_selectedQuotaIdentity);
        return true;
    }

    public bool CycleDisplayedQuota()
    {
        if (Quotas.Count <= 1) return false;
        var displayed = ResolveDisplayedQuota();
        var currentIndex = displayed is null ? -1 : Quotas.IndexOf(displayed);
        var nextIndex = (currentIndex + 1 + Quotas.Count) % Quotas.Count;
        return SelectQuota(Quotas[nextIndex].Identity);
    }

    public async Task RefreshAsync(bool reconnect = false)
    {
        if (!await _refreshLock.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => IsLoading = true);
            if (reconnect) await _client.RestartAsync().ConfigureAwait(false);
            var snapshot = await _client.FetchSnapshotAsync().ConfigureAwait(false);
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => ApplySnapshot(snapshot));
        }
        catch (Exception exception)
        {
            var message = exception is CodexConnectionException
                ? exception.Message
                : "读取用量时发生未知错误。";
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => ApplyError(message));
        }
        finally
        {
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => IsLoading = false);
            _refreshLock.Release();
        }
    }

    public void UpdateRelativeTimes()
    {
        foreach (var quota in Quotas) quota.UpdateRelativeTime();
        if (LastRefreshAt.HasValue) LastUpdatedText = $"{LastRefreshAt:HH:mm} 更新";
    }

    private void ApplySnapshot(UsageSnapshot snapshot)
    {
        var hadSnapshot = _snapshot is not null;
        var previousQuotaBrushes = Quotas
            .GroupBy(quota => quota.Identity, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().UsageBrush, StringComparer.Ordinal);
        _snapshot = snapshot;
        LastRefreshAt = snapshot.UpdatedAt;
        HasError = false;
        ErrorText = string.Empty;
        PlanLabel = string.IsNullOrWhiteSpace(snapshot.PlanType) ? "CODEX" : snapshot.PlanType!.ToUpperInvariant();
        Quotas.Clear();
        foreach (var quota in CreateQuotaItems(
                     snapshot,
                     previousQuotaBrushes,
                     _animationsEnabled && hadSnapshot))
        {
            Quotas.Add(quota);
        }
        var selectionInvalidated = _selectedQuotaIdentity is not null
                                   && Quotas.All(quota => quota.Identity != _selectedQuotaIdentity);
        if (selectionInvalidated)
        {
            _selectedQuotaIdentity = null;
            OnQuotaSelectionPropertiesChanged();
        }
        UpdateDisplayedQuota(_animationsEnabled && hadSnapshot);
        if (selectionInvalidated) QuotaSelectionChanged?.Invoke(null);
        UpdateRelativeTimes();
    }

    private void ApplyError(string message)
    {
        HasError = true;
        ErrorText = message;
        UsageBrush = new SolidColorBrush(MediaColor.FromRgb(126, 126, 120));
        if (_snapshot is null)
        {
            RemainingPercent = 0d;
            RemainingText = "--";
            Quotas.Clear();
        }
    }

    private IEnumerable<QuotaItemViewModel> CreateQuotaItems(
        UsageSnapshot snapshot,
        Dictionary<string, MediaBrush>? previousBrushes = null,
        bool animate = false)
    {
        var items = new List<(QuotaBucket Bucket, QuotaWindow Window)>();
        foreach (var bucket in snapshot.Buckets)
        {
            if (bucket.Primary is not null) items.Add((bucket, bucket.Primary));
            if (bucket.Secondary is not null) items.Add((bucket, bucket.Secondary));
        }

        return items.OrderBy(item => item.Window.WindowDurationMinutes)
            .ThenBy(item => item.Bucket.LimitName ?? item.Bucket.LimitId, StringComparer.CurrentCulture)
            .Select(item =>
            {
                var identity = $"{item.Bucket.LimitId}\u001f{item.Window.WindowDurationMinutes}";
                var previousBrush = previousBrushes is not null
                                    && previousBrushes.TryGetValue(identity, out var brush)
                    ? brush
                    : null;
                return new QuotaItemViewModel(
                    identity,
                    BuildQuotaLabel(item.Bucket, item.Window),
                    item.Window,
                    _warningUsedPercent,
                    _dangerUsedPercent,
                    _isLightTheme,
                    previousBrush,
                    animate);
            });
    }

    private void UpdateDisplayedQuota(bool animate)
    {
        var displayed = ResolveDisplayedQuota();
        foreach (var quota in Quotas) quota.SetIsDisplayed(ReferenceEquals(quota, displayed));
        RemainingPercent = displayed?.RemainingPercent ?? _snapshot?.MostConstrainedRemaining ?? 0d;
        if (!HasError)
        {
            UsageBrush = CreateUsageBrush(
                GetUsageColor(
                    RemainingPercent,
                    _warningUsedPercent,
                    _dangerUsedPercent,
                    _isLightTheme),
                UsageBrush,
                animate);
        }
        RemainingText = displayed is null ? "--" : $"{Math.Round(RemainingPercent):0}%";
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayedQuota)));
    }

    private QuotaItemViewModel? ResolveDisplayedQuota()
    {
        if (_selectedQuotaIdentity is not null)
        {
            return Quotas.FirstOrDefault(quota => quota.Identity == _selectedQuotaIdentity);
        }

        if (Quotas.Count == 0) return null;
        var highestSeverity = Quotas.Max(GetQuotaSeverity);
        return highestSeverity == 0
            ? Quotas.OrderBy(quota => quota.WindowDurationMinutes)
                .ThenBy(quota => quota.Label, StringComparer.CurrentCulture)
                .First()
            : Quotas.Where(quota => GetQuotaSeverity(quota) == highestSeverity)
                .OrderBy(quota => quota.RemainingPercent)
                .ThenBy(quota => quota.WindowDurationMinutes)
                .First();
    }

    private int GetQuotaSeverity(QuotaItemViewModel quota)
        => quota.UsedPercent >= _dangerUsedPercent ? 2
            : quota.UsedPercent >= _warningUsedPercent ? 1
            : 0;

    private void OnQuotaSelectionPropertiesChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedQuotaIdentity)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSmartQuotaSelection)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayedQuota)));
    }

    private static string? NormalizeQuotaIdentity(string? identity)
        => string.IsNullOrWhiteSpace(identity) ? null : identity.Trim();

    private static string BuildQuotaLabel(QuotaBucket bucket, QuotaWindow window)
    {
        var duration = FormatDuration(window.WindowDurationMinutes);
        return string.Equals(bucket.LimitId, "codex", StringComparison.OrdinalIgnoreCase)
               || string.IsNullOrWhiteSpace(bucket.LimitName)
            ? $"{duration}额度"
            : $"{bucket.LimitName} · {duration}";
    }

    internal static MediaColor GetUsageColor(
        double remaining,
        double warningUsedPercent,
        double dangerUsedPercent,
        bool isLightTheme)
    {
        var used = 100d - Math.Clamp(remaining, 0d, 100d);
        var warning = Math.Clamp(warningUsedPercent, 10d, 90d);
        var danger = Math.Clamp(Math.Max(dangerUsedPercent, warning + 5d), 20d, 100d);
        var green = isLightTheme
            ? MediaColor.FromRgb(35, 138, 75)
            : MediaColor.FromRgb(185, 245, 200);
        var yellow = isLightTheme
            ? MediaColor.FromRgb(201, 138, 0)
            : MediaColor.FromRgb(255, 214, 90);
        var red = isLightTheme
            ? MediaColor.FromRgb(201, 75, 71)
            : MediaColor.FromRgb(241, 123, 118);
        var deepRed = isLightTheme
            ? MediaColor.FromRgb(181, 60, 57)
            : MediaColor.FromRgb(228, 94, 89);

        var greenHoldEnd = warning * 0.45d;
        if (used <= greenHoldEnd) return green;

        if (used < warning)
        {
            var progress = SmoothStep((used - greenHoldEnd) / (warning - greenHoldEnd));
            return InterpolateHsl(green, yellow, progress);
        }

        if (used < danger)
        {
            var progress = SmoothStep((used - warning) / (danger - warning));
            return InterpolateHsl(yellow, red, progress);
        }

        if (danger >= 100d) return red;
        var exhaustionProgress = SmoothStep((used - danger) / (100d - danger));
        return InterpolateHsl(red, deepRed, exhaustionProgress);
    }

    private static double SmoothStep(double value)
    {
        var t = Math.Clamp(value, 0d, 1d);
        return t * t * (3d - 2d * t);
    }

    private static MediaColor InterpolateHsl(MediaColor from, MediaColor to, double amount)
    {
        var t = Math.Clamp(amount, 0d, 1d);
        if (t <= 0d) return from;
        if (t >= 1d) return to;

        var start = ToHsl(from);
        var end = ToHsl(to);
        var hueDelta = (end.Hue - start.Hue + 540d) % 360d - 180d;
        return FromHsl(
            (start.Hue + hueDelta * t + 360d) % 360d,
            start.Saturation + (end.Saturation - start.Saturation) * t,
            start.Lightness + (end.Lightness - start.Lightness) * t);
    }

    private static (double Hue, double Saturation, double Lightness) ToHsl(MediaColor color)
    {
        var red = color.R / 255d;
        var green = color.G / 255d;
        var blue = color.B / 255d;
        var maximum = Math.Max(red, Math.Max(green, blue));
        var minimum = Math.Min(red, Math.Min(green, blue));
        var delta = maximum - minimum;
        var lightness = (maximum + minimum) / 2d;
        if (delta <= double.Epsilon) return (0d, 0d, lightness);

        var saturation = delta / (1d - Math.Abs(2d * lightness - 1d));
        var hue = maximum == red
            ? 60d * (((green - blue) / delta) % 6d)
            : maximum == green
                ? 60d * ((blue - red) / delta + 2d)
                : 60d * ((red - green) / delta + 4d);
        if (hue < 0d) hue += 360d;
        return (hue, saturation, lightness);
    }

    private static MediaColor FromHsl(double hue, double saturation, double lightness)
    {
        var chroma = (1d - Math.Abs(2d * lightness - 1d)) * saturation;
        var segment = hue / 60d;
        var intermediate = chroma * (1d - Math.Abs(segment % 2d - 1d));
        var (red, green, blue) = segment switch
        {
            < 1d => (chroma, intermediate, 0d),
            < 2d => (intermediate, chroma, 0d),
            < 3d => (0d, chroma, intermediate),
            < 4d => (0d, intermediate, chroma),
            < 5d => (intermediate, 0d, chroma),
            _ => (chroma, 0d, intermediate)
        };
        var match = lightness - chroma / 2d;
        return MediaColor.FromRgb(
            ToColorByte(red + match),
            ToColorByte(green + match),
            ToColorByte(blue + match));
    }

    private static byte ToColorByte(double value)
        => (byte)Math.Clamp(Math.Round(value * 255d), 0d, 255d);

    internal static MediaBrush CreateUsageBrush(
        MediaColor target,
        MediaBrush? previous,
        bool animate)
    {
        var from = previous is SolidColorBrush solid ? solid.Color : target;
        var brush = new SolidColorBrush(target);
        if (animate && from != target)
        {
            brush.BeginAnimation(
                SolidColorBrush.ColorProperty,
                new ColorAnimation(from, target, TimeSpan.FromMilliseconds(500))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    FillBehavior = FillBehavior.Stop
                });
        }

        return brush;
    }

    internal static string FormatDuration(int minutes)
    {
        if (minutes <= 0) return "当前";
        if (minutes % 10_080 == 0)
        {
            var weeks = minutes / 10_080;
            return weeks == 1 ? "7 天" : $"{weeks} 周";
        }
        if (minutes % 1_440 == 0) return $"{minutes / 1_440} 天";
        if (minutes % 60 == 0) return $"{minutes / 60} 小时";
        return $"{minutes} 分钟";
    }

    internal static string FormatCountdown(DateTimeOffset resetsAt)
    {
        var remaining = resetsAt - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero) return "即将";
        if (remaining.TotalDays >= 1d) return $"{(int)remaining.TotalDays} 天 {remaining.Hours} 小时";
        if (remaining.TotalHours >= 1d) return $"{(int)remaining.TotalHours} 小时 {remaining.Minutes} 分";
        if (remaining.TotalMinutes >= 1d) return $"{(int)remaining.TotalMinutes} 分";
        return "不到 1 分钟";
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

public sealed class QuotaItemViewModel : INotifyPropertyChanged
{
    private string _resetText = string.Empty;
    private bool _isDisplayed;
    private MediaBrush _usageBrush;

    public QuotaItemViewModel(
        string identity,
        string label,
        QuotaWindow window,
        double warningUsedPercent,
        double dangerUsedPercent,
        bool isLightTheme,
        MediaBrush? previousBrush,
        bool animate)
    {
        Identity = identity;
        Label = label;
        UsedPercent = window.UsedPercent;
        RemainingPercent = window.RemainingPercent;
        WindowDurationMinutes = window.WindowDurationMinutes;
        UsedText = $"已用 {Math.Round(window.UsedPercent):0}%";
        _usageBrush = MainViewModel.CreateUsageBrush(
            MainViewModel.GetUsageColor(
                RemainingPercent,
                warningUsedPercent,
                dangerUsedPercent,
                isLightTheme),
            previousBrush,
            animate);
        ResetsAt = window.ResetsAt;
        UpdateRelativeTime();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string Identity { get; }
    public string Label { get; }
    public double UsedPercent { get; }
    public double RemainingPercent { get; }
    public int WindowDurationMinutes { get; }
    public string RemainingText => $"{Math.Round(RemainingPercent):0}% 剩余";
    public string UsedText { get; }
    public MediaBrush UsageBrush
    {
        get => _usageBrush;
        private set
        {
            if (ReferenceEquals(_usageBrush, value)) return;
            _usageBrush = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UsageBrush)));
        }
    }
    public DateTimeOffset? ResetsAt { get; }
    public bool IsDisplayed
    {
        get => _isDisplayed;
        private set
        {
            if (_isDisplayed == value) return;
            _isDisplayed = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDisplayed)));
        }
    }
    public string ResetText
    {
        get => _resetText;
        private set
        {
            if (_resetText == value) return;
            _resetText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ResetText)));
        }
    }

    public void UpdateRelativeTime()
        => ResetText = ResetsAt.HasValue
            ? $"{MainViewModel.FormatCountdown(ResetsAt.Value)}后重置"
            : "未提供重置时间";

    public void UpdateUsageColor(
        double warningUsedPercent,
        double dangerUsedPercent,
        bool isLightTheme,
        bool animate)
        => UsageBrush = MainViewModel.CreateUsageBrush(
            MainViewModel.GetUsageColor(
                RemainingPercent,
                warningUsedPercent,
                dangerUsedPercent,
                isLightTheme),
            UsageBrush,
            animate);

    public void SetIsDisplayed(bool isDisplayed) => IsDisplayed = isDisplayed;
}
