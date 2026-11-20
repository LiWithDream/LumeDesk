using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace LumeDesk
{
    public class TodoEntry
    {
        public string Text { get; set; }
        public bool Done { get; set; }
    }

    public class AppConfig
    {
        public string DataRoot { get; set; }
        public string TargetMonitor { get; set; }
        public string LayoutMode { get; set; }
        public int Columns { get; set; }
        public int Rows { get; set; }
        public double CardTintOpacity { get; set; }
        public int BlurRadius { get; set; }
        public bool ShowTopClock { get; set; }
        public bool ShowWidgets { get; set; }
        public List<string> EnabledWidgets { get; set; }
        public string MailProvider { get; set; }
        public string MailImapHost { get; set; }
        public int MailImapPort { get; set; }
        public string MailEmail { get; set; }
        public string MailPasswordDpapi { get; set; }
        public string GmailClientId { get; set; }
        public string GmailClientSecretDpapi { get; set; }
        public string GmailRefreshTokenDpapi { get; set; }
        public List<TodoEntry> TodoItems { get; set; }
        public bool ForceMiniGridSlot { get; set; }
        public int AutoNineGridThreshold { get; set; }
        public List<string> FolderOrder { get; set; }
        public Dictionary<string, List<string>> ItemOrder { get; set; }
        public Dictionary<string, string> IconBgOverrides { get; set; }
        public Dictionary<string, string> FolderGridModes { get; set; }

        public AppConfig()
        {
            DataRoot = "";
            TargetMonitor = "auto";
            LayoutMode = "auto";
            Columns = 2;
            Rows = 3;
            CardTintOpacity = 0.62;
            BlurRadius = 18;
            ShowTopClock = true;
            ShowWidgets = true;
            EnabledWidgets = new List<string> { "mail", "clash", "music", "sys_todo" };
            MailProvider = "oauth";
            MailImapHost = "imap.gmail.com";
            MailImapPort = 993;
            MailEmail = "";
            MailPasswordDpapi = "";
            GmailClientId = "";
            GmailClientSecretDpapi = "";
            GmailRefreshTokenDpapi = "";
            TodoItems = new List<TodoEntry>();
            ForceMiniGridSlot = true;
            AutoNineGridThreshold = 7;
            FolderOrder = new List<string>();
            ItemOrder = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            IconBgOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            FolderGridModes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public static class ConfigManager
    {
        private static readonly object SaveLock = new object();

        public static string BaseDir
        {
            get
            {
                string exeDir = AppDomain.CurrentDomain.BaseDirectory;
                // If running from dist\LumeDesk.exe and ..\data exists, use parent as base
                DirectoryInfo di = new DirectoryInfo(exeDir.TrimEnd('\\'));
                if (di.Name.Equals("dist", StringComparison.OrdinalIgnoreCase) && di.Parent != null)
                {
                    return di.Parent.FullName;
                }
                return exeDir.TrimEnd('\\');
            }
        }

        public static string ConfigPath
        {
            get { return Path.Combine(BaseDir, "config.json"); }
        }

        public static AppConfig Load()
        {
            AppConfig cfg = new AppConfig();
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath, Encoding.UTF8);
                    cfg = DeserializeAndNormalize(json);
                }
            }
            catch
            {
                // Fallback to defaults
                cfg = new AppConfig();
            }

            NormalizeConfig(cfg);

            if (string.IsNullOrEmpty(cfg.DataRoot) || !Directory.Exists(cfg.DataRoot))
            {
                cfg.DataRoot = Path.Combine(BaseDir, "data");
            }

            if (!Directory.Exists(cfg.DataRoot))
            {
                Directory.CreateDirectory(cfg.DataRoot);
            }

            return cfg;
        }

        internal static AppConfig DeserializeAndNormalize(string json)
        {
            AppConfig cfg = null;
            if (!string.IsNullOrEmpty(json))
            {
                cfg = new JavaScriptSerializer().Deserialize<AppConfig>(json);
            }
            if (cfg == null)
            {
                cfg = new AppConfig();
            }
            NormalizeConfig(cfg);
            return cfg;
        }

        private static void NormalizeConfig(AppConfig cfg)
        {
            if (cfg == null) return;
            if (string.IsNullOrEmpty(cfg.LayoutMode)) cfg.LayoutMode = "auto";
            if (cfg.Columns < 1) cfg.Columns = 2;
            if (cfg.Rows < 1) cfg.Rows = 3;
            if (cfg.CardTintOpacity < 0.20 || cfg.CardTintOpacity > 0.92) cfg.CardTintOpacity = 0.62;
            if (cfg.BlurRadius < 4 || cfg.BlurRadius > 40) cfg.BlurRadius = 18;
            if (cfg.AutoNineGridThreshold < 1) cfg.AutoNineGridThreshold = 7;
            if (cfg.FolderOrder == null) cfg.FolderOrder = new List<string>();
            // JavaScriptSerializer always instantiates case-sensitive Dictionaries; re-wrap with OrdinalIgnoreCase
            cfg.ItemOrder = ToCaseInsensitiveDictionary(cfg.ItemOrder);
            cfg.IconBgOverrides = ToCaseInsensitiveDictionary(cfg.IconBgOverrides);
            cfg.FolderGridModes = ToCaseInsensitiveDictionary(cfg.FolderGridModes);
            if (cfg.EnabledWidgets == null) cfg.EnabledWidgets = new List<string> { "mail", "clash", "music", "sys_todo" };
            if (cfg.TodoItems == null) cfg.TodoItems = new List<TodoEntry>();
            if (string.IsNullOrEmpty(cfg.MailProvider))
            {
                cfg.MailProvider = !string.IsNullOrEmpty(cfg.MailPasswordDpapi) ? "imap" : "oauth";
            }
            if (string.IsNullOrEmpty(cfg.MailImapHost)) cfg.MailImapHost = "imap.gmail.com";
            if (cfg.MailImapPort <= 0) cfg.MailImapPort = 993;
        }

        private static Dictionary<string, T> ToCaseInsensitiveDictionary<T>(Dictionary<string, T> src)
        {
            Dictionary<string, T> dst = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
            if (src != null)
            {
                foreach (KeyValuePair<string, T> kv in src)
                {
                    if (kv.Key != null)
                    {
                        dst[kv.Key] = kv.Value;
                    }
                }
            }
            return dst;
        }

        public static void Save(AppConfig cfg)
        {
            if (cfg == null) return;
            lock (SaveLock)
            {
                try
                {
                    string raw = new JavaScriptSerializer().Serialize(cfg);
                    string pretty = FormatJson(raw);
                    string tmpPath = ConfigPath + ".tmp";
                    File.WriteAllText(tmpPath, pretty, new UTF8Encoding(false));
                    if (File.Exists(ConfigPath))
                    {
                        File.Copy(tmpPath, ConfigPath, true);
                        try { File.Delete(tmpPath); } catch { }
                    }
                    else
                    {
                        File.Move(tmpPath, ConfigPath);
                    }
                }
                catch
                {
                }
            }
        }

        internal static string FormatJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return "";
            StringBuilder sb = new StringBuilder(json.Length + 128);
            bool inQuotes = false;
            bool escaped = false;
            int indent = 0;
            for (int i = 0; i < json.Length; i++)
            {
                char ch = json[i];
                if (inQuotes)
                {
                    sb.Append(ch);
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (ch == '\\')
                    {
                        escaped = true;
                    }
                    else if (ch == '"')
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    if (ch == '"')
                    {
                        inQuotes = true;
                        sb.Append(ch);
                    }
                    else if (ch == '{' || ch == '[')
                    {
                        char closeCh = (ch == '{') ? '}' : ']';
                        int nextNonWs = i + 1;
                        while (nextNonWs < json.Length && char.IsWhiteSpace(json[nextNonWs])) nextNonWs++;
                        if (nextNonWs < json.Length && json[nextNonWs] == closeCh)
                        {
                            sb.Append(ch);
                            sb.Append(closeCh);
                            i = nextNonWs;
                        }
                        else
                        {
                            sb.Append(ch);
                            sb.AppendLine();
                            indent++;
                            sb.Append(new string(' ', indent * 2));
                        }
                    }
                    else if (ch == '}' || ch == ']')
                    {
                        sb.AppendLine();
                        indent = Math.Max(0, indent - 1);
                        sb.Append(new string(' ', indent * 2));
                        sb.Append(ch);
                    }
                    else if (ch == ',')
                    {
                        sb.Append(ch);
                        sb.AppendLine();
                        sb.Append(new string(' ', indent * 2));
                    }
                    else if (ch == ':')
                    {
                        sb.Append(": ");
                    }
                    else if (!char.IsWhiteSpace(ch))
                    {
                        sb.Append(ch);
                    }
                }
            }
            return sb.ToString();
        }
    }
}
