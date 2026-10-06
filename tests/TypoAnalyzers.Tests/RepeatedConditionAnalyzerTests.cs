using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verify = TypoAnalyzers.Tests.Verifier<TypoAnalyzers.Analyzers.RepeatedConditionAnalyzer>;

namespace TypoAnalyzers.Tests;

public sealed class RepeatedConditionAnalyzerTests
{
    // Markup: {|#0:...|} is the reported condition, {|#1:...|} the earlier condition it repeats.
    private static DiagnosticResult Expect(DiagnosticDescriptor descriptor, params object[] arguments) =>
        Verify.Diagnostic(descriptor).WithLocation(0).WithLocation(1).WithArguments(arguments);

    [Fact]
    public Task ElseIfRepeatsEarlierCondition() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static int M(int a)
            {
                if ({|#1:a == 1|})
                {
                    return 1;
                }
                else if (a == 2)
                {
                    return 2;
                }
                else if ({|#0:a == 1|})
                {
                    return 3;
                }

                return 0;
            }
        }
        """,
        Expect(Descriptors.RepeatedElseIfCondition, "a == 1"));

    [Fact]
    public Task ElseIfRepeatsDisjunctOfEarlierCondition() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static int M(int a)
            {
                if ({|#1:a == 1 || a == 2|})
                {
                    return 1;
                }
                else if ({|#0:a == 2|})
                {
                    return 2;
                }

                return 0;
            }
        }
        """,
        Expect(Descriptors.RepeatedElseIfCondition, "a == 2"));

    [Fact]
    public Task DotNet10_AdjacentIfsWithSameCondition() => Verify.VerifyAsync(
        """
        public struct Descriptor
        {
            public int Kind0;
            public int Kind1;
        }

        public static class C
        {
            public static int Size(Descriptor descriptor)
            {
                var size = 0;
                if ({|#1:descriptor.Kind0 == 1|})
                {
                    size += 1;
                }

                if ({|#0:descriptor.Kind0 == 1|})
                {
                    size += 2;
                }

                return size;
            }
        }
        """,
        Expect(Descriptors.RepeatedAdjacentCondition, "descriptor.Kind0 == 1"));

    [Fact]
    public Task RepoDb_SecondCheckGuardsWrongCollection() => Verify.VerifyAsync(
        """
        using System;
        using System.Collections.Generic;

        public sealed class UpdateAllRequest
        {
            public IEnumerable<string> Fields { get; set; }
            public IEnumerable<string> Qualifiers { get; set; }

            public override int GetHashCode()
            {
                var hash = 0;
                if ({|#1:Fields != null|})
                {
                    foreach (var field in Fields)
                    {
                        hash = HashCode.Combine(hash, field);
                    }
                }

                if ({|#0:Fields != null|})
                {
                    foreach (var field in Qualifiers)
                    {
                        hash = HashCode.Combine(hash, field);
                    }
                }

                return hash;
            }
        }
        """,
        Expect(Descriptors.RepeatedAdjacentCondition, "Fields != null"));

    [Fact]
    public Task Nethermind_AdjacentIfsAfterThrow() => Verify.VerifyAsync(
        """
        public sealed class Buffer
        {
            public int ReadableBytes { get; set; }
        }

        public static class C
        {
            public static void Check(Buffer buffer)
            {
                if ({|#1:buffer.ReadableBytes > 0|})
                {
                    throw new System.Exception("first");
                }

                if ({|#0:buffer.ReadableBytes > 0|})
                {
                    throw new System.Exception("second");
                }
            }
        }
        """,
        Expect(Descriptors.RepeatedAdjacentCondition, "buffer.ReadableBytes > 0"));

    [Fact]
    public Task Nethermind_NestedRecurringCheck() => Verify.VerifyAsync(
        """
        public sealed class Logger
        {
            public bool IsInfo { get; set; }

            public void Info(string message)
            {
            }
        }

        public static class C
        {
            public static void Log(Logger logger)
            {
                if ({|#1:logger.IsInfo|})
                {
                    if ({|#0:logger.IsInfo|})
                    {
                        logger.Info("x");
                    }
                }
            }
        }
        """,
        Expect(Descriptors.RepeatedNestedCondition, "logger.IsInfo", "true"));

    [Fact]
    public Task NestedCheckInElseIsAlwaysFalse() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static int M(string x)
            {
                if ({|#1:x == null|})
                {
                    return 0;
                }
                else
                {
                    if ({|#0:x == null|})
                    {
                        return 1;
                    }
                }

                return 2;
            }
        }
        """,
        Expect(Descriptors.RepeatedNestedCondition, "x == null", "false"));

    [Fact]
    public Task NestedCheckRepeatsConjunct() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static int M(string x, int y)
            {
                if ({|#1:x != null|} && y > 0)
                {
                    if ({|#0:x != null|})
                    {
                        return 1;
                    }
                }

                return 0;
            }
        }
        """,
        Expect(Descriptors.RepeatedNestedCondition, "x != null", "true"));

    [Fact]
    public Task ElseIfRepeatsNullPattern() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static int M(string value)
            {
                if ({|#1:value is null|})
                {
                    return 0;
                }
                else if (value.Length == 0)
                {
                    return 1;
                }
                else if ({|#0:value is null|})
                {
                    return 2;
                }

                return 3;
            }
        }
        """,
        Expect(Descriptors.RepeatedElseIfCondition, "value is null"));

    [Fact]
    public Task NestedOppositeCheckIsAlwaysFalse() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static int M(string s)
            {
                if ({|#1:s == null|})
                {
                    if ({|#0:s != null|})
                    {
                        return 1;
                    }
                }

                return 0;
            }
        }
        """,
        Expect(Descriptors.RepeatedNestedCondition, "s != null", "false"));

    [Fact]
    public Task NestedOppositeTypeTestInElseIsAlwaysTrue() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static int M(object o)
            {
                if ({|#1:o is string|})
                {
                    return 0;
                }
                else
                {
                    if ({|#0:o is not string|})
                    {
                        return 1;
                    }
                }

                return 2;
            }
        }
        """,
        Expect(Descriptors.RepeatedNestedCondition, "o is not string", "true"));

    [Fact]
    public Task ElseIfRepeatsNullTestInAnotherSpelling() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static int M(string value)
            {
                if ({|#1:value == null|})
                {
                    return 0;
                }
                else if ({|#0:value is null|})
                {
                    return 1;
                }

                return 2;
            }
        }
        """,
        Expect(Descriptors.RepeatedElseIfCondition, "value is null"));

    [Fact]
    public Task AdjacentNullTestsInDifferentSpellings() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static int M(string value)
            {
                if ({|#1:value is null|})
                {
                    return 0;
                }

                if ({|#0:value == null|})
                {
                    return 1;
                }

                return 2;
            }
        }
        """,
        Expect(Descriptors.RepeatedAdjacentCondition, "value == null"));

    [Fact]
    public Task CallsCannotChangeUncapturedLocals() => Verify.VerifyAsync(
        """
        public static class C
        {
            private static bool Compute() => true;

            public static void M()
            {
                var ready = Compute();
                if ({|#1:!ready|})
                {
                    System.Console.WriteLine("a");
                }

                if ({|#0:!ready|})
                {
                    System.Console.WriteLine("b");
                }
            }
        }
        """,
        Expect(Descriptors.RepeatedAdjacentCondition, "!ready"));

    [Fact]
    public Task NoDiagnostics_WhenClosureChangesCapturedLocal() => Verify.VerifyAsync(
        """
        using System;

        public static class C
        {
            public static int AdjacentWithLocalFunction()
            {
                var ready = false;
                void Prepare()
                {
                    ready = true;
                }

                if (!ready)
                {
                    Prepare();
                }

                if (!ready)
                {
                    return 1;
                }

                return 0;
            }

            public static int NestedWithLocalFunction()
            {
                var ready = false;
                void Prepare()
                {
                    ready = true;
                }

                if (!ready)
                {
                    Prepare();
                    if (!ready)
                    {
                        return 2;
                    }
                }

                return 0;
            }

            public static int AdjacentWithLambda()
            {
                var count = 0;
                Action increment = () => count++;
                if (count == 0)
                {
                    increment();
                }

                if (count == 0)
                {
                    return 1;
                }

                return 0;
            }

            public static void InsideLambda(bool flag)
            {
                if (flag)
                {
                    Action action = () =>
                    {
                        if (flag)
                        {
                            Console.WriteLine();
                        }
                    };
                    action();
                }
            }
        }
        """);

    [Fact]
    public Task NoDiagnostics_ForUnrelatedOrImpureConditions() => Verify.VerifyAsync(
        """
        public static class C
        {
            private static int s_position;

            private static bool Advance() => ++s_position > 10;

            public static int Unrelated(int a, int b)
            {
                if (a > 0)
                {
                    if (b > 0)
                    {
                        return 1;
                    }
                }

                return 0;
            }

            public static int ImpureBetween(int a)
            {
                if (a == 1)
                {
                    return 1;
                }
                else if (Advance())
                {
                    return 2;
                }
                else if (a == 1)
                {
                    return 3;
                }

                return 0;
            }

            public static int DisjunctionEarlierWithoutMatch(int a)
            {
                if (a == 1 || a == 2)
                {
                    return 1;
                }
                else if (a == 3)
                {
                    return 2;
                }

                return 0;
            }

            public static int ConjunctionEarlier(int a, int b)
            {
                if (a > 0 && b > 0)
                {
                    return 1;
                }
                else if (a > 1)
                {
                    return 2;
                }

                return 0;
            }

            public static int ImpureInner(int a)
            {
                if (a > 0)
                {
                    if (Advance())
                    {
                        return 1;
                    }
                }

                return 0;
            }
        }
        """);

    [Fact]
    public Task NoDiagnostics_WhenConditionCanChange() => Verify.VerifyAsync(
        """
        using System.Collections.Generic;

        public static class C
        {
            private static bool Compute() => true;

            public static void Decrement(int count)
            {
                if (count > 0)
                {
                    count--;
                }

                if (count > 0)
                {
                    count = 0;
                }
            }

            public static void Drain(Queue<int> queue)
            {
                if (queue.Count > 0)
                {
                    queue.Dequeue();
                }

                if (queue.Count > 0)
                {
                    queue.Clear();
                }
            }

            public static void Recompute(bool flag)
            {
                if (flag)
                {
                    flag = Compute();
                    if (flag)
                    {
                        return;
                    }
                }
            }

            public static void Loop(int n)
            {
                if (n > 0)
                {
                    while (true)
                    {
                        if (n > 0)
                        {
                            n--;
                        }
                        else
                        {
                            break;
                        }
                    }
                }
            }

            public static int Different(int a)
            {
                if (a == 1)
                {
                    return 1;
                }
                else if (a == 2)
                {
                    return 2;
                }

                return 0;
            }
        }
        """);
}
