static class WatchCommand
{
    public static int RunWatch(BuildGraph graph, string binlogPath, int debounceMs, Func<string, BuildGraph> loadGraph, string? runCmd = null)
    {
        var maxJobs = Environment.ProcessorCount;
        var watchExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".c", ".cc", ".cpp", ".cxx", ".inl", ".h", ".hh", ".hxx", ".hpp", ".idl" };

        // Count watched files in graph
        var watchedCount = graph.Files.Values.Count(f => watchExts.Contains(Path.GetExtension(f.Path)));
        var projects = graph.Commands.Values.Select(c => c.Project).Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Console.Error.WriteLine($"{Clr.Bold}Watching{Clr.Reset} {Clr.Cyan}{watchedCount}{Clr.Reset} files in {Clr.Cyan}{projects.Count}{Clr.Reset} projects {Clr.Dim}(debounce {debounceMs}ms, {maxJobs} cores){Clr.Reset}");
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
                    watchedCount = graph.Files.Values.Count(f => watchExts.Contains(Path.GetExtension(f.Path)));
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
            foreach (var f in resolved.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                Console.Error.WriteLine($"  {Clr.Green}{f}{Clr.Reset}");
            Console.Error.WriteLine();

            var rc = BuildCommand.RunBuild(graph, resolved.ToArray(), maxJobs, dryRun: false);

            if (rc == 0 && !string.IsNullOrEmpty(runCmd))
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine($"{Clr.Cyan}▶ {runCmd}{Clr.Reset}");
                var (exitCode, output) = BuildCommand.ExecuteCommand(
                    new CommandNode("run", "#run", "", "", [], [], runCmd, graph.RootDir));
                if (!string.IsNullOrWhiteSpace(output))
                    Console.Error.WriteLine(output);
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

            var relativePath = Path.GetRelativePath(graph.RootDir, fullPath);

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
}
