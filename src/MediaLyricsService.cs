using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Media.Imaging;
using Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace LumeDesk
{
    public class LyricLine
    {
        public double Seconds { get; set; }
        public string Text { get; set; }
    }

    public class MediaSnapshot
    {
        public bool HasSession { get; set; }
        public bool IsPlaying { get; set; }
        public string Title { get; set; }
        public string Artist { get; set; }
        public string SourceApp { get; set; }
        public string SourceAppId { get; set; }
        public double PositionSeconds { get; set; }
        public double DurationSeconds { get; set; }
        public BitmapSource CoverArt { get; set; }
        public string PrevLyric { get; set; }
        public string CurrentLyric { get; set; }
        public string NextLyric { get; set; }
        public bool HasSyncedLyrics { get; set; }
        public string LyricStatus { get; set; }

        public MediaSnapshot()
        {
            Title = "暂无播放中的媒体";
            Artist = "网易云 · Spotify · Apple Music";
            SourceApp = "";
            SourceAppId = "";
            PrevLyric = "";
            CurrentLyric = "";
            NextLyric = "";
            LyricStatus = "";
        }
    }

    public class MediaLyricsService : IDisposable
    {
        private readonly object _lock = new object();

        private volatile bool _running = false;
        private Thread _pollThread;
        private GlobalSystemMediaTransportControlsSessionManager _sessionMgr;

        private string _lastTrackKey = "";
        private string _lastTrackSessionKey = "";
        private string _lastCoverTrackKey = "";
        private ulong _lastCoverHash = 0;
        private ulong _prevTrackCoverHash = 0;
        private DateTime _trackChangedUtc = DateTime.MinValue;
        private bool _coverSettled = false;
        private BitmapSource _cachedCover;
        private Dictionary<string, List<LyricLine>> _lyricsCache = new Dictionary<string, List<LyricLine>>(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _lyricsInFlight = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private MediaSnapshot _state = new MediaSnapshot();
        private double _anchorPosSec = 0;
        private DateTime _anchorUtc = DateTime.UtcNow;

        // NetEase Cloud Music fallback state (when NetEase 3.x does not publish Windows SMTC)
        private volatile bool _usingNetEaseFallback = false;
        private bool _nePlaying = true;
        private string _neLastRawTitle = "";
        private DateTime _nePlayingListMtime = DateTime.MinValue;
        private List<NetEaseTrackMeta> _nePlayingListCache = new List<NetEaseTrackMeta>();
        private HashSet<string> _coverInFlight = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private class NetEaseTrackMeta
        {
            public string Id;
            public string Name;
            public string Artist;
            public double DurationSec;
            public string CoverUrl;
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        private const byte VK_MEDIA_NEXT_TRACK = 0xB0;
        private const byte VK_MEDIA_PREV_TRACK = 0xB1;
        private const byte VK_MEDIA_PLAY_PAUSE = 0xB3;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        public MediaLyricsService()
        {
            try
            {
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2
            }
            catch
            {
            }
        }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _pollThread = new Thread(PollLoop);
            _pollThread.IsBackground = true;
            _pollThread.Start();
        }

        public void Dispose()
        {
            _running = false;
        }

        public void RefreshOnceSync()
        {
            PollOnce();
        }

        public MediaSnapshot GetSnapshot()
        {
            lock (_lock)
            {
                double livePos = _anchorPosSec;
                if (_state.HasSession && _state.IsPlaying)
                {
                    livePos = _anchorPosSec + (DateTime.UtcNow - _anchorUtc).TotalSeconds;
                    if (_state.DurationSeconds > 1 && livePos > _state.DurationSeconds)
                    {
                        livePos = _state.DurationSeconds;
                    }
                }

                string prevL = "", curL = "", nextL = "";
                bool hasSynced = false;
                List<LyricLine> lines;
                if (!string.IsNullOrEmpty(_lastTrackKey) &&
                    _lyricsCache.TryGetValue(_lastTrackKey, out lines) &&
                    lines != null && lines.Count > 0)
                {
                    hasSynced = true;
                    ResolveLyricLines(lines, livePos, out prevL, out curL, out nextL);
                }

                return new MediaSnapshot
                {
                    HasSession = _state.HasSession,
                    IsPlaying = _state.IsPlaying,
                    Title = _state.Title,
                    Artist = _state.Artist,
                    SourceApp = _state.SourceApp,
                    SourceAppId = _state.SourceAppId,
                    PositionSeconds = livePos,
                    DurationSeconds = _state.DurationSeconds,
                    CoverArt = _cachedCover,
                    PrevLyric = prevL,
                    CurrentLyric = curL,
                    NextLyric = nextL,
                    HasSyncedLyrics = hasSynced,
                    LyricStatus = _state.LyricStatus
                };
            }
        }

        private sealed class WinRtWaitGuard
        {
            public readonly ManualResetEventSlim Event = new ManualResetEventSlim(false);
            private int _refs = 2;

            public void SignalAndRelease()
            {
                try { Event.Set(); } catch { }
                Release();
            }

            public void Release()
            {
                if (Interlocked.Decrement(ref _refs) == 0)
                {
                    try { Event.Dispose(); } catch { }
                }
            }
        }

        private static T WaitWinRt<T>(IAsyncOperation<T> op)
        {
            if (op == null) return default(T);
            try
            {
                if (op.Status == AsyncStatus.Started)
                {
                    WinRtWaitGuard guard = new WinRtWaitGuard();
                    try
                    {
                        op.Completed = delegate(IAsyncOperation<T> info, AsyncStatus st)
                        {
                            guard.SignalAndRelease();
                        };
                        if (op.Status == AsyncStatus.Started)
                        {
                            guard.Event.Wait(3500);
                        }
                    }
                    finally
                    {
                        guard.Release();
                    }
                }
                if (op.Status == AsyncStatus.Completed)
                {
                    return op.GetResults();
                }
            }
            catch
            {
            }
            return default(T);
        }

        private static uint WaitWinRtLoad(DataReaderLoadOperation op)
        {
            if (op == null) return 0;
            try
            {
                if (op.Status == AsyncStatus.Started)
                {
                    WinRtWaitGuard guard = new WinRtWaitGuard();
                    try
                    {
                        op.Completed = delegate(IAsyncOperation<uint> info, AsyncStatus st)
                        {
                            guard.SignalAndRelease();
                        };
                        if (op.Status == AsyncStatus.Started)
                        {
                            guard.Event.Wait(3500);
                        }
                    }
                    finally
                    {
                        guard.Release();
                    }
                }
                if (op.Status == AsyncStatus.Completed)
                {
                    return op.GetResults();
                }
            }
            catch
            {
            }
            return 0;
        }

        private static void SendMediaKey(byte vk)
        {
            try
            {
                keybd_event(vk, 0, 0, UIntPtr.Zero);
                Thread.Sleep(25);
                keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
            catch
            {
            }
        }

        public void TogglePlayPause()
        {
            RunSessionAction(
                delegate(GlobalSystemMediaTransportControlsSession s)
                {
                    WaitWinRt<bool>(s.TryTogglePlayPauseAsync());
                },
                delegate
                {
                    SendMediaKey(VK_MEDIA_PLAY_PAUSE);
                    lock (_lock)
                    {
                        if (_usingNetEaseFallback && _state.HasSession)
                        {
                            double curPos = _anchorPosSec;
                            if (_nePlaying)
                            {
                                curPos += (DateTime.UtcNow - _anchorUtc).TotalSeconds;
                                if (_state.DurationSeconds > 1 && curPos > _state.DurationSeconds) curPos = _state.DurationSeconds;
                            }
                            _nePlaying = !_nePlaying;
                            _anchorPosSec = curPos;
                            _anchorUtc = DateTime.UtcNow;
                            _state.IsPlaying = _nePlaying;
                        }
                    }
                });
        }

        public void NextTrack()
        {
            RunSessionAction(
                delegate(GlobalSystemMediaTransportControlsSession s)
                {
                    WaitWinRt<bool>(s.TrySkipNextAsync());
                },
                delegate
                {
                    SendMediaKey(VK_MEDIA_NEXT_TRACK);
                });
        }

        public void PrevTrack()
        {
            RunSessionAction(
                delegate(GlobalSystemMediaTransportControlsSession s)
                {
                    WaitWinRt<bool>(s.TrySkipPreviousAsync());
                },
                delegate
                {
                    SendMediaKey(VK_MEDIA_PREV_TRACK);
                });
        }

        private void RunSessionAction(Action<GlobalSystemMediaTransportControlsSession> action, Action fallbackAction)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    if (_sessionMgr == null)
                    {
                        _sessionMgr = WaitWinRt<GlobalSystemMediaTransportControlsSessionManager>(
                            GlobalSystemMediaTransportControlsSessionManager.RequestAsync());
                    }
                    GlobalSystemMediaTransportControlsSession session = GetBestSmtcSession();
                    if (session != null && action != null)
                    {
                        action(session);
                        Thread.Sleep(150);
                        PollOnce();
                        return;
                    }
                    if (_usingNetEaseFallback && fallbackAction != null)
                    {
                        fallbackAction();
                        Thread.Sleep(250);
                        PollOnce();
                    }
                }
                catch
                {
                }
            });
        }

        private GlobalSystemMediaTransportControlsSession GetBestSmtcSession()
        {
            if (_sessionMgr == null) return null;
            GlobalSystemMediaTransportControlsSession current = null;
            try
            {
                current = _sessionMgr.GetCurrentSession();
                if (current != null)
                {
                    GlobalSystemMediaTransportControlsSessionPlaybackInfo pi = current.GetPlaybackInfo();
                    if (pi != null && pi.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                    {
                        return current;
                    }
                }
            }
            catch
            {
            }

            try
            {
                IReadOnlyList<GlobalSystemMediaTransportControlsSession> all = _sessionMgr.GetSessions();
                if (all != null)
                {
                    for (int i = 0; i < all.Count; i++)
                    {
                        GlobalSystemMediaTransportControlsSession s = all[i];
                        if (s == null) continue;
                        try
                        {
                            GlobalSystemMediaTransportControlsSessionPlaybackInfo pi = s.GetPlaybackInfo();
                            if (pi != null && pi.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                            {
                                return s;
                            }
                        }
                        catch
                        {
                        }
                    }
                    if (current == null && all.Count > 0) return all[0];
                }
            }
            catch
            {
            }
            return current;
        }

        private void PollLoop()
        {
            while (_running)
            {
                try
                {
                    PollOnce();
                }
                catch
                {
                }
                for (int i = 0; i < 6 && _running; i++) Thread.Sleep(100);
            }
        }

        private void PollOnce()
        {
            try
            {
                if (_sessionMgr == null)
                {
                    _sessionMgr = WaitWinRt<GlobalSystemMediaTransportControlsSessionManager>(
                        GlobalSystemMediaTransportControlsSessionManager.RequestAsync());
                }
            }
            catch
            {
                return;
            }

            if (_sessionMgr == null) return;

            GlobalSystemMediaTransportControlsSession session = GetBestSmtcSession();

            if (session == null)
            {
                if (TryPollNetEaseFallback())
                {
                    return;
                }
                _usingNetEaseFallback = false;
                _neLastRawTitle = "";
                lock (_lock)
                {
                    _state.HasSession = false;
                    _state.IsPlaying = false;
                    _state.Title = "暂无播放中的媒体";
                    _state.Artist = "网易云 · Spotify · Apple Music";
                    _state.SourceApp = "";
                    _state.SourceAppId = "";
                    _state.DurationSeconds = 0;
                    _state.LyricStatus = "";
                    _cachedCover = null;
                    _lastTrackKey = "";
                    _lastTrackSessionKey = "";
                    _lastCoverTrackKey = "";
                    _lastCoverHash = 0;
                    _prevTrackCoverHash = 0;
                    _coverSettled = false;
                }
                return;
            }

            _usingNetEaseFallback = false;

            string title = "";
            string artist = "";
            string rawAppId = session.SourceAppUserModelId ?? "";
            string sourceApp = PrettifyAppId(rawAppId);
            bool isPlaying = false;
            double posSec = 0;
            double durSec = 0;
            IRandomAccessStreamReference thumbRef = null;

            try
            {
                GlobalSystemMediaTransportControlsSessionPlaybackInfo pInfo = session.GetPlaybackInfo();
                if (pInfo != null)
                {
                    isPlaying = (pInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
                }
            }
            catch
            {
            }

            try
            {
                GlobalSystemMediaTransportControlsSessionTimelineProperties tl = session.GetTimelineProperties();
                if (tl != null)
                {
                    durSec = tl.EndTime.TotalSeconds;
                    posSec = tl.Position.TotalSeconds;
                    if (isPlaying && tl.LastUpdatedTime.Year > 2000)
                    {
                        double elapsed = (DateTimeOffset.UtcNow - tl.LastUpdatedTime).TotalSeconds;
                        if (elapsed > 0 && elapsed < 3600) posSec += elapsed;
                    }
                    if (durSec > 0 && posSec > durSec) posSec = durSec;
                    if (posSec < 0) posSec = 0;
                }
            }
            catch
            {
            }

            try
            {
                GlobalSystemMediaTransportControlsSessionMediaProperties props =
                    WaitWinRt<GlobalSystemMediaTransportControlsSessionMediaProperties>(session.TryGetMediaPropertiesAsync());
                if (props != null)
                {
                    title = (props.Title ?? "").Trim();
                    artist = CleanArtist(props.Artist ?? "");
                    thumbRef = props.Thumbnail;
                }
            }
            catch
            {
            }

            if (string.IsNullOrEmpty(title))
            {
                title = "媒体播放中";
            }

            string trackKey = title + "||" + artist;
            string trackSessionKey = trackKey + "||" + rawAppId;
            bool trackChanged = !trackSessionKey.Equals(_lastTrackSessionKey, StringComparison.OrdinalIgnoreCase);

            if (trackChanged)
            {
                _prevTrackCoverHash = _lastCoverHash;
                _trackChangedUtc = DateTime.UtcNow;
                _coverSettled = false;
            }

            double secsSinceTrackChange = (DateTime.UtcNow - _trackChangedUtc).TotalSeconds;
            bool needCheckCover = trackChanged || !_coverSettled || (_cachedCover == null && secsSinceTrackChange < 30.0);

            BitmapSource newCover = null;
            ulong newCoverHash = 0;
            bool applyNewCover = false;
            bool clearStaleCover = false;

            if (needCheckCover && thumbRef != null)
            {
                byte[] rawThumb = LoadThumbnailBytes(thumbRef);
                if (rawThumb != null && rawThumb.Length >= 32)
                {
                    ulong h = ComputeBytesHash(rawThumb);
                    if (_prevTrackCoverHash == 0 || h != _prevTrackCoverHash)
                    {
                        if (h != _lastCoverHash || _cachedCover == null)
                        {
                            BitmapSource decoded = DecodeCoverBitmap(rawThumb);
                            if (decoded != null)
                            {
                                newCover = decoded;
                                newCoverHash = h;
                                applyNewCover = true;
                                _coverSettled = true;
                            }
                        }
                        else
                        {
                            _coverSettled = true;
                        }
                    }
                    else
                    {
                        // Thumbnail bytes still match the PREVIOUS track (common browser/CDN delay after track switch).
                        // Clear stale artwork immediately on switch; if still identical after 2.5s (same album), show it
                        // while continuing to watch up to 12s for late-arriving distinct artwork.
                        if (trackChanged)
                        {
                            clearStaleCover = true;
                        }
                        else if (_cachedCover == null && secsSinceTrackChange >= 2.5)
                        {
                            BitmapSource decoded = DecodeCoverBitmap(rawThumb);
                            if (decoded != null)
                            {
                                newCover = decoded;
                                newCoverHash = h;
                                applyNewCover = true;
                            }
                        }
                        if (secsSinceTrackChange >= 12.0)
                        {
                            _coverSettled = true;
                        }
                    }
                }
                else if (trackChanged)
                {
                    clearStaleCover = true;
                }
            }
            else if (trackChanged)
            {
                clearStaleCover = true;
            }

            lock (_lock)
            {
                _state.HasSession = true;
                _state.IsPlaying = isPlaying;
                _state.Title = title;
                _state.Artist = string.IsNullOrEmpty(artist) ? sourceApp : artist;
                _state.SourceApp = sourceApp;
                _state.SourceAppId = rawAppId;
                _state.DurationSeconds = durSec;
                _anchorPosSec = posSec;
                _anchorUtc = DateTime.UtcNow;

                if (trackChanged)
                {
                    _lastTrackKey = trackKey;
                    _lastTrackSessionKey = trackSessionKey;
                    _state.LyricStatus = "正在匹配歌词...";
                }
                if (applyNewCover)
                {
                    _cachedCover = newCover;
                    _lastCoverHash = newCoverHash;
                    _lastCoverTrackKey = trackKey;
                }
                else if (clearStaleCover)
                {
                    _cachedCover = null;
                    _lastCoverTrackKey = "";
                }
            }

            // Trigger async synced lyrics fetch when track changes
            if (!string.IsNullOrEmpty(title) && title != "媒体播放中")
            {
                bool needFetch = false;
                lock (_lock)
                {
                    if (!_lyricsCache.ContainsKey(trackKey) && !_lyricsInFlight.Contains(trackKey))
                    {
                        _lyricsInFlight.Add(trackKey);
                        needFetch = true;
                    }
                }
                if (needFetch)
                {
                    string fetchTitle = title;
                    string fetchArtist = artist;
                    string fetchKey = trackKey;
                    double fetchDur = durSec;
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        FetchLyricsWorker(fetchKey, fetchTitle, fetchArtist, fetchDur, null);
                    });
                }
            }
        }

        private bool TryPollNetEaseFallback()
        {
            string rawWindowTitle = FindNetEaseTrackWindowTitle();
            if (string.IsNullOrEmpty(rawWindowTitle))
            {
                return false;
            }

            string title = rawWindowTitle;
            string artist = "";
            int sep = rawWindowTitle.LastIndexOf(" - ");
            if (sep > 0 && sep + 3 < rawWindowTitle.Length)
            {
                title = rawWindowTitle.Substring(0, sep).Trim();
                artist = CleanArtist(rawWindowTitle.Substring(sep + 3).Trim());
            }

            NetEaseTrackMeta meta = MatchNetEasePlayingList(title, artist);
            double durSec = (meta != null && meta.DurationSec > 1) ? meta.DurationSec : 0;
            string songId = (meta != null) ? meta.Id : null;
            string coverUrl = (meta != null) ? meta.CoverUrl : null;
            if (meta != null && !string.IsNullOrEmpty(meta.Name)) title = meta.Name;
            if (meta != null && !string.IsNullOrEmpty(meta.Artist)) artist = CleanArtist(meta.Artist);

            string trackKey = title + "||" + artist;
            bool trackChanged = !trackKey.Equals(_lastTrackKey, StringComparison.OrdinalIgnoreCase) || !_usingNetEaseFallback;
            bool rawChanged = !rawWindowTitle.Equals(_neLastRawTitle, StringComparison.Ordinal);
            _usingNetEaseFallback = true;

            lock (_lock)
            {
                _state.HasSession = true;
                _state.Title = title;
                _state.Artist = string.IsNullOrEmpty(artist) ? "网易云音乐" : artist;
                _state.SourceApp = "网易云音乐";
                _state.SourceAppId = "cloudmusic.exe";
                _state.DurationSeconds = durSec;

                if (trackChanged || rawChanged)
                {
                    _neLastRawTitle = rawWindowTitle;
                    _nePlaying = true;
                    _anchorPosSec = 0;
                    _anchorUtc = DateTime.UtcNow;
                }
                _state.IsPlaying = _nePlaying;

                if (trackChanged)
                {
                    _lastTrackKey = trackKey;
                    _lastTrackSessionKey = trackKey + "||cloudmusic.exe";
                    _cachedCover = null;
                    _lastCoverTrackKey = "";
                    _lastCoverHash = 0;
                    _prevTrackCoverHash = 0;
                    _coverSettled = false;
                    _state.LyricStatus = "正在匹配歌词...";
                }
            }

            // Async album cover download from NetEase playingList picUrl
            if (!string.IsNullOrEmpty(coverUrl))
            {
                bool needCover = false;
                lock (_lock)
                {
                    if (!trackKey.Equals(_lastCoverTrackKey, StringComparison.OrdinalIgnoreCase) && !_coverInFlight.Contains(trackKey))
                    {
                        _coverInFlight.Add(trackKey);
                        needCover = true;
                    }
                }
                if (needCover)
                {
                    string cKey = trackKey;
                    string cUrl = coverUrl;
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        FetchCoverUrlWorker(cKey, cUrl);
                    });
                }
            }

            // Async synced lyrics fetch (NetEase official API first, then LRCLIB)
            if (!string.IsNullOrEmpty(title))
            {
                bool needFetch = false;
                lock (_lock)
                {
                    if (!_lyricsCache.ContainsKey(trackKey) && !_lyricsInFlight.Contains(trackKey))
                    {
                        _lyricsInFlight.Add(trackKey);
                        needFetch = true;
                    }
                }
                if (needFetch)
                {
                    string fetchTitle = title;
                    string fetchArtist = artist;
                    string fetchKey = trackKey;
                    double fetchDur = durSec;
                    string fetchId = songId;
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        FetchLyricsWorker(fetchKey, fetchTitle, fetchArtist, fetchDur, fetchId);
                    });
                }
            }

            return true;
        }

        private static string FindNetEaseTrackWindowTitle()
        {
            Process[] procs = null;
            try
            {
                procs = Process.GetProcessesByName("cloudmusic");
            }
            catch
            {
                return null;
            }
            if (procs == null || procs.Length == 0) return null;

            HashSet<uint> pids = new HashSet<uint>();
            for (int i = 0; i < procs.Length; i++)
            {
                try { pids.Add((uint)procs[i].Id); } catch { }
                try { procs[i].Dispose(); } catch { }
            }
            if (pids.Count == 0) return null;

            string bestTitle = null;
            try
            {
                EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
                {
                    uint pid;
                    GetWindowThreadProcessId(hWnd, out pid);
                    if (!pids.Contains(pid)) return true;

                    StringBuilder cls = new StringBuilder(128);
                    GetClassName(hWnd, cls, cls.Capacity);
                    string clsName = cls.ToString();

                    StringBuilder sb = new StringBuilder(256);
                    int len = GetWindowText(hWnd, sb, sb.Capacity);
                    if (len <= 0) return true;
                    string text = sb.ToString().Trim();
                    if (string.IsNullOrEmpty(text)) return true;

                    if (text == "网易云音乐" || text == "桌面歌词" || text == "迷你模式" || text == "NetEase Cloud Music")
                        return true;

                    if (clsName.IndexOf("OrpheusBrowserHost", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        bestTitle = text;
                        return false;
                    }
                    if (bestTitle == null && text.IndexOf(" - ") > 0)
                    {
                        bestTitle = text;
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch
            {
            }
            return bestTitle;
        }

        private NetEaseTrackMeta MatchNetEasePlayingList(string title, string artist)
        {
            try
            {
                string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"NetEase\CloudMusic\webdata\file\playingList");
                if (!File.Exists(path)) return null;

                DateTime mtime = File.GetLastWriteTimeUtc(path);
                if (mtime != _nePlayingListMtime)
                {
                    string raw = File.ReadAllText(path, Encoding.UTF8);
                    _nePlayingListCache = ParseNetEasePlayingList(raw);
                    _nePlayingListMtime = mtime;
                }

                if (_nePlayingListCache == null || _nePlayingListCache.Count == 0) return null;

                NetEaseTrackMeta titleOnlyMatch = null;
                for (int i = 0; i < _nePlayingListCache.Count; i++)
                {
                    NetEaseTrackMeta m = _nePlayingListCache[i];
                    if (string.IsNullOrEmpty(m.Name)) continue;
                    bool nameEq = m.Name.Equals(title, StringComparison.OrdinalIgnoreCase);
                    if (!nameEq && title.Length >= 2 && m.Name.IndexOf(title, StringComparison.OrdinalIgnoreCase) >= 0)
                        nameEq = true;
                    if (!nameEq) continue;

                    if (string.IsNullOrEmpty(artist) ||
                        (!string.IsNullOrEmpty(m.Artist) &&
                         (m.Artist.IndexOf(artist, StringComparison.OrdinalIgnoreCase) >= 0 ||
                          artist.IndexOf(m.Artist, StringComparison.OrdinalIgnoreCase) >= 0)))
                    {
                        return m;
                    }
                    if (titleOnlyMatch == null) titleOnlyMatch = m;
                }
                return titleOnlyMatch;
            }
            catch
            {
                return null;
            }
        }

        private List<NetEaseTrackMeta> ParseNetEasePlayingList(string json)
        {
            List<NetEaseTrackMeta> result = new List<NetEaseTrackMeta>();
            if (string.IsNullOrEmpty(json)) return result;
            try
            {
                JavaScriptSerializer js = new JavaScriptSerializer();
                js.MaxJsonLength = 32 * 1024 * 1024;
                Dictionary<string, object> root = js.Deserialize<Dictionary<string, object>>(json);
                if (root == null || !root.ContainsKey("list")) return result;
                object[] list = root["list"] as object[];
                if (list == null) return result;

                for (int i = 0; i < list.Length; i++)
                {
                    Dictionary<string, object> entry = list[i] as Dictionary<string, object>;
                    if (entry == null || !entry.ContainsKey("track")) continue;
                    Dictionary<string, object> track = entry["track"] as Dictionary<string, object>;
                    if (track == null) continue;

                    NetEaseTrackMeta meta = new NetEaseTrackMeta();
                    if (track.ContainsKey("id") && track["id"] != null) meta.Id = track["id"].ToString();
                    if (track.ContainsKey("name") && track["name"] != null) meta.Name = track["name"].ToString().Trim();
                    if (track.ContainsKey("duration") && track["duration"] != null)
                    {
                        double ms;
                        if (double.TryParse(track["duration"].ToString(), out ms)) meta.DurationSec = ms / 1000.0;
                    }
                    if (track.ContainsKey("artists"))
                    {
                        object[] arts = track["artists"] as object[];
                        if (arts != null && arts.Length > 0)
                        {
                            List<string> names = new List<string>();
                            for (int a = 0; a < arts.Length; a++)
                            {
                                Dictionary<string, object> ao = arts[a] as Dictionary<string, object>;
                                if (ao != null && ao.ContainsKey("name") && ao["name"] != null)
                                    names.Add(ao["name"].ToString().Trim());
                            }
                            meta.Artist = string.Join(", ", names.ToArray());
                        }
                    }
                    if (track.ContainsKey("album"))
                    {
                        Dictionary<string, object> alb = track["album"] as Dictionary<string, object>;
                        if (alb != null && alb.ContainsKey("picUrl") && alb["picUrl"] != null)
                        {
                            string u = alb["picUrl"].ToString().Trim();
                            if (!string.IsNullOrEmpty(u))
                            {
                                if (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                                    u = "https://" + u.Substring(7);
                                meta.CoverUrl = u + "?param=240y240";
                            }
                        }
                    }
                    if (!string.IsNullOrEmpty(meta.Name)) result.Add(meta);
                }
            }
            catch
            {
            }
            return result;
        }

        private void FetchCoverUrlWorker(string trackKey, string url)
        {
            BitmapSource bmp = null;
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = 4500;
                req.ReadWriteTimeout = 4500;
                req.UserAgent = "LumeDesk/1.0";
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (Stream s = resp.GetResponseStream())
                using (MemoryStream ms = new MemoryStream())
                {
                    s.CopyTo(ms);
                    byte[] bytes = ms.ToArray();
                    if (bytes.Length >= 64)
                    {
                        BitmapImage bi = new BitmapImage();
                        using (MemoryStream imgMs = new MemoryStream(bytes))
                        {
                            bi.BeginInit();
                            bi.CacheOption = BitmapCacheOption.OnLoad;
                            bi.DecodePixelWidth = 240;
                            bi.StreamSource = imgMs;
                            bi.EndInit();
                        }
                        bi.Freeze();
                        bmp = bi;
                    }
                }
            }
            catch
            {
            }

            lock (_lock)
            {
                _coverInFlight.Remove(trackKey);
                if (bmp != null && _lastTrackKey.Equals(trackKey, StringComparison.OrdinalIgnoreCase))
                {
                    _cachedCover = bmp;
                    _lastCoverTrackKey = trackKey;
                }
            }
        }

        private byte[] LoadThumbnailBytes(IRandomAccessStreamReference thumbRef)
        {
            try
            {
                using (IRandomAccessStreamWithContentType stream =
                    WaitWinRt<IRandomAccessStreamWithContentType>(thumbRef.OpenReadAsync()))
                {
                    if (stream == null || stream.Size < 32) return null;
                    uint len = (uint)Math.Min(stream.Size, 4UL * 1024UL * 1024UL);
                    using (DataReader reader = new DataReader(stream.GetInputStreamAt(0)))
                    {
                        uint loaded = WaitWinRtLoad(reader.LoadAsync(len));
                        if (loaded < 32) return null;
                        byte[] bytes = new byte[loaded];
                        reader.ReadBytes(bytes);
                        return bytes;
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        private static ulong ComputeBytesHash(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return 0;
            unchecked
            {
                ulong hash = 14695981039346656037UL;
                hash ^= (ulong)bytes.Length;
                hash *= 1099511628211UL;
                for (int i = 0; i < bytes.Length; i++)
                {
                    hash ^= bytes[i];
                    hash *= 1099511628211UL;
                }
                return hash == 0 ? 1UL : hash;
            }
        }

        private static BitmapSource DecodeCoverBitmap(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 32) return null;
            try
            {
                BitmapImage bi = new BitmapImage();
                using (MemoryStream imgMs = new MemoryStream(bytes))
                {
                    bi.BeginInit();
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.DecodePixelWidth = 240;
                    bi.StreamSource = imgMs;
                    bi.EndInit();
                }
                bi.Freeze();
                return bi;
            }
            catch
            {
                return null;
            }
        }

        private void FetchLyricsWorker(string trackKey, string rawTitle, string rawArtist, double durationSec, string netEaseSongId)
        {
            List<LyricLine> parsed = null;
            try
            {
                // 0. Official NetEase Cloud Music synced lyric API when songId is known
                if (!string.IsNullOrEmpty(netEaseSongId))
                {
                    string neUrl = "https://music.163.com/api/song/lyric?id=" + Uri.EscapeDataString(netEaseSongId) + "&lv=1&tv=-1";
                    string neJson = HttpGetText(neUrl, 4500);
                    parsed = ParseNetEaseLyric(neJson);
                }

                string cleanTitle = CleanTrackTitle(rawTitle);
                string cleanArtist = CleanArtist(rawArtist);

                // 1. Exact lookup on LRCLIB public API
                if ((parsed == null || parsed.Count == 0) && !string.IsNullOrEmpty(cleanTitle) && !string.IsNullOrEmpty(cleanArtist))
                {
                    string url = "https://lrclib.net/api/get?track_name=" + Uri.EscapeDataString(cleanTitle) +
                                 "&artist_name=" + Uri.EscapeDataString(cleanArtist);
                    if (durationSec > 10) url += "&duration=" + (int)Math.Round(durationSec);
                    string json = HttpGetText(url, 4500);
                    parsed = ParseLrcLibSingle(json);
                }

                // 2. Fuzzy search fallback on LRCLIB (handles YouTube Music titles like "Artist - Song")
                if ((parsed == null || parsed.Count == 0) && !string.IsNullOrEmpty(cleanTitle))
                {
                    string q = string.IsNullOrEmpty(cleanArtist) ? cleanTitle : (cleanTitle + " " + cleanArtist);
                    string url2 = "https://lrclib.net/api/search?q=" + Uri.EscapeDataString(q);
                    string json2 = HttpGetText(url2, 4500);
                    parsed = ParseLrcLibSearch(json2);
                }
            }
            catch
            {
            }

            lock (_lock)
            {
                _lyricsInFlight.Remove(trackKey);
                _lyricsCache[trackKey] = parsed ?? new List<LyricLine>();
                if (_lastTrackKey.Equals(trackKey, StringComparison.OrdinalIgnoreCase))
                {
                    _state.LyricStatus = (parsed != null && parsed.Count > 0) ? "" : "暂无同步歌词";
                }
            }
        }

        private List<LyricLine> ParseNetEaseLyric(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                JavaScriptSerializer localJson = new JavaScriptSerializer();
                Dictionary<string, object> obj = localJson.Deserialize<Dictionary<string, object>>(json);
                if (obj == null || !obj.ContainsKey("lrc")) return null;
                Dictionary<string, object> lrcObj = obj["lrc"] as Dictionary<string, object>;
                if (lrcObj != null && lrcObj.ContainsKey("lyric") && lrcObj["lyric"] is string)
                {
                    List<LyricLine> lines = ParseLrc((string)lrcObj["lyric"]);
                    if (lines.Count > 0) return lines;
                }
            }
            catch
            {
            }
            return null;
        }

        private List<LyricLine> ParseLrcLibSingle(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                JavaScriptSerializer localJson = new JavaScriptSerializer();
                Dictionary<string, object> obj = localJson.Deserialize<Dictionary<string, object>>(json);
                if (obj == null) return null;
                if (obj.ContainsKey("syncedLyrics") && obj["syncedLyrics"] is string)
                {
                    List<LyricLine> lines = ParseLrc((string)obj["syncedLyrics"]);
                    if (lines.Count > 0) return lines;
                }
            }
            catch
            {
            }
            return null;
        }

        private List<LyricLine> ParseLrcLibSearch(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                JavaScriptSerializer localJson = new JavaScriptSerializer();
                object[] arr = localJson.Deserialize<object[]>(json);
                if (arr == null) return null;
                for (int i = 0; i < arr.Length; i++)
                {
                    Dictionary<string, object> item = arr[i] as Dictionary<string, object>;
                    if (item != null && item.ContainsKey("syncedLyrics") && item["syncedLyrics"] is string)
                    {
                        List<LyricLine> lines = ParseLrc((string)item["syncedLyrics"]);
                        if (lines.Count > 0) return lines;
                    }
                }
            }
            catch
            {
            }
            return null;
        }

        public static List<LyricLine> ParseLrc(string lrc)
        {
            List<LyricLine> list = new List<LyricLine>();
            if (string.IsNullOrEmpty(lrc)) return list;

            string[] rawLines = lrc.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            Regex timeRx = new Regex(@"\[(\d{1,2}):(\d{2})(?:[\.:](\d{1,3}))?\]");
            for (int i = 0; i < rawLines.Length; i++)
            {
                string line = rawLines[i].Trim();
                MatchCollection matches = timeRx.Matches(line);
                if (matches.Count == 0) continue;
                string text = timeRx.Replace(line, "").Trim();
                if (string.IsNullOrEmpty(text)) continue;

                for (int m = 0; m < matches.Count; m++)
                {
                    int min = int.Parse(matches[m].Groups[1].Value);
                    int sec = int.Parse(matches[m].Groups[2].Value);
                    double frac = 0;
                    if (matches[m].Groups[3].Success)
                    {
                        string fStr = matches[m].Groups[3].Value;
                        if (fStr.Length == 1) frac = int.Parse(fStr) * 0.1;
                        else if (fStr.Length == 2) frac = int.Parse(fStr) * 0.01;
                        else frac = int.Parse(fStr) * 0.001;
                    }
                    list.Add(new LyricLine { Seconds = min * 60.0 + sec + frac, Text = text });
                }
            }
            list.Sort(delegate(LyricLine a, LyricLine b) { return a.Seconds.CompareTo(b.Seconds); });
            return list;
        }

        public static void ResolveLyricLines(List<LyricLine> lines, double posSec, out string prev, out string cur, out string next)
        {
            prev = "";
            cur = "";
            next = "";
            if (lines == null || lines.Count == 0) return;

            int active = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].Seconds <= posSec + 0.35) active = i;
                else break;
            }

            if (active < 0)
            {
                cur = lines[0].Text;
                if (lines.Count > 1) next = lines[1].Text;
            }
            else
            {
                if (active > 0) prev = lines[active - 1].Text;
                cur = lines[active].Text;
                if (active + 1 < lines.Count) next = lines[active + 1].Text;
            }
        }

        private static string HttpGetText(string url, int timeoutMs)
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.UserAgent = "LumeDesk/1.0 (Windows Desktop Lyrics Widget)";
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (Stream s = resp.GetResponseStream())
                using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
                {
                    return sr.ReadToEnd();
                }
            }
            catch
            {
                return null;
            }
        }

        private static string CleanTrackTitle(string t)
        {
            if (string.IsNullOrEmpty(t)) return "";
            string s = Regex.Replace(t, @"\s*[\(\[【（](Official|Lyrics|Audio|Video|MV|HD|4K|Live|Explicit|Remaster)[^\)\]】）]*[\)\]】）]", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\s*-\s*(Official|Lyrics).*$", "", RegexOptions.IgnoreCase);
            return s.Trim();
        }

        private static string CleanArtist(string a)
        {
            if (string.IsNullOrEmpty(a)) return "";
            string s = a.Replace(" - Topic", "").Replace("VEVO", "").Trim();
            int comma = s.IndexOf(',');
            if (comma > 0) s = s.Substring(0, comma).Trim();
            return s;
        }

        private static string PrettifyAppId(string appId)
        {
            if (string.IsNullOrEmpty(appId)) return "";
            string low = appId.ToLowerInvariant();
            if (low.IndexOf("youtube") >= 0) return "YouTube Music";
            if (low.IndexOf("chrome") >= 0) return "Chrome";
            if (low.IndexOf("msedge") >= 0 || low.IndexOf("edge") >= 0) return "Edge";
            if (low.IndexOf("spotify") >= 0) return "Spotify";
            if (low.IndexOf("cloudmusic") >= 0 || low.IndexOf("netease") >= 0) return "网易云音乐";
            if (low.IndexOf("qqmusic") >= 0) return "QQ 音乐";
            if (low.IndexOf("apple") >= 0 || low.IndexOf("itunes") >= 0) return "Apple Music";
            if (low.IndexOf("foobar") >= 0) return "foobar2000";
            string name = Path.GetFileNameWithoutExtension(appId);
            if (name.Length > 18) name = name.Substring(0, 18);
            return name;
        }
    }
}
