#!/usr/bin/env bash
#
# Builds the CuteDB native accelerator and stages it where the .NET build expects it.
#
# The POSIX twin of build.ps1. Compiles native/cutedb-core with cargo and copies the shared
# library to native/artifacts/<rid>/, which is where CuteDB.csproj looks for it.
#
# Nothing here is required to use CuteDB: the managed library implements the same behaviour and
# takes over whenever the accelerator is absent. A machine with no Rust toolchain builds and tests
# the whole solution normally, it just scans large collections more slowly.
#
# Usage:
#   ./build.sh                              build for this machine
#   ./build.sh aarch64-apple-darwin         cross-compile for Apple silicon
#   CONFIGURATION=debug ./build.sh          build unoptimised

set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
configuration="${CONFIGURATION:-release}"

if ! command -v cargo >/dev/null 2>&1; then
    echo "cargo was not found. Install Rust from https://rustup.rs to build the accelerator." >&2
    echo "CuteDB builds and runs without it; scans of large collections use the managed path." >&2
    exit 0
fi

target="${1:-$(rustc -vV | awk '/^host:/ { print $2 }')}"

# Rust target triples and .NET runtime identifiers name the same platforms differently, and the
# package layout uses the .NET spelling.
case "$target" in
    x86_64-pc-windows*)     rid="win-x64";     library="cutedb_core.dll" ;;
    aarch64-pc-windows*)    rid="win-arm64";   library="cutedb_core.dll" ;;
    x86_64-unknown-linux*)  rid="linux-x64";   library="libcutedb_core.so" ;;
    aarch64-unknown-linux*) rid="linux-arm64"; library="libcutedb_core.so" ;;
    x86_64-apple-darwin)    rid="osx-x64";     library="libcutedb_core.dylib" ;;
    aarch64-apple-darwin)   rid="osx-arm64";   library="libcutedb_core.dylib" ;;
    *)
        echo "No .NET runtime identifier is mapped for the Rust target '$target'." >&2
        exit 1
        ;;
esac

echo "Building cutedb_core for $target -> $rid"

cargo_args=(build --manifest-path "$root/Cargo.toml" --target "$target")
if [ "$configuration" = "release" ]; then
    cargo_args+=(--release)
fi

cargo "${cargo_args[@]}"

built="$root/target/$target/$configuration/$library"
if [ ! -f "$built" ]; then
    echo "cargo reported success but '$built' is not there." >&2
    exit 1
fi

destination="$root/artifacts/$rid"
mkdir -p "$destination"
cp -f "$built" "$destination/$library"

echo "Staged artifacts/$rid/$library ($(du -h "$destination/$library" | cut -f1))"
