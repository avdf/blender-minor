# R12: everything is modeled at the origin

- **When:** 2026-11-21 14:39:37
- **Why this round:** The 0.6B (R11) put the box at the coordinates of the hole or of the area, and the second hole fell in the void while the tool answered OK. Modeling is done at the origin and a part is moved only when it is finished, so the model must not be able to place the box.
- **What changed since the previous round:** x, y, z hidden from create_box (the box is always at the origin) and rule 4 rewritten: everything is modeled at the origin.
- **Code under test:** commit `?` 
- **Blender:** None | **Host:** None
- **Result:** 5 passed, 0 failed, 0 skipped

Compared with `20261121-142006-qwen3-0.6B-q8-f16` (0 passed, 5 failed):

| case | before | now | volume before | volume now |
|---|---|---|---|---|
| box + M3 hole | FAIL | PASS | 19200.0 | 19127.483 |
| box + 5 mm hole | FAIL | PASS | 24000.0 | 23882.3794 |
| box + 2 holes | FAIL | PASS | 8850.0384 | 8749.0761 |
| pad (raise a strip) | FAIL | PASS | 2000.0 | 2320.0 |
| pocket (cut a recess) | FAIL | PASS | 16000.0 | 14800.0 |


## Files

`output.txt` (what the test printed), `evidence.json` (every call and measurement, with the hashes of the files), `prompt.txt` (the system prompt).

`scene.blend` is the scene as it was when the round ended: open it in Blender to look at the parts.
