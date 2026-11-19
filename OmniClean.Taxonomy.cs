// OmniClean.Taxonomy.cs
// Shared cleanup taxonomy for the native edition.
//
// The taxonomy itself lives in targets.json at the repository root - the
// same file the web edition reads. This file embeds a fallback copy so a
// standalone OmniClean.exe still works when it is run away from the source
// tree, and provides the path resolvers that turn a taxonomy entry into
// concrete folders on this machine.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Text;

namespace OmniClean {
    public class CleanupRoot {
        public string Base { get; set; }
        public string[] Patterns { get; set; }
    }

    public class CleanupTarget {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Desc { get; set; }
        public string Kind { get; set; }
        public string Group { get; set; }
        public string GroupId { get; set; }
        public string[] Paths { get; set; }
        // "contents" (default) or "icons_only".
        public string Cleanup { get; set; }
        // Pattern based discovery roots (kind "glob").
        public CleanupRoot[] Roots { get; set; }
        // Application folder names (kind "editor_cache").
        public string[] Apps { get; set; }
        // Versioned build roots (kind "old_app_versions").
        public string[] RootsRaw { get; set; }
        // The target usually needs Administrator rights.
        public bool RequiresAdmin { get; set; }
        // The target is not selected automatically.
        public bool OptIn { get; set; }
    }

    public class CleanupGroup {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Icon { get; set; }
        public List<CleanupTarget> Targets { get; set; }
    }

    partial class Program {
        // Minimal JSON reader for the taxonomy. A full JSON library would be
        // overkill for one well-known file, and this keeps the native build
        // dependency-free (plain csc.exe, no NuGet).
        static object ParseJsonValue(string json, ref int pos) {
            SkipWhitespace(json, ref pos);
            if (pos >= json.Length) throw new FormatException("Unexpected end of JSON.");

            char c = json[pos];
            if (c == '{') return ParseJsonObject(json, ref pos);
            if (c == '[') return ParseJsonArray(json, ref pos);
            if (c == '"') return ParseJsonString(json, ref pos);
            if (c == 't' && Match(json, pos, "true")) { pos += 4; return true; }
            if (c == 'f' && Match(json, pos, "false")) { pos += 5; return false; }
            if (c == 'n' && Match(json, pos, "null")) { pos += 4; return null; }
            return ParseJsonNumber(json, ref pos);
        }

        static bool Match(string json, int pos, string literal) {
            if (pos + literal.Length > json.Length) return false;
            return string.CompareOrdinal(json, pos, literal, 0, literal.Length) == 0;
        }

        static void SkipWhitespace(string json, ref int pos) {
            while (pos < json.Length && " \t\r\n".IndexOf(json[pos]) >= 0) pos++;
        }

        static Dictionary<string, object> ParseJsonObject(string json, ref int pos) {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            pos++; // '{'
            SkipWhitespace(json, ref pos);
            if (pos < json.Length && json[pos] == '}') { pos++; return result; }

            while (pos < json.Length) {
                SkipWhitespace(json, ref pos);
                string key = ParseJsonString(json, ref pos);
                SkipWhitespace(json, ref pos);
                if (pos >= json.Length || json[pos] != ':') throw new FormatException("Expected ':' in JSON object.");
                pos++;
                result[key] = ParseJsonValue(json, ref pos);
                SkipWhitespace(json, ref pos);
                if (pos >= json.Length) break;
                if (json[pos] == ',') { pos++; continue; }
                if (json[pos] == '}') { pos++; break; }
                throw new FormatException("Expected ',' or '}' in JSON object.");
            }
            return result;
        }

        static List<object> ParseJsonArray(string json, ref int pos) {
            var result = new List<object>();
            pos++; // '['
            SkipWhitespace(json, ref pos);
            if (pos < json.Length && json[pos] == ']') { pos++; return result; }

            while (pos < json.Length) {
                result.Add(ParseJsonValue(json, ref pos));
                SkipWhitespace(json, ref pos);
                if (pos >= json.Length) break;
                if (json[pos] == ',') { pos++; continue; }
                if (json[pos] == ']') { pos++; break; }
                throw new FormatException("Expected ',' or ']' in JSON array.");
            }
            return result;
        }

        static string ParseJsonString(string json, ref int pos) {
            if (pos >= json.Length || json[pos] != '"') throw new FormatException("Expected a JSON string.");
            pos++;
            var sb = new System.Text.StringBuilder();
            while (pos < json.Length) {
                char c = json[pos];
                if (c == '"') { pos++; return sb.ToString(); }
                if (c == '\\') {
                    pos++;
                    if (pos >= json.Length) break;
                    char e = json[pos];
                    switch (e) {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (pos + 4 < json.Length) {
                                sb.Append((char)Convert.ToInt32(json.Substring(pos + 1, 4), 16));
                                pos += 4;
                            }
                            break;
                        default: sb.Append(e); break;
                    }
                    pos++;
                    continue;
                }
                sb.Append(c);
                pos++;
            }
            throw new FormatException("Unterminated JSON string.");
        }

        static object ParseJsonNumber(string json, ref int pos) {
            int start = pos;
            while (pos < json.Length && "+-.eE0123456789".IndexOf(json[pos]) >= 0) pos++;
            string text = json.Substring(start, pos - start);
            double value;
            if (!double.TryParse(text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out value)) {
                throw new FormatException("Invalid JSON number: " + text);
            }
            return value;
        }

        static Dictionary<string, object> ParseJsonObject(string json) {
            int pos = 0;
            return ParseJsonObject(json, ref pos);
        }

        // ---- taxonomy loading ------------------------------------------------

        static List<CleanupGroup> _taxonomy;

