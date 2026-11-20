using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

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

    private static readonly OpProp[] HolePlacement =
    [
        X, Y, Z,
        new("axis", "string", "X, Y or Z (default Z)"),
        new("side", "string", "+ (default) or -: which side of the axis the hole starts on (the top for Z)"),
    ];

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
        new("set_color",
            "Change the color of an existing object by name (replaces its material).",
            [NameProp, new("color", "string", "name (red, blue...) or #RRGGBB"), new("roughness", "number", "0 to 1"), new("specular", "number", "0 to 1")],
            ["name", "color"]),
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
            "Drill a round hole of an exact diameter in an existing object by name, along the X, Y or Z axis. Without depth the hole goes all the way through. With depth it is a blind hole that starts at the entry point (x, y, z) and goes into the part. Optional counterbore (a wider recess for a screw head) or countersink (a cone for a flat head).",
            new OpProp[] { NameProp, new("diameter", "number", "hole diameter") }
                .Concat(HolePlacement)
                .Concat([new OpProp("depth", "number", "omit for a through hole"),
                         new OpProp("clearance", "number", "extra diameter for 3D printing, default 0"),
                         new OpProp("counterbore_diameter", "number", "recess diameter at the entry"),
                         new OpProp("counterbore_depth", "number", "recess depth"),
                         new OpProp("countersink_diameter", "number", "countersink diameter at the entry (90 degree cone)"),
                         new OpProp("segments", "number", "sides of the hole, 8 to 256, default 64")]).ToArray(),
            ["name", "diameter"]),
        new("screw_hole",
            "Drill a hole for a standard screw size by name, using a table of typical dimensions. kind: clearance (screw passes through), counterbore (plus a recess for a socket head), countersunk (plus a cone for a flat head) or insert (blind hole for a heat-set insert; needs the entry point z along the axis).",
            new OpProp[] { NameProp, new("size", "string", "M2, M2.5, M3, M4, M5, M6 or M8 (default M3)"),
                           new("kind", "string", "clearance (default), counterbore, countersunk or insert") }
                .Concat(HolePlacement)
                .Concat([new OpProp("depth", "number", "optional: makes a clearance hole blind")]).ToArray(),
            ["name"]),
        new("check_mesh",
            "Check an existing object: is the mesh closed (manifold, printable), its volume and its dimensions.",
            [NameProp],
            ["name"]),
        new("repair_mesh",
            "Repair an existing mesh so slicers accept it: merge duplicate vertices, remove loose parts, fix normals, fill holes. Reports before and after; leaves the object unchanged if the repair makes it worse.",
            [NameProp, new("tolerance", "number", "merge distance, default 0.0001")],
            ["name"]),
        new("delete_object",
            "Delete one existing object by name.",
            [NameProp],
            ["name"]),
        new("export_stl",
            "Export an existing object as a binary STL file for a slicer. Only a file name is accepted; the file is written to the export folder.",
            [NameProp, new("filename", "string", "file name, e.g. bracket.stl (default: the object name)")],
            ["name"]),
        new("set_units",
            "Set the scene units so that one Blender unit equals the chosen unit. Use mm for 3D printing.",
            [new("unit", "string", "mm, cm or m (default mm)")],
            []),
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
        "set_color" => WithObject(args, "name", (lit, a) => BuildSetColor(lit, a)),
        "move_object" => WithObject(args, "name", (lit, a) => BuildMove(lit, a)),
        "bevel_object" => WithObject(args, "name", (lit, a) => BuildBevel(lit, a)),
        "check_mesh" => WithObject(args, "name", (lit, a) => BuildCheck(lit)),
        "repair_mesh" => WithObject(args, "name", (lit, a) => BuildRepair(lit, a)),
        "delete_object" => WithObject(args, "name", (lit, a) => BuildDelete(lit)),
        "export_stl" => WithObject(args, "name", (lit, a) => BuildExport(lit, a)),
        "drill_hole" => WithObject(args, "name", (lit, a) => BuildDrill(lit, a, null)),
        "screw_hole" => WithObject(args, "name", (lit, a) => BuildScrewHole(lit, a)),
        "boolean_op" => BuildBoolean(args),
        "set_units" => BuildUnits(args),
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

    private static (bool Value, string? Error) ReadBool(JsonElement args, string key, bool def)
    {
        if (!ShapeScripts.Has(args, key)) return (def, null);
        var el = args.GetProperty(key);
        if (el.ValueKind == JsonValueKind.True) return (true, null);
        if (el.ValueKind == JsonValueKind.False) return (false, null);
        if (el.ValueKind == JsonValueKind.String && bool.TryParse(el.GetString(), out var b)) return (b, null);
        return (def, $"Argument '{key}' must be true or false.");
    }

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

    private static (string? Script, string? Error) BuildSetColor(string lit, JsonElement args)
    {
        string? colorText = ShapeScripts.Text(args, "color");
        if (string.IsNullOrWhiteSpace(colorText)) return (null, "Argument 'color' is required.");
        var rgb = ShapeScripts.ParseColor(colorText);
        if (rgb is null) return (null, $"Invalid color '{colorText}'. Use one of: {ShapeScripts.ColorHelp}.");
        var (rough, er) = ShapeScripts.Number(args, "roughness", 0.5, 0, 1); if (er is not null) return (null, er);
        var (spec, es) = ShapeScripts.Number(args, "specular", 0.5, 0, 1); if (es is not null) return (null, es);
        string matName = ShapeScripts.CleanName(ShapeScripts.Text(args, "name"), "Object");
        var values = new Dictionary<string, string>
        {
            ["NAME"] = matName,
            ["COLOR"] = $"({ShapeScripts.F(rgb.Value.R)}, {ShapeScripts.F(rgb.Value.G)}, {ShapeScripts.F(rgb.Value.B)}, 1)",
            ["ROUGHNESS"] = ShapeScripts.F(rough),
            ["SPECULAR"] = ShapeScripts.F(spec),
        };
        // the same material template the shapes use (so an override file applies here too), indented into the else block
        string block = Templates.Fill(Templates.Resolve("material", null, ShapeScripts.MaterialAllowed, "@NAME@", "@COLOR@").Text, values);
        var sb = new StringBuilder();
        sb.Append("import bpy\n");
        sb.Append($"o = bpy.data.objects.get({lit})\n");
        sb.Append("if o is None:\n").Append(Missing(lit)).Append('\n');
        sb.Append("elif o.type != 'MESH':\n");
        sb.Append("    print(\"ERROR: \" + o.name + \" is \" + o.type + \"; set_color needs a MESH object\")\n");
        sb.Append("else:\n");
        sb.Append("    o.data.materials.clear()\n");
        foreach (var line in block.Split('\n')) if (line.Length > 0) sb.Append("    ").Append(line).Append('\n');
        sb.Append("    print(\"OK \" + o.name + \" color set\")");
        string script = sb.ToString();
        return Templates.Leftover(script) is { } left ? (null, $"Template error: marker {left} has no value.") : (script, null);
    }

    private static (string? Script, string? Error) BuildMove(string lit, JsonElement args)
    {
        var (relative, rerr) = ReadBool(args, "relative", false);
        if (rerr is not null) return (null, rerr);
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

    private static (string? Script, string? Error) BuildRepair(string lit, JsonElement args)
    {
        var (tol, et) = ShapeScripts.Number(args, "tolerance", 0.0001, 0, 1); if (et is not null) return (null, et);
        var sb = new StringBuilder(PyHelpers).Append('\n');
        sb.Append($"t = bpy.data.objects.get({lit})\n");
        sb.Append("if t is None:\n").Append(Missing(lit)).Append('\n');
        sb.Append("elif t.type != 'MESH':\n");
        sb.Append("    print(\"ERROR: \" + t.name + \" is \" + t.type + \"; repair_mesh needs a MESH object\")\n");
        sb.Append("else:\n");
        sb.Append("    bad0, vol0, n0 = bm_check(t)\n");
        sb.Append("    old = t.data.copy()\n");
        sb.Append("    bm = bmesh.new()\n");
        sb.Append("    bm.from_mesh(t.data)\n");
        sb.Append($"    bmesh.ops.remove_doubles(bm, verts=bm.verts[:], dist={N(tol)})\n");
        sb.Append("    loose = [e for e in bm.edges if not e.link_faces]\n");
        sb.Append("    if loose: bmesh.ops.delete(bm, geom=loose, context='EDGES')\n");
        sb.Append("    lone = [v for v in bm.verts if not v.link_edges]\n");
        sb.Append("    if lone: bmesh.ops.delete(bm, geom=lone, context='VERTS')\n");
        sb.Append("    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])\n");
        sb.Append("    boundary = [e for e in bm.edges if e.is_boundary]\n");
        sb.Append("    if boundary: bmesh.ops.holes_fill(bm, edges=boundary, sides=0)\n");
        sb.Append("    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])\n");
        sb.Append("    bm.to_mesh(t.data)\n");
        sb.Append("    bm.free()\n");
        sb.Append("    t.data.update()\n");
        sb.Append("    bad1, vol1, n1 = bm_check(t)\n");
        sb.Append("    if bad1 > bad0:\n");
        sb.Append("        broken = t.data\n");
        sb.Append("        t.data = old\n");
        sb.Append("        bpy.data.meshes.remove(broken)\n");
        sb.Append("        print(\"ERROR: the repair made the mesh worse (non-manifold edges %d -> %d); the object was left unchanged\" % (bad0, bad1))\n");
        sb.Append("    else:\n");
        sb.Append("        bpy.data.meshes.remove(old)\n");
        sb.Append("        print(\"OK \" + t.name + \" repaired: non-manifold edges %d -> %d, vertices %d -> %d, volume %.4f\" % (bad0, bad1, n0, n1, vol1))");
        return (sb.ToString(), null);
    }

    private static (string? Script, string? Error) BuildDelete(string lit)
    {
        var sb = new StringBuilder();
        sb.Append("import bpy\n");
        sb.Append($"o = bpy.data.objects.get({lit})\n");
        sb.Append("if o is None:\n").Append(Missing(lit)).Append('\n');
        sb.Append("else:\n");
        sb.Append("    n = o.name\n");
        sb.Append("    bpy.data.objects.remove(o, do_unlink=True)\n");
        sb.Append("    print(\"OK deleted \" + n)");
        return (sb.ToString(), null);
    }

    /// <summary>Folder where export_stl writes. The model never chooses a path, only a file name inside this folder.</summary>
    public static string ExportDir =>
        Environment.GetEnvironmentVariable("BLENDER_MINOR_EXPORT_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "blender-minor-exports");

    private static readonly Regex SafeFile = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.Compiled);

    private static (string? Script, string? Error) BuildExport(string lit, JsonElement args)
    {
        string? file = ShapeScripts.Text(args, "filename");
        if (string.IsNullOrWhiteSpace(file))
            file = ShapeScripts.CleanName(ShapeScripts.Text(args, "name"), "export").Replace(' ', '_');
        if (!file.EndsWith(".stl", StringComparison.OrdinalIgnoreCase)) file += ".stl";
        if (!SafeFile.IsMatch(file))
            return (null, "Argument 'filename' must be a plain file name (letters, digits, '.', '_' or '-'), without folders.");
        string full = Path.Combine(ExportDir, file);
        string pathLit = JsonSerializer.Serialize(full);
        var sb = new StringBuilder();
        sb.Append("import bpy, os\n");
        sb.Append($"o = bpy.data.objects.get({lit})\n");
        sb.Append("if o is None:\n").Append(Missing(lit)).Append('\n');
        sb.Append("elif o.type != 'MESH':\n");
        sb.Append("    print(\"ERROR: \" + o.name + \" is \" + o.type + \"; export_stl needs a MESH object\")\n");
        sb.Append("else:\n");
        sb.Append($"    path = {pathLit}\n");
        sb.Append("    os.makedirs(os.path.dirname(path), exist_ok=True)\n");
        sb.Append("    bpy.ops.object.select_all(action='DESELECT')\n");
        sb.Append("    o.select_set(True)\n");
        sb.Append("    bpy.context.view_layer.objects.active = o\n");
        sb.Append("    bpy.ops.wm.stl_export(filepath=path, export_selected_objects=True, apply_modifiers=True)\n");
        sb.Append("    print(\"OK exported \" + o.name + \" to \" + path + \" (\" + str(os.path.getsize(path)) + \" bytes)\")");
        return (sb.ToString(), null);
    }

    private static (string? Script, string? Error) BuildUnits(JsonElement args)
    {
        string unit = (ShapeScripts.Text(args, "unit") ?? "mm").ToLowerInvariant();
        var (scale, name) = unit switch
        {
            "mm" or "millimeter" or "millimeters" => ("0.001", "MILLIMETERS"),
            "cm" or "centimeter" or "centimeters" => ("0.01", "CENTIMETERS"),
            "m" or "meter" or "meters" => ("1.0", "METERS"),
            _ => ("", ""),
        };
        if (scale.Length == 0) return (null, $"Invalid unit '{unit}'. Use mm, cm or m.");
        var sb = new StringBuilder();
        sb.Append("import bpy\n");
        sb.Append("u = bpy.context.scene.unit_settings\n");
        sb.Append("u.system = 'METRIC'\n");
        sb.Append($"u.scale_length = {scale}\n");
        sb.Append($"u.length_unit = '{name}'\n");
        sb.Append($"print(\"OK units set: 1 Blender unit = 1 {unit}\")");
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
        var (deleteCutter, db) = ReadBool(args, "delete_cutter", true);
        if (db is not null) return (null, db);
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

    private static (string? Script, string? Error) BuildDrill(string lit, JsonElement args, string? label)
    {
        if (!ShapeScripts.Has(args, "diameter")) return (null, "Argument 'diameter' is required.");
        var (dia, ed) = ShapeScripts.Number(args, "diameter", 0, 0.01, 10000); if (ed is not null) return (null, ed);
        var (clr, ecl) = ShapeScripts.Number(args, "clearance", 0, 0, 10); if (ecl is not null) return (null, ecl);
        var (segf, esg) = ShapeScripts.Number(args, "segments", 64, 8, 256); if (esg is not null) return (null, esg);
        int seg = (int)Math.Round(segf);
        string axis = (ShapeScripts.Text(args, "axis") ?? "Z").ToUpperInvariant();
        if (axis is not ("X" or "Y" or "Z")) return (null, $"Invalid axis '{axis}'. Use X, Y or Z.");
        int ai = axis[0] - 'X';
        string sideText = (ShapeScripts.Text(args, "side") ?? "+").ToLowerInvariant();
        int d = sideText switch { "+" or "top" or "pos" or "positive" => 1, "-" or "bottom" or "neg" or "negative" => -1, _ => 0 };
        if (d == 0) return (null, $"Invalid side '{sideText}'. Use + or - (the side of the axis where the hole starts).");
        bool through = !ShapeScripts.Has(args, "depth");
        double depth = 0;
        if (!through) { var (dp, ep) = ShapeScripts.Number(args, "depth", 0, 0.001, 10000); if (ep is not null) return (null, ep); depth = dp; }

        // Optional recess at the entry: a counterbore (cylinder) or a countersink (90 degree cone), never both.
        bool hasCb = ShapeScripts.Has(args, "counterbore_diameter") || ShapeScripts.Has(args, "counterbore_depth");
        bool hasCs = ShapeScripts.Has(args, "countersink_diameter");
        if (hasCb && hasCs) return (null, "Use either a counterbore or a countersink, not both.");
        double cbD = 0, cbH = 0, csD = 0;
        if (hasCb)
        {
            if (!ShapeScripts.Has(args, "counterbore_diameter") || !ShapeScripts.Has(args, "counterbore_depth"))
                return (null, "A counterbore needs both counterbore_diameter and counterbore_depth.");
            var (a1, e1) = ShapeScripts.Number(args, "counterbore_diameter", 0, 0.01, 10000); if (e1 is not null) return (null, e1);
            var (a2, e2) = ShapeScripts.Number(args, "counterbore_depth", 0, 0.001, 10000); if (e2 is not null) return (null, e2);
            if (a1 <= dia) return (null, "counterbore_diameter must be larger than the hole diameter.");
            cbD = a1; cbH = a2;
            if (!through && cbH >= depth) return (null, "counterbore_depth must be smaller than the hole depth.");
        }
        if (hasCs)
        {
            var (a1, e1) = ShapeScripts.Number(args, "countersink_diameter", 0, 0.01, 10000); if (e1 is not null) return (null, e1);
            if (a1 <= dia) return (null, "countersink_diameter must be larger than the hole diameter.");
            csD = a1;
        }

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

        double holeR = (dia + clr) / 2;
        double margin = Math.Max(0.5, dia * 0.25);   // the cutter sticks out of the part: no coplanar faces
        string rot = ai switch { 0 => "(0, 1.5707963267948966, 0)", 1 => "(1.5707963267948966, 0, 0)", _ => "(0, 0, 0)" };
        // the cone's wide end must point out of the part, on the entry side
        const string H = "1.5707963267948966";
        string coneRot = (ai, d) switch
        {
            (2, 1) => "(0, 0, 0)", (2, -1) => "(3.141592653589793, 0, 0)",
            (0, 1) => $"(0, {H}, 0)", (0, -1) => $"(0, -{H}, 0)",
            (1, 1) => $"(-{H}, 0, 0)", _ => $"({H}, 0, 0)",
        };
        string where = through
            ? "(" + string.Join(", ", Enumerable.Range(0, 3).Select(i => i == ai ? "*" : N(pos[i]))) + ")"
            : $"({N(pos[0])}, {N(pos[1])}, {N(pos[2])})";

        var sb = new StringBuilder(PyHelpers).Append('\n');
        sb.Append($"t = bpy.data.objects.get({lit})\n");
        sb.Append("if t is None:\n").Append(Missing(lit)).Append('\n');
        sb.Append("elif t.type != 'MESH':\n");
        sb.Append("    print(\"ERROR: \" + t.name + \" is \" + t.type + \"; drilling needs a MESH object\")\n");
        sb.Append("else:\n");
        sb.Append($"    pos = [{N(pos[0])}, {N(pos[1])}, {N(pos[2])}]\n");
        sb.Append($"    ai = {ai}\n");
        sb.Append($"    d = {d}\n");
        sb.Append("    corners = [t.matrix_world @ Vector(v) for v in t.bound_box]\n");
        sb.Append("    lo = min(v[ai] for v in corners)\n");
        sb.Append("    hi = max(v[ai] for v in corners)\n");
        if (through)
            sb.Append($"    e = hi if d > 0 else lo\n    start = hi + {N(margin)}\n    end = lo - {N(margin)}\n");
        else
            sb.Append($"    e = pos[ai]\n    start = e + d * {N(margin)}\n    end = e - d * {N(depth)}\n");
        sb.Append("    center = list(pos)\n");
        sb.Append("    center[ai] = (start + end) / 2\n");
        sb.Append($"    bpy.ops.mesh.primitive_cylinder_add(vertices={seg}, radius={N(holeR)}, depth=abs(start - end), location=tuple(center), rotation={rot})\n");
        sb.Append("    cutter = bpy.context.active_object\n");
        sb.Append("    cutter.name = 'BM_hole_cutter'\n");
        sb.Append("    err = bm_boolean(t, cutter, 'DIFFERENCE')\n");
        sb.Append("    bpy.data.objects.remove(cutter, do_unlink=True)\n");
        string recess = "";
        if (hasCb || hasCs)
        {
            double m2 = Math.Max(0.5, (hasCb ? cbD : csD) * 0.1);
            sb.Append("    if not err:\n");
            sb.Append("        c2 = list(pos)\n");
            if (hasCb)
            {
                double cbR = (cbD + clr) / 2;
                sb.Append($"        c2[ai] = e + d * ({N(m2)} - {N(cbH)}) / 2\n");
                sb.Append($"        bpy.ops.mesh.primitive_cylinder_add(vertices={seg}, radius={N(cbR)}, depth={N(cbH + m2)}, location=tuple(c2), rotation={rot})\n");
                recess = $" counterbore {N(cbD + clr)}x{N(cbH)}";
            }
            else
            {
                double csR = (csD + clr) / 2;
                double h = csR - holeR;   // 90 degree cone: the radius grows 1:1 with the depth
                sb.Append($"        c2[ai] = e + d * ({N(m2)} - {N(h)}) / 2\n");
                sb.Append($"        bpy.ops.mesh.primitive_cone_add(vertices={seg}, radius1={N(holeR)}, radius2={N(csR + m2)}, depth={N(h + m2)}, location=tuple(c2), rotation={coneRot})\n");
                recess = $" countersink {N(csD + clr)}";
            }
            sb.Append("        cutter2 = bpy.context.active_object\n");
            sb.Append("        cutter2.name = 'BM_recess_cutter'\n");
            sb.Append("        err = bm_boolean(t, cutter2, 'DIFFERENCE')\n");
            sb.Append("        bpy.data.objects.remove(cutter2, do_unlink=True)\n");
            sb.Append("        if err: err = 'the hole was drilled but the recess failed: ' + err\n");
        }
        sb.Append("    if err:\n        print(\"ERROR: \" + err)\n");
        sb.Append("    else:\n");
        sb.Append("        bad, vol, n = bm_check(t)\n");
        string kind = through ? "through" : $"depth {N(depth)}";
        string tag = label is null ? "" : $" [{label}]";
        sb.Append($"        print(\"OK \" + t.name + \" hole diameter {N(dia + clr)} at {where} axis {axis} {kind}{recess}{tag} vertices=\" + str(n) + \" volume=\" + str(round(vol, 4)))");
        return (sb.ToString(), null);
    }

    private static (string? Script, string? Error) BuildScrewHole(string lit, JsonElement args)
    {
        string size = (ShapeScripts.Text(args, "size") ?? "M3").Trim().ToUpperInvariant();
        string kind = (ShapeScripts.Text(args, "kind") ?? "clearance").Trim().ToLowerInvariant();
        var (table, _) = Screws.Load();
        if (!table.TryGetValue(size, out var row))
            return (null, $"Unknown screw size '{size}'. Known sizes: {string.Join(", ", table.Keys.OrderBy(k => k))}.");
        string? Get(string key, out double v) { if (row.TryGetValue(key, out v)) return null; return $"The screw table has no '{key}' for {size}."; }

        var o = new JsonObject();
        foreach (var k in new[] { "x", "y", "z", "axis", "side", "depth", "segments" })
            if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(k, out var v)) o[k] = JsonNode.Parse(v.GetRawText());
        double clearance, cbD, cbH, csD, insD, insH;
        switch (kind)
        {
            case "clearance":
                if (Get("clearance", out clearance) is { } e0) return (null, e0);
                o["diameter"] = clearance;
                break;
            case "counterbore":
                if (Get("clearance", out clearance) is { } e1) return (null, e1);
                if (Get("counterbore_diameter", out cbD) is { } e2) return (null, e2);
                if (Get("counterbore_depth", out cbH) is { } e3) return (null, e3);
                o["diameter"] = clearance; o["counterbore_diameter"] = cbD; o["counterbore_depth"] = cbH;
                break;
            case "countersunk" or "countersink":
                if (Get("clearance", out clearance) is { } e4) return (null, e4);
                if (Get("countersink_diameter", out csD) is { } e5) return (null, e5);
                o["diameter"] = clearance; o["countersink_diameter"] = csD;
                break;
            case "insert":
                if (Get("insert_diameter", out insD) is { } e6) return (null, e6);
                if (Get("insert_depth", out insH) is { } e7) return (null, e7);
                o["diameter"] = insD;
                if (!o.ContainsKey("depth")) o["depth"] = insH;
                break;
            default:
                return (null, $"Invalid kind '{kind}'. Use clearance, counterbore, countersunk or insert.");
        }
        using var doc = JsonDocument.Parse(o.ToJsonString());
        return BuildDrill(lit, doc.RootElement, $"{size} {kind}");
    }
}
