// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// Modified for Scarlet.Extensions.Hosting.DirectN.Threading

using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Scarlet.Extensions.Hosting.DirectN.Threading;

/// <summary>
/// A SynchronizationContext whose synchronously blocking Wait method does not allow
/// any reentrancy via the message pump.
/// </summary>
internal partial class NoMessagePumpSyncContext : SynchronizationContext
{
    /// <summary>
    /// A shared singleton.
    /// </summary>
    private static readonly SynchronizationContext DefaultInstance = new NoMessagePumpSyncContext();

    /// <summary>
    /// Initializes a new instance of the <see cref="NoMessagePumpSyncContext"/> class.
    /// </summary>
    public NoMessagePumpSyncContext()
    {
        // This is required so that our override of Wait is invoked.
        this.SetWaitNotificationRequired();
    }

    /// <summary>
    /// Gets a shared instance of this class.
    /// </summary>
    public static SynchronizationContext Default
    {
        get { return DefaultInstance; }
    }

    /// <summary>
    /// Synchronously blocks without a message pump.
    /// </summary>
    /// <param name="waitHandles">An array of type <see cref="IntPtr" /> that contains the native operating system handles.</param>
    /// <param name="waitAll">true to wait for all handles; false to wait for any handle.</param>
    /// <param name="millisecondsTimeout">The number of milliseconds to wait, or <see cref="Timeout.Infinite" /> (-1) to wait indefinitely.</param>
    /// <returns>
    /// The array index of the object that satisfied the wait.
    /// </returns>
    public override int Wait(IntPtr[] waitHandles, bool waitAll, int millisecondsTimeout)
    {
        Requires.NotNull(waitHandles, nameof(waitHandles));

        // We must take special care to NOT end up in a call to CoWait (which lets in RPC calls and pumps messages on STA threads).
        // Calling WaitForMultipleObjects directly guarantees a non-pumping wait.
        return (int)WaitForMultipleObjects((uint)waitHandles.Length, waitHandles, waitAll ? 1 : 0, unchecked((uint)millisecondsTimeout));
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint WaitForMultipleObjects(uint nCount, [In] IntPtr[] lpHandles, int bWaitAll, uint dwMilliseconds);
}
