# R10: rule 5 in the prompt, llama-server as a systemd service

- **When:** 2026-11-21 14:13:17
- **Why this round:** The M3 case failed in R8 and R9: the model created the box and stopped, without calling the hole tool.
- **What changed since the previous round:** Rule 5 in the parts prompt (a request for a hole, pad or pocket is not finished when the box exists). The llama-server now runs as a systemd service from the SSD.
- **Code under test:** commit `?` 
- **Blender:** None | **Host:** None
- **Result:** 5 passed, 0 failed, 0 skipped

Compared with `20261121-040608-qwen3-1.7B-q8-f16-show` (4 passed, 1 failed):

| case | before | now | volume before | volume now |
|---|---|---|---|---|
| box + M3 hole | FAIL | PASS | 19200.0 | 19127.483 |
| box + 5 mm hole | PASS | PASS | 23882.3794 | 23882.3794 |
| box + 2 holes | PASS | PASS | 8749.0761 | 8749.0761 |
| pad (raise a strip) | PASS | PASS | 2320.0 | 2320.0 |
| pocket (cut a recess) | PASS | PASS | 14800.0 | 14800.0 |


## Files

`output.txt` (what the test printed), `evidence.json` (every call and measurement, with the hashes of the files), `prompt.txt` (the system prompt).

`scene.blend` is the scene as it was when the round ended: open it in Blender to look at the parts.
