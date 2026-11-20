using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;

namespace LumeDesk
{
    public class AppItem
    {
        public string FilePath { get; set; }
        public string FileName { get; set; }
        public string DisplayName { get; set; }
        public string FolderKey { get; set; }
        public string FolderDisplayName { get; set; }
        public BitmapSource Icon { get; set; }
    }

    public class FolderGroup
    {
        public string FolderPath { get; set; }
        public string FolderName { get; set; }
        public string DisplayName { get; set; }
        public List<AppItem> Items { get; set; }

        public FolderGroup()
        {
            Items = new List<AppItem>();
        }
    }

    public class WorkspaceModel
    {
        public List<FolderGroup> Folders { get; set; }
        public List<AppItem> DockItems { get; set; }
        public List<AppItem> AllItems { get; set; }

        public WorkspaceModel()
        {
            Folders = new List<FolderGroup>();
            DockItems = new List<AppItem>();
            AllItems = new List<AppItem>();
        }
    }

    public static class FolderScanner
    {
        private static readonly HashSet<string> SupportedExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".lnk", ".url", ".exe", ".bat", ".cmd", ".appref-ms"
        };

        private static readonly Regex PrefixPattern = new Regex(@"^\d{1,2}\s*[·\.\-_\s]\s*", RegexOptions.Compiled);
        private static readonly Regex ShortcutSuffixPattern = new Regex(@"\s*[-–—]\s*(快捷方式|Shortcut)(\s*\(\d+\))?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static string CleanItemDisplayName(string rawNameWithoutExt)
        {
            if (string.IsNullOrEmpty(rawNameWithoutExt)) return "";
            string s = rawNameWithoutExt.Trim();
            s = ShortcutSuffixPattern.Replace(s, "").Trim();
            if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && s.Length > 4)
            {
                s = s.Substring(0, s.Length - 4).Trim();
            }
            return string.IsNullOrEmpty(s) ? rawNameWithoutExt.Trim() : s;
        }

