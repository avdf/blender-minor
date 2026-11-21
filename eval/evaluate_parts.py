#!/usr/bin/env python3
"""Evaluates a small model on PARTS: sizes, holes, pad/pocket. The model is served by llama-server with the blender-minor tools.

For each request the model may call tools (up to N rounds); the calls go through the llama-server POST /tools endpoint. Then the part
is read back with check_mesh and compared with a size and a VOLUME worked out by hand, so "the model said OK" is never enough.
Every object the test makes is removed afterwards (names start with "pt_").

A case whose tools are not in the model's tool list (see --allow in tools/mcp_stdio_bridge.py) is skipped, not failed.

Usage:
  python3 -I evaluate_parts.py --url http://127.0.0.1:5080 --label 1.7B-q8
  python3 -I evaluate_parts.py --url http://host:9931 --system ../prompts/system.parts.en.txt --label my-model
Options: --blender-mcp (default http://127.0.0.1:9191/, used only to remove the test objects)  --think  --max-rounds  --max-tokens

Exit code 0 = every case that ran was correct.
"""
import argparse, hashlib, json, math, os, re, shutil, sys, time, urllib.request

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))   # python -I does not add the script folder
import evaluate_model as ev                                        # reuse its small HTTP helpers
import roundtrace as rt                                            # the trace of the round (see roundtrace.py)

_log = []
_print = print


def print(*a, **k):   # everything the test prints is also kept, to be saved as output.txt in the trace of the round
    _print(*a, **k)
    _log.append(" ".join(str(x) for x in a))


here = os.path.dirname(os.path.abspath(__file__))
ap = argparse.ArgumentParser()
ap.add_argument("--url", default="http://127.0.0.1:5080", help="llama-server")
ap.add_argument("--blender-mcp", default="http://127.0.0.1:9191/")
ap.add_argument("--system", default=os.path.join(here, "..", "prompts", "system.parts.en.txt"), help="file with the system prompt")
ap.add_argument("--label", required=True)
ap.add_argument("--tool-prefix", default="blender-minor_")
ap.add_argument("--think", action="store_true", help="enable Qwen3 reasoning")
ap.add_argument("--max-rounds", type=int, default=8)
ap.add_argument("--max-tokens", type=int, default=400)
ap.add_argument("--set", choices=["basic", "extra", "all", "more", "everything"], default="basic",
                help="basic: the 12 fixed cases; extra: variations of wording and numbers, bigger and composite parts, and questions that must call no tool; all: basic + extra; more: the tools no other case calls (pad_back, pocket_front, array_object, check_mesh, export_stl); everything: all of them")
ap.add_argument("--rounds-dir", default=None, metavar="DIR",
                help="write the trace of the round under DIR/rounds/ instead of eval/rounds/ (so a test folder outside git can keep its own results)")
ap.add_argument("--name-prefix", default="pt_",
                help="prefix of the part names (default pt_). Two runs at the same time (on different machines) use different prefixes so their parts never collide in Blender")
ap.add_argument("--timeout", type=int, default=3600,
                help="seconds to wait for ONE answer of the model (default 3600). The first request of a cold server reads the whole prompt: 6000 tokens take about 870 s at 7 tok/s")
ap.add_argument("--direct-tools", default=None, metavar="URL",
                help="blender-minor address (e.g. http://host:9192/). The test then takes the tool list from blender-minor, applies the same allow/hide "
                     "lists as the llama-server bridge (see --profile), sends them to ANY OpenAI-compatible server in --url (llama-server, dotnet-llm...) and "
                     "runs the tool calls itself. Without it the llama-server /tools route is used")
ap.add_argument("--profile", choices=["parts21", "parts9"], default="parts21",
                help="with --direct-tools: parts21 = the 21 tools of the parts profile; parts9 = the original 9 (shorter prompt: about 2300 tokens instead of 6000)")
ap.add_argument("--only", default="", help="run only the cases whose label contains this text (e.g. --only holes)")
ap.add_argument("--show", action="store_true",
                help="keep the parts in the Blender scene, side by side in a row (each in its own colour), and frame the 3D view at "
                     "the end, so you can look at them in the open Blender window; the previous pt_* objects are removed first")
ap.add_argument("--save-blend", nargs="*", default=None, metavar="DIR",
                help="also copy the folder of this round (eval/rounds/<date>-<label>/) into each DIR, e.g. a folder on the desktop")
ap.add_argument("--why", default="", help="WHY this round is being run (the problem it attacks or the hypothesis it tests): goes into WHY.md")
ap.add_argument("--changed", default="", help="what changed since the previous round (code, prompt, tools, model): goes into WHY.md")
ap.add_argument("--compare", default=None, metavar="evidence.json",
                help="the evidence.json of the round to compare with (default: the newest round already in eval/rounds/)")
