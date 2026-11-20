using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace LumeDesk
{
    public static class IconExtractor
    {
        private static readonly Dictionary<string, BitmapSource> Cache = new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE
        {
            public int cx;
            public int cy;
        }

        [Flags]
        private enum SIIGBF
        {
            SIIGBF_RESIZETOFIT = 0x00,
            SIIGBF_BIGGERSIZEOK = 0x01,
            SIIGBF_MEMORYONLY = 0x02,
            SIIGBF_ICONONLY = 0x04,
            SIIGBF_THUMBNAILONLY = 0x08,
            SIIGBF_INCACHEONLY = 0x10
        }

        [ComImport]
        [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            void GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
            IntPtr pbc,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint PrivateExtractIconsW(
            string szFileName,
            int nIconIndex,
            int cxIcon,
            int cyIcon,
            IntPtr[] phicon,
            uint[] piconid,
            uint nIcons,
            uint flags);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        public static void ClearCache()
        {
            Cache.Clear();
        }

        public static BitmapSource GetPhoneIcon(string filePath, string customIconsDir, string bgOverride)
        {
            string mode = string.IsNullOrEmpty(bgOverride) ? "auto" : bgOverride.ToLowerInvariant();
            string stamp = "";
            try { stamp = File.GetLastWriteTimeUtc(filePath).Ticks.ToString(); } catch { }
            string key = filePath + "|" + mode + "|" + stamp;

            BitmapSource cached;
            if (Cache.TryGetValue(key, out cached)) return cached;

            Bitmap raw = null;
            try
            {
                // 0. Check custom PNG/ICO override in data\_Icons\<DisplayName>.png
                string nameNoExt = Path.GetFileNameWithoutExtension(filePath);
                if (!string.IsNullOrEmpty(customIconsDir) && Directory.Exists(customIconsDir))
                {
                    string pngPath = Path.Combine(customIconsDir, nameNoExt + ".png");
                    if (File.Exists(pngPath))
                    {
                        raw = LoadImageSafe(pngPath);
                    }
                    else
                    {
                        string icoPath = Path.Combine(customIconsDir, nameNoExt + ".ico");
                        if (File.Exists(icoPath))
                        {
                            raw = LoadIcoLargestFrame(icoPath);
                        }
                    }
                }

                // 1. Extract directly from target/icon source (avoids shortcut arrow!)
                if (raw == null)
                {
                    raw = ExtractRawBitmap(filePath);
                }

                if (raw == null)
                {
                    raw = CreateFallbackBadge(nameNoExt);
                }

                using (raw)
                {
                    BitmapSource bs = ComposeSquircleBitmapSource(raw, mode);
                    Cache[key] = bs;
                    return bs;
                }
            }
            catch
            {
                using (Bitmap fb = CreateFallbackBadge(Path.GetFileNameWithoutExtension(filePath)))
                {
                    BitmapSource bs = ComposeSquircleBitmapSource(fb, "raw");
                    Cache[key] = bs;
                    return bs;
                }
            }
        }

        private static Bitmap ExtractRawBitmap(string filePath)
        {
            string ext = (Path.GetExtension(filePath) ?? "").ToLowerInvariant();

            if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp")
            {
                return LoadImageSafe(filePath);
            }

            if (ext == ".ico")
            {
                return LoadIcoLargestFrame(filePath);
            }

            if (ext == ".lnk")
            {
                string targetPath;
                string iconLoc;
                ResolveShortcut(filePath, out targetPath, out iconLoc);

                int fallbackIconIndex = 0;

                // First check explicit IconLocation (e.g., Chrome PWA .ico or exe,index)
                if (!string.IsNullOrEmpty(iconLoc))
                {
                    string iconFile;
                    int iconIndex;
                    ParseIconLocation(iconLoc, out iconFile, out iconIndex);
                    fallbackIconIndex = iconIndex;
                    if (!string.IsNullOrEmpty(iconFile))
                    {
                        iconFile = Environment.ExpandEnvironmentVariables(iconFile);
                        if (File.Exists(iconFile))
                        {
                            string iExt = (Path.GetExtension(iconFile) ?? "").ToLowerInvariant();
                            if (iExt == ".ico")
                            {
                                Bitmap icoBmp = LoadIcoLargestFrame(iconFile);
                                if (icoBmp != null) return icoBmp;
                            }
                            else if (iExt == ".png" || iExt == ".jpg" || iExt == ".bmp")
                            {
                                Bitmap imgBmp = LoadImageSafe(iconFile);
                                if (imgBmp != null) return imgBmp;
                            }
                            else
                            {
                                if (iconIndex != 0)
                                {
                                    Bitmap peBmp = ExtractViaPrivateExtractIcons(iconFile, iconIndex, 256);
                                    if (peBmp != null) return peBmp;
                                }
                                Bitmap shellBmp = ExtractViaShellImageFactory(iconFile, 256);
                                if (shellBmp != null) return StripWindowsShellBoxFrame(shellBmp);
                                Bitmap peBmp0 = ExtractViaPrivateExtractIcons(iconFile, iconIndex, 256);
                                if (peBmp0 != null) return peBmp0;
                            }
                        }
                    }
                }

                // Next extract directly from target .exe (clean 256x256, NO shortcut arrow!)
                if (!string.IsNullOrEmpty(targetPath) && File.Exists(targetPath))
                {
                    string tExt = (Path.GetExtension(targetPath) ?? "").ToLowerInvariant();
                    if (tExt == ".exe" || tExt == ".dll" || tExt == ".ico")
                    {
                        if (fallbackIconIndex != 0)
                        {
                            Bitmap peIdx = ExtractViaPrivateExtractIcons(targetPath, fallbackIconIndex, 256);
                            if (peIdx != null) return peIdx;
                        }
                        Bitmap shellBmp = ExtractViaShellImageFactory(targetPath, 256);
                        if (shellBmp != null) return StripWindowsShellBoxFrame(shellBmp);
                        Bitmap peBmp = ExtractViaPrivateExtractIcons(targetPath, 0, 256);
                        if (peBmp != null) return peBmp;
                    }
                }
            }
            else if (ext == ".url")
            {
                string iconFile;
                int iconIndex;
                if (ParseUrlShortcutIcon(filePath, out iconFile, out iconIndex) && File.Exists(iconFile))
                {
                    string iExt = (Path.GetExtension(iconFile) ?? "").ToLowerInvariant();
                    if (iExt == ".ico")
                    {
                        Bitmap icoBmp = LoadIcoLargestFrame(iconFile);
                        if (icoBmp != null) return icoBmp;
                    }
                    Bitmap shellBmp = ExtractViaShellImageFactory(iconFile, 256);
                    if (shellBmp != null) return StripWindowsShellBoxFrame(shellBmp);
                }
            }

            // Fallback: Shell image factory directly on the file itself
            Bitmap fallbackShell = ExtractViaShellImageFactory(filePath, 256);
            return fallbackShell != null ? StripWindowsShellBoxFrame(fallbackShell) : null;
        }

        private static void ResolveShortcut(string lnkPath, out string targetPath, out string iconLocation)
        {
            targetPath = "";
            iconLocation = "";
            object shell = null;
            object sc = null;
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return;
                shell = Activator.CreateInstance(shellType);
                sc = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
                Type scType = sc.GetType();
                targetPath = scType.InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, sc, null) as string;
                iconLocation = scType.InvokeMember("IconLocation", System.Reflection.BindingFlags.GetProperty, null, sc, null) as string;
            }
            catch
            {
            }
            finally
            {
                if (sc != null) { try { Marshal.FinalReleaseComObject(sc); } catch { } }
                if (shell != null) { try { Marshal.FinalReleaseComObject(shell); } catch { } }
            }
        }

        private static void ParseIconLocation(string raw, out string file, out int index)
        {
            file = "";
            index = 0;
            if (string.IsNullOrEmpty(raw)) return;
            int comma = raw.LastIndexOf(',');
            if (comma >= 0)
            {
                file = raw.Substring(0, comma).Trim().Trim('"');
                int parsed;
                if (int.TryParse(raw.Substring(comma + 1).Trim(), out parsed))
                {
                    index = parsed;
                }
            }
            else
            {
                file = raw.Trim().Trim('"');
            }
        }

        private static bool ParseUrlShortcutIcon(string urlPath, out string iconFile, out int iconIndex)
        {
            iconFile = "";
            iconIndex = 0;
            try
            {
                string[] lines = File.ReadAllLines(urlPath);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.StartsWith("IconFile=", StringComparison.OrdinalIgnoreCase))
                    {
                        iconFile = Environment.ExpandEnvironmentVariables(line.Substring(9).Trim().Trim('"'));
                    }
                    else if (line.StartsWith("IconIndex=", StringComparison.OrdinalIgnoreCase))
                    {
                        int idx;
                        if (int.TryParse(line.Substring(10).Trim(), out idx)) iconIndex = idx;
                    }
                }
                return !string.IsNullOrEmpty(iconFile);
            }
            catch
            {
                return false;
            }
        }

        private static Bitmap LoadImageSafe(string path)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                using (MemoryStream ms = new MemoryStream(bytes))
                using (Bitmap tmp = new Bitmap(ms))
                {
                    Bitmap clone = new Bitmap(tmp.Width, tmp.Height, PixelFormat.Format32bppArgb);
                    using (Graphics g = Graphics.FromImage(clone))
                    {
                        g.DrawImage(tmp, 0, 0, tmp.Width, tmp.Height);
                    }
                    return clone;
                }
            }
            catch
            {
                return null;
            }
        }

        private static Bitmap LoadIcoLargestFrame(string icoPath)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(icoPath);
                using (MemoryStream ms = new MemoryStream(bytes))
                {
                    IconBitmapDecoder decoder = new IconBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    BitmapFrame best = null;
                    int bestArea = -1;
                    for (int i = 0; i < decoder.Frames.Count; i++)
                    {
                        BitmapFrame f = decoder.Frames[i];
                        int area = f.PixelWidth * f.PixelHeight;
                        if (area > bestArea)
                        {
                            bestArea = area;
                            best = f;
                        }
                    }
                    if (best != null)
                    {
                        return BitmapSourceToDrawingBitmap(best);
                    }
                }
            }
            catch
            {
            }

            return ExtractViaPrivateExtractIcons(icoPath, 0, 256);
        }

        private static Bitmap ExtractViaPrivateExtractIcons(string file, int index, int size)
        {
            try
            {
                IntPtr[] icons = new IntPtr[1];
                uint[] ids = new uint[1];
                uint count = PrivateExtractIconsW(file, index, size, size, icons, ids, 1, 0);
                if (count > 0 && icons[0] != IntPtr.Zero)
                {
                    try
                    {
                        BitmapSource bs = Imaging.CreateBitmapSourceFromHIcon(icons[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        return BitmapSourceToDrawingBitmap(bs);
                    }
                    finally
                    {
                        DestroyIcon(icons[0]);
                    }
                }
            }
            catch
            {
            }
            return null;
        }

        private static Bitmap ExtractViaShellImageFactory(string file, int size)
        {
            IShellItemImageFactory factory = null;
            IntPtr hbm = IntPtr.Zero;
            try
            {
                Guid iid = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");
                SHCreateItemFromParsingName(file, IntPtr.Zero, iid, out factory);
                if (factory == null) return null;

                SIZE sz = new SIZE();
                sz.cx = size;
                sz.cy = size;
                factory.GetImage(sz, SIIGBF.SIIGBF_BIGGERSIZEOK | SIIGBF.SIIGBF_ICONONLY, out hbm);
                if (hbm != IntPtr.Zero)
                {
                    BitmapSource bs = Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    return BitmapSourceToDrawingBitmap(bs);
                }
            }
            catch
            {
            }
            finally
            {
                if (hbm != IntPtr.Zero) DeleteObject(hbm);
                if (factory != null) Marshal.FinalReleaseComObject(factory);
            }
            return null;
        }

        private static Bitmap BitmapSourceToDrawingBitmap(BitmapSource source)
        {
            FormatConvertedBitmap formatted = new FormatConvertedBitmap();
            formatted.BeginInit();
            formatted.Source = source;
            formatted.DestinationFormat = System.Windows.Media.PixelFormats.Bgra32;
            formatted.EndInit();

            int w = formatted.PixelWidth;
            int h = formatted.PixelHeight;
            Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            BitmapData data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                formatted.CopyPixels(Int32Rect.Empty, data.Scan0, data.Stride * h, data.Stride);
            }
            finally
            {
                bmp.UnlockBits(data);
            }
            return bmp;
        }

        /// <summary>
        /// Strips Windows Explorer's 256x256 small-icon frame that IShellItemImageFactory
        /// adds around apps whose largest embedded icon is smaller than 256px (e.g. 48px/64px/96px).
        /// Windows draws a 5px translucent border (alpha ~26..77) around a completely transparent moat
        /// surrounding the crisp centered native icon. Cropping to the inner icon removes the fake frame!
        /// </summary>
        public static Bitmap StripWindowsShellBoxFrame(Bitmap bmp)
        {
            if (bmp == null) return null;
            int w = bmp.Width;
            int h = bmp.Height;
            if (w < 128 || h < 128 || Math.Abs(w - h) > 4) return bmp;

            BitmapData data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            Rectangle? cropRect = null;
            try
            {
                int bytes = Math.Abs(data.Stride) * h;
                byte[] buf = new byte[bytes];
                Marshal.Copy(data.Scan0, buf, 0, bytes);
                int stride = data.Stride;

                int midX = w / 2;
                int midY = h / 2;
                int inset = Math.Max(10, w / 18);

                // Check outer 5px frame midpoints for Windows Shell's translucent card border (alpha 10..120)
                int[,] outerPts = new int[,]
                {
                    { midX, 0 }, { midX, 1 }, { midX, 2 },
                    { midX, h - 1 }, { midX, h - 2 }, { midX, h - 3 },
                    { 0, midY }, { 1, midY }, { 2, midY },
                    { w - 1, midY }, { w - 2, midY }, { w - 3, midY }
                };
                int translucentFrameHits = 0;
                int opaqueGrayHits = 0;
                for (int i = 0; i < 12; i++)
                {
                    int idx = outerPts[i, 1] * stride + outerPts[i, 0] * 4;
                    byte b = buf[idx], g = buf[idx + 1], r = buf[idx + 2], a = buf[idx + 3];
                    int diff = Math.Max(Math.Abs(r - g), Math.Abs(g - b));
                    if (a >= 10 && a <= 125 && diff <= 16) translucentFrameHits++;
                    if (a > 140 && diff <= 14 && r >= 150 && r <= 252) opaqueGrayHits++;
                }

                // Sample 8 points on the inset moat ring
                int[,] ringPts = new int[,]
                {
                    { inset, inset }, { midX, inset }, { w - 1 - inset, inset },
                    { inset, midY }, { w - 1 - inset, midY },
                    { inset, h - 1 - inset }, { midX, h - 1 - inset }, { w - 1 - inset, h - 1 - inset }
                };

                bool isTransparentMoat = (translucentFrameHits >= 8);
                bool isWhiteMoat = (opaqueGrayHits >= 6);
                for (int i = 0; i < 8; i++)
                {
                    int idx = ringPts[i, 1] * stride + ringPts[i, 0] * 4;
                    byte b = buf[idx], g = buf[idx + 1], r = buf[idx + 2], a = buf[idx + 3];
                    if (a > 8) isTransparentMoat = false;
                    if (a < 245 || r < 245 || g < 245 || b < 245) isWhiteMoat = false;
                }

                if (!isTransparentMoat && !isWhiteMoat) return bmp;

                // Find true inner icon core strictly inside the moat
                int minX = w, minY = h, maxX = -1, maxY = -1;
                for (int y = inset; y < h - inset; y++)
                {
                    int row = y * stride;
                    for (int x = inset; x < w - inset; x++)
                    {
                        int idx = row + x * 4;
                        byte b = buf[idx], g = buf[idx + 1], r = buf[idx + 2], a = buf[idx + 3];
                        bool isContent = isTransparentMoat
                            ? (a > 24)
                            : (a > 40 && (r < 238 || g < 238 || b < 238));
                        if (isContent)
                        {
                            if (x < minX) minX = x;
                            if (x > maxX) maxX = x;
                            if (y < minY) minY = y;
                            if (y > maxY) maxY = y;
                        }
                    }
                }

                if (maxX > minX && maxY > minY)
                {
                    int cw = maxX - minX + 1;
                    int ch = maxY - minY + 1;
                    if (cw >= 24 && ch >= 24 && cw <= (int)(w * 0.78) && ch <= (int)(h * 0.78))
                    {
                        cropRect = new Rectangle(minX, minY, cw, ch);
                    }
                }
            }
            finally
            {
                bmp.UnlockBits(data);
            }

            if (cropRect.HasValue)
            {
                Rectangle rc = cropRect.Value;
                Bitmap cropped = new Bitmap(rc.Width, rc.Height, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(cropped))
                {
                    SetupHighQuality(g);
                    g.DrawImage(bmp, new Rectangle(0, 0, rc.Width, rc.Height), rc, GraphicsUnit.Pixel);
                }
                bmp.Dispose();
                return cropped;
            }
            return bmp;
        }

        private static BitmapSource ComposeSquircleBitmapSource(Bitmap src, string mode)
        {
            // 512x512 supersampled canvas so even huge 4K tiles (~340px physical) stay razor-sharp
            const int S = 512;

            Rectangle bounds = GetOpaqueBounds(src, 24);
            if (bounds.Width < 2 || bounds.Height < 2)
            {
                bounds = new Rectangle(0, 0, src.Width, src.Height);
            }

            // Step 1: Paint full unclipped 512x512 square content into a temporary surface
            using (Bitmap content = new Bitmap(S, S, PixelFormat.Format32bppArgb))
            {
                bool isFullBleedTile = false;
                using (Graphics gc = Graphics.FromImage(content))
                {
                    SetupHighQuality(gc);
                    gc.Clear(Color.Transparent);

                    if (mode == "raw")
                    {
                        DrawFitted(gc, src, bounds, new RectangleF(20, 20, S - 40, S - 40));
                    }
                    else
                    {
                        double fillRatio;
                        bool cornersFilled;
                        double avgLuminance;
                        double brightPixelRatio;
                        double avgSaturation;
                        AnalyzeGlyph(src, bounds, out fillRatio, out cornersFilled, out avgLuminance, out brightPixelRatio, out avgSaturation);

                        isFullBleedTile = (mode == "auto") && fillRatio > 0.88 && cornersFilled &&
                                          Math.Abs((double)bounds.Width / bounds.Height - 1.0) < 0.12;

                        if (isFullBleedTile)
                        {
                            // Tighten bounds to solid opaque core (alpha > 200) if source has soft outer glow/shadow margin
                            Rectangle tight = GetOpaqueBounds(src, 200);
                            if (tight.Width >= (int)(bounds.Width * 0.88) &&
                                tight.Height >= (int)(bounds.Height * 0.88) &&
                                Math.Abs((double)tight.Width / Math.Max(1, tight.Height) - 1.0) < 0.12)
                            {
                                bounds = tight;
                            }

                            // TileFlipXY prevents GDI+ HighQualityBicubic from sampling transparent (0,0,0,0) past edges!
                            using (ImageAttributes ia = new ImageAttributes())
                            {
                                ia.SetWrapMode(WrapMode.TileFlipXY);
                                Rectangle dst = new Rectangle(-4, -4, S + 8, S + 8);
                                gc.DrawImage(src, dst, bounds.X, bounds.Y, bounds.Width, bounds.Height, GraphicsUnit.Pixel, ia);
                            }
                        }
                        else
                        {
                            bool useDarkPlate;
                            if (mode == "dark") useDarkPlate = true;
                            else if (mode == "light") useDarkPlate = false;
                            else
                            {
                                useDarkPlate = (avgLuminance > 0.74 && brightPixelRatio > 0.45 && avgSaturation < 0.28);
                            }

                            if (useDarkPlate)
                            {
                                using (LinearGradientBrush bg = new LinearGradientBrush(
                                    new PointF(0, 0), new PointF(0, S),
                                    Color.FromArgb(255, 30, 38, 54),
                                    Color.FromArgb(255, 14, 19, 30)))
                                {
                                    gc.FillRectangle(bg, 0, 0, S, S);
                                }
                            }
                            else
                            {
                                using (LinearGradientBrush bg = new LinearGradientBrush(
                                    new PointF(0, 0), new PointF(0, S),
                                    Color.FromArgb(255, 255, 255, 255),
                                    Color.FromArgb(255, 240, 244, 251)))
                                {
                                    gc.FillRectangle(bg, 0, 0, S, S);
                                }
                            }

                            float pad = (fillRatio > 0.72) ? 56f : 72f;
                            DrawFitted(gc, src, bounds, new RectangleF(pad, pad, S - pad * 2f, S - pad * 2f));
                        }
                    }
                }

                // Step 2: For full-bleed icons (which often carry their own rounded corners or soft alpha fringe),
                // extrapolate solid interior edge colors outward past our squircle boundary so zero halo/seams remain.
                if (isFullBleedTile)
                {
                    BleedOpaqueEdges(content);
                }

                // Step 3: Apply analytical 8-bit sub-pixel anti-aliased continuous Apple squircle mask
                // directly into native WPF premultiplied Pbgra32 (avoids Bitmap.GetHbitmap()'s LightGray RGB(211,211,211) halo!)
                return ApplyAnalyticalSquircleToPbgra32(content, mode != "raw");
            }
        }

        private static void BleedOpaqueEdges(Bitmap bmp)
        {
            int w = bmp.Width;
            int h = bmp.Height;
            int count = w * h;
            BitmapData data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                int stride = data.Stride;
                int totalBytes = Math.Abs(stride) * h;
                byte[] buf = new byte[totalBytes];
                Marshal.Copy(data.Scan0, buf, 0, totalBytes);

                byte maxAlpha = 0;
                for (int y = 0; y < h; y++)
                {
                    int row = y * stride;
                    for (int x = 0; x < w; x++)
                    {
                        byte a = buf[row + x * 4 + 3];
                        if (a > maxAlpha) maxAlpha = a;
                    }
                }
                if (maxAlpha < 40) return;

                byte solidThreshold = (byte)Math.Min(242, Math.Max(180, maxAlpha - 8));
                byte[] solid = new byte[count];
                int nonSolidCount = 0;

                for (int y = 0; y < h; y++)
                {
                    int row = y * stride;
                    int pRow = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        byte a = buf[row + x * 4 + 3];
                        if (a >= solidThreshold)
                        {
                            solid[pRow + x] = 1;
                        }
                        else
                        {
                            nonSolidCount++;
                        }
                    }
                }

                if (nonSolidCount > 0 && nonSolidCount < count)
                {
                    // Erode 2px inward strictly along existing transparency boundaries (never along opaque image borders)
                    // so fuzzy pre-rounded icon fringes (e.g. AliMeeting / WeMeeting) are replaced with pure interior color.
                    for (int pass = 0; pass < 2; pass++)
                    {
                        byte[] nextSolid = (byte[])solid.Clone();
                        int eroded = 0;
                        for (int y = 0; y < h; y++)
                        {
                            int pRow = y * w;
                            for (int x = 0; x < w; x++)
                            {
                                int p = pRow + x;
                                if (solid[p] == 0) continue;
                                bool adjTrans =
                                    (x > 0 && solid[p - 1] == 0) ||
                                    (x + 1 < w && solid[p + 1] == 0) ||
                                    (y > 0 && solid[p - w] == 0) ||
                                    (y + 1 < h && solid[p + w] == 0);
                                if (adjTrans)
                                {
                                    nextSolid[p] = 0;
                                    eroded++;
                                }
                            }
                        }
                        if (eroded == 0 || nonSolidCount + eroded >= count) break;
                        solid = nextSolid;
                        nonSolidCount += eroded;
                    }

                    // Multi-source BFS outward color extrapolation from clean solid boundary pixels
                    int[] queue = new int[count];
                    int head = 0;
                    int tail = 0;

                    for (int y = 0; y < h; y++)
                    {
                        int pRow = y * w;
                        for (int x = 0; x < w; x++)
                        {
                            int p = pRow + x;
                            if (solid[p] == 0) continue;
                            bool adjEmpty =
                                (x > 0 && solid[p - 1] == 0) ||
                                (x + 1 < w && solid[p + 1] == 0) ||
                                (y > 0 && solid[p - w] == 0) ||
                                (y + 1 < h && solid[p + w] == 0);
                            if (adjEmpty)
                            {
                                queue[tail++] = p;
                            }
                        }
                    }

                    while (head < tail)
                    {
                        int cur = queue[head++];
                        int cx = cur % w;
                        int cy = cur / w;
                        int cIdx = cy * stride + cx * 4;
                        byte cb = buf[cIdx];
                        byte cg = buf[cIdx + 1];
                        byte cr = buf[cIdx + 2];

                        if (cx > 0)
                        {
                            int np = cur - 1;
                            if (solid[np] == 0)
                            {
                                solid[np] = 1;
                                int nIdx = cy * stride + (cx - 1) * 4;
                                buf[nIdx] = cb; buf[nIdx + 1] = cg; buf[nIdx + 2] = cr; buf[nIdx + 3] = 255;
                                queue[tail++] = np;
                            }
                        }
                        if (cx + 1 < w)
                        {
                            int np = cur + 1;
                            if (solid[np] == 0)
                            {
                                solid[np] = 1;
                                int nIdx = cy * stride + (cx + 1) * 4;
                                buf[nIdx] = cb; buf[nIdx + 1] = cg; buf[nIdx + 2] = cr; buf[nIdx + 3] = 255;
                                queue[tail++] = np;
                            }
                        }
                        if (cy > 0)
                        {
                            int np = cur - w;
                            if (solid[np] == 0)
                            {
                                solid[np] = 1;
                                int nIdx = (cy - 1) * stride + cx * 4;
                                buf[nIdx] = cb; buf[nIdx + 1] = cg; buf[nIdx + 2] = cr; buf[nIdx + 3] = 255;
                                queue[tail++] = np;
                            }
                        }
                        if (cy + 1 < h)
                        {
                            int np = cur + w;
                            if (solid[np] == 0)
                            {
                                solid[np] = 1;
                                int nIdx = (cy + 1) * stride + cx * 4;
                                buf[nIdx] = cb; buf[nIdx + 1] = cg; buf[nIdx + 2] = cr; buf[nIdx + 3] = 255;
                                queue[tail++] = np;
                            }
                        }
                    }
                }

                // Ensure full-bleed canvas is 100% opaque before analytical squircle masking
                for (int y = 0; y < h; y++)
                {
                    int row = y * stride;
                    for (int x = 0; x < w; x++)
                    {
                        buf[row + x * 4 + 3] = 255;
                    }
                }

                Marshal.Copy(buf, 0, data.Scan0, totalBytes);
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }

        private static BitmapSource ApplyAnalyticalSquircleToPbgra32(Bitmap content, bool applyAdaptiveDarkRim)
        {
            int w = content.Width;
            int h = content.Height;
            int outStride = w * 4;
            byte[] outBuf = new byte[outStride * h];

            // Continuous Apple/ColorOS superellipse squircle (|u|^4.6 + |v|^4.6 = 1)
            // Gradient-normalized SDF provides exact Euclidean pixel distance on both flat sides and 45-deg corners.
            const double Exponent = 4.6;
            const double ExpMinus1 = Exponent - 1.0;
            const double AaHalfWidth = 0.65; // 1.3px C1 smoothstep at 512px (~0.25% edge feather)
            double cx = (w - 1) * 0.5;
            double cy = (h - 1) * 0.5;
            double radius = (Math.Min(w, h) - 3.0) * 0.5;
            double invR = 1.0 / radius;
            double invH = 1.0 / Math.Max(1, h - 1);

            BitmapData data = content.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int srcStride = data.Stride;
                byte[] srcBuf = new byte[Math.Abs(srcStride) * h];
                Marshal.Copy(data.Scan0, srcBuf, 0, srcBuf.Length);

                for (int y = 0; y < h; y++)
                {
                    int sRow = y * srcStride;
                    int dRow = y * outStride;
                    double av = Math.Abs(y - cy) * invR;
                    double vn1 = (av > 1e-9) ? Math.Pow(av, ExpMinus1) : 0.0;
                    double vn = vn1 * av;
                    double vn1Sq = vn1 * vn1;
                    double vertFade = 0.16 - 0.10 * (y * invH);

                    for (int x = 0; x < w; x++)
                    {
                        double au = Math.Abs(x - cx) * invR;
                        double distPx;
                        if (au < 0.72 && av < 0.72)
                        {
                            distPx = -50.0;
                        }
                        else
                        {
                            double un1 = (au > 1e-9) ? Math.Pow(au, ExpMinus1) : 0.0;
                            double un = un1 * au;
                            double f = un + vn - 1.0;
                            double grad = (Exponent * invR) * Math.Sqrt(un1 * un1 + vn1Sq);
                            distPx = (grad > 1e-9) ? (f / grad) : -radius;
                        }

                        if (distPx >= AaHalfWidth)
                        {
                            // Strictly outside squircle -> exact transparent zero (no LightGray fringe!)
                            continue;
                        }

                        double cov;
                        if (distPx <= -AaHalfWidth)
                        {
                            cov = 1.0;
                        }
                        else
                        {
                            double t = (AaHalfWidth - distPx) / (2.0 * AaHalfWidth);
                            cov = t * t * (3.0 - 2.0 * t);
                        }

                        int sIdx = sRow + x * 4;
                        double b = srcBuf[sIdx];
                        double g = srcBuf[sIdx + 1];
                        double r = srcBuf[sIdx + 2];
                        int srcA = applyAdaptiveDarkRim ? 255 : srcBuf[sIdx + 3];

                        // Subtle studio inner rim ONLY strictly inside the opaque squircle AND only on dark icons,
                        // so dark tiles separate from dark frosted cards while colored/photo icons have zero white halo.
                        if (applyAdaptiveDarkRim && distPx < -0.9 && distPx > -4.3)
                        {
                            double rd = Math.Abs(distPx - (-2.5)) / 1.6;
                            if (rd < 1.0)
                            {
                                double lum = (0.299 * r + 0.587 * g + 0.114 * b) / 255.0;
                                if (lum < 0.36)
                                {
                                    double rimBell = 0.5 * (1.0 + Math.Cos(rd * Math.PI));
                                    double darkness = (0.36 - lum) / 0.36;
                                    double blend = rimBell * darkness * vertFade;
                                    r += (255.0 - r) * blend;
                                    g += (255.0 - g) * blend;
                                    b += (255.0 - b) * blend;
                                }
                            }
                        }

                        int aOut = (int)Math.Round(srcA * cov);
                        if (aOut <= 0) continue;
                        if (aOut > 255) aOut = 255;

                        int bInt = (int)Math.Round(b);
                        int gInt = (int)Math.Round(g);
                        int rInt = (int)Math.Round(r);
                        if (bInt > 255) bInt = 255;
                        if (gInt > 255) gInt = 255;
                        if (rInt > 255) rInt = 255;

                        int dIdx = dRow + x * 4;
                        if (aOut == 255)
                        {
                            outBuf[dIdx] = (byte)bInt;
                            outBuf[dIdx + 1] = (byte)gInt;
                            outBuf[dIdx + 2] = (byte)rInt;
                            outBuf[dIdx + 3] = 255;
                        }
                        else
                        {
                            // Exact premultiplied Pbgra32 (WPF native GPU texture format)
                            outBuf[dIdx] = (byte)((bInt * aOut + 127) / 255);
                            outBuf[dIdx + 1] = (byte)((gInt * aOut + 127) / 255);
                            outBuf[dIdx + 2] = (byte)((rInt * aOut + 127) / 255);
                            outBuf[dIdx + 3] = (byte)aOut;
                        }
                    }
                }
            }
            finally
            {
                content.UnlockBits(data);
            }

            BitmapSource bs = BitmapSource.Create(
                w, h, 96.0, 96.0,
                System.Windows.Media.PixelFormats.Pbgra32,
                null, outBuf, outStride);
            bs.Freeze();
            return bs;
        }

        private static void SetupHighQuality(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        }

        private static void DrawFitted(Graphics g, Bitmap src, Rectangle srcRect, RectangleF dstBox)
        {
            float scale = Math.Min(dstBox.Width / Math.Max(1, srcRect.Width), dstBox.Height / Math.Max(1, srcRect.Height));
            float w = srcRect.Width * scale;
            float h = srcRect.Height * scale;
            float x = dstBox.X + (dstBox.Width - w) * 0.5f;
            float y = dstBox.Y + (dstBox.Height - h) * 0.5f;
            g.DrawImage(src, new RectangleF(x, y, w, h), srcRect, GraphicsUnit.Pixel);
        }

        private static Rectangle GetOpaqueBounds(Bitmap bmp, byte alphaThreshold)
        {
            int w = bmp.Width;
            int h = bmp.Height;
            BitmapData data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int bytes = Math.Abs(data.Stride) * h;
                byte[] buf = new byte[bytes];
                Marshal.Copy(data.Scan0, buf, 0, bytes);

                int minX = w, minY = h, maxX = -1, maxY = -1;
                for (int y = 0; y < h; y++)
                {
                    int row = y * data.Stride;
                    for (int x = 0; x < w; x++)
                    {
                        byte a = buf[row + x * 4 + 3];
                        if (a > alphaThreshold)
                        {
                            if (x < minX) minX = x;
                            if (x > maxX) maxX = x;
                            if (y < minY) minY = y;
                            if (y > maxY) maxY = y;
                        }
                    }
                }
                if (maxX < minX || maxY < minY) return new Rectangle(0, 0, w, h);
                return new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1);
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }

        private static void AnalyzeGlyph(
            Bitmap bmp, Rectangle bounds,
            out double fillRatio, out bool cornersFilled, out double avgLuminance, out double brightPixelRatio, out double avgSaturation)
        {
            int w = bmp.Width;
            int h = bmp.Height;
            BitmapData data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int bytes = Math.Abs(data.Stride) * h;
                byte[] buf = new byte[bytes];
                Marshal.Copy(data.Scan0, buf, 0, bytes);

                long opaqueCount = 0;
                long brightCount = 0;
                double lumSum = 0;
                double satSum = 0;
                long total = (long)bounds.Width * bounds.Height;

                for (int y = bounds.Top; y < bounds.Bottom; y++)
                {
                    int row = y * data.Stride;
                    for (int x = bounds.Left; x < bounds.Right; x++)
                    {
                        int idx = row + x * 4;
                        byte a = buf[idx + 3];
                        if (a > 40)
                        {
                            opaqueCount++;
                            double rC = buf[idx + 2] / 255.0;
                            double gC = buf[idx + 1] / 255.0;
                            double bC = buf[idx] / 255.0;
                            double lum = 0.299 * rC + 0.587 * gC + 0.114 * bC;
                            double maxC = Math.Max(rC, Math.Max(gC, bC));
                            double minC = Math.Min(rC, Math.Min(gC, bC));
                            double sat = maxC > 0.001 ? (maxC - minC) / maxC : 0.0;
                            lumSum += lum;
                            satSum += sat;
                            if (lum > 0.78) brightCount++;
                        }
                    }
                }

                fillRatio = total > 0 ? (double)opaqueCount / total : 0;
                avgLuminance = opaqueCount > 0 ? lumSum / opaqueCount : 0.5;
                brightPixelRatio = opaqueCount > 0 ? (double)brightCount / opaqueCount : 0;
                avgSaturation = opaqueCount > 0 ? satSum / opaqueCount : 0.0;

                // Sample 8 perimeter points (4 corners at 9% inset + 4 edge midpoints at 4% inset)
                // Ensures notched folders (WizTree) or tabbed notebooks (Zotero) get clean tile plates
                int ix = Math.Max(1, (int)(bounds.Width * 0.09));
                int iy = Math.Max(1, (int)(bounds.Height * 0.09));
                int ex = Math.Max(1, (int)(bounds.Width * 0.04));
                int ey = Math.Max(1, (int)(bounds.Height * 0.04));
                int midX = bounds.Left + bounds.Width / 2;
                int midY = bounds.Top + bounds.Height / 2;
                int[,] pts = new int[,]
                {
                    { bounds.Left + ix, bounds.Top + iy },
                    { bounds.Right - 1 - ix, bounds.Top + iy },
                    { bounds.Left + ix, bounds.Bottom - 1 - iy },
                    { bounds.Right - 1 - ix, bounds.Bottom - 1 - iy },
                    { midX, bounds.Top + ey },
                    { midX, bounds.Bottom - 1 - ey },
                    { bounds.Left + ex, midY },
                    { bounds.Right - 1 - ex, midY }
                };
                int filledPts = 0;
                for (int i = 0; i < 8; i++)
                {
                    int px = Math.Max(0, Math.Min(w - 1, pts[i, 0]));
                    int py = Math.Max(0, Math.Min(h - 1, pts[i, 1]));
                    if (buf[py * data.Stride + px * 4 + 3] > 210) filledPts++;
                }
                cornersFilled = (filledPts == 8);
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }

        private static Bitmap CreateFallbackBadge(string label)
        {
            Bitmap bmp = new Bitmap(256, 256, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                SetupHighQuality(g);
                using (LinearGradientBrush bg = new LinearGradientBrush(
                    new PointF(0, 0), new PointF(256, 256),
                    Color.FromArgb(255, 59, 130, 246),
                    Color.FromArgb(255, 37, 99, 235)))
                {
                    g.FillRectangle(bg, 0, 0, 256, 256);
                }

                string ch = string.IsNullOrEmpty(label) ? "?" : label.Substring(0, 1).ToUpperInvariant();
                using (Font font = new Font("Microsoft YaHei UI", 96, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel))
                using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    g.DrawString(ch, font, Brushes.White, new RectangleF(0, 0, 256, 256), sf);
                }
            }
            return bmp;
        }
    }
}
