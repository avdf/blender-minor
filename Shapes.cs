using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BlenderMinor;

/// <summary>Numeric parameter specific to one shape (flat field; <c>BpyName</c> is the name in bpy).</summary>
public sealed record Param(string Name, string BpyName, double Default, double Min, double Max);

/// <summary>One shape = one tool. Every tool calls the same <see cref="ShapeScripts.Build"/>.</summary>
public sealed record Shape(string Tool, string Label, string Description, string BpyFunc, string FixedArgs, Param[] Params);

public static class ShapeScripts
{
    public const double CoordLimit = 10000;

    public static readonly Shape[] Shapes =
    [
        new("create_sphere", "Sphere", "Create a sphere in Blender.", "primitive_uv_sphere_add", "",
            [new("radius", "radius", 1, 0.001, 1000)]),
        new("create_cube", "Cube", "Create a cube in Blender.", "primitive_cube_add", "",
            [new("size", "size", 2, 0.001, 1000)]),
        new("create_cylinder", "Cylinder", "Create a cylinder in Blender.", "primitive_cylinder_add", "",
            [new("radius", "radius", 1, 0.001, 1000), new("height", "depth", 2, 0.001, 1000)]),
        new("create_cone", "Cone", "Create a cone in Blender.", "primitive_cone_add", "radius2=0",
            [new("radius", "radius1", 1, 0.001, 1000), new("height", "depth", 2, 0.001, 1000)]),
        new("create_torus", "Torus", "Create a torus (donut) in Blender.", "primitive_torus_add", "",
            [new("major_radius", "major_radius", 1, 0.001, 1000), new("minor_radius", "minor_radius", 0.25, 0.001, 1000)]),
        new("create_plane", "Plane", "Create a flat square plane in Blender.", "primitive_plane_add", "",
            [new("size", "size", 2, 0.001, 1000)]),
        new("create_icosphere", "Icosphere", "Create an icosphere (a sphere made of triangles) in Blender.", "primitive_ico_sphere_add", "",
            [new("radius", "radius", 1, 0.001, 1000), new("subdivisions", "subdivisions", 2, 1, 6)]),
    ];

    public static Shape? Find(string tool) => Shapes.FirstOrDefault(s => s.Tool == tool);

