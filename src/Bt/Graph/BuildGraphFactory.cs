using Microsoft.Build.Logging.StructuredLogger;
using MSTask = Microsoft.Build.Logging.StructuredLogger.Task;

static class BuildGraphFactory
{
    public static BuildGraph FromBinlog(Build build)
    {
        // Discover solution root as common ancestor of all project directories
        var projectDirSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in build.FindChildrenRecursive<Project>())
            if (p.ProjectFile != null)
                projectDirSet.Add(Path.GetDirectoryName(Path.GetFullPath(p.ProjectFile))!);
        var projectDirs = new List<string>(projectDirSet);
        var rootDir = GetCommonAncestor(projectDirs);

        var graph = new BuildGraph { RootDir = rootDir };
        int cmdIndex = 0;

        // Extract environment variables from SetEnv tasks (per-project).
        // These include PATH, INCLUDE, LIB, etc. needed to replay CL/LINK.
        // Also extract CAExcludePath for mtime-skip prefixes.
        foreach (var task in build.FindChildrenRecursive<MSTask>(t => t.Name == "SetEnv"))
        {
            var pf = FindChildFolder(task.Children, "Parameters");
            var name = pf == null ? null : PropValue(pf, "Name", null!);
            if (string.IsNullOrEmpty(name)) continue;
            var value = pf == null ? "" : PropValue(pf, "Value");
            var projNode = task.GetNearestParent<Project>();
            var projFile = projNode?.ProjectFile != null
                ? Path.GetFileName(projNode.ProjectFile) : "";
            var projDir = projNode?.ProjectFile != null
                ? Path.GetDirectoryName(Path.GetFullPath(projNode.ProjectFile)) ?? ""
                : "";

            // Store env var for later replay in ExecuteCommand
            if (!string.IsNullOrEmpty(projFile))
            {
                if (!graph.ProjectEnv.TryGetValue(projFile, out var envMap))
                {
                    envMap = new(StringComparer.OrdinalIgnoreCase);
                    graph.ProjectEnv[projFile] = envMap;
                }
                envMap[name] = value;
            }

            // CAExcludePath: also extract as mtime-skip prefixes
            if (name != "CAExcludePath") continue;
            foreach (var dir in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (dir == "PreventSdkUapPropsAssignment") continue;
                var abs = Path.IsPathRooted(dir) ? dir : Path.GetFullPath(Path.Combine(projDir, dir));
                var rel = Path.GetRelativePath(rootDir, abs);
                if (!rel.EndsWith('\\')) rel += '\\';
                graph.ExternalPrefixes.Add(rel);
            }
        }

        // Extract global environment variables from the Build-level Environment
        // folder.  These are vars MSBuild inherited from the shell (TEMP, TMP,
        // VCToolsInstallDir, etc.) that CL/LINK need but SetEnv doesn't set.
        var envFolderNode = FindChildFolder(build.Children, "Environment");
        if (envFolderNode != null)
        {
            foreach (var child in envFolderNode.Children)
            {
                if (child is not Property prop) continue;
                var n = prop.Name;
                if (string.IsNullOrEmpty(n)) continue;
                // Skip MSBuild-internal vars that shouldn't leak to child processes
                if (n.StartsWith("MSBUILD", StringComparison.OrdinalIgnoreCase)) continue;
                if (n.StartsWith("MSBuild", StringComparison.Ordinal)) continue;
                graph.GlobalEnv[n] = prop.Value ?? "";
            }
        }

