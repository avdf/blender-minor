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

## What carries over to other small models

- The **order of the properties** in a tool schema matters (llama.cpp turns the schema into a grammar that keeps the order).
- Put the **operation in the tool name** (`join`, `subtract`, `pad`, `pocket`), not in a parameter the model has to choose.
- An **optional parameter is filled on impulse** and a prompt does not stop it: hide it (`--hide` in `tools/mcp_stdio_bridge.py`).
- A tool must **not answer OK when nothing changed**, and should refuse a repeated name instead of silently renaming it.
- A longer prompt changes the other cases: always rerun the whole set. And check what the tolerance lets through before trusting a pass.

## Open

The M3 case is unstable: depending on the prompt the model uses `screw_hole`, a 3 mm `drill_hole`, or stops after `create_box`.
Five fixed cases are too few to measure a real success rate; vary the numbers.

The raw output of each round is kept locally in `eval/results/rodadas-2026-11-21/` (not committed).
