// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Generic;
using System.Runtime.InteropServices;
using SharpEmu.Core.Cpu.Native.Windows;
using SharpEmu.HLE.Host;

namespace SharpEmu.Core.Cpu.Native.Linux;

/// <summary>
/// POSIX sibling of <see cref="WindowsFaultHandling"/>. The execution engine's
/// managed handlers were written against Win64 EXCEPTION_POINTERS and a Win64
/// CONTEXT record; rather than fork them, this installs a sigaction handler that
/// synthesizes those structures from the delivered ucontext, invokes the same
/// managed callbacks, and — when a callback asks to resume (returns -1) — copies
/// the possibly-mutated CONTEXT registers back into the ucontext before
/// returning. sigaltstack replaces the manual guest->host stack switch the
/// Windows thunk emitted, so no per-handler machine code is generated here.
/// This runs on x86-64 hosts (including x86-64 guest code under a translator,
/// whose signal frames are themselves x86-64), matching the guest ISA.
/// </summary>
internal sealed unsafe partial class LinuxFaultHandling : IHostFaultHandling
{
    private const int SIGSEGV = 11;
    private const int SIGBUS = 7;
    private const int SIGILL = 4;
    private const int SIGFPE = 8;
    private const int SIGTRAP = 5;

    private const int SA_SIGINFO = 0x00000004;
    private const int SA_ONSTACK = 0x08000000;
    private const int SA_RESTART = 0x10000000;

    private const int SS_SIZE = 256 * 1024;

    // glibc x86-64 ucontext_t: uc_flags(8) + uc_link(8) + uc_stack(24) = 40,
    // then uc_mcontext.gregs[] as 23 longs.
    private const int GregsBase = 40;

    // Register indices into gregs[]; the same numbering the kernel/glibc use.
    private const int REG_R8 = 0, REG_R9 = 1, REG_R10 = 2, REG_R11 = 3;
    private const int REG_R12 = 4, REG_R13 = 5, REG_R14 = 6, REG_R15 = 7;
    private const int REG_RDI = 8, REG_RSI = 9, REG_RBP = 10, REG_RBX = 11;
    private const int REG_RDX = 12, REG_RAX = 13, REG_RCX = 14, REG_RSP = 15;
    private const int REG_RIP = 16, REG_EFL = 17;

    // siginfo_t.si_addr sits at offset 16 on x86-64 for SIGSEGV/SIGBUS.
    private const int SiAddrOffset = 16;

    // A synthesized CONTEXT only needs to be large enough for the Win64 integer
    // offsets the managed handlers read (up to RIP at 248, plus slack).
    private const int SynthContextSize = 0x4D0;

    private static readonly List<nint> FirstChanceCallbacks = new();
    private static readonly object InstallLock = new();
    private static bool _installed;

    // Per-signal previous disposition, so CONTINUE_SEARCH chains onward.
    private static SigAction _prevSegv, _prevBus, _prevIll, _prevFpe, _prevTrap;

    private readonly IHostMemory _memory;

    public LinuxFaultHandling(IHostMemory memory)
    {
        _memory = memory;
    }

    public nint CreateHandlerThunk(nint managedCallback, uint hostRspSwitchTlsSlot, nint tlsGetValueAddress)
    {
        // No emitted thunk: sigaltstack handles the stack switch and signal
        // numbers map directly to exception codes. The "thunk" identity is just
        // the managed callback pointer, which AddFirstChanceHandler registers.
        _ = hostRspSwitchTlsSlot;
        _ = tlsGetValueAddress;
        return managedCallback;
    }

    public void FreeThunk(nint thunk)
    {
    }

    public nint AddFirstChanceHandler(nint thunk)
    {
        lock (InstallLock)
        {
            // First-chance order = installation order; the newest is tried first
            // to mirror AddVectoredExceptionHandler(first: 1).
            FirstChanceCallbacks.Insert(0, thunk);
            EnsureInstalled();
        }

        return thunk;
    }

    public void RemoveHandler(nint handle)
    {
        lock (InstallLock)
        {
            FirstChanceCallbacks.Remove(handle);
        }
    }

    public void SetUnhandledFilter(nint thunk)
    {
        // The unhandled filter is the last-chance handler; register it at the end
        // of the first-chance list so it runs only after every real handler
        // returned CONTINUE_SEARCH. A 0 clears any previously set one.
        lock (InstallLock)
        {
            if (thunk == 0)
            {
                return;
            }

            FirstChanceCallbacks.Add(thunk);
            EnsureInstalled();
        }
    }

