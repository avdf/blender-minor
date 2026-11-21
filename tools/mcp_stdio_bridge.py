#!/usr/bin/env python3
"""stdio -> HTTP bridge for MCP.

llama-server can only start MCP servers as processes (stdio, "command" entries), while blender-minor speaks HTTP. This script
reads one JSON-RPC message per line on stdin, POSTs it to the URL and writes the answer (one line) to stdout.

Optional allow list: with --allow the model only sees (and can only call) the tools you name. Small models do better with a short
tool list, and llama-server has no allow list of its own.

Optional --hide tool.param,...: the model does not see those parameters and cannot send them. A small model fills optional
parameters on impulse (Qwen3-1.7B kept sending axis="X" to drill_hole, even when the prompt said not to), so for a job that never
needs them it is safer, and cheaper in tokens, to take them away.

  python3 -I mcp_stdio_bridge.py http://127.0.0.1:9192/
  python3 -I mcp_stdio_bridge.py http://127.0.0.1:9192/ --allow create_box,drill_hole,screw_hole,list_objects
  python3 -I mcp_stdio_bridge.py http://127.0.0.1:9192/ --allow create_box,drill_hole --hide drill_hole.axis,drill_hole.side
"""
import argparse, json, sys, urllib.request

ap = argparse.ArgumentParser()
ap.add_argument("url", nargs="?", default="http://127.0.0.1:9192/")
ap.add_argument("--allow", default="", help="comma-separated tool names; empty means every tool")
ap.add_argument("--hide", default="", help="comma-separated tool.param pairs to hide from the model, e.g. drill_hole.axis")
args = ap.parse_args()
URL = args.url
ALLOW = {n.strip() for n in args.allow.split(",") if n.strip()}
HIDE = {}
for pair in args.hide.split(","):
    if "." in pair.strip():
        tool, param = pair.strip().split(".", 1)
        HIDE.setdefault(tool, set()).add(param)


def reply(msg_id, result=None, error=None):
    out = {"jsonrpc": "2.0", "id": msg_id}
    out.update({"error": error} if error else {"result": result})
    print(json.dumps(out), flush=True)


for line in sys.stdin:
    line = line.strip()
    if not line:
        continue
    try:
        msg = json.loads(line)
    except json.JSONDecodeError:
        continue
    method = msg.get("method")
    # a call to a tool outside the allow list never reaches blender-minor
    if ALLOW and method == "tools/call" and "id" in msg:
        name = (msg.get("params") or {}).get("name")
        if name not in ALLOW:
            reply(msg["id"], {"content": [{"type": "text", "text": f"Tool '{name}' is not available. Tools: {', '.join(sorted(ALLOW))}."}], "isError": True})
            continue
    # a hidden parameter is dropped even if the model sends it anyway
    if HIDE and method == "tools/call":
        p = msg.get("params") or {}
        hidden = HIDE.get(p.get("name"), set())
        if hidden and isinstance(p.get("arguments"), dict) and hidden & set(p["arguments"]):
            for k in hidden:
                p["arguments"].pop(k, None)
            line = json.dumps(msg)
    req = urllib.request.Request(
        URL, data=line.encode(),
        headers={"Content-Type": "application/json", "Accept": "application/json, text/event-stream"})
    try:
        with urllib.request.urlopen(req, timeout=300) as r:
            status, body = r.status, r.read().decode()
    except Exception as e:  # server down etc.: answer with an error if it was a call (it has an id)
        if "id" in msg:
            reply(msg["id"], error={"code": -32000, "message": str(e)})
        continue
    if "id" not in msg or status == 202 or not body.strip():
        continue  # notification: no answer
    if "data:" in body:
        body = "".join(l[5:].strip() for l in body.splitlines() if l.startswith("data:"))
    if (ALLOW or HIDE) and method == "tools/list":
        try:
            d = json.loads(body)
            tools = [t for t in d["result"]["tools"] if not ALLOW or t.get("name") in ALLOW]
            for t in tools:
                hidden = HIDE.get(t.get("name"), set())
                if hidden:
                    schema = t.get("inputSchema") or {}
                    schema["properties"] = {k: v for k, v in (schema.get("properties") or {}).items() if k not in hidden}
                    if "required" in schema:
                        schema["required"] = [r for r in schema["required"] if r not in hidden]
            d["result"]["tools"] = tools
            body = json.dumps(d)
        except (json.JSONDecodeError, KeyError, TypeError):
            pass
    print(body.replace("\n", " "), flush=True)
