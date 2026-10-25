using Xunit;
using Verify = Typo.Tests.Verifier<Typo.Analyzers.LoopIndexAnalyzer>;

namespace Typo.Tests;

public sealed class LoopIndexAnalyzerTests
{
    [Fact]
    public Task Nethermind_LoopAlwaysReadsFirstElement() => Verify.VerifyAsync(
        """
        public sealed class Info
        {
            public int Hash;
        }

        public sealed class Level
        {
            public Info[] BlockInfos = [];
        }

        public static class C
        {
            public static bool Contains(Level currentLevel, int hash)
            {
                for (var i = 0; i < currentLevel.BlockInfos.Length; i++)
                {
                    if ({|#0:currentLevel.BlockInfos[0]|}.Hash == hash)
                    {
                        return true;
                    }
                }

                return false;
            }
        }
        """,
        Verify.Diagnostic(Descriptors.ConstantIndexInLoop).WithLocation(0).WithArguments("currentLevel.BlockInfos", "i", "currentLevel.BlockInfos[0]"));

    [Fact]
    public Task Runtime_RefToFirstElementIsBufferStart() => Verify.VerifyAsync(
        """
        using System;

        public static class C
        {
            private static ref T Add<T>(ref T source, nint offset) => ref source;

            private static void Write(ref int destination, int value) => destination = value;

            public static void Fill(Span<int> result, int[] values)
            {
                nint destinationIndex = -1;
                for (nint i = 0; i < result.Length; i++)
                {
                    destinationIndex++;
                    Write(ref Add(ref result[0], destinationIndex), values[i]);
                }

                for (var i = 0; i < values.Length; i++)
                {
                    ref var start = ref values[0];
                    Write(ref Add(ref start, i), 0);
                }
            }
        }
        """);

    [Fact]
    public Task ListIndexerWithConstant() => Verify.VerifyAsync(
        """
        using System.Collections.Generic;

        public static class C
        {
            public static bool AllPositive(List<int> seq)
            {
                for (var i = 0; i < seq.Count; i++)
                {
                    if ({|#0:seq[0]|} <= 0)
                    {
                        return false;
                    }
                }

                return true;
            }
        }
        """,
        Verify.Diagnostic(Descriptors.ConstantIndexInLoop).WithLocation(0).WithArguments("seq", "i", "seq[0]"));

    [Fact]
    public Task StringIndexerWithConstant() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static int CountSpaces(string text)
            {
                var count = 0;
                for (var i = 0; i < text.Length; i++)
                {
                    if ({|#0:text[0]|} == ' ')
                    {
                        count++;
                    }
                }

                return count;
            }
        }
        """,
        Verify.Diagnostic(Descriptors.ConstantIndexInLoop).WithLocation(0).WithArguments("text", "i", "text[0]"));

    [Fact]
    public Task SpanIndexerWithConstant() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static int Sum(System.ReadOnlySpan<int> values)
            {
                var total = 0;
                for (var i = 0; i < values.Length; i++)
                {
                    total += {|#0:values[0]|};
                }

                return total;
            }
        }
        """,
        Verify.Diagnostic(Descriptors.ConstantIndexInLoop).WithLocation(0).WithArguments("values", "i", "values[0]"));

    [Fact]
    public Task BoundWrittenAsCountMinusOne() => Verify.VerifyAsync(
        """
        using System.Collections.Generic;

        public static class C
        {
            public static int Sum(List<int> list)
            {
                var total = 0;
                for (var i = 0; i <= list.Count - 1; i++)
                {
                    total += {|#0:list[0]|};
                }

                return total;
            }
        }
        """,
        Verify.Diagnostic(Descriptors.ConstantIndexInLoop).WithLocation(0).WithArguments("list", "i", "list[0]"));

