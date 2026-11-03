using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

// Shared by the launcher and console power workers; closing the handle kills owned children.
sealed class OwnedJob : IDisposable {
    [StructLayout(LayoutKind.Sequential)] struct Basic {
        public long ProcessTime, JobTime;
        public uint Flags;
        public UIntPtr MinWorkingSet, MaxWorkingSet;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)] struct Io {
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
    }
    [StructLayout(LayoutKind.Sequential)] struct Extended {
        public Basic Basic;
        public Io Io;
        public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref Extended info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    readonly SafeFileHandle handle;
    public OwnedJob() {
        handle = CreateJobObject(IntPtr.Zero, null);
        var info = new Extended { Basic = new Basic { Flags = 0x2000 } };
        if (handle.IsInvalid || !SetInformationJobObject(handle, 9, ref info, (uint)Marshal.SizeOf<Extended>())) {
            var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error);
        }
    }
    public void Add(Process process) {
        if (!AssignProcessToJobObject(handle, process.Handle)) {
            var error = Marshal.GetLastWin32Error();
            if (!process.HasExited) process.Kill(true);
            throw new Win32Exception(error);
        }
    }
    public void Dispose() => handle.Dispose();
}
