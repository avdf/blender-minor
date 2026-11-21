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
import argparse, json, math, os, re, sys, time

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
a = ap.parse_args()
P = a.tool_prefix


def box_minus(w, d, h, holes=(), cuts=0.0, adds=0.0):
    return w * d * h - sum(math.pi * (dia / 2) ** 2 * h for dia in holes) - cuts + adds


# (label, tools it needs, request, object name, expected size, expected volume)
CASES = [
    ("box + M3 hole", {"create_box", "screw_hole"},
     "Make a box named pt_base, 60 by 40 by 8, with an M3 hole at x=10, y=5",
     "pt_base", (60, 40, 8), box_minus(60, 40, 8, [3.4])),
    ("box + 5 mm hole", {"create_box", "drill_hole"},
     "Create a box named pt_plate, 80 by 50 by 6, and drill a 5 mm hole at x=-20, y=10.",
     "pt_plate", (80, 50, 6), box_minus(80, 50, 6, [5.0])),
    ("box + 2 holes", {"create_box", "drill_hole"},
     "Make a box named pt_bracket, 30 by 30 by 10. Drill a 4 mm hole at x=-8, y=0 and another 4 mm hole at x=8, y=0.",
     "pt_bracket", (30, 30, 10), box_minus(30, 30, 10, [4.0, 4.0])),
    ("pad (raise a strip)", {"create_box", "pad"},
     "Make a box named pt_strip, 100 by 10 by 2. Then raise a rectangle on its top face from x=-50 to x=-30 and from y=-4 to y=4, 2 high.",
     "pt_strip", (100, 10, 4), 100 * 10 * 2 + 20 * 8 * 2),
    ("pocket (cut a recess)", {"create_box", "pocket"},
     "Make a box named pt_block, 40 by 40 by 10. Cut a pocket in its top face from x=-10 to x=10 and from y=-10 to y=10, 3 deep.",
     "pt_block", (40, 40, 10), 40 * 40 * 10 - 20 * 20 * 3),
]

defs = [t["definition"] for t in ev.http_json(a.url + "/tools", timeout=20)]
have = {d["function"]["name"].replace(P, "") for d in defs}
system = open(a.system, encoding="utf-8").read() if a.system and os.path.exists(a.system) else None
print(f"== {a.label} | system: {os.path.basename(a.system) if system else '-'} | think: {a.think} | tools: {sorted(have)}\n")

passed = failed = skipped = 0
for label, needs, request, obj, dims, vol in CASES:
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
    ok = bool(measured and measured[0] and measured[1] == dims and abs(measured[0] - vol) / vol < 0.01 and measured[2])
    passed += 1 if ok else 0
    failed += 0 if ok else 1
    print(f"{'PASS' if ok else 'FAIL'}  {label}  ({dt:.0f}s)")
    print(f"      request : {request}")
    for fn, args_, text in calls:
        print(f"      called  : {fn} {json.dumps(args_)} -> {text[:80].replace(chr(10), ' ')}")
    print(f"      expected: size={dims} volume~{vol:.1f} | measured: {measured}")
    if final:
        print(f"      model   : {final[:150].replace(chr(10), ' ')}")
    print()
    try:   # remove ONLY what this case made (the name and the .001 copies Blender adds when a name is already taken)
        ev.remove_objects(a.blender_mcp, [obj, obj + ".001", obj + ".002"])
    except Exception as e:
        print("      (could not clean up: " + str(e) + ")")

print(f"RESULT {a.label}: {passed} passed, {failed} failed, {skipped} skipped")
sys.exit(1 if failed else 0)