        public static string ResolveTargetExeName(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return "";
            try
            {
                string ext = (Path.GetExtension(filePath) ?? "").ToLowerInvariant();
                string target = "";
                if (ext == ".exe")
                {
                    target = filePath;
                }
                else if (ext == ".lnk")
                {
                    Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                    if (shellType != null)
                    {
                        object shell = null;
                        object sc = null;
                        try
                        {
                            shell = Activator.CreateInstance(shellType);
                            sc = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { filePath });
                            target = sc.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, sc, null) as string ?? "";
                        }
                        finally
                        {
                            if (sc != null) { try { Marshal.FinalReleaseComObject(sc); } catch { } }
                            if (shell != null) { try { Marshal.FinalReleaseComObject(shell); } catch { } }
                        }
                    }
                }
                if (!string.IsNullOrEmpty(target) && target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    return Path.GetFileNameWithoutExtension(target);
                }
            }
            catch
            {
            }
            return "";
        }

        public static string CustomIconsDir(AppConfig cfg)
        {
            return Path.Combine(cfg.DataRoot, "_Icons");
        }

        public static string DockDir(AppConfig cfg)
        {
            return Path.Combine(cfg.DataRoot, "_Dock");
        }

        public static string TrashDir(AppConfig cfg)
        {
            return Path.Combine(cfg.DataRoot, "_Trash");
        }

        public static string CleanFolderTitle(string folderName)
        {
            if (string.IsNullOrEmpty(folderName)) return "";
            string stripped = PrefixPattern.Replace(folderName.Trim(), "").Trim();
            return string.IsNullOrEmpty(stripped) ? folderName : stripped;
        }

        public static WorkspaceModel Scan(AppConfig cfg)
        {
            WorkspaceModel model = new WorkspaceModel();
            if (cfg == null || string.IsNullOrEmpty(cfg.DataRoot) || !Directory.Exists(cfg.DataRoot))
            {
                return model;
            }

            string customIcons = CustomIconsDir(cfg);
            string dockDir = DockDir(cfg);
            if (!Directory.Exists(customIcons))
            {
                try { Directory.CreateDirectory(customIcons); } catch { }
            }
            if (!Directory.Exists(dockDir))
            {
                try { Directory.CreateDirectory(dockDir); } catch { }
            }

            // Scan card folders (subdirectories not starting with '_' or '.')
            string[] dirs = Directory.GetDirectories(cfg.DataRoot);
            bool hasAnyCategory = false;
            for (int i = 0; i < dirs.Length; i++)
            {
                string n = Path.GetFileName(dirs[i]);
                if (!n.StartsWith("_") && !n.StartsWith("."))
                {
                    hasAnyCategory = true;
                    break;
                }
            }

            // First-run out-of-the-box guard: create starter folders if data\ has no categories yet
            if (!hasAnyCategory)
            {
                string[] starter = new string[]
                {
                    "01 · 常用工具",
                    "02 · 社媒沟通",
                    "03 · 影音娱乐",
                    "04 · 系统网络"
                };
                for (int i = 0; i < starter.Length; i++)
                {
                    try { Directory.CreateDirectory(Path.Combine(cfg.DataRoot, starter[i])); } catch { }
                }
                dirs = Directory.GetDirectories(cfg.DataRoot);
            }
            Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);

            Dictionary<string, string> validDirsByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            List<string> validDirs = new List<string>();
            for (int i = 0; i < dirs.Length; i++)
            {
                string name = Path.GetFileName(dirs[i]);
                if (string.IsNullOrEmpty(name) || name.StartsWith("_") || name.StartsWith(".")) continue;
                if (!validDirsByName.ContainsKey(name))
                {
                    validDirsByName[name] = dirs[i];
                    validDirs.Add(dirs[i]);
                }
            }

            List<string> orderedDirs = new List<string>();
            if (cfg.FolderOrder != null && cfg.FolderOrder.Count > 0)
            {
                HashSet<string> used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < cfg.FolderOrder.Count; i++)
                {
                    string orderName = cfg.FolderOrder[i];
                    if (string.IsNullOrEmpty(orderName)) continue;
                    string canonicalDir;
                    if (validDirsByName.TryGetValue(orderName, out canonicalDir) && !used.Contains(canonicalDir))
                    {
                        orderedDirs.Add(canonicalDir);
                        used.Add(canonicalDir);
                    }
                }
                for (int i = 0; i < validDirs.Count; i++)
                {
                    if (!used.Contains(validDirs[i])) orderedDirs.Add(validDirs[i]);
                }
            }
            else
            {
                orderedDirs.AddRange(validDirs);
            }

            for (int i = 0; i < orderedDirs.Count; i++)
            {
                string dirPath = orderedDirs[i];
                string folderName = Path.GetFileName(dirPath);
                FolderGroup group = new FolderGroup();
                group.FolderPath = dirPath;
                group.FolderName = folderName;
                group.DisplayName = CleanFolderTitle(folderName);
                group.Items = ScanItemsInDirectory(dirPath, folderName, group.DisplayName, cfg, customIcons);
                model.Folders.Add(group);
                model.AllItems.AddRange(group.Items);
            }

            // Scan Dock items
            model.DockItems = ScanItemsInDirectory(dockDir, "_Dock", "Dock", cfg, customIcons);

            // Include Dock items in AllItems if not already represented by DisplayName
            HashSet<string> seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < model.AllItems.Count; i++) seenNames.Add(model.AllItems[i].DisplayName);
            for (int i = 0; i < model.DockItems.Count; i++)
            {
                if (!seenNames.Contains(model.DockItems[i].DisplayName))
                {
                    model.AllItems.Add(model.DockItems[i]);
                }
            }

            return model;
        }

        private static List<AppItem> ScanItemsInDirectory(
            string dirPath, string folderKey, string folderDisplayName, AppConfig cfg, string customIcons)
        {
            List<AppItem> items = new List<AppItem>();
            if (!Directory.Exists(dirPath)) return items;

            string[] files = Directory.GetFiles(dirPath);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);

            List<string> validFiles = new List<string>();
            for (int i = 0; i < files.Length; i++)
            {
                string fn = Path.GetFileName(files[i]);
                if (fn.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                string ext = Path.GetExtension(files[i]) ?? "";
                if (!SupportedExts.Contains(ext)) continue;
                validFiles.Add(files[i]);
            }

            // Apply saved ordering for this folder if present
            List<string> savedOrder;
            if (cfg.ItemOrder != null && cfg.ItemOrder.TryGetValue(folderKey, out savedOrder) && savedOrder != null && savedOrder.Count > 0)
            {
                Dictionary<string, string> byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < validFiles.Count; i++)
                {
                    byName[Path.GetFileName(validFiles[i])] = validFiles[i];
                }

                List<string> sorted = new List<string>();
                HashSet<string> placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < savedOrder.Count; i++)
                {
                    string match;
                    if (byName.TryGetValue(savedOrder[i], out match))
                    {
                        sorted.Add(match);
                        placed.Add(match);
                    }
                }
                for (int i = 0; i < validFiles.Count; i++)
                {
                    if (!placed.Contains(validFiles[i])) sorted.Add(validFiles[i]);
                }
                validFiles = sorted;
            }

            for (int i = 0; i < validFiles.Count; i++)
            {
                string fp = validFiles[i];
                string fn = Path.GetFileName(fp);
                string rawDisp = Path.GetFileNameWithoutExtension(fp);
                string disp = CleanItemDisplayName(rawDisp);

                string bgOverride = null;
                if (cfg.IconBgOverrides != null)
                {
                    if (!cfg.IconBgOverrides.TryGetValue(disp, out bgOverride))
                    {
                        cfg.IconBgOverrides.TryGetValue(rawDisp, out bgOverride);
                    }
                }

                AppItem item = new AppItem();
                item.FilePath = fp;
                item.FileName = fn;
                item.DisplayName = disp;
                item.FolderKey = folderKey;
                item.FolderDisplayName = folderDisplayName;
                item.Icon = IconExtractor.GetPhoneIcon(fp, customIcons, bgOverride);
                items.Add(item);
            }

            return items;
        }

        public static void LaunchItem(AppItem item)
        {
            LaunchItem(item, false);
        }

        public static void LaunchItem(AppItem item, bool forceNewInstance)
        {
            if (item == null || string.IsNullOrEmpty(item.FilePath)) return;
            try
            {
                if (!forceNewInstance && WindowActivator.TryActivateExistingShortcut(item.FilePath, item.DisplayName))
                {
                    return;
                }

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = item.FilePath;
                psi.UseShellExecute = true;
                psi.WorkingDirectory = Path.GetDirectoryName(item.FilePath);
                Process.Start(psi);
            }
            catch
            {
            }
        }

        public static void OpenContainingTargetFolder(AppItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.FilePath)) return;
            try
            {
                string target = item.FilePath;
                if (item.FilePath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                    if (shellType != null)
                    {
                        object shell = null;
                        object sc = null;
                        try
                        {
                            shell = Activator.CreateInstance(shellType);
                            sc = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { item.FilePath });
                            string t = sc.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, sc, null) as string;
                            if (!string.IsNullOrEmpty(t) && (File.Exists(t) || Directory.Exists(t)))
                            {
                                target = t;
                            }
                        }
                        finally
                        {
                            if (sc != null) { try { Marshal.FinalReleaseComObject(sc); } catch { } }
                            if (shell != null) { try { Marshal.FinalReleaseComObject(shell); } catch { } }
                        }
                    }
                }
                Process.Start("explorer.exe", "/select,\"" + target + "\"");
            }
            catch
            {
            }
        }

        public static void IngestDroppedFiles(string[] droppedPaths, string destFolder)
        {
            if (droppedPaths == null || string.IsNullOrEmpty(destFolder)) return;
            if (!Directory.Exists(destFolder)) Directory.CreateDirectory(destFolder);

            for (int i = 0; i < droppedPaths.Length; i++)
            {
                string src = droppedPaths[i];
                if (string.IsNullOrEmpty(src)) continue;

                try
                {
                    string ext = (Path.GetExtension(src) ?? "").ToLowerInvariant();
                    if (File.Exists(src) && (ext == ".lnk" || ext == ".url"))
                    {
                        string dst = Path.Combine(destFolder, Path.GetFileName(src));
                        if (!src.Equals(dst, StringComparison.OrdinalIgnoreCase))
                        {
                            File.Copy(src, dst, true);
                        }
                    }
                    else if (File.Exists(src) || Directory.Exists(src))
                    {
                        // Create a shortcut (.lnk) pointing to the dropped exe/file/folder
                        string baseName = Path.GetFileNameWithoutExtension(src);
                        if (string.IsNullOrEmpty(baseName)) baseName = new DirectoryInfo(src).Name;
                        string lnkPath = Path.Combine(destFolder, baseName + ".lnk");
                        CreateShortcut(lnkPath, src);
                    }
                }
                catch
                {
                }
            }
        }

        public static void MoveToTrash(AppConfig cfg, AppItem item)
        {
            if (item == null || !File.Exists(item.FilePath)) return;
            try
            {
                string trash = TrashDir(cfg);
                if (!Directory.Exists(trash)) Directory.CreateDirectory(trash);
                string dst = Path.Combine(trash, DateTime.Now.ToString("yyyyMMdd_HHmmss_") + item.FileName);
                File.Move(item.FilePath, dst);
            }
            catch
            {
            }
        }

        public static void ReorderItem(AppConfig cfg, FolderGroup group, List<AppItem> list, AppItem item, int delta, bool pinFirst)
        {
            if (cfg == null || list == null || item == null) return;
            string folderKey = !string.IsNullOrEmpty(item.FolderKey) ? item.FolderKey : (group != null ? group.FolderName : null);
            if (string.IsNullOrEmpty(folderKey)) return;
            if (cfg.ItemOrder == null) cfg.ItemOrder = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            List<string> names = new List<string>();
            for (int i = 0; i < list.Count; i++) names.Add(list[i].FileName);

            int idx = names.FindIndex(delegate(string s) { return s.Equals(item.FileName, StringComparison.OrdinalIgnoreCase); });
            if (idx < 0) return;

            if (pinFirst)
            {
                if (idx == 0) return;
                names.RemoveAt(idx);
                names.Insert(0, item.FileName);
                AppItem movedObj = list[idx];
                list.RemoveAt(idx);
                list.Insert(0, movedObj);
            }
            else
            {
                int next = idx + delta;
                if (next < 0) next = 0;
                if (next >= names.Count) next = names.Count - 1;
                if (next == idx) return;
                names.RemoveAt(idx);
                names.Insert(next, item.FileName);
                AppItem movedObj = list[idx];
                list.RemoveAt(idx);
                list.Insert(next, movedObj);
            }

            cfg.ItemOrder[folderKey] = names;
            ConfigManager.Save(cfg);
        }

        public static bool MoveItemToIndex(AppConfig cfg, List<AppItem> list, AppItem sourceItem, AppItem targetItem)
        {
            if (cfg == null || list == null || sourceItem == null || targetItem == null) return false;
            if (sourceItem == targetItem) return false;
            string folderKey = sourceItem.FolderKey;
            if (string.IsNullOrEmpty(folderKey) || !folderKey.Equals(targetItem.FolderKey, StringComparison.OrdinalIgnoreCase))
                return false;

            List<string> names = new List<string>();
            for (int i = 0; i < list.Count; i++) names.Add(list[i].FileName);

            int srcIdx = names.FindIndex(delegate(string s) { return s.Equals(sourceItem.FileName, StringComparison.OrdinalIgnoreCase); });
            int dstIdx = names.FindIndex(delegate(string s) { return s.Equals(targetItem.FileName, StringComparison.OrdinalIgnoreCase); });
            if (srcIdx < 0 || dstIdx < 0 || srcIdx == dstIdx) return false;

            string moved = names[srcIdx];
            names.RemoveAt(srcIdx);
            names.Insert(dstIdx, moved);

            // Also keep live in-memory list synchronized for zero-flicker instant modal updates
            AppItem movedObj = list[srcIdx];
            list.RemoveAt(srcIdx);
            list.Insert(dstIdx, movedObj);

            if (cfg.ItemOrder == null) cfg.ItemOrder = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            cfg.ItemOrder[folderKey] = names;
            ConfigManager.Save(cfg);
            return true;
        }

        public static bool MoveItemToFolder(AppConfig cfg, AppItem item, FolderGroup targetFolder)
        {
            if (cfg == null || item == null || targetFolder == null) return false;
            if (string.IsNullOrEmpty(item.FilePath) || !File.Exists(item.FilePath)) return false;
            if (string.IsNullOrEmpty(targetFolder.FolderPath) || !Directory.Exists(targetFolder.FolderPath)) return false;
            if (string.IsNullOrEmpty(item.FolderKey)) return false;
            if (item.FolderKey.Equals(targetFolder.FolderName, StringComparison.OrdinalIgnoreCase)) return false;
            if (item.FolderKey.Equals("_Dock", StringComparison.OrdinalIgnoreCase)) return false;

            try
            {
                string destPath = Path.Combine(targetFolder.FolderPath, item.FileName);
                if (File.Exists(destPath))
                {
                    string stem = Path.GetFileNameWithoutExtension(item.FileName);
                    string ext = Path.GetExtension(item.FileName);
                    int n = 2;
                    while (File.Exists(destPath))
                    {
                        destPath = Path.Combine(targetFolder.FolderPath, stem + " (" + n + ")" + ext);
                        n++;
                    }
                }
                File.Move(item.FilePath, destPath);
                string newFileName = Path.GetFileName(destPath);

                // Clean source ItemOrder and append to target ItemOrder if present
                List<string> srcOrder;
                if (cfg.ItemOrder != null && cfg.ItemOrder.TryGetValue(item.FolderKey, out srcOrder) && srcOrder != null)
                {
                    srcOrder.RemoveAll(delegate(string s) { return s.Equals(item.FileName, StringComparison.OrdinalIgnoreCase); });
                }
                List<string> dstOrder;
                if (cfg.ItemOrder != null && cfg.ItemOrder.TryGetValue(targetFolder.FolderName, out dstOrder) && dstOrder != null)
                {
                    dstOrder.Add(newFileName);
                }
                ConfigManager.Save(cfg);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool CopyItemToDock(AppConfig cfg, AppItem item)
        {
            if (cfg == null || item == null || string.IsNullOrEmpty(item.FilePath) || !File.Exists(item.FilePath)) return false;
            if (string.Equals(item.FolderKey, "_Dock", StringComparison.OrdinalIgnoreCase)) return false;
            try
            {
                string dockDir = DockDir(cfg);
                if (!Directory.Exists(dockDir)) Directory.CreateDirectory(dockDir);
                string destPath = Path.Combine(dockDir, item.FileName);
                if (File.Exists(destPath)) return false; // Already pinned in Dock
                File.Copy(item.FilePath, destPath, false);

                List<string> dockOrder;
                if (cfg.ItemOrder != null && cfg.ItemOrder.TryGetValue("_Dock", out dockOrder) && dockOrder != null)
                {
                    if (!dockOrder.Exists(delegate(string s) { return s.Equals(item.FileName, StringComparison.OrdinalIgnoreCase); }))
                    {
                        dockOrder.Add(item.FileName);
                        ConfigManager.Save(cfg);
                    }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool MoveFolderToIndex(AppConfig cfg, WorkspaceModel workspace, FolderGroup sourceFolder, FolderGroup targetFolder)
        {
            if (cfg == null || workspace == null || sourceFolder == null || targetFolder == null) return false;
            if (sourceFolder == targetFolder) return false;
            EnsureFolderOrderInitialized(cfg, workspace);

            int srcIdx = cfg.FolderOrder.FindIndex(delegate(string s) { return s.Equals(sourceFolder.FolderName, StringComparison.OrdinalIgnoreCase); });
            int dstIdx = cfg.FolderOrder.FindIndex(delegate(string s) { return s.Equals(targetFolder.FolderName, StringComparison.OrdinalIgnoreCase); });
            if (srcIdx < 0 || dstIdx < 0 || srcIdx == dstIdx) return false;

            string moved = cfg.FolderOrder[srcIdx];
            cfg.FolderOrder.RemoveAt(srcIdx);
            cfg.FolderOrder.Insert(dstIdx, moved);
            ConfigManager.Save(cfg);
            return true;
        }

        public static string CreateCategoryFolder(AppConfig cfg, WorkspaceModel workspace, string displayName)
        {
            string created;
            string err;
            return TryCreateCategoryFolder(cfg, workspace, displayName, out created, out err) ? created : null;
        }

        public static bool TryCreateCategoryFolder(
            AppConfig cfg, WorkspaceModel workspace, string displayName,
            out string createdFolderName, out string errorMessage)
        {
            createdFolderName = null;
            errorMessage = null;
            if (cfg == null)
            {
                errorMessage = "配置未初始化，无法创建分类";
                return false;
            }
            string safe;
            if (!ValidateCategoryFolderName(displayName, out safe, out errorMessage))
            {
                return false;
            }

            string targetCleanTitle = CleanFolderTitle(safe);
            if (workspace != null && workspace.Folders != null)
            {
                for (int i = 0; i < workspace.Folders.Count; i++)
                {
                    FolderGroup other = workspace.Folders[i];
                    if (other == null) continue;
                    if (string.Equals(other.FolderName, safe, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(CleanFolderTitle(other.FolderName), targetCleanTitle, StringComparison.OrdinalIgnoreCase))
                    {
                        errorMessage = string.Format("已存在同名分类「{0}」，请换一个名称", targetCleanTitle);
                        return false;
                    }
                }
            }

            try
            {
                string dirPath = Path.Combine(cfg.DataRoot, safe);
                if (Directory.Exists(dirPath))
                {
                    errorMessage = string.Format("已存在同名分类目录「{0}」，请换一个名称", targetCleanTitle);
                    return false;
                }

                Directory.CreateDirectory(dirPath);
                string newName = Path.GetFileName(dirPath);

                EnsureFolderOrderInitialized(cfg, workspace);
                cfg.FolderOrder.RemoveAll(delegate(string s)
                {
                    return string.Equals(s, newName, StringComparison.OrdinalIgnoreCase);
                });
                cfg.FolderOrder.Add(newName);
                ConfigManager.Save(cfg);
                createdFolderName = newName;
                return true;
            }
            catch
            {
                errorMessage = "创建分类文件夹失败，请检查目录权限";
                return false;
            }
        }

        public static bool RenameCategoryFolder(AppConfig cfg, WorkspaceModel workspace, FolderGroup group, string newDisplayName)
        {
            string err;
            return TryRenameCategoryFolder(cfg, workspace, group, newDisplayName, out err);
        }

        public static bool TryRenameCategoryFolder(
            AppConfig cfg, WorkspaceModel workspace, FolderGroup group, string newDisplayName,
            out string errorMessage)
        {
            errorMessage = null;
            if (cfg == null || group == null)
            {
                errorMessage = "未找到目标分类文件夹";
                return false;
            }
            string safe;
            if (!ValidateCategoryFolderName(newDisplayName, out safe, out errorMessage))
            {
                return false;
            }

            // Treat unchanged clean display name as a clean no-op instead of stripping the numeric prefix on disk!
            if (safe.Equals(CleanFolderTitle(group.FolderName), StringComparison.OrdinalIgnoreCase) && !PrefixPattern.IsMatch(safe))
            {
                return false;
            }

            // Preserve original numeric sort prefix (e.g. "01 · ") if user entered a clean title without a custom prefix
            string finalFolderName = safe;
            if (!PrefixPattern.IsMatch(safe))
            {
                Match pm = PrefixPattern.Match(group.FolderName ?? "");
                if (pm.Success && !string.IsNullOrEmpty(pm.Value))
                {
                    finalFolderName = pm.Value + safe;
                }
            }

            string newPath = Path.Combine(cfg.DataRoot, finalFolderName);
            if (newPath.Equals(group.FolderPath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string targetCleanTitle = CleanFolderTitle(finalFolderName);
            if (workspace != null && workspace.Folders != null)
            {
                for (int i = 0; i < workspace.Folders.Count; i++)
                {
                    FolderGroup other = workspace.Folders[i];
                    if (other == null || ReferenceEquals(other, group)) continue;
                    if (string.Equals(other.FolderName, group.FolderName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(other.FolderName, finalFolderName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(CleanFolderTitle(other.FolderName), targetCleanTitle, StringComparison.OrdinalIgnoreCase))
                    {
                        errorMessage = string.Format("已存在同名分类「{0}」，请换一个名称", targetCleanTitle);
                        return false;
                    }
                }
            }

            if (Directory.Exists(newPath))
            {
                errorMessage = string.Format("已存在同名分类目录「{0}」，请换一个名称", targetCleanTitle);
                return false;
            }

            try
            {
                Directory.Move(group.FolderPath, newPath);
                string oldKey = group.FolderName;
                string newKey = Path.GetFileName(newPath);

                EnsureFolderOrderInitialized(cfg, workspace);
                for (int i = 0; i < cfg.FolderOrder.Count; i++)
                {
                    if (cfg.FolderOrder[i].Equals(oldKey, StringComparison.OrdinalIgnoreCase))
                    {
                        cfg.FolderOrder[i] = newKey;
                        break;
                    }
                }

                List<string> savedItems;
                if (cfg.ItemOrder != null && cfg.ItemOrder.TryGetValue(oldKey, out savedItems))
                {
                    cfg.ItemOrder.Remove(oldKey);
                    cfg.ItemOrder[newKey] = savedItems;
                }

                string savedGridMode;
                if (cfg.FolderGridModes != null && cfg.FolderGridModes.TryGetValue(oldKey, out savedGridMode))
                {
                    cfg.FolderGridModes.Remove(oldKey);
                    cfg.FolderGridModes[newKey] = savedGridMode;
                }

                group.FolderName = newKey;
                group.DisplayName = CleanFolderTitle(newKey);
                group.FolderPath = newPath;
                if (group.Items != null)
                {
                    for (int i = 0; i < group.Items.Count; i++)
                    {
                        group.Items[i].FolderKey = newKey;
                        group.Items[i].FolderDisplayName = group.DisplayName;
                        if (!string.IsNullOrEmpty(group.Items[i].FileName))
                        {
                            group.Items[i].FilePath = Path.Combine(newPath, group.Items[i].FileName);
                        }
                    }
                }

                ConfigManager.Save(cfg);
                return true;
            }
            catch
            {
                errorMessage = "重命名失败：目录可能正被其他程序占用";
                return false;
            }
        }

        public static void MoveFolderOrder(AppConfig cfg, WorkspaceModel workspace, FolderGroup group, int delta)
        {
            if (cfg == null || workspace == null || group == null) return;
            EnsureFolderOrderInitialized(cfg, workspace);

            int idx = cfg.FolderOrder.FindIndex(delegate(string s) { return s.Equals(group.FolderName, StringComparison.OrdinalIgnoreCase); });
            if (idx < 0) return;

            int next = idx + delta;
            if (next < 0) next = 0;
            if (next >= cfg.FolderOrder.Count) next = cfg.FolderOrder.Count - 1;
            if (next == idx) return;

            string item = cfg.FolderOrder[idx];
            cfg.FolderOrder.RemoveAt(idx);
            cfg.FolderOrder.Insert(next, item);
            ConfigManager.Save(cfg);
        }

        private static void EnsureFolderOrderInitialized(AppConfig cfg, WorkspaceModel workspace)
        {
            if (cfg.FolderOrder == null) cfg.FolderOrder = new List<string>();
            if (workspace == null) return;
            HashSet<string> existing = new HashSet<string>(cfg.FolderOrder, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < workspace.Folders.Count; i++)
            {
                if (!existing.Contains(workspace.Folders[i].FolderName))
                {
                    cfg.FolderOrder.Add(workspace.Folders[i].FolderName);
                    existing.Add(workspace.Folders[i].FolderName);
                }
            }
        }

        internal static bool ValidateCategoryFolderName(string rawName, out string safeName, out string errorMessage)
        {
            safeName = null;
            errorMessage = null;

            string trimmed = (rawName ?? "").Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                errorMessage = "请输入分类名称";
                return false;
            }
            if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                errorMessage = "名称不能包含 \\ / : * ? \" < > | 等特殊字符";
                return false;
            }
            if (trimmed.StartsWith("_", StringComparison.Ordinal) || trimmed.StartsWith(".", StringComparison.Ordinal))
            {
                errorMessage = "名称不能以“_”或“.”开头";
                return false;
            }

            bool trimmedHasPrefix;
            string strippedFromTrimmed = StripLeadingNumericPrefixes(trimmed, out trimmedHasPrefix);

            string safe = SanitizeFolderName(trimmed).TrimEnd('.', ' ');
            if (string.IsNullOrEmpty(safe))
            {
                errorMessage = "名称无效或为系统保留名，请换一个名称";
                return false;
            }

            bool safeHasPrefix;
            string strippedFromSafe = StripLeadingNumericPrefixes(safe, out safeHasPrefix);

            if ((trimmedHasPrefix && string.IsNullOrEmpty(strippedFromTrimmed)) ||
                (safeHasPrefix && string.IsNullOrEmpty(strippedFromSafe)))
            {
                errorMessage = "名称不能仅包含序号前缀，请输入分类名称";
                return false;
            }

            if (IsReservedWindowsDeviceName(safe) ||
                (!string.IsNullOrEmpty(strippedFromSafe) && IsReservedWindowsDeviceName(strippedFromSafe)) ||
                (!string.IsNullOrEmpty(strippedFromTrimmed) && IsReservedWindowsDeviceName(strippedFromTrimmed)))
            {
                errorMessage = "名称无效或为系统保留名，请换一个名称";
                return false;
            }

            safeName = safe;
            return true;
        }

        private static string StripLeadingNumericPrefixes(string value, out bool hadPrefix)
        {
            hadPrefix = false;
            string cur = (value ?? "").Trim();
            for (int iter = 0; iter < 8; iter++)
            {
                if (!PrefixPattern.IsMatch(cur)) break;
                hadPrefix = true;
                cur = PrefixPattern.Replace(cur, "", 1).Trim();
            }
            return cur.TrimEnd('.', ' ');
        }

        internal static bool IsReservedWindowsDeviceName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string cleaned = name.Trim().TrimEnd('.', ' ');
            if (string.IsNullOrEmpty(cleaned)) return false;

            int dotIdx = cleaned.IndexOf('.');
            string stem = (dotIdx >= 0 ? cleaned.Substring(0, dotIdx) : cleaned).Trim().ToUpperInvariant();
            if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" ||
                stem == "CONIN$" || stem == "CONOUT$") return true;
            if (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)))
            {
                char d = stem[3];
                if ((d >= '0' && d <= '9') || d == '\u00B9' || d == '\u00B2' || d == '\u00B3') return true;
            }
            return false;
        }

        private static string SanitizeFolderName(string name)
        {
            char[] bad = Path.GetInvalidFileNameChars();
            for (int i = 0; i < bad.Length; i++)
            {
                name = name.Replace(bad[i].ToString(), "");
            }
            while (name.StartsWith("_") || name.StartsWith("."))
            {
                name = name.Substring(1);
            }
            return name.Trim();
        }

        private static void CreateShortcut(string lnkPath, string targetPath)
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;
            object shell = null;
            object sc = null;
            try
            {
                shell = Activator.CreateInstance(shellType);
                sc = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
                Type scType = sc.GetType();
                scType.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { targetPath });
                if (File.Exists(targetPath))
                {
                    scType.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { Path.GetDirectoryName(targetPath) });
                }
                scType.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, sc, null);
            }
            finally
            {
                if (sc != null) { try { Marshal.FinalReleaseComObject(sc); } catch { } }
                if (shell != null) { try { Marshal.FinalReleaseComObject(shell); } catch { } }
            }
        }
    }
}
