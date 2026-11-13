using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using MHX.Configuration;

namespace MHX.Execution;

internal static unsafe partial class WindowsProcess
{
    internal sealed record StandardHandles(nint Input, nint Output, nint Error);

    public static int Run(LaunchPlan plan, StandardHandles? streams = null, Action? started = null, CancellationToken cancellationToken = default)
    {
        string commandLine = WindowsArguments.Encode(new[] { plan.Executable }.Concat(plan.Arguments));
        if (commandLine.Length >= 32767) throw new MhxException("Windows command line exceeds its 32,767-character limit.");
        char[] command = (commandLine + '\0').ToCharArray();
        char[] environment = (string.Join('\0', plan.Environment.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Key + "=" + x.Value)) + "\0\0").ToCharArray();

        using SafeFileHandle job = Own(CreateJobObject(0, null), "create process job");
        JobLimits limits = default;
        limits.Basic.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        Check(SetInformationJobObject(job.DangerousGetHandle(), 9, &limits, (uint)sizeof(JobLimits)), "configure process job");
        using SafeFileHandle input = DuplicateStandard(streams?.Input ?? GetStdHandle(-10), 0x80000000);
        using SafeFileHandle output = DuplicateStandard(streams?.Output ?? GetStdHandle(-11), 0x40000000);
        using SafeFileHandle error = DuplicateStandard(streams?.Error ?? GetStdHandle(-12), 0x40000000);
        nint* handles = stackalloc nint[] { input.DangerousGetHandle(), output.DangerousGetHandle(), error.DangerousGetHandle() };
        nint jobValue = job.DangerousGetHandle();
        nuint bytes = 0;
        InitializeProcThreadAttributeList(0, 2, 0, ref bytes);
        if (bytes == 0) throw Failure("size process attributes");
        nint attributes = (nint)NativeMemory.Alloc(bytes);
        bool initialized = false;
        try
        {
            Check(InitializeProcThreadAttributeList(attributes, 2, 0, ref bytes), "initialize process attributes");
            initialized = true;
            Check(UpdateProcThreadAttribute(attributes, 0, 0x20002, handles, (nuint)(3 * sizeof(nint)), 0, 0), "restrict inherited handles");
            // Atomically join the job during creation. There is no interval in
            // which the target can run or fork outside supervision.
            Check(UpdateProcThreadAttribute(attributes, 0, 0x2000D, &jobValue, (nuint)sizeof(nint), 0, 0), "assign creation job");
            StartupInfoEx startup = default;
            startup.Info.Size = (uint)sizeof(StartupInfoEx);
            startup.Info.Flags = 0x100; // STARTF_USESTDHANDLES
            startup.Info.Input = handles[0]; startup.Info.Output = handles[1]; startup.Info.Error = handles[2];
            startup.Attributes = attributes;
            uint flags = 0x80000 | 0x400; // EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT
            if (GetConsoleWindow() == 0) flags |= 0x8000000; // no new console in headless hosts
            ProcessInformation info;
            fixed (char* cmd = command)
            fixed (char* env = environment)
                Check(CreateProcess(plan.Executable, cmd, 0, 0, 1, flags, env, plan.WorkingDirectory, &startup, out info), "start target");
            using SafeFileHandle process = Own(info.Process, "open process");
            using SafeFileHandle thread = Own(info.Thread, "open thread");
            using CancellationTokenRegistration cancellation = cancellationToken.Register(() => TerminateJobObject(job.DangerousGetHandle(), 1));
            started?.Invoke();
            ConsoleCancelEventHandler handler = (_, e) => e.Cancel = true; // child receives the same console event
            Console.CancelKeyPress += handler;
            try
            {
                if (WaitForSingleObject(process.DangerousGetHandle(), uint.MaxValue) != 0) throw Failure("wait for target");
                Check(GetExitCodeProcess(process.DangerousGetHandle(), out uint exitCode), "read target exit code");
                return unchecked((int)exitCode);
            }
            finally { Console.CancelKeyPress -= handler; }
        }
        finally
        {
            if (initialized) DeleteProcThreadAttributeList(attributes);
            NativeMemory.Free((void*)attributes);
        }
    }

    private static SafeFileHandle DuplicateStandard(nint handle, uint access)
    {
        SafeFileHandle? fallback = null;
        try
        {
            if (handle == 0 || handle == -1)
            {
                fallback = Own(CreateFile("\\\\.\\NUL", access, 3, 0, 3, 0, 0), "open null device");
                handle = fallback.DangerousGetHandle();
            }
            nint current = GetCurrentProcess();
            Check(DuplicateHandle(current, handle, current, out nint copy, 0, 1, 2), "duplicate standard handle");
            return Own(copy, "duplicate standard handle");
        }
        finally { fallback?.Dispose(); }
    }

    private static MhxException Failure(string operation) => new($"Unable to {operation} (Windows error {Marshal.GetLastPInvokeError()}).");
    private static void Check(int result, string operation) { if (result == 0) throw Failure(operation); }
    private static SafeFileHandle Own(nint handle, string operation) => handle == 0 || handle == -1 ? throw Failure(operation) : new(handle, true);

    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    {
        public long ProcessTime, JobTime; public uint LimitFlags;
        public nuint MinimumWorkingSet, MaximumWorkingSet; public uint ActiveProcesses;
        public nuint Affinity; public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct JobLimits
    {
        public BasicLimits Basic;
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfo
    {
        public uint Size; public nint Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XChars, YChars, Fill, Flags;
        public ushort Show, ReservedSize; public nint ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo Info; public nint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public nint Process, Thread; public uint ProcessId, ThreadId; }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)] private static partial nint CreateJobObject(nint security, string? name);
    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial int SetInformationJobObject(nint job, int type, void* info, uint length);
    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial int TerminateJobObject(nint job, uint exitCode);
    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial int InitializeProcThreadAttributeList(nint list, int count, uint flags, ref nuint bytes);
    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial int UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, void* value, nuint size, nint previous, nint returned);
    [LibraryImport("kernel32.dll")] private static partial void DeleteProcThreadAttributeList(nint list);
    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CreateProcess(string application, char* command, nint processSecurity, nint threadSecurity, int inherit, uint flags, char* environment, string cwd, StartupInfoEx* startup, out ProcessInformation info);
    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial uint WaitForSingleObject(nint handle, uint timeout);
    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial int GetExitCodeProcess(nint process, out uint exitCode);
    [LibraryImport("kernel32.dll")] private static partial nint GetStdHandle(int number);
    [LibraryImport("kernel32.dll")] private static partial nint GetCurrentProcess();
    [LibraryImport("kernel32.dll")] private static partial nint GetConsoleWindow();
    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial int DuplicateHandle(nint sourceProcess, nint source, nint targetProcess, out nint target, uint access, int inherit, uint options);
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)] private static partial nint CreateFile(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);
}
