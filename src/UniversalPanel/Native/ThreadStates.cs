// Enum order mirrors System Informer's phnt/include/ntkeapi.h (MIT): KTHREAD_STATE, KWAIT_REASON.
// https://github.com/winsiderss/systeminformer @ c2f1489890f14de76b985c0142cbad154d92623d

namespace UniversalPanel.Native;

internal static class ThreadStates
{
    public const int Waiting = 5;
    public const int WrSuspendedReason = 5;   // "Suspended" (KWAIT_REASON 5)
    public const int WrSuspended = 12;        // "WrSuspended" (KWAIT_REASON 12)

    private static readonly string[] States =
    [
        "Initialized", "Ready", "Running", "Standby", "Terminated", "Waiting",
        "Transition", "DeferredReady", "GateWaitObsolete", "WaitingForProcessInSwap",
    ];

    private static readonly string[] WaitReasons =
    [
        "Executive", "FreePage", "PageIn", "PoolAllocation", "DelayExecution", "Suspended",
        "UserRequest", "WrExecutive", "WrFreePage", "WrPageIn", "WrPoolAllocation",
        "WrDelayExecution", "WrSuspended", "WrUserRequest", "WrEventPair", "WrQueue",
        "WrLpcReceive", "WrLpcReply", "WrVirtualMemory", "WrPageOut", "WrRendezvous",
        "WrKeyedEvent", "WrTerminated", "WrProcessInSwap", "WrCpuRateControl", "WrCalloutStack",
        "WrKernel", "WrResource", "WrPushLock", "WrMutex", "WrQuantumEnd", "WrDispatchInt",
        "WrPreempted", "WrYieldExecution", "WrFastMutex", "WrGuardedMutex", "WrRundown",
        "WrAlertByThreadId", "WrDeferredPreempt", "WrPhysicalFault", "WrIoRing", "WrMdlCache", "WrRcu",
    ];

    public static bool IsSuspended(int state, int waitReason) =>
        state == Waiting && waitReason is WrSuspendedReason or WrSuspended;

    /// <summary>"Running", or "Wait: UserRequest" style for waiting threads, like System Informer.</summary>
    public static string Describe(int state, int waitReason)
    {
        var name = (uint)state < States.Length ? States[state] : $"State {state}";
        if (state != Waiting)
        {
            return name;
        }

        var reason = (uint)waitReason < WaitReasons.Length ? WaitReasons[waitReason] : $"{waitReason}";
        return IsSuspended(state, waitReason) ? "Suspended" : $"Wait: {reason}";
    }
}
