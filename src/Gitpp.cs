using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gitpp
{
    /// <summary>
    /// A record in the git.pp fold. Three kinds: task, witness, override.
    /// </summary>
    public record Record
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = "";

        [JsonPropertyName("kind")]
        public string Kind { get; init; } = "task";

        [JsonPropertyName("parents")]
        public List<string> Parents { get; init; } = new();

        [JsonPropertyName("effects")]
        public Dictionary<string, object?> Effects { get; init; } = new();

        [JsonPropertyName("reason")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Reason { get; init; }
    }

    /// <summary>
    /// The fold: derive state, conflicts, order, orphans from a record set.
    /// </summary>
    public static class Fold
    {
        public record DerivedResult
        {
            [JsonPropertyName("state")]
            public Dictionary<string, object?> State { get; init; } = new();

            [JsonPropertyName("order")]
            public List<string> Order { get; init; } = new();

            [JsonPropertyName("conflicts")]
            public List<Dictionary<string, object?>> Conflicts { get; init; } = new();

            [JsonPropertyName("orphans")]
            public List<string> Orphans { get; init; } = new();
        }

        public static DerivedResult Derive(IEnumerable<Record> recordsList)
        {
            var records = recordsList.Where(r => !string.IsNullOrEmpty(r.Id)).ToList();
            var byId = records.ToDictionary(r => r.Id);

            // Find orphans
            var orphans = new HashSet<string>();
            foreach (var r in records)
            {
                foreach (var p in r.Parents)
                {
                    if (!byId.ContainsKey(p))
                    {
                        orphans.Add(r.Id);
                        break;
                    }
                }
            }

            // Detect cycles
            var color = records.ToDictionary(r => r.Id, _ => 0);
            var cycle = new HashSet<string>();
            void Dfs(string rid, List<string> path)
            {
                var c = color[rid];
                if (c == 1)
                {
                    int idx = path.IndexOf(rid);
                    foreach (var x in path.GetRange(idx, path.Count - idx))
                        cycle.Add(x);
                    return;
                }
                if (c == 2) return;
                color[rid] = 1;
                path.Add(rid);
                foreach (var p in byId[rid].Parents)
                {
                    if (byId.ContainsKey(p))
                        Dfs(p, path);
                }
                path.RemoveAt(path.Count - 1);
                color[rid] = 2;
            }
            foreach (var rid in byId.Keys.ToList())
            {
                if (color[rid] == 0)
                    Dfs(rid, new List<string>());
            }

            var allOrphans = orphans.Union(cycle).ToHashSet();
            var applied = byId.Where(kv => !allOrphans.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);

            // Topo sort, smallest-id-first
            var inDeg = applied.ToDictionary(kv => kv.Key, _ => 0);
            var edges = applied.ToDictionary(kv => kv.Key, _ => new List<string>());
            foreach (var (rid, r) in applied)
            {
                foreach (var p in r.Parents)
                {
                    if (applied.ContainsKey(p))
                    {
                        edges[p].Add(rid);
                        inDeg[rid]++;
                    }
                }
            }

            var queue = new SortedSet<string>(inDeg.Where(kv => kv.Value == 0).Select(kv => kv.Key));
            var order = new List<string>();
            while (queue.Count > 0)
            {
                var rid = queue.Min!;
                queue.Remove(rid);
                order.Add(rid);
                foreach (var child in edges[rid].OrderBy(x => x))
                {
                    inDeg[child]--;
                    if (inDeg[child] == 0)
                        queue.Add(child);
                }
            }

            // Apply effects in topo order
            var state = new Dictionary<string, object?>();
            var setters = new Dictionary<string, string>();
            var precedence = new Dictionary<string, int> { { "override", 3 }, { "task", 2 }, { "witness", 1 } };
            var conflictData = new Dictionary<string, (string winner, List<string> losers)>();

            foreach (var rid in order)
            {
                var r = applied[rid];
                foreach (var (k, v) in r.Effects)
                {
                    if (state.ContainsKey(k))
                    {
                        var existingKind = applied[setters[k]].Kind;
                        var newKind = r.Kind;
                        if (precedence.GetValueOrDefault(newKind, 0) > precedence.GetValueOrDefault(existingKind, 0))
                        {
                            // New wins (higher precedence)
                            if (!conflictData.ContainsKey(k))
                                conflictData[k] = (rid, new List<string>());
                            else
                                conflictData[k] = (rid, conflictData[k].losers);
                            var prevWinner = setters[k];
                            if (prevWinner != rid && !conflictData[k].losers.Contains(prevWinner))
                                conflictData[k].losers.Add(prevWinner);
                            state[k] = v;
                            setters[k] = rid;
                        }
                        else if (precedence.GetValueOrDefault(newKind, 0) == precedence.GetValueOrDefault(existingKind, 0))
                        {
                            // Same precedence: descendant wins
                            var existingSetter = setters[k];
                            bool newIsDescendant = IsDescendant(applied, existingSetter, rid);
                            if (newIsDescendant)
                            {
                                if (!conflictData.ContainsKey(k))
                                    conflictData[k] = (rid, new List<string>());
                                else
                                    conflictData[k] = (rid, conflictData[k].losers);
                                if (!conflictData[k].losers.Contains(existingSetter))
                                    conflictData[k].losers.Add(existingSetter);
                                state[k] = v;
                                setters[k] = rid;
                            }
                            else
                            {
                                // Existing wins; new is a loser
                                if (!conflictData.ContainsKey(k))
                                    conflictData[k] = (setters[k], new List<string>());
                                if (!conflictData[k].losers.Contains(rid))
                                    conflictData[k].losers.Add(rid);
                            }
                        }
                        else
                        {
                            // Lower precedence: existing wins; new is a loser
                            if (!conflictData.ContainsKey(k))
                                conflictData[k] = (setters[k], new List<string>());
                            if (!conflictData[k].losers.Contains(rid))
                                conflictData[k].losers.Add(rid);
                        }
                    }
                    else
                    {
                        state[k] = v;
                        setters[k] = rid;
                    }
                }
            }

            var conflicts = new List<Dictionary<string, object?>>();
            foreach (var (k, (winner, losers)) in conflictData)
            {
                conflicts.Add(new Dictionary<string, object?>
                {
                    ["key"] = k,
                    ["winner"] = winner,
                    ["losers"] = losers,
                });
            }

            return new DerivedResult
            {
                State = state,
                Order = order,
                Conflicts = conflicts,
                Orphans = allOrphans.ToList(),
            };
        }

        public static bool IsDescendant(Dictionary<string, Record> applied, string ancestor, string descendant)
        {
            if (ancestor == descendant) return false;
            var seen = new HashSet<string>();
            var stack = new Stack<string>();
            stack.Push(descendant);
            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                if (cur == ancestor) return true;
                if (seen.Contains(cur)) continue;
                seen.Add(cur);
                if (!applied.ContainsKey(cur)) continue;
                foreach (var p in applied[cur].Parents)
                {
                    if (!seen.Contains(p)) stack.Push(p);
                }
            }
            return false;
        }
    }
}
