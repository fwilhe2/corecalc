# Corecalc / Funcalc — Code Documentation

Findings from reading the source of this repository (~12,150 lines of C# across 19 files).

This is a .NET 6 port of Peter Sestoft's **Corecalc** (an interpreted spreadsheet
core) and **Funcalc** (an extension that compiles *sheet-defined functions* to
.NET IL at runtime). The upstream project and the accompanying book,
*Spreadsheet Implementation Technology* (MIT Press, 2014), are at
<https://www.itu.dk/people/sestoft/funcalc/>.

---

## 1. What the project is

Two layers live side by side in one assembly:

| Layer | Namespace | Location | Job |
|---|---|---|---|
| **Corecalc** | `Corecalc` | repo root `*.cs` | Workbook/sheet/cell model, formula AST, values, built-in functions, *interpretive* evaluation, dependency ("support") graph, minimal recalculation. |
| **Funcalc** | `Corecalc.Funcalc` | `Funcalc/*.cs` | Turns a region of a *function sheet* into a compiled .NET delegate via `System.Reflection.Emit`, with partial evaluation, evaluation conditions, and unboxing optimisations. |

The port's changes relative to upstream are cosmetic and platform-related:
`net6.0`, nullable + implicit usings enabled, file-scoped namespaces, Unix line
endings, and **the WinForms GUI and the Coco/R-generated formula parser have
been dropped** (see §7). What remains is the engine plus a 22-line
`Program.Main` smoke test.

---

## 2. File map

### Corecalc (root)

| File | Lines | Contents |
|---|---:|---|
| `Types.cs` | 347 | `IDepend`, `Applier` delegate, exceptions (`CyclicException`, `ImpossibleException`), `Formats` (A1 / C0R0 / R1C1 display options), and hand-rolled collections `HashBag<T>`, `HashList<T>`, `ValueCache<T,U>`, `ValueTable<T>` (replacements for the C5 library used upstream). |
| `CellAddressing.cs` | 775 | `CellAddr`, `FullCellAddr`, `RARef` (relative/absolute reference), `Interval`, `Adjusted<T>`, and the support-graph types `SupportSet` / `SupportRange` / `SupportCell` / `SupportArea`. |
| `Cells.cs` | 733 | `CellState` enum and the `Cell` hierarchy. |
| `Sheet.cs` | 512 | `Sheet` plus `SheetRep`, the sparse quadtree cell store. |
| `Workbook.cs` | 254 | `Workbook`, recalculation drivers, volatile-cell set, edited-cell set, evaluation queue. |
| `Expressions.cs` | 798 | `Expr` AST: `Const` (`NumberConst`, `TextConst`, `ValueConst`, `Error`), `FunCall`, `CellRef`, `CellArea`; `IExpressionVisitor`; `RefSet`. |
| `Values.cs` | 1348 | `Value` hierarchy: `NumberValue`, `TextValue`, `ObjectValue`, `ErrorValue`, `ArrayValue` (`ArrayView`, `ArrayExplicit`, `ArrayDouble`), `FunctionValue` (closures). |
| `Functions.cs` | 1440 | `Function` — the built-in function table; 74 registrations. |
| `Program.cs` | 22 | Console entry point / smoke test. |

### Funcalc (`Funcalc/`)

| File | Lines | Contents |
|---|---:|---|
| `SdfManager.cs` | 540 | Static catalogue of sheet-defined functions (SDFs); `SdfInfo`; compile / register / update / specialize. |
| `SdfTypes.cs` | 577 | Signature lexer+parser (`SdfType`, `SimpleType`, `FunctionType`, `ArrayType`) and `ExternalFunction` (the `EXTERN` bridge to arbitrary .NET methods). |
| `DependencyGraph.cs` | 265 | Two-way precedent/dependent graph over function-sheet cells; topological `PrecedentOrder()`; static cycle detection. |
| `ExprToCGExpr.cs` | 125 | `CGExpressionBuilder`: `IExpressionVisitor` that lowers `Expr` → `CGExpr`. |
| `CGExpr.cs` | 3010 | The code-generation expression hierarchy (~40 classes) — the largest file. |
| `CodeGenerate.cs` | 251 | `Typ` lattice, shared `ILGenerator` state, temporaries, type-test emission. |
| `ProgramLines.cs` | 497 | `ProgramLines` (an SDF as a topologically ordered list), `ComputeCell`, `UnwrapInputCell`. |
| `PathConditions.cs` | 403 | `PathCond` / `CachedAtom` / `Conj` / `Disj` — evaluation conditions. |
| `Variable.cs` | 146 | `Variable`, `LocalVariable` (IL local), `LocalArgument` (IL argument). |
| `CellsInFuncs.cs` | 106 | Bidirectional map cell ↔ SDFs using it, for edit detection. |

