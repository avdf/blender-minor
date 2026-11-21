"""The trace of a test round: everything needed to understand, later, WHAT was run, with WHAT, WHY, and whether the fix worked.

Used by evaluate_parts.py and check_tools.py (module name roundtrace, so it never shadows the standard library trace). Each round writes one folder, eval/rounds/<date>-<label>/, that IS committed to git:

  WHY.md          why the round was run, what changed since the previous one, the result and a comparison with that previous round
  output.txt      the text the test printed
  evidence.json   code under test (git commit, uncommitted diff hash), host, Blender version, the prompt, the tools the model saw,
                  every call with its answer, every measurement
  prompt.txt      the system prompt, in full
  scene.blend     the Blender scene as it was when the round ended (the visual proof: open it and look)
  window.png      a capture of the whole Blender window, made BY BLENDER (no dependency on the desktop or the window manager)
  viewport.png    the 3D view as it appears on screen, zoomed to all the parts (a crop of window.png; not a render)

The pictures are the proof of a problem and of its fix: a part without its hole, a hole in the void, and the same part right.
LAN addresses and home directories are replaced before anything is written, because this folder goes to a public repository.
"""
import hashlib
import json
import os
import platform
import re
import subprocess
import time
import urllib.request


def sanitize(text):
    text = re.sub(r"\b192\.168\.\d+\.\d+\b", "<lan-ip>", text)
    return re.sub(r"/home/[A-Za-z0-9_.-]+", "~", text)


def sha256_bytes(b):
    return hashlib.sha256(b).hexdigest()


