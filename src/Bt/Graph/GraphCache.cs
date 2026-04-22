using Bt.Cache;
using FlatSharp;

/// FlatBuffer-based serialization of the build graph cache.
/// Uses a sorted string table with integer indices for compact storage.
static class GraphCache
{
    const int CacheVersion = 5;
    static readonly ISerializer<GraphFb> Serializer = GraphFb.Serializer;

    public static void Save(string path, BuildGraph graph, DateTime binlogStamp)
    {
        // Build string table: collect all unique strings, sorted
        var stringSet = new SortedSet<string>(StringComparer.Ordinal) { "" };
        foreach (var f in graph.Files.Values)
            stringSet.Add(f.Path);
        foreach (var c in graph.Commands.Values)
        {
            stringSet.Add(c.Id);
            stringSet.Add(c.Tool);
            stringSet.Add(c.Project);
            stringSet.Add(c.Target);
            stringSet.Add(c.CommandLine);
            stringSet.Add(c.WorkingDir);
            foreach (var i in c.Inputs) stringSet.Add(i);
            foreach (var o in c.Outputs) stringSet.Add(o);
        }
        foreach (var p in graph.ExternalPrefixes)
            stringSet.Add(p);
        foreach (var (proj, envMap) in graph.ProjectEnv)
        {
            stringSet.Add(proj);
            foreach (var (k, v) in envMap) { stringSet.Add(k); stringSet.Add(v); }
        }
        foreach (var (k, v) in graph.GlobalEnv) { stringSet.Add(k); stringSet.Add(v); }

        var strings = new string[stringSet.Count];
        stringSet.CopyTo(strings);
        var indexOf = new Dictionary<string, int>(strings.Length, StringComparer.Ordinal);
        for (int i = 0; i < strings.Length; i++) indexOf[strings[i]] = i;

        var fbFiles = new List<FileFb>(graph.Files.Count);
        foreach (var f in graph.Files.Values)
            fbFiles.Add(new FileFb { StrIdx = indexOf[f.Path], Kind = (byte)f.Kind });

        var fbCommands = new List<CommandFb>(graph.Commands.Count);
        foreach (var c in graph.Commands.Values)
        {
            var inputIndices = new int[c.Inputs.Count];
            for (int i = 0; i < c.Inputs.Count; i++) inputIndices[i] = indexOf[c.Inputs[i]];
            var outputIndices = new int[c.Outputs.Count];
            for (int i = 0; i < c.Outputs.Count; i++) outputIndices[i] = indexOf[c.Outputs[i]];
            fbCommands.Add(new CommandFb
            {
                IdIdx = indexOf[c.Id],
                ToolIdx = indexOf[c.Tool],
                ProjectIdx = indexOf[c.Project],
                TargetIdx = indexOf[c.Target],
                InputIndices = inputIndices,
                OutputIndices = outputIndices,
                CmdlineIdx = indexOf[c.CommandLine],
                WorkdirIdx = indexOf[c.WorkingDir],
            });
        }

        var extPfxIndices = new int[graph.ExternalPrefixes.Count];
        int extIdx = 0;
        foreach (var p in graph.ExternalPrefixes) extPfxIndices[extIdx++] = indexOf[p];

        var fbEnvs = new List<ProjectEnvFb>(graph.ProjectEnv.Count);
        foreach (var (proj, envMap) in graph.ProjectEnv)
        {
            var nameIndices = new int[envMap.Count];
            var valueIndices = new int[envMap.Count];
            int ei = 0;
            foreach (var (k, v) in envMap)
            {
                nameIndices[ei] = indexOf[k];
                valueIndices[ei] = indexOf[v];
                ei++;
            }
            fbEnvs.Add(new ProjectEnvFb
            {
                ProjectIdx = indexOf[proj],
                VarNameIndices = nameIndices,
                VarValueIndices = valueIndices,
            });
        }

        var fb = new GraphFb
        {
            Version = CacheVersion,
            BinlogTimestamp = binlogStamp.Ticks,
            RootDir = graph.RootDir,
            Strings = strings,
            Files = fbFiles,
            Commands = fbCommands,
            ExternalPrefixIndices = extPfxIndices,
            ProjectEnvs = fbEnvs,
            GlobalEnvNameIndices = BuildEnvIndices(graph.GlobalEnv, indexOf, name: true),
            GlobalEnvValueIndices = BuildEnvIndices(graph.GlobalEnv, indexOf, name: false),
        };

        int maxSize = Serializer.GetMaxSize(fb);
        byte[] buffer = new byte[maxSize];
        int written = Serializer.Write(buffer, fb);
        using var fs = File.Create(path);
        fs.Write(buffer, 0, written);
    }

