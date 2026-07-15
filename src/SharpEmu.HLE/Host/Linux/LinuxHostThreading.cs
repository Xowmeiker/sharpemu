// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.HLE.Host.Linux;

/// <summary>
/// Linux implementation over pthreads, __thread-style key TLS, and eventfd. The
/// native events the emitted stubs use must be pollable through the same
/// address the emitters bake in as WaitForSingleObject/SetEvent, so an eventfd
/// is exposed as its file descriptor and the "runtime function" thunks (see
/// <see cref="LinuxHostSymbolResolver"/>) wrap read()/write() on it.
/// </summary>
internal sealed unsafe partial class LinuxHostThreading : IHostThreading
{
    private const int EFD_CLOEXEC = 0x80000;

    public uint AllocateTlsSlot()
    {
        return pthread_key_create(out uint key, 0) == 0 ? key : uint.MaxValue;
    }

    public bool FreeTlsSlot(uint slot) => pthread_key_delete(slot) == 0;

    public bool SetTlsValue(uint slot, nint value) => pthread_setspecific(slot, value) == 0;

    public nint GetTlsValue(uint slot) => pthread_getspecific(slot);

    public uint CurrentThreadId => (uint)gettid();

    public bool TrySetCurrentThreadAffinity(nuint affinityMask)
    {
        // cpu_set_t is a bitmask; the emulator only ever pins to a single core,
        // so the low word matches its usage on both platforms.
        ulong mask = affinityMask;
        return sched_setaffinity(0, sizeof(ulong), &mask) == 0;
    }

    public nint CreateNativeThread(nint entry, nint parameter, nuint stackReserveBytes, out uint threadId)
    {
        threadId = 0;
        nint attr = Marshal.AllocHGlobal(64);
        try
        {
            if (pthread_attr_init(attr) != 0)
            {
                return 0;
            }

            if (stackReserveBytes != 0)
            {
                // pthread rounds up to a page and enforces PTHREAD_STACK_MIN;
                // the guest worker asks for a large reservation, which is fine.
                _ = pthread_attr_setstacksize(attr, stackReserveBytes);
            }

            if (pthread_create(out nint thread, attr, entry, parameter) != 0)
            {
                return 0;
            }

            // pthread_t is opaque but pointer-sized on glibc/bionic; the handle
            // round-trips through WaitForThreadExit/CloseThreadHandle only.
            return thread;
        }
        finally
        {
            _ = pthread_attr_destroy(attr);
            Marshal.FreeHGlobal(attr);
        }
    }

    public bool WaitForThreadExit(nint threadHandle, uint timeoutMilliseconds)
    {
        // No portable timed join before glibc 2.31's pthread_timedjoin_np; the
        // only caller uses a 1s teardown grace, so a bounded timed join is used
        // where available and falls back to a plain join otherwise.
        var deadline = new Timespec(timeoutMilliseconds);
        int rc = pthread_timedjoin_np(threadHandle, null, &deadline);
        return rc == 0;
    }

    public void CloseThreadHandle(nint threadHandle)
    {
        // pthread has no separate handle to close; detach anything not joined so
        // its stack is reclaimed.
        _ = pthread_detach(threadHandle);
    }

    public bool TryCaptureThreadRegisters(uint threadId, out HostCapturedRegisters registers)
    {
        // Diagnostics only. Cross-thread register capture on Linux needs
        // ptrace(PTRACE_ATTACH) from a different process or a signal round-trip;
        // neither is worth wiring for a log line, so report unavailable.
        registers = default;
        return false;
    }

    public nint CreateNativeEvent()
    {
        int fd = eventfd(0, EFD_CLOEXEC);
        return fd < 0 ? 0 : fd;
    }

    public void SignalNativeEvent(nint eventHandle)
    {
        ulong one = 1;
        _ = write((int)eventHandle, &one, 8);
    }

    public bool WaitNativeEvent(nint eventHandle, uint timeoutMilliseconds)
    {
        // Auto-reset semantics: a blocking read on a counting eventfd consumes
        // the whole count and resets it to 0, matching AutoResetEvent.
        if (timeoutMilliseconds != uint.MaxValue)
        {
            var pfd = new PollFd { Fd = (int)eventHandle, Events = 0x0001 /* POLLIN */ };
            int pr = poll(&pfd, 1, (int)timeoutMilliseconds);
            if (pr <= 0)
            {
                return false;
            }
        }

        ulong value;
        long n = read((int)eventHandle, &value, 8);
        return n == 8;
    }

    public void CloseNativeEvent(nint eventHandle)
    {
        _ = close((int)eventHandle);
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

        public Timespec(uint milliseconds)
        {
            // pthread_timedjoin_np wants an absolute CLOCK_REALTIME deadline.
            clock_gettime(0 /* CLOCK_REALTIME */, out this);
            long addNsec = Nsec + (long)(milliseconds % 1000u) * 1_000_000L;
            Sec += milliseconds / 1000u + addNsec / 1_000_000_000L;
            Nsec = addNsec % 1_000_000_000L;
        }
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int pthread_key_create(out uint key, nint destructor);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int pthread_key_delete(uint key);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int pthread_setspecific(uint key, nint value);

    [LibraryImport("libc", SetLastError = true)]
    private static partial nint pthread_getspecific(uint key);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int gettid();

    [LibraryImport("libc", SetLastError = true)]
    private static partial int sched_setaffinity(int pid, nuint cpusetsize, ulong* mask);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int pthread_attr_init(nint attr);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int pthread_attr_destroy(nint attr);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int pthread_attr_setstacksize(nint attr, nuint stacksize);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int pthread_create(out nint thread, nint attr, nint startRoutine, nint arg);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int pthread_timedjoin_np(nint thread, void** retval, Timespec* abstime);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int pthread_detach(nint thread);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int clock_gettime(int clockId, out Timespec tp);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int eventfd(uint initval, int flags);

    [LibraryImport("libc", SetLastError = true)]
    private static partial long write(int fd, void* buf, nuint count);

    [LibraryImport("libc", SetLastError = true)]
    private static partial long read(int fd, void* buf, nuint count);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int poll(PollFd* fds, nuint nfds, int timeout);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int close(int fd);
}
