#!/usr/bin/env bash
set -euo pipefail
PROJECT_ROOT="$(cd "$(dirname "$0")" && pwd)"
cd "$PROJECT_ROOT"
command -v cargo >/dev/null || { echo "Rust is required: https://rustup.rs" >&2; exit 1; }
case "$(uname -s)-$(uname -m)" in
  Darwin-arm64) rid=osx-arm64; binary=libseekstorm_ffi.dylib ;;
  Darwin-x86_64) rid=osx-x64; binary=libseekstorm_ffi.dylib ;;
  Linux-x86_64) rid=linux-x64; binary=libseekstorm_ffi.so ;;
  Linux-aarch64) rid=linux-arm64; binary=libseekstorm_ffi.so ;;
  *) echo "Use the CI native build steps for this host." >&2; exit 1 ;;
esac
cargo build --release --locked --manifest-path src/seekstorm-ffi/Cargo.toml
mkdir -p "runtimes/$rid/native"
cp "src/seekstorm-ffi/target/release/$binary" "runtimes/$rid/native/"
dotnet build -c Release --warnaserror
dotnet test -c Release --no-build
dotnet run --project test/SeekStorm.SmokeTest -c Release --no-build
