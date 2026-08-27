using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CodexUsageBall.Services;

namespace CodexUsageBall;

public partial class SettingsPanelWindow : Window
{
    private const int WmNcHitTest = 0x0084;
    private const int WmEnterSizeMove = 0x0231;
    private const int WmExitSizeMove = 0x0232;
    private const int HtClient = 1;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;
    private const double ScreenInset = 12d;
    private const double ResizeBorderThickness = 7d;
    private bool _initializing = true;
    private bool _allowClose;
    private bool _dismissRequested;
    private bool _adjustingThresholds;
    private bool _isSizingOrMoving;
    private bool _animationsEnabled = true;
    private Rect _workArea = Rect.Empty;

    public SettingsPanelWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
        Deactivated += (_, _) => { if (!_isSizingOrMoving) RequestDismiss(); };
        PreviewKeyDown += OnPreviewKeyDown;
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(SettingsMessageHook);
    }

    public event Action<double>? BallSizeChanged;
    public event Action<double>? BallOpacityChanged;
    public event Action<string>? ThemeChanged;
    public event Action<double, double>? UsageThresholdsChanged;
    public event Action<double, double>? PanelSizeChanged;
    public event Action<bool>? TopmostChanged;
    public event Action<bool>? AutoShowWithCodexChanged;
    public event Action<bool>? HideWhenCodexClosesChanged;
    public event Action<bool>? SingleClickCyclesQuotaChanged;
    public event Action<bool>? AnimationsChanged;
    public event EventHandler? AllSettingsResetRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler? DismissRequested;

    public void LoadSettings(AppSettings settings, bool forcePanelSize = false)
    {
        _initializing = true;
        if (!IsVisible || forcePanelSize)
        {
            Width = ClampFinite(settings.SettingsPanelWidth, MinWidth, 1200d, 372d);
            Height = ClampFinite(settings.SettingsPanelHeight, MinHeight, 1600d, 560d);
            if (forcePanelSize)
            {
                ApplyWorkAreaLimits();
                ClampPositionToWorkArea();
            }
        }
        SizeSlider.Value = settings.BallSize;
        SizeValueText.Text = $"{Math.Round(settings.BallSize):0} px";
        OpacitySlider.Value = settings.BallOpacity * 100d;
        OpacityValueText.Text = $"{Math.Round(settings.BallOpacity * 100d):0}%";
        SystemThemeOption.IsChecked = settings.Theme == "System";
        LightThemeOption.IsChecked = settings.Theme == "Light";
        DarkThemeOption.IsChecked = settings.Theme == "Dark";
        _adjustingThresholds = true;
        WarningThresholdSlider.Value = settings.WarningUsedPercent;
        DangerThresholdSlider.Value = settings.DangerUsedPercent;
        _adjustingThresholds = false;
        UpdateThresholdDisplay();
        TopmostToggle.IsChecked = settings.AlwaysOnTop;
        AutoShowWithCodexToggle.IsChecked = settings.AutoShowWithCodex;
        HideWhenCodexClosesToggle.IsChecked = settings.HideWhenCodexCloses;
        HideWhenCodexClosesToggle.IsEnabled = settings.AutoShowWithCodex;
        SingleClickCyclesQuotaToggle.IsChecked = settings.SingleClickCyclesQuota;
        AnimationsToggle.IsChecked = settings.AnimationsEnabled;
        _animationsEnabled = settings.AnimationsEnabled;
        _initializing = false;
    }

    public async Task ShowFromAsync(Rect ballRect, Rect workArea, bool animate)
    {
        _dismissRequested = false;
        _workArea = workArea;
        var widthBeforeClamp = Width;
        var heightBeforeClamp = Height;
        ApplyWorkAreaLimits();
        if (Math.Abs(Width - widthBeforeClamp) > .1d || Math.Abs(Height - heightBeforeClamp) > .1d)
        {
            PanelSizeChanged?.Invoke(Width, Height);
        }
        var centerX = ballRect.Left + ballRect.Width / 2d;
        var centerY = ballRect.Top + ballRect.Height / 2d;
        Left = centerX >= workArea.Left + workArea.Width / 2d
            ? workArea.Right - Width - ScreenInset
            : workArea.Left + ScreenInset;
        Top = centerY >= workArea.Top + workArea.Height / 2d
            ? workArea.Bottom - Height - ScreenInset
            : workArea.Top + ScreenInset;
        ClampPositionToWorkArea();
        StopAnimations();
        Opacity = animate ? 0d : 1d;
        WindowTranslation.Y = animate ? 6d : 0d;
        Show();
        Activate();
        if (animate) await AnimateAsync(1d, 0d, 190, new CubicEase { EasingMode = EasingMode.EaseOut });
    }

    public async Task HidePanelAsync(bool animate)
    {
        if (!IsVisible) return;
        if (animate) await AnimateAsync(0d, 4d, 120, new CubicEase { EasingMode = EasingMode.EaseIn });
        HidePanel();
    }

    public void HidePanel()
    {
        HideResetAllConfirmation();
        StopAnimations();
        if (IsVisible) Hide();
        Opacity = 1d;
        WindowTranslation.Y = 0d;
    }

    public void ClosePermanently() { _allowClose = true; Close(); }

    private Task AnimateAsync(double opacity, double y, int ms, IEasingFunction easing)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var duration = TimeSpan.FromMilliseconds(ms);
        Opacity = opacity;
        WindowTranslation.Y = y;
        var fade = new DoubleAnimation { To = opacity, Duration = duration, EasingFunction = easing, FillBehavior = FillBehavior.Stop };
        fade.Completed += (_, _) => completion.TrySetResult();
        BeginAnimation(OpacityProperty, fade);
        WindowTranslation.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation { To = y, Duration = duration, EasingFunction = easing, FillBehavior = FillBehavior.Stop });
        return completion.Task;
    }

    private void StopAnimations()
    {
        BeginAnimation(OpacityProperty, null);
        WindowTranslation.BeginAnimation(TranslateTransform.YProperty, null);
    }

    private void RequestDismiss()
    {
        if (!IsVisible || _dismissRequested || _isSizingOrMoving) return;
        HideResetAllConfirmation();
        _dismissRequested = true;
        DismissRequested?.Invoke(this, EventArgs.Empty);
    }

    private void SizeSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing) return;
        var value = Math.Round(e.NewValue / 2d) * 2d;
        SizeValueText.Text = $"{value:0} px";
        BallSizeChanged?.Invoke(value);
    }
    private void OpacitySlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing) return;
        var value = Math.Clamp(Math.Round(e.NewValue / 5d) * 5d, 30d, 100d);
        OpacityValueText.Text = $"{value:0}%";
        BallOpacityChanged?.Invoke(value / 100d);
    }
    private void ThemeOption_OnChecked(object sender, RoutedEventArgs e)
    {
        if (_initializing || sender is not FrameworkElement { Tag: string theme }) return;
        ThemeChanged?.Invoke(theme is "Light" or "Dark" ? theme : "System");
    }
    private void WarningThresholdSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing || _adjustingThresholds) return;
        _adjustingThresholds = true;
        var warning = SnapThreshold(e.NewValue, 10d, 90d);
        var danger = SnapThreshold(DangerThresholdSlider.Value, 20d, 100d);
        WarningThresholdSlider.Value = warning;
        if (danger < warning + 5d)
        {
            danger = Math.Min(100d, warning + 5d);
            DangerThresholdSlider.Value = danger;
        }
        _adjustingThresholds = false;
        UpdateThresholdDisplay();
        if (!_initializing) UsageThresholdsChanged?.Invoke(warning, danger);
    }
    private void DangerThresholdSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing || _adjustingThresholds) return;
        _adjustingThresholds = true;
        var danger = SnapThreshold(e.NewValue, 20d, 100d);
        var warning = SnapThreshold(WarningThresholdSlider.Value, 10d, 90d);
        DangerThresholdSlider.Value = danger;
        if (warning > danger - 5d)
        {
            warning = Math.Max(10d, danger - 5d);
            WarningThresholdSlider.Value = warning;
        }
        _adjustingThresholds = false;
        UpdateThresholdDisplay();
        if (!_initializing) UsageThresholdsChanged?.Invoke(warning, danger);
    }
    private void ResetAllButton_OnClick(object sender, RoutedEventArgs e) => ShowResetAllConfirmation();
    private void CancelResetAllButton_OnClick(object sender, RoutedEventArgs e) => HideResetAllConfirmation();
    private void ConfirmResetAllButton_OnClick(object sender, RoutedEventArgs e)
    {
        HideResetAllConfirmation();
        AllSettingsResetRequested?.Invoke(this, EventArgs.Empty);
    }
    private void ResetAllOverlay_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, ResetAllOverlay)) HideResetAllConfirmation();
    }
    private void ShowResetAllConfirmation()
    {
        ResetAllOverlay.BeginAnimation(OpacityProperty, null);
        ResetAllCardScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ResetAllCardScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        ResetAllOverlay.Visibility = Visibility.Visible;
        ResetAllOverlay.Opacity = 1d;
        ResetAllCardScale.ScaleX = ResetAllCardScale.ScaleY = 1d;
        if (_animationsEnabled)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            ResetAllOverlay.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0d, 1d, TimeSpan.FromMilliseconds(130))
                {
                    EasingFunction = ease,
                    FillBehavior = FillBehavior.Stop
                });
            var scale = new DoubleAnimation(.96d, 1d, TimeSpan.FromMilliseconds(170))
            {
                EasingFunction = ease,
                FillBehavior = FillBehavior.Stop
            };
            ResetAllCardScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
            ResetAllCardScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale.Clone());
        }
        ConfirmResetAllButton.Focus();
    }
    private void HideResetAllConfirmation()
    {
        ResetAllOverlay.BeginAnimation(OpacityProperty, null);
        ResetAllCardScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ResetAllCardScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        ResetAllOverlay.Opacity = 0d;
        ResetAllOverlay.Visibility = Visibility.Collapsed;
        ResetAllCardScale.ScaleX = ResetAllCardScale.ScaleY = 1d;
    }
    private void UpdateThresholdDisplay()
    {
        var warning = SnapThreshold(WarningThresholdSlider.Value, 10d, 90d);
        var danger = SnapThreshold(DangerThresholdSlider.Value, 20d, 100d);
        WarningThresholdValueText.Text = $"已用 {warning:0}%";
        DangerThresholdValueText.Text = $"已用 {danger:0}%";
        ColorRangeText.Text = $"低用量绿色 · {warning:0}% 达到黄色 · {danger:0}% 达到红色";
    }
    private static double SnapThreshold(double value, double minimum, double maximum)
        => Math.Clamp(Math.Round(value / 5d) * 5d, minimum, maximum);
    private void DecreaseSizeButton_OnClick(object sender, RoutedEventArgs e) => SizeSlider.Value = Math.Max(SizeSlider.Minimum, SizeSlider.Value - 2d);
    private void IncreaseSizeButton_OnClick(object sender, RoutedEventArgs e) => SizeSlider.Value = Math.Min(SizeSlider.Maximum, SizeSlider.Value + 2d);
    private void TopmostToggle_OnChanged(object sender, RoutedEventArgs e) { if (!_initializing) TopmostChanged?.Invoke(TopmostToggle.IsChecked == true); }
    private void AutoShowWithCodexToggle_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        var enabled = AutoShowWithCodexToggle.IsChecked == true;
        HideWhenCodexClosesToggle.IsEnabled = enabled;
        AutoShowWithCodexChanged?.Invoke(enabled);
    }
    private void HideWhenCodexClosesToggle_OnChanged(object sender, RoutedEventArgs e) { if (!_initializing) HideWhenCodexClosesChanged?.Invoke(HideWhenCodexClosesToggle.IsChecked == true); }
    private void SingleClickCyclesQuotaToggle_OnChanged(object sender, RoutedEventArgs e) { if (!_initializing) SingleClickCyclesQuotaChanged?.Invoke(SingleClickCyclesQuotaToggle.IsChecked == true); }
    private void AnimationsToggle_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _animationsEnabled = AnimationsToggle.IsChecked == true;
        AnimationsChanged?.Invoke(_animationsEnabled);
    }
    private void ExitButton_OnClick(object sender, RoutedEventArgs e) => ExitRequested?.Invoke(this, EventArgs.Empty);
    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => RequestDismiss();
    private void Header_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left
            || e.OriginalSource is not DependencyObject source
            || FindVisualParent<System.Windows.Controls.Button>(source) is not null) return;
        DragMove();
    }
    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        if (ResetAllOverlay.Visibility == Visibility.Visible) HideResetAllConfirmation();
        else RequestDismiss();
    }
    private IntPtr SettingsMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmNcHitTest && GetWindowRect(hwnd, out var windowRect))
        {
            var packedPoint = lParam.ToInt64();
            var screenX = unchecked((short)(packedPoint & 0xFFFF));
            var screenY = unchecked((short)((packedPoint >> 16) & 0xFFFF));
            var dpi = VisualTreeHelper.GetDpi(this);
            var resizeHit = GetResizeHitTest(
                screenX - windowRect.Left,
                screenY - windowRect.Top,
                windowRect.Right - windowRect.Left,
                windowRect.Bottom - windowRect.Top,
                ResizeBorderThickness * dpi.DpiScaleX,
                ResizeBorderThickness * dpi.DpiScaleY);
            if (resizeHit != HtClient)
            {
                handled = true;
                return new IntPtr(resizeHit);
            }
        }
        else if (message == WmEnterSizeMove)
        {
            _isSizingOrMoving = true;
        }
        else if (message == WmExitSizeMove)
        {
            _isSizingOrMoving = false;
            ApplyWorkAreaLimits();
            ClampPositionToWorkArea();
            PanelSizeChanged?.Invoke(Width, Height);
        }
        if ((message is 0x0100 or 0x0104) && wParam.ToInt32() == 0x1B)
        {
            handled = true;
            if (ResetAllOverlay.Visibility == Visibility.Visible) HideResetAllConfirmation();
            else RequestDismiss();
        }
        return IntPtr.Zero;
    }

    private static T? FindVisualParent<T>(DependencyObject source) where T : DependencyObject
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match) return match;
        }
        return null;
    }

    private void ApplyWorkAreaLimits()
    {
        if (_workArea.IsEmpty) return;
        MaxWidth = Math.Max(MinWidth, _workArea.Width - ScreenInset * 2d);
        MaxHeight = Math.Max(MinHeight, _workArea.Height - ScreenInset * 2d);
        Width = ClampFinite(Width, MinWidth, MaxWidth, 372d);
        Height = ClampFinite(Height, MinHeight, MaxHeight, 560d);
    }

    private void ClampPositionToWorkArea()
    {
        if (_workArea.IsEmpty) return;
        var minimumLeft = _workArea.Left + ScreenInset;
        var minimumTop = _workArea.Top + ScreenInset;
        var maximumLeft = Math.Max(minimumLeft, _workArea.Right - Width - ScreenInset);
        var maximumTop = Math.Max(minimumTop, _workArea.Bottom - Height - ScreenInset);
        Left = Math.Clamp(Left, minimumLeft, maximumLeft);
        Top = Math.Clamp(Top, minimumTop, maximumTop);
    }

    private static int GetResizeHitTest(
        double x,
        double y,
        double width,
        double height,
        double horizontalBorder,
        double verticalBorder)
    {
        var left = x >= 0d && x <= horizontalBorder;
        var right = x >= width - horizontalBorder && x <= width;
        var top = y >= 0d && y <= verticalBorder;
        var bottom = y >= height - verticalBorder && y <= height;
        if (top && left) return HtTopLeft;
        if (top && right) return HtTopRight;
        if (bottom && left) return HtBottomLeft;
        if (bottom && right) return HtBottomRight;
        if (left) return HtLeft;
        if (right) return HtRight;
        if (top) return HtTop;
        if (bottom) return HtBottom;
        return HtClient;
    }

    private static double ClampFinite(double value, double minimum, double maximum, double fallback)
        => double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
    private void OnClosing(object? sender, CancelEventArgs e) { if (!_allowClose) { e.Cancel = true; RequestDismiss(); } }
}
