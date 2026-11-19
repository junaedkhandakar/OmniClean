// OmniClean.Engine.cs
// Cleanup engine: storage scanning, category size pipeline, and deletion routines.
// Partial class compiled together with OmniClean.cs and OmniClean.Analysis.cs.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OmniClean {
    partial class Program {
        static bool IsAdministrator() {
            try {
                var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            } catch { return false; }
        }

        // A junction, symlink or other reparse point can point back up the
        // directory tree. Following one during a recursive walk makes the walk
        // loop forever and counts the same bytes many times, so every
        // recursive traversal in OmniClean skips these entries.
        static bool IsReparsePoint(FileSystemInfo info) {
            try {
                return (info.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
            } catch {
                return false;
            }
        }

        // Walks a directory tree and invokes onFile for every file found.
        // Unlike Directory.GetFiles(..., SearchOption.AllDirectories) this
        // never aborts the whole scan because one folder is unreadable, and
        // it re-checks the cancellation token between entries so a running
        // scan can actually be stopped.
        // Returns true when the walk was cancelled.
        static bool WalkFiles(string root, CancellationToken token, Action<string> onFile) {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return false;

            var stack = new Stack<string>();
            stack.Push(root);

            while (stack.Count > 0) {
                if (token.IsCancellationRequested) return true;
                string current = stack.Pop();

                string[] files = null;
                try { files = Directory.GetFiles(current); } catch { files = null; }
                if (files != null) {
                    for (int i = 0; i < files.Length; i++) {
                        if (token.IsCancellationRequested) return true;
                        try {
                            var fi = new FileInfo(files[i]);
                            if (IsReparsePoint(fi)) continue;
                            onFile(fi.FullName);
                        } catch { }
                    }
                }

                string[] dirs = null;
                try { dirs = Directory.GetDirectories(current); } catch { dirs = null; }
                if (dirs != null) {
                    for (int i = 0; i < dirs.Length; i++) {
                        try {
                            if (IsReparsePoint(new DirectoryInfo(dirs[i]))) continue;
                        } catch { continue; }
                        stack.Push(dirs[i]);
                    }
                }
            }
            return false;
        }

        static double GetDirectorySizeMB(string path) {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return 0;
            double bytes = 0;
            WalkFiles(path, CancellationToken.None, delegate(string filePath) {
                try { bytes += new FileInfo(filePath).Length; } catch { }
            });
            return bytes / (1024.0 * 1024.0);
        }

        // Size of a single file in MB. Used for the "icons_only" cleanup
        // mode, where only the thumbnail / icon cache database files inside a
        // folder are removed - the folder itself holds much more.
        static double GetFileSizeMB(string path) {
            if (string.IsNullOrEmpty(path)) return 0;
            try {
                var fi = new FileInfo(path);
                if (!fi.Exists) return 0;
                return fi.Length / (1024.0 * 1024.0);
            } catch {
                return 0;
            }
        }

        // CapCut installs each build in a version-named folder ("2.9.1").
        // A plain string sort is wrong here: "2.10.0" sorts BEFORE "2.9.1",
        // which would make the cleanup delete the newest build and keep an
        // old one. Compare the parsed numeric version instead.
        static Version ParseAppVersion(string folderName) {
            Match m = Regex.Match(folderName, @"^\d+(\.\d+){0,3}");
            if (!m.Success) return new Version(0, 0);
            try { return new Version(m.Value); } catch { return new Version(0, 0); }
        }

        // Returns the CapCut install folders that are older than the newest
        // one. The newest folder is always preserved.
        static List<string> GetCapCutOldVersionPaths(string localApp) {
            var outdated = new List<string>();
            string apps = Path.Combine(localApp, @"CapCut\Apps");
            if (!Directory.Exists(apps)) return outdated;

            var versioned = new List<KeyValuePair<string, Version>>();
            try {
                foreach (var dir in Directory.GetDirectories(apps)) {
                    string name = Path.GetFileName(dir);
                    if (!Regex.IsMatch(name, @"^\d+(\.\d+)+")) continue;
                    try {
                        if (IsReparsePoint(new DirectoryInfo(dir))) continue;
                    } catch { continue; }
                    versioned.Add(new KeyValuePair<string, Version>(dir, ParseAppVersion(name)));
                }
            } catch { return outdated; }

            if (versioned.Count <= 1) return outdated;

            // Highest version wins. Ties prefer the folder named exactly after
            // the version ("2.9.1" over "2.9.1-beta") so the release build is
            // the one that survives.
            versioned.Sort(delegate(KeyValuePair<string, Version> a, KeyValuePair<string, Version> b) {
                int cmp = a.Value.CompareTo(b.Value);
                if (cmp != 0) return cmp;
                bool aExact = string.Equals(a.Key, a.Value.ToString(), StringComparison.OrdinalIgnoreCase);
                bool bExact = string.Equals(b.Key, b.Value.ToString(), StringComparison.OrdinalIgnoreCase);
                if (aExact != bExact) return aExact ? 1 : -1;
                return string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase);
            });

            string latest = versioned[versioned.Count - 1].Key;
            foreach (var pair in versioned) {
                if (!string.Equals(pair.Key, latest, StringComparison.OrdinalIgnoreCase)) {
                    outdated.Add(pair.Key);
                }
            }
            return outdated;
        }

        // The browser and GPU cache resolvers now live in OmniClean.Resolvers.cs
        // so they can follow the shared taxonomy and cover every browser and
        // graphics vendor, not just a fixed list.


        static void RunScanAsync() {
            if (isScanning || isCleaning) return;
            isScanning = true;
            btnRescan.IsEnabled = false;
            btnClean.IsEnabled = false;
            bannerSuccess.Visibility = Visibility.Collapsed;

            foreach (var c in categories) {
                c.BadgeRef.Text = "...";
                c.BadgeRef.Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));
            }

            Log(string.Format("Scanning storage volume {0}: for cleanable caches...", selectedDriveLetter));

            Task.Run(new Action(() => {
                try {
                    string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                    string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    string progData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                    string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

                    foreach (var cat in categories) {
                        double size = 0;
                        bool installed = true;

                        try {
                            CleanupTarget target = FindTaxonomyTarget(cat.Id);

                            if (target != null && target.Kind == "recycle") {
                                size = 0;
                                try {
                                    SHQUERYRBINFO rbInfo = new SHQUERYRBINFO();
                                    rbInfo.cbSize = Marshal.SizeOf(typeof(SHQUERYRBINFO));
                                    string root = selectedDriveLetter + @":\";
                                    if (SHQueryRecycleBin(root, ref rbInfo) == 0) {
                                        size = rbInfo.i64Size / (1024.0 * 1024.0);
                                    } else {
                                        size = GetDirectorySizeMB(selectedDriveLetter + @":\$Recycle.Bin");
                                    }
                                } catch { }
                            } else if (target != null && target.Kind == "empty_dirs") {
                                size = 0.5;
                            } else {
                                // Every remaining target kind resolves to a set
                                // of folders. A target counts as "not detected"
                                // when none of its folders exist on this
                                // machine - that is how a category that does
                                // not apply here (an AMD cache on an Intel
                                // laptop) is reported honestly.
                                var targetPaths = ResolveTargetPaths(target);
                                if (targetPaths.Count == 0) {
                                    installed = false;
                                } else {
                                    string mode = GetCleanupMode(target);
                                    foreach (var p in targetPaths) {
                                        if (mode == "icons_only") {
                                            foreach (var f in GetIconCacheFiles(p)) {
                                                size += GetFileSizeMB(f);
                                            }
                                        } else {
                                            size += GetDirectorySizeMB(p);
                                        }
                                    }
                                }
                            }
                        } catch { }

                        cat.SizeMB = size;
                        cat.IsInstalled = installed;

                        var capturedCat = cat;
                        double capturedSize = size;
                        bool capturedInstalled = installed;
                        mainWindow.Dispatcher.Invoke(new Action(() => {
                            if (!capturedInstalled) {
                                capturedCat.BadgeRef.Text = "Not Detected";
                                capturedCat.BadgeRef.Foreground = new SolidColorBrush(Color.FromRgb(0x47, 0x55, 0x69));
                                capturedCat.CheckBoxRef.IsEnabled = false;
                                capturedCat.CheckBoxRef.IsChecked = false;
                            } else if (capturedSize > 1024) {
                                capturedCat.BadgeRef.Text = string.Format("{0:F2} GB", capturedSize / 1024.0);
                                capturedCat.BadgeRef.Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x84, 0xFC));
                            } else if (capturedSize > 0.05) {
                                capturedCat.BadgeRef.Text = string.Format("{0:F1} MB", capturedSize);
                                capturedCat.BadgeRef.Foreground = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8));
                            } else {
                                capturedCat.BadgeRef.Text = "Clean";
                                capturedCat.BadgeRef.Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81));
                            }
                        }));
                    }
                } finally {
                    mainWindow.Dispatcher.Invoke(new Action(() => {
                        isScanning = false;
                        btnRescan.IsEnabled = true;
                        UpdateSelectedSize();
                        Log("Scan completed.");
                    }));
                }
            }));
        }

        static void StartCleanAsync() {
            if (isCleaning || isScanning) return;

            bool isDryRun = (chkDryRun.IsChecked == true);

            // Snapshot selected category IDs and sizes on the UI thread before starting the background task
            var selectedIds = new HashSet<string>();
            int selectedCount = 0;
            double selectedMB = 0;
            foreach (var c in categories) {
                if (c.CheckBoxRef.IsChecked == true && c.IsInstalled) {
                    selectedIds.Add(c.Id);
                    selectedCount++;
                    selectedMB += c.SizeMB;
                }
            }
            if (selectedCount == 0) {
                MessageBox.Show(mainWindow, "No cleanup categories are selected. Tick at least one target first.", "OmniClean", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            string selectedStr = selectedMB > 1024 ? string.Format("{0:F2} GB", selectedMB / 1024.0) : string.Format("{0:F1} MB", selectedMB);
            string prompt = isDryRun
                ? string.Format("Preview Mode is ON (Dry Run) - nothing will be deleted.\r\n\r\nSimulate cleanup of {0} selected targets (~{1} estimated)?", selectedCount, selectedStr)
                : string.Format("This will PERMANENTLY delete temporary files from {0} selected targets (~{1} estimated).\r\n\r\nA deletion manifest will be saved so every removed item stays auditable.\r\n\r\nContinue?", selectedCount, selectedStr);
            if (MessageBox.Show(mainWindow, prompt, isDryRun ? "Confirm Preview Run (No Deletion)" : "Confirm Cleanup", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) {
                return;
            }

            isCleaning = true;
            btnClean.IsEnabled = false;
            btnClean.Content = "Cleaning in Progress...";
            bannerSuccess.Visibility = Visibility.Collapsed;

            BeginCleanManifest(isDryRun);
            Log(isDryRun ? "Starting Safe Dry Run simulation (Preview Mode)..." : "Starting clean on selected targets...");

            Task.Run(new Action(() => {
                try {
                    string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                    string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    string progData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                    string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

                    foreach (var cat in categories) {
                        // Check against the thread-safe set captured on UI thread; NEVER touch CheckBoxRef here!
                        if (!selectedIds.Contains(cat.Id)) continue;

                        Log((isDryRun ? "[Simulate] " : "[Purging] ") + cat.Name);

                        try {
                            CleanupTarget target = FindTaxonomyTarget(cat.Id);

                            if (target != null && target.Kind == "recycle") {
                                if (!isDryRun) {
                                    SHQUERYRBINFO rbInfo = new SHQUERYRBINFO();
                                    rbInfo.cbSize = Marshal.SizeOf(typeof(SHQUERYRBINFO));
                                    string root = selectedDriveLetter + @":\";
                                    long preCount = 0;
                                    double preMB = 0;
                                    if (SHQueryRecycleBin(root, ref rbInfo) == 0) {
                                        preCount = rbInfo.i64NumItems;
                                        preMB = rbInfo.i64Size / (1024.0 * 1024.0);
                                    }
                                    uint rbResult = SHEmptyRecycleBin(IntPtr.Zero, root, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
                                    if (rbResult == 0) {
                                        cleanDeletedCount += Math.Max(1, preCount);
                                        cleanFreedMB += preMB;
                                        Manifest(string.Format("[RECYCLE-BIN] Emptied {0} items, freed {1:F2} MB", preCount, preMB));
                                    } else {
                                        Manifest("[RECYCLE-BIN] SHEmptyRecycleBin returned 0x" + rbResult.ToString("X") + " (locked or partial)");
                                    }
                                } else {
                                    Manifest("[PREVIEW] Recycle Bin on drive " + selectedDriveLetter + ":");
                                }
                            } else if (target != null && target.Kind == "browser_cache") {
                                CleanBrowserCaches(isDryRun);
                            } else if (target != null && target.Kind == "empty_dirs") {
                                CleanEmptyDirectories(Path.GetTempPath(), isDryRun);
                            } else if (target != null && target.Kind == "capcut_old") {
                                foreach (var d in GetCapCutOldVersionPaths(localApp)) {
                                    try {
                                        if (!isDryRun) {
                                            CleanFolder(d, false);
                                            try { Directory.Delete(d, true); } catch { }
                                        } else {
                                            Manifest("[PREVIEW] " + d + " (recursive)");
                                        }
                                    } catch (Exception ex) {
                                        cleanFailedCount++;
                                        Manifest("[FAILED]  " + d + " -- " + ex.Message);
                                    }
                                }
                            } else {
                                // Everything else is a "paths" or "gpu_cache"
                                // target: resolve the folders the taxonomy
                                // declares and clear them. Adding a new
                                // category to targets.json therefore needs
                                // no code change here.
                                foreach (string path in ResolveTargetPaths(target)) {
                                    CleanFolder(path, isDryRun);
                                }
                            }
                        } catch (Exception ex) {
                            Log("Warning: " + ex.Message);
                        }
                    }
                } catch (Exception ex) {
                    Log("Cleanup error: " + ex.Message);
                    Manifest("[FATAL] " + ex.ToString());
                } finally {
                    mainWindow.Dispatcher.Invoke(new Action(() => {
                        isCleaning = false;
                        btnClean.Content = "⚡ Clean Selected Storage";
                        btnClean.IsEnabled = true;
                        UpdateDiskMetrics();

                        string freedStr = cleanFreedMB > 1024 ? string.Format("{0:F2} GB", cleanFreedMB / 1024.0) : string.Format("{0:F1} MB", cleanFreedMB);
                        string summary = string.Format("{0} items removed, ~{1} freed, {2} failures", cleanDeletedCount, freedStr, cleanFailedCount);
                        EndCleanManifest(isDryRun ? "Preview finished - nothing was deleted." : "Summary: " + summary);

                        if (isDryRun) {
                            Log("Simulation finished. No files were modified (Dry Run active).");
                        } else {
                            Log("Cleanup finished: " + summary + ".");
                            Log("Deletion manifest: " + lastCleanLogPath);
                            if (cleanFailedCount > 0) {
                                if (!IsAdministrator()) {
                                    Log("Note: Some files could not be removed (in use by running apps, or system folders requiring Administrator privileges).");
                                } else {
                                    Log("Some items could not be removed (files currently locked by running applications) - see manifest for details.");
                                }
                            }
                        }
                        bannerSuccess.Visibility = Visibility.Visible;
                        txtSuccessMsg.Text = isDryRun
                            ? "Simulation complete (Dry Run): 0 files deleted. Uncheck 'Dry Run' to delete files."
                            : string.Format("Cleanup complete - {0} removed, {1} freed.", cleanDeletedCount, freedStr);
                        RunScanAsync();
                    }));
                }
            }));
        }

        static void CleanFolder(string path, bool isDryRun) {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
            CleanFolderRecursive(path, isDryRun);
        }

        static void CleanFolderRecursive(string currentDir, bool isDryRun) {
            try {
                string[] files = null;
                try {
                    files = Directory.GetFiles(currentDir);
                } catch (Exception ex) {
                    cleanFailedCount++;
                    Manifest("[FAILED]  Cannot list files in " + currentDir + " -- " + ex.Message);
                }

                if (files != null) {
                    for (int i = 0; i < files.Length; i++) {
                        string f = files[i];
                        try {
                            if (isDryRun) {
                                Manifest("[PREVIEW] " + f);
                            } else {
                                FileInfo fi = new FileInfo(f);
                                double mb = fi.Length / (1024.0 * 1024.0);
                                if ((fi.Attributes & (FileAttributes.ReadOnly | FileAttributes.System)) != 0) {
                                    try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                                }
                                File.Delete(f);
                                cleanDeletedCount++;
                                cleanFreedMB += mb;
                                Manifest("[DELETED] " + f + string.Format(" ({0:F2} MB)", mb));
                            }
                        } catch (Exception ex) {
                            cleanFailedCount++;
                            Manifest("[FAILED]  " + f + " -- " + ex.Message);
                        }
                    }
                }

                string[] subDirs = null;
                try {
                    subDirs = Directory.GetDirectories(currentDir);
                } catch (Exception ex) {
                    cleanFailedCount++;
                    Manifest("[FAILED]  Cannot list directories in " + currentDir + " -- " + ex.Message);
                }

                if (subDirs != null) {
                    for (int i = 0; i < subDirs.Length; i++) {
                        string d = subDirs[i];
                        // Never descend into a junction/symlink: it can point
                        // back up the tree and the recursion would never end.
                        try {
                            if (IsReparsePoint(new DirectoryInfo(d))) continue;
                        } catch { continue; }
                        CleanFolderRecursive(d, isDryRun);
                        if (!isDryRun) {
                            try {
                                if (Directory.GetFileSystemEntries(d).Length == 0) {
                                    if ((File.GetAttributes(d) & (FileAttributes.ReadOnly | FileAttributes.System)) != 0) {
                                        try { File.SetAttributes(d, FileAttributes.Normal); } catch { }
                                    }
                                    Directory.Delete(d, false);
                                    cleanDeletedCount++;
                                    Manifest("[DELETED] " + d + " (empty directory)");
                                }
                            } catch { }
                        }
                    }
                }
            } catch (Exception ex) {
                Manifest("[ERROR]   Unexpected error in " + currentDir + " -- " + ex.Message);
            }
        }

        static void CleanBrowserCaches(bool isDryRun) {
            foreach (var bPath in GetBrowserCachePaths()) {
                CleanFolder(bPath, isDryRun);
            }
        }

        static void CleanEmptyDirectories(string startLocation, bool isDryRun) {
            if (string.IsNullOrEmpty(startLocation) || !Directory.Exists(startLocation)) return;
            try {
                foreach (var d in Directory.GetDirectories(startLocation)) {
                    try {
                        if (IsReparsePoint(new DirectoryInfo(d))) continue;
                    } catch { continue; }
                    CleanEmptyDirectories(d, isDryRun);
                    try {
                        if (Directory.GetFileSystemEntries(d).Length == 0) {
                            if (!isDryRun) {
                                if ((File.GetAttributes(d) & (FileAttributes.ReadOnly | FileAttributes.System)) != 0) {
                                    try { File.SetAttributes(d, FileAttributes.Normal); } catch { }
                                }
                                Directory.Delete(d, false);
                                cleanDeletedCount++;
                                Manifest("[DELETED] " + d + " (empty directory)");
                            } else {
                                Manifest("[PREVIEW] " + d + " (empty directory)");
                            }
                        }
                    } catch (Exception ex) {
                        cleanFailedCount++;
                        Manifest("[FAILED]  " + d + " -- " + ex.Message);
                    }
                }
            } catch { }
        }
    }
}
