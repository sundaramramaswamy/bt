using System.Diagnostics;

/// Wires precise header → source edges for inferred CL commands by spawning
/// `cl /showIncludes /Zs` on each inferred source.  Inference itself produces
/// CL commands with no header edges; without enrichment, a header touch can
/// silently leave the inferred .obj stale and crash at runtime
/// (STATUS_HEAP_CORRUPTION).
///
/// PCH coverage: with /Yu pch.h, cl reads the .pch binary and does NOT
/// re-parse PCH-internal headers, so they are absent from /showIncludes.
/// Empirically (XaBench), peer /Yu sources' tlogs also lack them — only
/// the project's /Yc pch.cpp invocation tlog contains them.  We copy
/// SyntheticProducers[pch.cpp] edges onto each inferred source.
///
/// Failure: /showIncludes /Zs failures are a strict subset of real-build
/// failures (preprocessor + parse only).  If it errors out, the next
/// `bt build` would also fail.  Print cl's stderr and throw.
static class InferredHeaderProbe
{
    public static void Enrich(BuildGraph graph)
    {
        // Find inferred CL commands grouped by project.
        var inferredByProject = new Dictionary<string, List<CommandNode>>(StringComparer.OrdinalIgnoreCase);
        foreach (var cmd in graph.Commands.Values)
        {
            if (cmd.Tool != "CL") continue;
            if (!cmd.Id.StartsWith("CL#inferred#", StringComparison.Ordinal)) continue;
            if (!inferredByProject.TryGetValue(cmd.Project, out var list))
            {
                list = [];
                inferredByProject[cmd.Project] = list;
            }
            list.Add(cmd);
        }

        if (inferredByProject.Count == 0) return;

        int probeIdx = 0;
        foreach (var (proj, cmds) in inferredByProject)
        {
            // Locate /Yc pch.cpp source for this project (if any).
            string? pchSource = FindPchSource(graph, proj);
            bool peerUsesYu = AnyPeerUsesYu(graph, proj);

            if (pchSource == null && peerUsesYu)
                Console.Error.WriteLine(
                    $"{Clr.Yellow}warning:{Clr.Reset} project {proj} uses /Yu but no /Yc source found in graph; PCH-internal headers will be missed");

            foreach (var inferred in cmds)
            {
                var newSrc = inferred.Inputs[0];

                // ── Step 1: copy PCH-internal edges from /Yc source ────────────
                var wired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (pchSource != null && graph.SyntheticProducers.TryGetValue(pchSource, out var pchIncludes))
                {
                    foreach (var includeCmdId in pchIncludes)
                    {
                        if (!graph.Commands.TryGetValue(includeCmdId, out var includeCmd)) continue;
                        if (includeCmd.Inputs.Count == 0) continue;
                        var header = includeCmd.Inputs[0];
                        if (!wired.Add(header)) continue;
                        WireInclude(graph, header, newSrc, proj, ref probeIdx);
                    }
                }

                // ── Step 2: spawn cl /showIncludes /Zs ────────────────────────
                var includes = RunShowIncludes(graph, inferred);

                // ── Step 3: wire post-PCH headers ─────────────────────────────
                foreach (var header in includes)
                {
                    if (!wired.Add(header)) continue;
                    WireInclude(graph, header, newSrc, proj, ref probeIdx);
                }
            }
        }
    }

    static string? FindPchSource(BuildGraph graph, string project)
    {
        foreach (var cmd in graph.Commands.Values)
        {
            if (cmd.Tool != "CL" || cmd.Project != project) continue;
            if (cmd.CommandLine.Contains("/Yc", StringComparison.OrdinalIgnoreCase)
                && cmd.Inputs.Count > 0)
                return cmd.Inputs[0];
        }
        return null;
    }

