// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// Modified for Scarlet.Extensions.Hosting.DirectN.Threading
// (WPF DispatcherSynchronizationContext branch removed; always uses SingleThreadedSynchronizationContext.)

using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace Scarlet.Extensions.Hosting.DirectN.Threading.Tests.Joinable;

/// <summary>
/// A single-threaded synchronization context, akin to the DispatcherSynchronizationContext
/// and WindowsFormsSynchronizationContext.
/// </summary>
public static class SingleThreadedTestSynchronizationContext
{
    public interface IFrame
    {
#pragma warning disable CA1716 // Identifiers should not match keywords
        bool Continue { get; set; }
#pragma warning restore CA1716 // Identifiers should not match keywords
    }

    public static SynchronizationContext New()
    {
        return new SingleThreadedSynchronizationContext();
    }

    public static bool IsSingleThreadedSyncContext([NotNullWhen(true)] SynchronizationContext? context)
    {
        return context is SingleThreadedSynchronizationContext;
    }

    public static IFrame NewFrame()
    {
        return new OwnWrapperFrame();
    }

    public static void PushFrame(SynchronizationContext context, IFrame frame)
    {
        Requires.NotNull(context, nameof(context));
        Requires.NotNull(frame, nameof(frame));

        var ctxt = (SingleThreadedSynchronizationContext)context;
        ctxt.PushFrame(((OwnWrapperFrame)frame).Frame);
    }

    private class OwnWrapperFrame : IFrame
    {
        internal readonly SingleThreadedSynchronizationContext.Frame Frame = new SingleThreadedSynchronizationContext.Frame();

        public bool Continue
        {
            get { return this.Frame.Continue; }
            set { this.Frame.Continue = value; }
        }
    }
}
