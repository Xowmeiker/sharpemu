// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;

namespace SharpEmu.HLE.Host.Linux;

/// <summary>
/// Linux implementation over mmap/mprotect/munmap. VirtualAlloc semantics are
/// mapped as: Reserve = PROT_NONE mapping with MAP_NORESERVE, Commit = mprotect
/// inside it, Free = munmap of the whole allocation (sizes are tracked per base
/// address because munmap, unlike VirtualFree, needs the length). Query parses
/// /proc/self/maps; it is diagnostics/fault-path only, never hot.
/// </summary>
internal sealed unsafe partial class LinuxHostMemory : IHostMemory
{
    private const int PROT_NONE = 0x0;
    private const int PROT_READ = 0x1;
    private const int PROT_WRITE = 0x2;
    private const int PROT_EXEC = 0x4;

    private const int MAP_PRIVATE = 0x02;
    private const int MAP_FIXED = 0x10;
    private const int MAP_ANONYMOUS = 0x20;
    private const int MAP_NORESERVE = 0x4000;
    private const int MAP_FIXED_NOREPLACE = 0x100000;

    private static readonly nint MapFailed = -1;

    // Base address -> reserved size, so Free(base) can munmap the full range.
    private readonly ConcurrentDictionary<ulong, ulong> _allocationSizes = new();

    public ulong Allocate(ulong desiredAddress, ulong size, HostPageProtection protection)
    {
        return Map(desiredAddress, size, ToNativeProtection(protection), 0);
    }

    public ulong Reserve(ulong desiredAddress, ulong size, HostPageProtection protection)
    {
        // A reservation must never consume commit charge; pages become usable
        // only after Commit (mprotect). PROT_NONE matches MEM_RESERVE faulting
        // behavior exactly.
        _ = protection;
        return Map(desiredAddress, size, PROT_NONE, MAP_NORESERVE);
    }

    public bool Commit(ulong address, ulong size, HostPageProtection protection)
    {
        return mprotect((void*)address, (nuint)size, ToNativeProtection(protection)) == 0;
    }

    public bool Free(ulong address)
    {
        if (!_allocationSizes.TryRemove(address, out var size))
        {
            return false;
        }

        return munmap((void*)address, (nuint)size) == 0;
    }

    public bool Protect(ulong address, ulong size, HostPageProtection protection, out uint rawOldProtection)
    {
        rawOldProtection = QueryRawProtection(address);
        return mprotect((void*)address, (nuint)size, ToNativeProtection(protection)) == 0;
    }

    public bool ProtectRaw(ulong address, ulong size, uint rawProtection, out uint rawOldProtection)
    {
        rawOldProtection = QueryRawProtection(address);
        return mprotect((void*)address, (nuint)size, (int)rawProtection) == 0;
    }

    public bool Query(ulong address, out HostRegionInfo info)
    {
        foreach (var line in File.ReadLines("/proc/self/maps"))
        {
            // "7f0000000000-7f0000021000 rw-p 00000000 00:00 0 ..."
            int dash = line.IndexOf('-');
            int space = line.IndexOf(' ', dash + 1);
            if (dash < 0 || space < 0)
            {
                continue;
            }

            ulong start = ulong.Parse(line.AsSpan(0, dash), NumberStyles.HexNumber);
            ulong end = ulong.Parse(line.AsSpan(dash + 1, space - dash - 1), NumberStyles.HexNumber);
            if (address < start)
            {
                // Maps are sorted: the address sits in an unmapped gap.
                info = new HostRegionInfo(address, 0, start - address, HostRegionState.Free, 0, HostPageProtection.NoAccess, 0, 0);
                return true;
            }

            if (address >= end)
            {
                continue;
            }

            var perms = line.AsSpan(space + 1, 4);
            int prot = (perms[0] == 'r' ? PROT_READ : 0) | (perms[1] == 'w' ? PROT_WRITE : 0) | (perms[2] == 'x' ? PROT_EXEC : 0);
            var state = prot == PROT_NONE ? HostRegionState.Reserved : HostRegionState.Committed;
            ulong allocationBase = _allocationSizes.ContainsKey(start) ? start : start;
            info = new HostRegionInfo(start, allocationBase, end - start, state, (uint)state, ToHostProtection(prot), (uint)prot, (uint)prot);
            return true;
        }

        info = new HostRegionInfo(address, 0, 0, HostRegionState.Free, 0, HostPageProtection.NoAccess, 0, 0);
        return true;
    }

    public void FlushInstructionCache(ulong address, ulong size)
    {
        // Self-modifying x86-64 needs no explicit flush; translation layers
        // (e.g. Box64) track writes to executable pages via protection, which
        // the emitters already toggle around every patch.
    }

    private ulong Map(ulong desiredAddress, ulong size, int protection, int extraFlags)
    {
        int flags = MAP_PRIVATE | MAP_ANONYMOUS | extraFlags;
        if (desiredAddress != 0)
        {
            // VirtualAlloc with a base address fails rather than relocating;
            // MAP_FIXED_NOREPLACE reproduces that (plain MAP_FIXED would clobber).
            flags |= MAP_FIXED_NOREPLACE;
        }

        var result = mmap((void*)desiredAddress, (nuint)size, protection, flags, -1, 0);
        if ((nint)result == MapFailed)
        {
            return 0;
        }

        if (desiredAddress != 0 && (ulong)result != desiredAddress)
        {
            // Pre-4.17 kernels ignore MAP_FIXED_NOREPLACE and may relocate.
            _ = munmap(result, (nuint)size);
            return 0;
        }

        _allocationSizes[(ulong)result] = size;
        return (ulong)result;
    }

    private uint QueryRawProtection(ulong address)
    {
        return Query(address, out var info) ? info.RawProtection : 0u;
    }

    private static int ToNativeProtection(HostPageProtection protection) => protection switch
    {
        HostPageProtection.NoAccess => PROT_NONE,
        HostPageProtection.ReadOnly => PROT_READ,
        HostPageProtection.ReadWrite => PROT_READ | PROT_WRITE,
        HostPageProtection.Execute => PROT_EXEC,
        HostPageProtection.ReadExecute => PROT_READ | PROT_EXEC,
        HostPageProtection.ReadWriteExecute => PROT_READ | PROT_WRITE | PROT_EXEC,
        HostPageProtection.ExecuteWriteCopy => PROT_READ | PROT_WRITE | PROT_EXEC,
        _ => throw new ArgumentOutOfRangeException(nameof(protection), protection, null),
    };

    private static HostPageProtection ToHostProtection(int prot) => prot switch
    {
        PROT_READ => HostPageProtection.ReadOnly,
        PROT_READ | PROT_WRITE => HostPageProtection.ReadWrite,
        PROT_EXEC => HostPageProtection.Execute,
        PROT_READ | PROT_EXEC => HostPageProtection.ReadExecute,
        PROT_READ | PROT_WRITE | PROT_EXEC => HostPageProtection.ReadWriteExecute,
        _ => HostPageProtection.NoAccess,
    };

    [LibraryImport("libc", SetLastError = true)]
    private static partial void* mmap(void* addr, nuint length, int prot, int flags, int fd, nint offset);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int munmap(void* addr, nuint length);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int mprotect(void* addr, nuint length, int prot);
}
