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

* **Under an x86-64 translator (Box64)** run the `linux-x64` build. Use the helper
  `run-android-box64.sh` (bakes in the required env, incl. the `DOTNET_EnableAVX2=0`
  workaround below); from bare Termux invoke it as `bash run-android-box64.sh`:

  ```bash
  dotnet publish src/SharpEmu.CLI/SharpEmu.CLI.csproj -c Release -r linux-x64 --self-contained
  bash run-android-box64.sh /path/to/decrypted/eboot.bin
  # equivalently, by hand:
  DOTNET_EnableAVX2=0 BOX64_LOG=0 DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 \
      DOTNET_GCHeapHardLimit=0xC0000000 box64 ./SharpEmu /path/to/decrypted/eboot.bin
  ```

  With `DOTNET_EnableAVX2=0` the full on-device chain runs under Box64: all 154,457
  NID entries register, the SELF/ELF loader parses the image, and the Linux mmap
  memory backend allocates the main image — i.e. HLE + loader + host memory are live
  on the phone. (Verified with a bare-ELF smoke input; a real decrypted game dump is
  still required to go further — see blocker 7.)

### Box64 0.4.3 (needed for real dumps — see blocker 3)

The Termux-packaged **Box64 0.3.2** aborts on `SA_ONSTACK` signal delivery, which
CoreCLR and `LinuxFaultHandling` both rely on. Build a current Box64 in the proot
Ubuntu (glibc) and run the `linux-x64` build under it there:

```bash
# inside proot Ubuntu (glibc); one-time
apt-get install -y build-essential cmake git
git clone --depth 1 https://github.com/ptitSeb/box64 && cd box64
cmake -S . -B build -DARM_DYNAREC=ON -DCMAKE_BUILD_TYPE=Release && make -C build -j4

# run (from inside proot)
cd /path/to/sharpemu/artifacts/bin/Release/net10.0/linux-x64
DOTNET_EnableAVX2=0 DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 DOTNET_GCHeapHardLimit=0xC0000000 \
    /root/box64/build/box64 ./SharpEmu /path/to/decrypted/eboot.bin --log-level=info
```

With 0.4.3 a real decrypted dump loads fully — main eboot + all PRX modules, ~90k
symbols, `unresolved=0` — and reaches the first guest module initializer with faults
surfaced cleanly instead of aborting (see blocker 4).

## Known blockers to actually booting a game

1. **Box64 + CoreCLR reflection — RESOLVED (`DOTNET_EnableAVX2=0`).** During HLE
   registration, reading the `[SysAbiExport(Target = Generation.…)]` attributes
   (applied in `SharpEmu.Libs`, whose `Generation` enum lives in the referenced
   `SharpEmu.HLE`) tripped a `TypeLoadException` resolving
   `SharpEmu.HLE.Generation` — **only** under Box64, in both dynarec and
   interpreter modes; the identical build runs through this step natively on ARM64
   and x86-64.

   Root cause: the attribute blob stores the enum's **assembly-qualified** name
   (`"SharpEmu.HLE.Generation, SharpEmu.HLE, Version=…"`). .NET's vectorized
   type-name parser splits that on `','` using an **AVX2 / `Vector256`** code path,
   and Box64 (tested v0.3.2) mis-emulates that instruction so the `", SharpEmu.HLE,
   …"` assembly component is lost. The parser then probes only the *default*
   assemblies (`GetTypeFromDefaultAssemblies`), which don't contain the enum, and
   throws. Failing in both Box64 modes matches a shared SIMD helper, not codegen;
   the scalar path is correct, which is why native ARM64/x86-64 are fine and a
   minimal cross-assembly repro (that happened not to hit the vectorized branch)
   didn't reproduce.

   Fix: set `DOTNET_EnableAVX2=0` (forces the scalar/SSE `IndexOf`, keeps every
   other SIMD family). With it, HLE registration completes and execution proceeds
   into the loader and Linux memory backend. Baked into `run-android-box64.sh`.