---

## 3. The Corecalc data model

```
Workbook  1 ──* Sheet  ──(SheetRep)──*  Cell
                                        ├── ConstCell ── NumberCell | TextCell | QuoteCell | BlankCell
                                        ├── Formula      (Expr + cached Value + CellState)
                                        └── ArrayFormula (view into a shared CachedArrayFormula)
```

**Addressing.** `CellAddr` is a zero-based `(col, row)` pair; `FullCellAddr`
adds a `Sheet`. `RARef` stores a reference as it appears in a formula — each of
col/row is either absolute or an offset relative to the containing cell — which
is what makes copy/paste and row/column insertion re-target references
correctly (`RARef.Move`, `Expr.InsertRowCols`, `Adjusted<T>`).

**Storage.** `SheetRep` (`Sheet.cs:406`) is a four-level quadtree of 16×32 tiles
stored as flat 1-D arrays, addressing up to 2^16 columns × 2^20 rows. Index
computation is pure bit-masking; empty tiles are `null`, so a sparse sheet costs
almost nothing. The default logical sheet is 20 × 1000 (`Sheet.cs:40`).

**Values.** `Value` is the interpretive result type. Notable design points:

- `NumberValue` interns/caches wrappers and, crucially, encodes **errors inside
  NaN payloads** — the low 32 bits of a NaN index into `ErrorValue`'s error
  table. This lets compiled code pass errors around as raw `float64` without
  boxing.
- `ArrayValue` has three representations: `ArrayView` (lazy window onto sheet
  cells; indexing may trigger evaluation), `ArrayExplicit` (materialised
  `Value[,]`), and `ArrayDouble` (unboxed `double[,]` for linear algebra).
- `FunctionValue` is a closure: an `SdfInfo` plus already-supplied arguments.

**Expressions.** `Expr` is the formula AST. Every node implements evaluation
(`Eval`), display (`Show`), reference rewriting (`Move`, `CopyTo`,
`InsertRowCols`), support-set maintenance, and visitor dispatch (`VisitorCall`).

---

## 4. Recalculation: the support graph

This is the algorithmic heart of Corecalc and worth understanding before
touching anything else.

Each `Cell` carries a **support set** — the set of cells that *refer to it*
(`Cell.supportSet`, `CellAddressing.cs:206`). It is the reverse of the
dependency relation. Ranges are stored as `SupportArea` rectangles rather than
individual cells where possible, so `SUM(A1:A1000)` adds one entry to each of
1000 cells rather than 1000 entries.

`CellState` (`Cells.cs:37`) is `Dirty → Enqueued → Computing → Uptodate`.
Encountering `Computing` during evaluation *is* the cycle detection: `Formula.Eval`
throws `CyclicException` (`Cells.cs:371-375`).

`Workbook.Recalculate()` (`Workbook.cs:91`) implements **minimal recalculation**
in two stages, starting from the edited and volatile cells as roots:

1. Transitively mark everything reachable through support sets `Dirty`.
2. Enqueue the roots and drain `awaitsEvaluation`; evaluating a formula
   enqueues its supported cells in turn (`Formula.Eval`, `Cells.cs:381`).

Fallbacks: `RecalculateFull()` (evaluate every cell, Ctrl+Alt+F9 style) and
`RecalculateFullRebuild()` (also rebuild the support graph from scratch).
Any exception during recalculation resets all cells to `Dirty`
(`TimeRecalculation`, `Workbook.cs:155`); a `CyclicException` is stashed in
`Workbook.Cyclic`, and while that is non-null every recalculation is a full one.
All three drivers return elapsed milliseconds.

`SupportArea.IdempotentForeach` is set before the marking and enqueueing loops:
overlapping support ranges would otherwise visit cells repeatedly.

---

## 5. Built-in functions

