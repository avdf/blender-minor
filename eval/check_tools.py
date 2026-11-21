#!/usr/bin/env python3
"""Checks the blender-minor tools WITHOUT a model: each tool is called directly and the result is compared with a volume
worked out by hand (read back with check_mesh). Use it after changing a tool or updating Blender.

It builds its own objects (names start with "ct_") and deletes every one of them at the end, so it is safe to run against a
Blender window that has your own work in it.

Usage:
  python3 -I check_tools.py                         # blender-minor on http://127.0.0.1:9192/
  python3 -I check_tools.py --url http://127.0.0.1:9193/
Exit code 0 = every check passed.
"""
import argparse, json, os, re, shutil, sys, time, urllib.request

ap = argparse.ArgumentParser()
ap.add_argument("--url", default="http://127.0.0.1:9192/", help="blender-minor address")
ap.add_argument("--blender-mcp", default="http://127.0.0.1:9191/", help="blender-mcp address, used only to read WHERE the material is (z)")
ap.add_argument("--trace", action="store_true",
                help="write the trace of this run into eval/rounds/<date>-check-tools/ (WHY.md, output.txt, evidence.json, scene.blend, window.png, "
                     "viewport.png), to be committed: see roundtrace.py")
ap.add_argument("--save-blend", nargs="*", default=None, metavar="DIR", help="with --trace, also copy that folder into each DIR (e.g. the desktop)")
ap.add_argument("--why", default="", help="WHY this run was made: goes into WHY.md")
ap.add_argument("--changed", default="", help="what changed since the previous run: goes into WHY.md")
ap.add_argument("--keep", action="store_true", help="do not delete the test parts at the end (they stay in the scene to be looked at)")
args = ap.parse_args()

here = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, here)   # python -I does not add the script folder
import roundtrace as rt
_t0 = time.time()
_log = []
_print = print


def print(*a, **k):   # every line is also kept, so the run can be saved as evidence
    _print(*a, **k)
    _log.append(" ".join(str(x) for x in a))


_id = [0]
_made = []


def call(tool, **kw):
    _id[0] += 1
    body = {"jsonrpc": "2.0", "id": _id[0], "method": "tools/call", "params": {"name": tool, "arguments": kw}}
    req = urllib.request.Request(args.url, data=json.dumps(body).encode(),
                                 headers={"Content-Type": "application/json", "Accept": "application/json, text/event-stream"})
    r = json.loads(urllib.request.urlopen(req, timeout=120).read().decode())["result"]
    return ("ERROR " if r.get("isError") else "") + " ".join(c["text"] for c in r["content"]).replace("\n", " ")


def make(name, **kw):
    _made.append(name)
    return call("create_box", name=name, **kw)


def volume(name):
    m = re.search(r"volume=([0-9.]+)", call("check_mesh", name=name))
    return float(m.group(1)) if m else float("nan")


def ray_hit(name, origin, direction):
    """World point where a ray from `origin` along `direction` first meets the object (read through the Blender MCP), or None."""
    code = ("import bpy\nfrom mathutils import Vector\no = bpy.data.objects['%s']\ninv = o.matrix_world.inverted()\n"
            "ok, loc, n, i = o.ray_cast(inv @ Vector(%r), (inv.to_3x3() @ Vector(%r)).normalized())\n"
            "w = o.matrix_world @ loc\nprint('HIT', w.x, w.y, w.z) if ok else print('HIT none')" % (name, tuple(origin), tuple(direction)))
    body = {"jsonrpc": "2.0", "id": 1, "method": "tools/call", "params": {"name": "execute_blender_code", "arguments": {"code": code}}}
    req = urllib.request.Request(args.blender_mcp, data=json.dumps(body).encode(),
                                 headers={"Content-Type": "application/json", "Accept": "application/json, text/event-stream"})
    m = re.search(r"HIT (-?[0-9.e+-]+) (-?[0-9.e+-]+) (-?[0-9.e+-]+)", urllib.request.urlopen(req, timeout=60).read().decode().replace("\\n", " "))
    return tuple(float(g) for g in m.groups()) if m else None


