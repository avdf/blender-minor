# R9: strict check, 1.7B, parts left in the Blender window

- **When:** 2026-11-21 04:06:08
- **Why this round:** Confirm that the strict check (0.03% volume tolerance, pad z range) gives the same result when run again, and look at the parts in the Blender window.
- **What changed since the previous round:** Same code and prompt as R8; added --show (the parts stay in the scene in a row).
- **Code under test:** commit `?` 
- **Blender:** None | **Host:** None
- **Result:** 4 passed, 1 failed, 0 skipped

## Files

`output.txt` (what the test printed), `evidence.json` (every call and measurement, with the hashes of the files), `prompt.txt` (the system prompt).
