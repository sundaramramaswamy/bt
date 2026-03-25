using System.Xml.Linq;

record InferenceResult(int InferredCount, List<string> Warnings);

/// Infers new source files that appear in a .vcxproj but are not yet in the build graph
/// (because they were added after the last full MSBuild run).  For each such file a CL
/// command is synthesised by mirroring flags from a peer source in the same project, and
/// the resulting .obj is injected into every LINK/LIB command of that project.
static class SourceInference
{
    public static InferenceResult InferNewSources(BuildGraph graph, string binlogPath)
    {
        var warnings = new List<string>();
        int inferredCount = 0;
        int inferredIdx = 0;

        var binlogStamp = File.GetLastWriteTimeUtc(binlogPath);

        // Collect unique (displayName, workingDir) pairs from CL commands.
        // Multiple commands can share the same project; we only need one workingDir.
        var projects = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cmd in graph.Commands.Values)
        {
            if (cmd.Tool != "CL") continue;
            if (!string.IsNullOrEmpty(cmd.Project) && !string.IsNullOrEmpty(cmd.WorkingDir))
                projects.TryAdd(cmd.Project, cmd.WorkingDir);
        }

        foreach (var (proj, workingDir) in projects)
        {
            // Locate the .vcxproj.  cmd.Project may be a bare name ("XaBench"), a name with
            // extension, or a full path — GetFileNameWithoutExtension normalises all three.
            var projName = Path.GetFileNameWithoutExtension(proj);
            var vcxprojPath = Path.Combine(workingDir, projName + ".vcxproj");
            if (!File.Exists(vcxprojPath))
            {
                var found = Directory.GetFiles(workingDir, "*.vcxproj");
                if (found.Length != 1) continue;
                vcxprojPath = found[0];
            }

            // Only inspect projects that have been touched since the last build.
            if (File.GetLastWriteTimeUtc(vcxprojPath) <= binlogStamp) continue;

            XDocument doc;
            try { doc = XDocument.Load(vcxprojPath); }
            catch { continue; }

            foreach (var item in CollectClCompileItems(doc, workingDir, graph.RootDir))
            {
                var (include, hasMetadata) = item;

                // Skip wildcards — evaluating them requires MSBuild property expansion.
                if (include.Contains('*') || include.Contains('?')) continue;

                // Resolve to a root-relative path the graph understands.
                var absPath = Path.GetFullPath(Path.Combine(workingDir, include));
                if (!File.Exists(absPath)) continue;
                var relPath = graph.ToRelative(absPath);

                // Already tracked by the graph (present in the binlog).
                if (graph.Files.ContainsKey(relPath)) continue;

                if (hasMetadata)
                    warnings.Add(
                        $"{relPath}: has per-file metadata — using peer flags (run full build for exact flags)");

                // ── Find a peer CL command ──────────────────────────────────────────
                // Skip PCH creators (/Yc) during selection so we keep searching for a
                // usable peer even when the same-directory candidate is pch.cpp.
                CommandNode? peer = null;
                var newSrcDir = Path.GetDirectoryName(relPath) ?? "";
                foreach (var cmd in graph.Commands.Values)
                {
                    if (cmd.Tool != "CL" || cmd.Project != proj || cmd.Inputs.Count == 0) continue;
                    if (cmd.CommandLine.Contains("/Yc", StringComparison.OrdinalIgnoreCase)) continue;
                    if (peer == null) peer = cmd;
                    // Prefer a peer in the same directory.
                    if (string.Equals(
                            Path.GetDirectoryName(cmd.Inputs[0]) ?? "",
                            newSrcDir,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        peer = cmd;
                        break;
                    }
                }

                if (peer == null)
                {
                    warnings.Add($"{relPath}: no usable peer CL command in project, skipping");
                    continue;
                }

                // ── Derive output .obj path from the peer's obj directory ───────────
                var peerObjAbs = graph.ToAbsolute(peer.Outputs[0]);
                var objDir = Path.GetDirectoryName(peerObjAbs) ?? workingDir;
                var absNewObj = Path.Combine(objDir,
                    Path.GetFileNameWithoutExtension(relPath) + ".obj");
                var newObj = graph.ToRelative(absNewObj);

                // ── Build the per-file command line ──────────────────────────────────
                var cmdLine = BuildInferredCmdLine(peer.CommandLine, absPath, absNewObj);

                // ── Register the synthesised CL command ──────────────────────────────
                var cmdId = $"CL#inferred#{inferredIdx++}:{proj}/{peer.Target}";
                var newCmd = new CommandNode(cmdId, "CL", proj, peer.Target,
                    [relPath], [newObj], cmdLine, peer.WorkingDir);

                graph.Commands[cmdId] = newCmd;
                graph.Files.TryAdd(relPath, new FileNode(relPath, FileKinds.Classify(relPath)));
                graph.Files.TryAdd(newObj,  new FileNode(newObj,  FileKinds.Classify(newObj)));
                graph.AddConsumer(relPath, cmdId);
                graph.FileToProducer[newObj] = cmdId;

                // ── Inject the new .obj into every LINK/LIB command in this project ──
                foreach (var linkCmd in graph.Commands.Values)
                {
                    if (linkCmd.Tool is not ("LINK" or "LIB")) continue;
                    if (linkCmd.Project != proj) continue;
                    linkCmd.Inputs.Add(newObj);
                    graph.AddConsumer(newObj, linkCmd.Id);
                }

                inferredCount++;
            }
        }

        return new InferenceResult(inferredCount, warnings);
    }