def sha256_file(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def _git(repo, *args):
    try:
        return subprocess.run(["git", "-C", repo, *args], capture_output=True, text=True, timeout=30).stdout.strip()
    except Exception:
        return ""


def git_state(repo):
    """The code under test: the commit, and a hash of whatever is not committed yet (so a dirty run can still be matched)."""
    try:
        diff = subprocess.run(["git", "-C", repo, "diff", "HEAD"], capture_output=True, timeout=30).stdout
    except Exception:
        diff = b""
    return {"commit": _git(repo, "rev-parse", "HEAD"), "subject": _git(repo, "log", "-1", "--format=%s"),
            "uncommitted_files": [l for l in _git(repo, "status", "--porcelain").splitlines() if l.strip()],
            "uncommitted_diff_sha256": sha256_bytes(diff) if diff else None}


def host_info():
    cpu, mem = "?", "?"
    try:
        cpu = next(l.split(":", 1)[1].strip() for l in open("/proc/cpuinfo") if l.startswith("model name"))
        mem = next(l.split(":", 1)[1].strip() for l in open("/proc/meminfo") if l.startswith("MemTotal"))
    except Exception:
        pass
    return {"os": platform.platform(), "cpu": cpu, "mem_total": mem, "python": platform.python_version()}


def http_json(url, timeout=20):
    try:
        return json.loads(urllib.request.urlopen(url, timeout=timeout).read().decode())
    except Exception:
        return None


def run_code(blender_mcp, code, timeout=120):
    body = {"jsonrpc": "2.0", "id": 1, "method": "tools/call", "params": {"name": "execute_blender_code", "arguments": {"code": code}}}
    req = urllib.request.Request(blender_mcp, data=json.dumps(body).encode(),
                                 headers={"Content-Type": "application/json", "Accept": "application/json, text/event-stream"})
    return urllib.request.urlopen(req, timeout=timeout).read().decode().replace("\\n", "\n")


def blender_version(blender_mcp):
    try:
        m = re.search(r"BLENDVER ([0-9][0-9.]*)", run_code(blender_mcp, "import bpy\nprint('BLENDVER', bpy.app.version_string)"))
        return m.group(1) if m else None
    except Exception:
        return None


_CAPTURE = '''import bpy, math
from mathutils import Vector
win = bpy.context.window_manager.windows[0]
sc = bpy.context.scene
meshes = sorted([o for o in sc.objects if o.type == 'MESH'], key=lambda o: o.name)
bpy.context.view_layer.update()
if @GRID@ and meshes:   # the test parts are far apart on purpose (so tests never touch): put them in a compact grid to be photographed
    def box(o):
        cs = [o.matrix_world @ Vector(c) for c in o.bound_box]
        return Vector((min(c.x for c in cs), min(c.y for c in cs), min(c.z for c in cs))), Vector((max(c.x for c in cs), max(c.y for c in cs), max(c.z for c in cs)))
    step = max(max(box(o)[1] - box(o)[0]) for o in meshes) + 20.0
    cols = max(1, math.ceil(math.sqrt(len(meshes))))
    for i, o in enumerate(meshes):
        lo, hi = box(o)
        ctr = (lo + hi) / 2
        o.location.x += (i % cols) * step - ctr.x
        o.location.y += (i // cols) * step - ctr.y
        o.location.z += -ctr.z
    bpy.context.view_layer.update()
for o in bpy.context.view_layer.objects:
    o.select_set(False)
for o in meshes:
    o.select_set(True)
view = None
for area in win.screen.areas:
    if area.type == 'VIEW_3D':
        area.spaces[0].clip_end = max(area.spaces[0].clip_end, 100000.0)
        region = next(r for r in area.regions if r.type == 'WINDOW')
        with bpy.context.temp_override(window=win, area=area, region=region):
            if meshes:
                bpy.ops.view3d.view_selected()   # zoom to all the parts
            bpy.ops.wm.redraw_timer(type='DRAW_WIN_SWAP', iterations=1)
        view = (region.x, region.y, region.width, region.height)
        break
with bpy.context.temp_override(window=win, screen=win.screen):
    bpy.ops.wm.redraw_timer(type='DRAW_WIN_SWAP', iterations=1)
    bpy.ops.screen.screenshot(filepath=@WINDOW@)          # the whole window, as it is on screen
for o in meshes:
    o.select_set(False)
if view:   # the 3D view alone: a crop of that capture (no render: this is what is visible on screen)
    import numpy as np
    img = bpy.data.images.load(@WINDOW@)
    w, h = img.size
    px = np.array(img.pixels[:], dtype=np.float32).reshape(h, w, 4)
    x, y, vw, vh = view
    crop = px[y:y + vh, x:x + vw]
    out = bpy.data.images.new('viewport_crop', vw, vh)
    out.pixels = crop.ravel().tolist()
    out.filepath_raw = @VIEWPORT@
    out.file_format = 'PNG'
    out.save()
    bpy.data.images.remove(out)
    bpy.data.images.remove(img)
bpy.ops.wm.save_as_mainfile(filepath=@BLEND@, copy=True)
print('CAPTURED')'''


def capture(blender_mcp, folder, grid=False):
    """Saves the visual proof into the folder, made by Blender itself: viewport.png, window.png and scene.blend.
    grid=True first puts the mesh objects in a compact grid (for tests whose parts are spread far apart on purpose)."""
    paths = {"viewport": os.path.join(folder, "viewport.png"), "window": os.path.join(folder, "window.png"),
             "blend": os.path.join(folder, "scene.blend")}
    os.makedirs(folder, exist_ok=True)
    code = (_CAPTURE.replace("@GRID@", "True" if grid else "False").replace("@VIEWPORT@", repr(paths["viewport"]))
            .replace("@WINDOW@", repr(paths["window"])).replace("@BLEND@", repr(paths["blend"])))
    try:
        out = run_code(blender_mcp, code)
        if "CAPTURED" not in out:
            print("capture : Blender did not finish the capture ->", re.sub(r"\s+", " ", out)[:400])
    except Exception as e:
        print("capture : failed ->", e)
    for k, p in paths.items():
        if not os.path.exists(p) or os.path.getsize(p) < 1000:
            print(f"capture : WARNING {k} is missing or tiny: {p}")
    return {k: (p if os.path.exists(p) else None) for k, p in paths.items()}


def round_dir(eval_dir, label):
    safe = re.sub(r"[^A-Za-z0-9._-]+", "-", label).strip("-")
    return os.path.join(eval_dir, "rounds", f"{time.strftime('%Y%m%d-%H%M%S')}-{safe}")


def compare_table(previous_json, rows):
    """A case-by-case comparison with the previous round: the proof of whether a fix was effective."""
    try:
        prev = json.load(open(previous_json, encoding="utf-8"))
    except Exception as e:
        return f"(could not read the previous round: {e})\n"
    before = {r["case"]: r for r in prev.get("rows", [])}
    lines = [f"Compared with `{os.path.basename(os.path.dirname(previous_json)) or os.path.basename(previous_json)}` "
             f"({prev.get('passed', '?')} passed, {prev.get('failed', '?')} failed):", "",
             "| case | before | now | volume before | volume now |", "|---|---|---|---|---|"]
    for r in rows:
        b = before.get(r["case"])
        bv = (b or {}).get("measured", {}).get("volume")
        nv = (r.get("measured") or {}).get("volume")
        lines.append(f"| {r['case']} | {'-' if b is None else ('PASS' if b['pass'] else 'FAIL')} | {'PASS' if r['pass'] else 'FAIL'} | {bv} | {nv} |")
    return "\n".join(lines) + "\n"


def write_round(folder, *, label, why, changed, result_line, evidence, output_lines, prompt=None, compare_to=None, rows=None, files=None):
    """Writes WHY.md, output.txt, evidence.json and prompt.txt (sanitized) into the folder."""
    os.makedirs(folder, exist_ok=True)
    files = files or {}
    evidence = dict(evidence)
    evidence["files"] = {name: {"sha256": sha256_file(p), "bytes": os.path.getsize(p)} for name, p in files.items() if p and os.path.exists(p)}
    w = lambda name, text: open(os.path.join(folder, name), "w", encoding="utf-8").write(sanitize(text))
    w("evidence.json", json.dumps(evidence, ensure_ascii=False, indent=1) + "\n")
    w("output.txt", "\n".join(output_lines) + "\n")
    if prompt is not None:
        w("prompt.txt", prompt)
    g = evidence.get("git", {})
    md = [f"# {label}", "", f"- **When:** {evidence.get('when')}",
          f"- **Why this round:** {why or '(not given)'}",
          f"- **What changed since the previous round:** {changed or '(not given)'}",
          f"- **Code under test:** commit `{(g.get('commit') or '?')[:10]}` {g.get('subject') or ''}"
          + (f" (+ {len(g.get('uncommitted_files', []))} uncommitted file(s), diff sha256 `{(g.get('uncommitted_diff_sha256') or '')[:12]}`)" if g.get("uncommitted_files") else ""),
          f"- **Blender:** {evidence.get('blender_version')} | **Host:** {evidence.get('host', {}).get('cpu')}",
          f"- **Result:** {result_line}", ""]
    if compare_to and rows is not None:
        md += [compare_table(compare_to, rows), ""]
    shots = [n for n in ("viewport.png", "window.png") if n in {os.path.basename(p) for p in files.values() if p}]
    if shots:
        md += ["## Visual proof", ""] + [f"![{n}]({n})" for n in shots] + ["", "`scene.blend` is the scene itself: open it in Blender to look at the parts.", ""]
    md += ["## Files", "", "`output.txt` (what the test printed), `evidence.json` (every call and measurement, with the hashes of the files), "
           "`prompt.txt` (the system prompt)" + (", `scene.blend`, `window.png`, `viewport.png`." if shots else "."), ""]
    w("WHY.md", "\n".join(md))
    return folder
