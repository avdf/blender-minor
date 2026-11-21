# R11: the same job with the Qwen3-0.6B

- **When:** 2026-11-21 14:20:06
- **Why this round:** Is a smaller and faster model good enough for the parts job? Speed with failures is not a choice, so the strict check decides.
- **What changed since the previous round:** Only the model: Qwen3-0.6B Q8_0 instead of the 1.7B. Same prompt, same tools, same check.
- **Code under test:** commit `?` 
- **Blender:** None | **Host:** None
- **Result:** 0 passed, 5 failed, 0 skipped

Compared with `20261121-141317-qwen3-1.7B-q8-f16-regra5-systemd` (5 passed, 0 failed):

| case | before | now | volume before | volume now |
|---|---|---|---|---|
| box + M3 hole | PASS | FAIL | 19127.483 | 19200.0 |
| box + 5 mm hole | PASS | FAIL | 23882.3794 | 24000.0 |
| box + 2 holes | PASS | FAIL | 8749.0761 | 8850.0384 |
| pad (raise a strip) | PASS | FAIL | 2320.0 | 2000.0 |
| pocket (cut a recess) | PASS | FAIL | 14800.0 | 16000.0 |


## Files

`output.txt` (what the test printed), `evidence.json` (every call and measurement, with the hashes of the files), `prompt.txt` (the system prompt).

`scene.blend` is the scene as it was when the round ended: open it in Blender to look at the parts.
