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
import argparse, hashlib, json, math, os, re, sys, time, urllib.request

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))   # python -I does not add the script folder
import evaluate_model as ev                                        # reuse its small HTTP helpers

here = os.path.dirname(os.path.abspath(__file__))
ap = argparse.ArgumentParser()
ap.add_argument("--url", default="http://127.0.0.1:5080", help="llama-server")
ap.add_argument("--blender-mcp", default="http://127.0.0.1:9191/")
ap.add_argument("--system", default=os.path.join(here, "..", "prompts", "system.parts.en.txt"), help="file with the system prompt")
ap.add_argument("--label", required=True)
ap.add_argument("--tool-prefix", default="blender-minor_")
ap.add_argument("--think", action="store_true", help="enable Qwen3 reasoning")
ap.add_argument("--max-rounds", type=int, default=5)
ap.add_argument("--max-tokens", type=int, default=400)
ap.add_argument("--only", default="", help="run only the cases whose label contains this text (e.g. --only holes)")
ap.add_argument("--show", action="store_true",
                help="keep the parts in the Blender scene, side by side in a row (each in its own colour), and frame the 3D view at "
                     "the end, so you can look at them in the open Blender window; the previous pt_* objects are removed first")
ap.add_argument("--spacing", type=float, default=130.0, help="distance between the parts of the row with --show (default 130)")
a = ap.parse_args()
P = a.tool_prefix


def box_minus(w, d, h, holes=(), cuts=0.0, adds=0.0):
    return w * d * h - sum(math.pi * (dia / 2) ** 2 * h for dia in holes) - cuts + adds


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
]


def run_code(code):
    """Runs Python in Blender through the Blender MCP (execute_blender_code) and returns the raw answer text."""
    body = {"jsonrpc": "2.0", "id": 1, "method": "tools/call", "params": {"name": "execute_blender_code", "arguments": {"code": code}}}
    req = urllib.request.Request(a.blender_mcp, data=json.dumps(body).encode(),
                                 headers={"Content-Type": "application/json", "Accept": "application/json, text/event-stream"})
    return urllib.request.urlopen(req, timeout=60).read().decode()


def z_range(obj):
    """Lowest and highest z of the object in world space."""
    text = run_code("import bpy\nfrom mathutils import Vector\no = bpy.data.objects['%s']\n"
                    "zs = [(o.matrix_world @ Vector(c)).z for c in o.bound_box]\nprint('ZRANGE', min(zs), max(zs))" % obj)
    m = re.search(r"ZRANGE (-?[0-9.]+) (-?[0-9.]+)", text.replace("\\n", " "))
    return (float(m.group(1)), float(m.group(2))) if m else None


PALETTE = [(0.9, 0.35, 0.2, 1), (0.2, 0.6, 0.9, 1), (0.3, 0.8, 0.4, 1), (0.95, 0.8, 0.2, 1), (0.7, 0.4, 0.9, 1)]


def place_in_row(obj, slot):
    """--show: centre the part on x = slot * spacing, y = 0 and give it its own colour, so every part stays visible."""
    r, g, b, al = PALETTE[slot % len(PALETTE)]
    run_code("import bpy\nfrom mathutils import Vector\no = bpy.data.objects.get('%s')\nif o is not None:\n"
             "    cs = [o.matrix_world @ Vector(c) for c in o.bound_box]\n"
             "    o.location.x += %r - (min(c.x for c in cs) + max(c.x for c in cs)) / 2\n"
             "    o.location.y += 0.0 - (min(c.y for c in cs) + max(c.y for c in cs)) / 2\n"
             "    m = bpy.data.materials.new('%s_color'); m.diffuse_color = (%r, %r, %r, %r)\n"
             "    o.data.materials.clear(); o.data.materials.append(m)\n" % (obj, slot * a.spacing, obj, r, g, b, al))


def frame_view():
    """--show: frame everything in the 3D view of the open Blender window."""
    run_code("import bpy\nwin = bpy.context.window_manager.windows[0]\nfor area in win.screen.areas:\n"
             "    if area.type == 'VIEW_3D':\n"
             "        region = next(r for r in area.regions if r.type == 'WINDOW')\n"
             "        with bpy.context.temp_override(window=win, area=area, region=region):\n"
             "            bpy.ops.view3d.view_all()\n"
             "        break\n")

