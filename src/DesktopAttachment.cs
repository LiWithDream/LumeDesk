using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace LumeDesk
{
    public static class DesktopAttachment
    {
        private const int GWL_EXSTYLE = -20;
        private const int GWLP_HWNDPARENT = -8;
        private const long WS_EX_TOOLWINDOW = 0x00000080L;
        private const long WS_EX_APPWINDOW = 0x00040000L;

        private const int WM_WINDOWPOSCHANGING = 0x0046;
        private const int WM_SYSCOMMAND = 0x0112;
        private const int WM_DISPLAYCHANGE = 0x007E;
        private const int WM_DPICHANGED = 0x02E0;
        private const int WM_COMMAND = 0x0111;

        private const int SC_MINIMIZE = 0xF020;
        private const int SC_MAXIMIZE = 0xF030;

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;

        private static readonly IntPtr HWND_BOTTOM = new IntPtr(1);
        private static readonly IntPtr HWND_TOP = new IntPtr(0);
        public static bool AllowTemporaryFront = false;

        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPOS
        {
            public IntPtr hwnd;
            public IntPtr hwndInsertAfter;
            public int x;
            public int y;
            public int cx;
            public int cy;
            public uint flags;
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowW(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowExW(IntPtr parentHandle, IntPtr childAfter, string className, string windowTitle);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

        private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
        {
            if (IntPtr.Size == 8) return GetWindowLongPtr64(hWnd, nIndex);
            return new IntPtr(GetWindowLong32(hWnd, nIndex));
        }

        private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        {
            if (IntPtr.Size == 8) return SetWindowLongPtr64(hWnd, nIndex, dwNewLong);
            return new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));
        }

        public static IntPtr FindDesktopHostWindow()
        {
            IntPtr progman = FindWindowW("Progman", null);
            if (progman != IntPtr.Zero)
            {
                IntPtr defView = FindWindowExW(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (defView != IntPtr.Zero)
                {
                    return progman;
                }
            }

            IntPtr foundWorker = IntPtr.Zero;
            EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
            {
                StringBuilder sb = new StringBuilder(64);
                GetClassNameW(hWnd, sb, sb.Capacity);
                if (sb.ToString() == "WorkerW")
                {
                    IntPtr defView = FindWindowExW(hWnd, IntPtr.Zero, "SHELLDLL_DefView", null);
                    if (defView != IntPtr.Zero)
                    {
                        foundWorker = hWnd;
                        return false;
                    }
                }
                return true;
            }, IntPtr.Zero);

            if (foundWorker != IntPtr.Zero) return foundWorker;
            return progman;
        }

        public static IntPtr FindShellDefView()
        {
            IntPtr host = FindDesktopHostWindow();
            if (host == IntPtr.Zero) return IntPtr.Zero;
            return FindWindowExW(host, IntPtr.Zero, "SHELLDLL_DefView", null);
        }

        public static void AttachWindow(Window window, MonitorInfoItem monitor, Action onDisplayChanged)
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            // Hide from Taskbar & Alt+Tab
            long exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            exStyle |= WS_EX_TOOLWINDOW;
            exStyle &= ~WS_EX_APPWINDOW;
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(exStyle));

            // Disable Windows 11 DWM rounded corners, 1px border, and system transitions so our
            // hardware Direct3D SwapChain desktop window blends seamlessly edge-to-edge!
            try
            {
                int noRound = 1; // DWMWCP_DONOTROUND
                DwmSetWindowAttribute(hwnd, 33, ref noRound, sizeof(int));
                int noBorder = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
                DwmSetWindowAttribute(hwnd, 34, ref noBorder, sizeof(int));
                int disableTransitions = 1; // DWMWA_TRANSITIONS_FORCEDISABLED
                DwmSetWindowAttribute(hwnd, 3, ref disableTransitions, sizeof(int));
            }
            catch
            {
            }

            // Set desktop host as Owner (survives Win+D while remaining interactive & transparent)
            IntPtr desktopHost = FindDesktopHostWindow();
            if (desktopHost != IntPtr.Zero)
            {
                SetWindowLongPtr(hwnd, GWLP_HWNDPARENT, desktopHost);
            }

            PositionOnMonitor(hwnd, monitor);

            HwndSource source = HwndSource.FromHwnd(hwnd);
            if (source != null)
            {
                source.AddHook(delegate(IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
                {
                    if (msg == WM_WINDOWPOSCHANGING)
                    {
                        if (!AllowTemporaryFront)
                        {
                            WINDOWPOS wp = (WINDOWPOS)Marshal.PtrToStructure(lParam, typeof(WINDOWPOS));
                            if ((wp.flags & SWP_NOZORDER) == 0)
                            {
                                wp.hwndInsertAfter = HWND_BOTTOM;
                                Marshal.StructureToPtr(wp, lParam, true);
                            }
                        }
                    }
                    else if (msg == WM_SYSCOMMAND)
                    {
                        int cmd = wParam.ToInt32() & 0xFFF0;
                        if (cmd == SC_MINIMIZE || cmd == SC_MAXIMIZE)
                        {
                            handled = true;
                        }
                    }
                    else if (msg == WM_DISPLAYCHANGE || msg == WM_DPICHANGED)
                    {
                        if (onDisplayChanged != null)
                        {
                            window.Dispatcher.BeginInvoke(onDisplayChanged);
                        }
                    }
                    return IntPtr.Zero;
                });
            }
        }

        public static void PositionOnMonitor(IntPtr hwnd, MonitorInfoItem monitor)
        {
            if (hwnd == IntPtr.Zero || monitor == null) return;
            SetWindowPos(
                hwnd,
                HWND_BOTTOM,
                monitor.WorkLeft,
                monitor.WorkTop,
                monitor.WorkWidth,
                monitor.WorkHeight,
                SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        public static void SendToBottom(IntPtr hwnd)
        {
            AllowTemporaryFront = false;
            if (hwnd == IntPtr.Zero) return;
            SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        public static void BringToFrontForOverlay(IntPtr hwnd)
        {
            AllowTemporaryFront = true;
            if (hwnd == IntPtr.Zero) return;
            SetWindowPos(hwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
        }

        public static bool AreNativeDesktopIconsHidden()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", false))
                {
                    if (key != null)
                    {
                        object val = key.GetValue("HideIcons");
                        if (val != null)
                        {
                            return Convert.ToInt32(val) == 1;
                        }
                    }
                }
            }
            catch
            {
            }
            return false;
        }

        public static bool ToggleNativeDesktopIcons()
        {
            try
            {
                IntPtr defView = FindShellDefView();
                if (defView != IntPtr.Zero)
                {
                    // Standard Explorer Shell toggle command for desktop icons (0x7402)
                    SendMessageW(defView, WM_COMMAND, new IntPtr(0x7402), IntPtr.Zero);
                    return true;
                }
            }
            catch
            {
            }
            return false;
        }

        private static string StartupShortcutPath
        {
            get
            {
                string startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                return Path.Combine(startup, "LumeDesk.lnk");
            }
        }

        private static string LegacyStartupShortcutPath
        {
            get
            {
                string startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                return Path.Combine(startup, "VertiHome.lnk");
            }
        }

        public static bool IsAutoStartEnabled()
        {
            return File.Exists(StartupShortcutPath);
        }

        public static void SetAutoStart(bool enable)
        {
            try
            {
                string lnkPath = StartupShortcutPath;
                string legacyPath = LegacyStartupShortcutPath;
                if (File.Exists(legacyPath))
                {
                    try { File.Delete(legacyPath); } catch { }
                }
                if (!enable)
                {
                    if (File.Exists(lnkPath)) File.Delete(lnkPath);
                    return;
                }

                string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return;
                object shell = null;
                object sc = null;
                try
                {
                    shell = Activator.CreateInstance(shellType);
                    sc = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
                    Type scType = sc.GetType();
                    scType.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { exePath });
                    scType.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { Path.GetDirectoryName(exePath) });
                    scType.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { "LumeDesk Adaptive Frosted-Glass Desktop Launcher" });
                    scType.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, sc, null);
                }
                finally
                {
                    if (sc != null) { try { Marshal.FinalReleaseComObject(sc); } catch { } }
                    if (shell != null) { try { Marshal.FinalReleaseComObject(shell); } catch { } }
                }
            }
            catch
            {
            }
        }
    }
}
