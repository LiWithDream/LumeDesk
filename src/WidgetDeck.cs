using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace LumeDesk
{
    public class WidgetDeck
    {
        private readonly AppConfig _config;
        private readonly ClashService _clash;
        private readonly MediaLyricsService _media;
        private readonly ImapMailService _mail;
        private readonly SystemStatsService _sys;

        private readonly Action _onOpenMailApp;
        private readonly Action _onConfigureMail;
        private readonly Action _onOpenClashApp;
        private readonly Action _onOpenMusicApp;
        private readonly Action _onAddTodo;
        private readonly Action _onTodosChanged;
        private readonly Func<ContextMenu> _onBuildWidgetContextMenu;
        private readonly Func<ContextMenu> _onCreateDarkMenu;
        private readonly Func<string, Action, MenuItem> _onCreateDarkMenuItem;
        private readonly Style _thinScrollBarStyle;
        private readonly Func<int> _getAnimFps;

        private int AnimFps
        {
            get
            {
                if (_getAnimFps != null)
                {
                    int fps = _getAnimFps();
                    if (fps >= 60 && fps <= 240) return fps;
                }
                return 60;
            }
        }

        // Live UI element refs updated in-place (ZERO visual tree rebuilds!)
        // Clash widget refs
        private TextBlock _clashProfileText;
        private Ellipse _clashStatusDot;
        private Ellipse _clashStatusGlow;
        private Border _clashNodePill;
        private Border _clashRegionBadge;
        private TextBlock _clashRegionText;
        private TextBlock _clashNodeText;
        private TextBlock _clashDelayText;
        private Border _clashDelayPill;
        private Border _clashTunPill;
        private Border _clashModeRuleBtn;
        private Border _clashModeGlobalBtn;
        private Border _clashModeDirectBtn;
        private TextBlock _clashDownText;
        private TextBlock _clashUpText;
        private TextBlock _clashSessionText;
        private TextBlock _clashPeakLabel;
        private Polyline _clashDownLine;
        private Polygon _clashDownArea;
        private Polyline _clashUpLine;
        private Polygon _clashUpArea;
        private Ellipse _clashDownDot;
        private double _sparkW;
        private double _sparkH;
        private TextBlock _clashSubLeftText;
        private TextBlock _clashSubExpireText;
        private Border _clashSubBarFill;
        private double _clashSubBarMaxW;
        private double _clashScaleU = 1.0;

        // Mail widget refs
        private Border _mailUnreadPill;
        private TextBlock _mailUnreadText;
        private TextBlock _mailStatusText;
        private Border _mailListWell;
        private ScrollViewer _mailScrollViewer;
        private StackPanel _mailListHost;
        private FrameworkElement _mailSetupView;
        private string _lastMailRenderSig = "";
        private double _mailScaleU = 1.0;

        // Music widget refs
        private Border _musicCoverArtBorder;
        private Border _musicCoverFallback;
        private TextBlock _musicTitleText;
        private TextBlock _musicArtistText;
        private TextBlock _musicAppBadgeText;
        private Border _musicAppBadge;
        private Path _musicPlayPath;
        private bool _lastMusicPlayingState = false;
        private TextBlock _musicPrevLyric;
        private Border _musicCurLyricPill;
        private TextBlock _musicCurLyric;
        private TranslateTransform _musicCurLyricSlide;
        private TextBlock _musicNextLyric;
        private string _lastDisplayedLyric = "";
        private TextBlock _musicTimeLeft;
        private TextBlock _musicTimeRight;
        private Border _musicProgFill;
        private double _musicProgMaxW;
        private double _musicScaleU = 1.0;
        private System.Windows.Media.Imaging.BitmapSource _lastCoverRef;

        // System + Todo widget refs
        private Border _sysCpuBox;
        private TextBlock _sysCpuText;
        private Border _sysCpuBar;
        private Border _sysRamBox;
        private ToolTip _sysRamTip;
        private string _lastRamTipText = "";
        private TextBlock _sysRamText;
        private Border _sysRamBar;
        private Border _sysDiskBox;
        private ToolTip _sysDiskTip;
        private string _lastDiskTipText = "";
        private TextBlock _sysDiskText;
        private Border _sysDiskBar;
        private double _sysMiniBarMaxW;
        private Border _todoCountPill;
        private TextBlock _todoCountText;
        private Border _todoClearDoneBtn;
        private StackPanel _todoListHost;
        private double _todoScaleU = 1.0;

        // Typography (standard Segoe UI avoids .NET 4.8 WPF ClearType speckling on OpenType Variable fonts!)
        private static readonly FontFamily FontDisplay = new FontFamily("Segoe UI, Microsoft YaHei UI");
        private static readonly FontFamily FontText = new FontFamily("Segoe UI, Microsoft YaHei UI");

        // Shared frozen vector geometries
        private static readonly Geometry GeoEnvelope = FreezeGeo("M 1,3 L 15,3 A 1,1 0 0 1 16,4 L 16,12 A 1,1 0 0 1 15,13 L 1,13 A 1,1 0 0 1 0,12 L 0,4 A 1,1 0 0 1 1,3 Z M 1,4 L 8,9 L 15,4");
        private static readonly Geometry GeoRefresh = FreezeGeo("M 13.5,8 A 5.5,5.5 0 1 1 11.8,4.0 M 12.2,1.6 L 12.4,4.6 L 9.4,4.4");
        private static readonly Geometry GeoPrev = FreezeGeo("M 2,2 L 4,2 L 4,14 L 2,14 Z M 13,2.3 L 5.2,8 L 13,13.7 Z");
        private static readonly Geometry GeoNext = FreezeGeo("M 12,2 L 14,2 L 14,14 L 12,14 Z M 3,2.3 L 10.8,8 L 3,13.7 Z");
        private static readonly Geometry GeoPlay = FreezeGeo("M 4.5,2.2 L 14.2,8 L 4.5,13.8 Z");
        private static readonly Geometry GeoPause = FreezeGeo("M 3.5,2.5 L 6.6,2.5 L 6.6,13.5 L 3.5,13.5 Z M 9.4,2.5 L 12.5,2.5 L 12.5,13.5 L 9.4,13.5 Z");
        private static readonly Geometry GeoNote = FreezeGeo("M 7,13.2 A 2.4,2.1 0 1 1 4.6,11.1 A 2.4,2.1 0 0 1 7,13.2 Z M 7,12.2 L 7,3.6 L 14.5,2.2 L 14.5,10.5 M 14.5,11.5 A 2.4,2.1 0 1 1 12.1,9.4 A 2.4,2.1 0 0 1 14.5,11.5 Z");
        private static readonly Geometry GeoCheckCircle = FreezeGeo("M 8,1 A 7,7 0 1 0 8,15 A 7,7 0 1 0 8,1 Z M 4.8,8.1 L 7.1,10.4 L 11.4,5.6");
        private static readonly Geometry GeoCheckMark = FreezeGeo("M 2.5,7.2 L 5.8,10.5 L 12.5,3.5");
        private static readonly Geometry GeoCloseX = FreezeGeo("M 2.5,2.5 L 11.5,11.5 M 11.5,2.5 L 2.5,11.5");

        private static readonly CubicEase EaseOutCubic = FreezeEase();
        private static readonly Dictionary<string, DropShadowEffect> ShadowEffectCache = new Dictionary<string, DropShadowEffect>();

        // Frozen brushes
        private static readonly SolidColorBrush BrWhite = FreezeBrush(255, 255, 255, 255);
        private static readonly SolidColorBrush BrTextPrimary = FreezeBrush(248, 246, 250, 255);
        private static readonly SolidColorBrush BrTextMuted = FreezeBrush(196, 210, 222, 240);
        private static readonly SolidColorBrush BrTextDim = FreezeBrush(158, 192, 208, 228);
        private static readonly SolidColorBrush BrCyan = FreezeBrush(255, 56, 189, 248);
        private static readonly SolidColorBrush BrSkySoft = FreezeBrush(255, 125, 211, 252);
        private static readonly SolidColorBrush BrEmerald = FreezeBrush(255, 52, 211, 153);
        private static readonly SolidColorBrush BrViolet = FreezeBrush(255, 167, 139, 250);
        private static readonly SolidColorBrush BrAmber = FreezeBrush(255, 251, 191, 36);
        private static readonly SolidColorBrush BrCoral = FreezeBrush(255, 248, 113, 113);
        private static readonly SolidColorBrush BrModeActiveBg = FreezeBrush(228, 59, 130, 246);
        private static readonly SolidColorBrush BrModeHoverBg = FreezeBrush(34, 255, 255, 255);
        private static readonly SolidColorBrush BrPillBg = FreezeBrush(30, 255, 255, 255);
        private static readonly SolidColorBrush BrPillHover = FreezeBrush(58, 255, 255, 255);
        private static readonly SolidColorBrush BrPillBorder = FreezeBrush(38, 255, 255, 255);
        private static readonly SolidColorBrush BrInnerWell = FreezeBrush(44, 6, 12, 24);
        private static readonly SolidColorBrush BrInnerWellHover = FreezeBrush(66, 12, 22, 40);
        private static readonly SolidColorBrush BrInnerWellBorder = FreezeBrush(26, 255, 255, 255);
        private static readonly SolidColorBrush BrInnerWellBorderHover = FreezeBrush(64, 255, 255, 255);
        private static readonly SolidColorBrush BrRowHover = FreezeBrush(30, 255, 255, 255);
        private static readonly SolidColorBrush BrRowDivider = FreezeBrush(18, 255, 255, 255);
        private static readonly SolidColorBrush BrLyricActiveBg = FreezeBrush(26, 125, 211, 252);
        private static readonly SolidColorBrush BrLyricActiveBorder = FreezeBrush(44, 125, 211, 252);
        private static readonly SolidColorBrush BrTrackBg = FreezeBrush(36, 255, 255, 255);
        private static readonly SolidColorBrush BrGlowConnected = FreezeBrush(58, 52, 211, 153);
        private static readonly SolidColorBrush BrGlowOffline = FreezeBrush(52, 251, 191, 36);
        private static readonly SolidColorBrush BrDelayFastBg = FreezeBrush(42, 52, 211, 153);
        private static readonly SolidColorBrush BrDelayMedBg = FreezeBrush(44, 251, 191, 36);
        private static readonly SolidColorBrush BrDelaySlowBg = FreezeBrush(46, 248, 113, 113);
        private static readonly SolidColorBrush BrDelIdleBg = FreezeBrush(18, 255, 255, 255);
        private static readonly SolidColorBrush BrDelIdleBorder = FreezeBrush(30, 255, 255, 255);
        private static readonly SolidColorBrush BrDelHoverBg = FreezeBrush(56, 248, 113, 113);
        private static readonly SolidColorBrush BrDelHoverBorder = FreezeBrush(110, 248, 113, 113);
        private static readonly SolidColorBrush BrMailUnreadBg = FreezeBrush(204, 225, 29, 72);
        private static readonly SolidColorBrush BrMailUnreadBorder = FreezeBrush(130, 253, 164, 175);
        private static readonly SolidColorBrush BrNodeSelectedRowBg = FreezeBrush(38, 56, 189, 248);
        private static readonly SolidColorBrush BrNodeSelectedRowBorder = FreezeBrush(76, 125, 211, 252);

        private static DropShadowEffect GetFrozenShadowEffect(Color color, double blurRadius, double shadowDepth, double direction, double opacity)
        {
            string key = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0:X2}{1:X2}{2:X2}|{3:F1}|{4:F1}|{5:F0}|{6:F2}",
                color.R, color.G, color.B, blurRadius, shadowDepth, direction, opacity);
            lock (ShadowEffectCache)
            {
                DropShadowEffect cached;
                if (ShadowEffectCache.TryGetValue(key, out cached))
                {
                    return cached;
                }
                DropShadowEffect eff = new DropShadowEffect
                {
                    Color = color,
                    BlurRadius = blurRadius,
                    ShadowDepth = shadowDepth,
                    Direction = direction,
                    Opacity = opacity
                };
                eff.Freeze();
                ShadowEffectCache[key] = eff;
                return eff;
            }
        }

        private static Geometry FreezeGeo(string data)
        {
            Geometry g = Geometry.Parse(data);
            g.Freeze();
            return g;
        }

        private static CubicEase FreezeEase()
        {
            CubicEase e = new CubicEase { EasingMode = EasingMode.EaseOut };
            e.Freeze();
            return e;
        }

        private static SolidColorBrush FreezeBrush(byte a, byte r, byte g, byte b)
        {
            SolidColorBrush br = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            br.Freeze();
            return br;
        }

        private static LinearGradientBrush FreezeGrad(Color c1, Color c2, double x2, double y2)
        {
            LinearGradientBrush br = new LinearGradientBrush(c1, c2, new Point(0, 0), new Point(x2, y2));
            br.Freeze();
            return br;
        }

        public WidgetDeck(
            AppConfig config,
            ClashService clash,
            MediaLyricsService media,
            ImapMailService mail,
            SystemStatsService sys,
            Action onOpenMailApp,
            Action onConfigureMail,
            Action onOpenClashApp,
            Action onOpenMusicApp,
            Action onAddTodo,
            Action onTodosChanged,
            Func<ContextMenu> onBuildWidgetContextMenu,
            Func<ContextMenu> onCreateDarkMenu,
            Func<string, Action, MenuItem> onCreateDarkMenuItem,
            Style thinScrollBarStyle,
            Func<int> getAnimFps = null)
        {
            _config = config;
            _clash = clash;
            _media = media;
            _mail = mail;
            _sys = sys;
            _onOpenMailApp = onOpenMailApp;
            _onConfigureMail = onConfigureMail;
            _onOpenClashApp = onOpenClashApp;
            _onOpenMusicApp = onOpenMusicApp;
            _onAddTodo = onAddTodo;
            _onTodosChanged = onTodosChanged;
            _onBuildWidgetContextMenu = onBuildWidgetContextMenu;
            _onCreateDarkMenu = onCreateDarkMenu;
            _onCreateDarkMenuItem = onCreateDarkMenuItem;
            _thinScrollBarStyle = thinScrollBarStyle;
            _getAnimFps = getAnimFps;
        }

        public void ResetRefs()
        {
            _clashProfileText = null;
            _clashStatusDot = null;
            _clashStatusGlow = null;
            _clashNodePill = null;
            _clashRegionBadge = null;
            _clashRegionText = null;
            _clashPeakLabel = null;
            _clashDownDot = null;
            _clashUpArea = null;
            _mailListWell = null;
            _mailScrollViewer = null;
            _mailListHost = null;
            _musicCoverArtBorder = null;
            _musicTitleText = null;
            _musicPlayPath = null;
            _lastMusicPlayingState = false;
            _musicCurLyricPill = null;
            _musicCurLyricSlide = null;
            _lastDisplayedLyric = "";
            _sysCpuText = null;
            _sysRamTip = null;
            _sysDiskTip = null;
            _lastRamTipText = "";
            _lastDiskTipText = "";
            _todoListHost = null;
            _todoCountPill = null;
            _todoClearDoneBtn = null;
            _lastMailRenderSig = "";
            _lastCoverRef = null;
        }

        public void RenderIntoCanvas(
            Canvas canvas,
            WallpaperBundle wallpaper,
            double zoneLeft,
            double zoneTop,
            double zoneW,
            double zoneH,
            double canvasW,
            double canvasH,
            double u)
        {
            RenderIntoCanvas(canvas, wallpaper, zoneLeft, zoneTop, zoneW, zoneH, canvasW, canvasH, u, 2, false);
        }

        public void RenderIntoCanvas(
            Canvas canvas,
            WallpaperBundle wallpaper,
            double zoneLeft,
            double zoneTop,
            double zoneW,
            double zoneH,
            double canvasW,
            double canvasH,
            double u,
            int forceCols,
            bool alignToZoneBounds)
        {
            ResetRefs();
            if (!_config.ShowWidgets || _config.EnabledWidgets == null || _config.EnabledWidgets.Count == 0)
            {
                return;
            }

            List<string> active = new List<string>();
            for (int i = 0; i < _config.EnabledWidgets.Count; i++)
            {
                string id = (_config.EnabledWidgets[i] ?? "").ToLowerInvariant();
                if (id == "mail" || id == "clash" || id == "music" || id == "sys_todo")
                {
                    if (!active.Contains(id)) active.Add(id);
                }
            }
            if (active.Count == 0 || zoneH < 120 * u) return;

            int widgetCols = (forceCols <= 1) ? 1 : 2;
            double colGap = Math.Round(22 * u);
            double rowGap = Math.Round(20 * u);
            int widgetRows = (int)Math.Ceiling((double)active.Count / widgetCols);
            double rowH = Math.Floor((zoneH - rowGap * (widgetRows - 1)) / widgetRows);
            double maxRowH = Math.Round((alignToZoneBounds ? 430 : (widgetCols == 1 ? 370 : 355)) * u);
            if (rowH > maxRowH) rowH = maxRowH;
            if (rowH < 110 * u) return;

            // Align pixel-for-pixel to zoneTop when harmonizing with landscape folder grid, else center cleanly
            double actualDeckH = widgetRows * rowH + (widgetRows - 1) * rowGap;
            double startY = alignToZoneBounds
                ? Math.Round(zoneTop + Math.Max(0, (zoneH - actualDeckH) * 0.5))
                : Math.Round(zoneTop + (zoneH - actualDeckH) * 0.48);

            for (int i = 0; i < active.Count; i++)
            {
                int r = i / widgetCols;
                int c = i % widgetCols;
                bool isLastAlone = (widgetCols == 2) && (i == active.Count - 1) && (active.Count % 2 == 1);
                double w = (widgetCols == 1 || isLastAlone) ? zoneW : Math.Floor((zoneW - colGap) * 0.5);
                double x = (widgetCols == 1 || isLastAlone) ? zoneLeft : (zoneLeft + c * (w + colGap));
                double y = startY + r * (rowH + rowGap);

                UIElement widgetCard = BuildWidgetShell(active[i], wallpaper, x, y, w, rowH, canvasW, canvasH, u);
                if (widgetCard != null)
                {
                    Canvas.SetLeft(widgetCard, x);
                    Canvas.SetTop(widgetCard, y);
                    canvas.Children.Add(widgetCard);
                }
            }

            UpdateLiveData();
        }

        private UIElement BuildWidgetShell(
            string widgetId,
            WallpaperBundle wallpaper,
            double x, double y, double w, double h,
            double canvasW, double canvasH, double u)
        {
            double radius = Math.Round(32 * u);
            double borderW = Math.Max(1.0, Math.Round(1.2 * u * 10) / 10.0);
            double baseOpacity = Math.Max(0.28, Math.Min(0.92, _config.CardTintOpacity));
            byte topA = (byte)Math.Round(Math.Min(0.90, baseOpacity + 0.03) * 255);
            byte botA = (byte)Math.Round(Math.Min(0.94, baseOpacity + 0.12) * 255);

            LinearGradientBrush rim = new LinearGradientBrush();
            rim.StartPoint = new Point(0, 0);
            rim.EndPoint = new Point(0, 1);
            rim.GradientStops.Add(new GradientStop(Color.FromArgb(105, 255, 255, 255), 0.0));
            rim.GradientStops.Add(new GradientStop(Color.FromArgb(34, 255, 255, 255), 0.45));
            rim.GradientStops.Add(new GradientStop(Color.FromArgb(20, 255, 255, 255), 1.0));
            rim.Freeze();

            LinearGradientBrush rimHover = new LinearGradientBrush();
            rimHover.StartPoint = new Point(0, 0);
            rimHover.EndPoint = new Point(0, 1);
            rimHover.GradientStops.Add(new GradientStop(Color.FromArgb(148, 255, 255, 255), 0.0));
            rimHover.GradientStops.Add(new GradientStop(Color.FromArgb(56, 255, 255, 255), 0.5));
            rimHover.GradientStops.Add(new GradientStop(Color.FromArgb(32, 255, 255, 255), 1.0));
            rimHover.Freeze();

            Grid root = new Grid();
            root.Width = w;
            root.Height = h;

            // Sibling cached Gaussian shadow plate (soft visionOS elevation)
            Border shadowPlate = new Border();
            shadowPlate.CornerRadius = new CornerRadius(radius);
            shadowPlate.Background = FreezeBrush(150, 5, 9, 16);
            shadowPlate.IsHitTestVisible = false;
            shadowPlate.CacheMode = new BitmapCache();
            shadowPlate.Effect = GetFrozenShadowEffect(Colors.Black, Math.Round(30 * u), Math.Round(8 * u), 270, 0.38);
            root.Children.Add(shadowPlate);

            // Pixel-aligned blurred wallpaper glass layer
            Border blurLayer = new Border();
            blurLayer.CornerRadius = new CornerRadius(radius);
            blurLayer.IsHitTestVisible = false;
            if (wallpaper != null && wallpaper.BlurredBackdrop != null)
            {
                ImageBrush ib = new ImageBrush(wallpaper.BlurredBackdrop);
                ib.ViewboxUnits = BrushMappingMode.RelativeToBoundingBox;
                ib.Viewbox = new Rect(
                    Math.Max(0, Math.Min(0.99, x / canvasW)),
                    Math.Max(0, Math.Min(0.99, y / canvasH)),
                    Math.Max(0.01, Math.Min(1.0 - x / canvasW, w / canvasW)),
                    Math.Max(0.01, Math.Min(1.0 - y / canvasH, h / canvasH)));
                ib.Stretch = Stretch.Fill;
                ib.Freeze();
                blurLayer.Background = ib;
            }
            root.Children.Add(blurLayer);

            LinearGradientBrush tint = new LinearGradientBrush();
            tint.StartPoint = new Point(0, 0);
            tint.EndPoint = new Point(0, 1);
            tint.GradientStops.Add(new GradientStop(Color.FromArgb(topA, 15, 22, 38), 0.0));
            tint.GradientStops.Add(new GradientStop(Color.FromArgb(botA, 9, 14, 26), 1.0));
            tint.Freeze();

            LinearGradientBrush sheen = new LinearGradientBrush();
            sheen.StartPoint = new Point(0.1, 0);
            sheen.EndPoint = new Point(0.9, 0.85);
            sheen.GradientStops.Add(new GradientStop(Color.FromArgb(24, 255, 255, 255), 0.0));
            sheen.GradientStops.Add(new GradientStop(Color.FromArgb(6, 255, 255, 255), 0.35));
            sheen.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 1.0));
            sheen.Freeze();

            Border outer = new Border();
            outer.CornerRadius = new CornerRadius(radius);
            outer.Background = tint;
            outer.BorderThickness = new Thickness(borderW);
            outer.BorderBrush = rim;

            Border sheenLayer = new Border();
            sheenLayer.CornerRadius = new CornerRadius(Math.Max(1, radius - borderW));
            sheenLayer.Background = sheen;
            sheenLayer.Padding = new Thickness(Math.Round(22 * u), Math.Round(18 * u), Math.Round(22 * u), Math.Round(18 * u));

            double innerW = w - Math.Round(44 * u) - borderW * 2;
            double innerH = h - Math.Round(36 * u) - borderW * 2;

            UIElement content = null;
            if (widgetId == "mail") content = BuildMailContent(innerW, innerH, u);
            else if (widgetId == "clash") content = BuildClashContent(innerW, innerH, u);
            else if (widgetId == "music") content = BuildMusicContent(innerW, innerH, u);
            else if (widgetId == "sys_todo") content = BuildSysTodoContent(innerW, innerH, u);

            sheenLayer.Child = content;
            outer.Child = sheenLayer;
            root.Children.Add(outer);

            root.MouseEnter += delegate { outer.BorderBrush = rimHover; };
            root.MouseLeave += delegate { outer.BorderBrush = rim; };
            root.MouseRightButtonDown += delegate
            {
                if (_onBuildWidgetContextMenu != null)
                {
                    root.ContextMenu = _onBuildWidgetContextMenu();
                }
            };

            return root;
        }

        // =========================================================================
        // WIDGET 1: MAIL INBOX (Gmail OAuth / Standard IMAP SSL)
        // =========================================================================
        private UIElement BuildMailContent(double innerW, double innerH, double u)
        {
            _mailScaleU = u;
            DockPanel root = new DockPanel();

            // Header bar
            DockPanel header = new DockPanel();
            header.Margin = new Thickness(0, 0, 0, Math.Round(14 * u));
            DockPanel.SetDock(header, Dock.Top);

            // Right action chips
            StackPanel rightBtns = new StackPanel();
            rightBtns.Orientation = Orientation.Horizontal;
            rightBtns.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(rightBtns, Dock.Right);

            // Vector Refresh icon button with crisp spin feedback
            RotateTransform spinTf = new RotateTransform(0);
            Path refIcon = new Path
            {
                Data = GeoRefresh,
                Stroke = BrTextPrimary,
                StrokeThickness = 1.6,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Width = Math.Round(14 * u),
                Height = Math.Round(14 * u),
                Stretch = Stretch.Uniform,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = spinTf
            };
            Border refreshBtn = CreateIconChipButton(refIcon, "刷新邮件", u, delegate
            {
                DoubleAnimation spin = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(480))
                {
                    EasingFunction = EaseOutCubic
                };
                Timeline.SetDesiredFrameRate(spin, AnimFps);
                spinTf.BeginAnimation(RotateTransform.AngleProperty, spin);
                if (_mail != null) _mail.TriggerRefresh();
            });
            refreshBtn.Margin = new Thickness(0, 0, Math.Round(6 * u), 0);
            rightBtns.Children.Add(refreshBtn);

            Border cfgBtn = CreateMiniChipButton("配置", u, delegate { if (_onConfigureMail != null) _onConfigureMail(); });
            cfgBtn.Margin = new Thickness(0, 0, Math.Round(6 * u), 0);
            rightBtns.Children.Add(cfgBtn);

            Border openBtn = CreateMiniChipButton("打开", u, delegate { if (_onOpenMailApp != null) _onOpenMailApp(); });
            rightBtns.Children.Add(openBtn);
            header.Children.Add(rightBtns);

            // Left vector mail badge + title + unread badge
            StackPanel leftTitle = new StackPanel();
            leftTitle.Orientation = Orientation.Horizontal;
            leftTitle.VerticalAlignment = VerticalAlignment.Center;

            Border badgeIcon = new Border();
            badgeIcon.Width = Math.Round(28 * u);
            badgeIcon.Height = Math.Round(28 * u);
            badgeIcon.CornerRadius = new CornerRadius(Math.Round(9 * u));
            badgeIcon.Background = FreezeGrad(Color.FromRgb(244, 63, 94), Color.FromRgb(220, 38, 38), 1, 1);
            badgeIcon.Margin = new Thickness(0, 0, Math.Round(10 * u), 0);
            badgeIcon.Child = new Path
            {
                Data = GeoEnvelope,
                Stroke = BrWhite,
                StrokeThickness = 1.65,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Width = Math.Round(15 * u),
                Height = Math.Round(12 * u),
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            leftTitle.Children.Add(badgeIcon);

            TextBlock title = new TextBlock
            {
                Text = "收件箱",
                FontFamily = FontDisplay,
                FontSize = Math.Round(20 * u),
                FontWeight = FontWeights.SemiBold,
                Foreground = BrTextPrimary,
                VerticalAlignment = VerticalAlignment.Center
            };
            leftTitle.Children.Add(title);

            _mailUnreadPill = new Border();
            _mailUnreadPill.CornerRadius = new CornerRadius(Math.Round(10 * u));
            _mailUnreadPill.Background = BrMailUnreadBg;
            _mailUnreadPill.BorderBrush = BrMailUnreadBorder;
            _mailUnreadPill.BorderThickness = new Thickness(0.8);
            _mailUnreadPill.Padding = new Thickness(Math.Round(8.5 * u), Math.Round(2 * u), Math.Round(8.5 * u), Math.Round(2 * u));
            _mailUnreadPill.Margin = new Thickness(Math.Round(8 * u), 0, 0, 0);
            _mailUnreadPill.VerticalAlignment = VerticalAlignment.Center;
            _mailUnreadPill.Visibility = Visibility.Collapsed;
            _mailUnreadText = new TextBlock
            {
                FontFamily = FontDisplay,
                FontSize = Math.Round(12.5 * u),
                FontWeight = FontWeights.SemiBold,
                Foreground = BrWhite
            };
            _mailUnreadPill.Child = _mailUnreadText;
            leftTitle.Children.Add(_mailUnreadPill);

            header.Children.Add(leftTitle);
            root.Children.Add(header);

            // Footer status line (shown cleanly when configured)
            _mailStatusText = new TextBlock
            {
                FontFamily = FontText,
                FontSize = Math.Round(12.5 * u),
                Foreground = BrTextDim,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(Math.Round(2 * u), Math.Round(8 * u), 0, 0)
            };
            DockPanel.SetDock(_mailStatusText, Dock.Bottom);
            root.Children.Add(_mailStatusText);

            // Body stack (switches between scrollable live email list & clean setup view)
            Grid body = new Grid();

            _mailListWell = new Border
            {
                CornerRadius = new CornerRadius(Math.Round(18 * u)),
                Background = BrInnerWell,
                BorderBrush = BrInnerWellBorder,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(Math.Round(8 * u), Math.Round(6 * u), Math.Round(6 * u), Math.Round(6 * u))
            };
            _mailScrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            if (_thinScrollBarStyle != null)
            {
                _mailScrollViewer.Resources.Add(typeof(ScrollBar), _thinScrollBarStyle);
            }
            new SmoothScrollDriver(_mailScrollViewer);
            _mailListHost = new StackPanel();
            _mailListHost.VerticalAlignment = VerticalAlignment.Top;
            _mailScrollViewer.Content = _mailListHost;
            _mailListWell.Child = _mailScrollViewer;
            body.Children.Add(_mailListWell);

            Border setupCard = new Border();
            setupCard.CornerRadius = new CornerRadius(Math.Round(18 * u));
            setupCard.Background = BrInnerWell;
            setupCard.BorderBrush = BrInnerWellBorder;
            setupCard.BorderThickness = new Thickness(1);
            setupCard.Padding = new Thickness(Math.Round(18 * u), Math.Round(16 * u), Math.Round(18 * u), Math.Round(16 * u));
            setupCard.VerticalAlignment = VerticalAlignment.Stretch;

            StackPanel setupSp = new StackPanel();
            setupSp.VerticalAlignment = VerticalAlignment.Center;

            Border mailIllust = new Border
            {
                Width = Math.Round(38 * u),
                Height = Math.Round(38 * u),
                CornerRadius = new CornerRadius(Math.Round(12 * u)),
                Background = FreezeBrush(34, 244, 63, 94),
                BorderBrush = FreezeBrush(62, 248, 113, 113),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, Math.Round(12 * u)),
                Child = new Path
                {
                    Data = GeoEnvelope,
                    Stroke = FreezeBrush(240, 252, 165, 165),
                    StrokeThickness = 1.6,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    Width = Math.Round(19 * u),
                    Height = Math.Round(15 * u),
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            setupSp.Children.Add(mailIllust);

            TextBlock setupHint = new TextBlock
            {
                Text = "实时同步 Gmail 及 QQ / 163 邮箱",
                FontFamily = FontDisplay,
                FontSize = Math.Round(16.5 * u),
                FontWeight = FontWeights.SemiBold,
                Foreground = BrTextPrimary,
                TextWrapping = TextWrapping.Wrap
            };
            setupSp.Children.Add(setupHint);

            TextBlock setupSub = new TextBlock
            {
                Text = "支持 Google OAuth 浏览器一键授权或标准 IMAP，凭据经本机 DPAPI 加密且仅只读",
                FontFamily = FontText,
                FontSize = Math.Round(13.5 * u),
                Foreground = BrTextMuted,
                LineHeight = Math.Round(20 * u),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, Math.Round(6 * u), 0, Math.Round(16 * u))
            };
            setupSp.Children.Add(setupSub);

            StackPanel btnRow = new StackPanel { Orientation = Orientation.Horizontal };
            Border primarySetup = CreateMiniChipButton(
                "连接 Gmail / 配置邮箱", u,
                delegate { if (_onConfigureMail != null) _onConfigureMail(); },
                BrModeActiveBg, FreezeBrush(245, 79, 148, 255));
            primarySetup.Padding = new Thickness(Math.Round(14 * u), Math.Round(7 * u), Math.Round(14 * u), Math.Round(7 * u));
            primarySetup.Margin = new Thickness(0, 0, Math.Round(10 * u), 0);
            btnRow.Children.Add(primarySetup);

            Border launchGmail = CreateMiniChipButton("打开 Gmail", u, delegate { if (_onOpenMailApp != null) _onOpenMailApp(); });
            launchGmail.Padding = new Thickness(Math.Round(14 * u), Math.Round(7 * u), Math.Round(14 * u), Math.Round(7 * u));
            btnRow.Children.Add(launchGmail);

            setupSp.Children.Add(btnRow);
            setupCard.Child = setupSp;
            _mailSetupView = setupCard;
            body.Children.Add(setupCard);

            root.Children.Add(body);
            return root;
        }

        // =========================================================================
        // WIDGET 2: CLASH VERGE PROXY, ONE-CLICK NODE SWITCHER & PRO SPARKLINE
        // =========================================================================
        private UIElement BuildClashContent(double innerW, double innerH, double u)
        {
            _clashScaleU = u;
            DockPanel root = new DockPanel();

            // Header: Glowing status dot + Profile name + Unified Segmented Mode Switcher
            DockPanel header = new DockPanel();
            header.Margin = new Thickness(0, 0, 0, Math.Round(12 * u));
            DockPanel.SetDock(header, Dock.Top);

            Border modeTrack = new Border
            {
                CornerRadius = new CornerRadius(Math.Round(11 * u)),
                Background = BrInnerWell,
                BorderBrush = BrInnerWellBorder,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(Math.Round(3 * u)),
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(modeTrack, Dock.Right);

            StackPanel modeSeg = new StackPanel { Orientation = Orientation.Horizontal };
            _clashModeRuleBtn = CreateModeSegment("规则", "rule", u);
            _clashModeGlobalBtn = CreateModeSegment("全局", "global", u);
            _clashModeDirectBtn = CreateModeSegment("直连", "direct", u);
            _clashModeRuleBtn.Margin = new Thickness(0, 0, Math.Round(2 * u), 0);
            _clashModeGlobalBtn.Margin = new Thickness(0, 0, Math.Round(2 * u), 0);
            modeSeg.Children.Add(_clashModeRuleBtn);
            modeSeg.Children.Add(_clashModeGlobalBtn);
            modeSeg.Children.Add(_clashModeDirectBtn);
            modeTrack.Child = modeSeg;
            header.Children.Add(modeTrack);

            StackPanel titleRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand
            };
            titleRow.ToolTip = CreateSimpleDarkTip("点击打开 Clash Verge", u);
            titleRow.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                if (_onOpenClashApp != null) _onOpenClashApp();
                e.Handled = true;
            };

            Grid dotWrap = new Grid
            {
                Width = Math.Round(16 * u),
                Height = Math.Round(16 * u),
                Margin = new Thickness(0, 0, Math.Round(7 * u), 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            _clashStatusGlow = new Ellipse
            {
                Width = Math.Round(16 * u),
                Height = Math.Round(16 * u),
                Fill = FreezeBrush(58, 52, 211, 153)
            };
            _clashStatusDot = new Ellipse
            {
                Width = Math.Round(9 * u),
                Height = Math.Round(9 * u),
                Fill = BrEmerald,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            dotWrap.Children.Add(_clashStatusGlow);
            dotWrap.Children.Add(_clashStatusDot);
            titleRow.Children.Add(dotWrap);

            _clashProfileText = new TextBlock
            {
                Text = "Clash Verge",
                FontFamily = FontDisplay,
                FontSize = Math.Round(20 * u),
                FontWeight = FontWeights.SemiBold,
                Foreground = BrTextPrimary,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = Math.Max(80 * u, innerW - Math.Round(190 * u)),
                VerticalAlignment = VerticalAlignment.Center
            };
            titleRow.Children.Add(_clashProfileText);

            header.Children.Add(titleRow);
            root.Children.Add(header);

            // Bottom Subscription Bar (remaining GB & expire date)
            StackPanel subPanel = new StackPanel();
            subPanel.Margin = new Thickness(0, Math.Round(10 * u), 0, 0);
            DockPanel.SetDock(subPanel, Dock.Bottom);

            DockPanel subMeta = new DockPanel();
            _clashSubExpireText = new TextBlock
            {
                FontFamily = FontText,
                FontSize = Math.Round(12.5 * u),
                Foreground = BrTextDim,
                Text = ""
            };
            DockPanel.SetDock(_clashSubExpireText, Dock.Right);
            subMeta.Children.Add(_clashSubExpireText);

            _clashSubLeftText = new TextBlock
            {
                FontFamily = FontText,
                FontSize = Math.Round(13 * u),
                Foreground = BrTextMuted,
                Text = "实时网卡监测"
            };
            subMeta.Children.Add(_clashSubLeftText);
            subPanel.Children.Add(subMeta);

            _clashSubBarMaxW = Math.Max(60, innerW);
            Border subTrack = new Border
            {
                Height = Math.Round(5.5 * u),
                CornerRadius = new CornerRadius(Math.Round(2.75 * u)),
                Background = BrTrackBg,
                Margin = new Thickness(0, Math.Round(6 * u), 0, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            _clashSubBarFill = new Border
            {
                Height = Math.Round(5.5 * u),
                Width = 0,
                HorizontalAlignment = HorizontalAlignment.Left,
                CornerRadius = new CornerRadius(Math.Round(2.75 * u)),
                Background = FreezeGrad(Color.FromRgb(56, 189, 248), Color.FromRgb(52, 211, 153), 1, 0)
            };
            subTrack.Child = _clashSubBarFill;
            subPanel.Children.Add(subTrack);
            root.Children.Add(subPanel);

            // Middle section: Interactive Active Node Pill + Speed Readout + Pro Sparkline
            DockPanel mid = new DockPanel();

            // Active node selector pill row (click to quick-switch node!)
            _clashNodePill = new Border
            {
                CornerRadius = new CornerRadius(Math.Round(13 * u)),
                Background = BrInnerWell,
                BorderBrush = BrInnerWellBorder,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(Math.Round(10 * u), Math.Round(7 * u), Math.Round(10 * u), Math.Round(7 * u)),
                Margin = new Thickness(0, 0, 0, Math.Round(10 * u)),
                Cursor = Cursors.Hand,
                RenderTransformOrigin = new Point(0.5, 0.5)
            };
            ScaleTransform nodePillSt = new ScaleTransform(1.0, 1.0);
            _clashNodePill.RenderTransform = nodePillSt;
            _clashNodePill.ToolTip = CreateSimpleDarkTip("点击快速切换代理节点", u);
            _clashNodePill.MouseEnter += delegate { _clashNodePill.Background = BrInnerWellHover; _clashNodePill.BorderBrush = BrInnerWellBorderHover; };
            _clashNodePill.MouseLeave += delegate { _clashNodePill.Background = BrInnerWell; _clashNodePill.BorderBrush = BrInnerWellBorder; QuickScale(nodePillSt, 1.0, 100); };
            _clashNodePill.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { QuickScale(nodePillSt, 0.985, 65); e.Handled = true; };
            _clashNodePill.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                QuickScale(nodePillSt, 1.0, 100);
                OpenClashNodeSwitcher(_clashNodePill);
                e.Handled = true;
            };
            DockPanel.SetDock(_clashNodePill, Dock.Top);

            DockPanel nodeRow = new DockPanel();

            StackPanel badges = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(badges, Dock.Right);

            _clashTunPill = new Border
            {
                CornerRadius = new CornerRadius(Math.Round(7 * u)),
                Background = FreezeBrush(44, 56, 189, 248),
                Padding = new Thickness(Math.Round(6 * u), Math.Round(2 * u), Math.Round(6 * u), Math.Round(2 * u)),
                Margin = new Thickness(Math.Round(6 * u), 0, 0, 0),
                Visibility = Visibility.Collapsed
            };
            _clashTunPill.Child = new TextBlock { Text = "TUN", FontFamily = FontDisplay, FontSize = Math.Round(11 * u), FontWeight = FontWeights.Bold, Foreground = BrCyan };

            _clashDelayPill = new Border
            {
                CornerRadius = new CornerRadius(Math.Round(7 * u)),
                Background = FreezeBrush(40, 52, 211, 153),
                Padding = new Thickness(Math.Round(7 * u), Math.Round(2 * u), Math.Round(7 * u), Math.Round(2 * u)),
                Margin = new Thickness(Math.Round(6 * u), 0, 0, 0),
                Cursor = Cursors.Hand,
                RenderTransformOrigin = new Point(0.5, 0.5)
            };
            ScaleTransform delaySt = new ScaleTransform(1.0, 1.0);
            _clashDelayPill.RenderTransform = delaySt;
            _clashDelayPill.ToolTip = CreateSimpleDarkTip("点击重新测速", u);
            _clashDelayPill.MouseEnter += delegate { QuickScale(delaySt, 1.06, 100); };
            _clashDelayPill.MouseLeave += delegate { QuickScale(delaySt, 1.0, 100); };
            _clashDelayPill.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { QuickScale(delaySt, 0.92, 60); e.Handled = true; };
            _clashDelayPill.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                QuickScale(delaySt, 1.0, 100);
                if (_clash != null)
                {
                    _clashDelayText.Text = "...";
                    _clash.TestActiveNodeDelayAsync();
                }
                e.Handled = true;
            };
            _clashDelayText = new TextBlock { Text = "-- ms", FontFamily = FontDisplay, FontSize = Math.Round(12 * u), FontWeight = FontWeights.SemiBold, Foreground = BrEmerald };
            _clashDelayPill.Child = _clashDelayText;

            TextBlock chevron = new TextBlock
            {
                Text = "▾",
                FontSize = Math.Round(13.5 * u),
                Foreground = BrTextMuted,
                Margin = new Thickness(Math.Round(6 * u), 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            badges.Children.Add(_clashDelayPill);
            badges.Children.Add(_clashTunPill);
            badges.Children.Add(chevron);
            nodeRow.Children.Add(badges);

            // Region pill badge (clean replacement for broken Windows flag emojis!)
            _clashRegionBadge = new Border
            {
                CornerRadius = new CornerRadius(Math.Round(6 * u)),
                Background = FreezeBrush(44, 59, 130, 246),
                BorderBrush = FreezeBrush(70, 147, 197, 253),
                BorderThickness = new Thickness(0.8),
                Padding = new Thickness(Math.Round(6 * u), Math.Round(1.5 * u), Math.Round(6 * u), Math.Round(1.5 * u)),
                Margin = new Thickness(0, 0, Math.Round(8 * u), 0),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed
            };
            DockPanel.SetDock(_clashRegionBadge, Dock.Left);
            _clashRegionText = new TextBlock
            {
                Text = "",
                FontFamily = FontDisplay,
                FontSize = Math.Round(11.5 * u),
                FontWeight = FontWeights.Bold,
                Foreground = BrSkySoft
            };
            _clashRegionBadge.Child = _clashRegionText;
            nodeRow.Children.Add(_clashRegionBadge);

            _clashNodeText = new TextBlock
            {
                Text = "正在读取节点...",
                FontFamily = FontText,
                FontSize = Math.Round(14.5 * u),
                FontWeight = FontWeights.Medium,
                Foreground = BrTextPrimary,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            nodeRow.Children.Add(_clashNodeText);
            _clashNodePill.Child = nodeRow;
            mid.Children.Add(_clashNodePill);

            // Live Down/Up speed row
            DockPanel speedRow = new DockPanel();
            speedRow.Margin = new Thickness(Math.Round(2 * u), 0, Math.Round(2 * u), Math.Round(8 * u));
            DockPanel.SetDock(speedRow, Dock.Top);

            _clashSessionText = new TextBlock
            {
                Text = "",
                FontFamily = FontText,
                FontSize = Math.Round(13 * u),
                Foreground = BrTextDim,
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(_clashSessionText, Dock.Right);
            speedRow.Children.Add(_clashSessionText);

            StackPanel speeds = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _clashDownText = new TextBlock
            {
                Text = "↓ 0 B/s",
                FontFamily = FontDisplay,
                FontSize = Math.Round(17 * u),
                FontWeight = FontWeights.SemiBold,
                Foreground = BrCyan,
                Margin = new Thickness(0, 0, Math.Round(16 * u), 0)
            };
            _clashUpText = new TextBlock
            {
                Text = "↑ 0 B/s",
                FontFamily = FontDisplay,
                FontSize = Math.Round(15.5 * u),
                FontWeight = FontWeights.Medium,
                Foreground = BrEmerald,
                VerticalAlignment = VerticalAlignment.Bottom
            };
            speeds.Children.Add(_clashDownText);
            speeds.Children.Add(_clashUpText);
            speedRow.Children.Add(speeds);
            mid.Children.Add(speedRow);

            // Real-time Speed Sparkline Graph (fills remaining vertical space cleanly!)
            _sparkW = Math.Max(80, innerW - Math.Round(12 * u));
            _sparkH = Math.Max(Math.Round(48 * u), Math.Min(Math.Round(130 * u), innerH - Math.Round(168 * u)));

            Border sparkWrap = new Border
            {
                CornerRadius = new CornerRadius(Math.Round(14 * u)),
                Background = BrInnerWell,
                BorderBrush = BrInnerWellBorder,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(Math.Round(5 * u), Math.Round(5 * u), Math.Round(5 * u), Math.Round(5 * u)),
                ClipToBounds = true
            };
            Grid sparkGrid = new Grid { Width = _sparkW, Height = _sparkH };
            Canvas sparkCanvas = new Canvas { Width = _sparkW, Height = _sparkH, ClipToBounds = true };

            // Subtle dashed horizontal gridlines at 33% and 66%
            DoubleCollection dashes = new DoubleCollection { 3, 4 };
            dashes.Freeze();
            SolidColorBrush gridStroke = FreezeBrush(22, 255, 255, 255);
            for (int gl = 1; gl <= 2; gl++)
            {
                double gy = Math.Round(_sparkH * gl / 3.0);
                sparkCanvas.Children.Add(new Line
                {
                    X1 = 0, Y1 = gy, X2 = _sparkW, Y2 = gy,
                    Stroke = gridStroke,
                    StrokeThickness = 1,
                    StrokeDashArray = dashes,
                    IsHitTestVisible = false
                });
            }

            LinearGradientBrush downAreaFill = new LinearGradientBrush();
            downAreaFill.StartPoint = new Point(0, 0);
            downAreaFill.EndPoint = new Point(0, 1);
            downAreaFill.GradientStops.Add(new GradientStop(Color.FromArgb(92, 56, 189, 248), 0.0));
            downAreaFill.GradientStops.Add(new GradientStop(Color.FromArgb(6, 56, 189, 248), 1.0));
            downAreaFill.Freeze();

            LinearGradientBrush upAreaFill = new LinearGradientBrush();
            upAreaFill.StartPoint = new Point(0, 0);
            upAreaFill.EndPoint = new Point(0, 1);
            upAreaFill.GradientStops.Add(new GradientStop(Color.FromArgb(46, 52, 211, 153), 0.0));
            upAreaFill.GradientStops.Add(new GradientStop(Color.FromArgb(2, 52, 211, 153), 1.0));
            upAreaFill.Freeze();

            _clashUpArea = new Polygon { Fill = upAreaFill, IsHitTestVisible = false };
            _clashDownArea = new Polygon { Fill = downAreaFill, IsHitTestVisible = false };
            _clashUpLine = new Polyline
            {
                Stroke = FreezeBrush(185, 52, 211, 153),
                StrokeThickness = Math.Max(1.3, 1.5 * u),
                StrokeLineJoin = PenLineJoin.Round,
                IsHitTestVisible = false
            };
            _clashDownLine = new Polyline
            {
                Stroke = BrCyan,
                StrokeThickness = Math.Max(1.8, 2.2 * u),
                StrokeLineJoin = PenLineJoin.Round,
                IsHitTestVisible = false
            };
            double dotSz = Math.Round(6 * u);
            _clashDownDot = new Ellipse
            {
                Width = dotSz,
                Height = dotSz,
                Fill = BrWhite,
                Stroke = BrCyan,
                StrokeThickness = 1.6,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };

            sparkCanvas.Children.Add(_clashUpArea);
            sparkCanvas.Children.Add(_clashDownArea);
            sparkCanvas.Children.Add(_clashUpLine);
            sparkCanvas.Children.Add(_clashDownLine);
            sparkCanvas.Children.Add(_clashDownDot);
            sparkGrid.Children.Add(sparkCanvas);

            _clashPeakLabel = new TextBlock
            {
                Text = "64 KB/s",
                FontFamily = FontDisplay,
                FontSize = Math.Round(10.5 * u),
                Foreground = FreezeBrush(96, 203, 213, 225),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, Math.Round(1 * u), Math.Round(4 * u), 0),
                IsHitTestVisible = false
            };
            sparkGrid.Children.Add(_clashPeakLabel);

            sparkWrap.Child = sparkGrid;
            mid.Children.Add(sparkWrap);

            root.Children.Add(mid);
            return root;
        }

        private void OpenClashNodeSwitcher(FrameworkElement anchor)
        {
            ContextMenu cm = BuildClashNodeSwitcherMenu(anchor);
            if (cm == null)
            {
                if (_onOpenClashApp != null) _onOpenClashApp();
                return;
            }
            cm.PlacementTarget = anchor;
            cm.Placement = PlacementMode.Bottom;
            cm.IsOpen = true;
        }

        internal ContextMenu BuildClashNodeSwitcherMenu(FrameworkElement anchor)
        {
            if (_clash == null || _onCreateDarkMenu == null || _onCreateDarkMenuItem == null)
            {
                return null;
            }

            string groupName;
            List<ProxyNodeInfo> nodes = _clash.GetActiveGroupNodes(out groupName);
            if (nodes == null || nodes.Count == 0 || string.IsNullOrEmpty(groupName))
            {
                return null;
            }

            double u = _clashScaleU > 0.2 ? _clashScaleU : 1.0;
            ContextMenu cm = _onCreateDarkMenu();
            double availH = SystemParameters.WorkArea.Height;
            Window ownerWin = anchor != null ? Window.GetWindow(anchor) : null;
            if (ownerWin != null && ownerWin.ActualHeight > 200) availH = ownerWin.ActualHeight;
            cm.MaxHeight = Math.Max(260.0, Math.Min(Math.Round(440 * u), Math.Round(availH * 0.54)));

            int added = 0;
            MenuItem selectedMenuItem = null;
            SolidColorBrush regBg = FreezeBrush(40, 59, 130, 246);
            SolidColorBrush regBorder = FreezeBrush(66, 147, 197, 253);

            for (int i = 0; i < nodes.Count; i++)
            {
                ProxyNodeInfo node = nodes[i];
                // Skip non-routable subscription info banners ("剩余流量 / 套餐到期") unless currently selected
                if (!node.IsSelected && ClashService.IsInfoPseudoNode(node.Name)) continue;

                string capGroup = groupName;
                string capNode = node.Name;
                string capClean = node.CleanName;
                string capReg = node.RegionCode;

                DockPanel row = new DockPanel
                {
                    MinWidth = Math.Round(290 * u),
                    LastChildFill = true
                };

                // Right-aligned color-coded latency pill with uniform width for crisp column alignment
                if (node.DelayMs > 0)
                {
                    Brush delayFg = node.DelayMs < 150 ? BrEmerald : (node.DelayMs < 350 ? BrAmber : BrCoral);
                    Brush delayBg = node.DelayMs < 150 ? BrDelayFastBg : (node.DelayMs < 350 ? BrDelayMedBg : BrDelaySlowBg);
                    Border delayBadge = new Border
                    {
                        CornerRadius = new CornerRadius(Math.Round(6 * u)),
                        Background = delayBg,
                        MinWidth = Math.Round(58 * u),
                        Padding = new Thickness(Math.Round(6 * u), Math.Round(1.5 * u), Math.Round(6 * u), Math.Round(1.5 * u)),
                        Margin = new Thickness(Math.Round(10 * u), 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    delayBadge.Child = new TextBlock
                    {
                        Text = node.DelayMs + " ms",
                        FontFamily = FontDisplay,
                        FontSize = Math.Round(11.5 * u),
                        FontWeight = FontWeights.SemiBold,
                        Foreground = delayFg,
                        HorizontalAlignment = HorizontalAlignment.Center
                    };
                    DockPanel.SetDock(delayBadge, Dock.Right);
                    row.Children.Add(delayBadge);
                }

                // Left active dot indicator
                Ellipse activeDot = new Ellipse
                {
                    Width = Math.Round(7 * u),
                    Height = Math.Round(7 * u),
                    Fill = node.IsSelected ? BrCyan : Brushes.Transparent,
                    Margin = new Thickness(0, 0, Math.Round(8 * u), 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                DockPanel.SetDock(activeDot, Dock.Left);
                row.Children.Add(activeDot);

                // Region code badge pill (e.g. JP / HK / SG / US)
                if (!string.IsNullOrEmpty(node.RegionCode))
                {
                    Border regPill = new Border
                    {
                        CornerRadius = new CornerRadius(Math.Round(5 * u)),
                        Background = regBg,
                        BorderBrush = regBorder,
                        BorderThickness = new Thickness(0.8),
                        MinWidth = Math.Round(28 * u),
                        Padding = new Thickness(Math.Round(4 * u), Math.Round(1 * u), Math.Round(4 * u), Math.Round(1 * u)),
                        Margin = new Thickness(0, 0, Math.Round(8 * u), 0),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    regPill.Child = new TextBlock
                    {
                        Text = node.RegionCode,
                        FontFamily = FontDisplay,
                        FontSize = Math.Round(10.5 * u),
                        FontWeight = FontWeights.Bold,
                        Foreground = BrSkySoft,
                        HorizontalAlignment = HorizontalAlignment.Center
                    };
                    DockPanel.SetDock(regPill, Dock.Left);
                    row.Children.Add(regPill);
                }

                TextBlock nameLbl = new TextBlock
                {
                    Text = node.CleanName,
                    FontFamily = FontText,
                    FontSize = Math.Round(14 * u),
                    FontWeight = node.IsSelected ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = node.IsSelected ? BrSkySoft : BrTextPrimary,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = Math.Round(235 * u),
                    VerticalAlignment = VerticalAlignment.Center
                };
                row.Children.Add(nameLbl);

                MenuItem mi = _onCreateDarkMenuItem("", delegate
                {
                    // Instant optimistic UI feedback while switching in background
                    if (_clashNodeText != null) _clashNodeText.Text = capClean;
                    if (_clashRegionBadge != null && _clashRegionText != null)
                    {
                        if (!string.IsNullOrEmpty(capReg))
                        {
                            _clashRegionText.Text = capReg;
                            _clashRegionBadge.Visibility = Visibility.Visible;
                        }
                        else
                        {
                            _clashRegionBadge.Visibility = Visibility.Collapsed;
                        }
                    }
                    if (_clashDelayText != null) _clashDelayText.Text = "...";

                    _clash.SelectProxyNodeAsync(capGroup, capNode, delegate
                    {
                        if (anchor != null && !anchor.Dispatcher.HasShutdownStarted)
                        {
                            anchor.Dispatcher.BeginInvoke((Action)UpdateClashUi);
                        }
                    });
                });
                mi.Header = row;
                mi.Tag = node.Name;
                if (node.IsSelected)
                {
                    mi.Background = BrNodeSelectedRowBg;
                    mi.BorderBrush = BrNodeSelectedRowBorder;
                    mi.BorderThickness = new Thickness(0.8);
                    selectedMenuItem = mi;
                }
                cm.Items.Add(mi);
                added++;
            }

            if (added > 0)
            {
                cm.Items.Add(CreateMenuSeparator());
            }
            cm.Items.Add(_onCreateDarkMenuItem("重新测速当前节点", delegate
            {
                if (_clashDelayText != null) _clashDelayText.Text = "...";
                _clash.TestActiveNodeDelayAsync();
            }));
            cm.Items.Add(_onCreateDarkMenuItem("打开 Clash Verge 完整面板 ↗", delegate
            {
                if (_onOpenClashApp != null) _onOpenClashApp();
            }));

            MenuItem capSel = selectedMenuItem;
            SmoothScrollDriver popupSmoothScroll = null;
            cm.Opened += delegate
            {
                Action initAndCenter = delegate
                {
                    try
                    {
                        cm.ApplyTemplate();
                        cm.UpdateLayout();
                        ScrollViewer sv = FindVisualDescendant<ScrollViewer>(cm);
                        if (sv != null)
                        {
                            if (popupSmoothScroll == null)
                            {
                                popupSmoothScroll = new SmoothScrollDriver(sv);
                            }
                            if (capSel != null && sv.ScrollableHeight > 0 && sv.ViewportHeight > 0)
                            {
                                Point relPt = capSel.TransformToAncestor(sv).Transform(new Point(0, 0));
                                double itemContentY = relPt.Y + sv.VerticalOffset;
                                double itemH = capSel.ActualHeight > 1 ? capSel.ActualHeight : capSel.DesiredSize.Height;
                                double targetY = ComputeCenteredScrollOffset(itemContentY, itemH, sv.ViewportHeight, sv.ScrollableHeight);
                                popupSmoothScroll.Stop();
                                sv.ScrollToVerticalOffset(targetY);
                                sv.UpdateLayout();
                            }
                        }
                        else if (capSel != null)
                        {
                            capSel.BringIntoView();
                        }
                    }
                    catch { }
                };
                initAndCenter();
                cm.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, initAndCenter);
            };
            cm.PreviewKeyDown += delegate
            {
                if (popupSmoothScroll != null) popupSmoothScroll.Stop();
            };
            cm.Closed += delegate
            {
                if (popupSmoothScroll != null) popupSmoothScroll.Stop();
            };

            return cm;
        }

        internal static double ComputeCenteredScrollOffset(double itemTopY, double itemHeight, double viewportHeight, double scrollableHeight)
        {
            if (scrollableHeight <= 0 || viewportHeight <= 0) return 0;
            double target = itemTopY - (viewportHeight - itemHeight) * 0.5;
            if (target < 0) target = 0;
            if (target > scrollableHeight) target = scrollableHeight;
            return target;
        }

        private static T FindVisualDescendant<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) return null;
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                DependencyObject ch = VisualTreeHelper.GetChild(parent, i);
                if (ch is T) return (T)ch;
                T found = FindVisualDescendant<T>(ch);
                if (found != null) return found;
            }
            return null;
        }

        private static Separator CreateMenuSeparator()
        {
            Separator sep = new Separator();
            ControlTemplate sepTpl = new ControlTemplate(typeof(Separator));
            FrameworkElementFactory sepLine = new FrameworkElementFactory(typeof(Border));
            sepLine.SetValue(Border.HeightProperty, 1.0);
            sepLine.SetValue(Border.MarginProperty, new Thickness(12, 5, 12, 5));
            sepLine.SetValue(Border.BackgroundProperty, FreezeBrush(38, 255, 255, 255));
            sepTpl.VisualTree = sepLine;
            sep.Template = sepTpl;
            return sep;
        }

        private Border CreateModeSegment(string label, string modeKey, double u)
        {
            Border b = new Border
            {
                CornerRadius = new CornerRadius(Math.Round(8 * u)),
                Background = Brushes.Transparent,
                Padding = new Thickness(Math.Round(9 * u), Math.Round(4 * u), Math.Round(9 * u), Math.Round(4 * u)),
                Cursor = Cursors.Hand,
                Tag = modeKey,
                RenderTransformOrigin = new Point(0.5, 0.5)
            };
            ScaleTransform st = new ScaleTransform(1.0, 1.0);
            b.RenderTransform = st;
            b.Child = new TextBlock
            {
                Text = label,
                FontFamily = FontText,
                FontSize = Math.Round(12.5 * u),
                FontWeight = FontWeights.Medium,
                Foreground = BrTextPrimary
            };
            b.MouseEnter += delegate
            {
                if (b.Background != BrModeActiveBg) b.Background = BrModeHoverBg;
            };
            b.MouseLeave += delegate
            {
                if (b.Background != BrModeActiveBg) b.Background = Brushes.Transparent;
                QuickScale(st, 1.0, 100);
            };
            b.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                QuickScale(st, 0.93, 60);
                e.Handled = true;
            };
            b.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                QuickScale(st, 1.0, 100);
                if (_clash != null) _clash.SwitchModeAsync(modeKey);
                HighlightClashMode(modeKey);
                e.Handled = true;
            };
            return b;
        }

        private void HighlightClashMode(string mode)
        {
            string m = (mode ?? "rule").ToLowerInvariant();
            if (_clashModeRuleBtn != null) _clashModeRuleBtn.Background = (m == "rule") ? BrModeActiveBg : Brushes.Transparent;
            if (_clashModeGlobalBtn != null) _clashModeGlobalBtn.Background = (m == "global") ? BrModeActiveBg : Brushes.Transparent;
            if (_clashModeDirectBtn != null) _clashModeDirectBtn.Background = (m == "direct") ? BrModeActiveBg : Brushes.Transparent;
        }

        // =========================================================================
        // WIDGET 3: MUSIC PLAYER & LIVE SYNCED LYRICS (Vector Controls + Smooth Lyrics)
        // =========================================================================
        private UIElement BuildMusicContent(double innerW, double innerH, double u)
        {
            _musicScaleU = u;
            DockPanel root = new DockPanel();

            // Top: Cover Art + Song/Artist + Vector Playback Controls
            DockPanel topRow = new DockPanel();
            topRow.Margin = new Thickness(0, 0, 0, Math.Round(14 * u));
            DockPanel.SetDock(topRow, Dock.Top);

            double coverSz = Math.Round(Math.Min(74 * u, innerH * 0.28));
            Grid coverWrap = new Grid
            {
                Width = coverSz,
                Height = coverSz,
                Margin = new Thickness(0, 0, Math.Round(14 * u), 0),
                Cursor = Cursors.Hand,
                RenderTransformOrigin = new Point(0.5, 0.5)
            };
            ScaleTransform coverSt = new ScaleTransform(1.0, 1.0);
            coverWrap.RenderTransform = coverSt;
            coverWrap.ToolTip = CreateSimpleDarkTip("点击打开或唤起音乐播放器", u);
            coverWrap.MouseEnter += delegate { QuickScale(coverSt, 1.055, 130); };
            coverWrap.MouseLeave += delegate { QuickScale(coverSt, 1.0, 130); };
            coverWrap.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { QuickScale(coverSt, 0.94, 70); e.Handled = true; };
            coverWrap.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                QuickScale(coverSt, 1.0, 110);
                if (_onOpenMusicApp != null) _onOpenMusicApp();
                e.Handled = true;
            };
            DockPanel.SetDock(coverWrap, Dock.Left);

            LinearGradientBrush artGrad = new LinearGradientBrush();
            artGrad.StartPoint = new Point(0, 0);
            artGrad.EndPoint = new Point(1, 1);
            artGrad.GradientStops.Add(new GradientStop(Color.FromRgb(236, 72, 153), 0.0));
            artGrad.GradientStops.Add(new GradientStop(Color.FromRgb(139, 92, 246), 0.55));
            artGrad.GradientStops.Add(new GradientStop(Color.FromRgb(59, 130, 246), 1.0));
            artGrad.Freeze();

            _musicCoverFallback = new Border
            {
                CornerRadius = new CornerRadius(Math.Round(18 * u)),
                Background = artGrad,
                BorderBrush = FreezeBrush(65, 255, 255, 255),
                BorderThickness = new Thickness(1)
            };
            _musicCoverFallback.Child = new Path
            {
                Data = GeoNote,
                Stroke = BrWhite,
                Fill = BrWhite,
                StrokeThickness = 1.5,
                StrokeLineJoin = PenLineJoin.Round,
                Width = Math.Round(coverSz * 0.44),
                Height = Math.Round(coverSz * 0.44),
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            coverWrap.Children.Add(_musicCoverFallback);

            // True GPU anti-aliased rounded album cover via Border.Background ImageBrush
            // (unlike Border + Child Image, which only clips to a sharp rectangle in WPF!)
            _musicCoverArtBorder = new Border
            {
                CornerRadius = new CornerRadius(Math.Round(18 * u)),
                BorderBrush = FreezeBrush(65, 255, 255, 255),
                BorderThickness = new Thickness(1),
                Visibility = Visibility.Collapsed
            };
            RenderOptions.SetBitmapScalingMode(_musicCoverArtBorder, BitmapScalingMode.Fant);
            coverWrap.Children.Add(_musicCoverArtBorder);
            topRow.Children.Add(coverWrap);

            // Right crisp vector playback buttons
            StackPanel ctrlRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(Math.Round(8 * u), 0, 0, 0)
            };
            DockPanel.SetDock(ctrlRow, Dock.Right);

            Path playPath;
            Border prevBtn = CreateMediaVectorBtn(GeoPrev, u, false, "上一首", out playPath, delegate { if (_media != null) _media.PrevTrack(); });
            Border playBtn = CreateMediaVectorBtn(GeoPlay, u, true, "播放 / 暂停", out _musicPlayPath, delegate { if (_media != null) _media.TogglePlayPause(); });
            Border nextBtn = CreateMediaVectorBtn(GeoNext, u, false, "下一首", out playPath, delegate { if (_media != null) _media.NextTrack(); });
            prevBtn.Margin = new Thickness(0, 0, Math.Round(7 * u), 0);
            playBtn.Margin = new Thickness(0, 0, Math.Round(7 * u), 0);
            ctrlRow.Children.Add(prevBtn);
            ctrlRow.Children.Add(playBtn);
            ctrlRow.Children.Add(nextBtn);
            topRow.Children.Add(ctrlRow);

            // Center title + artist + source badge (clickable to focus player)
            StackPanel meta = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand
            };
            meta.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                if (_onOpenMusicApp != null) _onOpenMusicApp();
                e.Handled = true;
            };
            _musicTitleText = new TextBlock
            {
                Text = "暂无播放中的媒体",
                FontFamily = FontDisplay,
                FontSize = Math.Round(18.5 * u),
                FontWeight = FontWeights.SemiBold,
                Foreground = BrTextPrimary,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            meta.Children.Add(_musicTitleText);

            DockPanel subRow = new DockPanel
            {
                Margin = new Thickness(0, Math.Round(4 * u), 0, 0)
            };

            _musicAppBadge = new Border
            {
                CornerRadius = new CornerRadius(Math.Round(7 * u)),
                Background = FreezeBrush(38, 255, 255, 255),
                BorderBrush = BrPillBorder,
                BorderThickness = new Thickness(0.8),
                Padding = new Thickness(Math.Round(7 * u), Math.Round(1.5 * u), Math.Round(7 * u), Math.Round(1.5 * u)),
                Margin = new Thickness(Math.Round(8 * u), 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed
            };
            _musicAppBadgeText = new TextBlock
            {
                FontFamily = FontText,
                FontSize = Math.Round(11 * u),
                FontWeight = FontWeights.Medium,
                Foreground = BrTextPrimary
            };
            _musicAppBadge.Child = _musicAppBadgeText;
            DockPanel.SetDock(_musicAppBadge, Dock.Right);
            subRow.Children.Add(_musicAppBadge);

            _musicArtistText = new TextBlock
            {
                Text = "网易云 · Spotify · Apple Music",
                FontFamily = FontText,
                FontSize = Math.Round(13.5 * u),
                Foreground = BrTextMuted,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            subRow.Children.Add(_musicArtistText);
            meta.Children.Add(subRow);

            topRow.Children.Add(meta);
            root.Children.Add(topRow);

            // Bottom progress bar
            StackPanel progPanel = new StackPanel { Margin = new Thickness(0, Math.Round(12 * u), 0, 0) };
            DockPanel.SetDock(progPanel, Dock.Bottom);

            _musicProgMaxW = Math.Max(60, innerW);
            Border progTrack = new Border
            {
                Height = Math.Round(5 * u),
                CornerRadius = new CornerRadius(Math.Round(2.5 * u)),
                Background = BrTrackBg
            };
            _musicProgFill = new Border
            {
                Height = Math.Round(5 * u),
                Width = 0,
                HorizontalAlignment = HorizontalAlignment.Left,
                CornerRadius = new CornerRadius(Math.Round(2.5 * u)),
                Background = FreezeGrad(Color.FromRgb(255, 255, 255), Color.FromRgb(56, 189, 248), 1, 0)
            };
            progTrack.Child = _musicProgFill;
            progPanel.Children.Add(progTrack);

            DockPanel timeRow = new DockPanel { Margin = new Thickness(0, Math.Round(5 * u), 0, 0) };
            _musicTimeRight = new TextBlock { Text = "00:00", FontFamily = FontDisplay, FontSize = Math.Round(11.5 * u), Foreground = BrTextDim };
            DockPanel.SetDock(_musicTimeRight, Dock.Right);
            timeRow.Children.Add(_musicTimeRight);
            _musicTimeLeft = new TextBlock { Text = "00:00", FontFamily = FontDisplay, FontSize = Math.Round(11.5 * u), Foreground = BrTextDim };
            timeRow.Children.Add(_musicTimeLeft);
            progPanel.Children.Add(timeRow);
            root.Children.Add(progPanel);

            // Center Live 3-Line Synced Lyrics Well
            Border lyricsWell = new Border
            {
                CornerRadius = new CornerRadius(Math.Round(18 * u)),
                Background = BrInnerWell,
                BorderBrush = BrInnerWellBorder,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(Math.Round(14 * u), Math.Round(10 * u), Math.Round(14 * u), Math.Round(10 * u)),
                ClipToBounds = true
            };
            StackPanel lyricStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

            _musicPrevLyric = new TextBlock
            {
                Text = "",
                FontFamily = FontText,
                FontSize = Math.Round(13.5 * u),
                Foreground = BrTextDim,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            _musicCurLyricSlide = new TranslateTransform(0, 0);
            _musicCurLyric = new TextBlock
            {
                Text = "播放音乐后自动同步实时滚动歌词",
                FontFamily = FontDisplay,
                FontSize = Math.Round(18 * u),
                FontWeight = FontWeights.SemiBold,
                Foreground = BrWhite,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                LineHeight = Math.Round(24 * u),
                MaxHeight = Math.Round(49 * u)
            };
            _musicCurLyricPill = new Border
            {
                CornerRadius = new CornerRadius(Math.Round(12 * u)),
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(Math.Round(12 * u), Math.Round(5 * u), Math.Round(12 * u), Math.Round(5 * u)),
                Margin = new Thickness(0, Math.Round(5 * u), 0, Math.Round(5 * u)),
                HorizontalAlignment = HorizontalAlignment.Center,
                RenderTransform = _musicCurLyricSlide,
                Child = _musicCurLyric
            };
            _musicNextLyric = new TextBlock
            {
                Text = "点击左侧封面快速打开音乐播放器",
                FontFamily = FontText,
                FontSize = Math.Round(13.5 * u),
                Foreground = BrTextMuted,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            lyricStack.Children.Add(_musicPrevLyric);
            lyricStack.Children.Add(_musicCurLyricPill);
            lyricStack.Children.Add(_musicNextLyric);
            lyricsWell.Child = lyricStack;
            root.Children.Add(lyricsWell);

            return root;
        }

        private Border CreateMediaVectorBtn(Geometry geo, double u, bool primary, string tooltipText, out Path pathRef, Action onClick)
        {
            double sz = Math.Round((primary ? 38 : 32) * u);
            double iconSz = Math.Round((primary ? 15 : 12.5) * u);
            SolidColorBrush normalBg = primary ? FreezeBrush(60, 255, 255, 255) : BrPillBg;
            SolidColorBrush hoverBg = primary ? FreezeBrush(92, 255, 255, 255) : BrPillHover;
            SolidColorBrush normalBorder = primary ? FreezeBrush(84, 255, 255, 255) : BrPillBorder;
            SolidColorBrush hoverBorder = primary ? FreezeBrush(140, 255, 255, 255) : BrInnerWellBorderHover;

            Border b = new Border
            {
                Width = sz,
                Height = sz,
                CornerRadius = new CornerRadius(sz * 0.5),
                Background = normalBg,
                BorderBrush = normalBorder,
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                RenderTransformOrigin = new Point(0.5, 0.5)
            };
            if (!string.IsNullOrEmpty(tooltipText)) b.ToolTip = CreateSimpleDarkTip(tooltipText, u);
            ScaleTransform st = new ScaleTransform(1.0, 1.0);
            b.RenderTransform = st;

            pathRef = new Path
            {
                Data = geo,
                Fill = BrWhite,
                Width = iconSz,
                Height = iconSz,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            b.Child = pathRef;

            b.MouseEnter += delegate { b.Background = hoverBg; b.BorderBrush = hoverBorder; QuickScale(st, 1.08, 120); };
            b.MouseLeave += delegate { b.Background = normalBg; b.BorderBrush = normalBorder; QuickScale(st, 1.0, 120); };
            b.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { QuickScale(st, 0.90, 65); e.Handled = true; };
            b.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                QuickScale(st, 1.04, 100);
                if (onClick != null) onClick();
                e.Handled = true;
            };
            return b;
        }

        // =========================================================================
        // WIDGET 4: SYSTEM TELEMETRY + SCROLLABLE QUICK DESKTOP TODO
        // =========================================================================
        private UIElement BuildSysTodoContent(double innerW, double innerH, double u)
        {
            _todoScaleU = u;
            DockPanel root = new DockPanel();

            // Top: 3-column compact System Telemetry Strip (CPU / RAM / C:), clickable to open Task Manager
            UniformGrid sysGrid = new UniformGrid();
            sysGrid.Columns = 3;
            sysGrid.Margin = new Thickness(0, 0, 0, Math.Round(14 * u));
            DockPanel.SetDock(sysGrid, Dock.Top);

            _sysMiniBarMaxW = Math.Max(40, Math.Floor((innerW - Math.Round(36 * u)) / 3.0) - Math.Round(20 * u));
            _sysCpuBox = BuildMiniMetricBox("CPU", BrCyan, u, out _sysCpuText, out _sysCpuBar);
            _sysRamBox = BuildMiniMetricBox("内存", BrViolet, u, out _sysRamText, out _sysRamBar);
            _sysDiskBox = BuildMiniMetricBox("C 盘", BrEmerald, u, out _sysDiskText, out _sysDiskBar);
            sysGrid.Children.Add(_sysCpuBox);
            sysGrid.Children.Add(_sysRamBox);
            sysGrid.Children.Add(_sysDiskBox);
            root.Children.Add(sysGrid);

            // Todo Header
            DockPanel todoHeader = new DockPanel { Margin = new Thickness(0, 0, 0, Math.Round(10 * u)) };
            DockPanel.SetDock(todoHeader, Dock.Top);

            StackPanel todoRight = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(todoRight, Dock.Right);

            _todoClearDoneBtn = CreateMiniChipButton("清理已完成", u, delegate
            {
                if (_config.TodoItems != null)
                {
                    _config.TodoItems.RemoveAll(delegate(TodoEntry e) { return e.Done; });
                    ConfigManager.Save(_config);
                    RebuildTodoRows();
                    if (_onTodosChanged != null) _onTodosChanged();
                }
            });
            _todoClearDoneBtn.Margin = new Thickness(0, 0, Math.Round(6 * u), 0);
            _todoClearDoneBtn.Visibility = Visibility.Collapsed;
            todoRight.Children.Add(_todoClearDoneBtn);

            Border addBtn = CreateMiniChipButton("+ 添加待办", u, delegate { if (_onAddTodo != null) _onAddTodo(); });
            todoRight.Children.Add(addBtn);
            todoHeader.Children.Add(todoRight);

            StackPanel todoLeft = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            TextBlock todoTitle = new TextBlock
            {
                Text = "快捷待办",
                FontFamily = FontDisplay,
                FontSize = Math.Round(17.5 * u),
                FontWeight = FontWeights.SemiBold,
                Foreground = BrTextPrimary,
                VerticalAlignment = VerticalAlignment.Center
            };
            todoLeft.Children.Add(todoTitle);

            _todoCountPill = new Border
            {
                CornerRadius = new CornerRadius(Math.Round(9 * u)),
                Background = FreezeBrush(40, 56, 189, 248),
                Padding = new Thickness(Math.Round(7 * u), Math.Round(1.5 * u), Math.Round(7 * u), Math.Round(1.5 * u)),
                Margin = new Thickness(Math.Round(8 * u), 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed
            };
            _todoCountText = new TextBlock { FontFamily = FontDisplay, FontSize = Math.Round(12 * u), FontWeight = FontWeights.SemiBold, Foreground = BrSkySoft };
            _todoCountPill.Child = _todoCountText;
            todoLeft.Children.Add(_todoCountPill);

            todoHeader.Children.Add(todoLeft);
            root.Children.Add(todoHeader);

            // Scrollable Todo Checklist Well
            Border well = new Border
            {
                CornerRadius = new CornerRadius(Math.Round(18 * u)),
                Background = BrInnerWell,
                BorderBrush = BrInnerWellBorder,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(Math.Round(10 * u), Math.Round(8 * u), Math.Round(8 * u), Math.Round(8 * u))
            };
            ScrollViewer todoSv = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            if (_thinScrollBarStyle != null)
            {
                todoSv.Resources.Add(typeof(ScrollBar), _thinScrollBarStyle);
            }
            new SmoothScrollDriver(todoSv);
            _todoListHost = new StackPanel();
            todoSv.Content = _todoListHost;
            well.Child = todoSv;
            root.Children.Add(well);

            RebuildTodoRows();
            return root;
        }

        private Border BuildMiniMetricBox(string label, Brush accent, double u, out TextBlock valueLbl, out Border barFill)
        {
            Border box = new Border
            {
                CornerRadius = new CornerRadius(Math.Round(14 * u)),
                Background = BrInnerWell,
                BorderBrush = BrInnerWellBorder,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(Math.Round(10 * u), Math.Round(8 * u), Math.Round(10 * u), Math.Round(8 * u)),
                Margin = new Thickness(Math.Round(3 * u), 0, Math.Round(3 * u), 0),
                Cursor = Cursors.Hand,
                RenderTransformOrigin = new Point(0.5, 0.5)
            };
            ScaleTransform st = new ScaleTransform(1.0, 1.0);
            box.RenderTransform = st;
            box.MouseEnter += delegate { box.Background = BrInnerWellHover; box.BorderBrush = BrInnerWellBorderHover; };
            box.MouseLeave += delegate { box.Background = BrInnerWell; box.BorderBrush = BrInnerWellBorder; QuickScale(st, 1.0, 100); };
            box.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { QuickScale(st, 0.96, 65); e.Handled = true; };
            box.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                QuickScale(st, 1.0, 100);
                try { Process.Start("taskmgr.exe"); } catch { }
                e.Handled = true;
            };

            StackPanel sp = new StackPanel();
            DockPanel top = new DockPanel();

            valueLbl = new TextBlock
            {
                Text = "--",
                FontFamily = FontDisplay,
                FontSize = Math.Round(14.5 * u),
                FontWeight = FontWeights.SemiBold,
                Foreground = BrTextPrimary
            };
            DockPanel.SetDock(valueLbl, Dock.Right);
            top.Children.Add(valueLbl);

            top.Children.Add(new TextBlock
            {
                Text = label,
                FontFamily = FontText,
                FontSize = Math.Round(12.5 * u),
                Foreground = BrTextMuted,
                VerticalAlignment = VerticalAlignment.Center
            });
            sp.Children.Add(top);

            Border track = new Border
            {
                Height = Math.Round(4.5 * u),
                CornerRadius = new CornerRadius(Math.Round(2.25 * u)),
                Background = BrTrackBg,
                Margin = new Thickness(0, Math.Round(7 * u), 0, 0)
            };
            barFill = new Border
            {
                Height = Math.Round(4.5 * u),
                Width = 0,
                HorizontalAlignment = HorizontalAlignment.Left,
                CornerRadius = new CornerRadius(Math.Round(2.25 * u)),
                Background = accent
            };
            track.Child = barFill;
            sp.Children.Add(track);
            box.Child = sp;
            return box;
        }

        public void RebuildTodoRows()
        {
            if (_todoListHost == null) return;
            _todoListHost.Children.Clear();
            double u = _todoScaleU;

            int pendingCount = 0;
            int doneCount = 0;
            if (_config.TodoItems != null)
            {
                for (int i = 0; i < _config.TodoItems.Count; i++)
                {
                    if (_config.TodoItems[i].Done) doneCount++;
                    else pendingCount++;
                }
            }

            if (_todoCountPill != null && _todoCountText != null)
            {
                if (pendingCount > 0)
                {
                    _todoCountText.Text = pendingCount + " 待完成";
                    _todoCountPill.Visibility = Visibility.Visible;
                }
                else
                {
                    _todoCountPill.Visibility = Visibility.Collapsed;
                }
            }
            if (_todoClearDoneBtn != null)
            {
                _todoClearDoneBtn.Visibility = doneCount > 0 ? Visibility.Visible : Visibility.Collapsed;
            }

            if (_config.TodoItems == null || _config.TodoItems.Count == 0)
            {
                Border emptyTap = new Border
                {
                    CornerRadius = new CornerRadius(Math.Round(14 * u)),
                    Background = Brushes.Transparent,
                    Padding = new Thickness(Math.Round(10 * u), Math.Round(24 * u), Math.Round(10 * u), Math.Round(24 * u)),
                    Cursor = Cursors.Hand,
                    VerticalAlignment = VerticalAlignment.Center
                };
                emptyTap.MouseEnter += delegate { emptyTap.Background = BrRowHover; };
                emptyTap.MouseLeave += delegate { emptyTap.Background = Brushes.Transparent; };
                emptyTap.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
                {
                    if (_onAddTodo != null) _onAddTodo();
                    e.Handled = true;
                };
                StackPanel empSp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                empSp.Children.Add(new Path
                {
                    Data = GeoCheckCircle,
                    Stroke = FreezeBrush(110, 148, 163, 184),
                    StrokeThickness = 1.4,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    Width = Math.Round(28 * u),
                    Height = Math.Round(28 * u),
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, Math.Round(10 * u))
                });
                empSp.Children.Add(new TextBlock
                {
                    Text = "暂无待办事项",
                    FontFamily = FontDisplay,
                    FontSize = Math.Round(15 * u),
                    FontWeight = FontWeights.Medium,
                    Foreground = BrTextMuted,
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                empSp.Children.Add(new TextBlock
                {
                    Text = "点击此处或右上角「+ 添加待办」随手记事",
                    FontFamily = FontText,
                    FontSize = Math.Round(12.5 * u),
                    Foreground = BrTextDim,
                    Margin = new Thickness(0, Math.Round(5 * u), 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                emptyTap.Child = empSp;
                _todoListHost.Children.Add(emptyTap);
                return;
            }

            for (int i = 0; i < _config.TodoItems.Count; i++)
            {
                TodoEntry entry = _config.TodoItems[i];
                int capturedIdx = i;

                Border row = new Border
                {
                    CornerRadius = new CornerRadius(Math.Round(10 * u)),
                    Padding = new Thickness(Math.Round(8 * u), Math.Round(6 * u), Math.Round(8 * u), Math.Round(6 * u)),
                    Background = Brushes.Transparent,
                    Cursor = Cursors.Hand
                };

                DockPanel dp = new DockPanel();
                double delSz = Math.Round(24 * u);
                Border delBtnWrap = new Border
                {
                    Width = delSz,
                    Height = delSz,
                    CornerRadius = new CornerRadius(delSz * 0.5),
                    Background = BrDelIdleBg,
                    BorderBrush = BrDelIdleBorder,
                    BorderThickness = new Thickness(0.8),
                    Margin = new Thickness(Math.Round(6 * u), 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Opacity = 0.72
                };
                Path delIcon = new Path
                {
                    Data = GeoCloseX,
                    Stroke = BrTextMuted,
                    StrokeThickness = Math.Max(1.4, Math.Round(1.5 * u * 10) / 10.0),
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    Width = Math.Round(9.5 * u),
                    Height = Math.Round(9.5 * u),
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                delBtnWrap.Child = delIcon;
                delBtnWrap.ToolTip = CreateSimpleDarkTip("删除此待办", u);
                delBtnWrap.MouseEnter += delegate
                {
                    delBtnWrap.Background = BrDelHoverBg;
                    delBtnWrap.BorderBrush = BrDelHoverBorder;
                    delIcon.Stroke = BrCoral;
                    delBtnWrap.Opacity = 1.0;
                };
                delBtnWrap.MouseLeave += delegate
                {
                    delBtnWrap.Background = BrDelIdleBg;
                    delBtnWrap.BorderBrush = BrDelIdleBorder;
                    delIcon.Stroke = BrTextMuted;
                    delBtnWrap.Opacity = row.IsMouseOver ? 0.94 : 0.72;
                };
                delBtnWrap.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; };
                delBtnWrap.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
                {
                    if (capturedIdx >= 0 && capturedIdx < _config.TodoItems.Count)
                    {
                        _config.TodoItems.RemoveAt(capturedIdx);
                        ConfigManager.Save(_config);
                        RebuildTodoRows();
                        if (_onTodosChanged != null) _onTodosChanged();
                    }
                    e.Handled = true;
                };
                DockPanel.SetDock(delBtnWrap, Dock.Right);
                dp.Children.Add(delBtnWrap);

                row.MouseEnter += delegate { row.Background = BrRowHover; delBtnWrap.Opacity = 0.94; };
                row.MouseLeave += delegate
                {
                    row.Background = Brushes.Transparent;
                    row.Opacity = 1.0;
                    delBtnWrap.Background = BrDelIdleBg;
                    delBtnWrap.BorderBrush = BrDelIdleBorder;
                    delIcon.Stroke = BrTextMuted;
                    delBtnWrap.Opacity = 0.72;
                };

                Border checkCircle = new Border
                {
                    Width = Math.Round(19 * u),
                    Height = Math.Round(19 * u),
                    CornerRadius = new CornerRadius(Math.Round(9.5 * u)),
                    Background = entry.Done ? FreezeBrush(48, 52, 211, 153) : Brushes.Transparent,
                    BorderBrush = entry.Done ? BrEmerald : BrCyan,
                    BorderThickness = new Thickness(Math.Max(1.3, 1.5 * u)),
                    Margin = new Thickness(0, 0, Math.Round(10 * u), 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                if (entry.Done)
                {
                    checkCircle.Child = new Path
                    {
                        Data = GeoCheckMark,
                        Stroke = BrEmerald,
                        StrokeThickness = 1.9,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round,
                        StrokeLineJoin = PenLineJoin.Round,
                        Width = Math.Round(10 * u),
                        Height = Math.Round(10 * u),
                        Stretch = Stretch.Uniform,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                }
                DockPanel.SetDock(checkCircle, Dock.Left);
                dp.Children.Add(checkCircle);

                TextBlock txt = new TextBlock
                {
                    Text = entry.Text,
                    FontFamily = FontText,
                    FontSize = Math.Round(14.5 * u),
                    Foreground = entry.Done ? BrTextDim : BrTextPrimary,
                    TextDecorations = entry.Done ? TextDecorations.Strikethrough : null,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                };
                dp.Children.Add(txt);

                row.Child = dp;
                row.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { row.Opacity = 0.78; e.Handled = true; };
                row.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
                {
                    row.Opacity = 1.0;
                    entry.Done = !entry.Done;
                    ConfigManager.Save(_config);
                    RebuildTodoRows();
                    if (_onTodosChanged != null) _onTodosChanged();
                    e.Handled = true;
                };

                _todoListHost.Children.Add(row);
            }
        }

        private Border CreateIconChipButton(UIElement icon, string tooltipText, double u, Action onClick)
        {
            Border b = new Border
            {
                CornerRadius = new CornerRadius(Math.Round(10 * u)),
                Background = BrPillBg,
                BorderBrush = BrPillBorder,
                BorderThickness = new Thickness(0.8),
                Padding = new Thickness(Math.Round(8 * u), Math.Round(5 * u), Math.Round(8 * u), Math.Round(5 * u)),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5),
                Child = icon
            };
            if (!string.IsNullOrEmpty(tooltipText)) b.ToolTip = CreateSimpleDarkTip(tooltipText, u);
            ScaleTransform st = new ScaleTransform(1.0, 1.0);
            b.RenderTransform = st;
            b.MouseEnter += delegate { b.Background = BrPillHover; };
            b.MouseLeave += delegate { b.Background = BrPillBg; QuickScale(st, 1.0, 100); };
            b.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { QuickScale(st, 0.92, 60); e.Handled = true; };
            b.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                QuickScale(st, 1.0, 100);
                if (onClick != null) onClick();
                e.Handled = true;
            };
            return b;
        }

        private Border CreateMiniChipButton(string text, double u, Action onClick)
        {
            return CreateMiniChipButton(text, u, onClick, BrPillBg, BrPillHover);
        }

        private Border CreateMiniChipButton(string text, double u, Action onClick, Brush normalBg, Brush hoverBg)
        {
            Border b = new Border
            {
                CornerRadius = new CornerRadius(Math.Round(10 * u)),
                Background = normalBg,
                BorderBrush = BrPillBorder,
                BorderThickness = new Thickness(0.8),
                Padding = new Thickness(Math.Round(10 * u), Math.Round(4.5 * u), Math.Round(10 * u), Math.Round(4.5 * u)),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5)
            };
            ScaleTransform st = new ScaleTransform(1.0, 1.0);
            b.RenderTransform = st;
            b.Child = new TextBlock
            {
                Text = text,
                FontFamily = FontText,
                FontSize = Math.Round(12.5 * u),
                FontWeight = FontWeights.Medium,
                Foreground = BrTextPrimary
            };
            b.MouseEnter += delegate { b.Background = hoverBg; };
            b.MouseLeave += delegate { b.Background = normalBg; QuickScale(st, 1.0, 100); };
            b.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { QuickScale(st, 0.94, 60); e.Handled = true; };
            b.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                QuickScale(st, 1.0, 100);
                if (onClick != null) onClick();
                e.Handled = true;
            };
            return b;
        }

        private static ToolTip CreateSimpleDarkTip(string text, double u)
        {
            ToolTip tt = new ToolTip
            {
                Content = text,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                HasDropShadow = true
            };
            ControlTemplate tpl = new ControlTemplate(typeof(ToolTip));
            FrameworkElementFactory bd = new FrameworkElementFactory(typeof(Border));
            bd.SetValue(Border.BackgroundProperty, FreezeBrush(244, 15, 22, 37));
            bd.SetValue(Border.BorderBrushProperty, FreezeBrush(82, 255, 255, 255));
            bd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(Math.Round(10 * u)));
            bd.SetValue(Border.PaddingProperty, new Thickness(Math.Round(11 * u), Math.Round(5.5 * u), Math.Round(11 * u), Math.Round(5.5 * u)));

            FrameworkElementFactory cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(TextBlock.ForegroundProperty, BrWhite);
            cp.SetValue(TextBlock.FontFamilyProperty, FontText);
            cp.SetValue(TextBlock.FontSizeProperty, Math.Round(13.5 * u));
            cp.SetValue(TextBlock.FontWeightProperty, FontWeights.Medium);
            bd.AppendChild(cp);
            tpl.VisualTree = bd;
            tt.Template = tpl;
            return tt;
        }

        private void QuickScale(ScaleTransform st, double to, int ms)
        {
            if (st == null) return;
            DoubleAnimation a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = EaseOutCubic };
            Timeline.SetDesiredFrameRate(a, AnimFps);
            st.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }

        // =========================================================================
        // LIVE UI UPDATE (IN-PLACE, ZERO FLICKER!)
        // =========================================================================
        public void UpdateLiveData()
        {
            bool clashConnected = (_clash != null && _clash.IsConnected);
            SystemStatsSnapshot sysSnap = (_sys != null) ? _sys.Sample(!clashConnected) : null;
            if (sysSnap != null && _clash != null && !clashConnected)
            {
                _clash.PushFallbackSpeed(sysSnap.NetDownBps, sysSnap.NetUpBps);
            }

            UpdateClashUi();
            UpdateMailUi();
            UpdateMusicUi();
            UpdateSysUi(sysSnap);
        }

        private void UpdateClashUi()
        {
            if (_clashProfileText == null || _clash == null) return;
            ClashSnapshot snap = _clash.GetSnapshot();

            _clashProfileText.Text = string.IsNullOrEmpty(snap.ProfileName) ? "Clash Verge" : snap.ProfileName;
            _clashStatusDot.Fill = snap.IsConnected ? BrEmerald : BrAmber;
            if (_clashStatusGlow != null)
            {
                _clashStatusGlow.Fill = snap.IsConnected ? BrGlowConnected : BrGlowOffline;
            }
            HighlightClashMode(snap.Mode);

            if (snap.IsConnected)
            {
                string rawNode = string.IsNullOrEmpty(snap.ActiveNode) ? snap.ActiveGroup : snap.ActiveNode;
                string reg, cln;
                ClashService.ParseRegionAndCleanNodeName(rawNode, out reg, out cln);
                _clashNodeText.Text = cln;
                if (_clashRegionBadge != null && _clashRegionText != null)
                {
                    if (!string.IsNullOrEmpty(reg))
                    {
                        _clashRegionText.Text = reg;
                        _clashRegionBadge.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        _clashRegionBadge.Visibility = Visibility.Collapsed;
                    }
                }
            }
            else
            {
                _clashNodeText.Text = "代理核心未连接（显示系统网速）";
                if (_clashRegionBadge != null) _clashRegionBadge.Visibility = Visibility.Collapsed;
            }

            if (snap.IsConnected && snap.ActiveDelayMs > 0)
            {
                _clashDelayText.Text = snap.ActiveDelayMs + " ms";
                if (snap.ActiveDelayMs < 150)
                {
                    _clashDelayText.Foreground = BrEmerald;
                    _clashDelayPill.Background = BrDelayFastBg;
                }
                else if (snap.ActiveDelayMs < 350)
                {
                    _clashDelayText.Foreground = BrAmber;
                    _clashDelayPill.Background = BrDelayMedBg;
                }
                else
                {
                    _clashDelayText.Foreground = BrCoral;
                    _clashDelayPill.Background = BrDelaySlowBg;
                }
                _clashDelayPill.Visibility = Visibility.Visible;
            }
            else if (snap.IsConnected)
            {
                _clashDelayText.Text = "测速";
                _clashDelayText.Foreground = BrTextMuted;
                _clashDelayPill.Background = BrPillBg;
                _clashDelayPill.Visibility = Visibility.Visible;
            }
            else
            {
                _clashDelayPill.Visibility = Visibility.Collapsed;
            }

            _clashTunPill.Visibility = (snap.IsConnected && snap.TunEnabled) ? Visibility.Visible : Visibility.Collapsed;

            _clashDownText.Text = "↓ " + ClashService.FormatSpeed(snap.DownBps);
            _clashUpText.Text = "↑ " + ClashService.FormatSpeed(snap.UpBps);
            _clashSessionText.Text = snap.SessionTotalBytes > 0
                ? ("本次 " + ClashService.FormatBytes(snap.SessionTotalBytes))
                : "";

            // Update subscription remaining bar
            if (snap.SubTotalBytes > 0)
            {
                long remain = Math.Max(0, snap.SubTotalBytes - snap.SubUsedBytes);
                double remainRatio = (double)remain / snap.SubTotalBytes;
                _clashSubLeftText.Text = string.Format("剩余 {0} / {1}",
                    ClashService.FormatBytes(remain), ClashService.FormatBytes(snap.SubTotalBytes));
                _clashSubBarFill.Width = Math.Round(_clashSubBarMaxW * Math.Max(0.02, Math.Min(1.0, remainRatio)));

                if (snap.SubExpireUnix > 0)
                {
                    DateTime exp = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(snap.SubExpireUnix).ToLocalTime();
                    _clashSubExpireText.Text = "到期 " + exp.ToString("yyyy-MM-dd");
                }
            }
            else
            {
                _clashSubLeftText.Text = snap.IsConnected ? ("活跃连接 " + snap.ActiveConnections) : "系统网卡实时监测";
                _clashSubBarFill.Width = 0;
            }

            // Update 36-point pro sparkline
            if (_clashDownLine != null && snap.DownHistory != null && snap.DownHistory.Length > 1)
            {
                long maxVal = 64 * 1024;
                for (int i = 0; i < snap.DownHistory.Length; i++)
                {
                    if (snap.DownHistory[i] > maxVal) maxVal = snap.DownHistory[i];
                    if (snap.UpHistory[i] > maxVal) maxVal = snap.UpHistory[i];
                }
                if (_clashPeakLabel != null)
                {
                    _clashPeakLabel.Text = ClashService.FormatSpeed(maxVal);
                }

                int n = snap.DownHistory.Length;
                PointCollection downPts = new PointCollection(n);
                PointCollection downAreaPts = new PointCollection(n + 2);
                PointCollection upPts = new PointCollection(n);
                PointCollection upAreaPts = new PointCollection(n + 2);

                double stepX = _sparkW / Math.Max(1, n - 1);
                double lastDy = _sparkH - 2;
                downAreaPts.Add(new Point(0, _sparkH));
                upAreaPts.Add(new Point(0, _sparkH));

                for (int i = 0; i < n; i++)
                {
                    double px = i * stepX;
                    double dy = _sparkH - Math.Max(2.5, (_sparkH - 6) * ((double)snap.DownHistory[i] / maxVal));
                    double uy = _sparkH - Math.Max(1.8, (_sparkH - 6) * ((double)snap.UpHistory[i] / maxVal));
                    downPts.Add(new Point(px, dy));
                    downAreaPts.Add(new Point(px, dy));
                    upPts.Add(new Point(px, uy));
                    upAreaPts.Add(new Point(px, uy));
                    lastDy = dy;
                }
                downAreaPts.Add(new Point(_sparkW, _sparkH));
                upAreaPts.Add(new Point(_sparkW, _sparkH));

                _clashDownLine.Points = downPts;
                _clashDownArea.Points = downAreaPts;
                _clashUpLine.Points = upPts;
                if (_clashUpArea != null) _clashUpArea.Points = upAreaPts;

                if (_clashDownDot != null)
                {
                    if (snap.DownBps > 0 || snap.UpBps > 0)
                    {
                        Canvas.SetLeft(_clashDownDot, Math.Max(0, _sparkW - _clashDownDot.Width));
                        Canvas.SetTop(_clashDownDot, Math.Max(0, Math.Min(_sparkH - _clashDownDot.Height, lastDy - _clashDownDot.Height * 0.5)));
                        _clashDownDot.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        _clashDownDot.Visibility = Visibility.Collapsed;
                    }
                }
            }
        }

        internal static string ComputeMailRenderSignature(MailSnapshot snap)
        {
            if (snap == null) return "";
            System.Text.StringBuilder sb = new System.Text.StringBuilder(256);
            sb.Append(snap.UnreadCount).Append('|')
              .Append(snap.IsLoading ? '1' : '0').Append('|');
            int total = snap.Messages != null ? snap.Messages.Count : 0;
            sb.Append(total).Append('|');
            if (total == 0)
            {
                sb.Append(snap.StatusMessage ?? "");
            }
            else
            {
                int maxRows = Math.Min(12, total);
                for (int i = 0; i < maxRows; i++)
                {
                    MailMessageSummary m = snap.Messages[i];
                    if (m == null) continue;
                    sb.Append(m.IsUnread ? 'U' : 'R').Append('\x1F')
                      .Append(m.Sender ?? "").Append('\x1F')
                      .Append(m.Subject ?? "").Append('\x1F')
                      .Append(m.TimeText ?? "").Append('\x1F')
                      .Append(m.ThreadUrl ?? "").Append('\x1E');
                }
            }
            return sb.ToString();
        }

        private void UpdateMailUi()
        {
            if (_mailListHost == null || _mail == null) return;
            MailSnapshot snap = _mail.GetSnapshot();

            if (!snap.IsConfigured)
            {
                _mailUnreadPill.Visibility = Visibility.Collapsed;
                _mailSetupView.Visibility = Visibility.Visible;
                if (_mailListWell != null) _mailListWell.Visibility = Visibility.Collapsed;
                _mailStatusText.Visibility = Visibility.Collapsed;
                return;
            }

            _mailStatusText.Visibility = Visibility.Visible;
            _mailStatusText.Text = string.IsNullOrEmpty(snap.AccountEmail)
                ? snap.StatusMessage
                : (snap.AccountEmail + "  ·  " + snap.StatusMessage);

            _mailSetupView.Visibility = Visibility.Collapsed;
            if (_mailListWell != null) _mailListWell.Visibility = Visibility.Visible;

            if (snap.UnreadCount > 0)
            {
                _mailUnreadText.Text = snap.UnreadCount + " 未读";
                _mailUnreadPill.Visibility = Visibility.Visible;
            }
            else
            {
                _mailUnreadPill.Visibility = Visibility.Collapsed;
            }

            string sig = ComputeMailRenderSignature(snap);
            if (sig == _lastMailRenderSig) return;
            _lastMailRenderSig = sig;

            _mailListHost.Children.Clear();
            double u = _mailScaleU;

            if (snap.Messages == null || snap.Messages.Count == 0)
            {
                _mailListHost.Children.Add(new TextBlock
                {
                    Text = snap.IsLoading ? "正在同步最新邮件..." : snap.StatusMessage,
                    FontFamily = FontText,
                    FontSize = Math.Round(15 * u),
                    Foreground = BrTextMuted,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(Math.Round(10 * u), Math.Round(22 * u), Math.Round(10 * u), 0)
                });
                return;
            }

            int maxRows = Math.Min(12, snap.Messages.Count);
            for (int i = 0; i < maxRows; i++)
            {
                MailMessageSummary msg = snap.Messages[i];
                if (msg == null) continue;
                Border row = new Border
                {
                    CornerRadius = new CornerRadius(Math.Round(10 * u)),
                    Padding = new Thickness(Math.Round(10 * u), Math.Round(7 * u), Math.Round(10 * u), Math.Round(7 * u)),
                    Background = Brushes.Transparent,
                    Cursor = Cursors.Hand
                };
                row.MouseEnter += delegate { row.Background = BrRowHover; };
                row.MouseLeave += delegate { row.Background = Brushes.Transparent; row.Opacity = 1.0; };
                row.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { row.Opacity = 0.78; e.Handled = true; };
                string threadUrl = msg.ThreadUrl;
                row.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
                {
                    row.Opacity = 1.0;
                    if (!string.IsNullOrEmpty(threadUrl))
                    {
                        try { Process.Start(threadUrl); }
                        catch { if (_onOpenMailApp != null) _onOpenMailApp(); }
                    }
                    else if (_onOpenMailApp != null)
                    {
                        _onOpenMailApp();
                    }
                    e.Handled = true;
                };

                StackPanel rowSp = new StackPanel();
                DockPanel topLine = new DockPanel();
                if (!string.IsNullOrEmpty(msg.TimeText))
                {
                    TextBlock tLbl = new TextBlock
                    {
                        Text = msg.TimeText,
                        FontFamily = FontDisplay,
                        FontSize = Math.Round(11.5 * u),
                        Foreground = BrTextDim,
                        Margin = new Thickness(Math.Round(8 * u), 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    DockPanel.SetDock(tLbl, Dock.Right);
                    topLine.Children.Add(tLbl);
                }

                Ellipse dot = new Ellipse
                {
                    Width = Math.Round(7 * u),
                    Height = Math.Round(7 * u),
                    Fill = msg.IsUnread ? BrCyan : Brushes.Transparent,
                    Margin = new Thickness(0, 0, Math.Round(8 * u), 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                DockPanel.SetDock(dot, Dock.Left);
                topLine.Children.Add(dot);

                TextBlock sender = new TextBlock
                {
                    Text = msg.Sender,
                    FontFamily = FontDisplay,
                    FontSize = Math.Round(13.5 * u),
                    FontWeight = msg.IsUnread ? FontWeights.SemiBold : FontWeights.Medium,
                    Foreground = msg.IsUnread ? BrWhite : BrTextMuted,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                };
                topLine.Children.Add(sender);
                rowSp.Children.Add(topLine);

                TextBlock subj = new TextBlock
                {
                    Text = msg.Subject,
                    FontFamily = FontText,
                    FontSize = Math.Round(13 * u),
                    Foreground = msg.IsUnread ? BrTextPrimary : BrTextDim,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(Math.Round(15 * u), Math.Round(2.5 * u), 0, 0)
                };
                rowSp.Children.Add(subj);

                row.Child = rowSp;
                _mailListHost.Children.Add(row);

                if (i < maxRows - 1)
                {
                    _mailListHost.Children.Add(new Border
                    {
                        Height = 1,
                        Background = BrRowDivider,
                        Margin = new Thickness(Math.Round(10 * u), Math.Round(1 * u), Math.Round(10 * u), Math.Round(1 * u)),
                        IsHitTestVisible = false
                    });
                }
            }
        }

        private void UpdateMusicUi()
        {
            if (_musicTitleText == null || _media == null) return;
            MediaSnapshot snap = _media.GetSnapshot();

            _musicTitleText.Text = snap.Title;
            _musicArtistText.Text = snap.Artist;

            if (_musicPlayPath != null && snap.IsPlaying != _lastMusicPlayingState)
            {
                _lastMusicPlayingState = snap.IsPlaying;
                _musicPlayPath.Data = snap.IsPlaying ? GeoPause : GeoPlay;
            }

            if (!string.IsNullOrEmpty(snap.SourceApp))
            {
                _musicAppBadgeText.Text = snap.SourceApp;
                _musicAppBadge.Visibility = Visibility.Visible;
            }
            else
            {
                _musicAppBadge.Visibility = Visibility.Collapsed;
            }

            if (snap.CoverArt != _lastCoverRef)
            {
                _lastCoverRef = snap.CoverArt;
                if (snap.CoverArt != null && _musicCoverArtBorder != null)
                {
                    ImageBrush coverBrush = new ImageBrush(snap.CoverArt) { Stretch = Stretch.UniformToFill };
                    coverBrush.Freeze();
                    _musicCoverArtBorder.Background = coverBrush;
                    _musicCoverArtBorder.Visibility = Visibility.Visible;
                    _musicCoverFallback.Visibility = Visibility.Collapsed;
                }
                else if (_musicCoverArtBorder != null)
                {
                    _musicCoverArtBorder.Background = null;
                    _musicCoverArtBorder.Visibility = Visibility.Collapsed;
                    _musicCoverFallback.Visibility = Visibility.Visible;
                }
            }

            string targetCurLyric;
            if (snap.HasSyncedLyrics)
            {
                _musicPrevLyric.Text = snap.PrevLyric;
                targetCurLyric = string.IsNullOrEmpty(snap.CurrentLyric) ? "♪  ·  ·  ·  ♪" : snap.CurrentLyric;
                _musicNextLyric.Text = snap.NextLyric;
            }
            else if (snap.HasSession)
            {
                _musicPrevLyric.Text = "";
                targetCurLyric = string.IsNullOrEmpty(snap.LyricStatus) ? "♪ 正在播放" : snap.LyricStatus;
                _musicNextLyric.Text = snap.Title;
            }
            else
            {
                _musicPrevLyric.Text = "";
                targetCurLyric = "播放音乐后自动同步实时滚动歌词";
                _musicNextLyric.Text = "点击左侧封面快速打开音乐播放器";
            }

            if (_musicCurLyricPill != null)
            {
                _musicCurLyricPill.Background = snap.HasSyncedLyrics ? (Brush)BrLyricActiveBg : Brushes.Transparent;
                _musicCurLyricPill.BorderBrush = snap.HasSyncedLyrics ? (Brush)BrLyricActiveBorder : Brushes.Transparent;
            }

            if (targetCurLyric != _lastDisplayedLyric)
            {
                bool animateLyric = !string.IsNullOrEmpty(_lastDisplayedLyric) && snap.HasSyncedLyrics;
                _lastDisplayedLyric = targetCurLyric;
                _musicCurLyric.Text = targetCurLyric;
                if (animateLyric && _musicCurLyricSlide != null)
                {
                    DoubleAnimation slideAnim = new DoubleAnimation(Math.Round(10 * _musicScaleU), 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = EaseOutCubic };
                    DoubleAnimation fadeAnim = new DoubleAnimation(0.25, 1.0, TimeSpan.FromMilliseconds(240)) { EasingFunction = EaseOutCubic };
                    Timeline.SetDesiredFrameRate(slideAnim, AnimFps);
                    Timeline.SetDesiredFrameRate(fadeAnim, AnimFps);
                    _musicCurLyricSlide.BeginAnimation(TranslateTransform.YProperty, slideAnim);
                    if (_musicCurLyricPill != null) _musicCurLyricPill.BeginAnimation(UIElement.OpacityProperty, fadeAnim);
                    else _musicCurLyric.BeginAnimation(UIElement.OpacityProperty, fadeAnim);
                }
            }

            _musicTimeLeft.Text = FormatTime(snap.PositionSeconds);
            _musicTimeRight.Text = FormatTime(snap.DurationSeconds);
            if (snap.DurationSeconds > 1)
            {
                double r = Math.Max(0.0, Math.Min(1.0, snap.PositionSeconds / snap.DurationSeconds));
                _musicProgFill.Width = Math.Round(_musicProgMaxW * r);
            }
            else
            {
                _musicProgFill.Width = 0;
            }
        }

        private void UpdateSysUi(SystemStatsSnapshot snap)
        {
            if (_sysCpuText == null || snap == null) return;

            _sysCpuText.Text = string.Format("{0:F0}%", snap.CpuPercent);
            _sysCpuBar.Width = Math.Round(_sysMiniBarMaxW * Math.Max(0.03, Math.Min(1.0, snap.CpuPercent / 100.0)));
            _sysCpuBar.Background = snap.CpuPercent >= 90 ? BrCoral : (snap.CpuPercent >= 75 ? BrAmber : BrCyan);

            _sysRamText.Text = string.Format("{0:F0}%", snap.RamPercent);
            _sysRamBar.Width = Math.Round(_sysMiniBarMaxW * Math.Max(0.03, Math.Min(1.0, snap.RamPercent / 100.0)));
            _sysRamBar.Background = snap.RamPercent >= 90 ? BrCoral : (snap.RamPercent >= 80 ? BrAmber : BrViolet);

            _sysDiskText.Text = string.Format("{0:F0}G", snap.DiskFreeGb);
            _sysDiskBar.Width = Math.Round(_sysMiniBarMaxW * Math.Max(0.03, Math.Min(1.0, snap.DiskUsedPercent / 100.0)));
            _sysDiskBar.Background = snap.DiskUsedPercent >= 92 ? BrCoral : (snap.DiskUsedPercent >= 85 ? BrAmber : BrEmerald);

            if (_sysCpuBox != null && _sysCpuBox.ToolTip == null)
            {
                _sysCpuBox.ToolTip = CreateSimpleDarkTip("点击打开任务管理器", _todoScaleU);
            }
            if (_sysRamBox != null && snap.RamTotalGb > 0)
            {
                string ramTip = string.Format("内存已用 {0:F1} GB / {1:F1} GB (点击打开任务管理器)", snap.RamUsedGb, snap.RamTotalGb);
                if (_sysRamTip == null)
                {
                    _sysRamTip = CreateSimpleDarkTip(ramTip, _todoScaleU);
                    _sysRamBox.ToolTip = _sysRamTip;
                    _lastRamTipText = ramTip;
                }
                else if (ramTip != _lastRamTipText)
                {
                    _sysRamTip.Content = ramTip;
                    _lastRamTipText = ramTip;
                }
            }
            if (_sysDiskBox != null && snap.DiskTotalGb > 0)
            {
                string diskTip = string.Format("C 盘可用 {0:F1} GB / {1:F0} GB (点击打开任务管理器)", snap.DiskFreeGb, snap.DiskTotalGb);
                if (_sysDiskTip == null)
                {
                    _sysDiskTip = CreateSimpleDarkTip(diskTip, _todoScaleU);
                    _sysDiskBox.ToolTip = _sysDiskTip;
                    _lastDiskTipText = diskTip;
                }
                else if (diskTip != _lastDiskTipText)
                {
                    _sysDiskTip.Content = diskTip;
                    _lastDiskTipText = diskTip;
                }
            }
        }

        private static string FormatTime(double sec)
        {
            if (sec < 0 || double.IsNaN(sec)) sec = 0;
            int total = (int)Math.Floor(sec);
            int m = total / 60;
            int s = total % 60;
            return string.Format("{0:D2}:{1:D2}", m, s);
        }
    }
}
