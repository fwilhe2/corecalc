# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A .NET 6 port of Peter Sestoft's **Corecalc** (an interpreted spreadsheet core) and
**Funcalc** (which compiles *sheet-defined functions* to .NET IL at runtime), from
*Spreadsheet Implementation Technology* (MIT Press, 2014). Class names match the
book's exactly; chapters 1–8 cover Corecalc, 9–14 Funcalc.
Upstream: <https://www.itu.dk/people/sestoft/funcalc/>

`ARCHITECTURE.md` holds a detailed walkthrough of the engine, including file-by-file
notes and a section on the port's known gaps. Read it before non-trivial work.

## Commands

```bash
dotnet build                       # solution: engine + tests
dotnet test                        # 23 tests
dotnet test --filter "FullyQualifiedName~SdfTests"                 # one class
dotnet test --filter "FullyQualifiedName~SdfTests.CompiledFunctionHasExpectedMetadata"
```

**Local gotcha:** the projects target `net6.0`, which is out of support. If only a
newer runtime is installed, `dotnet test` and `dotnet run` fail with
"You must install or update .NET". Prefix with `DOTNET_ROLL_FORWARD=LatestMajor`:

```bash
DOTNET_ROLL_FORWARD=LatestMajor dotnet test
DOTNET_ROLL_FORWARD=LatestMajor dotnet run
```

CI (`.github/workflows/ci.yaml`) installs a real 6.0.x SDK and needs no such flag.
Builds emit ~335 nullable warnings — `<Nullable>enable</Nullable>` is on but the
2006-era code is not annotated. That is the baseline, not a regression.

## Architecture

Two layers in one assembly:

| Layer | Namespace | Location |
|---|---|---|
| Corecalc — cell model, formula AST, values, built-ins, interpretation, minimal recalculation | `Corecalc` | root `*.cs` |
| Funcalc — compiles function-sheet regions to delegates via `Reflection.Emit` | `Corecalc.Funcalc` | `Funcalc/*.cs` |

```
Workbook 1──* Sheet ──(SheetRep quadtree)──* Cell
                                             ├── ConstCell (Number|Text|Quote|Blank)
                                             ├── Formula      (Expr + cached Value + CellState)
                                             └── ArrayFormula
```

### Recalculation is support-graph driven

The algorithmic heart, and the thing to understand before touching `Cells.cs`,
`Workbook.cs` or `CellAddressing.cs`. Each cell stores a **support set** — the cells
that *refer to it*, i.e. the reverse of the dependency relation — with ranges kept as
`SupportArea` rectangles so `SUM(A1:A1000)` costs one entry per cell, not 1000.

`Workbook.Recalculate()` marks everything reachable from edited and volatile cells
`Dirty`, then drains an evaluation queue. `CellState` runs `Dirty → Enqueued →
Computing → Uptodate`, and hitting `Computing` *is* the cycle detection — `Formula.Eval`
throws `CyclicException`. While `Workbook.Cyclic` is non-null every recalculation is a
full one. `SupportArea.IdempotentForeach` must be set before the marking and enqueueing
loops or overlapping ranges revisit cells.

### Values encode errors in NaN payloads

`ErrorValue.MakeNan`/`ErrorCode` hide an error-table index in the low bits of a NaN,
and `NumberValue.Make` maps such a NaN back to the corresponding `ErrorValue`. This is
what lets compiled code pass errors around as raw `float64` without boxing — don't
"clean up" NaN handling without understanding this.

### Built-ins are a self-registering table

`Function`'s static initialiser *is* the function table: each `new Function(...)`
registers itself by name. An `Applier` receives **unevaluated argument expressions**
(`Value f(Sheet, Expr[], col, row)`), which is what makes `IF`, `AND`, `OR` and
`CHOOSE` non-strict. Volatile functions (`NOW`, `RAND`, `VOLATILIZE`) propagate an
`IsVolatile` flag up the tree into `Workbook`'s volatile-cell set.

### Funcalc compilation pipeline

`SdfManager.CreateFunction` → `DependencyGraph` (walk back from the output cell;
static cycles throw here, at compile time) → `PrecedentOrder()` topological sort →
`CGExpressionBuilder` lowers `Expr` → `CGExpr` → `ProgramLines.AddComputeCells`
(one IL local per cell, except single-use cells which are inlined) →
`CompileToDelegate` emitting a `DynamicMethod`.

Things that constrain edits to `Funcalc/`:

- The function is registered *before* compilation so it can recurse. SDF calls compile
  to an indexed load from the public static `SdfManager.sdfDelegates` array — a
  deliberate modularity sacrifice for call speed.
- Every `CGExpr` compiles three ways: to a `Value`, to a raw `float64`, or as a
  *condition* with separate true/false/other continuations. Numeric subexpressions stay
  unboxed.
- A compiled SDF is straight-line code, but the source sheet had lazy `IF` branches, so
  each cell carries a **path condition** (`PathConditions.cs`) guarding its computation.
  Each atom is evaluated once and cached, which is why all copies and negations of an
  atom must share the same `cachedExpr`.
- `Reflection.Emit`/`DynamicMethod` is unconditional, so Funcalc cannot run under AOT
  or trimming.

## Working in this codebase

**There is no formula parser.** `Cell.Parse` is a stub that always returns `null` (the
Coco/R scanner was dropped along with the WinForms GUI). Formulas must be built
programmatically as `Expr` trees — see `tests/Corecalc.Tests/SheetBuilder.cs` for the
helpers, and note `Sheet.SetCell` + `Formula.Make`. There is also no workbook I/O.

**Most AST types are internal.** `Formula`, `FunCall`, `CellRef`, `NumberConst` and
friends are internal to the assembly; `corecalc.csproj` grants `InternalsVisibleTo`
to `Corecalc.Tests`.

**`corecalc.csproj` sits at the repo root**, so its default globs would swallow
`tests/**`; a `<Compile Remove>` prevents that. Any new project under the root needs
the same treatment.

**`SdfManager` holds process-wide static state** and `Workbook`'s constructor calls
`SdfManager.ResetTables()`, wiping it. The test assembly therefore disables xUnit
parallelisation, and any new test project must do the same.

**Debug vs Release matters here.** `Funcalc/` contains `Debug.Assert`s that are
compiled out in Release, so a Funcalc bug can be invisible in Release and fatal in
Debug. Run tests in both when changing that layer.
