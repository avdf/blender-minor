using System.Text.RegularExpressions;

namespace BlenderMinor;

/// <summary>
/// Python (bpy) templates. The DEFAULT text is embedded here and is valid for the Blender version we use today.
/// If Blender changes the API contract, just create a file in the templates folder that OVERRIDES the default,
/// without recompiling or restarting (the file is read on every call). Deleting the file goes back to the default.
///
/// Files recognized in <see cref="Dir"/>:
///   base.py        (replaces the base template of ALL shapes)
///   material.py    (replaces the color/specular/roughness block)
///   footer.py      (replaces the final line that prints the result)
///   create_cone.py (etc.: replaces the base for that tool only; takes priority over base.py)
///
/// Contract between the blocks: the base leaves the variable <c>o</c> pointing at the created object; the material and the footer use <c>o</c>.
/// @NAME@ markers are replaced by ALREADY VALIDATED values. A file with an unknown marker, empty, or missing the
/// required markers is IGNORED (warning on stderr) and the embedded text is used.
/// Write only loose statements, no blocks: no line starts with a space.
/// </summary>
public static class Templates
{
    public static string Dir { get; set; } = Environment.GetEnvironmentVariable("BLENDER_MINOR_TEMPLATES")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "blender-minor", "templates");

    public const string BaseDefault = """
        import bpy
        bpy.ops.mesh.@FUNC@(@ARGS@)
        o = bpy.context.active_object
        o.name = '@NAME@'
        """;

    public const string MaterialDefault = """
        m = bpy.data.materials.new('@NAME@_mat')
        m.diffuse_color = @COLOR@
        m.node_tree or setattr(m, 'use_nodes', True)
        p = m.node_tree.nodes['Principled BSDF']
        p.inputs['Base Color'].default_value = @COLOR@
        p.inputs['Roughness'].default_value = @ROUGHNESS@
        s = p.inputs.get('Specular IOR Level') or p.inputs.get('Specular')
        s.default_value = @SPECULAR@
        o.data.materials.append(m)
        """;

    public const string FooterDefault = "print('OK', o.name, tuple(round(v, 4) for v in o.location))";

    public static readonly string[] Keys = ["base", "material", "footer"];

    private static readonly Regex Marker = new(@"@([A-Z][A-Z_]*)@", RegexOptions.Compiled);
    private static readonly HashSet<string> Warned = [];

    public static string Default(string key) => key switch
    {
        "base" => BaseDefault,
        "material" => MaterialDefault,
        "footer" => FooterDefault,
        _ => throw new ArgumentException($"Unknown template '{key}'"),
    };

    /// <summary>Looks for the file (the tool's own first, if any, then the generic one); if neither works, uses the embedded text.</summary>
    public static (string Text, string Source) Resolve(string key, string? toolKey, ISet<string> allowed, params string[] required)
    {
        foreach (var candidate in toolKey is null ? new[] { key } : new[] { toolKey, key })
        {
            string path = Path.Combine(Dir, candidate + ".py");
            if (!File.Exists(path)) continue;
            string text;
            try { text = File.ReadAllText(path).Replace("\r\n", "\n").TrimEnd(); }
            catch (IOException e) { Warn($"{path}: could not be read ({e.Message})"); continue; }
            string[] need = candidate == toolKey ? ["@NAME@"] : required;
            string? problem = Validate(text, allowed, need);
            if (problem is null) return (text, "file " + path);
            Warn($"{path} ignored ({problem}); using the next one in the list (generic base.py or the embedded text)");
        }
        return (Default(key), "embedded");
    }

    private static string? Validate(string text, ISet<string> allowed, string[] required)
    {
        if (string.IsNullOrWhiteSpace(text)) return "vazio";
        foreach (Match m in Marker.Matches(text))
            if (!allowed.Contains(m.Groups[1].Value)) return $"marcador desconhecido @{m.Groups[1].Value}@";
        foreach (var r in required)
            if (!text.Contains(r, StringComparison.Ordinal)) return $"falta o marcador {r}";
        return null;
    }

    /// <summary>Replaces the markers in a single pass (a value is never reprocessed as a marker).</summary>
    public static string Fill(string text, IReadOnlyDictionary<string, string> values) =>
        Marker.Replace(text, m => values.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);

    /// <summary>Is a marker without a value left after Fill? (only happens with a bug of ours; avoids sending garbage to Blender.)</summary>
    public static string? Leftover(string filled) => Marker.Match(filled) is { Success: true } m ? m.Value : null;

    /// <summary>Writes the embedded texts as a starting point for editing. Does not overwrite an existing file.</summary>
    public static IEnumerable<string> Dump(string dir)
    {
        Directory.CreateDirectory(dir);
        foreach (var key in Keys)
        {
            string path = Path.Combine(dir, key + ".py");
            if (File.Exists(path)) { yield return $"already exists, kept: {path}"; continue; }
            File.WriteAllText(path, Default(key) + "\n");
            yield return $"written: {path}";
        }
    }

    private static void Warn(string msg)
    {
        lock (Warned) { if (!Warned.Add(msg)) return; }
        Console.Error.WriteLine("[blender-minor] aviso: " + msg);
    }
}
