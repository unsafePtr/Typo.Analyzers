# AGENTS.md

Guidance for agents and contributors who change Typo.Analyzers.
The [README](README.md) describes the rules for the people who use them; this file covers how the code is built, laid out and changed.

## Build and test

```shell
dotnet restore
dotnet build -v:q --no-restore
dotnet run --project tests/Typo.Tests/Typo.Tests.csproj
```

- Never build with `-p:WarningLevel=0`. It hides every analyzer diagnostic, even the CA rules that `.editorconfig` raises to errors, so code passes locally that CI rejects.
- The tests run on xUnit v3's own runner, so its options follow `--`, as in `-- -class "*HelpLinkTests"`, and `dotnet test` filter syntax does not apply.
- Coverage needs the Microsoft.Testing.Platform host that `dotnet test` provides, because the runner behind `dotnet run` rejects `--coverage`.
- The first restore points git at `.githooks`, whose pre-commit hook checks the whitespace of the staged C# files with `dotnet format whitespace`.
- CI also runs `dotnet format --verify-no-changes --no-restore` with style and analyzer rules, so run it before pushing.

```shell
dotnet test --project tests/Typo.Tests/Typo.Tests.csproj --coverage --coverage-output-format cobertura
```

## Layout

- `src/Typo.Analyzers/Analyzers`: one analyzer class per file; RepeatedConditionAnalyzer, IdenticalBranchesAnalyzer and SimilarRunAnalyzer each report more than one rule.
- `src/Typo.Analyzers/Descriptors.cs`: the descriptor of every rule, with its message, severity and help link.
- `src/Typo.Analyzers/Core`: the core the analyzers share, listed below.
- `tests/Typo.Tests`: one `<Analyzer>Tests` class per analyzer, plus `CoreTests`, `CultureIndependenceTests`, `HelpLinkTests` and `RobustnessTests`, which runs every analyzer over code as it looks mid-typing and asserts that none throws.
- `Verifier<TAnalyzer>` runs one analyzer over source with markup locations such as `{|#0:...|}`; `AllAnalyzers` finds every analyzer in the assembly by reflection and runs them together.

The analyzers share the core in `src/Typo.Analyzers/Core`; IdenticalBranchesAnalyzer compares syntax and uses only `Quote`:
- `OperationEquivalence`: semantic expression equality, where `a + b` matches `b + a` and `a < b` matches `b > a`, plus a matching hash.
- `Negation`: one test in different spellings (`x == null`, `x is null`), exact opposites, and tests that exclude each other.
- `PurityOracle`: which calls and getters are side-effect free (clocks, `Guid.NewGuid` and `Path.GetRandomFileName` are not).
- `OperandChain`: operator-chain flattening.
- `ReadSet`: whether code in between can change a condition, including through closures and `Span<T>` arguments.
- `Jumps`: whether a statement always exits.
- `Framework`: .NET types versus user types, for the reflexivity skips, TYPO0012's framework values and TYPO0008 initializers.
- `Limits`: depth and stack guards, so huge generated expressions cannot overflow the compiler's stack.
- `StatementList`: the lists of neighbouring statements that rules compare, from blocks and switch sections alike.
- `Quote`: the source text of a diagnostic message on one line, so MSBuild's line-by-line log does not cut it.

## Changing a rule

The rules favour recall over precision, as the opening of the README says.
- A change may drop a diagnostic only where the analyzer can show that the code is correct, never by narrowing a rule in a way that also loses real bugs.
- A proposed skip lists the true-positive shapes it would stop reporting; if there are any, find a narrower test or keep reporting.
- A false positive worth a skip comes from real code; one written only to look like a false positive does not justify it.
- A test taken from real code keeps its shape but not the name of the project it came from.
- Patterns that are noisy by nature report at Info severity instead of staying silent.
- The README's Known gaps change with the rule whenever it starts or stops covering a shape.

