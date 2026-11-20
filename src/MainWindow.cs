using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

namespace LumeDesk
{
    internal class SmoothScrollDriver
    {
        private readonly ScrollViewer _sv;
        private double _targetY;
        private double _currentY;
        private bool _animating;
        private long _lastTimestamp;

        public SmoothScrollDriver(ScrollViewer sv)
        {
            _sv = sv;
            _sv.PreviewMouseWheel += OnWheel;
            _sv.PreviewMouseLeftButtonDown += delegate { Stop(); };
            _sv.PreviewKeyDown += delegate { Stop(); };
            _sv.IsVisibleChanged += delegate { if (!_sv.IsVisible) Stop(); };
            _sv.Unloaded += delegate { Stop(); };
        }

        public void ResetToTop()
        {
            Stop();
            _targetY = 0;
            _currentY = 0;
            _sv.ScrollToTop();
        }

        private void OnWheel(object sender, MouseWheelEventArgs e)
        {
            if (_sv.ScrollableHeight <= 0.5) return;
            if (!_animating)
            {
                _currentY = _sv.VerticalOffset;
                _targetY = _currentY;
                _lastTimestamp = Stopwatch.GetTimestamp();
                _animating = true;
                CompositionTarget.Rendering += OnRenderFrame;
            }
            _targetY -= e.Delta * 0.92;
            if (_targetY < 0) _targetY = 0;
            if (_targetY > _sv.ScrollableHeight) _targetY = _sv.ScrollableHeight;
            e.Handled = true;
        }

        private void OnRenderFrame(object sender, EventArgs e)
        {
            long now = Stopwatch.GetTimestamp();
            double dt = (double)(now - _lastTimestamp) / Stopwatch.Frequency;
            _lastTimestamp = now;
            if (dt <= 0.0002) dt = 0.00625;
            if (dt > 0.05) dt = 0.05;

            double maxY = _sv.ScrollableHeight;
            if (_targetY > maxY) _targetY = maxY;
            if (_targetY < 0) _targetY = 0;

            double diff = _targetY - _currentY;
            if (Math.Abs(diff) < 0.30)
            {
                _currentY = _targetY;
                _sv.ScrollToVerticalOffset(_currentY);
                Stop();
                return;
            }

            double factor = 1.0 - Math.Exp(-dt * 18.0);
            _currentY += diff * factor;
            _sv.ScrollToVerticalOffset(_currentY);
        }

        public void Stop()
        {
            if (_animating)
            {
                _animating = false;
                CompositionTarget.Rendering -= OnRenderFrame;
            }
        }
    }

    public class MainWindow : Window
    {
        private class SearchTileItem
        {
            public AppItem App;
            public StackPanel Cell;
            public Border Highlight;
            public bool IsTopHit;
        }

        private class CachedFolderModalView
        {
            public FolderGroup Group;
            public Grid Root;
            public ScaleTransform Scale;
            public TranslateTransform Translate;
            public ScrollViewer ScrollHost;
            public bool IsInlineRenaming;
            public TextBox InlineRenameBox;
            public Action BeginInlineRename;
            public Action SubmitInlineRename;
            public Action CancelInlineRename;
        }

        private class DragAppTarget
        {
            public AppItem Item;
            public FolderGroup Group;
            public List<AppItem> OwnerList;
            public FrameworkElement Host;
            public Border Highlight;
            public ScaleTransform Scale;
            public ScrollViewer ScrollHost;
            public bool IsModal;
        }

        private class DragCardTarget
        {
            public FolderGroup Group;
            public Grid CardRoot;
            public Border OuterBorder;
            public Brush NormalRim;
            public double NormalBorderW;
            public Rect Bounds;
        }

        internal struct DockCapsuleMetrics
        {
            public int Count;
            public double IconSize;
            public double PadX;
            public double PadY;
            public double Gap;
            public double DotBandH;
            public double DotOffsetY;
            public double DotW;
            public double DotH;
            public double CapsuleW;
            public double CapsuleH;
            public double Radius;
        }

        private class DockTileRef
        {
            public AppItem Item;
            public string TargetExe;
            public ScaleTransform Scale;
            public TranslateTransform Translate;
            public Border RunningDot;
            public double CenterX;
            public double TopY;
            public double LastTargetScale = 1.0;
            public double LastTargetLift = 0.0;
        }

        private static readonly FontFamily FontDisplay = new FontFamily("Segoe UI, Microsoft YaHei UI");
        private static readonly FontFamily FontText = new FontFamily("Segoe UI, Microsoft YaHei UI");

        private AppConfig _config;
        private MonitorInfoItem _monitor;
        private WallpaperBundle _wallpaper;
        private WorkspaceModel _workspace;

        private ClashService _clashService;
        private MediaLyricsService _mediaService;
        private ImapMailService _mailService;
        private SystemStatsService _sysService;
        private WidgetDeck _widgetDeck;
        private DispatcherTimer _widgetTimer;
        private int _widgetTickCount;
        private int _runningProcPollInFlight;

        private Grid _rootGrid;
        private Image _wallpaperImage;
        private ScaleTransform _wallpaperScaleTransform;
        private Canvas _desktopCanvas;
        private TranslateTransform _desktopTranslateTransform;

        private Grid _modalOverlay;
        private Border _modalBackdrop;
        private Grid _modalContentHost;
        private Grid _dialogContentHost;
        private FrameworkElement _activeModalRoot;
        private ScaleTransform _activeModalScale;
        private TranslateTransform _activeModalTranslate;
        private CachedFolderModalView _activeModalView;
        private double _modalOriginDx;
        private double _modalOriginDy;
        private int _modalAnimToken;
        private bool _modalOverlayActive;
        private bool _modalIsCachedFolder;
        private Dictionary<string, CachedFolderModalView> _folderModalCache = new Dictionary<string, CachedFolderModalView>(StringComparer.OrdinalIgnoreCase);
        private bool _folderModalsDirty = true;

        private Grid _searchOverlay;
        private Border _searchBackdrop;
        private Grid _searchContentHost;
        private FrameworkElement _searchInputRoot;
        private ScaleTransform _searchInputScale;
        private TranslateTransform _searchInputTranslate;
        private FrameworkElement _searchResultsRoot;
        private ScaleTransform _searchResultsScale;
        private TranslateTransform _searchResultsTranslate;
        private ScrollViewer _searchScrollViewer;
        private SmoothScrollDriver _searchSmoothScroll;
        private Border _searchTopFade;
        private Border _searchBottomFade;
        private Border _searchClearBtn;
        private TextBlock _searchFooterHint;
        private TextBlock _searchEmptyHint;
        private double _searchAvailViewportH;
        private double _searchInnerPanelW;
        private List<SearchTileItem> _searchTilePool = new List<SearchTileItem>();
        private List<SearchTileItem> _visibleSearchTiles = new List<SearchTileItem>();
        private int _searchSelectedIndex = 0;
        private bool _searchHasSelectionHighlight = false;
        private int _searchColumns = 5;
        private bool _searchCacheDirty = true;
        private double _searchBuiltW = -1;
        private double _searchBuiltH = -1;
        private bool _searchBuiltShowClock;
        private double _searchBuiltBottomTopY = -1;
        private int _searchAnimToken;
        private bool _searchOverlayActive;
        private Action _detachPendingModalScrollRestore;

        private TextBox _searchBox;
        private TextBlock _searchPlaceholder;
        private UniformGrid _searchResultsPanel;
        private List<AppItem> _currentSearchMatches = new List<AppItem>();
        private Dictionary<string, Rect> _folderCardRects = new Dictionary<string, Rect>(StringComparer.OrdinalIgnoreCase);

        // GPU Floating Drag-and-Drop Overlay + Targets
        private Canvas _dragOverlayCanvas;
        private bool _isPointerDragging;
        private AppItem _dragApp;
        private List<AppItem> _dragAppOwnerList;
        private bool _dragAppFromModal;
        private FrameworkElement _dragSourceHost;
        private ScaleTransform _dragSourceScale;
        private FolderGroup _dragFolder;
        private Grid _dragFolderCardRoot;
        private TranslateTransform _dragGhostTranslate;
        private double _dragGhostHalfW;
        private double _dragGhostHalfH;
        private Border _dragBadgePill;
        private TextBlock _dragBadgeText;
        private TranslateTransform _dragBadgeTranslate;
        private bool _modalDragAutoScrollActive;
        private long _modalDragAutoScrollLastTs;
        private double _modalDragScrollVelocity;
        private List<DragAppTarget> _desktopAppTargets = new List<DragAppTarget>();
        private Dictionary<string, List<DragAppTarget>> _modalAppTargets = new Dictionary<string, List<DragAppTarget>>(StringComparer.OrdinalIgnoreCase);
        private List<DragCardTarget> _desktopCardTargets = new List<DragCardTarget>();
        private DragAppTarget _hoveredAppTarget;
        private DragCardTarget _hoveredCardTarget;
        private bool _hoveredDockTarget;
        private Border _landscapeDockGlass;
        private Brush _landscapeDockNormalRim;
        private double _landscapeDockNormalBorderW = 1.2;
        private Rect _landscapeDockRect = Rect.Empty;
        private double _lastDockTopY = -1;
        private double _lastSearchPillTopY = -1;

        // macOS Dock Wave + Floating Label + Running Indicators
        private List<DockTileRef> _dockTileRefs = new List<DockTileRef>();
        private Border _dockHoverLabelPill;
        private TextBlock _dockHoverLabelText;
        private FrameworkElement _searchPillElement;
        private bool _dockWaveActive;
        private int _lastDockHoverIndex = -1;
        private double _lastDockWavePointerX = double.NaN;

        private FolderGroup _openFolder;
        private FileSystemWatcher _dataWatcher;
        private FileSystemWatcher _themeWatcher;
        private DispatcherTimer _reloadDebounce;
        private DateTime _lastManualReloadUtc = DateTime.MinValue;
        private DispatcherTimer _wallpaperDebounce;
        private DispatcherTimer _clockTimer;
        private TextBlock _clockTimeText;
        private TextBlock _clockTimeShadow;
        private TextBlock _clockDateText;
        private TextBlock _clockDateShadow;

        private bool _isPreviewMode;
        private double _canvasW = 1080;
        private double _canvasH = 1872;
        private int _currentPage = 0;
        private int _totalPages = 1;
        private DateTime _lastWheelPageSwitch = DateTime.MinValue;

        private Style _darkMenuItemStyle;
        private Style _darkSeparatorStyle;
        private Style _thinScrollBarStyle;

        // Frozen GPU-friendly shared resources (zero per-click heap/render-thread churn!)
        private ExponentialEase _easeOutExpo;
        private CubicEase _easeOutCubic;
        private BackEase _easeOutBackSoft;
        private SolidColorBrush _brushModalScrim;
        private SolidColorBrush _brushSearchScrim;
        private SolidColorBrush _brushHoverTile;
        private SolidColorBrush _brushTopHitTile;
        private SolidColorBrush _brushTopHitBorder;
        private SolidColorBrush _brushDropRim;
        private SolidColorBrush _brushHalo1;
        private SolidColorBrush _brushModalIconShadow;
        private Geometry _magGeometry;
        private Geometry _closeGeometry;
        private Geometry _checkGeometry;
        private static readonly Dictionary<string, DropShadowEffect> ShadowEffectCache = new Dictionary<string, DropShadowEffect>();

        public event Action RequestExit;

        public MainWindow(AppConfig config, bool isPreviewMode)
        {
            _config = config;
            _isPreviewMode = isPreviewMode;

            Title = "LumeDesk";
            WindowStyle = WindowStyle.None;
            // Hardware-accelerated Direct3D9Ex SwapChain (NO WS_EX_LAYERED / UpdateLayeredWindow 32MB CPU readback!)
            // Renders native 1:1 4K monitor wallpaper directly on GPU for locked 60Hz-240Hz silky transitions.
            AllowsTransparency = false;
            Background = CreateFrozenBrush(255, 10, 14, 24);
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Topmost = false;
            FontFamily = FontText;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);

            _monitor = MonitorHelper.PickTargetMonitor(_config.TargetMonitor);
            if (_monitor != null)
            {
                _canvasW = _monitor.LogicalWorkWidth;
                _canvasH = _monitor.LogicalWorkHeight;
                // Place initial creation coordinates onto the target monitor so Windows initializes it at that monitor's DPI!
                Left = _monitor.WorkLeft;
                Top = _monitor.WorkTop;
            }

            Width = _canvasW;
            Height = _canvasH;

            BuildModernStyles();
            BuildRootStructure();

            _clashService = new ClashService();
            _mediaService = new MediaLyricsService();
            _mailService = new ImapMailService(_config);
            _sysService = new SystemStatsService();
            _widgetDeck = new WidgetDeck(
                _config,
                _clashService,
                _mediaService,
                _mailService,
                _sysService,
                LaunchMailApp,
                OpenMailConfigDialog,
                LaunchClashApp,
                LaunchMusicApp,
                OpenAddTodoDialog,
                null,
                BuildDesktopContextMenu,
                CreateDarkContextMenu,
                CreateMenuItem,
                _thinScrollBarStyle,
                delegate { return AnimFps; });

            _reloadDebounce = new DispatcherTimer();
            _reloadDebounce.Interval = TimeSpan.FromMilliseconds(350);
            _reloadDebounce.Tick += delegate
            {
                _reloadDebounce.Stop();
                ReloadWorkspace();
            };

            _wallpaperDebounce = new DispatcherTimer();
            _wallpaperDebounce.Interval = TimeSpan.FromMilliseconds(900);
            _wallpaperDebounce.Tick += delegate
            {
                _wallpaperDebounce.Stop();
                ReloadWallpaperAndRender();
            };

            _clockTimer = new DispatcherTimer();
            _clockTimer.Interval = TimeSpan.FromSeconds(10);
            _clockTimer.Tick += delegate { UpdateClockTexts(); };

            _widgetTimer = new DispatcherTimer();
            _widgetTimer.Interval = TimeSpan.FromMilliseconds(600);
            _widgetTimer.Tick += delegate
            {
                _widgetTickCount++;
                if (_widgetDeck != null && _config.ShowWidgets && _currentPage == 0)
                {
                    _widgetDeck.UpdateLiveData();
                }
                if (_widgetTickCount % 4 == 1 && _dockTileRefs.Count > 0)
                {
                    RefreshDockRunningIndicatorsAsync();
                }
            };

            PreviewKeyDown += OnWindowPreviewKeyDown;
            PreviewTextInput += OnWindowPreviewTextInput;
            PreviewMouseWheel += OnWindowPreviewMouseWheel;
            SizeChanged += OnWindowSizeChanged;

            if (!_isPreviewMode)
            {
                SourceInitialized += OnWindowSourceInitialized;
                Loaded += OnWindowLoaded;
                Closed += OnWindowClosed;
                SystemEvents.DisplaySettingsChanged += OnSystemDisplaySettingsChanged;
                SystemEvents.UserPreferenceChanged += OnSystemUserPreferenceChanged;
            }

            ReloadWallpaperOnly();
            ReloadWorkspace();
        }

        public bool IsLandscape
        {
            get
            {
                double w = _canvasW > 200 ? _canvasW : 1080;
                double h = _canvasH > 200 ? _canvasH : 1872;
                return w > h * 1.05;
            }
        }

        private double ScaleUnit
        {
            get
            {
                double w = _canvasW > 200 ? _canvasW : 1080;
                double h = _canvasH > 200 ? _canvasH : 1872;
                if (w > h * 1.05)
                {
                    // Landscape reference: 1920x1080 @ 100% (WorkHeight ~1032 -> u ~ 0.83)
                    // Bounded by both height and width so 1080p, 125% laptop, 2K, 4K, and ultrawide all feel crisp!
                    double uLand = Math.Min(h / 1240.0, w / 1920.0);
                    return Math.Max(0.60, Math.Min(1.30, uLand));
                }
                return w / 1080.0;
            }
        }

        private double LiveDpiScale
        {
            get
            {
                double monScale = (_monitor != null && _monitor.ScaleFactor >= 1.0) ? _monitor.ScaleFactor : 1.0;
                if (!_isPreviewMode)
                {
                    PresentationSource ps = PresentationSource.FromVisual(this);
                    if (ps != null && ps.CompositionTarget != null && ps.CompositionTarget.TransformToDevice.M11 >= 1.0)
                    {
                        return Math.Max(monScale, ps.CompositionTarget.TransformToDevice.M11);
                    }
                }
                return monScale;
            }
        }

        private BitmapCache CreateHiDpiBitmapCache()
        {
            return new BitmapCache
            {
                EnableClearType = true,
                SnapsToDevicePixels = true,
                RenderAtScale = LiveDpiScale
            };
        }

        private int AnimFps
        {
            get
            {
                if (_monitor != null && _monitor.RefreshRate >= 60)
                {
                    return Math.Min(240, _monitor.RefreshRate);
                }
                return 60;
            }
        }

        private static SolidColorBrush CreateFrozenBrush(byte a, byte r, byte g, byte b)
        {
            SolidColorBrush br = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            br.Freeze();
            return br;
        }

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

