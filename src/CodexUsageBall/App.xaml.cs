using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace CodexUsageBall;

public partial class App : System.Windows.Application
{
    private const string MutexName = "Local\\CodexUsageBall.8D37E3A0";
    private const int ShowExistingMessage = 0x8000 + 419;
    private Mutex? _singleInstanceMutex;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(true, MutexName, out var isFirstInstance);
        _ownsMutex = isFirstInstance;
        if (!isFirstInstance)
        {
            var existing = FindExistingWindow();
            if (existing != IntPtr.Zero)
            {
                NativeMethods.PostMessage(existing, ShowExistingMessage, IntPtr.Zero, IntPtr.Zero);
            }

            Shutdown();
            return;
        }

        base.OnStartup(e);
        var startupLaunch = e.Args.Any(argument =>
            string.Equals(argument, "--startup", StringComparison.OrdinalIgnoreCase));
        var window = new CodexUsageBall.MainWindow(startupLaunch);
        MainWindow = window;
        window.Show();
    }

    private static IntPtr FindExistingWindow()
    {
        var result = IntPtr.Zero;
        NativeMethods.EnumWindows((windowHandle, _) =>
        {
            var titleLength = NativeMethods.GetWindowTextLength(windowHandle);
            if (titleLength != CodexUsageBall.MainWindow.WindowTitle.Length)
            {
                return true;
            }

            var title = new StringBuilder(titleLength + 1);
            _ = NativeMethods.GetWindowText(windowHandle, title, title.Capacity);
            if (!string.Equals(title.ToString(), CodexUsageBall.MainWindow.WindowTitle, StringComparison.Ordinal))
            {
                return true;
            }

            result = windowHandle;
            return false;
        }, IntPtr.Zero);

        return result;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex)
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    internal static void AttachSingleInstanceMessageHook(Window window, Action showAction)
    {
        var source = (HwndSource)PresentationSource.FromVisual(window)!;
        source.AddHook((IntPtr _, int message, IntPtr _, IntPtr _, ref bool handled) =>
        {
            if (message == ShowExistingMessage)
            {
                showAction();
                handled = true;
            }

            return IntPtr.Zero;
        });
    }

    private static class NativeMethods
    {
        internal delegate bool EnumWindowsProc(IntPtr windowHandle, IntPtr state);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr state);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetWindowText(IntPtr windowHandle, StringBuilder title, int maxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetWindowTextLength(IntPtr windowHandle);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    }
}