    [Fact]
    public Task ReverseLoop() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static int Sum(int[] items)
            {
                var total = 0;
                for (var i = items.Length - 1; i >= 0; i--)
                {
                    total += {|#0:items[0]|};
                }

                return total;
            }
        }
        """,
        Verify.Diagnostic(Descriptors.ConstantIndexInLoop).WithLocation(0).WithArguments("items", "i", "items[0]"));

    [Fact]
    public Task BoundOnTheLeft() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static int Sum(int[] items)
            {
                var total = 0;
                for (var i = 0; items.Length > i; i++)
                {
                    total += {|#0:items[0]|};
                }

                return total;
            }
        }
        """,
        Verify.Diagnostic(Descriptors.ConstantIndexInLoop).WithLocation(0).WithArguments("items", "i", "items[0]"));

    [Fact]
    public Task LongLengthBoundWithWidenedIndex() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static long Sum(long[] items)
            {
                long total = 0;
                for (var i = 0; i < items.LongLength; i++)
                {
                    total += {|#0:items[0]|};
                }

                return total;
            }
        }
        """,
        Verify.Diagnostic(Descriptors.ConstantIndexInLoop).WithLocation(0).WithArguments("items", "i", "items[0]"));

    [Fact]
    public Task NoDiagnostics_ForUnsupportedShapes() => Verify.VerifyAsync(
        """
        public static class C
        {
            private static int[] GetItems() => new int[3];

            public static int Impure()
            {
                var total = 0;
                for (var i = 0; i < GetItems().Length; i++)
                {
                    total += GetItems()[0];
                }

                return total;
            }

            public static int NotEquals(int[] items)
            {
                var total = 0;
                for (var i = 0; i != items.Length; i++)
                {
                    total += items[0];
                }

                return total;
            }
        }
        """);

    [Fact]
    public Task NoDiagnostics_WhenLoopVariableIsUsed() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static int Sum(int[] items)
            {
                var total = 0;
                for (var i = 0; i < items.Length; i++)
                {
                    total += items[i];
                }

                return total;
            }

            public static bool FirstIsSpecial(int[] items)
            {
                for (var i = 0; i < items.Length; i++)
                {
                    if (i == 0 && items[0] > 0)
                    {
                        return true;
                    }
                }

                return false;
            }

            public static int Other(int[] items, int[] header)
            {
                var total = 0;
                for (var i = 0; i < items.Length; i++)
                {
                    total += header[0];
                }

                return total;
            }

            public static void FirstGoesToEveryOtherSlot(int[] items, int[] result)
            {
                for (var i = 0; i < items.Length; i++)
                {
                    result[i] = i == 0 ? items[0] : 0;
                }
            }
        }
        """);

    [Fact]
    public Task PascalAbc_InnerLoopReadsFirstIdentifier() => Verify.VerifyAsync(
        """
        using System.Collections.Generic;

        public sealed class Ident
        {
        }

        public sealed class IdentList
        {
            public List<Ident> idents = new List<Ident>();
        }

        public sealed class TypedParameters
        {
            public IdentList idents = new IdentList();
        }

        public sealed class FormalParameters
        {
            public List<TypedParameters> params_list = new List<TypedParameters>();
        }

        public sealed class FunctionLambdaDefinition
        {
            public FormalParameters formal_parameters = new FormalParameters();
        }

        public sealed class Location
        {
        }

        public sealed class SyntaxTreeVisitor
        {
            public Location get_location(Ident ident) => new Location();
        }

        public static class LambdaHelper
        {
            public static List<Location> GetTempFunctionNodeForTypeInference(FunctionLambdaDefinition def, SyntaxTreeVisitor visitor)
            {
                var locations = new List<Location>();
                for (int i = 0; i < def.formal_parameters.params_list.Count; i++)
                {
                    for (int j = 0; j < def.formal_parameters.params_list[i].idents.idents.Count; j++)
                    {
                        locations.Add(visitor.get_location({|#0:def.formal_parameters.params_list[i].idents.idents[0]|}));
                    }
                }

                return locations;
            }
        }
        """,
        Verify.Diagnostic(Descriptors.ConstantIndexInLoop).WithLocation(0).WithArguments("def.formal_parameters.params_list[i].idents.idents", "j", "def.formal_parameters.params_list[i].idents.idents[0]"));

    [Fact]
    public Task Telerik_ArrayComparerComparesFirstElements() => Verify.VerifyAsync(
        """
        using System.Collections.Generic;

        public sealed class ArrayComparer : IEqualityComparer<object[]>
        {
            public bool Equals(object[] x, object[] y)
            {
                if (x.Length != y.Length)
                {
                    return false;
                }

                for (int i = 0; i < x.Length; i++)
                {
                    if (!object.Equals({|#0:x[0]|}, y[0]))
                    {
                        return false;
                    }
                }

                return true;
            }

            public int GetHashCode(object[] obj) => obj.Length;
        }
        """,
        Verify.Diagnostic(Descriptors.ConstantIndexInLoop).WithLocation(0).WithArguments("x", "i", "x[0]"));

    [Fact]
    public Task SharpDevelop_LoopFromOneReadsFirstPoint() => Verify.VerifyAsync(
        """
        using System;
        using System.Collections.Generic;

        public sealed class CodeCoverageSequencePoint
        {
            public int Line;
        }

        public sealed class CodeCoverageMethod
        {
            public List<CodeCoverageSequencePoint> SequencePoints { get; } = new List<CodeCoverageSequencePoint>();
        }

        public sealed class CodeCoverageMethodTreeNode
        {
            private readonly CodeCoverageMethod method;

            public CodeCoverageMethodTreeNode(CodeCoverageMethod method)
            {
                this.method = method;
            }

            public int ActivateItem()
            {
                var line = 0;
                if (method != null && method.SequencePoints.Count > 0)
                {
                    CodeCoverageSequencePoint firstSequencePoint = method.SequencePoints[0];
                    line = firstSequencePoint.Line;
                    for (int i = 1; i < method.SequencePoints.Count; ++i)
                    {
                        CodeCoverageSequencePoint sequencePoint = {|#0:method.SequencePoints[0]|};
                        line = Math.Min(line, sequencePoint.Line);
                    }
                }

                return line;
            }
        }
        """,
        Verify.Diagnostic(Descriptors.ConstantIndexInLoop).WithLocation(0).WithArguments("method.SequencePoints", "i", "method.SequencePoints[0]"));

    [Fact]
    public Task Mono_GlobalAttributesReadFirstOnly() => Verify.VerifyAsync(
        """
        using System.Collections.Generic;

        public sealed class Attribute
        {
            public string ExplicitTarget;
        }

        public sealed class Attributes
        {
            public List<Attribute> Attrs = new List<Attribute>();

            public int ConvertGlobalAttributes(bool isGlobal)
            {
                var converted = 0;
                for (int i = 0; i < Attrs.Count; ++i)
                {
                    var attr = {|#0:Attrs[0]|};
                    if (attr.ExplicitTarget == null)
                        continue;
                    converted++;
                }

                return converted;
            }
        }
        """,
        Verify.Diagnostic(Descriptors.ConstantIndexInLoop).WithLocation(0).WithArguments("Attrs", "i", "Attrs[0]"));

    [Fact]
    public Task Accord_RectangularCheckReadsFirstRow() => Verify.VerifyAsync(
        """
        using System;

        public sealed class DimensionMismatchException : Exception
        {
            public DimensionMismatchException(string paramName, string message)
                : base(message)
            {
            }
        }

        public sealed class BinarySplit
        {
            public int[] Compute(double[][] data, double[] weights)
            {
                int cols = data[0].Length;
                for (int i = 0; i < data.Length; i++)
                    if ({|#0:data[0]|}.Length != cols)
                        throw new DimensionMismatchException("data", "The points matrix should be rectangular. The vector at position {} has a different length than previous ones.");
                return new int[data.Length];
            }
        }
        """,
        Verify.Diagnostic(Descriptors.ConstantIndexInLoop).WithLocation(0).WithArguments("data", "i", "data[0]"));

    [Fact]
    public Task Orleans_FirstParameterComparedWithEveryType() => Verify.VerifyAsync(
        """
        using System;
        using System.Reflection;

        public static class OrleansGeneratedCodeHelper
        {
            public static MethodInfo GetMethodInfoOrDefault(Type interfaceType, string methodName, Type[] parameterTypes)
            {
                foreach (var method in interfaceType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (method.Name != methodName)
                    {
                        continue;
                    }

                    var parameters = method.GetParameters();
                    if (parameters.Length != parameterTypes.Length)
                    {
                        continue;
                    }

                    for (int i = 0; i < parameters.Length; i++)
                    {
                        if (!{|#0:parameters[0]|}.ParameterType.Equals(parameterTypes[i]))
                        {
                            continue;
                        }
                    }

                    return method;
                }

                return null;
            }
        }
        """,
        Verify.Diagnostic(Descriptors.ConstantIndexInLoop).WithLocation(0).WithArguments("parameters", "i", "parameters[0]"));
}
