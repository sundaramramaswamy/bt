using System.Diagnostics;

/// Fire-and-forget telemetry to Azure Application Insights.
/// Spawns a detached curl.exe process to POST — zero latency on the parent.
/// Set BT_NO_TELEMETRY=1 to disable.
static class Telemetry
{
    const string IKey = "89e7fce8-3a1a-442e-b5c7-eca51539d9f5";
    const string Endpoint = "https://westus3-1.in.applicationinsights.azure.com/v2/track";

    static readonly bool Disabled =
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BT_NO_TELEMETRY"));

    /// Set once at startup from Program.cs.
    public static string Version { get; set; } = "unknown";

    /// Spawn a detached curl.exe to POST the telemetry event.
    /// Returns immediately (~2ms for Process.Start).
    public static void LogCommand(string command, bool ok, string flags = "", int count = 1)
    {
        if (Disabled) return;
        try
        {
            var time = DateTime.UtcNow.ToString("O");
            var json = "[{\"name\":\"AppEvents\",\"time\":\"" + time
                + "\",\"iKey\":\"" + IKey
                + "\",\"data\":{\"baseType\":\"EventData\",\"baseData\":{\"ver\":2,\"name\":\"invocation\""
                + ",\"properties\":{\"command\":\"" + command
                + "\",\"version\":\"" + Version
                + "\",\"ok\":\"" + (ok ? "true" : "false")
                + "\",\"flags\":\"" + flags
                + "\",\"count\":\"" + count
                + "\"}}}}]";
            Process.Start(new ProcessStartInfo
            {
                FileName = "curl.exe",
                ArgumentList = { "-s", "-X", "POST",
                    "-H", "Content-Type: application/json",
                    "-d", json, Endpoint },
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })?.Dispose();
        }
        catch { }
    }
}
