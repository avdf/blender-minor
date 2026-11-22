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

    /// <summary>Sides of the cylinder that cuts a hole. Each side is a flat face, so a round hole is a polygon: with 64 sides the hole
    /// is 0.16% short of the true circle in area (0.006 mm at a 5 mm radius), with 256 it is 0.01%. The default (64) is right for a
    /// small model and an old CPU. A strong machine can raise it: BLENDER_MINOR_SEGMENTS is the default and BLENDER_MINOR_MAX_SEGMENTS
    /// is the highest value a call may ask for (default 256, at most 4096). The "segments" argument of a call still decides, up to that cap.</summary>
    private static readonly int MaxSegments = EnvInt("BLENDER_MINOR_MAX_SEGMENTS", 256, 8, 4096);
    private static readonly int DefaultSegments = Math.Min(EnvInt("BLENDER_MINOR_SEGMENTS", 64, 8, 4096), MaxSegments);

    private static int EnvInt(string name, int fallback, int lo, int hi)
        => int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
            ? Math.Clamp(v, lo, hi) : fallback;

    /// <summary>Printer compensation, in mm, a property of the MACHINE and not of the request, so it is a server setting (environment) and
    /// not an argument the model has to choose. FDM plastic squeezes inward, so a hole prints smaller than it was drawn: the tool cuts
    /// the requested diameter PLUS this value (ask for 3, with 0.2 it cuts 3.2). Read on every call. Default 0, so the results are
    /// exact until the owner of the printer sets it. BLENDER_MINOR_HOLE_COMP applies to every round hole (drill_hole*, screw_hole, with its
    /// counterbore or countersink) and BLENDER_MINOR_NUT_COMP to the nut traps (across the flats), kept apart because a hexagon closes
    /// differently from a circle and a nut trap is the tightest fit of the part (too small: the nut does not go in; too big: it spins).
    /// Allowed range 0 to 2.</summary>
    public static double HoleComp => EnvDouble("BLENDER_MINOR_HOLE_COMP");
    public static double NutComp => EnvDouble("BLENDER_MINOR_NUT_COMP");

    private static double EnvDouble(string name)
        => double.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            ? Math.Clamp(v, 0, 2) : 0;

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
        new("array_object",
            "Repeat an existing object in a grid (Blender's Array modifier): count_x copies along X and count_y along Y, with a gap between neighbours. Use it to print many copies of a small part so each layer has time to cool. By default the copies are merged into the object, so check_mesh and export_stl see all of them. At most 400 copies.",
            [NameProp,
             new("count_x", "number", "copies along X, 1 to 100, default 1"),
             new("count_y", "number", "copies along Y, 1 to 100, default 1"),
             new("gap", "number", "space between copies (same unit as the scene), default 2"),
             new("apply", "boolean", "merge the copies into the object, default true")],
            ["name"]),
        new("pad",
            "Add material on a face of an existing object, like FreeCAD's Pad: a rectangle on the face is raised by height. axis + side choose the face (Z and + is the top, the default). x1, x2, y1, y2, z1, z2 are the corners of the rectangle: give only the two pairs that lie on that face (face Z: x and y; face X: y and z; face Y: x and z). Safe: a broken result leaves the object unchanged.",
            [NameProp, new("x1", "number", "rectangle corner"), new("x2", "number", "opposite corner"), new("y1", "number", "rectangle corner"), new("y2", "number", "opposite corner"),
             new("z1", "number", "rectangle corner"), new("z2", "number", "opposite corner"), new("height", "number", "how far it rises from the face"),
             new("axis", "string", "face normal: X, Y or Z (default Z)"), new("side", "string", "+ (default) or -: which face of that axis")],
            ["name", "height"]),
        new("pocket",
            "Cut a rectangular recess into a face of an existing object, like FreeCAD's Pocket: a rectangle on the face is cut depth deep. axis + side choose the face (Z and + is the top, the default). x1, x2, y1, y2, z1, z2 are the corners of the rectangle: give only the two pairs that lie on that face (face Z: x and y; face X: y and z; face Y: x and z). Safe: a broken result leaves the object unchanged.",
            [NameProp, new("x1", "number", "rectangle corner"), new("x2", "number", "opposite corner"), new("y1", "number", "rectangle corner"), new("y2", "number", "opposite corner"),
             new("z1", "number", "rectangle corner"), new("z2", "number", "opposite corner"), new("depth", "number", "how deep it cuts into the object"),
             new("axis", "string", "face normal: X, Y or Z (default Z)"), new("side", "string", "+ (default) or -: which face of that axis")],
            ["name", "depth"]),
        new("mirror_object",
            "Mirror an existing object across a plane (Blender's Mirror modifier): draw one half, get the other. axis is the axis the plane is perpendicular to (X default). at = where the plane is (default: the object's own origin). merge welds the vertices that meet on the plane, default true. By default the mirror is applied, so check_mesh and export_stl see both halves.",
            [NameProp, new("axis", "string", "X, Y or Z (default X)"), new("at", "number", "position of the mirror plane along that axis (default: the object origin)"),
             new("merge", "boolean", "weld the vertices on the plane, default true"), new("apply", "boolean", "merge the halves into the object, default true")],
            ["name"]),
        new("join",
            "Join two existing objects into one (union). The other object is merged into the target and removed. Safe: if the result is a broken mesh the target is left unchanged and an error is returned.",
            [new("target", "string", "name of the object that is kept"), new("other", "string", "name of the object to join into it")],
            ["target", "other"]),
        new("subtract",
            "Cut one existing object out of another (difference): the cutter is removed from the target, then the cutter is deleted. Safe: if the result is a broken mesh the target is left unchanged and an error is returned.",
            [new("target", "string", "name of the object that is kept and cut"), new("cutter", "string", "name of the object that does the cutting")],
            ["target", "cutter"]),
        new("intersect",
            "Keep only the part that two existing objects have in common (intersection). The other object is removed. Safe: if the result is a broken mesh the target is left unchanged and an error is returned.",
            [new("target", "string", "name of the object that is kept"), new("other", "string", "name of the other object")],
            ["target", "other"]),
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
                         new OpProp("segments", "number", $"sides of the hole, 8 to {MaxSegments}, default {DefaultSegments}")]).ToArray(),
            ["name", "diameter"]),
        .. FaceOps(),
        // The hexagonal recess that holds a nut so it cannot turn, with the sizes taken from the screw table. Its own tools (and not a
        // pocket with a "sides" number) so a small model only gives the screw size and the position.
        new("nut_trap",
            "Cut the hexagonal recess that holds a nut, from the TOP face of an existing object, so the nut cannot turn. size is the screw size (M2 to M8); the width across the flats and the depth come from the nut table, plus a little room.",
            [NameProp, new("size", "string", "screw size: M2, M2.5, M3, M4, M5, M6 or M8"), X, Y, new("clearance", "number", "extra room around the nut, default 0.2")],
            ["name", "size", "x", "y"]),
        new("nut_trap_bottom",
            "Cut the hexagonal recess that holds a nut, from the BOTTOM face of an existing object (the screw enters from the top and the nut sits underneath), so the nut cannot turn. size is the screw size (M2 to M8).",
            [NameProp, new("size", "string", "screw size: M2, M2.5, M3, M4, M5, M6 or M8"), X, Y, new("clearance", "number", "extra room around the nut, default 0.2")],
            ["name", "size", "x", "y"]),
        // Side holes for a part that stands up (a foot, an ear). The axis is in the tool NAME, not in a parameter: a small model fills
        // an optional "axis" on impulse (and gets it wrong), but it does not pick the wrong tool name.
        new("drill_hole_x",
            "Drill a round hole all the way through an existing object, parallel to the X axis (from the side, for a part that stands up). y and z say where the hole is on that face.",
            [NameProp, new("diameter", "number", "hole diameter"), Y, Z],
            ["name", "diameter", "y", "z"]),
        new("drill_hole_y",
            "Drill a round hole all the way through an existing object, parallel to the Y axis (from the front, for a part that stands up). x and z say where the hole is on that face.",
            [NameProp, new("diameter", "number", "hole diameter"), X, Z],
            ["name", "diameter", "x", "z"]),
        new("screw_hole",
            "Drill a hole for a standard screw size by name, using a table of typical dimensions. kind: clearance (screw passes through), counterbore (plus a recess for a socket head), countersunk (plus a cone for a flat head) insert (blind hole for a heat-set insert; needs the entry point z along the axis) or pilot (the smaller hole the screw cuts its own thread in, as in printed plastic).",
            new OpProp[] { NameProp, new("size", "string", "M2, M2.5, M3, M4, M5, M6 or M8 (default M3)"),
                           new("kind", "string", "clearance (default), counterbore, countersunk, insert or pilot") }
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
        // "name" goes FIRST. llama.cpp turns the schema into a grammar that keeps the property order, and a model writes the
        // name first ("a box called base, 60 by 40..."). With name last, Qwen3-1.7B produced {"name": "base', " } and lost every
        // size. Order carries no meaning for other clients.
        foreach (var p in op.Props.Where(p => p.Name == "name")) props[p.Name] = new JsonObject { ["type"] = p.Type, ["description"] = p.Description };
        foreach (var p in op.Props.Where(p => p.Name != "name")) props[p.Name] = new JsonObject { ["type"] = p.Type, ["description"] = p.Description };
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
        "array_object" => WithObject(args, "name", (lit, a) => BuildArray(lit, a)),
        "pad" => WithObject(args, "name", (lit, a) => BuildFaceRect(lit, a, additive: true)),
        "pocket" => WithObject(args, "name", (lit, a) => BuildFaceRect(lit, a, additive: false)),
        "mirror_object" => WithObject(args, "name", (lit, a) => BuildMirror(lit, a)),
        "join" => BuildBoolean(args, "UNION", "other"),
        "subtract" => BuildBoolean(args, "DIFFERENCE", "cutter"),
        "intersect" => BuildBoolean(args, "INTERSECT", "other"),
        "check_mesh" => WithObject(args, "name", (lit, a) => BuildCheck(lit)),
        "repair_mesh" => WithObject(args, "name", (lit, a) => BuildRepair(lit, a)),
        "delete_object" => WithObject(args, "name", (lit, a) => BuildDelete(lit)),
        "export_stl" => WithObject(args, "name", (lit, a) => BuildExport(lit, a)),
        "drill_hole" => WithObject(args, "name", (lit, a) => BuildDrill(lit, a, null)),
        "pad_right" => WithObject(args, "name", (lit, a) => BuildFaceRect(lit, WithFace(a, "X", "+"), additive: true)),
        "pocket_right" => WithObject(args, "name", (lit, a) => BuildFaceRect(lit, WithFace(a, "X", "+"), additive: false)),
        "pad_left" => WithObject(args, "name", (lit, a) => BuildFaceRect(lit, WithFace(a, "X", "-"), additive: true)),
        "pocket_left" => WithObject(args, "name", (lit, a) => BuildFaceRect(lit, WithFace(a, "X", "-"), additive: false)),
        "pad_front" => WithObject(args, "name", (lit, a) => BuildFaceRect(lit, WithFace(a, "Y", "-"), additive: true)),
        "pocket_front" => WithObject(args, "name", (lit, a) => BuildFaceRect(lit, WithFace(a, "Y", "-"), additive: false)),
        "pad_back" => WithObject(args, "name", (lit, a) => BuildFaceRect(lit, WithFace(a, "Y", "+"), additive: true)),
        "pocket_back" => WithObject(args, "name", (lit, a) => BuildFaceRect(lit, WithFace(a, "Y", "+"), additive: false)),
        "nut_trap" => WithObject(args, "name", (lit, a) => BuildNutTrap(lit, a, top: true)),
        "nut_trap_bottom" => WithObject(args, "name", (lit, a) => BuildNutTrap(lit, a, top: false)),
        "drill_hole_x" => WithObject(args, "name", (lit, a) => BuildDrill(lit, WithAxis(a, "X"), null)),
        "drill_hole_y" => WithObject(args, "name", (lit, a) => BuildDrill(lit, WithAxis(a, "Y"), null)),
        "screw_hole" => WithObject(args, "name", (lit, a) => BuildScrewHole(lit, a)),
        "boolean_op" => BuildBoolean(args),
        "set_units" => BuildUnits(args),
        _ => (null, $"Unknown tool '{op.Tool}'."),
    };

    // ---- shared pieces -------------------------------------------------------------------------------------------

    /// <summary>pad and pocket on the four SIDE faces of a part that stands up (a foot, an ear): right (+X), left (-X), front (-Y: the face
    /// Blender's front view looks at) and back (+Y). The face is in the tool NAME: an optional "side" parameter was filled on impulse by a
    /// small model, which raised a strip on the bottom face. The rectangle is given by the two coordinates that exist on that face.
    /// The top face keeps the plain pad / pocket.</summary>
    private static Op[] FaceOps()
    {
        var faces = new (string Word, string Sign, string A, string B)[]
        {
            ("right", "+X", "y", "z"), ("left", "-X", "y", "z"), ("front", "-Y", "x", "z"), ("back", "+Y", "x", "z"),
        };
        var list = new List<Op>();
        foreach (var (word, sign, a, b) in faces)
            foreach (bool pad in new[] { true, false })
            {
                string size = pad ? "height" : "depth";
                string what = pad ? $"Add material on the {word.ToUpperInvariant()} face ({sign}) of an existing object, for a part that stands up: a rectangle on that face is raised by height."
                                  : $"Cut a rectangular recess into the {word.ToUpperInvariant()} face ({sign}) of an existing object, for a part that stands up: a rectangle on that face is cut depth deep.";
                list.Add(new Op($"{(pad ? "pad" : "pocket")}_{word}",
                    $"{what} {a}1, {a}2 and {b}1, {b}2 are the corners of the rectangle on that face.",
                    [NameProp, new($"{a}1", "number", "rectangle corner"), new($"{a}2", "number", "opposite corner"),
                     new($"{b}1", "number", "rectangle corner"), new($"{b}2", "number", "opposite corner"),
                     new(size, "number", pad ? "how far it rises from the face" : "how deep the recess is")],
                    ["name", $"{a}1", $"{a}2", $"{b}1", $"{b}2", size]));
            }
        return [.. list];
    }

    /// <summary>The same arguments with the face chosen (axis and side), for the pad_* and pocket_* tools.</summary>
    private static JsonElement WithFace(JsonElement args, string axis, string side)
    {
        var node = JsonObject.Create(args) ?? new JsonObject();
        node["axis"] = axis;
        node["side"] = side;
        return JsonSerializer.SerializeToElement(node);
    }

    /// <summary>nut_trap / nut_trap_bottom: a hexagonal recess for a nut, cut from the top or the bottom face. Blender's 6-sided cylinder has its corners on +-Y, so it is
    /// turned 90 degrees: the corners are on +-X and two flat edges are parallel to X. Across the flats = nut width (screw table) + clearance; depth = nut thickness + clearance. The
    /// surface under it is found with rays, like pad and pocket; a part thinner than the recess is refused.</summary>
    private static (string? Script, string? Error) BuildNutTrap(string lit, JsonElement args, bool top)
    {
        string size = (ShapeScripts.Text(args, "size") ?? "M3").Trim().ToUpperInvariant();
        var (table, _) = Screws.Load();
        if (!table.TryGetValue(size, out var row))
            return (null, $"Unknown screw size '{size}'. Known sizes: {string.Join(", ", table.Keys.OrderBy(k => k))}.");
        if (!row.TryGetValue("nut_flats", out double flats) || !row.TryGetValue("nut_thickness", out double thick))
            return (null, $"The screw table has no nut dimensions (nut_flats, nut_thickness) for {size}.");
        var (clr, ec) = ShapeScripts.Number(args, "clearance", 0.2, 0, 2); if (ec is not null) return (null, ec);
        var (x, ex) = ShapeScripts.Number(args, "x", 0, -CoordLimit, CoordLimit); if (ex is not null) return (null, ex);
        var (y, ey) = ShapeScripts.Number(args, "y", 0, -CoordLimit, CoordLimit); if (ey is not null) return (null, ey);
        double across = flats + clr + NutComp, radius = across / Math.Sqrt(3), depth = thick + clr;   // radius: corner of the hexagon; NutComp: printer compensation

        string face = top ? "top" : "bottom";
        var sb = new StringBuilder(PyHelpers).Append('\n');
        sb.Append($"o = bpy.data.objects.get({lit})\n");
        sb.Append("if o is None:\n").Append(Missing(lit)).Append('\n');
        sb.Append("elif o.type != 'MESH':\n");
        sb.Append("    print(\"ERROR: \" + o.name + \" is \" + o.type + \"; a nut trap needs a MESH object\")\n");
        sb.Append("else:\n");
        sb.Append("    import math\n");
        sb.Append("    bpy.context.view_layer.update()\n");
        sb.Append("    cs = [o.matrix_world @ Vector(c) for c in o.bound_box]\n");
        sb.Append("    lo = min(c[2] for c in cs); hi = max(c[2] for c in cs)\n");
        sb.Append($"    d, x, y, R, depth = {(top ? 1 : -1)}, {N(x)}, {N(y)}, {N(radius)}, {N(depth)}\n");
        sb.Append("    if depth >= hi - lo:\n");
        sb.Append("        print(\"ERROR: the part is only \" + str(round(hi - lo, 4)) + \" thick: a nut trap \" + str(round(depth, 4)) + \" deep would cut through it\")\n");
        sb.Append("    else:\n");
        sb.Append("        inv = o.matrix_world.inverted()\n");
        sb.Append("        dloc = (inv.to_3x3() @ Vector((0.0, 0.0, -float(d)))).normalized()\n");
        sb.Append("        far = (hi + 1.0) if d > 0 else (lo - 1.0)\n");
        sb.Append("        pts = [(x, y)] + [(x + 0.9 * R * math.cos(math.radians(60 * k)), y + 0.9 * R * math.sin(math.radians(60 * k))) for k in range(6)]\n");
        sb.Append("        hits = []\n");
        sb.Append("        for px, py in pts:\n");
        sb.Append("            ok, loc_hit, nrm_hit, idx_hit = o.ray_cast(inv @ Vector((px, py, far)), dloc)\n");
        sb.Append("            if ok: hits.append((o.matrix_world @ loc_hit)[2])\n");
        sb.Append("        if not hits:\n");
        sb.Append($"            print(\"ERROR: the nut trap does not touch the {face} face: there is no surface of the object under (\" + str(round(x, 4)) + \", \" + str(round(y, 4)) + \")\")\n");
        sb.Append("        else:\n");
        sb.Append("            plane = max(hits) if d > 0 else min(hits)\n");
        sb.Append("            ov = min(0.2, (hi - lo) * 0.25)\n");
        sb.Append("            bpy.ops.mesh.primitive_cylinder_add(vertices=6, radius=R, depth=depth + ov, location=(x, y, plane + d * (ov - depth) / 2), rotation=(0.0, 0.0, math.radians(90)))\n");
        sb.Append("            c = bpy.context.active_object\n");
        sb.Append("            bad0, vol0, n0 = bm_check(o)\n");
        sb.Append("            err = bm_boolean(o, c, 'DIFFERENCE')\n");
        sb.Append("            cm = c.data\n");
        sb.Append("            bpy.data.objects.remove(c, do_unlink=True)\n");
        sb.Append("            bpy.data.meshes.remove(cm)\n");
        sb.Append("            bpy.context.view_layer.objects.active = o\n");
        sb.Append("            if err:\n                print(\"ERROR: \" + err)\n");
        sb.Append("            else:\n");
        sb.Append("                bad, vol, n = bm_check(o)\n");
        sb.Append("                full = 2.598076211353316 * R * R * depth\n");
        sb.Append("                if abs(vol - vol0) < max(1e-6 * abs(vol0), 1e-9):\n");
        sb.Append("                    print(\"ERROR: nothing was cut: the nut trap does not overlap the part\")\n");
        sb.Append("                else:\n");
        sb.Append("                    note = (\" (warning: the nut trap only partly overlaps the part: it removed %.1f of the %.1f mm3 a whole trap removes; check x and y)\" % (vol0 - vol, full)) if (vol0 - vol) < 0.5 * full else \"\"\n");
        sb.Append($"                    print(\"OK \" + o.name + \" nut trap {size} ({N(Math.Round(across, 4))} across the flats, {N(Math.Round(depth, 4))} deep) at (\" + str(round(x, 4)) + \", \" + str(round(y, 4)) + \") in the {face} face, which is at z=\" + str(round(plane, 4)) + \" vertices=\" + str(n) + \" volume=\" + str(round(vol, 4)) + note)");
        return (sb.ToString(), null);
    }

    /// <summary>The same arguments with "axis" set (drill_hole_x / drill_hole_y reuse the code of drill_hole).</summary>
    private static JsonElement WithAxis(JsonElement args, string axis)
    {
        var node = JsonObject.Create(args) ?? new JsonObject();
        node["axis"] = axis;
        return JsonSerializer.SerializeToElement(node);
    }

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
        var (script, err) = ShapeScripts.Build(ShapeScripts.Find("create_cube")!, doc.RootElement, scale);
        // A NAME THE USER GAVE that is already taken is an error, not a silent "base.001": a small model that creates the box twice
        // (once per hole) would then drill the first one and think it is done. Without a name Blender numbers boxes as usual.
        string? given = ShapeScripts.Text(args, "name");
        if (err is not null || script is null || string.IsNullOrWhiteSpace(given)) return (script, err);
        string lit = JsonSerializer.Serialize(given);
        var sb = new StringBuilder("import bpy\n");
        sb.Append($"if bpy.data.objects.get({lit}) is not None:\n");
        sb.Append($"    print(\"ERROR: an object named \" + {lit} + \" already exists. Pick another name, or change that object (drill_hole, pad, pocket...) instead of creating it again.\")\n");
        sb.Append("else:\n");
        foreach (var line in script.Split('\n')) sb.Append("    ").Append(line).Append('\n');
        return (sb.ToString().TrimEnd('\n'), null);
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

    /// <summary>Repeats an object in a grid with two Array modifiers (X, then Y). The offset is RELATIVE to the part's own size:
    /// factor = 1 + gap / size, so the real distance between copies is size + gap whatever the object's scale. With apply
    /// (default) the modifiers are baked in order, so the copies become real geometry for check_mesh and export_stl.</summary>
    private static (string? Script, string? Error) BuildArray(string lit, JsonElement args)
    {
        var (cx, ex) = ShapeScripts.Number(args, "count_x", 1, 1, 100); if (ex is not null) return (null, ex);
        var (cy, ey) = ShapeScripts.Number(args, "count_y", 1, 1, 100); if (ey is not null) return (null, ey);
        var (gap, eg) = ShapeScripts.Number(args, "gap", 2, 0, 1000); if (eg is not null) return (null, eg);
        var (apply, ea) = ReadBool(args, "apply", true); if (ea is not null) return (null, ea);
        int nx = (int)Math.Round(cx), ny = (int)Math.Round(cy);
        if (nx * ny < 2) return (null, "count_x and count_y are both 1: there is nothing to repeat. Pass count_x and/or count_y.");
        if (nx * ny > 400) return (null, $"{nx} x {ny} = {nx * ny} copies is too many (limit 400).");
        string gs = N(gap), ap = apply ? "True" : "False";

        var sb = new StringBuilder(PyHelpers).Append('\n');
        sb.Append($"o = bpy.data.objects.get({lit})\n");
        sb.Append("if o is None:\n").Append(Missing(lit)).Append('\n');
        sb.Append("elif o.type != 'MESH':\n");
        sb.Append("    print(\"ERROR: \" + o.name + \" is \" + o.type + \"; array_object needs a MESH object\")\n");
        sb.Append("else:\n");
        sb.Append("    bpy.context.view_layer.update()\n");
        sb.Append("    dx, dy = o.dimensions.x, o.dimensions.y\n");
        sb.Append("    if dx < 1e-9 or dy < 1e-9:\n");
        sb.Append("        print(\"ERROR: \" + o.name + \" has no size along X or Y\")\n");
        sb.Append("    else:\n");
        sb.Append("        for old in [m for m in o.modifiers if m.name.startswith('BM_Array')]: o.modifiers.remove(old)\n");
        sb.Append($"        gap = {gs}\n");
        sb.Append($"        if {nx.ToString(CultureInfo.InvariantCulture)} > 1:\n");
        sb.Append("            m = o.modifiers.new('BM_ArrayX', 'ARRAY')\n");
        sb.Append($"            m.count = {nx.ToString(CultureInfo.InvariantCulture)}\n");
        sb.Append("            m.use_relative_offset = True\n");
        sb.Append("            m.relative_offset_displace = (1 + gap / dx, 0, 0)\n");
        sb.Append($"        if {ny.ToString(CultureInfo.InvariantCulture)} > 1:\n");
        sb.Append("            m = o.modifiers.new('BM_ArrayY', 'ARRAY')\n");
        sb.Append($"            m.count = {ny.ToString(CultureInfo.InvariantCulture)}\n");
        sb.Append("            m.use_relative_offset = True\n");
        sb.Append("            m.relative_offset_displace = (0, 1 + gap / dy, 0)\n");
        sb.Append($"        if {ap}:\n");
        sb.Append("            with bpy.context.temp_override(object=o, active_object=o, selected_objects=[o]):\n");
        sb.Append("                for m in list(o.modifiers): bpy.ops.object.modifier_apply(modifier=m.name)\n");
        sb.Append("        bpy.context.view_layer.update()\n");
        sb.Append("        size = tuple(round(v, 3) for v in o.dimensions)\n");
        sb.Append($"        txt = \"OK \" + o.name + \" array {nx.ToString(CultureInfo.InvariantCulture)} x {ny.ToString(CultureInfo.InvariantCulture)} = {(nx * ny).ToString(CultureInfo.InvariantCulture)} copies, gap {gs}, total size \" + str(size)\n");
        sb.Append($"        if {ap}:\n");
        sb.Append("            bad, vol, n = bm_check(o)\n");
        sb.Append("            txt += \" volume=%.4f non_manifold_edges=%d\" % (vol, bad)\n");
        sb.Append("        else:\n");
        sb.Append("            txt += \" (modifiers kept, not applied yet)\"\n");
        sb.Append("        print(txt)");
        return (sb.ToString(), null);
    }

    /// <summary>pad (additive) and pocket (subtractive): a rectangle on one face of an object, raised or cut. Same result as Ctrl+R,
    /// selecting the new face and extruding it, but with numbers only: a box is built over (pad) or into (pocket) the face and
    /// joined or cut with the TRANSACTIONAL boolean, so a bad result leaves the object as it was. The face plane comes from the
    /// object's bounding box in world space (axis-aligned objects). The box sticks a little into the part (pad) or out of the face
    /// (pocket) so the faces never coincide.</summary>
    private static (string? Script, string? Error) BuildFaceRect(string lit, JsonElement args, bool additive)
    {
        string tool = additive ? "pad" : "pocket", sizeKey = additive ? "height" : "depth";
        string axis = (ShapeScripts.Text(args, "axis") ?? "Z").ToUpperInvariant();
        if (axis is not ("X" or "Y" or "Z")) return (null, $"Invalid axis '{axis}'. Use X, Y or Z.");
        int ai = axis[0] - 'X';
        string sideText = (ShapeScripts.Text(args, "side") ?? "+").ToLowerInvariant();
        int d = sideText switch { "+" or "top" or "pos" or "positive" => 1, "-" or "bottom" or "neg" or "negative" => -1, _ => 0 };
        if (d == 0) return (null, $"Invalid side '{sideText}'. Use + or - (which face of the {axis} axis).");
        if (!ShapeScripts.Has(args, sizeKey)) return (null, $"Argument '{sizeKey}' is required.");
        var (size, es) = ShapeScripts.Number(args, sizeKey, 0, 0.001, 10000); if (es is not null) return (null, es);

        // the two axes that lie on the face, and the four keys that give the rectangle
        int ia = ai == 0 ? 1 : 0, ib = ai == 2 ? 1 : 2;
        string[] keys = ai switch
        {
            0 => ["y1", "y2", "z1", "z2"],
            1 => ["x1", "x2", "z1", "z2"],
            _ => ["x1", "x2", "y1", "y2"],
        };
        var v = new double[4];
        for (int i = 0; i < 4; i++)
        {
            if (!ShapeScripts.Has(args, keys[i]))
                return (null, $"For a face on the {axis} axis give {keys[0]}, {keys[1]}, {keys[2]} and {keys[3]} (the corners of the rectangle on the face).");
            var (val, ev) = ShapeScripts.Number(args, keys[i], 0, -CoordLimit, CoordLimit); if (ev is not null) return (null, ev);
            v[i] = val;
        }
        double a1 = Math.Min(v[0], v[1]), a2 = Math.Max(v[0], v[1]), b1 = Math.Min(v[2], v[3]), b2 = Math.Max(v[2], v[3]);
        if (a2 - a1 < 1e-6 || b2 - b1 < 1e-6) return (null, $"The rectangle has no size: {keys[0]} and {keys[1]}, and {keys[2]} and {keys[3]}, must be different.");

        var sb = new StringBuilder(PyHelpers).Append('\n');
        sb.Append($"o = bpy.data.objects.get({lit})\n");
        sb.Append("if o is None:\n").Append(Missing(lit)).Append('\n');
        sb.Append("elif o.type != 'MESH':\n");
        sb.Append($"    print(\"ERROR: \" + o.name + \" is \" + o.type + \"; {tool} needs a MESH object\")\n");
        sb.Append("else:\n");
        sb.Append("    bpy.context.view_layer.update()\n");
        sb.Append("    cs = [o.matrix_world @ Vector(c) for c in o.bound_box]\n");
        sb.Append("    lo = [min(c[i] for c in cs) for i in range(3)]\n");
        sb.Append("    hi = [max(c[i] for c in cs) for i in range(3)]\n");
        sb.Append($"    ai, ia, ib, d = {ai}, {ia}, {ib}, {d}\n");
        sb.Append($"    a1, a2, b1, b2, size = {N(a1)}, {N(a2)}, {N(b1)}, {N(b2)}, {N(size)}\n");
        // The face plane is the REAL surface under the rectangle (rays cast along the axis at the centre and the four corners),
        // not the top of the whole bounding box: after a pad the box top is higher than the surface a pocket must start from.
        sb.Append("    inv = o.matrix_world.inverted()\n");
        sb.Append("    dirv = Vector((0.0, 0.0, 0.0)); dirv[ai] = -float(d)\n");
        sb.Append("    dloc = (inv.to_3x3() @ dirv).normalized()\n");
        sb.Append("    far = (hi[ai] + 1.0) if d > 0 else (lo[ai] - 1.0)\n");
        sb.Append("    eps = min(a2 - a1, b2 - b1) * 0.05\n");
        sb.Append("    hits = []\n");
        sb.Append("    for fa, fb in ((0.5, 0.5), (0.0, 0.0), (1.0, 0.0), (0.0, 1.0), (1.0, 1.0)):\n");
        sb.Append("        p = [0.0, 0.0, 0.0]\n");
        sb.Append("        p[ia] = a1 + (a2 - a1) * fa + (eps if fa == 0.0 else (-eps if fa == 1.0 else 0.0))\n");
        sb.Append("        p[ib] = b1 + (b2 - b1) * fb + (eps if fb == 0.0 else (-eps if fb == 1.0 else 0.0))\n");
        sb.Append("        p[ai] = far\n");
        sb.Append("        ok, loc_hit, nrm_hit, idx_hit = o.ray_cast(inv @ Vector(p), dloc)\n");
        sb.Append("        if ok: hits.append((o.matrix_world @ loc_hit)[ai])\n");
        sb.Append("    no_hit = not hits\n");
        sb.Append("    plane = (max(hits) if d > 0 else min(hits)) if hits else 0.0\n");
        sb.Append("    ov = min(0.2, (hi[ai] - lo[ai]) * 0.25)\n");
        sb.Append(additive
            ? "    length = size + ov\n    mid = plane + d * (size - ov) / 2\n"
            : "    length = size + ov\n    mid = plane + d * (ov - size) / 2\n");
        sb.Append("    dims = [0.0, 0.0, 0.0]; loc = [0.0, 0.0, 0.0]\n");
        sb.Append("    dims[ai] = length; loc[ai] = mid\n");
        sb.Append("    dims[ia] = a2 - a1; loc[ia] = (a1 + a2) / 2\n");
        sb.Append("    dims[ib] = b2 - b1; loc[ib] = (b1 + b2) / 2\n");
        sb.Append("    outside = a1 < lo[ia] - 1e-6 or a2 > hi[ia] + 1e-6 or b1 < lo[ib] - 1e-6 or b2 > hi[ib] + 1e-6\n");
        sb.Append("    bpy.ops.mesh.primitive_cube_add(size=1, location=tuple(loc))\n");
        sb.Append("    c = bpy.context.active_object\n");
        sb.Append("    c.scale = tuple(dims)\n");
        sb.Append("    bad0, vol0, n0 = bm_check(o)\n");
        sb.Append($"    err = ('the rectangle does not touch the ' + 'XYZ'[ai] + ('+' if d > 0 else '-') + ' face: there is no surface of the object under it' if no_hit else bm_boolean(o, c, '{(additive ? "UNION" : "DIFFERENCE")}'))\n");
        sb.Append("    cm = c.data\n");
        sb.Append("    bpy.data.objects.remove(c, do_unlink=True)\n");
        sb.Append("    bpy.data.meshes.remove(cm)\n");
        sb.Append("    bpy.context.view_layer.objects.active = o\n");
        sb.Append("    if err:\n        print(\"ERROR: \" + err)\n");
        sb.Append("    else:\n");
        sb.Append("        bad, vol, n = bm_check(o)\n");
        // A tool that changed nothing must not say OK: a small model would believe the part was modified.
        sb.Append("        if abs(vol - vol0) < max(1e-6 * abs(vol0), 1e-9):\n");
        sb.Append($"            print(\"ERROR: nothing was {(additive ? "added" : "cut")}: the rectangle does not overlap the part at that face (the surface there is at \" + str(round(plane, 4)) + \")\")\n");
        sb.Append("        else:\n");
        sb.Append($"            note = \" (note: the rectangle goes beyond the face)\" if outside else \"\"\n");
        sb.Append($"            print(\"OK \" + o.name + \" {tool} \" + 'XYZ'[ai] + ('+' if d > 0 else '-') + \" face at \" + str(round(plane, 4)) + \", {sizeKey} \" + str(size) + \" vertices=\" + str(n) + \" volume=\" + str(round(vol, 4)) + note)");
        return (sb.ToString(), null);
    }

    /// <summary>Mirror modifier across a plane perpendicular to X, Y or Z. The plane is the object's origin, or an Empty placed at "at".</summary>
    private static (string? Script, string? Error) BuildMirror(string lit, JsonElement args)
    {
        string axis = (ShapeScripts.Text(args, "axis") ?? "X").ToUpperInvariant();
        if (axis is not ("X" or "Y" or "Z")) return (null, $"Invalid axis '{axis}'. Use X, Y or Z.");
        int ai = axis[0] - 'X';
        bool hasAt = ShapeScripts.Has(args, "at");
        double at = 0;
        if (hasAt) { var (av, ea) = ShapeScripts.Number(args, "at", 0, -CoordLimit, CoordLimit); if (ea is not null) return (null, ea); at = av; }
        var (merge, em) = ReadBool(args, "merge", true); if (em is not null) return (null, em);
        var (apply, ep) = ReadBool(args, "apply", true); if (ep is not null) return (null, ep);

        var sb = new StringBuilder(PyHelpers).Append('\n');
        sb.Append($"o = bpy.data.objects.get({lit})\n");
        sb.Append("if o is None:\n").Append(Missing(lit)).Append('\n');
        sb.Append("elif o.type != 'MESH':\n");
        sb.Append("    print(\"ERROR: \" + o.name + \" is \" + o.type + \"; mirror_object needs a MESH object\")\n");
        sb.Append("else:\n");
        sb.Append("    for old in [m for m in o.modifiers if m.name == 'BM_Mirror']: o.modifiers.remove(old)\n");
        sb.Append("    m = o.modifiers.new('BM_Mirror', 'MIRROR')\n");
        sb.Append($"    m.use_axis = ({(ai == 0 ? "True" : "False")}, {(ai == 1 ? "True" : "False")}, {(ai == 2 ? "True" : "False")})\n");
        sb.Append($"    m.use_mirror_merge = {(merge ? "True" : "False")}\n");
        sb.Append("    m.merge_threshold = 0.001\n");
        sb.Append("    plane = None\n");
        if (hasAt)
        {
            sb.Append("    plane = bpy.data.objects.new('_bm_mirror_plane', None)\n");
            sb.Append("    bpy.context.scene.collection.objects.link(plane)\n");
            sb.Append($"    loc = [0.0, 0.0, 0.0]; loc[{ai}] = {N(at)}\n");
            sb.Append("    plane.location = tuple(loc)\n");
            sb.Append("    m.mirror_object = plane\n");
        }
        if (apply)
        {
            sb.Append("    with bpy.context.temp_override(object=o, active_object=o, selected_objects=[o]):\n");
            sb.Append("        for mm in list(o.modifiers): bpy.ops.object.modifier_apply(modifier=mm.name)\n");
        }
        sb.Append("    if plane is not None: bpy.data.objects.remove(plane, do_unlink=True)\n");
        sb.Append("    bpy.context.view_layer.update()\n");
        sb.Append("    size = tuple(round(x, 3) for x in o.dimensions)\n");
        sb.Append($"    txt = \"OK \" + o.name + \" mirrored across {axis}\" + (\" at {N(at)}\" if {(hasAt ? "True" : "False")} else \" at its origin\") + \", size \" + str(size)\n");
        sb.Append($"    if {(apply ? "True" : "False")}:\n");
        sb.Append("        bad, vol, n = bm_check(o)\n");
        sb.Append("        txt += \" volume=%.4f non_manifold_edges=%d\" % (vol, bad)\n");
        sb.Append("    else:\n");
        sb.Append("        txt += \" (modifier kept, not applied yet)\"\n");
        sb.Append("    print(txt)");
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
        // Blender 4.0+ has the built-in operator wm.stl_export; Blender 3.x only has the bundled STL add-on (export_mesh.stl).
        // Do NOT test with hasattr(bpy.ops...): bpy.ops answers for any name. bpy.types is a real registry.
        sb.Append("    if hasattr(bpy.types, 'WM_OT_stl_export'):\n");
        sb.Append("        bpy.ops.wm.stl_export(filepath=path, export_selected_objects=True, apply_modifiers=True)\n");
        sb.Append("    else:\n");
        sb.Append("        import addon_utils\n");
        sb.Append("        addon_utils.enable('io_mesh_stl', default_set=False, persistent=False)\n");
        sb.Append("        bpy.ops.export_mesh.stl(filepath=path, use_selection=True, use_mesh_modifiers=True)\n");
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

    /// <summary>fixedOp is set by join / subtract / intersect: the TOOL carries the operation, so a small model never has to pick
    /// UNION / DIFFERENCE / INTERSECT from a parameter. boolean_op keeps the parameter for clients that want it.</summary>
    private static (string? Script, string? Error) BuildBoolean(JsonElement args, string? fixedOp = null, string otherKey = "cutter")
    {
        var (tl, et) = NameLiteral(args, "target"); if (et is not null) return (null, et);
        var (cl, ec) = NameLiteral(args, otherKey); if (ec is not null) return (null, ec);
        string op = fixedOp ?? "DIFFERENCE";
        if (fixedOp is null && ShapeScripts.Has(args, "operation"))
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
        clr += HoleComp;   // printer compensation (server setting): goes into the hole, the counterbore and the countersink alike
        var (segf, esg) = ShapeScripts.Number(args, "segments", DefaultSegments, 8, MaxSegments); if (esg is not null) return (null, esg);
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
        sb.Append("    bad0, vol0, n0 = bm_check(t)\n");   // volume before: a hole that removed nothing must not say OK
        sb.Append("    center = list(pos)\n");
        sb.Append("    center[ai] = (start + end) / 2\n");
        // A hole exactly tangent to a face puts a vertex of the cutter in the plane of that face, and the boolean can give a bad
        // mesh (non-manifold). The boolean is transactional (a bad result leaves the part as it was), so try again with the cutter
        // 0.02% wider, then 0.02% narrower: a sliver of 0.06 mm or less, far below what a printer can make, and the mesh stays closed.
        sb.Append("    nudge = ''\n");
        sb.Append("    for k in (1.0, 1.0002, 0.9998):\n");
        sb.Append($"        bpy.ops.mesh.primitive_cylinder_add(vertices={seg}, radius={N(holeR)} * k, depth=abs(start - end), location=tuple(center), rotation={rot})\n");
        sb.Append("        cutter = bpy.context.active_object\n");
        sb.Append("        cutter.name = 'BM_hole_cutter'\n");
        sb.Append("        err = bm_boolean(t, cutter, 'DIFFERENCE')\n");
        sb.Append("        bpy.data.objects.remove(cutter, do_unlink=True)\n");
        sb.Append("        if k != 1.0 and not err:\n            nudge = ' (the hole touches a face exactly: the cutter was ' + ('widened' if k > 1 else 'narrowed') + ' by 0.02% so that the mesh stays closed)'\n");
        sb.Append("        if not (err and 'bad mesh' in err):\n            break\n");
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
                recess = $" counterbore {N(Math.Round(cbD + clr, 4))}x{N(cbH)}";
            }
            else
            {
                double csR = (csD + clr) / 2;
                double h = csR - holeR;   // 90 degree cone: the radius grows 1:1 with the depth
                sb.Append($"        c2[ai] = e + d * ({N(m2)} - {N(h)}) / 2\n");
                sb.Append($"        bpy.ops.mesh.primitive_cone_add(vertices={seg}, radius1={N(holeR)}, radius2={N(csR + m2)}, depth={N(h + m2)}, location=tuple(c2), rotation={coneRot})\n");
                recess = $" countersink {N(Math.Round(csD + clr, 4))}";
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
        // A hole that touches nothing changed nothing: say so, and say where the part is.
        sb.Append("        if abs(vol - vol0) < max(1e-6 * abs(vol0), 1e-9):\n");
        sb.Append("            span = ', '.join('XYZ'[i] + ' ' + str(round(min(c[i] for c in corners), 2)) + ' to ' + str(round(max(c[i] for c in corners), 2)) for i in range(3))\n");
        sb.Append($"            print(\"ERROR: nothing was cut: the hole at {where} does not touch the part. The part spans \" + span + \". Give x and y inside it.\")\n");
        sb.Append("        else:\n");
        // A hole that only grazes the part: warn (not an error: a hole through an existing pocket legitimately removes less).
        if (!hasCb && !hasCs)
        {
            string length = through ? "(hi - lo)" : N(depth);
            sb.Append($"            full = 3.14159265 * {N(holeR)} ** 2 * {length}\n");
            sb.Append("            note = (\" (warning: the hole only partly overlaps the part: it removed %.1f of the %.1f mm3 a full hole removes; check x and y)\" % (vol0 - vol, full)) if (vol0 - vol) < 0.5 * full else \"\"\n");
        }
        else
            sb.Append("            note = \"\"\n");
        sb.Append($"            print(\"OK \" + t.name + \" hole diameter {N(Math.Round(dia + clr, 4))} at {where} axis {axis} {kind}{recess}{tag} vertices=\" + str(n) + \" volume=\" + str(round(vol, 4)) + nudge + note)");
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
            case "pilot":   // the hole a screw cuts its own thread in (printed plastic): the standard tap drill, blind with "depth" or through
                if (Get("pilot_diameter", out double pilot) is { } e8) return (null, e8);
                o["diameter"] = pilot;
                break;
            case "insert":
                if (Get("insert_diameter", out insD) is { } e6) return (null, e6);
                if (Get("insert_depth", out insH) is { } e7) return (null, e7);
                o["diameter"] = insD;
                if (!o.ContainsKey("depth")) o["depth"] = insH;
                break;
            default:
                return (null, $"Invalid kind '{kind}'. Use clearance, counterbore, countersunk, insert or pilot.");
        }
        using var doc = JsonDocument.Parse(o.ToJsonString());
        return BuildDrill(lit, doc.RootElement, $"{size} {kind}");
    }
}
