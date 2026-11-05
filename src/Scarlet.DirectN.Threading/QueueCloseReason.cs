namespace Scarlet.DirectN.Threading;

/// <summary>
/// Why a <see cref="DirectNMessageQueue"/> stopped accepting work (diagnostics).
/// </summary>
internal enum QueueCloseReason
{
    None,
    Closed,
    OwnerExitedDuringSend,
    OwnerThreadExited,
    WindowGone,
}
