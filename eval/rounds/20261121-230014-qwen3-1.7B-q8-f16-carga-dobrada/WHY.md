# qwen3-1.7B-q8-f16-carga-dobrada

- **When:** 2026-11-21 23:00:20
- **Why this round:** the WHOLE set (`--set everything`, 38 items) restarted from zero with the corrected test (long timeout, measurement retried, `infra` flag), llama pinned to CPUs 0,1, no browser media, 21 tools
- **What changed since the previous round:** the set grew from 7 cases to 38 (12 fixed, variations, composites, coverage holes, questions, read-only tools), and the 21-tool profile replaced the 11-tool one
- **Code under test:** commit `9c32e61821` eval: full round set, infra retry, direct-tools mode and round traces (+ 2 uncommitted file(s), diff sha256 ``)
- **Blender:** 3.6.23 | **Host:** Intel(R) Core(TM) i5-3317U CPU @ 1.70GHz
- **Result:** 33 passed, 5 failed, 0 skipped

Compared with `20261121-150833-qwen3-1.7B-q8-f16-furos-laterais` (7 passed, 0 failed):

| case | before | now | volume before | volume now |
|---|---|---|---|---|
| box + M3 hole | PASS | FAIL | 19127.483 | 19143.5421 |
| box + 5 mm hole | PASS | PASS | 23882.3794 | 23882.3794 |
| box + 2 holes | PASS | PASS | 8749.0761 | 8749.0761 |
| pad (raise a strip) | PASS | PASS | 2320.0 | 2320.0 |
| pocket (cut a recess) | PASS | PASS | 14800.0 | 14800.0 |
| side hole along X (a foot) | PASS | PASS | 58870.8426 | 58870.8426 |
| nut trap (top) | - | PASS | None | 8926.8434 |
| screw hole + nut trap underneath | - | PASS | None | 12677.8944 |
| pad on the right face (a foot) | - | PASS | None | 60300.0 |
| pocket on the left face (a foot) | - | PASS | None | 59800.0 |
| side hole along Y (an ear) | PASS | PASS | 29607.9314 | 29607.9314 |
| variant: request in Portuguese | - | PASS | None | 8698.8913 |
| variant: coordinates in parentheses | - | PASS | None | 7161.2528 |
| variant: M4 screw hole | - | PASS | None | 15591.2122 |
| variant: M5 screw hole | - | FAIL | None | 19043.1726 |
| three holes | - | PASS | None | 9430.6264 |
| four corner holes (the model works out the positions) | - | FAIL | None | 17437.269 |
| variant: pad, other numbers | - | PASS | None | 5600.0 |
| variant: pocket, other numbers | - | PASS | None | 39200.0 |
| pad on the left face | - | PASS | None | 36160.0 |
| pocket on the right face | - | PASS | None | 35700.0 |
| pad on the front face | - | PASS | None | 36200.0 |
| pocket on the back face | - | PASS | None | 35700.0 |
| variant: M4 nut trap on top | - | PASS | None | 19047.3578 |
| variant: M2.5 nut trap underneath | - | PASS | None | 7148.4819 |
| variant: side hole along X | - | PASS | None | 71623.6142 |
| variant: side hole along Y | - | PASS | None | 69992.609 |
| composite: hole + pad + pocket on one plate | - | PASS | None | 19943.1726 |
| composite: a foot with a hole along X and another along Y | - | PASS | None | 58282.7397 |
| composite: two M3 holes, each with a nut trap underneath | - | PASS | None | 6155.7888 |
| pad on the back face | - | PASS | None | 36200.0 |
| pocket on the front face | - | PASS | None | 35700.0 |
| array: three copies along X, two along Y | - | PASS | None | 2400.0 |
| question: what tools do you have (English) | - | FAIL | None | None |
| question: what tools do you have (Portuguese) | - | PASS | None | None |
| tool: check_mesh (is the part printable?) | - | PASS | None | None |
| tool: export_stl (a file for the slicer) | - | PASS | None | None |
| question: what is in the scene (must call list_objects) | - | FAIL | None | None |


## Visual proof

![viewport.png](viewport.png)
![window.png](window.png)

`scene.blend` is the scene itself: open it in Blender to look at the parts.

## Files

`output.txt` (what the test printed), `evidence.json` (every call and measurement, with the hashes of the files), `prompt.txt` (the system prompt), `scene.blend`, `window.png`, `viewport.png`.

## Visual evidence (note written after the round, 21/11/2026)
- `window.png` and `viewport.png` (taken by Blender itself with `bpy.ops.screen.screenshot`) came out **entirely black**: the round ran inside an xrdp
  session (display :10, llvmpipe), where Blender's own screenshot reads an empty buffer. They are kept as produced.
- `viewport-x11.png` is the 3D viewport of the same Blender window taken **from the X server** (`xwd`, converted to PNG), a few minutes after the last case,
  with the scene as the round left it (all the parts in the grid). The numbers of the round do NOT come from the images: they come from measuring the meshes in Blender.
