#!/usr/bin/env python3
"""Native shard failures can be logged while the engine still returns success."""
import re
import subprocess
import sys
result = subprocess.run(["dotnet", "run", "--project", "test/SeekStorm.SmokeTest", "-c", "Release", "--no-build"], capture_output=True, text=True, encoding="utf-8", errors="replace")
print(result.stdout, end="")
print(result.stderr, end="", file=sys.stderr)
failed = re.search(r"panicked at|JoinError::Panic|commit shard task failed", result.stdout + result.stderr)
if failed:
    print("Native background failure detected; smoke exit code alone is insufficient.", file=sys.stderr)
sys.exit(result.returncode or (1 if failed else 0))
