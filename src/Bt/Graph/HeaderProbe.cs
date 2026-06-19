using System.Diagnostics;

/// Wires precise header → source edges via `cl /showIncludes /Zs`.
///
/// Two classes of sources are probed:
///
/// 1. **Inferred sources** (.cpp added to .vcxproj after the binlog):
///    these have no tlog data at all — the probe is the only way to
///    discover their #include edges.
///
/// 2. **Stale-tlog sources** (existing .cpp whose include closure may
///    have drifted since the last `msbuild /bl`): if the .cpp itself or
///    any of its tlog-known headers has mtime > binlog, a new #include
///    may have been added during a prior bt session.  bt never writes
///    tlogs, so the edge is invisible until reprobed.  Without this,
///    a later layout change to the unseen header silently leaves the
///    .obj stale → ODR heap corruption (STATUS_HEAP_CORRUPTION).
///
/// PCH coverage: with /Yu pch.h, cl reads the .pch binary and does NOT
/// re-parse PCH-internal headers, so they are absent from /showIncludes.
/// Empirically (XaBench), peer /Yu sources' tlogs also lack them — only
/// the project's /Yc pch.cpp invocation tlog contains them.  We copy
/// SyntheticProducers[pch.cpp] edges onto each probed source.
///
/// Failure: /showIncludes /Zs failures are a strict subset of real-build
/// failures (preprocessor + parse only).  If it errors out, the next
/// `bt build` would also fail.  Print cl's stderr and throw.
static class HeaderProbe
{
    public static void Enrich(BuildGraph graph)
    {
        int probeIdx = 0;

        // ── Phase 1: inferred sources (no tlog data at all) ───────────────
        EnrichInferred(graph, ref probeIdx);

        // ── Phase 2: existing sources with potentially stale tlog edges ───
        EnrichStale(graph, ref probeIdx);
    }

    /// Probe inferred CL commands (added by SourceInference, no tlog data).
    static void EnrichInferred(BuildGraph graph, ref int probeIdx)
    {
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

        foreach (var (proj, cmds) in inferredByProject)
        {
            string? pchSource = FindPchSource(graph, proj);
            bool peerUsesYu = AnyPeerUsesYu(graph, proj);

            if (pchSource == null && peerUsesYu)
                Console.Error.WriteLine(
                    $"{Clr.Yellow}warning:{Clr.Reset} project {proj} uses /Yu but no /Yc source found in graph; PCH-internal headers will be missed");

            foreach (var inferred in cmds)
                ProbeSource(graph, inferred, inferred.Inputs[0], pchSource, ref probeIdx);
        }
    }

    /// Probe existing (non-inferred) CL commands whose include closure may
    /// have drifted: source mtime > binlog, or any tlog-known header mtime > binlog.
    static void EnrichStale(BuildGraph graph, ref int probeIdx)
    {
        if (graph.BinlogTimestamp == default) return;
        var binlogStamp = graph.BinlogTimestamp;

        // Group non-inferred, non-/Yc CL commands by project.
        var staleByProject = new Dictionary<string, List<CommandNode>>(StringComparer.OrdinalIgnoreCase);
        foreach (var cmd in graph.Commands.Values)
        {
            if (cmd.Tool != "CL") continue;
            if (cmd.Id.StartsWith("CL#inferred#", StringComparison.Ordinal)) continue;
            if (cmd.CommandLine.Contains("/Yc", StringComparison.OrdinalIgnoreCase)) continue;
            if (cmd.Inputs.Count == 0 || string.IsNullOrEmpty(cmd.CommandLine)) continue;

            var src = cmd.Inputs[0];

            // Check if source or any of its known headers have mtime > binlog.
            if (!IsStale(graph, src, binlogStamp)) continue;

            if (!staleByProject.TryGetValue(cmd.Project, out var list))
            {
                list = [];
                staleByProject[cmd.Project] = list;
            }
            list.Add(cmd);
        }

        if (staleByProject.Count == 0) return;

        int reprobed = 0;
        foreach (var (proj, cmds) in staleByProject)
        {
            string? pchSource = FindPchSource(graph, proj);
            foreach (var cmd in cmds)
            {
                ProbeSource(graph, cmd, cmd.Inputs[0], pchSource, ref probeIdx);
                reprobed++;
            }
        }

        if (reprobed > 0)
            Console.Error.WriteLine(
                $"{Clr.Yellow}reprobe:{Clr.Reset} {reprobed} source(s) with potentially stale tlog edges");
    }