def surface_z(name, x, y):
    """World z of the highest surface of the object at (x, y), by a ray cast from above (read through the Blender MCP)."""
    code = ("import bpy\nfrom mathutils import Vector\no = bpy.data.objects['%s']\ninv = o.matrix_world.inverted()\n"
            "ok, loc, n, i = o.ray_cast(inv @ Vector((%r, %r, 1000.0)), inv.to_3x3() @ Vector((0, 0, -1)))\n"
            "print('SURF', (o.matrix_world @ loc).z if ok else 'none')" % (name, x, y))
    body = {"jsonrpc": "2.0", "id": 1, "method": "tools/call", "params": {"name": "execute_blender_code", "arguments": {"code": code}}}
    req = urllib.request.Request(args.blender_mcp, data=json.dumps(body).encode(),
                                 headers={"Content-Type": "application/json", "Accept": "application/json, text/event-stream"})
    m = re.search(r"SURF (-?[0-9.]+)", urllib.request.urlopen(req, timeout=60).read().decode().replace("\\n", " "))
    return float(m.group(1)) if m else float("nan")


fails = 0


def check(label, got, want, tol=0.005):
    global fails
    ok = abs(got - want) <= max(tol * abs(want), 1e-6)
    fails += 0 if ok else 1
    print(f"{'PASS' if ok else 'FAIL'}  {label}: got {got:.3f}, expected {want:.3f}")


def expect_error(label, text):
    global fails
    ok = text.startswith("ERROR") or "ERROR" in text[:40]
    fails += 0 if ok else 1
    print(f"{'PASS' if ok else 'FAIL'}  {label}: {text[:110]}")


def closed(name):
    return "manifold=True" in call("check_mesh", name=name)


def lim(label, name, want, tol=1e-6):
    """A limit case: the exact volume AND a closed (manifold) mesh. Coplanar faces are the classic way for a boolean to fail."""
    global fails
    check(label, volume(name), want, tol=tol)
    ok = closed(name)
    fails += 0 if ok else 1
    print(f"{'PASS' if ok else 'FAIL'}  {label}: the mesh is closed (manifold)")


