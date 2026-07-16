// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;

namespace SharpEmu.Libs;

/// <summary>
/// Wires the execution backend's HLE seams to their kernel-library
/// implementations when this assembly loads. Static HLE export classes cannot
/// receive constructor injection, and the backend (SharpEmu.Core) cannot
/// reference this assembly, so the hooks live on
/// <see cref="GuestThreadExecution"/> and are populated here.
/// </summary>
internal static class LibsModuleInitializer
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        GuestThreadExecution.CurrentThreadHandleProvider = KernelPthreadState.GetCurrentThreadHandle;
        GuestThreadExecution.ExceptionHandlerResolver = KernelExceptionCompatExports.GetInstalledHandler;
        GuestThreadExecution.ThreadStackRegistrar = KernelPthreadExtendedCompatExports.RegisterThreadStack;
    }
}