        // Track CL command IDs per project so we can wire headers to them later.
        var clCmdsByProject = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        // Track CL command by absolute source path (for tlog matching — tlogs use absolute uppercase paths).
        var clCmdByAbsSource = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Track intermediate output dirs per project (for discovering tlog directories).
        var objDirsByProject = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var knownTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CL", "LINK", "LIB", "MIDL" };
        foreach (var task in build.FindChildrenRecursive<MSTask>(t => knownTools.Contains(t.Name)))
        {
            var projNode = task.GetNearestParent<Project>();
            var proj = projNode?.Name ?? "unknown";
            var projDir = projNode?.ProjectFile != null
                ? Path.GetDirectoryName(Path.GetFullPath(projNode.ProjectFile)) ?? ""
                : "";
            var target = task.GetNearestParent<Target>()?.Name ?? "unknown";
            var pf = FindChildFolder(task.Children, "Parameters");
            if (pf == null) continue;

            // Normalize tool names at the boundary — MSBuild may report any casing
            // (e.g. "LIB" vs "Lib", "Link" vs "LINK"). Canonical form is uppercase.
            var toolName = task.Name.ToUpperInvariant();
            // Extract the full command line from binlog (available for CL, Link, Lib, MIDL)
            // CommandLineArguments is a child of the task, not inside the Parameters folder.
            var cmdLineRaw = PropValue(task, "CommandLineArguments").ReplaceLineEndings(" ").Trim();

            // Skip tasks that MSBuild's incremental check deemed up-to-date:
            // they appear in the binlog with Sources but no CommandLineArguments,
            // so bt cannot replay them.
            if (string.IsNullOrEmpty(cmdLineRaw)) continue;

            var sources = ParameterItems(pf, "Sources", text => graph.ToRelative(ResolveAbsolute(projDir, text)));

            // MIDL uses a Source property (singular), not Sources items
            if (sources.Count == 0 && toolName != "MIDL") continue;

            if (toolName == "CL")
            {
                // CL batches N sources but the relationship is 1:1 (each .cpp → its .obj).
                // Split into individual commands to keep the graph accurate.
                var objFileName = PropValue(pf, "ObjectFileName");
                var absObjFileName = ResolveAbsolute(projDir, objFileName);
                // ObjectFileName ending with \ or / is a directory; otherwise it's a specific file.
                bool objIsDir = objFileName.Length == 0
                    || objFileName[^1] is '\\' or '/';
                if (objIsDir) objDirsByProject.TryAdd(proj, absObjFileName);

                // PCH detection: /Yc = create, /Yu = use
                var pchOutFile = PropValue(pf, "PrecompiledHeaderOutputFile");
                var pchPath = string.IsNullOrEmpty(pchOutFile) ? ""
                    : graph.ToRelative(ResolveAbsolute(projDir, pchOutFile));
                bool createsYc = cmdLineRaw.Contains("/Yc");
                bool usesYu = cmdLineRaw.Contains("/Yu");

                foreach (var src in sources)
                {
                    var obj = objIsDir
                        ? graph.ToRelative(Path.Combine(absObjFileName,
                            Path.GetFileNameWithoutExtension(src) + ".obj"))
                        : graph.ToRelative(absObjFileName);
                    var cmdId = $"CL#{cmdIndex++}:{proj}/{target}";
                    // Build per-file command line: strip batched sources, append single source
                    var absSrc = Path.GetFullPath(Path.Combine(graph.RootDir, src));
                    var absObj = Path.GetFullPath(Path.Combine(graph.RootDir, obj));
                    var clCmdLine = BuildClCommandLine(cmdLineRaw, absSrc, absObj, sources.Count);

                    var inputs = new List<string> { src };
                    var outputs = new List<string> { obj };

                    // /Yc command (pch.cpp): also produces pch.pch
                    if (createsYc && !string.IsNullOrEmpty(pchPath))
                        outputs.Add(pchPath);
                    // /Yu command (regular .cpp): depends on pch.pch
                    if (usesYu && !string.IsNullOrEmpty(pchPath))
                        inputs.Add(pchPath);

                    var cmd = new CommandNode(cmdId, "CL", proj, target, inputs, outputs, clCmdLine, projDir);
                    graph.Commands[cmdId] = cmd;
                    graph.Files.TryAdd(src, new FileNode(src, FileKinds.Classify(src)));
                    graph.Files.TryAdd(obj, new FileNode(obj, FileKinds.Classify(obj)));
                    if (!string.IsNullOrEmpty(pchPath))
                        graph.Files.TryAdd(pchPath, new FileNode(pchPath, FileKinds.Classify(pchPath)));
                    foreach (var i in inputs) graph.AddConsumer(i, cmdId);
                    foreach (var o in outputs) graph.FileToProducer.TryAdd(o, cmdId);

                    // Record absolute source path for tlog matching
                    clCmdByAbsSource.TryAdd(absSrc, cmdId);

                    if (!clCmdsByProject.TryGetValue(proj, out var projCmds))
                    {
                        projCmds = [];
                        clCmdsByProject[proj] = projCmds;
                    }
                    projCmds.Add(cmdId);
                }
            }
            else if (toolName == "MIDL")
            {
                // MIDL compiles one .idl at a time; Source is a property, not items.
                var srcProp = PropValue(pf, "Source");
                if (string.IsNullOrEmpty(srcProp)) continue;
                var src = graph.ToRelative(ResolveAbsolute(projDir, srcProp));

                var metaProp = PropValue(pf, "MetadataFileName");
                if (string.IsNullOrEmpty(metaProp)) continue;
                var meta = graph.ToRelative(ResolveAbsolute(projDir, metaProp));

                var cmdId = $"MIDL#{cmdIndex++}:{proj}/{target}";
                var cmd = new CommandNode(cmdId, "MIDL", proj, target, [src], [meta], cmdLineRaw, projDir);
                graph.Commands[cmdId] = cmd;
                graph.Files.TryAdd(src, new FileNode(src, FileKinds.Classify(src)));
                graph.Files.TryAdd(meta, new FileNode(meta, FileKinds.Classify(meta)));
                graph.AddConsumer(src, cmdId);
                graph.FileToProducer[meta] = cmdId;
            }
            else // Link or Lib — N inputs → 1 output
            {
                // Skip CreateWinMD: link.exe /WINMD:ONLY produces .winmd, not the .exe
                // it claims.  This is a metadata-extraction step, not an inner-loop build.
                var genWinMD = PropValue(pf, "GenerateWindowsMetadata");
                if (genWinMD.Equals("Only", StringComparison.OrdinalIgnoreCase)) continue;

                var outFile = PropValue(pf, "OutputFile");
                outFile = string.IsNullOrEmpty(outFile) ? ""
                    : graph.ToRelative(ResolveAbsolute(projDir, outFile));
                if (string.IsNullOrEmpty(outFile)) continue;

                var cmdId = $"{toolName}#{cmdIndex++}:{proj}/{target}";
                var outputs = new List<string> { outFile };

                // LINK also produces a .pdb alongside the .dll/.exe (when debug
                // info is enabled).  Register it so that downstream Copy tasks
                // (e.g. Binplace copying to Symbols\Product\) wire up and mtime
                // dirty-checking via the LINK command propagates to the PDB.
                if (toolName == "LINK")
                {
                    var pdbFile = PropValue(pf, "ProgramDatabaseFile");
                    if (!string.IsNullOrEmpty(pdbFile))
                    {
                        var pdbRel = graph.ToRelative(ResolveAbsolute(projDir, pdbFile));
                        if (!string.Equals(pdbRel, outFile, StringComparison.OrdinalIgnoreCase))
                            outputs.Add(pdbRel);
                    }
                }

                var cmd = new CommandNode(cmdId, toolName, proj, target, sources, outputs, cmdLineRaw, projDir);
                graph.Commands[cmdId] = cmd;
                foreach (var o in outputs)
                {
                    graph.Files.TryAdd(o, new FileNode(o, FileKinds.Classify(o)));
                    graph.FileToProducer[o] = cmdId;
                }

                foreach (var input in sources)
                {
                    graph.Files.TryAdd(input, new FileNode(input, FileKinds.Classify(input)));
                    graph.AddConsumer(input, cmdId);
                }
            }
        }

