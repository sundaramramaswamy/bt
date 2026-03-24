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

            foreach (var item in CollectClCompileItems(doc, workingDir))
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
                CommandNode? peer = null;
                var newSrcDir = Path.GetDirectoryName(relPath) ?? "";
                foreach (var cmd in graph.Commands.Values)
                {
                    if (cmd.Tool != "CL" || cmd.Project != proj || cmd.Inputs.Count == 0) continue;
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
                    warnings.Add($"{relPath}: no peer CL command in project, skipping");
                    continue;
                }
                if (peer.CommandLine.Contains("/Yc", StringComparison.OrdinalIgnoreCase))
                {
                    warnings.Add($"{relPath}: peer is PCH creator, skipping");
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
    /// found anywhere in the document.
    static IEnumerable<(string Include, bool HasMetadata)> CollectClCompileItems(
        XDocument doc, string _workingDir)
    {
        var ns = doc.Root?.Name.Namespace ?? XNamespace.None;
        foreach (var el in doc.Descendants(ns + "ClCompile"))
        {
            var inc = el.Attribute("Include")?.Value;
            if (!string.IsNullOrEmpty(inc))
                yield return (inc, el.HasElements);
        }
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
