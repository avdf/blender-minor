#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""
Smoke test for the official Blender MCP server talking to the patched add-on inside Blender 3.6.

Start the add-on first (see README.md), then run this with a Python that has the ``mcp`` package
(the virtualenv of the official ``blender_mcp`` project has it):

    BLENDER_MCP_PORT=9877 BLENDER_PATH=/path/to/blender-3.6/blender \
    BLENDER_MCP_CMD=/path/to/blender_mcp/.venv/bin/blender-mcp  python test_mcp_blender36.py

It checks that: a 20 x 30 x 5 plate with a 4 mm hole comes out with the exact size, a summary tool works,
the weak sandbox blocks ``bpy.ops.wm.read_factory_settings`` *without* stopping the server, and an object
scaled in one call has correct ``dimensions`` in the next call.
"""
import asyncio
import os
import sys

from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client

ENV = dict(os.environ)
ENV.setdefault("BLENDER_MCP_HOST", "localhost")
ENV.setdefault("BLENDER_MCP_PORT", "9877")
CMD = os.environ.get("BLENDER_MCP_CMD", "blender-mcp")

PLATE = """
import bpy
for o in list(bpy.data.objects): bpy.data.objects.remove(o)
bpy.ops.mesh.primitive_cube_add(size=1)
plate = bpy.context.active_object; plate.name = "Plate"
plate.scale = (20.0, 30.0, 5.0)
bpy.context.view_layer.update()
bpy.ops.mesh.primitive_cylinder_add(radius=2.0, depth=10.0, location=(0, 0, 0))
hole = bpy.context.active_object; hole.name = "Hole"
m = plate.modifiers.new("hole", "BOOLEAN"); m.operation = "DIFFERENCE"; m.object = hole
bpy.context.view_layer.objects.active = plate
bpy.ops.object.modifier_apply(modifier="hole")
bpy.data.objects.remove(hole)
bpy.context.view_layer.update()
print("BLENDER", bpy.app.version_string, "| plate", tuple(round(v, 3) for v in plate.dimensions), "| vertices", len(plate.data.vertices))
"""


async def call(session, name, args):
    res = await session.call_tool(name, args)
    return res.isError, "\n".join(getattr(c, "text", str(c)) for c in res.content)


async def main() -> int:
    failures = 0

    def check(label, ok, detail=""):
        nonlocal failures
        print(("PASS  " if ok else "FAIL  ") + label + (("  -> " + detail) if detail and not ok else ""))
        failures += 0 if ok else 1

    params = StdioServerParameters(command=CMD, args=[], env=ENV)
    async with stdio_client(params) as (r, w):
        async with ClientSession(r, w) as s:
            await s.initialize()
            tools = [t.name for t in (await s.list_tools()).tools]
            check("tools listed (execute_blender_code present)", "execute_blender_code" in tools, str(tools)[:200])

            err, txt = await call(s, "execute_blender_code", {"code": PLATE})
            check("plate 20x30x5 with a 4 mm hole has the exact outer size", (not err) and "(20.0, 30.0, 5.0)" in txt, txt[-300:])

            err, txt = await call(s, "get_objects_summary", {})
            check("get_objects_summary lists the plate", (not err) and "Plate" in txt, txt[-300:])

            err, txt = await call(s, "execute_blender_code", {"code": "import bpy\nbpy.ops.wm.read_factory_settings(use_empty=True)\nprint('SHOULD NOT RUN')"})
            check("read_factory_settings is blocked by the sandbox", "not allowed" in txt and "SHOULD NOT RUN" not in txt, txt[-300:])

            err, txt = await call(s, "execute_blender_code", {"code": "import bpy\nbpy.ops.wm.read_homefile(use_empty=True)\nprint('alive', len(bpy.data.objects))"})
            check("server still alive after the blocked call", (not err) and "alive 0" in txt, txt[-300:])

            await call(s, "execute_blender_code", {"code": "import bpy\nbpy.ops.mesh.primitive_cube_add(size=1)\no = bpy.context.active_object; o.name = 'Box'; o.scale = (20.0, 30.0, 5.0)"})
            err, txt = await call(s, "execute_blender_code", {"code": "import bpy\nprint('dims', tuple(round(v, 3) for v in bpy.data.objects['Box'].dimensions))"})
            check("dimensions are fresh in the call after a scale change", "(20.0, 30.0, 5.0)" in txt, txt[-300:])
    print("\n%d failure(s)" % failures)
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))
