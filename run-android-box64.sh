#!/usr/bin/env bash
# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later
#
# Run the linux-x64 SharpEmu build on an ARM64 Android phone (Termux) under Box64.
#
# Usage:
#   ./run-android-box64.sh /path/to/decrypted/eboot.bin [extra SharpEmu args...]
#
# Prerequisites:
#   * A self-contained linux-x64 publish of SharpEmu.CLI, e.g.:
#       dotnet publish src/SharpEmu.CLI/SharpEmu.CLI.csproj -c Release -r linux-x64 --self-contained
#   * Box64 installed (Termux glibc): $HOME/../usr/glibc/bin/box64  (override with BOX64=)
#
# Why DOTNET_EnableAVX2=0:
#   Box64 (tested v0.3.2) mis-emulates an AVX2 / Vector256 instruction that .NET's
#   vectorized type-name parser uses to split an assembly-qualified type name on ','.
#   Without this flag, HLE registration throws
#     TypeLoadException: Could not resolve type 'SharpEmu.HLE.Generation, SharpEmu.HLE, ...'
#   because the parser loses the ", SharpEmu.HLE, ..." assembly component and then only
#   probes the default assemblies. It fails identically in Box64 dynarec AND interpreter
#   modes (shared SIMD helper), and does NOT occur natively on ARM64 or x86-64.
#   Disabling only AVX2 forces the scalar/SSE path and keeps every other SIMD family.

set -euo pipefail

EBOOT="${1:?usage: run-android-box64.sh <eboot.bin> [args...]}"
shift || true

BOX64="${BOX64:-$HOME/../usr/glibc/bin/box64}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PUBLISH_DIR="${SHARPEMU_PUBLISH_DIR:-$SCRIPT_DIR/artifacts/bin/Release/net10.0/linux-x64}"
APP="$PUBLISH_DIR/SharpEmu"

[ -x "$BOX64" ] || { echo "box64 not found/executable at: $BOX64 (set BOX64=)" >&2; exit 1; }
[ -x "$APP" ]   || { echo "SharpEmu apphost not found at: $APP (set SHARPEMU_PUBLISH_DIR=)" >&2; exit 1; }

cd "$PUBLISH_DIR"
# SHARPEMU_DISABLE_NATIVE_GUEST_WORKERS: Box64 gives raw pthreads whose entry is
#   emitted x86-64 code an undersized emulated stack, so the pooled native guest
#   workers overflow it as soon as CoreCLR JITs on that thread. The inline calli
#   path runs guest frames on the managed thread's full stack and works.
# DOTNET_EnableWriteXorExecute=0 / TieredCompilation=0 / big gen0: fewer runtime
#   remaps and GC suspensions under Box64's signal handling.
exec env \
    DOTNET_EnableAVX2=0 \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 \
    DOTNET_GCHeapHardLimit=0xC0000000 \
    DOTNET_EnableWriteXorExecute=0 \
    DOTNET_TieredCompilation=0 \
    DOTNET_GCgen0size=0x10000000 \
    SHARPEMU_DISABLE_NATIVE_GUEST_WORKERS="${SHARPEMU_DISABLE_NATIVE_GUEST_WORKERS:-1}" \
    BOX64_LOG="${BOX64_LOG:-0}" \
    "$BOX64" ./SharpEmu "$EBOOT" "$@"
