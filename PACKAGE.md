# Typo.Analyzers

Roslyn analyzers for copy-paste and typo bugs that the common open-source .NET analyzers don't cover.
They load in Visual Studio 2026 and every .NET 10 SDK, whatever framework the project targets.

## Rules

Each ID links to examples on GitHub.

| ID | Finds | Severity |
|---|---|---|
| [TYPO0001](https://github.com/unsafePtr/Typo.Analyzers#typo0001) | Identical sub-expressions | Warning |
| [TYPO0002](https://github.com/unsafePtr/Typo.Analyzers#typo0002) | Value compared with itself | Warning |
| [TYPO0003](https://github.com/unsafePtr/Typo.Analyzers#typo0003) | Condition repeated in an if-else chain | Warning |
| [TYPO0004](https://github.com/unsafePtr/Typo.Analyzers#typo0004) | `if` statement repeats the condition of an earlier one | Warning |
| [TYPO0005](https://github.com/unsafePtr/Typo.Analyzers#typo0005) | Condition already checked by an enclosing `if` | Warning |
| [TYPO0006](https://github.com/unsafePtr/Typo.Analyzers#typo0006) | Both branches are identical | Warning |
| [TYPO0007](https://github.com/unsafePtr/Typo.Analyzers#typo0007) | Two branches do the same thing | Warning |
| [TYPO0008](https://github.com/unsafePtr/Typo.Analyzers#typo0008) | Value overwritten before it is used | Warning |
| [TYPO0009](https://github.com/unsafePtr/Typo.Analyzers#typo0009) | Loop always reads the same element | Warning |
| [TYPO0010](https://github.com/unsafePtr/Typo.Analyzers#typo0010) | Comparison with a string of the wrong case | Warning |
| [TYPO0011](https://github.com/unsafePtr/Typo.Analyzers#typo0011) | Probable unedited copy in a sequence of similar lines | Info |
| [TYPO0012](https://github.com/unsafePtr/Typo.Analyzers#typo0012) | Probable unedited copy in a pair of similar lines | Info |
| [TYPO0013](https://github.com/unsafePtr/Typo.Analyzers#typo0013) | Comparison operand repeated on the other side | Info |
| [TYPO0014](https://github.com/unsafePtr/Typo.Analyzers#typo0014) | Condition checks the source of the variable just assigned | Info |

## Options

TYPO0012 reads one option from `.editorconfig`, which limits where it looks for pairs:

```ini
[*.cs]
TYPO0012.scan = declarations, arguments
```

The values are `statements`, `declarations`, `initializers`, `conditions` and `arguments`, and the default is all five.

Building with `-p:WarningLevel=0` hides every analyzer diagnostic, these included.
The [README](https://github.com/unsafePtr/Typo.Analyzers#known-gaps) lists the known gaps.
