// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SharpEmu.HLE.Host.Linux;

/// <summary>
/// Supplies the "runtime function" addresses the execution engine bakes into
/// emitted stubs. The stubs call these with the Win64 convention (first args in
/// rcx/rdx), so each resolved address is a small emitted bridge thunk that
/// shuffles rcx->rdi, rdx->rsi and tail-jumps to a managed SysV implementation.
/// The implementations reproduce the Win32 semantics the stubs expect
/// (WaitForSingleObject returns 0 on signal; SetEvent/ExitThread act on the
/// eventfd/pthread primitives from <see cref="LinuxHostThreading"/>).
/// </summary>
internal sealed unsafe partial class LinuxHostSymbolResolver : IHostSymbolResolver
{
    private readonly IHostMemory _memory;
    private readonly Dictionary<HostRuntimeFunction, nint> _thunks = new();

    public LinuxHostSymbolResolver(IHostMemory memory)
    {
        _memory = memory;
    }

    public nint GetAddress(HostRuntimeFunction function)
    {
        if (_thunks.TryGetValue(function, out var existing))
        {
            return existing;
        }

        nint target = function switch
        {
            HostRuntimeFunction.TlsGetValue => (nint)(delegate* unmanaged<nint, nint>)&TlsGetValueImpl,
            HostRuntimeFunction.QueryPerformanceCounter => (nint)(delegate* unmanaged<ulong*, int>)&QueryPerformanceCounterImpl,
            HostRuntimeFunction.SwitchToThread => (nint)(delegate* unmanaged<int>)&SwitchToThreadImpl,
            HostRuntimeFunction.Sleep => (nint)(delegate* unmanaged<uint, void>)&SleepImpl,
            HostRuntimeFunction.WaitForSingleObject => (nint)(delegate* unmanaged<nint, uint, uint>)&WaitForSingleObjectImpl,
            HostRuntimeFunction.SetEvent => (nint)(delegate* unmanaged<nint, int>)&SetEventImpl,
            HostRuntimeFunction.ExitThread => (nint)(delegate* unmanaged<uint, void>)&ExitThreadImpl,
            _ => throw new ArgumentOutOfRangeException(nameof(function), function, null),
        };

        nint thunk = EmitWin64ToSysVBridge(target);
        _thunks[function] = thunk;
        return thunk;
    }

    /// <summary>
    /// mov rdi, rcx ; mov rsi, rdx ; movabs rax, target ; jmp rax.
    /// A tail jump (not call) so the managed function returns straight to the
    /// Win64 caller with rax carrying the result, and the entry RSP alignment
    /// the SysV callee expects is exactly the one the Win64 caller produced.
    /// </summary>
    private nint EmitWin64ToSysVBridge(nint target)
    {
        const uint size = 32u;
        byte* code = (byte*)_memory.Allocate(0, size, HostPageProtection.ReadWriteExecute);
        if (code == null)
        {
            return 0;
        }

        int o = 0;
        code[o++] = 0x48; code[o++] = 0x89; code[o++] = 0xCF; // mov rdi, rcx
        code[o++] = 0x48; code[o++] = 0x89; code[o++] = 0xD6; // mov rsi, rdx
        code[o++] = 0x48; code[o++] = 0xB8;                   // movabs rax, target
        *(nint*)(code + o) = target; o += sizeof(nint);
        code[o++] = 0xFF; code[o++] = 0xE0;                   // jmp rax

        if (!_memory.Protect((ulong)code, size, HostPageProtection.ReadExecute, out _))
        {
            _ = _memory.Free((ulong)code);
            return 0;
        }
        _memory.FlushInstructionCache((ulong)code, size);
        return (nint)code;
    }

    [UnmanagedCallersOnly]
    private static nint TlsGetValueImpl(nint key)
    {
        return pthread_getspecific((uint)key);
    }

    [UnmanagedCallersOnly]
    private static int QueryPerformanceCounterImpl(ulong* lpCounter)
    {
        // The stubs use QPC purely as a monotonically increasing tick source;
        // nanoseconds since an arbitrary epoch satisfies that. QPF is never
        // consulted by the emitted code, so the unit only needs monotonicity.
        if (clock_gettime(1 /* CLOCK_MONOTONIC */, out var ts) != 0)
        {
            *lpCounter = 0;
            return 0;
        }

        *lpCounter = (ulong)ts.Sec * 1_000_000_000UL + (ulong)ts.Nsec;
        return 1;
    }

    [UnmanagedCallersOnly]
    private static int SwitchToThreadImpl()
    {
        return sched_yield();
    }

    [UnmanagedCallersOnly]
    private static void SleepImpl(uint milliseconds)
    {
        var req = new Timespec
        {
            Sec = milliseconds / 1000,
            Nsec = (long)(milliseconds % 1000u) * 1_000_000L,
        };
        _ = nanosleep(&req, null);
    }

    [UnmanagedCallersOnly]
    private static uint WaitForSingleObjectImpl(nint eventHandle, uint timeoutMilliseconds)
    {
        int fd = (int)eventHandle;
        if (timeoutMilliseconds != uint.MaxValue)
        {
            var pfd = new PollFd { Fd = fd, Events = 0x0001 };
            int pr = poll(&pfd, 1, (int)timeoutMilliseconds);
            if (pr <= 0)
            {
                return 0x00000102u; // WAIT_TIMEOUT
            }
        }

        ulong value;
        long n = read(fd, &value, 8);
        return n == 8 ? 0u /* WAIT_OBJECT_0 */ : 0xFFFFFFFFu /* WAIT_FAILED */;
    }

    [UnmanagedCallersOnly]
    private static int SetEventImpl(nint eventHandle)
    {
        ulong one = 1;
        return write((int)eventHandle, &one, 8) == 8 ? 1 : 0;
    }

    [UnmanagedCallersOnly]
    private static void ExitThreadImpl(uint exitCode)
    {
        pthread_exit((nint)exitCode);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec
    {
        public long Sec;
        public long Nsec;
    }

    [LibraryImport("libc")]
    private static partial nint pthread_getspecific(uint key);

    [LibraryImport("libc")]
    private static partial int clock_gettime(int clockId, out Timespec tp);

    [LibraryImport("libc")]
    private static partial int sched_yield();

    [LibraryImport("libc")]
    private static partial int nanosleep(Timespec* req, Timespec* rem);

    [LibraryImport("libc")]
    private static partial long read(int fd, void* buf, nuint count);

    [LibraryImport("libc")]
    private static partial long write(int fd, void* buf, nuint count);

    [LibraryImport("libc")]
    private static partial int poll(PollFd* fds, nuint nfds, int timeout);

    [LibraryImport("libc")]
    private static partial void pthread_exit(nint retval);
}
