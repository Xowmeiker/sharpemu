<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# Running SharpEmu on ARM64 / Android (Termux)

This branch adds a **Linux host-platform backend** so SharpEmu builds and runs on
Linux, including ARM64 devices (tested on a Snapdragon 8 Elite / Galaxy S25 Ultra
under Termux + a proot Ubuntu 26.04 with the .NET 10 SDK).

## What was added

Previously the native execution engine was Windows-only: `HostPlatform.Create`
returned a `WindowsHostPlatform` and threw `PlatformNotSupportedException`
everywhere else, and the sole `IHostFaultHandling` implementation was the Win64
vectored-exception thunk.

New, mirroring the existing Windows classes 1:1:

| Concern | Windows | New Linux sibling |
|---|---|---|
| Page memory | `WindowsHostMemory` (VirtualAlloc) | `LinuxHostMemory` (mmap/mprotect, `/proc/self/maps` for Query) |
| Threads / TLS | `WindowsHostThreading` | `LinuxHostThreading` (pthreads, pthread-key TLS, eventfd) |
| Runtime-fn addresses | `WindowsHostSymbolResolver` | `LinuxHostSymbolResolver` |
| Fault interception | `WindowsFaultHandling` (VEH) | `LinuxFaultHandling` (sigaction + sigaltstack) |

Notable design points:

* **Native events.** The emitted worker run-loop calls `WaitForSingleObject` /
  `SetEvent` / `ExitThread` at addresses baked into machine code. `IHostThreading`
  gained `CreateNativeEvent`/`SignalNativeEvent`/`WaitNativeEvent`/`CloseNativeEvent`;
  the Linux side backs them with an **eventfd** (auto-reset via a counting read).
* **Calling-convention bridge.** The emitted stubs use the **Win64** convention
  (args in `rcx`/`rdx`). `LinuxHostSymbolResolver` emits a tiny per-function
  `mov rdi,rcx ; mov rsi,rdx ; movabs rax,impl ; jmp rax` thunk in front of a
  managed SysV implementation, so no stub code had to change.
* **Fault handler.** The managed handlers were written against Win64
  `EXCEPTION_POINTERS` + `CONTEXT`. Rather than fork them, `LinuxFaultHandling`
  installs a `sigaction` handler that synthesizes those structures from the
  delivered `ucontext`, calls the same managed callbacks, and copies any mutated
  registers back on `EXCEPTION_CONTINUE_EXECUTION` (−1). `sigaltstack` replaces
  the Windows thunk's manual guest→host stack switch.

## Build (in a glibc environment; Termux is bionic, so use proot)

```bash
# one-time: install the pinned SDK from global.json inside proot Ubuntu
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --version 10.0.103 --install-dir /opt/dotnet

# Android needs an explicit GC heap cap (the default reserves 256 GiB of range)
export DOTNET_GCHeapHardLimit=0xC0000000
/opt/dotnet/dotnet build src/SharpEmu.CLI/SharpEmu.CLI.csproj -c Release -m:1
```

## Run

Guest code and the emitted stubs are **x86-64**, so the host *process* must be
x86-64:

* **On a native ARM64 process** the emulator initializes fully (loads the ~154k
  NID table, warms type initializers, JIT-compiles the HLE surface) and then, by
  design, throws at `HostPlatform.Create` — an ARM CPU cannot execute the x86-64
  guest/stub code. This is the "everything except the CPU substrate works" path
  and is useful for validating loading/HLE on-device.

* **Under an x86-64 translator (Box64)** run the `linux-x64` build:

  ```bash
  dotnet publish src/SharpEmu.CLI/SharpEmu.CLI.csproj -c Release -r linux-x64 --self-contained
  BOX64_LOG=0 DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 DOTNET_GCHeapHardLimit=0xC0000000 \
      box64 ./SharpEmu /path/to/decrypted/eboot.bin
  ```

## Known blockers to actually booting a game

1. **Box64 + CoreCLR reflection.** During HLE registration, reading the
   `[SysAbiExport(Target = Generation.…)]` attributes trips a
   `TypeLoadException` resolving `SharpEmu.HLE.Generation` — **only** under
   Box64, in both dynarec and interpreter modes; the identical build runs
   through this step natively on ARM64 and x86-64. A minimal cross-assembly
   enum-attribute test does **not** reproduce it, so it is a narrow Box64/CoreCLR
   interaction (a metadata/assembly-probing edge), not an emulator bug. This
   blocks the Box64 path before guest execution begins.

2. **A decrypted dump is required.** SharpEmu is a high-level emulator: it loads
   an already-decrypted `eboot.bin`/ELF and reimplements the system libraries. It
   does not (and this port does not) handle console keys or firmware decryption.

3. **Upstream maturity.** Even on desktop x86-64, upstream currently reaches at
   most an early video loop / shader-conversion stage on its lead titles — no
   in-game menu yet on any host. ARM/Android inherits that ceiling.
