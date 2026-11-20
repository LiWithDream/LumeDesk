using System;
using System.Collections.Generic;
using System.Text;

namespace LumeDesk
{
    public static class SearchMatcher
    {
        private static readonly int[] GbBounds = new int[]
        {
            45217, 45253, 45761, 46318, 46826, 47010, 47297, 47614,
            48119, 49062, 49324, 49896, 50371, 50614, 50622, 50906,
            51387, 51446, 52218, 52698, 52980, 53689, 54481
        };

        private static readonly char[] GbLetters = new char[]
        {
            'a', 'b', 'c', 'd', 'e', 'f', 'g', 'h',
            'j', 'k', 'l', 'm', 'n', 'o', 'p', 'q',
            'r', 's', 't', 'w', 'x', 'y', 'z'
        };

        // Common Level-2 CJK characters used in popular Chinese app/game names
        private static readonly Dictionary<char, char> ExtraPinyinInitials = new Dictionary<char, char>
        {
            { '哔', 'b' }, { '哩', 'l' }, { '咪', 'm' }, { '咕', 'g' },
            { '咚', 'd' }, { '咔', 'k' }, { '喵', 'm' }, { '炫', 'x' },
            { '睿', 'r' }, { '骁', 'x' }, { '曜', 'y' }, { '岚', 'l' },
            { '穹', 'q' }, { '吒', 'z' }, { '弑', 's' }, { '珀', 'p' },
            { '麒', 'q' }, { '麟', 'l' }, { '钛', 't' }, { '氪', 'k' },
            { '弈', 'y' }, { '皓', 'h' }, { '沐', 'm' }, { '梓', 'z' },
            { '宸', 'c' }, { '奕', 'y' }, { '淼', 'm' }, { '鑫', 'x' },
            { '魅', 'm' }, { '酷', 'k' }, { '哒', 'd' }, { '喔', 'o' },
            { '乐', 'y' } // 音乐 (网易云音乐 / QQ音乐)
        };

        private static readonly Encoding GbEncoding = ResolveGbEncoding();

        private static Encoding ResolveGbEncoding()
        {
            try { return Encoding.GetEncoding(936); } catch { }
            try { return Encoding.GetEncoding("GB2312"); } catch { }
            return null;
        }

        public static string BuildInitials(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            StringBuilder sb = new StringBuilder(text.Length);
            bool prevAlphaNum = false;
            bool prevLower = false;

            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (ch >= 0x4E00 && ch <= 0x9FFF)
                {
                    char py = GetChineseInitial(ch);
                    if (py != '\0') sb.Append(py);
                    prevAlphaNum = false;
                    prevLower = false;
                }
                else if (char.IsLetterOrDigit(ch))
                {
                    bool isUpper = char.IsUpper(ch);
                    // Capture start of every word or CamelCase boundary (e.g. Visual Studio Code -> vsc, PotPlayer -> pp)
                    if (!prevAlphaNum || (isUpper && prevLower))
                    {
                        sb.Append(char.ToLowerInvariant(ch));
                    }
                    prevAlphaNum = true;
                    prevLower = char.IsLower(ch);
                }
                else
                {
                    prevAlphaNum = false;
                    prevLower = false;
                }
            }
            return sb.ToString();
        }

        public static char GetChineseInitial(char ch)
        {
            char mapped;
            if (ExtraPinyinInitials.TryGetValue(ch, out mapped)) return mapped;
            if (GbEncoding == null) return '\0';

            try
            {
                byte[] bytes = GbEncoding.GetBytes(new char[] { ch });
                if (bytes.Length == 2)
                {
                    int code = bytes[0] * 256 + bytes[1];
                    if (code >= 45217 && code <= 55289)
                    {
                        for (int i = GbBounds.Length - 1; i >= 0; i--)
                        {
                            if (code >= GbBounds[i]) return GbLetters[i];
                        }
                    }
                }
            }
            catch
            {
            }
            return '\0';
        }

        /// <summary>
        /// Scores how well an app matches a user's search query.
        /// Returns 0 if no match; higher positive integer = better match.
        /// Supports exact prefix, substring, Chinese Pinyin initials, English acronyms, and fuzzy subsequence.
        /// </summary>
        public static int ScoreMatch(string displayName, string folderName, string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return 1;
            string q = query.Trim();
            string name = displayName ?? "";
            string folder = folderName ?? "";

            // 1. Exact equality
            if (name.Equals(q, StringComparison.OrdinalIgnoreCase)) return 1000;

            // 2. Direct prefix match on app name
            if (name.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return 900;

            // 3. Direct substring match on app name (earlier index scores higher)
            int subIdx = name.IndexOf(q, StringComparison.OrdinalIgnoreCase);
            if (subIdx >= 0) return 780 - Math.Min(120, subIdx * 4);

            string qCompact = StripSpaces(q).ToLowerInvariant();
            if (qCompact.Length == 0) return 0;

            // 4. Compact name match without spaces (e.g. "vscode" vs "VS Code", "qqmusic")
            string nameCompact = StripSpaces(name).ToLowerInvariant();
            int compactIdx = nameCompact.IndexOf(qCompact, StringComparison.Ordinal);
            if (compactIdx == 0) return 840;
            if (compactIdx > 0) return 720 - Math.Min(100, compactIdx * 3);

            // 5. Pinyin / Word Acronym initials match on app name (e.g. "wx" -> "微信", "vsc" -> "Visual Studio Code")
            string nameInitials = BuildInitials(name);
            if (!string.IsNullOrEmpty(nameInitials))
            {
                if (nameInitials.Equals(qCompact, StringComparison.Ordinal)) return 860;
                if (nameInitials.StartsWith(qCompact, StringComparison.Ordinal)) return 760;
                int initIdx = nameInitials.IndexOf(qCompact, StringComparison.Ordinal);
                if (initIdx > 0) return 640 - Math.Min(80, initIdx * 5);
                if (qCompact.Length >= 2 && IsSubsequence(qCompact, nameInitials)) return 520;
            }

            // 6. Category folder direct or Pinyin-initial match (e.g. "yx" -> "游戏库", "ai" -> "AI 与开发")
            if (!string.IsNullOrEmpty(folder))
            {
                if (folder.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return 480;
                if (folder.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) return 440;
                string folderInitials = BuildInitials(folder);
                if (!string.IsNullOrEmpty(folderInitials))
                {
                    if (folderInitials.StartsWith(qCompact, StringComparison.Ordinal)) return 410;
                    if (folderInitials.IndexOf(qCompact, StringComparison.Ordinal) >= 0) return 360;
                }
            }

            // 7. Ordered subsequence match on compact ASCII/Latin name (e.g. "chrm" -> "Google Chrome", "vscd" -> "Visual Studio Code")
            if (qCompact.Length >= 2 && IsSubsequence(qCompact, nameCompact))
            {
                return 300;
            }

            return 0;
        }

        private static string StripSpaces(string s)
        {
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (!char.IsWhiteSpace(c) && c != '-' && c != '_' && c != '·' && c != '.')
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        private static bool IsSubsequence(string needle, string haystack)
        {
            if (string.IsNullOrEmpty(needle) || string.IsNullOrEmpty(haystack)) return false;
            if (needle.Length > haystack.Length) return false;
            int ni = 0;
            for (int hi = 0; hi < haystack.Length && ni < needle.Length; hi++)
            {
                if (haystack[hi] == needle[ni]) ni++;
            }
            return ni == needle.Length;
        }
    }
}