ap.add_argument("--spacing", type=float, default=130.0, help="distance between the parts of the row with --show (default 130)")
a = ap.parse_args()
P = a.tool_prefix


def hex_vol(across, depth):
    """Volume of a regular hexagon recess: (3 sqrt(3) / 2) R^2 x depth, with R = across the flats / sqrt(3)."""
    r = across / math.sqrt(3)
    return 1.5 * math.sqrt(3) * r * r * depth


def box_minus(w, d, h, holes=(), cuts=0.0, adds=0.0, lengths=None):
    """Volume of the box minus round holes; lengths = how far each hole goes (default: the height, a hole along Z)."""
    lengths = lengths or [h] * len(holes)
    return w * d * h - sum(math.pi * (dia / 2) ** 2 * ln for dia, ln in zip(holes, lengths)) - cuts + adds


# Volume tolerance: 0.03%. A 1% tolerance let a 3 mm hole pass for an M3 clearance hole (3.4 mm), a 0.08% difference.
TOL = 0.0003

# (label, tools it needs, request, object name, expected size, expected volume, expected (min z, max z) or None)
# The z range tells WHERE the material is: a strip raised on the bottom face has the same volume and height as one on the top.
CASES = [
    ("box + M3 hole", {"create_box", "screw_hole"},
     "Make a box named pt_base, 60 by 40 by 8, with an M3 hole at x=10, y=5",
     "pt_base", (60, 40, 8), box_minus(60, 40, 8, [3.4]), None),
    ("box + 5 mm hole", {"create_box", "drill_hole"},
     "Create a box named pt_plate, 80 by 50 by 6, and drill a 5 mm hole at x=-20, y=10.",
     "pt_plate", (80, 50, 6), box_minus(80, 50, 6, [5.0]), None),
    ("box + 2 holes", {"create_box", "drill_hole"},
     "Make a box named pt_bracket, 30 by 30 by 10. Drill a 4 mm hole at x=-8, y=0 and another 4 mm hole at x=8, y=0.",
     "pt_bracket", (30, 30, 10), box_minus(30, 30, 10, [4.0, 4.0]), None),
    ("pad (raise a strip)", {"create_box", "pad"},
     "Make a box named pt_strip, 100 by 10 by 2. Then raise a rectangle on its top face from x=-50 to x=-30 and from y=-4 to y=4, 2 high.",
     "pt_strip", (100, 10, 4), 100 * 10 * 2 + 20 * 8 * 2, (-1.0, 3.0)),
    ("pocket (cut a recess)", {"create_box", "pocket"},
     "Make a box named pt_block, 40 by 40 by 10. Cut a pocket in its top face from x=-10 to x=10 and from y=-10 to y=10, 3 deep.",
     "pt_block", (40, 40, 10), 40 * 40 * 10 - 20 * 20 * 3, (-5.0, 5.0)),
    ("side hole along X (a foot)", {"create_box", "drill_hole_x"},
     "Make a box named pt_foot, 40 by 30 by 50. Drill a 6 mm hole through it, parallel to the X axis, at y=0 and z=10.",
     "pt_foot", (40, 30, 50), box_minus(40, 30, 50, [6.0], lengths=[40]), None),
    ("nut trap (top)", {"create_box", "nut_trap"},
     "Make a box named pt_nut, 30 by 30 by 10, with an M3 nut trap at x=0, y=0.",
     "pt_nut", (30, 30, 10), 30 * 30 * 10 - hex_vol(5.7, 2.6), None),
    ("screw hole + nut trap underneath", {"create_box", "screw_hole", "nut_trap_bottom"},
     "Make a box named pt_nutplate, 40 by 40 by 8, with an M3 screw hole at x=0, y=0 and a nut trap for it on the bottom.",
     "pt_nutplate", (40, 40, 8), 40 * 40 * 8 - hex_vol(5.7, 2.6) - math.pi * 1.7 ** 2 * (8 - 2.6), None),
    ("pad on the right face (a foot)", {"create_box", "pad_right"},
     "Make a box named pt_padr, 40 by 30 by 50. Raise a rectangle on its right face from y=-5 to y=5 and from z=-5 to z=5, 3 high.",
     "pt_padr", (43, 30, 50), 40 * 30 * 50 + 10 * 10 * 3, None),
    ("pocket on the left face (a foot)", {"create_box", "pocket_left"},
     "Make a box named pt_pockl, 40 by 30 by 50. Cut a pocket in its left face from y=-5 to y=5 and from z=-5 to z=5, 2 deep.",
     "pt_pockl", (40, 30, 50), 40 * 30 * 50 - 10 * 10 * 2, None),
    ("side hole along Y (an ear)", {"create_box", "drill_hole_y"},
     "Make a box named pt_ear, 50 by 20 by 30. Drill a 5 mm hole through it, parallel to the Y axis, at x=10 and z=-5.",
     "pt_ear", (50, 20, 30), box_minus(50, 20, 30, [5.0], lengths=[20]), None),
]


