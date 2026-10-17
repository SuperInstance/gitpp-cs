using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Gitpp;

class Program
{
    static async Task Main(string[] args)
    {
        // Spec \u00a75.5 orphan regression battery. No test framework in this
        // project (the runner is a console app), so it is a flag and an exit code.
        if (args.Length == 1 && args[0] == "--cycle-orphans")
        {
            Environment.Exit(CycleOrphansTests.Run());
            return;
        }

        string json;
        if (args.Length > 0 && args[0] != "-")
        {
            json = await File.ReadAllTextAsync(args[0]);
        }
        else
        {
            json = await Console.In.ReadToEndAsync();
        }

        var records = JsonSerializer.Deserialize<List<Record>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ?? new List<Record>();

        var d = Fold.Derive(records);

        string? at = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--at" && i + 1 < args.Length)
            {
                at = args[i + 1];
                break;
            }
        }

        if (at != null)
        {
            var (root, closure) = Fold.ClosureRoot(records, at);
            var output = new Output
            {
                State = d.State,
                Order = d.Order,
                Conflicts = d.Conflicts,
                Orphans = d.Orphans,
                Closure = closure,
                ClosureRoot = root,
            };
            Console.WriteLine(JsonSerializer.Serialize(output, new JsonSerializerOptions
            {
                WriteIndented = true,
            }));
        }
        else
        {
            Console.WriteLine(JsonSerializer.Serialize(d, new JsonSerializerOptions
            {
                WriteIndented = true,
            }));
        }
    }
}

class Output
{
    [JsonPropertyName("state")]
    public Dictionary<string, object?> State { get; set; } = new();
    [JsonPropertyName("order")]
    public List<string> Order { get; set; } = new();
    [JsonPropertyName("conflicts")]
    public List<Dictionary<string, object?>> Conflicts { get; set; } = new();
    [JsonPropertyName("orphans")]
    public List<string> Orphans { get; set; } = new();
    [JsonPropertyName("closure")]
    public List<string> Closure { get; set; } = new();
    [JsonPropertyName("closureRoot")]
    public string? ClosureRoot { get; set; }
}
