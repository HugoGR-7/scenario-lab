using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace ScenarioLab.Client
{
    /// <summary>What the user chose for one snapshot: the content of &lt;camera&gt;_scenarios_user.json.</summary>
    public class ScenarioSelection
    {
        public string Camera;            // XProtect name of the camera the snapshot came from
        public string Snapshot;          // full path of the snapshot PNG
        public DateTime SnapshotUtc;     // the snapshot frame's own time (the "nothing happens" control starts here)
        public string SuggestionsFile;   // the file the suggestions came from
        public DateTime SavedUtc;
        public List<Scenario> Scenarios = new List<Scenario>();
    }

    /// <summary>
    /// Reading the prepared suggestions and writing / reading the user's selection. Plain JSON files in
    /// C:\ScenarioLab\suggestions, so the hand-off to and from the video generator (ComfyUI) is a file copy.
    ///
    /// Suggestions file, as the scene-analysis step writes it (one object per suggestion):
    ///   [{"title": "...", "category": "...", "where": "..."}, ...]
    /// IDs: suggestion n (1-based, file order) gets &lt;prefix&gt;_s&lt;n&gt;; the user's own scenarios get
    /// &lt;prefix&gt;_u1, _u2, ... The generated clip's XProtect camera is named after the ID (see FindCameraFor).
    /// </summary>
    public static class ScenarioFiles
    {
        public static readonly string SuggestionsFolder = Path.Combine(ScenarioLabDefinition.DataFolder, "suggestions");
        private const string SuggestionsSuffix = "_scenarios";
        private const string SelectionSuffix = "_scenarios_user";
        private const string HarmlessCategory = "harmless";   // "harmless look-alike": the clip should NOT raise the alarm

        /// <summary>Where the suggestions for a camera are expected: suggestions\vandalism_scenarios.json.</summary>
        public static string SuggestionsPathFor(string cameraName)
        {
            return Path.Combine(SuggestionsFolder, Slug(cameraName) + SuggestionsSuffix + ".json");
        }

        /// <summary>"vandalism_scenarios.json" -> "vandalism". Used as the start of every scenario ID.</summary>
        public static string PrefixFromFile(string path)
        {
            string stem = Path.GetFileNameWithoutExtension(path);
            if (stem.EndsWith(SuggestionsSuffix, StringComparison.OrdinalIgnoreCase))
            {
                stem = stem.Substring(0, stem.Length - SuggestionsSuffix.Length);
            }
            return Slug(stem);
        }

        public static List<Scenario> LoadSuggestions(string path)
        {
            string prefix = PrefixFromFile(path);
            object parsed = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path, Encoding.UTF8));
            var array = parsed as object[];
            if (array == null)
            {
                throw new InvalidDataException("Expected a JSON list of suggestions: [{\"title\": ..., \"category\": ..., \"where\": ...}, ...]");
            }

            var list = new List<Scenario>();
            for (int i = 0; i < array.Length; i++)
            {
                var entry = array[i] as Dictionary<string, object>;
                if (entry == null)
                {
                    continue;
                }
                string category = Text(entry, "category");
                list.Add(new Scenario
                {
                    Id = prefix + "_s" + (i + 1),
                    Title = Text(entry, "title"),
                    Category = category,
                    Where = Text(entry, "where"),
                    Source = Scenario.FromSuggestion,
                    Expected = ExpectedFor(category)
                });
            }
            return list;
        }

        /// <summary>Harmless look-alikes should not trigger the alarm; everything else should.</summary>
        public static string ExpectedFor(string category)
        {
            return category != null && category.IndexOf(HarmlessCategory, StringComparison.OrdinalIgnoreCase) >= 0
                ? Scenario.NoDetect
                : Scenario.Detect;
        }

        /// <summary>suggestions\vandalism_scenarios.json -> suggestions\vandalism_scenarios_user.json</summary>
        public static string SelectionPathFor(string suggestionsPath)
        {
            return Path.Combine(SuggestionsFolder, PrefixFromFile(suggestionsPath) + SelectionSuffix + ".json");
        }

        /// <summary>The most recently saved selection in the suggestions folder, or null.</summary>
        public static string LatestSelectionPath()
        {
            if (!Directory.Exists(SuggestionsFolder))
            {
                return null;
            }
            var newest = new DirectoryInfo(SuggestionsFolder).GetFiles("*" + SelectionSuffix + ".json")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            return newest != null ? newest.FullName : null;
        }

        /// <summary>
        /// Writes the selection as readable JSON (one scenario per line), so it looks clean when shown in the demo
        /// and is easy to read from Python on the generation side.
        /// </summary>
        public static void SaveSelection(string path, ScenarioSelection selection)
        {
            var json = new JavaScriptSerializer();
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"camera\": " + json.Serialize(selection.Camera) + ",");
            sb.AppendLine("  \"snapshot\": " + json.Serialize(selection.Snapshot) + ",");
            sb.AppendLine("  \"snapshot_utc\": " + json.Serialize(selection.SnapshotUtc.ToString("o")) + ",");
            sb.AppendLine("  \"suggestions_file\": " + json.Serialize(selection.SuggestionsFile) + ",");
            sb.AppendLine("  \"saved_utc\": " + json.Serialize(selection.SavedUtc.ToString("o")) + ",");
            sb.AppendLine("  \"scenarios\": [");
            for (int i = 0; i < selection.Scenarios.Count; i++)
            {
                Scenario s = selection.Scenarios[i];
                // Dictionary keeps insertion order when serialized, so the keys come out in this order.
                string line = json.Serialize(new Dictionary<string, object>
                {
                    { "id", s.Id }, { "title", s.Title }, { "category", s.Category }, { "where", s.Where },
                    { "source", s.Source }, { "expected", s.Expected }
                });
                sb.AppendLine("    " + line + (i < selection.Scenarios.Count - 1 ? "," : ""));
            }
            sb.AppendLine("  ]");
            sb.AppendLine("}");

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));   // UTF-8 without BOM: friendlier to Python
        }

        public static ScenarioSelection LoadSelection(string path)
        {
            var root = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>;
            if (root == null)
            {
                throw new InvalidDataException("Not a Scenario Lab selection file: " + path);
            }
            var selection = new ScenarioSelection
            {
                Camera = Text(root, "camera"),
                Snapshot = Text(root, "snapshot"),
                SnapshotUtc = Time(root, "snapshot_utc"),
                SuggestionsFile = Text(root, "suggestions_file"),
                SavedUtc = Time(root, "saved_utc")
            };
            object scenarios;
            if (root.TryGetValue("scenarios", out scenarios) && scenarios is object[])
            {
                foreach (var entry in ((object[])scenarios).OfType<Dictionary<string, object>>())
                {
                    string expected = Text(entry, "expected");
                    selection.Scenarios.Add(new Scenario
                    {
                        Id = Text(entry, "id"),
                        Title = Text(entry, "title"),
                        Category = Text(entry, "category"),
                        Where = Text(entry, "where"),
                        Source = Text(entry, "source"),
                        Expected = expected == Scenario.NoDetect ? Scenario.NoDetect : Scenario.Detect,
                        IsSelected = true
                    });
                }
            }
            return selection;
        }

        /// <summary>Lower case, spaces and odd characters become "_": "Vandalism cam" -> "vandalism_cam".</summary>
        public static string Slug(string name)
        {
            var sb = new StringBuilder();
            foreach (char c in (name ?? "").Trim().ToLowerInvariant())
            {
                sb.Append(char.IsLetterOrDigit(c) || c == '-' ? c : '_');
            }
            return sb.ToString();
        }

        private static string Text(Dictionary<string, object> entry, string key)
        {
            object value;
            return entry.TryGetValue(key, out value) && value != null ? value.ToString() : "";
        }

        private static DateTime Time(Dictionary<string, object> entry, string key)
        {
            DateTime time;
            return DateTime.TryParse(Text(entry, key), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out time)
                ? time.ToUniversalTime()
                : DateTime.MinValue;
        }
    }
}