    private static readonly Dictionary<string, (double R, double G, double B)> NamedColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["red"] = (1, 0, 0), ["vermelho"] = (1, 0, 0),
        ["green"] = (0, 0.6, 0), ["verde"] = (0, 0.6, 0),
        ["blue"] = (0, 0, 1), ["azul"] = (0, 0, 1),
        ["yellow"] = (1, 1, 0), ["amarelo"] = (1, 1, 0),
        ["orange"] = (1, 0.5, 0), ["laranja"] = (1, 0.5, 0),
        ["purple"] = (0.5, 0, 0.5), ["roxo"] = (0.5, 0, 0.5),
        ["pink"] = (1, 0.4, 0.7), ["rosa"] = (1, 0.4, 0.7),
        ["white"] = (1, 1, 1), ["branco"] = (1, 1, 1),
        ["black"] = (0, 0, 0), ["preto"] = (0, 0, 0),
        ["gray"] = (0.5, 0.5, 0.5), ["grey"] = (0.5, 0.5, 0.5), ["cinza"] = (0.5, 0.5, 0.5),
        ["brown"] = (0.4, 0.2, 0.05), ["marrom"] = (0.4, 0.2, 0.05),
        ["cyan"] = (0, 1, 1), ["ciano"] = (0, 1, 1),
        // Feminine forms (the model agrees with the noun: "esfera roxa") and common synonyms.
        ["vermelha"] = (1, 0, 0), ["amarela"] = (1, 1, 0), ["roxa"] = (0.5, 0, 0.5), ["branca"] = (1, 1, 1), ["preta"] = (0, 0, 0),
        ["violet"] = (0.5, 0, 0.5), ["violeta"] = (0.5, 0, 0.5), ["lilas"] = (0.7, 0.5, 0.9), ["lilás"] = (0.7, 0.5, 0.9),
        ["gold"] = (1, 0.8, 0), ["dourado"] = (1, 0.8, 0), ["dourada"] = (1, 0.8, 0), ["ouro"] = (1, 0.8, 0),
        ["silver"] = (0.75, 0.75, 0.75), ["prata"] = (0.75, 0.75, 0.75), ["prateado"] = (0.75, 0.75, 0.75), ["prateada"] = (0.75, 0.75, 0.75),
    };

    public const string ColorHelp = "red, green, blue, yellow, orange, purple, pink, white, black, gray, brown, cyan, or #RRGGBB (Portuguese names also work)";

    // Markers accepted in each template (a file using any marker not listed here is ignored).
    private static readonly string[] BaseFixed = ["FUNC", "ARGS", "NAME", "X", "Y", "Z"];
    internal static readonly HashSet<string> MaterialAllowed = ["NAME", "COLOR", "ROUGHNESS", "SPECULAR"];
    private static readonly HashSet<string> FooterAllowed = ["NAME"];

    public static HashSet<string> BaseAllowed(Shape shape) =>
        new(BaseFixed.Concat(shape.Params.Select(p => p.Name.ToUpperInvariant())));

    /// <summary>Where each text comes from (file or embedded) for this shape; used by /templates and by --selftest.</summary>
    public static Dictionary<string, string> Sources(Shape shape) => new()
    {
        ["base"] = Templates.Resolve("base", shape.Tool, BaseAllowed(shape), "@NAME@", "@ARGS@").Source,
        ["material"] = Templates.Resolve("material", null, MaterialAllowed, "@NAME@", "@COLOR@").Source,
        ["footer"] = Templates.Resolve("footer", null, FooterAllowed).Source,
    };

    /// <summary>Builds the bpy script from the templates. Returns an error as text if any argument is invalid.</summary>
    public static (string? Script, string? Error) Build(Shape shape, JsonElement args, string? beforeFooter = null)
    {
        var inv = CultureInfo.InvariantCulture;

        var vals = new List<string>();
        var values = new Dictionary<string, string>();
        foreach (var p in shape.Params)
        {
            var (v, err) = Number(args, p.Name, p.Default, p.Min, p.Max);
            if (err is not null) return (null, err);
            string s = v.ToString("R", inv);
            vals.Add($"{p.BpyName}={s}");
            values[p.Name.ToUpperInvariant()] = s;
        }
        var (x, ex) = Number(args, "x", 0, -CoordLimit, CoordLimit); if (ex is not null) return (null, ex);
        var (y, ey) = Number(args, "y", 0, -CoordLimit, CoordLimit); if (ey is not null) return (null, ey);
        var (z, ez) = Number(args, "z", 0, -CoordLimit, CoordLimit); if (ez is not null) return (null, ez);

        string name = CleanName(Text(args, "name"), shape.Label);

        bool hasColor = Text(args, "color") is { Length: > 0 };
        bool hasSpec = Has(args, "specular");
        bool hasRough = Has(args, "roughness");
        (double R, double G, double B) rgb = (0.8, 0.8, 0.8);
        double spec = 0.5, rough = 0.5;
        if (hasColor)
        {
            var c = ParseColor(Text(args, "color")!);
            if (c is null) return (null, $"Invalid color '{Text(args, "color")}'. Use one of: {ColorHelp}.");
            rgb = c.Value;
        }
        if (hasSpec) { var (v, e) = Number(args, "specular", 0.5, 0, 1); if (e is not null) return (null, e); spec = v; }
        if (hasRough) { var (v, e) = Number(args, "roughness", 0.5, 0, 1); if (e is not null) return (null, e); rough = v; }

        // Only already-validated values go into the markers: formatted numbers, a cleaned name (no '@' or quotes), a numeric color.
        string fixedArgs = shape.FixedArgs.Length > 0 ? shape.FixedArgs + ", " : "";
        string xs = x.ToString("R", inv), ys = y.ToString("R", inv), zs = z.ToString("R", inv);
        values["FUNC"] = shape.BpyFunc;
        values["ARGS"] = $"{string.Join(", ", vals)}, {fixedArgs}location=({xs}, {ys}, {zs})";
        values["NAME"] = name;
        values["X"] = xs; values["Y"] = ys; values["Z"] = zs;

        var sb = new StringBuilder();
        var baseT = Templates.Resolve("base", shape.Tool, BaseAllowed(shape), "@NAME@", "@ARGS@").Text;
        sb.Append(Templates.Fill(baseT, values)).Append('\n');
        if (hasColor || hasSpec || hasRough)
        {
            string col = $"({F(rgb.R)}, {F(rgb.G)}, {F(rgb.B)}, 1)";
            var matValues = new Dictionary<string, string>
            {
                ["NAME"] = name, ["COLOR"] = col, ["ROUGHNESS"] = F(rough), ["SPECULAR"] = F(spec),
            };
            var matT = Templates.Resolve("material", null, MaterialAllowed, "@NAME@", "@COLOR@").Text;
            sb.Append(Templates.Fill(matT, matValues)).Append('\n');
        }
        if (beforeFooter is not null) sb.Append(beforeFooter).Append('\n');   // extra statements (e.g. create_box scales the new object)
        var footT = Templates.Resolve("footer", null, FooterAllowed).Text;
        sb.Append(Templates.Fill(footT, new Dictionary<string, string> { ["NAME"] = name })).Append('\n');

        string script = sb.ToString();
        if (Templates.Leftover(script) is { } left)
            return (null, $"Template error: marker {left} has no value.");
        return (script, null);
    }

    internal static string F(double v) => v.ToString("0.#####", CultureInfo.InvariantCulture);

    internal static bool Has(JsonElement args, string key) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var v) &&
        v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) &&
        !(v.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(v.GetString()));

    internal static string? Text(JsonElement args, string key) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()?.Trim() : null;

    /// <summary>Accepts a number or numeric text ("2", "2,5"); out of range returns an error (nothing is silently adjusted).</summary>
    internal static (double Value, string? Error) Number(JsonElement args, string key, double def, double min, double max)
    {
        if (!Has(args, key)) return (def, null);
        var el = args.GetProperty(key);
        double v;
        if (el.ValueKind == JsonValueKind.Number) v = el.GetDouble();
        else if (el.ValueKind == JsonValueKind.String &&
                 double.TryParse(el.GetString()!.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) v = d;
        else return (0, $"Argument '{key}' must be a number.");
        if (double.IsNaN(v) || double.IsInfinity(v) || v < min || v > max)
            return (0, $"Argument '{key}' must be between {F(min)} and {F(max)}.");
        return (v, null);
    }

    internal static string CleanName(string? raw, string fallback)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        string s = Regex.Replace(raw, @"[^\p{L}\p{N} _.\-]", "").Trim();
        if (s.Length > 40) s = s[..40];
        return s.Length == 0 ? fallback : s;
    }

    internal static (double R, double G, double B)? ParseColor(string text)
    {
        if (NamedColors.TryGetValue(text.Trim(), out var named)) return Linear(named);
        var m = Regex.Match(text.Trim(), @"^#?([0-9a-fA-F]{6})$");
        if (!m.Success) return null;
        int rgb = int.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return Linear((((rgb >> 16) & 255) / 255.0, ((rgb >> 8) & 255) / 255.0, (rgb & 255) / 255.0));
    }

    // Blender stores color in linear space: converts from sRGB (what people "see" in the name/hex).
    private static (double R, double G, double B) Linear((double R, double G, double B) c) => (L(c.R), L(c.G), L(c.B));
    private static double L(double s) => s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
}
