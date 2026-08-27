using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CodexUsageBall.Services;

public static class SystemIntegration
{
    private const int ExtendedStyleIndex = -20;
    private const long ExtendedStyleTransparent = 0x00000020L;
    private const long ExtendedStyleToolWindow = 0x00000080L;
    private const long ExtendedStyleNoActivate = 0x08000000L;
    private const int DwmWindowCornerPreference = 33;
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmBorderColor = 34;
    private const int DoNotRoundCornerPreference = 1;
    private const int DwmColorNone = unchecked((int)0xFFFFFFFE);
    private const uint SetWindowPositionNoSize = 0x0001;
    private const uint SetWindowPositionNoMove = 0x0002;
    private const uint SetWindowPositionNoZOrder = 0x0004;
    private const uint SetWindowPositionNoActivate = 0x0010;

    public static void ApplyNativeWindowHints(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            // The ball is clipped with a true elliptic window region. Asking DWM to
            // round the underlying rectangular HWND can add a faint square frame.
            var corner = DoNotRoundCornerPreference;
            var dark = 1;
            var border = DwmColorNone;
            _ = DwmSetWindowAttribute(handle, DwmWindowCornerPreference, ref corner, sizeof(int));
            _ = DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref dark, sizeof(int));
            _ = DwmSetWindowAttribute(handle, DwmBorderColor, ref border, sizeof(int));
        }
        catch
        {
            // Older Windows builds may not expose these DWM attributes.
        }
    }

    public static void ApplyCircularWindowRegion(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero || !GetWindowRect(handle, out var windowRect))
            {
                return;
            }

            var width = Math.Max(1, windowRect.Right - windowRect.Left);
            var height = Math.Max(1, windowRect.Bottom - windowRect.Top);
            var region = CreateEllipticRgn(0, 0, width + 1, height + 1);
            if (region == IntPtr.Zero)
            {
                return;
            }

            if (SetWindowRgn(handle, region, true) == 0)
            {
                DeleteObject(region);
            }
        }
        catch
        {
            // WPF's own transparency fallback remains circular visually.
        }
    }

    public static void CommitWindowSize(Window window, double logicalWidth, double logicalHeight)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(window);
            var width = Math.Max(1, (int)Math.Round(logicalWidth * dpi.DpiScaleX));
            var height = Math.Max(1, (int)Math.Round(logicalHeight * dpi.DpiScaleY));
            SetWindowPos(
                handle,
                IntPtr.Zero,
                0,
                0,
                width,
                height,
                SetWindowPositionNoMove | SetWindowPositionNoZOrder | SetWindowPositionNoActivate);
        }
        catch
        {
            // WPF's own size remains the fallback.
        }
    }

    public static void CommitWindowPosition(Window window, double logicalLeft, double logicalTop)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(window);
            var left = (int)Math.Round(logicalLeft * dpi.DpiScaleX);
            var top = (int)Math.Round(logicalTop * dpi.DpiScaleY);
            SetWindowPos(
                handle,
                IntPtr.Zero,
                left,
                top,
                0,
                0,
                SetWindowPositionNoSize | SetWindowPositionNoZOrder | SetWindowPositionNoActivate);
        }
        catch
        {
            // WPF's own position remains the fallback.
        }
    }

    public static void ApplyRoundedWindowRegion(Window window, double logicalRadius)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero || !GetWindowRect(handle, out var windowRect))
            {
                return;
            }

            var width = Math.Max(1, windowRect.Right - windowRect.Left);
            var height = Math.Max(1, windowRect.Bottom - windowRect.Top);
            var logicalWidth = Math.Max(window.ActualWidth, 1d);
            var scale = width / logicalWidth;
            var radius = Math.Max(1, (int)Math.Round(logicalRadius * scale));
            var isCircle = Math.Abs(width - height) <= 2 && radius * 2 >= Math.Min(width, height) - 2;
            var region = isCircle
                ? CreateEllipticRgn(0, 0, width + 1, height + 1)
                : CreateRoundRectRgn(0, 0, width + 1, height + 1, radius * 2, radius * 2);
            if (region == IntPtr.Zero)
            {
                return;
            }

            if (SetWindowRgn(handle, region, true) == 0)
            {
                DeleteObject(region);
            }
        }
        catch
        {
            // The WPF transparency fallback remains circular visually.
        }
    }

    public static void MakeWindowNonActivatingClickThrough(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            var styles = GetWindowLongPointer(handle, ExtendedStyleIndex).ToInt64();
            styles |= ExtendedStyleTransparent | ExtendedStyleToolWindow | ExtendedStyleNoActivate;
            SetWindowLongPointer(handle, ExtendedStyleIndex, new IntPtr(styles));
        }
        catch
        {
            // Hover content remains non-focusable even when extended styles are unavailable.
        }
    }

    public static void OpenCodex()
    {
        OpenUri("codex://");
    }

    public static bool IsCodexDesktopRunning()
    {
        try
        {
            foreach (var process in Process.GetProcessesByName("ChatGPT"))
            {
                using (process)
                {
                    try
                    {
                        var executable = process.MainModule?.FileName;
                        if (process.MainWindowHandle != IntPtr.Zero
                            && IsWindowVisible(process.MainWindowHandle)
                            && !string.IsNullOrWhiteSpace(executable)
                            && executable.Contains("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                    catch
                    {
                        // Continue checking sibling processes when package metadata
                        // is temporarily unavailable during an app update.
                    }
                }
            }
        }
        catch
        {
            // A failed probe must not disturb normal floating-ball behavior.
        }

        return false;
    }

    private static void OpenUri(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch
        {
            if (uri.StartsWith("codex", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(
                        "explorer.exe",
                        @"shell:AppsFolder\OpenAI.Codex_2p2nqsd0c76g0!App")
                    {
                        UseShellExecute = true
                    });
                }
                catch
                {
                    // The tray remains usable even when the installed app cannot be resolved.
                }
            }
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr window,
        int attribute,
        ref int attributeValue,
        int attributeSize);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateEllipticRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr window, IntPtr region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr value);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPointer64(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern IntPtr GetWindowLongPointer32(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPointer64(IntPtr window, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern IntPtr SetWindowLongPointer32(IntPtr window, int index, IntPtr value);

    private static IntPtr GetWindowLongPointer(IntPtr window, int index)
        => IntPtr.Size == 8
            ? GetWindowLongPointer64(window, index)
            : GetWindowLongPointer32(window, index);

    private static IntPtr SetWindowLongPointer(IntPtr window, int index, IntPtr value)
        => IntPtr.Size == 8
            ? SetWindowLongPointer64(window, index, value)
            : SetWindowLongPointer32(window, index, value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out Rect windowRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

}
