#!/usr/bin/env python3
"""Checks the blender-minor tools WITHOUT a model: each tool is called directly and the result is compared with a volume
worked out by hand (read back with check_mesh). Use it after changing a tool or updating Blender.

It builds its own objects (names start with "ct_") and deletes every one of them at the end, so it is safe to run against a
Blender window that has your own work in it.

Usage:
  python3 -I check_tools.py                         # blender-minor on http://127.0.0.1:9192/
  python3 -I check_tools.py --url http://127.0.0.1:9193/
Exit code 0 = every check passed.
"""
import argparse, json, re, sys, urllib.request

ap = argparse.ArgumentParser()
ap.add_argument("--url", default="http://127.0.0.1:9192/", help="blender-minor address")
args = ap.parse_args()

_id = [0]
_made = []


def call(tool, **kw):
    _id[0] += 1
    body = {"jsonrpc": "2.0", "id": _id[0], "method": "tools/call", "params": {"name": tool, "arguments": kw}}
    req = urllib.request.Request(args.url, data=json.dumps(body).encode(),
                                 headers={"Content-Type": "application/json", "Accept": "application/json, text/event-stream"})
    r = json.loads(urllib.request.urlopen(req, timeout=120).read().decode())["result"]
    return ("ERROR " if r.get("isError") else "") + " ".join(c["text"] for c in r["content"]).replace("\n", " ")


def make(name, **kw):
    _made.append(name)
    return call("create_box", name=name, **kw)


def volume(name):
    m = re.search(r"volume=([0-9.]+)", call("check_mesh", name=name))
    return float(m.group(1)) if m else float("nan")


fails = 0


def check(label, got, want, tol=0.005):
    global fails
    ok = abs(got - want) <= max(tol * abs(want), 1e-6)
    fails += 0 if ok else 1
    print(f"{'PASS' if ok else 'FAIL'}  {label}: got {got:.3f}, expected {want:.3f}")


def expect_error(label, text):
    global fails
    ok = text.startswith("ERROR") or "ERROR" in text[:40]
    fails += 0 if ok else 1
    print(f"{'PASS' if ok else 'FAIL'}  {label}: {text[:110]}")


try:
    # pad / pocket on a 100 x 10 x 2 plate: a strip raised at one end, then a recess, then a recess INSIDE the raised strip
    make("ct_plate", width=100, depth=10, height=2, x=0, y=200, z=1)
    check("plate", volume("ct_plate"), 100 * 10 * 2)
    call("pad", name="ct_plate", x1=-50, x2=-30, y1=196, y2=204, height=2)
    check("pad 20x8x2", volume("ct_plate"), 2000 + 20 * 8 * 2)
    call("pocket", name="ct_plate", x1=10, x2=30, y1=197, y2=203, depth=1)
    check("pocket 20x6x1 (surface at z=2)", volume("ct_plate"), 2000 + 320 - 20 * 6 * 1)
    call("pocket", name="ct_plate", x1=-45, x2=-35, y1=198, y2=202, depth=1)
    check("pocket 10x4x1 inside the raised strip (surface at z=4)", volume("ct_plate"), 2000 + 320 - 120 - 10 * 4 * 1)
    before = volume("ct_plate")
    expect_error("pad outside the face is refused", call("pad", name="ct_plate", x1=500, x2=510, y1=196, y2=204, height=2))
    check("the refused pad changed nothing", volume("ct_plate"), before)

    # a pad on a side face (axis X, side -): a 10 x 10 x 2 boss on the left end
    make("ct_side", width=40, depth=20, height=20, x=0, y=300, z=10)
    call("pad", name="ct_side", axis="X", side="-", y1=295, y2=305, z1=5, z2=15, height=2)
    check("pad on the X- face (10x10x2)", volume("ct_side"), 40 * 20 * 20 + 10 * 10 * 2)

    # mirror: half part of 10 x 10 x 4 at x=60 mirrored across X=0 gives two
    make("ct_half", width=10, depth=10, height=4, x=60, y=400, z=2)
    call("mirror_object", name="ct_half", axis="X", at=0)
    check("mirror_object (two halves)", volume("ct_half"), 2 * 10 * 10 * 4)

    # join / subtract / intersect: the TOOL carries the operation, there is no parameter to choose
    make("ct_a", width=10, depth=10, height=10, x=80, y=500, z=5)
    make("ct_b", width=10, depth=10, height=10, x=85, y=500, z=5)
    call("join", target="ct_a", other="ct_b")
    check("join (5x10x10 overlap)", volume("ct_a"), 1000 + 1000 - 500)
    make("ct_c", width=20, depth=20, height=5, x=120, y=500, z=2.5)
    make("ct_d", width=10, depth=10, height=10, x=120, y=500, z=2.5)
    call("subtract", target="ct_c", cutter="ct_d")
    check("subtract (10x10x5 removed)", volume("ct_c"), 20 * 20 * 5 - 10 * 10 * 5)
    make("ct_e", width=10, depth=10, height=10, x=160, y=500, z=5)
    make("ct_f", width=10, depth=10, height=10, x=165, y=500, z=5)
    call("intersect", target="ct_e", other="ct_f")
    check("intersect (5x10x10 in common)", volume("ct_e"), 5 * 10 * 10)

    # array: 10 parts of 5 x 5 x 2 in a 5 x 2 grid with a 2 mm gap
    make("ct_piece", width=5, depth=5, height=2, x=0, y=600, z=1)
    call("array_object", name="ct_piece", count_x=5, count_y=2, gap=2)
    check("array_object 5x2 (ten parts)", volume("ct_piece"), 10 * 5 * 5 * 2)
    out = call("check_mesh", name="ct_piece")
    m = re.search(r"dimensions=\(([^)]*)\)", out)
    dims = tuple(round(float(x)) for x in m.group(1).split(",")) if m else None
    check("array size X = 5*5 + 4*2", dims[0] if dims else float("nan"), 33)
    check("array size Y = 2*5 + 1*2", dims[1] if dims else float("nan"), 12)
    expect_error("array with both counts 1 is refused", call("array_object", name="ct_piece", count_x=1, count_y=1))
finally:
    for n in _made:
        try:
            call("delete_object", name=n)
        except Exception:
            pass

print(f"\n{fails} failure(s)")
sys.exit(1 if fails else 0)
