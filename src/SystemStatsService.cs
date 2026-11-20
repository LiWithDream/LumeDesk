using System;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace LumeDesk
{
    public class SystemStatsSnapshot
    {
        public double CpuPercent { get; set; }
        public double RamPercent { get; set; }
        public double RamUsedGb { get; set; }
        public double RamTotalGb { get; set; }
        public double DiskFreeGb { get; set; }
        public double DiskTotalGb { get; set; }
        public double DiskUsedPercent { get; set; }
        public long NetDownBps { get; set; }
        public long NetUpBps { get; set; }
    }

    public class SystemStatsService
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct FILETIME
        {
            public uint dwLowDateTime;
            public uint dwHighDateTime;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        private ulong _prevIdle;
        private ulong _prevKernel;
        private ulong _prevUser;
        private bool _hasCpuSample;

        private long _prevNetRecv;
        private long _prevNetSent;
        private DateTime _prevNetTime = DateTime.MinValue;
        private DateTime _lastDiskCheck = DateTime.MinValue;
        private double _cachedDiskFreeGb;
        private double _cachedDiskTotalGb;
        private double _cachedDiskUsedPct;

        private static ulong ToUInt64(FILETIME ft)
        {
            return ((ulong)ft.dwHighDateTime << 32) | ft.dwLowDateTime;
        }

        public SystemStatsSnapshot Sample()
        {
            return Sample(true);
        }

        public SystemStatsSnapshot Sample(bool includeNetworkFallback)
        {
            SystemStatsSnapshot snap = new SystemStatsSnapshot();

            // 1. CPU via Win32 GetSystemTimes (instant, zero PerformanceCounter startup lag!)
            try
            {
                FILETIME fIdle, fKernel, fUser;
                if (GetSystemTimes(out fIdle, out fKernel, out fUser))
                {
                    ulong idle = ToUInt64(fIdle);
                    ulong kernel = ToUInt64(fKernel);
                    ulong user = ToUInt64(fUser);

                    if (_hasCpuSample)
                    {
                        ulong dIdle = idle - _prevIdle;
                        ulong dKernel = kernel - _prevKernel;
                        ulong dUser = user - _prevUser;
                        ulong dTotal = dKernel + dUser;
                        if (dTotal > 0)
                        {
                            double busy = (double)(dTotal - dIdle) * 100.0 / dTotal;
                            if (busy < 0) busy = 0;
                            if (busy > 100) busy = 100;
                            snap.CpuPercent = busy;
                        }
                    }
                    _prevIdle = idle;
                    _prevKernel = kernel;
                    _prevUser = user;
                    _hasCpuSample = true;
                }
            }
            catch
            {
            }

            // 2. RAM via GlobalMemoryStatusEx
            try
            {
                MEMORYSTATUSEX mem = new MEMORYSTATUSEX();
                mem.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
                if (GlobalMemoryStatusEx(ref mem) && mem.ullTotalPhys > 0)
                {
                    double totalGb = mem.ullTotalPhys / (1024.0 * 1024.0 * 1024.0);
                    double usedGb = (mem.ullTotalPhys - mem.ullAvailPhys) / (1024.0 * 1024.0 * 1024.0);
                    snap.RamTotalGb = totalGb;
                    snap.RamUsedGb = usedGb;
                    snap.RamPercent = mem.dwMemoryLoad;
                }
            }
            catch
            {
            }

            // 3. System Drive C: (refresh every 30s)
            try
            {
                if ((DateTime.UtcNow - _lastDiskCheck).TotalSeconds > 30)
                {
                    DriveInfo di = new DriveInfo("C");
                    if (di.IsReady && di.TotalSize > 0)
                    {
                        _cachedDiskTotalGb = di.TotalSize / (1024.0 * 1024.0 * 1024.0);
                        _cachedDiskFreeGb = di.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0);
                        _cachedDiskUsedPct = ((double)(di.TotalSize - di.AvailableFreeSpace) / di.TotalSize) * 100.0;
                    }
                    _lastDiskCheck = DateTime.UtcNow;
                }
                snap.DiskFreeGb = _cachedDiskFreeGb;
                snap.DiskTotalGb = _cachedDiskTotalGb;
                snap.DiskUsedPercent = _cachedDiskUsedPct;
            }
            catch
            {
            }

            // 4. Fallback OS-level NIC speed (only polled when Clash is disconnected to avoid UI-thread NIC stalls on high-refresh displays)
            if (includeNetworkFallback)
            try
            {
                long recv = 0, sent = 0;
                NetworkInterface[] nics = NetworkInterface.GetAllNetworkInterfaces();
                for (int i = 0; i < nics.Length; i++)
                {
                    NetworkInterface nic = nics[i];
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    string desc = (nic.Description ?? "").ToLowerInvariant();
                    if (desc.IndexOf("wintun") >= 0 || desc.IndexOf("mihomo") >= 0 || desc.IndexOf("virtual") >= 0) continue;
                    IPv4InterfaceStatistics stats = nic.GetIPv4Statistics();
                    recv += stats.BytesReceived;
                    sent += stats.BytesSent;
                }
                DateTime now = DateTime.UtcNow;
                if (_prevNetTime != DateTime.MinValue)
                {
                    double dt = (now - _prevNetTime).TotalSeconds;
                    if (dt > 0.2)
                    {
                        long dR = recv - _prevNetRecv;
                        long dS = sent - _prevNetSent;
                        if (dR >= 0) snap.NetDownBps = (long)(dR / dt);
                        if (dS >= 0) snap.NetUpBps = (long)(dS / dt);
                    }
                }
                _prevNetRecv = recv;
                _prevNetSent = sent;
                _prevNetTime = now;
            }
            catch
            {
            }

            return snap;
        }
    }
}
