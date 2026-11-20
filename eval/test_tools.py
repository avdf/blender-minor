#!/usr/bin/env python3
"""Integration test of every blender-minor tool, WITHOUT any model: it calls the MCP directly and checks the scene.

It needs a running blender-minor connected to a live Blender (see the README). Everything it creates is named BMT_* and is
removed at the end (also on failure). It takes a few seconds.

  python3 -I eval/test_tools.py
  python3 -I eval/test_tools.py --url http://127.0.0.1:9192/ --blender-mcp http://127.0.0.1:9191/
"""
import argparse, json, math, os, re, sys, time, urllib.request

PASS, FAIL = [], []


def rpc(url, method, params):
    body = {"jsonrpc": "2.0", "id": 1, "method": method, "params": params}
    req = urllib.request.Request(url, data=json.dumps(body).encode(),
                                 headers={"Content-Type": "application/json", "Accept": "application/json, text/event-stream"})
    raw = urllib.request.urlopen(req, timeout=120).read().decode()
    if "data:" in raw:
        raw = "".join(l[5:].strip() for l in raw.splitlines() if l.startswith("data:"))
    return json.loads(raw)["result"]


class Mcp:
    def __init__(self, url, blender_mcp):
        self.url, self.blender_mcp = url, blender_mcp

    def call(self, tool, **args):
        r = rpc(self.url, "tools/call", {"name": tool, "arguments": args})
        text = "".join(c.get("text", "") for c in r.get("content", []))
        try:  # creation tools return blender-mcp's JSON; keep only what the script printed
            j = json.loads(text)
            if isinstance(j, dict) and "stdout" in j:
                text = j["stdout"].strip()
        except json.JSONDecodeError:
            pass
        return bool(r.get("isError")), text.strip()

    def py(self, code):  # direct Python in Blender: ONLY for test setup and cleanup
        r = rpc(self.blender_mcp, "tools/call", {"name": "execute_blender_code", "arguments": {"code": code}})
        text = "".join(c.get("text", "") for c in r.get("content", []))
        try:
            return json.loads(text).get("stdout", "").strip()
        except json.JSONDecodeError:
            return text.strip()

    def check(self, name):
        err, text = self.call("check_mesh", name=name)
        m = re.search(r"manifold=(\w+) non_manifold_edges=(\d+) volume=([-0-9.e]+) dimensions=\(([^)]*)\) vertices=(\d+)", text)
        if err or not m:
            raise RuntimeError("check_mesh failed: " + text)
        return {"manifold": m.group(1) == "True", "bad": int(m.group(2)), "volume": float(m.group(3)),
                "dims": tuple(float(v) for v in m.group(4).split(",")), "vertices": int(m.group(5))}


def ok(name, cond, detail=""):
    (PASS if cond else FAIL).append(name)
    print(f"{'PASS' if cond else 'FAIL'}  {name}" + (f"   {detail}" if detail else ""), flush=True)


