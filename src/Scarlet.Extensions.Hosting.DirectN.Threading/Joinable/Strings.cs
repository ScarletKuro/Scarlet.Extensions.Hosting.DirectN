// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// Modified for Scarlet.Extensions.Hosting.DirectN.Threading
// Replaces the upstream Strings.resx / Strings.Designer.cs with English constants (no satellite assemblies).

namespace Scarlet.Extensions.Hosting.DirectN.Threading;

/// <summary>
/// The upstream resource strings that are still used, as English constants.
/// </summary>
internal static class Strings
{
    internal const string InvalidAfterCompleted = "Already transitioned to the Completed state.";

    internal const string JoinableTaskContextAndCollectionMismatch = "JoinableTask does not belong to the context this collection was instantiated with.";

    internal const string JoinableTaskContextNodeAlreadyRegistered = "This node already registered.";

    internal const string LazyValueFaulted = "Lazily created value faulted during construction.";

    internal const string LazyValueNotCreated = "Lazily created value not yet constructed.";

    internal const string QueueEmpty = "The queue is empty.";

    internal const string SwitchToMainThreadFailedToReachExpectedThread = "An attempt to switch to the main thread failed to reach the expected thread. Was the JoinableTaskContext initialized on the wrong thread or with a SynchronizationContext whose Post method does not execute its delegate on the main thread?";

    internal const string SyncContextNotSet = "No SynchronizationContext to reach the main thread has been set.";

    internal const string ValueFactoryReentrancy = "The value factory has called for the value on the same instance.";
}
