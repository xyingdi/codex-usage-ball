using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CodexUsageBall.Services;
using CodexUsageBall.ViewModels;
using DrawingPoint = System.Drawing.Point;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace CodexUsageBall;

public partial class MainWindow : Window
{
    public const string WindowTitle = "Codex Usage Ball";
    private const double MinimumBallSize = 48d;
    private const double MaximumBallSize = 96d;
    private const double ScreenInset = 12d;
    private const double SnapDistance = 30d;
    private static readonly TimeSpan CodexCloseHideDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RegularRefreshAge = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan InteractionRefreshAge = TimeSpan.FromSeconds(30);

    private enum UiState { Hidden, Ball, Settings }

    private readonly CodexAppServerClient _client = new();
    private readonly SettingsService _settingsService = new();
    private readonly bool _startupLaunch;
    private readonly MainViewModel _viewModel;
    private readonly TrayService _trayService;
    private readonly HoverCardWindow _hoverCard;
    private readonly SettingsPanelWindow _settingsPanel;
    private readonly DispatcherTimer _lifecycleTimer;
    private readonly DispatcherTimer _dragTimer;
    private readonly DispatcherTimer _singleClickTimer;
    private readonly DispatcherTimer _hoverShowTimer;
    private readonly DispatcherTimer _hoverHideTimer;
    private AppSettings _settings;
    private UiState _state = UiState.Ball;
    private Rect _ballRect;
    private bool _settingsTransitioning;
    private bool _settingsDismissPending;
    private bool _pointerActive;
    private bool _pointerMoved;
    private bool _pointerWasSecondClick;
    private bool _refreshAnimationRunning;
    private bool _isExiting;
    private bool _hiddenByCodex;
    private bool _codexWasRunning;
    private bool _manualVisibilityOverrideUntilCodexRuns;
    private bool _codexVisibilityTransitioning;
    private string _actualTheme = "Dark";
    private double _pointerStartLeft;
    private double _pointerStartTop;
    private DrawingPoint _pointerStartScreen;
    private DateTimeOffset? _codexClosedAt;

    public MainWindow(bool startupLaunch = false)
    {
        _startupLaunch = startupLaunch;
        _manualVisibilityOverrideUntilCodexRuns = false;
        _settings = _settingsService.Load();
        if (_settings.AutoShowWithCodex && !SettingsService.SetStartWithWindows(true))
        {
            _settings.AutoShowWithCodex = false;
            _settingsService.Save(_settings);
        }

        _actualTheme = ThemeService.Apply(_settings.Theme);
        InitializeComponent();
        Width = Height = _settings.BallSize;
        BallHitArea.Width = BallHitArea.Height = _settings.BallSize;
        BallVisual.Opacity = _settings.BallOpacity;

        _viewModel = new MainViewModel(_client);
        _viewModel.ConfigureQuotaSelection(_settings.SelectedQuotaIdentity);
        _viewModel.QuotaSelectionChanged += identity => Dispatcher.Invoke(() => SaveQuotaSelection(identity));
        _viewModel.ConfigureUsageColors(
            _settings.WarningUsedPercent,
            _settings.DangerUsedPercent,
            _actualTheme == "Light",
            _settings.AnimationsEnabled);
        DataContext = _viewModel;
        Topmost = _settings.AlwaysOnTop;
        _hoverCard = new HoverCardWindow
        {
            DataContext = _viewModel,
            Topmost = _settings.AlwaysOnTop,
            AnimationsEnabled = _settings.AnimationsEnabled
        };
        _settingsPanel = new SettingsPanelWindow { Topmost = _settings.AlwaysOnTop };
        _settingsPanel.LoadSettings(_settings);
        WirePanelEvents();
        _trayService = new TrayService();
        WireTrayEvents();

        _lifecycleTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _lifecycleTimer.Tick += LifecycleTimer_OnTick;

        _dragTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _dragTimer.Tick += DragTimer_OnTick;

        _singleClickTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(WinForms.SystemInformation.DoubleClickTime + 30)
        };
        _singleClickTimer.Tick += (_, _) =>
        {
            _singleClickTimer.Stop();
            CycleQuotaFromBall();
        };