    /// Returns (binlogTimestamp, graph) or null if the file can't be read.
    public static (long BinlogTimestamp, BuildGraph Graph)? Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var fb = Serializer.Parse(bytes);

        if (fb.Version != CacheVersion) return null;
        if (fb.Strings is not { Count: > 0 } fbStrings) return null;
        if (fb.Files is not { } fbFiles) return null;
        if (fb.Commands is not { } fbCommands) return null;

        // Materialize the string table once so repeated index lookups
        // return the same object — free interning via the cache array.
        var strings = new string[fbStrings.Count];
        for (int i = 0; i < fbStrings.Count; i++)
            strings[i] = fbStrings[i];

        var graph = new BuildGraph
        {
            RootDir = fb.RootDir ?? "",
            Files = new(fbFiles.Count, StringComparer.OrdinalIgnoreCase),
            Commands = new(fbCommands.Count),
            FileToConsumers = new(fbFiles.Count, StringComparer.OrdinalIgnoreCase),
            FileToProducer = new(fbFiles.Count, StringComparer.OrdinalIgnoreCase),
        };

        // Rebuild ExternalPrefixes
        if (fb.ExternalPrefixIndices is { } extPfx)
            foreach (var idx in extPfx)
                graph.ExternalPrefixes.Add(strings[idx]);

        // Rebuild Files dictionary
        foreach (var f in fbFiles)
        {
            var p = strings[f.StrIdx];
            graph.Files.TryAdd(p, new FileNode(p, (FileKind)f.Kind));
        }

        // Rebuild Commands and relationship maps
        foreach (var c in fbCommands)
        {
            var ii = c.InputIndices;
            var inputs = new List<string>(ii?.Count ?? 0);
            if (ii != null)
                for (int i = 0; i < ii.Count; i++)
                    inputs.Add(strings[ii[i]]);

            var oi = c.OutputIndices;
            var outputs = new List<string>(oi?.Count ?? 0);
            if (oi != null)
                for (int i = 0; i < oi.Count; i++)
                    outputs.Add(strings[oi[i]]);

            var cmd = new CommandNode(
                strings[c.IdIdx], strings[c.ToolIdx],
                strings[c.ProjectIdx], strings[c.TargetIdx],
                inputs, outputs,
                strings[c.CmdlineIdx], strings[c.WorkdirIdx]);

            graph.Commands[cmd.Id] = cmd;
            foreach (var input in cmd.Inputs)
                graph.AddConsumer(input, cmd.Id);
            foreach (var output in cmd.Outputs)
            {
                if (cmd.Tool.StartsWith("#"))
                {
                    if (!graph.SyntheticProducers.TryGetValue(output, out var spList))
                    {
                        spList = [];
                        graph.SyntheticProducers[output] = spList;
                    }
                    spList.Add(cmd.Id);
                }
                else
                {
                    graph.FileToProducer.TryAdd(output, cmd.Id);
                }
            }
        }

        // Rebuild ProjectEnv
        if (fb.ProjectEnvs is { } envs)
            foreach (var pe in envs)
            {
                var proj = strings[pe.ProjectIdx];
                var names = pe.VarNameIndices;
                var values = pe.VarValueIndices;
                if (names is null || values is null || names.Count != values.Count) continue;
                var envMap = new Dictionary<string, string>(names.Count, StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < names.Count; i++)
                    envMap[strings[names[i]]] = strings[values[i]];
                graph.ProjectEnv[proj] = envMap;
            }

        // Rebuild GlobalEnv
        if (fb.GlobalEnvNameIndices is { } gNames && fb.GlobalEnvValueIndices is { } gValues
            && gNames.Count == gValues.Count)
            for (int i = 0; i < gNames.Count; i++)
                graph.GlobalEnv[strings[gNames[i]]] = strings[gValues[i]];

        return (fb.BinlogTimestamp, graph);
    }

    static int[] BuildEnvIndices(Dictionary<string, string> env, Dictionary<string, int> indexOf, bool name)
    {
        var indices = new int[env.Count];
        int i = 0;
        foreach (var (k, v) in env)
            indices[i++] = indexOf[name ? k : v];
        return indices;
    }
}