# More load, to be sure and not only to be quick: other wording (Portuguese too), other numbers, other sizes, parts with three and four holes,
# parts that need several tools in a row, and questions that must call NO tool.
CASES_EXTRA = [
    ("variant: request in Portuguese", {"create_box", "drill_hole"},
     "Faça uma caixa chamada pt_v1, 50 por 30 por 6, com um furo de 8 mm em x=-10, y=5.",
     "pt_v1", (50, 30, 6), box_minus(50, 30, 6, [8.0]), None),
    ("variant: coordinates in parentheses", {"create_box", "drill_hole"},
     "Create a box called pt_v2 that is 25 x 25 x 12 and add a hole of diameter 6 at (5, -5).",
     "pt_v2", (25, 25, 12), box_minus(25, 25, 12, [6.0]), None),
    ("variant: M4 screw hole", {"create_box", "screw_hole"},
     "Make a box named pt_v3, 45 by 35 by 10, with an M4 hole at x=0, y=0.",
     "pt_v3", (45, 35, 10), box_minus(45, 35, 10, [4.5]), None),
    ("variant: M5 screw hole", {"create_box", "screw_hole"},
     "A plate called pt_v4, 60 by 40 by 8 mm, with an M5 hole at x=-12, y=8.",
     "pt_v4", (60, 40, 8), box_minus(60, 40, 8, [5.5]), None),
    ("three holes", {"create_box", "drill_hole"},
     "Make a box named pt_v5, 60 by 20 by 8. Drill a 3 mm hole at x=-20, y=0, another at x=0, y=0 and another at x=20, y=0.",
     "pt_v5", (60, 20, 8), box_minus(60, 20, 8, [3.0, 3.0, 3.0]), None),
    ("four corner holes (the model works out the positions)", {"create_box", "drill_hole"},
     "Make a box named pt_v6, 70 by 50 by 5, with a 4 mm hole in each corner, 6 mm from the edges.",
     "pt_v6", (70, 50, 5), box_minus(70, 50, 5, [4.0] * 4), None),
    ("variant: pad, other numbers", {"create_box", "pad"},
     "Make a box named pt_v7, 80 by 20 by 3. Raise a rectangle on its top face from x=10 to x=30 and from y=-5 to y=5, 4 high.",
     "pt_v7", (80, 20, 7), 80 * 20 * 3 + 20 * 10 * 4, (-1.5, 5.5)),
    ("variant: pocket, other numbers", {"create_box", "pocket"},
     "Make a box named pt_v8, 60 by 60 by 12. Cut a pocket in its top face from x=-20 to x=20 and from y=-10 to y=10, 5 deep.",
     "pt_v8", (60, 60, 12), 60 * 60 * 12 - 40 * 20 * 5, (-6.0, 6.0)),
    ("pad on the left face", {"create_box", "pad_left"},
     "Make a box named pt_v9, 30 by 30 by 40. Raise a rectangle on its left face from y=-4 to y=4 and from z=0 to z=10, 2 high.",
     "pt_v9", (32, 30, 40), 30 * 30 * 40 + 8 * 10 * 2, None),
    ("pocket on the right face", {"create_box", "pocket_right"},
     "Make a box named pt_v10, 30 by 30 by 40. Cut a pocket in its right face from y=-5 to y=5 and from z=-5 to z=5, 3 deep.",
     "pt_v10", (30, 30, 40), 30 * 30 * 40 - 10 * 10 * 3, None),
    ("pad on the front face", {"create_box", "pad_front"},
     "Make a box named pt_v11, 30 by 30 by 40. Raise a rectangle on its front face from x=-5 to x=5 and from z=-5 to z=5, 2 high.",
     "pt_v11", (30, 32, 40), 30 * 30 * 40 + 10 * 10 * 2, None),
    ("pocket on the back face", {"create_box", "pocket_back"},
     "Make a box named pt_v12, 30 by 30 by 40. Cut a pocket in its back face from x=-5 to x=5 and from z=-5 to z=5, 3 deep.",
     "pt_v12", (30, 30, 40), 30 * 30 * 40 - 10 * 10 * 3, None),
    ("variant: M4 nut trap on top", {"create_box", "nut_trap"},
     "Make a box named pt_v13, 40 by 40 by 12, with an M4 nut trap at x=5, y=5.",
     "pt_v13", (40, 40, 12), 40 * 40 * 12 - hex_vol(7.2, 3.4), None),
    ("variant: M2.5 nut trap underneath", {"create_box", "nut_trap_bottom"},
     "Make a box named pt_v14, 30 by 30 by 8, with an M2.5 nut trap on the bottom at x=0, y=0.",
     "pt_v14", (30, 30, 8), 30 * 30 * 8 - hex_vol(5.2, 2.2), None),
    ("variant: side hole along X", {"create_box", "drill_hole_x"},
     "Make a box named pt_v15, 30 by 40 by 60. Drill a 4 mm hole through it along the X axis at y=-5 and z=-8.",
     "pt_v15", (30, 40, 60), box_minus(30, 40, 60, [4.0], lengths=[30]), None),
    ("variant: side hole along Y", {"create_box", "drill_hole_y"},
     "Make a box named pt_v16, 30 by 40 by 60. Drill an 8 mm hole through it along the Y axis at x=0 and z=15.",
     "pt_v16", (30, 40, 60), box_minus(30, 40, 60, [8.0], lengths=[40]), None),
    ("composite: hole + pad + pocket on one plate", {"create_box", "drill_hole", "pad", "pocket"},
     "Make a box named pt_c1, 50 by 50 by 8. Drill a 5 mm hole at x=0, y=0. Raise a rectangle on its top face from x=10 to x=20 and from y=10 to y=20, 3 high. "
     "Cut a pocket in its top face from x=-20 to x=-10 and from y=-20 to y=-10, 2 deep.",
     "pt_c1", (50, 50, 11), 50 * 50 * 8 - math.pi * 6.25 * 8 + 10 * 10 * 3 - 10 * 10 * 2, None),
    ("composite: a foot with a hole along X and another along Y", {"create_box", "drill_hole_x", "drill_hole_y"},
     "Make a box named pt_c2, 40 by 30 by 50. Drill a 6 mm hole through it, parallel to the X axis, at y=0 and z=10. "
     "Then drill a 5 mm hole through it, parallel to the Y axis, at x=0 and z=-10.",
     "pt_c2", (40, 30, 50), 40 * 30 * 50 - math.pi * 9 * 40 - math.pi * 6.25 * 30, None),
    ("composite: two M3 holes, each with a nut trap underneath", {"create_box", "screw_hole", "nut_trap_bottom"},
     "Make a box named pt_c3, 40 by 20 by 8, with an M3 screw hole at x=-10, y=0 and another at x=10, y=0, and a nut trap underneath each one.",
     "pt_c3", (40, 20, 8), 40 * 20 * 8 - 2 * (hex_vol(5.7, 2.6) + math.pi * 1.7 ** 2 * (8 - 2.6)), None),
]


