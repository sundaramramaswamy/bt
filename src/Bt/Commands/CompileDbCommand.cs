using System.Text.Json;

static class CompileDbCommand
{
    public static int CompileCommands(BuildGraph g, string? outputPath)
    {
        var entries = g.Commands.Values
            .Where(c => c.Tool == "CL" && !string.IsNullOrEmpty(c.CommandLine))
            .Select(c =>
            {
                var file = c.Inputs.Count > 0
                    ? Path.GetFullPath(Path.Combine(g.RootDir, c.Inputs[0]))
                    : "";
                var dir = !string.IsNullOrEmpty(c.WorkingDir) ? c.WorkingDir : g.RootDir;
                return new CompileCommandEntry { Directory = dir, Command = c.CommandLine, File = file };
            })
            .ToList();

        var outFile = outputPath ?? Path.Combine(g.RootDir, "compile_commands.json");
        using var fs = File.Create(outFile);
        using var writer = new Utf8JsonWriter(fs, new JsonWriterOptions
        {
            Indented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        JsonSerializer.Serialize(writer, entries, CompileDbJsonContext.Default.ListCompileCommandEntry);
        Console.Error.WriteLine($"Wrote {entries.Count} entries to {outFile}");
        return 0;
    }
}

class CompileCommandEntry
{
    public string Directory { get; set; } = "";
    public string Command { get; set; } = "";
    public string File { get; set; } = "";
}

[System.Text.Json.Serialization.JsonSerializableAttribute(typeof(List<CompileCommandEntry>))]
[System.Text.Json.Serialization.JsonSourceGenerationOptions(
    PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
partial class CompileDbJsonContext : System.Text.Json.Serialization.JsonSerializerContext { }
