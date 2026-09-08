using System;
using System.Runtime.InteropServices;

namespace TaskbarTelemetry
{
    internal static class NativeMethods
    {
        internal const int GWL_STYLE = -16;
        internal const int GWL_EXSTYLE = -20;

        internal const long WS_CHILD = 0x40000000L;
        internal const long WS_VISIBLE = 0x10000000L;
        internal const long WS_POPUP = unchecked((long)0x80000000L);
        internal const long WS_CAPTION = 0x00C00000L;
        internal const long WS_THICKFRAME = 0x00040000L;
        internal const long WS_CLIPSIBLINGS = 0x04000000L;
        internal const long WS_CLIPCHILDREN = 0x02000000L;

        internal const long WS_EX_APPWINDOW = 0x00040000L;
        internal const long WS_EX_LAYERED = 0x00080000L;
        internal const long WS_EX_TOPMOST = 0x00000008L;
        internal const long WS_EX_TOOLWINDOW = 0x00000080L;
        internal const long WS_EX_NOACTIVATE = 0x08000000L;

        internal static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        internal static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

        internal const uint SWP_NOSIZE = 0x0001;
        internal const uint SWP_NOMOVE = 0x0002;
        internal const uint SWP_NOZORDER = 0x0004;
        internal const uint SWP_NOACTIVATE = 0x0010;
        internal const uint SWP_FRAMECHANGED = 0x0020;
        internal const uint SWP_SHOWWINDOW = 0x0040;

        internal const int SW_HIDE = 0;
        internal const int SW_SHOWNOACTIVATE = 4;
        internal const int WM_MOUSEACTIVATE = 0x0021;
        internal const int MA_NOACTIVATE = 3;

        private static readonly IntPtr DpiAwarenessContextPerMonitorAwareV2 = new IntPtr(-4);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr window);

        internal static double GetDpiScale(IntPtr window)
        {
            try { uint dpi = GetDpiForWindow(window); return dpi > 0 ? dpi / 96.0 : 1.0; }
            catch (EntryPointNotFoundException) { return 1.0; }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT
        {
            internal int Left;
            internal int Top;
            internal int Right;
            internal int Bottom;

            internal int Width
            {
                get { return Math.Max(0, Right - Left); }
            }

            internal int Height
            {
                get { return Math.Max(0, Bottom - Top); }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT
        {
            internal int X;
            internal int Y;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr FindWindow(string className, string windowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string windowName);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindow(IntPtr window);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr GetParent(IntPtr window);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr SetParent(IntPtr child, IntPtr newParent);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(IntPtr window, out RECT rectangle);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ScreenToClient(IntPtr window, ref POINT point);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowPos(
            IntPtr window,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        internal static extern IntPtr WindowFromPoint(POINT point);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ShowWindow(IntPtr window, int command);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern int RegisterWindowMessage(string messageName);

        [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
        private static extern int GetWindowLong32(IntPtr window, int index);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
        private static extern int SetWindowLong32(IntPtr window, int index, int newValue);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr64(IntPtr window, int index, IntPtr newValue);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

        internal static IntPtr GetWindowLongPointer(IntPtr window, int index)
        {
            if (IntPtr.Size == 8)
                return GetWindowLongPtr64(window, index);
            return new IntPtr(GetWindowLong32(window, index));
        }

        internal static void SetWindowLongPointer(IntPtr window, int index, IntPtr value)
        {
            if (IntPtr.Size == 8)
                SetWindowLongPtr64(window, index, value);
            else
                SetWindowLong32(window, index, value.ToInt32());
        }

        internal static bool TryAttachToTaskbar(IntPtr window, IntPtr taskbar)
        {
            if (window == IntPtr.Zero || taskbar == IntPtr.Zero ||
                !IsWindow(window) || !IsWindow(taskbar))
                return false;

            ApplyTaskbarChildStyles(window);
            SetParent(window, taskbar);
            bool attached = GetParent(window) == taskbar;
            if (!attached)
                ApplyTaskbarFallbackStyles(window);
            return attached;
        }

        internal static bool HasTaskbarChildStyle(IntPtr window)
        {
            if (window == IntPtr.Zero || !IsWindow(window))
                return false;

            long style = GetWindowLongPointer(window, GWL_STYLE).ToInt64();
            return (style & WS_CHILD) != 0 && (style & WS_POPUP) == 0;
        }

        private static void ApplyTaskbarChildStyles(IntPtr window)
        {
            long style = GetWindowLongPointer(window, GWL_STYLE).ToInt64();
            style &= ~(WS_POPUP | WS_CAPTION | WS_THICKFRAME);
            style |= WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN;
            SetWindowLongPointer(window, GWL_STYLE, new IntPtr(style));

            long extendedStyle = GetWindowLongPointer(window, GWL_EXSTYLE).ToInt64();
            extendedStyle &= ~(WS_EX_APPWINDOW | WS_EX_TOPMOST);
            // TrafficMonitor's Win11 GDI path uses WS_EX_LAYERED. A plain
            // Win32 child can be hit-testable yet still render underneath the
            // taskbar's XAML/DirectComposition surface. TaskbarForm's
            // TransparencyKey initializes the layered attributes while this
            // style keeps the window in the visible composition path.
            extendedStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED;
            SetWindowLongPointer(window, GWL_EXSTYLE, new IntPtr(extendedStyle));

            SetWindowPos(window, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE |
                SWP_FRAMECHANGED);
        }

        internal static void ApplyTaskbarFallbackStyles(IntPtr window)
        {
            if (window == IntPtr.Zero || !IsWindow(window))
                return;

            SetParent(window, IntPtr.Zero);

            long style = GetWindowLongPointer(window, GWL_STYLE).ToInt64();
            style &= ~(WS_CHILD | WS_CAPTION | WS_THICKFRAME);
            style |= WS_POPUP | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN;
            SetWindowLongPointer(window, GWL_STYLE, new IntPtr(style));

            long extendedStyle = GetWindowLongPointer(window, GWL_EXSTYLE).ToInt64();
            extendedStyle &= ~(WS_EX_APPWINDOW | WS_EX_TOPMOST);
            extendedStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            SetWindowLongPointer(window, GWL_EXSTYLE, new IntPtr(extendedStyle));

            SetWindowPos(window, HWND_NOTOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE |
                SWP_FRAMECHANGED);
        }

        internal static void TryEnablePerMonitorDpiAwareness()
        {
            try
            {
                SetProcessDpiAwarenessContext(DpiAwarenessContextPerMonitorAwareV2);
            }
            catch (EntryPointNotFoundException)
            {
            }
            catch (DllNotFoundException)
            {
            }
        }
    }
}
