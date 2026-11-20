#!/usr/bin/env python3
"""Evaluates how well a model served by llama-server uses the blender-minor tools.

For each scenario it sends the request, lets the model call tools (up to N rounds), runs the calls through the
llama-server POST /tools endpoint, and then CHECKS THE SCENE (list_objects) to see whether the object exists and sits at
the right position. After each scenario it removes ONLY the objects this test created.

Usage:
  python3 -I evaluate_model.py --label 0.6B-no-prompt
  python3 -I evaluate_model.py --label 0.6B-with-prompt --system ../prompts/system.en.txt
  python3 -I evaluate_model.py --label 0.6B-en-requests --system ../prompts/system.en.txt --lang en
Options: --url (llama-server, default http://127.0.0.1:5080)  --blender-mcp (default http://127.0.0.1:9191/)
         --think (turn on Qwen3 reasoning; off by default)  --scenarios N  --no-clean  --lang pt|en  --tool-prefix
"""
import argparse, json, os, re, sys, time, urllib.request

# (label, request in Portuguese, request in English, tool, expected arguments)
SCENARIOS = [
    ("blue sphere",    "Crie uma esfera azul de raio 3 na posição 5, 0, 0.", "Create a blue sphere with radius 3 at position 5, 0, 0.", "create_sphere", {"radius": 3, "x": 5, "y": 0, "z": 0, "color": "blue"}),
    ("red cube",       "Crie um cubo vermelho de tamanho 2 na posição -5, 0, 0.", "Create a red cube of size 2 at position -5, 0, 0.", "create_cube", {"size": 2, "x": -5, "y": 0, "z": 0, "color": "red"}),
    ("green cylinder", "Crie um cilindro verde de altura 4 e raio 1 na posição 0, 5, 0.", "Create a green cylinder with height 4 and radius 1 at position 0, 5, 0.", "create_cylinder", {"height": 4, "radius": 1, "x": 0, "y": 5, "z": 0, "color": "green"}),
    ("yellow cone",    "Crie um cone amarelo de altura 3 e raio 2 na posição 0, -5, 0.", "Create a yellow cone with height 3 and radius 2 at position 0, -5, 0.", "create_cone", {"height": 3, "radius": 2, "x": 0, "y": -5, "z": 0, "color": "yellow"}),
    ("orange torus",   "Crie um toro laranja com raio maior 2 e raio menor 0.5 na posição 0, 0, 5.", "Create an orange torus with major radius 2 and minor radius 0.5 at position 0, 0, 5.", "create_torus", {"major_radius": 2, "minor_radius": 0.5, "x": 0, "y": 0, "z": 5, "color": "orange"}),
    ("red sphere",     "Crie uma esfera vermelha de raio 1 na posição 10, 10, 0.", "Create a red sphere with radius 1 at position 10, 10, 0.", "create_sphere", {"radius": 1, "x": 10, "y": 10, "z": 0, "color": "red"}),
    ("blue cube",      "Crie um cubo azul de tamanho 4 na posição 0, 0, 10.", "Create a blue cube of size 4 at position 0, 0, 10.", "create_cube", {"size": 4, "x": 0, "y": 0, "z": 10, "color": "blue"}),
    ("purple sphere",  "Crie uma esfera roxa de raio 2 na posição -10, 5, 0.", "Create a purple sphere with radius 2 at position -10, 5, 0.", "create_sphere", {"radius": 2, "x": -10, "y": 5, "z": 0, "color": "purple"}),
    ("blue cylinder",  "Crie um cilindro azul de altura 6 e raio 2 na posição 8, -8, 0.", "Create a blue cylinder with height 6 and radius 2 at position 8, -8, 0.", "create_cylinder", {"height": 6, "radius": 2, "x": 8, "y": -8, "z": 0, "color": "blue"}),
    ("sphere #00FF00", "Crie uma esfera com a cor #00FF00 de raio 2 na posição 3, 3, 3.", "Create a sphere with color #00FF00 and radius 2 at position 3, 3, 3.", "create_sphere", {"radius": 2, "x": 3, "y": 3, "z": 3, "color": "#00FF00"}),
    ("white cone",     "Crie um cone branco de altura 5 e raio 1 na posição -8, -8, 0.", "Create a white cone with height 5 and radius 1 at position -8, -8, 0.", "create_cone", {"height": 5, "radius": 1, "x": -8, "y": -8, "z": 0, "color": "white"}),
    ("list scene",     "Quais objetos existem na cena agora?", "Which objects exist in the scene right now?", "list_objects", {}),
]
LINE = re.compile(r"^(.*) ([A-Z_]+) \(([-0-9.]+), ([-0-9.]+), ([-0-9.]+)\)$")
CLAIMS = re.compile(r"foi criad|criei|criada|criado|was created|has been created|i created|created a", re.I)
# the tool accepts English and Portuguese color names
COLOR_SYNONYMS = {"blue": "azul", "red": "vermelho", "green": "verde", "yellow": "amarelo", "orange": "laranja", "purple": "roxo",
                  "pink": "rosa", "white": "branco", "black": "preto", "gray": "cinza", "brown": "marrom", "cyan": "ciano"}
