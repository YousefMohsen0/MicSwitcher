using System;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;

namespace MicSwitcher.App;

/// <summary>Returns memory to Windows when the app goes idle (tray only), which is what Task Manager shows.</summary>
public static class MemoryTrimmer
{
    public static void Trim()
    {
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            using Process process = Process.GetCurrentProcess();
            EmptyWorkingSet(process.Handle);
        }
        catch (Exception)
        {
            // Purely an optimisation.
        }
    }

    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr process);
}
