# Parts test: round-by-round evidence (21/11/2026)

A small model (Qwen3-1.7B Q8_0, llama.cpp, f16 KV cache unless said otherwise) is asked for 3D-printing parts through the
blender-minor tools. Every part is read back from Blender with `check_mesh` and compared with a size and a **volume worked out by
hand**. Run it with `eval/evaluate_parts.py` (each run also writes `eval/results/parts-<label>-<date>.json`, not committed).
The model ran on an old 2-core i5 with 5.6 GB of RAM at about 4 tokens/s, so a request takes 1 to 5 minutes.

"Lenient" means the volume tolerance was 1%, which turned out to hide wrong holes (see round 7). "Strict" is 0.03% plus the z range.

| Round | What changed | Result | What the evidence showed |
|---|---|---|---|
| R0 | `name` was the **last** property of `create_box` | **0 / 3** | the call came out truncated, `{"name": "base', "}`, so every box was 1x1x1 (volume 1.0). Same request without a name gave perfect arguments; renaming `name` to `object_name` changed nothing; putting it first fixed it. The tool `drill_hole`, with `name` first, was always fine |
| R1 | `name` first in every schema (commit `bacf745`) | **2 / 3** | box + M3 hole 19127.48 and box + 5 mm hole 23882.38 are exact. The bracket failed: the model centred the box on the first hole (8850.0) |
| R2 | KV cache `-ctk q8_0 -ctv q4_0 -fa on` | **2 / 3** | same two parts exact, the bracket got worse (box created twice). Memory 2.15 GB against 2.80 GB. No accuracy gain, so f16 stays |
| R3 | 5 cases (adds `pad`, `pocket`), prompt rule "create_box sits at the origin" | **4 / 5** (lenient) | placement fixed. The bracket failed because the model sent `axis: "X"` to `drill_hole` (8623.6) |
| R4 | prompt says "do NOT pass axis" | bracket still fails | the model sent `axis: "X"` anyway: a prompt does not stop a small model from filling an optional parameter |
| R5 | `--hide` removes `axis`/`side` from `drill_hole` and `screw_hole` | **4 / 5** (lenient) | axis gone. The bracket now failed by creating the box once per hole (8850.0) |
| R6 | prompt gets a two-hole example; `create_box` refuses a name that already exists | bracket **passes** (8749.08) | one box and two `drill_hole` calls |
| R7 | all five cases, final prompt | **5 / 5** (lenient) | **inflated.** The M3 case used `drill_hole` with 3 mm instead of `screw_hole` (19143.5 against 19127.4, a 0.08% difference, under the 1% tolerance) and the `pad` was raised on the **bottom** face after three failed calls (same volume and height as the top face) |
| R8 | **strict**: tolerance 0.03%, `pad` z range checked, `axis`/`side`/`z1`/`z2` hidden from `pad` and `pocket` | **4 / 5** | two holes 8749.08, 5 mm hole 23882.38, `pad` 2320.0 on the top face (z -1..3) in one call, `pocket` 14800.0 are exact. The M3 case failed: the model created the box and **stopped**, no hole (19200.0) |
| R9 | same as R8, run with `--show` (parts left in the Blender window in a row) | **4 / 5** | identical to R8 down to the last digit: at temperature 0 the model repeats its choices, so a rerun is a real check. Evidence JSON saved with the prompt hash, the tools the model saw and every call |
| R10 | rule 5 in the prompt (a request for a hole, pad or pocket is not finished when the box exists); the llama-server now runs as a systemd service from the SSD; direct tool checks run first | **5 / 5** (strict) | all five exact: **M3 hole 19127.483** with `screw_hole` (expected 19127.4), 5 mm hole 23882.38, two holes 8749.08, `pad` 2320.0 on the top face, `pocket` 14800.0. The 17 direct tool checks pass too. First case took 346 s (prompt changed, cold cache), the others 76 to 110 s. At temperature 0 the model repeats its choices, so this is one reproducible run of five fixed cases, not a success rate |
| R11 | **Qwen3-0.6B** Q8_0 (f16 KV), same prompt, same tools, strict check | **0 / 5** | in all five cases the model put the box at the coordinates of the hole or of the area (rule 4 ignored): `create_box` at x=10,y=5 / x=-20,y=10 / x=-8,y=0 / x=-50,y=-4,z=4 / x=-10,y=-10,z=10. It stopped after `create_box` in four of the five (no hole, pad or pocket call); in the bracket it did call `drill_hole` twice, but on a box off the origin (volume 8850.04, expected 8748.7). Same speed as the 1.7B in generation (about 250 ms per token), so the smaller model buys nothing here: **speed with failures is not a choice** |
| R12 | **everything is modeled at the origin**: `x`, `y`, `z` hidden from `create_box`, rule 4 rewritten | **5 / 5** | the box is always created at (0, 0, 0), so the error of R11 (box at the coordinates of the hole) cannot happen in any model; the parts are measured and then moved, as one does in Blender. [trace](rounds/20261121-143937-qwen3-1.7B-q8-f16-tudo-na-origem) |
| R13 | **tools called directly, no model**: 52 checks | **52 / 52** | found and fixed while writing it: `drill_hole` answered OK for a hole in the void (the volume comparison was too tight for floating point); a hole exactly tangent to a face gave a bad mesh (now retried with the cutter nudged by 0.02%); new limit cases with coplanar faces (rectangle = whole face, cut as deep as the part, boxes that only touch) all give the exact volume and a closed mesh. [trace](rounds/20261121-145317-check-tools) |
| R14 | **side holes**: `drill_hole_x` and `drill_hole_y` (11 tools), two new cases | **7 / 7** | a foot with a 6 mm hole along X (58870.84, expected 58869.0 plus the polygon) and an ear with a 5 mm hole along Y (29607.93) are exact on the first try; the five earlier cases are unchanged with the longer prompt. The axis is in the tool name, so the model never has to pick it. [trace](rounds/20261121-150833-qwen3-1.7B-q8-f16-furos-laterais) |