2. **Import-stub region VA on small address spaces — RESOLVED (adaptive base).**
   The loader reserved its import-stub region at a fixed `0x0000_7000_0000_0000`
   (~112 TiB) canonical base, walking downward. Android/ARM64 kernels commonly use
   a **39-bit (512 GiB) user VA**, so every `MAP_FIXED_NOREPLACE` there failed and
   the loader threw *"Unable to reserve an import stub region in virtual memory"*
   — right after mapping the main image (which fits at ~34 GiB).

   Fix: the stub region base is now adaptive. `SelfLoader` (which maps the stubs)
   and `DirectExecutionBackend` (which recognises stub addresses at runtime) both
   try the canonical base first, then a **128 GiB fallback**
   (`0x0000_0020_0000_0000`) that fits a 39-bit VA and stays clear of the guest
   image/module window (≤ ~36 GiB). Desktop x86-64 keeps using the canonical base
   (first attempt succeeds), so its behaviour is unchanged. With this, on-device
   loading of a real decrypted `eboot.bin` completes end to end: main eboot + all
   dependent PRX modules load, every import stub is created, ~90k symbols resolve,
   `unresolved=0`, and control reaches the first guest module initializer.

3. **Box64 signal handling — needs Box64 ≥ 0.4.x (packaged 0.3.2 is too old).**
   With 1 and 2 fixed, a real dump under the Termux-packaged **Box64 0.3.2** aborts
   hard — `BOX64: calling Signal 11 function handler SIG_DFL` / *"Unhandled signal
   caught, aborting"* — at varying points (module load, GC, guest entry). Cause:
   0.3.2 mis-delivers `SA_ONSTACK`/`sigaltstack` signals, so the `SA_SIGINFO |
   SA_ONSTACK` `SIGSEGV` handlers that both CoreCLR (GC/null-ref) and
   `LinuxFaultHandling` install never run, and otherwise-recoverable faults escalate
   to abort. **Building current Box64 (0.4.3) from source fixes this**: faults are
   caught and surfaced cleanly instead of aborting. Simplest route is to build it in
   the proot Ubuntu (glibc) and run the `linux-x64` build under it there — see the
   "Box64 0.4.3" note below.

4. **Entry-frame + guest-thread region VA — RESOLVED (adaptive fallback).** Guest
   execution setup (`CpuDispatcher` stack/TLS/return-to-host/bootstrap/dynlib stub
   regions and `DirectExecutionBackend` guest-thread stack/TLS) hard-coded bases near
   the **top of a 47-bit x86-64 user VA (~128 TiB)**. On Android's 39-bit VA every
   one failed, so `DispatchEntryCore` returned `MEMORY_FAULT` before executing any
   guest instruction. Fixed the same way as the import stubs: each region gets a
   512-GiB-safe fallback base (entry-frame band ~360-384 GiB; guest-thread bands
   ~332/352 GiB) tried after the canonical base. Desktop x86-64 unchanged.

5. **`mprotect` page alignment — RESOLVED.** `LinuxHostMemory.Protect/ProtectRaw/
   Commit` passed byte-granular addresses (e.g. an import stub at `base+0x10`)
   straight to `mprotect`, which requires page alignment and returns `EINVAL`
   otherwise — so `PatchImportStub` failed on the first stub. Windows `VirtualProtect`
   (the sibling backend) rounds to whole pages; the Linux side now does the same.

6. **Executing guest x86-64 — current frontier (HLE-dispatch stack switch).** With
   1-5 fixed and Box64 0.4.3, a real dump loads fully, patches **all 1068 import
   stubs** (267 LLE redirects), and **calls the guest entry point**
   (`ExecuteEntry starting at 0x804000010`, `Calling guest entry…`) — i.e. guest
   x86-64 now executes on-device. It then faults (`SIGSEGV`) with the emulated PC
   inside **`libclrjit.so`** and `rsp` in the HLE-trampoline region (~1.8 GiB): the
   guest called an imported function, dispatch switched into managed HLE code, and
   CoreCLR's JIT faulted on a stack access — a guest↔host stack-switch problem in the
   native execution engine under Box64. This is the deep native-execution/Box64
   boundary; the Linux host backend itself now runs through load, entry-frame setup,
   import patching, and into guest execution. Open.

7. **A decrypted dump is required.** SharpEmu is a high-level emulator: it loads
   an already-decrypted `eboot.bin`/ELF and reimplements the system libraries. It
   does not (and this port does not) handle console keys or firmware decryption.

8. **Upstream maturity.** Even on desktop x86-64, upstream currently reaches at
   most an early video loop / shader-conversion stage on its lead titles — no
   in-game menu yet on any host. ARM/Android inherits that ceiling.
