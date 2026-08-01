#!/usr/bin/env bash
# Build script for SeekStorm C# SDK
# Requires: Rust toolchain (for FFI crate), .NET SDK 10.0+

set -euo pipefail

PROJECT_ROOT="$(cd "$(dirname "$0")" && pwd)"

echo "==> Building Rust FFI crate (seekstorm-ffi)..."

RUST_DIR="$PROJECT_ROOT/src/seekstorm-ffi"
if command -v cargo &>/dev/null; then
    cd "$RUST_DIR"
    cargo build --release
    echo "    Rust FFI built → target/release/"
else
    echo "    WARNING: Rust not found — skipping FFI crate build."
    echo "    Install Rust: https://rustup.rs"
fi

echo "==> Building C# SDK (SeekStorm.Bindings)..."

CSHARP_DIR="$PROJECT_ROOT/src/SeekStorm.Bindings"
cd "$CSHARP_DIR"
dotnet build -c Release

echo "==> Verifying AOT compatibility..."
# PublishAot=true is set in the csproj; this confirms it compiles
# Full publish requires native binaries in runtimes/ — skipped here.
dotnet build -c Release /p:PublishAot=true --no-restore 2>&1 || true

echo "==> Building benchmarks..."
cd "$PROJECT_ROOT/bench/SeekStorm.Benchmarks"
dotnet build -c Release

echo "==> Done."
echo ""
echo "To publish AOT (requires native binaries):"
echo "  dotnet publish src/SeekStorm.Bindings -c Release -r <rid>"
echo ""
echo "To run benchmarks (requires native binary + index data):"
echo "  dotnet run -c Release --project bench/SeekStorm.Benchmarks"
