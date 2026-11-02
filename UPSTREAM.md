# Upstream: microsoft/vs-threading

`Scarlet.Extensions.Hosting.DirectN.Threading` contains a trimmed, Native AOT compatible extract of
[Microsoft.VisualStudio.Threading](https://github.com/microsoft/vs-threading) (MIT, see `LICENSE-3RD-PARTY.txt`).
It is not affiliated with Microsoft.

| | |
|---|---|
| Repository | https://github.com/microsoft/vs-threading |
| Tag | `v17.12.19` |
| Commit | `2ac34d4ddb3f5defd767488e3d7072f8047de1ea` |
| Library code | `src/Scarlet.Extensions.Hosting.DirectN.Threading/Joinable/` |
| Ported tests | `tests/Scarlet.Extensions.Hosting.DirectN.Threading.Tests/Joinable/` |

## What is kept

The JoinableTask machinery and `AsyncLazy<T>`. Public types (names and members identical to upstream, so consumers only change
`using Microsoft.VisualStudio.Threading;` to `using Scarlet.Extensions.Hosting.DirectN.Threading;`):

- `JoinableTaskContext` (+ nested `RevertRelevance`, `HangDetails`)
- `JoinableTaskContextNode`
- `JoinableTaskFactory` (+ nested `MainThreadAwaitable`, `MainThreadAwaiter`)
- `JoinableTask`, `JoinableTask<T>`
- `JoinableTaskCollection` (+ nested `JoinRelease`)
- `JoinableTaskCreationOptions`
- `JoinableTaskContextException`
- `AsyncLazy<T>` (+ nested `RevertRelevance`)

Everything else is `internal`.

## What is removed

- Everything not reachable from the types above: `AsyncReaderWriterLock`, `AsyncReaderWriterResourceLock`, `ReentrantSemaphore`,
  `AsyncSemaphore`, `AsyncAutoResetEvent`, `AsyncCountdownEvent`, `AsyncBarrier`, `AsyncCrossProcessMutex`, `AsyncLazyInitializer`,
  `AsyncLocal<T>`, `DelegatingJoinableTaskFactory`, `JoinableTaskInternals`, `ProgressWithCompletion`, `NonConcurrentSynchronizationContext`,
  `SingleThreadedSynchronizationContext`, `CancellationTokenExtensions`, `EnumerateOneOrMany`, the VS `IAsyncDisposable`, etc.
- WPF / WindowsBase: `DispatcherExtensions` (and `UseWPF`), `net472` and `netstandard2.0` code paths.
- Hang report UI/diagnostics: `IHangReportContributor`, `HangReportContribution`, `JoinableTaskContext+HangReportContributor`
  (`IHangReportContributor.GetHangReport()` on `JoinableTaskContext`), `Dgml`, and the reflection-based async return stack walker
  (`InternalUtilities.GetAsyncReturnStackFrames`, `SingleExecuteProtector.DelegateLabel`/`WalkAsyncReturnStackFrames`).
  Hang *detection* (`JoinableTaskContext.OnHangDetected`, `JoinableTaskContextNode.OnHangDetected`/`RegisterOnHangDetected`) is kept.
- Registry / process helpers in `AwaitExtensions` (`WaitForChangeAsync`, `WaitForExitAsync`, `LightUps`, CsWin32 interop).
- `Microsoft.VisualStudio.Validation` package dependency, `Strings.resx` (and satellite assemblies), `PolySharp`/`Nullable`
  package helpers, CsWin32, PublicAPI analyzer files.
- The binary-serialization constructor of `JoinableTaskContextException` (SYSLIB0051) and its `[Serializable]` attribute.

## Files

Original paths are relative to the upstream repository root. All library files: namespace
`Microsoft.VisualStudio.Threading` → `Scarlet.Extensions.Hosting.DirectN.Threading`, UTF-8 BOM dropped, and a
`// Modified for Scarlet.Extensions.Hosting.DirectN.Threading` line added below the upstream license header.

### Library (`src/Scarlet.Extensions.Hosting.DirectN.Threading/Joinable/`)

| Local file | Upstream file (`src/Microsoft.VisualStudio.Threading/…`) | Local modifications beyond the namespace |
|---|---|---|
| ``AsyncLazy`1.cs`` | ``AsyncLazy`1.cs`` | `AsyncLocal<object?>` for nullable correctness; `DisposeValueAsync` drops the VS `IAsyncDisposable` branch (only `System.IAsyncDisposable`/`IDisposable`). |
| `AsyncManualResetEvent.cs` | same | `public` → `internal`. |
| ``AsyncQueue`1.cs`` | same | `public` → `internal`. |
| `AwaitExtensions.cs` | same | `internal`; only `GetAwaiter(TaskScheduler)`, `SwitchTo`, `ConfigureAwaitRunInline` and their awaitables kept; registry/process/WaitHandle/`SynchronizationContext` awaiters, `ConfigureAwait(YieldAwaitable)`, `ConfigureAwaitForAggregateException` removed; `#if NETFRAMEWORK` branches removed. |
| `EmptyStruct.cs` | same | — |
| `IJoinableTaskDependent.cs` | same | — |
| `InlineResumable.cs` | same | — |
| `JoinableTask.cs` | same | — |
| ``JoinableTask`1.cs`` | same | — |
| `JoinableTask+ExecutionQueue.cs` | same | — |
| `JoinableTask+JoinableTaskSynchronizationContext.cs` | same | — |
| `JoinableTaskCollection.cs` | same | — |
| `JoinableTaskContext.cs` | same | `AsyncLocal<WeakReference<JoinableTask>?>` for nullable correctness; `#if NET6_0_OR_GREATER` branch kept unconditionally. (The `HangReportContributor` partial is not copied.) |
| `JoinableTaskContextException.cs` | same | Removed obsolete serialization ctor and `[Serializable]`. |
| `JoinableTaskContextNode.cs` | same | — |
| `JoinableTaskCreationOptions.cs` | same | — |
| `JoinableTaskDependencyGraph.cs` | same | — |
| `JoinableTaskFactory.cs` | same | Removed `VerifyNoNonConcurrentSyncContext` (AsyncReaderWriterLock check) and its calls in `Run`; removed `SingleExecuteProtector.DelegateLabel`, `WalkAsyncReturnStackFrames` and the `[DebuggerDisplay]` that used them. |
| ``ListOfOftenOne`1.cs`` | same | — |
| `NoMessagePumpSyncContext.cs` | same | `internal`; CsWin32 `PInvoke.WaitForMultipleObjects` replaced by a `[LibraryImport]` of `kernel32!WaitForMultipleObjects`; non-Windows/`NETFRAMEWORK` branches removed. |
| `NullableHelpers.cs` | same | Unused `AsNullableArgFunc` removed. |
| ``RarelyRemoveItemSet`1.cs`` | same | — |
| `RoslynDebug.cs` | same | — |
| `SpecializedSyncContext.cs` | same | `public` → `internal`. |
| ``TaskCompletionSourceWithoutInlining`1.cs`` | same | — |
| `ThreadingEventSource.cs` | same | EventSource renamed to `Scarlet-Extensions-Hosting-DirectN-Threading`; AsyncReaderWriterLock events, `Tasks` and `Opcodes` removed; the two `(int, bool)` events use a `WriteEventCore` helper instead of the reflection-based `WriteEvent(int, params object[])` (+ `Boxed.cs` no longer needed). |
| `ThreadingTools.cs` | same | `internal`; `ApplyChangeOptimistically` removed. |
| `TplExtensions.cs` | same | `internal`; only `ApplyResultTo`, `Forget(Task)`, `CanceledTaskOfT` and their private helpers kept. |
| `Strings.cs` | `Strings.resx` | New: the used resource strings as English `const`s. |
| `Validation.cs` | — (`Microsoft.VisualStudio.Validation` package) | New: minimal internal re-implementation of `Requires`, `Verify`, `Assumes`, `Report` (only the members used). `Assumes` failures throw an internal `InternalErrorException`; `Report` is `[Conditional("DEBUG")]` as upstream. |

### Tests (`tests/Scarlet.Extensions.Hosting.DirectN.Threading.Tests/Joinable/`)

Upstream tests are in `test/Microsoft.VisualStudio.Threading.Tests/` and use xunit 2. Common changes: file-scoped namespace
`Scarlet.Extensions.Hosting.DirectN.Threading.Tests.Joinable`, xunit.v3 (`ITestOutputHelper` from `Xunit`, `[StaFact]`/`[SkippableFact]`
→ `[Fact]`, `Skip.If*` → `Assert.SkipWhen/SkipUnless`, `CombinatorialData`/`PairwiseData` → `InlineData`/`MemberData`),
overrides of `protected internal` members declared `protected internal override` (the test assembly is a friend assembly).

| Local file | Upstream file | Local modifications |
|---|---|---|
| `JoinableTaskTests.cs` | `test/…/JoinableTaskTests.cs` | Removed: `CallContextWasOverwrittenByReentrance` (AsyncReaderWriterLock), `SendToSyncContextCapturedFromWithinRunSynchronously` (AsyncCountdownEvent), `SwitchToMainThreadMemoryLeak`, `SwitchToMainThreadMemoryLeakWithCancellationToken`, `RunSynchronouslyTask[OfT]{No,With}YieldGCPressure` (out-of-process isolation + GC pressure measurements), `PrintActiveTasksReport` (hang report). |
| `JoinableTaskFactoryTests.cs` | `test/…/JoinableTaskFactoryTests.cs` | — |
| `JoinableTaskContextTests.cs` | `test/…/JoinableTaskContextTests.cs` | Removed the five `GetHangReport*` tests (hang report / DGML). |
| `JoinableTaskCollectionTests.cs` | `test/…/JoinableTaskCollectionTests.cs` | — |
| `AsyncLazyTests.cs` | `test/…/AsyncLazyTests.cs` | Removed `ValueFactoryRequiresReadLockHeldByOther` (AsyncReaderWriterLock) and the VS `IAsyncDisposable` dispose variant; local `NamedSyncContext` enum (upstream borrows it from `AwaitExtensionsTests`). |
| `TestBase.cs` | `test/…/TestBase.cs` | Removed GC pressure and out-of-process isolation helpers. |
| `JoinableTaskTestBase.cs` | `test/…/JoinableTaskTestBase.cs` | `GetPendingTasksCount` reads `JoinableTaskContext.pendingTasks` via reflection instead of parsing the DGML hang report. |
| `TestUtilities.cs` | `test/…/TestUtilities.cs` | Removed `ExecuteInIsolationAsync`; added `NoThrowAwaitable`/`WithTimeout` replacements for the non-extracted `TplExtensions` members. |
| `SingleThreadedTestSynchronizationContext.cs` | `test/…/SingleThreadedTestSynchronizationContext.cs` | WPF `DispatcherSynchronizationContext` branch removed. |
| `SingleThreadedSynchronizationContext.cs` | `src/Microsoft.VisualStudio.Threading/SingleThreadedSynchronizationContext.cs` | Test-only copy (not shipped); resource strings inlined. |
| `AssertEx.cs` | `test/…/AssertEx.cs` | Uses the non-obsolete xunit.v3 `EqualException.ForMismatchedValues` overload. |
| `GenericParameterHelper.cs` | `test/…/GenericParameterHelper.cs` | — |
| `.editorconfig` | — | Silences xUnit1030/xUnit1031/xUnit1051 for the ported tests. |

## Syncing with a newer upstream tag

The script below shows what changed upstream between the extracted tag and a newer one, for exactly the files we copied.
Review each hunk and port the relevant ones by hand (the local files are trimmed, so the upstream diff will not apply blindly).

```bash
#!/usr/bin/env bash
# usage: ./upstream-diff.sh v17.14.15   (run from the repository root, with git and diff on PATH)
set -euo pipefail
OLD=v17.12.19
NEW=${1:?new upstream tag}
WORK=$(mktemp -d)
for tag in "$OLD" "$NEW"; do
  git -c core.longpaths=true clone -q --depth 1 --branch "$tag" https://github.com/microsoft/vs-threading "$WORK/$tag"
done
SRC=src/Microsoft.VisualStudio.Threading
TST=test/Microsoft.VisualStudio.Threading.Tests
FILES=(
  "$SRC/AsyncLazy\`1.cs" "$SRC/AsyncManualResetEvent.cs" "$SRC/AsyncQueue\`1.cs" "$SRC/AwaitExtensions.cs"
  "$SRC/EmptyStruct.cs" "$SRC/IJoinableTaskDependent.cs" "$SRC/InlineResumable.cs" "$SRC/JoinableTask.cs"
  "$SRC/JoinableTask\`1.cs" "$SRC/JoinableTask+ExecutionQueue.cs" "$SRC/JoinableTask+JoinableTaskSynchronizationContext.cs"
  "$SRC/JoinableTaskCollection.cs" "$SRC/JoinableTaskContext.cs" "$SRC/JoinableTaskContextException.cs"
  "$SRC/JoinableTaskContextNode.cs" "$SRC/JoinableTaskCreationOptions.cs" "$SRC/JoinableTaskDependencyGraph.cs"
  "$SRC/JoinableTaskFactory.cs" "$SRC/ListOfOftenOne\`1.cs" "$SRC/NoMessagePumpSyncContext.cs" "$SRC/NullableHelpers.cs"
  "$SRC/RarelyRemoveItemSet\`1.cs" "$SRC/RoslynDebug.cs" "$SRC/SpecializedSyncContext.cs"
  "$SRC/TaskCompletionSourceWithoutInlining\`1.cs" "$SRC/ThreadingEventSource.cs" "$SRC/ThreadingTools.cs"
  "$SRC/TplExtensions.cs" "$SRC/Strings.resx" "$SRC/SingleThreadedSynchronizationContext.cs"
  "$TST/JoinableTaskTests.cs" "$TST/JoinableTaskFactoryTests.cs" "$TST/JoinableTaskContextTests.cs"
  "$TST/JoinableTaskCollectionTests.cs" "$TST/AsyncLazyTests.cs" "$TST/TestBase.cs" "$TST/JoinableTaskTestBase.cs"
  "$TST/TestUtilities.cs" "$TST/SingleThreadedTestSynchronizationContext.cs" "$TST/AssertEx.cs" "$TST/GenericParameterHelper.cs"
)
for f in "${FILES[@]}"; do
  diff -u --label "$OLD/$f" --label "$NEW/$f" "$WORK/$OLD/$f" "$WORK/$NEW/$f" || true
done > upstream-changes.diff
# Also list files that are new upstream and might now be needed (e.g. newly split partial classes):
diff <(cd "$WORK/$OLD/$SRC" && ls) <(cd "$WORK/$NEW/$SRC" && ls) || true
echo "Wrote upstream-changes.diff"
```

To compare a local file directly against an upstream file (normalising the namespace first):

```bash
f='JoinableTaskFactory.cs'
diff -u <(sed '1s/^\xEF\xBB\xBF//; s/Microsoft\.VisualStudio\.Threading/Scarlet.Extensions.Hosting.DirectN.Threading/g' "$WORK/$NEW/src/Microsoft.VisualStudio.Threading/$f" | tr -d '\r') \
        <(tr -d '\r' < "src/Scarlet.Extensions.Hosting.DirectN.Threading/Joinable/$f")
```
