using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace LumeDesk
{
    public class App : Application
    {
        private const string SingleInstanceMutexName = "LumeDesk_SingleInstance_Mutex_v1";
        private const string WakeEventName = "LumeDesk_Wake_Event_v1";
        private static Mutex _instanceMutex;
        private static readonly object LogLock = new object();
        private MainWindow _mainWindow;
        private WinForms.NotifyIcon _trayIcon;
        private AppConfig _config;
        private EventWaitHandle _wakeEvent;
        private Thread _wakeThread;
        private volatile bool _shuttingDown;
        private DateTime _uiFaultWindowStart = DateTime.MinValue;
        private int _uiFaultBurstCount = 0;

        [STAThread]
        public static void Main(string[] args)
        {
            MonitorHelper.EnablePerMonitorDpiV2();

            // Self-verification offline screenshot rendering mode
            if (args != null && args.Length >= 2 &&
                (args[0].Equals("--render-preview", StringComparison.OrdinalIgnoreCase) ||
                 args[0].Equals("--render-modal", StringComparison.OrdinalIgnoreCase) ||
                 args[0].Equals("--render-search", StringComparison.OrdinalIgnoreCase) ||
                 args[0].Equals("--render-landscape", StringComparison.OrdinalIgnoreCase) ||
                 args[0].Equals("--render-landscape-modal", StringComparison.OrdinalIgnoreCase) ||
                 args[0].Equals("--render-landscape-search", StringComparison.OrdinalIgnoreCase) ||
                 args[0].Equals("--render-landscape-nowidget", StringComparison.OrdinalIgnoreCase) ||
                 args[0].Equals("--render-landscape-laptop", StringComparison.OrdinalIgnoreCase) ||
                 args[0].Equals("--render-portrait-4k", StringComparison.OrdinalIgnoreCase) ||
                 args[0].Equals("--render-portrait-search", StringComparison.OrdinalIgnoreCase) ||
                 args[0].Equals("--render-demo", StringComparison.OrdinalIgnoreCase)))
            {
                string mode = "home";
                string outPng = args[1];
                if (args[0].Equals("--render-modal", StringComparison.OrdinalIgnoreCase)) mode = "modal";
                else if (args[0].Equals("--render-search", StringComparison.OrdinalIgnoreCase)) mode = "search";
                else if (args[0].Equals("--render-landscape", StringComparison.OrdinalIgnoreCase)) mode = "landscape";
                else if (args[0].Equals("--render-landscape-modal", StringComparison.OrdinalIgnoreCase)) mode = "landscape-modal";
                else if (args[0].Equals("--render-landscape-search", StringComparison.OrdinalIgnoreCase)) mode = "landscape-search";
                else if (args[0].Equals("--render-landscape-nowidget", StringComparison.OrdinalIgnoreCase)) mode = "landscape-nowidget";
                else if (args[0].Equals("--render-landscape-laptop", StringComparison.OrdinalIgnoreCase)) mode = "landscape-laptop";
                else if (args[0].Equals("--render-portrait-4k", StringComparison.OrdinalIgnoreCase)) mode = "portrait-4k";
                else if (args[0].Equals("--render-portrait-search", StringComparison.OrdinalIgnoreCase)) mode = "portrait-search";
                else if (args[0].Equals("--render-demo", StringComparison.OrdinalIgnoreCase) && args.Length >= 3)
                {
                    mode = args[1];
                    outPng = args[2];
                }

                App previewApp = new App();
                previewApp.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                AppConfig cfg = ConfigManager.Load();
                MainWindow w = new MainWindow(cfg, true);
                w.RenderPreviewToPng(outPng, mode);
                return;
            }

            bool createdNew;
            _instanceMutex = new Mutex(true, SingleInstanceMutexName, out createdNew);
            if (!createdNew)
            {
                // Signal already-running instance to verify/re-anchor desktop visibility instead of failing silently
                try
                {
                    using (EventWaitHandle wake = EventWaitHandle.OpenExisting(WakeEventName))
                    {
                        wake.Set();
                    }
                }
                catch
                {
                }
                return;
            }

            try
            {
                App app = new App();
                app.InstallCrashGuards();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                app.StartInteractive();
                app.Run();
            }
            catch (Exception ex)
            {
                LogCrash("App.Main fatal", ex);
            }
            finally
            {
                try { _instanceMutex.ReleaseMutex(); } catch { }
                try { _instanceMutex.Dispose(); } catch { }
            }
        }

        private void InstallCrashGuards()
        {
            DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e)
            {
                LogCrash("DispatcherUnhandledException", e.Exception);
                DateTime now = DateTime.UtcNow;
                if ((now - _uiFaultWindowStart).TotalSeconds > 3.0)
                {
                    _uiFaultWindowStart = now;
                    _uiFaultBurstCount = 1;
                }
                else
                {
                    _uiFaultBurstCount++;
                }
                if (_uiFaultBurstCount <= 5)
                {
                    e.Handled = true;
                }
            };

            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                LogCrash("AppDomain.UnhandledException", e.ExceptionObject as Exception);
            };

            TaskScheduler.UnobservedTaskException += delegate(object sender, UnobservedTaskExceptionEventArgs e)
            {
                LogCrash("TaskScheduler.UnobservedTaskException", e.Exception);
                e.SetObserved();
            };

            SessionEnding += delegate
            {
                if (_mainWindow != null) _mainWindow.AllowExit();
            };
        }

        internal static void LogCrash(string context, Exception ex)
        {
            try
            {
                lock (LogLock)
                {
                    string logDir = Path.Combine(ConfigManager.BaseDir, "logs");
                    if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir);
                    string logPath = Path.Combine(logDir, "crash.log");
                    if (File.Exists(logPath) && new FileInfo(logPath).Length > 512 * 1024)
                    {
                        string oldPath = Path.Combine(logDir, "crash.old.log");
                        if (File.Exists(oldPath)) File.Delete(oldPath);
                        File.Move(logPath, oldPath);
                    }
                    string entry = string.Format("[{0:yyyy-MM-dd HH:mm:ss.fff}] [{1}] {2}{3}",
                        DateTime.Now, context, ex != null ? ex.ToString() : "(null)", Environment.NewLine + Environment.NewLine);
                    File.AppendAllText(logPath, entry, Encoding.UTF8);
                }
            }
            catch
            {
            }
        }

        private void StartInteractive()
        {
            _config = ConfigManager.Load();
            _mainWindow = new MainWindow(_config, false);
            _mainWindow.RequestExit += ShutdownApp;
            _mainWindow.ShellRestarted += delegate
            {
                RefreshTrayIconVisibility();
            };
            SetupTrayIcon();
            StartWakeListener();
            _mainWindow.Show();
        }

        private void StartWakeListener()
        {
            try
            {
                bool ignored;
                _wakeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, WakeEventName, out ignored);
                _wakeThread = new Thread(delegate()
                {
                    while (!_shuttingDown)
                    {
                        try
                        {
                            if (_wakeEvent == null || !_wakeEvent.WaitOne()) continue;
                            if (_shuttingDown) break;
                            Dispatcher.BeginInvoke((Action)delegate
                            {
                                if (!_shuttingDown && _mainWindow != null)
                                {
                                    _mainWindow.EnsureVisibleAndAttached();
                                    RefreshTrayIconVisibility();
                                }
                            });
                        }
                        catch
                        {
                            if (_shuttingDown) break;
                            Thread.Sleep(500);
                        }
                    }
                });
                _wakeThread.IsBackground = true;
                _wakeThread.Start();
            }
            catch
            {
            }
        }

        private void RefreshTrayIconVisibility()
        {
            if (_trayIcon == null) return;
            try
            {
                _trayIcon.Visible = false;
                _trayIcon.Visible = true;
            }
            catch
            {
            }
        }

        private void SetupTrayIcon()
        {
            _trayIcon = new WinForms.NotifyIcon();
            _trayIcon.Text = "LumeDesk · 横竖屏自适应磨砂桌面启动台";
            _trayIcon.Icon = CreateTrayIcon();
            _trayIcon.Visible = true;
            _trayIcon.DoubleClick += delegate
            {
                Dispatcher.BeginInvoke((Action)delegate { _mainWindow.OpenSearchOverlay(); });
            };

            WinForms.ContextMenuStrip menu = new WinForms.ContextMenuStrip();
            menu.Opening += delegate
            {
                RebuildTrayMenu(menu);
            };
            RebuildTrayMenu(menu);
            _trayIcon.ContextMenuStrip = menu;
        }

        private void RebuildTrayMenu(WinForms.ContextMenuStrip menu)
        {
            menu.Items.Clear();

            menu.Items.Add("➕ 新建分类大文件夹", null, delegate
            {
                Dispatcher.BeginInvoke((Action)delegate { _mainWindow.OpenNewCategoryDialog(); });
            });
            menu.Items.Add("🔍 搜索应用", null, delegate
            {
                Dispatcher.BeginInvoke((Action)delegate { _mainWindow.OpenSearchOverlay(); });
            });
            menu.Items.Add("🔄 刷新图标与壁纸", null, delegate
            {
                Dispatcher.BeginInvoke((Action)delegate { _mainWindow.RefreshAll(); });
            });
            menu.Items.Add("📂 打开图标管理目录 (data)", null, delegate
            {
                Dispatcher.BeginInvoke((Action)delegate { _mainWindow.OpenDataFolder(); });
            });
            menu.Items.Add(new WinForms.ToolStripSeparator());

            // Layout mode submenu (adapts labels automatically for Landscape vs Portrait!)
            string lm = (_config.LayoutMode ?? "auto").ToLowerInvariant();
            bool land = (_mainWindow != null && _mainWindow.IsLandscape);
            WinForms.ToolStripMenuItem layoutMenu = new WinForms.ToolStripMenuItem("📐 桌面网格布局");
            string[][] layoutOpts = land
                ? new string[][]
                {
                    new string[] { "auto", "智能自适应 (横屏双栏 / 自动扩展)" },
                    new string[] { "3x2", "固定 3×2 (6格/页，超出自动分页)" },
                    new string[] { "4x2", "固定 4×2 (8格/页，超出自动分页)" },
                    new string[] { "4x3", "固定 4×3 (12格/页，超出自动分页)" },
                    new string[] { "5x3", "固定 5×3 (15格/页，超出自动分页)" }
                }
                : new string[][]
                {
                    new string[] { "auto", "智能自适应 (竖屏 6→8→12格)" },
                    new string[] { "2x3", "固定 2×3 (6格/页，超出自动分页)" },
                    new string[] { "2x4", "固定 2×4 (8格/页，超出自动分页)" },
                    new string[] { "3x4", "固定 3×4 (12格/页，超出自动分页)" },
                    new string[] { "3x5", "固定 3×5 (15格/页，超出自动分页)" }
                };
            for (int i = 0; i < layoutOpts.Length; i++)
            {
                string optVal = layoutOpts[i][0];
                WinForms.ToolStripMenuItem li = new WinForms.ToolStripMenuItem(layoutOpts[i][1]);
                li.Checked = (lm == optVal) ||
                    (optVal == "3x2" && lm == "2x3") || (optVal == "2x3" && lm == "3x2") ||
                    (optVal == "4x2" && lm == "2x4") || (optVal == "2x4" && lm == "4x2") ||
                    (optVal == "4x3" && lm == "3x4") || (optVal == "3x4" && lm == "4x3") ||
                    (optVal == "5x3" && lm == "3x5") || (optVal == "3x5" && lm == "5x3");
                li.Click += delegate
                {
                    Dispatcher.BeginInvoke((Action)delegate { _mainWindow.SetLayoutMode(optVal); });
                };
                layoutMenu.DropDownItems.Add(li);
            }
            menu.Items.Add(layoutMenu);

            WinForms.ToolStripMenuItem widgetMenu = new WinForms.ToolStripMenuItem("🧩 桌面小部件");
            WinForms.ToolStripMenuItem wMaster = new WinForms.ToolStripMenuItem("显示桌面小部件区");
            wMaster.Checked = _config.ShowWidgets;
            wMaster.Click += delegate { Dispatcher.BeginInvoke((Action)delegate { _mainWindow.ToggleWidgets(); }); };
            widgetMenu.DropDownItems.Add(wMaster);
            widgetMenu.DropDownItems.Add(new WinForms.ToolStripSeparator());

            string[][] wOpts = new string[][]
            {
                new string[] { "mail", "✉️ 邮箱收件箱 (Gmail / IMAP)" },
                new string[] { "clash", "🌐 代理状态与实时网速 (Clash Verge)" },
                new string[] { "music", "🎵 音乐播放与实时歌词" },
                new string[] { "sys_todo", "⚡ 系统监控与快捷待办" }
            };
            for (int i = 0; i < wOpts.Length; i++)
            {
                string wid = wOpts[i][0];
                WinForms.ToolStripMenuItem wi = new WinForms.ToolStripMenuItem(wOpts[i][1]);
                wi.Checked = _mainWindow.IsWidgetEnabled(wid);
                wi.Click += delegate { Dispatcher.BeginInvoke((Action)delegate { _mainWindow.ToggleSingleWidget(wid); }); };
                widgetMenu.DropDownItems.Add(wi);
            }
            widgetMenu.DropDownItems.Add(new WinForms.ToolStripSeparator());
            widgetMenu.DropDownItems.Add("⚙ 配置邮箱收件箱 (Gmail / IMAP)...", null, delegate
            {
                Dispatcher.BeginInvoke((Action)delegate { _mainWindow.OpenMailConfigDialog(); });
            });
            widgetMenu.DropDownItems.Add("➕ 添加快捷待办...", null, delegate
            {
                Dispatcher.BeginInvoke((Action)delegate { _mainWindow.OpenAddTodoDialog(); });
            });
            menu.Items.Add(widgetMenu);

            bool hidden = DesktopAttachment.AreNativeDesktopIconsHidden();
            menu.Items.Add(hidden ? "👁 显示 Windows 原生桌面图标" : "🙈 隐藏 Windows 原生桌面图标", null, delegate
            {
                DesktopAttachment.ToggleNativeDesktopIcons();
            });

            WinForms.ToolStripMenuItem clockItem = new WinForms.ToolStripMenuItem("🕒 显示顶部极简时钟");
            clockItem.Checked = _config.ShowTopClock;
            clockItem.Click += delegate
            {
                Dispatcher.BeginInvoke((Action)delegate { _mainWindow.ToggleTopClock(); });
            };
            menu.Items.Add(clockItem);

            // Monitor selector submenu
            WinForms.ToolStripMenuItem monMenu = new WinForms.ToolStripMenuItem("🖥 显示屏幕");
            WinForms.ToolStripMenuItem autoPrimaryItem = new WinForms.ToolStripMenuItem("自动适配主屏 (推荐)");
            autoPrimaryItem.Checked = string.IsNullOrEmpty(_config.TargetMonitor) ||
                                      _config.TargetMonitor.Equals("auto", StringComparison.OrdinalIgnoreCase) ||
                                      _config.TargetMonitor.Equals("primary", StringComparison.OrdinalIgnoreCase);
            autoPrimaryItem.Click += delegate
            {
                Dispatcher.BeginInvoke((Action)delegate { _mainWindow.SwitchMonitor("auto"); });
            };
            monMenu.DropDownItems.Add(autoPrimaryItem);

            WinForms.ToolStripMenuItem autoPortraitItem = new WinForms.ToolStripMenuItem("优先选择竖屏副屏");
            autoPortraitItem.Checked = _config.TargetMonitor != null &&
                                       _config.TargetMonitor.Equals("auto-portrait", StringComparison.OrdinalIgnoreCase);
            autoPortraitItem.Click += delegate
            {
                Dispatcher.BeginInvoke((Action)delegate { _mainWindow.SwitchMonitor("auto-portrait"); });
            };
            monMenu.DropDownItems.Add(autoPortraitItem);
            monMenu.DropDownItems.Add(new WinForms.ToolStripSeparator());

            List<MonitorInfoItem> monitors = MonitorHelper.GetAllMonitors();
            for (int i = 0; i < monitors.Count; i++)
            {
                MonitorInfoItem m = monitors[i];
                WinForms.ToolStripMenuItem mi = new WinForms.ToolStripMenuItem(m.FriendlyLabel);
                mi.Checked = m.DeviceName.Equals(_config.TargetMonitor, StringComparison.OrdinalIgnoreCase);
                string dev = m.DeviceName;
                mi.Click += delegate
                {
                    Dispatcher.BeginInvoke((Action)delegate { _mainWindow.SwitchMonitor(dev); });
                };
                monMenu.DropDownItems.Add(mi);
            }
            menu.Items.Add(monMenu);

            // Glass tint preset submenu
            WinForms.ToolStripMenuItem glassMenu = new WinForms.ToolStripMenuItem("🎨 卡片磨砂深浅");
            double[][] glassPresets = new double[][]
            {
                new double[] { 0.36 },
                new double[] { 0.48 },
                new double[] { 0.62 },
                new double[] { 0.76 }
            };
            string[] glassLabels = new string[]
            {
                "冰晶通透 (36%)",
                "通透轻盈 (48%)",
                "标准深邃 (62%)",
                "浓郁暗色 (76%)"
            };
            for (int gi = 0; gi < glassPresets.Length; gi++)
            {
                double val = glassPresets[gi][0];
                WinForms.ToolStripMenuItem gmi = new WinForms.ToolStripMenuItem(glassLabels[gi]);
                gmi.Checked = Math.Abs(_config.CardTintOpacity - val) < 0.04;
                gmi.Click += delegate { Dispatcher.BeginInvoke((Action)delegate { _mainWindow.SetGlassPreset(val); }); };
                glassMenu.DropDownItems.Add(gmi);
            }
            menu.Items.Add(glassMenu);

            WinForms.ToolStripMenuItem autoStartItem = new WinForms.ToolStripMenuItem("🚀 开机自动启动");
            autoStartItem.Checked = DesktopAttachment.IsAutoStartEnabled();
            autoStartItem.Click += delegate
            {
                DesktopAttachment.SetAutoStart(!autoStartItem.Checked);
            };
            menu.Items.Add(autoStartItem);

            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("❌ 退出 LumeDesk", null, delegate
            {
                Dispatcher.BeginInvoke((Action)delegate { ShutdownApp(); });
            });
        }

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private Icon CreateTrayIcon()
        {
            using (Bitmap bmp = new Bitmap(32, 32))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (SolidBrush bg = new SolidBrush(Color.FromArgb(255, 22, 32, 52)))
                {
                    g.FillRectangle(bg, 2, 2, 28, 28);
                }
                using (SolidBrush b1 = new SolidBrush(Color.FromArgb(255, 96, 165, 250)))
                using (SolidBrush b2 = new SolidBrush(Color.FromArgb(255, 248, 250, 252)))
                using (SolidBrush b3 = new SolidBrush(Color.FromArgb(255, 52, 211, 153)))
                using (SolidBrush b4 = new SolidBrush(Color.FromArgb(255, 148, 163, 184)))
                {
                    g.FillRectangle(b1, 5, 5, 9, 9);
                    g.FillRectangle(b2, 17, 5, 9, 9);
                    g.FillRectangle(b3, 5, 17, 9, 9);
                    g.FillRectangle(b4, 17, 17, 4, 4);
                    g.FillRectangle(b4, 22, 17, 4, 4);
                    g.FillRectangle(b4, 17, 22, 4, 4);
                    g.FillRectangle(b4, 22, 22, 4, 4);
                }
                IntPtr hIcon = bmp.GetHicon();
                try
                {
                    using (Icon tmp = Icon.FromHandle(hIcon))
                    {
                        return (Icon)tmp.Clone();
                    }
                }
                finally
                {
                    DestroyIcon(hIcon);
                }
            }
        }

        private void ShutdownApp()
        {
            _shuttingDown = true;
            if (_wakeEvent != null)
            {
                try { _wakeEvent.Set(); } catch { }
                try { _wakeEvent.Dispose(); } catch { }
                _wakeEvent = null;
            }
            if (_trayIcon != null)
            {
                try { _trayIcon.Visible = false; _trayIcon.Dispose(); } catch { }
                _trayIcon = null;
            }
            if (_mainWindow != null)
            {
                try
                {
                    _mainWindow.AllowExit();
                    _mainWindow.Close();
                }
                catch { }
                _mainWindow = null;
            }
            Shutdown();
        }
    }
}