        // Wire header dependencies: prefer precise tlog data, fall back to
        // conservative ClInclude if tlogs are missing.
        // Seed case cache: graph.Files (binlog-derived) + ClInclude items (correct casing).
        // This avoids filesystem lookups for tlog ALLCAPS paths.
        graph.SeedCaseCache();
        foreach (var addItem in build.FindChildrenRecursive<AddItem>(ai => ai.Name == "ClInclude"))
        {
            var eval = addItem.GetNearestParent<ProjectEvaluation>();
            var pd = eval?.ProjectFile != null
                ? Path.GetDirectoryName(Path.GetFullPath(eval.ProjectFile)) ?? "" : "";
            foreach (var child in addItem.Children)
            {
                if (child is not Item item) continue;
                var abs = ResolveAbsolute(pd, item.Text);
                var rel = Path.GetRelativePath(graph.RootDir, abs);
                graph.PrimeCaseCacheEntry(abs, rel);
            }
        }
        var tlogWired = WireTlogHeaders(graph, objDirsByProject, clCmdByAbsSource);
        if (!tlogWired)
        {
            Console.Error.WriteLine($"{Clr.Yellow}warning:{Clr.Reset} tlog files not found; using conservative ClInclude header tracking");
            // Conservative fallback: each ClInclude header feeds every CL
            // command in its project.
            foreach (var addItem in build.FindChildrenRecursive<AddItem>(ai => ai.Name == "ClInclude"))
            {
                var eval = addItem.GetNearestParent<ProjectEvaluation>();
                var proj = eval?.Name ?? "unknown";
                var projDir2 = eval?.ProjectFile != null
                    ? Path.GetDirectoryName(Path.GetFullPath(eval.ProjectFile)) ?? ""
                    : "";

                if (!clCmdsByProject.TryGetValue(proj, out var projCmds)) continue;

                foreach (var child2 in addItem.Children)
                {
                    if (child2 is not Item item) continue;
                    var headerPath = graph.ToRelative(ResolveAbsolute(projDir2, item.Text));
                    graph.Files.TryAdd(headerPath, new FileNode(headerPath, FileKinds.Classify(headerPath)));

                    foreach (var cmdId in projCmds)
                    {
                        graph.AddConsumer(headerPath, cmdId);
                        if (graph.Commands.TryGetValue(cmdId, out var cmd) && !cmd.Inputs.Contains(headerPath, StringComparer.OrdinalIgnoreCase))
                            cmd.Inputs.Add(headerPath);
                    }
                }
            }
        }

        // CompileXaml: .xaml → generated .g.h, .g.cpp, .xbf, XamlTypeInfo.g.cpp
        // Two passes: Pass1 (before CL) and Pass2 (after CL, needs CreateWinMD).
        // Pass2 cannot run standalone — the XAML compiler needs MSBuild's CL/
        // CreateWinMD targets in the same invocation for reference resolution.
        // We collapse both passes into ONE CommandNode per project that invokes
        // msbuild /t:MarkupCompilePass1;ClCompile;CreateWinMD;MarkupCompilePass2.
        // MSBuild's tlog-based incremental checking skips already-compiled CL
        // sources; Pass1 has its own fingerprinting (XamlSaveStateFile.xml).
        //
        // Outputs from both passes are merged.  Pass1 owns overlapping files
        // (.g.h, .xbf) via TryAdd first-wins.  Pass2 adds its unique output
        // (XamlTypeInfo.g.cpp).

