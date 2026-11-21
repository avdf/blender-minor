#!/usr/bin/env python3
"""Ask the small model for something by hand, and watch it happen in the open Blender window.

It does what evaluate_parts.py does for ONE request, with the same system prompt and the same tools: it sends your sentence to the
llama-server, runs every tool call the model makes (through the same MCP bridge the server uses), and prints each call with its answer.
Nothing is measured or deleted: the part stays in the Blender scene for you to look at.

Usage:
  python3 -I eval/ask.py "Make a box named foot, 40 by 30 by 50, with a 6 mm hole along X at y=0, z=10"
  python3 -I eval/ask.py --url http://host:9931 "a box named plate, 80 by 50 by 6, with an M3 hole at x=10, y=5"
  python3 -I eval/ask.py            # no sentence: asks, then keeps asking until you press Enter on an empty line
"""
import argparse, json, os, sys, time

here = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, here)   # python -I does not add the script folder
import evaluate_model as ev

ap = argparse.ArgumentParser()
ap.add_argument("request", nargs="*", help="what to ask (omit it to be asked)")
ap.add_argument("--url", default="http://127.0.0.1:5080", help="llama-server")
ap.add_argument("--system", default=os.path.join(here, "..", "prompts", "system.parts.en.txt"), help="file with the system prompt")
ap.add_argument("--tool-prefix", default="blender-minor_")
ap.add_argument("--max-rounds", type=int, default=6)
ap.add_argument("--max-tokens", type=int, default=400)
a = ap.parse_args()

defs = [t["definition"] for t in ev.http_json(a.url + "/tools", timeout=20)]
system = open(a.system, encoding="utf-8").read() if os.path.exists(a.system) else None
print(f"model server {a.url} | {len(defs)} tools: " + ", ".join(sorted(d["function"]["name"].replace(a.tool_prefix, "") for d in defs)) + "\n")


def ask(request):
    t0 = time.time()
    msgs = ([{"role": "system", "content": system}] if system else []) + [{"role": "user", "content": request}]
    for _ in range(a.max_rounds):
        body = {"messages": msgs, "tools": defs, "temperature": 0, "max_tokens": a.max_tokens, "chat_template_kwargs": {"enable_thinking": False}}
        m = ev.http_json(a.url + "/v1/chat/completions", body)["choices"][0]["message"]
        calls = m.get("tool_calls") or []
        if not calls:
            print(f"\nmodel: {(m.get('content') or '').strip()}   ({time.time() - t0:.0f} s)\n")
            return
        msgs.append({"role": "assistant", "content": m.get("content") or "", "tool_calls": calls})
        for c in calls:
            fn = c["function"]["name"]
            try:
                args = json.loads(c["function"].get("arguments") or "{}")
            except json.JSONDecodeError:
                args = {}
            try:
                text = ev.call_tool(a.url, fn, args)
            except Exception as e:
                text = "error: " + str(e)
            print(f"  call  {fn.replace(a.tool_prefix, '')} {json.dumps(args)}\n  ->    {text[:200].replace(chr(10), ' ')}")
            msgs.append({"role": "tool", "tool_call_id": c.get("id", ""), "content": text})
    print("\n(stopped: too many rounds)\n")


if a.request:
    ask(" ".join(a.request))
else:
    while True:
        try:
            r = input("you> ").strip()
        except EOFError:
            break
        if not r:
            break
        ask(r)