        private void BuildModernStyles()
        {
            // Pre-build & freeze GPU animation easings and shared brushes
            _easeOutExpo = new ExponentialEase { Exponent = 5.0, EasingMode = EasingMode.EaseOut };
            _easeOutExpo.Freeze();
            _easeOutCubic = new CubicEase { EasingMode = EasingMode.EaseOut };
            _easeOutCubic.Freeze();
            _easeOutBackSoft = new BackEase { Amplitude = 0.22, EasingMode = EasingMode.EaseOut };
            _easeOutBackSoft.Freeze();

            _brushModalScrim = CreateFrozenBrush(148, 5, 9, 18);
            _brushSearchScrim = CreateFrozenBrush(164, 5, 9, 18);
            _brushHoverTile = CreateFrozenBrush(38, 255, 255, 255);
            _brushTopHitTile = CreateFrozenBrush(56, 56, 189, 248);
            _brushTopHitBorder = CreateFrozenBrush(168, 125, 211, 252);
            _brushDropRim = CreateFrozenBrush(232, 56, 189, 248);
            _brushHalo1 = CreateFrozenBrush(160, 4, 7, 14);
            _brushModalIconShadow = CreateFrozenBrush(68, 0, 0, 0);

            _magGeometry = Geometry.Parse("M 7,1 A 6,6 0 1 0 7,13 A 6,6 0 1 0 7,1 Z M 11.4,11.4 L 15.5,15.5");
            _magGeometry.Freeze();
            _closeGeometry = Geometry.Parse("M 3,3 L 13,13 M 13,3 L 3,13");
            _closeGeometry.Freeze();
            _checkGeometry = Geometry.Parse("M 2.5,7.2 L 5.8,10.5 L 12.5,3.5");
            _checkGeometry.Freeze();

            // Sleek dark rounded MenuItem template (no legacy Win7 blue highlight, crisp disabled state & custom row tint support!)
            _darkMenuItemStyle = new Style(typeof(MenuItem));
            _darkMenuItemStyle.Setters.Add(new Setter(Control.ForegroundProperty, CreateFrozenBrush(245, 241, 245, 249)));
            _darkMenuItemStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            _darkMenuItemStyle.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.Transparent));
            _darkMenuItemStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            _darkMenuItemStyle.Setters.Add(new Setter(Control.FontFamilyProperty, FontText));
            _darkMenuItemStyle.Setters.Add(new Setter(Control.FontSizeProperty, 14.5));
            _darkMenuItemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(14, 9, 18, 9)));
            _darkMenuItemStyle.Setters.Add(new Setter(Control.CursorProperty, Cursors.Hand));

            Trigger disabledStyleTr = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabledStyleTr.Setters.Add(new Setter(Control.ForegroundProperty, CreateFrozenBrush(106, 184, 198, 218)));
            disabledStyleTr.Setters.Add(new Setter(Control.CursorProperty, Cursors.Arrow));
            _darkMenuItemStyle.Triggers.Add(disabledStyleTr);

            ControlTemplate miTpl = new ControlTemplate(typeof(MenuItem));
            FrameworkElementFactory bd = new FrameworkElementFactory(typeof(Border), "ItemBd");
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            bd.SetValue(Border.MarginProperty, new Thickness(5, 2, 5, 2));
            bd.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            bd.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            bd.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));

            FrameworkElementFactory grid = new FrameworkElementFactory(typeof(Grid));
            FrameworkElementFactory col1 = new FrameworkElementFactory(typeof(ColumnDefinition));
            col1.SetValue(ColumnDefinition.WidthProperty, new GridLength(1, GridUnitType.Star));
            FrameworkElementFactory col2 = new FrameworkElementFactory(typeof(ColumnDefinition));
            col2.SetValue(ColumnDefinition.WidthProperty, GridLength.Auto);
            grid.AppendChild(col1);
            grid.AppendChild(col2);

            FrameworkElementFactory cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.ContentSourceProperty, "Header");
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            cp.SetValue(Grid.ColumnProperty, 0);
            grid.AppendChild(cp);

            FrameworkElementFactory arrow = new FrameworkElementFactory(typeof(TextBlock), "SubArrow");
            arrow.SetValue(TextBlock.TextProperty, "›");
            arrow.SetValue(TextBlock.FontSizeProperty, 16.0);
            arrow.SetValue(TextBlock.MarginProperty, new Thickness(14, -2, 0, 0));
            arrow.SetValue(TextBlock.ForegroundProperty, CreateFrozenBrush(160, 203, 213, 225));
            arrow.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            arrow.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
            arrow.SetValue(Grid.ColumnProperty, 1);
            grid.AppendChild(arrow);

            // Submenu popup for nested menu items
            FrameworkElementFactory popup = new FrameworkElementFactory(typeof(Popup), "SubMenuPopup");
            popup.SetValue(Popup.PlacementProperty, PlacementMode.Right);
            popup.SetValue(Popup.AllowsTransparencyProperty, true);
            popup.SetValue(Popup.FocusableProperty, false);
            popup.SetBinding(Popup.IsOpenProperty, new System.Windows.Data.Binding("IsSubmenuOpen")
            {
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)
            });

            FrameworkElementFactory subBorder = new FrameworkElementFactory(typeof(Border));
            subBorder.SetValue(Border.BackgroundProperty, CreateFrozenBrush(245, 20, 27, 42));
            subBorder.SetValue(Border.BorderBrushProperty, CreateFrozenBrush(65, 255, 255, 255));
            subBorder.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            subBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(12));
            subBorder.SetValue(Border.PaddingProperty, new Thickness(0, 5, 0, 5));

            FrameworkElementFactory itemsHost = new FrameworkElementFactory(typeof(StackPanel));
            itemsHost.SetValue(Panel.IsItemsHostProperty, true);
            subBorder.AppendChild(itemsHost);
            popup.AppendChild(subBorder);
            grid.AppendChild(popup);

            bd.AppendChild(grid);
            miTpl.VisualTree = bd;

            MultiTrigger hoverTr = new MultiTrigger();
            hoverTr.Conditions.Add(new Condition(MenuItem.IsHighlightedProperty, true));
            hoverTr.Conditions.Add(new Condition(UIElement.IsEnabledProperty, true));
            hoverTr.Setters.Add(new Setter(Border.BackgroundProperty, CreateFrozenBrush(44, 255, 255, 255), "ItemBd"));
            miTpl.Triggers.Add(hoverTr);

            Trigger disabledTplTr = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabledTplTr.Setters.Add(new Setter(Border.BackgroundProperty, Brushes.Transparent, "ItemBd"));
            disabledTplTr.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45, "SubArrow"));
            miTpl.Triggers.Add(disabledTplTr);

            Trigger hasSubTr = new Trigger { Property = MenuItem.HasItemsProperty, Value = true };
            hasSubTr.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "SubArrow"));
            miTpl.Triggers.Add(hasSubTr);

            _darkMenuItemStyle.Setters.Add(new Setter(Control.TemplateProperty, miTpl));

            // Sleek dark separator
            _darkSeparatorStyle = new Style(typeof(Separator));
            ControlTemplate sepTpl = new ControlTemplate(typeof(Separator));
            FrameworkElementFactory sepLine = new FrameworkElementFactory(typeof(Border));
            sepLine.SetValue(Border.HeightProperty, 1.0);
            sepLine.SetValue(Border.MarginProperty, new Thickness(12, 5, 12, 5));
            sepLine.SetValue(Border.BackgroundProperty, CreateFrozenBrush(35, 255, 255, 255));
            sepTpl.VisualTree = sepLine;
            _darkSeparatorStyle.Setters.Add(new Setter(Control.TemplateProperty, sepTpl));

            // Ultra-thin translucent rounded scrollbar for modals (via clean XAML template)
            string sbXaml =
                "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ScrollBar'>" +
                "  <Setter Property='Width' Value='7'/>" +
                "  <Setter Property='MinWidth' Value='7'/>" +
                "  <Setter Property='Margin' Value='2,8,2,8'/>" +
                "  <Setter Property='Background' Value='Transparent'/>" +
                "  <Setter Property='Template'>" +
                "    <Setter.Value>" +
                "      <ControlTemplate TargetType='ScrollBar'>" +
                "        <Track Name='PART_Track' IsDirectionReversed='True'>" +
                "          <Track.Thumb>" +
                "            <Thumb>" +
                "              <Thumb.Template>" +
                "                <ControlTemplate TargetType='Thumb'>" +
                "                  <Border CornerRadius='3.5' Background='#66E2E8F0'/>" +
                "                </ControlTemplate>" +
                "              </Thumb.Template>" +
                "            </Thumb>" +
                "          </Track.Thumb>" +
                "        </Track>" +
                "      </ControlTemplate>" +
                "    </Setter.Value>" +
                "  </Setter>" +
                "</Style>";
            _thinScrollBarStyle = (Style)System.Windows.Markup.XamlReader.Parse(sbXaml);
        }

        private void BuildRootStructure()
        {
            _rootGrid = new Grid();
            _rootGrid.Background = CreateFrozenBrush(255, 10, 14, 24);
            _rootGrid.ClipToBounds = true;

            // Layer 0: Native 1:1 4K GPU-textured desktop wallpaper with subtle depth-of-field parallax zoom
            _wallpaperImage = new Image();
            _wallpaperImage.Stretch = Stretch.Fill;
            _wallpaperImage.IsHitTestVisible = false;
            _wallpaperImage.RenderTransformOrigin = new Point(0.5, 0.5);
            _wallpaperScaleTransform = new ScaleTransform(1.0, 1.0);
            _wallpaperImage.RenderTransform = _wallpaperScaleTransform;
            RenderOptions.SetBitmapScalingMode(_wallpaperImage, BitmapScalingMode.Linear);
            _rootGrid.Children.Add(_wallpaperImage);

            // Layer 1: Main Big-Folder Desktop Canvas (smooth page slide)
            _desktopCanvas = new Canvas();
            _desktopCanvas.Background = Brushes.Transparent;
            _desktopCanvas.ClipToBounds = true;
            _desktopCanvas.RenderTransformOrigin = new Point(0.5, 0.5);
            _desktopTranslateTransform = new TranslateTransform(0, 0);
            _desktopCanvas.RenderTransform = _desktopTranslateTransform;
            _desktopCanvas.MouseRightButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                if (e.OriginalSource == _desktopCanvas)
                {
                    _desktopCanvas.ContextMenu = BuildDesktopContextMenu();
                }
            };
            _rootGrid.Children.Add(_desktopCanvas);

            // Layer 2: Folder / Dialog Modal Overlay (kept Hidden instead of Collapsed so GPU layout & caches stay HOT!)
            _modalOverlay = new Grid();
            _modalOverlay.Visibility = Visibility.Hidden;
            _modalOverlay.IsHitTestVisible = false;

            _modalBackdrop = new Border();
            _modalBackdrop.Background = _brushModalScrim;
            _modalBackdrop.Opacity = 0;
            _modalBackdrop.MouseDown += delegate(object s, MouseButtonEventArgs e)
            {
                CloseFolderModal(false);
                e.Handled = true;
            };
            _modalOverlay.Children.Add(_modalBackdrop);

            _modalContentHost = new Grid();
            _modalContentHost.Background = null;
            _modalOverlay.Children.Add(_modalContentHost);

            _dialogContentHost = new Grid();
            _dialogContentHost.Background = null;
            _modalOverlay.Children.Add(_dialogContentHost);
            _rootGrid.Children.Add(_modalOverlay);

            // Layer 3: Pre-built Spotlight Search Overlay (kept Hidden instead of Collapsed for 0ms Frame-0 launch!)
            _searchOverlay = new Grid();
            _searchOverlay.Visibility = Visibility.Hidden;
            _searchOverlay.IsHitTestVisible = false;

            _searchBackdrop = new Border();
            _searchBackdrop.Background = _brushSearchScrim;
            _searchBackdrop.Opacity = 0;
            _searchBackdrop.MouseDown += delegate(object s, MouseButtonEventArgs e)
            {
                CloseSearchOverlay(false);
                e.Handled = true;
            };
            _searchOverlay.Children.Add(_searchBackdrop);

            _searchContentHost = new Grid();
            _searchContentHost.Background = null;
            _searchOverlay.Children.Add(_searchContentHost);
            _rootGrid.Children.Add(_searchOverlay);

            // Layer 4: Silky 160Hz GPU Floating Drag-and-Drop Overlay (replaces clunky OLE cursor!)
            _dragOverlayCanvas = new Canvas();
            _dragOverlayCanvas.IsHitTestVisible = false;
            _dragOverlayCanvas.Background = null;
            _rootGrid.Children.Add(_dragOverlayCanvas);

            _rootGrid.PreviewMouseMove += OnRootPointerDragMove;
            _rootGrid.PreviewMouseLeftButtonUp += OnRootPointerDragEnd;
            _rootGrid.PreviewMouseRightButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                if (_isPointerDragging)
                {
                    CancelPointerDrag();
                    e.Handled = true;
                }
            };
            _rootGrid.LostMouseCapture += delegate { CancelPointerDrag(); };
            Deactivated += delegate { CancelPointerDrag(); };

            Content = _rootGrid;
        }

        private void OnWindowSourceInitialized(object sender, EventArgs e)
        {
            _monitor = MonitorHelper.PickTargetMonitor(_config.TargetMonitor);
            DesktopAttachment.AttachWindow(this, _monitor, delegate { ReAnchorToMonitor(); });
        }

        private void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            ReAnchorToMonitor();
            SetupWatchers();
            _clockTimer.Start();
            if (_clashService != null) _clashService.Start();
            if (_mediaService != null) _mediaService.Start();
            if (_mailService != null) _mailService.Start();
            _widgetTimer.Start();
            SchedulePrewarmOverlays();
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        private void OnWindowClosed(object sender, EventArgs e)
        {
            try { SystemEvents.DisplaySettingsChanged -= OnSystemDisplaySettingsChanged; } catch { }
            try { SystemEvents.UserPreferenceChanged -= OnSystemUserPreferenceChanged; } catch { }
            DisposeWatchers();
            _clockTimer.Stop();
            if (_widgetTimer != null) _widgetTimer.Stop();
            StopModalDragAutoScroll();
            if (_clashService != null) _clashService.Dispose();
            if (_mediaService != null) _mediaService.Dispose();
            if (_mailService != null) _mailService.Dispose();
        }

        private void OnSystemDisplaySettingsChanged(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke((Action)delegate { ReAnchorToMonitor(); });
        }

        private void OnSystemUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category == UserPreferenceCategory.Desktop)
            {
                Dispatcher.BeginInvoke((Action)delegate
                {
                    _wallpaperDebounce.Stop();
                    _wallpaperDebounce.Start();
                });
            }
        }

        private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (ActualWidth > 100 && ActualHeight > 100)
            {
                bool changed = Math.Abs(_canvasW - ActualWidth) > 1.0 || Math.Abs(_canvasH - ActualHeight) > 1.0;
                _canvasW = ActualWidth;
                _canvasH = ActualHeight;
                if (changed)
                {
                    _folderModalsDirty = true;
                    RenderDesktop();
                    if (_openFolder != null && _modalOverlayActive)
                    {
                        OpenFolderModal(_openFolder);
                    }
                    InvalidateSearchOverlay(true);
                }
            }
        }

        private void OnWindowPreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            // If user starts typing while Search open animation is still gliding, focus SearchBox immediately without dropping keys!
            if (_searchOverlayActive && _searchBox != null && !_searchBox.IsKeyboardFocused && !string.IsNullOrEmpty(e.Text))
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                DesktopAttachment.BringToFrontForOverlay(hwnd);
                Activate();
                _searchBox.Focus();
                Keyboard.Focus(_searchBox);
            }
        }

        private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_isPointerDragging && e.Key == Key.Escape)
            {
                CancelPointerDrag();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape)
            {
                if (_searchOverlayActive)
                {
                    if (_searchBox != null && !string.IsNullOrEmpty(_searchBox.Text))
                    {
                        _searchBox.Text = "";
                    }
                    else
                    {
                        CloseSearchOverlay(false);
                    }
                    e.Handled = true;
                }
                else if (_modalOverlayActive)
                {
                    if (_modalIsCachedFolder && _activeModalView != null && _activeModalView.IsInlineRenaming && _activeModalView.CancelInlineRename != null)
                    {
                        _activeModalView.CancelInlineRename();
                    }
                    else
                    {
                        CloseFolderModal(false);
                    }
                    e.Handled = true;
                }
                return;
            }

            // Full keyboard navigation inside Spotlight Search
            if (_searchOverlayActive)
            {
                if (e.Key == Key.Down)
                {
                    MoveSearchSelection(_searchColumns);
                    e.Handled = true;
                }
                else if (e.Key == Key.Up)
                {
                    MoveSearchSelection(-_searchColumns);
                    e.Handled = true;
                }
                else if (e.Key == Key.Right && (_searchBox == null || _searchBox.CaretIndex >= _searchBox.Text.Length))
                {
                    MoveSearchSelection(1);
                    e.Handled = true;
                }
                else if (e.Key == Key.Left && (_searchBox == null || _searchBox.CaretIndex <= 0))
                {
                    MoveSearchSelection(-1);
                    e.Handled = true;
                }
                else if (e.Key == Key.Tab)
                {
                    bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
                    MoveSearchSelection(shift ? -1 : 1);
                    e.Handled = true;
                }
                else if (e.Key == Key.Enter && _currentSearchMatches.Count > 0)
                {
                    int idx = Math.Max(0, Math.Min(_currentSearchMatches.Count - 1, _searchSelectedIndex));
                    bool forceNew = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
                    FolderScanner.LaunchItem(_currentSearchMatches[idx], forceNew);
                    CloseSearchOverlay(false);
                    e.Handled = true;
                }
                return;
            }

            if (!_modalOverlayActive)
            {
                // Ctrl+F jumps straight into Spotlight Search
                if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
                {
                    OpenSearchOverlay();
                    e.Handled = true;
                    return;
                }
                if (_totalPages > 1)
                {
                    if (e.Key == Key.Right || e.Key == Key.PageDown)
                    {
                        GoToPage(_currentPage + 1);
                        e.Handled = true;
                    }
                    else if (e.Key == Key.Left || e.Key == Key.PageUp)
                    {
                        GoToPage(_currentPage - 1);
                        e.Handled = true;
                    }
                }
            }
        }

        private void OnWindowPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_searchOverlayActive || _modalOverlayActive) return;
            if (_totalPages <= 1) return;

            // Let scrollable desktop widgets (Mail inbox, Todo list) consume wheel scrolling instead of flipping pages
            DependencyObject src = e.OriginalSource as DependencyObject;
            while (src != null)
            {
                ScrollViewer sv = src as ScrollViewer;
                if (sv != null && sv.ScrollableHeight > 0.5) return;
                if (src is Visual || src is System.Windows.Media.Media3D.Visual3D)
                {
                    src = VisualTreeHelper.GetParent(src);
                }
                else
                {
                    src = LogicalTreeHelper.GetParent(src);
                }
            }

            DateTime now = DateTime.UtcNow;
            if ((now - _lastWheelPageSwitch).TotalMilliseconds < 180) return;
            _lastWheelPageSwitch = now;

            if (e.Delta < 0) GoToPage(_currentPage + 1);
            else if (e.Delta > 0) GoToPage(_currentPage - 1);
            e.Handled = true;
        }

        public void GoToPage(int pageIndex)
        {
            if (_totalPages <= 1) return;
            int clamped = Math.Max(0, Math.Min(_totalPages - 1, pageIndex));
            if (clamped != _currentPage)
            {
                int dir = clamped > _currentPage ? 1 : -1;
                _currentPage = clamped;
                RenderDesktop();
                if (!_isPreviewMode)
                {
                    double u = ScaleUnit;
                    _desktopTranslateTransform.BeginAnimation(TranslateTransform.XProperty, MakeAnim(dir * 60 * u, 0, 240, _easeOutCubic, 0));
                    _desktopCanvas.BeginAnimation(UIElement.OpacityProperty, MakeAnim(0.55, 1.0, 200, _easeOutCubic, 0));
                }
            }
        }

        public void SetLayoutMode(string mode)
        {
            _config.LayoutMode = string.IsNullOrEmpty(mode) ? "auto" : mode.ToLowerInvariant();
            _currentPage = 0;
            ConfigManager.Save(_config);
            RenderDesktop();
            InvalidateSearchOverlay(true);
        }

        private void ComputePaginationCapacities(int totalFolders, out int page0Capacity, out int otherPageCapacity)
        {
            int cols, rows;
            ResolveEffectiveGrid(totalFolders, out cols, out rows);
            int activeWidgetCount = CountActiveWidgets();
            bool hasWidgetsOnPage0 = (activeWidgetCount > 0);
            if (IsLandscape)
            {
                page0Capacity = Math.Max(1, cols * rows);
                int otherCols = hasWidgetsOnPage0 ? Math.Max(cols, 4) : cols;
                int otherRows = Math.Max(rows, 2);
                otherPageCapacity = Math.Max(1, otherCols * otherRows);
            }
            else
            {
                int widgetRowsNeeded = (activeWidgetCount <= 2) ? 1 : 2;
                int page0FolderRowsCap = hasWidgetsOnPage0
                    ? (rows >= 4 ? Math.Max(1, rows - widgetRowsNeeded) : Math.Max(1, rows - 1))
                    : rows;
                page0Capacity = Math.Max(1, page0FolderRowsCap * cols);
                otherPageCapacity = Math.Max(1, cols * rows);
            }
        }

        internal static int ComputePageForFolderIndex(int folderIndex, int page0Capacity, int otherPageCapacity)
        {
            if (folderIndex < 0) return 0;
            int p0 = Math.Max(1, page0Capacity);
            if (folderIndex < p0) return 0;
            int pOther = Math.Max(1, otherPageCapacity);
            return 1 + (folderIndex - p0) / pOther;
        }

        private int CountActiveWidgets()
        {
            if (!_config.ShowWidgets || _config.EnabledWidgets == null) return 0;
            int cnt = 0;
            for (int i = 0; i < _config.EnabledWidgets.Count; i++)
            {
                string id = (_config.EnabledWidgets[i] ?? "").ToLowerInvariant();
                if (id == "mail" || id == "clash" || id == "music" || id == "sys_todo") cnt++;
            }
            return cnt;
        }

        private void ResolveEffectiveGrid(int totalFolders, out int cols, out int rows)
        {
            string mode = (_config.LayoutMode ?? "auto").ToLowerInvariant();
            if (IsLandscape)
            {
                int activeWidgets = CountActiveWidgets();
                if (mode == "3x2" || mode == "2x3") { cols = 3; rows = 2; return; }
                if (mode == "4x2" || mode == "2x4") { cols = (activeWidgets >= 3 && totalFolders <= 6) ? 3 : 4; rows = 2; return; }
                if (mode == "4x3") { cols = 4; rows = 3; return; }
                if (mode == "3x4")
                {
                    // Migrated from Portrait 3x4: when Landscape Split Stage has widgets on the right, keep 3x2 lush cards on the left!
                    if (activeWidgets > 0) { cols = 3; rows = 2; }
                    else { cols = 4; rows = 3; }
                    return;
                }
                if (mode == "5x3" || mode == "3x5") { cols = activeWidgets > 0 ? 4 : 5; rows = 3; return; }
                if (mode == "5x2") { cols = 5; rows = 2; return; }

                if (activeWidgets > 0)
                {
                    // Split Stage on Page 1: Left Big Folders | Right Frosted Widgets
                    if (activeWidgets <= 2)
                    {
                        cols = (totalFolders <= 6) ? 3 : 4;
                        rows = 2;
                    }
                    else
                    {
                        cols = (totalFolders <= 4) ? 2 : 3;
                        rows = 2;
                    }
                    return;
                }

                if (totalFolders <= 6) { cols = 3; rows = 2; }
                else if (totalFolders <= 8) { cols = 4; rows = 2; }
                else if (totalFolders <= 10) { cols = 5; rows = 2; }
                else { cols = 4; rows = 3; }
                return;
            }

            if (mode == "2x3" || mode == "3x2") { cols = 2; rows = 3; return; }
            if (mode == "2x4" || mode == "4x2") { cols = 2; rows = 4; return; }
            if (mode == "3x4" || mode == "4x3") { cols = 3; rows = 4; return; }
            if (mode == "3x5" || mode == "5x3") { cols = 3; rows = 5; return; }

            // Smart Adaptive Auto Layout (Portrait): when Widgets are enabled with >=5 folders, prefer 3x4 so folders + widgets shine on Page 1!
            if (CountActiveWidgets() > 0 && totalFolders >= 5) { cols = 3; rows = 4; return; }
            if (totalFolders <= 6) { cols = 2; rows = 3; }
            else if (totalFolders <= 8) { cols = 2; rows = 4; }
            else { cols = 3; rows = 4; }
        }

        private string GetLayoutModeLabel(string mode)
        {
            string m = (mode ?? "auto").ToLowerInvariant();
            if (IsLandscape)
            {
                if (m == "2x3" || m == "3x2") return "固定 3×2 (6格/页)";
                if (m == "2x4" || m == "4x2") return "固定 4×2 (8格/页)";
                if (m == "3x4" || m == "4x3") return "固定 4×3 (12格/页)";
                if (m == "3x5" || m == "5x3") return "固定 5×3 (15格/页)";
                return "智能自适应 (横屏双栏 / 自动扩展)";
            }
            if (m == "2x3" || m == "3x2") return "固定 2×3 (6格/页)";
            if (m == "2x4" || m == "4x2") return "固定 2×4 (8格/页)";
            if (m == "3x4" || m == "4x3") return "固定 3×4 (12格/页)";
            if (m == "3x5" || m == "5x3") return "固定 3×5 (15格/页)";
            return "智能自适应 (竖屏 6→8→12格)";
        }

        private void SyncCanvasMetricsFromLiveWindow()
        {
            if (_isPreviewMode)
            {
                if (_monitor != null)
                {
                    _canvasW = _monitor.LogicalWorkWidth;
                    _canvasH = _monitor.LogicalWorkHeight;
                }
                return;
            }

            // Always trust WPF's real rendered coordinate space (DPI-proof even under mixed-DPI!)
            if (ActualWidth > 100 && ActualHeight > 100)
            {
                _canvasW = ActualWidth;
                _canvasH = ActualHeight;
                return;
            }

            PresentationSource ps = PresentationSource.FromVisual(this);
            if (ps != null && ps.CompositionTarget != null && _monitor != null)
            {
                Matrix m = ps.CompositionTarget.TransformFromDevice;
                _canvasW = _monitor.WorkWidth * m.M11;
                _canvasH = _monitor.WorkHeight * m.M22;
            }
            else if (_monitor != null)
            {
                _canvasW = _monitor.LogicalWorkWidth;
                _canvasH = _monitor.LogicalWorkHeight;
            }
        }

        public void ReAnchorToMonitor()
        {
            _monitor = MonitorHelper.PickTargetMonitor(_config.TargetMonitor);
            if (_monitor == null) return;

            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                DesktopAttachment.PositionOnMonitor(hwnd, _monitor);
            }

            // Sync WPF Width/Height explicitly to the exact live DPI-transformed WorkingArea so WPF NEVER clips the right edge!
            PresentationSource ps = PresentationSource.FromVisual(this);
            if (ps != null && ps.CompositionTarget != null)
            {
                Matrix m = ps.CompositionTarget.TransformFromDevice;
                double dipW = Math.Round(_monitor.WorkWidth * m.M11);
                double dipH = Math.Round(_monitor.WorkHeight * m.M22);
                if (dipW > 100 && dipH > 100)
                {
                    Width = dipW;
                    Height = dipH;
                    _canvasW = dipW;
                    _canvasH = dipH;
                }
            }
            else
            {
                SyncCanvasMetricsFromLiveWindow();
            }

            if (hwnd != IntPtr.Zero)
            {
                DesktopAttachment.PositionOnMonitor(hwnd, _monitor);
            }

            _folderModalsDirty = true;
            ReloadWallpaperAndRender();
            if (_openFolder != null && _modalOverlayActive)
            {
                OpenFolderModal(_openFolder);
            }
            InvalidateSearchOverlay(true);

            // Post-DPI settle verification pass (guarantees exact edge-to-edge coverage after WM_DPICHANGED completes)
            DispatcherTimer settle = new DispatcherTimer();
            settle.Interval = TimeSpan.FromMilliseconds(180);
            settle.Tick += delegate
            {
                settle.Stop();
                IntPtr h2 = new WindowInteropHelper(this).Handle;
                if (h2 != IntPtr.Zero && _monitor != null)
                {
                    DesktopAttachment.PositionOnMonitor(h2, _monitor);
                    PresentationSource ps2 = PresentationSource.FromVisual(this);
                    if (ps2 != null && ps2.CompositionTarget != null)
                    {
                        Matrix m2 = ps2.CompositionTarget.TransformFromDevice;
                        double w2 = Math.Round(_monitor.WorkWidth * m2.M11);
                        double hDip2 = Math.Round(_monitor.WorkHeight * m2.M22);
                        if (w2 > 100 && hDip2 > 100 &&
                            (Math.Abs(Width - w2) > 1.0 || Math.Abs(Height - hDip2) > 1.0 ||
                             Math.Abs(_canvasW - w2) > 1.0 || Math.Abs(_canvasH - hDip2) > 1.0))
                        {
                            Width = w2;
                            Height = hDip2;
                            _canvasW = w2;
                            _canvasH = hDip2;
                            _folderModalsDirty = true;
                            DesktopAttachment.PositionOnMonitor(h2, _monitor);
                            RenderDesktop();
                            if (_openFolder != null && _modalOverlayActive)
                            {
                                OpenFolderModal(_openFolder);
                            }
                            InvalidateSearchOverlay(true);
                        }
                    }
                }
            };
            settle.Start();
        }

        public void SwitchMonitor(string deviceName)
        {
            _config.TargetMonitor = deviceName;
            ConfigManager.Save(_config);
            ReAnchorToMonitor();
        }

        public void RefreshAll()
        {
            IconExtractor.ClearCache();
            ReloadWallpaperOnly();
            ReloadWorkspace();
        }

        public void SetGlassPreset(double tintOpacity)
        {
            _config.CardTintOpacity = tintOpacity;
            ConfigManager.Save(_config);
            _folderModalsDirty = true;
            RenderDesktop();
            if (_openFolder != null && _modalOverlayActive)
            {
                OpenFolderModal(_openFolder);
            }
            SchedulePrewarmOverlays();
        }

        public void ToggleTopClock()
        {
            _config.ShowTopClock = !_config.ShowTopClock;
            ConfigManager.Save(_config);
            RenderDesktop();
            InvalidateSearchOverlay(true);
        }

        public void ToggleWidgets()
        {
            _config.ShowWidgets = !_config.ShowWidgets;
            _currentPage = 0;
            ConfigManager.Save(_config);
            RenderDesktop();
            InvalidateSearchOverlay(true);
        }

        public bool IsWidgetEnabled(string widgetId)
        {
            if (_config.EnabledWidgets == null) return false;
            for (int i = 0; i < _config.EnabledWidgets.Count; i++)
            {
                if (string.Equals(_config.EnabledWidgets[i], widgetId, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        public void ToggleSingleWidget(string widgetId)
        {
            if (_config.EnabledWidgets == null) _config.EnabledWidgets = new List<string>();
            int idx = -1;
            for (int i = 0; i < _config.EnabledWidgets.Count; i++)
            {
                if (string.Equals(_config.EnabledWidgets[i], widgetId, StringComparison.OrdinalIgnoreCase))
                {
                    idx = i;
                    break;
                }
            }
            if (idx >= 0) _config.EnabledWidgets.RemoveAt(idx);
            else
            {
                _config.EnabledWidgets.Add(widgetId);
                _config.ShowWidgets = true;
            }
            _currentPage = 0;
            ConfigManager.Save(_config);
            RenderDesktop();
            InvalidateSearchOverlay(true);
        }

        private void LaunchMailApp()
        {
            if (_workspace != null)
            {
                for (int i = 0; i < _workspace.AllItems.Count; i++)
                {
                    if (_workspace.AllItems[i].DisplayName.IndexOf("Gmail", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        FolderScanner.LaunchItem(_workspace.AllItems[i]);
                        return;
                    }
                }
            }
            try { Process.Start("https://mail.google.com/"); } catch { }
        }

        private void LaunchClashApp()
        {
            if (_workspace != null)
            {
                // Prefer Clash Verge explicitly before generic Clash (e.g. avoid launching legacy Clash for Windows!)
                for (int i = 0; i < _workspace.AllItems.Count; i++)
                {
                    if (_workspace.AllItems[i].DisplayName.IndexOf("Clash Verge", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        FolderScanner.LaunchItem(_workspace.AllItems[i]);
                        return;
                    }
                }
                for (int i = 0; i < _workspace.AllItems.Count; i++)
                {
                    if (_workspace.AllItems[i].DisplayName.IndexOf("Clash", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        FolderScanner.LaunchItem(_workspace.AllItems[i]);
                        return;
                    }
                }
            }
            string[] candidates = new string[]
            {
                @"C:\Program Files\Clash Verge\clash-verge.exe",
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Clash Verge\clash-verge.exe")
            };
            for (int i = 0; i < candidates.Length; i++)
            {
                if (File.Exists(candidates[i]))
                {
                    try { Process.Start(candidates[i]); return; } catch { }
                }
            }
        }

        private void LaunchMusicApp()
        {
            if (_workspace != null)
            {
                string[] preferred = new string[] { "网易云音乐", "Spotify", "QQ音乐", "Apple Music", "YouTube Music", "PotPlayer" };
                for (int p = 0; p < preferred.Length; p++)
                {
                    for (int i = 0; i < _workspace.AllItems.Count; i++)
                    {
                        if (_workspace.AllItems[i].DisplayName.IndexOf(preferred[p], StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            FolderScanner.LaunchItem(_workspace.AllItems[i]);
                            return;
                        }
                    }
                }
            }
            string[] fallbackExes = new string[]
            {
                @"C:\Program Files\NetEase\CloudMusic\cloudmusic.exe",
                @"C:\Program Files (x86)\Netease\CloudMusic\cloudmusic.exe",
                @"C:\CloudMusic\cloudmusic.exe",
                @"D:\CloudMusic\cloudmusic.exe"
            };
            for (int i = 0; i < fallbackExes.Length; i++)
            {
                if (File.Exists(fallbackExes[i]))
                {
                    try
                    {
                        if (WindowActivator.TryActivateExistingShortcut(fallbackExes[i], "网易云音乐")) return;
                        Process.Start(fallbackExes[i]);
                        return;
                    }
                    catch { }
                }
            }
        }

        public void OpenAddTodoDialog()
        {
            OpenCategoryInputDialog("添加快捷待办", "输入待办内容，按回车保存...", "", delegate(string text)
            {
                string trimmed = (text ?? "").Trim();
                if (string.IsNullOrEmpty(trimmed)) return "请输入待办内容";
                if (_config.TodoItems == null) _config.TodoItems = new List<TodoEntry>();
                _config.TodoItems.Insert(0, new TodoEntry { Text = trimmed, Done = false });
                ConfigManager.Save(_config);
                if (_widgetDeck != null) _widgetDeck.RebuildTodoRows();
                return null;
            });
        }

        public void OpenDataFolder()
        {
            try
            {
                if (!Directory.Exists(_config.DataRoot)) Directory.CreateDirectory(_config.DataRoot);
                Process.Start("explorer.exe", "\"" + _config.DataRoot + "\"");
            }
            catch
            {
            }
        }

        public void RenderPreviewToPng(string pngPath, string mode)
        {
            _isPreviewMode = true;

            // Optional landscape / laptop / portrait preview simulation (works on any machine!)
            if (mode != null && mode.StartsWith("landscape", StringComparison.OrdinalIgnoreCase))
            {
                bool simLaptop = mode.Equals("landscape-laptop", StringComparison.OrdinalIgnoreCase);
                List<MonitorInfoItem> mons = MonitorHelper.GetAllMonitors();
                MonitorInfoItem landMon = null;
                if (!simLaptop)
                {
                    for (int i = 0; i < mons.Count; i++)
                    {
                        if (!mons[i].IsPortrait) { landMon = mons[i]; break; }
                    }
                }
                if (landMon == null)
                {
                    landMon = new MonitorInfoItem
                    {
                        DeviceName = @"\\.\DISPLAY_SIM",
                        IsPrimary = true,
                        BoundsLeft = 0,
                        BoundsTop = 0,
                        BoundsWidth = 1920,
                        BoundsHeight = 1080,
                        WorkLeft = 0,
                        WorkTop = 0,
                        WorkWidth = 1920,
                        WorkHeight = 1032,
                        DpiX = simLaptop ? 120u : 96u,
                        DpiY = simLaptop ? 120u : 96u,
                        RefreshRate = 60
                    };
                }
                _monitor = landMon;
                _canvasW = _monitor.LogicalWorkWidth;
                _canvasH = _monitor.LogicalWorkHeight;
                _wallpaper = WallpaperService.LoadForMonitor(_monitor, _config.BlurRadius);
                _folderModalsDirty = true;
                _searchCacheDirty = true;
            }
            else if (mode != null && (mode.Equals("portrait-4k", StringComparison.OrdinalIgnoreCase) || mode.Equals("portrait-search", StringComparison.OrdinalIgnoreCase)))
            {
                _monitor = new MonitorInfoItem
                {
                    DeviceName = @"\\.\DISPLAY_PORTRAIT_SIM",
                    IsPrimary = false,
                    BoundsLeft = 0,
                    BoundsTop = 0,
                    BoundsWidth = 2160,
                    BoundsHeight = 3840,
                    WorkLeft = 0,
                    WorkTop = 0,
                    WorkWidth = 2160,
                    WorkHeight = 3744,
                    DpiX = 192,
                    DpiY = 192,
                    RefreshRate = 60
                };
                _canvasW = _monitor.LogicalWorkWidth;
                _canvasH = _monitor.LogicalWorkHeight;
                _wallpaper = WallpaperService.LoadForMonitor(_monitor, _config.BlurRadius);
                _folderModalsDirty = true;
                _searchCacheDirty = true;
            }

            _wallpaperImage.Visibility = Visibility.Visible;
            if (_wallpaper != null && _wallpaper.RawWallpaper != null)
            {
                _wallpaperImage.Source = _wallpaper.RawWallpaper;
            }
            else
            {
                _rootGrid.Background = new LinearGradientBrush(
                    Color.FromRgb(18, 28, 48), Color.FromRgb(10, 15, 28), 90);
            }

            // Pull live Clash & Media data once so preview shows real widgets!
            if (_clashService != null) _clashService.RefreshOnceSync();
            if (_mediaService != null) _mediaService.RefreshOnceSync();

            if (mode == "landscape-nowidget")
            {
                _config.ShowWidgets = false;
            }

            RenderDesktop();

            if ((mode == "modal" || mode == "landscape-modal") && _workspace != null && _workspace.Folders.Count > 0)
            {
                OpenFolderModal(_workspace.Folders[0]);
            }
            else if (mode != null && mode.StartsWith("landscape-modal:", StringComparison.OrdinalIgnoreCase) && _workspace != null && _workspace.Folders.Count > 0)
            {
                int idx = 0;
                int.TryParse(mode.Substring("landscape-modal:".Length), out idx);
                idx = Math.Max(0, Math.Min(_workspace.Folders.Count - 1, idx));
                OpenFolderModal(_workspace.Folders[idx]);
            }
            else if (mode == "search" || mode == "landscape-search" || mode == "portrait-search")
            {
                OpenSearchOverlay();
            }
            else if (mode != null && mode.StartsWith("demo:", StringComparison.OrdinalIgnoreCase))
            {
                // Format: demo:<layoutMode>:<fakeTotalCards>
                string[] parts = mode.Split(':');
                if (parts.Length >= 3 && _workspace != null && _workspace.Folders.Count > 0)
                {
                    _config.LayoutMode = parts[1];
                    int targetCount;
                    if (int.TryParse(parts[2], out targetCount))
                    {
                        int baseCount = _workspace.Folders.Count;
                        while (_workspace.Folders.Count < targetCount)
                        {
                            _workspace.Folders.Add(_workspace.Folders[_workspace.Folders.Count % baseCount]);
                        }
                    }
                    RenderDesktop();
                }
            }

            _rootGrid.Width = _canvasW;
            _rootGrid.Height = _canvasH;
            _rootGrid.Measure(new Size(_canvasW, _canvasH));
            _rootGrid.Arrange(new Rect(0, 0, _canvasW, _canvasH));
            _rootGrid.UpdateLayout();

            // Render at true native 200% 4K (192 DPI) when previewing so we can inspect 1:1 4K sharpness!
            double dpi = (_monitor != null && _monitor.DpiX >= 96) ? _monitor.DpiX : 96.0;
            double scale = dpi / 96.0;
            int pixelW = (int)Math.Round(_canvasW * scale);
            int pixelH = (int)Math.Round(_canvasH * scale);
            RenderTargetBitmap rtb = new RenderTargetBitmap(pixelW, pixelH, dpi, dpi, PixelFormats.Pbgra32);
            rtb.Render(_rootGrid);

            PngBitmapEncoder enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            string dir = System.IO.Path.GetDirectoryName(pngPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            using (FileStream fs = new FileStream(pngPath, FileMode.Create, FileAccess.Write))
            {
                enc.Save(fs);
            }
        }

        private void SetupWatchers()
        {
            DisposeWatchers();
            try
            {
                if (Directory.Exists(_config.DataRoot))
                {
                    _dataWatcher = new FileSystemWatcher(_config.DataRoot);
                    _dataWatcher.IncludeSubdirectories = true;
                    _dataWatcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite;
                    FileSystemEventHandler onDataChanged = delegate
                    {
                        Dispatcher.BeginInvoke((Action)delegate
                        {
                            if ((DateTime.UtcNow - _lastManualReloadUtc).TotalMilliseconds < 500) return;
                            _reloadDebounce.Stop();
                            _reloadDebounce.Start();
                        });
                    };
                    _dataWatcher.Created += onDataChanged;
                    _dataWatcher.Deleted += onDataChanged;
                    _dataWatcher.Changed += onDataChanged;
                    _dataWatcher.Renamed += delegate { onDataChanged(null, null); };
                    _dataWatcher.EnableRaisingEvents = true;
                }
            }
            catch
            {
            }

            try
            {
                string themesDir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"Microsoft\Windows\Themes");
                if (Directory.Exists(themesDir))
                {
                    _themeWatcher = new FileSystemWatcher(themesDir);
                    _themeWatcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName;
                    _themeWatcher.Changed += delegate
                    {
                        Dispatcher.BeginInvoke((Action)delegate
                        {
                            _wallpaperDebounce.Stop();
                            _wallpaperDebounce.Start();
                        });
                    };
                    _themeWatcher.EnableRaisingEvents = true;
                }
            }
            catch
            {
            }
        }

        private void DisposeWatchers()
        {
            if (_dataWatcher != null)
            {
                try { _dataWatcher.EnableRaisingEvents = false; _dataWatcher.Dispose(); } catch { }
                _dataWatcher = null;
            }
            if (_themeWatcher != null)
            {
                try { _themeWatcher.EnableRaisingEvents = false; _themeWatcher.Dispose(); } catch { }
                _themeWatcher = null;
            }
        }

        private void ReloadWallpaperOnly()
        {
            _wallpaper = WallpaperService.LoadForMonitor(_monitor, _config.BlurRadius);
            if (_wallpaper != null && _wallpaper.RawWallpaper != null)
            {
                _wallpaperImage.Source = _wallpaper.RawWallpaper;
                _wallpaperImage.Visibility = Visibility.Visible;
            }
            else
            {
                _wallpaperImage.Source = null;
            }
        }

        private void ReloadWallpaperAndRender()
        {
            ReloadWallpaperOnly();
            _folderModalsDirty = true;
            RenderDesktop();
            SchedulePrewarmOverlays();
        }

        private void ReloadWorkspace()
        {
            if (_reloadDebounce != null) _reloadDebounce.Stop();
            _lastManualReloadUtc = DateTime.UtcNow;

            string openFolderName = (_openFolder != null && _modalOverlayActive)
                ? _openFolder.FolderName : null;

            _workspace = FolderScanner.Scan(_config);
            _searchCacheDirty = true;
            _folderModalsDirty = true;
            RenderDesktop();

            if (!string.IsNullOrEmpty(openFolderName))
            {
                FolderGroup refreshed = null;
                for (int i = 0; i < _workspace.Folders.Count; i++)
                {
                    if (_workspace.Folders[i].FolderName.Equals(openFolderName, StringComparison.OrdinalIgnoreCase))
                    {
                        refreshed = _workspace.Folders[i];
                        break;
                    }
                }
                if (refreshed != null) OpenFolderModal(refreshed);
                else CloseFolderModal(true);
            }

            if (_searchOverlayActive)
            {
                InvalidateSearchOverlay(true);
            }
            SchedulePrewarmOverlays();
        }

        private void InvalidateSearchOverlay(bool preserveOpenState)
        {
            _searchCacheDirty = true;
            if (preserveOpenState && _searchOverlayActive)
            {
                string prevQuery = _searchBox != null ? _searchBox.Text : "";
                int prevCaret = _searchBox != null ? _searchBox.CaretIndex : 0;
                bool prevHadFocus = _searchBox != null && _searchBox.IsKeyboardFocused;
                int prevSelectedIndex = _searchSelectedIndex;
                bool prevHasHighlight = _searchHasSelectionHighlight;

                EnsureSearchOverlayBuilt();
                if (!string.IsNullOrEmpty(prevQuery) && _searchBox != null)
                {
                    _searchBox.Text = prevQuery;
                    _searchBox.CaretIndex = Math.Max(0, Math.Min(prevCaret, prevQuery.Length));
                }
                else
                {
                    UpdateSearchResults();
                }

                if (_visibleSearchTiles.Count > 0 && (prevHasHighlight || !string.IsNullOrEmpty(prevQuery)))
                {
                    _searchSelectedIndex = Math.Max(0, Math.Min(_visibleSearchTiles.Count - 1, prevSelectedIndex));
                    _searchHasSelectionHighlight = true;
                    ApplySearchSelectionVisuals(true);
                    try { _visibleSearchTiles[_searchSelectedIndex].Cell.BringIntoView(); } catch { }
                }

                if (_searchBackdrop != null) _searchBackdrop.Opacity = 1.0;
                if (_searchInputRoot != null) { _searchInputRoot.Opacity = 1.0; _searchInputRoot.CacheMode = null; }
                if (_searchResultsRoot != null) { _searchResultsRoot.Opacity = 1.0; _searchResultsRoot.CacheMode = null; }
                if (_searchInputScale != null) { _searchInputScale.ScaleX = 1.0; _searchInputScale.ScaleY = 1.0; }
                if (_searchInputTranslate != null) _searchInputTranslate.Y = 0;
                if (_searchResultsScale != null) { _searchResultsScale.ScaleX = 1.0; _searchResultsScale.ScaleY = 1.0; }
                if (_searchResultsTranslate != null) _searchResultsTranslate.Y = 0;

                if (prevHadFocus && !_isPreviewMode && _searchBox != null)
                {
                    _searchBox.Focus();
                    Keyboard.Focus(_searchBox);
                }
            }
            else
            {
                SchedulePrewarmOverlays();
            }
        }

        private void SchedulePrewarmOverlays()
        {
            if (_isPreviewMode) return;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
            {
                if (_workspace == null) return;
                if (_searchCacheDirty && !_searchOverlayActive)
                {
                    EnsureSearchOverlayBuilt();
                }
                if (_folderModalsDirty && !_modalOverlayActive)
                {
                    EnsureFolderModalsPrebuilt();
                }
            });
        }

        private void RenderDesktop()
        {
            CancelPointerDrag();
            _desktopCanvas.Children.Clear();
            _folderCardRects.Clear();
            _desktopAppTargets.Clear();
            _desktopCardTargets.Clear();
            _dockTileRefs.Clear();
            _landscapeDockGlass = null;
            _landscapeDockRect = Rect.Empty;
            _lastDockTopY = -1;
            _lastSearchPillTopY = -1;
            _dockHoverLabelPill = null;
            _dockHoverLabelText = null;
            _searchPillElement = null;
            _dockWaveActive = false;
            _lastDockHoverIndex = -1;
            _lastDockWavePointerX = double.NaN;
            if (_widgetDeck != null) _widgetDeck.ResetRefs();
            if (_workspace == null) return;

            double W = _canvasW > 200 ? _canvasW : 1080;
            double H = _canvasH > 200 ? _canvasH : 1872;
            double u = ScaleUnit;

            if (IsLandscape)
            {
                RenderDesktopLandscape(W, H, u);
                return;
            }

            int totalFolders = _workspace.Folders.Count;
            int cols, rows;
            ResolveEffectiveGrid(totalFolders, out cols, out rows);

            int activeWidgetCount = CountActiveWidgets();
            bool hasWidgetsOnPage0 = (activeWidgetCount > 0);
            int page0Capacity, otherPageCapacity;
            ComputePaginationCapacities(totalFolders, out page0Capacity, out otherPageCapacity);

            if (totalFolders <= page0Capacity)
            {
                _totalPages = 1;
            }
            else
            {
                _totalPages = 1 + (int)Math.Ceiling((double)(totalFolders - page0Capacity) / otherPageCapacity);
            }

            if (_currentPage >= _totalPages) _currentPage = _totalPages - 1;
            if (_currentPage < 0) _currentPage = 0;

            bool showPageDots = _totalPages > 1;
            int dockCount = (_workspace.DockItems != null) ? _workspace.DockItems.Count : 0;
            DockCapsuleMetrics dockMetrics = ComputeDockCapsuleMetrics(W, H, u, dockCount, false);
            bool hasDock = dockMetrics.Count > 0;

            // Adaptive horizontal margins & column gaps for 2-column vs 3-column grids
            double sideMargin = Math.Round(W * (cols >= 3 ? 0.048 : 0.068));
            double colGap = Math.Round(W * (cols >= 3 ? 0.036 : 0.060));
            double maxCardW = (W - sideMargin * 2 - colGap * (cols - 1)) / cols;

            double topZone = _config.ShowTopClock ? Math.Round(H * 0.095) : Math.Round(H * (rows >= 4 ? 0.030 : 0.040));
            double bottomMargin = Math.Round(H * (rows >= 4 ? 0.022 : 0.028));
            // Cohesive iOS/iPadOS Frosted Glass Dock Capsule proportions in Portrait (adapts height when 6-10 items shrink!)
            double dockCapsuleH = hasDock ? dockMetrics.CapsuleH : 0;
            double dockTopGap = hasDock ? Math.Round(H * (rows >= 4 ? 0.014 : 0.018)) : 0;
            double searchH = Math.Round(52 * u);
            double pageDotsH = showPageDots ? Math.Round(24 * u) : 0;
            double searchTopGap = Math.Round(H * (showPageDots ? 0.011 : (rows >= 4 ? 0.013 : 0.016)));
            double titleGap = Math.Round((rows >= 4 ? 12 : 15) * u);
            double titleH = Math.Round((rows >= 4 ? 30 : 35) * u);
            double rowGap = Math.Round(H * (rows >= 5 ? 0.010 : (rows == 4 ? 0.012 : 0.017)));

            double availGridH = H - topZone - pageDotsH - searchTopGap - searchH - dockTopGap - dockCapsuleH - bottomMargin;
            double maxCardH = (availGridH - rows * (titleGap + titleH) - (rows - 1) * rowGap) / rows;
            double cardSize = Math.Floor(Math.Max(110, Math.Min(maxCardW, maxCardH)));

            // Perfectly centered horizontally & balanced vertically
            double totalGridW = cols * cardSize + (cols - 1) * colGap;
            double gridLeft = Math.Round((W - totalGridW) * 0.5);
            double totalGridH = rows * (cardSize + titleGap + titleH) + (rows - 1) * rowGap;
            double contentTotalH = totalGridH + pageDotsH + searchTopGap + searchH + dockTopGap + dockCapsuleH;
            double startY = _config.ShowTopClock
                ? Math.Max(topZone + Math.Round(8 * u), Math.Round(topZone + (H - topZone - bottomMargin - contentTotalH) * 0.48))
                : Math.Max(topZone, Math.Round((H - contentTotalH - bottomMargin * 0.35) * 0.5));

            // Optional minimal lockscreen-style top clock
            if (_config.ShowTopClock)
            {
                BuildTopClockOverlay(W, Math.Round(24 * u), Math.Round(68 * u), Math.Round(21 * u), u);
            }

            // Render Current Page of Big Folder Cards
            int startIdx = (_currentPage == 0) ? 0 : (page0Capacity + (_currentPage - 1) * otherPageCapacity);
            int curCap = (_currentPage == 0) ? page0Capacity : otherPageCapacity;
            int endIdx = Math.Min(totalFolders, startIdx + curCap);
            double titleFontSize = Math.Round(Math.Max(18 * u, Math.Min(25 * u, cardSize * 0.064)));
            DropShadowEffect sharedTightHalo = CreateTightTitleHalo(u);
            DropShadowEffect sharedTitleShadow = CreateTextShadow(u);
            SolidColorBrush sharedTitleBrush = CreateFrozenBrush(252, 250, 252, 255);

            for (int idx = startIdx; idx < endIdx; idx++)
            {
                int slot = idx - startIdx;
                int r = slot / cols;
                int c = slot % cols;
                double cx = gridLeft + c * (cardSize + colGap);
                double cy = startY + r * (cardSize + titleGap + titleH + rowGap);

                FolderGroup group = _workspace.Folders[idx];
                _folderCardRects[group.FolderName] = new Rect(cx, cy, cardSize, cardSize);
                UIElement cardEl = BuildBigFolderCard(group, cx, cy, cardSize, W, H, u);
                Canvas.SetLeft(cardEl, cx);
                Canvas.SetTop(cardEl, cy);
                _desktopCanvas.Children.Add(cardEl);

                UIElement titleWrap = BuildFolderTitleElement(group, cardSize + Math.Round(10 * u), titleFontSize, sharedTightHalo, sharedTitleShadow, sharedTitleBrush, u);
                Canvas.SetLeft(titleWrap, cx - Math.Round(5 * u));
                Canvas.SetTop(titleWrap, cy + cardSize + titleGap);
                _desktopCanvas.Children.Add(titleWrap);
            }

            // Render Smartphone-Style Frosted Glass Widget Deck on Page 1 beneath Big Folders!
            if (_currentPage == 0 && hasWidgetsOnPage0 && _widgetDeck != null)
            {
                int cardsShown = Math.Max(0, endIdx - startIdx);
                int actualFolderRows = (cardsShown > 0) ? (int)Math.Ceiling((double)cardsShown / cols) : 0;
                double usedFolderH = (actualFolderRows > 0)
                    ? (actualFolderRows * (cardSize + titleGap + titleH) + (actualFolderRows - 1) * rowGap)
                    : 0;
                double widgetTop = startY + usedFolderH + (actualFolderRows > 0 ? Math.Round(24 * u) : 0);
                double widgetBottomPad = showPageDots ? Math.Round(16 * u) : Math.Round(4 * u);
                double widgetH = (startY + totalGridH - widgetBottomPad) - widgetTop;
                if (widgetH >= 130 * u)
                {
                    _widgetDeck.RenderIntoCanvas(_desktopCanvas, _wallpaper, gridLeft, widgetTop, totalGridW, widgetH, W, H, u);
                }
            }

            // Smartphone-style Page Indicator Dots when > 1 page!
            double afterGridY = startY + totalGridH;
            if (showPageDots)
            {
                UIElement dots = BuildPageIndicator(W, u);
                Canvas.SetLeft(dots, 0);
                Canvas.SetTop(dots, Math.Round(afterGridY + Math.Round(8 * u)));
                _desktopCanvas.Children.Add(dots);
                afterGridY += pageDotsH;
            }

            // Render Floating Frosted Search Pill ("搜索")
            double searchW = Math.Round(196 * u);
            double searchX = Math.Round((W - searchW) * 0.5);
            double searchY = Math.Round(afterGridY + searchTopGap);
            _lastSearchPillTopY = searchY;
            UIElement searchPill = BuildSearchPill(searchX, searchY, searchW, searchH, W, H, u);
            _searchPillElement = searchPill as FrameworkElement;
            Canvas.SetLeft(searchPill, searchX);
            Canvas.SetTop(searchPill, searchY);
            _desktopCanvas.Children.Add(searchPill);

            // Render Frosted Glass Portrait Dock Capsule (matches Landscape studio quality!)
            double dockY = Math.Round(searchY + searchH + dockTopGap);
            _lastDockTopY = hasDock ? dockY : (H - bottomMargin);
            if (hasDock)
            {
                RenderDockCapsule(W, H, dockY, dockMetrics, false, u);
            }
        }

        private void BuildTopClockOverlay(double W, double topY, double timeFontSz, double dateFontSz, double u)
        {
            StackPanel clockPanel = new StackPanel();
            clockPanel.Orientation = Orientation.Vertical;
            clockPanel.HorizontalAlignment = HorizontalAlignment.Center;
            clockPanel.Width = W;

            DropShadowEffect clockAmbient = GetFrozenShadowEffect(
                Color.FromRgb(4, 10, 22), Math.Round(18 * u), Math.Round(3 * u), 270, 0.62);
            SolidColorBrush shadowInk = CreateFrozenBrush(185, 0, 0, 0);

            Grid timeWrap = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
            _clockTimeShadow = new TextBlock
            {
                FontFamily = FontDisplay,
                FontSize = timeFontSz,
                FontWeight = FontWeights.SemiBold,
                Foreground = shadowInk,
                HorizontalAlignment = HorizontalAlignment.Center,
                IsHitTestVisible = false,
                Effect = clockAmbient
            };
            _clockTimeText = new TextBlock
            {
                FontFamily = FontDisplay,
                FontSize = timeFontSz,
                FontWeight = FontWeights.SemiBold,
                Foreground = CreateFrozenBrush(252, 252, 254, 255),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            timeWrap.Children.Add(_clockTimeShadow);
            timeWrap.Children.Add(_clockTimeText);

            Grid dateWrap = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, Math.Round(1 * u), 0, 0)
            };
            _clockDateShadow = new TextBlock
            {
                FontFamily = FontText,
                FontSize = dateFontSz,
                FontWeight = FontWeights.Medium,
                Foreground = shadowInk,
                HorizontalAlignment = HorizontalAlignment.Center,
                IsHitTestVisible = false,
                Effect = clockAmbient
            };
            _clockDateText = new TextBlock
            {
                FontFamily = FontText,
                FontSize = dateFontSz,
                FontWeight = FontWeights.Medium,
                Foreground = CreateFrozenBrush(240, 244, 250, 255),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            dateWrap.Children.Add(_clockDateShadow);
            dateWrap.Children.Add(_clockDateText);

            UpdateClockTexts();
            clockPanel.Children.Add(timeWrap);
            clockPanel.Children.Add(dateWrap);
            Canvas.SetLeft(clockPanel, 0);
            Canvas.SetTop(clockPanel, topY);
            _desktopCanvas.Children.Add(clockPanel);
        }

        private UIElement BuildFolderTitleElement(
            FolderGroup group, double width, double fontSize,
            DropShadowEffect tightHalo, DropShadowEffect ambientShadow, SolidColorBrush fgBrush, double u)
        {
            Grid titleWrap = new Grid();
            titleWrap.Width = width;
            titleWrap.Background = Brushes.Transparent;
            titleWrap.Cursor = Cursors.Hand;

            // Velvety zero-offset dual-tier Gaussian shadow: both shadow TextBlocks match the foreground
            // geometry pixel-for-pixel so ONLY smooth diffused dark-slate auras radiate from behind crisp white text
            // (completely eliminating harsh black 1px step-outlines while staying effortlessly legible over bright snow!)
            TextBlock ambientLayer = new TextBlock();
            ambientLayer.Text = group.DisplayName;
            ambientLayer.Width = width;
            ambientLayer.TextAlignment = TextAlignment.Center;
            ambientLayer.TextTrimming = TextTrimming.CharacterEllipsis;
            ambientLayer.FontFamily = FontDisplay;
            ambientLayer.FontSize = fontSize;
            ambientLayer.FontWeight = FontWeights.SemiBold;
            ambientLayer.Foreground = fgBrush;
            ambientLayer.IsHitTestVisible = false;
            ambientLayer.Effect = ambientShadow;
            titleWrap.Children.Add(ambientLayer);

            TextBlock tightLayer = new TextBlock();
            tightLayer.Text = group.DisplayName;
            tightLayer.Width = width;
            tightLayer.TextAlignment = TextAlignment.Center;
            tightLayer.TextTrimming = TextTrimming.CharacterEllipsis;
            tightLayer.FontFamily = FontDisplay;
            tightLayer.FontSize = fontSize;
            tightLayer.FontWeight = FontWeights.SemiBold;
            tightLayer.Foreground = fgBrush;
            tightLayer.IsHitTestVisible = false;
            tightLayer.Effect = tightHalo;
            titleWrap.Children.Add(tightLayer);

            TextBlock title = new TextBlock();
            title.Text = group.DisplayName;
            title.Width = width;
            title.TextAlignment = TextAlignment.Center;
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            title.FontFamily = FontDisplay;
            title.FontSize = fontSize;
            title.FontWeight = FontWeights.SemiBold;
            title.Foreground = fgBrush;
            titleWrap.Children.Add(title);

            FolderGroup capturedGroup = group;
            bool dragArmed = false;
            Point dragOrigin = new Point(0, 0);

            titleWrap.MouseEnter += delegate(object s, MouseEventArgs e)
            {
                if (e.LeftButton != MouseButtonState.Pressed) dragArmed = false;
            };
            titleWrap.MouseLeave += delegate
            {
                dragArmed = false;
            };
            titleWrap.IsVisibleChanged += delegate
            {
                if (!titleWrap.IsVisible) dragArmed = false;
            };
            titleWrap.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                dragArmed = true;
                dragOrigin = e.GetPosition(_rootGrid);
                e.Handled = true;
            };
            titleWrap.MouseMove += delegate(object s, MouseEventArgs e)
            {
                if (!dragArmed) return;
                if (e.LeftButton != MouseButtonState.Pressed)
                {
                    dragArmed = false;
                    return;
                }
                Point cur = e.GetPosition(_rootGrid);
                double dx = cur.X - dragOrigin.X;
                double dy = cur.Y - dragOrigin.Y;
                if (dx * dx + dy * dy >= 81.0)
                {
                    dragArmed = false;
                    StartFolderCardDrag(capturedGroup, cur);
                }
            };
            titleWrap.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                bool wasArmed = dragArmed;
                dragArmed = false;
                if (wasArmed && !_isPointerDragging)
                {
                    OpenFolderModal(capturedGroup);
                }
                e.Handled = true;
            };
            titleWrap.MouseRightButtonDown += delegate
            {
                dragArmed = false;
                titleWrap.ContextMenu = BuildCardContextMenu(capturedGroup);
            };
            return titleWrap;
        }

        private void RenderDesktopLandscape(double W, double H, double u)
        {
            int totalFolders = _workspace.Folders.Count;
            int cols, rows;
            ResolveEffectiveGrid(totalFolders, out cols, out rows);

            int activeWidgetCount = CountActiveWidgets();
            bool hasWidgetsOnPage0 = (activeWidgetCount > 0);
            int page0Capacity = Math.Max(1, cols * rows);

            // Page 2+ has full landscape width (no widgets) so fit slightly more columns nicely
            int otherCols = hasWidgetsOnPage0 ? Math.Max(cols, 4) : cols;
            int otherRows = Math.Max(rows, 2);
            int otherPageCapacity = Math.Max(1, otherCols * otherRows);

            if (totalFolders <= page0Capacity)
            {
                _totalPages = 1;
            }
            else
            {
                _totalPages = 1 + (int)Math.Ceiling((double)(totalFolders - page0Capacity) / otherPageCapacity);
            }

            if (_currentPage >= _totalPages) _currentPage = _totalPages - 1;
            if (_currentPage < 0) _currentPage = 0;

            bool showPageDots = _totalPages > 1;
            bool splitStage = (_currentPage == 0 && hasWidgetsOnPage0 && _widgetDeck != null);

            // Cap content width on ultrawide monitors (21:9 / 32:9) so elements stay ergonomically centered
            double stageW = Math.Round(Math.Min(W * 0.90, H * 1.88));
            double stageLeft = Math.Round((W - stageW) * 0.5);

            // Compact top clock in landscape (modern Windows 11 Segoe UI Variable Display harmony)
            double topZone = _config.ShowTopClock ? Math.Round(100 * u) : Math.Round(H * 0.044);
            if (_config.ShowTopClock)
            {
                BuildTopClockOverlay(W, Math.Round(12 * u), Math.Round(48 * u), Math.Round(16.5 * u), u);
            }

            // Bottom macOS/iPadOS-style compact floating Dock + Search Pill vertical budget (adapts cleanly for 1-10 Dock items)
            int dockCount = (_workspace.DockItems != null) ? _workspace.DockItems.Count : 0;
            DockCapsuleMetrics dockMetrics = ComputeDockCapsuleMetrics(W, H, u, dockCount, true);
            bool hasDock = dockMetrics.Count > 0;
            double bottomMargin = Math.Round(Math.Max(14, H * 0.018));
            double dockCapsuleH = hasDock ? dockMetrics.CapsuleH : 0;
            double dockTopGap = hasDock ? Math.Round(13 * u) : 0;
            double searchH = Math.Round(44 * u);
            double pageDotsH = showPageDots ? Math.Round(22 * u) : 0;
            double searchTopGap = Math.Round(showPageDots ? 10 * u : 14 * u);

            double bottomReserve = pageDotsH + searchTopGap + searchH + dockTopGap + dockCapsuleH + bottomMargin;
            double availStageH = Math.Max(260, H - topZone - bottomReserve);

            int pageCols = (_currentPage == 0) ? cols : otherCols;
            int pageRows = (_currentPage == 0) ? rows : otherRows;
            int startIdx = (_currentPage == 0) ? 0 : (page0Capacity + (_currentPage - 1) * otherPageCapacity);
            int curCap = (_currentPage == 0) ? page0Capacity : otherPageCapacity;
            int endIdx = Math.Min(totalFolders, startIdx + curCap);
            int cardsShown = Math.Max(0, endIdx - startIdx);

            double folderZoneLeft = stageLeft;
            double folderZoneW = stageW;
            double widgetZoneLeft = 0;
            double widgetZoneW = 0;
            int widgetCols = (activeWidgetCount <= 2) ? 1 : 2;

            if (splitStage)
            {
                double gutter = Math.Round(Math.Max(34 * u, stageW * 0.030));
                if (widgetCols == 1)
                {
                    widgetZoneW = Math.Round(Math.Min(stageW * 0.34, 520 * u));
                }
                else
                {
                    widgetZoneW = Math.Round(Math.Min(stageW * 0.49, 1000 * u));
                }
                folderZoneW = Math.Max(320, stageW - widgetZoneW - gutter);
                widgetZoneLeft = folderZoneLeft + folderZoneW + gutter;
            }

            double colGap = Math.Round(Math.Max(18 * u, Math.Min(34 * u, folderZoneW * 0.038)));
            double rowGap = Math.Round(Math.Max(16 * u, Math.Min(28 * u, availStageH * 0.036)));
            double titleGap = Math.Round(9 * u);
            double titleH = Math.Round(26 * u);

            double maxCardW = (folderZoneW - colGap * (pageCols - 1)) / pageCols;
            double maxCardH = (availStageH - pageRows * (titleGap + titleH) - (pageRows - 1) * rowGap) / pageRows;
            double maxCapCard = splitStage ? Math.Round(285 * u) : Math.Round(258 * u);
            double cardSize = Math.Floor(Math.Max(110, Math.Min(Math.Min(maxCardW, maxCardH), maxCapCard)));

            int actualRows = (cardsShown > 0) ? Math.Min(pageRows, (int)Math.Ceiling((double)cardsShown / pageCols)) : pageRows;
            int actualCols = (cardsShown > 0 && cardsShown < pageCols) ? cardsShown : pageCols;

            double totalFolderW = actualCols * cardSize + (actualCols - 1) * colGap;
            double totalFolderH = actualRows * (cardSize + titleGap + titleH) + (actualRows - 1) * rowGap;

            double gridLeft = Math.Round(folderZoneLeft + (folderZoneW - totalFolderW) * 0.5);
            double gridTop = Math.Round(topZone + Math.Max(0, (availStageH - totalFolderH) * 0.48));

            // Crisp readable title sizing tailored for landscape card widths
            double titleFontSize = Math.Round(Math.Max(15 * u, Math.Min(20.5 * u, cardSize * 0.078)));
            DropShadowEffect sharedTightHalo = CreateTightTitleHalo(u);
            DropShadowEffect sharedTitleShadow = CreateTextShadow(u);
            SolidColorBrush sharedTitleBrush = CreateFrozenBrush(252, 250, 252, 255);

            for (int idx = startIdx; idx < endIdx; idx++)
            {
                int slot = idx - startIdx;
                int r = slot / pageCols;
                int c = slot % pageCols;
                double cx = gridLeft + c * (cardSize + colGap);
                double cy = gridTop + r * (cardSize + titleGap + titleH + rowGap);

                FolderGroup group = _workspace.Folders[idx];
                _folderCardRects[group.FolderName] = new Rect(cx, cy, cardSize, cardSize);
                UIElement cardEl = BuildBigFolderCard(group, cx, cy, cardSize, W, H, u);
                Canvas.SetLeft(cardEl, cx);
                Canvas.SetTop(cardEl, cy);
                _desktopCanvas.Children.Add(cardEl);

                UIElement titleWrap = BuildFolderTitleElement(
                    group, cardSize + Math.Round(12 * u), titleFontSize,
                    sharedTightHalo, sharedTitleShadow, sharedTitleBrush, u);
                Canvas.SetLeft(titleWrap, cx - Math.Round(6 * u));
                Canvas.SetTop(titleWrap, cy + cardSize + titleGap);
                _desktopCanvas.Children.Add(titleWrap);
            }

            // Right Side: Swiss-Grid Architectural Alignment!
            // Align top edge pixel-for-pixel with Row 1 of Folder Cards, and bottom edge with the bottom of the last row's cards!
            if (splitStage)
            {
                double lastCardBottom = gridTop + actualRows * cardSize + (actualRows - 1) * (titleGap + titleH + rowGap);
                double folderCardSpanH = Math.Max(Math.Round(460 * u), lastCardBottom - gridTop);
                double widgetTargetH = Math.Min(availStageH, folderCardSpanH);
                double widgetTop = (actualRows >= 2)
                    ? gridTop
                    : Math.Round(topZone + Math.Max(0, (availStageH - widgetTargetH) * 0.48));

                _widgetDeck.RenderIntoCanvas(
                    _desktopCanvas, _wallpaper,
                    widgetZoneLeft, widgetTop, widgetZoneW, widgetTargetH,
                    W, H, u, widgetCols, true);
            }

            // Bottom anchored controls (Page Dots -> Floating Search Pill -> Compact Frosted Dock Capsule)
            double dockCapsuleY = H - bottomMargin - dockCapsuleH;
            double searchW = Math.Round(212 * u);
            double searchX = Math.Round((W - searchW) * 0.5);
            double searchY = Math.Round((hasDock ? (dockCapsuleY - dockTopGap) : (H - bottomMargin)) - searchH);

            if (showPageDots)
            {
                UIElement dots = BuildPageIndicator(W, u);
                Canvas.SetLeft(dots, 0);
                Canvas.SetTop(dots, Math.Round(searchY - searchTopGap - pageDotsH + Math.Round(4 * u)));
                _desktopCanvas.Children.Add(dots);
            }

            _lastSearchPillTopY = searchY;
            UIElement searchPill = BuildSearchPill(searchX, searchY, searchW, searchH, W, H, u);
            _searchPillElement = searchPill as FrameworkElement;
            Canvas.SetLeft(searchPill, searchX);
            Canvas.SetTop(searchPill, searchY);
            _desktopCanvas.Children.Add(searchPill);

            _lastDockTopY = hasDock ? dockCapsuleY : (H - bottomMargin);
            if (hasDock)
            {
                RenderDockCapsule(W, H, dockCapsuleY, dockMetrics, true, u);
            }
        }

        internal static DockCapsuleMetrics ComputeDockCapsuleMetrics(double W, double H, double u, int dockItemCount, bool isLandscape)
        {
            DockCapsuleMetrics m = new DockCapsuleMetrics();
            int maxIcons = 10;
            int count = Math.Min(maxIcons, Math.Max(0, dockItemCount));
            m.Count = count;
            if (count == 0) return m;

            double baseIconSize = isLandscape
                ? Math.Round(Math.Min(74 * u, H * 0.066))
                : Math.Round(Math.Min(110 * u, Math.Min(W * 0.105, H * 0.060)));
            double basePadX = Math.Round((isLandscape ? 22 : 28) * u);
            double basePadY = Math.Round((isLandscape ? 10 : 15) * u);
            double baseGap = Math.Round((isLandscape ? 20 : 28) * u);
            double baseDotBandH = Math.Round((isLandscape ? 5 : 7) * u);
            double baseDotGap = Math.Round((isLandscape ? 4.5 : 6) * u);
            double baseDotW = Math.Round((isLandscape ? 14 : 18) * u);
            double baseDotH = Math.Round((isLandscape ? 3.5 : 4.2) * u);
            double baseRadius = Math.Round((isLandscape ? 26 : 36) * u);

            double maxDockW = Math.Round(W * (isLandscape ? 0.72 : 0.90));
            double rawW = basePadX * 2 + count * baseIconSize + (count - 1) * baseGap;
            double shrink = (rawW > maxDockW && rawW > 1) ? (maxDockW / rawW) : 1.0;

            double padX = Math.Max(Math.Round(10 * u), Math.Round(basePadX * shrink));
            double gap = Math.Max(Math.Round(8 * u), Math.Round(baseGap * shrink));
            double iconSize = Math.Max(Math.Round(28 * u), Math.Floor(baseIconSize * shrink));
            double capsuleW = padX * 2 + count * iconSize + (count - 1) * gap;
            if (capsuleW > maxDockW)
            {
                iconSize = Math.Max(20.0, Math.Floor((maxDockW - padX * 2 - (count - 1) * gap) / count));
                capsuleW = padX * 2 + count * iconSize + (count - 1) * gap;
            }

            // Recompute exact visual ratio from final iconSize so height, padding, corner radius & dot band adapt seamlessly!
            double finalRatio = baseIconSize > 1 ? Math.Min(1.0, iconSize / baseIconSize) : 1.0;
            double padY = Math.Max(Math.Round(6 * u), Math.Round(basePadY * finalRatio));
            double dotBandH = Math.Max(Math.Round(4 * u), Math.Round(baseDotBandH * finalRatio));
            double dotGap = Math.Max(Math.Round(3 * u), Math.Round(baseDotGap * finalRatio));
            double dotW = Math.Max(Math.Round(10 * u), Math.Round(baseDotW * finalRatio));
            double dotH = Math.Max(Math.Round(2.8 * u), Math.Round(baseDotH * finalRatio));
            double capsuleH = iconSize + padY * 2 + dotBandH;
            double radius = Math.Min(Math.Round(baseRadius * finalRatio), Math.Floor(capsuleH * 0.44));

            // Keep running indicator centered comfortably in the bottom band below icons
            double maxDotOffsetY = Math.Max(padY + iconSize + 1, capsuleH - dotH - Math.Max(2.0, Math.Round(padY * 0.28)));
            double dotOffsetY = Math.Min(Math.Round(padY + iconSize + dotGap), maxDotOffsetY);

            m.IconSize = iconSize;
            m.PadX = padX;
            m.PadY = padY;
            m.Gap = gap;
            m.DotBandH = dotBandH;
            m.DotOffsetY = dotOffsetY;
            m.DotW = dotW;
            m.DotH = dotH;
            m.CapsuleW = capsuleW;
            m.CapsuleH = capsuleH;
            m.Radius = radius;
            return m;
        }

        private void RenderDockCapsule(double W, double H, double capsuleY, DockCapsuleMetrics dm, bool isLandscape, double u)
        {
            List<AppItem> dockItems = _workspace.DockItems;
            int count = Math.Min(dm.Count, dockItems != null ? dockItems.Count : 0);
            if (count == 0) return;

            double padX = dm.PadX;
            double padY = dm.PadY;
            double gap = dm.Gap;
            double effectiveIconSize = dm.IconSize;
            double capsuleW = dm.CapsuleW;
            double capsuleH = dm.CapsuleH;
            double capsuleX = Math.Round((W - capsuleW) * 0.5);
            double radius = dm.Radius;
            double borderW = Math.Max(1.0, Math.Round(1.25 * u * 10) / 10.0);

            _landscapeDockRect = new Rect(capsuleX, capsuleY, capsuleW, capsuleH);
            _landscapeDockNormalBorderW = borderW;

            Grid dockRoot = new Grid();
            dockRoot.Width = capsuleW;
            dockRoot.Height = capsuleH;
            dockRoot.AllowDrop = true;
            dockRoot.Drop += delegate(object s, DragEventArgs e)
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    string[] dropped = e.Data.GetData(DataFormats.FileDrop) as string[];
                    FolderScanner.IngestDroppedFiles(dropped, FolderScanner.DockDir(_config));
                    ReloadWorkspace();
                }
                e.Handled = true;
            };

            Border shadowPlate = new Border();
            shadowPlate.CornerRadius = new CornerRadius(radius);
            shadowPlate.Background = CreateFrozenBrush(145, 5, 9, 16);
            shadowPlate.IsHitTestVisible = false;
            shadowPlate.CacheMode = new BitmapCache();
            shadowPlate.Effect = GetFrozenShadowEffect(Colors.Black, Math.Round(26 * u), Math.Round(7 * u), 270, 0.40);
            dockRoot.Children.Add(shadowPlate);

            Border blurLayer = new Border();
            blurLayer.CornerRadius = new CornerRadius(radius);
            blurLayer.IsHitTestVisible = false;
            if (_wallpaper != null && _wallpaper.BlurredBackdrop != null)
            {
                ImageBrush ib = new ImageBrush(_wallpaper.BlurredBackdrop);
                ib.ViewboxUnits = BrushMappingMode.RelativeToBoundingBox;
                ib.Viewbox = new Rect(
                    Math.Max(0, Math.Min(0.99, capsuleX / W)),
                    Math.Max(0, Math.Min(0.99, capsuleY / H)),
                    Math.Max(0.01, Math.Min(1.0 - capsuleX / W, capsuleW / W)),
                    Math.Max(0.01, Math.Min(1.0 - capsuleY / H, capsuleH / H)));
                ib.Stretch = Stretch.Fill;
                ib.Freeze();
                blurLayer.Background = ib;
            }
            dockRoot.Children.Add(blurLayer);

            LinearGradientBrush rim = new LinearGradientBrush();
            rim.StartPoint = new Point(0, 0);
            rim.EndPoint = new Point(0, 1);
            rim.GradientStops.Add(new GradientStop(Color.FromArgb(116, 255, 255, 255), 0.0));
            rim.GradientStops.Add(new GradientStop(Color.FromArgb(34, 255, 255, 255), 0.55));
            rim.GradientStops.Add(new GradientStop(Color.FromArgb(24, 255, 255, 255), 1.0));
            rim.Freeze();
            _landscapeDockNormalRim = rim;

            double baseOp = Math.Max(0.28, Math.Min(0.92, _config.CardTintOpacity));
            byte dockTopA = (byte)Math.Round(Math.Min(0.88, baseOp + 0.05) * 255);
            byte dockBotA = (byte)Math.Round(Math.Min(0.92, baseOp + 0.14) * 255);
            LinearGradientBrush dockTint = new LinearGradientBrush();
            dockTint.StartPoint = new Point(0, 0);
            dockTint.EndPoint = new Point(0, 1);
            dockTint.GradientStops.Add(new GradientStop(Color.FromArgb(dockTopA, 15, 22, 38), 0.0));
            dockTint.GradientStops.Add(new GradientStop(Color.FromArgb(dockBotA, 9, 14, 26), 1.0));
            dockTint.Freeze();

            Border glass = new Border();
            glass.CornerRadius = new CornerRadius(radius);
            glass.Background = dockTint;
            glass.BorderThickness = new Thickness(borderW);
            glass.BorderBrush = rim;
            _landscapeDockGlass = glass;
            dockRoot.Children.Add(glass);

            // Delicate top specular sheen matching Folder Cards & Widgets
            LinearGradientBrush dockSheen = new LinearGradientBrush();
            dockSheen.StartPoint = new Point(0.15, 0);
            dockSheen.EndPoint = new Point(0.85, 0.9);
            dockSheen.GradientStops.Add(new GradientStop(Color.FromArgb(24, 255, 255, 255), 0.0));
            dockSheen.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.7));
            dockSheen.Freeze();
            Border sheenOverlay = new Border
            {
                CornerRadius = new CornerRadius(Math.Max(1, radius - borderW)),
                Background = dockSheen,
                IsHitTestVisible = false
            };
            dockRoot.Children.Add(sheenOverlay);

            // Overlay Canvas inside dockRoot (ClipToBounds=false) so icons + gaps share one seamless hover surface with ZERO gap flicker!
            Canvas dockIconsCanvas = new Canvas();
            dockIconsCanvas.Width = capsuleW;
            dockIconsCanvas.Height = capsuleH;
            dockIconsCanvas.Background = Brushes.Transparent;
            dockIconsCanvas.ClipToBounds = false;
            dockRoot.Children.Add(dockIconsCanvas);

            double localIconY = padY;
            double localDotY = dm.DotOffsetY;
            double dotW = dm.DotW;
            double dotH = dm.DotH;

            for (int i = 0; i < count; i++)
            {
                double localTileX = Math.Round(padX + i * (effectiveIconSize + gap));
                ScaleTransform dScale;
                TranslateTransform dTrans;
                UIElement tile = BuildAppIconTile(dockItems[i], null, dockItems, effectiveIconSize, false, true, u, out dScale, out dTrans, i);
                Canvas.SetLeft(tile, localTileX);
                Canvas.SetTop(tile, localIconY);
                dockIconsCanvas.Children.Add(tile);

                Border runDot = new Border
                {
                    Width = dotW,
                    Height = dotH,
                    CornerRadius = new CornerRadius(dotH * 0.5),
                    Background = CreateFrozenBrush(235, 186, 230, 253),
                    IsHitTestVisible = false,
                    Opacity = 0
                };
                Canvas.SetLeft(runDot, Math.Round(localTileX + (effectiveIconSize - dotW) * 0.5));
                Canvas.SetTop(runDot, localDotY);
                dockIconsCanvas.Children.Add(runDot);

                _dockTileRefs.Add(new DockTileRef
                {
                    Item = dockItems[i],
                    TargetExe = FolderScanner.ResolveTargetExeName(dockItems[i].FilePath),
                    Scale = dScale,
                    Translate = dTrans,
                    RunningDot = runDot,
                    CenterX = capsuleX + localTileX + effectiveIconSize * 0.5,
                    TopY = capsuleY
                });
            }

            double waveSigma = Math.Max(effectiveIconSize * 1.15, (effectiveIconSize + gap) * 0.95);
            double labelHitRadius = effectiveIconSize * 0.62;
            dockRoot.MouseMove += delegate(object s, MouseEventArgs e)
            {
                if (_isPointerDragging) return;
                Point pt = e.GetPosition(_desktopCanvas);
                ApplyContinuousDockWave(pt.X, waveSigma, labelHitRadius, u);
            };
            dockRoot.MouseLeave += delegate
            {
                if (_isPointerDragging) return;
                ApplyDockWaveHover(-1, u);
            };

            Canvas.SetLeft(dockRoot, capsuleX);
            Canvas.SetTop(dockRoot, capsuleY);
            _desktopCanvas.Children.Add(dockRoot);

            // Floating frosted Dock hover label pill (macOS style, clamped inside monitor edges!)
            _dockHoverLabelPill = new Border
            {
                CornerRadius = new CornerRadius(Math.Round((isLandscape ? 10 : 12) * u)),
                Background = CreateFrozenBrush(238, 14, 20, 34),
                BorderBrush = CreateFrozenBrush(86, 255, 255, 255),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(Math.Round((isLandscape ? 12 : 14) * u), Math.Round((isLandscape ? 5 : 6) * u), Math.Round((isLandscape ? 12 : 14) * u), Math.Round((isLandscape ? 5 : 6) * u)),
                Opacity = 0,
                IsHitTestVisible = false
            };
            _dockHoverLabelText = new TextBlock
            {
                FontFamily = FontText,
                FontSize = Math.Round((isLandscape ? 13.5 : 16) * u),
                FontWeight = FontWeights.Medium,
                Foreground = Brushes.White
            };
            _dockHoverLabelPill.Child = _dockHoverLabelText;
            Panel.SetZIndex(_dockHoverLabelPill, 80);
            _desktopCanvas.Children.Add(_dockHoverLabelPill);

            if (!_isPreviewMode)
            {
                RefreshDockRunningIndicatorsAsync();
            }
        }

        private void ApplyContinuousDockWave(double pointerX, double sigma, double labelHitRadius, double u)
        {
            if (_dockTileRefs.Count == 0) return;
            if (!double.IsNaN(_lastDockWavePointerX) && Math.Abs(pointerX - _lastDockWavePointerX) < 0.75) return;
            _lastDockWavePointerX = pointerX;

            int nearestIdx = -1;
            double nearestDist = double.MaxValue;

            for (int i = 0; i < _dockTileRefs.Count; i++)
            {
                DockTileRef dt = _dockTileRefs[i];
                double dist = Math.Abs(pointerX - dt.CenterX);
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearestIdx = i;
                }
                double falloff = Math.Exp(-(dist * dist) / (2.0 * sigma * sigma));
                double targetScale = Math.Round((1.0 + 0.14 * falloff) * 1000.0) / 1000.0;
                double targetLift = -Math.Round(7.5 * u * falloff * 10.0) / 10.0;
                if (Math.Abs(targetScale - dt.LastTargetScale) > 0.003)
                {
                    dt.LastTargetScale = targetScale;
                    AnimateScale(dt.Scale, targetScale, 75);
                }
                if (Math.Abs(targetLift - dt.LastTargetLift) > 0.18)
                {
                    dt.LastTargetLift = targetLift;
                    AnimateTranslateY(dt.Translate, targetLift, 75);
                }
            }

            int nextHoverIdx = (nearestIdx >= 0 && nearestDist <= labelHitRadius) ? nearestIdx : -1;
            if (nextHoverIdx != _lastDockHoverIndex)
            {
                _lastDockHoverIndex = nextHoverIdx;
                if (nextHoverIdx >= 0)
                {
                    PositionAndShowDockHoverPill(nextHoverIdx, u);
                }
                else
                {
                    HideDockHoverPill();
                }
            }
        }

        private void ApplyDockWaveHover(int hoveredIndex, double u)
        {
            if (_dockTileRefs.Count == 0) return;
            _lastDockWavePointerX = double.NaN;
            for (int i = 0; i < _dockTileRefs.Count; i++)
            {
                DockTileRef dt = _dockTileRefs[i];
                double targetScale;
                double targetLift;
                int dur;
                if (hoveredIndex < 0)
                {
                    targetScale = 1.0; targetLift = 0; dur = 150;
                }
                else if (i == hoveredIndex)
                {
                    targetScale = 1.14; targetLift = -Math.Round(7.5 * u); dur = 130;
                }
                else if (Math.Abs(i - hoveredIndex) == 1)
                {
                    targetScale = 1.05; targetLift = -Math.Round(2.8 * u); dur = 140;
                }
                else
                {
                    targetScale = 1.0; targetLift = 0; dur = 150;
                }
                dt.LastTargetScale = targetScale;
                dt.LastTargetLift = targetLift;
                AnimateScale(dt.Scale, targetScale, dur);
                AnimateTranslateY(dt.Translate, targetLift, dur);
            }

            _lastDockHoverIndex = hoveredIndex;
            if (hoveredIndex >= 0 && hoveredIndex < _dockTileRefs.Count && !_isPointerDragging)
            {
                PositionAndShowDockHoverPill(hoveredIndex, u);
            }
            else
            {
                HideDockHoverPill();
            }
        }

        private void PositionAndShowDockHoverPill(int index, double u)
        {
            if (_dockHoverLabelPill == null || _dockHoverLabelText == null || index < 0 || index >= _dockTileRefs.Count) return;
            DockTileRef d = _dockTileRefs[index];
            _dockHoverLabelText.Text = d.Item.DisplayName;
            _dockHoverLabelPill.Measure(new Size(420, 80));
            double pw = _dockHoverLabelPill.DesiredSize.Width;
            double ph = _dockHoverLabelPill.DesiredSize.Height;
            double W = _canvasW > 200 ? _canvasW : 1920;
            double margin = Math.Round(12 * u);
            double left = Math.Max(margin, Math.Min(W - pw - margin, Math.Round(d.CenterX - pw * 0.5)));
            double top = Math.Round(d.TopY - ph - Math.Round(10 * u));
            Canvas.SetLeft(_dockHoverLabelPill, left);
            Canvas.SetTop(_dockHoverLabelPill, top);

            if (_isPreviewMode)
            {
                _dockHoverLabelPill.Opacity = 1.0;
                _dockWaveActive = true;
                if (_searchPillElement != null) _searchPillElement.Opacity = 0.08;
            }
            else
            {
                _dockHoverLabelPill.BeginAnimation(UIElement.OpacityProperty, MakeAnim(null, 1.0, 120, _easeOutCubic, 0));
                if (!_dockWaveActive)
                {
                    _dockWaveActive = true;
                    // Gracefully recede Search Pill while Dock hover label occupies the space directly above Dock!
                    if (_searchPillElement != null)
                    {
                        _searchPillElement.BeginAnimation(UIElement.OpacityProperty, MakeAnim(null, 0.08, 130, _easeOutCubic, 0));
                    }
                }
            }
        }

        private void HideDockHoverPill()
        {
            if (_dockHoverLabelPill != null)
            {
                if (_isPreviewMode) _dockHoverLabelPill.Opacity = 0.0;
                else _dockHoverLabelPill.BeginAnimation(UIElement.OpacityProperty, MakeAnim(null, 0.0, 120, _easeOutCubic, 0));
            }
            if (_dockWaveActive)
            {
                _dockWaveActive = false;
                if (_searchPillElement != null)
                {
                    if (_isPreviewMode) _searchPillElement.Opacity = 1.0;
                    else _searchPillElement.BeginAnimation(UIElement.OpacityProperty, MakeAnim(null, 1.0, 160, _easeOutCubic, 0));
                }
            }
        }

        private void RefreshDockRunningIndicatorsAsync()
        {
            if (_isPreviewMode || _dockTileRefs.Count == 0) return;
            if (System.Threading.Interlocked.CompareExchange(ref _runningProcPollInFlight, 1, 0) != 0) return;

            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                HashSet<string> running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    Process[] procs = Process.GetProcesses();
                    for (int i = 0; i < procs.Length; i++)
                    {
                        try
                        {
                            if (!string.IsNullOrEmpty(procs[i].ProcessName))
                            {
                                running.Add(procs[i].ProcessName);
                            }
                        }
                        catch { }
                        finally { try { procs[i].Dispose(); } catch { } }
                    }
                }
                catch
                {
                }
                finally
                {
                    System.Threading.Interlocked.Exchange(ref _runningProcPollInFlight, 0);
                }

                if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
                Dispatcher.BeginInvoke((Action)delegate
                {
                    for (int i = 0; i < _dockTileRefs.Count; i++)
                    {
                        DockTileRef dt = _dockTileRefs[i];
                        if (dt.RunningDot == null) continue;
                        bool isRun = IsDockItemRunning(dt.TargetExe, dt.Item.DisplayName, running);
                        double targetOp = isRun ? 0.92 : 0.0;
                        if (Math.Abs(dt.RunningDot.Opacity - targetOp) > 0.05)
                        {
                            dt.RunningDot.BeginAnimation(UIElement.OpacityProperty, MakeAnim(null, targetOp, 200, _easeOutCubic, 0));
                        }
                    }
                });
            });
        }

        private static bool IsDockItemRunning(string targetExe, string displayName, HashSet<string> running)
        {
            if (!string.IsNullOrEmpty(targetExe) && running.Contains(targetExe)) return true;
            string lowExe = (targetExe ?? "").ToLowerInvariant();
            if (lowExe == "wechat" || lowExe == "weixin" || (displayName ?? "").IndexOf("微信") >= 0)
            {
                return running.Contains("WeChat") || running.Contains("Weixin");
            }
            if (lowExe == "chrome") return running.Contains("chrome");
            if (lowExe == "steam") return running.Contains("steam") || running.Contains("steamwebhelper");
            if (lowExe == "code") return running.Contains("Code");
            return false;
        }

        private UIElement BuildPageIndicator(double W, double u)
        {
            StackPanel pills = new StackPanel();
            pills.Orientation = Orientation.Horizontal;
            pills.HorizontalAlignment = HorizontalAlignment.Center;

            for (int p = 0; p < _totalPages; p++)
            {
                bool active = (p == _currentPage);
                Border dot = new Border();
                dot.Height = Math.Round(9 * u);
                dot.Width = active ? Math.Round(26 * u) : Math.Round(9 * u);
                dot.CornerRadius = new CornerRadius(Math.Round(4.5 * u));
                dot.Margin = new Thickness(Math.Round(6 * u), 0, Math.Round(6 * u), 0);
                dot.Cursor = Cursors.Hand;
                dot.Background = active
                    ? CreateFrozenBrush(245, 255, 255, 255)
                    : CreateFrozenBrush(115, 226, 232, 240);
                dot.ToolTip = CreateDarkToolTip(string.Format("第 {0} / {1} 页 (支持鼠标滚轮翻页)", p + 1, _totalPages), u);
                int targetPage = p;
                dot.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
                {
                    GoToPage(targetPage);
                    e.Handled = true;
                };
                pills.Children.Add(dot);
            }

            // Wrap in centered Grid so StackPanel centers horizontally across full W
            Grid host = new Grid();
            host.Width = W;
            pills.HorizontalAlignment = HorizontalAlignment.Center;
            host.Children.Add(pills);
            return host;
        }

        public void OpenNewCategoryDialog()
        {
            OpenCategoryInputDialog("新建分类大文件夹", "例如：设计修图、游戏库、代码开发...", "", delegate(string name)
            {
                string created;
                string err;
                if (!FolderScanner.TryCreateCategoryFolder(_config, _workspace, name, out created, out err))
                {
                    return string.IsNullOrEmpty(err) ? "创建分类失败，请检查名称" : err;
                }
                if (!string.IsNullOrEmpty(created))
                {
                    ReloadWorkspace();
                    // Jump straight to the exact page containing the newly created card (matching Portrait/Landscape widget pagination!)
                    if (_workspace != null)
                    {
                        int p0Cap, otherCap;
                        ComputePaginationCapacities(_workspace.Folders.Count, out p0Cap, out otherCap);
                        int idx = _workspace.Folders.FindIndex(delegate(FolderGroup g) { return g.FolderName.Equals(created, StringComparison.OrdinalIgnoreCase); });
                        if (idx >= 0)
                        {
                            _currentPage = ComputePageForFolderIndex(idx, p0Cap, otherCap);
                            RenderDesktop();
                        }
                    }
                }
                return null;
            });
        }

        private void OpenRenameCategoryDialog(FolderGroup group)
        {
            if (group == null) return;
            OpenCategoryInputDialog("重命名分类", "输入新的分类名称...", group.DisplayName, delegate(string newName)
            {
                string err;
                bool renamed = FolderScanner.TryRenameCategoryFolder(_config, _workspace, group, newName, out err);
                if (!string.IsNullOrEmpty(err))
                {
                    return err;
                }
                if (renamed)
                {
                    ReloadWorkspace();
                }
                return null;
            });
        }

        private void OpenCategoryInputDialog(string title, string placeholder, string initialText, Func<string, string> onConfirm)
        {
            CloseSearchOverlay(true);
            HideAllCachedFolderModals();
            int token = ++_modalAnimToken;
            _openFolder = null;
            _activeModalView = null;
            _modalIsCachedFolder = false;
            _dialogContentHost.Children.Clear();

            double W = _canvasW > 200 ? _canvasW : 1080;
            double u = ScaleUnit;
            double dlgW = IsLandscape ? Math.Round(Math.Min(W * 0.46, 700 * u)) : Math.Round(W * 0.78);
            double radius = Math.Round(40 * u);

            Grid dlgRoot = new Grid();
            dlgRoot.Width = dlgW;
            dlgRoot.HorizontalAlignment = HorizontalAlignment.Center;
            dlgRoot.VerticalAlignment = VerticalAlignment.Center;
            dlgRoot.RenderTransformOrigin = new Point(0.5, 0.5);
            ScaleTransform st = new ScaleTransform(_isPreviewMode ? 1.0 : 0.90, _isPreviewMode ? 1.0 : 0.90);
            TranslateTransform tt = new TranslateTransform(0, _isPreviewMode ? 0 : Math.Round(18 * u));
            TransformGroup tg = new TransformGroup();
            tg.Children.Add(st);
            tg.Children.Add(tt);
            dlgRoot.RenderTransform = tg;
            dlgRoot.Opacity = _isPreviewMode ? 1.0 : 0.0;

            AddSoftShadowHalo(dlgRoot, radius, u);

            Border box = new Border();
            box.CornerRadius = new CornerRadius(radius);
            box.Background = CreateFrozenBrush(244, 16, 23, 38);
            box.BorderBrush = CreateFrozenBrush(88, 255, 255, 255);
            box.BorderThickness = new Thickness(Math.Max(1.0, Math.Round(1.2 * u * 10) / 10.0));
            box.Padding = new Thickness(Math.Round(40 * u), Math.Round(36 * u), Math.Round(40 * u), Math.Round(34 * u));

            StackPanel sp = new StackPanel();

            TextBlock titleLbl = new TextBlock();
            titleLbl.Text = title;
            titleLbl.FontSize = Math.Round(30 * u);
            titleLbl.FontWeight = FontWeights.Bold;
            titleLbl.Foreground = Brushes.White;
            titleLbl.Margin = new Thickness(0, 0, 0, Math.Round(24 * u));
            sp.Children.Add(titleLbl);

            SolidColorBrush normalBorder = CreateFrozenBrush(96, 96, 165, 250);
            SolidColorBrush errorBorder = CreateFrozenBrush(235, 248, 113, 113);
            TranslateTransform inputShakeTf = new TranslateTransform(0, 0);

            Border inputWrap = new Border();
            inputWrap.CornerRadius = new CornerRadius(Math.Round(18 * u));
            inputWrap.Background = CreateFrozenBrush(32, 255, 255, 255);
            inputWrap.BorderBrush = normalBorder;
            inputWrap.BorderThickness = new Thickness(Math.Max(1.2, Math.Round(1.4 * u * 10) / 10.0));
            inputWrap.Padding = new Thickness(Math.Round(20 * u), Math.Round(14 * u), Math.Round(20 * u), Math.Round(14 * u));
            inputWrap.RenderTransform = inputShakeTf;

            Grid textGrid = new Grid();
            TextBlock ph = new TextBlock();
            ph.Text = placeholder;
            ph.FontSize = Math.Round(24 * u);
            ph.Foreground = CreateFrozenBrush(130, 203, 213, 225);
            ph.VerticalAlignment = VerticalAlignment.Center;
            ph.IsHitTestVisible = false;
            ph.Visibility = string.IsNullOrEmpty(initialText) ? Visibility.Visible : Visibility.Collapsed;
            textGrid.Children.Add(ph);

            TextBlock errorLbl = new TextBlock();
            errorLbl.FontFamily = FontText;
            errorLbl.FontSize = Math.Round(15.5 * u);
            errorLbl.FontWeight = FontWeights.Medium;
            errorLbl.Foreground = CreateFrozenBrush(255, 252, 165, 165);
            errorLbl.TextWrapping = TextWrapping.Wrap;

            Border errorPill = new Border();
            errorPill.CornerRadius = new CornerRadius(Math.Round(10 * u));
            errorPill.Background = CreateFrozenBrush(42, 239, 68, 68);
            errorPill.BorderBrush = CreateFrozenBrush(115, 248, 113, 113);
            errorPill.BorderThickness = new Thickness(1);
            errorPill.Padding = new Thickness(Math.Round(12 * u), Math.Round(7 * u), Math.Round(12 * u), Math.Round(7 * u));
            errorPill.Margin = new Thickness(0, Math.Round(12 * u), 0, 0);
            errorPill.Visibility = Visibility.Collapsed;
            errorPill.Child = errorLbl;

            TextBox tb = new TextBox();
            tb.Text = initialText ?? "";
            tb.FontSize = Math.Round(25 * u);
            tb.Foreground = Brushes.White;
            tb.CaretBrush = Brushes.White;
            tb.Background = Brushes.Transparent;
            tb.BorderThickness = new Thickness(0);
            tb.VerticalContentAlignment = VerticalAlignment.Center;
            tb.TextChanged += delegate
            {
                ph.Visibility = string.IsNullOrEmpty(tb.Text) ? Visibility.Visible : Visibility.Collapsed;
                if (errorPill.Visibility == Visibility.Visible)
                {
                    errorPill.Visibility = Visibility.Collapsed;
                    inputWrap.BorderBrush = normalBorder;
                }
            };
            textGrid.Children.Add(tb);
            inputWrap.Child = textGrid;
            sp.Children.Add(inputWrap);
            sp.Children.Add(errorPill);

            StackPanel btnRow = new StackPanel();
            btnRow.Orientation = Orientation.Horizontal;
            btnRow.HorizontalAlignment = HorizontalAlignment.Right;
            btnRow.Margin = new Thickness(0, Math.Round(26 * u), 0, 0);

            Button cancelBtn = CreatePillButton("取消", u);
            cancelBtn.Margin = new Thickness(0, 0, Math.Round(14 * u), 0);
            cancelBtn.Click += delegate
            {
                CloseFolderModal(false);
            };
            btnRow.Children.Add(cancelBtn);

            Button okBtn = CreatePillButton("确定", u);
            okBtn.Background = CreateFrozenBrush(220, 59, 130, 246);
            okBtn.BorderBrush = CreateFrozenBrush(160, 147, 197, 253);
            Action submit = delegate
            {
                string val = tb.Text.Trim();
                string err = (onConfirm != null) ? onConfirm(val) : null;
                if (!string.IsNullOrEmpty(err))
                {
                    errorLbl.Text = err;
                    errorPill.Visibility = Visibility.Visible;
                    inputWrap.BorderBrush = errorBorder;
                    ShakeHorizontal(inputShakeTf, u);
                    tb.Focus();
                    tb.SelectAll();
                    return;
                }
                CloseFolderModal(false);
            };
            okBtn.Click += delegate { submit(); };
            tb.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Enter)
                {
                    submit();
                    e.Handled = true;
                }
            };
            btnRow.Children.Add(okBtn);
            sp.Children.Add(btnRow);

            ScrollViewer dlgSv = new ScrollViewer();
            dlgSv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            dlgSv.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            dlgSv.Resources.Add(typeof(ScrollBar), _thinScrollBarStyle);
            dlgSv.Content = sp;

            box.Child = dlgSv;
            dlgRoot.Children.Add(box);

            _activeModalRoot = dlgRoot;
            _activeModalScale = st;
            _activeModalTranslate = tt;
            _modalOriginDx = 0;
            _modalOriginDy = Math.Round(18 * u);
            _modalOverlayActive = true;

            _dialogContentHost.Children.Add(dlgRoot);
            _modalOverlay.IsHitTestVisible = true;
            _modalOverlay.Visibility = Visibility.Visible;

            if (_isPreviewMode)
            {
                _modalBackdrop.Opacity = 1.0;
                return;
            }

            Dispatcher.BeginInvoke(DispatcherPriority.Render, (Action)delegate
            {
                if (token != _modalAnimToken || !_modalOverlayActive) return;
                _modalBackdrop.BeginAnimation(UIElement.OpacityProperty, MakeAnim(0.0, 1.0, 220, _easeOutCubic, 0));
                AnimateBackgroundDepth(true);
                DoubleAnimation openAnim = MakeAnim(0.90, 1.0, 240, _easeOutCubic, 0);
                openAnim.Completed += delegate
                {
                    if (token != _modalAnimToken || !_modalOverlayActive) return;
                    IntPtr hwnd = new WindowInteropHelper(this).Handle;
                    DesktopAttachment.BringToFrontForOverlay(hwnd);
                    Activate();
                    tb.Focus();
                    tb.SelectAll();
                    Keyboard.Focus(tb);
                };
                st.BeginAnimation(ScaleTransform.ScaleXProperty, openAnim);
                st.BeginAnimation(ScaleTransform.ScaleYProperty, MakeAnim(0.90, 1.0, 240, _easeOutCubic, 0));
                tt.BeginAnimation(TranslateTransform.YProperty, MakeAnim(Math.Round(18 * u), 0.0, 240, _easeOutCubic, 0));
                dlgRoot.BeginAnimation(UIElement.OpacityProperty, MakeAnim(0.0, 1.0, 180, _easeOutCubic, 0));
            });
        }

        public void OpenMailConfigDialog()
        {
            CloseSearchOverlay(true);
            HideAllCachedFolderModals();
            int token = ++_modalAnimToken;
            _openFolder = null;
            _modalIsCachedFolder = false;
            _dialogContentHost.Children.Clear();

            double W = _canvasW > 200 ? _canvasW : 1080;
            double H = _canvasH > 200 ? _canvasH : 1872;
            double u = ScaleUnit;
            double dlgW = IsLandscape ? Math.Round(Math.Min(W * 0.54, 860 * u)) : Math.Round(W * 0.86);
            double radius = Math.Round(40 * u);

            Grid dlgRoot = new Grid();
            dlgRoot.Width = dlgW;
            dlgRoot.MaxHeight = Math.Round(H * 0.88);
            dlgRoot.HorizontalAlignment = HorizontalAlignment.Center;
            dlgRoot.VerticalAlignment = VerticalAlignment.Center;
            dlgRoot.RenderTransformOrigin = new Point(0.5, 0.5);
            ScaleTransform st = new ScaleTransform(_isPreviewMode ? 1.0 : 0.90, _isPreviewMode ? 1.0 : 0.90);
            TranslateTransform tt = new TranslateTransform(0, _isPreviewMode ? 0 : Math.Round(18 * u));
            TransformGroup tg = new TransformGroup();
            tg.Children.Add(st);
            tg.Children.Add(tt);
            dlgRoot.RenderTransform = tg;
            dlgRoot.Opacity = _isPreviewMode ? 1.0 : 0.0;

            AddSoftShadowHalo(dlgRoot, radius, u);

            Border box = new Border();
            box.CornerRadius = new CornerRadius(radius);
            box.Background = CreateFrozenBrush(246, 16, 23, 38);
            box.BorderBrush = CreateFrozenBrush(88, 255, 255, 255);
            box.BorderThickness = new Thickness(Math.Max(1.0, Math.Round(1.2 * u * 10) / 10.0));
            box.Padding = new Thickness(Math.Round(38 * u), Math.Round(34 * u), Math.Round(38 * u), Math.Round(32 * u));

            StackPanel sp = new StackPanel();

            TextBlock titleLbl = new TextBlock
            {
                Text = "配置邮箱收件箱",
                FontSize = Math.Round(28 * u),
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White
            };
            sp.Children.Add(titleLbl);

            // 2-Mode Segmented Tab Header: [Gmail OAuth 2.0 (推荐)] | [IMAP (QQ / 163 / 通用)]
            bool startOAuth = !string.Equals(_config.MailProvider, "imap", StringComparison.OrdinalIgnoreCase) ||
                              string.IsNullOrEmpty(_config.MailPasswordDpapi);

            SolidColorBrush tabActiveBg = CreateFrozenBrush(220, 59, 130, 246);
            SolidColorBrush tabActiveBorder = CreateFrozenBrush(160, 147, 197, 253);
            SolidColorBrush tabIdleBg = CreateFrozenBrush(34, 255, 255, 255);
            SolidColorBrush tabIdleBorder = CreateFrozenBrush(62, 255, 255, 255);

            StackPanel tabRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, Math.Round(16 * u), 0, Math.Round(18 * u))
            };
            Button tabOAuthBtn = CreatePillButton("Gmail 浏览器授权 (OAuth 推荐)", u);
            tabOAuthBtn.Margin = new Thickness(0, 0, Math.Round(12 * u), 0);
            Button tabImapBtn = CreatePillButton("IMAP (QQ / 163 / 通用)", u);
            tabRow.Children.Add(tabOAuthBtn);
            tabRow.Children.Add(tabImapBtn);
            sp.Children.Add(tabRow);

            // --- PANEL A: GOOGLE OAUTH 2.0 (NO APP PASSWORD NEEDED!) ---
            StackPanel oauthPanel = new StackPanel();
            oauthPanel.Children.Add(new TextBlock
            {
                Text = "在 Google Cloud 控制台下载桌面端 OAuth 凭据 JSON 后点击下方导入，再点「浏览器授权登录」在 Chrome 中确认一次即可长期同步（仅只读权限，点击邮件可精准直达）。",
                FontSize = Math.Round(16.5 * u),
                Foreground = CreateFrozenBrush(185, 203, 213, 225),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, Math.Round(16 * u))
            });

            TextBox clientIdBox = CreateStyledFieldBox(_config.GmailClientId ?? "", u);
            PasswordBox clientSecretBox = new PasswordBox
            {
                FontSize = Math.Round(21 * u),
                Foreground = Brushes.White,
                CaretBrush = Brushes.White,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            string existingSecret = ImapMailService.UnprotectSecret(_config.GmailClientSecretDpapi);
            if (!string.IsNullOrEmpty(existingSecret)) clientSecretBox.Password = existingSecret;

            TextBlock oauthStatusLbl = new TextBlock
            {
                Text = !string.IsNullOrEmpty(_config.GmailRefreshTokenDpapi)
                    ? ("✓ 已完成授权：" + (_config.MailEmail ?? "Gmail"))
                    : "尚未授权 — 导入 JSON 后点击下方蓝色按钮打开 Chrome 授权",
                FontSize = Math.Round(16 * u),
                Foreground = !string.IsNullOrEmpty(_config.GmailRefreshTokenDpapi)
                    ? CreateFrozenBrush(255, 52, 211, 153)
                    : CreateFrozenBrush(180, 147, 197, 253),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(Math.Round(4 * u), Math.Round(4 * u), 0, Math.Round(10 * u))
            };

            WrapPanel oauthTools = new WrapPanel { Margin = new Thickness(0, 0, 0, Math.Round(14 * u)) };
            Button importJsonBtn = CreatePillButton("导入 Google 凭据 JSON ...", u);
            importJsonBtn.Margin = new Thickness(0, 0, Math.Round(10 * u), Math.Round(8 * u));
            importJsonBtn.Background = CreateFrozenBrush(72, 16, 185, 129);
            importJsonBtn.BorderBrush = CreateFrozenBrush(140, 110, 231, 183);
            importJsonBtn.Click += delegate
            {
                OpenFileDialog ofd = new OpenFileDialog();
                ofd.Title = "选择从 Google Cloud 下载的 client_secret JSON 文件";
                ofd.Filter = "Google 凭据 JSON (*.json)|*.json|所有文件 (*.*)|*.*";
                string dlDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                if (Directory.Exists(dlDir)) ofd.InitialDirectory = dlDir;
                if (ofd.ShowDialog() == true)
                {
                    string cid, csec;
                    if (ImapMailService.ParseGoogleClientSecretJson(ofd.FileName, out cid, out csec))
                    {
                        clientIdBox.Text = cid;
                        clientSecretBox.Password = csec ?? "";
                        oauthStatusLbl.Text = "✓ 已成功读取凭据 JSON！现在点击右下角「浏览器授权登录 Gmail」即可完成。";
                        oauthStatusLbl.Foreground = CreateFrozenBrush(255, 52, 211, 153);
                    }
                    else
                    {
                        oauthStatusLbl.Text = "未能从所选 JSON 中识别到 client_id，请确认是 Google Cloud 下载的 OAuth 桌面端凭据文件。";
                        oauthStatusLbl.Foreground = CreateFrozenBrush(255, 248, 113, 113);
                    }
                }
            };
            Button openCloudBtn = CreatePillButton("打开 Google Cloud 凭据页 ↗", u);
            openCloudBtn.Margin = new Thickness(0, 0, 0, Math.Round(8 * u));
            openCloudBtn.Click += delegate
            {
                try { Process.Start("https://console.cloud.google.com/apis/credentials"); } catch { }
            };
            oauthTools.Children.Add(importJsonBtn);
            oauthTools.Children.Add(openCloudBtn);
            oauthPanel.Children.Add(oauthTools);

            oauthPanel.Children.Add(WrapLabeledField("Client ID（导入 JSON 自动填充）", clientIdBox, u));
            oauthPanel.Children.Add(WrapLabeledField("Client Secret（导入 JSON 自动填充，DPAPI 本地加密）", clientSecretBox, u));
            oauthPanel.Children.Add(oauthStatusLbl);

            StackPanel oauthBtnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, Math.Round(12 * u), 0, 0)
            };
            Button oauthClearBtn = CreatePillButton("清除授权", u);
            oauthClearBtn.Margin = new Thickness(0, 0, Math.Round(12 * u), 0);
            oauthClearBtn.Click += delegate
            {
                _config.GmailClientId = "";
                _config.GmailClientSecretDpapi = "";
                _config.GmailRefreshTokenDpapi = "";
                ConfigManager.Save(_config);
                if (_mailService != null) _mailService.TriggerRefresh();
                CloseFolderModal(false);
            };
            Button oauthCancelBtn = CreatePillButton("取消", u);
            oauthCancelBtn.Margin = new Thickness(0, 0, Math.Round(12 * u), 0);
            oauthCancelBtn.Click += delegate
            {
                if (_mailService != null) _mailService.CancelPendingOAuth();
                CloseFolderModal(false);
            };
            Button oauthLoginBtn = CreatePillButton("浏览器授权登录 Gmail", u);
            oauthLoginBtn.Background = tabActiveBg;
            oauthLoginBtn.BorderBrush = tabActiveBorder;
            oauthLoginBtn.Click += delegate
            {
                string cid = clientIdBox.Text.Trim();
                string csec = clientSecretBox.Password.Trim();
                if (string.IsNullOrEmpty(cid))
                {
                    oauthStatusLbl.Text = "请先点击绿色按钮导入下载的 client_secret_*.json 文件，或手动填入 Client ID。";
                    oauthStatusLbl.Foreground = CreateFrozenBrush(255, 251, 191, 36);
                    return;
                }
                oauthLoginBtn.IsEnabled = false;
                oauthStatusLbl.Text = "已为你打开浏览器，请在 Chrome 中选择账号并点击「继续 / 允许」...";
                oauthStatusLbl.Foreground = CreateFrozenBrush(255, 56, 189, 248);

                if (_mailService != null)
                {
                    _mailService.BeginGmailOAuthAsync(cid, csec, delegate(bool ok, string msg)
                    {
                        Dispatcher.BeginInvoke((Action)delegate
                        {
                            oauthLoginBtn.IsEnabled = true;
                            oauthStatusLbl.Text = msg;
                            oauthStatusLbl.Foreground = ok
                                ? CreateFrozenBrush(255, 52, 211, 153)
                                : CreateFrozenBrush(255, 248, 113, 113);
                            if (ok)
                            {
                                CloseFolderModal(false);
                            }
                        });
                    });
                }
            };
            oauthBtnRow.Children.Add(oauthClearBtn);
            oauthBtnRow.Children.Add(oauthCancelBtn);
            oauthBtnRow.Children.Add(oauthLoginBtn);
            oauthPanel.Children.Add(oauthBtnRow);

            // --- PANEL B: STANDARD IMAP SSL (QQ / 163 / GMAIL APP PASSWORD) ---
            StackPanel imapPanel = new StackPanel();
            imapPanel.Children.Add(new TextBlock
            {
                Text = "适用于 QQ 邮箱、163 邮箱或已生成 16 位应用专用密码的 Gmail。密码经 Windows DPAPI 加密存本机，只读同步不标已读。",
                FontSize = Math.Round(16.5 * u),
                Foreground = CreateFrozenBrush(185, 203, 213, 225),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, Math.Round(14 * u))
            });

            TextBox hostBox = CreateStyledFieldBox(_config.MailImapHost ?? "imap.gmail.com", u);
            TextBox portBox = CreateStyledFieldBox((_config.MailImapPort > 0 ? _config.MailImapPort : 993).ToString(), u);
            TextBox emailBox = CreateStyledFieldBox(_config.MailEmail ?? "", u);
            PasswordBox passBox = new PasswordBox
            {
                FontSize = Math.Round(22 * u),
                Foreground = Brushes.White,
                CaretBrush = Brushes.White,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            string existingPwd = ImapMailService.UnprotectSecret(_config.MailPasswordDpapi);
            if (!string.IsNullOrEmpty(existingPwd)) passBox.Password = existingPwd;

            WrapPanel presetRow = new WrapPanel { Margin = new Thickness(0, 0, 0, Math.Round(12 * u)) };
            Button gmailPreset = CreatePillButton("Gmail 预设", u);
            gmailPreset.Margin = new Thickness(0, 0, Math.Round(10 * u), Math.Round(6 * u));
            gmailPreset.Click += delegate { hostBox.Text = "imap.gmail.com"; portBox.Text = "993"; };
            Button qqPreset = CreatePillButton("QQ 邮箱预设", u);
            qqPreset.Margin = new Thickness(0, 0, Math.Round(10 * u), Math.Round(6 * u));
            qqPreset.Click += delegate { hostBox.Text = "imap.qq.com"; portBox.Text = "993"; };
            Button neteasePreset = CreatePillButton("163 邮箱预设", u);
            neteasePreset.Margin = new Thickness(0, 0, 0, Math.Round(6 * u));
            neteasePreset.Click += delegate { hostBox.Text = "imap.163.com"; portBox.Text = "993"; };
            presetRow.Children.Add(gmailPreset);
            presetRow.Children.Add(qqPreset);
            presetRow.Children.Add(neteasePreset);
            imapPanel.Children.Add(presetRow);

            imapPanel.Children.Add(WrapLabeledField("IMAP 服务器地址", hostBox, u));
            imapPanel.Children.Add(WrapLabeledField("SSL 端口 (默认 993)", portBox, u));
            imapPanel.Children.Add(WrapLabeledField("邮箱地址", emailBox, u));
            imapPanel.Children.Add(WrapLabeledField("应用专用密码 / IMAP 授权码", passBox, u));

            StackPanel imapBtnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, Math.Round(16 * u), 0, 0)
            };
            Button imapClearBtn = CreatePillButton("清除配置", u);
            imapClearBtn.Margin = new Thickness(0, 0, Math.Round(12 * u), 0);
            imapClearBtn.Click += delegate
            {
                _config.MailPasswordDpapi = "";
                ConfigManager.Save(_config);
                if (_mailService != null) _mailService.TriggerRefresh();
                CloseFolderModal(false);
            };
            Button imapCancelBtn = CreatePillButton("取消", u);
            imapCancelBtn.Margin = new Thickness(0, 0, Math.Round(12 * u), 0);
            imapCancelBtn.Click += delegate { CloseFolderModal(false); };

            Action saveImapAction = delegate
            {
                _config.MailProvider = "imap";
                _config.MailImapHost = hostBox.Text.Trim();
                int p;
                _config.MailImapPort = (int.TryParse(portBox.Text.Trim(), out p) && p > 0) ? p : 993;
                _config.MailEmail = emailBox.Text.Trim();
                _config.MailPasswordDpapi = ImapMailService.ProtectSecret(passBox.Password.Trim().Replace(" ", ""));
                ConfigManager.Save(_config);
                if (_mailService != null) _mailService.TriggerRefresh();
                CloseFolderModal(false);
            };
            Button saveBtn = CreatePillButton("保存并同步", u);
            saveBtn.Background = tabActiveBg;
            saveBtn.BorderBrush = tabActiveBorder;
            saveBtn.Click += delegate { saveImapAction(); };
            passBox.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Enter) { saveImapAction(); e.Handled = true; }
            };
            imapBtnRow.Children.Add(imapClearBtn);
            imapBtnRow.Children.Add(imapCancelBtn);
            imapBtnRow.Children.Add(saveBtn);
            imapPanel.Children.Add(imapBtnRow);

            Action<bool> switchTab = delegate(bool toOAuth)
            {
                oauthPanel.Visibility = toOAuth ? Visibility.Visible : Visibility.Collapsed;
                imapPanel.Visibility = toOAuth ? Visibility.Collapsed : Visibility.Visible;
                tabOAuthBtn.Background = toOAuth ? tabActiveBg : tabIdleBg;
                tabOAuthBtn.BorderBrush = toOAuth ? tabActiveBorder : tabIdleBorder;
                tabImapBtn.Background = toOAuth ? tabIdleBg : tabActiveBg;
                tabImapBtn.BorderBrush = toOAuth ? tabIdleBorder : tabActiveBorder;
            };
            tabOAuthBtn.Click += delegate { switchTab(true); };
            tabImapBtn.Click += delegate { switchTab(false); };
            switchTab(startOAuth);

            sp.Children.Add(oauthPanel);
            sp.Children.Add(imapPanel);

            ScrollViewer mailSv = new ScrollViewer();
            mailSv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            mailSv.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            mailSv.Resources.Add(typeof(ScrollBar), _thinScrollBarStyle);
            mailSv.Content = sp;

            box.Child = mailSv;
            dlgRoot.Children.Add(box);

            _activeModalRoot = dlgRoot;
            _activeModalScale = st;
            _activeModalTranslate = tt;
            _modalOriginDx = 0;
            _modalOriginDy = Math.Round(18 * u);
            _modalOverlayActive = true;

            _dialogContentHost.Children.Add(dlgRoot);
            _modalOverlay.IsHitTestVisible = true;
            _modalOverlay.Visibility = Visibility.Visible;

            if (_isPreviewMode)
            {
                _modalBackdrop.Opacity = 1.0;
                return;
            }

            Dispatcher.BeginInvoke(DispatcherPriority.Render, (Action)delegate
            {
                if (token != _modalAnimToken || !_modalOverlayActive) return;
                _modalBackdrop.BeginAnimation(UIElement.OpacityProperty, MakeAnim(0.0, 1.0, 220, _easeOutCubic, 0));
                AnimateBackgroundDepth(true);
                DoubleAnimation openAnim = MakeAnim(0.90, 1.0, 240, _easeOutCubic, 0);
                openAnim.Completed += delegate
                {
                    if (token != _modalAnimToken || !_modalOverlayActive) return;
                    IntPtr hwnd = new WindowInteropHelper(this).Handle;
                    DesktopAttachment.BringToFrontForOverlay(hwnd);
                    Activate();
                };
                st.BeginAnimation(ScaleTransform.ScaleXProperty, openAnim);
                st.BeginAnimation(ScaleTransform.ScaleYProperty, MakeAnim(0.90, 1.0, 240, _easeOutCubic, 0));
                tt.BeginAnimation(TranslateTransform.YProperty, MakeAnim(Math.Round(18 * u), 0.0, 240, _easeOutCubic, 0));
                dlgRoot.BeginAnimation(UIElement.OpacityProperty, MakeAnim(0.0, 1.0, 180, _easeOutCubic, 0));
            });
        }

        private TextBox CreateStyledFieldBox(string initial, double u)
        {
            return new TextBox
            {
                Text = initial ?? "",
                FontSize = Math.Round(22 * u),
                Foreground = Brushes.White,
                CaretBrush = Brushes.White,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                VerticalContentAlignment = VerticalAlignment.Center
            };
        }

        private StackPanel WrapLabeledField(string label, UIElement input, double u)
        {
            StackPanel sp = new StackPanel { Margin = new Thickness(0, 0, 0, Math.Round(12 * u)) };
            sp.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = Math.Round(16 * u),
                Foreground = CreateFrozenBrush(195, 203, 213, 225),
                Margin = new Thickness(Math.Round(4 * u), 0, 0, Math.Round(6 * u))
            });
            Border wrap = new Border
            {
                CornerRadius = new CornerRadius(Math.Round(14 * u)),
                Background = CreateFrozenBrush(30, 255, 255, 255),
                BorderBrush = CreateFrozenBrush(68, 255, 255, 255),
                BorderThickness = new Thickness(Math.Max(1.0, u)),
                Padding = new Thickness(Math.Round(16 * u), Math.Round(10 * u), Math.Round(16 * u), Math.Round(10 * u)),
                Child = input
            };
            sp.Children.Add(wrap);
            return sp;
        }

        private UIElement BuildBigFolderCard(FolderGroup group, double cardX, double cardY, double cardSize, double W, double H, double u)
        {
            double radius = Math.Round(cardSize * 0.155);
            double baseOpacity = Math.Max(0.25, Math.Min(0.92, _config.CardTintOpacity));
            byte topTintAlpha = (byte)Math.Round(Math.Min(0.90, baseOpacity + 0.03) * 255);
            byte botTintAlpha = (byte)Math.Round(Math.Min(0.94, baseOpacity + 0.12) * 255);

            // Studio-lit directional crystal glass rim (bright luminous top hairline fading cleanly along sides)
            LinearGradientBrush rimBrush = new LinearGradientBrush();
            rimBrush.StartPoint = new Point(0, 0);
            rimBrush.EndPoint = new Point(0, 1);
            rimBrush.GradientStops.Add(new GradientStop(Color.FromArgb(105, 255, 255, 255), 0.0));
            rimBrush.GradientStops.Add(new GradientStop(Color.FromArgb(34, 255, 255, 255), 0.45));
            rimBrush.GradientStops.Add(new GradientStop(Color.FromArgb(20, 255, 255, 255), 1.0));
            rimBrush.Freeze();

            LinearGradientBrush rimHoverBrush = new LinearGradientBrush();
            rimHoverBrush.StartPoint = new Point(0, 0);
            rimHoverBrush.EndPoint = new Point(0, 1);
            rimHoverBrush.GradientStops.Add(new GradientStop(Color.FromArgb(152, 255, 255, 255), 0.0));
            rimHoverBrush.GradientStops.Add(new GradientStop(Color.FromArgb(58, 255, 255, 255), 0.5));
            rimHoverBrush.GradientStops.Add(new GradientStop(Color.FromArgb(34, 255, 255, 255), 1.0));
            rimHoverBrush.Freeze();

            double borderW = Math.Max(1.0, Math.Round(1.25 * u * 10) / 10.0);

            Grid cardRoot = new Grid();
            cardRoot.Width = cardSize;
            cardRoot.Height = cardSize;
            cardRoot.AllowDrop = true;

            Border shadowPlate = new Border();
            shadowPlate.CornerRadius = new CornerRadius(radius);
            shadowPlate.Background = CreateFrozenBrush(150, 6, 10, 18);
            shadowPlate.IsHitTestVisible = false;
            shadowPlate.CacheMode = new BitmapCache();
            shadowPlate.Effect = GetFrozenShadowEffect(Colors.Black, Math.Round(30 * u), Math.Round(8 * u), 270, 0.38);
            cardRoot.Children.Add(shadowPlate);

            // Layer 1: Native anti-aliased Border with pixel-aligned blurred wallpaper brush
            Border blurLayer = new Border();
            blurLayer.CornerRadius = new CornerRadius(radius);
            blurLayer.IsHitTestVisible = false;
            if (_wallpaper != null && _wallpaper.BlurredBackdrop != null)
            {
                ImageBrush ib = new ImageBrush(_wallpaper.BlurredBackdrop);
                ib.ViewboxUnits = BrushMappingMode.RelativeToBoundingBox;
                ib.Viewbox = new Rect(
                    Math.Max(0, Math.Min(0.99, cardX / W)),
                    Math.Max(0, Math.Min(0.99, cardY / H)),
                    Math.Max(0.01, Math.Min(1.0 - cardX / W, cardSize / W)),
                    Math.Max(0.01, Math.Min(1.0 - cardY / H, cardSize / H)));
                ib.Stretch = Stretch.Fill;
                ib.Freeze();
                blurLayer.Background = ib;
            }
            cardRoot.Children.Add(blurLayer);

            // Layer 2: Crystal slate glass gradient + directional specular rim
            LinearGradientBrush tintGrad = new LinearGradientBrush();
            tintGrad.StartPoint = new Point(0, 0);
            tintGrad.EndPoint = new Point(0, 1);
            tintGrad.GradientStops.Add(new GradientStop(Color.FromArgb(topTintAlpha, 15, 22, 38), 0.0));
            tintGrad.GradientStops.Add(new GradientStop(Color.FromArgb(botTintAlpha, 9, 14, 26), 1.0));
            tintGrad.Freeze();

            Border outer = new Border();
            outer.CornerRadius = new CornerRadius(radius);
            outer.Background = tintGrad;
            outer.BorderThickness = new Thickness(borderW);
            outer.BorderBrush = rimBrush;

            // Layer 3: Subtle diagonal specular sheen
            LinearGradientBrush sheen = new LinearGradientBrush();
            sheen.StartPoint = new Point(0.12, 0);
            sheen.EndPoint = new Point(0.88, 0.85);
            sheen.GradientStops.Add(new GradientStop(Color.FromArgb(26, 255, 255, 255), 0.0));
            sheen.GradientStops.Add(new GradientStop(Color.FromArgb(6, 255, 255, 255), 0.35));
            sheen.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 1.0));
            sheen.Freeze();

            Border sheenLayer = new Border();
            sheenLayer.CornerRadius = new CornerRadius(Math.Max(1, radius - borderW));
            sheenLayer.Background = sheen;

            // Interior Icon Grid: Clean 2x2 or 3x3 without ugly dark dummy placeholder squares!
            bool useNineGrid = ShouldUseNineGrid(group);
            int gridN = useNineGrid ? 3 : 2;
            int totalSlots = gridN * gridN;
            bool needMiniGrid = group.Items.Count > totalSlots;
            int directSlots = needMiniGrid ? (totalSlots - 1) : Math.Min(totalSlots, group.Items.Count);

            double innerSize = cardSize - borderW * 2;
            double pad = Math.Round(innerSize * (useNineGrid ? 0.068 : 0.074));
            double gap = Math.Round(innerSize * (useNineGrid ? 0.044 : 0.056));
            double tile = Math.Floor((innerSize - pad * 2 - gap * (gridN - 1)) / gridN);

            Canvas inner = new Canvas();
            inner.Width = innerSize;
            inner.Height = innerSize;
            inner.Background = Brushes.Transparent;

            for (int idx = 0; idx < directSlots; idx++)
            {
                int rr = idx / gridN;
                int cc = idx % gridN;
                double sx = pad + cc * (tile + gap);
                double sy = pad + rr * (tile + gap);

                AppItem app = group.Items[idx];
                UIElement tileEl = BuildAppIconTile(app, group, group.Items, tile, false, false, u);
                Canvas.SetLeft(tileEl, sx);
                Canvas.SetTop(tileEl, sy);
                inner.Children.Add(tileEl);
            }

            // Bottom-Right Last Slot: render 2x2 Mini Sub-Grid ONLY when overflowing (> totalSlots).
            // Folders with 1..totalSlots items show ONLY real icons cleanly without a misleading "..." tile!
            double lastX = pad + (gridN - 1) * (tile + gap);
            double lastY = pad + (gridN - 1) * (tile + gap);
            if (needMiniGrid)
            {
                UIElement miniGrid = BuildMiniSubGrid(group, tile, directSlots, u);
                Canvas.SetLeft(miniGrid, lastX);
                Canvas.SetTop(miniGrid, lastY);
                inner.Children.Add(miniGrid);
            }
            else if (group.Items.Count == 0)
            {
                double hintSz = Math.Round(Math.Min(innerSize * 0.34, 52 * u));
                Border emptyBadge = new Border
                {
                    Width = hintSz,
                    Height = hintSz,
                    CornerRadius = new CornerRadius(hintSz * 0.5),
                    Background = CreateFrozenBrush(20, 255, 255, 255),
                    BorderBrush = CreateFrozenBrush(42, 255, 255, 255),
                    BorderThickness = new Thickness(Math.Max(1.0, u)),
                    IsHitTestVisible = false,
                    Child = new TextBlock
                    {
                        Text = "+",
                        FontFamily = FontDisplay,
                        FontSize = Math.Round(hintSz * 0.56),
                        FontWeight = FontWeights.Light,
                        Foreground = CreateFrozenBrush(165, 226, 232, 240),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, -Math.Round(2 * u), 0, 0)
                    }
                };
                Canvas.SetLeft(emptyBadge, Math.Round((innerSize - hintSz) * 0.5));
                Canvas.SetTop(emptyBadge, Math.Round((innerSize - hintSz) * 0.5));
                inner.Children.Add(emptyBadge);
            }

            sheenLayer.Child = inner;
            outer.Child = sheenLayer;
            cardRoot.Children.Add(outer);

            _desktopCardTargets.Add(new DragCardTarget
            {
                Group = group,
                CardRoot = cardRoot,
                OuterBorder = outer,
                NormalRim = rimBrush,
                NormalBorderW = borderW,
                Bounds = new Rect(cardX, cardY, cardSize, cardSize)
            });

            // Subtle hover rim illumination & click empty background to expand folder
            cardRoot.MouseEnter += delegate
            {
                if (!_isPointerDragging) outer.BorderBrush = rimHoverBrush;
            };
            cardRoot.MouseLeave += delegate
            {
                if (!_isPointerDragging) outer.BorderBrush = rimBrush;
            };
            cardRoot.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                if (!_isPointerDragging &&
                    (e.OriginalSource == cardRoot || e.OriginalSource == outer || e.OriginalSource == sheenLayer || e.OriginalSource == inner))
                {
                    OpenFolderModal(group);
                    e.Handled = true;
                }
            };

            // External Drag & Drop support directly onto any card (from Windows Explorer)
            cardRoot.DragEnter += delegate(object s, DragEventArgs e)
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    e.Effects = DragDropEffects.Copy;
                    outer.BorderBrush = _brushDropRim;
                    outer.BorderThickness = new Thickness(Math.Round(2.5 * u));
                }
                else
                {
                    e.Effects = DragDropEffects.None;
                }
                e.Handled = true;
            };
            cardRoot.DragLeave += delegate
            {
                outer.BorderBrush = rimBrush;
                outer.BorderThickness = new Thickness(borderW);
            };
            cardRoot.Drop += delegate(object s, DragEventArgs e)
            {
                outer.BorderBrush = rimBrush;
                outer.BorderThickness = new Thickness(borderW);
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    string[] dropped = e.Data.GetData(DataFormats.FileDrop) as string[];
                    FolderScanner.IngestDroppedFiles(dropped, group.FolderPath);
                    ReloadWorkspace();
                }
                e.Handled = true;
            };

            cardRoot.MouseRightButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                if (e.OriginalSource == cardRoot || e.OriginalSource == outer || e.OriginalSource == sheenLayer || e.OriginalSource == inner)
                {
                    cardRoot.ContextMenu = BuildCardContextMenu(group);
                }
                else if (cardRoot.ContextMenu == null)
                {
                    cardRoot.ContextMenu = BuildCardContextMenu(group);
                }
            };
            return cardRoot;
        }

        private bool ShouldUseNineGrid(FolderGroup group)
        {
            if (group == null) return false;
            string mode = GetFolderGridMode(group.FolderName);
            if (mode == "3x3") return true;
            if (mode == "2x2") return false;
            return group.Items.Count > _config.AutoNineGridThreshold;
        }

        private string GetFolderGridMode(string folderName)
        {
            string mode;
            if (_config.FolderGridModes != null &&
                _config.FolderGridModes.TryGetValue(folderName, out mode) &&
                !string.IsNullOrEmpty(mode))
            {
                return mode.ToLowerInvariant();
            }
            return "auto";
        }

        private void SetFolderGridMode(FolderGroup group, string mode)
        {
            if (group == null) return;
            if (mode == "auto")
            {
                _config.FolderGridModes.Remove(group.FolderName);
            }
            else
            {
                _config.FolderGridModes[group.FolderName] = mode;
            }
            ConfigManager.Save(_config);
            RenderDesktop();
        }

        private UIElement BuildMiniSubGrid(FolderGroup group, double size, int startIndex, double u)
        {
            double miniGap = Math.Round(size * 0.095);
            double miniTile = Math.Floor((size - miniGap) * 0.5);

            Grid container = new Grid();
            container.Width = size;
            container.Height = size;
            container.Background = Brushes.Transparent;
            container.Cursor = Cursors.Hand;
            container.RenderTransformOrigin = new Point(0.5, 0.5);
            ScaleTransform st = new ScaleTransform(1.0, 1.0);
            container.RenderTransform = st;

            Canvas c = new Canvas();
            c.Width = size;
            c.Height = size;

            Point[] pts = new Point[]
            {
                new Point(0, 0),
                new Point(miniTile + miniGap, 0),
                new Point(0, miniTile + miniGap),
                new Point(miniTile + miniGap, miniTile + miniGap)
            };

            // Render ONLY genuine existing app icons (clean HarmonyOS/iOS look — zero ugly opaque gray filler boxes!)
            for (int k = 0; k < 4; k++)
            {
                int itemIdx = startIndex + k;
                if (itemIdx < group.Items.Count)
                {
                    Image img = new Image();
                    img.Source = group.Items[itemIdx].Icon;
                    img.Width = miniTile;
                    img.Height = miniTile;
                    img.Stretch = Stretch.UniformToFill;
                    RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.Fant);
                    Canvas.SetLeft(img, pts[k].X);
                    Canvas.SetTop(img, pts[k].Y);
                    c.Children.Add(img);
                }
            }

            container.Children.Add(c);
            container.ToolTip = CreateDarkToolTip(string.Format("展开「{0}」(共 {1} 项)", group.DisplayName, group.Items.Count), u);

            container.MouseEnter += delegate { AnimateScale(st, 1.06, 130); };
            container.MouseLeave += delegate { AnimateScale(st, 1.0, 130); };
            container.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                AnimateScale(st, 0.94, 70);
                e.Handled = true;
            };
            container.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                AnimateScale(st, 1.0, 100);
                OpenFolderModal(group);
                e.Handled = true;
            };

            return container;
        }

        private UIElement BuildAppIconTile(
            AppItem item, FolderGroup group, List<AppItem> ownerList,
            double size, bool closeModalOnLaunch, bool isDockIcon, double u)
        {
            ScaleTransform dummyS;
            TranslateTransform dummyT;
            return BuildAppIconTile(item, group, ownerList, size, closeModalOnLaunch, isDockIcon, u, out dummyS, out dummyT, -1, null);
        }

        private UIElement BuildAppIconTile(
            AppItem item, FolderGroup group, List<AppItem> ownerList,
            double size, bool closeModalOnLaunch, bool isDockIcon, double u,
            out ScaleTransform outScale, out TranslateTransform outTranslate, int dockWaveIndex)
        {
            return BuildAppIconTile(item, group, ownerList, size, closeModalOnLaunch, isDockIcon, u, out outScale, out outTranslate, dockWaveIndex, null);
        }

        private UIElement BuildAppIconTile(
            AppItem item, FolderGroup group, List<AppItem> ownerList,
            double size, bool closeModalOnLaunch, bool isDockIcon, double u,
            out ScaleTransform outScale, out TranslateTransform outTranslate, int dockWaveIndex, ScrollViewer modalScrollHost)
        {
            Grid host = new Grid();
            host.Width = size;
            host.Height = size;
            host.Background = Brushes.Transparent;
            host.Cursor = Cursors.Hand;
            host.RenderTransformOrigin = new Point(0.5, 0.5);

            ScaleTransform st = new ScaleTransform(1.0, 1.0);
            TranslateTransform tt = new TranslateTransform(0, 0);
            TransformGroup tg = new TransformGroup();
            tg.Children.Add(st);
            tg.Children.Add(tt);
            host.RenderTransform = tg;
            outScale = st;
            outTranslate = tt;

            double shSize = Math.Max(4, size - Math.Max(4, Math.Round(size * 0.06)));
            if (closeModalOnLaunch)
            {
                Border fastShadow = new Border();
                fastShadow.Width = shSize;
                fastShadow.Height = shSize;
                fastShadow.Margin = new Thickness(0, Math.Round(4 * u), 0, -Math.Round(4 * u));
                fastShadow.CornerRadius = new CornerRadius(Math.Round(shSize * 0.25));
                fastShadow.Background = _brushModalIconShadow;
                fastShadow.IsHitTestVisible = false;
                host.Children.Add(fastShadow);
            }
            else
            {
                Border iconShadow = new Border();
                iconShadow.Width = shSize;
                iconShadow.Height = shSize;
                iconShadow.CornerRadius = new CornerRadius(Math.Round(shSize * 0.25));
                iconShadow.Background = CreateFrozenBrush(86, 0, 0, 0);
                iconShadow.IsHitTestVisible = false;
                iconShadow.CacheMode = CreateHiDpiBitmapCache();
                iconShadow.Effect = GetFrozenShadowEffect(
                    Colors.Black,
                    Math.Round((isDockIcon ? 20 : 12) * u),
                    Math.Round((isDockIcon ? 6 : 3) * u),
                    270,
                    isDockIcon ? 0.42 : 0.28);
                host.Children.Add(iconShadow);
            }

            Image img = new Image();
            img.Source = item.Icon;
            img.Width = size;
            img.Height = size;
            img.Stretch = Stretch.UniformToFill;
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.Fant);
            RenderOptions.SetEdgeMode(img, EdgeMode.Unspecified);
            host.Children.Add(img);

            // Sleek glowing drop-target indicator for silky floating drag-to-reorder
            Border dropHighlight = new Border();
            dropHighlight.Width = size;
            dropHighlight.Height = size;
            dropHighlight.CornerRadius = new CornerRadius(Math.Round(size * 0.226));
            dropHighlight.BorderBrush = CreateFrozenBrush(238, 125, 211, 252);
            dropHighlight.BorderThickness = new Thickness(Math.Max(2.0, Math.Round(2.6 * u)));
            dropHighlight.Background = CreateFrozenBrush(48, 56, 189, 248);
            dropHighlight.Opacity = 0;
            dropHighlight.IsHitTestVisible = false;
            host.Children.Add(dropHighlight);

            // Register this tile as a live GPU drag-and-drop target
            DragAppTarget targetReg = new DragAppTarget
            {
                Item = item,
                Group = group,
                OwnerList = ownerList,
                Host = host,
                Highlight = dropHighlight,
                Scale = st,
                ScrollHost = modalScrollHost,
                IsModal = closeModalOnLaunch
            };
            if (closeModalOnLaunch && group != null)
            {
                List<DragAppTarget> modalList;
                if (!_modalAppTargets.TryGetValue(group.FolderName, out modalList))
                {
                    modalList = new List<DragAppTarget>();
                    _modalAppTargets[group.FolderName] = modalList;
                }
                modalList.Add(targetReg);
            }
            else if (!closeModalOnLaunch)
            {
                _desktopAppTargets.Add(targetReg);
            }

            if (!closeModalOnLaunch && dockWaveIndex < 0)
            {
                host.ToolTip = CreateDarkToolTip(item.DisplayName, u);
            }

            bool dragArmed = false;
            Point dragOrigin = new Point(0, 0);
            Action<double> syncDockWaveScaleTrack = delegate(double scaleVal)
            {
                if (dockWaveIndex >= 0 && dockWaveIndex < _dockTileRefs.Count)
                {
                    _dockTileRefs[dockWaveIndex].LastTargetScale = scaleVal;
                }
            };

            host.MouseRightButtonDown += delegate
            {
                dragArmed = false;
                host.ContextMenu = BuildAppContextMenu(item, group, ownerList);
            };

            host.MouseEnter += delegate(object s, MouseEventArgs e)
            {
                if (e.LeftButton != MouseButtonState.Pressed) dragArmed = false;
                if (_isPointerDragging) return;
                if (dockWaveIndex < 0)
                {
                    AnimateScale(st, 1.072, 130);
                    if (isDockIcon) AnimateTranslateY(tt, -6 * u, 130);
                }
            };
            host.MouseLeave += delegate
            {
                bool wasArmed = dragArmed;
                dragArmed = false;
                if (_isPointerDragging) return;
                if (dockWaveIndex < 0)
                {
                    AnimateScale(st, 1.0, 135);
                    if (isDockIcon) AnimateTranslateY(tt, 0, 135);
                }
                else if (wasArmed)
                {
                    syncDockWaveScaleTrack(1.0);
                    AnimateScale(st, 1.0, 120);
                }
            };
            host.IsVisibleChanged += delegate
            {
                if (!host.IsVisible) dragArmed = false;
            };
            host.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                dragArmed = true;
                dragOrigin = e.GetPosition(_rootGrid);
                syncDockWaveScaleTrack(0.91);
                AnimateScale(st, 0.91, 65);
                e.Handled = true;
            };
            host.MouseMove += delegate(object s, MouseEventArgs e)
            {
                if (!dragArmed) return;
                if (e.LeftButton != MouseButtonState.Pressed)
                {
                    dragArmed = false;
                    return;
                }
                Point cur = e.GetPosition(_rootGrid);
                double dx = cur.X - dragOrigin.X;
                double dy = cur.Y - dragOrigin.Y;
                if (dx * dx + dy * dy < 49.0) return;

                dragArmed = false;
                if (dockWaveIndex >= 0) ApplyDockWaveHover(-1, u);
                StartAppPointerDrag(item, ownerList, host, st, size, closeModalOnLaunch, cur);
            };
            host.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                bool wasArmed = dragArmed;
                dragArmed = false;
                if (!wasArmed || _isPointerDragging)
                {
                    syncDockWaveScaleTrack(1.0);
                    AnimateScale(st, 1.0, 100);
                    e.Handled = true;
                    return;
                }
                syncDockWaveScaleTrack(1.04);
                AnimateScale(st, 1.04, 110);
                FolderScanner.LaunchItem(item);
                if (closeModalOnLaunch)
                {
                    CloseFolderModal(false);
                }
                e.Handled = true;
            };

            return host;
        }

        // =========================================================================
        // SILKY 160Hz GPU FLOATING GHOST DRAG-AND-DROP ENGINE
        // Supports: Icon Reordering, Cross-Folder Move, Dock Pinning, & Folder Card Reorder!
        // =========================================================================
        private void StartAppPointerDrag(
            AppItem item, List<AppItem> ownerList,
            FrameworkElement sourceHost, ScaleTransform sourceScale, double tileSize, bool fromModal, Point startPos)
        {
            if (_isPointerDragging || item == null) return;
            _isPointerDragging = true;
            _dragApp = item;
            _dragAppOwnerList = ownerList;
            _dragAppFromModal = fromModal;
            _dragSourceHost = sourceHost;
            _dragSourceScale = sourceScale;
            _dragFolder = null;

            if (sourceHost != null) sourceHost.Opacity = 0.26;
            if (sourceScale != null) AnimateScale(sourceScale, 0.94, 110);

            double u = ScaleUnit;
            double ghostSz = Math.Round(Math.Max(56 * u, Math.Min(116 * u, tileSize * 1.12)));
            _dragGhostHalfW = ghostSz * 0.5;
            _dragGhostHalfH = ghostSz * 0.5;

            Grid ghost = new Grid
            {
                Width = ghostSz,
                Height = ghostSz,
                IsHitTestVisible = false
            };
            double gShSize = Math.Max(4, ghostSz - Math.Max(4, Math.Round(ghostSz * 0.06)));
            Border gShadow = new Border
            {
                Width = gShSize,
                Height = gShSize,
                CornerRadius = new CornerRadius(Math.Round(gShSize * 0.25)),
                Background = CreateFrozenBrush(110, 0, 0, 0),
                Effect = GetFrozenShadowEffect(Colors.Black, Math.Round(26 * u), Math.Round(10 * u), 270, 0.55)
            };
            Image gImg = new Image
            {
                Source = item.Icon,
                Width = ghostSz,
                Height = ghostSz,
                Stretch = Stretch.UniformToFill
            };
            RenderOptions.SetBitmapScalingMode(gImg, BitmapScalingMode.Fant);
            ghost.Children.Add(gShadow);
            ghost.Children.Add(gImg);

            MountDragGhost(ghost, startPos, u);
            if (!_rootGrid.CaptureMouse())
            {
                CancelPointerDrag();
            }
        }

        private void StartFolderCardDrag(FolderGroup group, Point startPos)
        {
            if (_isPointerDragging || group == null) return;
            _isPointerDragging = true;
            _dragFolder = group;
            _dragApp = null;
            _dragSourceHost = null;
            _dragSourceScale = null;
            _dragFolderCardRoot = null;
            for (int i = 0; i < _desktopCardTargets.Count; i++)
            {
                if (_desktopCardTargets[i].Group == group && _desktopCardTargets[i].CardRoot != null)
                {
                    _dragFolderCardRoot = _desktopCardTargets[i].CardRoot;
                    _dragFolderCardRoot.Opacity = 0.45;
                    break;
                }
            }

            double u = ScaleUnit;
            Border ghost = new Border
            {
                CornerRadius = new CornerRadius(Math.Round(20 * u)),
                Background = CreateFrozenBrush(228, 18, 28, 48),
                BorderBrush = _brushDropRim,
                BorderThickness = new Thickness(Math.Max(1.4, 1.8 * u)),
                Padding = new Thickness(Math.Round(18 * u), Math.Round(10 * u), Math.Round(18 * u), Math.Round(10 * u)),
                IsHitTestVisible = false,
                Effect = GetFrozenShadowEffect(Colors.Black, Math.Round(24 * u), Math.Round(8 * u), 315, 0.50)
            };
            StackPanel sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (group.Items.Count > 0 && group.Items[0].Icon != null)
            {
                double mini = Math.Round(26 * u);
                sp.Children.Add(new Image
                {
                    Source = group.Items[0].Icon,
                    Width = mini,
                    Height = mini,
                    Margin = new Thickness(0, 0, Math.Round(10 * u), 0)
                });
            }
            sp.Children.Add(new TextBlock
            {
                Text = group.DisplayName,
                FontFamily = FontDisplay,
                FontSize = Math.Round(17 * u),
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center
            });
            ghost.Child = sp;
            ghost.Measure(new Size(400, 100));
            _dragGhostHalfW = ghost.DesiredSize.Width * 0.5;
            _dragGhostHalfH = ghost.DesiredSize.Height * 0.5;

            MountDragGhost(ghost, startPos, u);
            if (!_rootGrid.CaptureMouse())
            {
                CancelPointerDrag();
            }
        }

        private void MountDragGhost(FrameworkElement ghost, Point startPos, double u)
        {
            _dragOverlayCanvas.Children.Clear();
            // Cache ghost + DropShadowEffect once into a single HiDPI GPU quad so 160Hz dragging never re-runs blur shaders!
            ghost.CacheMode = CreateHiDpiBitmapCache();
            ghost.RenderTransformOrigin = new Point(0.5, 0.5);
            ScaleTransform liftScale = new ScaleTransform(_isPreviewMode ? 1.04 : 0.92, _isPreviewMode ? 1.04 : 0.92);
            _dragGhostTranslate = new TranslateTransform(startPos.X - _dragGhostHalfW, startPos.Y - _dragGhostHalfH);
            TransformGroup tg = new TransformGroup();
            tg.Children.Add(liftScale);
            tg.Children.Add(_dragGhostTranslate);
            ghost.RenderTransform = tg;
            _dragOverlayCanvas.Children.Add(ghost);

            if (!_isPreviewMode)
            {
                liftScale.BeginAnimation(ScaleTransform.ScaleXProperty, MakeAnim(0.92, 1.05, 160, _easeOutBackSoft, 0));
                liftScale.BeginAnimation(ScaleTransform.ScaleYProperty, MakeAnim(0.92, 1.05, 160, _easeOutBackSoft, 0));
                ghost.BeginAnimation(UIElement.OpacityProperty, MakeAnim(0.84, 1.0, 95, _easeOutCubic, 0));
            }

            _dragBadgePill = new Border
            {
                CornerRadius = new CornerRadius(Math.Round(11 * u)),
                Background = CreateFrozenBrush(236, 14, 116, 184),
                BorderBrush = CreateFrozenBrush(160, 186, 230, 253),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(Math.Round(11 * u), Math.Round(4.5 * u), Math.Round(11 * u), Math.Round(4.5 * u)),
                Opacity = 0,
                IsHitTestVisible = false
            };
            _dragBadgeText = new TextBlock
            {
                FontFamily = FontText,
                FontSize = Math.Round(13 * u),
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White
            };
            _dragBadgePill.Child = _dragBadgeText;
            _dragBadgeTranslate = new TranslateTransform(startPos.X + Math.Round(18 * u), startPos.Y - _dragGhostHalfH - Math.Round(28 * u));
            _dragBadgePill.RenderTransform = _dragBadgeTranslate;
            _dragOverlayCanvas.Children.Add(_dragBadgePill);
        }

        private void OnRootPointerDragMove(object sender, MouseEventArgs e)
        {
            if (!_isPointerDragging) return;
            Point pt = e.GetPosition(_rootGrid);
            double u = ScaleUnit;

            if (_dragGhostTranslate != null)
            {
                _dragGhostTranslate.X = pt.X - _dragGhostHalfW;
                _dragGhostTranslate.Y = pt.Y - _dragGhostHalfH;
            }

            // Smooth 160Hz edge auto-scroll when dragging inside a scrollable folder modal
            UpdateModalDragAutoScroll(pt, u);

            DragAppTarget nextAppTarget = null;
            DragCardTarget nextCardTarget = null;
            bool nextDockTarget = false;
            string badgeMsg = null;

            if (_dragFolder != null)
            {
                DragCardTarget hitCard = HitTestDesktopCard(pt);
                if (hitCard != null && hitCard.Group != _dragFolder)
                {
                    nextCardTarget = hitCard;
                    badgeMsg = "移动到「" + hitCard.Group.DisplayName + "」位置";
                }
            }
            else if (_dragApp != null)
            {
                // 1. Check sibling app icon hit (in-place reorder)
                DragAppTarget hitApp = HitTestAppTarget(pt);
                if (hitApp != null && hitApp.Item != _dragApp &&
                    !string.IsNullOrEmpty(_dragApp.FolderKey) &&
                    _dragApp.FolderKey.Equals(hitApp.Item.FolderKey, StringComparison.OrdinalIgnoreCase))
                {
                    nextAppTarget = hitApp;
                    badgeMsg = "调整排序";
                }
                else if (!_dragAppFromModal)
                {
                    // 2. Check cross-folder move onto another Big Folder card
                    DragCardTarget hitCard = HitTestDesktopCard(pt);
                    if (hitCard != null &&
                        !string.IsNullOrEmpty(_dragApp.FolderKey) &&
                        !_dragApp.FolderKey.Equals(hitCard.Group.FolderName, StringComparison.OrdinalIgnoreCase) &&
                        !_dragApp.FolderKey.Equals("_Dock", StringComparison.OrdinalIgnoreCase))
                    {
                        nextCardTarget = hitCard;
                        badgeMsg = "移动到「" + hitCard.Group.DisplayName + "」";
                    }
                    // 3. Check Dock pin drop
                    else if (!_landscapeDockRect.IsEmpty && _landscapeDockRect.Contains(pt) &&
                             !string.IsNullOrEmpty(_dragApp.FolderKey) &&
                             !_dragApp.FolderKey.Equals("_Dock", StringComparison.OrdinalIgnoreCase))
                    {
                        nextDockTarget = true;
                        badgeMsg = "固定到 Dock";
                    }
                }
            }

            // Diff hover state transitions so we NEVER restart 4 animations on every 160Hz mouse move!
            if (!ReferenceEquals(nextAppTarget, _hoveredAppTarget))
            {
                if (_hoveredAppTarget != null)
                {
                    if (_isPreviewMode) _hoveredAppTarget.Highlight.Opacity = 0.0;
                    else _hoveredAppTarget.Highlight.BeginAnimation(UIElement.OpacityProperty, MakeAnim(null, 0.0, 100, _easeOutCubic, 0));
                    AnimateScale(_hoveredAppTarget.Scale, 1.0, 110);
                }
                _hoveredAppTarget = nextAppTarget;
                if (_hoveredAppTarget != null)
                {
                    if (_isPreviewMode) _hoveredAppTarget.Highlight.Opacity = 1.0;
                    else _hoveredAppTarget.Highlight.BeginAnimation(UIElement.OpacityProperty, MakeAnim(null, 1.0, 110, _easeOutCubic, 0));
                    AnimateScale(_hoveredAppTarget.Scale, 1.075, 110);
                }
            }

            if (!ReferenceEquals(nextCardTarget, _hoveredCardTarget))
            {
                if (_hoveredCardTarget != null)
                {
                    _hoveredCardTarget.OuterBorder.BorderBrush = _hoveredCardTarget.NormalRim;
                    _hoveredCardTarget.OuterBorder.BorderThickness = new Thickness(_hoveredCardTarget.NormalBorderW);
                }
                _hoveredCardTarget = nextCardTarget;
                if (_hoveredCardTarget != null)
                {
                    _hoveredCardTarget.OuterBorder.BorderBrush = _brushDropRim;
                    _hoveredCardTarget.OuterBorder.BorderThickness = new Thickness(Math.Round(2.6 * u));
                }
            }

            if (nextDockTarget != _hoveredDockTarget)
            {
                _hoveredDockTarget = nextDockTarget;
                if (_landscapeDockGlass != null)
                {
                    if (_hoveredDockTarget)
                    {
                        _landscapeDockGlass.BorderBrush = _brushDropRim;
                        _landscapeDockGlass.BorderThickness = new Thickness(Math.Round(2.4 * u));
                    }
                    else if (_landscapeDockNormalRim != null)
                    {
                        _landscapeDockGlass.BorderBrush = _landscapeDockNormalRim;
                        _landscapeDockGlass.BorderThickness = new Thickness(_landscapeDockNormalBorderW);
                    }
                }
            }

            if (_dragBadgePill != null && _dragBadgeText != null)
            {
                if (!string.IsNullOrEmpty(badgeMsg))
                {
                    if (_dragBadgeText.Text != badgeMsg)
                    {
                        _dragBadgeText.Text = badgeMsg;
                        _dragBadgePill.Measure(new Size(420, 60));
                    }
                    double bw = _dragBadgePill.DesiredSize.Width;
                    double maxBx = Math.Max(12 * u, (_canvasW > 200 ? _canvasW : 1920) - bw - Math.Round(12 * u));
                    if (_dragBadgeTranslate != null)
                    {
                        double bx = pt.X + Math.Round(16 * u);
                        if (bx > maxBx) bx = Math.Max(Math.Round(12 * u), pt.X - bw - Math.Round(16 * u));
                        _dragBadgeTranslate.X = Math.Max(Math.Round(12 * u), Math.Min(maxBx, bx));
                        _dragBadgeTranslate.Y = Math.Max(8, pt.Y - _dragGhostHalfH - Math.Round(30 * u));
                    }
                    _dragBadgePill.Opacity = 1.0;
                }
                else
                {
                    _dragBadgePill.Opacity = 0.0;
                }
            }
        }

        private void UpdateModalDragAutoScroll(Point rootPt, double u)
        {
            _modalDragScrollVelocity = 0;
            if (!_dragAppFromModal || _openFolder == null)
            {
                StopModalDragAutoScroll();
                return;
            }
            CachedFolderModalView view;
            if (!_folderModalCache.TryGetValue(_openFolder.FolderName, out view) || view == null || view.ScrollHost == null)
            {
                StopModalDragAutoScroll();
                return;
            }
            ScrollViewer sv = view.ScrollHost;
            if (sv.ScrollableHeight <= 0.5 || !sv.IsVisible)
            {
                StopModalDragAutoScroll();
                return;
            }

            try
            {
                Point svTopLeft = sv.TransformToAncestor(_rootGrid).Transform(new Point(0, 0));
                double vpTop = svTopLeft.Y;
                double vpBot = svTopLeft.Y + sv.ViewportHeight;
                double zone = Math.Round(54 * u);

                // Velocity in logical pixels per second (frame-rate independent across 60Hz-240Hz!)
                if (rootPt.X >= svTopLeft.X - 24 && rootPt.X <= svTopLeft.X + sv.ViewportWidth + 24)
                {
                    if (rootPt.Y >= vpTop - 18 && rootPt.Y < vpTop + zone && sv.VerticalOffset > 0)
                    {
                        double ratio = Math.Max(0.15, Math.Min(1.0, (vpTop + zone - rootPt.Y) / zone));
                        _modalDragScrollVelocity = -840.0 * u * ratio;
                    }
                    else if (rootPt.Y <= vpBot + 18 && rootPt.Y > vpBot - zone && sv.VerticalOffset < sv.ScrollableHeight)
                    {
                        double ratio = Math.Max(0.15, Math.Min(1.0, (rootPt.Y - (vpBot - zone)) / zone));
                        _modalDragScrollVelocity = 840.0 * u * ratio;
                    }
                }
            }
            catch
            {
            }

            if (Math.Abs(_modalDragScrollVelocity) > 1.0)
            {
                if (!_modalDragAutoScrollActive)
                {
                    _modalDragAutoScrollActive = true;
                    _modalDragAutoScrollLastTs = Stopwatch.GetTimestamp();
                    CompositionTarget.Rendering += OnModalDragAutoScrollFrame;
                }
            }
            else
            {
                StopModalDragAutoScroll();
            }
        }

        private void OnModalDragAutoScrollFrame(object sender, EventArgs e)
        {
            if (!_isPointerDragging || !_dragAppFromModal || _openFolder == null || Math.Abs(_modalDragScrollVelocity) < 1.0)
            {
                StopModalDragAutoScroll();
                return;
            }
            long now = Stopwatch.GetTimestamp();
            double dt = (double)(now - _modalDragAutoScrollLastTs) / Stopwatch.Frequency;
            _modalDragAutoScrollLastTs = now;
            if (dt <= 0.0002) dt = 0.00625;
            if (dt > 0.05) dt = 0.05;

            CachedFolderModalView v;
            if (_folderModalCache.TryGetValue(_openFolder.FolderName, out v) && v != null && v.ScrollHost != null)
            {
                double next = Math.Max(0, Math.Min(v.ScrollHost.ScrollableHeight, v.ScrollHost.VerticalOffset + _modalDragScrollVelocity * dt));
                v.ScrollHost.ScrollToVerticalOffset(next);
            }
        }

        private void StopModalDragAutoScroll()
        {
            if (_modalDragAutoScrollActive)
            {
                _modalDragAutoScrollActive = false;
                CompositionTarget.Rendering -= OnModalDragAutoScrollFrame;
            }
        }

        private void OnRootPointerDragEnd(object sender, MouseButtonEventArgs e)
        {
            if (!_isPointerDragging) return;

            AppItem dragApp = _dragApp;
            FolderGroup dragFolder = _dragFolder;
            List<AppItem> ownerList = _dragAppOwnerList;
            bool fromModal = _dragAppFromModal;
            DragAppTarget appTarget = _hoveredAppTarget;
            DragCardTarget cardTarget = _hoveredCardTarget;
            bool dockTarget = _hoveredDockTarget;

            CancelPointerDrag();
            e.Handled = true;

            if (dragFolder != null && cardTarget != null && cardTarget.Group != dragFolder)
            {
                if (FolderScanner.MoveFolderToIndex(_config, _workspace, dragFolder, cardTarget.Group))
                {
                    ReloadWorkspace();
                }
                return;
            }

            if (dragApp != null)
            {
                if (appTarget != null && appTarget.Item != dragApp &&
                    FolderScanner.MoveItemToIndex(_config, ownerList, dragApp, appTarget.Item))
                {
                    if (fromModal && _openFolder != null)
                    {
                        RefreshOpenModalInPlace();
                    }
                    else
                    {
                        _folderModalsDirty = true;
                        _searchCacheDirty = true;
                        RenderDesktop();
                        SchedulePrewarmOverlays();
                    }
                    return;
                }

                if (cardTarget != null &&
                    FolderScanner.MoveItemToFolder(_config, dragApp, cardTarget.Group))
                {
                    ReloadWorkspace();
                    return;
                }

                if (dockTarget && FolderScanner.CopyItemToDock(_config, dragApp))
                {
                    ReloadWorkspace();
                }
            }
        }

        private void CancelPointerDrag()
        {
            if (!_isPointerDragging) return;
            _isPointerDragging = false;
            _modalDragScrollVelocity = 0;
            StopModalDragAutoScroll();
            ClearDragHighlights();
            if (_dragSourceHost != null) _dragSourceHost.Opacity = 1.0;
            if (_dragSourceScale != null) AnimateScale(_dragSourceScale, 1.0, 140);
            if (_dragFolderCardRoot != null) _dragFolderCardRoot.Opacity = 1.0;
            _dragOverlayCanvas.Children.Clear();
            _dragApp = null;
            _dragAppOwnerList = null;
            _dragSourceHost = null;
            _dragSourceScale = null;
            _dragFolder = null;
            _dragFolderCardRoot = null;
            _dragGhostTranslate = null;
            _dragBadgePill = null;
            _dragBadgeText = null;
            _dragBadgeTranslate = null;
            if (_rootGrid != null && _rootGrid.IsMouseCaptured)
            {
                _rootGrid.ReleaseMouseCapture();
            }
        }

        private void ClearDragHighlights()
        {
            if (_hoveredAppTarget != null)
            {
                if (_isPreviewMode) _hoveredAppTarget.Highlight.Opacity = 0.0;
                else _hoveredAppTarget.Highlight.BeginAnimation(UIElement.OpacityProperty, MakeAnim(null, 0.0, 100, _easeOutCubic, 0));
                AnimateScale(_hoveredAppTarget.Scale, 1.0, 95);
                _hoveredAppTarget = null;
            }
            if (_hoveredCardTarget != null)
            {
                _hoveredCardTarget.OuterBorder.BorderBrush = _hoveredCardTarget.NormalRim;
                _hoveredCardTarget.OuterBorder.BorderThickness = new Thickness(_hoveredCardTarget.NormalBorderW);
                _hoveredCardTarget = null;
            }
            if (_hoveredDockTarget)
            {
                _hoveredDockTarget = false;
                if (_landscapeDockGlass != null && _landscapeDockNormalRim != null)
                {
                    _landscapeDockGlass.BorderBrush = _landscapeDockNormalRim;
                    _landscapeDockGlass.BorderThickness = new Thickness(_landscapeDockNormalBorderW);
                }
            }
        }

        private DragAppTarget HitTestAppTarget(Point rootPt)
        {
            List<DragAppTarget> list = null;
            Rect modalViewportClip = Rect.Empty;
            if (_dragAppFromModal && _openFolder != null)
            {
                _modalAppTargets.TryGetValue(_openFolder.FolderName, out list);
                CachedFolderModalView view;
                if (_folderModalCache.TryGetValue(_openFolder.FolderName, out view) &&
                    view != null && view.ScrollHost != null && view.ScrollHost.IsVisible)
                {
                    try
                    {
                        Point svTopLeft = view.ScrollHost.TransformToAncestor(_rootGrid).Transform(new Point(0, 0));
                        double vw = view.ScrollHost.ViewportWidth > 1 ? view.ScrollHost.ViewportWidth : view.ScrollHost.ActualWidth;
                        double vh = view.ScrollHost.ViewportHeight > 1 ? view.ScrollHost.ViewportHeight : view.ScrollHost.ActualHeight;
                        modalViewportClip = new Rect(svTopLeft.X, svTopLeft.Y, vw, vh);
                        // Reject points outside the visible ScrollViewer viewport (header, footer, outside modal!)
                        if (!modalViewportClip.Contains(rootPt)) return null;
                    }
                    catch
                    {
                    }
                }
            }
            else if (!_dragAppFromModal)
            {
                list = _desktopAppTargets;
            }
            if (list == null) return null;

            for (int i = 0; i < list.Count; i++)
            {
                DragAppTarget t = list[i];
                if (t.Host == null || !t.Host.IsVisible) continue;
                try
                {
                    Point topLeft = t.Host.TransformToAncestor(_rootGrid).Transform(new Point(0, 0));
                    Rect r = new Rect(topLeft.X - 6, topLeft.Y - 6, t.Host.ActualWidth + 12, t.Host.ActualHeight + 12);
                    if (!modalViewportClip.IsEmpty)
                    {
                        r.Intersect(modalViewportClip);
                        if (r.IsEmpty || r.Width <= 0 || r.Height <= 0) continue;
                    }
                    if (r.Contains(rootPt)) return t;
                }
                catch
                {
                }
            }
            return null;
        }

        private DragCardTarget HitTestDesktopCard(Point rootPt)
        {
            for (int i = 0; i < _desktopCardTargets.Count; i++)
            {
                if (_desktopCardTargets[i].Bounds.Contains(rootPt))
                {
                    return _desktopCardTargets[i];
                }
            }
            return null;
        }

        private void DetachPendingModalScrollRestore()
        {
            if (_detachPendingModalScrollRestore != null)
            {
                Action detach = _detachPendingModalScrollRestore;
                _detachPendingModalScrollRestore = null;
                detach();
            }
        }

        private void RefreshOpenModalInPlace()
        {
            DetachPendingModalScrollRestore();
            if (_openFolder == null || !_modalOverlayActive) return;
            FolderGroup activeGroup = _openFolder;
            _folderModalsDirty = true;
            _searchCacheDirty = true;
            RenderDesktop();

            // Rebuild ONLY the open modal seamlessly with ZERO entrance zoom animation flash!
            double prevScrollOffset = 0;
            CachedFolderModalView oldView;
            if (_folderModalCache.TryGetValue(activeGroup.FolderName, out oldView) && oldView != null)
            {
                if (oldView.ScrollHost != null) prevScrollOffset = oldView.ScrollHost.VerticalOffset;
                _modalContentHost.Children.Remove(oldView.Root);
            }
            _modalAppTargets.Remove(activeGroup.FolderName);
            CachedFolderModalView fresh = BuildCachedFolderModalView(activeGroup);
            fresh.Scale.ScaleX = 1.0;
            fresh.Scale.ScaleY = 1.0;
            fresh.Translate.X = 0;
            fresh.Translate.Y = 0;
            fresh.Root.Opacity = 1.0;
            fresh.Root.Visibility = Visibility.Visible;
            fresh.Root.IsHitTestVisible = true;
            fresh.Root.CacheMode = null;

            _folderModalCache[activeGroup.FolderName] = fresh;
            _modalContentHost.Children.Add(fresh.Root);
            _activeModalView = fresh;
            _activeModalRoot = fresh.Root;
            _activeModalScale = fresh.Scale;
            _activeModalTranslate = fresh.Translate;
            if (prevScrollOffset > 0.5 && fresh.ScrollHost != null)
            {
                // Synchronously lay out before the next render pass so ScrollToVerticalOffset lands on Frame 0 (ZERO 1-frame scroll-jump!)
                _modalContentHost.UpdateLayout();
                if (fresh.ScrollHost.ScrollableHeight > 0)
                {
                    fresh.ScrollHost.ScrollToVerticalOffset(Math.Min(prevScrollOffset, fresh.ScrollHost.ScrollableHeight));
                    fresh.ScrollHost.UpdateLayout();
                }
                else if (fresh.ScrollHost.ActualHeight <= 0)
                {
                    // Fallback ONLY when parent container has not yet been measured/arranged; unconditionally detaches on first arranged pass or Unloaded!
                    double restoreY = prevScrollOffset;
                    ScrollViewer freshSv = fresh.ScrollHost;
                    EventHandler onLayoutUpdated = null;
                    RoutedEventHandler onUnloaded = null;
                    Action detach = delegate
                    {
                        if (onLayoutUpdated != null)
                        {
                            freshSv.LayoutUpdated -= onLayoutUpdated;
                            onLayoutUpdated = null;
                        }
                        if (onUnloaded != null)
                        {
                            freshSv.Unloaded -= onUnloaded;
                            onUnloaded = null;
                        }
                        if (_detachPendingModalScrollRestore != null)
                        {
                            _detachPendingModalScrollRestore = null;
                        }
                    };
                    onUnloaded = delegate { detach(); };
                    onLayoutUpdated = delegate
                    {
                        if (!freshSv.IsVisible || freshSv.ActualHeight > 0 || freshSv.ScrollableHeight > 0)
                        {
                            detach();
                            if (freshSv.ScrollableHeight > 0)
                            {
                                freshSv.ScrollToVerticalOffset(Math.Min(restoreY, freshSv.ScrollableHeight));
                            }
                        }
                    };
                    _detachPendingModalScrollRestore = detach;
                    freshSv.Unloaded += onUnloaded;
                    freshSv.LayoutUpdated += onLayoutUpdated;
                }
            }
        }

        private UIElement BuildSearchPill(double x, double y, double w, double h, double W, double H, double u)
        {
            double r = h * 0.5;
            double borderW = Math.Max(1.0, Math.Round(1.2 * u * 10) / 10.0);

            LinearGradientBrush pillRim = new LinearGradientBrush();
            pillRim.StartPoint = new Point(0, 0);
            pillRim.EndPoint = new Point(0, 1);
            pillRim.GradientStops.Add(new GradientStop(Color.FromArgb(105, 255, 255, 255), 0.0));
            pillRim.GradientStops.Add(new GradientStop(Color.FromArgb(32, 255, 255, 255), 1.0));
            pillRim.Freeze();

            SolidColorBrush pillRimHover = CreateFrozenBrush(156, 255, 255, 255);

            Grid pillRoot = new Grid();
            pillRoot.Width = w;
            pillRoot.Height = h;
            pillRoot.Cursor = Cursors.Hand;
            pillRoot.RenderTransformOrigin = new Point(0.5, 0.5);
            ScaleTransform st = new ScaleTransform(1.0, 1.0);
            pillRoot.RenderTransform = st;

            Border pillShadow = new Border();
            pillShadow.CornerRadius = new CornerRadius(r);
            pillShadow.Background = CreateFrozenBrush(140, 5, 8, 15);
            pillShadow.IsHitTestVisible = false;
            pillShadow.CacheMode = new BitmapCache();
            pillShadow.Effect = GetFrozenShadowEffect(Colors.Black, Math.Round(22 * u), Math.Round(5 * u), 315, 0.38);
            pillRoot.Children.Add(pillShadow);

            Border pillBlur = new Border();
            pillBlur.CornerRadius = new CornerRadius(r);
            pillBlur.IsHitTestVisible = false;
            if (_wallpaper != null && _wallpaper.BlurredBackdrop != null)
            {
                ImageBrush ib = new ImageBrush(_wallpaper.BlurredBackdrop);
                ib.ViewboxUnits = BrushMappingMode.RelativeToBoundingBox;
                ib.Viewbox = new Rect(
                    Math.Max(0, Math.Min(0.99, x / W)),
                    Math.Max(0, Math.Min(0.99, y / H)),
                    Math.Max(0.01, Math.Min(1.0 - x / W, w / W)),
                    Math.Max(0.01, Math.Min(1.0 - y / H, h / H)));
                ib.Stretch = Stretch.Fill;
                ib.Freeze();
                pillBlur.Background = ib;
            }
            pillRoot.Children.Add(pillBlur);

            double baseOp = Math.Max(0.28, Math.Min(0.92, _config.CardTintOpacity));
            byte pillAlpha = (byte)Math.Round(Math.Min(0.86, baseOp + 0.10) * 255);

            Border pill = new Border();
            pill.CornerRadius = new CornerRadius(r);
            pill.Background = CreateFrozenBrush(pillAlpha, 13, 20, 34);
            pill.BorderThickness = new Thickness(borderW);
            pill.BorderBrush = pillRim;

            StackPanel content = new StackPanel();
            content.Orientation = Orientation.Horizontal;
            content.HorizontalAlignment = HorizontalAlignment.Center;
            content.VerticalAlignment = VerticalAlignment.Center;

            System.Windows.Shapes.Path mag = new System.Windows.Shapes.Path();
            mag.Data = _magGeometry;
            mag.Stroke = CreateFrozenBrush(242, 242, 246, 252);
            mag.StrokeThickness = 1.9;
            mag.StrokeStartLineCap = PenLineCap.Round;
            mag.StrokeEndLineCap = PenLineCap.Round;
            mag.Width = Math.Round(18 * u);
            mag.Height = Math.Round(18 * u);
            mag.Stretch = Stretch.Uniform;
            mag.VerticalAlignment = VerticalAlignment.Center;
            mag.Margin = new Thickness(0, 0, Math.Round(9 * u), 0);

            TextBlock lbl = new TextBlock();
            lbl.Text = "搜索";
            lbl.FontFamily = FontDisplay;
            lbl.FontSize = Math.Round(18.5 * u);
            lbl.FontWeight = FontWeights.Medium;
            lbl.Foreground = CreateFrozenBrush(248, 246, 250, 255);
            lbl.VerticalAlignment = VerticalAlignment.Center;

            content.Children.Add(mag);
            content.Children.Add(lbl);
            pill.Child = content;
            pillRoot.Children.Add(pill);
            pillRoot.MouseRightButtonDown += delegate
            {
                pillRoot.ContextMenu = BuildDesktopContextMenu();
            };

            pillRoot.MouseEnter += delegate
            {
                AnimateScale(st, 1.055, 130);
                pill.BorderBrush = pillRimHover;
            };
            pillRoot.MouseLeave += delegate
            {
                AnimateScale(st, 1.0, 130);
                pill.BorderBrush = pillRim;
            };
            pillRoot.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                AnimateScale(st, 0.93, 65);
                e.Handled = true;
            };
            pillRoot.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                AnimateScale(st, 1.0, 110);
                OpenSearchOverlay();
                e.Handled = true;
            };

            return pillRoot;
        }

        private void AddSoftShadowHalo(Grid host, double radius, double u)
        {
            // Single pre-cached soft Gaussian shadow plate on sibling layer (zero concentric bands,
            // zero per-frame shader re-evaluation on the Direct3D GPU SwapChain!)
            Border shadowPlate = new Border();
            shadowPlate.CornerRadius = new CornerRadius(radius);
            shadowPlate.Background = _brushHalo1;
            shadowPlate.IsHitTestVisible = false;
            shadowPlate.CacheMode = new BitmapCache();
            shadowPlate.Effect = GetFrozenShadowEffect(Colors.Black, Math.Round(28 * u), Math.Round(8 * u), 270, 0.50);
            host.Children.Add(shadowPlate);
        }

        private void AnimateBackgroundDepth(bool recessed)
        {
            if (_isPreviewMode) return;
            // Animate ONLY the single-quad GPU wallpaper texture (0.05ms GPU!) rather than re-rasterizing
            // 35 desktop icons + widgets on every frame — guarantees rock-solid locked 60-240 FPS!
            double wallTarget = recessed ? 1.028 : 1.0;
            int wallMs = recessed ? 290 : 220;
            _wallpaperScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, MakeAnim(null, wallTarget, wallMs, _easeOutCubic, 0));
            _wallpaperScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, MakeAnim(null, wallTarget, wallMs, _easeOutCubic, 0));
        }

        private void HideAllCachedFolderModals()
        {
            foreach (KeyValuePair<string, CachedFolderModalView> kv in _folderModalCache)
            {
                if (kv.Value != null && kv.Value.Root != null)
                {
                    kv.Value.Root.Visibility = Visibility.Hidden;
                    kv.Value.Root.IsHitTestVisible = false;
                }
            }
        }

        private void EnsureFolderModalsPrebuilt()
        {
            if (!_folderModalsDirty && _folderModalCache.Count > 0) return;
            _folderModalsDirty = false;
            _folderModalCache.Clear();
            _modalAppTargets.Clear();
            _modalContentHost.Children.Clear();
            if (_workspace == null) return;

            for (int i = 0; i < _workspace.Folders.Count; i++)
            {
                FolderGroup group = _workspace.Folders[i];
                CachedFolderModalView view = BuildCachedFolderModalView(group);
                _folderModalCache[group.FolderName] = view;
                _modalContentHost.Children.Add(view.Root);
            }
        }

        private CachedFolderModalView BuildCachedFolderModalView(FolderGroup group)
        {
            _modalAppTargets.Remove(group.FolderName);
            double W = _canvasW > 200 ? _canvasW : 1080;
            double H = _canvasH > 200 ? _canvasH : 1872;
            double u = ScaleUnit;
            bool land = IsLandscape;

            int modalCols = land ? ((group.Items.Count > 0 && group.Items.Count <= 3) ? group.Items.Count : 4) : 3;
            int itemRows = Math.Max(1, (int)Math.Ceiling((double)Math.Max(1, group.Items.Count) / Math.Max(1, modalCols)));

            double modalW = land ? Math.Round(Math.Min(W * 0.58, 980 * u)) : Math.Round(W * 0.88);
            double modalMaxH = land ? Math.Round(H * 0.78) : Math.Round(H * 0.68);
            double innerW = modalW - Math.Round(108 * u);
            double cellW = Math.Floor(innerW / Math.Max(1, modalCols));
            double iconSz = land
                ? Math.Round(Math.Min(116 * u, cellW * 0.55))
                : Math.Round(Math.Min(178 * u, cellW * 0.68));

            // Initial height estimate before card.SizeChanged locks the blurred wallpaper backdrop to exact ActualHeight!
            double estRowH = iconSz + Math.Round((land ? 92 : 112) * u);
            double estModalH = Math.Min(modalMaxH, Math.Round((land ? 185 : 210) * u) + itemRows * estRowH);
            double radius = Math.Round((land ? 40 : 54) * u);
            double borderW = Math.Max(1.0, Math.Round(1.35 * u * 10) / 10.0);

            LinearGradientBrush modalRim = new LinearGradientBrush();
            modalRim.StartPoint = new Point(0, 0);
            modalRim.EndPoint = new Point(0, 1);
            modalRim.GradientStops.Add(new GradientStop(Color.FromArgb(116, 255, 255, 255), 0.0));
            modalRim.GradientStops.Add(new GradientStop(Color.FromArgb(36, 255, 255, 255), 0.45));
            modalRim.GradientStops.Add(new GradientStop(Color.FromArgb(22, 255, 255, 255), 1.0));
            modalRim.Freeze();

            Grid modalRoot = new Grid();
            modalRoot.Width = modalW;
            modalRoot.MaxHeight = modalMaxH;
            modalRoot.HorizontalAlignment = HorizontalAlignment.Center;
            modalRoot.VerticalAlignment = VerticalAlignment.Center;
            modalRoot.AllowDrop = true;
            modalRoot.RenderTransformOrigin = new Point(0.5, 0.5);
            modalRoot.Visibility = Visibility.Hidden;
            modalRoot.IsHitTestVisible = false;
            modalRoot.Opacity = 0;
            // NOTE: CacheMode is attached ONLY during open/close animations and cleared to null at rest
            // so icon hovers, scrolling, and floating drags run at locked 160Hz with zero 4K VRAM re-rasterization!

            ScaleTransform st = new ScaleTransform(1.0, 1.0);
            TranslateTransform tt = new TranslateTransform(0, 0);
            TransformGroup tg = new TransformGroup();
            tg.Children.Add(st);
            tg.Children.Add(tt);
            modalRoot.RenderTransform = tg;

            AddSoftShadowHalo(modalRoot, radius, u);

            Border card = new Border();
            card.CornerRadius = new CornerRadius(radius);
            card.BorderThickness = new Thickness(borderW);
            card.BorderBrush = modalRim;

            Grid surface = new Grid();

            Border bgLayer = new Border();
            bgLayer.CornerRadius = new CornerRadius(Math.Max(1, radius - borderW));
            Action<double, double> syncBackdropViewbox = delegate(double actualW, double actualH)
            {
                if (_wallpaper == null || _wallpaper.BlurredBackdrop == null || actualW < 40 || actualH < 40) return;
                double mx = Math.Max(0, (W - actualW) * 0.5);
                double my = Math.Max(0, (H - actualH) * 0.5);
                ImageBrush ib = new ImageBrush(_wallpaper.BlurredBackdrop);
                ib.ViewboxUnits = BrushMappingMode.RelativeToBoundingBox;
                ib.Viewbox = new Rect(
                    Math.Max(0, Math.Min(0.99, mx / W)),
                    Math.Max(0, Math.Min(0.99, my / H)),
                    Math.Max(0.01, Math.Min(1.0 - mx / W, actualW / W)),
                    Math.Max(0.01, Math.Min(1.0 - my / H, actualH / H)));
                ib.Stretch = Stretch.Fill;
                ib.Freeze();
                bgLayer.Background = ib;
            };
            syncBackdropViewbox(modalW, estModalH);
            card.SizeChanged += delegate(object s, SizeChangedEventArgs e)
            {
                if (e.NewSize.Width > 40 && e.NewSize.Height > 40)
                {
                    syncBackdropViewbox(e.NewSize.Width, e.NewSize.Height);
                }
            };
            surface.Children.Add(bgLayer);

            Border tintLayer = new Border();
            tintLayer.CornerRadius = new CornerRadius(Math.Max(1, radius - borderW));
            LinearGradientBrush modalTint = new LinearGradientBrush();
            modalTint.StartPoint = new Point(0, 0);
            modalTint.EndPoint = new Point(0, 1);
            modalTint.GradientStops.Add(new GradientStop(Color.FromArgb(190, 16, 23, 39), 0.0));
            modalTint.GradientStops.Add(new GradientStop(Color.FromArgb(212, 10, 15, 27), 1.0));
            modalTint.Freeze();
            tintLayer.Background = modalTint;
            surface.Children.Add(tintLayer);

            // Delicate top-left crystal sheen
            Border modalSheen = new Border();
            modalSheen.CornerRadius = new CornerRadius(Math.Max(1, radius - borderW));
            modalSheen.IsHitTestVisible = false;
            LinearGradientBrush mSheenGrad = new LinearGradientBrush();
            mSheenGrad.StartPoint = new Point(0.1, 0);
            mSheenGrad.EndPoint = new Point(0.85, 0.75);
            mSheenGrad.GradientStops.Add(new GradientStop(Color.FromArgb(24, 255, 255, 255), 0.0));
            mSheenGrad.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 1.0));
            mSheenGrad.Freeze();
            modalSheen.Background = mSheenGrad;
            surface.Children.Add(modalSheen);

            DockPanel layout = new DockPanel();
            layout.Margin = new Thickness(Math.Round(44 * u), Math.Round(36 * u), Math.Round(44 * u), Math.Round(30 * u));

            StackPanel headerBlock = new StackPanel();
            headerBlock.Margin = new Thickness(Math.Round(6 * u), 0, Math.Round(6 * u), Math.Round(24 * u));
            DockPanel.SetDock(headerBlock, Dock.Top);

            Grid header = new Grid();

            Border countPill = new Border();
            countPill.CornerRadius = new CornerRadius(Math.Round(18 * u));
            countPill.Background = CreateFrozenBrush(28, 255, 255, 255);
            countPill.BorderBrush = CreateFrozenBrush(40, 255, 255, 255);
            countPill.BorderThickness = new Thickness(Math.Max(1.0, u));
            countPill.Padding = new Thickness(Math.Round(16 * u), Math.Round(8 * u), Math.Round(16 * u), Math.Round(8 * u));
            countPill.HorizontalAlignment = HorizontalAlignment.Left;
            countPill.VerticalAlignment = VerticalAlignment.Center;

            TextBlock countText = new TextBlock();
            countText.Text = string.Format("共 {0} 项", group.Items.Count);
            countText.FontFamily = FontText;
            countText.FontSize = Math.Round(17.5 * u);
            countText.FontWeight = FontWeights.Medium;
            countText.Foreground = CreateFrozenBrush(226, 232, 240, 248);
            countPill.Child = countText;
            header.Children.Add(countPill);

            // Center title + true in-place inline rename box (zero modal-close disruption!)
            Grid titleSlot = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            TextBlock titleText = new TextBlock();
            titleText.Text = group.DisplayName;
            titleText.FontFamily = FontDisplay;
            titleText.FontSize = Math.Round(34 * u);
            titleText.FontWeight = FontWeights.Bold;
            titleText.Foreground = Brushes.White;
            titleText.TextTrimming = TextTrimming.CharacterEllipsis;
            titleText.TextWrapping = TextWrapping.NoWrap;
            titleText.HorizontalAlignment = HorizontalAlignment.Center;
            titleText.VerticalAlignment = VerticalAlignment.Center;
            titleText.Cursor = Cursors.Hand;
            titleText.ToolTip = CreateDarkToolTip("点击直接重命名此分类", u);
            titleSlot.Children.Add(titleText);

            SolidColorBrush renameNormalBorder = CreateFrozenBrush(142, 96, 165, 250);
            SolidColorBrush renameErrorBorder = CreateFrozenBrush(235, 248, 113, 113);
            TranslateTransform renameShakeTf = new TranslateTransform(0, 0);

            StackPanel inlineRenamePanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed
            };

            Border renameBoxWrap = new Border
            {
                Width = Math.Round(Math.Min(330 * u, Math.Max(200 * u, innerW * 0.44))),
                CornerRadius = new CornerRadius(Math.Round(15 * u)),
                Background = CreateFrozenBrush(36, 255, 255, 255),
                BorderBrush = renameNormalBorder,
                BorderThickness = new Thickness(Math.Max(1.3, Math.Round(1.5 * u * 10) / 10.0)),
                Padding = new Thickness(Math.Round(14 * u), Math.Round(6 * u), Math.Round(14 * u), Math.Round(6 * u)),
                RenderTransform = renameShakeTf
            };

            TextBox inlineRenameBox = new TextBox
            {
                Text = group.DisplayName,
                FontFamily = FontDisplay,
                FontSize = Math.Round(24 * u),
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                CaretBrush = Brushes.White,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                TextAlignment = TextAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            renameBoxWrap.Child = inlineRenameBox;
            inlineRenamePanel.Children.Add(renameBoxWrap);

            double miniActSz = Math.Round(34 * u);
            Border confirmRenameBtn = new Border
            {
                Width = miniActSz,
                Height = miniActSz,
                CornerRadius = new CornerRadius(miniActSz * 0.5),
                Background = CreateFrozenBrush(220, 59, 130, 246),
                BorderBrush = CreateFrozenBrush(160, 147, 197, 253),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(Math.Round(8 * u), 0, 0, 0),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = CreateDarkToolTip("保存重命名 (Enter)", u),
                Child = new System.Windows.Shapes.Path
                {
                    Data = _checkGeometry,
                    Stroke = Brushes.White,
                    StrokeThickness = 2.0,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round,
                    Width = Math.Round(14 * u),
                    Height = Math.Round(14 * u),
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            inlineRenamePanel.Children.Add(confirmRenameBtn);

            Border cancelRenameBtn = new Border
            {
                Width = miniActSz,
                Height = miniActSz,
                CornerRadius = new CornerRadius(miniActSz * 0.5),
                Background = CreateFrozenBrush(34, 255, 255, 255),
                BorderBrush = CreateFrozenBrush(62, 255, 255, 255),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(Math.Round(6 * u), 0, 0, 0),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = CreateDarkToolTip("取消重命名 (Esc)", u),
                Child = new System.Windows.Shapes.Path
                {
                    Data = _closeGeometry,
                    Stroke = CreateFrozenBrush(230, 226, 232, 240),
                    StrokeThickness = 1.8,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    Width = Math.Round(11 * u),
                    Height = Math.Round(11 * u),
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            inlineRenamePanel.Children.Add(cancelRenameBtn);
            titleSlot.Children.Add(inlineRenamePanel);
            header.Children.Add(titleSlot);

            TextBlock renameErrorText = new TextBlock
            {
                FontFamily = FontText,
                FontSize = Math.Round(14 * u),
                FontWeight = FontWeights.Medium,
                Foreground = CreateFrozenBrush(255, 252, 165, 165),
                TextAlignment = TextAlignment.Center
            };
            Border renameErrorPill = new Border
            {
                CornerRadius = new CornerRadius(Math.Round(10 * u)),
                Background = CreateFrozenBrush(46, 239, 68, 68),
                BorderBrush = CreateFrozenBrush(125, 248, 113, 113),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(Math.Round(14 * u), Math.Round(5.5 * u), Math.Round(14 * u), Math.Round(5.5 * u)),
                Margin = new Thickness(0, Math.Round(10 * u), 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                Visibility = Visibility.Collapsed,
                Child = renameErrorText
            };

            // Right header actions: [打开目录] + sleek circular [✕] close button
            StackPanel rightActions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            Button openDirBtn = CreatePillButton("打开目录", u);
            openDirBtn.Margin = new Thickness(0, 0, Math.Round(10 * u), 0);
            FolderGroup capDirGroup = group;
            openDirBtn.Click += delegate
            {
                try { Process.Start("explorer.exe", "\"" + capDirGroup.FolderPath + "\""); } catch { }
            };
            rightActions.Children.Add(openDirBtn);

            double closeSz = Math.Round(40 * u);
            SolidColorBrush closeIdleBg = CreateFrozenBrush(36, 255, 255, 255);
            SolidColorBrush closeHoverBg = CreateFrozenBrush(72, 255, 255, 255);
            Border closeBtn = new Border
            {
                Width = closeSz,
                Height = closeSz,
                CornerRadius = new CornerRadius(closeSz * 0.5),
                Background = closeIdleBg,
                BorderBrush = CreateFrozenBrush(62, 255, 255, 255),
                BorderThickness = new Thickness(Math.Max(1.0, u)),
                Cursor = Cursors.Hand,
                RenderTransformOrigin = new Point(0.5, 0.5)
            };
            ScaleTransform closeSt = new ScaleTransform(1.0, 1.0);
            closeBtn.RenderTransform = closeSt;
            closeBtn.Child = new System.Windows.Shapes.Path
            {
                Data = _closeGeometry,
                Stroke = CreateFrozenBrush(238, 244, 248, 255),
                StrokeThickness = 1.9,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Width = Math.Round(14 * u),
                Height = Math.Round(14 * u),
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            closeBtn.ToolTip = CreateDarkToolTip("关闭 (Esc)", u);
            closeBtn.MouseEnter += delegate { closeBtn.Background = closeHoverBg; AnimateScale(closeSt, 1.06, 110); };
            closeBtn.MouseLeave += delegate { closeBtn.Background = closeIdleBg; AnimateScale(closeSt, 1.0, 110); };
            closeBtn.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { AnimateScale(closeSt, 0.92, 60); e.Handled = true; };
            closeBtn.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e) { CloseFolderModal(false); e.Handled = true; };
            rightActions.Children.Add(closeBtn);
            header.Children.Add(rightActions);

            // Constrain centered title & inline rename width symmetrically so long folder names trim with clean ellipsis
            // and NEVER collide with the left count pill ("共 X 项") or right buttons ("打开目录" / "✕")!
            countPill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            rightActions.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double preferredRenameW = renameBoxWrap.Width;
            Action<double> syncHeaderConstraints = delegate(double hostW)
            {
                if (hostW <= 0) return;
                double leftW = countPill.ActualWidth > 0 ? countPill.ActualWidth : countPill.DesiredSize.Width;
                double rightW = rightActions.ActualWidth > 0 ? rightActions.ActualWidth : rightActions.DesiredSize.Width;
                double symReserve = Math.Max(leftW, rightW) + Math.Round(18 * u);
                double avail = Math.Max(Math.Round(80 * u), Math.Floor(hostW - symReserve * 2));
                titleText.MaxWidth = avail;
                double maxRenameBoxW = Math.Max(Math.Round(60 * u), Math.Floor(avail - miniActSz * 2 - Math.Round(16 * u)));
                renameBoxWrap.Width = Math.Min(preferredRenameW, maxRenameBoxW);
            };
            double headerInnerW = modalW - borderW * 2 - Math.Round(88 * u) - Math.Round(12 * u);
            syncHeaderConstraints(headerInnerW);
            header.SizeChanged += delegate
            {
                syncHeaderConstraints(header.ActualWidth);
            };

            headerBlock.Children.Add(header);
            headerBlock.Children.Add(renameErrorPill);
            layout.Children.Add(headerBlock);

            TextBlock tip = new TextBlock();
            tip.Text = "拖拽图标可即时调整顺序  ·  点击标题可重命名  ·  右键图标可置顶或切换底色";
            tip.FontFamily = FontText;
            tip.FontSize = Math.Round(15 * u);
            tip.Foreground = CreateFrozenBrush(142, 203, 213, 225);
            tip.HorizontalAlignment = HorizontalAlignment.Center;
            tip.Margin = new Thickness(0, Math.Round(12 * u), 0, 0);
            DockPanel.SetDock(tip, Dock.Bottom);
            layout.Children.Add(tip);

            ScrollViewer sv = new ScrollViewer();
            sv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            sv.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            sv.Resources.Add(typeof(ScrollBar), _thinScrollBarStyle);
            new SmoothScrollDriver(sv);

            UniformGrid grid = new UniformGrid();
            grid.Columns = Math.Max(1, modalCols);
            grid.HorizontalAlignment = HorizontalAlignment.Stretch;
            grid.VerticalAlignment = VerticalAlignment.Top;

            SolidColorBrush labelBrush = CreateFrozenBrush(248, 245, 248, 254);

            for (int i = 0; i < group.Items.Count; i++)
            {
                AppItem app = group.Items[i];
                StackPanel cell = new StackPanel();
                cell.Background = Brushes.Transparent;
                cell.Margin = new Thickness(
                    Math.Round(10 * u),
                    Math.Round((land ? 10 : 12) * u),
                    Math.Round(10 * u),
                    Math.Round((land ? 20 : 26) * u));
                cell.HorizontalAlignment = HorizontalAlignment.Center;

                ScaleTransform dummyS;
                TranslateTransform dummyT;
                UIElement iconTile = BuildAppIconTile(app, group, group.Items, iconSz, true, false, u, out dummyS, out dummyT, -1, sv);
                cell.Children.Add(iconTile);

                TextBlock nameLbl = new TextBlock();
                nameLbl.Text = app.DisplayName;
                nameLbl.FontFamily = FontText;
                nameLbl.FontSize = Math.Round((land ? 17 : 21.5) * u);
                nameLbl.FontWeight = FontWeights.Medium;
                nameLbl.Foreground = labelBrush;
                nameLbl.TextAlignment = TextAlignment.Center;
                nameLbl.TextWrapping = TextWrapping.Wrap;
                nameLbl.TextTrimming = TextTrimming.CharacterEllipsis;
                nameLbl.LineHeight = Math.Round((land ? 22.5 : 28.5) * u);
                nameLbl.MaxHeight = Math.Round((land ? 46 : 58) * u);
                nameLbl.MaxWidth = cellW - Math.Round(4 * u);
                nameLbl.Margin = new Thickness(0, Math.Round((land ? 11 : 15) * u), 0, 0);
                cell.Children.Add(nameLbl);

                grid.Children.Add(cell);
            }

            if (group.Items.Count == 0)
            {
                TextBlock emptyHint = new TextBlock();
                emptyHint.Text = "将任意快捷方式或程序拖入此处即可添加";
                emptyHint.FontFamily = FontText;
                emptyHint.FontSize = Math.Round(21 * u);
                emptyHint.Foreground = CreateFrozenBrush(170, 203, 213, 225);
                emptyHint.HorizontalAlignment = HorizontalAlignment.Center;
                emptyHint.Margin = new Thickness(Math.Round(20 * u), Math.Round(72 * u), Math.Round(20 * u), Math.Round(72 * u));
                sv.Content = emptyHint;
            }
            else
            {
                sv.Content = grid;
            }
            layout.Children.Add(sv);

            modalRoot.Drop += delegate(object s, DragEventArgs e)
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    string[] dropped = e.Data.GetData(DataFormats.FileDrop) as string[];
                    FolderScanner.IngestDroppedFiles(dropped, capDirGroup.FolderPath);
                    ReloadWorkspace();
                }
                e.Handled = true;
            };

            surface.Children.Add(layout);
            card.Child = surface;
            modalRoot.Children.Add(card);

            CachedFolderModalView modalView = new CachedFolderModalView
            {
                Group = group,
                Root = modalRoot,
                Scale = st,
                Translate = tt,
                ScrollHost = sv,
                IsInlineRenaming = false,
                InlineRenameBox = inlineRenameBox
            };

            Action cancelInlineRename = delegate
            {
                modalView.IsInlineRenaming = false;
                inlineRenamePanel.Visibility = Visibility.Collapsed;
                renameErrorPill.Visibility = Visibility.Collapsed;
                renameBoxWrap.BorderBrush = renameNormalBorder;
                titleText.Visibility = Visibility.Visible;
                if (!_isPreviewMode && DesktopAttachment.AllowTemporaryFront)
                {
                    IntPtr h = new WindowInteropHelper(this).Handle;
                    DesktopAttachment.SendToBottom(h);
                }
            };

            Action beginInlineRename = delegate
            {
                modalView.IsInlineRenaming = true;
                inlineRenameBox.Text = capDirGroup.DisplayName;
                renameErrorPill.Visibility = Visibility.Collapsed;
                renameBoxWrap.BorderBrush = renameNormalBorder;
                titleText.Visibility = Visibility.Collapsed;
                inlineRenamePanel.Visibility = Visibility.Visible;
                if (!_isPreviewMode)
                {
                    IntPtr h = new WindowInteropHelper(this).Handle;
                    DesktopAttachment.BringToFrontForOverlay(h);
                    Activate();
                    inlineRenameBox.Focus();
                    inlineRenameBox.SelectAll();
                    Keyboard.Focus(inlineRenameBox);
                }
            };

            Action submitInlineRename = delegate
            {
                string oldKey = capDirGroup.FolderName;
                string err;
                bool renamed = FolderScanner.TryRenameCategoryFolder(_config, _workspace, capDirGroup, inlineRenameBox.Text, out err);
                if (!string.IsNullOrEmpty(err))
                {
                    renameErrorText.Text = err;
                    renameErrorPill.Visibility = Visibility.Visible;
                    renameBoxWrap.BorderBrush = renameErrorBorder;
                    ShakeHorizontal(renameShakeTf, u);
                    if (!_isPreviewMode)
                    {
                        inlineRenameBox.Focus();
                        inlineRenameBox.SelectAll();
                    }
                    return;
                }

                if (renamed)
                {
                    titleText.Text = capDirGroup.DisplayName;
                    string newKey = capDirGroup.FolderName;
                    if (!string.Equals(oldKey, newKey, StringComparison.OrdinalIgnoreCase))
                    {
                        _folderModalCache.Remove(oldKey);
                        _folderModalCache[newKey] = modalView;
                        List<DragAppTarget> modalTargets;
                        if (_modalAppTargets.TryGetValue(oldKey, out modalTargets))
                        {
                            _modalAppTargets.Remove(oldKey);
                            _modalAppTargets[newKey] = modalTargets;
                        }
                    }
                    _searchCacheDirty = true;
                    _lastManualReloadUtc = DateTime.UtcNow;
                    RenderDesktop();
                }

                cancelInlineRename();
            };

            modalView.BeginInlineRename = beginInlineRename;
            modalView.SubmitInlineRename = submitInlineRename;
            modalView.CancelInlineRename = cancelInlineRename;

            titleText.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                beginInlineRename();
                e.Handled = true;
            };
            confirmRenameBtn.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                submitInlineRename();
                e.Handled = true;
            };
            cancelRenameBtn.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                cancelInlineRename();
                e.Handled = true;
            };
            inlineRenameBox.TextChanged += delegate
            {
                if (renameErrorPill.Visibility == Visibility.Visible)
                {
                    renameErrorPill.Visibility = Visibility.Collapsed;
                    renameBoxWrap.BorderBrush = renameNormalBorder;
                }
            };
            inlineRenameBox.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Enter)
                {
                    submitInlineRename();
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    cancelInlineRename();
                    e.Handled = true;
                }
            };

            return modalView;
        }

        private void OpenFolderModal(FolderGroup group)
        {
            if (group == null) return;
            CloseSearchOverlay(true);
            int token = ++_modalAnimToken;
            _openFolder = group;
            _dialogContentHost.Children.Clear();
            HideAllCachedFolderModals();

            EnsureFolderModalsPrebuilt();
            CachedFolderModalView view;
            if (!_folderModalCache.TryGetValue(group.FolderName, out view))
            {
                view = BuildCachedFolderModalView(group);
                _folderModalCache[group.FolderName] = view;
                _modalContentHost.Children.Add(view.Root);
            }
            if (view.IsInlineRenaming && view.CancelInlineRename != null)
            {
                view.CancelInlineRename();
            }

            double W = _canvasW > 200 ? _canvasW : 1080;
            double H = _canvasH > 200 ? _canvasH : 1872;

            // Origin-anchored smooth morph vector from the clicked Big Folder card!
            double startDx = 0;
            double startDy = 0;
            Rect cardRect;
            if (_folderCardRects.TryGetValue(group.FolderName, out cardRect))
            {
                double cardCx = cardRect.X + cardRect.Width * 0.5;
                double cardCy = cardRect.Y + cardRect.Height * 0.5;
                startDx = Math.Round((cardCx - W * 0.5) * 0.34);
                startDy = Math.Round((cardCy - H * 0.5) * 0.34);
            }
            _modalOriginDx = startDx;
            _modalOriginDy = startDy;

            _activeModalView = view;
            _activeModalRoot = view.Root;
            _activeModalScale = view.Scale;
            _activeModalTranslate = view.Translate;
            _modalIsCachedFolder = true;
            _modalOverlayActive = true;

            if (_isPreviewMode)
            {
                _modalBackdrop.Opacity = 1.0;
                view.Scale.ScaleX = 1.0;
                view.Scale.ScaleY = 1.0;
                view.Translate.X = 0;
                view.Translate.Y = 0;
                view.Root.Opacity = 1.0;
                view.Root.CacheMode = null;
                view.Root.Visibility = Visibility.Visible;
                view.Root.IsHitTestVisible = true;
                _modalOverlay.Visibility = Visibility.Visible;
                _modalOverlay.IsHitTestVisible = true;
                return;
            }

            // Enable HiDPI BitmapCache ONLY for the 260ms open transition so it flies as 1 GPU quad!
            view.Root.CacheMode = CreateHiDpiBitmapCache();

            _modalBackdrop.BeginAnimation(UIElement.OpacityProperty, null);
            _modalBackdrop.Opacity = 0.0;
            view.Root.BeginAnimation(UIElement.OpacityProperty, null);
            view.Root.Opacity = 0.0;
            view.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            view.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            view.Scale.ScaleX = 0.80;
            view.Scale.ScaleY = 0.80;
            view.Translate.BeginAnimation(TranslateTransform.XProperty, null);
            view.Translate.BeginAnimation(TranslateTransform.YProperty, null);
            view.Translate.X = startDx;
            view.Translate.Y = startDy;

            view.Root.Visibility = Visibility.Visible;
            view.Root.IsHitTestVisible = true;
            _modalOverlay.Visibility = Visibility.Visible;
            _modalOverlay.IsHitTestVisible = true;

            Dispatcher.BeginInvoke(DispatcherPriority.Render, (Action)delegate
            {
                if (token != _modalAnimToken || !_modalOverlayActive) return;
                _modalBackdrop.BeginAnimation(UIElement.OpacityProperty, MakeAnim(0.0, 1.0, 240, _easeOutCubic, 0));
                AnimateBackgroundDepth(true);

                DoubleAnimation scaleXAnim = MakeAnim(0.80, 1.0, 270, _easeOutBackSoft, 0);
                scaleXAnim.Completed += delegate
                {
                    if (token != _modalAnimToken || !_modalOverlayActive) return;
                    // Drop BitmapCache once landed so hovering, scrolling, and dragging run at locked 160Hz with zero VRAM churn!
                    view.Root.CacheMode = null;
                    Focus();
                };
                view.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleXAnim);
                view.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, MakeAnim(0.80, 1.0, 270, _easeOutBackSoft, 0));
                view.Translate.BeginAnimation(TranslateTransform.XProperty, MakeAnim(startDx, 0.0, 260, _easeOutExpo, 0));
                view.Translate.BeginAnimation(TranslateTransform.YProperty, MakeAnim(startDy, 0.0, 260, _easeOutExpo, 0));
                view.Root.BeginAnimation(UIElement.OpacityProperty, MakeAnim(0.0, 1.0, 180, _easeOutCubic, 0));
            });
        }

        private void CloseFolderModal(bool immediate)
        {
            CancelPointerDrag();
            DetachPendingModalScrollRestore();
            if (_activeModalView != null && _activeModalView.IsInlineRenaming && _activeModalView.CancelInlineRename != null)
            {
                _activeModalView.CancelInlineRename();
            }
            if (!_modalIsCachedFolder && _mailService != null)
            {
                _mailService.CancelPendingOAuth();
            }
            int token = ++_modalAnimToken;
            _openFolder = null;
            _activeModalView = null;
            bool wasActive = _modalOverlayActive;
            _modalOverlayActive = false;

            if (immediate || _isPreviewMode || !wasActive || _activeModalRoot == null)
            {
                _modalOverlay.Visibility = Visibility.Hidden;
                _modalOverlay.IsHitTestVisible = false;
                _modalBackdrop.BeginAnimation(UIElement.OpacityProperty, null);
                _modalBackdrop.Opacity = 0;
                HideAllCachedFolderModals();
                _dialogContentHost.Children.Clear();
                _activeModalRoot = null;
                _activeModalScale = null;
                _activeModalTranslate = null;
                if (!_searchOverlayActive)
                {
                    AnimateBackgroundDepth(false);
                }
                if (DesktopAttachment.AllowTemporaryFront)
                {
                    IntPtr h = new WindowInteropHelper(this).Handle;
                    DesktopAttachment.SendToBottom(h);
                }
                return;
            }

            _modalOverlay.IsHitTestVisible = false;
            AnimateBackgroundDepth(false);

            FrameworkElement closingRoot = _activeModalRoot;
            closingRoot.CacheMode = CreateHiDpiBitmapCache();
            bool isCached = _modalIsCachedFolder;

            DoubleAnimation fadeBack = MakeAnim(null, 0.0, 200, _easeOutCubic, 0);
            fadeBack.Completed += delegate
            {
                if (token != _modalAnimToken) return;
                _modalOverlay.Visibility = Visibility.Hidden;
                _modalOverlay.IsHitTestVisible = false;
                if (isCached)
                {
                    if (closingRoot != null)
                    {
                        closingRoot.CacheMode = null;
                        closingRoot.Visibility = Visibility.Hidden;
                        closingRoot.IsHitTestVisible = false;
                    }
                }
                else
                {
                    _dialogContentHost.Children.Clear();
                }
                _activeModalRoot = null;
                _activeModalScale = null;
                _activeModalTranslate = null;
                if (DesktopAttachment.AllowTemporaryFront)
                {
                    IntPtr h = new WindowInteropHelper(this).Handle;
                    DesktopAttachment.SendToBottom(h);
                }
            };
            _modalBackdrop.BeginAnimation(UIElement.OpacityProperty, fadeBack);

            if (_activeModalScale != null)
            {
                _activeModalScale.BeginAnimation(ScaleTransform.ScaleXProperty, MakeAnim(null, 0.82, 195, _easeOutCubic, 0));
                _activeModalScale.BeginAnimation(ScaleTransform.ScaleYProperty, MakeAnim(null, 0.82, 195, _easeOutCubic, 0));
            }
            if (_activeModalTranslate != null)
            {
                _activeModalTranslate.BeginAnimation(TranslateTransform.XProperty, MakeAnim(null, _modalOriginDx * 0.85, 195, _easeOutCubic, 0));
                _activeModalTranslate.BeginAnimation(TranslateTransform.YProperty, MakeAnim(null, _modalOriginDy * 0.85, 195, _easeOutCubic, 0));
            }
            closingRoot.BeginAnimation(UIElement.OpacityProperty, MakeAnim(null, 0.0, 165, _easeOutCubic, 0));
        }

        private double ComputeFallbackSearchPillTopY(double W, double H, double u, bool land)
        {
            int dockCount = (_workspace != null && _workspace.DockItems != null) ? _workspace.DockItems.Count : 0;
            DockCapsuleMetrics dm = ComputeDockCapsuleMetrics(W, H, u, dockCount, land);
            bool hasDock = dm.Count > 0;
            if (land)
            {
                double bMargin = Math.Round(Math.Max(14, H * 0.018));
                double dCapH = hasDock ? dm.CapsuleH : 0;
                double dTopGap = hasDock ? Math.Round(13 * u) : 0;
                double sH = Math.Round(44 * u);
                return Math.Round(H - bMargin - dCapH - dTopGap - sH);
            }
            else
            {
                double bMargin = Math.Round(H * 0.022);
                double dCapH = hasDock ? dm.CapsuleH : 0;
                double dTopGap = hasDock ? Math.Round(H * 0.014) : 0;
                double sH = Math.Round(52 * u);
                return Math.Round(H - bMargin - dCapH - dTopGap - sH);
            }
        }

        private void EnsureSearchOverlayBuilt()
        {
            double W = _canvasW > 200 ? _canvasW : 1080;
            double H = _canvasH > 200 ? _canvasH : 1872;
            double u = ScaleUnit;
            bool land = IsLandscape;

            double fallbackBottomTopY = ComputeFallbackSearchPillTopY(W, H, u, land);
            double bottomAnchorTopY = (_lastSearchPillTopY > 100 && _lastSearchPillTopY < H)
                ? _lastSearchPillTopY
                : fallbackBottomTopY;

            bool layoutChanged =
                Math.Abs(_searchBuiltW - W) > 0.5 ||
                Math.Abs(_searchBuiltH - H) > 0.5 ||
                _searchBuiltShowClock != _config.ShowTopClock ||
                Math.Abs(_searchBuiltBottomTopY - bottomAnchorTopY) > 0.5;

            if (!_searchCacheDirty && !layoutChanged && _searchBox != null && _searchResultsPanel != null) return;
            _searchCacheDirty = false;
            _searchBuiltW = W;
            _searchBuiltH = H;
            _searchBuiltShowClock = _config.ShowTopClock;
            _searchBuiltBottomTopY = bottomAnchorTopY;

            if (_searchSmoothScroll != null)
            {
                _searchSmoothScroll.Stop();
                _searchSmoothScroll = null;
            }
            _searchContentHost.Children.Clear();
            _searchTilePool.Clear();
            _visibleSearchTiles.Clear();

            double panelW = land ? Math.Round(Math.Min(W * 0.58, 960 * u)) : Math.Round(W * 0.88);
            double inputRadius = Math.Round(30 * u);
            double resultsRadius = Math.Round((land ? 34 : 42) * u);

            // Keep crisp breathing room below the top clock
            double topMargin = land
                ? (_config.ShowTopClock ? Math.Max(Math.Round(102 * u), Math.Round(H * 0.106)) : Math.Round(H * 0.068))
                : (_config.ShowTopClock ? Math.Round(H * 0.112) : Math.Round(H * 0.075));

            StackPanel container = new StackPanel();
            container.Width = panelW;
            container.HorizontalAlignment = HorizontalAlignment.Center;
            container.VerticalAlignment = VerticalAlignment.Top;
            container.Margin = new Thickness(0, topMargin, 0, 0);

            // 1. Sleek Spotlight Search Capsule
            Grid inputRoot = new Grid();
            inputRoot.RenderTransformOrigin = new Point(0.5, 0.5);
            _searchInputScale = new ScaleTransform(1.0, 1.0);
            _searchInputTranslate = new TranslateTransform(0, 0);
            TransformGroup inTg = new TransformGroup();
            inTg.Children.Add(_searchInputScale);
            inTg.Children.Add(_searchInputTranslate);
            inputRoot.RenderTransform = inTg;
            AddSoftShadowHalo(inputRoot, inputRadius, u);

            Border inputCard = new Border();
            inputCard.CornerRadius = new CornerRadius(inputRadius);
            inputCard.Background = CreateFrozenBrush(248, 15, 22, 37);
            inputCard.BorderBrush = CreateFrozenBrush(112, 255, 255, 255);
            inputCard.BorderThickness = new Thickness(Math.Max(1.0, Math.Round(1.3 * u * 10) / 10.0));
            inputCard.Padding = new Thickness(Math.Round(24 * u), Math.Round(13 * u), Math.Round(16 * u), Math.Round(13 * u));

            DockPanel inputRow = new DockPanel();

            System.Windows.Shapes.Path mag = new System.Windows.Shapes.Path();
            mag.Data = _magGeometry;
            mag.Stroke = CreateFrozenBrush(220, 203, 213, 225);
            mag.StrokeThickness = 2.0;
            mag.StrokeStartLineCap = PenLineCap.Round;
            mag.StrokeEndLineCap = PenLineCap.Round;
            mag.Width = Math.Round(23 * u);
            mag.Height = Math.Round(23 * u);
            mag.Stretch = Stretch.Uniform;
            mag.VerticalAlignment = VerticalAlignment.Center;
            mag.Margin = new Thickness(0, 0, Math.Round(14 * u), 0);
            DockPanel.SetDock(mag, Dock.Left);
            inputRow.Children.Add(mag);

            bool hasEverything = !string.IsNullOrEmpty(FindEverythingPath());
            Button globalSearchBtn = CreatePillButton(hasEverything ? "Everything 全局搜" : "Windows 全局搜", u);
            DockPanel.SetDock(globalSearchBtn, Dock.Right);
            globalSearchBtn.Margin = new Thickness(Math.Round(10 * u), 0, 0, 0);
            globalSearchBtn.Click += delegate
            {
                LaunchEverythingSearch(_searchBox != null ? _searchBox.Text : "");
                CloseSearchOverlay(false);
            };
            inputRow.Children.Add(globalSearchBtn);

            // Sleek inline clear (×) button shown whenever query is non-empty
            double clearSz = Math.Round(28 * u);
            SolidColorBrush clearIdleBg = CreateFrozenBrush(36, 255, 255, 255);
            SolidColorBrush clearHoverBg = CreateFrozenBrush(68, 255, 255, 255);
            _searchClearBtn = new Border
            {
                Width = clearSz,
                Height = clearSz,
                CornerRadius = new CornerRadius(clearSz * 0.5),
                Background = clearIdleBg,
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(Math.Round(8 * u), 0, 0, 0),
                Visibility = Visibility.Collapsed,
                ToolTip = CreateDarkToolTip("清空搜索词 (Esc)", u),
                Child = new System.Windows.Shapes.Path
                {
                    Data = _closeGeometry,
                    Stroke = CreateFrozenBrush(230, 226, 232, 240),
                    StrokeThickness = 1.8,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    Width = Math.Round(10 * u),
                    Height = Math.Round(10 * u),
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            _searchClearBtn.MouseEnter += delegate { _searchClearBtn.Background = clearHoverBg; };
            _searchClearBtn.MouseLeave += delegate { _searchClearBtn.Background = clearIdleBg; };
            _searchClearBtn.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                if (_searchBox != null)
                {
                    _searchBox.Text = "";
                    _searchBox.Focus();
                }
                e.Handled = true;
            };
            DockPanel.SetDock(_searchClearBtn, Dock.Right);
            inputRow.Children.Add(_searchClearBtn);

            Grid boxWrap = new Grid();
            boxWrap.VerticalAlignment = VerticalAlignment.Center;

            _searchPlaceholder = new TextBlock();
            _searchPlaceholder.Text = "搜索应用、分类或拼音首字母 (如 wx / vsc)...";
            _searchPlaceholder.FontFamily = FontText;
            _searchPlaceholder.FontSize = Math.Round((land ? 21 : 24) * u);
            _searchPlaceholder.Foreground = CreateFrozenBrush(136, 203, 213, 225);
            _searchPlaceholder.VerticalAlignment = VerticalAlignment.Center;
            _searchPlaceholder.IsHitTestVisible = false;
            boxWrap.Children.Add(_searchPlaceholder);

            _searchBox = new TextBox();
            _searchBox.FontFamily = FontDisplay;
            _searchBox.FontSize = Math.Round((land ? 22 : 25) * u);
            _searchBox.Foreground = Brushes.White;
            _searchBox.CaretBrush = Brushes.White;
            _searchBox.Background = Brushes.Transparent;
            _searchBox.BorderThickness = new Thickness(0);
            _searchBox.VerticalContentAlignment = VerticalAlignment.Center;
            _searchBox.TextChanged += delegate
            {
                bool isEmpty = string.IsNullOrEmpty(_searchBox.Text);
                if (_searchPlaceholder != null)
                {
                    _searchPlaceholder.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
                }
                if (_searchClearBtn != null)
                {
                    _searchClearBtn.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;
                }
                UpdateSearchResults();
            };
            boxWrap.Children.Add(_searchBox);
            inputRow.Children.Add(boxWrap);
            inputCard.Child = inputRow;
            inputRoot.Children.Add(inputCard);
            _searchInputRoot = inputRoot;
            container.Children.Add(inputRoot);

            // Measure exact input card height + compute exact bottom clearance above the floating Search pill (and Dock)!
            inputCard.Measure(new Size(panelW, 240));
            double inputCardH = Math.Max(Math.Round(66 * u), Math.Ceiling(inputCard.DesiredSize.Height));
            double resultsGap = Math.Round((land ? 16 : 20) * u);
            double resultsTopY = topMargin + inputCardH + resultsGap;

            double bottomClearance = Math.Round((land ? 16 : 24) * u);
            double maxResultsH = Math.Max(Math.Round(240 * u), Math.Floor(bottomAnchorTopY - bottomClearance - resultsTopY));
            if (!land)
            {
                maxResultsH = Math.Min(maxResultsH, Math.Round(H * 0.65));
            }

            // 2. Results Frosted Glass Box
            Grid resultsRoot = new Grid();
            resultsRoot.Margin = new Thickness(0, resultsGap, 0, 0);
            resultsRoot.RenderTransformOrigin = new Point(0.5, 0.12);
            _searchResultsScale = new ScaleTransform(1.0, 1.0);
            _searchResultsTranslate = new TranslateTransform(0, 0);
            TransformGroup resTg = new TransformGroup();
            resTg.Children.Add(_searchResultsScale);
            resTg.Children.Add(_searchResultsTranslate);
            resultsRoot.RenderTransform = resTg;
            AddSoftShadowHalo(resultsRoot, resultsRadius, u);

            double padL = Math.Round((land ? 24 : 26) * u);
            double padT = Math.Round((land ? 20 : 24) * u);
            double padR = Math.Round((land ? 20 : 22) * u);
            double padB = Math.Round((land ? 16 : 20) * u);

            Border resultsCard = new Border();
            resultsCard.CornerRadius = new CornerRadius(resultsRadius);
            resultsCard.Background = CreateFrozenBrush(236, 13, 20, 34);
            resultsCard.BorderBrush = CreateFrozenBrush(84, 255, 255, 255);
            resultsCard.BorderThickness = new Thickness(Math.Max(1.0, Math.Round(1.2 * u * 10) / 10.0));
            resultsCard.Padding = new Thickness(padL, padT, padR, padB);
            resultsCard.MaxHeight = maxResultsH;

            DockPanel resDock = new DockPanel();

            _searchFooterHint = new TextBlock
            {
                Text = "↑ ↓ ← → 选择  ·  Enter 启动  ·  Shift+Enter 多开  ·  Esc 关闭",
                FontFamily = FontText,
                FontSize = Math.Round((land ? 13 : 15) * u),
                Foreground = CreateFrozenBrush(150, 198, 212, 232),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, Math.Round(12 * u), 0, 0)
            };
            _searchFooterHint.Measure(new Size(panelW, 60));
            double footerTotalH = Math.Ceiling(_searchFooterHint.DesiredSize.Height) + Math.Round(12 * u);
            DockPanel.SetDock(_searchFooterHint, Dock.Bottom);
            resDock.Children.Add(_searchFooterHint);

            _searchAvailViewportH = Math.Max(Math.Round(160 * u), Math.Floor(maxResultsH - padT - padB - footerTotalH - Math.Round(4 * u)));
            _searchInnerPanelW = panelW - padL - padR - Math.Round(16 * u);

            Grid resultsBody = new Grid();

            _searchScrollViewer = new ScrollViewer();
            _searchScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _searchScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            _searchScrollViewer.MaxHeight = _searchAvailViewportH;
            _searchScrollViewer.Resources.Add(typeof(ScrollBar), _thinScrollBarStyle);
            _searchSmoothScroll = new SmoothScrollDriver(_searchScrollViewer);
            _searchScrollViewer.ScrollChanged += delegate { UpdateSearchScrollFades(); };

            _searchColumns = land ? 5 : 3;
            _searchResultsPanel = new UniformGrid();
            _searchResultsPanel.Columns = _searchColumns;
            _searchResultsPanel.VerticalAlignment = VerticalAlignment.Top;
            _searchScrollViewer.Content = _searchResultsPanel;
            resultsBody.Children.Add(_searchScrollViewer);

            // Dynamic top & bottom soft scroll fades (ONLY visible when overflow exists above/below!)
            LinearGradientBrush fadeTop = new LinearGradientBrush();
            fadeTop.StartPoint = new Point(0, 0);
            fadeTop.EndPoint = new Point(0, 1);
            fadeTop.GradientStops.Add(new GradientStop(Color.FromArgb(210, 13, 20, 34), 0.0));
            fadeTop.GradientStops.Add(new GradientStop(Color.FromArgb(0, 13, 20, 34), 1.0));
            fadeTop.Freeze();
            _searchTopFade = new Border
            {
                Height = Math.Round(16 * u),
                VerticalAlignment = VerticalAlignment.Top,
                Background = fadeTop,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            resultsBody.Children.Add(_searchTopFade);

            LinearGradientBrush fadeBottom = new LinearGradientBrush();
            fadeBottom.StartPoint = new Point(0, 0);
            fadeBottom.EndPoint = new Point(0, 1);
            fadeBottom.GradientStops.Add(new GradientStop(Color.FromArgb(0, 13, 20, 34), 0.0));
            fadeBottom.GradientStops.Add(new GradientStop(Color.FromArgb(205, 13, 20, 34), 1.0));
            fadeBottom.Freeze();
            _searchBottomFade = new Border
            {
                Height = Math.Round(16 * u),
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = fadeBottom,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            resultsBody.Children.Add(_searchBottomFade);

            _searchEmptyHint = new TextBlock();
            _searchEmptyHint.Text = hasEverything
                ? "未在启动台中匹配到应用 — 点击右上角「Everything 全局搜」检索全盘"
                : "未在启动台中匹配到应用 — 点击右上角「Windows 全局搜」继续检索";
            _searchEmptyHint.FontFamily = FontText;
            _searchEmptyHint.FontSize = Math.Round(18.5 * u);
            _searchEmptyHint.TextAlignment = TextAlignment.Center;
            _searchEmptyHint.TextWrapping = TextWrapping.Wrap;
            _searchEmptyHint.Foreground = CreateFrozenBrush(196, 208, 222, 236);
            _searchEmptyHint.HorizontalAlignment = HorizontalAlignment.Center;
            _searchEmptyHint.VerticalAlignment = VerticalAlignment.Center;
            _searchEmptyHint.Margin = new Thickness(Math.Round(24 * u), Math.Round(42 * u), Math.Round(24 * u), Math.Round(42 * u));
            _searchEmptyHint.Visibility = Visibility.Collapsed;
            resultsBody.Children.Add(_searchEmptyHint);

            resDock.Children.Add(resultsBody);
            resultsCard.Child = resDock;
            resultsRoot.Children.Add(resultsCard);
            _searchResultsRoot = resultsRoot;
            container.Children.Add(resultsRoot);

            if (_workspace != null)
            {
                double cellW = Math.Floor(_searchInnerPanelW / _searchColumns);
                double iconSz = land
                    ? Math.Round(Math.Min(86 * u, cellW * 0.52))
                    : Math.Round(Math.Min(138 * u, cellW * 0.58));
                SolidColorBrush catBrush = CreateFrozenBrush(168, 190, 206, 228);

                for (int i = 0; i < _workspace.AllItems.Count; i++)
                {
                    AppItem item = _workspace.AllItems[i];
                    StackPanel cell = new StackPanel();
                    cell.Margin = new Thickness(Math.Round(4 * u), Math.Round(4 * u), Math.Round(4 * u), Math.Round(8 * u));
                    cell.HorizontalAlignment = HorizontalAlignment.Center;
                    cell.Cursor = Cursors.Hand;

                    Border highlight = new Border();
                    highlight.CornerRadius = new CornerRadius(Math.Round(20 * u));
                    highlight.Padding = new Thickness(Math.Round(9 * u), Math.Round(10 * u), Math.Round(9 * u), Math.Round(9 * u));
                    highlight.Background = Brushes.Transparent;
                    highlight.BorderBrush = Brushes.Transparent;
                    highlight.BorderThickness = new Thickness(Math.Max(1.0, 1.3 * u));
                    highlight.Width = cellW - Math.Round(8 * u);

                    StackPanel inner = new StackPanel();
                    inner.HorizontalAlignment = HorizontalAlignment.Center;

                    Image img = new Image();
                    img.Source = item.Icon;
                    img.Width = iconSz;
                    img.Height = iconSz;
                    RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.Fant);
                    inner.Children.Add(img);

                    TextBlock name = new TextBlock();
                    name.Text = item.DisplayName;
                    name.FontFamily = FontText;
                    name.FontSize = Math.Round((land ? 15 : 19.5) * u);
                    name.FontWeight = FontWeights.Medium;
                    name.Foreground = Brushes.White;
                    name.TextAlignment = TextAlignment.Center;
                    name.TextWrapping = TextWrapping.Wrap;
                    name.TextTrimming = TextTrimming.CharacterEllipsis;
                    name.LineHeight = Math.Round((land ? 19.5 : 25) * u);
                    name.MaxHeight = Math.Round((land ? 40 : 51) * u);
                    name.MaxWidth = cellW - Math.Round(26 * u);
                    name.Margin = new Thickness(0, Math.Round(8 * u), 0, 0);
                    inner.Children.Add(name);

                    TextBlock cat = new TextBlock();
                    cat.Text = item.FolderDisplayName;
                    cat.FontFamily = FontText;
                    cat.FontSize = Math.Round((land ? 12 : 15) * u);
                    cat.Foreground = catBrush;
                    cat.TextAlignment = TextAlignment.Center;
                    cat.TextTrimming = TextTrimming.CharacterEllipsis;
                    cat.MaxWidth = cellW - Math.Round(26 * u);
                    cat.Margin = new Thickness(0, Math.Round(2.5 * u), 0, 0);
                    inner.Children.Add(cat);

                    highlight.Child = inner;
                    SearchTileItem tileEntry = new SearchTileItem
                    {
                        App = item,
                        Cell = cell,
                        Highlight = highlight,
                        IsTopHit = false
                    };
                    AppItem captured = item;
                    highlight.MouseLeftButtonUp += delegate
                    {
                        bool forceNew = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
                        FolderScanner.LaunchItem(captured, forceNew);
                        CloseSearchOverlay(false);
                    };
                    highlight.MouseEnter += delegate
                    {
                        if (!tileEntry.IsTopHit) highlight.Background = _brushHoverTile;
                    };
                    highlight.MouseLeave += delegate
                    {
                        highlight.Background = tileEntry.IsTopHit ? (Brush)_brushTopHitTile : Brushes.Transparent;
                    };

                    cell.Children.Add(highlight);
                    _searchTilePool.Add(tileEntry);
                }
            }

            _searchContentHost.Children.Add(container);
            UpdateSearchResults();
        }

        public void OpenSearchOverlay()
        {
            CloseFolderModal(true);
            int token = ++_searchAnimToken;

            EnsureSearchOverlayBuilt();

            if (_searchBox != null && !string.IsNullOrEmpty(_searchBox.Text))
            {
                _searchBox.Text = "";
            }
            else if (_searchResultsPanel != null && _searchResultsPanel.Children.Count == 0 && _searchTilePool.Count > 0)
            {
                UpdateSearchResults();
            }
            if (_searchSmoothScroll != null) _searchSmoothScroll.ResetToTop();
            else if (_searchScrollViewer != null) _searchScrollViewer.ScrollToTop();

            double u = ScaleUnit;
            double inSlide = Math.Round(28 * u);
            double resSlide = Math.Round(42 * u);

            _searchOverlayActive = true;
            _searchOverlay.IsHitTestVisible = true;
            _searchOverlay.Visibility = Visibility.Visible;

            if (_isPreviewMode)
            {
                _searchBackdrop.Opacity = 1.0;
                if (_searchInputRoot != null) { _searchInputRoot.Opacity = 1.0; _searchInputRoot.CacheMode = null; }
                if (_searchResultsRoot != null) { _searchResultsRoot.Opacity = 1.0; _searchResultsRoot.CacheMode = null; }
                return;
            }

            // Enable HiDPI BitmapCache ONLY for the reveal animation, then drop to null for locked 160Hz typing & scrolling!
            if (_searchInputRoot != null) _searchInputRoot.CacheMode = CreateHiDpiBitmapCache();
            if (_searchResultsRoot != null) _searchResultsRoot.CacheMode = CreateHiDpiBitmapCache();

            _searchBackdrop.BeginAnimation(UIElement.OpacityProperty, null);
            _searchBackdrop.Opacity = 0.0;
            if (_searchInputRoot != null)
            {
                _searchInputRoot.BeginAnimation(UIElement.OpacityProperty, null);
                _searchInputRoot.Opacity = 0.0;
                _searchInputScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                _searchInputScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                _searchInputScale.ScaleX = 0.94;
                _searchInputScale.ScaleY = 0.94;
                _searchInputTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                _searchInputTranslate.Y = inSlide;
            }
            if (_searchResultsRoot != null)
            {
                _searchResultsRoot.BeginAnimation(UIElement.OpacityProperty, null);
                _searchResultsRoot.Opacity = 0.0;
                _searchResultsScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                _searchResultsScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                _searchResultsScale.ScaleX = 0.95;
                _searchResultsScale.ScaleY = 0.95;
                _searchResultsTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                _searchResultsTranslate.Y = resSlide;
            }

            Dispatcher.BeginInvoke(DispatcherPriority.Render, (Action)delegate
            {
                if (token != _searchAnimToken || !_searchOverlayActive) return;

                _searchBackdrop.BeginAnimation(UIElement.OpacityProperty, MakeAnim(0.0, 1.0, 230, _easeOutCubic, 0));
                AnimateBackgroundDepth(true);

                if (_searchInputRoot != null)
                {
                    _searchInputScale.BeginAnimation(ScaleTransform.ScaleXProperty, MakeAnim(0.94, 1.0, 250, _easeOutExpo, 0));
                    _searchInputScale.BeginAnimation(ScaleTransform.ScaleYProperty, MakeAnim(0.94, 1.0, 250, _easeOutExpo, 0));
                    _searchInputTranslate.BeginAnimation(TranslateTransform.YProperty, MakeAnim(inSlide, 0.0, 250, _easeOutExpo, 0));
                    _searchInputRoot.BeginAnimation(UIElement.OpacityProperty, MakeAnim(0.0, 1.0, 180, _easeOutCubic, 0));
                }
                if (_searchResultsRoot != null)
                {
                    DoubleAnimation resAnim = MakeAnim(resSlide, 0.0, 270, _easeOutExpo, 20);
                    resAnim.Completed += delegate
                    {
                        if (token != _searchAnimToken || !_searchOverlayActive) return;
                        if (_searchInputRoot != null) _searchInputRoot.CacheMode = null;
                        if (_searchResultsRoot != null) _searchResultsRoot.CacheMode = null;
                        IntPtr hwnd = new WindowInteropHelper(this).Handle;
                        DesktopAttachment.BringToFrontForOverlay(hwnd);
                        Activate();
                        if (_searchBox != null)
                        {
                            _searchBox.Focus();
                            Keyboard.Focus(_searchBox);
                        }
                    };
                    _searchResultsScale.BeginAnimation(ScaleTransform.ScaleXProperty, MakeAnim(0.95, 1.0, 270, _easeOutExpo, 20));
                    _searchResultsScale.BeginAnimation(ScaleTransform.ScaleYProperty, MakeAnim(0.95, 1.0, 270, _easeOutExpo, 20));
                    _searchResultsTranslate.BeginAnimation(TranslateTransform.YProperty, resAnim);
                    _searchResultsRoot.BeginAnimation(UIElement.OpacityProperty, MakeAnim(0.0, 1.0, 200, _easeOutCubic, 20));
                }
            });
        }

        private void UpdateSearchResults()
        {
            if (_searchResultsPanel == null || _workspace == null) return;
            _searchResultsPanel.Children.Clear();
            _currentSearchMatches.Clear();
            _visibleSearchTiles.Clear();

            string query = (_searchBox != null ? _searchBox.Text : "").Trim();
            bool hasQuery = !string.IsNullOrEmpty(query);

            List<KeyValuePair<int, SearchTileItem>> scored = new List<KeyValuePair<int, SearchTileItem>>(_searchTilePool.Count);
            for (int i = 0; i < _searchTilePool.Count; i++)
            {
                SearchTileItem entry = _searchTilePool[i];
                int score = hasQuery ? SearchMatcher.ScoreMatch(entry.App.DisplayName, entry.App.FolderDisplayName, query) : 1;
                if (score > 0)
                {
                    scored.Add(new KeyValuePair<int, SearchTileItem>(score, entry));
                }
            }

            if (hasQuery)
            {
                scored.Sort(delegate(KeyValuePair<int, SearchTileItem> a, KeyValuePair<int, SearchTileItem> b)
                {
                    return b.Key.CompareTo(a.Key);
                });
            }

            for (int i = 0; i < scored.Count; i++)
            {
                SearchTileItem entry = scored[i].Value;
                _currentSearchMatches.Add(entry.App);
                _visibleSearchTiles.Add(entry);
                _searchResultsPanel.Children.Add(entry.Cell);
            }

            _searchSelectedIndex = 0;
            _searchHasSelectionHighlight = hasQuery && (_visibleSearchTiles.Count > 0);
            ApplySearchSelectionVisuals(_searchHasSelectionHighlight);

            bool hasOverflow = false;
            if (_searchScrollViewer != null && _searchAvailViewportH > 40)
            {
                int count = _visibleSearchTiles.Count;
                if (count > 0)
                {
                    int cols = Math.Max(1, _searchColumns);
                    int totalRows = (int)Math.Ceiling((double)count / cols);
                    _searchResultsPanel.Measure(new Size(Math.Max(200, _searchInnerPanelW), double.PositiveInfinity));
                    double measuredH = _searchResultsPanel.DesiredSize.Height;
                    double exactRowH = (totalRows > 0 && measuredH > 10) ? (measuredH / totalRows) : 0;
                    if (exactRowH > 10)
                    {
                        int maxFitRows = Math.Max(1, (int)Math.Floor(_searchAvailViewportH / exactRowH));
                        if (totalRows > maxFitRows)
                        {
                            // Snap scroll viewport strictly to exact integer complete rows — ZERO sliced glyphs at scroll=0!
                            _searchScrollViewer.MaxHeight = Math.Floor(maxFitRows * exactRowH);
                            hasOverflow = true;
                        }
                        else
                        {
                            _searchScrollViewer.MaxHeight = _searchAvailViewportH;
                        }
                    }
                    else
                    {
                        _searchScrollViewer.MaxHeight = _searchAvailViewportH;
                    }
                }
                else
                {
                    _searchScrollViewer.MaxHeight = _searchAvailViewportH;
                }
            }

            if (_searchFooterHint != null)
            {
                string countPrefix = hasQuery
                    ? string.Format("匹配 {0} 项  ·  ", _currentSearchMatches.Count)
                    : string.Format("共 {0} 项  ·  ", _currentSearchMatches.Count);
                _searchFooterHint.Text = countPrefix + "↑ ↓ ← → 选择  ·  Enter 启动  ·  Shift+Enter 多开  ·  Esc 关闭";
            }

            if (_searchEmptyHint != null)
            {
                _searchEmptyHint.Visibility = (_currentSearchMatches.Count == 0) ? Visibility.Visible : Visibility.Collapsed;
            }
            if (_searchSmoothScroll != null) _searchSmoothScroll.ResetToTop();
            UpdateSearchScrollFades(hasOverflow);
        }

        private void UpdateSearchScrollFades()
        {
            UpdateSearchScrollFades(false);
        }

        private void UpdateSearchScrollFades(bool knownOverflow)
        {
            if (_searchScrollViewer == null) return;
            double scrollable = _searchScrollViewer.ScrollableHeight;
            double offset = _searchScrollViewer.VerticalOffset;
            bool showTop = scrollable > 1.5 && offset > 2.0;
            bool showBottom = (scrollable > 1.5 && offset < scrollable - 2.0) || (scrollable <= 0.1 && knownOverflow);
            if (_searchTopFade != null)
            {
                _searchTopFade.Visibility = showTop ? Visibility.Visible : Visibility.Collapsed;
            }
            if (_searchBottomFade != null)
            {
                _searchBottomFade.Visibility = showBottom ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void MoveSearchSelection(int delta)
        {
            if (_visibleSearchTiles.Count == 0) return;
            if (!_searchHasSelectionHighlight)
            {
                _searchSelectedIndex = (delta < 0) ? (_visibleSearchTiles.Count - 1) : 0;
                _searchHasSelectionHighlight = true;
            }
            else
            {
                int next = _searchSelectedIndex + delta;
                if (Math.Abs(delta) <= 1)
                {
                    if (next < 0) next = _visibleSearchTiles.Count - 1;
                    else if (next >= _visibleSearchTiles.Count) next = 0;
                }
                else
                {
                    if (next < 0) next = 0;
                    else if (next >= _visibleSearchTiles.Count) next = _visibleSearchTiles.Count - 1;
                }
                _searchSelectedIndex = next;
            }
            ApplySearchSelectionVisuals(true);
            if (_searchSmoothScroll != null) _searchSmoothScroll.Stop();
            try { _visibleSearchTiles[_searchSelectedIndex].Cell.BringIntoView(); } catch { }
        }

        private void ApplySearchSelectionVisuals(bool highlightSelected)
        {
            for (int i = 0; i < _visibleSearchTiles.Count; i++)
            {
                bool active = highlightSelected && (i == _searchSelectedIndex);
                _visibleSearchTiles[i].IsTopHit = active;
                _visibleSearchTiles[i].Highlight.Background = active ? (Brush)_brushTopHitTile : Brushes.Transparent;
                _visibleSearchTiles[i].Highlight.BorderBrush = active ? (Brush)_brushTopHitBorder : Brushes.Transparent;
            }
        }

        private void CloseSearchOverlay(bool immediate)
        {
            int token = ++_searchAnimToken;
            bool wasActive = _searchOverlayActive;
            _searchOverlayActive = false;

            if (immediate || _isPreviewMode || !wasActive)
            {
                _searchOverlay.Visibility = Visibility.Hidden;
                _searchOverlay.IsHitTestVisible = false;
                _searchBackdrop.BeginAnimation(UIElement.OpacityProperty, null);
                _searchBackdrop.Opacity = 0;
                if (!_modalOverlayActive)
                {
                    AnimateBackgroundDepth(false);
                }
                if (DesktopAttachment.AllowTemporaryFront)
                {
                    IntPtr h = new WindowInteropHelper(this).Handle;
                    DesktopAttachment.SendToBottom(h);
                }
                return;
            }

            double u = ScaleUnit;
            _searchOverlay.IsHitTestVisible = false;
            AnimateBackgroundDepth(false);

            if (_searchInputRoot != null) _searchInputRoot.CacheMode = CreateHiDpiBitmapCache();
            if (_searchResultsRoot != null) _searchResultsRoot.CacheMode = CreateHiDpiBitmapCache();

            DoubleAnimation fadeBack = MakeAnim(null, 0.0, 195, _easeOutCubic, 0);
            fadeBack.Completed += delegate
            {
                if (token != _searchAnimToken) return;
                if (_searchInputRoot != null) _searchInputRoot.CacheMode = null;
                if (_searchResultsRoot != null) _searchResultsRoot.CacheMode = null;
                _searchOverlay.Visibility = Visibility.Hidden;
                _searchOverlay.IsHitTestVisible = false;
                if (DesktopAttachment.AllowTemporaryFront)
                {
                    IntPtr h = new WindowInteropHelper(this).Handle;
                    DesktopAttachment.SendToBottom(h);
                }
            };
            _searchBackdrop.BeginAnimation(UIElement.OpacityProperty, fadeBack);

            if (_searchInputRoot != null)
            {
                _searchInputScale.BeginAnimation(ScaleTransform.ScaleXProperty, MakeAnim(null, 0.94, 180, _easeOutCubic, 0));
                _searchInputScale.BeginAnimation(ScaleTransform.ScaleYProperty, MakeAnim(null, 0.94, 180, _easeOutCubic, 0));
                _searchInputTranslate.BeginAnimation(TranslateTransform.YProperty, MakeAnim(null, Math.Round(24 * u), 180, _easeOutCubic, 0));
                _searchInputRoot.BeginAnimation(UIElement.OpacityProperty, MakeAnim(null, 0.0, 160, _easeOutCubic, 0));
            }
            if (_searchResultsRoot != null)
            {
                _searchResultsScale.BeginAnimation(ScaleTransform.ScaleXProperty, MakeAnim(null, 0.95, 180, _easeOutCubic, 0));
                _searchResultsScale.BeginAnimation(ScaleTransform.ScaleYProperty, MakeAnim(null, 0.95, 180, _easeOutCubic, 0));
                _searchResultsTranslate.BeginAnimation(TranslateTransform.YProperty, MakeAnim(null, Math.Round(36 * u), 180, _easeOutCubic, 0));
                _searchResultsRoot.BeginAnimation(UIElement.OpacityProperty, MakeAnim(null, 0.0, 160, _easeOutCubic, 0));
            }
        }

        private static string FindEverythingPath()
        {
            string[] candidates = new string[]
            {
                @"C:\Program Files\Everything\Everything.exe",
                @"C:\Program Files (x86)\Everything\Everything.exe",
                @"C:\Program Files\Everything 1.5a\Everything64.exe"
            };
            for (int i = 0; i < candidates.Length; i++)
            {
                if (File.Exists(candidates[i])) return candidates[i];
            }
            return null;
        }

        private void LaunchEverythingSearch(string query)
        {
            string exe = FindEverythingPath();
            if (!string.IsNullOrEmpty(exe))
            {
                try
                {
                    string args = string.IsNullOrWhiteSpace(query) ? "" : ("-s \"" + query.Replace("\"", "") + "\"");
                    Process.Start(exe, args);
                    return;
                }
                catch { }
            }

            // Graceful Windows native Explorer Search fallback when Everything is not installed
            try
            {
                string q = (query ?? "").Trim();
                if (!string.IsNullOrEmpty(q))
                {
                    Process.Start("search-ms:query=" + Uri.EscapeDataString(q));
                }
                else
                {
                    Process.Start("explorer.exe", "shell:MyComputerFolder");
                }
            }
            catch
            {
            }
        }

        private ContextMenu BuildCardContextMenu(FolderGroup group)
        {
            ContextMenu cm = CreateDarkContextMenu();

            cm.Items.Add(CreateMenuItem("展开「" + group.DisplayName + "」", delegate { OpenFolderModal(group); }));
            cm.Items.Add(CreateMenuItem("重命名此分类", delegate { OpenRenameCategoryDialog(group); }));
            int folderIdx = -1;
            int folderCount = (_workspace != null && _workspace.Folders != null) ? _workspace.Folders.Count : 0;
            for (int fi = 0; fi < folderCount; fi++)
            {
                if (string.Equals(_workspace.Folders[fi].FolderName, group.FolderName, StringComparison.OrdinalIgnoreCase))
                {
                    folderIdx = fi;
                    break;
                }
            }
            MenuItem moveCardEarlier = CreateMenuItem("卡片向前移动一位", delegate
            {
                FolderScanner.MoveFolderOrder(_config, _workspace, group, -1);
                ReloadWorkspace();
            });
            moveCardEarlier.IsEnabled = (folderIdx > 0);
            cm.Items.Add(moveCardEarlier);

            MenuItem moveCardLater = CreateMenuItem("卡片向后移动一位", delegate
            {
                FolderScanner.MoveFolderOrder(_config, _workspace, group, 1);
                ReloadWorkspace();
            });
            moveCardLater.IsEnabled = (folderIdx >= 0 && folderIdx < folderCount - 1);
            cm.Items.Add(moveCardLater);
            cm.Items.Add(CreateMenuItem("在资源管理器中打开此分类文件夹", delegate
            {
                try { Process.Start("explorer.exe", "\"" + group.FolderPath + "\""); } catch { }
            }));

            string mode = GetFolderGridMode(group.FolderName);
            MenuItem gridModeMenu = CreateMenuItem("单卡片宫格模式", null);
            gridModeMenu.Items.Add(CreateMenuItem((mode == "auto" ? "✓ " : "   ") + "智能自动 (≤7 四宫格 / >7 九宫格)", delegate { SetFolderGridMode(group, "auto"); }));
            gridModeMenu.Items.Add(CreateMenuItem((mode == "2x2" ? "✓ " : "   ") + "固定四宫格 (2×2)", delegate { SetFolderGridMode(group, "2x2"); }));
            gridModeMenu.Items.Add(CreateMenuItem((mode == "3x3" ? "✓ " : "   ") + "固定九宫格 (3×3)", delegate { SetFolderGridMode(group, "3x3"); }));
            cm.Items.Add(gridModeMenu);

            cm.Items.Add(CreateSeparator());
            AppendCommonMenuItems(cm);
            return cm;
        }

        private ContextMenu BuildDesktopContextMenu()
        {
            ContextMenu cm = CreateDarkContextMenu();
            AppendCommonMenuItems(cm);
            return cm;
        }

        private void AppendCommonMenuItems(ContextMenu cm)
        {
            cm.Items.Add(CreateMenuItem("新建分类大文件夹", delegate { OpenNewCategoryDialog(); }));
            cm.Items.Add(CreateMenuItem("搜索应用", delegate { OpenSearchOverlay(); }));
            cm.Items.Add(CreateMenuItem("刷新图标与壁纸", delegate { RefreshAll(); }));
            cm.Items.Add(CreateMenuItem("打开图标管理总目录 (data)", delegate { OpenDataFolder(); }));
            cm.Items.Add(CreateSeparator());

            string lm = (_config.LayoutMode ?? "auto").ToLowerInvariant();
            bool landMenu = IsLandscape;
            MenuItem layoutMenu = CreateMenuItem("桌面网格布局 (" + GetLayoutModeLabel(lm) + ")", null);
            layoutMenu.Items.Add(CreateMenuItem((lm == "auto" ? "✓ " : "   ") + (landMenu ? "智能自适应 (横屏双栏 / 自动扩展)" : "智能自适应 (竖屏 6→8→12格)"), delegate { SetLayoutMode("auto"); }));
            layoutMenu.Items.Add(CreateMenuItem(((lm == "2x3" || lm == "3x2") ? "✓ " : "   ") + (landMenu ? "固定 3×2 (6格/页，超出自动分页)" : "固定 2×3 (6格/页，超出自动分页)"), delegate { SetLayoutMode(landMenu ? "3x2" : "2x3"); }));
            layoutMenu.Items.Add(CreateMenuItem(((lm == "2x4" || lm == "4x2") ? "✓ " : "   ") + (landMenu ? "固定 4×2 (8格/页，超出自动分页)" : "固定 2×4 (8格/页，超出自动分页)"), delegate { SetLayoutMode(landMenu ? "4x2" : "2x4"); }));
            layoutMenu.Items.Add(CreateMenuItem(((lm == "3x4" || lm == "4x3") ? "✓ " : "   ") + (landMenu ? "固定 4×3 (12格/页，超出自动分页)" : "固定 3×4 (12格/页，超出自动分页)"), delegate { SetLayoutMode(landMenu ? "4x3" : "3x4"); }));
            layoutMenu.Items.Add(CreateMenuItem(((lm == "3x5" || lm == "5x3") ? "✓ " : "   ") + (landMenu ? "固定 5×3 (15格/页，超出自动分页)" : "固定 3×5 (15格/页，超出自动分页)"), delegate { SetLayoutMode(landMenu ? "5x3" : "3x5"); }));
            cm.Items.Add(layoutMenu);

            MenuItem widgetMenu = CreateMenuItem("桌面小部件 (" + (_config.ShowWidgets ? "已开启" : "已隐藏") + ")", null);
            widgetMenu.Items.Add(CreateMenuItem((_config.ShowWidgets ? "✓ " : "   ") + "显示桌面小部件区", delegate { ToggleWidgets(); }));
            widgetMenu.Items.Add(CreateSeparator());
            widgetMenu.Items.Add(CreateMenuItem((IsWidgetEnabled("mail") ? "✓ " : "   ") + "邮箱收件箱 (Gmail / IMAP)", delegate { ToggleSingleWidget("mail"); }));
            widgetMenu.Items.Add(CreateMenuItem((IsWidgetEnabled("clash") ? "✓ " : "   ") + "代理状态与实时网速 (Clash Verge)", delegate { ToggleSingleWidget("clash"); }));
            widgetMenu.Items.Add(CreateMenuItem((IsWidgetEnabled("music") ? "✓ " : "   ") + "音乐播放与实时歌词 (SMTC)", delegate { ToggleSingleWidget("music"); }));
            widgetMenu.Items.Add(CreateMenuItem((IsWidgetEnabled("sys_todo") ? "✓ " : "   ") + "系统监控与快捷待办", delegate { ToggleSingleWidget("sys_todo"); }));
            widgetMenu.Items.Add(CreateSeparator());
            widgetMenu.Items.Add(CreateMenuItem("配置邮箱收件箱 (Gmail / IMAP)...", delegate { OpenMailConfigDialog(); }));
            widgetMenu.Items.Add(CreateMenuItem("添加快捷待办...", delegate { OpenAddTodoDialog(); }));
            cm.Items.Add(widgetMenu);

            bool hidden = DesktopAttachment.AreNativeDesktopIconsHidden();
            cm.Items.Add(CreateMenuItem(hidden ? "显示 Windows 原生桌面图标" : "隐藏 Windows 原生桌面图标", delegate
            {
                DesktopAttachment.ToggleNativeDesktopIcons();
            }));

            cm.Items.Add(CreateMenuItem(_config.ShowTopClock ? "隐藏顶部极简时钟" : "显示顶部极简时钟", delegate
            {
                ToggleTopClock();
            }));

            MenuItem glassMenu = CreateMenuItem("卡片磨砂深浅", null);
            double op = _config.CardTintOpacity;
            glassMenu.Items.Add(CreateMenuItem((Math.Abs(op - 0.36) < 0.04 ? "✓ " : "   ") + "冰晶通透 (36%)", delegate { SetGlassPreset(0.36); }));
            glassMenu.Items.Add(CreateMenuItem((Math.Abs(op - 0.48) < 0.04 ? "✓ " : "   ") + "通透轻盈 (48%)", delegate { SetGlassPreset(0.48); }));
            glassMenu.Items.Add(CreateMenuItem((Math.Abs(op - 0.62) < 0.04 ? "✓ " : "   ") + "标准深邃 (62%)", delegate { SetGlassPreset(0.62); }));
            glassMenu.Items.Add(CreateMenuItem((Math.Abs(op - 0.76) < 0.04 ? "✓ " : "   ") + "浓郁暗色 (76%)", delegate { SetGlassPreset(0.76); }));
            cm.Items.Add(glassMenu);

            bool autoStart = DesktopAttachment.IsAutoStartEnabled();
            cm.Items.Add(CreateMenuItem(autoStart ? "✓ 已开启开机自启 (点击关闭)" : "开启开机自启", delegate
            {
                DesktopAttachment.SetAutoStart(!autoStart);
            }));

            cm.Items.Add(CreateSeparator());
            cm.Items.Add(CreateMenuItem("退出 LumeDesk", delegate
            {
                if (RequestExit != null) RequestExit();
                else Close();
            }));
        }

        private ContextMenu BuildAppContextMenu(AppItem item, FolderGroup group, List<AppItem> ownerList)
        {
            ContextMenu cm = CreateDarkContextMenu();
            bool isDock = string.Equals(item.FolderKey, "_Dock", StringComparison.OrdinalIgnoreCase);

            Action applyReorder = delegate
            {
                if (!isDock && _openFolder != null && _modalOverlayActive && group == _openFolder)
                {
                    RefreshOpenModalInPlace();
                }
                else
                {
                    ReloadWorkspace();
                }
            };

            cm.Items.Add(CreateMenuItem("打开 / 唤起 " + item.DisplayName, delegate { FolderScanner.LaunchItem(item, false); }));
            cm.Items.Add(CreateMenuItem("强制新开窗口启动 (多开)", delegate { FolderScanner.LaunchItem(item, true); }));
            cm.Items.Add(CreateSeparator());

            int itemIdx = -1;
            int listCount = (ownerList != null) ? ownerList.Count : 0;
            for (int li = 0; li < listCount; li++)
            {
                if (string.Equals(ownerList[li].FileName, item.FileName, StringComparison.OrdinalIgnoreCase))
                {
                    itemIdx = li;
                    break;
                }
            }
            bool canMoveEarlier = (itemIdx > 0);
            bool canMoveLater = (itemIdx >= 0 && itemIdx < listCount - 1);

            MenuItem pinFirstMi = CreateMenuItem(isDock ? "移到 Dock 最左侧" : "置顶到大图标第 1 位", delegate
            {
                FolderScanner.ReorderItem(_config, group, ownerList, item, 0, true);
                applyReorder();
            });
            pinFirstMi.IsEnabled = canMoveEarlier;
            cm.Items.Add(pinFirstMi);

            MenuItem moveEarlierMi = CreateMenuItem("向前移动一位", delegate
            {
                FolderScanner.ReorderItem(_config, group, ownerList, item, -1, false);
                applyReorder();
            });
            moveEarlierMi.IsEnabled = canMoveEarlier;
            cm.Items.Add(moveEarlierMi);

            MenuItem moveLaterMi = CreateMenuItem("向后移动一位", delegate
            {
                FolderScanner.ReorderItem(_config, group, ownerList, item, 1, false);
                applyReorder();
            });
            moveLaterMi.IsEnabled = canMoveLater;
            cm.Items.Add(moveLaterMi);

            if (!isDock && _workspace != null && _workspace.DockItems != null)
            {
                AppItem pinnedDockCopy = null;
                for (int di = 0; di < _workspace.DockItems.Count; di++)
                {
                    if (string.Equals(_workspace.DockItems[di].DisplayName, item.DisplayName, StringComparison.OrdinalIgnoreCase))
                    {
                        pinnedDockCopy = _workspace.DockItems[di];
                        break;
                    }
                }
                if (pinnedDockCopy == null)
                {
                    cm.Items.Add(CreateMenuItem("固定到底部 Dock", delegate
                    {
                        if (FolderScanner.CopyItemToDock(_config, item))
                        {
                            ReloadWorkspace();
                        }
                    }));
                }
                else
                {
                    AppItem capturedDockCopy = pinnedDockCopy;
                    cm.Items.Add(CreateMenuItem("从底部 Dock 取消固定", delegate
                    {
                        FolderScanner.MoveToTrash(_config, capturedDockCopy);
                        ReloadWorkspace();
                    }));
                }
            }

            string curBg = "auto";
            string bgOv;
            if (_config.IconBgOverrides != null &&
                _config.IconBgOverrides.TryGetValue(item.DisplayName, out bgOv) &&
                !string.IsNullOrEmpty(bgOv))
            {
                curBg = bgOv.ToLowerInvariant();
            }

            MenuItem bgMenu = CreateMenuItem("图标底色风格", null);
            bgMenu.Items.Add(CreateMenuItem((curBg == "auto" ? "✓ " : "   ") + "自动识别", delegate { SetIconBgOverride(item, "auto"); }));
            bgMenu.Items.Add(CreateMenuItem((curBg == "light" ? "✓ " : "   ") + "纯白圆角底", delegate { SetIconBgOverride(item, "light"); }));
            bgMenu.Items.Add(CreateMenuItem((curBg == "dark" ? "✓ " : "   ") + "深黑圆角底", delegate { SetIconBgOverride(item, "dark"); }));
            bgMenu.Items.Add(CreateMenuItem((curBg == "raw" ? "✓ " : "   ") + "透明原图", delegate { SetIconBgOverride(item, "raw"); }));
            cm.Items.Add(bgMenu);

            cm.Items.Add(CreateSeparator());
            cm.Items.Add(CreateMenuItem("打开程序所在目录", delegate { FolderScanner.OpenContainingTargetFolder(item); }));
            cm.Items.Add(CreateMenuItem("在分类文件夹中定位", delegate
            {
                try { Process.Start("explorer.exe", "/select,\"" + item.FilePath + "\""); } catch { }
            }));
            cm.Items.Add(CreateMenuItem(isDock ? "从 Dock 移除 (移入 _Trash)" : "从此分类移除 (移入 _Trash)", delegate
            {
                FolderScanner.MoveToTrash(_config, item);
                ReloadWorkspace();
            }));

            return cm;
        }

        private void SetIconBgOverride(AppItem item, string mode)
        {
            if (mode == "auto")
            {
                _config.IconBgOverrides.Remove(item.DisplayName);
            }
            else
            {
                _config.IconBgOverrides[item.DisplayName] = mode;
            }
            ConfigManager.Save(_config);
            IconExtractor.ClearCache();
            ReloadWorkspace();
        }

        private ContextMenu CreateDarkContextMenu()
        {
            ContextMenu cm = new ContextMenu();
            cm.HasDropShadow = true;
            double availH = ActualHeight > 100 ? ActualHeight : SystemParameters.WorkArea.Height;
            cm.MaxHeight = Math.Max(320.0, Math.Round(availH * 0.72));
            if (_thinScrollBarStyle != null)
            {
                cm.Resources.Add(typeof(ScrollBar), _thinScrollBarStyle);
            }

            ControlTemplate tpl = new ControlTemplate(typeof(ContextMenu));
            FrameworkElementFactory bd = new FrameworkElementFactory(typeof(Border));
            bd.SetValue(Border.BackgroundProperty, CreateFrozenBrush(245, 20, 27, 42));
            bd.SetValue(Border.BorderBrushProperty, CreateFrozenBrush(68, 255, 255, 255));
            bd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(14));
            bd.SetValue(Border.PaddingProperty, new Thickness(0, 6, 0, 6));

            FrameworkElementFactory sv = new FrameworkElementFactory(typeof(ScrollViewer));
            sv.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
            sv.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
            sv.SetValue(ScrollViewer.CanContentScrollProperty, false);

            FrameworkElementFactory ip = new FrameworkElementFactory(typeof(ItemsPresenter));
            ip.SetValue(KeyboardNavigation.DirectionalNavigationProperty, KeyboardNavigationMode.Cycle);
            sv.AppendChild(ip);
            bd.AppendChild(sv);
            tpl.VisualTree = bd;
            cm.Template = tpl;
            return cm;
        }

        private MenuItem CreateMenuItem(string header, Action onClick)
        {
            MenuItem mi = new MenuItem();
            mi.Header = header;
            mi.Style = _darkMenuItemStyle;
            if (onClick != null)
            {
                mi.Click += delegate { onClick(); };
            }
            return mi;
        }

        private Separator CreateSeparator()
        {
            Separator s = new Separator();
            s.Style = _darkSeparatorStyle;
            return s;
        }

        private Button CreatePillButton(string text, double u)
        {
            SolidColorBrush idleBg = CreateFrozenBrush(40, 255, 255, 255);
            SolidColorBrush hoverBg = CreateFrozenBrush(72, 255, 255, 255);
            SolidColorBrush idleBorder = CreateFrozenBrush(72, 255, 255, 255);
            SolidColorBrush hoverBorder = CreateFrozenBrush(128, 255, 255, 255);

            Button btn = new Button();
            btn.Content = text;
            btn.FontFamily = FontText;
            btn.FontSize = Math.Round(17.5 * u);
            btn.FontWeight = FontWeights.Medium;
            btn.Foreground = CreateFrozenBrush(248, 246, 250, 255);
            btn.Padding = new Thickness(Math.Round(20 * u), Math.Round(9 * u), Math.Round(20 * u), Math.Round(9 * u));
            btn.Cursor = Cursors.Hand;
            btn.Background = idleBg;
            btn.BorderBrush = idleBorder;
            btn.BorderThickness = new Thickness(Math.Max(1.0, Math.Round(1.1 * u * 10) / 10.0));
            btn.RenderTransformOrigin = new Point(0.5, 0.5);
            ScaleTransform st = new ScaleTransform(1.0, 1.0);
            btn.RenderTransform = st;

            ControlTemplate tpl = new ControlTemplate(typeof(Button));
            FrameworkElementFactory bd = new FrameworkElementFactory(typeof(Border));
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(Math.Round(20 * u)));
            bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
            bd.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
            bd.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
            bd.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Button.PaddingProperty));

            FrameworkElementFactory cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);
            tpl.VisualTree = bd;
            btn.Template = tpl;

            btn.MouseEnter += delegate
            {
                if (btn.Background == idleBg)
                {
                    btn.Background = hoverBg;
                    if (btn.BorderBrush == idleBorder) btn.BorderBrush = hoverBorder;
                }
                else
                {
                    btn.Opacity = 0.92;
                }
                AnimateScale(st, 1.03, 110);
            };
            btn.MouseLeave += delegate
            {
                if (btn.Background == hoverBg)
                {
                    btn.Background = idleBg;
                }
                if (btn.BorderBrush == hoverBorder)
                {
                    btn.BorderBrush = idleBorder;
                }
                btn.Opacity = 1.0;
                AnimateScale(st, 1.0, 110);
            };
            btn.PreviewMouseLeftButtonDown += delegate { AnimateScale(st, 0.95, 65); };
            btn.PreviewMouseLeftButtonUp += delegate { AnimateScale(st, 1.02, 100); };
            return btn;
        }

        private ToolTip CreateDarkToolTip(string text, double u)
        {
            ToolTip tt = new ToolTip();
            tt.Content = text;
            tt.Background = Brushes.Transparent;
            tt.BorderThickness = new Thickness(0);
            tt.HasDropShadow = true;

            ControlTemplate tpl = new ControlTemplate(typeof(ToolTip));
            FrameworkElementFactory bd = new FrameworkElementFactory(typeof(Border));
            bd.SetValue(Border.BackgroundProperty, CreateFrozenBrush(244, 15, 22, 37));
            bd.SetValue(Border.BorderBrushProperty, CreateFrozenBrush(82, 255, 255, 255));
            bd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(Math.Round(10 * u)));
            bd.SetValue(Border.PaddingProperty, new Thickness(Math.Round(12 * u), Math.Round(6 * u), Math.Round(12 * u), Math.Round(6 * u)));

            FrameworkElementFactory cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(TextBlock.ForegroundProperty, Brushes.White);
            cp.SetValue(TextBlock.FontFamilyProperty, FontText);
            cp.SetValue(TextBlock.FontSizeProperty, Math.Round(14 * u));
            cp.SetValue(TextBlock.FontWeightProperty, FontWeights.Medium);
            bd.AppendChild(cp);
            tpl.VisualTree = bd;
            tt.Template = tpl;
            return tt;
        }

        private DropShadowEffect CreateTightTitleHalo(double u)
        {
            return GetFrozenShadowEffect(
                Color.FromRgb(6, 12, 24),
                Math.Round(7.5 * u),
                Math.Max(0.5, Math.Round(0.8 * u * 10) / 10.0),
                270,
                0.72);
        }

        private DropShadowEffect CreateTextShadow(double u)
        {
            return GetFrozenShadowEffect(
                Color.FromRgb(4, 10, 22),
                Math.Round(18 * u),
                Math.Round(2.5 * u),
                270,
                0.62);
        }

        private void ShakeHorizontal(TranslateTransform tt, double u)
        {
            if (tt == null || _isPreviewMode) return;
            double amp = Math.Round(8 * u);
            DoubleAnimationUsingKeyFrames kf = new DoubleAnimationUsingKeyFrames();
            kf.Duration = TimeSpan.FromMilliseconds(260);
            kf.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(0))));
            kf.KeyFrames.Add(new LinearDoubleKeyFrame(-amp, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(45))));
            kf.KeyFrames.Add(new LinearDoubleKeyFrame(amp, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(100))));
            kf.KeyFrames.Add(new LinearDoubleKeyFrame(-amp * 0.6, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(155))));
            kf.KeyFrames.Add(new LinearDoubleKeyFrame(amp * 0.4, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(205))));
            kf.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(260))));
            Timeline.SetDesiredFrameRate(kf, AnimFps);
            tt.BeginAnimation(TranslateTransform.XProperty, kf);
        }

        private void UpdateClockTexts()
        {
            string t = DateTime.Now.ToString("HH:mm");
            string d = DateTime.Now.ToString("M月d日 dddd");
            if (_clockTimeText != null) _clockTimeText.Text = t;
            if (_clockTimeShadow != null) _clockTimeShadow.Text = t;
            if (_clockDateText != null) _clockDateText.Text = d;
            if (_clockDateShadow != null) _clockDateShadow.Text = d;
        }

        private DoubleAnimation MakeAnim(double? from, double to, int ms, IEasingFunction ease, int beginMs)
        {
            DoubleAnimation anim = new DoubleAnimation();
            if (from.HasValue) anim.From = from.Value;
            anim.To = to;
            anim.Duration = TimeSpan.FromMilliseconds(ms);
            anim.EasingFunction = ease;
            if (beginMs > 0) anim.BeginTime = TimeSpan.FromMilliseconds(beginMs);
            Timeline.SetDesiredFrameRate(anim, AnimFps);
            return anim;
        }

        private void AnimateScale(ScaleTransform st, double target, int ms)
        {
            if (_isPreviewMode)
            {
                st.ScaleX = target;
                st.ScaleY = target;
                return;
            }
            st.BeginAnimation(ScaleTransform.ScaleXProperty, MakeAnim(null, target, ms, _easeOutCubic, 0));
            st.BeginAnimation(ScaleTransform.ScaleYProperty, MakeAnim(null, target, ms, _easeOutCubic, 0));
        }

        private void AnimateTranslateY(TranslateTransform tt, double targetY, int ms)
        {
            if (_isPreviewMode)
            {
                tt.Y = targetY;
                return;
            }
            tt.BeginAnimation(TranslateTransform.YProperty, MakeAnim(null, targetY, ms, _easeOutCubic, 0));
        }
    }
}