        // Extract Configuration, Platform, and MSBuild path for the command line.
        string? msbuildPath = null;
        string? buildCfg = null;
        string? buildPlat = null;
        foreach (var p in build.FindChildrenRecursive<Property>(
            p => p.Name is "MSBuildToolsPath" or "Configuration" or "Platform"))
        {
            switch (p.Name)
            {
                case "MSBuildToolsPath" when msbuildPath == null:
                    msbuildPath = Path.Combine(p.Value ?? "", "MSBuild.exe");
                    break;
                case "Configuration" when buildCfg == null:
                    buildCfg = p.Value;
                    break;
                case "Platform" when buildPlat == null:
                    buildPlat = p.Value;
                    break;
            }
            if (msbuildPath != null && buildCfg != null && buildPlat != null) break;
        }

        // Collect CompileXaml tasks and merge outputs per project.
        // Both Pass1 and Pass2 outputs feed the same CommandNode.
        var xamlByProj = new Dictionary<string, (string proj, string projDir, string projFile,
            List<string> inputs, List<string> outputs)>(StringComparer.OrdinalIgnoreCase);

        foreach (var task in build.FindChildrenRecursive<MSTask>(t => t.Name == "CompileXaml"))
        {
            var projNode = task.GetNearestParent<Project>();
            var proj = projNode?.Name ?? "unknown";
            var projFile = projNode?.ProjectFile != null
                ? Path.GetFullPath(projNode.ProjectFile) : "";
            var projDir = projNode?.ProjectFile != null
                ? Path.GetDirectoryName(projFile) ?? "" : "";
            var pf = FindChildFolder(task.Children, "Parameters");
            if (pf == null) continue;

            // Gather input .xaml files
            var xamlInputs = new List<string>();
            foreach (var paramName in new[] { "XamlPages", "XamlApplications" })
                xamlInputs.AddRange(ParameterItems(pf, paramName, text => graph.ToRelative(ResolveAbsolute(projDir, text))));
            if (xamlInputs.Count == 0) continue;

            // Gather output files from OutputItems.
            var of = FindChildFolder(task.Children, "OutputItems");
            var taskOutputs = new List<string>();
            if (of != null)
            {
                for (int ci = 0; ci < of.Children.Count; ci++)
                {
                    if (of.Children[ci] is not NamedNode nn) continue;
                    if (nn.Name is not "_GeneratedCodeFiles" and not "_GeneratedXbfFiles") continue;
                    for (int ji = 0; ji < nn.Children.Count; ji++)
                        if (nn.Children[ji] is Item item)
                            taskOutputs.Add(graph.ToRelative(ResolveAbsolute(projDir, item.Text)));
                }
            }
            if (taskOutputs.Count == 0) continue;

            // Merge into per-project group
            if (!xamlByProj.TryGetValue(projFile, out var grp))
            {
                grp = (proj, projDir, projFile, new List<string>(), new List<string>());
                xamlByProj[projFile] = grp;
            }
            foreach (var i in xamlInputs)
                if (!grp.inputs.Contains(i, StringComparer.OrdinalIgnoreCase))
                    grp.inputs.Add(i);
            foreach (var o in taskOutputs)
                if (!grp.outputs.Contains(o, StringComparer.OrdinalIgnoreCase))
                    grp.outputs.Add(o);
        }

        // Create one CommandNode per project.
        foreach (var (_, grp) in xamlByProj)
        {
            var cmdId = $"CompileXaml#{cmdIndex++}:{grp.proj}/MarkupCompile";

            var cmdLine = "";
            if (msbuildPath != null && !string.IsNullOrEmpty(grp.projFile))
            {
                // Combined target chain: Pass1, SelectClCompile (evaluates
                // CL items and CppWinRT references — needed for XAML type
                // resolution — without invoking cl.exe), then Pass2.
                var targets = "MarkupCompilePass1;SelectClCompile;MarkupCompilePass2";
                var args = $"\"{grp.projFile}\" /t:{targets} /v:quiet /nologo";
                if (buildCfg != null) args += $" /p:Configuration={buildCfg}";
                if (buildPlat != null) args += $" /p:Platform={buildPlat}";
                // SolutionDir needed so OutDir/IntDir resolve to solution-level
                // paths (e.g. for .winmd lookup during Pass2).
                var solDir = rootDir.EndsWith('\\') ? rootDir : rootDir + "\\";
                args += $" /p:SolutionDir={solDir}";
                cmdLine = $"\"{msbuildPath}\" {args}";
            }

            if (grp.outputs.Count == 0) continue;

            var cmd = new CommandNode(cmdId, "CompileXaml", grp.proj, "MarkupCompile",
                grp.inputs, grp.outputs, cmdLine, rootDir);
            graph.Commands[cmdId] = cmd;
            foreach (var input in grp.inputs)
            {
                graph.Files.TryAdd(input, new FileNode(input, FileKinds.Classify(input)));
                graph.AddConsumer(input, cmdId);
            }
            foreach (var output in grp.outputs)
            {
                graph.Files.TryAdd(output, new FileNode(output, FileKinds.Classify(output)));
                graph.FileToProducer.TryAdd(output, cmdId);
            }
        }

