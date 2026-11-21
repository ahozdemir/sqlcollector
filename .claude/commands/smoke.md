---
description: End-to-end check of the local test environment (setup if needed, all extracts, simulate, incremental, all-tables export to the GCS emulator)
---
Use the `test-runner` subagent to:
1. Run `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/dev.ps1 setup` if `src/SqlDataCollector/appsettings.LocalTest.json` does not exist.
2. Run `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/dev.ps1 smoke`.
3. Report rows per extract job (first and second pass), the export summary line and the number of objects in `gs://test-bucket/exports/`.

Summarize the result for me in Turkish with the numbers.
