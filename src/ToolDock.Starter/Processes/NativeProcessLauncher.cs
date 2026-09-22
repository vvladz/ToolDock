using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using ToolDock.Starter.Jobs;

namespace ToolDock.Starter.Processes;

internal static class NativeProcessLauncher
{
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;
    private const uint StartfUseStdHandles = 0x00000100;
    private const uint HandleFlagInherit = 0x00000001;

    public static LaunchedProcess StartSuspendedInJob(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        JobObject job)
    {
        IntPtr stdoutRead = IntPtr.Zero;
        IntPtr stdoutWrite = IntPtr.Zero;
        IntPtr stderrRead = IntPtr.Zero;
        IntPtr stderrWrite = IntPtr.Zero;
        IntPtr stdinRead = IntPtr.Zero;
        IntPtr stdinWrite = IntPtr.Zero;
        IntPtr environmentBlock = IntPtr.Zero;
        byte[]? environmentBytes = null;
        var processInfo = default(ProcessInformation);

        try
        {
            CreateOutputPipe(out stdoutRead, out stdoutWrite);
            CreateOutputPipe(out stderrRead, out stderrWrite);
            CreateInputPipe(out stdinRead, out stdinWrite);

            var startupInfo = new StartupInfo
            {
                Cb = Marshal.SizeOf<StartupInfo>(),
                Flags = StartfUseStdHandles,
                StdInput = stdinRead,
                StdOutput = stdoutWrite,
                StdError = stderrWrite
            };
            var commandLine = new StringBuilder(QuoteArgument(executable));
            foreach (var argument in arguments)
            {
                commandLine.Append(' ').Append(QuoteArgument(argument));
            }
            environmentBytes = BuildEnvironmentBlock(environment);
            environmentBlock = Marshal.AllocHGlobal(environmentBytes.Length);
            Marshal.Copy(environmentBytes, 0, environmentBlock, environmentBytes.Length);
            if (!CreateProcess(
                    executable,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    inheritHandles: true,
                    CreateSuspended | CreateNoWindow | CreateUnicodeEnvironment,
                    environmentBlock,
                    Path.GetDirectoryName(executable),
                    ref startupInfo,
                    out processInfo))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"CreateProcess failed for {executable}.");
            }

            Close(ref stdoutWrite);
            Close(ref stderrWrite);
            Close(ref stdinRead);
            Close(ref stdinWrite);

            try
            {
                job.Assign(processInfo.Process);
                var process = Process.GetProcessById(unchecked((int)processInfo.ProcessId));
                _ = process.Handle;
                var stdout = OpenReader(ref stdoutRead);
                var stderr = OpenReader(ref stderrRead);

                if (ResumeThread(processInfo.Thread) == uint.MaxValue)
                {
                    stdout.Dispose();
                    stderr.Dispose();
                    process.Dispose();
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "ResumeThread failed.");
                }

                return new LaunchedProcess(process, stdout, stderr);
            }
            catch
            {
                _ = TerminateProcess(processInfo.Process, 1);
                throw;
            }
        }
        finally
        {
            Close(ref stdoutRead);
            Close(ref stdoutWrite);
            Close(ref stderrRead);
            Close(ref stderrWrite);
            Close(ref stdinRead);
            Close(ref stdinWrite);
            if (environmentBytes is not null)
            {
                if (environmentBlock != IntPtr.Zero)
                {
                    for (var index = 0; index < environmentBytes.Length; index++)
                    {
                        Marshal.WriteByte(environmentBlock, index, 0);
                    }
                    Marshal.FreeHGlobal(environmentBlock);
                }
                CryptographicOperations.ZeroMemory(environmentBytes);
            }
            Close(ref processInfo.Thread);
            Close(ref processInfo.Process);
        }
    }

    private static byte[] BuildEnvironmentBlock(IReadOnlyDictionary<string, string> environment)
    {
        var builder = new StringBuilder();
        foreach (var (name, value) in environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(name).Append('=').Append(value).Append('\0');
        }
        builder.Append('\0');
        return Encoding.Unicode.GetBytes(builder.ToString());
    }

    private static StreamReader OpenReader(ref IntPtr handle)
    {
        var safeHandle = new SafeFileHandle(handle, ownsHandle: true);
        handle = IntPtr.Zero;
        return new StreamReader(
            new FileStream(safeHandle, FileAccess.Read, 4096, isAsync: false),
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);
    }

    private static string QuoteArgument(string argument)
    {
        if (argument.Length != 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            return argument;
        }

        var result = new StringBuilder(argument.Length + 2).Append('"');
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                result.Append('\\', backslashes * 2 + 1).Append(character);
                backslashes = 0;
                continue;
            }

            result.Append('\\', backslashes).Append(character);
            backslashes = 0;
        }

        return result.Append('\\', backslashes * 2).Append('"').ToString();
    }

    private static void CreateOutputPipe(out IntPtr read, out IntPtr write)
    {
        CreatePipe(out read, out write);
        if (!SetHandleInformation(read, HandleFlagInherit, 0))
        {
            var error = Marshal.GetLastWin32Error();
            Close(ref read);
            Close(ref write);
            throw new Win32Exception(error, "SetHandleInformation failed for an output pipe.");
        }
    }

    private static void CreateInputPipe(out IntPtr read, out IntPtr write)
    {
        CreatePipe(out read, out write);
        if (!SetHandleInformation(write, HandleFlagInherit, 0))
        {
            var error = Marshal.GetLastWin32Error();
            Close(ref read);
            Close(ref write);
            throw new Win32Exception(error, "SetHandleInformation failed for the input pipe.");
        }
    }

    private static void CreatePipe(out IntPtr read, out IntPtr write)
    {
        var attributes = new SecurityAttributes
        {
            Length = Marshal.SizeOf<SecurityAttributes>(),
            InheritHandle = true
        };
        if (!CreatePipe(out read, out write, ref attributes, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe failed.");
        }
    }

    private static void Close(ref IntPtr handle)
    {
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            return;
        }

        _ = CloseHandle(handle);
        handle = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Cb;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort Reserved2;
        public IntPtr ReservedData;
        public IntPtr StdInput;
        public IntPtr StdOutput;
        public IntPtr StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(
        out IntPtr readPipe,
        out IntPtr writePipe,
        ref SecurityAttributes pipeAttributes,
        uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(
        string applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

internal sealed record LaunchedProcess(Process Process, StreamReader StandardOutput, StreamReader StandardError);
