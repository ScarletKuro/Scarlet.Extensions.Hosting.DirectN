// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// Modified for Scarlet.Extensions.Hosting.DirectN.Threading

using System;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;

namespace Scarlet.Extensions.Hosting.DirectN.Threading;

/// <summary>
/// The ETW source for logging events for this library.
/// </summary>
/// <remarks>
/// We use a fully-descriptive type name because the type name becomes the name
/// of the ETW Provider.
/// </remarks>
[EventSource(Name = "Scarlet-Extensions-Hosting-DirectN-Threading")]
internal sealed partial class ThreadingEventSource : EventSource
{
    /// <summary>
    /// The singleton instance used for logging.
    /// </summary>
    internal static readonly ThreadingEventSource Instance = new ThreadingEventSource();

    /// <summary>
    /// The event ID for the <see cref="CompleteOnCurrentThreadStart(int, bool)"/>.
    /// </summary>
    private const int CompleteOnCurrentThreadStartEvent = 11;

    /// <summary>
    /// The event ID for the <see cref="CompleteOnCurrentThreadStop(int)"/>.
    /// </summary>
    private const int CompleteOnCurrentThreadStopEvent = 12;

    /// <summary>
    /// The event ID for the <see cref="WaitSynchronouslyStart()"/>.
    /// </summary>
    private const int WaitSynchronouslyStartEvent = 13;

    /// <summary>
    /// The event ID for the <see cref="WaitSynchronouslyStop()"/>.
    /// </summary>
    private const int WaitSynchronouslyStopEvent = 14;

    /// <summary>
    /// The event ID for the <see cref="PostExecutionStart(int, bool)"/>.
    /// </summary>
    private const int PostExecutionStartEvent = 15;

    /// <summary>
    /// The event ID for the <see cref="PostExecutionStop(int)"/>.
    /// </summary>
    private const int PostExecutionStopEvent = 16;

    /// <summary>
    /// The event ID for the <see cref="CircularJoinableTaskDependencyDetected(int, int)"/>.
    /// </summary>
    private const int CircularJoinableTaskDependencyDetectedEvent = 17;

    /// <summary>
    /// Enters a synchronously task.
    /// </summary>
    /// <param name="taskId">Hash code of the task.</param>
    /// <param name="isOnMainThread">Whether the task is on the main thread.</param>
    [Event(CompleteOnCurrentThreadStartEvent)]
    public void CompleteOnCurrentThreadStart(int taskId, bool isOnMainThread)
    {
        this.WriteEventIntBool(CompleteOnCurrentThreadStartEvent, taskId, isOnMainThread);
    }

    /// <summary>
    /// Exits a synchronously task.
    /// </summary>
    /// <param name="taskId">Hash code of the task.</param>
    [Event(CompleteOnCurrentThreadStopEvent)]
    public void CompleteOnCurrentThreadStop(int taskId)
    {
        this.WriteEvent(CompleteOnCurrentThreadStopEvent, taskId);
    }

    /// <summary>
    /// The current thread starts to wait on execution requests.
    /// </summary>
    [Event(WaitSynchronouslyStartEvent, Level = EventLevel.Verbose)]
    public void WaitSynchronouslyStart()
    {
        this.WriteEvent(WaitSynchronouslyStartEvent);
    }

    /// <summary>
    /// The current thread gets an execution request.
    /// </summary>
    [Event(WaitSynchronouslyStopEvent, Level = EventLevel.Verbose)]
    public void WaitSynchronouslyStop()
    {
        this.WriteEvent(WaitSynchronouslyStopEvent);
    }

    /// <summary>
    /// Post a execution request to the queue.
    /// </summary>
    /// <param name="requestId">The request id.</param>
    /// <param name="mainThreadAffinitized">The execution need happen on the main thread.</param>
    [Event(PostExecutionStartEvent, Level = EventLevel.Verbose)]
    public void PostExecutionStart(int requestId, bool mainThreadAffinitized)
    {
        this.WriteEventIntBool(PostExecutionStartEvent, requestId, mainThreadAffinitized);
    }

    /// <summary>
    /// An execution request is processed.
    /// </summary>
    /// <param name="requestId">The request id.</param>
    [Event(PostExecutionStopEvent, Level = EventLevel.Verbose)]
    public void PostExecutionStop(int requestId)
    {
        this.WriteEvent(PostExecutionStopEvent, requestId);
    }

    /// <summary>
    /// Circular JoinableTask dependency detected.
    /// </summary>
    /// <param name="initUnreachableCount">Initial count of unreachable nodes.</param>
    /// <param name="reachableCount">The size of the connected dependency graph.</param>
    [Event(CircularJoinableTaskDependencyDetectedEvent, Level = EventLevel.Informational)]
    public void CircularJoinableTaskDependencyDetected(int initUnreachableCount, int reachableCount)
    {
        this.WriteEvent(CircularJoinableTaskDependencyDetectedEvent, initUnreachableCount, reachableCount);
    }

    /// <summary>
    /// Writes an event with an <see cref="int"/> and a <see cref="bool"/> payload without going through the
    /// reflection-based <c>WriteEvent(int, params object[])</c> overload, which is not trim/AOT safe.
    /// </summary>
    /// <param name="eventId">The event ID.</param>
    /// <param name="arg1">The first payload value.</param>
    /// <param name="arg2">The second payload value (serialized as a 4-byte Win32 BOOL, matching the EventSource manifest).</param>
    [NonEvent]
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", Justification = "Diagnostics only. The payload consists solely of primitive int values, which EventSource serializes without reflection over trimmed members.")]
    private unsafe void WriteEventIntBool(int eventId, int arg1, bool arg2)
    {
        int arg2AsInt = arg2 ? 1 : 0;
        EventData* data = stackalloc EventData[2];
        data[0] = default;
        data[0].DataPointer = (IntPtr)(&arg1);
        data[0].Size = sizeof(int);
        data[1] = default;
        data[1].DataPointer = (IntPtr)(&arg2AsInt);
        data[1].Size = sizeof(int);
        this.WriteEventCore(eventId, 2, data);
    }
}
