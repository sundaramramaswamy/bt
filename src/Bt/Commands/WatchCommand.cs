static class WatchCommand
{
    public static int RunWatch(BuildGraph graph, string binlogPath, int debounceMs, Func<string, BuildGraph> loadGraph, string? runCmd = null)
    {
        var maxJobs = Environment.ProcessorCount;
        var watchExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".c", ".cc", ".cpp", ".cxx", ".inl", ".h", ".hh", ".hxx", ".hpp", ".idl" };

        var watchedCount = 0;
        foreach (var f in graph.Files.Values)
            if (watchExts.Contains(Path.GetExtension(f.Path))) watchedCount++;
        var projectSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in graph.Commands.Values)
            if (!string.IsNullOrEmpty(c.Project)) projectSet.Add(c.Project);
        Console.Error.WriteLine($"{Clr.Bold}Watching{Clr.Reset} {Clr.Cyan}{watchedCount}{Clr.Reset} files in {Clr.Cyan}{projectSet.Count}{Clr.Reset} projects {Clr.Dim}(debounce {debounceMs}ms, {maxJobs} cores){Clr.Reset}");
        Console.Error.WriteLine($"{Clr.Dim}Press Ctrl+C to stop.{Clr.Reset}");
        Console.Error.WriteLine();

        // State
        var pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pendingLock = new object();
        var buildInProgress = false;
        var rerunNeeded = false;
        Timer? debounceTimer = null;
        var cts = new CancellationTokenSource();
        var binlogStamp = File.GetLastWriteTimeUtc(binlogPath);

        // Ctrl+C handler
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        void TriggerBuild()
        {
            HashSet<string> batch;
            lock (pendingLock)
            {
                if (pending.Count == 0) return;
                if (buildInProgress) { rerunNeeded = true; return; }
                batch = new HashSet<string>(pending, StringComparer.OrdinalIgnoreCase);
                pending.Clear();
                buildInProgress = true;
            }

            // Check if binlog was updated externally → reload graph
            var currentBinlogStamp = File.GetLastWriteTimeUtc(binlogPath);
            if (currentBinlogStamp != binlogStamp)
            {
                Console.Error.WriteLine($"\n{Clr.Yellow}Binlog changed — reloading graph...{Clr.Reset}");
                try
                {
                    graph = loadGraph(binlogPath);
                    binlogStamp = currentBinlogStamp;
                    watchedCount = 0;
                    foreach (var f in graph.Files.Values)
                        if (watchExts.Contains(Path.GetExtension(f.Path))) watchedCount++;
                    Console.Error.WriteLine($"{Clr.Dim}Reloaded: {watchedCount} files{Clr.Reset}");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"{Clr.Red}Reload failed: {ex.Message}{Clr.Reset}");
                }
            }

            // Resolve batch against graph
            var resolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in batch)
            {
                var r = FileResolver.Resolve(graph, f);
                if (r != null) resolved.Add(r);
            }

            if (resolved.Count == 0)
            {
                lock (pendingLock) { buildInProgress = false; }
                return;
            }

            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            Console.Error.WriteLine($"\n{Clr.Bold}--- {timestamp} ---{Clr.Reset}");
            var sortedResolved = new List<string>(resolved);
            sortedResolved.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var f in sortedResolved)
                Console.Error.WriteLine($"  {Clr.Green}{f}{Clr.Reset}");
            Console.Error.WriteLine();

            var rc = BuildCommand.RunBuild(graph, resolved.ToArray(), maxJobs, dryRun: false);

            if (rc == 0 && !string.IsNullOrEmpty(runCmd))
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine($"{Clr.Cyan}▶ {runCmd}{Clr.Reset}");
                var exitCode = RunPassthrough(runCmd, graph.RootDir);
                if (exitCode != 0)
                    Console.Error.WriteLine($"{Clr.Red}Exit {exitCode}{Clr.Reset}");
            }

            Console.Error.WriteLine($"{Clr.Dim}Waiting for changes...{Clr.Reset}");

            lock (pendingLock)
            {
                buildInProgress = false;
                if (rerunNeeded)
                {
                    rerunNeeded = false;
                    ThreadPool.QueueUserWorkItem(_ => TriggerBuild());
                }
            }
        }

        void OnFileEvent(string fullPath)
        {
            var ext = Path.GetExtension(fullPath);
            if (!watchExts.Contains(ext)) return;

            // Skip editor temporaries: Emacs (.#file, #file#), Vim (.swp/.swo), backups (~)
            var fileName = Path.GetFileName(fullPath);
            if (fileName.StartsWith(".#") || fileName.StartsWith('#') ||
                fileName.EndsWith('~') || fileName.EndsWith(".swp") || fileName.EndsWith(".swo"))
                return;

            var relativePath = Path.GetRelativePath(graph.RootDir, fullPath);

            // Skip generated/SDK headers — their changes are build output,
            // not user edits.  Without this, builds that regenerate headers
            // (e.g. cppwinrt) trigger an infinite rebuild loop.
            if (graph.IsExternal(relativePath)) return;

            lock (pendingLock)
            {
                pending.Add(relativePath);
            }

            // Reset debounce timer
            debounceTimer?.Dispose();
            debounceTimer = new Timer(_ => TriggerBuild(), null, debounceMs, Timeout.Infinite);
        }

        // Set up FileSystemWatcher on the repo root
        using var watcher = new FileSystemWatcher(graph.RootDir)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            EnableRaisingEvents = true
        };

        watcher.Changed += (_, e) => OnFileEvent(e.FullPath);
        watcher.Created += (_, e) => OnFileEvent(e.FullPath);
        watcher.Renamed += (_, e) => OnFileEvent(e.FullPath);

        // Also watch the binlog itself for external rebuilds
        var binlogDir = Path.GetDirectoryName(binlogPath) ?? ".";
        var binlogName = Path.GetFileName(binlogPath);
        using var binlogWatcher = new FileSystemWatcher(binlogDir, binlogName)
        {
            NotifyFilter = NotifyFilters.LastWrite,
            EnableRaisingEvents = true
        };
        binlogWatcher.Changed += (_, _) =>
        {
            lock (pendingLock)
            {
                // Don't trigger a build, just note the binlog changed.
                // The reload will happen at the start of the next TriggerBuild().
            }
        };

        Console.Error.WriteLine($"{Clr.Dim}Waiting for changes...{Clr.Reset}");

        // Block until Ctrl+C
        try { cts.Token.WaitHandle.WaitOne(); } catch (ObjectDisposedException) { }

        debounceTimer?.Dispose();
        Console.Error.WriteLine($"\n{Clr.Dim}Watch stopped.{Clr.Reset}");
        return 0;
    }

    /// Run a command with inherited stdin/stdout/stderr (no capture).
    static int RunPassthrough(string cmdLine, string workingDir)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
            Arguments = $"/c {cmdLine}",
            WorkingDirectory = workingDir,
            UseShellExecute = false,
        };
        try
        {
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return 1;
            proc.WaitForExit();
            return proc.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{Clr.Red}{ex.Message}{Clr.Reset}");
            return 1;
        }
    }
}
