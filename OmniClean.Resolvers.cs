// OmniClean.Resolvers.cs
// Path resolution for the shared cleanup taxonomy.
//
// Every cleanup target in targets.json declares a "kind". This file turns a
// target into the concrete folders on THIS machine, so the native edition
// and the web edition (targets.ps1) resolve the same categories the same
// way. A new category only needs a taxonomy entry - no C# changes.
//
// Resolution runs in phases and every resolved path is claimed by exactly
// one target, so no folder is ever emptied or counted twice:
//
//   1. paths          - explicit folder / file list
//   2. editor_cache / old_app_versions - specific app families
//   3. browser_cache  - every installed browser, discovered
//   4. gpu_cache      - every graphics cache of every vendor, discovered
//   5. glob           - broad pattern based discovery
//
// The order matters: the most precise declaration claims its folder first
// and the broad discovery passes then skip it.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace OmniClean {
    partial class Program {
        // Paths that must never be emptied. A taxonomy entry may point at
        // one of these, but the resolver filters it out so neither edition
        // can ever delete a system-critical or non-cache folder.
        static readonly string[] ProtectedPaths = {
            @"C:\Windows\WinSxS",
            @"C:\Windows\Installer",
            @"C:\Windows\servicing",
            @"C:\Windows\System32\DriverStore",
            @"C:\Windows\System32\config",
            @"C:\Windows\System32\spool\drivers",
            @"C:\ProgramData\Package Cache",
            @"C:\Program Files",
            @"C:\Program Files (x86)"
        };

        // Folder names that hold graphics caches for ANY vendor. The list is
        // based on the cache names the drivers use, not on the vendor, so a
        // GPU OmniClean has never heard of is still detected.
        static readonly string[] GpuCacheNames = {
            "DXCache", "DxcCache", "GLCache", "VkCache", "VulkanCache",
            "D3DSCache", "ShaderCache", "GrShaderCache", "ComputeCache",
            "PipelineCache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache",
            "shadercache", "NV_Cache", "PPCache", "DisplayCache"
        };

        // Vendor folder names - used to look one level deeper for download
        // and installer payloads that are not named like a shader cache.
        static readonly string[] VendorTokens = {
            "nvidia", "amd", "intel", "qualcomm", "via", "mesa", "broadcom",
            "imagination", "arm", "d3d", "gpu", "graphics", "radeon", "geforce", "arc"
        };

        // Cache folders inside a Chromium / Electron profile.
        static readonly string[] ChromiumSubs = {
            "Cache", "Code Cache", "GPUCache", "DawnCache", "DawnGraphiteCache",
            "DawnWebGPUCache", "GrShaderCache", "ShaderCache"
        };

        // The resolution cache. It is filled once per drive and then reused,
        // so the scan and the cleanup always see the same folders.
        static Dictionary<string, List<string>> _resolvedPaths =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        static HashSet<string> _claimedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static bool _resolutionReady = false;

        // ---- safety helpers -------------------------------------------------

        static bool IsProtectedPath(string path) {
            if (string.IsNullOrEmpty(path)) return true;
            string full;
            try { full = Path.GetFullPath(path); } catch { return true; }

            string localApp = SafeEnv("LOCALAPPDATA");
            if (!string.IsNullOrEmpty(localApp)) {
                string[] localGuards = {
                    Path.Combine(localApp, "Package Cache"),
                    Path.Combine(localApp, @"Docker\wsl"),
                    Path.Combine(localApp, @"Docker\vfs")
                };
                foreach (string guard in localGuards) {
                    if (IsSameOrUnder(full, guard)) return true;
                }
            }

            foreach (string guard in ProtectedPaths) {
                if (IsSameOrUnder(full, guard)) return true;
            }
            return false;
        }

        static bool IsSameOrUnder(string path, string guard) {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(guard)) return false;
            string g = guard.TrimEnd('\\');
            if (path.Equals(g, StringComparison.OrdinalIgnoreCase)) return true;
            return path.StartsWith(g + "\\", StringComparison.OrdinalIgnoreCase);
        }

        static string SafeEnv(string name) {
            try {
                string value = Environment.GetEnvironmentVariable(name);
                return string.IsNullOrEmpty(value) ? string.Empty : value;
            } catch { return string.Empty; }
        }

        // Existence check that never throws: a folder the user cannot
        // enumerate must not abort the whole scan.
        static bool PathExistsSafe(string path) {
            if (string.IsNullOrEmpty(path)) return false;
            try { return File.Exists(path) || Directory.Exists(path); } catch { return false; }
        }

        static string NormalizePath(string path) {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            string full;
            try { full = Path.GetFullPath(path); } catch { return string.Empty; }
            return full.TrimEnd('\\').ToLowerInvariant();
        }

        // True when the path overlaps something that was already resolved:
        // it is the same path, it lives inside an owned path, or it contains
        // one.
        static bool OverlapsClaimed(string path) {
            string norm = NormalizePath(path);
            if (norm.Length == 0) return true;
            foreach (string claimed in _claimedPaths) {
                if (claimed == norm) return true;
                if (norm.StartsWith(claimed + "\\", StringComparison.Ordinal)) return true;
                if (claimed.StartsWith(norm + "\\", StringComparison.Ordinal)) return true;
            }
            return false;
        }

        // ---- taxonomy lookup ------------------------------------------------

        static CleanupTarget FindTaxonomyTarget(string id) {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (CleanupGroup group in LoadTaxonomy()) {
                foreach (CleanupTarget target in group.Targets) {
                    if (string.Equals(target.Id, id, StringComparison.OrdinalIgnoreCase)) return target;
                }
            }
            return null;
        }

        // Expands %VAR% tokens against the current environment. Unknown
        // tokens collapse to an empty string so a bad path is skipped
        // instead of pointing at a literal "%FOO%" directory.
        static string ExpandEnvironmentTokens(string path) {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            string result = Regex.Replace(path, @"%([A-Za-z_][A-Za-z0-9_]*)%", delegate(Match m) {
                string value = SafeEnv(m.Groups[1].Value);
                return string.IsNullOrEmpty(value) ? string.Empty : value;
            });
            return result.IndexOf('%') >= 0 ? string.Empty : result;
        }

        // The cleanup mode of a target: "contents" (default) or "icons_only".
        static string GetCleanupMode(CleanupTarget target) {
            if (target == null) return "contents";
            string mode = target.Cleanup;
            return string.IsNullOrEmpty(mode) ? "contents" : mode;
        }

        // ---- pattern resolution ---------------------------------------------

        static List<string> GetChildDirectories(string dir) {
            var result = new List<string>();
            try {
                foreach (string child in Directory.GetDirectories(dir)) result.Add(child);
            } catch { }
            return result;
        }

        // Resolves a pattern against a base folder.
        //
        // Segments:
        //   *  - matches any text inside one path segment
        //   ** - matches any number of segments
        static List<string> ResolvePattern(string baseDir, string[] segments, bool directoriesOnly) {
            var results = new List<string>();
            if (string.IsNullOrEmpty(baseDir) || segments == null || segments.Length == 0) return results;
            if (!Directory.Exists(baseDir)) return results;

            var stack = new Stack<PatternNode>();
            stack.Push(new PatternNode { Dir = baseDir, Index = 0 });

            while (stack.Count > 0) {
                PatternNode node = stack.Pop();
                string dir = node.Dir;
                int index = node.Index;
                string segment = segments[index];

                if (segment == "**") {
                    // "**" matches zero segments ...
                    if (index + 1 < segments.Length) {
                        stack.Push(new PatternNode { Dir = dir, Index = index + 1 });
                    }
                    // ... or one or more.
                    foreach (string child in GetChildDirectories(dir)) {
                        if (IsReparsePoint(new DirectoryInfo(child))) continue;
                        stack.Push(new PatternNode { Dir = child, Index = index });
                    }
                    continue;
                }

                bool isLast = (index == segments.Length - 1);
                List<string> entries;
                if (isLast) {
                    entries = new List<string>();
                    try { entries.AddRange(Directory.GetFileSystemEntries(dir)); } catch { }
                } else {
                    entries = GetChildDirectories(dir);
                }

                foreach (string entry in entries) {
                    string name = Path.GetFileName(entry);
                    if (!WildcardMatch(name, segment)) continue;
                    if (IsReparsePoint(new DirectoryInfo(entry))) continue;
                    if (isLast) {
                        if (directoriesOnly && !Directory.Exists(entry)) continue;
                        if (!results.Contains(entry)) results.Add(entry);
                    } else {
                        stack.Push(new PatternNode { Dir = entry, Index = index + 1 });
                    }
                }
            }
            return results;
        }

        class PatternNode {
            public string Dir;
            public int Index;
        }

        // Matches a path segment against a pattern that may contain '*' and
        // '?'. Implemented by hand so the behaviour is identical on every
        // runtime and does not depend on the current directory.
        static bool WildcardMatch(string name, string pattern) {
            if (pattern == "*") return true;
            if (pattern.IndexOf('*') < 0 && pattern.IndexOf('?') < 0) {
                return string.Equals(name, pattern, StringComparison.OrdinalIgnoreCase);
            }

            int n = 0, m = 0, star = -1, match = 0;
            while (n < name.Length) {
                if (m < pattern.Length &&
                    (pattern[m] == '?' || char.ToLowerInvariant(pattern[m]) == char.ToLowerInvariant(name[n]))) {
                    n++; m++;
                } else if (m < pattern.Length && pattern[m] == '*') {
                    star = m++; match = n;
                } else if (star != -1) {
                    m = star + 1; n = ++match;
                } else {
                    return false;
                }
            }
            while (m < pattern.Length && pattern[m] == '*') m++;
            return m == pattern.Length;
        }

        // Resolves an absolute pattern that may contain wildcards. The
        // leading segments without a wildcard form the base folder.
        static List<string> ResolveAbsolutePattern(string pattern) {
            var results = new List<string>();
            if (string.IsNullOrEmpty(pattern)) return results;

            string[] segments = pattern.Split(new char[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) return results;

            int index = 0;
            var fixedParts = new List<string>();
            while (index < segments.Length && segments[index].IndexOfAny(new char[] { '*', '?' }) < 0) {
                fixedParts.Add(segments[index]);
                index++;
            }

            string baseDir = string.Join("\\", fixedParts.ToArray());
            if (fixedParts.Count == 1 && Regex.IsMatch(fixedParts[0], "^[A-Za-z]:$")) baseDir = fixedParts[0] + "\\";

            if (index >= segments.Length) {
                if (PathExistsSafe(baseDir)) results.Add(baseDir);
                return results;
            }

            string[] rest = new string[segments.Length - index];
            Array.Copy(segments, index, rest, 0, rest.Length);
            return ResolvePattern(baseDir, rest, false);
        }

        // Finds every folder with one of the given names, up to maxDepth
        // levels below the root.
        static List<string> GetDirsByName(string root, string[] names, int maxDepth) {
            var found = new List<string>();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return found;

            var nameSet = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            var stack = new Stack<DepthNode>();
            stack.Push(new DepthNode { Dir = root, Depth = 0 });

            while (stack.Count > 0) {
                DepthNode node = stack.Pop();
                foreach (string child in GetChildDirectories(node.Dir)) {
                    if (nameSet.Contains(Path.GetFileName(child)) && !found.Contains(child)) found.Add(child);
                    if (node.Depth + 1 < maxDepth) {
                        if (IsReparsePoint(new DirectoryInfo(child))) continue;
                        stack.Push(new DepthNode { Dir = child, Depth = node.Depth + 1 });
                    }
                }
            }
            return found;
        }

        class DepthNode {
            public string Dir;
            public int Depth;
        }

        // ---- target kind resolvers -------------------------------------------

        // Explicit folder / file list.
        static List<string> ResolveStaticPaths(CleanupTarget target) {
            var resolved = new List<string>();
            if (target == null || target.Paths == null) return resolved;

            foreach (string raw in target.Paths) {
                string expanded = ExpandEnvironmentTokens(raw);
                if (expanded.Length == 0) continue;
                if (IsProtectedPath(expanded)) continue;
                foreach (string p in ResolveAbsolutePattern(expanded)) {
                    if (!resolved.Contains(p)) resolved.Add(p);
                }
            }
            return resolved;
        }

        // Pattern based discovery.
        static List<string> ResolveGlobPaths(CleanupTarget target) {
            var resolved = new List<string>();
            if (target == null || target.Roots == null) return resolved;

            foreach (CleanupRoot root in target.Roots) {
                string baseDir = ExpandEnvironmentTokens(root.Base);
                if (baseDir.Length == 0) continue;
                if (IsProtectedPath(baseDir)) continue;
                if (root.Patterns == null) continue;

                foreach (string pattern in root.Patterns) {
                    string[] segments = pattern.Split(new char[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
                    if (segments.Length == 0) continue;
                    foreach (string p in ResolvePattern(baseDir, segments, true)) {
                        if (!resolved.Contains(p)) resolved.Add(p);
                    }
                }
            }
            return resolved;
        }

        // Browser cache folders: every Chromium / Gecko / Opera / Vivaldi /
        // Arc / WebView2 browser that is actually installed.
        static List<string> GetBrowserCachePaths() {
            var paths = new List<string>();
            string localApp = SafeEnv("LOCALAPPDATA");
            string appData = SafeEnv("APPDATA");

            // Chromium family: every folder literally named "User Data"
            // holds the profiles of one browser. The name is what the
            // browsers agree on, so this finds browsers OmniClean has never
            // heard of.
            var roots = new List<string>();
            foreach (string baseDir in new string[] { localApp, appData }) {
                if (string.IsNullOrEmpty(baseDir)) continue;
                roots.AddRange(GetDirsByName(baseDir, new string[] { "User Data" }, 3));
            }

            // Opera and Opera GX keep their caches one level deeper.
            if (!string.IsNullOrEmpty(localApp)) {
                string operaRoot = Path.Combine(localApp, "Opera Software");
                if (Directory.Exists(operaRoot)) roots.AddRange(GetChildDirectories(operaRoot));
            }

            var seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in roots) {
                if (!seenRoots.Add(root)) continue;

                var profiles = new List<string>();
                try {
                    foreach (string dir in Directory.GetDirectories(root)) {
                        string name = Path.GetFileName(dir);
                        if (name == "Default" || name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase)
                            || name.StartsWith("Guest", StringComparison.OrdinalIgnoreCase)
                            || name.StartsWith("System Profile", StringComparison.OrdinalIgnoreCase)) {
                            profiles.Add(dir);
                        }
                    }
                } catch { }

                if (profiles.Count == 0) {
                    // Opera style: the cache folders sit directly in the root.
                    foreach (string sub in ChromiumSubs) {
                        string p = Path.Combine(root, sub);
                        if (PathExistsSafe(p)) paths.Add(p);
                    }
                    continue;
                }

                foreach (string profile in profiles) {
                    foreach (string sub in ChromiumSubs) {
                        string p = Path.Combine(profile, sub);
                        if (PathExistsSafe(p)) paths.Add(p);
                    }
                }
            }

            // Firefox / Gecko keeps its caches inside each profile folder.
            foreach (string baseDir in new string[] { localApp, appData }) {
                if (string.IsNullOrEmpty(baseDir)) continue;
                foreach (string profilesRoot in GetDirsByName(baseDir, new string[] { "Profiles" }, 4)) {
                    if (!profilesRoot.EndsWith(@"\Mozilla\Firefox\Profiles", StringComparison.OrdinalIgnoreCase)
                        && !profilesRoot.EndsWith(@"\SeaMonkey\Profiles", StringComparison.OrdinalIgnoreCase)) continue;
                    try {
                        foreach (string profile in Directory.GetDirectories(profilesRoot)) {
                            foreach (string sub in new string[] { "cache2", "startupCache", "jumpListCache", "OfflineCache" }) {
                                string p = Path.Combine(profile, sub);
                                if (PathExistsSafe(p)) paths.Add(p);
                            }
                        }
                    } catch { }
                }
            }

            return paths;
        }

        // Graphics caches of every vendor: NVIDIA, AMD, Intel, Qualcomm,
        // VIA, Mesa and any vendor that is not on this list.
        static List<string> GetGpuCachePaths() {
            var paths = new List<string>();
            string localApp = SafeEnv("LOCALAPPDATA");
            string programData = SafeEnv("ProgramData");

            foreach (string baseDir in new string[] { localApp, programData }) {
                if (string.IsNullOrEmpty(baseDir)) continue;
                paths.AddRange(GetDirsByName(baseDir, GpuCacheNames, 3));
            }

            // Vendor folders may hold caches under a name that is not in the
            // list (for example "ota-artifacts"), so the vendor roots are
            // inspected one level deeper as well.
            foreach (string baseDir in new string[] { localApp, programData }) {
                if (string.IsNullOrEmpty(baseDir)) continue;
                foreach (string vendor in GetChildDirectories(baseDir)) {
                    string name = Path.GetFileName(vendor).ToLowerInvariant();
                    bool isVendor = false;
                    foreach (string token in VendorTokens) {
                        if (name.Contains(token)) { isVendor = true; break; }
                    }
                    if (!isVendor) continue;

                    foreach (string child in GetChildDirectories(vendor)) {
                        string childName = Path.GetFileName(child);
                        bool known = Array.IndexOf(GpuCacheNames, childName) >= 0;
                        bool payload = childName == "Downloader" || childName == "ota-artifacts"
                            || childName == "Download" || childName == "PPCache" || childName == "CN"
                            || childName == "DisplayCache" || childName == "Caches";
                        if (known || payload) paths.Add(child);
                    }
                }
            }

            return paths;
        }

        // Caches of the installed code editors.
        static List<string> GetEditorCachePaths(CleanupTarget target) {
            var paths = new List<string>();
            if (target == null || target.Apps == null) return paths;

            string[] subs = {
                "Cache", "Code Cache", "GPUCache", "CachedData", "CachedExtensions",
                "CachedExtensionVSIXs", "logs", "Crashpad", "DawnGraphiteCache",
                "DawnWebGPUCache", @"Service Worker\CacheStorage"
            };

            foreach (string app in target.Apps) {
                foreach (string baseDir in new string[] { SafeEnv("APPDATA"), SafeEnv("LOCALAPPDATA") }) {
                    if (string.IsNullOrEmpty(baseDir)) continue;
                    string root = Path.Combine(baseDir, app);
                    if (!Directory.Exists(root)) continue;
                    foreach (string sub in subs) {
                        string p = Path.Combine(root, sub);
                        if (PathExistsSafe(p)) paths.Add(p);
                    }
                }
            }
            return paths;
        }

        // Outdated application builds. The newest build of every app is
        // always kept.
        static List<string> GetOldAppVersionPaths(CleanupTarget target) {
            var paths = new List<string>();
            if (target == null || target.RootsRaw == null) return paths;

            foreach (string raw in target.RootsRaw) {
                string root = ExpandEnvironmentTokens(raw);
                if (root.Length == 0 || !Directory.Exists(root)) continue;

                var versions = new List<KeyValuePair<string, Version>>();
                try {
                    foreach (string dir in Directory.GetDirectories(root)) {
                        string name = Path.GetFileName(dir);
                        if (!Regex.IsMatch(name, @"^\d+(\.\d+)+")) continue;
                        if (IsReparsePoint(new DirectoryInfo(dir))) continue;
                        versions.Add(new KeyValuePair<string, Version>(dir, ParseAppVersion(name)));
                    }
                } catch { continue; }

                if (versions.Count <= 1) continue;

                // Highest version wins. Ties prefer the folder named exactly
                // after the version ("2.9.1" over "2.9.1-beta") so the
                // release build is the one that survives.
                versions.Sort(delegate(KeyValuePair<string, Version> a, KeyValuePair<string, Version> b) {
                    int cmp = a.Value.CompareTo(b.Value);
                    if (cmp != 0) return cmp;
                    bool aExact = string.Equals(a.Key, a.Value.ToString(), StringComparison.OrdinalIgnoreCase);
                    bool bExact = string.Equals(b.Key, b.Value.ToString(), StringComparison.OrdinalIgnoreCase);
                    if (aExact != bExact) return aExact ? 1 : -1;
                    return string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase);
                });

                string latest = versions[versions.Count - 1].Key;
                foreach (var pair in versions) {
                    if (!string.Equals(pair.Key, latest, StringComparison.OrdinalIgnoreCase)) paths.Add(pair.Key);
                }
            }
            return paths;
        }

        // ---- resolution ------------------------------------------------------

        // Keeps the candidates that may be touched and claims them, so no
        // other target can take the same folder.
        static List<string> AcceptCandidates(List<string> candidates) {
            var accepted = new List<string>();
            if (candidates == null) return accepted;

            foreach (string candidate in candidates) {
                if (string.IsNullOrEmpty(candidate)) continue;
                if (!PathExistsSafe(candidate)) continue;
                if (IsProtectedPath(candidate)) continue;
                if (OverlapsClaimed(candidate)) continue;
                string norm = NormalizePath(candidate);
                if (norm.Length > 0) _claimedPaths.Add(norm);
                accepted.Add(candidate);
            }
            return accepted;
        }

        // Resolves every target of the taxonomy in phase order.
        static void EnsureResolution() {
            if (_resolutionReady) return;
            _resolutionReady = true;

            List<CleanupGroup> groups = LoadTaxonomy();
            var targets = new List<CleanupTarget>();
            foreach (CleanupGroup group in groups) {
                targets.AddRange(group.Targets);
            }

            // Phase 1: explicit paths. The most precise declarations get
            // first claim on the folders they name.
            foreach (CleanupTarget target in targets) {
                if (target.Kind != "paths") continue;
                _resolvedPaths[target.Id] = AcceptCandidates(ResolveStaticPaths(target));
            }

            // Phase 2: specific app families.
            foreach (CleanupTarget target in targets) {
                if (target.Kind == "editor_cache") {
                    _resolvedPaths[target.Id] = AcceptCandidates(GetEditorCachePaths(target));
                } else if (target.Kind == "old_app_versions") {
                    _resolvedPaths[target.Id] = AcceptCandidates(GetOldAppVersionPaths(target));
                }
            }

            // Phase 3: browsers.
            foreach (CleanupTarget target in targets) {
                if (target.Kind != "browser_cache") continue;
                _resolvedPaths[target.Id] = AcceptCandidates(GetBrowserCachePaths());
            }

            // Phase 4: graphics caches of every vendor.
            foreach (CleanupTarget target in targets) {
                if (target.Kind != "gpu_cache") continue;
                _resolvedPaths[target.Id] = AcceptCandidates(GetGpuCachePaths());
            }

            // Phase 5: broad pattern based discovery.
            foreach (CleanupTarget target in targets) {
                if (target.Kind != "glob") continue;
                _resolvedPaths[target.Id] = AcceptCandidates(ResolveGlobPaths(target));
            }

            // Targets without a filesystem footprint.
            foreach (CleanupTarget target in targets) {
                if (_resolvedPaths.ContainsKey(target.Id)) continue;
                if (target.Kind == "recycle") {
                    _resolvedPaths[target.Id] = new List<string> { selectedDriveLetter + @":\$Recycle.Bin" };
                } else if (target.Kind == "empty_dirs") {
                    _resolvedPaths[target.Id] = GetTempRoots();
                } else {
                    _resolvedPaths[target.Id] = new List<string>();
                }
            }
        }

        // Roots that are scanned for empty directories (temp areas only).
        static List<string> GetTempRoots() {
            var roots = new List<string>();
            string temp = SafeEnv("TEMP");
            string winDir = SafeEnv("WINDIR");
            string localApp = SafeEnv("LOCALAPPDATA");

            if (!string.IsNullOrEmpty(temp) && Directory.Exists(temp)) roots.Add(temp);
            if (!string.IsNullOrEmpty(winDir)) {
                string winTemp = Path.Combine(winDir, "Temp");
                if (Directory.Exists(winTemp)) roots.Add(winTemp);
            }
            if (!string.IsNullOrEmpty(localApp)) {
                string localTemp = Path.Combine(localApp, "Temp");
                if (Directory.Exists(localTemp)) roots.Add(localTemp);
            }
            return roots;
        }

        // Resolves the folders a target would touch on this machine.
        static List<string> ResolveTargetPaths(CleanupTarget target) {
            var resolved = new List<string>();
            if (target == null) return resolved;

            EnsureResolution();

            List<string> paths;
            if (_resolvedPaths.TryGetValue(target.Id, out paths)) return paths;
            return resolved;
        }

        // ---- cleanup modes ---------------------------------------------------

        // Thumbnail and icon cache database files. Only these are removed for
        // a target with the "icons_only" cleanup mode - the surrounding
        // Explorer folder holds much more than cache data.
        static bool IsIconCacheFile(string name) {
            if (string.IsNullOrEmpty(name)) return false;
            return Regex.IsMatch(name, @"^(thumbcache|iconcache).*\.db(-wal|-shm|-journal)?$",
                RegexOptions.IgnoreCase);
        }

        static List<string> GetIconCacheFiles(string folder) {
            var files = new List<string>();
            if (string.IsNullOrEmpty(folder) || !PathExistsSafe(folder)) return files;

            if (File.Exists(folder)) {
                if (IsIconCacheFile(Path.GetFileName(folder))) files.Add(folder);
                return files;
            }
            try {
                foreach (string file in Directory.GetFiles(folder)) {
                    if (IsIconCacheFile(Path.GetFileName(file))) files.Add(file);
                }
            } catch { }
            return files;
        }
    }
}