`Function` (`Functions.cs:39`) is a name-keyed registry; construction registers
the instance, so the 74 `new Function(...)` statements in the static initialiser
*are* the function table. Each holds an `Applier` delegate
(`Value f(Sheet, Expr[], col, row)`) — note it receives **unevaluated argument
expressions**, which is what allows non-strict functions like `IF`, `AND`, `OR`
and `CHOOSE`. Helper factories `MakeFunction`, `MakeNumberFunction`,
`MakePredicate`, `MakeConstant` wrap ordinary .NET lambdas. Operators are
registered as functions too, with a precedence number used only for
parenthesis-minimal printing (`^`=8, `*` `/`=7, `+` `-` `&`=6, comparisons 4–5).

Registered names:

```
Arithmetic/math  ABS ACOS ASIN ATAN ATAN2 CEILING COS EXP FLOOR LN LOG LOG10
                 MOD NEG PI ROUND SIGN SIN SQRT TAN  ^ * / + -
Text/compare     &  <  <=  =  <>  >  >=  EQUAL
Logic/control    AND OR NOT IF CHOOSE
Aggregation      AVERAGE COUNTIF MAX MIN SUM SUMIF
Arrays           COLMAP COLUMNS CONSTARRAY HARRAY HCAT HSCAN INDEX ISARRAY
                 MAP REDUCE ROWMAP ROWS SLICE TABULATE TRANSPOSE VARRAY VCAT VSCAN
Higher-order/SDF APPLY CLOSURE DEFINE SPECIALIZE EXTERN
Errors/volatile  ERR ISERROR NA NOW RAND VOLATILIZE
Misc             BENCHMARK
```

Volatile functions (`NOW`, `RAND`, `VOLATILIZE`) propagate an `IsVolatile` flag
up the expression tree; `Workbook` keeps a `volatileCells` set that seeds every
minimal recalculation.

---

## 6. Funcalc: compiling sheet-defined functions

`SdfManager.CreateFunction(name, outputCell, inputCells)` is the entry point.
The pipeline (`SdfManager.CompileSdf`, `SdfManager.cs:109`):

1. **`DependencyGraph`** — walk back from the output cell, collecting all
   function-sheet cells it transitively depends on, recording both directions.
   Cells outside the function sheet are not traced. Static cycles throw
   `CyclicException` here, at compile time.
2. **`PrecedentOrder()`** — topological sort in calculation order, output cell
   last, constants omitted.
3. **`SetVolatility`** — does this SDF touch any volatile cell?
4. **`CGExpressionBuilder`** — lower each cell's `Expr` to a `CGExpr`.
5. **`ProgramLines.AddComputeCells`** — allocate an IL local per cell, *except*
   for cells with a single use, whose expression is inlined instead
   (`ProgramLines.cs:111-130`).
6. **`CompileToDelegate`** — emit into a `DynamicMethod` with signature
   `Value CGMethod(Value, …)` and bind the resulting `Delegate` into the
   `SdfManager.sdfDelegates` array.

The function is registered *before* compilation so it can call itself
recursively (`SdfManager.cs:94`), and SDF calls compile to an indexed load from
the public static `sdfDelegates` array — a deliberate modularity sacrifice for
call speed (`SdfManager.cs:42-46`).

**What makes the generated code fast** — four optimisations, all visible in
`CGExpr.cs`:

- **Unboxing / `CompileToDoubleProper`.** Every `CGExpr` can compile in three
  modes: to a `Value`, to a raw `float64`, or as a *condition* with separate
  true/false/other continuations. Numeric subexpressions therefore stay
  unwrapped, with errors riding along as NaN payloads.
- **Number variables.** `ProgramLines.CompileToDoubleOrNan` /
  `UnwrapInputCell` unwrap a cell into a `double` local when it is used as a
  number more than once (usage counted through `numberUses`, a `HashBag`).
- **Evaluation conditions** (`PathConditions.cs`). Because a compiled SDF is a
  straight-line program while the original sheet had lazy `IF` branches, each
  cell gets a path condition — a `Conj`/`Disj` of `CachedAtom`s — guarding its
  computation, so cells on untaken branches are not evaluated. Each atom is
  evaluated at most once and cached in a local (`CGCachedExpr`); this is why all
  copies and negations of an atom must share the same `cachedExpr`.
  `CodeGenerate.ShortcircuitEvaluationConditions` toggles whether `AND`/`OR`
  short-circuiting is modelled (more precise, more complex conditions).
