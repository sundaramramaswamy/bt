static class BuildCommand
{
    public enum BuildResult { UpToDate, Succeeded, Failed }

    public static BuildResult RunBuild(BuildGraph g, string[] explicitFiles, int maxJobs, bool dryRun, bool compileOnly = false)
    {
        List<CommandNode> plan;

        if (explicitFiles.Length > 0)
        {
            // Explicit files: resolve and walk forward
            var resolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var arg in explicitFiles)
            {
                var r = FileResolver.Resolve(g, arg);
                if (r != null) resolved.Add(r);
            }
            if (resolved.Count == 0)
            {
                Console.Error.WriteLine($"{Clr.Green}Nothing to build.{Clr.Reset}");
                return BuildResult.UpToDate;
            }
            plan = compileOnly
                ? g.GetCompileCommandsFor(resolved)
                : g.GetAffectedCommands(resolved);
        }
        else
        {
            // Default: mtime-based dirty detection
            Console.Error.WriteLine($"{Clr.Dim}Checking file timestamps...{Clr.Reset}");
            plan = g.GetDirtyCommandsByMtime().Plan;
            if (compileOnly)
            {
                var compileOnly_ = new List<CommandNode>(plan.Count);
                foreach (var c in plan)
                    if (BuildGraph.IsCompileTool(c.Tool)) compileOnly_.Add(c);
                plan = compileOnly_;
            }
        }

        if (plan.Count == 0)
        {
            Console.Error.WriteLine($"{Clr.Green}Everything up to date.{Clr.Reset}");
            return BuildResult.UpToDate;
        }

        // Filter to commands that have command lines (skip synthetic)
        var filtered = new List<CommandNode>(plan.Count);
        foreach (var c in plan)
            if (!string.IsNullOrEmpty(c.CommandLine)) filtered.Add(c);
        plan = filtered;
        if (plan.Count == 0)
        {
            Console.Error.WriteLine($"{Clr.Yellow}No executable commands in plan.{Clr.Reset}");
            return BuildResult.UpToDate;
        }

        Console.Error.WriteLine($"{Clr.Bold}Build plan: {plan.Count} command{(plan.Count == 1 ? "" : "s")}{Clr.Reset}");
        Console.Error.WriteLine();

        if (dryRun)
        {
            foreach (var cmd in plan)
            {
                Console.Error.WriteLine($"{Clr.Cyan}[{cmd.Tool}]{Clr.Reset} {Clr.Dim}{cmd.Project}{Clr.Reset}");
                if (cmd.Tool != "Copy" && !string.IsNullOrEmpty(cmd.WorkingDir))
                    Console.Error.WriteLine($"  {Clr.Dim}cd {cmd.WorkingDir}{Clr.Reset}");
                Console.WriteLine(cmd.CommandLine);
                Console.Error.WriteLine();
            }
            return BuildResult.Succeeded;  // dry-run always "succeeds"
        }

        // Execute in waves: commands whose inputs are all "done" can run in parallel.
        // Seed with files that are already available: source files (no producer)
        // plus outputs of commands NOT in the plan (already up-to-date).
        var planIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in plan) planIds.Add(c.Id);
        var produced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in g.Files.Values)
            if (!g.FileToProducer.TryGetValue(f.Path, out var pid) || !planIds.Contains(pid))
                produced.Add(f.Path);

        // Resolve per-project environment variables for command replay.
        // SetEnv tasks in binlog set PATH, INCLUDE, LIB, etc.
        var envByProject = g.ProjectEnv;

        var remaining = new List<CommandNode>(plan);
        int failures = 0;
        int skippedCount = 0;
        int completed = 0;
        int total = plan.Count;
        bool isTty = !Console.IsErrorRedirected;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var statusLock = new object();

        void WriteStatus(string tool, string file, bool done, bool failed = false)
        {
            lock (statusLock)
            {
                if (isTty)
                {
                    var sym = done ? (failed ? $"{Clr.Red}✗" : $"{Clr.Green}✓") : $"{Clr.Cyan}…";
                    var counter = $"[{completed}/{total}]";
                    var shortFile = Path.GetFileName(file);
                    var line = $"\r{Clr.Bold}{counter}{Clr.Reset} {sym}{Clr.Reset} [{tool}] {shortFile}";
                    var pad = Math.Max(0, Console.WindowWidth - Clr.VisibleLength(line));
                    Console.Error.Write(line + new string(' ', pad));
                    if (done && failed)
                        Console.Error.WriteLine();
                }
                else if (done)
                {
                    var sym = failed ? "✗" : "✓";
                    Console.Error.WriteLine($"  {sym} [{tool}] {file}");
                }
            }
        }

        while (remaining.Count > 0)
        {
            var wave = new List<CommandNode>();
            foreach (var c in remaining)
            {
                bool ready = true;
                foreach (var i in c.Inputs)
                    if (!produced.Contains(i)) { ready = false; break; }
                if (ready) wave.Add(c);
            }
            if (wave.Count == 0)
            {
                if (isTty) Console.Error.WriteLine();
                Console.Error.WriteLine($"{Clr.Red}Deadlock: {remaining.Count} commands stuck (missing inputs){Clr.Reset}");
                foreach (var c in remaining)
                {
                    var missing = new List<string>();
                    foreach (var i in c.Inputs)
                        if (!produced.Contains(i)) { missing.Add(i); if (missing.Count >= 3) break; }
                    Console.Error.WriteLine($"  [{c.Tool}] waiting on: {string.Join(", ", missing)}");
                }
                return BuildResult.Failed;
            }

            foreach (var c in wave) remaining.Remove(c);

            var results = new System.Collections.Concurrent.ConcurrentBag<(CommandNode cmd, int exitCode, string output)>();
            Parallel.ForEach(wave, new ParallelOptions { MaxDegreeOfParallelism = maxJobs }, cmd =>
            {
                WriteStatus(cmd.Tool, cmd.Outputs.Count > 0 ? cmd.Outputs[0] : cmd.Id, done: false);
                var (exitCode, output) = ExecuteCommand(cmd, g, g.GlobalEnv, envByProject.GetValueOrDefault(cmd.Project));
                Interlocked.Increment(ref completed);
                results.Add((cmd, exitCode, output));
                WriteStatus(cmd.Tool, cmd.Outputs.Count > 0 ? cmd.Outputs[0] : cmd.Id, done: true, failed: exitCode != 0);
            });

            foreach (var (cmd, exitCode, output) in results)
            {
                if (exitCode == 0)
                {
                    foreach (var o in cmd.Outputs)
                    {
                        produced.Add(o);
                        // Touch output files to ensure mtime > inputs.
                        // Incremental tools (e.g. link /INCREMENTAL) may skip
                        // rewriting the output when content is unchanged, leaving
                        // its mtime older than inputs — making dirty see stale.
                        var abs = g.ToAbsolute(o);
                        if (File.Exists(abs))
                            File.SetLastWriteTimeUtc(abs, DateTime.UtcNow);
                    }
                }
                else
                {
                    failures++;
                    if (isTty) Console.Error.WriteLine();
                    Console.Error.WriteLine($"  {Clr.Red}✗{Clr.Reset} [{cmd.Tool}] {(cmd.Outputs.Count > 0 ? cmd.Outputs[0] : cmd.Id)}  (exit {exitCode})");
                    if (!string.IsNullOrWhiteSpace(output))
                        Console.Error.WriteLine(output);

                    // Mark failed outputs as poisoned so downstream commands
                    // that depend on them are skipped rather than deadlocking.
                    var poison = new HashSet<string>(cmd.Outputs, StringComparer.OrdinalIgnoreCase);
                    var skipped = new List<CommandNode>();
                    foreach (var s in remaining)
                        foreach (var i in s.Inputs)
                            if (poison.Contains(i)) { skipped.Add(s); break; }
                    while (skipped.Count > 0)
                    {
                        foreach (var s in skipped)
                        {
                            remaining.Remove(s);
                            skippedCount++;
                            foreach (var o in s.Outputs) poison.Add(o);
                        }
                        skipped.Clear();
                        foreach (var s in remaining)
                            foreach (var i in s.Inputs)
                                if (poison.Contains(i)) { skipped.Add(s); break; }
                    }
                }
            }
        }

        if (isTty)
        {
            Console.Error.Write($"\r{new string(' ', Math.Max(Console.WindowWidth, 40))}\r");
        }

        sw.Stop();
        if (failures == 0)
            Console.Error.WriteLine($"{Clr.Green}Build succeeded{Clr.Reset} ({plan.Count} commands, {sw.Elapsed.TotalSeconds:F1}s)");
        else
        {
            var skippedMsg = skippedCount > 0 ? $", {skippedCount} skipped" : "";
            Console.Error.WriteLine($"{Clr.Red}Build failed{Clr.Reset} ({failures} failed{skippedMsg}, {plan.Count} total, {sw.Elapsed.TotalSeconds:F1}s)");
        }
        return failures > 0 ? BuildResult.Failed : BuildResult.Succeeded;
    }

    public static (int exitCode, string output) ExecuteCommand(CommandNode cmd, BuildGraph graph, Dictionary<string, string>? globalEnv = null, Dictionary<string, string>? projectEnv = null)
    {
        if (cmd.Tool == "Copy" && cmd.Inputs.Count > 0 && cmd.Outputs.Count > 0)
            return ExecuteCopy(cmd, graph);

        var cmdLine = cmd.CommandLine;
        string exe, args;

        if (cmdLine.StartsWith('"'))
        {
            var endQuote = cmdLine.IndexOf('"', 1);
            exe = endQuote > 0 ? cmdLine[1..endQuote] : cmdLine;
            args = endQuote > 0 && endQuote + 1 < cmdLine.Length ? cmdLine[(endQuote + 2)..] : "";
        }
        else
        {
            // Binlog command lines often have unquoted paths with spaces
            // e.g. "C:\Program Files\...\cl.exe /nologo /c ..."
            // Try progressively longer prefixes until we find an existing file.
            var sp = cmdLine.IndexOf(' ');
            exe = sp > 0 ? cmdLine[..sp] : cmdLine;
            args = sp > 0 ? cmdLine[(sp + 1)..] : "";

            while (sp > 0 && !File.Exists(exe))
            {
                sp = cmdLine.IndexOf(' ', sp + 1);
                if (sp < 0) { exe = cmdLine; args = ""; break; }
                exe = cmdLine[..sp];
                args = cmdLine[(sp + 1)..];
            }
        }

        var psi = new System.Diagnostics.ProcessStartInfo(exe, args)
        {
            WorkingDirectory = cmd.WorkingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        // Start with a clean environment — don't inherit shell vars
        // (e.g. _NT_SYMBOL_PATH, DBGHELP_*) that can cause tools to hang.
        // Apply binlog vars only: GlobalEnv (base), then ProjectEnv (overlay).
        psi.Environment.Clear();
        if (globalEnv is { Count: > 0 })
            foreach (var (k, v) in globalEnv)
                psi.Environment[k] = v;
        if (projectEnv is { Count: > 0 })
            foreach (var (k, v) in projectEnv)
                psi.Environment[k] = v;

        try
        {
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return (1, "Failed to start process");
            // Read stdout and stderr concurrently to avoid pipe buffer deadlock.
            // Sequential ReadToEnd() can hang when the child fills one pipe
            // while we're blocked draining the other.
            string stderr = "";
            var stderrTask = System.Threading.Tasks.Task.Run(() => stderr = proc.StandardError.ReadToEnd());
            var stdout = proc.StandardOutput.ReadToEnd();
            stderrTask.Wait();
            proc.WaitForExit();
            var output = (stdout + stderr).Trim();
            return (proc.ExitCode, output);
        }
        catch (Exception ex)
        {
            return (1, ex.Message);
        }
    }

    static (int exitCode, string output) ExecuteCopy(CommandNode cmd, BuildGraph graph)
    {
        try
        {
            var src = graph.ToAbsolute(cmd.Inputs[0]);
            var dst = graph.ToAbsolute(cmd.Outputs[0]);
            var dstDir = Path.GetDirectoryName(dst);
            if (dstDir is not null) Directory.CreateDirectory(dstDir);
            File.Copy(src, dst, overwrite: true);
            return (0, "");
        }
        catch (Exception ex)
        {
            return (1, ex.Message);
        }
    }
}
