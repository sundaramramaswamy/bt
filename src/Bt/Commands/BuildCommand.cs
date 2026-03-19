static class BuildCommand
{
    public static int RunBuild(BuildGraph g, string[] explicitFiles, int maxJobs, bool dryRun)
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
                return 0;
            }
            plan = g.GetAffectedCommands(resolved);
        }
        else
        {
            // Default: mtime-based dirty detection
            Console.Error.WriteLine($"{Clr.Dim}Checking file timestamps...{Clr.Reset}");
            plan = g.GetDirtyCommandsByMtime().Plan;
        }

        if (plan.Count == 0)
        {
            Console.Error.WriteLine($"{Clr.Green}Everything up to date.{Clr.Reset}");
            return 0;
        }

        // Filter to commands that have command lines (skip synthetic)
        plan = plan.Where(c => !string.IsNullOrEmpty(c.CommandLine)).ToList();
        if (plan.Count == 0)
        {
            Console.Error.WriteLine($"{Clr.Yellow}No executable commands in plan.{Clr.Reset}");
            return 0;
        }

        var effectiveJobs = Math.Min(plan.Count, maxJobs);
        Console.Error.WriteLine($"{Clr.Bold}Build plan: {plan.Count} command{(plan.Count == 1 ? "" : "s")}, {effectiveJobs} parallel{Clr.Reset}");
        Console.Error.WriteLine();

        if (dryRun)
        {
            foreach (var cmd in plan)
            {
                Console.Error.WriteLine($"{Clr.Cyan}[{cmd.Tool}]{Clr.Reset} {Clr.Dim}{cmd.Project}{Clr.Reset}");
                Console.WriteLine(cmd.CommandLine);
                Console.Error.WriteLine();
            }
            return 0;
        }

        // Execute in waves: commands whose inputs are all "done" can run in parallel.
        // Seed with files that are already available: source files (no producer)
        // plus outputs of commands NOT in the plan (already up-to-date).
        var planIds = new HashSet<string>(plan.Select(c => c.Id), StringComparer.OrdinalIgnoreCase);
        var produced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in g.Files.Values)
            if (!g.FileToProducer.TryGetValue(f.Path, out var pid) || !planIds.Contains(pid))
                produced.Add(f.Path);

        // Resolve per-project environment variables for command replay.
        // SetEnv tasks in binlog set PATH, INCLUDE, LIB, etc.
        var envByProject = g.ProjectEnv;

        var remaining = new List<CommandNode>(plan);
        int failures = 0;
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
                    Console.Error.Write(line + new string(' ', Math.Max(0, Console.WindowWidth - line.Length + 20)));
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
            var wave = remaining.Where(c => c.Inputs.All(i => produced.Contains(i))).ToList();
            if (wave.Count == 0)
            {
                if (isTty) Console.Error.WriteLine();
                Console.Error.WriteLine($"{Clr.Red}Deadlock: {remaining.Count} commands stuck (missing inputs){Clr.Reset}");
                foreach (var c in remaining)
                {
                    var missing = c.Inputs.Where(i => !produced.Contains(i)).ToList();
                    Console.Error.WriteLine($"  [{c.Tool}] waiting on: {string.Join(", ", missing.Take(3))}");
                }
                return 1;
            }

            foreach (var c in wave) remaining.Remove(c);

            var results = new System.Collections.Concurrent.ConcurrentBag<(CommandNode cmd, int exitCode, string output)>();
            Parallel.ForEach(wave, new ParallelOptions { MaxDegreeOfParallelism = maxJobs }, cmd =>
            {
                WriteStatus(cmd.Tool, cmd.Outputs.FirstOrDefault() ?? cmd.Id, done: false);
                var (exitCode, output) = ExecuteCommand(cmd, envByProject.GetValueOrDefault(cmd.Project));
                Interlocked.Increment(ref completed);
                results.Add((cmd, exitCode, output));
                WriteStatus(cmd.Tool, cmd.Outputs.FirstOrDefault() ?? cmd.Id, done: true, failed: exitCode != 0);
            });

            foreach (var (cmd, exitCode, output) in results)
            {
                if (exitCode == 0)
                {
                    foreach (var o in cmd.Outputs) produced.Add(o);
                }
                else
                {
                    failures++;
                    if (isTty) Console.Error.WriteLine();
                    Console.Error.WriteLine($"  {Clr.Red}✗{Clr.Reset} [{cmd.Tool}] {cmd.Outputs.FirstOrDefault() ?? cmd.Id}  (exit {exitCode})");
                    if (!string.IsNullOrWhiteSpace(output))
                        Console.Error.WriteLine(output);
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
            Console.Error.WriteLine($"{Clr.Red}Build failed{Clr.Reset} ({failures}/{plan.Count} commands failed, {sw.Elapsed.TotalSeconds:F1}s)");

        return failures > 0 ? 1 : 0;
    }

    public static (int exitCode, string output) ExecuteCommand(CommandNode cmd, Dictionary<string, string>? env = null)
    {
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

        // Apply per-project environment from binlog SetEnv tasks
        if (env is { Count: > 0 })
            foreach (var (k, v) in env)
                psi.Environment[k] = v;

        try
        {
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return (1, "Failed to start process");
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            var output = (stdout + stderr).Trim();
            return (proc.ExitCode, output);
        }
        catch (Exception ex)
        {
            return (1, ex.Message);
        }
    }
}
