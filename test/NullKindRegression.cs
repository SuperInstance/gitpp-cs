using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Gitpp;

/// <summary>
/// v0.4.6 regression: a record with kind:null must not throw.
///
/// Dictionary&lt;string,int&gt;.GetValueOrDefault(null, 0) throws
/// ArgumentNullException instead of returning the default, because a Dictionary
/// rejects a null key outright -- including through the CollectionExtensions
/// overload that is meant to be null-tolerant. Two kind:null records contesting a
/// key therefore crashed Derive() before the fix.
///
/// These pin spec 5.1 (v0.4.6): an unclassifiable record ranks lowest, two
/// unclassifiable records are a single class so 5.2's equal-class tie-break
/// (ancestry, then topo order) applies between them, and an unrecognised kind
/// string is accepted rather than malformed.
/// </summary>
static class NullKindRegression
{
    public static int Run()
    {
        var cases = new (string name, string json, Dictionary<string, object?> expected)[]
        {
            ("two unclassifiable contest a key",
             "[{\"id\":\"a\",\"kind\":null,\"parents\":[],\"effects\":{\"x\":1}},{\"id\":\"b\",\"kind\":null,\"parents\":[],\"effects\":{\"x\":2}}]",
             new Dictionary<string, object?> { ["x"] = 1 }),
            ("unclassifiable descendant of unclassifiable",
             "[{\"id\":\"a\",\"kind\":null,\"parents\":[],\"effects\":{\"x\":1}},{\"id\":\"b\",\"kind\":null,\"parents\":[\"a\"],\"effects\":{\"x\":2}}]",
             new Dictionary<string, object?> { ["x"] = 2 }),
            ("unrecognised kind string loses to witness",
             "[{\"id\":\"a\",\"kind\":\"banana\",\"parents\":[],\"effects\":{\"z\":1}},{\"id\":\"b\",\"kind\":\"witness\",\"parents\":[],\"effects\":{\"z\":2}}]",
             new Dictionary<string, object?> { ["z"] = 2 }),
            ("unclassifiable wins an uncontested key",
             "[{\"id\":\"a\",\"kind\":null,\"parents\":[],\"effects\":{\"q\":1}}]",
             new Dictionary<string, object?> { ["q"] = 1 }),
            ("override ancestor outranks task descendant",
             "[{\"id\":\"o\",\"kind\":\"override\",\"parents\":[],\"effects\":{\"x\":1}},{\"id\":\"t\",\"kind\":\"task\",\"parents\":[\"o\"],\"effects\":{\"x\":2}}]",
             new Dictionary<string, object?> { ["x"] = 1 }),
            ("orphan's descendant still applies",
             "[{\"id\":\"orph\",\"kind\":\"task\",\"parents\":[\"ghost\"],\"effects\":{\"x\":1}},{\"id\":\"kid\",\"kind\":\"task\",\"parents\":[\"orph\"],\"effects\":{\"y\":2}}]",
             new Dictionary<string, object?> { ["y"] = 2 }),
        };

        int fails = 0;
        foreach (var c in cases)
        {
            try
            {
                var recs = JsonSerializer.Deserialize<List<Gitpp.Record>>(c.json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                var got = Gitpp.Fold.Derive(recs).State;
                // Effects values arrive as JsonElement, so compare by their
                // serialised form: JsonElement.Equals(1) is false even when the
                // underlying number is 1, which silently failed every case the
                // first time this test was written.
                bool ok = got.Count == c.expected.Count
                    && c.expected.All(kv => got.TryGetValue(kv.Key, out var v)
                        && JsonSerializer.Serialize(v) == JsonSerializer.Serialize(kv.Value));
                Console.Error.WriteLine($"  {(ok ? "PASS" : "FAIL")}  v0.4.6: {c.name}");
                if (!ok) fails++;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"  FAIL  v0.4.6: {c.name}  THREW {e.GetType().Name}: {e.Message}");
                fails++;
            }
        }
        return fails;
    }
}