## What carries over to other small models

- The **order of the properties** in a tool schema matters (llama.cpp turns the schema into a grammar that keeps the order).
- Put the **operation in the tool name** (`join`, `subtract`, `pad`, `pocket`), not in a parameter the model has to choose.
- An **optional parameter is filled on impulse** and a prompt does not stop it: hide it (`--hide` in `tools/mcp_stdio_bridge.py`).
- A tool must **not answer OK when nothing changed**, and should refuse a repeated name instead of silently renaming it.
- A longer prompt changes the other cases: always rerun the whole set. And check what the tolerance lets through before trusting a pass.
- **Do not let the model place anything.** The part is modeled at the origin and moved only when it is finished. Hiding `x`, `y`, `z` from `create_box` removed a whole class of errors.
- **Test the limits**, where two faces lie in the same plane: that is where a boolean fails in Blender. A tool must refuse, or retry with a tiny margin, never give a bad mesh.
- **A round hole is a polygon.** With 64 sides it is 0.16% short in area (0.006 mm at a 5 mm radius), with 256 sides 0.01%. `BLENDER_MINOR_SEGMENTS` raises the default for a machine that can afford it.
- **Keep the evidence in git**, with the visual proof made by Blender itself: each round has a folder in [`rounds/`](rounds/) with `WHY.md`, the output, `evidence.json`, `scene.blend` and pictures.

## Open

The M3 case was unstable until rule 5 (R10). Seven fixed cases are still too few to measure a real success rate: the next step is a list of **real parts** (brackets, covers, jigs for building a machine), with the measures the owner actually uses, turned into cases; and other models (the Qwen3-0.6B failed all five in R11).

Every round has its trace in [`rounds/`](rounds/): why it was run, what changed, the result, the code under test, every call and the visual proof. R0 to R8 only have their text output ([`rounds/20261121-000000-historico-r0-a-r8`](rounds/20261121-000000-historico-r0-a-r8)).