    static bool AnyPeerUsesYu(BuildGraph graph, string project)
    {
        foreach (var cmd in graph.Commands.Values)
        {
            if (cmd.Tool != "CL" || cmd.Project != project) continue;
            if (cmd.Id.StartsWith("CL#inferred#", StringComparison.Ordinal)) continue;
            if (cmd.CommandLine.Contains("/Yu", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    static void WireInclude(BuildGraph graph, string headerRel, string sourceRel,
        string project, ref int probeIdx)
    {
        graph.Files.TryAdd(headerRel, new FileNode(headerRel, FileKinds.Classify(headerRel)));
        var cmdId = $"#include#probe#{probeIdx++}";
        var cmd = new CommandNode(cmdId, "#include", project, "", [headerRel], [sourceRel]);
        graph.Commands[cmdId] = cmd;
        graph.AddConsumer(headerRel, cmdId);
        if (!graph.SyntheticProducers.TryGetValue(sourceRel, out var spList))
        {
            spList = [];
            graph.SyntheticProducers[sourceRel] = spList;
        }
        spList.Add(cmdId);
    }

    /// Spawn cl with the inferred command line + /showIncludes /Zs, parse
    /// stdout for "Note: including file:" lines, return relative header paths
    /// under graph.RootDir.
    static List<string> RunShowIncludes(BuildGraph graph, CommandNode inferred)
    {
        var (exe, args) = SplitExeAndArgs(inferred.CommandLine);

        // /Zs disables codegen (subsumes /c); /showIncludes prints headers
        // to stderr.  Append both — cl ignores stale /Fo with /Zs.
        args = args + " /showIncludes /Zs";

        var psi = new ProcessStartInfo(exe, args)
        {
            WorkingDirectory = inferred.WorkingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.Environment.Clear();
        if (graph.GlobalEnv is { Count: > 0 })
            foreach (var (k, v) in graph.GlobalEnv) psi.Environment[k] = v;
        if (graph.ProjectEnv.TryGetValue(inferred.Project, out var pe))
            foreach (var (k, v) in pe) psi.Environment[k] = v;

        string stdout, stderr;
        int exitCode;
        try
        {
            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException($"Failed to start {exe}");
            string err = "";
            var errTask = System.Threading.Tasks.Task.Run(() => err = proc.StandardError.ReadToEnd());
            stdout = proc.StandardOutput.ReadToEnd();
            errTask.Wait();
            stderr = err;
            proc.WaitForExit();
            exitCode = proc.ExitCode;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"failed to probe headers for {inferred.Inputs[0]}: {ex.Message}", ex);
        }

        if (exitCode != 0)
        {
            Console.Error.WriteLine(
                $"{Clr.Red}error:{Clr.Reset} cl /showIncludes /Zs failed for inferred source {inferred.Inputs[0]}");
            if (!string.IsNullOrEmpty(stderr)) Console.Error.WriteLine(stderr.TrimEnd());
            if (!string.IsNullOrEmpty(stdout)) Console.Error.WriteLine(stdout.TrimEnd());
            Console.Error.WriteLine(
                $"Fix the source/header errors and re-run: {Clr.Dim}msbuild /bl{Clr.Reset}");
            throw new InvalidOperationException(
                $"header probe failed for {inferred.Inputs[0]}");
        }

        // /showIncludes goes to stdout.  Parse "Note: including file:<spaces><path>".
        var headers = new List<string>();
        const string marker = "Note: including file:";
        foreach (var line in (stdout + "\n" + stderr).Split('\n'))
        {
            var idx = line.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) continue;
            var path = line[(idx + marker.Length)..].Trim().TrimEnd('\r');
            if (string.IsNullOrEmpty(path)) continue;
            // Filter to paths under root (matches WireTlogHeaders behaviour).
            if (!path.StartsWith(graph.RootDir, StringComparison.OrdinalIgnoreCase)) continue;
            var ext = Path.GetExtension(path);
            if (!IsHeader(ext) && !ext.Equals(".cpp", StringComparison.OrdinalIgnoreCase)) continue;
            headers.Add(graph.ToRelative(path));
        }
        return headers;

        static bool IsHeader(string ext) =>
            ext.Equals(".h",   StringComparison.OrdinalIgnoreCase)
         || ext.Equals(".hpp", StringComparison.OrdinalIgnoreCase)
         || ext.Equals(".hxx", StringComparison.OrdinalIgnoreCase);
    }

    /// Split a command line into (exe, args), handling unquoted exe paths
    /// with spaces (e.g. "C:\Program Files\...\cl.exe").  Mirrors
    /// BuildCommand.ExecuteCommand.
    static (string exe, string args) SplitExeAndArgs(string cmdLine)
    {
        if (cmdLine.StartsWith('"'))
        {
            var endQuote = cmdLine.IndexOf('"', 1);
            return endQuote > 0
                ? (cmdLine[1..endQuote],
                   endQuote + 1 < cmdLine.Length ? cmdLine[(endQuote + 2)..] : "")
                : (cmdLine, "");
        }
        var sp = cmdLine.IndexOf(' ');
        var exe = sp > 0 ? cmdLine[..sp] : cmdLine;
        var args = sp > 0 ? cmdLine[(sp + 1)..] : "";
        while (sp > 0 && !File.Exists(exe))
        {
            sp = cmdLine.IndexOf(' ', sp + 1);
            if (sp < 0) return (cmdLine, "");
            exe = cmdLine[..sp];
            args = cmdLine[(sp + 1)..];
        }
        return (exe, args);
    }
}
