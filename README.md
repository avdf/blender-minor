# blender-minor

**BMLS: Blender Minor LLM Shortcuts.** A small MCP server (.NET) with safe shortcuts for Blender, made for **small
language models** (roughly 0.5B to 4B parameters, even on an old CPU).

> Português: veja [README.pt-BR.md](README.pt-BR.md).

The model does **not write Python**. It picks a tool by name and fills in a few flat fields:

```
create_sphere(radius=2, x=3, y=-5, color="blue")
move_object(name="Sphere", z=5, relative=true)
bevel_object(name="Cube", width=0.2, segments=3)
```

> **Status: experimental.** Tested with Blender 5.3 on Linux. Read the [security notes](#security) before exposing it
> to a network.

## Why it exists

Small models are bad at writing `bpy` code. In our tests they kept making the same mistakes:

- wrong indentation (`IndentationError`);
- inventing functions that do not exist (`primitive_sphere_add`, `CREATE_SOLID`);
- copying an example literally and failing when it must be adapted;
- one of them (a 4B model) wrote code that deleted every object in the scene.

And "thinking" is expensive on a slow CPU (1 to 3 tokens per second), so every decision the model has to make costs time.

## Tools

All numeric fields are optional unless noted. Colors accept a name (English or Portuguese, including feminine forms such
as `roxa`) or `#RRGGBB`.

| Tool | Fields | What it does |
|---|---|---|
| `create_sphere` | `radius`, `x`, `y`, `z`, `color`, `specular`, `roughness`, `name` | creates a UV sphere |
| `create_cube` | `size`, ... | creates a cube |
| `create_cylinder` | `radius`, `height`, ... | creates a cylinder |
| `create_cone` | `radius`, `height`, ... | creates a cone |
| `create_torus` | `major_radius`, `minor_radius`, ... | creates a torus |
| `list_objects` | none | lists the scene as short text: `name TYPE (x, y, z)` |
| `move_object` | `name` (required), `x`, `y`, `z`, `relative` | moves an existing object by name; axes you omit stay put |
| `bevel_object` | `name` (required), `width`, `segments` | adds/updates a Bevel modifier on an existing mesh |

Design rules (the point of the project):

- **One tool per shape.** Choosing the shape is already in the name; there is no `type` parameter to get wrong.
- **Flat fields**, no nested objects, sensible defaults.
- **The Python is a fixed template** embedded in the binary. Only validated values go into it (numbers inside a range,
  a cleaned name, a converted color). Object names are always inserted as an escaped string literal. The model cannot
  inject code.
- **Errors are short and useful.** A bad argument says what is valid; a wrong object name returns the list of real
  names, so the model can correct itself.

## How it works

```
model --MCP--> blender-minor (:9192) --MCP--> a Blender MCP server (:9191) --> Blender
```

`blender-minor` builds the script and sends it through the `execute_blender_code` tool of an existing Blender MCP
server. It speaks plain JSON-RPC over HTTP (stateless, path `/`). You need a Blender MCP server that exposes
`execute_blender_code` (we used the `blender-mcp` project with its Blender extension).

## Quick start

Requirements: .NET 10 SDK, Blender with a Blender MCP server running.

```bash
dotnet run                         # listens on 127.0.0.1:9192
```

Environment variables:

| Variable | Default | Meaning |
|---|---|---|
| `BLENDER_MINOR_PORT` | `9192` | port |
| `BLENDER_MINOR_HOST` | `127.0.0.1` | bind address (see [security](#security)) |
| `BLENDER_MCP_URL` | `http://127.0.0.1:9191/` | the Blender MCP server |
| `BLENDER_MINOR_DRYRUN` | unset | `1` returns the generated script instead of sending it (test without touching Blender) |
| `BLENDER_MINOR_TEMPLATES` | `~/.config/blender-minor/templates` | template override folder |
| `BLENDER_BIN` | | Blender executable used by `--selftest` |

Try it without Blender:

```bash
BLENDER_MINOR_DRYRUN=1 dotnet run &
curl -s -X POST http://127.0.0.1:9192/ -H 'Content-Type: application/json' -H 'Accept: application/json, text/event-stream' \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"create_sphere","arguments":{"radius":2,"color":"blue"}}}'
```

### Using it from llama.cpp

`llama-server` can only start MCP servers as **processes** (stdio), not by URL. Use the included bridge:

```json
{
  "mcpServers": {
    "blender-minor": {
      "command": "python3",
      "args": ["-I", "/path/to/tools/mcp_stdio_bridge.py", "http://127.0.0.1:9192/"]
    }
  }
}
```

```bash
llama-server -m model.gguf --jinja --mcp-servers-config mcp.json
```

Tool names are then prefixed with the server name (`blender-minor_create_sphere`).

### System prompt

Small models still need a short instruction. See [`prompts/system.en.txt`](prompts/system.en.txt) (and
[`system.pt-BR.txt`](prompts/system.pt-BR.txt)). The rules that matter most: always call the tool, never claim success
without an `OK`, always pass `color` when one is mentioned, copy object names exactly.

## When Blender changes its API: file override

Blender changes the `bpy` contract between versions (in 5.3, `Specular` became `Specular IOR Level`). The embedded text
is only the **default for the version we use**; a file in `~/.config/blender-minor/templates/` **overrides** it without
recompiling or restarting (it is read on every call; delete the file to go back to the default).

```bash
dotnet BlenderMinor.dll --dump-templates   # writes the embedded templates as a starting point
dotnet BlenderMinor.dll --selftest         # runs every tool in an ISOLATED headless Blender and reports what broke
curl http://127.0.0.1:9192/templates       # where each text comes from (file or embedded)
```

## Evaluation

[`eval/evaluate_model.py`](eval/evaluate_model.py) sends 12 requests to a `llama-server`, lets the model call the tools,
and then **checks the scene** (not the model's words) for the created object and its position. It removes only what it created.
A sample run (2026-11-20, temperature 0, thinking off, 12 scenarios, one run each, so treat it as a rough signal):

| Model | Called a tool | All arguments right | Created for real |
|---|---|---|---|
| Qwen3-0.6B Q4_K_M, no system prompt | 12/12 | 2/12 | 10/11 |
| Qwen3-0.6B Q4_K_M, with `prompts/system.pt-BR.txt` | 12/12 | 9/12 | 11/11 |
| Qwen3-VL-2B-Instruct Q4_K_M (text only), same prompt | 12/12 | 10/12 | 10/11 |

Most failures were a forgotten `color` field. The system prompt fixes most of them.

Run it against your own `llama-server` (started with the MCP bridge above):

```bash
python3 -I eval/evaluate_model.py --label my-model --system prompts/system.en.txt            # requests in Portuguese (default)
python3 -I eval/evaluate_model.py --label my-model --system prompts/system.en.txt --lang en  # the same 12 requests in English
```

## Security

- The API has **no authentication**. It listens on `127.0.0.1` by default. `BLENDER_MINOR_HOST=0.0.0.0` lets anyone who
  can reach the port create objects in your Blender; only do that on a network you trust.
- The model never sends code. It can only fill validated fields of fixed templates.
- The Blender MCP server you connect behind it may expose arbitrary Python execution (`execute_blender_code`).
  **Do not give that tool to the model**; give it only `blender-minor`.

## Roadmap

Aimed at modeling parts for 3D printing by conversation, with exact sizes and holes in the right places:

- [ ] `create_box(width, depth, height, ...)` and `set_dimensions(name, x, y, z)`
- [ ] `boolean_op(target, tool, UNION | DIFFERENCE | INTERSECT)` and `drill_hole(name, diameter, depth, x, y, z, axis)`
- [ ] `check_mesh(name)`: manifold check, volume, dimensions
- [ ] `export_stl(name, path)` and millimeter units
- [ ] `create_plane`, `create_icosphere`, `set_color`, `delete_object`

Ideas and pull requests are welcome.

## License

[MIT](LICENSE).
