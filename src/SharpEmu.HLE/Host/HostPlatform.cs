// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE.Host.Linux;
using SharpEmu.HLE.Host.Windows;

namespace SharpEmu.HLE.Host;

/// <summary>
/// Process-wide access point for the host platform backend. Static HLE export
/// classes (which cannot receive constructor injection) resolve host primitives
/// through <see cref="Current"/>; injectable components should instead accept an
/// <see cref="IHostPlatform"/> and merely default to this.
/// </summary>
public static class HostPlatform
{
    private static readonly Lazy<IHostPlatform> Instance = new(Create);

    public static IHostPlatform Current => Instance.Value;

    private static IHostPlatform Create()
    {
        // Guest x86-64 executes natively and the emitted stubs are x86-64, so the
        // host process must itself be x86-64 (either a real x86-64 CPU or an
        // x86-64 process running under a translator such as Box64, which presents
        // x86-64 register/signal state). A native ARM64 process cannot run the
        // emitted stubs and is rejected rather than crashing undefined later.
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            throw new PlatformNotSupportedException(
                "SharpEmu native guest execution requires an x86-64 host process. On ARM64, run the linux-x64 build under an x86-64 translation layer (e.g. Box64).");
        }

        if (OperatingSystem.IsWindows())
        {
            return new WindowsHostPlatform();
        }

        if (OperatingSystem.IsLinux())
        {
            return new LinuxHostPlatform();
        }

        throw new PlatformNotSupportedException(
            "SharpEmu native guest execution has no host platform backend for this OS yet (Windows and Linux x64 supported).");
    }
}
