using Bt.Cache;
using FlatSharp;

/// FlatBuffer-based serialization of the build graph cache.
/// Uses a sorted string table with integer indices for compact storage.
static class GraphCache
{
    const int CacheVersion = 2;
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

        var strings = stringSet.ToArray();
        var indexOf = new Dictionary<string, int>(strings.Length, StringComparer.Ordinal);
        for (int i = 0; i < strings.Length; i++) indexOf[strings[i]] = i;

        var fb = new GraphFb
        {
            Version = CacheVersion,
            BinlogTimestamp = binlogStamp.Ticks,
            RootDir = graph.RootDir,
            Strings = strings,
            Files = graph.Files.Values.Select(f => new FileFb
            {
                StrIdx = indexOf[f.Path],
                Kind = (byte)f.Kind
            }).ToList(),
            Commands = graph.Commands.Values.Select(c => new CommandFb
            {
                IdIdx = indexOf[c.Id],
                ToolIdx = indexOf[c.Tool],
                ProjectIdx = indexOf[c.Project],
                TargetIdx = indexOf[c.Target],
                InputIndices = c.Inputs.Select(i => indexOf[i]).ToArray(),
                OutputIndices = c.Outputs.Select(o => indexOf[o]).ToArray(),
                CmdlineIdx = indexOf[c.CommandLine],
                WorkdirIdx = indexOf[c.WorkingDir],
            }).ToList(),
            ExternalPrefixIndices = graph.ExternalPrefixes
                .Select(p => indexOf[p]).ToArray(),
            ProjectEnvs = graph.ProjectEnv.Select(kv => new ProjectEnvFb
            {
                ProjectIdx = indexOf[kv.Key],
                VarNameIndices = kv.Value.Keys.Select(k => indexOf[k]).ToArray(),
                VarValueIndices = kv.Value.Values.Select(v => indexOf[v]).ToArray(),
            }).ToList(),
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
        if (fb.Strings is not { Count: > 0 } strings) return null;
        if (fb.Files is not { } fbFiles) return null;
        if (fb.Commands is not { } fbCommands) return null;

        var graph = new BuildGraph { RootDir = fb.RootDir ?? "" };

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
            var inputs = c.InputIndices is { } ii
                ? ii.Select(i => strings[i]).ToList()
                : new List<string>();
            var outputs = c.OutputIndices is { } oi
                ? oi.Select(i => strings[i]).ToList()
                : new List<string>();

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
                graph.FileToProducer.TryAdd(output, cmd.Id);
                if (cmd.Tool.StartsWith("#"))
                {
                    if (!graph.SyntheticProducers.TryGetValue(output, out var spList))
                    {
                        spList = [];
                        graph.SyntheticProducers[output] = spList;
                    }
                    spList.Add(cmd.Id);
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

        return (fb.BinlogTimestamp, graph);
    }
}
