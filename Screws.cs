using System.Text.Json;

namespace BlenderMinor;

/// <summary>
/// Standard screw dimensions for <c>screw_hole</c>, in millimeters. The embedded table is only a STARTING POINT of typical values
/// (ISO 273 medium clearance, ISO 4762 socket cap heads, ISO 10642 countersunk heads, common brass heat-set inserts): check them
/// against your own screws, inserts and printer. A file OVERRIDES the embedded values field by field, without recompiling or
/// restarting (it is read on every call): <c>~/.config/blender-minor/screws.json</c> or the path in <c>BLENDER_MINOR_SCREWS</c>.
/// Example: {"M3": {"clearance": 3.5, "insert_diameter": 4.2, "nut_flats": 5.6}}
/// </summary>
public static class Screws
{
    public static readonly string[] Fields =
        ["clearance", "counterbore_diameter", "counterbore_depth", "countersink_diameter", "insert_diameter", "insert_depth", "nut_flats", "nut_thickness"];

    // size: clearance, counterbore diameter, counterbore depth, countersink diameter, insert hole diameter, insert hole depth,
    //       nut width across the flats and nut thickness (ISO 4032, regular hex nut)
    private static readonly Dictionary<string, double[]> Defaults = new()
    {
        ["M2"] = [2.4, 4.4, 2.0, 4.4, 3.2, 4.0, 4.0, 1.6],
        ["M2.5"] = [2.9, 5.0, 2.5, 5.5, 3.6, 5.0, 5.0, 2.0],
        ["M3"] = [3.4, 6.0, 3.0, 6.7, 4.0, 5.7, 5.5, 2.4],
        ["M4"] = [4.5, 8.0, 4.0, 9.0, 5.4, 8.1, 7.0, 3.2],
        ["M5"] = [5.5, 10.0, 5.0, 11.2, 6.4, 9.5, 8.0, 4.0],
        ["M6"] = [6.6, 11.0, 6.0, 13.4, 8.0, 12.0, 10.0, 5.0],
        ["M8"] = [9.0, 15.0, 8.0, 17.9, 9.6, 12.7, 13.0, 6.5],
    };

    public static string FilePath =>
        Environment.GetEnvironmentVariable("BLENDER_MINOR_SCREWS")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "blender-minor", "screws.json");

    /// <summary>The merged table (defaults, then the override file) and where it came from.</summary>
    public static (Dictionary<string, Dictionary<string, double>> Table, string Source) Load()
    {
        var table = Defaults.ToDictionary(
            kv => kv.Key,
            kv => Fields.Zip(kv.Value, (f, v) => (f, v)).ToDictionary(p => p.f, p => p.v),
            StringComparer.OrdinalIgnoreCase);
        string source = "embedded";
        string path = FilePath;
        if (File.Exists(path))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                foreach (var size in doc.RootElement.EnumerateObject())
                {
                    if (!table.TryGetValue(size.Name, out var row)) table[size.Name] = row = new Dictionary<string, double>();
                    foreach (var f in size.Value.EnumerateObject())
                        if (Fields.Contains(f.Name) && f.Value.ValueKind == JsonValueKind.Number) row[f.Name] = f.Value.GetDouble();
                }
                source = "file " + path;
            }
            catch (Exception e) when (e is JsonException or IOException or InvalidOperationException)
            {
                Console.Error.WriteLine($"[blender-minor] {path} ignored ({e.Message}); using the embedded screw table");
            }
        }
        return (table, source);
    }
}