- **Tail calls and partial evaluation.** `CGExpr.NoteTailPosition` is honoured
  by `CGIf`, `CGChoose` and `CGSdfCall`. `SPECIALIZE`/`CLOSURE` produce a
  `FunctionValue`, and `SdfManager.SpecializeAndCompile` partially evaluates the
  SDF against the fixed arguments (`ProgramLines.PEval` over a `PEnv`),
  compiling a *residual* function; results are memoised per `FunctionValue`, and
  the residual is registered before specialisation so specialisation cycles
  terminate.
- **`Gen`** (`CGExpr.cs:2815`) deduplicates emitted code and collapses
  jump-to-jump chains.

**Recompilation on edit.** `CellsUsedInFunctions` maps each function-sheet cell
to the SDFs using it. `Workbook.CheckForModifiedSdf` consults it with the
edited-cell list; if any SDF was affected, the `OnFunctionsAltered` event fires
and the workbook falls back to a full recalculation while
`SdfManager.Regenerate` re-emits the affected delegates.

**`EXTERN`** (`SdfTypes.cs`) parses a .NET-style signature string, resolves the
`MethodInfo` by reflection, and generates marshalling both ways between
spreadsheet `Value`s and .NET types — including array types.

---

## 7. State of this port — gaps and rough edges

### 7.1 Fixed defect: `CellAddr.operator==` compared the wrong field

*Fixed — recorded here because it explains why Funcalc appeared broken, and
because §7.2 item 4 is the reason it survived this long.*

`CellAddressing.cs:106-114` — both operators compared `row` against the *other*
operand's `col`:

```csharp
public static bool operator ==(CellAddr ca1, CellAddr ca2)
  { return ca1.col == ca2.col && ca1.row == ca2.col; }   // should be ca2.row
public static bool operator !=(CellAddr ca1, CellAddr ca2)
  { return ca1.col != ca2.col || ca1.row != ca2.col; }   // should be ca2.row
```

`Equals`/`GetHashCode` (`:91-99`) are correct, so dictionary and hash-set lookups
— which is how the engine addresses cells nearly everywhere — were unaffected.
Only the operators were wrong, and they were wrong in *both* directions:

| Comparison | `==` returned | Correct |
|---|---|---|
| `CellAddr(0,1) == CellAddr(0,1)` | `False` | `True` |
| `CellAddr(1,1) == CellAddr(1,3)` (B2 vs B4) | `True` | `False` |
| `CellAddr(3,3) == CellAddr(3,3)` | `True` | `True` (passed only because `col == row`) |

`FullCellAddr.operator==` (`:171-179`) is itself written correctly but delegates
to `ca1.ca == ca2.ca`, so it inherited the bug.

**Reachable impact.** The only call sites that reach these operators are
`Debug.Assert(sdfInfo.outputCell == dpGraph.outputCell)` (`ProgramLines.cs:75`)
and `Debug.Assert(dpGraph.outputCell == cellList[cellList.Count-1])`
(`ProgramLines.cs:103`). Everything else uses `Equals` or a hash container.
Because asserts are compiled out in Release, the effect was:

- **Release** — no observable misbehaviour; Funcalc worked.
- **Debug** (the default for `dotnet build` / `dotnet run`) — `SdfManager.CreateFunction`
  aborted the process with `Assertion failed: sdfInfo.outputCell == dpGraph.outputCell`
  for **any SDF whose output cell had `col != row`**. Since a typical function
  sheet puts the output in the same column a few rows down, that was essentially
  *all* of them: the Funcalc half of the project was unusable in a Debug build.

The fix was one character in each operator, `ca2.col` → `ca2.row`.

### 7.2 Other gaps

These are findings, not upstream defects; they follow from what the port kept.

1. **There is no formula parser.** `Cell.Parse` (`Cells.cs:94-102`) is a stub
   that always returns `null`; the Coco/R `Scanner`/`Parser` call is commented
   out. Formulas can only be built programmatically as `Expr` trees. This is the
   single biggest limitation: as shipped, the engine cannot read `=A1+B2` from
   a string.
2. **There is no I/O.** No workbook load/save (upstream had XMLSS import/export);
   the `IO` namespace referenced by the commented parser code is absent.
3. **No GUI.** The WinForms front end is gone, which is what made the Linux port
   possible.
