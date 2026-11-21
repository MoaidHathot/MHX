using System.ComponentModel;
using MHX.Configuration;
using MHX.Execution;
using MHX.Distribution;

namespace MHX;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (args is ["--mhx-complete-tool-update", string request]) return ToolUpdateHelper.Run(request);
            if (args.Length > 0 && args[0] == "--mhx-install") return Management.Install(args[1..]);
            if (args.Length > 0 && (args[0] == "--mhx-update" ||
                (args[0] == "update" && LauncherLocation.Current().Name.Equals("mhx", StringComparison.OrdinalIgnoreCase))))
                return Management.Update(args[1..]);
            if (args is ["--mhx-version"])
            {
                Console.WriteLine(AppInfo.Version);
                return 0;
            }
            if (args is ["--mhx-help"]) return Help();
            string? file = null, name = null;
            int offset = 0;
            if (args.Length != 0 && args[0] is "--mhx-config" or "--mhx-profile")
            {
                if (args.Length < 2) throw new MhxException("The MHX option requires a value.", 2);
                if (args[0] == "--mhx-config") file = args[1]; else name = args[1];
                offset = 2;
            }
            LauncherLocation launcher = LauncherLocation.Current();
            Dictionary<string, string> environment = PlanBuilder.ReadEnvironment();
            string? configuration = ProfileLocator.Locate(launcher, file, name, environment);
            string[] remaining = args[offset..];
            if (configuration is null && (remaining.Length == 0 || remaining is ["--help"] or ["-h"])) return Help();
            LaunchProfile profile = configuration is null ? LaunchProfile.DefaultDotnetTool() : ProfileReader.Read(configuration);
            LaunchPlan plan = PlanBuilder.Build(profile, configuration, launcher, remaining, environment);
            return OperatingSystem.IsWindows() ? WindowsProcess.Run(plan) : UnixProcess.Run(plan);
        }
        catch (MhxException error) { Console.Error.WriteLine("mhx: " + error.Message); return error.ExitCode; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or Win32Exception or NotSupportedException)
        {
            Console.Error.WriteLine("mhx: Configuration or process operation failed. Check file access, paths, and executable availability.");
            return 1;
        }
        catch (Exception)
        {
            Console.Error.WriteLine("mhx: Unexpected launcher failure; no arguments or environment values have been logged.");
            return 1;
        }
    }

    private static int Help()
    {
        Console.WriteLine("""
            MHX - configurable command launcher

            mhx Package[@Version] -- tool-arguments
            mhx --mhx-config path/to/profile.mhx [arguments...]
            mhx --mhx-profile name [arguments...]
            mhx update [--check]
            mhx --mhx-help
            mhx --mhx-version

            Without a profile, runs a .NET tool through NuGet v2 using the newest
            installed SDK (11+). Paired .mhx/.shim files configure generic commands.
            MHX_HOME selects a directory of named profiles; MHX_CONFIG selects a file.
            No configuration is automatically loaded from the working directory.
            """);
        return 0;
    }
}
