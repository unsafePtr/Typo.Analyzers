# Typo.Analyzers

Roslyn analyzers for copy-paste and typo bugs that the common open-source .NET analyzers don't cover.
They load in Visual Studio 2026 and every .NET 10 SDK, whatever framework the project targets.

The rules favour recall over precision, because a false positive costs a reader a glance while a missed bug ships:
1. Report every potential problem, even when some of the flagged code turns out to be correct.
2. Skip a finding only when it is certainly a false positive, that is, when the analyzer can show the code is meant that way.

A rule is never narrowed to cut noise when that also loses real bugs.
The skip has to match only the correct case, such as two identical reads with a mutating call on the same struct between them.
Code that only might make a repeat legitimate does not count.
A write between two equal checks may just make the second one dead: `if (!map.ContainsKey(k)) { map[k] = 0; }` twice.
TYPO0011 and TYPO0012 look for inherently noisy patterns, so they report at Info severity instead of staying silent.

## Rules

| ID | Analyzer | Finds |
|---|---|---|
| <a id="typo0001"></a>TYPO0001 | IdenticalOperandsAnalyzer | Repeated operand in a `\|\|` / `&&` / `\|` / `&` / `^` / `or` / `and` chain; identical operands of a comparison, `-`, `/` or `%` |
| <a id="typo0002"></a>TYPO0002 | SelfArgumentAnalyzer | `x.Equals(x)`, `x.CompareTo(x)`, `Equals(a, a)`, `comparer.Compare(a, a)`, `Math.Max(a, a)`, `s.Contains(s)`, `s.Replace(a, a)`, `s.IndexOf(s)`, `a.Except(a)` |
| <a id="typo0003"></a>TYPO0003 | RepeatedConditionAnalyzer | `else if` repeating a condition, or one disjunct of a condition, tested earlier in the chain |
| <a id="typo0004"></a>TYPO0004 | RepeatedConditionAnalyzer | An `if` repeating the condition of the `if` right before it, or of an earlier `if` that always exits, when nothing in between can change it |
| <a id="typo0005"></a>TYPO0005 | RepeatedConditionAnalyzer | Nested `if` or later `else if` repeating, or negating, a check of an enclosing `if`, as in `if (s == null) … else if (s != null)` |
| <a id="typo0006"></a>TYPO0006 | IdenticalBranchesAnalyzer | Identical `then` and `else` parts of `if` or `?:` |
| <a id="typo0007"></a>TYPO0007 | IdenticalBranchesAnalyzer | Two `switch` cases, switch-expression arms or else-if branches with identical non-trivial bodies |
| <a id="typo0008"></a>TYPO0008 | RepeatedAssignmentAnalyzer | Field, auto-property, array element or collection entry written twice with no read in between |
| <a id="typo0009"></a>TYPO0009 | LoopIndexAnalyzer | `for` loop over a collection (forward, `Count - 1` or reverse) that reads a constant index such as `[0]` and uses the loop variable, if at all, only to index other collections |
| <a id="typo0010"></a>TYPO0010 | StringCaseAnalyzer | Lowercased or uppercased value compared with, or searched for, text with letters of the other case |
| <a id="typo0011"></a>TYPO0011 | SimilarRunAnalyzer | One item of a run of three or more similar statements, declarations, initializer members, conditions or arguments repeating a neighbour's name, as in `Z += other.Y`, `maxY = Math.Max(maxY, p.X)` or `new Rect(r.Left, r.Top, r.Right, r.Right)` (Info severity) |
| <a id="typo0012"></a>TYPO0012 | SimilarRunAnalyzer | The second of two similar statements, declarations, initializer members, conditions or arguments keeping a name built from the first one's word, as in `Height == other.Width`, `var minY = pts.Min(p => p.X)` or `new Vec(p.X, p.X)` (Info severity) |
| <a id="typo0013"></a>TYPO0013 | ComparisonOperandAnalyzer | A comparison of a variable with a sum, difference or product that contains the same variable, as in `x == x + 1` or `x < x * y` (Info severity) |
| <a id="typo0014"></a>TYPO0014 | AssignedVariableCheckAnalyzer | A variable assigned a transformed copy of a similarly named one, followed by an `if` that tests the original, as in `_value = value < 0 ? 0 : value; if (value == 0)` (Info severity) |

## Options

TYPO0012 reads one option from `.editorconfig` or a global analyzer config, so it can differ per file:

```ini
[*.cs]
TYPO0012.scan = declarations, arguments
```

`TYPO0012.scan` lists where pairs are looked for, and the default is all five:
- `statements`: expression statements in a block or a switch section, as in `X = b.X; Y = b.X;`
- `declarations`: initialized locals, as in `var minY = pts.Min(p => p.X)`
- `initializers`: members of an object or `with` initializer
- `conditions`: operands of a `&&` or `||` chain
- `arguments`: the arguments of one call or `new`, as in `new Vec(p.X, p.X)`

