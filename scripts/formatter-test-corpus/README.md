# Formatter Test Corpus

Local-only corpus for the formatter regression harness. Contents are gitignored; only this README is tracked.

## What goes here

Place `.sql` files that represent realistic inputs the formatter should handle cleanly. Organise in subdirectories if useful — the harness walks recursively.

Source ideas per the doc (see `docs/FORMATTER-OVERHAUL.md` → "Test Corpus"):

- Top N sprocs by LOC scripted from the REPOSITORY DB production audit log
- `usp_get_sorter_lane_rpick` and other hairy WMS sprocs
- Solvoyo integration procs (`t_solvoyo_load_audit` indexers, archiving proc)
- AdventureWorks sample scripts (sanity control)
- User's saved queries folder (`~/Library/Application Support/Lookout/queries/*.sql`)
- Handwritten edge cases — single-line SELECT, 15-column SELECT, 3-level nested CTE, 20-WHEN CASE, all-three-WHEN MERGE, CREATE TABLE with every constraint type, TRY/CATCH inside WHILE inside IF, sp_executesql with string-literal SQL, comments in every position

## How to run the harness

```bash
dotnet run --project Tools/FormatterRegression/FormatterRegression.csproj -f net10.0
```

The harness:

1. Runs the new formatter on each `.sql` file.
2. Parses both original and formatted outputs with `TSql170Parser`.
3. Generates canonical form via `Sql170ScriptGenerator`.
4. String-compares the two canonical outputs.

Results:

- `[parse-fail]` — either original SQL didn't parse (input problem) or formatted output didn't parse (regression — check `reports/<file>.formatted`).
- `[mismatch]` — semantic equivalence check failed. Diff `reports/<file>.canon.orig` vs `reports/<file>.canon.fmt` to see what the formatter changed at the AST level.

Exit code: `0` if all files match, `2` otherwise.

## Ship gate

Per the doc: **95%+ of the corpus passes canonical equivalence** with comments preserved, no regressions on nested SELECTs / CTEs / CASE / procs vs Hogimn output, before the default flip in sequencing step 6.