    private void EnsureInstalled()
    {
        if (_installed)
        {
            return;
        }

        // Dedicated alternate signal stack: guest faults arrive on a guest stack
        // the CLR must never run managed frames on.
        var altStack = new SigAltStack
        {
            ss_sp = (void*)_memory.Allocate(0, SS_SIZE, HostPageProtection.ReadWrite),
            ss_flags = 0,
            ss_size = SS_SIZE,
        };
        _ = sigaltstack(&altStack, null);

        var handler = (nint)(delegate* unmanaged<int, void*, void*, void>)&SignalEntry;
        Install(SIGSEGV, handler, out _prevSegv);
        Install(SIGBUS, handler, out _prevBus);
        Install(SIGILL, handler, out _prevIll);
        Install(SIGFPE, handler, out _prevFpe);
        Install(SIGTRAP, handler, out _prevTrap);
        _installed = true;
    }

    private static void Install(int signo, nint handler, out SigAction previous)
    {
        var action = new SigAction
        {
            sa_handler = handler,
            sa_flags = SA_SIGINFO | SA_ONSTACK | SA_RESTART,
        };
        // sa_mask is a 128-byte set left zeroed (block nothing extra).
        previous = default;
        _ = sigaction(signo, &action, out previous);
    }

    [UnmanagedCallersOnly]
    private static void SignalEntry(int signo, void* siginfo, void* ucontext)
    {
        byte* gregs = (byte*)ucontext + GregsBase;

        // Build a Win64 CONTEXT the managed handlers can read/write at their
        // usual offsets. Zeroed first so unread fields are well-defined.
        byte* ctx = stackalloc byte[SynthContextSize];
        for (int i = 0; i < SynthContextSize; i++)
        {
            ctx[i] = 0;
        }

        CopyGregToCtx(gregs, ctx, REG_RAX, Win64ContextOffsets.Rax);
        CopyGregToCtx(gregs, ctx, REG_RCX, Win64ContextOffsets.Rcx);
        CopyGregToCtx(gregs, ctx, REG_RDX, Win64ContextOffsets.Rdx);
        CopyGregToCtx(gregs, ctx, REG_RBX, Win64ContextOffsets.Rbx);
        CopyGregToCtx(gregs, ctx, REG_RSP, Win64ContextOffsets.Rsp);
        CopyGregToCtx(gregs, ctx, REG_RBP, Win64ContextOffsets.Rbp);
        CopyGregToCtx(gregs, ctx, REG_RSI, Win64ContextOffsets.Rsi);
        CopyGregToCtx(gregs, ctx, REG_RDI, Win64ContextOffsets.Rdi);
        CopyGregToCtx(gregs, ctx, REG_R8, Win64ContextOffsets.R8);
        CopyGregToCtx(gregs, ctx, REG_R9, Win64ContextOffsets.R9);
        CopyGregToCtx(gregs, ctx, REG_R10, Win64ContextOffsets.R10);
        CopyGregToCtx(gregs, ctx, REG_R11, Win64ContextOffsets.R11);
        CopyGregToCtx(gregs, ctx, REG_R12, Win64ContextOffsets.R12);
        CopyGregToCtx(gregs, ctx, REG_R13, Win64ContextOffsets.R13);
        CopyGregToCtx(gregs, ctx, REG_R14, Win64ContextOffsets.R14);
        CopyGregToCtx(gregs, ctx, REG_R15, Win64ContextOffsets.R15);
        CopyGregToCtx(gregs, ctx, REG_RIP, Win64ContextOffsets.Rip);

        ulong faultAddr = *(ulong*)((byte*)siginfo + SiAddrOffset);
        uint code = SignalToExceptionCode(signo);
        ulong rip = ReadGreg(gregs, REG_RIP);

        // EXCEPTION_RECORD: ExceptionCode(0) Flags(4) *Record(8) *Address(16)
        //   NumberParameters(24) ExceptionInformation[15] at 32.
        // For access violations ExceptionInformation[0]=access type, [1]=addr.
        byte* record = stackalloc byte[152];
        for (int i = 0; i < 152; i++)
        {
            record[i] = 0;
        }
        *(uint*)(record + 0) = code;
        *(nint*)(record + 16) = (nint)rip; // ExceptionAddress
        if (code == WindowsFaultCodes.AccessViolation)
        {
            *(uint*)(record + 24) = 2; // NumberParameters
            // Access type is unknown from siginfo alone; report read (0). The
            // handlers that care re-derive write intent from the instruction.
            *(ulong*)(record + 32) = WindowsFaultCodes.AccessRead;
            *(ulong*)(record + 40) = faultAddr;
        }

        // EXCEPTION_POINTERS { EXCEPTION_RECORD*; CONTEXT*; }
        void** pointers = stackalloc void*[2];
        pointers[0] = record;
        pointers[1] = ctx;

        nint[] callbacks;
        lock (InstallLock)
        {
            callbacks = FirstChanceCallbacks.ToArray();
        }

        foreach (var cb in callbacks)
        {
            var fn = (delegate* unmanaged[Cdecl]<void*, int>)cb;
            int result = fn(pointers);
            if (result == -1)
            {
                // EXCEPTION_CONTINUE_EXECUTION: publish any register edits (the
                // lazy-commit path may rewrite RIP/RSP) back into the ucontext
                // and resume the faulting instruction.
                WriteGregFromCtx(gregs, ctx, REG_RAX, Win64ContextOffsets.Rax);
                WriteGregFromCtx(gregs, ctx, REG_RCX, Win64ContextOffsets.Rcx);
                WriteGregFromCtx(gregs, ctx, REG_RDX, Win64ContextOffsets.Rdx);
                WriteGregFromCtx(gregs, ctx, REG_RBX, Win64ContextOffsets.Rbx);
                WriteGregFromCtx(gregs, ctx, REG_RSP, Win64ContextOffsets.Rsp);
                WriteGregFromCtx(gregs, ctx, REG_RBP, Win64ContextOffsets.Rbp);
                WriteGregFromCtx(gregs, ctx, REG_RSI, Win64ContextOffsets.Rsi);
                WriteGregFromCtx(gregs, ctx, REG_RDI, Win64ContextOffsets.Rdi);
                WriteGregFromCtx(gregs, ctx, REG_R8, Win64ContextOffsets.R8);
                WriteGregFromCtx(gregs, ctx, REG_R9, Win64ContextOffsets.R9);
                WriteGregFromCtx(gregs, ctx, REG_R10, Win64ContextOffsets.R10);
                WriteGregFromCtx(gregs, ctx, REG_R11, Win64ContextOffsets.R11);
                WriteGregFromCtx(gregs, ctx, REG_R12, Win64ContextOffsets.R12);
                WriteGregFromCtx(gregs, ctx, REG_R13, Win64ContextOffsets.R13);
                WriteGregFromCtx(gregs, ctx, REG_R14, Win64ContextOffsets.R14);
                WriteGregFromCtx(gregs, ctx, REG_R15, Win64ContextOffsets.R15);
                WriteGregFromCtx(gregs, ctx, REG_RIP, Win64ContextOffsets.Rip);
                return;
            }
        }

        // No handler resumed: restore the default disposition and re-raise so the
        // process dies with the real fault (CONTINUE_SEARCH -> default action).
        RestoreDefault(signo);
    }