# The tools of the parts profile that no case above calls: pad_back, pocket_front, array_object, check_mesh and export_stl.
CASES_MORE = [
    ("pad on the back face", {"create_box", "pad_back"},
     "Make a box named pt_m1, 30 by 30 by 40. Raise a rectangle on its back face from x=-5 to x=5 and from z=-5 to z=5, 2 high.",
     "pt_m1", (30, 32, 40), 30 * 30 * 40 + 10 * 10 * 2, None),
    ("pocket on the front face", {"create_box", "pocket_front"},
     "Make a box named pt_m2, 30 by 30 by 40. Cut a pocket in its front face from x=-5 to x=5 and from z=-5 to z=5, 3 deep.",
     "pt_m2", (30, 30, 40), 30 * 30 * 40 - 10 * 10 * 3, None),
    ("array: three copies along X, two along Y", {"create_box", "array_object"},
     "Make a box named pt_m3, 10 by 10 by 4, then repeat it 3 times along X and 2 times along Y with a 2 mm gap between the copies.",
     "pt_m3", (34, 22, 4), 6 * 10 * 10 * 4, None),
]
EXPORT_DIR = os.environ.get("BLENDER_MINOR_EXPORT_DIR", "/projetos/blender-minor-exports")


def _mentions(final, have_):
    return sorted(n for n in have_ if n in (final or ""))


