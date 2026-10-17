// test/CycleOrphansTests.cs -- spec §5.5 orphan regression.
//
// The 13 cases below are the discriminating set from SuperInstance/gitpp-reading-room
// (readers/py/cyclefix.py, CASES). No single natural rule passes all of them:
//
//   rule A  "any parent missing from the record set"  correct on cycle members,
//                                                       WRONG on cycle descendants
//   rule B  "anything Kahn never emitted"               correct on cycle descendants,
//                                                       WRONG on missing-parent descendants
//   rule U3 three clauses                               correct on both
//
// The three load-bearing cases:
//
//   "desc of missing"    orphans must be ["orph"] ONLY. The kid's parent EXISTS
//                       (orphaned, not absent), so the kid is orderable and is
//                       NOT an orphan. Rule B regresses this.
//   "desc of 2-cycle"   orphans must be ["ca","cb","down"]. Rule A misses "down",
//                       whose parent exists but can never be topologically ordered.
//   "missing AND cycle" orphans must be ["ca","cb","m"]. Rule A returns ["m"],
//                       rule B returns ["ca","cb"]. Neither single rule passes.
//
// Records are fed in as JSON and deserialized exactly the way Program.cs does,
// so this exercises the real input path rather than a hand-built Record graph.
//
// Number normalization in Canon() folds integral doubles to their integer form
// (1.0 -> "1"). That is a comparison convenience, not a fold change: the fold's
// own signed-zero encoding is a separate matter (see Canon()).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gitpp;

internal static class CycleOrphansTests
{
    private static readonly JsonSerializerOptions InputOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static int _pass;
    private static int _fail;

    private static List<Record> Parse(string json) =>
        JsonSerializer.Deserialize<List<Record>>(json, InputOptions) ?? new List<Record>();

    /// Canonical, key-sorted JSON with integral doubles folded to integers so
    /// that a value's numeric spelling never decides a pass or a fail.
    private static string Canon(JsonNode? n)
    {
        if (n is null) return "null";
        if (n is JsonArray a) return "[" + string.Join(",", a.Select(Canon)) + "]";
        if (n is JsonObject o)
        {
            var props = o.Select(p => JsonSerializer.Serialize(p.Key) + ":" + Canon(p.Value))
                          .OrderBy(s => s, StringComparer.Ordinal);
            return "{" + string.Join(",", props) + "}";
        }
        var v = (JsonValue)n;
        if (v.TryGetValue<bool>(out var b)) return b ? "true" : "false";
        if (v.TryGetValue<string>(out var s)) return JsonSerializer.Serialize(s);
        if (v.TryGetValue<double>(out var d))
        {
            var ic = CultureInfo.InvariantCulture;
            if (double.IsFinite(d) && d == Math.Floor(d) && Math.Abs(d) < 1e16)
                return ((long)d).ToString(ic);
            return d.ToString("R", ic);
        }
        return v.ToJsonString();
    }

    private static void Case(string name, string recordsJson, string[] expectOrphans, string expectStateJson)
    {
        var problems = new List<string>();
        Fold.DerivedResult d;
        try
        {
            d = Fold.Derive(Parse(recordsJson));
        }
        catch (Exception e)
        {
            problems.Add("threw " + e.GetType().Name + ": " + e.Message);
            Report(name, problems);
            return;
        }

        var gotOrphans = d.Orphans.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        if (!gotOrphans.SequenceEqual(expectOrphans))
            problems.Add("orphans: got [" + string.Join(",", gotOrphans) +
                         "] want [" + string.Join(",", expectOrphans) + "]");

        var gotState = Canon(JsonSerializer.SerializeToNode(d.State));
        var wantState = Canon(JsonNode.Parse(expectStateJson));
        if (gotState != wantState)
            problems.Add("state: got " + gotState + " want " + wantState);

        Report(name, problems);
    }

    private static void Report(string name, List<string> problems)
    {
        if (problems.Count == 0)
        {
            _pass++;
            Console.WriteLine("  ok   " + name);
        }
        else
        {
            _fail++;
            Console.WriteLine("  FAIL " + name);
            foreach (var p in problems) Console.WriteLine("         " + p);
        }
    }

