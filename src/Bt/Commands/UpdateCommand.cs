using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;

/// Self-update from GitHub Releases.
static class UpdateCommand
{
    const string Owner = "sundaramramaswamy";
    const string Repo  = "bt";
    const string LatestUrl = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
    const string AllUrl    = $"https://api.github.com/repos/{Owner}/{Repo}/releases?per_page=1";

    /// Check for (and optionally install) a newer release.
    /// Returns 0 on success, 1 on error.
    public static int RunUpdate(string currentVersion, bool checkOnly)
    {
        // Parse current commit count from "1.0.0-ci.<count>.<hash>"
        if (!TryParseCount(currentVersion, out var currentCount))
        {
            Console.Error.WriteLine(
                $"{Clr.Yellow}warning:{Clr.Reset} can't determine current version " +
                $"({Clr.Dim}{currentVersion}{Clr.Reset}) — dev build?");
            Console.Error.WriteLine("Run from a published binary to use update.");
            return 1;
        }

        // Fetch latest release (stable first, fall back to prerelease)
        Console.Error.Write($"{Clr.Dim}checking github.com/{Owner}/{Repo} ...{Clr.Reset}");
        using var http = CreateClient();
        string json;
        try
        {
            using var resp = http.GetAsync(LatestUrl).GetAwaiter().GetResult();
            if (resp.IsSuccessStatusCode)
            {
                json = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            }
            else if ((int)resp.StatusCode == 404)
            {
                // No stable release — try newest prerelease
                using var allResp = http.GetAsync(AllUrl).GetAwaiter().GetResult();
                if (!allResp.IsSuccessStatusCode)
                {
                    ClearLine();
                    Console.Error.WriteLine(
                        $"{Clr.Red}error:{Clr.Reset} GitHub API returned {(int)allResp.StatusCode} {allResp.ReasonPhrase}");
                    return 1;
                }
                var allJson = allResp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                using var arr = JsonDocument.Parse(allJson);
                if (arr.RootElement.GetArrayLength() == 0)
                {
                    ClearLine();
                    Console.Error.WriteLine($"{Clr.Yellow}no releases found{Clr.Reset}");
                    return 0;
                }
                json = arr.RootElement[0].GetRawText();
            }
            else
            {
                ClearLine();
                Console.Error.WriteLine(
                    $"{Clr.Red}error:{Clr.Reset} GitHub API returned {(int)resp.StatusCode} {resp.ReasonPhrase}");
                if ((int)resp.StatusCode == 403)
                    Console.Error.WriteLine(
                        $"  {Clr.Dim}Rate-limited? Set GITHUB_TOKEN env var.{Clr.Reset}");
                return 1;
            }
        }
        catch (Exception ex)
        {
            ClearLine();
            Console.Error.WriteLine($"{Clr.Red}error:{Clr.Reset} {ex.Message}");
            return 1;
        }
        ClearLine();

        // Parse release
        using var doc = JsonDocument.Parse(json);
        var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
        var releaseVersion = tag.StartsWith('v') ? tag[1..] : tag;

        if (!TryParseCount(releaseVersion, out var latestCount))
        {
            Console.Error.WriteLine(
                $"{Clr.Red}error:{Clr.Reset} can't parse release tag: {Clr.Yellow}{tag}{Clr.Reset}");
            return 1;
        }

        if (latestCount <= currentCount)
        {
            Console.Error.WriteLine(
                $"{Clr.Green}up to date{Clr.Reset}  {Clr.Dim}{currentVersion}{Clr.Reset}");
            return 0;
        }

        Console.Error.WriteLine(
            $"{Clr.Cyan}update available:{Clr.Reset}  " +
            $"{Clr.Dim}{currentVersion}{Clr.Reset} → {Clr.Green}{releaseVersion}{Clr.Reset}");

        if (checkOnly)
            return 0;

        // Find matching asset for this architecture
        var arch = RuntimeInformation.ProcessArchitecture;
        var rid = arch switch
        {
            Architecture.X64   => "win-x64",
            Architecture.Arm64 => "win-arm64",
            _ => null
        };
        if (rid is null)
        {
            Console.Error.WriteLine(
                $"{Clr.Red}error:{Clr.Reset} unsupported architecture: {arch}");
            return 1;
        }

        var assetName = $"bt-{rid}.zip";
        string? downloadUrl = null;
        foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() == assetName)
            {
                downloadUrl = asset.GetProperty("browser_download_url").GetString();
                break;
            }
        }
        if (downloadUrl is null)
        {
            Console.Error.WriteLine(
                $"{Clr.Red}error:{Clr.Reset} asset {Clr.Yellow}{assetName}{Clr.Reset} " +
                $"not found in release {tag}");
            return 1;
        }

        // Download zip
        Console.Error.Write($"{Clr.Dim}downloading {assetName} ...{Clr.Reset}");
        var tempDir = Path.Combine(Path.GetTempPath(), "bt-update");
        Directory.CreateDirectory(tempDir);
        var zipPath = Path.Combine(tempDir, assetName);
        try
        {
            using var stream = http.GetStreamAsync(downloadUrl).GetAwaiter().GetResult();
            using var file = File.Create(zipPath);
            stream.CopyTo(file);
        }
        catch (Exception ex)
        {
            ClearLine();
            Console.Error.WriteLine($"{Clr.Red}error:{Clr.Reset} download failed: {ex.Message}");
            return 1;
        }
        ClearLine();

        // Extract bt.exe from zip
        var extractedExe = Path.Combine(tempDir, "bt.exe");
        try
        {
            if (File.Exists(extractedExe)) File.Delete(extractedExe);
            ZipFile.ExtractToDirectory(zipPath, tempDir, overwriteFiles: true);
            if (!File.Exists(extractedExe))
            {
                Console.Error.WriteLine(
                    $"{Clr.Red}error:{Clr.Reset} bt.exe not found in {assetName}");
                return 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"{Clr.Red}error:{Clr.Reset} extract failed: {ex.Message}");
            return 1;
        }

        // Swap the running binary
        var currentExe = Environment.ProcessPath;
        if (currentExe is null)
        {
            Console.Error.WriteLine($"{Clr.Red}error:{Clr.Reset} can't determine current exe path");
            return 1;
        }

        var oldExe = currentExe + ".old";
        try
        {
            if (File.Exists(oldExe)) File.Delete(oldExe);
            File.Move(currentExe, oldExe);
            File.Move(extractedExe, currentExe);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"{Clr.Red}error:{Clr.Reset} can't replace binary: {ex.Message}");
            // Try to restore if we renamed but failed to move new one in
            if (!File.Exists(currentExe) && File.Exists(oldExe))
            {
                try { File.Move(oldExe, currentExe); }
                catch { Console.Error.WriteLine($"  Backup at: {oldExe}"); }
            }
            return 1;
        }

        // Best-effort cleanup
        try { File.Delete(oldExe); } catch { }
        try { File.Delete(zipPath); } catch { }

        Console.Error.WriteLine(
            $"{Clr.Green}updated{Clr.Reset}  " +
            $"{Clr.Dim}{currentVersion}{Clr.Reset} → {Clr.Green}{releaseVersion}{Clr.Reset}");
        return 0;
    }

    /// Parse commit count from "1.0.0-ci.<count>.<hash>".
    static bool TryParseCount(string version, out int count)
    {
        count = 0;
        // Format: 1.0.0-ci.<count>.<hash>
        var ciIdx = version.IndexOf("-ci.", StringComparison.Ordinal);
        if (ciIdx < 0) return false;
        var afterCi = version[(ciIdx + 4)..]; // skip "-ci."
        var dotIdx = afterCi.IndexOf('.');
        var countStr = dotIdx < 0 ? afterCi : afterCi[..dotIdx];
        return int.TryParse(countStr, out count);
    }

    /// Clean up bt.exe.old left over from a previous update.
    public static void CleanupOldBinary()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null) return;
            var old = exe + ".old";
            if (File.Exists(old)) File.Delete(old);
        }
        catch { }
    }

    static HttpClient CreateClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("bt", Telemetry.Version));
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (!string.IsNullOrEmpty(token))
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);

        return client;
    }

    /// Erase the current line (for progress messages on stderr).
    static void ClearLine()
    {
        if (!Console.IsErrorRedirected)
            Console.Error.Write("\r\x1b[2K");
        else
            Console.Error.WriteLine();
    }
}
