using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CodexUsageBall.Services;
using WpfProgressBar = System.Windows.Controls.ProgressBar;

namespace CodexUsageBall;

public partial class HoverCardWindow : Window
{
    private readonly CubicEase _ease = new() { EasingMode = EasingMode.EaseOut };
    private bool _shownBelow;
    private int _animationVersion;

    public HoverCardWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => SystemIntegration.MakeWindowNonActivatingClickThrough(this);
    }

    public bool AnimationsEnabled { get; set; } = true;

    public void ShowAnimated(Rect ballRect, Rect workArea)
    {
        var version = ++_animationVersion;
        StopAnimations();
        Opacity = 0d;
        if (!IsVisible)
        {
            Show();
        }

        UpdateLayout();
        _shownBelow = ballRect.Top - ActualHeight - 7d < workArea.Top + 6d;
        TopArrow.Visibility = _shownBelow ? Visibility.Visible : Visibility.Collapsed;
        BottomArrow.Visibility = _shownBelow ? Visibility.Collapsed : Visibility.Visible;
        Left = Math.Clamp(
            ballRect.Left + ballRect.Width / 2d - ActualWidth / 2d,
            workArea.Left + 6d,
            workArea.Right - ActualWidth - 6d);
        Top = _shownBelow
            ? ballRect.Bottom + 7d
            : ballRect.Top - ActualHeight - 7d;

        if (!AnimationsEnabled)
        {
            Opacity = 1d;
            HoverTranslation.Y = 0d;
            return;
        }

        Opacity = 1d;
        HoverTranslation.Y = 0d;
        BeginAnimation(OpacityProperty, Animation(0d, 1d, 135));
        HoverTranslation.BeginAnimation(
            System.Windows.Media.TranslateTransform.YProperty,
            Animation(_shownBelow ? -6d : 6d, 0d, 150));
        AnimateQuotaBars();

        _ = version;
    }

    public void HideAnimated()
    {
        if (!IsVisible)
        {
            return;
        }

        if (!AnimationsEnabled)
        {
            HideImmediately();
            return;
        }

        var version = ++_animationVersion;
        StopAnimations();
        var opacity = Animation(1d, 0d, 90);
        opacity.Completed += (_, _) =>
        {
            if (version != _animationVersion)
            {
                return;
            }

            Hide();
            StopAnimations();
            Opacity = 1d;
            HoverTranslation.Y = 0d;
        };
        BeginAnimation(OpacityProperty, opacity);
        HoverTranslation.BeginAnimation(
            System.Windows.Media.TranslateTransform.YProperty,
            Animation(0d, _shownBelow ? -3d : 3d, 90));
    }

    public void HideImmediately()
    {
        _animationVersion++;
        StopAnimations();
        if (IsVisible)
        {
            Hide();
        }

        Opacity = 1d;
        HoverTranslation.Y = 0d;
    }

    public void ClosePermanently() => Close();

    private void StopAnimations()
    {
        BeginAnimation(OpacityProperty, null);
        HoverTranslation.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
        foreach (var progressBar in FindVisualChildren<WpfProgressBar>(HoverVisual))
        {
            progressBar.BeginAnimation(WpfProgressBar.ValueProperty, null);
        }
    }

    private void AnimateQuotaBars()
    {
        foreach (var progressBar in FindVisualChildren<WpfProgressBar>(HoverVisual))
        {
            var target = progressBar.Value;
            progressBar.BeginAnimation(
                WpfProgressBar.ValueProperty,
                new DoubleAnimation(0d, target, TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = _ease,
                    FillBehavior = FillBehavior.Stop
                });
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
    }

    private DoubleAnimation Animation(double from, double to, int milliseconds)
        => new(from, to, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = _ease,
            FillBehavior = FillBehavior.Stop
        };
}