        // Convention: cppwinrt generates .g.h/.g.cpp from .winmd produced by MIDL.
        var midlCmds = new List<CommandNode>();
        foreach (var c in graph.Commands.Values)
            if (c.Tool == "MIDL") midlCmds.Add(c);
        foreach (var cmd in midlCmds)
        {
            string? projFile = null;
            foreach (var p in build.FindChildrenRecursive<Project>(p => p.Name == cmd.Project))
            { projFile = p.ProjectFile; break; }
            if (projFile == null) continue;
            var absDir = Path.GetDirectoryName(Path.GetFullPath(projFile)) ?? "";

            foreach (var idlPath in cmd.Inputs)
            {
                var stem = Path.GetFileNameWithoutExtension(idlPath);
                foreach (var ext in new[] { ".g.h", ".g.cpp" })
                {
                    var genPath = Path.Combine(absDir, "Generated Files", stem + ext);
                    if (!File.Exists(genPath)) continue;
                    var rel = graph.ToRelative(genPath);
                    graph.Files.TryAdd(rel, new FileNode(rel, FileKinds.Classify(rel)));
                    cmd.Outputs.Add(rel);
                    graph.FileToProducer.TryAdd(rel, cmd.Id);
                }
            }
        }

        // mdmerge: Exec tasks in CppWinRTMergeProjectWinMDInputs target.
        foreach (var exec in build.FindChildrenRecursive<MSTask>(
            t => t.Name == "Exec"
                 && (t.GetNearestParent<Target>()?.Name == "CppWinRTMergeProjectWinMDInputs")))
        {
            var projNode = exec.GetNearestParent<Project>();
            var proj = projNode?.Name ?? "unknown";
            var projDir3 = projNode?.ProjectFile != null
                ? Path.GetDirectoryName(Path.GetFullPath(projNode.ProjectFile)) ?? ""
                : "";

            var inputs = new List<string>();
            string? output = null;

            foreach (var msg in exec.FindChildrenRecursive<Message>())
            {
                var text = msg.Text ?? "";
                if (text.StartsWith("Processing input metadata file "))
                {
                    var rel = text["Processing input metadata file ".Length..].TrimEnd('.');
                    inputs.Add(graph.ToRelative(ResolveAbsolute(projDir3, rel)));
                }
                else if (text.StartsWith("Validating metadata file "))
                {
                    var rel = text["Validating metadata file ".Length..].TrimEnd('.');
                    output = graph.ToRelative(ResolveAbsolute(projDir3, rel));
                }
            }

            if (output != null && inputs.Count > 0)
            {
                var target = exec.GetNearestParent<Target>()?.Name ?? "unknown";
                var cmdId = $"mdmerge#{cmdIndex++}:{proj}/{target}";
                var cmd = new CommandNode(cmdId, "mdmerge", proj, target, inputs, [output]);
                graph.Commands[cmdId] = cmd;
                foreach (var inp in inputs) graph.AddConsumer(inp, cmdId);
                graph.Files.TryAdd(output, new FileNode(output, FileKinds.Classify(output)));
                graph.FileToProducer[output] = cmdId;
            }
        }

        // makepri: WinAppSdkGenerateProjectPriFile → resources.pri
        foreach (var priTask in build.FindChildrenRecursive<MSTask>(
            t => t.Name == "WinAppSdkGenerateProjectPriFile"))
        {
            var projNode4 = priTask.GetNearestParent<Project>();
            var proj4 = projNode4?.Name ?? "unknown";
            var projDir4 = projNode4?.ProjectFile != null
                ? Path.GetDirectoryName(Path.GetFullPath(projNode4.ProjectFile)) ?? ""
                : "";
            var pf4 = FindChildFolder(priTask.Children, "Parameters");
            var outFile = pf4 == null ? null : PropValue(pf4, "OutputFileName", null!);
            if (outFile == null) continue;

            var priRel = graph.ToRelative(ResolveAbsolute(projDir4, outFile));
            var target4 = priTask.GetNearestParent<Target>()?.Name ?? "unknown";
            var cmdId = $"makepri#{cmdIndex++}:{proj4}/{target4}";
            var priCmdLine = PropValue(priTask, "CommandLineArguments").ReplaceLineEndings(" ").Trim();
            var cmd = new CommandNode(cmdId, "makepri", proj4, target4, [], [priRel])
            {
                CommandLine = priCmdLine,
                WorkingDir = projDir4
            };
            graph.Commands[cmdId] = cmd;
            graph.Files.TryAdd(priRel, new FileNode(priRel, FileKinds.Classify(priRel)));
            graph.FileToProducer[priRel] = cmdId;
        }

