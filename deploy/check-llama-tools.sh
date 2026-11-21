#!/bin/bash
# Fails when the llama-server found NO MCP tools. It discovers them only at start-up; if the discovery failed (a slow boot, the other
# machine not ready) it still starts, looks healthy, and the model is useless. Used as ExecStartPost: the unit fails and systemd
# starts it again. While the server is still loading the model, /tools does not answer yet: keep waiting.
#   check-llama-tools.sh 9931
port="${1:-9931}"
for i in $(seq 1 90); do
  n=$(curl -s -m 5 "http://127.0.0.1:$port/tools" | python3 -I -c 'import sys,json; print(len(json.load(sys.stdin)))' 2>/dev/null)
  if [ -n "$n" ]; then
    [ "$n" -ge 1 ] && exit 0
    echo "llama-server on port $port discovered no MCP tools" >&2
    exit 1
  fi
  sleep 2
done
echo "llama-server on port $port did not answer in 3 minutes" >&2
exit 1
