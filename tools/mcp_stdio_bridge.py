#!/usr/bin/env python3
# Ponte stdio -> HTTP para MCP: o llama-server so sobe MCPs por "command" (stdio); o blender-minor fala HTTP.
# Le uma mensagem JSON-RPC por linha no stdin, manda por POST para a URL e escreve a resposta (uma linha) no stdout.
# Uso: python3 -I llama-mcp-bridge.py http://127.0.0.1:9192/
import sys, json, urllib.request

URL = sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:9192/"

for line in sys.stdin:
    line = line.strip()
    if not line:
        continue
    try:
        msg = json.loads(line)
    except json.JSONDecodeError:
        continue
    req = urllib.request.Request(
        URL, data=line.encode(),
        headers={"Content-Type": "application/json", "Accept": "application/json, text/event-stream"})
    try:
        with urllib.request.urlopen(req, timeout=300) as r:
            status, body = r.status, r.read().decode()
    except Exception as e:  # servidor fora do ar etc.: responde erro se for uma chamada (tem id)
        if "id" in msg:
            print(json.dumps({"jsonrpc": "2.0", "id": msg["id"], "error": {"code": -32000, "message": str(e)}}), flush=True)
        continue
    if "id" not in msg or status == 202 or not body.strip():
        continue  # notificacao: sem resposta
    if "data:" in body:
        body = "".join(l[5:].strip() for l in body.splitlines() if l.startswith("data:"))
    print(body.replace("\n", " "), flush=True)
