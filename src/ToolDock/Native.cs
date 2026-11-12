using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ToolDock;

internal sealed class KernelHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public KernelHandle() : base(true) { }
    internal KernelHandle(nint value) : base(true) => SetHandle(value);
    protected override bool ReleaseHandle() => Native.CloseHandle(handle);
}

internal static class Native
{
    internal const uint Suspended = 4, NoWindow = 0x08000000, Breakaway = 0x01000000;
    internal static void Check(bool success)
    {
        if (!success) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    internal static string Quote(string value)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            result.Append(c);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    internal static string FinalPath(string path)
    {
        using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new StringBuilder(32768);
        var length = GetFinalPathNameByHandleW(file, buffer, (uint)buffer.Capacity, 0);
        if (length == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (length >= buffer.Capacity) throw new IOException("Executable path is too long.");
        return buffer.ToString();
    }

    internal static KernelHandle CreateJob()
    {
        var job = CreateJobObjectW(0, null);
        if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 | 0x800 } };
            Check(SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()));
            return job;
        }
        catch { job.Dispose(); throw; }
    }

    internal static async Task EmptyJobAsync(KernelHandle job)
    {
        Check(TerminateJobObject(job, 1));
        var deadline = DateTime.UtcNow + Settings.StopTimeout;
        while (true)
        {
            Check(QueryInformationJobObject(job, 1, out var info, (uint)Marshal.SizeOf<Accounting>(), 0));
            if (info.ActiveProcesses == 0) return;
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Process group did not terminate in time.");
            await Task.Delay(20);
        }
    }

    internal static ChildProcess CreateProcess(string executable, IEnumerable<string> arguments,
        string? directory = null, IReadOnlyDictionary<string, string>? environment = null,
        KernelHandle? job = null, nint[]? streams = null, bool detached = false, bool breakaway = false)
    {
        using var attributes = new ProcessAttributes((job is null ? 0 : 1) + (streams is null ? 0 : 1));
        if (job is not null) attributes.Add(0x2000D, [job.DangerousGetHandle()]);
        if (streams is not null) attributes.Add(0x20002, streams);
        var startup = new StartupInfoEx();
        startup.Startup.Size = Marshal.SizeOf<StartupInfoEx>();
        startup.Attributes = attributes.Pointer;
        if (streams is not null)
        {
            startup.Startup.Flags = 0x100;
            startup.Startup.Input = streams[0];
            startup.Startup.Output = streams[1];
            startup.Startup.Error = streams[2];
        }
        uint flags = Suspended | NoWindow | 0x400 | 0x80000;
        if (breakaway || (detached && IsInJob(new nint(-1)))) flags |= Breakaway;
        var command = new StringBuilder(string.Join(' ', new[] { executable }.Concat(arguments).Select(Quote)));
        nint environmentBlock = 0;
        try
        {
            if (environment is not null)
                environmentBlock = Marshal.StringToHGlobalUni(string.Join('\0', environment.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => $"{x.Key}={x.Value}")) + "\0\0");
            var created = CreateProcessW(executable, command, 0, 0, streams is not null, flags,
                environmentBlock, directory, ref startup, out var process);
            // A host may allow silent breakaway but reject an explicit breakaway flag.
            // The suspended child must still pass the no-external-Job check below.
            if (!created && detached && (flags & Breakaway) != 0 && Marshal.GetLastWin32Error() == 5)
            {
                command = new StringBuilder(string.Join(' ', new[] { executable }.Concat(arguments).Select(Quote)));
                created = CreateProcessW(executable, command, 0, 0, false, flags & ~Breakaway,
                    environmentBlock, directory, ref startup, out process);
            }
            Check(created);
            var child = new ChildProcess(new KernelHandle(process.Process), new KernelHandle(process.Thread), process.ProcessId);
            try
            {
                if (detached && IsInJob(process.Process))
                    throw new InvalidOperationException("The launch environment prevents detaching the runner from its Job.");
                return child;
            }
            catch { child.Kill(); child.Dispose(); throw; }
        }
        finally { if (environmentBlock != 0) Marshal.FreeHGlobal(environmentBlock); }
    }

    private static bool IsInJob(nint process) { Check(IsProcessInJob(process, 0, out var result)); return result; }

    internal static KernelHandle CreateStopEvent(string name)
    {
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        Check(ConvertStringSecurityDescriptorToSecurityDescriptorW($"D:P(A;;GA;;;{sid})(A;;GA;;;SY)", 1, out var descriptor, 0));
        try
        {
            var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            var handle = CreateEventW(ref security, true, false, name);
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            return handle;
        }
        finally { LocalFree(descriptor); }
    }

    internal static KernelHandle? OpenStopEvent(string name)
    {
        var handle = OpenEventW(0x100002, false, name);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        if (error == 2) return null;
        throw new Win32Exception(error);
    }

    internal static bool Signaled(KernelHandle handle) => WaitForSingleObject(handle, 0) switch
    {
        0 => true, 258 => false, _ => throw new Win32Exception(Marshal.GetLastWin32Error())
    };

    [StructLayout(LayoutKind.Sequential)] internal struct SecurityAttributes { public int Length; public nint Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfo
    {
        public int Size; public nint Reserved, Desktop, Title;
        public uint X, Y, Width, Height, XChars, YChars, Fill, Flags;
        public ushort Show, ReservedSize; public nint ReservedPointer, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo Startup; public nint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public nint Process, Thread; public int ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    { public long ProcessTime, JobTime; public uint Flags; public nuint MinWorkingSet, MaxWorkingSet; public uint ActiveProcessLimit; public nuint Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits { public BasicLimits Basic; public IoCounters Io; public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [StructLayout(LayoutKind.Sequential)] private struct Accounting
    { public long UserTime, KernelTime, PeriodUserTime, PeriodKernelTime; public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses; }

    [DllImport("kernel32", SetLastError = true)] internal static extern bool CloseHandle(nint handle);
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint length, uint flags);
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern KernelHandle CreateJobObjectW(nint attributes, string? name);
    [DllImport("kernel32", SetLastError = true)] private static extern bool SetInformationJobObject(KernelHandle job, int kind, ref ExtendedLimits info, uint size);
    [DllImport("kernel32", SetLastError = true)] private static extern bool QueryInformationJobObject(KernelHandle job, int kind, out Accounting info, uint size, nint returned);
    [DllImport("kernel32", SetLastError = true)] private static extern bool TerminateJobObject(KernelHandle job, uint code);
    [DllImport("kernel32", SetLastError = true)] private static extern bool IsProcessInJob(nint process, nint job, [MarshalAs(UnmanagedType.Bool)] out bool result);
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessW(string app, StringBuilder command, nint processAttributes, nint threadAttributes, bool inherit, uint flags, nint environment, string? directory, ref StartupInfoEx startup, out ProcessInfo process);
    [DllImport("kernel32", SetLastError = true)] internal static extern uint ResumeThread(KernelHandle thread);
    [DllImport("kernel32", SetLastError = true)] internal static extern bool TerminateProcess(KernelHandle process, uint code);
    [DllImport("kernel32", SetLastError = true)] internal static extern bool GetExitCodeProcess(KernelHandle process, out uint code);
    [DllImport("kernel32", SetLastError = true)] internal static extern uint WaitForSingleObject(KernelHandle handle, uint milliseconds);
    [DllImport("kernel32", SetLastError = true)] internal static extern bool SetEvent(KernelHandle handle);
    [DllImport("kernel32", SetLastError = true)] internal static extern bool SetHandleInformation(nint handle, uint mask, uint flags);
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern KernelHandle CreateEventW(ref SecurityAttributes attributes, bool manualReset, bool initial, string name);
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern KernelHandle OpenEventW(uint access, bool inherit, string name);
    [DllImport("advapi32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out nint descriptor, nint length);
    [DllImport("kernel32")] private static extern nint LocalFree(nint memory);
    [DllImport("kernel32", SetLastError = true)] internal static extern bool InitializeProcThreadAttributeList(nint list, int count, uint flags, ref nuint size);
    [DllImport("kernel32", SetLastError = true)] internal static extern bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value, nuint size, nint previous, nint returned);
    [DllImport("kernel32")] internal static extern void DeleteProcThreadAttributeList(nint list);
}

internal sealed class ProcessAttributes : IDisposable
{
    internal nint Pointer { get; }
    private readonly List<nint> values = [];
    internal ProcessAttributes(int count)
    {
        if (count == 0) return;
        nuint size = 0;
        Native.InitializeProcThreadAttributeList(0, count, 0, ref size);
        Pointer = Marshal.AllocHGlobal(checked((int)size));
        try { Native.Check(Native.InitializeProcThreadAttributeList(Pointer, count, 0, ref size)); }
        catch { Marshal.FreeHGlobal(Pointer); throw; }
    }
    internal void Add(nuint attribute, nint[] handles)
    {
        var memory = Marshal.AllocHGlobal(handles.Length * nint.Size);
        values.Add(memory);
        Marshal.Copy(handles, 0, memory, handles.Length);
        Native.Check(Native.UpdateProcThreadAttribute(Pointer, 0, attribute, memory, (nuint)(handles.Length * nint.Size), 0, 0));
    }
    public void Dispose()
    {
        if (Pointer != 0) { Native.DeleteProcThreadAttributeList(Pointer); Marshal.FreeHGlobal(Pointer); }
        foreach (var value in values) Marshal.FreeHGlobal(value);
    }
}

internal sealed class ChildProcess(KernelHandle process, KernelHandle thread, int id) : IDisposable
{
    internal int Id => id;
    internal bool HasExited => Native.Signaled(process);
    internal uint ExitCode { get { Native.Check(Native.GetExitCodeProcess(process, out var code)); return code; } }
    internal void Resume() { if (Native.ResumeThread(thread) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    internal void Kill() { if (!HasExited) Native.Check(Native.TerminateProcess(process, 1)); }
    internal async Task WaitAsync(CancellationToken token = default)
    { while (!HasExited) await Task.Delay(20, token); }
    public void Dispose() { thread.Dispose(); process.Dispose(); }
}