# Questions: (label, request, check(calls, final, have) -> (ok, why)). A question about the tools must be answered from the prompt, calling nothing.
QUESTIONS = [
    ("question: what tools do you have (English)", "What tools do you have available? List them.",
     lambda calls, final, have_: (not calls and len(_mentions(final, have_)) >= 5,
                                  f"{len(calls)} tool call(s); names mentioned: {len(_mentions(final, have_))}")),
    ("question: what tools do you have (Portuguese)", "liste as ferramentas disponiveis. responda em portugues.",
     lambda calls, final, have_: (not calls and len(_mentions(final, have_)) >= 5,
                                  f"{len(calls)} tool call(s); names mentioned: {len(_mentions(final, have_))}")),
    ("tool: check_mesh (is the part printable?)", "Make a box named pt_m4, 20 by 20 by 5, and tell me whether its mesh is closed and printable.",
     lambda calls, final, have_: (any(c[0] == "create_box" for c in calls) and any(c[0] == "check_mesh" for c in calls),
                                  "calls: " + ", ".join(c[0] for c in calls))),
    ("tool: export_stl (a file for the slicer)", "Make a box named pt_m5, 20 by 20 by 5, and export it as m5.stl.",
     lambda calls, final, have_: (any(c[0] == "export_stl" and c[2].lstrip().startswith("OK") for c in calls) and os.path.exists(os.path.join(EXPORT_DIR, "m5.stl")),
                                  "calls: " + ", ".join(c[0] for c in calls) + " | m5.stl exists: " + str(os.path.exists(os.path.join(EXPORT_DIR, "m5.stl"))))),
    ("question: what is in the scene (must call list_objects)", "What objects are in the scene right now?",
     lambda calls, final, have_: (any(c[0] == "list_objects" for c in calls) and not any(c[0] in ("create_box", "drill_hole", "pad", "pocket") for c in calls),
                                  "calls: " + ", ".join(c[0] for c in calls))),
]


def run_code(code):
    """Runs Python in Blender through the Blender MCP (execute_blender_code) and returns the raw answer text."""
    body = {"jsonrpc": "2.0", "id": 1, "method": "tools/call", "params": {"name": "execute_blender_code", "arguments": {"code": code}}}
    req = urllib.request.Request(a.blender_mcp, data=json.dumps(body).encode(),
                                 headers={"Content-Type": "application/json", "Accept": "application/json, text/event-stream"})
    return urllib.request.urlopen(req, timeout=300).read().decode()


def z_range(obj):
    """Lowest and highest z of the object in world space."""
    text = run_code("import bpy\nfrom mathutils import Vector\no = bpy.data.objects['%s']\n"
                    "zs = [(o.matrix_world @ Vector(c)).z for c in o.bound_box]\nprint('ZRANGE', min(zs), max(zs))" % obj)
    m = re.search(r"ZRANGE (-?[0-9.]+) (-?[0-9.]+)", text.replace("\\n", " "))
    return (float(m.group(1)), float(m.group(2))) if m else None


PALETTE = [(0.9, 0.35, 0.2, 1), (0.2, 0.6, 0.9, 1), (0.3, 0.8, 0.4, 1), (0.95, 0.8, 0.2, 1), (0.7, 0.4, 0.9, 1), (0.9, 0.5, 0.7, 1), (0.4, 0.8, 0.8, 1), (0.8, 0.8, 0.8, 1)]


