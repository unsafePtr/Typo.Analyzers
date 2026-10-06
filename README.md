# TypoAnalyzers

Roslyn analyzers for copy-paste and typo bugs that the common open-source .NET analyzers don't cover.

## Rules

| ID | Analyzer | Finds |
|---|---|---|
| TYPO0001 | IdenticalOperandsAnalyzer | Repeated operand in a `\|\|` / `&&` / `\|` / `&` / `^` / `or` / `and` chain; identical operands of a comparison, `-`, `/` or `%` |
| TYPO0002 | SelfArgumentAnalyzer | `x.Equals(x)`, `x.CompareTo(x)`, `Equals(a, a)`, `comparer.Compare(a, a)`, `Math.Max(a, a)` |
| TYPO0003 | RepeatedConditionAnalyzer | `else if` repeating a condition, or one disjunct of a condition, tested earlier in the chain |
| TYPO0004 | RepeatedConditionAnalyzer | Adjacent `if` statements with the same condition that nothing in between can change |
| TYPO0005 | RepeatedConditionAnalyzer | Nested `if` repeating, or negating, a check of an enclosing `if` |
| TYPO0006 | IdenticalBranchesAnalyzer | Identical `then` and `else` parts of `if` or `?:` |
| TYPO0007 | IdenticalBranchesAnalyzer | Two `switch` cases, switch-expression arms or else-if branches with identical non-trivial bodies |
| TYPO0008 | RepeatedAssignmentAnalyzer | Field, auto-property, array element or collection entry written twice with no read in between |
| TYPO0009 | LoopIndexAnalyzer | `for` loop over a collection (forward, `Count - 1` or reverse) that reads `[0]` and never uses the loop variable |
| TYPO0010 | StringCaseAnalyzer | Lowercased or uppercased value compared with, or searched for, text with letters of the other case |
| TYPO0011 | SimilarRunAnalyzer | One item of a run of three or more similar statements, declarations, conditions or arguments repeating a neighbour's name, as in `Z += other.Y`, `maxY = Math.Max(maxY, p.X)` or `new Rect(r.Left, r.Top, r.Right, r.Right)` (Info severity) |

Every analyzer shares the core in `src/TypoAnalyzers/Core`:
- `OperationEquivalence`: semantic expression equality, plus a matching hash.
- `Negation`: opposite conditions, and one test in different spellings (`x == null`, `x is null`).
- `PurityOracle`: which calls and getters are side-effect free (clocks and `Guid.NewGuid` are not).
- `OperandChain`: operator-chain flattening.
- `ReadSet`: whether code in between can change a condition, including through closures.
- `Jumps`: whether a statement always exits.
- `Framework`: .NET types versus user types, for the reflexivity exclusions.
- `Limits`: depth and stack guards, so huge generated expressions cannot overflow the compiler's stack.

## Culture independence

The compiler and IDE run analyzers under the user's culture, so no verdict may depend on it:
- The code uses only ordinal and invariant string operations, and `.editorconfig` turns the .NET globalization rules (CA1304, CA1305, CA1307, CA1309, CA1310, CA1311) into build errors.
- `CultureIndependenceTests` run analyzers under Turkish culture, where `"i".ToUpper()` is `"İ"`, and first assert that the culture really behaves that way.
- `<InvariantGlobalization>` is deliberately not used: it does not apply to a library loaded by the compiler, and in the test host it would hide exactly the bugs these tests look for.

`-p:WarningLevel=0` hides every analyzer diagnostic, even ones raised to errors in `.editorconfig`. Both projects therefore ignore an incoming `WarningLevel` (`TreatAsLocalProperty`). In consuming projects the same flag hides TypoAnalyzers' own findings.

## Known gaps

- Identical one-line `else if` bodies without calls or assignments are skipped on purpose to avoid noise.
- In TYPO0011, numeric literals that are not indexes count as table data and never form a pattern.
- Declarations that read a local declared earlier in the same run, as in `f8 = f4 * f4; f12 = f8 * f4`, are steps of one computation and never form a run.
- Swapped arguments matched by parameter name are left to Sonar S2234.

## Build and test

```powershell
$env:MSBUILDUSESERVER = '1'
dotnet restore
dotnet build -v:q --no-restore
dotnet run --project tests/TypoAnalyzers.Tests/TypoAnalyzers.Tests.csproj
```

Coverage needs the Microsoft.Testing.Platform host, which `dotnet test` provides (xUnit's native runner behind `dotnet run` rejects `--coverage`):

```powershell
dotnet test --project tests/TypoAnalyzers.Tests/TypoAnalyzers.Tests.csproj --coverage --coverage-output-format cobertura
```

The analyzer targets `netstandard2.0` and compiles against Roslyn 5.0.0 so it loads in Visual Studio 2026 and every .NET 10 SDK.
