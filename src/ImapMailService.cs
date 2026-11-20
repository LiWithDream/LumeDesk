using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace LumeDesk
{
    public class MailMessageSummary
    {
        public string Sender { get; set; }
        public string Subject { get; set; }
        public string TimeText { get; set; }
        public bool IsUnread { get; set; }
        public string ThreadUrl { get; set; }
    }

    public class MailSnapshot
    {
        public bool IsConfigured { get; set; }
        public bool IsConnected { get; set; }
        public bool IsLoading { get; set; }
        public string AccountEmail { get; set; }
        public int UnreadCount { get; set; }
        public string StatusMessage { get; set; }
        public List<MailMessageSummary> Messages { get; set; }

        public MailSnapshot()
        {
            AccountEmail = "";
            StatusMessage = "支持 Gmail 浏览器一键授权 (OAuth) 或 IMAP";
            Messages = new List<MailMessageSummary>();
        }
    }

    public class ImapMailService : IDisposable
    {
        private readonly object _lock = new object();
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
        private AppConfig _config;
        private MailSnapshot _state = new MailSnapshot();
        private volatile bool _running = false;
        private Thread _pollThread;
        private AutoResetEvent _wakeEvent = new AutoResetEvent(false);

        private string _oauthAccessToken = "";
        private DateTime _oauthTokenExpiryUtc = DateTime.MinValue;
        private volatile TcpListener _activeAuthListener;

        public ImapMailService(AppConfig config)
        {
            _config = config;
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }
            UpdateConfiguredState();
        }

        public static string ProtectSecret(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            try
            {
                byte[] raw = Encoding.UTF8.GetBytes(plain);
                byte[] enc = ProtectedData.Protect(raw, null, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(enc);
            }
            catch
            {
                return "";
            }
        }

        public static string UnprotectSecret(string b64)
        {
            if (string.IsNullOrEmpty(b64)) return "";
            try
            {
                byte[] enc = Convert.FromBase64String(b64);
                byte[] raw = ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(raw);
            }
            catch
            {
                return "";
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
            CancelPendingOAuth();
            try { _wakeEvent.Set(); } catch { }
        }

        public void CancelPendingOAuth()
        {
            TcpListener l = _activeAuthListener;
            _activeAuthListener = null;
            if (l != null)
            {
                try { l.Stop(); } catch { }
            }
        }

        public void TriggerRefresh()
        {
            UpdateConfiguredState();
            try { _wakeEvent.Set(); } catch { }
        }

        public MailSnapshot GetSnapshot()
        {
            lock (_lock)
            {
                MailSnapshot copy = new MailSnapshot
                {
                    IsConfigured = _state.IsConfigured,
                    IsConnected = _state.IsConnected,
                    IsLoading = _state.IsLoading,
                    AccountEmail = _state.AccountEmail,
                    UnreadCount = _state.UnreadCount,
                    StatusMessage = _state.StatusMessage,
                    Messages = _state.Messages != null
                        ? new List<MailMessageSummary>(_state.Messages)
                        : new List<MailMessageSummary>()
                };
                return copy;
            }
        }

        public static bool ParseGoogleClientSecretJson(string filePath, out string clientId, out string clientSecret)
        {
            clientId = "";
            clientSecret = "";
            try
            {
                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return false;
                string raw = File.ReadAllText(filePath, Encoding.UTF8);
                JavaScriptSerializer js = new JavaScriptSerializer();
                Dictionary<string, object> root = js.Deserialize<Dictionary<string, object>>(raw);
                if (root == null) return false;

                Dictionary<string, object> node = null;
                if (root.ContainsKey("installed") && root["installed"] is Dictionary<string, object>)
                    node = (Dictionary<string, object>)root["installed"];
                else if (root.ContainsKey("web") && root["web"] is Dictionary<string, object>)
                    node = (Dictionary<string, object>)root["web"];
                else
                    node = root;

                if (node != null && node.ContainsKey("client_id") && node["client_id"] != null)
                    clientId = node["client_id"].ToString().Trim();
                if (node != null && node.ContainsKey("client_secret") && node["client_secret"] != null)
                    clientSecret = node["client_secret"].ToString().Trim();

                return !string.IsNullOrEmpty(clientId);
            }
            catch
            {
                return false;
            }
        }

        public void BeginGmailOAuthAsync(string clientId, string clientSecret, Action<bool, string> onDone)
        {
            CancelPendingOAuth();
            if (string.IsNullOrWhiteSpace(clientId))
            {
                if (onDone != null) onDone(false, "请先导入 Google 凭据 JSON 或填写 Client ID");
                return;
            }

            Thread t = new Thread((ThreadStart)delegate
            {
                TcpListener listener = null;
                try
                {
                    listener = new TcpListener(IPAddress.Loopback, 0);
                    listener.Start();
                    _activeAuthListener = listener;
                    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                    string redirectUri = "http://127.0.0.1:" + port + "/";

                    string verifier = GenerateRandomUrlSafe(48);
                    string challenge = ComputePkceS256(verifier);
                    string state = GenerateRandomUrlSafe(24);

                    string authUrl =
                        "https://accounts.google.com/o/oauth2/v2/auth" +
                        "?client_id=" + Uri.EscapeDataString(clientId.Trim()) +
                        "&redirect_uri=" + Uri.EscapeDataString(redirectUri) +
                        "&response_type=code" +
                        "&scope=" + Uri.EscapeDataString("https://www.googleapis.com/auth/gmail.readonly") +
                        "&access_type=offline" +
                        "&prompt=consent" +
                        "&state=" + Uri.EscapeDataString(state) +
                        "&code_challenge=" + Uri.EscapeDataString(challenge) +
                        "&code_challenge_method=S256";

                    Process.Start(authUrl);

                    string authCode = null;
                    string oauthErr = null;
                    DateTime deadline = DateTime.UtcNow.AddMinutes(3);

                    while (DateTime.UtcNow < deadline && _activeAuthListener == listener)
                    {
                        if (!listener.Pending())
                        {
                            Thread.Sleep(80);
                            continue;
                        }
                        using (TcpClient client = listener.AcceptTcpClient())
                        {
                            client.ReceiveTimeout = 5000;
                            client.SendTimeout = 5000;
                            using (NetworkStream ns = client.GetStream())
                            using (StreamReader sr = new StreamReader(ns, Encoding.ASCII))
                            {
                                string reqLine = sr.ReadLine();
                                string hLine;
                                while (!string.IsNullOrEmpty(hLine = sr.ReadLine())) { }

                                if (string.IsNullOrEmpty(reqLine)) continue;
                                string[] parts = reqLine.Split(' ');
                                string reqPath = (parts.Length >= 2) ? parts[1] : "/";
                                if (reqPath.StartsWith("/favicon", StringComparison.OrdinalIgnoreCase))
                                {
                                    WriteHttpHtml(ns, 404, "Not Found");
                                    continue;
                                }

                                Dictionary<string, string> q = ParseQueryString(reqPath);
                                string retState;
                                q.TryGetValue("state", out retState);
                                if (q.ContainsKey("error"))
                                {
                                    oauthErr = q["error"];
                                    WriteHttpHtml(ns, 200, BuildCallbackHtml(false, "授权已取消或被拒绝：" + oauthErr));
                                    break;
                                }
                                if (q.ContainsKey("code") && retState == state)
                                {
                                    authCode = q["code"];
                                    WriteHttpHtml(ns, 200, BuildCallbackHtml(true, "Gmail 授权成功！可以关闭此标签页并返回桌面 LumeDesk。"));
                                    break;
                                }
                                WriteHttpHtml(ns, 400, BuildCallbackHtml(false, "无效的回调参数，请重试。"));
                            }
                        }
                    }

                    try { listener.Stop(); } catch { }
                    if (_activeAuthListener == listener) _activeAuthListener = null;

                    if (!string.IsNullOrEmpty(oauthErr))
                    {
                        if (onDone != null) onDone(false, "Google 授权返回错误：" + oauthErr);
                        return;
                    }
                    if (string.IsNullOrEmpty(authCode))
                    {
                        if (onDone != null) onDone(false, "授权等待超时或已取消");
                        return;
                    }

                    StringBuilder form = new StringBuilder();
                    form.Append("code=").Append(Uri.EscapeDataString(authCode));
                    form.Append("&client_id=").Append(Uri.EscapeDataString(clientId.Trim()));
                    if (!string.IsNullOrEmpty(clientSecret))
                        form.Append("&client_secret=").Append(Uri.EscapeDataString(clientSecret.Trim()));
                    form.Append("&redirect_uri=").Append(Uri.EscapeDataString(redirectUri));
                    form.Append("&code_verifier=").Append(Uri.EscapeDataString(verifier));
                    form.Append("&grant_type=authorization_code");

                    string tokJson = HttpPostForm("https://oauth2.googleapis.com/token", form.ToString(), 12000);
                    Dictionary<string, object> tok = _json.Deserialize<Dictionary<string, object>>(tokJson);
                    if (tok == null || !tok.ContainsKey("access_token"))
                    {
                        if (onDone != null) onDone(false, "换取 Token 失败，请核对 Client ID / Secret");
                        return;
                    }

                    string accessToken = tok["access_token"].ToString();
                    string refreshToken = (tok.ContainsKey("refresh_token") && tok["refresh_token"] != null)
                        ? tok["refresh_token"].ToString() : "";
                    int expiresIn = (tok.ContainsKey("expires_in") && tok["expires_in"] != null)
                        ? Convert.ToInt32(tok["expires_in"]) : 3500;

                    string authedEmail = _config.MailEmail ?? "";
                    try
                    {
                        string profJson = HttpGetWithBearer("https://gmail.googleapis.com/gmail/v1/users/me/profile", accessToken, 10000);
                        Dictionary<string, object> prof = _json.Deserialize<Dictionary<string, object>>(profJson);
                        if (prof != null && prof.ContainsKey("emailAddress") && prof["emailAddress"] != null)
                            authedEmail = prof["emailAddress"].ToString();
                    }
                    catch { }

                    _oauthAccessToken = accessToken;
                    _oauthTokenExpiryUtc = DateTime.UtcNow.AddSeconds(Math.Max(60, expiresIn - 90));

                    _config.MailProvider = "oauth";
                    _config.GmailClientId = clientId.Trim();
                    _config.GmailClientSecretDpapi = ProtectSecret((clientSecret ?? "").Trim());
                    if (!string.IsNullOrEmpty(refreshToken))
                        _config.GmailRefreshTokenDpapi = ProtectSecret(refreshToken);
                    if (!string.IsNullOrEmpty(authedEmail))
                        _config.MailEmail = authedEmail;
                    ConfigManager.Save(_config);

                    TriggerRefresh();
                    if (onDone != null) onDone(true, "授权成功：" + authedEmail);
                }
                catch (Exception ex)
                {
                    if (listener != null) { try { listener.Stop(); } catch { } }
                    if (_activeAuthListener == listener) _activeAuthListener = null;
                    if (onDone != null) onDone(false, "授权出错：" + ex.Message);
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        private bool IsOAuthConfigured()
        {
            return !string.IsNullOrEmpty(_config.GmailClientId) &&
                   (!string.IsNullOrEmpty(_config.GmailRefreshTokenDpapi) || !string.IsNullOrEmpty(_oauthAccessToken));
        }

        private bool IsImapConfigured()
        {
            return !string.IsNullOrEmpty(_config.MailImapHost) &&
                   !string.IsNullOrEmpty(_config.MailEmail) &&
                   !string.IsNullOrEmpty(_config.MailPasswordDpapi);
        }

        private void UpdateConfiguredState()
        {
            bool ok = IsOAuthConfigured() || IsImapConfigured();
            lock (_lock)
            {
                _state.IsConfigured = ok;
                _state.AccountEmail = _config.MailEmail ?? "";
                if (!ok)
                {
                    _state.IsConnected = false;
                    _state.UnreadCount = 0;
                    _state.Messages.Clear();
                    _state.StatusMessage = "支持 Gmail 浏览器一键授权 (OAuth) 或 IMAP";
                }
            }
        }

        private void PollLoop()
        {
            while (_running)
            {
                UpdateConfiguredState();
                bool configured;
                lock (_lock) { configured = _state.IsConfigured; }

                if (configured)
                {
                    if (IsOAuthConfigured() && !string.Equals(_config.MailProvider, "imap", StringComparison.OrdinalIgnoreCase))
                        FetchGmailApiOnce();
                    else if (IsImapConfigured())
                        FetchInboxOnce();
                    else if (IsOAuthConfigured())
                        FetchGmailApiOnce();
                }

                _wakeEvent.WaitOne(60000);
            }
        }

        private string EnsureOAuthAccessToken()
        {
            if (!string.IsNullOrEmpty(_oauthAccessToken) && DateTime.UtcNow < _oauthTokenExpiryUtc)
                return _oauthAccessToken;

            string refreshToken = UnprotectSecret(_config.GmailRefreshTokenDpapi);
            string clientId = (_config.GmailClientId ?? "").Trim();
            string clientSecret = UnprotectSecret(_config.GmailClientSecretDpapi);
            if (string.IsNullOrEmpty(refreshToken) || string.IsNullOrEmpty(clientId))
                return null;

            StringBuilder form = new StringBuilder();
            form.Append("client_id=").Append(Uri.EscapeDataString(clientId));
            if (!string.IsNullOrEmpty(clientSecret))
                form.Append("&client_secret=").Append(Uri.EscapeDataString(clientSecret));
            form.Append("&refresh_token=").Append(Uri.EscapeDataString(refreshToken));
            form.Append("&grant_type=refresh_token");

            string resp = HttpPostForm("https://oauth2.googleapis.com/token", form.ToString(), 12000);
            Dictionary<string, object> tok = _json.Deserialize<Dictionary<string, object>>(resp);
            if (tok == null || !tok.ContainsKey("access_token") || tok["access_token"] == null)
                throw new Exception("OAuth 令牌刷新失败，请重新授权");

            _oauthAccessToken = tok["access_token"].ToString();
            int expiresIn = (tok.ContainsKey("expires_in") && tok["expires_in"] != null)
                ? Convert.ToInt32(tok["expires_in"]) : 3500;
            _oauthTokenExpiryUtc = DateTime.UtcNow.AddSeconds(Math.Max(60, expiresIn - 90));
            return _oauthAccessToken;
        }

        private void FetchGmailApiOnce()
        {
            lock (_lock)
            {
                _state.IsLoading = true;
                if (!_state.IsConnected) _state.StatusMessage = "正在同步 Gmail API ...";
            }

            try
            {
                string token = EnsureOAuthAccessToken();
                if (string.IsNullOrEmpty(token))
                    throw new Exception("请重新点击浏览器授权登录");

                int unreadCount = 0;
                try
                {
                    string lblJson = HttpGetWithBearer("https://gmail.googleapis.com/gmail/v1/users/me/labels/INBOX", token, 10000);
                    Dictionary<string, object> lbl = _json.Deserialize<Dictionary<string, object>>(lblJson);
                    if (lbl != null)
                    {
                        if (lbl.ContainsKey("messagesUnread") && lbl["messagesUnread"] != null)
                            unreadCount = Convert.ToInt32(lbl["messagesUnread"]);
                        else if (lbl.ContainsKey("threadsUnread") && lbl["threadsUnread"] != null)
                            unreadCount = Convert.ToInt32(lbl["threadsUnread"]);
                    }
                }
                catch { }

                string listJson = HttpGetWithBearer("https://gmail.googleapis.com/gmail/v1/users/me/messages?labelIds=INBOX&maxResults=6", token, 10000);
                Dictionary<string, object> listObj = _json.Deserialize<Dictionary<string, object>>(listJson);
                List<MailMessageSummary> summaries = new List<MailMessageSummary>();

                if (listObj != null && listObj.ContainsKey("messages") && listObj["messages"] is System.Collections.IList)
                {
                    System.Collections.IList arr = (System.Collections.IList)listObj["messages"];
                    for (int i = 0; i < arr.Count && i < 6; i++)
                    {
                        Dictionary<string, object> entry = arr[i] as Dictionary<string, object>;
                        if (entry == null || !entry.ContainsKey("id") || entry["id"] == null) continue;
                        string msgId = entry["id"].ToString();
                        string threadId = (entry.ContainsKey("threadId") && entry["threadId"] != null)
                            ? entry["threadId"].ToString() : msgId;

                        try
                        {
                            string msgUrl = "https://gmail.googleapis.com/gmail/v1/users/me/messages/" + Uri.EscapeDataString(msgId) +
                                            "?format=metadata&metadataHeaders=From&metadataHeaders=Subject&metadataHeaders=Date";
                            string msgJson = HttpGetWithBearer(msgUrl, token, 8000);
                            Dictionary<string, object> msg = _json.Deserialize<Dictionary<string, object>>(msgJson);
                            if (msg == null) continue;

                            bool isUnread = false;
                            if (msg.ContainsKey("labelIds") && msg["labelIds"] is System.Collections.IList)
                            {
                                System.Collections.IList lIds = (System.Collections.IList)msg["labelIds"];
                                for (int k = 0; k < lIds.Count; k++)
                                {
                                    if (lIds[k] != null && lIds[k].ToString().Equals("UNREAD", StringComparison.OrdinalIgnoreCase))
                                    {
                                        isUnread = true;
                                        break;
                                    }
                                }
                            }

                            string fromRaw = "", subjRaw = "", dateRaw = "";
                            Dictionary<string, object> payload = msg.ContainsKey("payload") ? (msg["payload"] as Dictionary<string, object>) : null;
                            if (payload != null && payload.ContainsKey("headers") && payload["headers"] is System.Collections.IList)
                            {
                                System.Collections.IList hdrs = (System.Collections.IList)payload["headers"];
                                for (int h = 0; h < hdrs.Count; h++)
                                {
                                    Dictionary<string, object> hd = hdrs[h] as Dictionary<string, object>;
                                    if (hd == null || !hd.ContainsKey("name") || !hd.ContainsKey("value")) continue;
                                    string hn = (hd["name"] ?? "").ToString();
                                    string hv = (hd["value"] ?? "").ToString();
                                    if (hn.Equals("From", StringComparison.OrdinalIgnoreCase)) fromRaw = hv;
                                    else if (hn.Equals("Subject", StringComparison.OrdinalIgnoreCase)) subjRaw = hv;
                                    else if (hn.Equals("Date", StringComparison.OrdinalIgnoreCase)) dateRaw = hv;
                                }
                            }

                            string sender = CleanSender(fromRaw);
                            string subject = string.IsNullOrWhiteSpace(subjRaw) ? "(无主题)" : subjRaw.Trim();
                            if (string.IsNullOrWhiteSpace(sender)) sender = "未知发件人";

                            summaries.Add(new MailMessageSummary
                            {
                                Sender = sender,
                                Subject = subject,
                                TimeText = FormatMailTime(dateRaw),
                                IsUnread = isUnread,
                                ThreadUrl = "https://mail.google.com/mail/u/0/#inbox/" + Uri.EscapeDataString(threadId)
                            });
                        }
                        catch { }
                    }
                }

                lock (_lock)
                {
                    _state.IsConnected = true;
                    _state.IsLoading = false;
                    _state.UnreadCount = unreadCount;
                    _state.Messages = summaries;
                    _state.StatusMessage = "Gmail API 已同步 " + DateTime.Now.ToString("HH:mm");
                }
            }
            catch (Exception ex)
            {
                lock (_lock)
                {
                    _state.IsLoading = false;
                    _state.StatusMessage = ex.Message;
                }
            }
        }

        private static string HttpPostForm(string url, string body, int timeoutMs)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.ContentType = "application/x-www-form-urlencoded";
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            byte[] data = Encoding.UTF8.GetBytes(body);
            req.ContentLength = data.Length;
            using (Stream s = req.GetRequestStream()) { s.Write(data, 0, data.Length); }
            try
            {
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (Stream rs = resp.GetResponseStream())
                using (StreamReader sr = new StreamReader(rs, Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            catch (WebException wex)
            {
                if (wex.Response != null)
                {
                    using (Stream rs = wex.Response.GetResponseStream())
                    using (StreamReader sr = new StreamReader(rs, Encoding.UTF8))
                        throw new Exception(ExtractGoogleError(sr.ReadToEnd(), wex.Message));
                }
                throw;
            }
        }

        private static string HttpGetWithBearer(string url, string token, int timeoutMs)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.Headers["Authorization"] = "Bearer " + token;
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            try
            {
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (Stream rs = resp.GetResponseStream())
                using (StreamReader sr = new StreamReader(rs, Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            catch (WebException wex)
            {
                if (wex.Response != null)
                {
                    using (Stream rs = wex.Response.GetResponseStream())
                    using (StreamReader sr = new StreamReader(rs, Encoding.UTF8))
                        throw new Exception(ExtractGoogleError(sr.ReadToEnd(), wex.Message));
                }
                throw;
            }
        }

        private static string ExtractGoogleError(string body, string fallback)
        {
            if (string.IsNullOrEmpty(body)) return fallback;
            Match m = Regex.Match(body, "\"(?:error_description|message)\"\\s*:\\s*\"([^\"]+)\"");
            return m.Success ? m.Groups[1].Value : fallback;
        }

        private static string GenerateRandomUrlSafe(int byteCount)
        {
            byte[] buf = new byte[byteCount];
            using (RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider()) rng.GetBytes(buf);
            return Base64UrlEncode(buf);
        }

        private static string ComputePkceS256(string verifier)
        {
            using (SHA256 sha = SHA256.Create())
                return Base64UrlEncode(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
        }

        private static string Base64UrlEncode(byte[] data)
        {
            return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static Dictionary<string, string> ParseQueryString(string reqPath)
        {
            Dictionary<string, string> dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int qIdx = reqPath.IndexOf('?');
            if (qIdx < 0 || qIdx + 1 >= reqPath.Length) return dict;
            string[] pairs = reqPath.Substring(qIdx + 1).Split('&');
            for (int i = 0; i < pairs.Length; i++)
            {
                int eq = pairs[i].IndexOf('=');
                if (eq > 0)
                {
                    string k = Uri.UnescapeDataString(pairs[i].Substring(0, eq).Replace('+', ' '));
                    string v = Uri.UnescapeDataString(pairs[i].Substring(eq + 1).Replace('+', ' '));
                    dict[k] = v;
                }
            }
            return dict;
        }

        private static string BuildCallbackHtml(bool ok, string msg)
        {
            string accent = ok ? "#34d399" : "#f87171";
            string icon = ok ? "✓" : "!";
            return "<!DOCTYPE html><html><head><meta charset='utf-8'><title>LumeDesk Gmail 授权</title>" +
                   "<style>body{margin:0;height:100vh;display:flex;align-items:center;justify-content:center;" +
                   "background:#0b1120;color:#f1f5f9;font-family:'Microsoft YaHei UI',sans-serif;}" +
                   ".card{background:#111a2e;border:1px solid rgba(255,255,255,.12);border-radius:20px;padding:36px 44px;text-align:center;max-width:440px;}" +
                   ".ico{width:56px;height:56px;border-radius:50%;background:" + accent + ";color:#0b1120;font-size:30px;font-weight:bold;line-height:56px;margin:0 auto 18px;}" +
                   "h2{margin:0 0 10px;font-size:21px;}p{margin:0;color:#94a3b8;font-size:15px;line-height:1.6;}</style></head>" +
                   "<body><div class='card'><div class='ico'>" + icon + "</div><h2>" + (ok ? "LumeDesk 已连接 Gmail" : "授权未完成") +
                   "</h2><p>" + msg + "</p></div></body></html>";
        }

        private static void WriteHttpHtml(NetworkStream ns, int statusCode, string html)
        {
            byte[] body = Encoding.UTF8.GetBytes(html);
            string statusText = (statusCode == 200) ? "OK" : (statusCode == 404 ? "Not Found" : "Bad Request");
            string header = "HTTP/1.1 " + statusCode + " " + statusText + "\r\n" +
                            "Content-Type: text/html; charset=utf-8\r\n" +
                            "Content-Length: " + body.Length + "\r\n" +
                            "Connection: close\r\n\r\n";
            byte[] hBytes = Encoding.ASCII.GetBytes(header);
            ns.Write(hBytes, 0, hBytes.Length);
            ns.Write(body, 0, body.Length);
            ns.Flush();
        }

        private void FetchInboxOnce()
        {
            string host = _config.MailImapHost;
            int port = _config.MailImapPort > 0 ? _config.MailImapPort : 993;
            string email = _config.MailEmail;
            string password = UnprotectSecret(_config.MailPasswordDpapi);

            if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
            {
                return;
            }

            lock (_lock)
            {
                _state.IsLoading = true;
                if (!_state.IsConnected) _state.StatusMessage = "正在连接 " + host + " ...";
            }

            try
            {
                using (TcpClient tcp = new TcpClient())
                {
                    IAsyncResult ar = tcp.BeginConnect(host, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(7000))
                    {
                        throw new TimeoutException("连接超时，请检查代理或端口");
                    }
                    tcp.EndConnect(ar);
                    tcp.ReceiveTimeout = 8000;
                    tcp.SendTimeout = 6000;

                    using (SslStream ssl = new SslStream(tcp.GetStream(), false))
                    {
                        ssl.AuthenticateAsClient(host, null, SslProtocols.Tls12, false);
                        using (StreamReader sr = new StreamReader(ssl, Encoding.UTF8))
                        using (StreamWriter sw = new StreamWriter(ssl, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true })
                        {
                            // Greeting
                            sr.ReadLine();

                            // LOGIN
                            string loginResp = SendImapCommand(sw, sr, "A001", "LOGIN " + QuoteImap(email) + " " + QuoteImap(password));
                            if (loginResp.IndexOf("A001 OK", StringComparison.OrdinalIgnoreCase) < 0)
                            {
                                throw new Exception("登录被拒绝（Gmail/QQ需使用应用专用密码或授权码）");
                            }

                            // STATUS INBOX (MESSAGES UNSEEN)
                            string statusResp = SendImapCommand(sw, sr, "A002", "STATUS INBOX (MESSAGES UNSEEN)");
                            int totalMsgs = 0;
                            int unseen = 0;
                            Match mm = Regex.Match(statusResp, @"MESSAGES\s+(\d+)", RegexOptions.IgnoreCase);
                            Match mu = Regex.Match(statusResp, @"UNSEEN\s+(\d+)", RegexOptions.IgnoreCase);
                            if (mm.Success) int.TryParse(mm.Groups[1].Value, out totalMsgs);
                            if (mu.Success) int.TryParse(mu.Groups[1].Value, out unseen);

                            // EXAMINE INBOX (read-only! Never marks messages as read!)
                            SendImapCommand(sw, sr, "A003", "EXAMINE INBOX");

                            List<MailMessageSummary> list = new List<MailMessageSummary>();
                            if (totalMsgs > 0)
                            {
                                int startSeq = Math.Max(1, totalMsgs - 5);
                                string fetchResp = SendImapCommand(sw, sr, "A004",
                                    string.Format("FETCH {0}:{1} (FLAGS BODY.PEEK[HEADER.FIELDS (FROM SUBJECT DATE)])", startSeq, totalMsgs));
                                list = ParseFetchResponse(fetchResp);
                                list.Reverse(); // Newest first!
                            }

                            try { SendImapCommand(sw, sr, "A005", "LOGOUT"); } catch { }

                            lock (_lock)
                            {
                                _state.IsConnected = true;
                                _state.IsLoading = false;
                                _state.UnreadCount = unseen;
                                _state.Messages = list;
                                _state.StatusMessage = "已同步 " + DateTime.Now.ToString("HH:mm");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                lock (_lock)
                {
                    _state.IsLoading = false;
                    _state.StatusMessage = ex.Message;
                }
            }
        }

        private static string QuoteImap(string s)
        {
            return "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static string SendImapCommand(StreamWriter sw, StreamReader sr, string tag, string cmd)
        {
            sw.WriteLine(tag + " " + cmd);
            StringBuilder sb = new StringBuilder();
            string line;
            while ((line = sr.ReadLine()) != null)
            {
                sb.AppendLine(line);
                if (line.StartsWith(tag + " ", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
            }
            return sb.ToString();
        }

        private static List<MailMessageSummary> ParseFetchResponse(string resp)
        {
            List<MailMessageSummary> list = new List<MailMessageSummary>();
            if (string.IsNullOrEmpty(resp)) return list;

            string[] blocks = Regex.Split(resp, @"(?m)^\*\s+\d+\s+FETCH\s+");
            for (int i = 1; i < blocks.Length; i++)
            {
                string block = blocks[i];
                bool isSeen = block.IndexOf("\\Seen", StringComparison.OrdinalIgnoreCase) >= 0;

                // Unfold multiline RFC 2822 headers
                string unfolded = Regex.Replace(block, @"\r?\n[ \t]+", " ");
                string fromRaw = ExtractHeaderValue(unfolded, "From");
                string subjRaw = ExtractHeaderValue(unfolded, "Subject");
                string dateRaw = ExtractHeaderValue(unfolded, "Date");

                string sender = CleanSender(DecodeMimeWords(fromRaw));
                string subject = DecodeMimeWords(subjRaw);
                if (string.IsNullOrWhiteSpace(subject)) subject = "(无主题)";
                if (string.IsNullOrWhiteSpace(sender)) sender = "未知发件人";

                list.Add(new MailMessageSummary
                {
                    Sender = sender,
                    Subject = subject.Trim(),
                    TimeText = FormatMailTime(dateRaw),
                    IsUnread = !isSeen
                });
            }
            return list;
        }

        private static string ExtractHeaderValue(string block, string headerName)
        {
            Match m = Regex.Match(block, @"(?im)^" + headerName + @":\s*(.*)$");
            return m.Success ? m.Groups[1].Value.Trim() : "";
        }

        private static string CleanSender(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            int lt = raw.IndexOf('<');
            if (lt > 0)
            {
                string display = raw.Substring(0, lt).Trim().Trim('"', '\'');
                if (!string.IsNullOrEmpty(display)) return display;
            }
            string clean = raw.Trim().Trim('<', '>', '"');
            int at = clean.IndexOf('@');
            return at > 0 ? clean.Substring(0, at) : clean;
        }

        private static string FormatMailTime(string dateRaw)
        {
            if (string.IsNullOrEmpty(dateRaw)) return "";
            try
            {
                string cleaned = Regex.Replace(dateRaw, @"\s*\([^)]*\)\s*$", "").Trim();
                DateTimeOffset dto;
                if (DateTimeOffset.TryParse(cleaned, CultureInfo.InvariantCulture, DateTimeStyles.None, out dto))
                {
                    DateTime local = dto.LocalDateTime;
                    if (local.Date == DateTime.Today) return local.ToString("HH:mm");
                    return local.ToString("M/d");
                }
            }
            catch
            {
            }
            return "";
        }

        private static string DecodeMimeWords(string input)
        {
            if (string.IsNullOrEmpty(input) || input.IndexOf("=?") < 0) return input ?? "";

            // Remove whitespace strictly between adjacent encoded-words
            string collapsed = Regex.Replace(input, @"\?=\s+=\?", "?==?");
            // Merge adjacent same-charset Q-encoded chunks so split multi-byte UTF-8 Chinese glyphs never break
            while (true)
            {
                string merged = Regex.Replace(collapsed, @"=\?([^?]+)\?[qQ]\?([^?]*)\?==\?\1\?[qQ]\?([^?]*)\?=", "=?$1?Q?$2$3?=", RegexOptions.IgnoreCase);
                if (merged == collapsed) break;
                collapsed = merged;
            }

            return Regex.Replace(collapsed, @"=\?([^?]+)\?([bBqQ])\?([^?]*)\?=", delegate(Match m)
            {
                try
                {
                    string charset = m.Groups[1].Value.Trim();
                    string encType = m.Groups[2].Value.ToUpperInvariant();
                    string payload = m.Groups[3].Value;

                    Encoding enc = Encoding.UTF8;
                    try { enc = Encoding.GetEncoding(charset); } catch { }

                    if (encType == "B")
                    {
                        byte[] bytes = Convert.FromBase64String(payload);
                        return enc.GetString(bytes);
                    }
                    else
                    {
                        MemoryStream ms = new MemoryStream();
                        for (int i = 0; i < payload.Length; i++)
                        {
                            char c = payload[i];
                            if (c == '_') ms.WriteByte((byte)' ');
                            else if (c == '=' && i + 2 < payload.Length)
                            {
                                byte b;
                                if (byte.TryParse(payload.Substring(i + 1, 2), NumberStyles.HexNumber, null, out b))
                                {
                                    ms.WriteByte(b);
                                    i += 2;
                                    continue;
                                }
                                ms.WriteByte((byte)c);
                            }
                            else ms.WriteByte((byte)c);
                        }
                        return enc.GetString(ms.ToArray());
                    }
                }
                catch
                {
                    return m.Value;
                }
            });
        }
    }
}