4. **No tests.** CI (`.github/workflows/ci.yaml`) runs `dotnet test`, but the
   repo contains no test project, so that step is a no-op — verified to exit 0
   without discovering or running anything. It therefore cannot fail, which is
   why §7.1 has gone unnoticed. There is also no solution file — just
   `corecalc.csproj`.
5. **`SdfManager.ShowIL` (`SdfManager.cs:74`) is an empty method** — the IL
   dumper was dropped along with the GUI.
6. **`Program.cs` double-registers its sheet** (confirmed by running it — the
   two cells print twice). `new Sheet(wb, …)` already calls
   `workbook.AddSheet(this)` (`Sheet.cs:62`), and `Program.Main` then calls
   `wb.AddSheet(sheet)` again (`Program.cs:10`), so the workbook holds the same
   sheet twice and the sample prints its cells twice. The `x`/`y` counters in
   that loop are also not cell coordinates — `Sheet`'s enumerator yields non-null
   cells sparsely, so `ShowValue(sheet, x, y)` is passed positions unrelated to
   where the cell actually lives. It happens to print correctly only because
   `ConstCell.Eval` ignores its coordinates.
7. **`Nullable` is enabled in the csproj** but the 2006-era code is not
   null-annotated (e.g. `Workbook.this[String]` returns `null`, `OnFunctionsAltered`
   is an unassigned event). Expect nullable warnings.
8. **Debug output goes to `Console`** in library code — `RebuildSupportGraph`,
   `Formula.InsertRowCols`, `SpecializeAndCompile`, and `TimeRecalculation`'s
   `"BAD: {0}"` catch-all, which swallows non-cyclic exceptions.
9. **`Reflection.Emit`/`DynamicMethod` is used unconditionally**, so Funcalc
   cannot run under AOT or trimming, and it will not work on platforms without
   runtime code generation.
10. **Known upstream TODOs left in place**, e.g. `Formula.MoveContents` unshares
    expressions on block moves (`Cells.cs:356`), `Sheet.PasteCell` does not
    validate references such as `A0` (`Sheet.cs:130`), and
    `SdfManager.PendingSpecializations` is a linear scan (`SdfManager.cs:147`).

---

## 8. Orientation for a newcomer

- To follow **interpretation**: `Program.Main` → `Sheet.SetCell` →
  `Workbook.Recalculate` → `Formula.Eval` → `Expr.Eval` → `Function.Applier`.
- To follow **minimal recalculation**: `Cell.AddToSupportSets` →
  `SupportSet`/`SupportArea` → `Cell.MarkDirty` →
  `Cell.EnqueueForEvaluation` → `Workbook.awaitsEvaluation`.
- To follow **compilation**: `SdfManager.CreateFunction` → `DependencyGraph` →
  `CGExpressionBuilder` → `ProgramLines` → `CGExpr.Compile*` → `DynamicMethod`.

Chapters 1–8 of *Spreadsheet Implementation Technology* cover Corecalc; 9–14
cover Funcalc, and the class names in this codebase match the book's exactly.

---

## 9. Verification

Unlike the first revision of this document, the findings above have now been
checked against a running build (.NET SDK 10.0.400 on Debian 13).

| Check | Result |
|---|---|
| `dotnet build` | Succeeds — 0 errors, 335 warnings (all nullable-analysis, per §7.2 item 7) |
| `dotnet test` | Exits 0, runs nothing (no test project) |
| `dotnet run` | Fails — `net6.0` runtime absent; only `Microsoft.NETCore.App 10.0.11` installed. Runs under `DOTNET_ROLL_FORWARD=LatestMajor` |
| `net6.0` target | Out of support; SDK emits `NETSDK1138` |
| Interpretation | Correct. `A1=2, A2=3, A3=A1+A2, A4=A3*10, A5=SUM(A1:A2)` → `5, 50, 5` |
| Minimal recalculation | Correct. `A1:=40` then `Recalculate()` → `A3=43, A4=430, A5=43` |
| Cycle detection | Correct. `A6 = A6+1` → `### CYCLE in cell S1!A6 formula =S1!$A$6+1` |
| Funcalc SDF compile + call | Correct in both Debug and Release once §7.1 was fixed. `MYF(x) = IF(x>10; x*2; x+100)` → `MYF(5)=105`, `MYF(20)=40`; callable from an ordinary sheet |

The engine was exercised by compiling the repository's sources together with a
driver program in a scratch project (formulas built as `Expr` trees, since §7.2
item 1 means they cannot be parsed from strings).