try:
    # pad / pocket on a 100 x 10 x 2 plate: a strip raised at one end, then a recess, then a recess INSIDE the raised strip
    make("ct_plate", width=100, depth=10, height=2, x=0, y=200, z=1)
    check("plate", volume("ct_plate"), 100 * 10 * 2)
    again = call("create_box", name="ct_plate", width=1, depth=1, height=1)
    expect_error("a box named like an existing object is refused (no silent .001)", again)
    check("the refused box changed nothing", volume("ct_plate"), 100 * 10 * 2)
    if "ct_plate.001" in call("list_objects"):
        fails += 1
        print("FAIL  a ct_plate.001 was created anyway")
        _made.append("ct_plate.001")
    call("pad", name="ct_plate", x1=-50, x2=-30, y1=196, y2=204, height=2)
    check("pad 20x8x2", volume("ct_plate"), 2000 + 20 * 8 * 2)
    call("pocket", name="ct_plate", x1=10, x2=30, y1=197, y2=203, depth=1)
    check("pocket 20x6x1 (surface at z=2)", volume("ct_plate"), 2000 + 320 - 20 * 6 * 1)
    call("pocket", name="ct_plate", x1=-45, x2=-35, y1=198, y2=202, depth=1)
    check("pocket 10x4x1 inside the raised strip (surface at z=4)", volume("ct_plate"), 2000 + 320 - 120 - 10 * 4 * 1)
    before = volume("ct_plate")
    expect_error("pad outside the face is refused", call("pad", name="ct_plate", x1=500, x2=510, y1=196, y2=204, height=2))
    check("the refused pad changed nothing", volume("ct_plate"), before)

    # a pad on a side face (axis X, side -): a 10 x 10 x 2 boss on the left end
    make("ct_side", width=40, depth=20, height=20, x=0, y=300, z=10)
    call("pad", name="ct_side", axis="X", side="-", y1=295, y2=305, z1=5, z2=15, height=2)
    check("pad on the X- face (10x10x2)", volume("ct_side"), 40 * 20 * 20 + 10 * 10 * 2)

    # mirror: half part of 10 x 10 x 4 at x=60 mirrored across X=0 gives two
    make("ct_half", width=10, depth=10, height=4, x=60, y=400, z=2)
    call("mirror_object", name="ct_half", axis="X", at=0)
    check("mirror_object (two halves)", volume("ct_half"), 2 * 10 * 10 * 4)

    # join / subtract / intersect: the TOOL carries the operation, there is no parameter to choose
    make("ct_a", width=10, depth=10, height=10, x=80, y=500, z=5)
    make("ct_b", width=10, depth=10, height=10, x=85, y=500, z=5)
    call("join", target="ct_a", other="ct_b")
    check("join (5x10x10 overlap)", volume("ct_a"), 1000 + 1000 - 500)
    make("ct_c", width=20, depth=20, height=5, x=120, y=500, z=2.5)
    make("ct_d", width=10, depth=10, height=10, x=120, y=500, z=2.5)
    call("subtract", target="ct_c", cutter="ct_d")
    check("subtract (10x10x5 removed)", volume("ct_c"), 20 * 20 * 5 - 10 * 10 * 5)
    make("ct_e", width=10, depth=10, height=10, x=160, y=500, z=5)
    make("ct_f", width=10, depth=10, height=10, x=165, y=500, z=5)
    call("intersect", target="ct_e", other="ct_f")
    check("intersect (5x10x10 in common)", volume("ct_e"), 5 * 10 * 10)

    # array: 10 parts of 5 x 5 x 2 in a 5 x 2 grid with a 2 mm gap
    make("ct_piece", width=5, depth=5, height=2, x=0, y=600, z=1)
    call("array_object", name="ct_piece", count_x=5, count_y=2, gap=2)
    check("array_object 5x2 (ten parts)", volume("ct_piece"), 10 * 5 * 5 * 2)
    out = call("check_mesh", name="ct_piece")
    m = re.search(r"dimensions=\(([^)]*)\)", out)
    dims = tuple(round(float(x)) for x in m.group(1).split(",")) if m else None
    check("array size X = 5*5 + 4*2", dims[0] if dims else float("nan"), 33)
    check("array size Y = 2*5 + 1*2", dims[1] if dims else float("nan"), 12)
    expect_error("array with both counts 1 is refused", call("array_object", name="ct_piece", count_x=1, count_y=1))

    # drill_hole: a hole that touches nothing must not say OK, and one that only grazes the part must warn (the box is put at
    # x=700 on purpose, so that a hole can miss it).
    make("ct_drill", width=20, depth=20, height=5, x=700, y=0, z=2.5)
    pi = 3.14159265
    drill = call("drill_hole", name="ct_drill", diameter=4, x=700, y=0)
    check("drill_hole 4 mm through the middle (box at x=700)", volume("ct_drill"), 20 * 20 * 5 - pi * 2 * 2 * 5, tol=0.003)
    full = volume("ct_drill")
    ok_full = drill.startswith("OK") and "warning" not in drill
    fails += 0 if ok_full else 1
    print(f"{'PASS' if ok_full else 'FAIL'}  a whole hole carries no warning: {drill[:110]}")
    expect_error("a hole in the void is refused (no OK for nothing)", call("drill_hole", name="ct_drill", diameter=4, x=800, y=0))
    check("the refused hole changed nothing", volume("ct_drill"), full, tol=1e-9)
    graze = call("drill_hole", name="ct_drill", diameter=4, x=711, y=0)   # 1 mm outside the edge: only a sliver of the circle is inside
    ok_graze = graze.startswith("OK") and "warning" in graze
    fails += 0 if ok_graze else 1
    print(f"{'PASS' if ok_graze else 'FAIL'}  a hole that only grazes the part warns: {graze[:150]}")
    # pad / pocket always land on the REAL surface (a ray finds it): on a Z face there is no z to give, so they can never end up
    # "in the middle" of the part, and a rectangle with no size is refused. The box is centred on the origin plane (z -5 to 5).
    make("ct_zbox", width=40, depth=40, height=10, x=0, y=800, z=0)
    call("pocket", name="ct_zbox", x1=-5, x2=5, y1=795, y2=805, depth=0.5)
    check("pocket 10x10x0.5 removes only that much", volume("ct_zbox"), 40 * 40 * 10 - 10 * 10 * 0.5, tol=1e-6)
    check("the pocket floor is at z=4.5: cut from the top, not in the middle", surface_z("ct_zbox", 0, 800), 4.5, tol=1e-4)
    call("pad", name="ct_zbox", x1=-18, x2=-8, y1=795, y2=805, height=0.5)
    check("pad 10x10x0.5 adds only that much", volume("ct_zbox"), 40 * 40 * 10 - 50 + 50, tol=1e-6)
    check("the pad top is at z=5.5: raised from the top, not in the middle", surface_z("ct_zbox", -13, 800), 5.5, tol=1e-4)
    keep = volume("ct_zbox")
    expect_error("pocket with a rectangle of no size (z1 = z2 = 0) is refused",
                 call("pocket", name="ct_zbox", axis="X", side="+", y1=795, y2=805, z1=0, z2=0, depth=0.5))
    expect_error("pad with a rectangle of no size (z1 = z2 = 0) is refused",
                 call("pad", name="ct_zbox", axis="X", side="+", y1=795, y2=805, z1=0, z2=0, height=0.5))
    expect_error("pocket outside the face is refused", call("pocket", name="ct_zbox", x1=500, x2=510, y1=795, y2=805, depth=0.5))
    check("the refused calls changed nothing", volume("ct_zbox"), keep, tol=1e-9)
    # LIMITS: numbers exactly ON the boundary. Two faces in the same plane (coplanar) are the classic way for a boolean to fail or to
    # leave a skin inside the part in Blender, so each case must give the exact volume and a closed mesh. Boxes are 40 x 40 x 10 (z -5..5).
    pi = 3.14159265
    make("ct_l1", width=40, depth=40, height=10, x=0, y=900, z=0)
    call("pocket", name="ct_l1", x1=-20, x2=20, y1=880, y2=920, depth=3)
    lim("limit: pocket as wide as the whole top face (rectangle = face)", "ct_l1", 40 * 40 * 10 - 40 * 40 * 3)
    make("ct_l2", width=40, depth=40, height=10, x=0, y=1000, z=0)
    call("pad", name="ct_l2", x1=-20, x2=20, y1=980, y2=1020, height=2)
    lim("limit: pad as wide as the whole top face (rectangle = face)", "ct_l2", 40 * 40 * 10 + 40 * 40 * 2)
    make("ct_l3", width=40, depth=40, height=10, x=0, y=1100, z=0)
    call("pocket", name="ct_l3", x1=-5, x2=5, y1=1095, y2=1105, depth=10)
    lim("limit: pocket exactly as deep as the part (cutter ends on the bottom face)", "ct_l3", 40 * 40 * 10 - 10 * 10 * 10)
    make("ct_l4", width=40, depth=40, height=10, x=0, y=1200, z=0)
    call("drill_hole", name="ct_l4", diameter=10, x=0, y=1200, z=5, depth=10)
    lim("limit: blind hole exactly as deep as the part", "ct_l4", 40 * 40 * 10 - pi * 25 * 10, tol=0.003)
    make("ct_l5", width=40, depth=40, height=10, x=0, y=1300, z=0)
    tangent = call("drill_hole", name="ct_l5", diameter=10, x=15, y=1300)
    lim("limit: hole exactly tangent to the side face (x=15, radius 5, face at x=20)", "ct_l5", 40 * 40 * 10 - pi * 25 * 10, tol=0.003)
    said = tangent.startswith("OK") and "touches a face exactly" in tangent
    fails += 0 if said else 1
    print(f"{'PASS' if said else 'FAIL'}  the answer says the hole touched a face and the cutter was nudged: {tangent[-120:]}")
    make("ct_l6a", width=20, depth=40, height=10, x=-10, y=1400, z=0)
    make("ct_l6b", width=20, depth=40, height=10, x=10, y=1400, z=0)
    call("join", target="ct_l6a", other="ct_l6b")
    lim("limit: join of two boxes that only touch (zero overlap)", "ct_l6a", 40 * 40 * 10)
    make("ct_l7", width=40, depth=40, height=10, x=0, y=1500, z=0)
    make("ct_l7c", width=10, depth=10, height=5, x=0, y=1500, z=2.5)
    call("subtract", target="ct_l7", cutter="ct_l7c")
    lim("limit: subtract a cutter whose top is flush with the part's top", "ct_l7", 40 * 40 * 10 - 10 * 10 * 5)
    make("ct_l8", width=40, depth=40, height=10, x=0, y=1600, z=0)
    make("ct_l8b", width=20, depth=20, height=10, x=0, y=1600, z=0)
    call("intersect", target="ct_l8", other="ct_l8b")
    lim("limit: intersect where the inner box shares the top and the bottom plane", "ct_l8", 20 * 20 * 10)
    # side holes for a part that stands up (a foot, an ear): parallel to X and parallel to Y. 40 x 30 x 50 centred on the origin plane.
    make("ct_foot", width=40, depth=30, height=50, x=0, y=1700, z=0)
    full = 40 * 30 * 50
    sx = call("drill_hole_x", name="ct_foot", diameter=6, y=1700, z=10)
    check("drill_hole_x 6 mm (through 40 along X)", volume("ct_foot"), full - pi * 9 * 40, tol=0.001)
    sy = call("drill_hole_y", name="ct_foot", diameter=6, x=0, z=-10)
    check("drill_hole_y 6 mm (through 30 along Y)", volume("ct_foot"), full - pi * 9 * 40 - pi * 9 * 30, tol=0.001)
    ok_axis = sx.startswith("OK") and "axis X" in sx and sy.startswith("OK") and "axis Y" in sy
    fails += 0 if ok_axis else 1
    print(f"{'PASS' if ok_axis else 'FAIL'}  the answers say which axis was drilled: {sx[40:100]} | {sy[40:100]}")
    expect_error("a side hole outside the part is refused", call("drill_hole_x", name="ct_foot", diameter=6, y=1900, z=10))
    expect_error("segments above the absolute cap (4096) is refused", call("drill_hole", name="ct_foot", diameter=2, x=0, y=1700, segments=5000))
    # pad and pocket on the four SIDE faces of a part that stands up (right +X, left -X, front -Y, back +Y). The face is in the tool name;
    # a ray from outside proves the material appeared on the RIGHT side, not only that the volume changed. Foot: 40 x 30 x 50 at y=1900.
    make("ct_side4", width=40, depth=30, height=50, x=0, y=1900, z=0)
    v = 40 * 30 * 50
    call("pad_right", name="ct_side4", y1=1895, y2=1905, z1=-5, z2=5, height=3)
    call("pad_left", name="ct_side4", y1=1895, y2=1905, z1=-5, z2=5, height=3)
    call("pad_front", name="ct_side4", x1=-5, x2=5, z1=-5, z2=5, height=2)
    call("pad_back", name="ct_side4", x1=-5, x2=5, z1=-5, z2=5, height=2)
    check("four side pads add 300 + 300 + 200 + 200", volume("ct_side4"), v + 1000, tol=1e-6)
    for label, origin, direction, axis, want in (("right (+X) pad top at x=23", (1000, 1900, 0), (-1, 0, 0), 0, 23.0),
                                                 ("left (-X) pad top at x=-23", (-1000, 1900, 0), (1, 0, 0), 0, -23.0),
                                                 ("front (-Y) pad top at y=1883", (0, 900, 0), (0, 1, 0), 1, 1883.0),
                                                 ("back (+Y) pad top at y=1917", (0, 2900, 0), (0, -1, 0), 1, 1917.0)):
        h = ray_hit("ct_side4", origin, direction)
        check(label, h[axis] if h else float("nan"), want, tol=1e-4)
    call("pocket_right", name="ct_side4", y1=1897, y2=1903, z1=-3, z2=3, depth=1)
    call("pocket_left", name="ct_side4", y1=1897, y2=1903, z1=-3, z2=3, depth=1)
    call("pocket_front", name="ct_side4", x1=-3, x2=3, z1=-3, z2=3, depth=1)
    call("pocket_back", name="ct_side4", x1=-3, x2=3, z1=-3, z2=3, depth=1)
    check("four side pockets of 6 x 6 x 1 remove 4 x 36", volume("ct_side4"), v + 1000 - 4 * 36, tol=1e-6)
    for label, origin, direction, axis, want in (("right pocket floor at x=22", (1000, 1900, 0), (-1, 0, 0), 0, 22.0),
                                                 ("left pocket floor at x=-22", (-1000, 1900, 0), (1, 0, 0), 0, -22.0),
                                                 ("front pocket floor at y=1884", (0, 900, 0), (0, 1, 0), 1, 1884.0),
                                                 ("back pocket floor at y=1916", (0, 2900, 0), (0, -1, 0), 1, 1916.0)):
        h = ray_hit("ct_side4", origin, direction)
        check(label, h[axis] if h else float("nan"), want, tol=1e-4)
    keep = volume("ct_side4")
    expect_error("pad_right outside the face is refused", call("pad_right", name="ct_side4", y1=5000, y2=5010, z1=-5, z2=5, height=3))
    expect_error("pocket_front with a rectangle of no size is refused", call("pocket_front", name="ct_side4", x1=0, x2=0, z1=-3, z2=3, depth=1))
    check("the refused side calls changed nothing", volume("ct_side4"), keep, tol=1e-9)