FEMININE = {"roxo": "roxa", "vermelho": "vermelha", "amarelo": "amarela", "branco": "branca", "preto": "preta"}


def http_json(url, body=None, timeout=600):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(url, data=data, headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return json.loads(r.read().decode())


def call_tool(base, name, params):
    r = http_json(base + "/tools", {"tool": name, "params": params}, timeout=300)
    return r.get("plain_text_response") if isinstance(r, dict) and "plain_text_response" in r else json.dumps(r, ensure_ascii=False)


def list_scene(base, prefix):
    scene = {}
    for line in call_tool(base, prefix + "list_objects", {}).splitlines():
        m = LINE.match(line.strip())
        if m:
            scene[m.group(1)] = (m.group(2), float(m.group(3)), float(m.group(4)), float(m.group(5)))
    return scene


def remove_objects(blender_mcp, names):
    if not names:
        return
    code = "import bpy\nfor n in %r:\n    o = bpy.data.objects.get(n)\n    if o: bpy.data.objects.remove(o, do_unlink=True)" % list(names)
    body = {"jsonrpc": "2.0", "id": 1, "method": "tools/call", "params": {"name": "execute_blender_code", "arguments": {"code": code}}}
    req = urllib.request.Request(blender_mcp, data=json.dumps(body).encode(),
                                 headers={"Content-Type": "application/json", "Accept": "application/json, text/event-stream"})
    urllib.request.urlopen(req, timeout=60).read()


def check_args(expected, got):
    res = {}
    for k, v in expected.items():
        g = got.get(k)
        if k == "color":
            en = str(v).lower()
            pt = COLOR_SYNONYMS.get(en, en)
            accepted = {en, pt, FEMININE.get(pt, pt)}
            res[k] = isinstance(g, str) and g.strip().lower() in accepted
        else:
            try:
                res[k] = abs(float(g) - float(v)) < 1e-6
            except (TypeError, ValueError):
                res[k] = False
    return res


def main():
    ap = argparse.ArgumentParser(description="Evaluate tool use of a model served by llama-server against blender-minor.")
    ap.add_argument("--url", default="http://127.0.0.1:5080")
    ap.add_argument("--blender-mcp", default="http://127.0.0.1:9191/")
    ap.add_argument("--label", required=True, help="name of this run (used in the output file)")
    ap.add_argument("--system", default=None, help="file with the system prompt")
    ap.add_argument("--lang", choices=["pt", "en"], default="pt", help="language of the user requests (default pt)")
    ap.add_argument("--tool-prefix", default="blender-minor_", help="prefix llama-server adds to the tool names")
    ap.add_argument("--think", action="store_true", help="enable Qwen3 reasoning")
    ap.add_argument("--max-rounds", type=int, default=4)
    ap.add_argument("--max-tokens", type=int, default=400)
    ap.add_argument("--scenarios", type=int, default=len(SCENARIOS))
    ap.add_argument("--no-clean", action="store_true")
    a = ap.parse_args()
    P = a.tool_prefix

    system = open(os.path.expanduser(a.system), encoding="utf-8").read() if a.system else None
    defs = [t["definition"] for t in http_json(a.url + "/tools", timeout=20)]
    rows, t_start = [], time.time()
    print(f"== {a.label} | system: {a.system or '-'} | requests: {a.lang} | think: {a.think} | {len(defs)} tools")
    print(f"{'scenario':<16} {'s':>5}  called  right  args  created  pos   notes")

    for name, req_pt, req_en, expected_tool, exp_args in SCENARIOS[: a.scenarios]:
        request = req_pt if a.lang == "pt" else req_en
        t0 = time.time()
        before = list_scene(a.url, P)
        msgs = ([{"role": "system", "content": system}] if system else []) + [{"role": "user", "content": request}]
        calls, final = [], ""
        for _ in range(a.max_rounds):
            body = {"messages": msgs, "tools": defs, "temperature": 0, "max_tokens": a.max_tokens,
                    "chat_template_kwargs": {"enable_thinking": a.think}}
            m = http_json(a.url + "/v1/chat/completions", body)["choices"][0]["message"]
            tcs = m.get("tool_calls") or []
            if not tcs:
                final = m.get("content") or ""
                break
            msgs.append({"role": "assistant", "content": m.get("content") or "", "tool_calls": tcs})
            for c in tcs:
                fn = c["function"]["name"]
                try:
                    args = json.loads(c["function"].get("arguments") or "{}")
                except json.JSONDecodeError:
                    args = {}
                try:
                    text = call_tool(a.url, fn, args)
                except Exception as e:  # the tool call itself failed
                    text = "error: " + str(e)
                calls.append((fn, args, text))
                msgs.append({"role": "tool", "tool_call_id": c.get("id", ""), "content": text})
        after = list_scene(a.url, P)
        new = [n for n in after if n not in before]

        target = P + expected_tool
        of_target = [c for c in calls if c[0] == target]
        called = bool(calls)
        right_tool = bool(of_target)
        args_ok = pos_ok = created = None
        notes = ""
        if expected_tool == "list_objects":
            ok = right_tool and all(n in final for n in before)
            args_ok, created, pos_ok = (True if right_tool else False), None, ok
            notes = "final answer cites every object" if ok else "final answer does NOT cite every object"
        else:
            if right_tool:
                check = check_args(exp_args, of_target[0][1])
                args_ok = all(check.values())
                notes = "wrong: " + ",".join(k for k, v in check.items() if not v) if not args_ok else ""
            created = len(new) >= 1
            if created:
                want = (exp_args["x"], exp_args["y"], exp_args["z"])
                pos_ok = any(all(abs(after[n][i + 1] - want[i]) < 0.05 for i in range(3)) for n in new)
            if not called and CLAIMS.search(final):
                notes = "CLAIMED it created something without calling a tool"
        secs = time.time() - t0
        f = lambda v: "-" if v is None else ("yes" if v else "NO")
        print(f"{name:<16} {secs:5.0f}  {f(called):>6}  {f(right_tool):>5}  {f(args_ok):>4}  {f(created):>7}  {f(pos_ok):>4}  {notes}", flush=True)
        rows.append({"scenario": name, "request": request, "seconds": round(secs, 1), "called": called, "right_tool": right_tool,
                     "args_ok": args_ok, "created": created, "position_ok": pos_ok, "notes": notes,
                     "calls": [{"tool": c[0], "args": c[1], "result": c[2][:120]} for c in calls], "final": final[:300]})
        if not a.no_clean:
            remove_objects(a.blender_mcp, new)

    n = len(rows)
    count = lambda k: sum(1 for r in rows if r[k])
    cr = [r for r in rows if r["created"] is not None]
    print(f"\nSUMMARY {a.label}: called a tool {count('called')}/{n} | right tool {count('right_tool')}/{n} | "
          f"all arguments right {count('args_ok')}/{n} | created for real {sum(1 for r in cr if r['created'])}/{len(cr)} | "
          f"position right {sum(1 for r in rows if r['position_ok'])}/{n} | {time.time() - t_start:.0f} s total")
    here = os.path.dirname(os.path.abspath(__file__))
    os.makedirs(os.path.join(here, "results"), exist_ok=True)
    out = os.path.join(here, "results", f"{a.label}-{time.strftime('%Y%m%d-%H%M%S')}.json")
    json.dump({"label": a.label, "system": a.system, "lang": a.lang, "think": a.think, "rows": rows}, open(out, "w"), ensure_ascii=False, indent=1)
    print("details:", out)


if __name__ == "__main__":
    main()
