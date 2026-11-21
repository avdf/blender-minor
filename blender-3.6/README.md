# Blender 3.6 LTS port of the official Blender MCP add-on

`blender-minor` talks to Blender through the official **Blender MCP** add-on from Blender Lab
(<https://projects.blender.org/lab/blender_mcp>). That add-on asks for **Blender 5.1 or newer**, and
Blender 4.0+ needs **OpenGL 4.3** to open its window. On older machines (for example an Intel HD 4000,
which tops out at OpenGL 4.2) neither runs natively. **Blender 3.6 LTS needs only OpenGL 3.3**, so it opens
natively on that hardware. This folder makes the official add-on run on 3.6.

> Português: veja [README.pt-BR.md](README.pt-BR.md).

## License

The patch is derived from the Blender Lab add-on, which is **GPL-3.0-or-later**; the patch keeps that
license (see [`LICENSE-GPL-3.0`](LICENSE-GPL-3.0)). The upstream add-on files are **not** copied into this
repository: you apply the patch to your own checkout. The rest of this repository stays MIT.

## What is in this folder

| File | What it is |
|---|---|
| `blender-mcp-addon-3.6.patch` | unified diff against upstream commit `dbbf836ad4b1025f14a2b3b504c43903f39e0b04` (4 files, +68 / -16) |
| `serve_background.py` | starts the add-on's server inside Blender **without a window** (replaces `blender --command blender_mcp`, which does not exist in 3.6) |
| `test_mcp_blender36.py` | smoke test: official MCP server -> add-on -> Blender 3.6 (6 checks) |
| `LICENSE-GPL-3.0` | license of the patch |

## What the patch changes

1. **`bl_info`** added: Blender 3.6 has no extensions system, so it does not read `blender_manifest.toml`.
2. **`bpy.app.online_access`** (Blender 4.2+) is read with `getattr(..., True)`.
3. **`bpy.utils.register_cli_command`** / `unregister_cli_command` (not in 3.6) are only used when present.
4. **`typing.Self`** (Python 3.11+; Blender 3.6 ships Python 3.10) falls back to a plain annotation.
5. **The weak sandbox now really blocks `bpy.ops.wm.read_factory_settings` on 3.6.** Upstream hooks
   `bpy.ops._op_create_function`, which 3.6 does not have, so the block silently did nothing and the call
   reloaded the preferences, disabled the add-on and **stopped the MCP server**. The patch hooks the 3.6
   equivalent (`_bpy_ops_submodule__getattr__`) and raises an error if it recognises neither layout. Note:
   do not detect the hook with `hasattr(bpy.ops, ...)`, because `bpy.ops` has a catch-all module
   `__getattr__`, so `hasattr` is always true; the patch checks `vars(bpy.ops)`.
6. **The scene is refreshed after every execution** (`view_layer.update()`): in background mode nothing
   redraws, so `Object.dimensions` stayed stale after changing `scale`. Now the *next* call reads the right values.

## Use it

```bash
# 1) the official add-on at the commit this patch was made for
git clone https://projects.blender.org/lab/blender_mcp.git
cd blender_mcp && git checkout dbbf836ad4b1025f14a2b3b504c43903f39e0b04

# 2) apply the patch inside its addon/ folder
cd addon && patch -p1 < /path/to/blender-minor/blender-3.6/blender-mcp-addon-3.6.patch

# 3) a scripts folder for Blender 3.6 (nothing is installed into your ~/.config)
mkdir -p ~/blender36-scripts/addons && cp -r blender_mcp_addon ~/blender36-scripts/addons/

# 4) start the server in Blender 3.6, headless, on a port of your choice
BLENDER_USER_SCRIPTS=~/blender36-scripts \
  /path/to/blender-3.6/blender -b --factory-startup \
  --python /path/to/blender-minor/blender-3.6/serve_background.py -- --port 9877
```

The official MCP server finds the add-on through `BLENDER_MCP_HOST` / `BLENDER_MCP_PORT` (default
`localhost:9876`) and starts a background Blender for the `*_for_cli` tools through `BLENDER_PATH`, so point
`BLENDER_PATH` at the 3.6 binary. Then run the smoke test (see its header).

## Status

Tested on **Blender 3.6.23 (Python 3.10.13)**, Debian 13, Intel HD Graphics 4000 (OpenGL 4.2), headless:

- works: `execute_blender_code`, `get_objects_summary`, `execute_blender_code_for_cli` (needs a `blend_file`),
  exact dimensions and a Boolean hole, blocked `read_factory_settings` with the server staying up;
- **not tested**: the interactive mode with a window (auto-start timer in the preferences), the screenshot
  and "jump to" tools (they need a window);
- known gap: `bpy.data.file_path_foreach` does not exist in 3.6 (nor in 4.3), so the "missing files" tool
  does not work.

This is a port made for old hardware, not an upstream release: report problems here, not to Blender Lab.
