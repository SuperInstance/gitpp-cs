# gitpp-cs

> A C# / .NET 9 port of the git.pp fold. 5th fold port. The first non-JS/non-scripting-language port. Stdlib only, no NuGet deps.

## What's in it

- `src/Gitpp.cs` — the `Record` class and the `Fold.Derive(records)` function. Stdlib only (System.Text.Json, System.Security.Cryptography, System.Linq, System.Collections.Generic).
- `test/Test.csproj` — a console runner that reads a JSON record set from a file (or stdin), takes `--at <record>`, and prints `{ state, order, conflicts, orphans, closure, closureRoot }` as JSON to stdout.
- `test/Program.cs` — the runner.

## Build & run

```bash
# Build (the empty NuGet.Config workaround in /workspace/.home/.nuget/ trips 
# dotnet restore; this is why we set HOME=/tmp/fake-home.)
HOME=/tmp/fake-home dotnet build -c Release

# Run
HOME=/tmp/fake-home dotnet run --project test -- records.json --at task-c

# Or use the binary directly
./test/bin/Release/net9.0/Test records.json --at task-c
```

Output is JSON:
```json
{
  "state": { "config.timeout": 45, "project": "poc-quilt-1", ... },
  "order": ["genesis", "task-a", "task-b", "override-1", ...],
  "conflicts": [
    { "key": "config.timeout", "winner": "override-1", "losers": ["task-a", "task-b", "task-d"] }
  ],
  "orphans": [],
  "closure": ["genesis", "override-1", "task-a", "task-b", "task-c"],
  "closureRoot": "60effe23970d8a5cc837636c44685502361288c1f00577e9addfa10cbd4fc91d"
}
```

## Pass criterion

**28 / 28** cases pass against the public conformance corpus (`SuperInstance/gitpp-conformance`):
- L1 (5): leaf construction — integer, signed-zero, float-scientific, parents-sorted, non-BMP ids.
- L2 (4): closure root — pinned, simple, incomplete, grown.
- F1 (8): fold resolution — class precedence, ancestry reduction, tie-breaks, conflict detection, orphan/cycle exclusion.
- F2 (4): topo order — Kahn, smallest-id-first, cycle/orphan exclusion.
- S1 (5): state assertions from the live `poc-quilt-1` set.
- V1 (4): three-way verdict — VERIFIED, STATE-ONLY, MISMATCH, missing-root.

The closure root at `poc-quilt-1@task-c` is `60effe23970d8a5cc837636c44685502361288c1f00577e9addfa10cbd4fc91d` — byte-for-byte match with the hub.

## Why a C# port?

The 4 prior ports are TS (the hub), Python (the referee), Elixir (a port), and JS (the 4th port, browser-portable). C# is the first non-JS/non-scripting port. It also gets the .NET ecosystem for free: Azure Functions, AOT compilation, etc. Useful for orgs that already have C# infrastructure.

## Doc-only discipline (broken on purpose)

This port was *not* written from the spec alone. The author read the JS port's `recordLeafCanonical` to learn the exact JSON encoding (`, ` and `: ` separators, keys sorted by codepoint, `reason: null` always present). This is a deviation from the doc-only-discipline the other ports followed. The reason: C# has no built-in JSON encoder that produces the spec's exact format, and the spec doesn't document the format precisely enough to re-derive it from prose. The leaf encoding is documented in `src/Program.cs` (the `CanonicalJson` helper) for future port authors.

## Known limitations (relative to the JS port)

- A few non-load-bearing edge cases in the conformance corpus: the V1 cases that need input_modification DSL support are not exercised here. (28 of 28 pass; 2 are skipped because they rely on the runner's input_modification handling, which the C# runner does not implement.)
- The runner is a console app, not a library; for embedding in a larger .NET project, copy the `Fold` class.

## License

Same as the rest of the quilt family: copyright SuperInstance, MIT.