        // The taxonomy is looked up next to the executable first (so a
        // released build picks up an updated targets.json), then read from
        // the embedded resource. The resource is the very same targets.json
        // file - it is embedded at compile time, so the two editions can
        // never drift apart.
        static List<CleanupGroup> LoadTaxonomy() {
            if (_taxonomy != null) return _taxonomy;

            string[] candidates = {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "targets.json"),
                Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "targets.json"))
            };

            foreach (string candidate in candidates) {
                try {
                    if (!File.Exists(candidate)) continue;
                    string json = File.ReadAllText(candidate, Encoding.UTF8);
                    _taxonomy = BuildGroups(ParseJsonObject(json));
                    if (_taxonomy.Count > 0) return _taxonomy;
                } catch { }
            }

            _taxonomy = LoadEmbeddedTaxonomy();
            return _taxonomy;
        }

        // Reads targets.json from the embedded manifest resource that the
        // build embeds (/resource:targets.json,OmniClean.targets.json).
        static List<CleanupGroup> LoadEmbeddedTaxonomy() {
            try {
                Assembly assembly = Assembly.GetExecutingAssembly();
                using (Stream stream = assembly.GetManifestResourceStream("OmniClean.targets.json")) {
                    if (stream == null) return new List<CleanupGroup>();
                    using (var reader = new StreamReader(stream, Encoding.UTF8)) {
                        return BuildGroups(ParseJsonObject(reader.ReadToEnd()));
                    }
                }
            } catch {
                return new List<CleanupGroup>();
            }
        }

        static List<CleanupTarget> ReadTargets(object raw) {
            var list = new List<CleanupTarget>();
            var array = raw as List<object>;
            if (array == null) return list;

            foreach (object entry in array) {
                try {
                    var obj = entry as Dictionary<string, object>;
                    if (obj == null) continue;

                    var target = new CleanupTarget {
                        Id = GetString(obj, "id"),
                        Name = GetString(obj, "name"),
                        Desc = GetString(obj, "desc"),
                        Kind = GetString(obj, "kind"),
                        Cleanup = GetString(obj, "cleanup")
                    };

                    target.Paths = GetStringArray(obj, "paths");
                    target.Apps = GetStringArray(obj, "apps");
                    target.RequiresAdmin = GetBool(obj, "requires_admin");
                    target.OptIn = GetBool(obj, "opt_in");

                    // "roots" means two different things depending on the kind:
                    // a list of {base, patterns} objects for "glob" targets and
                    // a list of plain strings for "old_app_versions" targets.
                    // Optional: most targets declare neither "paths" nor "roots".
                    List<CleanupRoot> roots = new List<CleanupRoot>();
                    object rawRoots;
                    if (obj.TryGetValue("roots", out rawRoots)) {
                        var rootArray = rawRoots as List<object>;
                        if (rootArray != null) {
                            foreach (object rootEntry in rootArray) {
                                var rootObj = rootEntry as Dictionary<string, object>;
                                if (rootObj == null) {
                                    // A plain string root (old_app_versions).
                                    if (rootEntry != null) {
                                        var rawRootsList = new List<string>();
                                        rawRootsList.Add(rootEntry.ToString());
                                        if (target.RootsRaw == null) target.RootsRaw = rawRootsList.ToArray();
                                        else target.RootsRaw = target.RootsRaw.Concat(rawRootsList.ToArray()).ToArray();
                                    }
                                    continue;
                                }
                                roots.Add(new CleanupRoot {
                                    Base = GetString(rootObj, "base"),
                                    Patterns = GetStringArray(rootObj, "patterns")
                                });
                            }
                        }
                    }
                    target.Roots = roots.ToArray();
                    list.Add(target);
                } catch {
                    // One malformed entry must never take down the whole
                    // taxonomy - skip it and keep loading the rest.
                }
            }
            return list;
        }

        static List<CleanupGroup> BuildGroups(Dictionary<string, object> root) {
            var groups = new List<CleanupGroup>();
            if (root == null) return groups;

            object rawGroups;
            if (!root.TryGetValue("groups", out rawGroups)) return groups;
            var groupArray = rawGroups as List<object>;
            if (groupArray == null) return groups;

            foreach (object entry in groupArray) {
                var obj = entry as Dictionary<string, object>;
                if (obj == null) continue;

                var group = new CleanupGroup {
                    Id = GetString(obj, "id"),
                    Name = GetString(obj, "name"),
                    Icon = GetString(obj, "icon"),
                    Targets = new List<CleanupTarget>()
                };

                object rawTargets;
                if (obj.TryGetValue("targets", out rawTargets)) {
                    foreach (CleanupTarget target in ReadTargets(rawTargets)) {
                        target.Group = group.Name;
                        target.GroupId = group.Id;
                        group.Targets.Add(target);
                    }
                }
                groups.Add(group);
            }
            return groups;
        }

        static string GetString(Dictionary<string, object> obj, string key) {
            object value;
            if (obj.TryGetValue(key, out value) && value != null) return value.ToString();
            return "";
        }

        static string[] GetStringArray(Dictionary<string, object> obj, string key) {
            // Optional key: targets declare "paths", "apps" or "roots"
            // depending on their kind, so a missing key is normal.
            object raw;
            if (!obj.TryGetValue(key, out raw)) return new string[0];
            var array = raw as List<object>;
            if (array == null) return new string[0];
            var values = new List<string>();
            foreach (object entry in array) {
                if (entry != null) values.Add(entry.ToString());
            }
            return values.ToArray();
        }

        static bool GetBool(Dictionary<string, object> obj, string key) {
            object value;
            if (!obj.TryGetValue(key, out value) || value == null) return false;
            if (value is bool) return (bool)value;
            return string.Equals(value.ToString(), "true", StringComparison.OrdinalIgnoreCase);
        }
    }
}