        // AppxManifest: WinAppSdkGenerateAppxManifest
        foreach (var manTask in build.FindChildrenRecursive<MSTask>(
            t => t.Name == "WinAppSdkGenerateAppxManifest"))
        {
            var projNode5 = manTask.GetNearestParent<Project>();
            var proj5 = projNode5?.Name ?? "unknown";
            var projDir5 = projNode5?.ProjectFile != null
                ? Path.GetDirectoryName(Path.GetFullPath(projNode5.ProjectFile)) ?? ""
                : "";
            var pf5 = FindChildFolder(manTask.Children, "Parameters");
            if (pf5 == null) continue;

            var inputItems = ParameterItems(pf5, "AppxManifestInput", text => graph.ToRelative(ResolveAbsolute(projDir5, text)));
            var outFile = PropValue(pf5, "AppxManifestOutput", null!);
            if (outFile == null) continue;

            var outRel = graph.ToRelative(ResolveAbsolute(projDir5, outFile));
            var target5 = manTask.GetNearestParent<Target>()?.Name ?? "unknown";
            var cmdId = $"AppxManifest#{cmdIndex++}:{proj5}/{target5}";
            var cmd = new CommandNode(cmdId, "AppxManifest", proj5, target5, inputItems, [outRel]);
            graph.Commands[cmdId] = cmd;
            foreach (var inp in inputItems)
            {
                graph.Files.TryAdd(inp, new FileNode(inp, FileKinds.Classify(inp)));
                graph.AddConsumer(inp, cmdId);
            }
            graph.Files.TryAdd(outRel, new FileNode(outRel, FileKind.Output));
            graph.FileToProducer[outRel] = cmdId;
        }

        // Copy tasks: SourceFiles → DestinationFiles (1:1 parallel lists or scalar).
        foreach (var copyTask in build.FindChildrenRecursive<MSTask>(t => t.Name == "Copy"))
        {
            var projNode7 = copyTask.GetNearestParent<Project>();
            var proj7 = projNode7?.Name ?? "unknown";
            var projDir7 = projNode7?.ProjectFile != null
                ? Path.GetDirectoryName(Path.GetFullPath(projNode7.ProjectFile)) ?? ""
                : "";
            var pf7 = FindChildFolder(copyTask.Children, "Parameters");
            if (pf7 == null) continue;

            var srcParam = FirstChild<Parameter>(pf7.Children, p => p.Name == "SourceFiles");
            var srcItems = srcParam != null ? ItemTexts(srcParam.Children) : null;
            var dstParam = FirstChild<Parameter>(pf7.Children, p => p.Name == "DestinationFiles");
            var dstItems = dstParam != null ? ItemTexts(dstParam.Children) : null;

            if (srcItems == null || srcItems.Count == 0)
            {
                var srcProp = PropValue(pf7, "SourceFiles", null!);
                var dstProp = PropValue(pf7, "DestinationFiles", null!);
                if (srcProp != null && dstProp != null)
                {
                    srcItems = [srcProp];
                    dstItems = [dstProp];
                }
                else continue;
            }
            if (dstItems == null || srcItems.Count != dstItems.Count) continue;

            var target7 = copyTask.GetNearestParent<Target>()?.Name ?? "unknown";

            for (int i = 0; i < srcItems.Count; i++)
            {
                var srcRel = graph.ToRelative(ResolveAbsolute(projDir7, srcItems[i]));
                var dstRel = graph.ToRelative(ResolveAbsolute(projDir7, dstItems[i]));
                if (!graph.Files.ContainsKey(srcRel)) continue;
                if (string.Equals(srcRel, dstRel, StringComparison.OrdinalIgnoreCase)) continue;

                var cmdId = $"Copy#{cmdIndex++}:{proj7}/{target7}";
                var absSrc = ResolveAbsolute(projDir7, srcItems[i]);
                var absDst = ResolveAbsolute(projDir7, dstItems[i]);
                var copyCmd = $"copy \"{absSrc}\" \"{absDst}\"";
                var cmd = new CommandNode(cmdId, "Copy", proj7, target7, [srcRel], [dstRel], copyCmd, projDir7);
                graph.Commands[cmdId] = cmd;
                graph.AddConsumer(srcRel, cmdId);
                graph.Files.TryAdd(dstRel, new FileNode(dstRel, FileKinds.Classify(dstRel)));
                graph.FileToProducer.TryAdd(dstRel, cmdId);
            }
        }

