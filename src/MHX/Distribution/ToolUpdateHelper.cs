using System.Diagnostics;
using System.Text.Json;

namespace MHX.Distribution;

internal static class ToolUpdateHelper
{
    public static int Run(string requestPath)
    {
        requestPath = Path.GetFullPath(requestPath);
        string directory = Path.GetDirectoryName(requestPath)!;
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!Installation.PhysicalDirectory(directory).Equals(Installation.PhysicalDirectory(AppContext.BaseDirectory), comparison) ||
            !File.Exists(Path.Combine(directory, ".mhx-tool-update")))
            throw new MhxException("The update helper must run from its own staged update directory.");
        string status = Path.Combine(directory, "status.json");
        int result = 1;
        string? package = null;
        try
        {
            if (Path.GetFileName(requestPath) != "request.json" || new FileInfo(requestPath).Length > 16384)
                throw new MhxException("Invalid tool-update request.");
            ToolUpdateRequest request = JsonSerializer.Deserialize(File.ReadAllText(requestPath), ReleaseJsonContext.Default.ToolUpdateRequest)
                ?? throw new MhxException("Invalid tool-update request.");
            ReleaseValidation.StableVersion(request.Version);
            if (request.ParentId <= 0 || !Path.IsPathFullyQualified(request.InstallDirectory) ||
                !Path.IsPathFullyQualified(request.Dotnet) || !Path.IsPathFullyQualified(request.Sdk))
                throw new MhxException("Invalid tool-update locations.");
            package = Path.Combine(directory, $"MHX.{request.Version}.nupkg");
            if (!ReleaseValidation.ValidHash(request.Sha256) || !AtomicInstall.Hash(package).Equals(request.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new MhxException("Tool-update package failed integrity verification.");
            File.WriteAllText(Path.Combine(directory, ".waiting-for-parent"), "");
            WaitForParent(request);
            using FileStream gate = AtomicInstall.Lock(request.InstallDirectory);
            if (!AtomicInstall.Hash(package).Equals(request.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new MhxException("Staged update package changed while waiting.");
            result = ToolPackageInstall.Install(request.InstallDirectory, directory, request.Version, request.Dotnet, request.Sdk);
            Console.Error.WriteLine(result == 0 ? $"Updated MHX .NET tool to {request.Version}." : "The SDK could not update MHX. Close other running MHX/MCP instances and retry.");
            File.WriteAllText(status, result == 0 ? "{\"state\":\"completed\",\"exitCode\":0}" : "{\"state\":\"failed\",\"exitCode\":1}");
            return result;
        }
        catch (Exception)
        {
            File.WriteAllText(status, "{\"state\":\"failed\",\"exitCode\":1}");
            Console.Error.WriteLine("The MHX tool-update helper failed. Close other running instances and retry mhx update.");
            return 1;
        }
        finally
        {
            // Keep only a small status record plus the helper's loaded files.
            if (package is not null && File.Exists(package)) File.Delete(package);
            foreach (string file in new[] { requestPath, Path.Combine(directory, "nuget.config") }) if (File.Exists(file)) File.Delete(file);
        }
    }

    private static void WaitForParent(ToolUpdateRequest request)
    {
        try
        {
            using Process parent = Process.GetProcessById(request.ParentId);
            if (parent.StartTime.ToUniversalTime().Ticks == request.ParentStartTicks && !parent.WaitForExit(60000))
                throw new MhxException("The updating process did not exit.");
        }
        catch (ArgumentException) { } // Parent already exited; PID reuse is checked above.
        catch (InvalidOperationException) { } // Exited between opening the process and reading its start time.
    }
}
