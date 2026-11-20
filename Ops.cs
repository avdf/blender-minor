using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BlenderMinor;

/// <summary>Field of an editing tool (Type: string | number | boolean).</summary>
public sealed record OpProp(string Name, string Type, string Description);

/// <summary>A tool that EDITS an object that already exists, chosen by name (move, bevel...). Same design as the shapes:
/// flat fields, a fixed script inside the binary, validated values only.</summary>
public sealed record Op(string Tool, string Description, OpProp[] Props, string[] Required);

public static class OpScripts
{
    public const double CoordLimit = ShapeScripts.CoordLimit;

    private static readonly OpProp NameProp = new("name", "string", "exact object name, as listed by list_objects");

    public static readonly Op[] Ops =
    [
        new("move_object",
            "Move an existing object by name. x, y, z are the new position; axes you leave out stay where they are. With relative=true they are offsets from the current position.",
            [NameProp,
             new("x", "number", "new X (or offset if relative)"),
             new("y", "number", "new Y (or offset if relative)"),
             new("z", "number", "new Z (or offset if relative)"),
             new("relative", "boolean", "default false")],
            ["name"]),
        new("bevel_object",
            "Round or chamfer the sharp edges of an existing object by name (adds a Bevel modifier). width = size of the bevel; segments = 1 for a flat chamfer, more for a rounded edge.",
            [NameProp,
             new("width", "number", "bevel size, default 0.1"),
             new("segments", "number", "1 to 16, default 1")],
            ["name"]),
    ];

    public static Op? Find(string tool) => Ops.FirstOrDefault(o => o.Tool == tool);

    /// <summary>MCP definition (short description: every token counts for a small model).</summary>
    public static JsonNode ToolDef(Op op)
    {
        var props = new JsonObject();
        foreach (var p in op.Props) props[p.Name] = new JsonObject { ["type"] = p.Type, ["description"] = p.Description };
        var schema = new JsonObject { ["type"] = "object", ["properties"] = props };
        if (op.Required.Length > 0) schema["required"] = new JsonArray(op.Required.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray());
        return new JsonObject { ["name"] = op.Tool, ["description"] = op.Description, ["inputSchema"] = schema };
    }

    public static (string? Script, string? Error) Build(Op op, JsonElement args)
    {
        string? name = ShapeScripts.Text(args, "name");
        if (string.IsNullOrWhiteSpace(name))
            return (null, "Argument 'name' is required. Use list_objects to see the object names.");
        if (name.Length > 100 || name.Any(char.IsControl))
            return (null, "Argument 'name' is not a valid object name.");
        // The name ALWAYS enters the script as an escaped string literal (JSON is valid Python string syntax).
        string lit = JsonSerializer.Serialize(name);
        const string NotFound =
            "    print(\"ERROR: object not found: \" + {0} + \". Objects: \" + \", \".join(x.name for x in bpy.context.scene.objects))";

        return op.Tool switch
        {
            "move_object" => BuildMove(lit, args, NotFound),
            "bevel_object" => BuildBevel(lit, args, NotFound),
            _ => (null, $"Unknown tool '{op.Tool}'."),
        };
    }

    private static (string? Script, string? Error) BuildMove(string lit, JsonElement args, string notFound)
    {
        var inv = CultureInfo.InvariantCulture;
        bool relative = false;
        if (ShapeScripts.Has(args, "relative"))
        {
            var el = args.GetProperty("relative");
            if (el.ValueKind == JsonValueKind.True) relative = true;
            else if (el.ValueKind == JsonValueKind.False) relative = false;
            else if (el.ValueKind == JsonValueKind.String && bool.TryParse(el.GetString(), out var b)) relative = b;
            else return (null, "Argument 'relative' must be true or false.");
        }
        var axes = new[] { "x", "y", "z" };
        var parts = new string[3];
        int given = 0;
        for (int i = 0; i < 3; i++)
        {
            if (!ShapeScripts.Has(args, axes[i])) { parts[i] = $"loc[{i}]"; continue; }
            var (v, err) = ShapeScripts.Number(args, axes[i], 0, -CoordLimit, CoordLimit);
            if (err is not null) return (null, err);
            given++;
            parts[i] = relative ? $"loc[{i}] + {v.ToString("R", inv)}" : v.ToString("R", inv);
        }
        if (given == 0) return (null, "Give at least one of x, y, z.");

        var sb = new StringBuilder();
        sb.Append("import bpy\n");
        sb.Append($"o = bpy.data.objects.get({lit})\n");
        sb.Append("if o is None:\n");
        sb.Append(string.Format(notFound, lit)).Append('\n');
        sb.Append("else:\n");
        sb.Append("    loc = list(o.location)\n");
        sb.Append($"    o.location = ({parts[0]}, {parts[1]}, {parts[2]})\n");
        sb.Append("    print(\"OK \" + o.name + \" \" + str(tuple(round(v, 4) for v in o.location)))");
        return (sb.ToString(), null);
    }

    private static (string? Script, string? Error) BuildBevel(string lit, JsonElement args, string notFound)
    {
        var inv = CultureInfo.InvariantCulture;
        var (w, ew) = ShapeScripts.Number(args, "width", 0.1, 0.0001, 1000); if (ew is not null) return (null, ew);
        var (sg, es) = ShapeScripts.Number(args, "segments", 1, 1, 16); if (es is not null) return (null, es);
        int segs = (int)Math.Round(sg);
        string ws = w.ToString("R", inv);

        var sb = new StringBuilder();
        sb.Append("import bpy\n");
        sb.Append($"o = bpy.data.objects.get({lit})\n");
        sb.Append("if o is None:\n");
        sb.Append(string.Format(notFound, lit)).Append('\n');
        sb.Append("elif o.type != 'MESH':\n");
        sb.Append("    print(\"ERROR: \" + o.name + \" is \" + o.type + \"; bevel needs a MESH object\")\n");
        sb.Append("else:\n");
        sb.Append("    m = o.modifiers.get('Bevel')\n");
        sb.Append("    if m is None: m = o.modifiers.new('Bevel', 'BEVEL')\n");
        sb.Append($"    m.width = {ws}\n");
        sb.Append($"    m.segments = {segs.ToString(inv)}\n");
        sb.Append($"    print(\"OK \" + o.name + \" bevel width={ws} segments={segs.ToString(inv)}\")");
        return (sb.ToString(), null);
    }
}
