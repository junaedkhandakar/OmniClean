// OmniClean.Engine.cs
// Cleanup engine: storage scanning, category size pipeline, and deletion routines.
// Partial class compiled together with OmniClean.cs and OmniClean.Analysis.cs.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OmniClean {
    partial class Program {
        static double GetDirectorySizeMB(string path) {
            if (!Directory.Exists(path)) return 0;
            double bytes = 0;
            try {
                string[] files = Directory.GetFiles(path, "*.*", SearchOption.AllDirectories);
                foreach (var f in files) {
                    try {
                        FileInfo fi = new FileInfo(f);
                        bytes += fi.Length;
                    } catch { }
                }
            } catch { }
            return bytes / (1024.0 * 1024.0);
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
                                string binPath = selectedDriveLetter + @":\$Recycle.Bin";
                                size = GetDirectorySizeMB(binPath);
                            } catch { }
                        } else if (cat.Id == "temp_files") {
                            size += GetDirectorySizeMB(Path.GetTempPath());
                            size += GetDirectorySizeMB(Path.Combine(winDir, "Temp"));
                        } else if (cat.Id == "browser_cache") {
                            size += GetDirectorySizeMB(Path.Combine(localApp, @"Google\Chrome\User Data\Default\Cache"));
                            size += GetDirectorySizeMB(Path.Combine(localApp, @"Microsoft\Edge\User Data\Default\Cache"));
                            size += GetDirectorySizeMB(Path.Combine(localApp, @"BraveSoftware\Brave-Browser\User Data\Default\Cache"));
                            size += GetDirectorySizeMB(Path.Combine(localApp, @"Mozilla\Firefox\Profiles"));
                            size += GetDirectorySizeMB(Path.Combine(localApp, @"Opera Software\Opera Stable\Cache"));
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
                            } else {
                                installed = false;
                            }
                        } else if (cat.Id == "dev_cache") {
                            size += GetDirectorySizeMB(Path.Combine(localApp, "npm-cache"));
                            size += GetDirectorySizeMB(Path.Combine(localApp, @"pip\cache"));
                        } else if (cat.Id == "discord_cache") {
                            string dsc = Path.Combine(appData, @"discord\Cache");
                            if (Directory.Exists(dsc)) {
                                size = GetDirectorySizeMB(dsc);
                            } else {
                                installed = false;
                            }
                        }
                    } catch { }

                    cat.SizeMB = size;
                    cat.IsInstalled = installed;

                    mainWindow.Dispatcher.Invoke(new Action(() => {
                        if (!installed) {
                            cat.BadgeRef.Text = "Not Detected";
                            cat.BadgeRef.Foreground = new SolidColorBrush(Color.FromRgb(0x47, 0x55, 0x69));
                            cat.CheckBoxRef.IsEnabled = false;
                            cat.CheckBoxRef.IsChecked = false;
                        } else if (size > 1024) {
                            cat.BadgeRef.Text = string.Format("{0:F2} GB", size / 1024.0);
                            cat.BadgeRef.Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x84, 0xFC));
                        } else if (size > 0.05) {
                            cat.BadgeRef.Text = string.Format("{0:F1} MB", size);
                            cat.BadgeRef.Foreground = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8));
                        } else {
                            cat.BadgeRef.Text = "Clean";
                            cat.BadgeRef.Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81));
                        }
                    }));
                }

                mainWindow.Dispatcher.Invoke(new Action(() => {
                    isScanning = false;
                    btnRescan.IsEnabled = true;
                    UpdateSelectedSize();
                    Log("Scan completed.");
                }));
            }));
        }

        static void StartCleanAsync() {
            if (isCleaning || isScanning) return;

            bool isDryRun = (chkDryRun.IsChecked == true);

            // v1.0.4: explicit confirmation with an impact summary before anything is deleted.
            int selectedCount = 0;
            double selectedMB = 0;
            foreach (var c in categories) {
                if (c.CheckBoxRef.IsChecked == true && c.IsInstalled) {
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
                ? string.Format("Preview Mode is ON - nothing will be deleted.\r\n\r\nSimulate cleanup of {0} selected targets (~{1} estimated)?", selectedCount, selectedStr)
                : string.Format("This will PERMANENTLY delete temporary files from {0} selected targets (~{1} estimated).\r\n\r\nA deletion manifest will be saved so every removed item stays auditable.\r\n\r\nContinue?", selectedCount, selectedStr);
            if (MessageBox.Show(mainWindow, prompt, isDryRun ? "Confirm Preview Run" : "Confirm Cleanup", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) {
                return;
            }

            isCleaning = true;
            btnClean.IsEnabled = false;
            btnClean.Content = "Cleaning in Progress...";
            bannerSuccess.Visibility = Visibility.Collapsed;

            BeginCleanManifest(isDryRun);
            Log(isDryRun ? "Starting Safe Dry Run simulation (Preview Mode)..." : "Starting clean on selected targets...");

            Task.Run(new Action(() => {
                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string progData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

                foreach (var cat in categories) {
                    if (cat.CheckBoxRef.IsChecked != true || !cat.IsInstalled) continue;

                    Log((isDryRun ? "[Simulate] " : "[Purging] ") + cat.Name);

                    try {
                        if (cat.Id == "recycle_bin") {
                            if (!isDryRun) {
                                uint rbResult = SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
                                Manifest("[RECYCLE-BIN] SHEmptyRecycleBin returned 0x" + rbResult.ToString("X") + (rbResult == 0 ? " (success)" : " (locked or partial)"));
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
                                                    cleanFreedMB += GetDirectorySizeMB(d);
                                                    Directory.Delete(d, true);
                                                    cleanDeletedCount++;
                                                    Manifest("[DELETED] " + d + " (recursive)");
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
                        } else if (cat.Id == "dev_cache") {
                            CleanFolder(Path.Combine(localApp, "npm-cache"), isDryRun);
                            CleanFolder(Path.Combine(localApp, @"pip\cache"), isDryRun);
                        } else if (cat.Id == "discord_cache") {
                            CleanFolder(Path.Combine(appData, @"discord\Cache"), isDryRun);
                        }
                    } catch (Exception ex) {
                        Log("Warning: " + ex.Message);
                    }
                }

                mainWindow.Dispatcher.Invoke(new Action(() => {
                    isCleaning = false;
                    btnClean.Content = "⚡ Clean Selected Storage";
                    btnClean.IsEnabled = true;
                    UpdateDiskMetrics();

                    string freedStr = cleanFreedMB > 1024 ? string.Format("{0:F2} GB", cleanFreedMB / 1024.0) : string.Format("{0:F1} MB", cleanFreedMB);
                    string summary = string.Format("{0} items removed, ~{1} freed, {2} failures", cleanDeletedCount, freedStr, cleanFailedCount);
                    EndCleanManifest(isDryRun ? "Preview finished - nothing was deleted." : "Summary: " + summary);

                    if (isDryRun) {
                        Log("Simulation finished. No files were modified.");
                    } else {
                        Log("Cleanup finished: " + summary + ".");
                        Log("Deletion manifest: " + lastCleanLogPath);
                        if (cleanFailedCount > 0) {
                            Log("Some items could not be removed (in use or access denied) - see manifest for details.");
                        }
                    }
                    bannerSuccess.Visibility = Visibility.Visible;
                    txtSuccessMsg.Text = isDryRun
                        ? "Simulation complete. Storage preview calculated."
                        : string.Format("Cleanup complete - {0} removed, {1} freed.", cleanDeletedCount, freedStr);
                    RunScanAsync();
                }));
            }));
        }

        static void CleanFolder(string path, bool isDryRun) {
            if (!Directory.Exists(path)) return;
            try {
                foreach (var f in Directory.GetFiles(path)) {
                    try {
                        if (isDryRun) {
                            Manifest("[PREVIEW] " + f);
                        } else {
                            FileInfo fi = new FileInfo(f);
                            double mb = fi.Length / (1024.0 * 1024.0);
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
                foreach (var d in Directory.GetDirectories(path)) {
                    try {
                        if (isDryRun) {
                            Manifest("[PREVIEW] " + d + " (recursive)");
                        } else {
                            cleanFreedMB += GetDirectorySizeMB(d);
                            Directory.Delete(d, true);
                            cleanDeletedCount++;
                            Manifest("[DELETED] " + d + " (recursive)");
                        }
                    } catch (Exception ex) {
                        cleanFailedCount++;
                        Manifest("[FAILED]  " + d + " -- " + ex.Message);
                    }
                }
            } catch (Exception ex) {
                Manifest("[ERROR]   cannot enumerate " + path + " -- " + ex.Message);
            }
        }

        static void CleanBrowserCaches(bool isDryRun) {
            string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string[] browserCaches = new string[] {
                Path.Combine(localApp, @"Google\Chrome\User Data\Default\Cache"),
                Path.Combine(localApp, @"Google\Chrome\User Data\Default\Code Cache"),
                Path.Combine(localApp, @"Google\Chrome\User Data\Default\GPUCache"),
                Path.Combine(localApp, @"Microsoft\Edge\User Data\Default\Cache"),
                Path.Combine(localApp, @"Microsoft\Edge\User Data\Default\Code Cache"),
                Path.Combine(localApp, @"Microsoft\Edge\User Data\Default\GPUCache"),
                Path.Combine(localApp, @"BraveSoftware\Brave-Browser\User Data\Default\Cache"),
                Path.Combine(localApp, @"Opera Software\Opera Stable\Cache")
            };

            foreach (var bPath in browserCaches) {
                CleanFolder(bPath, isDryRun);
            }
        }

        static void CleanEmptyDirectories(string startLocation, bool isDryRun) {
            try {
                foreach (var d in Directory.GetDirectories(startLocation)) {
                    CleanEmptyDirectories(d, isDryRun);
                    if (Directory.GetFiles(d).Length == 0 && Directory.GetDirectories(d).Length == 0) {
                        try {
                            if (!isDryRun) {
                                Directory.Delete(d, false);
                                cleanDeletedCount++;
                                Manifest("[DELETED] " + d + " (empty directory)");
                            } else {
                                Manifest("[PREVIEW] " + d + " (empty directory)");
                            }
                        } catch (Exception ex) {
                            cleanFailedCount++;
                            Manifest("[FAILED]  " + d + " -- " + ex.Message);
                        }
                    }
                }
            } catch { }
        }


    }
}