    public static int Run()
    {
        Console.WriteLine("spec §5.5 cycle-orphan regression (13 discriminating cases + counterexample)");

        Case("clean chain",
            @"[{""id"":""a"",""kind"":""task"",""parents"":[],""effects"":{},""reason"":null},{""id"":""b"",""kind"":""task"",""parents"":[""a""],""effects"":{""x"":1},""reason"":null}]",
            new string[] {  },
            @"{""x"":1}");
        Case("missing parent",
            @"[{""id"":""a"",""kind"":""task"",""parents"":[""ghost""],""effects"":{""x"":1},""reason"":null}]",
            new string[] { "a" },
            @"{}");
        Case("desc of missing",
            @"[{""id"":""orph"",""kind"":""task"",""parents"":[""ghost""],""effects"":{""x"":1},""reason"":null},{""id"":""kid"",""kind"":""task"",""parents"":[""orph""],""effects"":{""y"":2},""reason"":null}]",
            new string[] { "orph" },
            @"{""y"":2}");
        Case("2-cycle",
            @"[{""id"":""ca"",""kind"":""task"",""parents"":[""cb""],""effects"":{},""reason"":null},{""id"":""cb"",""kind"":""task"",""parents"":[""ca""],""effects"":{},""reason"":null}]",
            new string[] { "ca", "cb" },
            @"{}");
        Case("desc of 2-cycle",
            @"[{""id"":""ca"",""kind"":""task"",""parents"":[""cb""],""effects"":{},""reason"":null},{""id"":""cb"",""kind"":""task"",""parents"":[""ca""],""effects"":{},""reason"":null},{""id"":""down"",""kind"":""task"",""parents"":[""cb""],""effects"":{""y"":3},""reason"":null}]",
            new string[] { "ca", "cb", "down" },
            @"{}");
        Case("desc^2 of 2-cycle",
            @"[{""id"":""ca"",""kind"":""task"",""parents"":[""cb""],""effects"":{},""reason"":null},{""id"":""cb"",""kind"":""task"",""parents"":[""ca""],""effects"":{},""reason"":null},{""id"":""d1"",""kind"":""task"",""parents"":[""cb""],""effects"":{},""reason"":null},{""id"":""d2"",""kind"":""task"",""parents"":[""d1""],""effects"":{""y"":4},""reason"":null}]",
            new string[] { "ca", "cb", "d1", "d2" },
            @"{}");
        Case("cycle + root",
            @"[{""id"":""ca"",""kind"":""task"",""parents"":[""cb""],""effects"":{},""reason"":null},{""id"":""cb"",""kind"":""task"",""parents"":[""ca""],""effects"":{},""reason"":null},{""id"":""ok"",""kind"":""task"",""parents"":[],""effects"":{""z"":5},""reason"":null}]",
            new string[] { "ca", "cb" },
            @"{""z"":5}");
        Case("two cycles",
            @"[{""id"":""a"",""kind"":""task"",""parents"":[""b""],""effects"":{},""reason"":null},{""id"":""b"",""kind"":""task"",""parents"":[""a""],""effects"":{},""reason"":null},{""id"":""c"",""kind"":""task"",""parents"":[""d""],""effects"":{},""reason"":null},{""id"":""d"",""kind"":""task"",""parents"":[""c""],""effects"":{},""reason"":null}]",
            new string[] { "a", "b", "c", "d" },
            @"{}");
        Case("self-loop",
            @"[{""id"":""a"",""kind"":""task"",""parents"":[""a""],""effects"":{""x"":6},""reason"":null}]",
            new string[] { "a" },
            @"{}");
        Case("desc of self-loop",
            @"[{""id"":""a"",""kind"":""task"",""parents"":[""a""],""effects"":{},""reason"":null},{""id"":""k"",""kind"":""task"",""parents"":[""a""],""effects"":{""y"":7},""reason"":null}]",
            new string[] { "a", "k" },
            @"{}");
        Case("diamond",
            @"[{""id"":""r"",""kind"":""task"",""parents"":[],""effects"":{},""reason"":null},{""id"":""l"",""kind"":""task"",""parents"":[""r""],""effects"":{},""reason"":null},{""id"":""m"",""kind"":""task"",""parents"":[""r""],""effects"":{},""reason"":null},{""id"":""j"",""kind"":""task"",""parents"":[""l"",""m""],""effects"":{""x"":8},""reason"":null}]",
            new string[] {  },
            @"{""x"":8}");
        Case("cycle + branch",
            @"[{""id"":""a"",""kind"":""task"",""parents"":[""b""],""effects"":{},""reason"":null},{""id"":""b"",""kind"":""task"",""parents"":[""a""],""effects"":{},""reason"":null},{""id"":""t"",""kind"":""task"",""parents"":[""r""],""effects"":{},""reason"":null},{""id"":""r"",""kind"":""task"",""parents"":[""t""],""effects"":{},""reason"":null},{""id"":""leaf"",""kind"":""task"",""parents"":[""r""],""effects"":{""x"":9},""reason"":null}]",
            new string[] { "a", "b", "leaf", "r", "t" },
            @"{}");
        Case("missing AND cycle",
            @"[{""id"":""ca"",""kind"":""task"",""parents"":[""cb""],""effects"":{},""reason"":null},{""id"":""cb"",""kind"":""task"",""parents"":[""ca""],""effects"":{},""reason"":null},{""id"":""m"",""kind"":""task"",""parents"":[""ghost""],""effects"":{""q"":1},""reason"":null},{""id"":""mk"",""kind"":""task"",""parents"":[""m""],""effects"":{""w"":2},""reason"":null}]",
            new string[] { "ca", "cb", "m" },
            @"{""w"":2}");

        // The counterexample from the spec §5.5 discussion, spelled out on its own
        // so the regression is readable without the table above.
        Case("counterexample: cycle descendant",
            @"[{""id"":""ca"",""kind"":""task"",""parents"":[""cb""],""effects"":{""x"":""cycle-a""}},{""id"":""cb"",""kind"":""task"",""parents"":[""ca""],""effects"":{""x"":""cycle-b""}},{""id"":""down"",""kind"":""task"",""parents"":[""cb""],""effects"":{""y"":""downstream""}},{""id"":""ok"",""kind"":""task"",""parents"":[],""effects"":{""z"":""fine""}}]",
            new string[] { "ca", "cb", "down" },
            @"{""z"":""fine""}");

        Console.WriteLine();
        Console.WriteLine("  " + _pass + " pass, " + _fail + " fail (out of " + (_pass + _fail) + ")");
        return _fail == 0 ? 0 : 1;
    }
}