        // AppxPackageRecipe: WinAppSdkGenerateAppxPackageRecipe
        foreach (var recipeTask in build.FindChildrenRecursive<MSTask>(
            t => t.Name == "WinAppSdkGenerateAppxPackageRecipe"))
        {
            var projNode6 = recipeTask.GetNearestParent<Project>();
            var proj6 = projNode6?.Name ?? "unknown";
            var projDir6 = projNode6?.ProjectFile != null
                ? Path.GetDirectoryName(Path.GetFullPath(projNode6.ProjectFile)) ?? ""
                : "";
            var pf6 = FindChildFolder(recipeTask.Children, "Parameters");
            if (pf6 == null) continue;

            var recipeFile = PropValue(pf6, "RecipeFile", null!);
            if (recipeFile == null) continue;

            var recipeRel = graph.ToRelative(ResolveAbsolute(projDir6, recipeFile));

            var inputs = new List<string>();
            var manifest = PropValue(pf6, "AppxManifestXml", null!);
            if (manifest != null)
            {
                var mRel = graph.ToRelative(ResolveAbsolute(projDir6, manifest));
                if (graph.Files.ContainsKey(mRel)) inputs.Add(mRel);
            }
            var payloadParam = FirstChild<Parameter>(pf6.Children, p => p.Name == "PayloadFiles");
            var payload = payloadParam != null ? ItemTexts(payloadParam.Children) : [];
            foreach (var itemText in payload)
            {
                var rel = graph.ToRelative(ResolveAbsolute(projDir6, itemText));
                if (graph.Files.ContainsKey(rel)) inputs.Add(rel);
            }

            var target6 = recipeTask.GetNearestParent<Target>()?.Name ?? "unknown";
            var cmdId = $"AppxRecipe#{cmdIndex++}:{proj6}/{target6}";
            var cmd = new CommandNode(cmdId, "AppxRecipe", proj6, target6, inputs, [recipeRel]);
            graph.Commands[cmdId] = cmd;
            foreach (var inp in inputs) graph.AddConsumer(inp, cmdId);
            graph.Files.TryAdd(recipeRel, new FileNode(recipeRel, FileKinds.Classify(recipeRel)));
            graph.FileToProducer[recipeRel] = cmdId;
        }

