using System.Runtime.InteropServices;

namespace MHX.Execution;

internal static unsafe partial class UnixProcess
{
    [LibraryImport("libc", EntryPoint = "execve", SetLastError = true)]
    private static partial int Execve(byte* path, nint* argv, nint* environment);

    public static int Run(LaunchPlan plan)
    {
        List<nint> allocations = [];
        nint Allocate(string value)
        {
            nint memory = Marshal.StringToCoTaskMemUTF8(value);
            allocations.Add(memory);
            return memory;
        }
        try
        {
            nint file = Allocate(plan.Executable);
            nint[] argv = new nint[plan.Arguments.Length + 2];
            argv[0] = file;
            for (int i = 0; i < plan.Arguments.Length; i++) argv[i + 1] = Allocate(plan.Arguments[i]);
            nint[] environment = new nint[plan.Environment.Count + 1];
            int index = 0;
            foreach ((string key, string value) in plan.Environment) environment[index++] = Allocate(key + "=" + value);
            Directory.SetCurrentDirectory(plan.WorkingDirectory);
            fixed (nint* args = argv)
            fixed (nint* env = environment)
                Execve((byte*)file, args, env);
            throw new MhxException($"Unable to execute target (OS error {Marshal.GetLastPInvokeError()}).", 126);
        }
        finally { foreach (nint allocation in allocations) Marshal.FreeCoTaskMem(allocation); }
    }
}
