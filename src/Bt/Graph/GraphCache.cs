using System.IO.Compression;
using System.Text.Json;

/// Serializable representation of the build graph.
class GraphCache
{
    public long BinlogTimestamp { get; set; }
    public string RootDir { get; set; } = "";
    public List<CachedFile> Files { get; set; } = [];
    public List<CachedCommand> Commands { get; set; } = [];
    public List<string> ExternalPrefixes { get; set; } = [];

    public static void Save(string path, BuildGraph graph, DateTime binlogStamp)
    {
        var cache = new GraphCache
        {
            BinlogTimestamp = binlogStamp.Ticks,
            RootDir = graph.RootDir,
            ExternalPrefixes = graph.ExternalPrefixes.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(),
            Files = graph.Files.Values.Select(f => new CachedFile { Path = f.Path, Kind = (int)f.Kind }).ToList(),
            Commands = graph.Commands.Values.Select(c => new CachedCommand
            {
                Id = c.Id, Tool = c.Tool, Project = c.Project, Target = c.Target,
                Inputs = c.Inputs, Outputs = c.Outputs,
                CommandLine = c.CommandLine, WorkingDir = c.WorkingDir
            }).ToList()
        };
        using var fs = File.Create(path);
        using var gz = new GZipStream(fs, CompressionLevel.Optimal);
        JsonSerializer.Serialize(gz, cache, BtJsonContext.Default.GraphCache);
    }

    public static GraphCache? Load(string path)
    {
        using var fs = File.OpenRead(path);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        return JsonSerializer.Deserialize(gz, BtJsonContext.Default.GraphCache);
    }

    public BuildGraph ToGraph()
    {
        var graph = new BuildGraph { RootDir = RootDir };
        foreach (var p in ExternalPrefixes)
            graph.ExternalPrefixes.Add(p);
        foreach (var f in Files)
            graph.Files.TryAdd(f.Path, new FileNode(f.Path, (FileKind)f.Kind));
        foreach (var c in Commands)
        {
            var cmd = new CommandNode(c.Id, c.Tool, c.Project, c.Target, c.Inputs, c.Outputs, c.CommandLine, c.WorkingDir);
            graph.Commands[cmd.Id] = cmd;
            foreach (var input in cmd.Inputs)
                graph.AddConsumer(input, cmd.Id);
            foreach (var output in cmd.Outputs)
            {
                graph.FileToProducer.TryAdd(output, cmd.Id);
                // Rebuild SyntheticProducers index for #include commands
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
        return graph;
    }
}

class CachedFile
{
    public string Path { get; set; } = "";
    public int Kind { get; set; }
}

class CachedCommand
{
    public string Id { get; set; } = "";
    public string Tool { get; set; } = "";
    public string Project { get; set; } = "";
    public string Target { get; set; } = "";
    public List<string> Inputs { get; set; } = [];
    public List<string> Outputs { get; set; } = [];
    public string CommandLine { get; set; } = "";
    public string WorkingDir { get; set; } = "";
}

[System.Text.Json.Serialization.JsonSerializableAttribute(typeof(GraphCache))]
[System.Text.Json.Serialization.JsonSourceGenerationOptions(
    PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false)]
partial class BtJsonContext : System.Text.Json.Serialization.JsonSerializerContext { }
