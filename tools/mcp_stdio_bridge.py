#!/usr/bin/env python3
"""stdio -> HTTP bridge for MCP.

llama-server can only start MCP servers as processes (stdio, "command" entries), while blender-minor speaks HTTP. This script
reads one JSON-RPC message per line on stdin, POSTs it to the URL and writes the answer (one line) to stdout.

Optional allow list: with --allow the model only sees (and can only call) the tools you name. Small models do better with a short
tool list, and llama-server has no allow list of its own.

  python3 -I mcp_stdio_bridge.py http://127.0.0.1:9192/
  python3 -I mcp_stdio_bridge.py http://127.0.0.1:9192/ --allow create_box,drill_hole,screw_hole,list_objects
"""
import argparse, json, sys, urllib.request

ap = argparse.ArgumentParser()
ap.add_argument("url", nargs="?", default="http://127.0.0.1:9192/")
ap.add_argument("--allow", default="", help="comma-separated tool names; empty means every tool")
args = ap.parse_args()
URL = args.url
ALLOW = {n.strip() for n in args.allow.split(",") if n.strip()}


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
    if ALLOW and method == "tools/list":
        try:
            d = json.loads(body)
            d["result"]["tools"] = [t for t in d["result"]["tools"] if t.get("name") in ALLOW]
            body = json.dumps(d)
        except (json.JSONDecodeError, KeyError, TypeError):
            pass
    print(body.replace("\n", " "), flush=True)
