"""Verify the distributable itself, not the source-tree runtime folders."""
import sys
import zipfile
from pathlib import Path

expected = {
    "linux-x64": "libseekstorm_ffi.so",
    "linux-arm64": "libseekstorm_ffi.so",
    "osx-x64": "libseekstorm_ffi.dylib",
    "osx-arm64": "libseekstorm_ffi.dylib",
    "win-x64": "seekstorm_ffi.dll",
    "win-arm64": "seekstorm_ffi.dll",
}
package = next(Path(sys.argv[1]).glob("SeekStorm.Bindings.*.nupkg"))
with zipfile.ZipFile(package) as archive:
    names = set(archive.namelist())
    required = {"lib/net10.0/SeekStorm.Bindings.dll"}
    required.update(f"runtimes/{rid}/native/{binary}" for rid, binary in expected.items())
    missing = required - names
    if missing:
        raise SystemExit(f"Missing package assets: {sorted(missing)}")
    for name in required:
        if archive.getinfo(name).file_size == 0:
            raise SystemExit(f"Empty package asset: {name}")
    print(f"Verified managed assembly and all {len(expected)} native runtimes in {package}")
