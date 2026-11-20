using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BlenderMinor;

/// <summary>Field of an editing tool (Type: string | number | boolean).</summary>
public sealed record OpProp(string Name, string Type, string Description);

/// <summary>A tool that works on objects by name (move, bevel, boolean, drill...) or builds a box. Same design as the shapes:
/// flat fields, a fixed script inside the binary, validated values only.</summary>
public sealed record Op(string Tool, string Description, OpProp[] Props, string[] Required);

public static class OpScripts
{
    public const double CoordLimit = ShapeScripts.CoordLimit;

    private static readonly OpProp NameProp = new("name", "string", "exact object name, as listed by list_objects");
    private static readonly OpProp X = new("x", "number", "X position");
    private static readonly OpProp Y = new("y", "number", "Y position");
    private static readonly OpProp Z = new("z", "number", "Z position");

    public static readonly Op[] Ops =
    [
        new("create_box",
            "Create a rectangular box with exact sizes. width = X size, depth = Y size, height = Z size. x, y, z are the center of the box.",
            [new("width", "number", "size along X, default 1"), new("depth", "number", "size along Y, default 1"), new("height", "number", "size along Z, default 1"),
             X, Y, Z, new("color", "string", "name (red, blue...) or #RRGGBB"), new("name", "string", "default Box")],
            []),
        new("set_dimensions",
            "Set the exact size of an existing object by name. x, y, z are the new sizes along each axis; axes you leave out keep their size.",
            [NameProp, new("x", "number", "new size along X"), new("y", "number", "new size along Y"), new("z", "number", "new size along Z")],
            ["name"]),
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
        new("boolean_op",
            "Combine two existing objects: UNION adds them, DIFFERENCE cuts the cutter out of the target, INTERSECT keeps only the overlap. The result replaces the target. If the result is a broken mesh the target is left unchanged and an error is returned.",
            [new("target", "string", "name of the object that is kept and modified"), new("cutter", "string", "name of the other object"),
             new("operation", "string", "UNION, DIFFERENCE or INTERSECT (default DIFFERENCE)"), new("delete_cutter", "boolean", "remove the cutter afterwards, default true")],
            ["target", "cutter"]),
        new("drill_hole",
            "Drill a round hole of an exact diameter in an existing object by name, along the X, Y or Z axis. Without depth the hole goes all the way through. With depth it is a blind hole that starts at the entry point and goes toward the negative axis direction (down for Z).",
            [NameProp, new("diameter", "number", "hole diameter"), X, Y, Z,
             new("axis", "string", "X, Y or Z (default Z)"), new("depth", "number", "omit for a through hole"),
             new("clearance", "number", "extra diameter for 3D printing, default 0"), new("segments", "number", "sides of the hole, 8 to 256, default 64")],
            ["name", "diameter"]),
        new("check_mesh",
            "Check an existing object: is the mesh closed (manifold, printable), its volume and its dimensions.",
            [NameProp],
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

    public static (string? Script, string? Error) Build(Op op, JsonElement args) => op.Tool switch
    {
        "create_box" => BuildBox(args),
        "set_dimensions" => WithObject(args, "name", (lit, a) => BuildSetDimensions(lit, a)),
        "move_object" => WithObject(args, "name", (lit, a) => BuildMove(lit, a)),
        "bevel_object" => WithObject(args, "name", (lit, a) => BuildBevel(lit, a)),
        "check_mesh" => WithObject(args, "name", (lit, a) => BuildCheck(lit)),
        "drill_hole" => WithObject(args, "name", (lit, a) => BuildDrill(lit, a)),
        "boolean_op" => BuildBoolean(args),
        _ => (null, $"Unknown tool '{op.Tool}'."),
    };

    // ---- shared pieces -------------------------------------------------------------------------------------------

    /// <summary>Validates an object name and hands it over as an escaped string literal. The name ALWAYS enters the script as an
    /// escaped string literal (JSON is valid Python string syntax), never as code.</summary>
    private static (string? Script, string? Error) WithObject(JsonElement args, string key, Func<string, JsonElement, (string? Script, string? Error)> build)
    {
        var (lit, err) = NameLiteral(args, key);
        return err is not null ? (null, err) : build(lit!, args);
    }

    private static (string? Lit, string? Error) NameLiteral(JsonElement args, string key)
    {
        string? name = ShapeScripts.Text(args, key);
        if (string.IsNullOrWhiteSpace(name))
            return (null, $"Argument '{key}' is required. Use list_objects to see the object names.");
        if (name.Length > 100 || name.Any(char.IsControl))
            return (null, $"Argument '{key}' is not a valid object name.");
        return (JsonSerializer.Serialize(name), null);
    }

    private static string Missing(string lit, string indent = "    ") =>
        $"{indent}print(\"ERROR: object not found: \" + {lit} + \". Objects: \" + \", \".join(x.name for x in bpy.context.scene.objects))";

    private static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Python helpers shared by the scripts that change or inspect a mesh. The boolean is TRANSACTIONAL: it keeps a copy
    /// of the mesh, applies the modifier, checks the result (closed mesh, volume) and puts the old mesh back if it came out broken.</summary>
    private const string PyHelpers = """
        import bpy, bmesh
        from mathutils import Vector
        def bm_check(o):
            bm = bmesh.new()
            bm.from_mesh(o.data)
            bad = sum(1 for e in bm.edges if not e.is_manifold)
            vol = bm.calc_volume() if len(bm.faces) else 0.0
            n = len(bm.verts)
            bm.free()
            return bad, vol, n
        def bm_boolean(t, c, op):
            bad0, vol0, n0 = bm_check(t)
            old = t.data.copy()
            m = t.modifiers.new('BM_Boolean', 'BOOLEAN')
            m.operation = op
            m.solver = 'EXACT'
            m.object = c
            try:
                with bpy.context.temp_override(object=t, active_object=t, selected_objects=[t]):
                    bpy.ops.object.modifier_apply(modifier=m.name)
            except Exception as e:
                if m.name in t.modifiers: t.modifiers.remove(m)
                bpy.data.meshes.remove(old)
                return 'boolean failed: ' + str(e).strip()
            bad1, vol1, n1 = bm_check(t)
            if n1 == 0 or abs(vol1) < 1e-9 or bad1 > bad0:
                broken = t.data
                t.data = old
                bpy.data.meshes.remove(broken)
                return 'boolean gave a bad mesh (non-manifold edges %d, volume %.4f); the object was left unchanged' % (bad1, vol1)
            bpy.data.meshes.remove(old)
            return None
        """;

    // ---- the tools -----------------------------------------------------------------------------------------------

    private static (string? Script, string? Error) BuildBox(JsonElement args)
    {
        var (w, ew) = ShapeScripts.Number(args, "width", 1, 0.001, 10000); if (ew is not null) return (null, ew);
        var (d, ed) = ShapeScripts.Number(args, "depth", 1, 0.001, 10000); if (ed is not null) return (null, ed);
        var (h, eh) = ShapeScripts.Number(args, "height", 1, 0.001, 10000); if (eh is not null) return (null, eh);
        // A box is a cube of size 1 scaled to width x depth x height; the scale is then applied so the mesh really has those sizes.
        var o = new JsonObject { ["size"] = 1 };
        foreach (var k in new[] { "x", "y", "z", "color", "specular", "roughness" })
            if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(k, out var v)) o[k] = JsonNode.Parse(v.GetRawText());
        o["name"] = ShapeScripts.Text(args, "name") is { Length: > 0 } nm ? nm : "Box";
        using var doc = JsonDocument.Parse(o.ToJsonString());
        string scale = $"o.scale = ({N(w)}, {N(d)}, {N(h)})\nbpy.ops.object.transform_apply(location=False, rotation=False, scale=True)";
        return ShapeScripts.Build(ShapeScripts.Find("create_cube")!, doc.RootElement, scale);
    }

    private static (string? Script, string? Error) BuildSetDimensions(string lit, JsonElement args)
    {
        var parts = new string[3];
        int given = 0;
        string[] axes = ["x", "y", "z"];
        for (int i = 0; i < 3; i++)
        {
            if (!ShapeScripts.Has(args, axes[i])) { parts[i] = $"d[{i}]"; continue; }
            var (v, err) = ShapeScripts.Number(args, axes[i], 1, 0.001, 10000);
            if (err is not null) return (null, err);
            given++;
            parts[i] = N(v);
        }
        if (given == 0) return (null, "Give at least one of x, y, z.");
        var sb = new StringBuilder();
        sb.Append("import bpy\n");
        sb.Append($"o = bpy.data.objects.get({lit})\n");
        sb.Append("if o is None:\n").Append(Missing(lit)).Append('\n');
        sb.Append("elif o.type != 'MESH':\n");
        sb.Append("    print(\"ERROR: \" + o.name + \" is \" + o.type + \"; set_dimensions needs a MESH object\")\n");
        sb.Append("else:\n");
        sb.Append("    d = list(o.dimensions)\n");
        sb.Append($"    o.dimensions = ({parts[0]}, {parts[1]}, {parts[2]})\n");
        sb.Append("    bpy.ops.object.select_all(action='DESELECT')\n");
        sb.Append("    o.select_set(True)\n");
        sb.Append("    bpy.context.view_layer.objects.active = o\n");
        sb.Append("    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)\n");
        sb.Append("    print(\"OK \" + o.name + \" dimensions \" + str(tuple(round(v, 4) for v in o.dimensions)))");
        return (sb.ToString(), null);
    }

    private static (string? Script, string? Error) BuildMove(string lit, JsonElement args)
    {
        bool relative = false;
        if (ShapeScripts.Has(args, "relative"))
        {
            var el = args.GetProperty("relative");
            if (el.ValueKind == JsonValueKind.True) relative = true;
            else if (el.ValueKind == JsonValueKind.False) relative = false;
            else if (el.ValueKind == JsonValueKind.String && bool.TryParse(el.GetString(), out var b)) relative = b;
            else return (null, "Argument 'relative' must be true or false.");
        }
        string[] axes = ["x", "y", "z"];
        var parts = new string[3];
        int given = 0;
        for (int i = 0; i < 3; i++)
        {
            if (!ShapeScripts.Has(args, axes[i])) { parts[i] = $"loc[{i}]"; continue; }
            var (v, err) = ShapeScripts.Number(args, axes[i], 0, -CoordLimit, CoordLimit);
            if (err is not null) return (null, err);
            given++;
            parts[i] = relative ? $"loc[{i}] + {N(v)}" : N(v);
        }
        if (given == 0) return (null, "Give at least one of x, y, z.");

        var sb = new StringBuilder();
        sb.Append("import bpy\n");
        sb.Append($"o = bpy.data.objects.get({lit})\n");
        sb.Append("if o is None:\n").Append(Missing(lit)).Append('\n');
        sb.Append("else:\n");
        sb.Append("    loc = list(o.location)\n");
        sb.Append($"    o.location = ({parts[0]}, {parts[1]}, {parts[2]})\n");
        sb.Append("    print(\"OK \" + o.name + \" \" + str(tuple(round(v, 4) for v in o.location)))");
        return (sb.ToString(), null);
    }

    private static (string? Script, string? Error) BuildBevel(string lit, JsonElement args)
    {
        var (w, ew) = ShapeScripts.Number(args, "width", 0.1, 0.0001, 1000); if (ew is not null) return (null, ew);
        var (sg, es) = ShapeScripts.Number(args, "segments", 1, 1, 16); if (es is not null) return (null, es);
        int segs = (int)Math.Round(sg);
        string ws = N(w);

        var sb = new StringBuilder();
        sb.Append("import bpy\n");
        sb.Append($"o = bpy.data.objects.get({lit})\n");
        sb.Append("if o is None:\n").Append(Missing(lit)).Append('\n');
        sb.Append("elif o.type != 'MESH':\n");
        sb.Append("    print(\"ERROR: \" + o.name + \" is \" + o.type + \"; bevel needs a MESH object\")\n");
        sb.Append("else:\n");
        sb.Append("    m = o.modifiers.get('Bevel')\n");
        sb.Append("    if m is None: m = o.modifiers.new('Bevel', 'BEVEL')\n");
        sb.Append($"    m.width = {ws}\n");
        sb.Append($"    m.segments = {segs.ToString(CultureInfo.InvariantCulture)}\n");
        sb.Append($"    print(\"OK \" + o.name + \" bevel width={ws} segments={segs.ToString(CultureInfo.InvariantCulture)}\")");
        return (sb.ToString(), null);
    }

    private static (string? Script, string? Error) BuildCheck(string lit)
    {
        var sb = new StringBuilder(PyHelpers).Append('\n');
        sb.Append($"t = bpy.data.objects.get({lit})\n");
        sb.Append("if t is None:\n").Append(Missing(lit)).Append('\n');
        sb.Append("elif t.type != 'MESH':\n");
        sb.Append("    print(\"ERROR: \" + t.name + \" is \" + t.type + \"; check_mesh needs a MESH object\")\n");
        sb.Append("else:\n");
        sb.Append("    bad, vol, n = bm_check(t)\n");
        sb.Append("    print(\"OK \" + t.name + \" manifold=\" + str(bad == 0) + \" non_manifold_edges=\" + str(bad) + \" volume=\" + str(round(vol, 4)) + \" dimensions=\" + str(tuple(round(v, 4) for v in t.dimensions)) + \" vertices=\" + str(n))");
        return (sb.ToString(), null);
    }

    private static readonly Dictionary<string, string> OperationNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["union"] = "UNION", ["add"] = "UNION", ["join"] = "UNION",
        ["difference"] = "DIFFERENCE", ["subtract"] = "DIFFERENCE", ["cut"] = "DIFFERENCE", ["diff"] = "DIFFERENCE",
        ["intersect"] = "INTERSECT", ["intersection"] = "INTERSECT", ["common"] = "INTERSECT",
    };