    /// Yields (Include attribute value, hasChildElements) for every <ClCompile> item
    /// in the document, and also follows <Import> links to .vcxitems shared-item files.
    /// Items from .vcxitems are yielded with their Include pre-resolved to an absolute
    /// path (so the caller's workingDir-relative resolution stays correct).
    static IEnumerable<(string Include, bool HasMetadata)> CollectClCompileItems(
        XDocument doc, string workingDir, string rootDir)
    {
        var ns = doc.Root?.Name.Namespace ?? XNamespace.None;

        // Direct items in this file.
        foreach (var el in doc.Descendants(ns + "ClCompile"))
        {
            var inc = el.Attribute("Include")?.Value;
            if (!string.IsNullOrEmpty(inc))
                yield return (inc, el.HasElements);
        }

        // Follow imports to .vcxitems (Shared Items Projects).
        // These are plain item-list XML files that need no MSBuild property evaluation.
        foreach (var import in doc.Descendants(ns + "Import"))
        {
            var projAttr = import.Attribute("Project")?.Value ?? "";
            if (!projAttr.EndsWith(".vcxitems", StringComparison.OrdinalIgnoreCase)) continue;

            var vcxitemsPath = Path.GetFullPath(
                Path.Combine(workingDir, ExpandSimpleMacros(projAttr, workingDir, rootDir)));
            if (!File.Exists(vcxitemsPath)) continue;

            var vcxiDir = Path.GetDirectoryName(vcxitemsPath) ?? workingDir;

            XDocument vcxi;
            try { vcxi = XDocument.Load(vcxitemsPath); }
            catch { continue; }

            var vcxiNs = vcxi.Root?.Name.Namespace ?? XNamespace.None;
            foreach (var el in vcxi.Descendants(vcxiNs + "ClCompile"))
            {
                var inc = el.Attribute("Include")?.Value;
                if (string.IsNullOrEmpty(inc)) continue;
                // Pre-resolve so caller's Path.Combine(workingDir, inc) is correct
                // regardless of where the .vcxitems lives relative to the .vcxproj.
                var absInc = Path.GetFullPath(
                    Path.Combine(vcxiDir, ExpandSimpleMacros(inc, vcxiDir, rootDir)));
                yield return (absInc, el.HasElements);
            }
        }
    }

    /// Expands the subset of MSBuild macros we can resolve without running MSBuild.
    static string ExpandSimpleMacros(string value, string projectDir, string rootDir)
    {
        // Append separator so $(ProjectDir)foo resolves to <dir>\foo correctly.
        var pd = projectDir.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var sd = rootDir.TrimEnd('\\', '/')    + Path.DirectorySeparatorChar;
        return value
            .Replace("$(ProjectDir)",             pd, StringComparison.OrdinalIgnoreCase)
            .Replace("$(MSBuildThisFileDirectory)", pd, StringComparison.OrdinalIgnoreCase)
            .Replace("$(SolutionDir)",             sd, StringComparison.OrdinalIgnoreCase);
    }

    /// Derives a per-file command line for a new source from a peer's existing
    /// per-file command line, replacing only the /Fo and source-file tokens at
    /// the end.
    static string BuildInferredCmdLine(string peerCmdLine, string absNewSrc, string absNewObj)
    {
        if (string.IsNullOrEmpty(peerCmdLine)) return "";

        var parts = BuildGraphFactory.SplitCommandLine(peerCmdLine);

        // Locate the last /Fo token — everything before it is the shared flags.
        int foIdx = -1;
        for (int i = parts.Count - 1; i >= 0; i--)
        {
            if (parts[i].StartsWith("/Fo", StringComparison.OrdinalIgnoreCase))
            {
                foIdx = i;
                break;
            }
        }

        var baseFlags = foIdx > 0
            ? string.Join(" ", parts.Take(foIdx))
            : peerCmdLine;

        return $"{baseFlags} /Fo\"{absNewObj}\" \"{absNewSrc}\"";
    }
}
