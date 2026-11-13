using System.IO.Pipes;

namespace MHX.Execution;

internal static class WindowsCapture
{
    public static string SdkList(LaunchPlan plan)
    {
        using AnonymousPipeServerStream input = new(PipeDirection.Out, HandleInheritability.Inheritable);
        using AnonymousPipeServerStream output = new(PipeDirection.In, HandleInheritability.Inheritable);
        using AnonymousPipeServerStream error = new(PipeDirection.In, HandleInheritability.Inheritable);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        WindowsProcess.StandardHandles handles = new(input.ClientSafePipeHandle.DangerousGetHandle(),
            output.ClientSafePipeHandle.DangerousGetHandle(), error.ClientSafePipeHandle.DangerousGetHandle());
        int released = 0;
        void ReleaseParentCopies()
        {
            if (Interlocked.Exchange(ref released, 1) != 0) return;
            input.DisposeLocalCopyOfClientHandle();
            output.DisposeLocalCopyOfClientHandle();
            error.DisposeLocalCopyOfClientHandle();
            input.Dispose();
        }
        Task<int> process = Task.Run(() =>
        {
            try
            {
                return WindowsProcess.Run(plan, handles, ReleaseParentCopies, timeout.Token);
            }
            catch
            {
                timeout.Cancel();
                throw;
            }
            finally { ReleaseParentCopies(); }
        });
        Task<string> text = ReadBounded(output, timeout);
        Task<string> diagnostic = ReadBounded(error, timeout);
        try
        {
            int exit = process.GetAwaiter().GetResult();
            if (timeout.IsCancellationRequested) throw new MhxException(".NET SDK discovery timed out or exceeded its output limit.");
            if (exit != 0) throw new MhxException(".NET SDK discovery failed.");
            Task.WhenAll(text, diagnostic).GetAwaiter().GetResult();
            return text.Result;
        }
        finally
        {
            timeout.Cancel();
            // Observe reader cancellation/failures before disposing their streams.
            try { Task.WhenAll(text, diagnostic).GetAwaiter().GetResult(); }
            catch (Exception ex) when (ex is OperationCanceledException or IOException) { }
        }
    }

    private static async Task<string> ReadBounded(Stream stream, CancellationTokenSource timeout)
    {
        using StreamReader reader = new(stream, leaveOpen: true);
        char[] buffer = new char[4096];
        System.Text.StringBuilder result = new();
        int read;
        while ((read = await reader.ReadAsync(buffer, timeout.Token)) != 0)
        {
            if (result.Length + read > 1024 * 1024)
            {
                timeout.Cancel();
                return "";
            }
            result.Append(buffer, 0, read);
        }
        return result.ToString();
    }
}