    private static (string? Script, string? Error) BuildBoolean(JsonElement args)
    {
        var (tl, et) = NameLiteral(args, "target"); if (et is not null) return (null, et);
        var (cl, ec) = NameLiteral(args, "cutter"); if (ec is not null) return (null, ec);
        string op = "DIFFERENCE";
        if (ShapeScripts.Has(args, "operation"))
        {
            string given = ShapeScripts.Text(args, "operation") ?? "";
            if (!OperationNames.TryGetValue(given, out var mapped))
                return (null, $"Invalid operation '{given}'. Use UNION, DIFFERENCE or INTERSECT.");
            op = mapped;
        }
        bool deleteCutter = true;
        if (ShapeScripts.Has(args, "delete_cutter"))
        {
            var el = args.GetProperty("delete_cutter");
            if (el.ValueKind == JsonValueKind.True) deleteCutter = true;
            else if (el.ValueKind == JsonValueKind.False) deleteCutter = false;
            else if (el.ValueKind == JsonValueKind.String && bool.TryParse(el.GetString(), out var b)) deleteCutter = b;
            else return (null, "Argument 'delete_cutter' must be true or false.");
        }
        var sb = new StringBuilder(PyHelpers).Append('\n');
        sb.Append($"t = bpy.data.objects.get({tl})\n");
        sb.Append($"c = bpy.data.objects.get({cl})\n");
        sb.Append("if t is None:\n").Append(Missing(tl!)).Append('\n');
        sb.Append("elif c is None:\n").Append(Missing(cl!)).Append('\n');
        sb.Append("elif t == c:\n    print(\"ERROR: target and cutter must be different objects\")\n");
        sb.Append("elif t.type != 'MESH' or c.type != 'MESH':\n    print(\"ERROR: boolean_op needs two MESH objects\")\n");
        sb.Append("else:\n");
        sb.Append($"    err = bm_boolean(t, c, '{op}')\n");
        sb.Append("    if err:\n        print(\"ERROR: \" + err)\n");
        sb.Append("    else:\n");
        if (deleteCutter) sb.Append("        bpy.data.objects.remove(c, do_unlink=True)\n");
        sb.Append("        bad, vol, n = bm_check(t)\n");
        sb.Append($"        print(\"OK \" + t.name + \" {op} vertices=\" + str(n) + \" volume=\" + str(round(vol, 4)))");
        return (sb.ToString(), null);
    }

