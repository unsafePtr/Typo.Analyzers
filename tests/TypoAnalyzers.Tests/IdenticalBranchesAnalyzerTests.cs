using Xunit;
using Verify = TypoAnalyzers.Tests.Verifier<TypoAnalyzers.Analyzers.IdenticalBranchesAnalyzer>;

namespace TypoAnalyzers.Tests;

public sealed class IdenticalBranchesAnalyzerTests
{
    [Fact]
    public Task IfAndElseIdentical() => Verify.VerifyAsync(
        """
        using System.Collections.Generic;

        public static class C
        {
            public static void M(bool condition, List<int> list)
            {
                if ({|#0:condition|})
                {|#1:{
                    list.Add(1);
                }|}
                else
                {|#2:{
                    list.Add(1);
                }|}
            }
        }
        """,
        Verify.Diagnostic(Descriptors.IdenticalBranches).WithLocation(0).WithLocation(1).WithLocation(2).WithArguments("if", "condition"));

    [Fact]
    public Task TernaryArmsIdentical() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static int M(int a, int x) => {|#0:a > 0|} ? {|#1:x|} : {|#2:x|};
        }
        """,
        Verify.Diagnostic(Descriptors.IdenticalBranches).WithLocation(0).WithLocation(1).WithLocation(2).WithArguments("?:", "a > 0"));

    [Fact]
    public Task DotNet10_SwitchCasePassesNeighbouringEnum() => Verify.VerifyAsync(
        """
        public enum HelperId { TypeHandle, MethodHandle, MethodDictionary }

        public enum FixupKind { TypeHandle, MethodHandle, MethodDictionary }

        public static class C
        {
            public static int Map(HelperId id)
            {
                switch (id)
                {
                    case HelperId.TypeHandle:
                        return Lookup(FixupKind.TypeHandle);
                    {|#1:case HelperId.MethodHandle:|}
                        return Lookup(FixupKind.MethodHandle);
                    {|#0:case HelperId.MethodDictionary:|}
                        return Lookup(FixupKind.MethodHandle);
                    default:
                        return -1;
                }
            }

            private static int Lookup(FixupKind kind) => (int)kind;
        }
        """,
        Verify.Diagnostic(Descriptors.DuplicateBranchBody).WithLocation(0).WithLocation(1).WithArguments("HelperId.MethodHandle"));

    [Fact]
    public Task SwitchExpressionArmsIdentical() => Verify.VerifyAsync(
        """
        public enum HelperId { TypeHandle, MethodHandle, MethodDictionary }

        public enum FixupKind { TypeHandle, MethodHandle, MethodDictionary }

        public static class C
        {
            public static int Map(HelperId id) => id switch
            {
                HelperId.TypeHandle => Lookup(FixupKind.TypeHandle),
                HelperId.MethodHandle => {|#1:Lookup(FixupKind.MethodHandle)|},
                HelperId.MethodDictionary => {|#0:Lookup(FixupKind.MethodHandle)|},
                _ => -1,
            };

            private static int Lookup(FixupKind kind) => (int)kind;
        }
        """,
        Verify.Diagnostic(Descriptors.DuplicateBranchBody).WithLocation(0).WithLocation(1).WithArguments("HelperId.MethodHandle"));

    [Fact]
    public Task ElseIfBranchesIdentical() => Verify.VerifyAsync(
        """
        public static class C
        {
            private static void Log(string message)
            {
            }

            public static void M(int a)
            {
                if (a > 0)
                {
                    {|#1:Log("positive");|}
                }
                else if (a < 0)
                {
                    {|#0:Log("positive");|}
                }
            }
        }
        """,
        Verify.Diagnostic(Descriptors.DuplicateBranchBody).WithLocation(0).WithLocation(1).WithArguments("a > 0"));

    [Fact]
    public Task FinalElseDuplicatesEarlierBranch() => Verify.VerifyAsync(
        """
        public static class C
        {
            private static void Log(string message)
            {
            }

            public static void M(int a)
            {
                if (a > 0)
                {
                    {|#1:Log("x");|}
                }
                else if (a < 0)
                {
                    Log("y");
                }
                else
                {
                    {|#0:Log("x");|}
                }
            }
        }
        """,
        Verify.Diagnostic(Descriptors.DuplicateBranchBody).WithLocation(0).WithLocation(1).WithArguments("a > 0"));

    [Fact]
    public Task CaseDuplicatesDefaultSection() => Verify.VerifyAsync(
        """
        public enum Kind { A, B }

        public static class C
        {
            private static int Lookup(int value) => value;

            public static int Map(Kind kind)
            {
                switch (kind)
                {
                    {|#1:default:|}
                        return Lookup(0);
                    {|#0:case Kind.B:|}
                        return Lookup(0);
                }
            }
        }
        """,
        Verify.Diagnostic(Descriptors.DuplicateBranchBody).WithLocation(0).WithLocation(1).WithArguments("default"));

    [Fact]
    public Task NoDiagnostics_ForTrivialElseIfBodies() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static int M(int a)
            {
                if (a > 0)
                {
                    return a * 2;
                }
                else if (a < 0)
                {
                    return a * 2;
                }

                return 0;
            }
        }
        """);

    [Fact]
    public Task SameTypedPatternVariablesAreStillDuplicates() => Verify.VerifyAsync(
        """
        public sealed class Circle
        {
            public double Area() => 1;
        }

        public static class C
        {
            private static double Log(Circle circle) => circle.Area();

            public static double M(object shape)
            {
                switch (shape)
                {
                    {|#1:case Circle s when s.Area() > 10:|}
                        return Log(s);
                    {|#0:case Circle s:|}
                        return Log(s);
                    default:
                        return 0;
                }
            }
        }
        """,
        Verify.Diagnostic(Descriptors.DuplicateBranchBody).WithLocation(0).WithLocation(1).WithArguments("Circle s"));

    [Fact]
    public Task NoDiagnostics_ForPatternVariablesOfDifferentTypes() => Verify.VerifyAsync(
        """
        public abstract class Shape
        {
        }

        public sealed class Circle : Shape
        {
            public double Area() => 1;
        }

        public sealed class Square : Shape
        {
            public double Area() => 2;
        }

        public static class C
        {
            public static double Area(Shape shape)
            {
                switch (shape)
                {
                    case Circle s:
                        return s.Area();
                    case Square s:
                        return s.Area();
                    default:
                        return 0;
                }
            }

            public static double AreaExpression(Shape shape) => shape switch
            {
                Circle s => s.Area(),
                Square s => s.Area(),
                _ => 0,
            };
        }
        """);

    [Fact]
    public Task NoDiagnostics_ForIdiomaticDuplicates() => Verify.VerifyAsync(
        """
        using System;

        public enum Kind { A, B, C }

        public static class C
        {
            public static bool IsSpecial(Kind kind) => kind switch
            {
                Kind.A => true,
                Kind.B => true,
                _ => false,
            };

            public static int Throwing(Kind kind)
            {
                switch (kind)
                {
                    case Kind.A:
                        throw new NotSupportedException();
                    case Kind.B:
                        throw new NotSupportedException();
                    default:
                        return 0;
                }
            }

            public static int Different(bool flag, int x, int y)
            {
                if (flag)
                {
                    return x;
                }
                else
                {
                    return y;
                }
            }

            public static void Empty(bool flag)
            {
                if (flag)
                {
                }
                else
                {
                }
            }
        }
        """);
}
