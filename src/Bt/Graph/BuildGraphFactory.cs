using Microsoft.Build.Logging.StructuredLogger;
using MSTask = Microsoft.Build.Logging.StructuredLogger.Task;

static class BuildGraphFactory
{
    public static BuildGraph FromBinlog(Build build)
    {
        // Discover solution root as common ancestor of all project directories
        var projectDirs = build.FindChildrenRecursive<Project>()
            .Where(p => p.ProjectFile != null)
            .Select(p => Path.GetDirectoryName(Path.GetFullPath(p.ProjectFile))!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var rootDir = GetCommonAncestor(projectDirs);

        var graph = new BuildGraph { RootDir = rootDir };
        int cmdIndex = 0;

        // Extract environment variables from SetEnv tasks (per-project).
        // These include PATH, INCLUDE, LIB, etc. needed to replay CL/LINK.
        // Also extract CAExcludePath for mtime-skip prefixes.
        foreach (var task in build.FindChildrenRecursive<MSTask>(t => t.Name == "SetEnv"))
        {
            var pf = task.Children.OfType<Folder>().FirstOrDefault(f => f.Name == "Parameters");
            var name = pf?.FindChildrenRecursive<Property>(p => p.Name == "Name")
                .FirstOrDefault()?.Value;
            if (string.IsNullOrEmpty(name)) continue;
            var value = pf?.FindChildrenRecursive<Property>(p => p.Name == "Value")
                .FirstOrDefault()?.Value ?? "";
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
            var pf = task.Children.OfType<Folder>().FirstOrDefault(f => f.Name == "Parameters");
            if (pf == null) continue;

            // Normalize tool names at the boundary — MSBuild may report any casing
            // (e.g. "LIB" vs "Lib", "Link" vs "LINK"). Canonical form is uppercase.
            var toolName = task.Name.ToUpperInvariant();
            // Extract the full command line from binlog (available for CL, Link, Lib, MIDL)
            // CommandLineArguments is a child of the task, not inside the Parameters folder.
            var cmdLineRaw = (task.FindChildrenRecursive<Property>(p => p.Name == "CommandLineArguments")
                .FirstOrDefault()?.Value ?? "").ReplaceLineEndings(" ").Trim();
            var sources = pf.Children.OfType<Parameter>()
                .FirstOrDefault(p => p.Name == "Sources")
                ?.Children.OfType<Item>()
                .Select(i => graph.ToRelative(ResolveAbsolute(projDir, i.Text)))
                .ToList() ?? [];

            // MIDL uses a Source property (singular), not Sources items
            if (sources.Count == 0 && toolName != "MIDL") continue;

            if (toolName == "CL")
            {
                // CL batches N sources but the relationship is 1:1 (each .cpp → its .obj).
                // Split into individual commands to keep the graph accurate.
                var objFileName = pf.FindChildrenRecursive<Property>(p => p.Name == "ObjectFileName")
                    .FirstOrDefault()?.Value ?? "";
                var absObjFileName = ResolveAbsolute(projDir, objFileName);
                // ObjectFileName ending with \ or / is a directory; otherwise it's a specific file.
                bool objIsDir = objFileName.Length == 0
                    || objFileName[^1] is '\\' or '/';
                if (objIsDir) objDirsByProject.TryAdd(proj, absObjFileName);

                // PCH detection: /Yc = create, /Yu = use
                var pchOutFile = pf.FindChildrenRecursive<Property>(p => p.Name == "PrecompiledHeaderOutputFile")
                    .FirstOrDefault()?.Value ?? "";
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
                        graph.Files.TryAdd(pchPath, new FileNode(pchPath, FileKind.Intermediate));
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
                var srcProp = pf.FindChildrenRecursive<Property>(p => p.Name == "Source")
                    .FirstOrDefault()?.Value ?? "";
                if (string.IsNullOrEmpty(srcProp)) continue;
                var src = graph.ToRelative(ResolveAbsolute(projDir, srcProp));

                var metaProp = pf.FindChildrenRecursive<Property>(p => p.Name == "MetadataFileName")
                    .FirstOrDefault()?.Value ?? "";
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
                var genWinMD = pf.FindChildrenRecursive<Property>(p => p.Name == "GenerateWindowsMetadata")
                    .FirstOrDefault()?.Value ?? "";
                if (genWinMD.Equals("Only", StringComparison.OrdinalIgnoreCase)) continue;

                var outFile = pf.FindChildrenRecursive<Property>(p => p.Name == "OutputFile")
                    .FirstOrDefault()?.Value ?? "";
                outFile = string.IsNullOrEmpty(outFile) ? ""
                    : graph.ToRelative(ResolveAbsolute(projDir, outFile));
                if (string.IsNullOrEmpty(outFile)) continue;

                var cmdId = $"{toolName}#{cmdIndex++}:{proj}/{target}";
                var cmd = new CommandNode(cmdId, toolName, proj, target, sources, [outFile], cmdLineRaw, projDir);
                graph.Commands[cmdId] = cmd;
                graph.Files.TryAdd(outFile, new FileNode(outFile, FileKinds.Classify(outFile)));
                graph.FileToProducer[outFile] = cmdId;

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
            foreach (var item in addItem.Children.OfType<Item>())
            {
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

                foreach (var item in addItem.Children.OfType<Item>())
                {
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

        // CompileXaml: .xaml → generated .xaml.g.h, .g.cpp, .xbf
        foreach (var task in build.FindChildrenRecursive<MSTask>(t => t.Name == "CompileXaml"))
        {
            var projNode = task.GetNearestParent<Project>();
            var proj = projNode?.Name ?? "unknown";
            var projDir = projNode?.ProjectFile != null
                ? Path.GetDirectoryName(Path.GetFullPath(projNode.ProjectFile)) ?? ""
                : "";
            var target = task.GetNearestParent<Target>()?.Name ?? "unknown";
            var pf = task.Children.OfType<Folder>().FirstOrDefault(f => f.Name == "Parameters");
            if (pf == null) continue;

            // Gather input .xaml files
            var xamlInputs = new List<string>();
            foreach (var paramName in new[] { "XamlPages", "XamlApplications" })
            {
                var items = pf.Children.OfType<Parameter>()
                    .FirstOrDefault(p => p.Name == paramName)
                    ?.Children.OfType<Item>()
                    .Select(i => graph.ToRelative(ResolveAbsolute(projDir, i.Text)));
                if (items != null) xamlInputs.AddRange(items);
            }
            if (xamlInputs.Count == 0) continue;

            // Gather output files from OutputItems
            var of = task.Children.OfType<Folder>().FirstOrDefault(f => f.Name == "OutputItems");
            var allOutputs = new List<string>();
            if (of != null)
            {
                foreach (var paramName in new[] { "_GeneratedCodeFiles", "_GeneratedXbfFiles" })
                {
                    var items = of.Children.OfType<NamedNode>()
                        .Where(n => n.Name == paramName)
                        .SelectMany(n => n.Children.OfType<Item>())
                        .Select(i => graph.ToRelative(ResolveAbsolute(projDir, i.Text)));
                    allOutputs.AddRange(items);
                }
            }
            if (allOutputs.Count == 0) continue;

            // Match each .xaml to its outputs by stem
            var sharedOutputs = new List<string>(allOutputs);
            foreach (var xaml in xamlInputs)
            {
                var stem = Path.GetFileNameWithoutExtension(xaml);
                var fullStem = Path.GetFileName(xaml);
                var matched = allOutputs
                    .Where(o => {
                        var fn = Path.GetFileName(o);
                        return fn.StartsWith(fullStem + ".", StringComparison.OrdinalIgnoreCase)
                            || fn.StartsWith(stem + ".xbf", StringComparison.OrdinalIgnoreCase);
                    }).ToList();
                if (matched.Count == 0) continue;

                foreach (var m in matched) sharedOutputs.Remove(m);

                var cmdId = $"CompileXaml#{cmdIndex++}:{proj}/{target}";
                var cmd = new CommandNode(cmdId, "CompileXaml", proj, target, [xaml], matched);
                graph.Commands[cmdId] = cmd;
                graph.Files.TryAdd(xaml, new FileNode(xaml, FileKinds.Classify(xaml)));
                graph.AddConsumer(xaml, cmdId);
                foreach (var output in matched)
                {
                    graph.Files.TryAdd(output, new FileNode(output, FileKinds.Classify(output)));
                    graph.FileToProducer.TryAdd(output, cmdId);
                }
            }

            // Shared outputs (XamlTypeInfo.g.cpp, XamlLibMetadataProvider.g.cpp, etc.)
            if (sharedOutputs.Count > 0)
            {
                var cmdId = $"CompileXaml#{cmdIndex++}:{proj}/{target}";
                var cmd = new CommandNode(cmdId, "CompileXaml", proj, target, xamlInputs, sharedOutputs);
                graph.Commands[cmdId] = cmd;
                foreach (var input in xamlInputs)
                {
                    graph.Files.TryAdd(input, new FileNode(input, FileKinds.Classify(input)));
                    graph.AddConsumer(input, cmdId);
                }
                foreach (var output in sharedOutputs)
                {
                    graph.Files.TryAdd(output, new FileNode(output, FileKinds.Classify(output)));
                    graph.FileToProducer.TryAdd(output, cmdId);
                }
            }
        }

        // Convention: cppwinrt generates .g.h/.g.cpp from .winmd produced by MIDL.
        foreach (var cmd in graph.Commands.Values.Where(c => c.Tool == "MIDL").ToList())
        {
            var projDir2 = build.FindChildrenRecursive<Project>(p => p.Name == cmd.Project)
                .FirstOrDefault()?.ProjectFile;
            if (projDir2 == null) continue;
            var absDir = Path.GetDirectoryName(Path.GetFullPath(projDir2)) ?? "";

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
            var pf4 = priTask.Children.OfType<Folder>().FirstOrDefault(f => f.Name == "Parameters");
            var outFile = pf4?.FindChildrenRecursive<Property>(p => p.Name == "OutputFileName")
                .FirstOrDefault()?.Value;
            if (outFile == null) continue;

            var priRel = graph.ToRelative(ResolveAbsolute(projDir4, outFile));
            var target4 = priTask.GetNearestParent<Target>()?.Name ?? "unknown";
            var cmdId = $"makepri#{cmdIndex++}:{proj4}/{target4}";
            var priCmdLine = (priTask.FindChildrenRecursive<Property>(
                p => p.Name == "CommandLineArguments").FirstOrDefault()?.Value ?? "")
                .ReplaceLineEndings(" ").Trim();
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
            var pf5 = manTask.Children.OfType<Folder>().FirstOrDefault(f => f.Name == "Parameters");
            if (pf5 == null) continue;

            var inputItems = pf5.FindChildrenRecursive<Parameter>(p => p.Name == "AppxManifestInput")
                .FirstOrDefault()?.Children.OfType<Item>()
                .Select(i => graph.ToRelative(ResolveAbsolute(projDir5, i.Text)))
                .ToList() ?? [];
            var outFile = pf5.FindChildrenRecursive<Property>(p => p.Name == "AppxManifestOutput")
                .FirstOrDefault()?.Value;
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
            var pf7 = copyTask.Children.OfType<Folder>().FirstOrDefault(f => f.Name == "Parameters");
            if (pf7 == null) continue;

            var srcItems = pf7.FindChildrenRecursive<Parameter>(p => p.Name == "SourceFiles")
                .FirstOrDefault()?.Children.OfType<Item>()
                .Select(i => i.Text).ToList();
            var dstItems = pf7.FindChildrenRecursive<Parameter>(p => p.Name == "DestinationFiles")
                .FirstOrDefault()?.Children.OfType<Item>()
                .Select(i => i.Text).ToList();

            if (srcItems == null || srcItems.Count == 0)
            {
                var srcProp = pf7.FindChildrenRecursive<Property>(p => p.Name == "SourceFiles")
                    .FirstOrDefault()?.Value;
                var dstProp = pf7.FindChildrenRecursive<Property>(p => p.Name == "DestinationFiles")
                    .FirstOrDefault()?.Value;
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
                var copyCmd = $"cmd /c copy /Y \"{absSrc}\" \"{absDst}\"";
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
            var pf6 = recipeTask.Children.OfType<Folder>().FirstOrDefault(f => f.Name == "Parameters");
            if (pf6 == null) continue;

            var recipeFile = pf6.FindChildrenRecursive<Property>(p => p.Name == "RecipeFile")
                .FirstOrDefault()?.Value;
            if (recipeFile == null) continue;

            var recipeRel = graph.ToRelative(ResolveAbsolute(projDir6, recipeFile));

            var inputs = new List<string>();
            var manifest = pf6.FindChildrenRecursive<Property>(p => p.Name == "AppxManifestXml")
                .FirstOrDefault()?.Value;
            if (manifest != null)
            {
                var mRel = graph.ToRelative(ResolveAbsolute(projDir6, manifest));
                if (graph.Files.ContainsKey(mRel)) inputs.Add(mRel);
            }
            var payload = pf6.FindChildrenRecursive<Parameter>(p => p.Name == "PayloadFiles")
                .FirstOrDefault()?.Children.OfType<Item>().ToList() ?? [];
            foreach (var item in payload)
            {
                var rel = graph.ToRelative(ResolveAbsolute(projDir6, item.Text));
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
                    var cmd = new CommandNode(cmdId, "#include", "", "", [headerRel], [currentSourceRel]);
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
        var flags = parts.Take(parts.Count - sourceCount);
        return string.Join(" ", flags) + $" /Fo\"{absObj}\" \"{absSource}\"";
    }

    /// Crude command-line splitter that respects double quotes.
    static List<string> SplitCommandLine(string cmdLine)
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
}