    private static (string? Script, string? Error) BuildDrill(string lit, JsonElement args)
    {
        var (dia, ed) = ShapeScripts.Number(args, "diameter", 0, 0.01, 10000); if (ed is not null) return (null, ed);
        if (!ShapeScripts.Has(args, "diameter")) return (null, "Argument 'diameter' is required.");
        var (clr, ecl) = ShapeScripts.Number(args, "clearance", 0, 0, 10); if (ecl is not null) return (null, ecl);
        var (segf, esg) = ShapeScripts.Number(args, "segments", 64, 8, 256); if (esg is not null) return (null, esg);
        int seg = (int)Math.Round(segf);
        string axis = (ShapeScripts.Text(args, "axis") ?? "Z").ToUpperInvariant();
        if (axis is not ("X" or "Y" or "Z")) return (null, $"Invalid axis '{axis}'. Use X, Y or Z.");
        int ai = axis[0] - 'X';
        bool through = !ShapeScripts.Has(args, "depth");
        double depth = 0;
        if (!through) { var (dp, ep) = ShapeScripts.Number(args, "depth", 0, 0.001, 10000); if (ep is not null) return (null, ep); depth = dp; }

        // Position of the hole axis. The two coordinates across the hole are required; the one ALONG the axis is
        // only needed for a blind hole (it is the entry point).
        string[] names = ["x", "y", "z"];
        var pos = new double[3];
        for (int i = 0; i < 3; i++)
        {
            bool needed = i != ai || !through;
            if (!ShapeScripts.Has(args, names[i]))
            {
                if (needed) return (null, i != ai
                    ? $"Argument '{names[i]}' is required (the hole position across the {axis} axis)."
                    : $"Argument '{names[i]}' is required for a blind hole (the entry point along {axis}).");
                continue;
            }
            var (v, err) = ShapeScripts.Number(args, names[i], 0, -CoordLimit, CoordLimit);
            if (err is not null) return (null, err);
            pos[i] = v;
        }

        double radius = (dia + clr) / 2;
        double margin = Math.Max(0.5, dia * 0.25);   // the cutter sticks out of the part: no coplanar faces
        string rot = ai switch { 0 => "(0, 1.5707963267948966, 0)", 1 => "(1.5707963267948966, 0, 0)", _ => "(0, 0, 0)" };
        string where = through
            ? "(" + string.Join(", ", Enumerable.Range(0, 3).Select(i => i == ai ? "*" : N(pos[i]))) + ")"
            : $"({N(pos[0])}, {N(pos[1])}, {N(pos[2])})";

        var sb = new StringBuilder(PyHelpers).Append('\n');
        sb.Append($"t = bpy.data.objects.get({lit})\n");
        sb.Append("if t is None:\n").Append(Missing(lit)).Append('\n');
        sb.Append("elif t.type != 'MESH':\n");
        sb.Append("    print(\"ERROR: \" + t.name + \" is \" + t.type + \"; drill_hole needs a MESH object\")\n");
        sb.Append("else:\n");
        sb.Append($"    pos = [{N(pos[0])}, {N(pos[1])}, {N(pos[2])}]\n");
        sb.Append($"    ai = {ai}\n");
        sb.Append("    corners = [t.matrix_world @ Vector(v) for v in t.bound_box]\n");
        sb.Append("    lo = min(v[ai] for v in corners)\n");
        sb.Append("    hi = max(v[ai] for v in corners)\n");
        if (through)
            sb.Append($"    start = hi + {N(margin)}\n    end = lo - {N(margin)}\n");
        else
            sb.Append($"    start = pos[ai] + {N(margin)}\n    end = pos[ai] - {N(depth)}\n");
        sb.Append("    center = list(pos)\n");
        sb.Append("    center[ai] = (start + end) / 2\n");
        sb.Append($"    bpy.ops.mesh.primitive_cylinder_add(vertices={seg}, radius={N(radius)}, depth=abs(start - end), location=tuple(center), rotation={rot})\n");
        sb.Append("    cutter = bpy.context.active_object\n");
        sb.Append("    cutter.name = 'BM_hole_cutter'\n");
        sb.Append("    err = bm_boolean(t, cutter, 'DIFFERENCE')\n");
        sb.Append("    bpy.data.objects.remove(cutter, do_unlink=True)\n");
        sb.Append("    if err:\n        print(\"ERROR: \" + err)\n");
        sb.Append("    else:\n");
        sb.Append("        bad, vol, n = bm_check(t)\n");
        string kind = through ? "through" : $"depth {N(depth)}";
        sb.Append($"        print(\"OK \" + t.name + \" hole diameter {N(dia + clr)} at {where} axis {axis} {kind} vertices=\" + str(n) + \" volume=\" + str(round(vol, 4)))");
        return (sb.ToString(), null);
    }
}
