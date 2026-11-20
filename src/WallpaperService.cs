using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace LumeDesk
{
    public class WallpaperBundle
    {
        public BitmapSource BlurredBackdrop { get; set; }
        public BitmapSource RawWallpaper { get; set; }
        public string SourcePath { get; set; }
    }

    public static class WallpaperService
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [ComImport]
        [Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDesktopWallpaper
        {
            void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorID, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);
            [return: MarshalAs(UnmanagedType.LPWStr)]
            string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorID);
            [return: MarshalAs(UnmanagedType.LPWStr)]
            string GetMonitorDevicePathAt(uint monitorIndex);
            uint GetMonitorDevicePathCount();
            RECT GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorID);
        }

        [ComImport]
        [Guid("C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD")]
        private class DesktopWallpaperCom
        {
        }

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        public static WallpaperBundle LoadForMonitor(MonitorInfoItem monitor, int blurRadius)
        {
            WallpaperBundle bundle = new WallpaperBundle();
            if (monitor == null) return bundle;

            string path = ResolveWallpaperPath(monitor);
            bundle.SourcePath = path;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return bundle;
            }

            try
            {
                byte[] bytes = File.ReadAllBytes(path);

                // 1. Decode TRUE 1:1 native-resolution wallpaper cropped to the monitor's exact WorkArea!
                //    This allows hardware-accelerated Direct3D SwapChain rendering (AllowsTransparency=false)
                //    that is pixel-identical to the native Windows desktop wallpaper at full 4K sharpness.
                bundle.RawWallpaper = LoadNativeWorkAreaWallpaper(bytes, monitor);

                // 2. Generate fast downscaled vibrant Gaussian blur backdrop for frosted glass cards
                using (MemoryStream ms = new MemoryStream(bytes))
                using (Bitmap src = new Bitmap(ms))
                {
                    int targetW = Math.Max(360, Math.Min(720, monitor.BoundsWidth / 4));
                    int targetH = (int)Math.Round((double)targetW * monitor.BoundsHeight / Math.Max(1, monitor.BoundsWidth));
                    if (targetH < 360) targetH = 360;

                    using (Bitmap fitted = FitCover(src, targetW, targetH))
                    {
                        int wx = (int)Math.Round((double)(monitor.WorkLeft - monitor.BoundsLeft) * targetW / Math.Max(1, monitor.BoundsWidth));
                        int wy = (int)Math.Round((double)(monitor.WorkTop - monitor.BoundsTop) * targetH / Math.Max(1, monitor.BoundsHeight));
                        int ww = (int)Math.Round((double)monitor.WorkWidth * targetW / Math.Max(1, monitor.BoundsWidth));
                        int wh = (int)Math.Round((double)monitor.WorkHeight * targetH / Math.Max(1, monitor.BoundsHeight));
                        if (wx < 0) wx = 0;
                        if (wy < 0) wy = 0;
                        if (wx + ww > targetW) ww = targetW - wx;
                        if (wy + wh > targetH) wh = targetH - wy;
                        if (ww < 16) ww = targetW;
                        if (wh < 16) wh = targetH;

                        using (Bitmap workSlice = fitted.Clone(new Rectangle(wx, wy, ww, wh), PixelFormat.Format32bppArgb))
                        {
                            if (bundle.RawWallpaper == null)
                            {
                                bundle.RawWallpaper = ConvertToFrozenBitmapSource(workSlice);
                            }
                            using (Bitmap blurred = ApplyVibrantGaussianBlur(workSlice, blurRadius))
                            {
                                bundle.BlurredBackdrop = ConvertToFrozenBitmapSource(blurred);
                            }
                        }
                    }
                }
            }
            catch
            {
            }

            return bundle;
        }

        private static BitmapSource LoadNativeWorkAreaWallpaper(byte[] bytes, MonitorInfoItem monitor)
        {
            if (bytes == null || bytes.Length == 0 || monitor == null) return null;

            try
            {
                BitmapImage bi = new BitmapImage();
                using (MemoryStream ms = new MemoryStream(bytes))
                {
                    bi.BeginInit();
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.StreamSource = ms;
                    bi.EndInit();
                }
                bi.Freeze();

                int pw = bi.PixelWidth;
                int ph = bi.PixelHeight;
                if (pw <= 0 || ph <= 0) return null;

                // Fast path: Transcoded_00x already matches exact physical monitor BoundsWidth x BoundsHeight!
                if (pw == monitor.BoundsWidth && ph == monitor.BoundsHeight)
                {
                    int wx = Math.Max(0, monitor.WorkLeft - monitor.BoundsLeft);
                    int wy = Math.Max(0, monitor.WorkTop - monitor.BoundsTop);
                    int ww = Math.Min(pw - wx, monitor.WorkWidth);
                    int wh = Math.Min(ph - wy, monitor.WorkHeight);
                    if (wx == 0 && wy == 0 && ww == pw && wh == ph)
                    {
                        return bi;
                    }
                    if (ww > 16 && wh > 16)
                    {
                        CroppedBitmap cropped = new CroppedBitmap(bi, new Int32Rect(wx, wy, ww, wh));
                        cropped.Freeze();
                        return cropped;
                    }
                }

                // General path: Cover-crop raw wallpaper to monitor aspect ratio and slice exact WorkArea
                double srcRatio = (double)pw / ph;
                double monRatio = (double)monitor.BoundsWidth / Math.Max(1, monitor.BoundsHeight);
                double coverX = 0, coverY = 0, coverW = pw, coverH = ph;
                if (srcRatio > monRatio)
                {
                    coverW = ph * monRatio;
                    coverX = (pw - coverW) * 0.5;
                }
                else
                {
                    coverH = pw / monRatio;
                    coverY = (ph - coverH) * 0.5;
                }

                double relX = (double)(monitor.WorkLeft - monitor.BoundsLeft) / Math.Max(1, monitor.BoundsWidth);
                double relY = (double)(monitor.WorkTop - monitor.BoundsTop) / Math.Max(1, monitor.BoundsHeight);
                double relW = (double)monitor.WorkWidth / Math.Max(1, monitor.BoundsWidth);
                double relH = (double)monitor.WorkHeight / Math.Max(1, monitor.BoundsHeight);

                int cx = Math.Max(0, (int)Math.Round(coverX + relX * coverW));
                int cy = Math.Max(0, (int)Math.Round(coverY + relY * coverH));
                int cw = Math.Min(pw - cx, Math.Max(16, (int)Math.Round(relW * coverW)));
                int ch = Math.Min(ph - cy, Math.Max(16, (int)Math.Round(relH * coverH)));

                CroppedBitmap cb = new CroppedBitmap(bi, new Int32Rect(cx, cy, cw, ch));
                cb.Freeze();
                return cb;
            }
            catch
            {
                return null;
            }
        }

        private static string ResolveWallpaperPath(MonitorInfoItem monitor)
        {
            string themesDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Themes");

            // 1. Check pre-transcoded per-monitor files whose exact dimensions match this monitor (fastest & exact Windows crop!)
            if (Directory.Exists(themesDir))
            {
                try
                {
                    string[] candidates = Directory.GetFiles(themesDir, "Transcoded_00*");
                    for (int i = 0; i < candidates.Length; i++)
                    {
                        string f = candidates[i];
                        if (f.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                        try
                        {
                            using (FileStream fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                            using (Image img = Image.FromStream(fs, false, false))
                            {
                                if (img.Width == monitor.BoundsWidth && img.Height == monitor.BoundsHeight)
                                {
                                    return f;
                                }
                            }
                        }
                        catch
                        {
                        }
                    }
                }
                catch
                {
                }
            }

            // 2. Try IDesktopWallpaper COM interface for exact monitor wallpaper
            IDesktopWallpaper dw = null;
            try
            {
                dw = (IDesktopWallpaper)new DesktopWallpaperCom();
                uint count = dw.GetMonitorDevicePathCount();
                for (uint i = 0; i < count; i++)
                {
                    string id = dw.GetMonitorDevicePathAt(i);
                    RECT rc = dw.GetMonitorRECT(id);
                    if (rc.Left == monitor.BoundsLeft && rc.Top == monitor.BoundsTop &&
                        (rc.Right - rc.Left) == monitor.BoundsWidth && (rc.Bottom - rc.Top) == monitor.BoundsHeight)
                    {
                        string wp = dw.GetWallpaper(id);
                        if (!string.IsNullOrEmpty(wp) && File.Exists(wp))
                        {
                            return wp;
                        }
                    }
                }
            }
            catch
            {
            }
            finally
            {
                if (dw != null) { try { Marshal.FinalReleaseComObject(dw); } catch { } }
            }

            // 3. Fallback to TranscodedWallpaper or registry WallPaper
            string transcoded = Path.Combine(themesDir, "TranscodedWallpaper");
            if (File.Exists(transcoded)) return transcoded;

            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", false))
                {
                    if (key != null)
                    {
                        string wp = key.GetValue("WallPaper") as string;
                        if (!string.IsNullOrEmpty(wp) && File.Exists(wp)) return wp;
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        private static Bitmap FitCover(Bitmap src, int dstW, int dstH)
        {
            Bitmap dst = new Bitmap(dstW, dstH, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(dst))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                double srcRatio = (double)src.Width / Math.Max(1, src.Height);
                double dstRatio = (double)dstW / Math.Max(1, dstH);

                RectangleF srcRect;
                if (srcRatio > dstRatio)
                {
                    float cropW = (float)(src.Height * dstRatio);
                    float cropX = (src.Width - cropW) * 0.5f;
                    srcRect = new RectangleF(cropX, 0, cropW, src.Height);
                }
                else
                {
                    float cropH = (float)(src.Width / dstRatio);
                    float cropY = (src.Height - cropH) * 0.5f;
                    srcRect = new RectangleF(0, cropY, src.Width, cropH);
                }

                g.DrawImage(src, new RectangleF(0, 0, dstW, dstH), srcRect, GraphicsUnit.Pixel);
            }
            return dst;
        }

        private static Bitmap ApplyVibrantGaussianBlur(Bitmap source, int radius)
        {
            int w = source.Width;
            int h = source.Height;
            Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.DrawImage(source, 0, 0, w, h);
            }

            Rectangle rect = new Rectangle(0, 0, w, h);
            BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            int bytes = Math.Abs(data.Stride) * h;
            byte[] buf = new byte[bytes];
            byte[] tmp = new byte[bytes];
            Marshal.Copy(data.Scan0, buf, 0, bytes);

            int stride = data.Stride;
            int r = Math.Max(2, Math.Min(36, radius));

            // 3 passes of separable box blur => smooth Gaussian blur
            for (int pass = 0; pass < 3; pass++)
            {
                BoxBlurH(buf, tmp, w, h, stride, r);
                BoxBlurV(tmp, buf, w, h, stride, r);
            }

            // Rich iOS/visionOS-style vibrancy boost (1.36x saturation) so frosted glass feels luminous and jewel-like
            for (int i = 0; i < bytes; i += 4)
            {
                float b = buf[i];
                float g = buf[i + 1];
                float rC = buf[i + 2];
                float lum = 0.299f * rC + 0.587f * g + 0.114f * b;
                float sat = 1.36f;
                rC = lum + (rC - lum) * sat;
                g = lum + (g - lum) * sat;
                b = lum + (b - lum) * sat;
                buf[i] = ClampByte(b);
                buf[i + 1] = ClampByte(g);
                buf[i + 2] = ClampByte(rC);
                buf[i + 3] = 255;
            }

            Marshal.Copy(buf, 0, data.Scan0, bytes);
            bmp.UnlockBits(data);
            return bmp;
        }

        private static void BoxBlurH(byte[] src, byte[] dst, int w, int h, int stride, int r)
        {
            int diam = r * 2 + 1;
            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                int sumB = 0, sumG = 0, sumR = 0;
                for (int ix = -r; ix <= r; ix++)
                {
                    int cx = ix < 0 ? 0 : (ix >= w ? w - 1 : ix);
                    int idx = row + cx * 4;
                    sumB += src[idx];
                    sumG += src[idx + 1];
                    sumR += src[idx + 2];
                }
                for (int x = 0; x < w; x++)
                {
                    int outIdx = row + x * 4;
                    dst[outIdx] = (byte)(sumB / diam);
                    dst[outIdx + 1] = (byte)(sumG / diam);
                    dst[outIdx + 2] = (byte)(sumR / diam);
                    dst[outIdx + 3] = 255;

                    int remX = x - r;
                    if (remX < 0) remX = 0;
                    int addX = x + r + 1;
                    if (addX >= w) addX = w - 1;

                    int rIdx = row + remX * 4;
                    int aIdx = row + addX * 4;
                    sumB += src[aIdx] - src[rIdx];
                    sumG += src[aIdx + 1] - src[rIdx + 1];
                    sumR += src[aIdx + 2] - src[rIdx + 2];
                }
            }
        }

        private static void BoxBlurV(byte[] src, byte[] dst, int w, int h, int stride, int r)
        {
            int diam = r * 2 + 1;
            for (int x = 0; x < w; x++)
            {
                int col = x * 4;
                int sumB = 0, sumG = 0, sumR = 0;
                for (int iy = -r; iy <= r; iy++)
                {
                    int cy = iy < 0 ? 0 : (iy >= h ? h - 1 : iy);
                    int idx = cy * stride + col;
                    sumB += src[idx];
                    sumG += src[idx + 1];
                    sumR += src[idx + 2];
                }
                for (int y = 0; y < h; y++)
                {
                    int outIdx = y * stride + col;
                    dst[outIdx] = (byte)(sumB / diam);
                    dst[outIdx + 1] = (byte)(sumG / diam);
                    dst[outIdx + 2] = (byte)(sumR / diam);
                    dst[outIdx + 3] = 255;

                    int remY = y - r;
                    if (remY < 0) remY = 0;
                    int addY = y + r + 1;
                    if (addY >= h) addY = h - 1;

                    int rIdx = remY * stride + col;
                    int aIdx = addY * stride + col;
                    sumB += src[aIdx] - src[rIdx];
                    sumG += src[aIdx + 1] - src[rIdx + 1];
                    sumR += src[aIdx + 2] - src[rIdx + 2];
                }
            }
        }

        private static byte ClampByte(float v)
        {
            if (v < 0) return 0;
            if (v > 255) return 255;
            return (byte)v;
        }

        private static BitmapSource ConvertToFrozenBitmapSource(Bitmap bmp)
        {
            IntPtr hBitmap = bmp.GetHbitmap();
            try
            {
                BitmapSource src = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap,
                    IntPtr.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            finally
            {
                DeleteObject(hBitmap);
            }
        }
    }
}