        return graph;
    }

    /// Parse CL.read.1.tlog files to wire precise header → source dependencies.
    /// Creates synthetic #include commands: .h → [#include] → .cpp
    /// so the graph reads .h → .cpp → .obj (not .h → .obj directly).
    /// Returns true if at least one tlog was found and parsed.
    static bool WireTlogHeaders(BuildGraph graph,
        Dictionary<string, string> objDirsByProject,
        Dictionary<string, string> clCmdByAbsSource)
    {
        bool foundAny = false;
        int inclIndex = 0;

        // Build reverse map: absSource → relative source path
        var absToRel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (abs, _) in clCmdByAbsSource)
        {
            var rel = graph.ToRelative(abs);
            absToRel[abs] = rel;
        }

        // Track which (header, source) pairs we've already wired to avoid duplication
        var wired = new HashSet<(string header, string source)>();

        foreach (var (proj, absObjDir) in objDirsByProject)
        {
            // Find *.tlog subdirectories under the obj dir
            if (!Directory.Exists(absObjDir)) continue;
            var tlogDirs = Directory.GetDirectories(absObjDir, "*.tlog");
            if (tlogDirs.Length == 0)
            {
                // tlog might be one level up (e.g., XaBench\ARM64\Debug\XaBench.tlog)
                var parent = Path.GetDirectoryName(absObjDir);
                if (parent != null) tlogDirs = Directory.GetDirectories(parent, "*.tlog");
            }

            foreach (var tlogDir in tlogDirs)
            {
                var readTlog = Path.Combine(tlogDir, "CL.read.1.tlog");
                if (!File.Exists(readTlog)) continue;
                foundAny = true;

                string? currentSourceRel = null;
                foreach (var line in File.ReadLines(readTlog))
                {
                    if (line.StartsWith('^'))
                    {
                        var absSource = line[1..];
                        currentSourceRel = absToRel.GetValueOrDefault(absSource);
                        continue;
                    }

                    if (currentSourceRel == null) continue;

                    // Only track headers under the solution root
                    if (!line.StartsWith(graph.RootDir, StringComparison.OrdinalIgnoreCase)) continue;

                    var headerRel = graph.ToRelative(line);
                    var ext = Path.GetExtension(headerRel);
                    if (!IsHeader(ext) && !IsGeneratedSource(ext)) continue;

                    // Deduplicate: same header→source pair from multiple tlog entries
                    if (!wired.Add((headerRel, currentSourceRel))) continue;

                    graph.Files.TryAdd(headerRel, new FileNode(headerRel, FileKinds.Classify(headerRel)));

                    // Create synthetic #include command: header → source
                    var cmdId = $"#include#{inclIndex++}";
                    var cmd = new CommandNode(cmdId, "#include", proj, "", [headerRel], [currentSourceRel]);
                    graph.Commands[cmdId] = cmd;
                    graph.AddConsumer(headerRel, cmdId);
                    // Track in SyntheticProducers (1:N) so mtime walk finds all headers for a source
                    if (!graph.SyntheticProducers.TryGetValue(currentSourceRel, out var spList))
                    {
                        spList = [];
                        graph.SyntheticProducers[currentSourceRel] = spList;
                    }
                    spList.Add(cmdId);
                }
            }
        }

        return foundAny;

        static bool IsHeader(string ext) =>
            ext.Equals(".h", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".hpp", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".hxx", StringComparison.OrdinalIgnoreCase);

        // .g.h, .g.cpp files produced by cppwinrt are #included by some sources
        static bool IsGeneratedSource(string ext) =>
            ext.Equals(".cpp", StringComparison.OrdinalIgnoreCase);
    }

    /// Build a per-file CL command line from the batched command line.
    static string BuildClCommandLine(string batchedCmdLine, string absSource, string absObj, int sourceCount)
    {
        if (string.IsNullOrEmpty(batchedCmdLine)) return "";
        var parts = SplitCommandLine(batchedCmdLine);
        if (parts.Count <= sourceCount) return batchedCmdLine;
        var flagCount = parts.Count - sourceCount;
        var flags = new List<string>(flagCount + 1);
        for (int i = 0; i < flagCount; i++) flags.Add(parts[i]);
        // Inject /FS (force synchronous PDB writes) so parallel CL processes
        // sharing a PDB file don't race.  MSBuild batches N sources into one
        // cl.exe; bt splits them into parallel invocations that contend.
        bool hasFS = false;
        foreach (var f in flags)
            if (f.Equals("/FS", StringComparison.OrdinalIgnoreCase)) { hasFS = true; break; }
        if (!hasFS) flags.Add("/FS");
        return string.Join(" ", flags) + $" /Fo\"{absObj}\" \"{absSource}\"";
    }

    /// Crude command-line splitter that respects double quotes.
    internal static List<string> SplitCommandLine(string cmdLine)
    {
        var result = new List<string>();
        var sb = new System.Text.StringBuilder();
        bool inQuote = false;
        foreach (var ch in cmdLine)
        {
            if (ch == '"') { inQuote = !inQuote; sb.Append(ch); }
            else if (ch == ' ' && !inQuote)
            {
                if (sb.Length > 0) { result.Add(sb.ToString()); sb.Clear(); }
            }
            else sb.Append(ch);
        }
        if (sb.Length > 0) result.Add(sb.ToString());
        return result;
    }

    static string ResolveAbsolute(string baseDir, string path)
    {
        if (Path.IsPathRooted(path)) return Path.GetFullPath(path);
        if (string.IsNullOrEmpty(baseDir)) return path;
        return Path.GetFullPath(Path.Combine(baseDir, path));
    }

    /// Find the longest common ancestor directory of a set of paths.
    static string GetCommonAncestor(List<string> dirs)
    {
        if (dirs.Count == 0) return "";
        if (dirs.Count == 1) return dirs[0];

        var parts = dirs[0].Split(Path.DirectorySeparatorChar);
        int commonLen = parts.Length;

        for (int i = 1; i < dirs.Count; i++)
        {
            var other = dirs[i].Split(Path.DirectorySeparatorChar);
            commonLen = Math.Min(commonLen, other.Length);
            for (int j = 0; j < commonLen; j++)
            {
                if (!string.Equals(parts[j], other[j], StringComparison.OrdinalIgnoreCase))
                {
                    commonLen = j;
                    break;
                }
            }
        }

        return string.Join(Path.DirectorySeparatorChar, parts[..commonLen]);
    }

    // --- Helpers to avoid System.Linq in binlog tree traversal ---

    /// Find first child of a specific type matching a predicate.
    static T? FirstChild<T>(IList<BaseNode> children, Func<T, bool>? predicate = null) where T : BaseNode
    {
        for (int i = 0; i < children.Count; i++)
            if (children[i] is T t && (predicate == null || predicate(t)))
                return t;
        return null;
    }

    /// Find first child Folder with a given name.
    static Folder? FindChildFolder(IList<BaseNode> children, string name)
        => FirstChild<Folder>(children, f => f.Name == name);

    /// Get the value of the first recursively-found Property with a given name, or fallback.
    static string PropValue(TreeNode node, string name, string fallback = "")
    {
        foreach (var p in node.FindChildrenRecursive<Property>(p => p.Name == name))
            return p.Value;
        return fallback;
    }

    /// Collect Item children of the first Parameter with a given name, transformed.
    static List<string> ParameterItems(TreeNode parent, string paramName, Func<string, string> transform)
    {
        var param = FirstChild<Parameter>(parent.Children, p => p.Name == paramName);
        if (param == null) return [];
        var result = new List<string>();
        for (int i = 0; i < param.Children.Count; i++)
            if (param.Children[i] is Item item)
                result.Add(transform(item.Text));
        return result;
    }

    /// Collect Item texts from children matching a type.
    static List<string> ItemTexts(IList<BaseNode> children)
    {
        var result = new List<string>();
        for (int i = 0; i < children.Count; i++)
            if (children[i] is Item item)
                result.Add(item.Text);
        return result;
    }
}