        _hoverShowTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(300)
        };
        _hoverShowTimer.Tick += (_, _) =>
        {
            _hoverShowTimer.Stop();
            if (BallHitArea.IsMouseOver && _state == UiState.Ball && !_pointerActive)
            {
                ShowHoverCard();
            }
        };

        _hoverHideTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(80)
        };
        _hoverHideTimer.Tick += (_, _) =>
        {
            _hoverHideTimer.Stop();
            _hoverCard.HideAnimated();
        };

        _viewModel.PropertyChanged += ViewModel_OnPropertyChanged;
        Loaded += MainWindow_OnLoaded;
        Closing += MainWindow_OnClosing;
        SystemEvents.UserPreferenceChanged += SystemEvents_OnUserPreferenceChanged;
        System.Windows.Application.Current.SessionEnding += Application_OnSessionEnding;
    }

    private async void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        SystemIntegration.ApplyNativeWindowHints(this);
        App.AttachSingleInstanceMessageHook(this, ShowBall);
        RestorePosition();
        _ballRect = GetBallRect();
        ApplyCircularWindowShape();
        _codexWasRunning = SystemIntegration.IsCodexDesktopRunning();
        _lifecycleTimer.Start();

        if (_settings.AutoShowWithCodex && _startupLaunch && !_codexWasRunning)
        {
            _hiddenByCodex = true;
            _state = UiState.Hidden;
            Hide();
            return;
        }

        await _viewModel.RefreshAsync();
    }

    private void WirePanelEvents()
    {
        _settingsPanel.BallSizeChanged += value => Dispatcher.Invoke(() => SetBallSize(value));
        _settingsPanel.BallOpacityChanged += value => Dispatcher.Invoke(() => SetBallOpacity(value));
        _settingsPanel.ThemeChanged += value => Dispatcher.Invoke(() => SetTheme(value));
        _settingsPanel.UsageThresholdsChanged += (warning, danger) =>
            Dispatcher.Invoke(() => SetUsageThresholds(warning, danger));
        _settingsPanel.PanelSizeChanged += (width, height) =>
            Dispatcher.Invoke(() => SetSettingsPanelSize(width, height));
        _settingsPanel.TopmostChanged += value => Dispatcher.Invoke(() => SetTopmost(value));
        _settingsPanel.AutoShowWithCodexChanged += value => Dispatcher.Invoke(() => SetAutoShowWithCodex(value));
        _settingsPanel.HideWhenCodexClosesChanged += value => Dispatcher.Invoke(() => SetHideWhenCodexCloses(value));
        _settingsPanel.SingleClickCyclesQuotaChanged += value => Dispatcher.Invoke(() => SetSingleClickCyclesQuota(value));
        _settingsPanel.AnimationsChanged += value => Dispatcher.Invoke(() => SetAnimationsEnabled(value));
        _settingsPanel.AllSettingsResetRequested += (_, _) => Dispatcher.Invoke(ResetAllSettings);
        _settingsPanel.ExitRequested += (_, _) => Dispatcher.Invoke(RequestExit);
        _settingsPanel.DismissRequested += (_, _) => Dispatcher.Invoke(() =>
        {
            if (_settingsTransitioning) _settingsDismissPending = true;
            else CloseSettings();
        });
    }

    private void WireTrayEvents()
    {
        _trayService.ShowRequested += (_, _) => Dispatcher.Invoke(ShowBall);
        _trayService.SettingsRequested += (_, _) => Dispatcher.Invoke(ShowSettings);
        _trayService.ExitRequested += (_, _) => Dispatcher.Invoke(RequestExit);
    }

    private void ViewModel_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.RemainingText)) return;
        _trayService.UpdateStatus(_viewModel.RemainingText);
        if (!_refreshAnimationRunning) PulseBallData();
    }

    private async void LifecycleTimer_OnTick(object? sender, EventArgs e)
    {
        if (_settings.AutoShowWithCodex && !_codexVisibilityTransitioning)
        {
            var running = SystemIntegration.IsCodexDesktopRunning();
            if (running)
            {
                _codexClosedAt = null;
                _manualVisibilityOverrideUntilCodexRuns = false;
                if (!_codexWasRunning && _hiddenByCodex) await ShowForCodexAsync();
                _codexWasRunning = true;
            }
            else
            {
                if (_codexWasRunning) _codexClosedAt = DateTimeOffset.Now;
                _codexWasRunning = false;
                if (_settings.HideWhenCodexCloses
                    && !_manualVisibilityOverrideUntilCodexRuns
                    && !_hiddenByCodex
                    && _codexClosedAt.HasValue
                    && DateTimeOffset.Now - _codexClosedAt.Value >= CodexCloseHideDelay
                    && _state == UiState.Ball
                    && !_pointerActive)
                {
                    await HideForCodexAsync();
                }
            }
        }

        if (_state == UiState.Ball && IsVisible && _viewModel.IsStale(RegularRefreshAge))
        {
            await _viewModel.RefreshAsync(_viewModel.HasError);
        }
    }

    private async Task ShowForCodexAsync()
    {
        _codexVisibilityTransitioning = true;
        try
        {
            _hiddenByCodex = false;
            _state = UiState.Ball;
            RestoreBallGeometry();
            Show();
            ApplyCircularWindowShape();
            await _viewModel.RefreshAsync(_viewModel.HasError);
        }
        finally
        {
            _codexVisibilityTransitioning = false;
        }
    }

    private async Task HideForCodexAsync()
    {
        _codexVisibilityTransitioning = true;
        try
        {
            StopRefreshAnimation();
            _hoverCard.HideImmediately();
            _hiddenByCodex = true;
            _state = UiState.Hidden;
            Hide();
            await _client.DisconnectAsync();
        }
        finally
        {
            _codexVisibilityTransitioning = false;
        }
    }

    private void BallHitArea_OnMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _hoverHideTimer.Stop();
        if (_state != UiState.Ball) return;
        AnimateBallOpacity(1d, 110);
        AnimateBallScale(1.035d, 145);
        if (_viewModel.IsStale(InteractionRefreshAge)) _ = _viewModel.RefreshAsync(_viewModel.HasError);
        _hoverShowTimer.Stop();
        _hoverShowTimer.Start();
    }

    private void BallHitArea_OnMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _hoverShowTimer.Stop();
        _hoverHideTimer.Stop();
        _hoverHideTimer.Start();
        if (_state == UiState.Ball)
        {
            AnimateBallOpacity(_settings.BallOpacity, 140);
            AnimateBallScale(1d, 170);
        }
    }

    private void ShowHoverCard()
    {
        _viewModel.UpdateRelativeTimes();
        _hoverCard.ShowAnimated(GetBallRect(), GetWorkingArea());
    }

    private void BallHitArea_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || _state != UiState.Ball) return;
        _hoverShowTimer.Stop();
        _hoverCard.HideImmediately();
        if (e.ClickCount > 1) _singleClickTimer.Stop();
        _pointerActive = true;
        _pointerMoved = false;
        _pointerWasSecondClick = e.ClickCount > 1;
        _pointerStartScreen = WinForms.Cursor.Position;
        _pointerStartLeft = Left;
        _pointerStartTop = Top;
        AnimateBallScale(.975d, 55);
        BallHitArea.CaptureMouse();
        _dragTimer.Start();
        e.Handled = true;
    }

    private void BallHitArea_OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_pointerActive) return;
        UpdatePointerPosition();
        e.Handled = true;
    }

    private void DragTimer_OnTick(object? sender, EventArgs e)
    {
        if (!_pointerActive)
        {
            _dragTimer.Stop();
            return;
        }

        if ((WinForms.Control.MouseButtons & WinForms.MouseButtons.Left) == 0) FinishPointerInteraction();
        else UpdatePointerPosition();
    }

    private void UpdatePointerPosition()
    {
        var cursor = WinForms.Cursor.Position;
        var dpi = VisualTreeHelper.GetDpi(this);
        var deltaX = (cursor.X - _pointerStartScreen.X) / dpi.DpiScaleX;
        var deltaY = (cursor.Y - _pointerStartScreen.Y) / dpi.DpiScaleY;
        if (!_pointerMoved && Math.Sqrt(deltaX * deltaX + deltaY * deltaY) > 3d)
        {
            _pointerMoved = true;
            _hoverCard.HideImmediately();
        }

        if (!_pointerMoved) return;
        Left = _pointerStartLeft + deltaX;
        Top = _pointerStartTop + deltaY;
        SystemIntegration.CommitWindowPosition(this, Left, Top);
    }

    private void BallHitArea_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || !_pointerActive) return;
        FinishPointerInteraction();
        e.Handled = true;
    }

    private void FinishPointerInteraction()
    {
        if (!_pointerActive) return;
        var moved = _pointerMoved;
        var secondClick = _pointerWasSecondClick;
        _pointerActive = _pointerMoved = _pointerWasSecondClick = false;
        _dragTimer.Stop();
        if (Mouse.Captured == BallHitArea) Mouse.Capture(null);
        AnimateBallScale(BallHitArea.IsMouseOver ? 1.02d : 1d, 90);

        if (moved)
        {
            _ballRect = GetBallRect();
            SnapToNearestEdge();
            _ballRect = GetBallRect();
            SystemIntegration.CommitWindowPosition(this, Left, Top);
            SavePosition();
            return;
        }

        if (secondClick)
        {
            _singleClickTimer.Stop();
            SystemIntegration.OpenCodex();
            return;
        }

        if (_settings.SingleClickCyclesQuota && _viewModel.Quotas.Count > 1)
        {
            _singleClickTimer.Stop();
            _singleClickTimer.Start();
        }
    }

    private void CycleQuotaFromBall()
    {
        if (_state != UiState.Ball || !IsVisible || _pointerActive || !_settings.SingleClickCyclesQuota) return;
        if (!_viewModel.CycleDisplayedQuota()) return;
        PulseBallData();
        if (BallHitArea.IsMouseOver) ShowHoverCard();
    }

    private async Task RefreshFromMenuAsync()
    {
        if (_refreshAnimationRunning || _state != UiState.Ball || !IsVisible) return;
        _refreshAnimationRunning = true;
        _hoverShowTimer.Stop();
        _hoverCard.HideImmediately();
        StartRefreshAnimation();

        try
        {
            var minimumFeedback = Task.Delay(_settings.AnimationsEnabled ? 520 : 0);
            var refresh = _viewModel.RefreshAsync(_viewModel.HasError);
            await Task.WhenAll(refresh, minimumFeedback);
        }
        finally
        {
            StopRefreshAnimation(_settings.AnimationsEnabled);
            _refreshAnimationRunning = false;
            PulseBallData();
            if (_state == UiState.Ball && BallHitArea.IsMouseOver && !_pointerActive)
            {
                ShowHoverCard();
            }
        }
    }

    private void StartRefreshAnimation()
    {
        StopRefreshAnimation();
        if (!_settings.AnimationsEnabled) return;

        RingRotation.BeginAnimation(
            RotateTransform.AngleProperty,
            new DoubleAnimation(0d, 360d, TimeSpan.FromMilliseconds(680))
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                RepeatBehavior = RepeatBehavior.Forever
            });

        RemainingTextLabel.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(RemainingTextLabel.Opacity, 0d, TimeSpan.FromMilliseconds(110))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        RefreshGlyph.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0d, 1d, TimeSpan.FromMilliseconds(130))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        RefreshGlyphRotation.BeginAnimation(
            RotateTransform.AngleProperty,
            new DoubleAnimation(0d, 360d, TimeSpan.FromMilliseconds(620))
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                RepeatBehavior = RepeatBehavior.Forever
            });

        RefreshHalo.Opacity = 0d;
        RefreshHaloScale.ScaleX = RefreshHaloScale.ScaleY = .78d;
        RefreshHalo.BeginAnimation(
            OpacityProperty,
            new DoubleAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromMilliseconds(520),
                KeyFrames =
                {
                    new EasingDoubleKeyFrame(0d, KeyTime.FromPercent(0d)),
                    new EasingDoubleKeyFrame(.72d, KeyTime.FromPercent(.25d)),
                    new EasingDoubleKeyFrame(0d, KeyTime.FromPercent(1d), new CubicEase { EasingMode = EasingMode.EaseOut })
                }
            });
        var haloScale = new DoubleAnimation(.78d, 1.08d, TimeSpan.FromMilliseconds(520))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        RefreshHaloScale.BeginAnimation(ScaleTransform.ScaleXProperty, haloScale);
        RefreshHaloScale.BeginAnimation(ScaleTransform.ScaleYProperty, haloScale.Clone());
    }

    private void StopRefreshAnimation(bool revealResult = false)
    {
        var textOpacity = RemainingTextLabel.Opacity;
        var glyphOpacity = RefreshGlyph.Opacity;
        RingRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        RingRotation.Angle = 0d;
        RemainingTextLabel.BeginAnimation(OpacityProperty, null);
        RemainingTextLabel.Opacity = 1d;
        RefreshGlyph.BeginAnimation(OpacityProperty, null);
        RefreshGlyph.Opacity = 0d;
        RefreshGlyphRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        RefreshGlyphRotation.Angle = 0d;
        RefreshHalo.BeginAnimation(OpacityProperty, null);
        RefreshHaloScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        RefreshHaloScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        RefreshHalo.Opacity = 0d;
        RefreshHaloScale.ScaleX = RefreshHaloScale.ScaleY = .78d;

        if (revealResult && _state == UiState.Ball && IsVisible)
        {
            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            RemainingTextLabel.BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(textOpacity, 1d, TimeSpan.FromMilliseconds(170))
                {
                    EasingFunction = easing,
                    FillBehavior = FillBehavior.Stop
                });
            RefreshGlyph.BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(glyphOpacity, 0d, TimeSpan.FromMilliseconds(130))
                {
                    EasingFunction = easing,
                    FillBehavior = FillBehavior.Stop
                });
        }
    }

    private void AnimateBallOpacity(double target, int milliseconds)
    {
        BallVisual.BeginAnimation(OpacityProperty, null);
        if (!_settings.AnimationsEnabled)
        {
            BallVisual.Opacity = target;
            return;
        }

        var current = BallVisual.Opacity;
        BallVisual.Opacity = target;
        BallVisual.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(milliseconds))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            });
    }

    private void AnimateBallScale(double target, int milliseconds)
    {
        BallScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        BallScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        if (!_settings.AnimationsEnabled)
        {
            BallScale.ScaleX = BallScale.ScaleY = 1d;
            return;
        }

        var current = BallScale.ScaleX;
        BallScale.ScaleX = BallScale.ScaleY = target;
        var animation = new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        BallScale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        BallScale.BeginAnimation(ScaleTransform.ScaleYProperty, animation.Clone());
    }

    private void PulseBallData()
    {
        if (!_settings.AnimationsEnabled || _state != UiState.Ball || !IsVisible) return;
        var pulse = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(330),
            FillBehavior = FillBehavior.Stop,
            KeyFrames =
            {
                new EasingDoubleKeyFrame(.68d, KeyTime.FromPercent(0d)),
                new EasingDoubleKeyFrame(1d, KeyTime.FromPercent(.55d), new CubicEase { EasingMode = EasingMode.EaseOut }),
                new EasingDoubleKeyFrame(.94d, KeyTime.FromPercent(1d))
            }
        };
        BallUsageRing.BeginAnimation(OpacityProperty, pulse, HandoffBehavior.SnapshotAndReplace);
    }

    private async void ShowSettings()
    {
        MarkManualVisibilityOverride();
        if (_settingsTransitioning || _settingsPanel.IsVisible) return;
        _singleClickTimer.Stop();
        _settingsTransitioning = true;
        _settingsDismissPending = false;
        StopRefreshAnimation();

        if (_state == UiState.Hidden)
        {
            RestoreBallGeometry();
            Show();
            _state = UiState.Ball;
        }

        _hoverCard.HideImmediately();
        _ballRect = GetBallRect();
        await HideBallForSettingsAsync();
        _state = UiState.Settings;
        _settingsPanel.LoadSettings(_settings);
        await _settingsPanel.ShowFromAsync(_ballRect, GetWorkingArea(), _settings.AnimationsEnabled);
        _settingsTransitioning = false;
        if (_settingsDismissPending)
        {
            _settingsDismissPending = false;
            CloseSettings();
        }
    }

    private void CloseSettings() => _ = CloseSettingsAsync();

    private async Task CloseSettingsAsync()
    {
        if (_settingsTransitioning)
        {
            _settingsDismissPending = true;
            return;
        }

        if (!_settingsPanel.IsVisible) return;
        _settingsTransitioning = true;
        _settingsDismissPending = false;
        await _settingsPanel.HidePanelAsync(_settings.AnimationsEnabled);
        RestoreBallGeometry();
        BallVisual.Opacity = 0d;
        BallScale.ScaleX = BallScale.ScaleY = .82d;
        Show();
        ApplyCircularWindowShape();
        _state = UiState.Ball;
        await RevealBallFromSettingsAsync();
        Activate();
        _settingsTransitioning = false;
    }

    private async Task HideBallForSettingsAsync()
    {
        if (!_settings.AnimationsEnabled)
        {
            Hide();
            return;
        }

        await AnimateBallTransitionAsync(0d, .82d, 135, new CubicEase { EasingMode = EasingMode.EaseIn });
        Hide();
    }

    private async Task RevealBallFromSettingsAsync()
    {
        if (!_settings.AnimationsEnabled)
        {
            BallVisual.Opacity = _settings.BallOpacity;
            BallScale.ScaleX = BallScale.ScaleY = 1d;
            return;
        }

        await AnimateBallTransitionAsync(
            _settings.BallOpacity,
            1d,
            205,
            new CubicEase { EasingMode = EasingMode.EaseOut });
    }

    private Task AnimateBallTransitionAsync(double opacity, double scale, int milliseconds, IEasingFunction easing)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var duration = TimeSpan.FromMilliseconds(milliseconds);
        var opacityFrom = BallVisual.Opacity;
        var scaleFrom = BallScale.ScaleX;
        BallVisual.Opacity = opacity;
        BallScale.ScaleX = BallScale.ScaleY = scale;
        var fade = new DoubleAnimation(opacityFrom, opacity, duration)
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop
        };
        fade.Completed += (_, _) => completion.TrySetResult();
        BallVisual.BeginAnimation(OpacityProperty, fade);
        var scaleAnimation = new DoubleAnimation(scaleFrom, scale, duration)
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop
        };
        BallScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnimation);
        BallScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnimation.Clone());
        return completion.Task;
    }

    private async void ShowBall()
    {
        MarkManualVisibilityOverride();
        if (_settingsPanel.IsVisible)
        {
            await CloseSettingsAsync();
            return;
        }

        if (!IsVisible)
        {
            RestoreBallGeometry();
            Show();
            _state = UiState.Ball;
            ApplyCircularWindowShape();
        }

        _hoverCard.HideImmediately();
        ClampBallToWorkingArea();
        Activate();
        if (_viewModel.IsStale(InteractionRefreshAge)) await _viewModel.RefreshAsync(_viewModel.HasError);
    }

    private void MarkManualVisibilityOverride()
    {
        _manualVisibilityOverrideUntilCodexRuns = true;
        _hiddenByCodex = false;
        _codexClosedAt = null;
    }

    private void RestoreBallGeometry()
    {
        ClampBallToWorkingArea();
        Left = _ballRect.Left;
        Top = _ballRect.Top;
        Width = Height = _settings.BallSize;
        MinWidth = MinHeight = MinimumBallSize;
        BallHitArea.Width = BallHitArea.Height = _settings.BallSize;
        BallVisual.Opacity = _settings.BallOpacity;
        BallScale.ScaleX = BallScale.ScaleY = 1d;
    }

    private void ApplyCircularWindowShape()
    {
        UpdateLayout();
        SystemIntegration.CommitWindowPosition(this, Left, Top);
        SystemIntegration.CommitWindowSize(this, _settings.BallSize, _settings.BallSize);
        SystemIntegration.ApplyCircularWindowRegion(this);
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            if (_state == UiState.Ball && IsVisible)
            {
                SystemIntegration.CommitWindowSize(this, _settings.BallSize, _settings.BallSize);
                SystemIntegration.ApplyCircularWindowRegion(this);
            }
        }));
    }

    private void SetBallSize(double value)
    {
        var size = Math.Clamp(Math.Round(value / 2d) * 2d, MinimumBallSize, MaximumBallSize);
        if (Math.Abs(size - _settings.BallSize) < .1d) return;
        var workArea = GetWorkingArea();
        var old = _ballRect.Width > 0d ? _ballRect : GetBallRect();
        var centerX = old.Left + old.Width / 2d;
        var centerY = old.Top + old.Height / 2d;
        var right = Math.Abs(workArea.Right - old.Right) <= SnapDistance + ScreenInset;
        var bottom = Math.Abs(workArea.Bottom - old.Bottom) <= SnapDistance + ScreenInset;
        _settings.BallSize = size;
        BallHitArea.Width = BallHitArea.Height = size;
        _ballRect = new Rect(
            right ? workArea.Right - size - ScreenInset : centerX - size / 2d,
            bottom ? workArea.Bottom - size - ScreenInset : centerY - size / 2d,
            size,
            size);
        ClampBallToWorkingArea();
        if (_state == UiState.Ball)
        {
            RestoreBallGeometry();
            ApplyCircularWindowShape();
        }
        SavePosition();
    }

    private void SetBallOpacity(double value)
    {
        _settings.BallOpacity = Math.Clamp(value, .3d, 1d);
        if (_state == UiState.Ball)
        {
            AnimateBallOpacity(BallHitArea.IsMouseOver ? 1d : _settings.BallOpacity, 110);
        }
        SaveAndSyncSettings();
    }

    private void SetTheme(string theme)
    {
        _settings.Theme = theme is "Light" or "Dark" ? theme : "System";
        _actualTheme = ThemeService.Apply(_settings.Theme);
        _viewModel.ConfigureUsageColors(
            _settings.WarningUsedPercent,
            _settings.DangerUsedPercent,
            _actualTheme == "Light",
            _settings.AnimationsEnabled);
        SaveAndSyncSettings();
    }

    private void SetUsageThresholds(double warningUsedPercent, double dangerUsedPercent)
    {
        _settings.WarningUsedPercent = Math.Clamp(warningUsedPercent, 10d, 90d);
        _settings.DangerUsedPercent = Math.Clamp(
            Math.Max(dangerUsedPercent, _settings.WarningUsedPercent + 5d),
            20d,
            100d);
        _viewModel.ConfigureUsageColors(
            _settings.WarningUsedPercent,
            _settings.DangerUsedPercent,
            _actualTheme == "Light",
            _settings.AnimationsEnabled);
        SaveAndSyncSettings();
    }

    private void SetSettingsPanelSize(double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height)) return;
        _settings.SettingsPanelWidth = Math.Clamp(width, 340d, 1200d);
        _settings.SettingsPanelHeight = Math.Clamp(height, 430d, 1600d);
        _settingsService.Save(_settings);
    }

    private void SetSingleClickCyclesQuota(bool enabled)
    {
        _settings.SingleClickCyclesQuota = enabled;
        if (!enabled) _singleClickTimer.Stop();
        SaveAndSyncSettings();
    }

    private void SaveQuotaSelection(string? identity)
    {
        _settings.SelectedQuotaIdentity = identity ?? string.Empty;
        _settingsService.Save(_settings);
    }

    private void SetTopmost(bool enabled)
    {
        _settings.AlwaysOnTop = enabled;
        Topmost = _hoverCard.Topmost = _settingsPanel.Topmost = enabled;
        SaveAndSyncSettings();
    }

    private void SetAutoShowWithCodex(bool enabled)
    {
        if (enabled && !SettingsService.SetStartWithWindows(true)) enabled = false;
        if (!enabled) SettingsService.SetStartWithWindows(false);
        _settings.AutoShowWithCodex = enabled;
        _codexClosedAt = null;
        _codexWasRunning = SystemIntegration.IsCodexDesktopRunning();
        if (!enabled && _hiddenByCodex) ShowBall();
        SaveAndSyncSettings();
    }

    private void SetHideWhenCodexCloses(bool enabled)
    {
        _settings.HideWhenCodexCloses = enabled;
        if (!enabled && _hiddenByCodex) ShowBall();
        SaveAndSyncSettings();
    }

    private void SetAnimationsEnabled(bool enabled)
    {
        _settings.AnimationsEnabled = enabled;
        _hoverCard.AnimationsEnabled = enabled;
        _viewModel.ConfigureUsageColors(
            _settings.WarningUsedPercent,
            _settings.DangerUsedPercent,
            _actualTheme == "Light",
            enabled);
        if (!enabled)
        {
            BallScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            BallScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            BallVisual.BeginAnimation(OpacityProperty, null);
            BallScale.ScaleX = BallScale.ScaleY = 1d;
            BallVisual.Opacity = BallHitArea.IsMouseOver ? 1d : _settings.BallOpacity;
            StopRefreshAnimation();
        }
        SaveAndSyncSettings();
    }

    private void ResetAllSettings()
    {
        _singleClickTimer.Stop();
        _hoverCard.HideImmediately();
        _settings.ResetAll();
        if (_settings.AutoShowWithCodex && !SettingsService.SetStartWithWindows(true))
        {
            _settings.AutoShowWithCodex = false;
        }

        _actualTheme = ThemeService.Apply(_settings.Theme);
        _viewModel.ConfigureQuotaSelection(null);
        _viewModel.ConfigureUsageColors(
            _settings.WarningUsedPercent,
            _settings.DangerUsedPercent,
            _actualTheme == "Light",
            _settings.AnimationsEnabled);
        Topmost = _hoverCard.Topmost = _settingsPanel.Topmost = _settings.AlwaysOnTop;
        _hoverCard.AnimationsEnabled = _settings.AnimationsEnabled;
        _codexClosedAt = null;
        _codexWasRunning = SystemIntegration.IsCodexDesktopRunning();
        _hiddenByCodex = false;

        var workArea = GetWorkingArea();
        _ballRect = new Rect(
            workArea.Right - _settings.BallSize - 22d,
            workArea.Bottom - _settings.BallSize - 30d,
            _settings.BallSize,
            _settings.BallSize);
        Width = Height = _settings.BallSize;
        BallHitArea.Width = BallHitArea.Height = _settings.BallSize;
        BallVisual.Opacity = _settings.BallOpacity;
        BallScale.ScaleX = BallScale.ScaleY = 1d;
        _settingsPanel.LoadSettings(_settings, forcePanelSize: true);
        _settingsService.Save(_settings);
    }

    private void SaveAndSyncSettings()
    {
        _settingsService.Save(_settings);
        _settingsPanel.LoadSettings(_settings);
    }

    private void SnapToNearestEdge()
    {
        var area = GetWorkingArea();
        Left = Math.Clamp(Left, area.Left + ScreenInset, area.Right - Width - ScreenInset);
        Top = Math.Clamp(Top, area.Top + ScreenInset, area.Bottom - Height - ScreenInset);
        var leftDistance = Math.Abs(Left - area.Left);
        var rightDistance = Math.Abs(area.Right - (Left + Width));
        if (leftDistance <= SnapDistance || rightDistance <= SnapDistance)
        {
            Left = leftDistance <= rightDistance
                ? area.Left + ScreenInset
                : area.Right - Width - ScreenInset;
        }
        var topDistance = Math.Abs(Top - area.Top);
        var bottomDistance = Math.Abs(area.Bottom - (Top + Height));
        if (topDistance <= SnapDistance) Top = area.Top + ScreenInset;
        else if (bottomDistance <= SnapDistance) Top = area.Bottom - Height - ScreenInset;
    }

    private void ClampBallToWorkingArea()
    {
        var area = GetWorkingArea();
        var size = _settings.BallSize;
        if (_ballRect.Width <= 0d) _ballRect = new Rect(Left, Top, size, size);
        _ballRect = new Rect(
            Math.Clamp(_ballRect.Left, area.Left + ScreenInset, area.Right - size - ScreenInset),
            Math.Clamp(_ballRect.Top, area.Top + ScreenInset, area.Bottom - size - ScreenInset),
            size,
            size);
        if (_state == UiState.Ball)
        {
            Left = _ballRect.Left;
            Top = _ballRect.Top;
        }
    }

    private Rect GetBallRect() => new(Left, Top, _settings.BallSize, _settings.BallSize);

    private Rect GetWorkingArea()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var center = new DrawingPoint(
            (int)((Left + Math.Max(Width, 1d) / 2d) * dpi.DpiScaleX),
            (int)((Top + Math.Max(Height, 1d) / 2d) * dpi.DpiScaleY));
        var area = WinForms.Screen.FromPoint(center).WorkingArea;
        return new Rect(
            area.Left / dpi.DpiScaleX,
            area.Top / dpi.DpiScaleY,
            area.Width / dpi.DpiScaleX,
            area.Height / dpi.DpiScaleY);
    }

    private void RestorePosition()
    {
        var area = GetWorkingArea();
        var size = _settings.BallSize;
        if (_settings.HasSavedPosition
            && IsFinite(_settings.Left)
            && IsFinite(_settings.Top)
            && _settings.Left < area.Right
            && _settings.Top < area.Bottom
            && _settings.Left + size > area.Left
            && _settings.Top + size > area.Top)
        {
            Left = Math.Clamp(_settings.Left, area.Left + ScreenInset, area.Right - size - ScreenInset);
            Top = Math.Clamp(_settings.Top, area.Top + ScreenInset, area.Bottom - size - ScreenInset);
        }
        else
        {
            Left = area.Right - size - 22d;
            Top = area.Bottom - size - 30d;
        }
    }

    private void SavePosition()
    {
        if (_state == UiState.Ball) _ballRect = GetBallRect();
        var position = _ballRect.Width > 0d ? _ballRect : GetBallRect();
        _settings.HasSavedPosition = true;
        _settings.Left = position.Left;
        _settings.Top = position.Top;
        _settingsService.Save(_settings);
    }

    private async void RequestExit()
    {
        if (_isExiting) return;
        _isExiting = true;
        _lifecycleTimer.Stop();
        _dragTimer.Stop();
        _hoverShowTimer.Stop();
        _hoverHideTimer.Stop();
        _singleClickTimer.Stop();
        StopRefreshAnimation();
        SavePosition();
        SystemEvents.UserPreferenceChanged -= SystemEvents_OnUserPreferenceChanged;
        _trayService.Dispose();
        _hoverCard.ClosePermanently();
        _settingsPanel.ClosePermanently();
        await _client.DisposeAsync();
        Close();
        System.Windows.Application.Current.Shutdown();
    }

    private void MainWindow_OnClosing(object? sender, CancelEventArgs e)
    {
        if (_isExiting) return;
        e.Cancel = true;
        _settingsPanel.HidePanel();
    }

    private void SystemEvents_OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (_settings.Theme != "System") return;
        Dispatcher.Invoke(() =>
        {
            _actualTheme = ThemeService.Apply("System");
            _viewModel.ConfigureUsageColors(
                _settings.WarningUsedPercent,
                _settings.DangerUsedPercent,
                _actualTheme == "Light",
                _settings.AnimationsEnabled);
        });
    }

    private void Application_OnSessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        _isExiting = true;
        SystemEvents.UserPreferenceChanged -= SystemEvents_OnUserPreferenceChanged;
        _trayService.Dispose();
    }

    private void ContextMenu_OnOpened(object sender, RoutedEventArgs e)
    {
        _hoverShowTimer.Stop();
        _hoverCard.HideImmediately();
        RebuildQuotaDisplayMenu();
    }

    private void ContextMenu_OnClosed(object sender, RoutedEventArgs e)
    {
        if (_state != UiState.Ball || !IsVisible || !BallHitArea.IsMouseOver) return;
        _hoverShowTimer.Stop();
        _hoverShowTimer.Start();
    }

    private void RebuildQuotaDisplayMenu()
    {
        QuotaDisplayMenuItem.Items.Clear();
        var displayed = _viewModel.DisplayedQuota;
        QuotaDisplayMenuValue.Text = _viewModel.IsSmartQuotaSelection
            ? "智能"
            : displayed is null ? "智能" : ShortQuotaLabel(displayed.Label);

        AddQuotaDisplayChoice("智能推荐", null, _viewModel.IsSmartQuotaSelection);
        foreach (var quota in _viewModel.Quotas)
        {
            AddQuotaDisplayChoice(
                quota.Label,
                quota.Identity,
                string.Equals(_viewModel.SelectedQuotaIdentity, quota.Identity, StringComparison.Ordinal));
        }
        QuotaDisplayMenuItem.IsEnabled = _viewModel.Quotas.Count > 0;
    }

    private void AddQuotaDisplayChoice(string label, string? identity, bool selected)
    {
        var header = new Grid { Width = 142d };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22d) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1d, GridUnitType.Star) });
        var check = new TextBlock
        {
            Text = selected ? "✓" : string.Empty,
            FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI"),
            FontSize = 11d,
            VerticalAlignment = VerticalAlignment.Center
        };
        check.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        var text = new TextBlock
        {
            Text = label,
            FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI"),
            FontSize = 12d,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(text, 1);
        header.Children.Add(check);
        header.Children.Add(text);

        var item = new MenuItem
        {
            Header = header,
            Style = (Style)FindResource("CodexMenuItemStyle"),
            Tag = identity
        };
        item.Click += QuotaChoiceMenuItem_OnClick;
        QuotaDisplayMenuItem.Items.Add(item);
    }

    private void QuotaChoiceMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item) return;
        if (_viewModel.SelectQuota(item.Tag as string)) PulseBallData();
    }

    private static string ShortQuotaLabel(string label)
        => label.EndsWith("额度", StringComparison.Ordinal) ? label[..^2].TrimEnd() : label;

    private async void RefreshMenuItem_OnClick(object sender, RoutedEventArgs e)
        => await RefreshFromMenuAsync();

    private void SettingsMenuItem_OnClick(object sender, RoutedEventArgs e) => ShowSettings();
    private void ExitMenuItem_OnClick(object sender, RoutedEventArgs e) => RequestExit();

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