`declarations, arguments` keeps only pairs keyed by a declared local or a parameter name.
Names are matched ignoring case, unknown names are skipped, and a value with no known name keeps all five.
TYPO0011 does not read this option.

Building with `-p:WarningLevel=0` hides every analyzer diagnostic, these included, even ones raised to errors in `.editorconfig`.

## Culture independence

The compiler and IDE run analyzers under the user's culture, and no verdict depends on it.
The analyzers use only ordinal and invariant string operations, and tests run them under Turkish culture, where `"i".ToUpper()` is `"İ"`.

## Known gaps

- TYPO0007 skips bodies without a call, object or array creation, assignment or increment, and bodies that are just a `throw`.
- In TYPO0011 and TYPO0012, numeric literals that are not indexes count as table data and never form a pattern.
- Deliberate copies still fire: TYPO0012 on `Start = c.Start; End = c.Start;`, on `new Size(b.Width, b.Width)` and on a fixed operand kept while the name after it is renamed, as `i0` in `IsNaN(data[i0, j0]) || IsNaN(data[i0, j1])`, and TYPO0011 on symmetric matrices `new M(m.M11, m.M12, m.M12, m.M22)`; such lines are suppressed one at a time with `#pragma warning disable` or `[SuppressMessage]`.
- TYPO0011 and TYPO0012 skip a call whose arguments are plain names that spell the containing member's name in order, as the swizzle `Yyzw => new(Y, Y, Z, W)` does; `Xyzw => new(Y, Y, Z, W)` still fires.
- TYPO0012 never pairs literal copies, all-lower-case compound names such as `dx` and `dy`, or names where one extends the other, such as `Name` and `DisplayName`.
- TYPO0012 skips pairs that also differ in a number, framework values such as `float.MaxValue`, `Index.Start` or `Vector2.UnitX`, and names inside an access or call the second line already renamed.
- TYPO0012 reports `f(x, x)` only when the `y` it should have been is never used, so a deliberate square such as `Multiply(x, x)` stays silent unless the function has a parameter it never reads that could stand in.
- TYPO0011 and TYPO0012 report only a suggestion that compiles and does not assign a field to itself, so `this.dv = constant` after `this.constant = constant` is silent when the only `dv` is that field and every parameter is read.
- When the renamed name of a local or parameter copy cannot be used, they suggest a field `_name`, `m_name` or `s_name` that is neither the target nor its backing field, and then a parameter the function never reads; a candidate with a usable plain name always wins over these.
- The type of a suggestion is checked only where the target type is fixed: an assignment, an explicitly typed declaration or a call argument.
- TYPO0012 does not report the second of two lines when the first holds one value twice, as in `Move(d.X, d.X); Move(d.X, d.Y)`, because the second line is then the first one's fix.
- Declarations that read a local declared earlier in the same run, as in `f8 = f4 * f4; f12 = f8 * f4`, are steps of one computation and never form a run.
- A shared receiver whose other names were renamed still fires, as in `LoadUnsafe(ref yRef, x.Length - n)` after a check that both spans have equal length.
- TYPO0011 needs one column whose values all differ, so a two-by-two block such as `a.Stock = a.Sum(x => x.Stock); a.Reserved = a.Sum(x => x.Stock); b.Stock = …; b.Reserved = …` forms no run.
- TYPO0009 stays silent when the loop variable is used for anything but indexing other collections, as in `(i == 0 ? "" : ",") + Name(items[0])`, because `if (i == 0 && items[0] > 0)` has the same shape on purpose.
- Operands of `+` and `*` match in either order, except string concatenation, delegate combination and the `Matrix3x2`, `Matrix4x4` and `Quaternion` products of System.Numerics; another library's matrix product, such as WPF's `Matrix`, is still taken to commute.
- TYPO0001 does not treat `x.HasValue` and `x != null` as the same test; such pairs are redundant rather than copy-paste mistakes.
- TYPO0001 leaves a comparison of a local, parameter or field with itself, as in `count < count`, to the compiler's CS1718, which reports exactly these; properties, elements and calls stay TYPO0001's.
- Swapped arguments matched by parameter name are left to Sonar S2234.
- Among statements, TYPO0011 and TYPO0012 pair only expression statements and initialized declarations, so an `if` between two similar lines ends the run and declarations separated by other statements are not paired.
- TYPO0008 ignores locals and parameters, which the compiler's unused-assignment check IDE0059 covers.
- TYPO0008 pairs a null-conditional write only with one of the same shape, so `a?.X = 1; a.X = 2;` is not reported.
- TYPO0002 does not flag an arbitrary user method called with its own receiver, as in `a.Foo(a)`, because `list.AddRange(list)` style calls are common.

## Build and test

```shell
dotnet restore
dotnet build -v:q --no-restore
dotnet run --project tests/Typo.Tests/Typo.Tests.csproj
```

[AGENTS.md](AGENTS.md) covers the code layout, test options, coverage, formatting, conventions and releases.
