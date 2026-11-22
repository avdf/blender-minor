#!/usr/bin/env python3
"""Checks the printer compensation settings of blender-minor against volumes worked out by hand.

BLENDER_MINOR_HOLE_COMP (every round hole, with its counterbore or countersink) and BLENDER_MINOR_NUT_COMP (nut traps, across the flats) are
SERVER settings. Start a second server with them set, next to the normal one, and point this script at it:

  BLENDER_MINOR_PORT=9193 BLENDER_MINOR_HOLE_COMP=0.2 BLENDER_MINOR_NUT_COMP=0.1 dotnet run -c Release --no-build
  python3 -I check_compensation.py --url http://127.0.0.1:9193/ --hole-comp 0.2 --nut-comp 0.1

The same command with 0 and 0 checks that a server without compensation cuts the exact diameters. The test parts are deleted at the end.
"""
import argparse, json, math, re, sys, urllib.request

ap = argparse.ArgumentParser()
ap.add_argument("--url", default="http://127.0.0.1:9193/")
ap.add_argument("--hole-comp", type=float, required=True, help="the value the server was started with")
ap.add_argument("--nut-comp", type=float, required=True)
ap.add_argument("--segments", type=int, default=64, help="sides of the hole cutter (the server default)")
args = ap.parse_args()

_id = [0]


def call(tool, **kw):
    _id[0] += 1
    body = {"jsonrpc": "2.0", "id": _id[0], "method": "tools/call", "params": {"name": tool, "arguments": kw}}
    req = urllib.request.Request(args.url, data=json.dumps(body).encode(), headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=120) as r:
        res = json.load(r)["result"]
    return "".join(c.get("text", "") for c in res.get("content", []))


def volume(text):
    m = re.search(r"volume=([0-9.]+)", text)
    return float(m.group(1)) if m else None


def polygon(d, n=args.segments):
    """Area of the n-sided cutter of a hole of diameter d (what the boolean really removes)."""
    return 0.5 * n * (d / 2) ** 2 * math.sin(2 * math.pi / n)


BOX = (40.0, 40.0, 10.0)
V0 = BOX[0] * BOX[1] * BOX[2]
hc, nc = args.hole_comp, args.nut_comp
results = []


def check(label, text, expected, tol=2e-5):
    got = volume(text)
    ok = got is not None and abs(got - expected) <= tol * V0
    results.append(ok)
    print(("PASS " if ok else "FAIL ") + label)
    print(f"      expected volume {expected:.4f}  measured {got}  (diameter in the reply: {re.search(r'diameter ([0-9.]+)', text).group(1) if re.search(r'diameter ([0-9.]+)', text) else '-'})")
    if not ok:
        print("      reply:", text[:240])


def fresh(name):
    t = call("create_box", name=name, width=BOX[0], depth=BOX[1], height=BOX[2])
    assert t.startswith("OK"), t


names = []
try:
    # 1. a plain hole: ask for 3, the tool cuts 3 + hole compensation
    fresh("cmp_hole"); names.append("cmp_hole")
    t = call("drill_hole", name="cmp_hole", diameter=3, x=0, y=0)
    check(f"drill_hole 3 mm with hole compensation {hc}", t, V0 - polygon(3 + hc) * BOX[2])

    # 2. a screw clearance hole: the table's 3.4 for M3 plus the compensation
    fresh("cmp_clear"); names.append("cmp_clear")
    t = call("screw_hole", name="cmp_clear", size="M3", x=0, y=0)
    check(f"screw_hole M3 clearance (3.4 + {hc})", t, V0 - polygon(3.4 + hc) * BOX[2])

    # 3. the new pilot hole: standard tap drill of M5 is 4.2, plus the compensation
    fresh("cmp_pilot"); names.append("cmp_pilot")
    t = call("screw_hole", name="cmp_pilot", size="M5", kind="pilot", x=0, y=0)
    check(f"screw_hole M5 pilot (4.2 + {hc})", t, V0 - polygon(4.2 + hc) * BOX[2])

    # 4. the nut trap, with ITS OWN compensation: across the flats = 5.5 (M3 nut) + 0.2 (default clearance) + nut compensation;
    #    depth = 2.4 (nut thickness) + 0.2. The hexagon area is (sqrt(3)/2) * across^2. The hole compensation must NOT change it.
    fresh("cmp_nut"); names.append("cmp_nut")
    across, depth = 5.5 + 0.2 + nc, 2.4 + 0.2
    t = call("nut_trap", name="cmp_nut", size="M3", x=0, y=0)
    check(f"nut_trap M3 with nut compensation {nc} (hole compensation {hc} must not matter)", t, V0 - (math.sqrt(3) / 2) * across ** 2 * depth, tol=3e-5)
finally:
    for n in names:
        call("delete_object", name=n)

print()
print("RESULT:", f"{sum(results)} passed, {len(results) - sum(results)} failed")
sys.exit(0 if all(results) else 1)