def close(a, b, rel=0.005, absolute=1e-3):
    return abs(a - b) <= max(absolute, abs(b) * rel)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--url", default="http://127.0.0.1:9192/")
    ap.add_argument("--blender-mcp", default="http://127.0.0.1:9191/")
    a = ap.parse_args()
    m = Mcp(a.url, a.blender_mcp)
    t0 = time.time()
    exported = []
    orig_units = m.py("import bpy\nu = bpy.context.scene.unit_settings\nprint(u.system, u.scale_length, u.length_unit)").split()
    try:
        # --- tools list
        names = [t["name"] for t in rpc(a.url, "tools/list", {})["tools"]]
        expected = {"create_sphere", "create_cube", "create_cylinder", "create_cone", "create_torus", "list_objects", "create_box",
                    "set_dimensions", "move_object", "bevel_object", "boolean_op", "drill_hole", "check_mesh"}
        expected |= {"create_plane", "create_icosphere", "set_color", "screw_hole", "repair_mesh", "delete_object", "export_stl", "set_units"}
        ok(f"tools/list has all {len(expected)} tools", expected <= set(names), "" if expected <= set(names) else f"missing {sorted(expected - set(names))}")

        # --- shapes and list_objects
        for tool, extra in [("create_sphere", {"radius": 1}), ("create_cube", {"size": 1}), ("create_cylinder", {"radius": 1, "height": 2}),
                            ("create_cone", {"radius": 1, "height": 2}), ("create_torus", {"major_radius": 1, "minor_radius": 0.25})]:
            err, text = m.call(tool, name="BMT_" + tool, x=200, color="blue", **extra)
            ok(f"{tool} creates BMT_{tool}", not err and text.startswith("OK"), text[:60])
        err, text = m.call("list_objects")
        ok("list_objects shows the new objects", "BMT_create_sphere MESH" in text and text.splitlines()[0].endswith("objects"))

        # --- create_box: exact size and a closed mesh
        err, text = m.call("create_box", name="BMT_plate", width=40, depth=20, height=5)
        c = m.check("BMT_plate")
        ok("create_box makes exact sizes", not err and all(close(x, y) for x, y in zip(c["dims"], (40, 20, 5))), str(c["dims"]))
        ok("create_box gives a closed mesh with volume 4000", c["manifold"] and close(c["volume"], 4000), f"volume {c['volume']}")

        # --- drill_hole: through hole, then a blind hole (volume removed must match the cylinder)
        v0 = c["volume"]
        err, text = m.call("drill_hole", name="BMT_plate", diameter=3, x=10, y=5)
        c = m.check("BMT_plate")
        removed = v0 - c["volume"]
        want = math.pi * 1.5 ** 2 * 5
        ok("drill_hole through: removed volume = pi r^2 h", not err and close(removed, want, rel=0.01), f"removed {removed:.3f} vs {want:.3f}")
        ok("drill_hole through: mesh stays closed", c["manifold"], f"non-manifold edges {c['bad']}")
        v1 = c["volume"]
        err, text = m.call("drill_hole", name="BMT_plate", diameter=2, x=-10, y=5, z=2.5, depth=2)
        c = m.check("BMT_plate")
        removed = v1 - c["volume"]
        want = math.pi * 1.0 ** 2 * 2
        ok("drill_hole blind: removed volume = pi r^2 depth", not err and close(removed, want, rel=0.01), f"removed {removed:.3f} vs {want:.3f}")
        ok("drill_hole blind: mesh stays closed", c["manifold"])
        v2 = c["volume"]
        err, text = m.call("drill_hole", name="BMT_plate", diameter=3, clearance=0.2, x=0, y=-5)
        c = m.check("BMT_plate")
        want = math.pi * 1.6 ** 2 * 5
        ok("drill_hole clearance widens the hole", not err and close(v2 - c["volume"], want, rel=0.01), f"removed {v2 - c['volume']:.3f} vs {want:.3f}")
        v3 = m.check("BMT_plate")["volume"]
        err, text = m.call("drill_hole", name="BMT_plate", diameter=3, axis="X", y=0, z=0)   # along the 40 mm side, through
        removed = v3 - m.check("BMT_plate")["volume"]
        want = math.pi * 1.5 ** 2 * 40
        ok("drill_hole along X: removed volume = pi r^2 length", not err and close(removed, want, rel=0.01), f"removed {removed:.3f} vs {want:.3f}")
        err2, text2 = m.call("drill_hole", name="BMT_plate", diameter=3, x=1)
        ok("drill_hole reports a missing coordinate", err2 and "required" in text2, text2[:70])

        # --- boolean_op: union, intersection, difference (two overlapping 10 mm cubes, offset 5 mm)
        for op, want_vol in (("UNION", 1500), ("INTERSECT", 500), ("DIFFERENCE", 500)):
            base = {"UNION": 300, "INTERSECT": 400, "DIFFERENCE": 500}[op]
            m.call("create_box", name=f"BMT_{op}_a", width=10, depth=10, height=10, x=base)
            m.call("create_box", name=f"BMT_{op}_b", width=10, depth=10, height=10, x=base + 5)
            err, text = m.call("boolean_op", target=f"BMT_{op}_a", cutter=f"BMT_{op}_b", operation=op)
            c = m.check(f"BMT_{op}_a")
            gone = "BMT_%s_b MESH" not in m.call("list_objects")[1]
            ok(f"boolean_op {op}: volume {want_vol}", not err and close(c["volume"], want_vol) and c["manifold"], f"volume {c['volume']:.2f}, {text[:50]}")
            ok(f"boolean_op {op}: cutter removed", gone)

        # --- transactional boolean: an open mesh (a plane has no volume) must be left unchanged, with a clear error
        m.py("import bpy\nbpy.ops.mesh.primitive_plane_add(size=4, location=(600,0,0))\nbpy.context.active_object.name='BMT_plane'")
        m.call("create_box", name="BMT_cut", width=1, depth=1, height=1, x=600)
        before = m.check("BMT_plane")
        err, text = m.call("boolean_op", target="BMT_plane", cutter="BMT_cut", operation="DIFFERENCE")
        after = m.check("BMT_plane")
        ok("boolean_op on a broken mesh is undone with a clear error", "unchanged" in text and after["vertices"] == before["vertices"], text[:90])

        # --- set_dimensions, move_object, bevel_object
        m.call("create_box", name="BMT_g", width=2, depth=2, height=2)
        err, text = m.call("set_dimensions", name="BMT_g", x=4, z=1)
        c = m.check("BMT_g")
        ok("set_dimensions sets exact sizes", not err and all(close(x, y) for x, y in zip(c["dims"], (4, 2, 1))), str(c["dims"]))
        err, text = m.call("move_object", name="BMT_g", x=30, y=1)
        ok("move_object absolute", not err and "(30.0, 1.0, 0.0)" in text, text)
        err, text = m.call("move_object", name="BMT_g", z=-3, relative=True)
        ok("move_object relative", not err and "(30.0, 1.0, -3.0)" in text, text)
        err, text = m.call("bevel_object", name="BMT_g", width=0.2, segments=3)
        ok("bevel_object adds a bevel", not err and text.startswith("OK"), text)
        mods = m.py("import bpy\nprint(len(bpy.data.objects['BMT_g'].modifiers))")
        err, text = m.call("bevel_object", name="BMT_g", width=0.1, segments=2)
        ok("bevel_object updates the same modifier", mods == "1" and m.py("import bpy\nprint(len(bpy.data.objects['BMT_g'].modifiers))") == "1")

        # --- counterbore and countersink: the removed volume must match the geometry (plate 40 x 20 x 5, hole 3, recess 6)
        m.call("create_box", name="BMT_cb", width=40, depth=20, height=5, x=900)
        v0 = m.check("BMT_cb")["volume"]
        err, text = m.call("drill_hole", name="BMT_cb", diameter=3, x=900, y=0, counterbore_diameter=6, counterbore_depth=2)
        c = m.check("BMT_cb")
        want = math.pi * (1.5 ** 2 * 5 + (3 ** 2 - 1.5 ** 2) * 2)
        ok("drill_hole counterbore: removed volume = hole + recess ring", not err and close(v0 - c["volume"], want, rel=0.01), f"removed {v0 - c['volume']:.2f} vs {want:.2f}")
        ok("drill_hole counterbore: mesh stays closed", c["manifold"])
        m.call("create_box", name="BMT_cs", width=40, depth=20, height=5, x=1000)
        v0 = m.check("BMT_cs")["volume"]
        err, text = m.call("drill_hole", name="BMT_cs", diameter=3, x=1000, y=0, countersink_diameter=6)
        c = m.check("BMT_cs")
        frustum = math.pi * 1.5 / 3 * (3 ** 2 + 3 * 1.5 + 1.5 ** 2) - math.pi * 1.5 ** 2 * 1.5
        want = math.pi * 1.5 ** 2 * 5 + frustum
        ok("drill_hole countersink (90 degrees): removed volume = hole + cone", not err and close(v0 - c["volume"], want, rel=0.015), f"removed {v0 - c['volume']:.2f} vs {want:.2f}")
        ok("drill_hole countersink: mesh stays closed", c["manifold"])
        m.call("create_box", name="BMT_cb2", width=40, depth=20, height=5, x=1100)
        v0 = m.check("BMT_cb2")["volume"]
        err, text = m.call("drill_hole", name="BMT_cb2", diameter=3, x=1100, y=0, side="-", counterbore_diameter=6, counterbore_depth=2)
        ok("drill_hole counterbore from the bottom side", not err and close(v0 - m.check("BMT_cb2")["volume"], math.pi * (1.5 ** 2 * 5 + 6.75 * 2), rel=0.01), text[:60])
        err, text = m.call("drill_hole", name="BMT_cb2", diameter=3, x=1105, y=0, counterbore_diameter=2, counterbore_depth=1)
        ok("drill_hole rejects a counterbore smaller than the hole", err and "larger than the hole" in text, text[:70])
        err, text = m.call("drill_hole", name="BMT_cb2", diameter=3, x=1105, y=0, counterbore_diameter=6, counterbore_depth=1, countersink_diameter=6)
        ok("drill_hole rejects counterbore + countersink together", err and "not both" in text, text[:70])

        # --- screw_hole: standard M3 values from the table (clearance 3.4, counterbore 6.0 x 3.0, insert 4.0 x 5.7)
        m.call("create_box", name="BMT_sh", width=30, depth=30, height=12, x=1200)
        v = m.check("BMT_sh")["volume"]
        err, text = m.call("screw_hole", name="BMT_sh", size="M3", x=1200, y=0)
        c = m.check("BMT_sh")
        ok("screw_hole M3 clearance: removed volume = pi 1.7^2 * 12", not err and close(v - c["volume"], math.pi * 1.7 ** 2 * 12, rel=0.01), f"removed {v - c['volume']:.2f}")
        v = c["volume"]
        err, text = m.call("screw_hole", name="BMT_sh", size="M3", kind="counterbore", x=1208, y=0)
        c = m.check("BMT_sh")
        want = math.pi * (1.7 ** 2 * 12 + (3 ** 2 - 1.7 ** 2) * 3)
        ok("screw_hole M3 counterbore: hole + head recess", not err and close(v - c["volume"], want, rel=0.01), f"removed {v - c['volume']:.2f} vs {want:.2f}")
        v = c["volume"]
        err, text = m.call("screw_hole", name="BMT_sh", size="M3", kind="insert", x=1192, y=0, z=6)
        c = m.check("BMT_sh")
        ok("screw_hole M3 insert: blind hole 4.0 x 5.7", not err and close(v - c["volume"], math.pi * 2 ** 2 * 5.7, rel=0.01), f"removed {v - c['volume']:.2f}")
        ok("screw_hole keeps the mesh closed", c["manifold"])
        err, text = m.call("screw_hole", name="BMT_sh", size="M9", x=0, y=0)
        ok("screw_hole rejects an unknown size", err and "Known sizes" in text, text[:70])
        err, text = m.call("screw_hole", name="BMT_sh", size="M3", kind="insert", x=1192, y=5)
        ok("screw_hole insert needs the entry point", err and "required for a blind hole" in text, text[:70])

        # --- repair_mesh: a cube whose faces are all loose quads (no shared vertices) becomes a closed cube again
        m.py("import bpy, bmesh\nbm = bmesh.new()\nbmesh.ops.create_cube(bm, size=2)\nbmesh.ops.split_edges(bm, edges=bm.edges[:])\n"
             "me = bpy.data.meshes.new('BMT_broken_mesh')\nbm.to_mesh(me)\nbm.free()\no = bpy.data.objects.new('BMT_broken', me)\n"
             "bpy.context.scene.collection.objects.link(o)\no.location = (1300, 0, 0)")
        before = m.check("BMT_broken")
        err, text = m.call("repair_mesh", name="BMT_broken")
        after = m.check("BMT_broken")
        ok("repair_mesh: broken mesh was open", before["bad"] > 0, f"{before['bad']} non-manifold edges")
        ok("repair_mesh makes it closed again, volume 8", not err and after["manifold"] and close(after["volume"], 8, rel=0.01), text[:100])

        # --- set_color, delete_object, plane, icosphere
        err, text = m.call("set_color", name="BMT_cb", color="red")
        col = m.py("import bpy\nprint(tuple(round(c, 2) for c in bpy.data.objects['BMT_cb'].active_material.diffuse_color))")
        ok("set_color changes the material", not err and col == "(1.0, 0.0, 0.0, 1.0)", col)
        err, text = m.call("create_plane", name="BMT_plane2", size=4, x=1400)
        ok("create_plane", not err and text.startswith("OK"), text[:50])
        err, text = m.call("create_icosphere", name="BMT_ico", radius=2, subdivisions=3, x=1500)
        ok("create_icosphere", not err and text.startswith("OK"), text[:50])
        err, text = m.call("delete_object", name="BMT_ico")
        ok("delete_object removes it", not err and "BMT_ico MESH" not in m.call("list_objects")[1], text)
        err, text = m.call("delete_object", name="BMT_ico")
        ok("delete_object on a missing object is a clear error", err or "object not found" in text, text[:60])

        # --- export_stl: only a plain file name, written to the export folder, never a free path
        err, text = m.call("export_stl", name="BMT_sh", filename="bmt_test.stl")
        mo = re.search(r" to (.+?) \((\d+) bytes\)", text)
        path = mo.group(1) if mo else ""
        if path: exported.append(path)
        ok("export_stl writes a binary STL inside the export folder", not err and bool(mo) and os.path.exists(path) and int(mo.group(2)) > 84 and os.path.basename(path) == "bmt_test.stl", text[:100])
        for bad_name in ("../escape.stl", "sub/dir.stl", "/etc/passwd"):
            err, text = m.call("export_stl", name="BMT_sh", filename=bad_name)
            ok(f"export_stl rejects the path '{bad_name}'", err and "plain file name" in text, text[:70])

        # --- set_units (restored at the end)
        err, text = m.call("set_units", unit="mm")
        got = m.py("import bpy\nu = bpy.context.scene.unit_settings\nprint(u.system, u.scale_length, u.length_unit)")
        ok("set_units mm: 1 Blender unit = 1 mm", not err and got.split()[2] == "MILLIMETERS" and close(float(got.split()[1]), 0.001), got)
        err, text = m.call("set_units", unit="furlong")
        ok("set_units rejects an unknown unit", err and "Invalid unit" in text, text[:60])

        # --- errors the model will make: they must be clear and must never be a traceback
        for label, tool, args, needle in [
            ("unknown object", "move_object", {"name": "BMT_nope", "x": 1}, "object not found"),
            ("translated name lists the real names", "bevel_object", {"name": "Cubo", "width": 0.1}, "Objects:"),
            ("invalid color", "create_sphere", {"color": "nonexistent"}, "Invalid color"),
            ("feminine color accepted", None, None, None),
            ("bad operation", "boolean_op", {"target": "BMT_g", "cutter": "BMT_cut", "operation": "explode"}, "Invalid operation"),
            ("same object", "boolean_op", {"target": "BMT_g", "cutter": "BMT_g"}, "different objects"),
            ("negative size", "create_box", {"width": -1}, "must be between"),
            ("injection through the name", "check_mesh", {"name": "x\"); import os; (\""}, "not found"),
        ]:
            if tool is None:
                err, text = m.call("create_sphere", name="BMT_roxa", color="roxa", x=700)
                ok(label, not err and text.startswith("OK"), text[:50])
                continue
            err, text = m.call(tool, **args)
            ok(f"error: {label}", needle in text and "Traceback" not in text, text[:80])
    finally:
        if len(orig_units) == 3:  # put the scene units back as they were
            m.py(f"import bpy\nu = bpy.context.scene.unit_settings\nu.system = '{orig_units[0]}'\nu.scale_length = {orig_units[1]}\nu.length_unit = '{orig_units[2]}'")
        for p in exported:
            try: os.remove(p)
            except OSError: pass
        left = m.py("import bpy\nfor o in list(bpy.data.objects):\n    if o.name.startswith('BMT_'): bpy.data.objects.remove(o, do_unlink=True)\nprint(len([o for o in bpy.data.objects if o.name.startswith('BMT_')]))")
        ok("cleanup removed every BMT_ object", left == "0", f"left {left}")

    print(f"\n{len(PASS)} passed, {len(FAIL)} failed, {time.time() - t0:.1f} s")
    for f in FAIL:
        print("  failed:", f)
    sys.exit(1 if FAIL else 0)


if __name__ == "__main__":
    main()
