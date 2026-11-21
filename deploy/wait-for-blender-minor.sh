#!/bin/bash
# Waits until blender-minor LISTS ITS TOOLS (an open port is not enough: it can accept connections before it can answer).
# Used as ExecStartPre of the llama-server unit, because llama-server only discovers the MCP tools when it starts.
#   wait-for-blender-minor.sh http://host:9192/
url="${1:?usage: $0 http://host:9192/}"
for i in $(seq 1 60); do
  curl -s -m 5 -X POST -H 'Content-Type: application/json' -H 'Accept: application/json, text/event-stream' \
       -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}' "$url" | grep -q create_box && exit 0
  sleep 2
done
echo "blender-minor did not list its tools in 2 minutes" >&2
exit 1