    /// Check if a source file's include closure is potentially stale:
    /// its own mtime > binlog, or any tlog-wired header's mtime > binlog.
    static bool IsStale(BuildGraph graph, string sourceRel, DateTime binlogStamp)
    {
        var abs = graph.ToAbsolute(sourceRel);
        if (File.Exists(abs) && File.GetLastWriteTimeUtc(abs) > binlogStamp)
            return true;

        if (!graph.SyntheticProducers.TryGetValue(sourceRel, out var cmdIds))
            return false;

        foreach (var cmdId in cmdIds)
        {
            if (!graph.Commands.TryGetValue(cmdId, out var cmd)) continue;
            if (cmd.Inputs.Count == 0) continue;
            var headerAbs = graph.ToAbsolute(cmd.Inputs[0]);
            if (File.Exists(headerAbs) && File.GetLastWriteTimeUtc(headerAbs) > binlogStamp)
                return true;
        }
        return false;
    }

    /// Run the 3-step probe for a single source: copy PCH edges, spawn
    /// cl /showIncludes /Zs, wire any newly discovered headers.
    static void ProbeSource(BuildGraph graph, CommandNode cmd, string sourceRel,
        string? pchSource, ref int probeIdx)
    {
        // Seed with already-wired headers to avoid duplicate edges.
        var wired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (graph.SyntheticProducers.TryGetValue(sourceRel, out var existing))
            foreach (var id in existing)
                if (graph.Commands.TryGetValue(id, out var c) && c.Inputs.Count > 0)
                    wired.Add(c.Inputs[0]);

        // ── Step 1: copy PCH-internal edges from /Yc source ────────────
        if (pchSource != null && graph.SyntheticProducers.TryGetValue(pchSource, out var pchIncludes))
        {
            foreach (var includeCmdId in pchIncludes)
            {
                if (!graph.Commands.TryGetValue(includeCmdId, out var includeCmd)) continue;
                if (includeCmd.Inputs.Count == 0) continue;
                var header = includeCmd.Inputs[0];
                if (!wired.Add(header)) continue;
                WireInclude(graph, header, sourceRel, cmd.Project, ref probeIdx);
            }
        }

        // ── Step 2: spawn cl /showIncludes /Zs ────────────────────────
        var includes = RunShowIncludes(graph, cmd);

        // ── Step 3: wire post-PCH headers ─────────────────────────────
        foreach (var header in includes)
        {
            if (!wired.Add(header)) continue;
            WireInclude(graph, header, sourceRel, cmd.Project, ref probeIdx);
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

    /// Spawn cl with the command line + /showIncludes /Zs, parse
    /// stdout for "Note: including file:" lines, return relative header paths
    /// under graph.RootDir.
    static List<string> RunShowIncludes(BuildGraph graph, CommandNode cmd)
    {
        var (exe, args) = SplitExeAndArgs(cmd.CommandLine);

        // Strip /Yu and /Fp (PCH consumption) so the probe doesn't depend on
        // a .pch file that may be stale or from a different compiler version.
        // PCH-internal headers are already wired in step 1 (copied from /Yc
        // source's tlog); /showIncludes without /Yu will rediscover them but
        // the wired set deduplicates.
        args = StripPchFlags(args);

        // /Zs disables codegen (subsumes /c); /showIncludes prints headers.
        args = args + " /Y- /showIncludes /Zs";

        var psi = new ProcessStartInfo(exe, args)
        {
            WorkingDirectory = cmd.WorkingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.Environment.Clear();
        if (graph.GlobalEnv is { Count: > 0 })
            foreach (var (k, v) in graph.GlobalEnv) psi.Environment[k] = v;
        if (graph.ProjectEnv.TryGetValue(cmd.Project, out var pe))
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
                $"failed to probe headers for {cmd.Inputs[0]}: {ex.Message}", ex);
        }

        if (exitCode != 0)
        {
            Console.Error.WriteLine(
                $"{Clr.Red}error:{Clr.Reset} cl /showIncludes /Zs failed for {cmd.Inputs[0]}");
            if (!string.IsNullOrEmpty(stderr)) Console.Error.WriteLine(stderr.TrimEnd());
            if (!string.IsNullOrEmpty(stdout)) Console.Error.WriteLine(stdout.TrimEnd());
            Console.Error.WriteLine(
                $"Fix the source/header errors and re-run: {Clr.Dim}msbuild /bl{Clr.Reset}");
            throw new InvalidOperationException(
                $"header probe failed for {cmd.Inputs[0]}");
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

    /// Remove /Yu, /Yc, and /Fp flags from a CL argument string so the probe
    /// runs without depending on a .pch file.
    static string StripPchFlags(string args)
    {
        // Flags: /Yu"pch.h", /Yupch.h, /Fp"path\pch.pch", /Fpath, /Yc"..."
        // Each may be followed by a quoted or unquoted value (no space between flag and value).
        var parts = BuildGraphFactory.SplitCommandLine(args);
        var sb = new System.Text.StringBuilder();
        foreach (var p in parts)
        {
            if (p.StartsWith("/Yu", StringComparison.OrdinalIgnoreCase)) continue;
            if (p.StartsWith("/Yc", StringComparison.OrdinalIgnoreCase)) continue;
            if (p.StartsWith("/Fp", StringComparison.OrdinalIgnoreCase)) continue;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(p);
        }
        return sb.ToString();
    }
}
