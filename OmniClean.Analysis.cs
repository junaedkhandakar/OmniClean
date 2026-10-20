// OmniClean.Analysis.cs
// Large-file profiler, SHA-256 duplicate finder, and startup optimizer.
// Partial class compiled together with OmniClean.cs and OmniClean.Engine.cs.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace OmniClean {
    partial class Program {
        // ================= TAB 1: LARGE FILES FINDER =================
        static void ScanLargeFilesAsync() {
            if (largeFilesCts != null) { try { largeFilesCts.Dispose(); } catch { } }
            largeFilesCts = new CancellationTokenSource();
            CancellationToken token = largeFilesCts.Token;

            btnScanLargeFiles.IsEnabled = false;
            btnScanLargeFiles.Content = "Scanning Drive...";
            btnStopLargeFiles.Visibility = Visibility.Visible;
            btnStopLargeFiles.IsEnabled = true;
            pnlLargeFiles.Children.Clear();
            txtLargeFilesSummary.Text = "Scanning user profile and common storage directories for files > 100 MB...";

            Task.Run(new Action(() => {
                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string[] searchDirs = new string[] {
                    Path.Combine(userProfile, "Downloads"),
                    Path.Combine(userProfile, "Documents"),
                    Path.Combine(userProfile, "Desktop"),
                    Path.Combine(userProfile, "Videos"),
                    Path.Combine(userProfile, "Music"),
                    Path.Combine(userProfile, "Pictures")
                };

                var found = new List<LargeFileInfo>();
                bool cancelled = false;

                try {
                    foreach (var dir in searchDirs) {
                        if (token.IsCancellationRequested) { cancelled = true; break; }
                        if (Directory.Exists(dir)) {
                            try {
                                foreach (var f in Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories)) {
                                    if (token.IsCancellationRequested) { cancelled = true; break; }
                                    try {
                                        FileInfo fi = new FileInfo(f);
                                        if (fi.Length > 100 * 1024 * 1024) { // > 100 MB
                                            found.Add(new LargeFileInfo {
                                                FilePath = fi.FullName,
                                                FileName = fi.Name,
                                                Extension = fi.Extension.ToUpper().Replace(".", ""),
                                                Bytes = fi.Length,
                                                SizeMB = fi.Length / (1024.0 * 1024.0)
                                            });
                                        }
                                    } catch { }
                                }
                            } catch { }
                        }
                    }
                } catch { }

                if (!cancelled) {
                    found.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
                    allLargeFiles = found;
                }

                mainWindow.Dispatcher.Invoke(new Action(() => {
                    btnScanLargeFiles.IsEnabled = true;
                    btnScanLargeFiles.Content = "🔍 Scan for Large Files";
                    btnStopLargeFiles.Visibility = Visibility.Collapsed;
                    if (cancelled) {
                        txtLargeFilesSummary.Text = "Scan cancelled by user. Results discarded - run the scan again when ready.";
                    } else {
                        ApplyLargeFileFilter(currentMinLargeFileBytes);
                    }
                }));
            }));
        }

        static void ApplyLargeFileFilter(long minBytes) {
            currentMinLargeFileBytes = minBytes;
            pnlLargeFiles.Children.Clear();

            var filtered = allLargeFiles.FindAll(x => x.Bytes >= minBytes);
            txtLargeFilesSummary.Text = string.Format("Found {0} files matching size filter.", filtered.Count);

            if (filtered.Count == 0) {
                TextBlock empty = new TextBlock();
                empty.Text = "No large files matching this filter were found in scanned folders.";
                empty.Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
                empty.Margin = new Thickness(0, 20, 0, 0);
                pnlLargeFiles.Children.Add(empty);
                return;
            }

            foreach (var item in filtered) {
                Border card = new Border();
                card.Background = CardBgBrush();
                card.BorderBrush = CardBorderBrush();
                card.BorderThickness = new Thickness(1);
                card.Padding = new Thickness(14, 10, 14, 10);
                card.Margin = new Thickness(0, 0, 0, 7);
                card.CornerRadius = new CornerRadius(10);

                card.MouseEnter += (s, e) => {
                    card.Background = CardHoverBrush();
                    card.BorderBrush = CardHoverBorderBrush();
                };
                card.MouseLeave += (s, e) => {
                    card.Background = CardBgBrush();
                    card.BorderBrush = CardBorderBrush();
                };

                Grid grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition() { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition() { Width = GridLength.Auto });

                // Extension Badge
                Border extBadge = new Border();
                extBadge.Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1B, 0x4B));
                extBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(0x63, 0x66, 0xF1));
                extBadge.BorderThickness = new Thickness(1);
                extBadge.CornerRadius = new CornerRadius(6);
                extBadge.Padding = new Thickness(6, 4, 6, 4);
                extBadge.Margin = new Thickness(0, 0, 12, 0);
                extBadge.VerticalAlignment = VerticalAlignment.Center;

                TextBlock txtExt = new TextBlock();
                txtExt.Text = string.IsNullOrEmpty(item.Extension) ? "FILE" : item.Extension;
                txtExt.FontSize = 10;
                txtExt.FontWeight = FontWeights.Bold;
                txtExt.Foreground = new SolidColorBrush(Color.FromRgb(0xA5, 0xB4, 0xFC));
                extBadge.Child = txtExt;

                Grid.SetColumn(extBadge, 0);
                grid.Children.Add(extBadge);

                StackPanel sp = new StackPanel();
                TextBlock name = new TextBlock();
                name.Text = item.FileName;
                name.FontWeight = FontWeights.Bold;
                name.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFC));
                name.FontSize = 13;

                TextBlock path = new TextBlock();
                path.Text = item.FilePath;
                path.FontSize = 11;
                path.Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
                path.TextTrimming = TextTrimming.CharacterEllipsis;
                path.MaxWidth = 550;

                sp.Children.Add(name);
                sp.Children.Add(path);
                Grid.SetColumn(sp, 1);
                grid.Children.Add(sp);

                StackPanel rightSp = new StackPanel();
                rightSp.Orientation = Orientation.Horizontal;
                rightSp.VerticalAlignment = VerticalAlignment.Center;

                Border sizeBadge = new Border();
                sizeBadge.Background = new SolidColorBrush(Color.FromRgb(0x23, 0x1B, 0x4D));
                sizeBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0x3F, 0x9E));
                sizeBadge.BorderThickness = new Thickness(1);
                sizeBadge.Padding = new Thickness(10, 4, 10, 4);
                sizeBadge.CornerRadius = new CornerRadius(11);
                sizeBadge.Margin = new Thickness(0, 0, 8, 0);

                TextBlock txtSize = new TextBlock();
                txtSize.Text = item.SizeMB > 1024 ? string.Format("{0:F2} GB", item.SizeMB / 1024.0) : string.Format("{0:F0} MB", item.SizeMB);
                txtSize.Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x84, 0xFC));
                txtSize.FontWeight = FontWeights.Bold;
                txtSize.FontSize = 11;
                sizeBadge.Child = txtSize;

                Button btnShow = new Button();
                btnShow.Style = (Style)mainWindow.TryFindResource("SecondaryBtn");
                btnShow.Content = "Open Folder";
                btnShow.Background = new SolidColorBrush(Color.FromRgb(0x16, 0x20, 0x32));
                btnShow.Foreground = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1));
                btnShow.BorderThickness = new Thickness(1);
                btnShow.BorderBrush = new SolidColorBrush(Color.FromRgb(0x22, 0x2F, 0x48));
                btnShow.Padding = new Thickness(10, 4, 10, 4);
                btnShow.FontSize = 11;
                btnShow.Cursor = Cursors.Hand;
                btnShow.Margin = new Thickness(0, 0, 6, 0);
                btnShow.Click += (s, e) => {
                    try { Process.Start("explorer.exe", string.Format("/select,\"{0}\"", item.FilePath)); } catch { }
                };

                Button btnDeleteFile = new Button();
                btnDeleteFile.Style = (Style)mainWindow.TryFindResource("SecondaryBtn");
                btnDeleteFile.Content = "Delete";
                btnDeleteFile.Background = new SolidColorBrush(Color.FromRgb(0x3B, 0x18, 0x22));
                btnDeleteFile.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
                btnDeleteFile.BorderThickness = new Thickness(0);
                btnDeleteFile.Padding = new Thickness(10, 4, 10, 4);
                btnDeleteFile.FontSize = 11;
                btnDeleteFile.Cursor = Cursors.Hand;
                btnDeleteFile.Click += (s, e) => {
                    if (MessageBox.Show(mainWindow, "Are you sure you want to permanently delete this file?\r\n" + item.FilePath, "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes) {
                        try {
                            File.Delete(item.FilePath);
                            card.Visibility = Visibility.Collapsed;
                            allLargeFiles.Remove(item);
                            Log("Deleted large file: " + item.FilePath);
                        } catch (Exception ex) {
                            Log("Delete error: " + ex.Message);
                        }
                    }
                };

                rightSp.Children.Add(sizeBadge);
                rightSp.Children.Add(btnShow);
                rightSp.Children.Add(btnDeleteFile);
                Grid.SetColumn(rightSp, 2);
                grid.Children.Add(rightSp);

                card.Child = grid;
                pnlLargeFiles.Children.Add(card);
            }
        }

        // ================= TAB 2: DUPLICATE FINDER =================
        // Full-file SHA-256: the UI and README promise "100% byte-level
        // certainty", so the whole stream must be hashed - not just a prefix.
        static string GetFileHash(string path) {
            try {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sha = SHA256.Create()) {
                    return BitConverter.ToString(sha.ComputeHash(fs));
                }
            } catch { return Guid.NewGuid().ToString(); }
        }

        static void ScanDuplicatesAsync() {
            if (duplicatesCts != null) { try { duplicatesCts.Dispose(); } catch { } }
            duplicatesCts = new CancellationTokenSource();
            CancellationToken token = duplicatesCts.Token;

            btnScanDuplicates.IsEnabled = false;
            btnScanDuplicates.Content = "Scanning Duplicates...";
            btnStopDuplicates.Visibility = Visibility.Visible;
            btnStopDuplicates.IsEnabled = true;
            pnlDuplicates.Children.Clear();
            txtDuplicatesSummary.Text = "Scanning Downloads, Documents, and Pictures for duplicate matches...";

            Task.Run(new Action(() => {
                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string[] searchDirs = new string[] {
                    Path.Combine(userProfile, "Downloads"),
                    Path.Combine(userProfile, "Documents"),
                    Path.Combine(userProfile, "Pictures")
                };

                var sizeMap = new Dictionary<long, List<FileInfo>>();
                bool cancelled = false;
                foreach (var dirPath in searchDirs) {
                    if (token.IsCancellationRequested) { cancelled = true; break; }
                    if (Directory.Exists(dirPath)) {
                        try {
                            foreach (var f in Directory.GetFiles(dirPath, "*.*", SearchOption.AllDirectories)) {
                                if (token.IsCancellationRequested) { cancelled = true; break; }
                                try {
                                    var fi = new FileInfo(f);
                                    if (fi.Length > 512 * 1024) { // > 512 KB
                                        if (!sizeMap.ContainsKey(fi.Length)) sizeMap[fi.Length] = new List<FileInfo>();
                                        sizeMap[fi.Length].Add(fi);
                                    }
                                } catch { }
                            }
                        } catch { }
                    }
                }

                var matchedGroups = new List<DuplicateFileGroup>();
                foreach (var pair in sizeMap) {
                    if (token.IsCancellationRequested) { cancelled = true; break; }
                    if (pair.Value.Count > 1) {
                        var hashMap = new Dictionary<string, List<string>>();
                        foreach (var fi in pair.Value) {
                            if (token.IsCancellationRequested) { cancelled = true; break; }
                            string h = GetFileHash(fi.FullName);
                            if (!hashMap.ContainsKey(h)) hashMap[h] = new List<string>();
                            hashMap[h].Add(fi.FullName);
                        }

                        foreach (var hPair in hashMap) {
                            if (hPair.Value.Count > 1) {
                                matchedGroups.Add(new DuplicateFileGroup {
                                    Hash = hPair.Key,
                                    Bytes = pair.Key,
                                    SizeMB = pair.Key / (1024.0 * 1024.0),
                                    FilePaths = hPair.Value
                                });
                            }
                        }
                    }
                }

                if (!cancelled) duplicateGroups = matchedGroups;

                mainWindow.Dispatcher.Invoke(new Action(() => {
                    btnScanDuplicates.IsEnabled = true;
                    btnScanDuplicates.Content = "🔎 Find Duplicates";
                    btnStopDuplicates.Visibility = Visibility.Collapsed;

                    if (cancelled) {
                        txtDuplicatesSummary.Text = "Scan cancelled by user. Results discarded - run the scan again when ready.";
                        return;
                    }

                    long totalWastedBytes = 0;
                    foreach (var g in matchedGroups) {
                        totalWastedBytes += g.Bytes * (g.FilePaths.Count - 1);
                    }
                    double wastedMB = totalWastedBytes / (1024.0 * 1024.0);
                    string wastedStr = wastedMB > 1024 ? string.Format("{0:F2} GB", wastedMB / 1024.0) : string.Format("{0:F1} MB", wastedMB);

                    txtDuplicatesSummary.Text = string.Format("Found {0} duplicate groups ({1} redundant storage can be recovered).", matchedGroups.Count, wastedStr);

                    if (matchedGroups.Count == 0) {
                        TextBlock empty = new TextBlock();
                        empty.Text = "Zero duplicate files found. Scanned directories are clean.";
                        empty.Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
                        empty.Margin = new Thickness(0, 20, 0, 0);
                        pnlDuplicates.Children.Add(empty);
                        return;
                    }

                    foreach (var group in matchedGroups) {
                        Border groupBorder = new Border();
                        groupBorder.Background = CardBgBrush();
                        groupBorder.BorderBrush = CardBorderBrush();
                        groupBorder.BorderThickness = new Thickness(1);
                        groupBorder.Padding = new Thickness(14);
                        groupBorder.Margin = new Thickness(0, 0, 0, 10);
                        groupBorder.CornerRadius = new CornerRadius(10);

                        StackPanel groupSp = new StackPanel();

                        Grid headerGrid = new Grid();
                        headerGrid.Margin = new Thickness(0, 0, 0, 8);
                        TextBlock grpTitle = new TextBlock();
                        grpTitle.Text = string.Format("Matched Size: {0:F1} MB • Hash: {1}...", group.SizeMB, group.Hash.Substring(0, Math.Min(12, group.Hash.Length)));
                        grpTitle.FontWeight = FontWeights.Bold;
                        grpTitle.FontSize = 12;
                        grpTitle.Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));
                        headerGrid.Children.Add(grpTitle);
                        groupSp.Children.Add(headerGrid);

                        for (int i = 0; i < group.FilePaths.Count; i++) {
                            string p = group.FilePaths[i];
                            Border itemCard = new Border();
                            itemCard.Background = new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x2B));
                            itemCard.Padding = new Thickness(10, 6, 10, 6);
                            itemCard.Margin = new Thickness(0, 0, 0, 4);
                            itemCard.CornerRadius = new CornerRadius(6);

                            Grid itemGrid = new Grid();
                            itemGrid.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1, GridUnitType.Star) });
                            itemGrid.ColumnDefinitions.Add(new ColumnDefinition() { Width = GridLength.Auto });

                            TextBlock itemPath = new TextBlock();
                            itemPath.Text = (i == 0 ? "🛡️ [ORIGINAL] " : "📑 [COPY] ") + p;
                            itemPath.FontSize = 11;
                            itemPath.Foreground = (i == 0) ? new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99)) : new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));
                            itemPath.TextTrimming = TextTrimming.CharacterEllipsis;
                            Grid.SetColumn(itemPath, 0);
                            itemGrid.Children.Add(itemPath);

                            Button btnDel = new Button();
                            btnDel.Style = (Style)mainWindow.TryFindResource("SecondaryBtn");
                            btnDel.Content = (i == 0 ? "Reveal" : "Delete");
                            btnDel.Background = (i == 0) ? new SolidColorBrush(Color.FromRgb(0x16, 0x20, 0x32)) : new SolidColorBrush(Color.FromRgb(0x3B, 0x18, 0x22));
                            btnDel.Foreground = (i == 0) ? new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1)) : new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
                            btnDel.BorderThickness = new Thickness(0);
                            btnDel.Padding = new Thickness(10, 2, 10, 2);
                            btnDel.FontSize = 10;
                            btnDel.Cursor = Cursors.Hand;

                            string capturedPath = p;
                            bool isOriginal = (i == 0);
                            btnDel.Click += (s, e) => {
                                if (isOriginal) {
                                    try { Process.Start("explorer.exe", string.Format("/select,\"{0}\"", capturedPath)); } catch { }
                                } else {
                                    if (MessageBox.Show(mainWindow, "Are you sure you want to delete this duplicate copy?\r\n" + capturedPath, "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes) {
                                        try {
                                            File.Delete(capturedPath);
                                            itemCard.Visibility = Visibility.Collapsed;
                                            Log("Deleted duplicate file: " + capturedPath);
                                        } catch (Exception ex) {
                                            Log("Delete error: " + ex.Message);
                                        }
                                    }
                                }
                            };

                            Grid.SetColumn(btnDel, 1);
                            itemGrid.Children.Add(btnDel);

                            itemCard.Child = itemGrid;
                            groupSp.Children.Add(itemCard);
                        }

                        groupBorder.Child = groupSp;
                        pnlDuplicates.Children.Add(groupBorder);
                    }
                }));
            }));
        }

        static void CleanAllDuplicatesAsync() {
            if (duplicateGroups.Count == 0) {
                MessageBox.Show(mainWindow, "No duplicates have been discovered yet. Run 'Find Duplicates' first.", "OmniClean", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            int count = 0;
            long bytes = 0;
            foreach (var g in duplicateGroups) {
                if (g.FilePaths.Count > 1) {
                    count += (g.FilePaths.Count - 1);
                    bytes += g.Bytes * (g.FilePaths.Count - 1);
                }
            }

            if (count == 0) {
                MessageBox.Show(mainWindow, "Zero duplicate copies available to remove.", "OmniClean", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            double mb = bytes / (1024.0 * 1024.0);
            string sizeStr = mb > 1024 ? string.Format("{0:F2} GB", mb / 1024.0) : string.Format("{0:F1} MB", mb);

            if (MessageBox.Show(mainWindow, string.Format("Safely purge {0} duplicate copies to reclaim {1}?\r\n\r\nOriginal files will be 100% preserved.", count, sizeStr), "Confirm Safe Duplicate Cleanup", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) {
                int deleted = 0;
                foreach (var g in duplicateGroups) {
                    for (int i = 1; i < g.FilePaths.Count; i++) {
                        try {
                            File.Delete(g.FilePaths[i]);
                            deleted++;
                        } catch { }
                    }
                }
                Log(string.Format("Safe Duplicate Cleanup: Removed {0} duplicate copies, reclaiming {1}.", deleted, sizeStr));
                ScanDuplicatesAsync();
            }
        }

        // ================= TAB 3: STARTUP APPS OPTIMIZER =================
        static void ScanStartupAppsAsync() {
            btnScanStartup.IsEnabled = false;
            pnlStartupApps.Children.Clear();
            txtStartupSummary.Text = "Scanning Windows Registry startup entries...";

            Task.Run(new Action(() => {
                var list = new List<StartupAppItem>();

                try {
                    using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) {
                        if (key != null) {
                            foreach (var valName in key.GetValueNames()) {
                                list.Add(new StartupAppItem {
                                    Name = valName,
                                    Command = key.GetValue(valName).ToString(),
                                    IsEnabled = true
                                });
                            }
                        }
                    }
                } catch { }

                mainWindow.Dispatcher.Invoke(new Action(() => {
                    startupApps = list;
                    btnScanStartup.IsEnabled = true;
                    txtStartupSummary.Text = string.Format("Found {0} active startup programs configured in user registry.", list.Count);

                    if (list.Count == 0) {
                        TextBlock empty = new TextBlock();
                        empty.Text = "No active startup applications found in user profile.";
                        empty.Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
                        empty.Margin = new Thickness(0, 20, 0, 0);
                        pnlStartupApps.Children.Add(empty);
                        return;
                    }

                    foreach (var app in list) {
                        Border card = new Border();
                        card.Background = CardBgBrush();
                        card.BorderBrush = CardBorderBrush();
                        card.BorderThickness = new Thickness(1);
                        card.Padding = new Thickness(14, 10, 14, 10);
                        card.Margin = new Thickness(0, 0, 0, 8);
                        card.CornerRadius = new CornerRadius(10);

                        card.MouseEnter += (s, e) => {
                            card.Background = CardHoverBrush();
                            card.BorderBrush = CardHoverBorderBrush();
                        };
                        card.MouseLeave += (s, e) => {
                            card.Background = CardBgBrush();
                            card.BorderBrush = CardBorderBrush();
                        };

                        Grid grid = new Grid();
                        grid.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1, GridUnitType.Star) });
                        grid.ColumnDefinitions.Add(new ColumnDefinition() { Width = GridLength.Auto });

                        StackPanel sp = new StackPanel();
                        TextBlock name = new TextBlock();
                        name.Text = app.Name;
                        name.FontWeight = FontWeights.Bold;
                        name.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFC));
                        name.FontSize = 13;

                        TextBlock cmd = new TextBlock();
                        cmd.Text = app.Command;
                        cmd.FontSize = 11;
                        cmd.Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
                        cmd.TextTrimming = TextTrimming.CharacterEllipsis;
                        cmd.MaxWidth = 550;

                        sp.Children.Add(name);
                        sp.Children.Add(cmd);
                        Grid.SetColumn(sp, 0);
                        grid.Children.Add(sp);

                        Button btnToggle = new Button();
                        btnToggle.Content = "● ACTIVE";
                        btnToggle.Background = new SolidColorBrush(Color.FromRgb(0x06, 0x4E, 0x3B));
                        btnToggle.Foreground = new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99));
                        btnToggle.BorderThickness = new Thickness(1);
                        btnToggle.BorderBrush = new SolidColorBrush(Color.FromRgb(0x05, 0x96, 0x69));
                        btnToggle.Padding = new Thickness(14, 5, 14, 5);
                        btnToggle.FontSize = 11;
                        btnToggle.FontWeight = FontWeights.Bold;
                        btnToggle.Cursor = Cursors.Hand;

                        StartupAppItem capturedApp = app;
                        btnToggle.Click += (s, e) => {
                            capturedApp.IsEnabled = !capturedApp.IsEnabled;
                            if (capturedApp.IsEnabled) {
                                btnToggle.Content = "● ACTIVE";
                                btnToggle.Background = new SolidColorBrush(Color.FromRgb(0x06, 0x4E, 0x3B));
                                btnToggle.Foreground = new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99));
                                btnToggle.BorderBrush = new SolidColorBrush(Color.FromRgb(0x05, 0x96, 0x69));
                                try {
                                    using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true)) {
                                        key.SetValue(capturedApp.Name, capturedApp.Command);
                                    }
                                    Log("Enabled startup app: " + capturedApp.Name);
                                } catch (Exception ex) { Log("Toggle error: " + ex.Message); }
                            } else {
                                btnToggle.Content = "○ DISABLED";
                                btnToggle.Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x37));
                                btnToggle.Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));
                                btnToggle.BorderBrush = new SolidColorBrush(Color.FromRgb(0x37, 0x41, 0x51));
                                try {
                                    using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true)) {
                                        key.DeleteValue(capturedApp.Name, false);
                                    }
                                    Log("Disabled startup app: " + capturedApp.Name);
                                } catch (Exception ex) { Log("Toggle error: " + ex.Message); }
                            }
                        };

                        Grid.SetColumn(btnToggle, 1);
                        grid.Children.Add(btnToggle);

                        card.Child = grid;
                        pnlStartupApps.Children.Add(card);
                    }
                }));
            }));
        }


    }
}