defs = [t["definition"] for t in ev.http_json(a.url + "/tools", timeout=20)]
have = {d["function"]["name"].replace(P, "") for d in defs}
system = open(a.system, encoding="utf-8").read() if a.system and os.path.exists(a.system) else None
print(f"== {a.label} | system: {os.path.basename(a.system) if system else '-'} | think: {a.think} | tools: {sorted(have)}\n")

if a.show:   # start from a clean row: remove the parts of a previous --show run
    old = [c[3] for c in CASES]
    run_code("import bpy\nfor n in %r:\n    for s in ('', '.001', '.002'):\n        o = bpy.data.objects.get(n + s)\n"
             "        if o: bpy.data.objects.remove(o, do_unlink=True)\n" % old)

passed = failed = skipped = 0
slot = 0
rows = []                 # the evidence of this round: one entry per case, saved as JSON at the end
t_start = time.time()
for label, needs, request, obj, dims, vol, zr in CASES:
    if a.only and a.only.lower() not in label.lower():
        continue
    if not needs <= have:
        print(f"SKIP  {label}: needs {sorted(needs - have)}")
        skipped += 1
        continue
    t0 = time.time()
    msgs = ([{"role": "system", "content": system}] if system else []) + [{"role": "user", "content": request}]
    calls, final = [], ""
    for _ in range(a.max_rounds):
        body = {"messages": msgs, "tools": defs, "temperature": 0, "max_tokens": a.max_tokens,
                "chat_template_kwargs": {"enable_thinking": a.think}}
        m = ev.http_json(a.url + "/v1/chat/completions", body)["choices"][0]["message"]
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
                text = ev.call_tool(a.url, fn, args)
            except Exception as e:
                text = "error: " + str(e)
            calls.append((fn.replace(P, ""), args, text))
            msgs.append({"role": "tool", "tool_call_id": c.get("id", ""), "content": text})
    dt = time.time() - t0

    measured = None
    scene = ev.list_scene(a.url, P) if P + "list_objects" in {d["function"]["name"] for d in defs} else {}
    if obj in scene:
        r = ev.call_tool(a.url, P + "check_mesh", {"name": obj}) if P + "check_mesh" in {d["function"]["name"] for d in defs} else ""
        mv, md = re.search(r"volume=([0-9.]+)", r), re.search(r"dimensions=\(([^)]*)\)", r)
        measured = (float(mv.group(1)) if mv else None,
                    tuple(round(float(x)) for x in md.group(1).split(",")) if md else None, "manifold=True" in r)
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
    if zr is not None:
        print(f"      z range : expected {zr} | measured {zmeasured}")
    if final:
        print(f"      model   : {final[:150].replace(chr(10), ' ')}")
    print()
    rows.append({"case": label, "pass": ok, "seconds": round(dt, 1), "request": request, "object": obj,
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

if a.show:
    try:
        frame_view()
    except Exception:
        pass
    print("The parts are left in the Blender scene, in a row (--show).")
print(f"RESULT {a.label}: {passed} passed, {failed} failed, {skipped} skipped")

# Evidence of the round, like evaluate_model.py (eval/results/ is not committed): what the model saw, every call, every measurement.
os.makedirs(os.path.join(here, "results"), exist_ok=True)
out = os.path.join(here, "results", f"parts-{a.label}-{time.strftime('%Y%m%d-%H%M%S')}.json")
json.dump({"label": a.label, "when": time.strftime("%Y-%m-%d %H:%M:%S"), "llama_server": a.url, "tolerance": TOL,
           "system_prompt": os.path.basename(a.system) if system else None,
           "system_prompt_sha256": hashlib.sha256(system.encode()).hexdigest() if system else None,
           "think": a.think, "show": a.show,
           "tools_seen_by_the_model": {d["function"]["name"].replace(P, ""): list(d["function"]["parameters"].get("properties", {})) for d in defs},
           "passed": passed, "failed": failed, "skipped": skipped, "seconds": round(time.time() - t_start), "rows": rows},
          open(out, "w"), ensure_ascii=False, indent=1)
print("evidence:", out)
sys.exit(1 if failed else 0)
