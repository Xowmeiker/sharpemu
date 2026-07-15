// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.Host.Linux;

internal sealed class LinuxHostPlatform : IHostPlatform
{
    public LinuxHostPlatform()
    {
        Memory = new LinuxHostMemory();
        Threading = new LinuxHostThreading();
        Symbols = new LinuxHostSymbolResolver(Memory);
    }

    public IHostMemory Memory { get; }

    public IHostThreading Threading { get; }

    public IHostSymbolResolver Symbols { get; }
}
