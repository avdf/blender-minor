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
>
> **Old hardware?** The official Blender MCP add-on needs Blender 5.1+, and Blender 4.0+ needs OpenGL 4.3. A
> patch that makes the add-on run on **Blender 3.6 LTS** (OpenGL 3.3) is in [`blender-3.6/`](blender-3.6/).

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
| `create_plane` | `size`, ... | creates a flat square plane |
| `create_icosphere` | `radius`, `subdivisions`, ... | creates an icosphere (a sphere made of triangles) |
| `list_objects` | none | lists the scene as short text: `name TYPE (x, y, z)` |
| `move_object` | `name` (required), `x`, `y`, `z`, `relative` | moves an existing object by name; axes you omit stay put |
| `bevel_object` | `name` (required), `width`, `segments` | adds/updates a Bevel modifier on an existing mesh |
| `create_box` | `width`, `depth`, `height`, `x`, `y`, `z`, `color`, `name` | a box with exact sizes (x, y, z = its center) |
| `set_dimensions` | `name` (required), `x`, `y`, `z` | sets the exact size of an existing object |
| `boolean_op` | `target`, `cutter` (required), `operation`, `delete_cutter` | UNION / DIFFERENCE / INTERSECT of two objects; **transactional** (see below) |
| `drill_hole` | `name`, `diameter` (required), `x`, `y`, `z`, `axis`, `side`, `depth`, `clearance`, `counterbore_diameter`, `counterbore_depth`, `countersink_diameter`, `segments` | a round hole of an exact diameter, through or blind, along X, Y or Z, with an optional counterbore (recess for a screw head) or 90 degree countersink |
| `screw_hole` | `name` (required), `size` (M2 to M8), `kind`, `x`, `y`, `z`, `axis`, `side`, `depth` | a hole for a standard screw from a table: `clearance`, `counterbore`, `countersunk` or `insert` (blind hole for a heat-set insert) |
| `set_color` | `name`, `color` (required), `roughness`, `specular` | changes the material of an existing object |
| `repair_mesh` | `name` (required), `tolerance` | merges duplicate vertices, removes loose parts, fixes normals, fills holes; reports before/after; undone if it makes things worse |
| `delete_object` | `name` (required) | deletes one object by name |
| `export_stl` | `name` (required), `filename` | exports a binary STL for the slicer, **only a plain file name** into the export folder |
| `set_units` | `unit` (`mm`, `cm`, `m`) | sets the scene so that 1 Blender unit = 1 mm (or cm, m) |
| `check_mesh` | `name` (required) | closed (manifold) or not, volume and dimensions |

**Booleans are transactional.** Triangle-mesh booleans fail on dirty meshes. `boolean_op` and `drill_hole` keep a copy of the
mesh, apply the operation (Exact solver), check that the result is a closed mesh with a volume, and **put the old mesh back with a
clear error** if it is not. `drill_hole` cuts with a cylinder that sticks out of the part on both sides (no coplanar faces); the hole is
a 64-sided polygon inscribed in the circle (a 3 mm hole is 2.996 mm across the flats), and `clearance` adds to the diameter for 3D printing.

**Screw table.** `screw_hole` uses typical values (ISO 273 medium clearance, ISO 4762 socket-cap recess, ISO 10642 countersunk
head, common brass heat-set inserts; for M3: clearance 3.4, recess 6.0 x 3.0, insert 4.0 x 5.7). They are a **starting point**:
check them against your screws, inserts and printer. Override any field without recompiling in
`~/.config/blender-minor/screws.json` (or `BLENDER_MINOR_SCREWS`), for example `{"M3": {"clearance": 3.5, "insert_diameter": 4.2}}`.

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

Small models do better with a **short tool list**, and `llama-server` has no allow list of its own, so the bridge has one:
pass `--allow` with the tool names the model should see. Anything else is hidden from `tools/list` and refused on
`tools/call` without ever reaching blender-minor:

