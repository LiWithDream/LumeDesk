using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace LumeDesk
{
    public static class WindowActivator
    {
        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;
        private const uint GW_OWNER = 4;

        private const long WS_CAPTION = 0x00C00000L;
        private const long WS_THICKFRAME = 0x00040000L;
        private const long WS_MINIMIZEBOX = 0x00020000L;
        private const long WS_EX_TOOLWINDOW = 0x00000080L;
        private const long WS_EX_NOACTIVATE = 0x08000000L;

        private const int SW_SHOW = 5;
        private const int SW_RESTORE = 9;

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_SHOWWINDOW = 0x0040;

        private static readonly IntPtr HWND_TOP = new IntPtr(0);
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

        private static readonly HashSet<string> MultiWindowExeExclusions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "chrome", "msedge", "firefox", "brave", "arc", "vivaldi", "opera",
            "explorer", "cmd", "powershell", "pwsh", "wt", "windowsterminal", "notepad"
        };

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        private static long GetWindowLongPtr(IntPtr hWnd, int nIndex)
        {
            if (IntPtr.Size == 8) return GetWindowLongPtr64(hWnd, nIndex).ToInt64();
            return GetWindowLong32(hWnd, nIndex);
        }

        public static bool TryActivateExistingShortcut(string filePath, string displayName)
        {
            if (string.IsNullOrEmpty(filePath)) return false;

            string targetExe = null;
            string args = "";
            string ext = (Path.GetExtension(filePath) ?? "").ToLowerInvariant();

            if (ext == ".lnk")
            {
                if (!ResolveShortcut(filePath, out targetExe, out args)) return false;
            }
            else if (ext == ".exe")
            {
                targetExe = filePath;
            }
            else
            {
                return false;
            }

            // Never intercept shortcuts with custom arguments (e.g. Chrome/Edge PWA --app-id=...)!
            if (!string.IsNullOrWhiteSpace(args)) return false;
            if (string.IsNullOrEmpty(targetExe) || !targetExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;

            string exeName = Path.GetFileNameWithoutExtension(targetExe);
            if (string.IsNullOrEmpty(exeName) || MultiWindowExeExclusions.Contains(exeName)) return false;

            return TryActivateProcessWindow(targetExe, exeName, displayName);
        }

        public static bool TryActivateProcessByName(string exeName, string displayName)
        {
            if (string.IsNullOrEmpty(exeName)) return false;
            string stem = exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileNameWithoutExtension(exeName)
                : exeName;
            if (string.IsNullOrEmpty(stem)) return false;
            return TryActivateProcessWindow("", stem, displayName ?? "");
        }

        private static bool ResolveShortcut(string lnkPath, out string targetPath, out string arguments)
        {
            targetPath = "";
            arguments = "";
            object shell = null;
            object sc = null;
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return false;
                shell = Activator.CreateInstance(shellType);
                sc = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
                Type scType = sc.GetType();
                targetPath = scType.InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, sc, null) as string ?? "";
                arguments = scType.InvokeMember("Arguments", System.Reflection.BindingFlags.GetProperty, null, sc, null) as string ?? "";
                return !string.IsNullOrEmpty(targetPath);
            }
            catch
            {
                return false;
            }
            finally
            {
                if (sc != null) { try { Marshal.FinalReleaseComObject(sc); } catch { } }
                if (shell != null) { try { Marshal.FinalReleaseComObject(shell); } catch { } }
            }
        }

        private static bool TryActivateProcessWindow(string targetExePath, string exeName, string displayName)
        {
            HashSet<uint> pids = new HashSet<uint>();
            string normalizedTarget = "";
            if (!string.IsNullOrEmpty(targetExePath))
            {
                try { normalizedTarget = Path.GetFullPath(targetExePath); } catch { normalizedTarget = targetExePath; }
            }

            Process[] procs = null;
            try { procs = Process.GetProcessesByName(exeName); } catch { return false; }
            if (procs == null || procs.Length == 0) return false;

            for (int i = 0; i < procs.Length; i++)
            {
                Process p = procs[i];
                try
                {
                    bool matched = true;
                    try
                    {
                        string pm = p.MainModule != null ? p.MainModule.FileName : null;
                        if (!string.IsNullOrEmpty(pm) && !string.IsNullOrEmpty(normalizedTarget))
                        {
                            matched = pm.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase);
                        }
                    }
                    catch
                    {
                        // Process may be elevated; ProcessName already matched.
                        matched = true;
                    }
                    if (matched) pids.Add((uint)p.Id);
                }
                catch
                {
                }
                finally
                {
                    try { p.Dispose(); } catch { }
                }
            }

            if (pids.Count == 0) return false;

            string lowExe = exeName.ToLowerInvariant();
            IntPtr bestHwnd = IntPtr.Zero;
            int bestScore = int.MinValue;

            EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
            {
                uint pid;
                GetWindowThreadProcessId(hWnd, out pid);
                if (!pids.Contains(pid)) return true;

                // Reject owned popup/dialog/shadow windows
                if (GetWindow(hWnd, GW_OWNER) != IntPtr.Zero) return true;

                long exStyle = GetWindowLongPtr(hWnd, GWL_EXSTYLE);
                if ((exStyle & WS_EX_TOOLWINDOW) != 0) return true;
                if ((exStyle & WS_EX_NOACTIVATE) != 0) return true;

                StringBuilder clsSb = new StringBuilder(128);
                GetClassNameW(hWnd, clsSb, clsSb.Capacity);
                string cls = clsSb.ToString();
                if (IsIgnoredHelperClass(cls)) return true;

                StringBuilder txtSb = new StringBuilder(256);
                GetWindowTextW(hWnd, txtSb, txtSb.Capacity);
                string title = txtSb.ToString().Trim();

                bool visible = IsWindowVisible(hWnd);
                bool iconic = IsIconic(hWnd);

                RECT rc;
                GetWindowRect(hWnd, out rc);
                int w = rc.Right - rc.Left;
                int h = rc.Bottom - rc.Top;

                // Reject tiny 0x0 / helper / hidden utility windows
                if (!iconic && (w < 260 || h < 200)) return true;
                // When hidden to system tray (SW_HIDE), only accept genuine main windows with a title and real size
                if (!visible && !iconic)
                {
                    if (string.IsNullOrEmpty(title) || w < 320 || h < 260) return true;
                }

                long style = GetWindowLongPtr(hWnd, GWL_STYLE);
                int score = 0;

                if (visible) score += 2000;
                if (iconic) score += 450;
                if (!string.IsNullOrEmpty(title)) score += 200;
                if (w >= 500 && h >= 400) score += 350;
                if ((style & (WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX)) != 0) score += 250;

                // Strong app-specific main window matching for WeChat / Weixin / QQ / TIM
                if (lowExe == "weixin" || lowExe == "wechat")
                {
                    if (title.Equals("微信", StringComparison.OrdinalIgnoreCase) ||
                        title.Equals("Weixin", StringComparison.OrdinalIgnoreCase) ||
                        title.Equals("WeChat", StringComparison.OrdinalIgnoreCase))
                    {
                        score += 1200;
                    }
                    if (cls.Equals("WeChatMainWndForPC", StringComparison.OrdinalIgnoreCase) ||
                        (cls.StartsWith("Qt", StringComparison.OrdinalIgnoreCase) && cls.EndsWith("QWindowIcon", StringComparison.OrdinalIgnoreCase)))
                    {
                        score += 400;
                    }
                }
                else if (lowExe == "qq" || lowExe == "tim")
                {
                    if (title.Equals("QQ", StringComparison.OrdinalIgnoreCase) ||
                        title.Equals("TIM", StringComparison.OrdinalIgnoreCase) ||
                        title.StartsWith("QQ", StringComparison.OrdinalIgnoreCase))
                    {
                        score += 1200;
                    }
                    if (cls.Equals("Chrome_WidgetWin_1", StringComparison.OrdinalIgnoreCase))
                    {
                        score += 350;
                    }
                }
                else if (!string.IsNullOrEmpty(displayName) &&
                         title.IndexOf(displayName, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    score += 600;
                }
                else if (title.IndexOf(exeName, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    score += 450;
                }

                // Prefer logged-in main window over any stray login dialog
                if (title.IndexOf("登录", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    title.IndexOf("Login", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    score -= 600;
                }

                // Tie-breaker by window area (bounded)
                score += Math.Min(250, (w * h) / 10000);

                if (score > bestScore)
                {
                    bestScore = score;
                    bestHwnd = hWnd;
                }

                return true;
            }, IntPtr.Zero);

            if (bestHwnd == IntPtr.Zero) return false;

            ForceBringToFront(bestHwnd);
            return true;
        }

        private static bool IsIgnoredHelperClass(string cls)
        {
            if (string.IsNullOrEmpty(cls)) return true;
            string low = cls.ToLowerInvariant();
            if (low == "ime" || low == "msctfime ui" || low == "cicerouiwndframe" ||
                low == "gdi+ hook window class" || low == "tooltips_class32" ||
                low == "chrome_widgetwin_0")
            {
                return true;
            }
            if (low.IndexOf("tray") >= 0 || low.IndexOf("splash") >= 0 ||
                low.IndexOf("shadow") >= 0 || low.IndexOf("toast") >= 0 ||
                low.IndexOf("tooltip") >= 0 || low.IndexOf("popup") >= 0 ||
                low.IndexOf("clipboard") >= 0 || low.IndexOf("notify") >= 0)
            {
                return true;
            }
            return false;
        }

        private static void ForceBringToFront(IntPtr hwnd)
        {
            try
            {
                bool wasVisible = IsWindowVisible(hwnd);
                bool wasIconic = IsIconic(hwnd);

                if (!wasVisible)
                {
                    ShowWindow(hwnd, SW_SHOW);
                }
                if (wasIconic)
                {
                    ShowWindow(hwnd, SW_RESTORE);
                }

                IntPtr fg = GetForegroundWindow();
                uint fgPid;
                uint fgThread = (fg != IntPtr.Zero) ? GetWindowThreadProcessId(fg, out fgPid) : 0;
                uint curThread = GetCurrentThreadId();
                uint targetPid;
                uint targetThread = GetWindowThreadProcessId(hwnd, out targetPid);

                bool attachedFg = false;
                bool attachedTarget = false;
                try
                {
                    if (fgThread != 0 && fgThread != curThread)
                    {
                        attachedFg = AttachThreadInput(curThread, fgThread, true);
                    }
                    if (targetThread != 0 && targetThread != curThread)
                    {
                        attachedTarget = AttachThreadInput(curThread, targetThread, true);
                    }

                    // Brief topmost nudge guarantees foreground pop-up even if Windows blocks SetForegroundWindow
                    SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                    SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                    SetWindowPos(hwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                    BringWindowToTop(hwnd);
                    SetForegroundWindow(hwnd);
                }
                finally
                {
                    if (attachedTarget) AttachThreadInput(curThread, targetThread, false);
                    if (attachedFg) AttachThreadInput(curThread, fgThread, false);
                }
            }
            catch
            {
            }
        }
    }
}
