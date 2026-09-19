using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ToolDock.Starter.Jobs;

internal sealed class JobObject : IDisposable
{
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private readonly SafeJobHandle _handle;

    public JobObject()
    {
        _handle = NativeMethods.CreateJobObject(IntPtr.Zero, null);
        if (_handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject failed.");
        }

        var limits = new NativeMethods.JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new NativeMethods.JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose
            }
        };

        if (!NativeMethods.SetInformationJobObject(
                _handle,
                NativeMethods.JobObjectInfoType.ExtendedLimitInformation,
                ref limits,
                Marshal.SizeOf<NativeMethods.JobObjectExtendedLimitInformation>()))
        {
            var error = Marshal.GetLastWin32Error();
            _handle.Dispose();
            throw new Win32Exception(error, "SetInformationJobObject failed.");
        }
    }

    public void Assign(IntPtr processHandle)
    {
        if (!NativeMethods.AssignProcessToJobObject(_handle, processHandle))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "AssignProcessToJobObject failed.");
        }
    }

    public void Dispose() => _handle.Dispose();

    private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeJobHandle() : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
    }

    private static class NativeMethods
    {
        internal enum JobObjectInfoType
        {
            ExtendedLimitInformation = 9
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct JobObjectBasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public nuint MinimumWorkingSetSize;
            public nuint MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public nuint Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct JobObjectExtendedLimitInformation
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public nuint ProcessMemoryLimit;
            public nuint JobMemoryLimit;
            public nuint PeakProcessMemoryUsed;
            public nuint PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeJobHandle CreateJobObject(IntPtr jobAttributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetInformationJobObject(
            SafeJobHandle job,
            JobObjectInfoType infoType,
            ref JobObjectExtendedLimitInformation information,
            int informationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AssignProcessToJobObject(SafeJobHandle job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);
    }
}
