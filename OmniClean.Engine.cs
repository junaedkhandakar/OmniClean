// OmniClean.Engine.cs
// Cleanup engine: storage scanning, category size pipeline, and deletion routines.
// Partial class compiled together with OmniClean.cs and OmniClean.Analysis.cs.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
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

        static double GetDirectorySizeMB(string path) {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return 0;
            double bytes = 0;
            try {
                var stack = new Stack<string>();
                stack.Push(path);
                while (stack.Count > 0) {
                    string current = stack.Pop();
                    try {
                        string[] files = Directory.GetFiles(current);
                        for (int i = 0; i < files.Length; i++) {
                            try {
                                bytes += new FileInfo(files[i]).Length;
                            } catch { }
                        }
                    } catch { }
                    try {
                        string[] dirs = Directory.GetDirectories(current);
                        for (int i = 0; i < dirs.Length; i++) {
                            stack.Push(dirs[i]);
                        }
                    } catch { }
                }
            } catch { }
            return bytes / (1024.0 * 1024.0);
        }

        static List<string> GetBrowserCachePaths(string localApp, string appData) {
            var paths = new List<string>();

            // Chromium based (Chrome, Edge, Brave): scan all user profiles (Default, Profile 1, etc.)
            string[] chromiumUsers = new string[] {
                Path.Combine(localApp, @"Google\Chrome\User Data"),
                Path.Combine(localApp, @"Microsoft\Edge\User Data"),
                Path.Combine(localApp, @"BraveSoftware\Brave-Browser\User Data")
            };
            foreach (var uDir in chromiumUsers) {
                if (!Directory.Exists(uDir)) continue;
                try {
                    foreach (var pDir in Directory.GetDirectories(uDir)) {
                        string name = Path.GetFileName(pDir);
                        if (name.Equals("Default", StringComparison.OrdinalIgnoreCase) ||
                            name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase) ||
                            name.StartsWith("Guest", StringComparison.OrdinalIgnoreCase)) {
                            paths.Add(Path.Combine(pDir, "Cache"));
                            paths.Add(Path.Combine(pDir, "Code Cache"));
                            paths.Add(Path.Combine(pDir, "GPUCache"));
                        }
                    }
                } catch { }
            }

            // Opera and Opera GX
            paths.Add(Path.Combine(localApp, @"Opera Software\Opera Stable\Cache"));
            paths.Add(Path.Combine(localApp, @"Opera Software\Opera GX Stable\Cache"));
            paths.Add(Path.Combine(appData, @"Opera Software\Opera Stable\GPUCache"));
            paths.Add(Path.Combine(appData, @"Opera Software\Opera GX Stable\GPUCache"));

            // Firefox
            string ffProfiles = Path.Combine(localApp, @"Mozilla\Firefox\Profiles");
            if (Directory.Exists(ffProfiles)) {
                try {
                    foreach (var p in Directory.GetDirectories(ffProfiles)) {
                        paths.Add(Path.Combine(p, "cache2"));
                        paths.Add(Path.Combine(p, "startupCache"));
                        paths.Add(Path.Combine(p, "jumpListCache"));
                    }
                } catch { }
            }

            return paths;
        }

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
                            if (cat.Id == "recycle_bin") {
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
                            } else if (cat.Id == "temp_files") {
                                size += GetDirectorySizeMB(Path.GetTempPath());
                                size += GetDirectorySizeMB(Path.Combine(winDir, "Temp"));
                            } else if (cat.Id == "browser_cache") {
                                foreach (var bPath in GetBrowserCachePaths(localApp, appData)) {
                                    size += GetDirectorySizeMB(bPath);
                                }
                            } else if (cat.Id == "win_update") {
                                size = GetDirectorySizeMB(Path.Combine(winDir, @"SoftwareDistribution\Download"));
                            } else if (cat.Id == "crash_dumps") {
                                size += GetDirectorySizeMB(Path.Combine(localApp, "CrashDumps"));
                                size += GetDirectorySizeMB(Path.Combine(progData, @"Microsoft\Windows\WER\ReportQueue"));
                                size += GetDirectorySizeMB(Path.Combine(progData, @"Microsoft\Windows\WER\ReportArchive"));
                            } else if (cat.Id == "empty_folders") {
                                size = 0.5;
                            } else if (cat.Id == "nvidia_artifacts") {
                                string ota = Path.Combine(progData, @"NVIDIA Corporation\NVIDIA app\UpdateFramework\ota-artifacts");
                                if (Directory.Exists(ota)) {
                                    size = GetDirectorySizeMB(ota);
                                } else {
                                    installed = false;
                                }
                            } else if (cat.Id == "capcut_old") {
                                string capApps = Path.Combine(localApp, @"CapCut\Apps");
                                if (Directory.Exists(capApps)) {
                                    var dirs = new List<string>(Directory.GetDirectories(capApps));
                                    if (dirs.Count > 1) {
                                        dirs.Sort();
                                        string latest = dirs[dirs.Count - 1];
                                        foreach (var d in dirs) {
                                            if (d != latest) size += GetDirectorySizeMB(d);
                                        }
                                    }
                                } else {
                                    installed = false;
                                }
                            } else if (cat.Id == "vscode_cache") {
                                string vscPath = Path.Combine(appData, "Code");
                                if (Directory.Exists(vscPath)) {
                                    size += GetDirectorySizeMB(Path.Combine(vscPath, "CachedExtensionVSIXs"));
                                    size += GetDirectorySizeMB(Path.Combine(vscPath, @"Crashpad\reports"));
                                    size += GetDirectorySizeMB(Path.Combine(vscPath, "CachedData"));
                                    size += GetDirectorySizeMB(Path.Combine(vscPath, "Cache"));
                                    size += GetDirectorySizeMB(Path.Combine(vscPath, "Code Cache"));
                                    size += GetDirectorySizeMB(Path.Combine(vscPath, "GPUCache"));
                                } else {
                                    installed = false;
                                }
                            } else if (cat.Id == "dev_cache") {
                                size += GetDirectorySizeMB(Path.Combine(localApp, "npm-cache"));
                                size += GetDirectorySizeMB(Path.Combine(localApp, @"pip\cache"));
                            } else if (cat.Id == "discord_cache") {
                                string dsc = Path.Combine(appData, "discord");
                                if (Directory.Exists(dsc)) {
                                    size += GetDirectorySizeMB(Path.Combine(dsc, "Cache"));
                                    size += GetDirectorySizeMB(Path.Combine(dsc, "Code Cache"));
                                    size += GetDirectorySizeMB(Path.Combine(dsc, "GPUCache"));
                                } else {
                                    installed = false;
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
                            if (cat.Id == "recycle_bin") {
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
                            } else if (cat.Id == "temp_files") {
                                CleanFolder(Path.GetTempPath(), isDryRun);
                                CleanFolder(Path.Combine(winDir, "Temp"), isDryRun);
                            } else if (cat.Id == "browser_cache") {
                                CleanBrowserCaches(isDryRun);
                            } else if (cat.Id == "win_update") {
                                CleanFolder(Path.Combine(winDir, @"SoftwareDistribution\Download"), isDryRun);
                            } else if (cat.Id == "crash_dumps") {
                                CleanFolder(Path.Combine(localApp, "CrashDumps"), isDryRun);
                                CleanFolder(Path.Combine(progData, @"Microsoft\Windows\WER\ReportQueue"), isDryRun);
                                CleanFolder(Path.Combine(progData, @"Microsoft\Windows\WER\ReportArchive"), isDryRun);
                            } else if (cat.Id == "empty_folders") {
                                CleanEmptyDirectories(Path.GetTempPath(), isDryRun);
                            } else if (cat.Id == "nvidia_artifacts") {
                                CleanFolder(Path.Combine(progData, @"NVIDIA Corporation\NVIDIA app\UpdateFramework\ota-artifacts"), isDryRun);
                            } else if (cat.Id == "capcut_old") {
                                string capApps = Path.Combine(localApp, @"CapCut\Apps");
                                if (Directory.Exists(capApps)) {
                                    var dirs = new List<string>(Directory.GetDirectories(capApps));
                                    if (dirs.Count > 1) {
                                        dirs.Sort();
                                        string latest = dirs[dirs.Count - 1];
                                        foreach (var d in dirs) {
                                            if (d != latest) {
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
                                        }
                                    }
                                }
                            } else if (cat.Id == "vscode_cache") {
                                string vscPath = Path.Combine(appData, "Code");
                                CleanFolder(Path.Combine(vscPath, "CachedExtensionVSIXs"), isDryRun);
                                CleanFolder(Path.Combine(vscPath, @"Crashpad\reports"), isDryRun);
                                CleanFolder(Path.Combine(vscPath, "CachedData"), isDryRun);
                                CleanFolder(Path.Combine(vscPath, "Cache"), isDryRun);
                                CleanFolder(Path.Combine(vscPath, "Code Cache"), isDryRun);
                                CleanFolder(Path.Combine(vscPath, "GPUCache"), isDryRun);
                            } else if (cat.Id == "dev_cache") {
                                CleanFolder(Path.Combine(localApp, "npm-cache"), isDryRun);
                                CleanFolder(Path.Combine(localApp, @"pip\cache"), isDryRun);
                            } else if (cat.Id == "discord_cache") {
                                string dsc = Path.Combine(appData, "discord");
                                CleanFolder(Path.Combine(dsc, "Cache"), isDryRun);
                                CleanFolder(Path.Combine(dsc, "Code Cache"), isDryRun);
                                CleanFolder(Path.Combine(dsc, "GPUCache"), isDryRun);
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
            string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            foreach (var bPath in GetBrowserCachePaths(localApp, appData)) {
                CleanFolder(bPath, isDryRun);
            }
        }

        static void CleanEmptyDirectories(string startLocation, bool isDryRun) {
            if (string.IsNullOrEmpty(startLocation) || !Directory.Exists(startLocation)) return;
            try {
                foreach (var d in Directory.GetDirectories(startLocation)) {
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
