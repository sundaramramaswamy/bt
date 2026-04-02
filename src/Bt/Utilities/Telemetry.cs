using System.Diagnostics;
using System.Net.Http;
using System.Text;

/// Fire-and-forget telemetry to Azure Application Insights.
/// Spawns a detached child process to POST — zero latency on the parent.
static class Telemetry
{
    const string IKey = "89e7fce8-3a1a-442e-b5c7-eca51539d9f5";
    const string Endpoint = "https://westus3-1.in.applicationinsights.azure.com/v2/track";

    /// Set once at startup from Program.cs.
    public static string Version { get; set; } = "unknown";

    /// Spawn a detached bt process to POST the telemetry event.
    /// Returns immediately (~2ms for Process.Start).
    public static void LogCommand(string command, bool ok, int count = 1)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe == null) return;
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                ArgumentList = { "--telemetry", command, Version, ok ? "1" : "0", count.ToString() },
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })?.Dispose();
        }
        catch { }
    }

    /// Child process entry point: POST the event and exit.
    /// Called when bt is invoked as `bt --telemetry <cmd> <ver> <ok> [count]`.
    public static int Post(string[] args)
    {
        try
        {
            var command = args[1];
            var version = args.Length > 2 ? args[2] : "unknown";
            var ok = args.Length > 3 && args[3] == "1";
            var count = args.Length > 4 && int.TryParse(args[4], out var c) ? c : 1;
            var time = DateTime.UtcNow.ToString("O");
            var json = "[{\"name\":\"AppEvents\",\"time\":\"" + time
                + "\",\"iKey\":\"" + IKey
                + "\",\"data\":{\"baseType\":\"EventData\",\"baseData\":{\"ver\":2,\"name\":\"invocation\""
                + ",\"properties\":{\"command\":\"" + command
                + "\",\"version\":\"" + version
                + "\",\"ok\":\"" + (ok ? "true" : "false")
                + "\",\"count\":\"" + count
                + "\"}}}}]";
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            http.PostAsync(Endpoint, new StringContent(json, Encoding.UTF8, "application/json"))
                .GetAwaiter().GetResult();
        }
        catch { }
        return 0;
    }
}
