namespace MHX.Distribution;

internal static class Management
{
    public static int Update(string[] arguments)
    {
        if (arguments.Length > 1 || (arguments.Length == 1 && arguments[0] != "--check"))
            throw new MhxException("Usage: mhx update [--check]", 2);
        return Cancellable(token => UpdateCommand.Run(arguments.Length == 1, token));
    }

    public static int Install(string[] arguments)
    {
        string? directory = null, profiles = null;
        string method = "native";
        bool register = true;
        for (int i = 0; i < arguments.Length; i++)
        {
            string option = arguments[i];
            if (option == "--no-register") { register = false; continue; }
            if (option is not ("--directory" or "--profile-directory" or "--method") || ++i == arguments.Length)
                throw new MhxException("Invalid installer option. Use --directory, --profile-directory, --method native|dotnet-tool, or --no-register.", 2);
            if (option == "--directory") directory = Path.GetFullPath(arguments[i]);
            if (option == "--profile-directory") profiles = Path.GetFullPath(arguments[i]);
            if (option == "--method") method = arguments[i];
        }
        if (method == "dotnet-tool")
            return Cancellable(token => ToolPackageInstall.FromRelease(directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools"), profiles, register, token));
        if (method != "native") throw new MhxException("Installation method must be native or dotnet-tool.", 2);
        if (AppInfo.IsTool) throw new MhxException("Use a standalone release executable to install the native distribution.");
        return Installation.InstallNative(Environment.ProcessPath!, directory ?? Installation.DefaultBin, profiles, register);
    }

    private static int Cancellable(Func<CancellationToken, Task<int>> operation)
    {
        using CancellationTokenSource cancellation = new(TimeSpan.FromMinutes(5));
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += handler;
        try { return operation(cancellation.Token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { throw new MhxException("Update cancelled or timed out.", 130); }
        finally { Console.CancelKeyPress -= handler; }
    }
}
