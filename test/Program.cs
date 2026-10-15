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
            var (root, closure) = ClosureRoot(records, at);
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

    static (string? root, List<string> closure) ClosureRoot(List<Record> records, string at)
    {
        var byId = records.Where(r => !string.IsNullOrEmpty(r.Id)).ToDictionary(r => r.Id);
        if (!byId.ContainsKey(at)) return (null, new List<string>());

        var seen = new HashSet<string>();
        var stack = new Stack<string>();
        stack.Push(at);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (seen.Contains(cur)) continue;
            if (!byId.ContainsKey(cur)) return (null, new List<string>());
            seen.Add(cur);
            foreach (var p in byId[cur].Parents)
            {
                if (!seen.Contains(p)) stack.Push(p);
            }
        }

        var sorted = seen.OrderBy(x => string.Concat(x.Select(c => ((int)c).ToString("D5")))).ToList();

        var lines = new List<string>();
        foreach (var id in sorted)
        {
            var leaf = RecordLeaf(byId[id]);
            lines.Add($"{id}:{leaf}");
        }

        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", lines)));
        return (BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant(), sorted);
    }

    static string RecordLeaf(Record r)
    {
        var parents = r.Parents.OrderBy(x => string.Concat(x.Select(c => ((int)c).ToString("D5")))).ToList();
        var canon = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["effects"] = r.Effects,
            ["id"] = r.Id,
            ["kind"] = r.Kind,
            ["parents"] = parents,
            ["reason"] = r.Reason,
        };
        var json = CanonicalJson(canon);
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(json));
        return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
    }

    static string CanonicalJson(object? obj)
    {
        return EmitValue(obj, ", ", ": ");
    }

    static string EmitValue(object? v, string sep, string kvSep)
    {
        if (v == null) return "null";
        if (v is JsonElement je) return EmitJsonElement(je, sep, kvSep);
        if (v is bool b) return b ? "true" : "false";
        if (v is int i) return i.ToString();
        if (v is long l) return l.ToString();
        if (v is double d) return d.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        if (v is string str) return JsonStringEncode(str);
        if (v is Dictionary<string, object?> dict)
        {
            var keys = dict.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
            if (keys.Count == 0) return "{}";
            return "{" + string.Join(sep, keys.Select(k => JsonStringEncode(k) + kvSep + EmitValue(dict[k], sep, kvSep))) + "}";
        }
        if (v is SortedDictionary<string, object?> sdict)
        {
            var keys = sdict.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
            if (keys.Count == 0) return "{}";
            return "{" + string.Join(sep, keys.Select(k => JsonStringEncode(k) + kvSep + EmitValue(sdict[k], sep, kvSep))) + "}";
        }
        if (v is List<object?> list)
        {
            if (list.Count == 0) return "[]";
            return "[" + string.Join(sep, list.Select(x => EmitValue(x, sep, kvSep))) + "]";
        }
        if (v is System.Collections.IEnumerable enumerable)
        {
            var items = enumerable.Cast<object?>().ToList();
            if (items.Count == 0) return "[]";
            return "[" + string.Join(sep, items.Select(x => EmitValue(x, sep, kvSep))) + "]";
        }
        return JsonSerializer.Serialize(v);
    }

    static string EmitJsonElement(JsonElement je, string sep, string kvSep)
    {
        switch (je.ValueKind)
        {
            case JsonValueKind.Null:
                return "null";
            case JsonValueKind.True:
                return "true";
            case JsonValueKind.False:
                return "false";
            case JsonValueKind.Number:
                return je.GetRawText();
            case JsonValueKind.String:
                return JsonStringEncode(je.GetString() ?? "");
            case JsonValueKind.Array:
                return "[" + string.Join(sep, je.EnumerateArray().Select(x => EmitJsonElement(x, sep, kvSep))) + "]";
            case JsonValueKind.Object:
                var props = new List<JsonProperty>();
                foreach (var p in je.EnumerateObject()) props.Add(p);
                props.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                return "{" + string.Join(sep, props.Select(p => JsonStringEncode(p.Name) + kvSep + EmitJsonElement(p.Value, sep, kvSep))) + "}";
            default:
                return je.GetRawText();
        }
    }

    static string JsonStringEncode(string str)
    {
        // JSON string encoding without \\uXXXX escape for non-ASCII (spec §2.5)
        var sb = new StringBuilder();
        sb.Append('"');
        foreach (var c in str)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.AppendFormat("\\u{0:x4}", (int)c);
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
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
