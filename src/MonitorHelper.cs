using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace LumeDesk
{
    public class MonitorInfoItem
    {
        public IntPtr HMonitor { get; set; }
        public string DeviceName { get; set; }
        public bool IsPrimary { get; set; }
        public int BoundsLeft { get; set; }
        public int BoundsTop { get; set; }
        public int BoundsWidth { get; set; }
        public int BoundsHeight { get; set; }
        public int WorkLeft { get; set; }
        public int WorkTop { get; set; }
        public int WorkWidth { get; set; }
        public int WorkHeight { get; set; }
        public uint DpiX { get; set; }
        public uint DpiY { get; set; }
        public int RefreshRate { get; set; }

        public bool IsPortrait { get { return BoundsHeight > BoundsWidth; } }
        public double ScaleFactor { get { return DpiX > 0 ? (DpiX / 96.0) : 1.0; } }
        public double LogicalWorkWidth { get { return WorkWidth / ScaleFactor; } }
        public double LogicalWorkHeight { get { return WorkHeight / ScaleFactor; } }

        public string FriendlyLabel
        {
            get
            {
                string orient = IsPortrait ? "竖屏" : "横屏";
                string prim = IsPrimary ? " [主屏]" : "";
                return string.Format("{0} ({1}x{2} {3}, {4}%){5}",
                    DeviceName.Replace(@"\\.\", ""),
                    BoundsWidth, BoundsHeight, orient,
                    (int)Math.Round(ScaleFactor * 100), prim);
            }
        }
    }

    public static class MonitorHelper
    {
        private const int MONITORINFOF_PRIMARY = 0x00000001;
        private const int MDT_EFFECTIVE_DPI = 0;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmDeviceName;
            public short dmSpecVersion;
            public short dmDriverVersion;
            public short dmSize;
            public short dmDriverExtra;
            public int dmFields;
            public int dmPositionX;
            public int dmPositionY;
            public int dmDisplayOrientation;
            public int dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel;
            public int dmPelsWidth;
            public int dmPelsHeight;
            public int dmDisplayFlags;
            public int dmDisplayFrequency;
        }

        private delegate bool MonitorEnumDelegate(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumDelegate lpfnEnum, IntPtr dwData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplaySettingsW(string deviceName, int modeNum, ref DEVMODE devMode);

        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        [DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiFlag);

        public static void EnablePerMonitorDpiV2()
        {
            try
            {
                SetProcessDpiAwarenessContext(new IntPtr(-4));
            }
            catch
            {
            }
        }

        public static List<MonitorInfoItem> GetAllMonitors()
        {
            List<MonitorInfoItem> list = new List<MonitorInfoItem>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr hMon, IntPtr hdc, ref RECT rc, IntPtr data)
            {
                MONITORINFOEX mi = new MONITORINFOEX();
                mi.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
                if (GetMonitorInfo(hMon, ref mi))
                {
                    uint dx = 96, dy = 96;
                    try
                    {
                        GetDpiForMonitor(hMon, MDT_EFFECTIVE_DPI, out dx, out dy);
                    }
                    catch
                    {
                        dx = 96;
                        dy = 96;
                    }

                    MonitorInfoItem item = new MonitorInfoItem();
                    item.HMonitor = hMon;
                    item.DeviceName = mi.szDevice;
                    item.IsPrimary = (mi.dwFlags & MONITORINFOF_PRIMARY) != 0;
                    item.BoundsLeft = mi.rcMonitor.Left;
                    item.BoundsTop = mi.rcMonitor.Top;
                    item.BoundsWidth = mi.rcMonitor.Right - mi.rcMonitor.Left;
                    item.BoundsHeight = mi.rcMonitor.Bottom - mi.rcMonitor.Top;
                    item.WorkLeft = mi.rcWork.Left;
                    item.WorkTop = mi.rcWork.Top;
                    item.WorkWidth = mi.rcWork.Right - mi.rcWork.Left;
                    item.WorkHeight = mi.rcWork.Bottom - mi.rcWork.Top;
                    item.DpiX = dx;
                    item.DpiY = dy;
                    item.RefreshRate = QueryRefreshRate(mi.szDevice);
                    list.Add(item);
                }
                return true;
            }, IntPtr.Zero);
            return list;
        }

        private static int QueryRefreshRate(string deviceName)
        {
            try
            {
                DEVMODE dm = new DEVMODE();
                dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
                if (EnumDisplaySettingsW(deviceName, -1, ref dm) && dm.dmDisplayFrequency >= 30 && dm.dmDisplayFrequency <= 500)
                {
                    return dm.dmDisplayFrequency;
                }
            }
            catch
            {
            }
            return 60;
        }

        public static MonitorInfoItem PickTargetMonitor(string targetSetting)
        {
            List<MonitorInfoItem> all = GetAllMonitors();
            if (all.Count == 0) return null;

            bool wantAutoPortrait = !string.IsNullOrEmpty(targetSetting) &&
                                    targetSetting.Equals("auto-portrait", StringComparison.OrdinalIgnoreCase);
            bool wantAutoPrimary = string.IsNullOrEmpty(targetSetting) ||
                                   targetSetting.Equals("auto", StringComparison.OrdinalIgnoreCase) ||
                                   targetSetting.Equals("primary", StringComparison.OrdinalIgnoreCase);

            if (!wantAutoPortrait && !wantAutoPrimary)
            {
                for (int i = 0; i < all.Count; i++)
                {
                    if (all[i].DeviceName.Equals(targetSetting, StringComparison.OrdinalIgnoreCase))
                        return all[i];
                }
            }

            if (wantAutoPortrait)
            {
                // Prefer portrait monitor with highest pixel count when user explicitly chose auto-portrait
                MonitorInfoItem bestPortrait = null;
                for (int i = 0; i < all.Count; i++)
                {
                    if (all[i].IsPortrait)
                    {
                        if (bestPortrait == null ||
                            ((long)all[i].BoundsWidth * all[i].BoundsHeight > (long)bestPortrait.BoundsWidth * bestPortrait.BoundsHeight))
                        {
                            bestPortrait = all[i];
                        }
                    }
                }
                if (bestPortrait != null) return bestPortrait;
            }

            // Default: follow primary monitor (auto-adapts to landscape or portrait!)
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].IsPrimary) return all[i];
            }
            return all[0];
        }
    }
}
