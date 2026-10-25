using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verify = Typo.Tests.Verifier<Typo.Analyzers.AssignedVariableCheckAnalyzer>;

namespace Typo.Tests;

public sealed class AssignedVariableCheckAnalyzerTests
{
    // Markup: {|#0:...|} is the checked source variable in the condition.
    private static DiagnosticResult Checked(string assigned, string source) =>
        Verify.Diagnostic(Descriptors.CheckedSourceInsteadOfAssigned).WithLocation(0).WithArguments(assigned, source);

    [Fact]
    public Task FieldAssignedFromClampedParameter() => Verify.VerifyAsync(
        """
        public sealed class CustomType
        {
            private readonly int _value;

            public CustomType(int value)
            {
                _value = value < 0 ? 0 : value;

                if ({|#0:value|} == 0)
                {
                    System.Console.WriteLine("empty");
                }
            }
        }
        """,
        Checked("_value", "value"));

    [Fact]
    public Task LocalAssignedFromCallOnSimilarName() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static bool M(string path)
            {
                var _path = path.Trim();
                if ({|#0:path|}.Length == 0)
                {
                    return false;
                }

                return _path.Length > 3;
            }
        }
        """,
        Checked("_path", "path"));

    [Fact]
    public Task DeclarationFromTransformedSource() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static int M(int count)
            {
                var _count = System.Math.Max(count, 0);
                if ({|#0:count|} == 0)
                {
                    return -1;
                }

                return _count;
            }
        }
        """,
        Checked("_count", "count"));

    [Fact]
    public Task ConditionTestsAssignedVariableIsSilent() => Verify.VerifyAsync(
        """
        public sealed class CustomType
        {
            private readonly int _value;

            public CustomType(int value)
            {
                _value = value < 0 ? 0 : value;

                if (_value == 0)
                {
                    System.Console.WriteLine("empty");
                }
            }
        }
        """);

    [Fact]
    public Task PlainCopyIsSilent() => Verify.VerifyAsync(
        """
        public sealed class CustomType
        {
            private readonly string _name;

            public CustomType(string name)
            {
                _name = name;

                if (name == null)
                {
                    throw new System.ArgumentNullException(nameof(name));
                }
            }
        }
        """);

    // The guard either stores 'body' itself or throws.
    [Fact]
    public Task NullGuardedCopyIsSilent() => Verify.VerifyAsync(
        """
        public sealed class Reader
        {
            private readonly System.IO.Stream _body;
            private readonly long _length;

            public Reader(System.IO.Stream body)
            {
                _body = body ?? throw new System.ArgumentNullException(nameof(body));

                if (body.CanSeek)
                {
                    _length = body.Length;
                }
            }
        }
        """);

    [Fact]
    public Task CoalesceWithFallbackStillReports() => Verify.VerifyAsync(
        """
        public sealed class CustomType
        {
            private readonly string _name;

            public CustomType(string name)
            {
                _name = name ?? "unknown";

                if ({|#0:name|} == null)
                {
                    System.Console.WriteLine("anonymous");
                }
            }
        }
        """,
        Checked("_name", "name"));

    [Fact]
    public Task SourceWithDifferentNameIsSilent() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static bool M(string input)
            {
                var parsed = input.Trim();
                if (input.Length == 0)
                {
                    return false;
                }

                return parsed.Length > 3;
            }
        }
        """);

    [Fact]
    public Task ConditionReadingBothIsSilent() => Verify.VerifyAsync(
        """
        public sealed class CustomType
        {
            private readonly int _value;

            public CustomType(int value)
            {
                _value = value < 0 ? 0 : value;

                if (value != _value)
                {
                    System.Console.WriteLine("clamped");
                }
            }
        }
        """);

    [Fact]
    public Task StatementBetweenAssignmentAndConditionIsSilent() => Verify.VerifyAsync(
        """
        public sealed class CustomType
        {
            private readonly int _value;

            public CustomType(int value)
            {
                _value = value < 0 ? 0 : value;
                System.Console.WriteLine(_value);

                if (value == 0)
                {
                    System.Console.WriteLine("empty");
                }
            }
        }
        """);

    [Fact]
    public Task SameNameButDifferentTypeIsSilent() => Verify.VerifyAsync(
        """
        public sealed class Builder
        {
            public bool EnableStatistics;
        }

        public sealed class Config
        {
            public bool EnableStatistics;
        }

        public sealed class Manager
        {
            private readonly Config _options = new();

            public void Setup()
            {
                var options = new Builder { EnableStatistics = _options.EnableStatistics };
                if (_options.EnableStatistics)
                {
                    System.Console.WriteLine(options);
                }
            }
        }
        """);

    [Fact]
    public Task NameOnlyContainingTheSourceNameIsSilent() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static bool M(string path)
            {
                var normalPath = path.Trim();
                if (path.Length == 0)
                {
                    return false;
                }

                return normalPath.Length > 3;
            }
        }
        """);
}