def place_in_row(obj, slot):
    """--show: centre the part in a grid cell (6 per row, spacing apart) and give it its own colour, so every part stays visible."""
    r, g, b, al = PALETTE[slot % len(PALETTE)]
    gx, gy = (slot % 6) * a.spacing, -(slot // 6) * 100.0
    run_code("import bpy\nfrom mathutils import Vector\no = bpy.data.objects.get('%s')\nif o is not None:\n"
             "    cs = [o.matrix_world @ Vector(c) for c in o.bound_box]\n"
             "    o.location.x += %r - (min(c.x for c in cs) + max(c.x for c in cs)) / 2\n"
             "    o.location.y += %r - (min(c.y for c in cs) + max(c.y for c in cs)) / 2\n"
             "    m = bpy.data.materials.new('%s_color'); m.diffuse_color = (%r, %r, %r, %r)\n"
             "    o.data.materials.clear(); o.data.materials.append(m)\n" % (obj, gx, gy, obj, r, g, b, al))


def frame_view():
    """--show: frame everything in the 3D view of the open Blender window."""
    run_code("import bpy\nwin = bpy.context.window_manager.windows[0]\nfor area in win.screen.areas:\n"
             "    if area.type == 'VIEW_3D':\n"
             "        region = next(r for r in area.regions if r.type == 'WINDOW')\n"
             "        with bpy.context.temp_override(window=win, area=area, region=region):\n"
             "            bpy.ops.view3d.view_all()\n"
             "        break\n")

PARTS9 = ["create_box", "drill_hole", "screw_hole", "pad", "pocket", "array_object", "list_objects", "check_mesh", "export_stl"]
PARTS21 = PARTS9 + ["drill_hole_x", "drill_hole_y", "nut_trap", "nut_trap_bottom", "pad_right", "pad_left", "pad_front", "pad_back",
                    "pocket_right", "pocket_left", "pocket_front", "pocket_back"]
# The parameters the model never sees (a small model fills an optional parameter on impulse): the same list as the bridge's --hide.
HIDE = ["create_box.x", "create_box.y", "create_box.z", "drill_hole.axis", "drill_hole.side", "screw_hole.axis", "screw_hole.side",
        "pad.axis", "pad.side", "pad.z1", "pad.z2", "pocket.axis", "pocket.side", "pocket.z1", "pocket.z2"]


def mcp_call(method, params=None):
    """JSON-RPC to blender-minor (HTTP, no session). Returns the result object."""
    body = {"jsonrpc": "2.0", "id": 1, "method": method, "params": params or {}}
    req = urllib.request.Request(a.direct_tools, data=json.dumps(body).encode(),
                                 headers={"Content-Type": "application/json", "Accept": "application/json, text/event-stream"})
    raw = urllib.request.urlopen(req, timeout=300).read().decode()
    if raw.lstrip().startswith("event:") or "data:" in raw[:20]:
        raw = next(l[5:].strip() for l in raw.splitlines() if l.startswith("data:"))
    return json.loads(raw)["result"]


def direct_defs():
    allow = PARTS9 if a.profile == "parts9" else PARTS21
    hide = [h.split(".", 1) for h in HIDE]
    out = []
    for t in mcp_call("tools/list")["tools"]:
        if t["name"] not in allow:
            continue
        schema = json.loads(json.dumps(t.get("inputSchema") or {"type": "object", "properties": {}}))
        props = schema.get("properties", {})
        for tool, param in hide:
            if tool == t["name"]:
                props.pop(param, None)
        schema["required"] = [r for r in schema.get("required", []) if r in props]
        out.append({"type": "function", "function": {"name": P + t["name"], "description": t.get("description", ""), "parameters": schema}})
    return out


def tool_exec(fn, args):
    """Runs one tool call: directly on blender-minor (--direct-tools) or through the llama-server /tools route."""
    if not a.direct_tools:
        return ev.call_tool(a.url, fn, args)
    name = fn[len(P):] if fn.startswith(P) else fn
    for tool, param in (h.split(".", 1) for h in HIDE):   # hidden parameters are dropped from any call, as the bridge does
        if tool == name:
            args = {k: v for k, v in args.items() if k != param}
    r = mcp_call("tools/call", {"name": name, "arguments": args})
    return " ".join(c.get("text", "") for c in r.get("content", [])).replace("\n", "\n")


def scene_now():
    scene = {}
    for line in tool_exec(P + "list_objects", {}).splitlines():
        m = ev.LINE.match(line.strip())
        if m:
            scene[m.group(1)] = (m.group(2), float(m.group(3)), float(m.group(4)), float(m.group(5)))
    return scene


ALL = {"basic": CASES, "extra": CASES_EXTRA, "all": CASES + CASES_EXTRA, "more": CASES_MORE, "everything": CASES + CASES_EXTRA + CASES_MORE}[a.set]

def _px(text):
    return text.replace("pt_", a.name_prefix) if isinstance(text, str) else text


if a.name_prefix != "pt_":
    ALL = [(l, n, _px(r), _px(o), d, v, z) for l, n, r, o, d, v, z in ALL]
    CASES = [(l, n, _px(r), _px(o), d, v, z) for l, n, r, o, d, v, z in CASES]
    CASES_EXTRA = [(l, n, _px(r), _px(o), d, v, z) for l, n, r, o, d, v, z in CASES_EXTRA]
    CASES_MORE = [(l, n, _px(r), _px(o), d, v, z) for l, n, r, o, d, v, z in CASES_MORE]

defs = direct_defs() if a.direct_tools else [t["definition"] for t in ev.http_json(a.url + "/tools", timeout=20)]
have = {d["function"]["name"].replace(P, "") for d in defs}
system = open(a.system, encoding="utf-8").read() if a.system and os.path.exists(a.system) else None
print(f"== {a.label} | system: {os.path.basename(a.system) if system else '-'} | think: {a.think} | tools: {sorted(have)}\n")

if a.show:   # start from a clean row: remove the parts of a previous --show run
    old = [c[3] for c in CASES + CASES_EXTRA + CASES_MORE] + ["pt_m4", "pt_m5"]
    run_code("import bpy\nfor n in %r:\n    for s in ('', '.001', '.002'):\n        o = bpy.data.objects.get(n + s)\n"
             "        if o: bpy.data.objects.remove(o, do_unlink=True)\n" % old)

def converse(request):
    t0 = time.time()
    msgs = ([{"role": "system", "content": system}] if system else []) + [{"role": "user", "content": request}]
    calls, final, infos = [], "", []
    for _ in range(a.max_rounds):
        body = {"messages": msgs, "tools": defs, "temperature": 0, "max_tokens": a.max_tokens,
                "chat_template_kwargs": {"enable_thinking": a.think}}
        t_req = time.time()
        resp = ev.http_json(a.url + "/v1/chat/completions", body, timeout=a.timeout)
        infos.append({"usage": resp.get("usage"), "timings": resp.get("timings"), "seconds": round(time.time() - t_req, 2)})
        m = resp["choices"][0]["message"]
        tcs = m.get("tool_calls") or []
        if not tcs:
            final = (m.get("content") or "").strip()
            break
        msgs.append({"role": "assistant", "content": m.get("content") or "", "tool_calls": tcs})
        for c in tcs:
            fn = c["function"]["name"]
            try:
                args = json.loads(c["function"].get("arguments") or "{}")
            except json.JSONDecodeError:
                args = {}
            try:
                text = tool_exec(fn, args)
            except Exception as e:
                text = "error: " + str(e)
            calls.append((fn.replace(P, ""), args, text))
            msgs.append({"role": "tool", "tool_call_id": c.get("id", ""), "content": text})
    return calls, final, time.time() - t0, infos


passed = failed = skipped = 0
slot = 0
rows = []                 # the evidence of this round: one entry per case, saved as JSON at the end
t_start = time.time()
for label, needs, request, obj, dims, vol, zr in ALL:
    if a.only and a.only.lower() not in label.lower():
        continue
    if not needs <= have:
        print(f"SKIP  {label}: needs {sorted(needs - have)}")
        skipped += 1
        continue
    calls, final, dt, infos = converse(request)

    measured, infra = None, None
    has_check = P + "check_mesh" in {d["function"]["name"] for d in defs}
    should_exist = any(c[0] == "create_box" and c[2].lstrip().startswith("OK") for c in calls)   # the part was created: it MUST be there
    for attempt in range(4 if should_exist else 1):
        try:
            scene = scene_now() if P + "list_objects" in {d["function"]["name"] for d in defs} else {}
            if obj in scene:
                r = tool_exec(P + "check_mesh", {"name": obj}) if has_check else ""
                mv, md = re.search(r"volume=([0-9.]+)", r), re.search(r"dimensions=\(([^)]*)\)", r)
                if mv:
                    measured = (float(mv.group(1)), tuple(round(float(x)) for x in md.group(1).split(",")) if md else None, "manifold=True" in r)
                    break
        except Exception as e:
            infra = f"Blender did not answer: {e.__class__.__name__}"
        if attempt < 3 and should_exist:
            time.sleep(20)   # Blender busy (another client, a slow boolean): wait and measure again
    if measured is None and should_exist:
        infra = infra or "the part was created but could not be measured"
    zmeasured = None
    if zr is not None and obj in scene:
        try:
            zmeasured = z_range(obj)
        except Exception:
            zmeasured = None
    z_ok = zr is None or (zmeasured is not None and all(abs(m_ - e_) < 0.01 for m_, e_ in zip(zmeasured, zr)))
    ok = bool(measured and measured[0] and measured[1] == dims and abs(measured[0] - vol) / vol < TOL and measured[2] and z_ok)
    passed += 1 if ok else 0
    failed += 0 if ok else 1
    print(f"{'PASS' if ok else 'FAIL'}  {label}  ({dt:.0f}s)")
    print(f"      request : {request}")
    for fn, args_, text in calls:
        print(f"      called  : {fn} {json.dumps(args_)} -> {text[:80].replace(chr(10), ' ')}")
    print(f"      expected: size={dims} volume~{vol:.1f} (tolerance {TOL * 100:.2f}%) | measured: {measured}")
    if infra:
        print(f"      INFRA   : {infra} (this is not a model error; re-run the case)")
    if zr is not None:
        print(f"      z range : expected {zr} | measured {zmeasured}")
    if final:
        print(f"      model   : {final[:150].replace(chr(10), ' ')}")
    print()
    rows.append({"case": label, "pass": ok, "seconds": round(dt, 1), "request": request, "object": obj, "needs": sorted(needs), "rounds": infos, "infra": infra,
                 "expected": {"size": dims, "volume": round(vol, 3), "z_range": zr},
                 "measured": {"volume": measured[0] if measured else None, "size": measured[1] if measured else None,
                              "manifold": measured[2] if measured else None, "z_range": zmeasured},
                 "calls": [{"tool": fn, "arguments": args_, "answer": text} for fn, args_, text in calls],
                 "model_final": final})
    try:
        if a.show:   # keep the part and put it in the row; extra copies Blender numbered (.001, .002) are removed
            ev.remove_objects(a.blender_mcp, [obj + ".001", obj + ".002"])
            place_in_row(obj, slot)
            frame_view()
            slot += 1
        else:        # remove ONLY what this case made (the name and the .001 copies Blender adds when a name is already taken)
            ev.remove_objects(a.blender_mcp, [obj, obj + ".001", obj + ".002"])
    except Exception as e:
        print("      (could not clean up / place: " + str(e) + ")")

if a.set in ("extra", "all", "more", "everything") or a.only:
    for label, request, check in QUESTIONS:
        if a.set == "basic" and not a.only:
            continue
        if a.only and a.only.lower() not in label.lower():
            continue
        request = _px(request)
        calls, final, dt, infos = converse(request)
        ok, why = check(calls, final, have)
        try:   # the questions that build a part leave nothing behind
            ev.remove_objects(a.blender_mcp, [n + sfx for n in (_px("pt_m4"), _px("pt_m5")) for sfx in ("", ".001", ".002")])
        except Exception:
            pass
        passed += 1 if ok else 0
        failed += 0 if ok else 1
        print(f"{'PASS' if ok else 'FAIL'}  {label}  ({dt:.0f}s)")
        print(f"      request : {request}")
        for fn, args_, text in calls:
            print(f"      called  : {fn} {json.dumps(args_)} -> {text[:80].replace(chr(10), ' ')}")
        print(f"      check   : {why}")
        print(f"      model   : {final[:200].replace(chr(10), ' ')}\n")
        rows.append({"case": label, "pass": ok, "seconds": round(dt, 1), "request": request, "object": None, "needs": [], "rounds": infos,
                     "expected": {"size": None, "volume": None, "z_range": None}, "measured": {"volume": None, "size": None, "manifold": None, "z_range": None},
                     "calls": [{"tool": fn, "arguments": args_, "answer": text} for fn, args_, text in calls], "model_final": final, "check": why})

if a.show:
    try:
        frame_view()
    except Exception:
        pass
    print("The parts are left in the Blender scene, in a row (--show).")
print(f"RESULT {a.label}: {passed} passed, {failed} failed, {skipped} skipped")

# The trace of the round, committed to git (see roundtrace.py): why it was run, what changed, the result, the code under test, every call
# and measurement, and the visual proof made by Blender itself (scene.blend, window.png, viewport.png).
import glob
previous = a.compare
if previous is None and not a.rounds_dir:
    found = sorted(glob.glob(os.path.join(here, "rounds", "*", "evidence.json")))
    previous = next((f for f in reversed(found) if '"rows"' in open(f, encoding="utf-8").read(200000)), None)
rd = rt.round_dir(a.rounds_dir or here, a.label)
shots = rt.capture(a.blender_mcp, rd) if a.show else {}
props = rt.http_json(a.url + "/props") or {}
evidence = {
    "label": a.label, "when": time.strftime("%Y-%m-%d %H:%M:%S"), "why": a.why, "changed": a.changed,
    "compare_to": os.path.relpath(previous, here) if previous else None,
    "git": rt.git_state(os.path.dirname(here)), "host": rt.host_info(), "blender_version": rt.blender_version(a.blender_mcp),
    "llama": {"url": a.url, "model_path": props.get("model_path"), "build_info": props.get("build_info"),
              "total_slots": props.get("total_slots"), "n_ctx": (props.get("default_generation_settings") or {}).get("n_ctx")},
    "tolerance": TOL, "think": a.think, "show": a.show,
    "system_prompt": os.path.basename(a.system) if system else None,
    "system_prompt_sha256": hashlib.sha256(system.encode()).hexdigest() if system else None,
    "tools_seen_by_the_model": {d["function"]["name"].replace(P, ""): d["function"] for d in defs},
    "passed": passed, "failed": failed, "skipped": skipped, "seconds": round(time.time() - t_start), "rows": rows,
}
rt.write_round(rd, label=a.label, why=a.why, changed=a.changed, result_line=f"{passed} passed, {failed} failed, {skipped} skipped",
               evidence=evidence, output_lines=_log, prompt=system, compare_to=previous, rows=rows, files=shots)
print("trace   :", rd)
for d in (a.save_blend or []):
    try:
        shutil.copytree(rd, os.path.join(os.path.expanduser(d), os.path.basename(rd)), dirs_exist_ok=True)
        print("copy    :", os.path.join(os.path.expanduser(d), os.path.basename(rd)))
    except Exception as e:
        print("copy    : could not copy to", d, "->", e)
sys.exit(1 if failed else 0)