    private static void CopyGregToCtx(byte* gregs, byte* ctx, int gregIndex, int ctxOffset)
    {
        *(ulong*)(ctx + ctxOffset) = ReadGreg(gregs, gregIndex);
    }

    private static void WriteGregFromCtx(byte* gregs, byte* ctx, int gregIndex, int ctxOffset)
    {
        *(ulong*)(gregs + gregIndex * 8) = *(ulong*)(ctx + ctxOffset);
    }

    private static ulong ReadGreg(byte* gregs, int index)
    {
        return *(ulong*)(gregs + index * 8);
    }

    private static uint SignalToExceptionCode(int signo) => signo switch
    {
        SIGSEGV or SIGBUS => WindowsFaultCodes.AccessViolation,
        SIGILL => WindowsFaultCodes.IllegalInstruction,
        SIGTRAP => WindowsFaultCodes.Breakpoint,
        SIGFPE => 0xC0000094u, // STATUS_INTEGER_DIVIDE_BY_ZERO family
        _ => WindowsFaultCodes.AccessViolation,
    };

    private static void RestoreDefault(int signo)
    {
        const nint SIG_DFL = 0;
        var action = new SigAction { sa_handler = SIG_DFL, sa_flags = 0 };
        _ = sigaction(signo, &action, out _);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SigAltStack
    {
        public void* ss_sp;
        public int ss_flags;
        public nuint ss_size;
    }

    // glibc struct sigaction: handler, then a 128-byte sa_mask, flags, restorer.
    // Laid out explicitly so the mask padding is present and zeroed.
    [StructLayout(LayoutKind.Sequential)]
    private struct SigAction
    {
        public nint sa_handler;
        public SigSet sa_mask;
        public int sa_flags;
        public nint sa_restorer;
    }

    [StructLayout(LayoutKind.Sequential, Size = 128)]
    private struct SigSet
    {
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int sigaction(int signum, SigAction* act, out SigAction oldact);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int sigaltstack(SigAltStack* ss, SigAltStack* oldss);
}