Adding a rule takes four places, and the build or the tests fail until all of them agree:
1. A descriptor in `Descriptors.cs` with the next free `TYPO` ID, category `Correctness` and the help link `HelpLinkBase + "typo00NN"`.
2. A row in `AnalyzerReleases.Unshipped.md`, which the release-tracking rules of Microsoft.CodeAnalysis.Analyzers check during the build.
3. A README row whose ID cell starts with `<a id="typo00NN"></a>`, and a `PACKAGE.md` row linking to that anchor, which `HelpLinkTests` checks.
4. Tests of the shapes the rule reports and of the shapes it skips.

## Analyzer constraints

- The analyzer targets `netstandard2.0` and compiles against Roslyn 5.0.0, the version in Visual Studio 2026 18.0 and the .NET 10 SDK; a newer reference stops it loading there.
- It stays on `netstandard2.0`: a `net10.0` analyzer fails silently with CS8032 in legacy projects and `RoslynCompilerType=Framework` builds, and trips RS1041.
- For speed, use what Roslyn's own dependencies give a `netstandard2.0` assembly, such as the frozen collections of System.Collections.Immutable and the spans of System.Memory.
- Every analyzer calls `EnableConcurrentExecution` and `ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None)`, and registers only the operation kinds it needs.
- Types are looked up once, in a compilation-start action, never in a per-operation callback; `PurityOracle.For(compilation)` gives all analyzers of a compilation one shared instance.
- Recursion checks `Limits.CanRecurse`, because a stack overflow in an analyzer kills the compiler or the IDE instead of reporting AD0001.
- Message text comes from `Quote.Of`, which runs only for a reported diagnostic.

The compiler and IDE run analyzers under the user's culture, so no verdict may depend on it:
- The code uses only ordinal and invariant string operations, and `.editorconfig` turns the .NET globalization rules (CA1304, CA1305, CA1307, CA1309, CA1310, CA1311) into build errors.
- `CultureIndependenceTests` run analyzers under Turkish culture, where `"i".ToUpper()` is `"İ"`, and first assert that the culture really behaves that way.
- `<InvariantGlobalization>` is deliberately not used: it does not apply to a library loaded by the compiler, and in the test host it would hide exactly the bugs these tests look for.

## Code style

- `.editorconfig` raises the style rules to build errors: braces on every body, `var` everywhere, file-scoped namespaces, pattern matching, switch expressions, sealed internal types (CA1852), static members (CA1822) and no unused usings (IDE0005).
- A comment says only what the code cannot: an invariant, a Roslyn quirk, why the obvious approach was rejected.
- A comment line holds one or more whole sentences and is never wrapped at a column; a sentence too long for one line is shortened or split in two.
- A rationale is stated once, at the contract it belongs to, and not again at each call site.
- Markdown follows the same layout: whole sentences per line, and one line per list item.

## Commits and pull requests

- Commit messages start with a type and no scope, as in `fix: …`, `docs: …`, `ci: …`, `build: …` or `refactor: …`.
- Work goes on a branch named for it, such as `fix/switch-sections`, and lands on `main` through a pull request.
- `ci.yml` builds and tests every push and pull request to `main` on Ubuntu and Windows.
- A change to Markdown alone skips CI, except in `README.md`, `PACKAGE.md` and the `AnalyzerReleases` files, which the build and the tests read.

## Release

`publish.yml` builds and tests again when a `v*.*.*` tag is pushed, then publishes to nuget.org:
1. Set `<Version>` in `Typo.Analyzers.csproj`; the workflow fails when the tag does not match it.
2. Move the released rules from `AnalyzerReleases.Unshipped.md` to `AnalyzerReleases.Shipped.md` under a `## Release x.y.z` header.
3. Push the tag, as in `git tag v0.1.0` and `git push origin v0.1.0`.

Publishing uses nuget.org trusted publishing instead of an API key.
It needs a `NUGET_USER` repository secret with the nuget.org profile name, and a nuget.org policy that trusts `publish.yml`.
The package readme is `PACKAGE.md`, because nuget.org shows the README's HTML anchors as text.
