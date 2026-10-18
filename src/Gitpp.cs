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
    /// <summary>
    /// Put every JSON number in the float64 domain, per spec SS2/SS3.
    ///
    /// System.Text.Json does not do this on its own: a bare integer literal
    /// arrives as an int64, so {"x": 1000000000000000000} was emitted as integer
    /// digits while js/c/perl/python all decoded it as a float64 and emitted
    /// 1e+18 (abs(v) >= 1e16 is scientific per SS3). Same class of defect as
    /// python's json module (arbitrary-precision int) and JSON::PP (string).
    /// Three of five ports shipped it; the host decoder quietly choosing a
    /// different numeric type is the entire problem, so the fix belongs at the
    /// decode boundary and never in the encoder. (gitpp-cs v0.3.3)
    /// </summary>
    public static class Numbers
    {
        public static object? Normalize(object? v)
        {
            switch (v)
            {
                case null: return null;
                case long l: return (double)l;
                case int i: return (double)i;
                case short sh: return (double)sh;
                case float f: return (double)f;
                case decimal m: return (double)m;
                case JsonElement je:
                    if (je.ValueKind == JsonValueKind.Number) return je.GetDouble();
                    if (je.ValueKind == JsonValueKind.Array)
                        return je.EnumerateArray().Select(x => Numbers.Normalize(x)).ToList();
                    if (je.ValueKind == JsonValueKind.Object)
                        return je.EnumerateObject().ToDictionary(
                            p => p.Name, p => Numbers.Normalize(p.Value), StringComparer.Ordinal);
                    return JsonSerializer.Deserialize<object?>(je);
                case System.Collections.IEnumerable list when list is not string:
                    return list.Cast<object?>().Select(x => Numbers.Normalize(x)).ToList();
                default: return v;
            }
        }

        public static Record Normalize(Record r)
        {
            var e = new Dictionary<string, object?>(r.Effects.Count, StringComparer.Ordinal);
            foreach (var kv in r.Effects) e[kv.Key] = Normalize(kv.Value);
            return r with { Effects = e };
        }

        /// <summary>
        /// Deserialize a record set with every number in the float64 domain.
        /// Prefer this over JsonSerializer.Deserialize&lt;Record[]&gt; directly.
        /// </summary>
        public static Record[] ParseRecords(string json)
        {
            var raw = JsonSerializer.Deserialize<Record[]>(json) ?? Array.Empty<Record>();
            return raw.Select(r => Normalize(r)).ToArray();
        }
    }

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

            /// <summary>
            /// Spec §5.6: which record sets each key. The streaming fold already
            /// tracked this in <c>setters</c> and discarded it, which left a
            /// same-class nudge parented on the wrong record landing or vanishing
            /// depending only on whether its own id sorted before the winner's.
            /// </summary>
            [JsonPropertyName("setters")]
            public Dictionary<string, string> Setters { get; init; } = new();
        }

        public static DerivedResult Derive(IEnumerable<Record> recordsList)
        {
            var records = recordsList.Where(r => !string.IsNullOrEmpty(r.Id)).ToList();
            var byId = records.ToDictionary(r => r.Id);

            // Spec §5.5 — "cycle-affected records are orphans for output purposes".
            //
            // This needs THREE clauses, not one. The two obvious single-clause rules
            // each get a different case wrong:
            //
            //   rule A: a parent id is missing from the record set. Correct for a
            //           missing-parent record, but blind to cycles — a cycle
            //           member's parents all exist, so records downstream of the
            //           cycle sail through and reach state.
            //   rule B: anything Kahn never emitted. Correct for cycles, but it also
            //           swallows the descendants of a MISSING parent, which are
            //           perfectly orderable. (That trade-off is gitpp-rr-reader3#1.)
            //
            // Clause 3 is what separates the two cases no single rule can: a cycle
            // DESCENDANT's parent exists but is unorderable, while a
            // missing-parent DESCENDANT's parent does not exist at all.
            //
            // `orderable` is Kahn over the FULL record set with in-degree counting
            // only parents THAT EXIST. An absent parent cannot block a record —
            // there is nothing to wait for — which is exactly why a missing-parent
            // record and its descendants stay orderable and must NOT be orphaned.
            var orderable = new HashSet<string>();
            var oInDeg = byId.ToDictionary(kv => kv.Key, _ => 0);
            var oEdges = byId.ToDictionary(kv => kv.Key, _ => new List<string>());
            foreach (var (rid, r) in byId)
            {
                foreach (var p in r.Parents)
                {
                    if (byId.ContainsKey(p))
                    {
                        oEdges[p].Add(rid);
                        oInDeg[rid]++;
                    }
                }
            }
            var oQueue = new SortedSet<string>(
                oInDeg.Where(kv => kv.Value == 0).Select(kv => kv.Key), StringComparer.Ordinal);
            while (oQueue.Count > 0)
            {
                var rid = oQueue.Min!;
                oQueue.Remove(rid);
                orderable.Add(rid);
                foreach (var child in oEdges[rid].OrderBy(x => x, StringComparer.Ordinal))
                {
                    if (--oInDeg[child] == 0)
                        oQueue.Add(child);
                }
            }

            var allOrphans = new HashSet<string>();
            foreach (var (rid, r) in byId)
            {
                var ps = r.Parents;
                // 1. missing parent
                if (ps.Any(p => !byId.ContainsKey(p)))
                {
                    allOrphans.Add(rid);
                    continue;
                }
                // 2. cycle member — can never be topologically ordered. This subsumes
                // the old DFS cycle scan: a node on a cycle always has an in-edge from
                // another cycle member, so Kahn can never emit it.
                if (!orderable.Contains(rid))
                {
                    allOrphans.Add(rid);
                    continue;
                }
                // 3. cycle descendant — an existing parent that is itself unorderable.
                // The byId.ContainsKey(p) guard is the whole point: it keeps clause 1
                // (parent absent) and clause 3 (parent present but stuck) apart.
                if (ps.Any(p => byId.ContainsKey(p) && !orderable.Contains(p)))
                {
                    allOrphans.Add(rid);
                }
            }
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

            // Spec 5.1 (v0.4.6): a record whose kind is null or unrecognised has no
            // class and ranks lowest -- below witness. It still wins an uncontested
            // key, and two such records are a single class, so 5.2's equal-class
            // tie-break (ancestry, then topo order) applies between them.
            //
            // This must be a null CHECK, not a defaulted lookup:
            // Dictionary<string,int>.GetValueOrDefault(null, 0) THROWS
            // ArgumentNullException rather than returning the default, because a
            // Dictionary rejects a null key outright -- including through the
            // CollectionExtensions overload that is meant to be null-tolerant.
            int Rank(string kind) => kind is null ? 0 : precedence.GetValueOrDefault(kind, 0);
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
                        if (Rank(newKind) > Rank(existingKind))
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
                        else if (Rank(newKind) == Rank(existingKind))
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

            // Per spec §5.3: a conflict is emitted only when 2+ DISTINCT values
            // exist for k across ALL candidates, and `losers` is every candidate
            // that set k with a value OTHER THAN the winner's. The streaming
            // model above records a loser the moment one outranks another, which
            // over-reports when two records agree. Reconcile here.
            // (gitpp-cs v0.3.2)
            foreach (var k in conflictData.Keys.ToList())
            {
                var (w, l) = conflictData[k];
                var cands = applied.Values.Where(r => r.Effects.ContainsKey(k)).ToList();
                var distinct = cands.Select(r => JsonSerializer.Serialize(r.Effects[k]))
                                    .Distinct(StringComparer.Ordinal).Count();
                if (distinct < 2) { conflictData.Remove(k); continue; }
                if (!applied.TryGetValue(w, out var wr)) { conflictData.Remove(k); continue; }
                var wv = JsonSerializer.Serialize(wr.Effects[k]);
                var kept = l.Where(x => applied.ContainsKey(x)
                              && JsonSerializer.Serialize(applied[x].Effects[k]) != wv).ToList();
                if (kept.Count == 0) { conflictData.Remove(k); continue; }
                conflictData[k] = (w, kept);
            }

            var conflicts = new List<Dictionary<string, object?>>();
            // SPEC 5.2 (v0.4.6): the losers are listed in the 5.4 order
            // sequence -- the same order the 'order' field uses, filtered
            // to the losers.
            //
            // Previously they were appended in DISCOVERY order, the order
            // in which the streaming fold displaced them. The old text
            // fixed the MEMBERSHIP of the loser set ("every record that
            // tried to set K with a value other than the winner's") and
            // said "N-1 losers" -- set language -- without ever saying
            // what order the set is written in.
            //
            // Minimal witness: m(override,x=2), z(witness,x=3),
            // a(override,x=1,parents:[z]) -> order [m,z,a], winner m,
            // losers z and a. Order sequence gives [z,a]; codepoint gives
            // [a,z]. Both satisfy "N-1 losers".
            //
            // The ledger is a RECEIPT: a consumer that hashes it or diffs
            // two ledgers must not get different bytes for the same fold.
            var orderIndex = new Dictionary<string, int>();
            for (int i = 0; i < order.Count; i++) orderIndex[order[i]] = i;
            foreach (var (k, (winner, losers)) in conflictData)
            {
                var ordered = losers
                    .OrderBy(l => orderIndex.TryGetValue(l, out var oi) ? oi : int.MaxValue)
                    .ToList();
                conflicts.Add(new Dictionary<string, object?>
                {
                    ["key"] = k,
                    ["winner"] = winner,
                    ["losers"] = ordered,
                });
            }

            return new DerivedResult
            {
                State = state,
                Setters = setters,
                Order = order,
                Conflicts = conflicts,
                Orphans = allOrphans.ToList(),
            };
        }

        /// <summary>
        /// Compute the closure root at <paramref name="at"/>. Returns (root, closure)
        /// where root is the SHA-256 hex digest of the closure, or null if the closure
        /// is incomplete.
        /// </summary>
        public static (string? root, List<string> closure) ClosureRoot(List<Record> records, string at)
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

        /// <summary>
        /// Compute the leaf hash of a record. Per spec §2: SHA-256 of the canonical
        /// JSON form of {id, parents, kind, effects, reason}.
        /// </summary>
        public static string RecordLeaf(Record r)
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

        /// <summary>
        /// Serialize a value to canonical JSON per spec §2.7: 
        /// keys sorted by ordinal, ", " and ": " separators, literal Unicode
        /// (no \\uXXXX escape), numbers in float64 domain.
        /// </summary>
        public static string CanonicalJson(object? obj) => EmitValue(obj, ", ", ": ");

        static string EmitValue(object? v, string sep, string kvSep)
        {
            if (v == null) return "null";
            if (v is JsonElement je) return EmitJsonElement(je, sep, kvSep);
            if (v is bool b) return b ? "true" : "false";
            if (v is int i) return i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (v is long l) return l.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (v is double d) return NumFormat(d);
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
            if (v is List<string> strs)
            {
                if (strs.Count == 0) return "[]";
                return "[" + string.Join(sep, strs.Select(x => JsonStringEncode(x))) + "]";
            }
            if (v is System.Collections.IEnumerable enumerable)
            {
                var items = enumerable.Cast<object?>().ToList();
                if (items.Count == 0) return "[]";
                return "[" + string.Join(sep, items.Select(x => EmitValue(x, sep, kvSep))) + "]";
            }
            return "null";
        }

        static string NumFormat(double v)
        {
            if (v == 0) return "0";
            var abs = Math.Abs(v);
            // Integer form requires |v| < 1e16 to fit safely in int64 across
            // all 5 ports. Larger integer-valued floats fall back to
            // scientific notation (matching what Python emits in the
            // cross-port runner, and what C# would emit anyway when the
            // long cast overflows to long.MinValue).
            if (v == Math.Floor(v) && abs < 1e16)
            {
                return ((long)v).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            if (abs < 1e-4 || abs >= 1e16) return SciFormat(v);
            return v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        }

        static string SciFormat(double v)
        {
            // Try 15-digit scientific first, then 16, then 17. The "shortest
            // round-trip" picks the smallest number of digits that produces
            // the same double when re-parsed. Matches Python repr() for
            // float values that don't fit in int64.
            for (int digits = 15; digits <= 17; digits++)
            {
                var s = v.ToString("e" + (digits - 1), System.Globalization.CultureInfo.InvariantCulture);
                s = System.Text.RegularExpressions.Regex.Replace(s, @"(\.\d*?)0+e", "$1e");
                s = s.Replace(".e", "e");
                double back = double.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
                if (back == v)
                {
                    var m = System.Text.RegularExpressions.Regex.Match(s, @"^([^e]+)e([+-]?\d+)$");
                    if (m.Success)
                    {
                        var exp = int.Parse(m.Groups[2].Value);
                        var expStr = Math.Abs(exp).ToString("D2");
                        if (exp < 0) return m.Groups[1].Value + "e-" + expStr;
                        return m.Groups[1].Value + "e+" + expStr;
                    }
                    return s;
                }
            }
            // 17 digits also didn't round-trip (shouldn't happen for IEEE-754 double).
            // Fall back to "R" format which always round-trips.
            var fallback = v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            return fallback;
        }

        static string JsonStringEncode(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (var c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20) sb.Append($"\\u{(int)c:X4}");
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        static string EmitJsonElement(JsonElement je, string sep, string kvSep)
        {
            switch (je.ValueKind)
            {
                case JsonValueKind.Null: return "null";
                case JsonValueKind.True: return "true";
                case JsonValueKind.False: return "false";
                case JsonValueKind.String: return JsonStringEncode(je.GetString() ?? "");
                case JsonValueKind.Number:
                    if (je.TryGetInt64(out var ln)) return ln.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    return NumFormat(je.GetDouble());
                case JsonValueKind.Array:
                    var items = je.EnumerateArray().Select(x => EmitJsonElement(x, sep, kvSep)).ToList();
                    if (items.Count == 0) return "[]";
                    return "[" + string.Join(sep, items) + "]";
                case JsonValueKind.Object:
                    var props = new List<JsonProperty>();
                    foreach (var p in je.EnumerateObject()) props.Add(p);
                    props.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                    if (props.Count == 0) return "{}";
                    return "{" + string.Join(sep, props.Select(p => JsonStringEncode(p.Name) + kvSep + EmitJsonElement(p.Value, sep, kvSep))) + "}";
                default: return "null";
            }
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
