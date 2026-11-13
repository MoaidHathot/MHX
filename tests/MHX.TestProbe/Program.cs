using System.Diagnostics;
using System.Text.Json;

if (args is ["--list-sdks"] && Environment.GetEnvironmentVariable("PROBE_SDK_MODE") is string mode)
{
    if (mode == "flood") { Console.Write(new string('x', 2 * 1024 * 1024)); return 0; }
    if (mode == "fail") { Console.Error.WriteLine("private-sdk-diagnostic"); return 42; }
    if (mode == "hold")
    {
        string pidPath = Environment.GetEnvironmentVariable("PROBE_PID_FILE")!;
        await File.WriteAllTextAsync(pidPath + ".tmp", Environment.ProcessId.ToString());
        File.Move(pidPath + ".tmp", pidPath);
        await Task.Delay(Timeout.Infinite);
        return 0;
    }
}
if (args is ["--list-sdks"] && Environment.GetEnvironmentVariable("PROBE_SDK_LIST") is string sdkList)
{
    Console.WriteLine(sdkList);
    return 0;
}
if (args is ["hold"]) { await Task.Delay(Timeout.Infinite); return 0; }
int? descendant = null;
if (args is ["tree"])
{
    ProcessStartInfo start = new(Environment.ProcessPath!) { UseShellExecute = false };
    start.ArgumentList.Add("hold");
    descendant = Process.Start(start)!.Id;
}
Console.Error.WriteLine(JsonSerializer.Serialize(new
{
    args, cwd = Environment.CurrentDirectory, pid = Environment.ProcessId, descendant,
    value = Environment.GetEnvironmentVariable("PROBE_VALUE"),
    removed = Environment.GetEnvironmentVariable("PROBE_REMOVE"),
    telemetry = Environment.GetEnvironmentVariable("DOTNET_CLI_TELEMETRY_OPTOUT"),
}));
await Console.OpenStandardInput().CopyToAsync(Console.OpenStandardOutput());
return int.TryParse(Environment.GetEnvironmentVariable("PROBE_EXIT"), out int code) ? code : 23;
