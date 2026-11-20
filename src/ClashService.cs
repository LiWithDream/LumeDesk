using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace LumeDesk
{
    public class ProxyNodeInfo
    {
        public string Name { get; set; }
        public string CleanName { get; set; }
        public string RegionCode { get; set; }
        public int DelayMs { get; set; }
        public bool IsSelected { get; set; }
    }

    public class ClashSnapshot
    {
        public bool IsConnected { get; set; }
        public string Mode { get; set; }
        public bool TunEnabled { get; set; }
        public string ProfileName { get; set; }
        public string ActiveGroup { get; set; }
        public string ActiveNode { get; set; }
        public int ActiveDelayMs { get; set; }
        public long UpBps { get; set; }
        public long DownBps { get; set; }
        public long SessionTotalBytes { get; set; }
        public int ActiveConnections { get; set; }
        public long SubUsedBytes { get; set; }
        public long SubTotalBytes { get; set; }
        public long SubExpireUnix { get; set; }
        public long[] DownHistory { get; set; }
        public long[] UpHistory { get; set; }

        public ClashSnapshot()
        {
            Mode = "rule";
            ProfileName = "Clash Verge";
            ActiveGroup = "";
            ActiveNode = "未连接代理核心";
            ActiveDelayMs = -1;
            DownHistory = new long[36];
            UpHistory = new long[36];
        }
    }

    public class ClashService : IDisposable
    {
        private const int HistoryLen = 36;

        private readonly object _lock = new object();

        private string _pipeName = "verge-mihomo";
        private string _tcpHost = "127.0.0.1";
        private int _tcpPort = 9097;
        private string _secret = "";
        private string _vergeDir = "";

        private ClashSnapshot _state = new ClashSnapshot();
        private string _cachedSelectorGroup = "";
        private List<ProxyNodeInfo> _cachedGroupNodes = new List<ProxyNodeInfo>();
        private long[] _downRing = new long[HistoryLen];
        private long[] _upRing = new long[HistoryLen];
        private int _ringHead = 0;

        private volatile bool _running = false;
        private bool _autoPingTriggered = false;
        private Thread _trafficThread;
        private Thread _metaThread;
        private Stream _activeTrafficStream;

        public bool IsConnected
        {
            get
            {
                lock (_lock) { return _state.IsConnected; }
            }
        }

        public ClashService()
        {
            _vergeDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "io.github.clash-verge-rev.clash-verge-rev");
            LoadVergeConfigFiles();
        }

        public void Start()
        {
            if (_running) return;
            _running = true;

            _metaThread = new Thread(MetaLoop);
            _metaThread.IsBackground = true;
            _metaThread.Start();

            _trafficThread = new Thread(TrafficLoop);
            _trafficThread.IsBackground = true;
            _trafficThread.Start();
        }

        public void Dispose()
        {
            _running = false;
            Stream s;
            lock (_lock)
            {
                s = _activeTrafficStream;
                _activeTrafficStream = null;
            }
            if (s != null)
            {
                try { s.Dispose(); } catch { }
            }
        }

        public void RefreshOnceSync()
        {
            LoadVergeConfigFiles();
            PollMetadata();
        }

        public ClashSnapshot GetSnapshot()
        {
            lock (_lock)
            {
                ClashSnapshot copy = new ClashSnapshot
                {
                    IsConnected = _state.IsConnected,
                    Mode = _state.Mode,
                    TunEnabled = _state.TunEnabled,
                    ProfileName = _state.ProfileName,
                    ActiveGroup = _state.ActiveGroup,
                    ActiveNode = _state.ActiveNode,
                    ActiveDelayMs = _state.ActiveDelayMs,
                    UpBps = _state.UpBps,
                    DownBps = _state.DownBps,
                    SessionTotalBytes = _state.SessionTotalBytes,
                    ActiveConnections = _state.ActiveConnections,
                    SubUsedBytes = _state.SubUsedBytes,
                    SubTotalBytes = _state.SubTotalBytes,
                    SubExpireUnix = _state.SubExpireUnix,
                    DownHistory = new long[HistoryLen],
                    UpHistory = new long[HistoryLen]
                };
                for (int i = 0; i < HistoryLen; i++)
                {
                    int idx = (_ringHead + i) % HistoryLen;
                    copy.DownHistory[i] = _downRing[idx];
                    copy.UpHistory[i] = _upRing[idx];
                }
                return copy;
            }
        }

        public void PushFallbackSpeed(long downBps, long upBps)
        {
            lock (_lock)
            {
                if (_state.IsConnected) return;
                _state.DownBps = downBps;
                _state.UpBps = upBps;
                _downRing[_ringHead] = downBps;
                _upRing[_ringHead] = upBps;
                _ringHead = (_ringHead + 1) % HistoryLen;
            }
        }

        public void SwitchModeAsync(string mode)
        {
            if (string.IsNullOrEmpty(mode)) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    string body = "{\"mode\":\"" + mode.ToLowerInvariant() + "\"}";
                    int status;
                    HttpRequestOverController("PATCH", "/configs", body, out status);
                    if (status >= 200 && status < 300)
                    {
                        lock (_lock)
                        {
                            _state.Mode = mode.ToLowerInvariant();
                        }
                    }
                    PollMetadata();
                }
                catch
                {
                }
            });
        }

        public void TestActiveNodeDelayAsync()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    string node;
                    lock (_lock) { node = _state.ActiveNode; }
                    if (string.IsNullOrEmpty(node)) return;
                    string enc = Uri.EscapeDataString(node);
                    string resp = HttpRequestOverController("GET", "/proxies/" + enc + "/delay?timeout=5000&url=http://www.gstatic.com/generate_204", null);
                    if (!string.IsNullOrEmpty(resp))
                    {
                        JavaScriptSerializer localJson = new JavaScriptSerializer();
                        Dictionary<string, object> dict = localJson.Deserialize<Dictionary<string, object>>(resp);
                        if (dict != null && dict.ContainsKey("delay"))
                        {
                            int d = Convert.ToInt32(dict["delay"]);
                            lock (_lock) { _state.ActiveDelayMs = d; }
                        }
                    }
                }
                catch
                {
                }
            });
        }

        public List<ProxyNodeInfo> GetActiveGroupNodes(out string selectorGroup)
        {
            lock (_lock)
            {
                selectorGroup = _cachedSelectorGroup ?? "";
                List<ProxyNodeInfo> copy = new List<ProxyNodeInfo>(_cachedGroupNodes.Count);
                for (int i = 0; i < _cachedGroupNodes.Count; i++)
                {
                    ProxyNodeInfo n = _cachedGroupNodes[i];
                    if (!n.IsSelected && IsInfoPseudoNode(n.Name)) continue;
                    copy.Add(new ProxyNodeInfo
                    {
                        Name = n.Name,
                        CleanName = n.CleanName,
                        RegionCode = n.RegionCode,
                        DelayMs = n.DelayMs,
                        IsSelected = n.IsSelected
                    });
                }
                return copy;
            }
        }

        public void SelectProxyNodeAsync(string selectorGroup, string nodeName, Action onDone)
        {
            if (string.IsNullOrEmpty(selectorGroup) || string.IsNullOrEmpty(nodeName)) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    string encGroup = Uri.EscapeDataString(selectorGroup);
                    JavaScriptSerializer localJson = new JavaScriptSerializer();
                    string body = localJson.Serialize(new Dictionary<string, string> { { "name", nodeName } });
                    int status;
                    HttpRequestOverController("PUT", "/proxies/" + encGroup, body, out status);
                    if (status >= 200 && status < 300)
                    {
                        lock (_lock)
                        {
                            _state.ActiveNode = nodeName;
                            _state.ActiveDelayMs = -1;
                        }
                    }
                    PollMetadata();
                    if (status >= 200 && status < 300)
                    {
                        TestActiveNodeDelayAsync();
                    }
                }
                catch
                {
                }
                if (onDone != null)
                {
                    try { onDone(); } catch { }
                }
            });
        }

        public static bool IsInfoPseudoNode(string nodeName)
        {
            if (string.IsNullOrEmpty(nodeName)) return false;
            string s = nodeName.Trim();
            string[] markers = new string[]
            {
                "剩余流量", "距离下次重置", "套餐到期", "过期时间", "到期时间",
                "流量重置", "重置剩余", "官网", "最新网址", "发布页", "订阅更新", "Traffic", "Expire"
            };
            for (int i = 0; i < markers.Length; i++)
            {
                if (s.IndexOf(markers[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        public static void ParseRegionAndCleanNodeName(string rawName, out string regionCode, out string cleanName)
        {
            regionCode = "";
            cleanName = (rawName ?? "").Trim();
            if (string.IsNullOrEmpty(cleanName)) return;

            // Detect Regional Indicator flag emoji pair (U+1F1E6..U+1F1FF -> UTF-16 0xD83C 0xDDE6..0xDDFF)
            StringBuilder sb = new StringBuilder(cleanName.Length);
            int i = 0;
            while (i < cleanName.Length)
            {
                if (i + 3 < cleanName.Length &&
                    cleanName[i] == '\uD83C' && cleanName[i + 1] >= '\uDDE6' && cleanName[i + 1] <= '\uDDFF' &&
                    cleanName[i + 2] == '\uD83C' && cleanName[i + 3] >= '\uDDE6' && cleanName[i + 3] <= '\uDDFF')
                {
                    if (string.IsNullOrEmpty(regionCode))
                    {
                        char c1 = (char)('A' + (cleanName[i + 1] - '\uDDE6'));
                        char c2 = (char)('A' + (cleanName[i + 3] - '\uDDE6'));
                        regionCode = new string(new char[] { c1, c2 });
                    }
                    i += 4;
                    continue;
                }
                sb.Append(cleanName[i]);
                i++;
            }

            string cleaned = sb.ToString().Trim();
            // Fallback region inference if name has recognizable Chinese city/country keywords
            if (string.IsNullOrEmpty(regionCode) && cleaned.Length >= 2)
            {
                if (cleaned.IndexOf("香港") >= 0) regionCode = "HK";
                else if (cleaned.IndexOf("日本") >= 0 || cleaned.IndexOf("东京") >= 0 || cleaned.IndexOf("大阪") >= 0) regionCode = "JP";
                else if (cleaned.IndexOf("新加坡") >= 0 || cleaned.IndexOf("狮城") >= 0) regionCode = "SG";
                else if (cleaned.IndexOf("美国") >= 0 || cleaned.IndexOf("洛杉矶") >= 0 || cleaned.IndexOf("圣何塞") >= 0 || cleaned.IndexOf("西雅图") >= 0) regionCode = "US";
                else if (cleaned.IndexOf("台湾") >= 0 || cleaned.IndexOf("台北") >= 0) regionCode = "TW";
                else if (cleaned.IndexOf("韩国") >= 0 || cleaned.IndexOf("首尔") >= 0) regionCode = "KR";
                else if (cleaned.IndexOf("英国") >= 0 || cleaned.IndexOf("伦敦") >= 0) regionCode = "UK";
                else if (cleaned.IndexOf("德国") >= 0 || cleaned.IndexOf("法兰克福") >= 0) regionCode = "DE";
                else if (cleaned.IndexOf("法国") >= 0 || cleaned.IndexOf("巴黎") >= 0) regionCode = "FR";
                else if (cleaned.IndexOf("加拿大") >= 0 || cleaned.IndexOf("多伦多") >= 0) regionCode = "CA";
                else if (cleaned.IndexOf("澳洲") >= 0 || cleaned.IndexOf("澳大利亚") >= 0 || cleaned.IndexOf("悉尼") >= 0) regionCode = "AU";
                else if (cleaned.IndexOf("荷兰") >= 0 || cleaned.IndexOf("阿姆斯特丹") >= 0) regionCode = "NL";
                else if (cleaned.IndexOf("俄罗斯") >= 0 || cleaned.IndexOf("莫斯科") >= 0) regionCode = "RU";
                else if (cleaned.IndexOf("土耳其") >= 0) regionCode = "TR";
                else if (cleaned.IndexOf("印度") >= 0) regionCode = "IN";
                else if (cleaned.IndexOf("阿根廷") >= 0) regionCode = "AR";
                else if (cleaned.IndexOf("巴西") >= 0) regionCode = "BR";
                else if (cleaned.IndexOf("泰国") >= 0) regionCode = "TH";
                else if (cleaned.IndexOf("越南") >= 0) regionCode = "VN";
                else if (cleaned.IndexOf("马来西亚") >= 0) regionCode = "MY";
            }

            cleanName = string.IsNullOrEmpty(cleaned) ? rawName.Trim() : cleaned;
        }

        private void LoadVergeConfigFiles()
        {
            try
            {
                string cfgFile = Path.Combine(_vergeDir, "config.yaml");
                if (!File.Exists(cfgFile)) cfgFile = Path.Combine(_vergeDir, "clash-verge.yaml");
                if (File.Exists(cfgFile))
                {
                    string[] lines = File.ReadAllLines(cfgFile, Encoding.UTF8);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        string l = lines[i].Trim();
                        if (l.StartsWith("secret:"))
                        {
                            string s = l.Substring(7).Trim().Trim('\'', '"');
                            if (!string.IsNullOrEmpty(s)) _secret = s;
                        }
                        else if (l.StartsWith("external-controller-pipe:"))
                        {
                            string p = l.Substring(25).Trim().Trim('\'', '"');
                            if (p.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase))
                            {
                                _pipeName = p.Substring(9);
                            }
                            else if (!string.IsNullOrEmpty(p))
                            {
                                _pipeName = p;
                            }
                        }
                        else if (l.StartsWith("external-controller:"))
                        {
                            string c = l.Substring(20).Trim().Trim('\'', '"');
                            if (!string.IsNullOrEmpty(c))
                            {
                                int colon = c.LastIndexOf(':');
                                if (colon > 0)
                                {
                                    string host = c.Substring(0, colon).Trim();
                                    int port;
                                    if (int.TryParse(c.Substring(colon + 1).Trim(), out port) && port > 0)
                                    {
                                        _tcpHost = (host == "0.0.0.0" || host == "") ? "127.0.0.1" : host;
                                        _tcpPort = port;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
            }

            // Parse active subscription info from profiles.yaml (name, selected node, remaining GB, expire timestamp)
            try
            {
                string profPath = Path.Combine(_vergeDir, "profiles.yaml");
                if (!File.Exists(profPath)) return;
                string[] lines = File.ReadAllLines(profPath, Encoding.UTF8);
                string currentUid = "";
                for (int i = 0; i < lines.Length; i++)
                {
                    string t = lines[i].Trim();
                    if (t.StartsWith("current:"))
                    {
                        currentUid = t.Substring(8).Trim().Trim('\'', '"');
                        break;
                    }
                }
                if (string.IsNullOrEmpty(currentUid)) return;

                bool inTarget = false;
                bool inExtra = false;
                string profName = "";
                string selGroup = "";
                string selNode = "";
                long up = 0, down = 0, total = 0, expire = 0;

                for (int i = 0; i < lines.Length; i++)
                {
                    string raw = lines[i];
                    string t = raw.Trim();
                    if (t.StartsWith("- uid:"))
                    {
                        string uid = t.Substring(6).Trim().Trim('\'', '"');
                        if (inTarget) break;
                        inTarget = uid.Equals(currentUid, StringComparison.OrdinalIgnoreCase);
                        inExtra = false;
                        continue;
                    }
                    if (!inTarget) continue;

                    if (t.StartsWith("name:") && string.IsNullOrEmpty(profName) && !raw.StartsWith("    -") && !raw.StartsWith("  -"))
                    {
                        string val = t.Substring(5).Trim().Trim('\'', '"');
                        if (!string.IsNullOrEmpty(val) && val != "null") profName = val;
                    }
                    else if (t.StartsWith("- name:") && string.IsNullOrEmpty(selGroup))
                    {
                        selGroup = t.Substring(7).Trim().Trim('\'', '"');
                    }
                    else if (t.StartsWith("now:") && string.IsNullOrEmpty(selNode))
                    {
                        selNode = t.Substring(4).Trim().Trim('\'', '"');
                    }
                    else if (t == "extra:")
                    {
                        inExtra = true;
                    }
                    else if (inExtra)
                    {
                        if (!raw.StartsWith("    ") && t.Length > 0)
                        {
                            inExtra = false;
                            continue;
                        }
                        if (t.StartsWith("upload:")) long.TryParse(t.Substring(7).Trim(), out up);
                        else if (t.StartsWith("download:")) long.TryParse(t.Substring(9).Trim(), out down);
                        else if (t.StartsWith("total:")) long.TryParse(t.Substring(6).Trim(), out total);
                        else if (t.StartsWith("expire:")) long.TryParse(t.Substring(7).Trim(), out expire);
                    }
                }

                lock (_lock)
                {
                    if (!string.IsNullOrEmpty(profName)) _state.ProfileName = profName;
                    if (!string.IsNullOrEmpty(selGroup) && string.IsNullOrEmpty(_state.ActiveGroup)) _state.ActiveGroup = selGroup;
                    if (!string.IsNullOrEmpty(selNode) && !_state.IsConnected) _state.ActiveNode = selNode;
                    if (total > 0)
                    {
                        _state.SubUsedBytes = up + down;
                        _state.SubTotalBytes = total;
                        _state.SubExpireUnix = expire;
                    }
                }
            }
            catch
            {
            }
        }

        private void MetaLoop()
        {
            int tick = 0;
            while (_running)
            {
                try
                {
                    if (tick % 10 == 0) LoadVergeConfigFiles();
                    PollMetadata();
                    tick++;
                }
                catch
                {
                }
                for (int i = 0; i < 40 && _running; i++) Thread.Sleep(100);
            }
        }

        private void PollMetadata()
        {
            JavaScriptSerializer localJson = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 };

            // 1. /configs -> mode + tun
            string cfgJson = HttpRequestOverController("GET", "/configs", null);
            if (string.IsNullOrEmpty(cfgJson))
            {
                lock (_lock) { _state.IsConnected = false; }
                return;
            }

            string mode = "rule";
            bool tun = false;
            try
            {
                Dictionary<string, object> cfg = localJson.Deserialize<Dictionary<string, object>>(cfgJson);
                if (cfg != null)
                {
                    if (cfg.ContainsKey("mode") && cfg["mode"] != null) mode = cfg["mode"].ToString().ToLowerInvariant();
                    if (cfg.ContainsKey("tun") && cfg["tun"] is Dictionary<string, object>)
                    {
                        Dictionary<string, object> td = (Dictionary<string, object>)cfg["tun"];
                        if (td.ContainsKey("enable") && td["enable"] != null) tun = Convert.ToBoolean(td["enable"]);
                    }
                }
            }
            catch
            {
            }

            // 2. /proxies -> resolve active Selector group & node & latest ping delay & selectable nodes
            string groupName = "";
            string nodeName = "";
            int delayMs = -1;
            List<ProxyNodeInfo> parsedNodes = null;
            try
            {
                string profName;
                lock (_lock) { profName = _state.ProfileName; }

                string proxiesJson = HttpRequestOverController("GET", "/proxies", null);
                if (!string.IsNullOrEmpty(proxiesJson))
                {
                    Dictionary<string, object> root = localJson.Deserialize<Dictionary<string, object>>(proxiesJson);
                    if (root != null && root.ContainsKey("proxies") && root["proxies"] is Dictionary<string, object>)
                    {
                        Dictionary<string, object> proxies = (Dictionary<string, object>)root["proxies"];
                        Dictionary<string, object> chosenGroup = null;

                        // Priority 1: match active profile name or previously selected group
                        if (!string.IsNullOrEmpty(profName) && proxies.ContainsKey(profName) && proxies[profName] is Dictionary<string, object>)
                        {
                            chosenGroup = (Dictionary<string, object>)proxies[profName];
                            groupName = profName;
                        }
                        // Priority 2: GLOBAL or first non-built-in Selector group
                        if (chosenGroup == null)
                        {
                            foreach (KeyValuePair<string, object> kv in proxies)
                            {
                                Dictionary<string, object> pd = kv.Value as Dictionary<string, object>;
                                if (pd == null) continue;
                                string pType = pd.ContainsKey("type") && pd["type"] != null ? pd["type"].ToString() : "";
                                if (pType == "Selector" && kv.Key != "GLOBAL" && pd.ContainsKey("now"))
                                {
                                    chosenGroup = pd;
                                    groupName = kv.Key;
                                    break;
                                }
                            }
                        }
                        if (chosenGroup == null && proxies.ContainsKey("GLOBAL"))
                        {
                            chosenGroup = proxies["GLOBAL"] as Dictionary<string, object>;
                            groupName = "GLOBAL";
                        }

                        if (chosenGroup != null && chosenGroup.ContainsKey("now") && chosenGroup["now"] != null)
                        {
                            string directSelected = chosenGroup["now"].ToString();
                            nodeName = directSelected;

                            // Build selectable node list for one-click desktop switcher
                            System.Collections.IList allList = chosenGroup.ContainsKey("all") ? (chosenGroup["all"] as System.Collections.IList) : null;
                            if (allList != null && allList.Count > 0)
                            {
                                parsedNodes = new List<ProxyNodeInfo>(allList.Count);
                                for (int ni = 0; ni < allList.Count; ni++)
                                {
                                    string nName = allList[ni] != null ? allList[ni].ToString() : "";
                                    if (string.IsNullOrEmpty(nName)) continue;
                                    int nDelay = -1;
                                    if (proxies.ContainsKey(nName))
                                    {
                                        Dictionary<string, object> nd = proxies[nName] as Dictionary<string, object>;
                                        nDelay = ExtractLatestDelay(nd);
                                        if (nDelay < 0 && nd != null && nd.ContainsKey("now") && nd["now"] != null)
                                        {
                                            string subNow = nd["now"].ToString();
                                            if (proxies.ContainsKey(subNow))
                                            {
                                                nDelay = ExtractLatestDelay(proxies[subNow] as Dictionary<string, object>);
                                            }
                                        }
                                    }
                                    string reg, cln;
                                    ParseRegionAndCleanNodeName(nName, out reg, out cln);
                                    parsedNodes.Add(new ProxyNodeInfo
                                    {
                                        Name = nName,
                                        CleanName = cln,
                                        RegionCode = reg,
                                        DelayMs = nDelay,
                                        IsSelected = nName.Equals(directSelected, StringComparison.Ordinal)
                                    });
                                }
                            }

                            // Follow nested Selector/URLTest up to 3 hops to find the real leaf node + delay!
                            string leaf = nodeName;
                            for (int hop = 0; hop < 3; hop++)
                            {
                                if (!proxies.ContainsKey(leaf)) break;
                                Dictionary<string, object> ld = proxies[leaf] as Dictionary<string, object>;
                                if (ld == null) break;
                                if (ld.ContainsKey("now") && ld["now"] != null && !string.IsNullOrEmpty(ld["now"].ToString()))
                                {
                                    leaf = ld["now"].ToString();
                                    nodeName = leaf;
                                }
                                else
                                {
                                    delayMs = ExtractLatestDelay(ld);
                                    break;
                                }
                            }
                            if (delayMs < 0 && proxies.ContainsKey(nodeName))
                            {
                                delayMs = ExtractLatestDelay(proxies[nodeName] as Dictionary<string, object>);
                            }
                        }
                    }
                }
            }
            catch
            {
            }

            // 3. /connections -> total session traffic & active connections
            long sessionTotal = 0;
            int connCount = 0;
            try
            {
                string connJson = HttpRequestOverController("GET", "/connections", null);
                if (!string.IsNullOrEmpty(connJson))
                {
                    // Lightweight regex extract of top-level downloadTotal / uploadTotal without allocating thousands of connection objects!
                    Match md = Regex.Match(connJson, "\"downloadTotal\"\\s*:\\s*(\\d+)");
                    Match mu = Regex.Match(connJson, "\"uploadTotal\"\\s*:\\s*(\\d+)");
                    long dTot = 0, uTot = 0;
                    if (md.Success) long.TryParse(md.Groups[1].Value, out dTot);
                    if (mu.Success) long.TryParse(mu.Groups[1].Value, out uTot);
                    sessionTotal = dTot + uTot;
                    connCount = Regex.Matches(connJson, "\"id\"\\s*:\\s*\"").Count;
                }
            }
            catch
            {
            }

            lock (_lock)
            {
                _state.IsConnected = true;
                _state.Mode = mode;
                _state.TunEnabled = tun;
                if (!string.IsNullOrEmpty(groupName))
                {
                    _state.ActiveGroup = groupName;
                    _cachedSelectorGroup = groupName;
                }
                if (parsedNodes != null)
                {
                    _cachedGroupNodes = parsedNodes;
                }
                if (!string.IsNullOrEmpty(nodeName)) _state.ActiveNode = nodeName;
                if (delayMs >= 0) _state.ActiveDelayMs = delayMs;
                if (sessionTotal > 0) _state.SessionTotalBytes = sessionTotal;
                _state.ActiveConnections = connCount;
            }

            if (delayMs < 0 && !string.IsNullOrEmpty(nodeName) && !_autoPingTriggered)
            {
                _autoPingTriggered = true;
                TestActiveNodeDelayAsync();
            }
        }

        private static int ExtractLatestDelay(Dictionary<string, object> nodeDict)
        {
            if (nodeDict == null || !nodeDict.ContainsKey("history")) return -1;
            System.Collections.IList hist = nodeDict["history"] as System.Collections.IList;
            if (hist == null || hist.Count == 0) return -1;
            for (int i = hist.Count - 1; i >= 0; i--)
            {
                Dictionary<string, object> entry = hist[i] as Dictionary<string, object>;
                if (entry != null && entry.ContainsKey("delay") && entry["delay"] != null)
                {
                    int d = Convert.ToInt32(entry["delay"]);
                    if (d > 0) return d;
                }
            }
            return -1;
        }

        private void TrafficLoop()
        {
            while (_running)
            {
                Stream stream = null;
                try
                {
                    stream = OpenControllerStream(2000);
                    if (stream == null)
                    {
                        for (int i = 0; i < 20 && _running; i++) Thread.Sleep(100);
                        continue;
                    }
                    lock (_lock) { _activeTrafficStream = stream; }

                    StringBuilder req = new StringBuilder();
                    req.Append("GET /traffic HTTP/1.1\r\n");
                    req.Append("Host: localhost\r\n");
                    if (!string.IsNullOrEmpty(_secret))
                    {
                        req.Append("Authorization: Bearer ").Append(_secret).Append("\r\n");
                    }
                    req.Append("\r\n");
                    byte[] reqBytes = Encoding.ASCII.GetBytes(req.ToString());
                    stream.Write(reqBytes, 0, reqBytes.Length);
                    stream.Flush();

                    using (StreamReader sr = new StreamReader(stream, Encoding.UTF8))
                    {
                        // Skip HTTP response headers
                        string line;
                        while (_running && (line = sr.ReadLine()) != null)
                        {
                            if (line.Length == 0) break;
                        }

                        // Stream live NDJSON lines: {"up":1234,"down":56789}
                        while (_running && (line = sr.ReadLine()) != null)
                        {
                            string trimmed = line.Trim();
                            if (!trimmed.StartsWith("{")) continue;
                            Match mu = Regex.Match(trimmed, "\"up\"\\s*:\\s*(\\d+)");
                            Match md = Regex.Match(trimmed, "\"down\"\\s*:\\s*(\\d+)");
                            if (mu.Success && md.Success)
                            {
                                long up = 0, down = 0;
                                long.TryParse(mu.Groups[1].Value, out up);
                                long.TryParse(md.Groups[1].Value, out down);
                                lock (_lock)
                                {
                                    _state.IsConnected = true;
                                    _state.UpBps = up;
                                    _state.DownBps = down;
                                    _downRing[_ringHead] = down;
                                    _upRing[_ringHead] = up;
                                    _ringHead = (_ringHead + 1) % HistoryLen;
                                }
                            }
                        }
                    }
                }
                catch
                {
                    for (int i = 0; i < 20 && _running; i++) Thread.Sleep(100);
                }
                finally
                {
                    lock (_lock)
                    {
                        if (_activeTrafficStream == stream) _activeTrafficStream = null;
                    }
                    if (stream != null)
                    {
                        try { stream.Dispose(); } catch { }
                    }
                }
            }
        }

        private Stream OpenControllerStream(int timeoutMs)
        {
            // 1. Prefer Mihomo Named Pipe (Clash Verge Rev default on Windows!)
            if (!string.IsNullOrEmpty(_pipeName))
            {
                NamedPipeClientStream pipe = null;
                try
                {
                    pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut);
                    pipe.Connect(timeoutMs);
                    return pipe;
                }
                catch
                {
                    if (pipe != null) { try { pipe.Dispose(); } catch { } }
                }
            }

            // 2. Fallback to TCP external-controller (e.g. 127.0.0.1:9097)
            if (!string.IsNullOrEmpty(_tcpHost) && _tcpPort > 0)
            {
                TcpClient tcp = null;
                try
                {
                    tcp = new TcpClient();
                    IAsyncResult ar = tcp.BeginConnect(_tcpHost, _tcpPort, null, null);
                    bool signaled = false;
                    try
                    {
                        signaled = ar.AsyncWaitHandle.WaitOne(timeoutMs);
                    }
                    finally
                    {
                        try { ar.AsyncWaitHandle.Close(); } catch { }
                    }
                    if (signaled)
                    {
                        tcp.EndConnect(ar);
                        tcp.ReceiveTimeout = 3000;
                        tcp.SendTimeout = 2000;
                        return tcp.GetStream();
                    }
                    tcp.Close();
                }
                catch
                {
                    if (tcp != null) { try { tcp.Close(); } catch { } }
                }
            }

            return null;
        }

        private string HttpRequestOverController(string method, string path, string jsonBody)
        {
            int status;
            return HttpRequestOverController(method, path, jsonBody, out status);
        }

        private string HttpRequestOverController(string method, string path, string jsonBody, out int statusCode)
        {
            statusCode = 0;
            using (Stream s = OpenControllerStream(1500))
            {
                if (s == null) return null;

                byte[] bodyBytes = string.IsNullOrEmpty(jsonBody) ? new byte[0] : Encoding.UTF8.GetBytes(jsonBody);
                StringBuilder req = new StringBuilder();
                req.Append(method).Append(" ").Append(path).Append(" HTTP/1.1\r\n");
                req.Append("Host: localhost\r\n");
                if (!string.IsNullOrEmpty(_secret))
                {
                    req.Append("Authorization: Bearer ").Append(_secret).Append("\r\n");
                }
                if (bodyBytes.Length > 0)
                {
                    req.Append("Content-Type: application/json\r\n");
                    req.Append("Content-Length: ").Append(bodyBytes.Length).Append("\r\n");
                }
                req.Append("Connection: close\r\n\r\n");

                byte[] headerBytes = Encoding.ASCII.GetBytes(req.ToString());
                s.Write(headerBytes, 0, headerBytes.Length);
                if (bodyBytes.Length > 0) s.Write(bodyBytes, 0, bodyBytes.Length);
                s.Flush();

                MemoryStream ms = new MemoryStream();
                byte[] buf = new byte[8192];
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    ms.Write(buf, 0, n);
                }

                byte[] all = ms.ToArray();
                int sep = FindHeaderBodySeparator(all);
                if (sep < 0) return null;

                string headerStr = Encoding.ASCII.GetString(all, 0, sep);
                Match sm = Regex.Match(headerStr, @"^HTTP/\d+\.\d+\s+(\d{3})");
                if (sm.Success) int.TryParse(sm.Groups[1].Value, out statusCode);
                bool isChunked = headerStr.IndexOf("Transfer-Encoding: chunked", StringComparison.OrdinalIgnoreCase) >= 0;

                int bodyOffset = sep + 4;
                int bodyLen = all.Length - bodyOffset;
                if (bodyLen <= 0) return "";

                if (!isChunked)
                {
                    return Encoding.UTF8.GetString(all, bodyOffset, bodyLen);
                }

                return DecodeChunkedBody(all, bodyOffset);
            }
        }

        private static int FindHeaderBodySeparator(byte[] data)
        {
            for (int i = 0; i < data.Length - 3; i++)
            {
                if (data[i] == 13 && data[i + 1] == 10 && data[i + 2] == 13 && data[i + 3] == 10)
                    return i;
            }
            return -1;
        }

        private static string DecodeChunkedBody(byte[] data, int offset)
        {
            MemoryStream outMs = new MemoryStream();
            int pos = offset;
            while (pos < data.Length)
            {
                int lineEnd = -1;
                for (int i = pos; i < data.Length - 1; i++)
                {
                    if (data[i] == 13 && data[i + 1] == 10)
                    {
                        lineEnd = i;
                        break;
                    }
                }
                if (lineEnd < 0) break;

                string sizeHex = Encoding.ASCII.GetString(data, pos, lineEnd - pos).Trim();
                int semi = sizeHex.IndexOf(';');
                if (semi >= 0) sizeHex = sizeHex.Substring(0, semi).Trim();

                int chunkSize;
                if (!int.TryParse(sizeHex, System.Globalization.NumberStyles.HexNumber, null, out chunkSize) || chunkSize <= 0)
                    break;

                pos = lineEnd + 2;
                int avail = Math.Min(chunkSize, data.Length - pos);
                if (avail > 0) outMs.Write(data, pos, avail);
                pos += chunkSize + 2; // skip trailing \r\n
            }
            return Encoding.UTF8.GetString(outMs.ToArray());
        }

        public static string FormatSpeed(long bps)
        {
            if (bps < 1024) return bps + " B/s";
            if (bps < 1024 * 1024) return string.Format("{0:F1} KB/s", bps / 1024.0);
            return string.Format("{0:F2} MB/s", bps / (1024.0 * 1024.0));
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024L * 1024L) return string.Format("{0:F0} KB", bytes / 1024.0);
            if (bytes < 1024L * 1024L * 1024L) return string.Format("{0:F1} MB", bytes / (1024.0 * 1024.0));
            return string.Format("{0:F1} GB", bytes / (1024.0 * 1024.0 * 1024.0));
        }
    }
}