finally:
    rd = rt.round_dir(here, "check-tools") if (args.trace or args.save_blend is not None) else None
    shots = rt.capture(args.blender_mcp, rd, grid=True) if rd else {}   # the visual proof, made by Blender, BEFORE the parts are deleted
    if not args.keep:
        for n in _made:
            try:
                call("delete_object", name=n)
            except Exception:
                pass

print(f"\n{fails} failure(s)")
if rd:
    results = [{"check": l[6:], "pass": l.startswith("PASS")} for l in _log if l.startswith(("PASS", "FAIL"))]
    tools = (rt.http_json(args.url.rstrip("/") + "/tools") or {})
    evidence = {"label": "check-tools", "when": time.strftime("%Y-%m-%d %H:%M:%S"), "why": args.why, "changed": args.changed,
                "git": rt.git_state(os.path.dirname(here)), "host": rt.host_info(), "blender_version": rt.blender_version(args.blender_mcp),
                "passed": sum(r["pass"] for r in results), "failed": fails, "seconds": round(time.time() - _t0), "checks": results}
    rt.write_round(rd, label="check-tools: every tool, called directly, against a volume worked out by hand", why=args.why, changed=args.changed,
                   result_line=f"{evidence['passed']} passed, {fails} failed", evidence=evidence, output_lines=_log, files=shots)
    _print("trace   :", rd)
    for d in (args.save_blend or []):
        try:
            shutil.copytree(rd, os.path.join(os.path.expanduser(d), os.path.basename(rd)), dirs_exist_ok=True)
            _print("copy    :", os.path.join(os.path.expanduser(d), os.path.basename(rd)))
        except Exception as e:
            _print("copy    : could not copy to", d, "->", e)
sys.exit(1 if fails else 0)