```json
"args": ["-I", "/path/to/tools/mcp_stdio_bridge.py", "http://127.0.0.1:9192/",
         "--allow", "create_box,drill_hole,screw_hole,list_objects"]
```

**Property order matters.** `llama-server` turns each tool schema into a grammar that keeps the property order, and a
model writes the name first ("a box called base, 60 by 40..."). Every tool therefore publishes `name` as its **first**
property. With `name` last, Qwen3-1.7B produced `{"name": "base', " }` for `create_box` and lost every size (0 of 3 parts
correct); with `name` first the same model gets exact dimensions and volumes (2 of 3, the miss being a placement mistake).

### System prompt

Small models still need a short instruction. See [`prompts/system.en.txt`](prompts/system.en.txt) (and
[`system.pt-BR.txt`](prompts/system.pt-BR.txt)). The rules that matter most: always call the tool, never claim success
without an `OK`, always pass `color` when one is mentioned, copy object names exactly.

For **parts with exact sizes and holes** (3D printing, laser) there is a shorter prompt that lists only the tools that
job needs: [`prompts/system.parts.en.txt`](prompts/system.parts.en.txt). It matches the `--allow` list of the
[stdio bridge](#using-it-from-llamacpp) (`create_box`, `drill_hole`, `screw_hole`, `boolean_op`, `move_object`,
`list_objects`, `check_mesh`, `export_stl`), so the model sees the same short tool list that the prompt describes.

## When Blender changes its API: file override

Blender changes the `bpy` contract between versions (in 5.3, `Specular` became `Specular IOR Level`). The embedded text
is only the **default for the version we use**; a file in `~/.config/blender-minor/templates/` **overrides** it without
recompiling or restarting (it is read on every call; delete the file to go back to the default).

```bash
dotnet BlenderMinor.dll --dump-templates   # writes the embedded templates as a starting point
dotnet BlenderMinor.dll --selftest         # runs every tool in an ISOLATED headless Blender and reports what broke
curl http://127.0.0.1:9192/templates       # where each text comes from (file or embedded)
```

## Testing

Three levels, from fast to slow:

```bash
dotnet BlenderMinor.dll --selftest      # every tool in an isolated headless Blender (no window)
python3 -I eval/test_tools.py           # integration test against a LIVE blender-minor + Blender, no model, ~20 s
python3 -I eval/evaluate_model.py ...   # how well a given model uses the tools (below)
```

`eval/test_tools.py` checks exact sizes, the volume removed by holes (against pi r^2 h), union/intersection/difference volumes, the
rollback of a broken boolean, error messages and name-injection attempts. It creates only `BMT_*` objects and removes them.

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
- `export_stl` never accepts a path: only a plain file name, written to `~/blender-minor-exports` (or `BLENDER_MINOR_EXPORT_DIR`).
  This is on purpose: a free path would let the model write anywhere on disk.
- `delete_object` is destructive. If your MCP client lets you choose which tools a model may call, leave it out for small models.
- The Blender MCP server you connect behind it may expose arbitrary Python execution (`execute_blender_code`).
  **Do not give that tool to the model**; give it only `blender-minor`.

## Roadmap

Aimed at modeling parts for 3D printing by conversation, with exact sizes and holes in the right places:

- [x] `create_box`, `set_dimensions`, `boolean_op` (transactional), `drill_hole` (through/blind, clearance), `check_mesh`
- [x] counterbore and countersink in `drill_hole`, and `screw_hole` with a standard-size table
- [x] `repair_mesh`, `export_stl` (file name only, fixed export folder), `set_units`
- [x] `create_plane`, `create_icosphere`, `set_color`, `delete_object`
- [ ] fillets (`bevel_object` on chosen edges), rectangular pockets and slots, patterns of holes
- [ ] a threaded-hole option and a configurable insert pocket depth per material

Ideas and pull requests are welcome.

## License

[MIT](LICENSE).
