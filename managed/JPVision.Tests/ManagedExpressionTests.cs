using JPVision.Sdk;
using System.Text.Json.Nodes;
using Xunit;

namespace JPVision.Tests;

public sealed class ManagedExpressionTests
{
    private static JsonNode Evaluate(string expression, JsonObject? functions = null) =>
        ManagedExpression.Evaluate(expression, _ => null, functions ?? new());

    [Theory]
    [InlineData("1 + 2 * 3", 7)]
    [InlineData("(1 + 2) * 3", 9)]
    [InlineData("20 / 5 / 2", 2)]
    [InlineData("10 - 3 - 2", 5)]
    [InlineData("-2 * +3 + .5 + 1.5e1", 9.5)]
    [InlineData("17 % 5", 2)]
    [InlineData("-17 % 5", -2)]
    [InlineData("abs(-3) + min(4, 5) + max(2, 7)", 14)]
    [InlineData("clamp(100, 2, 10)", 10)]
    [InlineData("sqrt(9) + pow(2, 3)", 11)]
    [InlineData("round(-1.5) + floor(2.9) + ceil(3.1)", 4)]
    [InlineData("round(2.5)", 3)]
    [InlineData("round(-2.5)", -3)]
    [InlineData("sin(pi / 2) + cos(0) + tan(0)", 2)]
    [InlineData("atan2(0, 1) + hypot(3, 4)", 5)]
    [InlineData("e - e", 0)]
    [InlineData("false ? 1 / 0 : 2", 2)]
    [InlineData("true ? 3 : sqrt(-1)", 3)]
    [InlineData("false ? 1 : true ? 2 : 3", 2)]
    public void ArithmeticAndFunctionsPreserveNativePrecedence(string expression, double expected) =>
        Assert.Equal(expected, Evaluate(expression).GetValue<double>(), 10);

    [Theory]
    [InlineData("1 < 2 && 3 <= 3 && 5 > 4 && 4 >= 4", true)]
    [InlineData("!false || false", true)]
    [InlineData("1 + 1 == 2 && 3 != 4", true)]
    [InlineData("true == true && false != true", true)]
    [InlineData("false && (1 / 0 > 0)", false)]
    [InlineData("true || (sqrt(-1) > 0)", true)]
    public void BooleanOperationsPreserveTypesAndShortCircuit(string expression, bool expected) =>
        Assert.Equal(expected, Evaluate(expression).GetValue<bool>());

    [Fact]
    public void UnselectedConditionalBranchNeverReadsUnavailableResult()
    {
        int lookups = 0;
        var value = ManagedExpression.Evaluate("true ? 2 : missing", _ => { ++lookups; return null; }, new());
        Assert.Equal(2, value.GetValue<double>());
        Assert.Equal(0, lookups);
    }

    [Fact]
    public void SymbolsAreDistinctQualifiedNamesRatherThanFunctionFormals()
    {
        var functions = Functions();
        Assert.Equal(new[] { "centerX", "global.OffsetX", "local.shift" },
            ManagedExpression.Symbols("centerX + global.OffsetX + local.shift + centerX", new()).Order());
        Assert.Equal(new[] { "global.X", "x" },
            ManagedExpression.Symbols("adjust(x) + offset(global.X, 2)", functions).Order());
        var segment = new string('a', 64);
        Assert.Equal(new[] { segment + "." + segment }, ManagedExpression.Symbols(segment + "." + segment, new()));
    }

    [Fact]
    public void SymbolValuesKeepTheirJsonTypesAndDoNotReparentCallerNodes()
    {
        var values = new JsonObject { ["x"] = 20L, ["global.OffsetX"] = 3, ["local.shift"] = .5, ["ok"] = true };
        Assert.Equal(23.5, ManagedExpression.Evaluate("x + global.OffsetX + local.shift", name => values[name], new()).GetValue<double>());
        Assert.Equal(20L, ManagedExpression.Evaluate("x", name => values[name], new()).GetValue<long>());
        Assert.True(ManagedExpression.Evaluate("ok", name => values[name], new()).GetValue<bool>());
        Assert.Same(values, values["x"]!.Parent);
    }

    [Fact]
    public void PureFunctionCompositionIsValidatedWithoutEvaluatingDummyArguments()
    {
        var functions = Functions();
        ManagedExpression.ValidateFunctions(functions);
        Assert.Equal(30, Evaluate("adjust(10)", functions).GetValue<double>());
        Assert.False(Evaluate("positive(-2)", functions).GetValue<bool>());
        Assert.Equal(0, Evaluate("guarded(0)", functions).GetValue<double>());
    }

    private static JsonObject Functions() => JsonNode.Parse("""
        {"offset":{"parameters":["x","dx"],"expression":"x + dx"},
         "adjust":{"parameters":["x"],"expression":"offset(x, 5) * 2"},
         "positive":{"parameters":["x"],"expression":"x > 0"},
         "guarded":{"parameters":["x"],"expression":"x == 0 ? 0 : 1 / x"}}
        """)!.AsObject();

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("1 +")]
    [InlineData("(1 + 2")]
    [InlineData("1; 2")]
    [InlineData("1 = 2")]
    [InlineData("1 & 2")]
    [InlineData("1 | 2")]
    [InlineData("\"text\"")]
    [InlineData("x.y.z")]
    [InlineData("global.")]
    [InlineData("a[0]")]
    [InlineData("1e+")]
    [InlineData("1e999")]
    [InlineData("1e-999")]
    [InlineData("foo(1)")]
    [InlineData("sqrt(1, 2)")]
    [InlineData("min(1)")]
    [InlineData("global.method(1)")]
    [InlineData("sin(1,)")]
    public void InvalidGrammarAndFunctionsUseRecipeError(string expression) =>
        Assert.Equal(2, Assert.Throws<VisionException>(() => Evaluate(expression)).Code);

    [Theory]
    [InlineData("1 / 0")]
    [InlineData("1 % 0")]
    [InlineData("sqrt(-1)")]
    [InlineData("pow(-1, .5)")]
    [InlineData("pow(1e200, 2)")]
    [InlineData("1e308 * 1e308")]
    [InlineData("true + 1")]
    [InlineData("-false")]
    [InlineData("!1")]
    [InlineData("1 && true")]
    [InlineData("false || 1")]
    [InlineData("1 ? 2 : 3")]
    [InlineData("true == 1")]
    [InlineData("clamp(1, 5, 2)")]
    [InlineData("missing")]
    public void ArithmeticAndLookupErrorsUseArgumentError(string expression) =>
        Assert.Throws<ArgumentException>(() => Evaluate(expression));

    [Fact]
    public void StringsAndNonfiniteLookupsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => ManagedExpression.Evaluate("value", _ => JsonValue.Create("1"), new()));
        Assert.Throws<ArgumentException>(() => ManagedExpression.Evaluate("value", _ => JsonValue.Create(double.PositiveInfinity), new()));
    }

    [Theory]
    [InlineData("{\"bad.name\":{\"parameters\":[],\"expression\":\"1\"}}")]
    [InlineData("{\"sqrt\":{\"parameters\":[\"x\"],\"expression\":\"x\"}}")]
    [InlineData("{\"f\":{\"parameters\":[\"x\",\"x\"],\"expression\":\"x\"}}")]
    [InlineData("{\"f\":{\"parameters\":[\"pi\"],\"expression\":\"pi\"}}")]
    [InlineData("{\"f\":{\"parameters\":[\"x\"],\"expression\":\"global.X + x\"}}")]
    [InlineData("{\"f\":{\"parameters\":[\"x\"],\"expression\":\"local.x + x\"}}")]
    [InlineData("{\"f\":{\"parameters\":[\"x\"],\"expression\":\"other + x\"}}")]
    [InlineData("{\"f\":{\"parameters\":[\"x\"],\"expression\":\"missing(x)\"}}")]
    [InlineData("{\"f\":{\"parameters\":[\"x\"],\"expression\":\"f(x)\"}}")]
    [InlineData("{\"f\":{\"parameters\":[\"x\"],\"expression\":\"g(x)\"},\"g\":{\"parameters\":[\"x\"],\"expression\":\"f(x)\"}}")]
    [InlineData("{\"f\":{\"parameters\":[\"x\"],\"expression\":\"true ? x : f(x)\"}}")]
    [InlineData("{\"f\":{\"parameters\":[\"x\"],\"expression\":\"x\",\"unknown\":0}}")]
    public void FunctionsCannotReadOutsideArgumentsOrRecurse(string json) =>
        Assert.Equal(2, Assert.Throws<VisionException>(() => ManagedExpression.ValidateFunctions(JsonNode.Parse(json)!.AsObject())).Code);

    [Fact]
    public void ParserAndSymbolLimitsUseResourceError()
    {
        Resource(() => Evaluate(new string(' ', 2049)));
        Resource(() => Evaluate(new string('-', 33) + "1"));
        Resource(() => Evaluate(new string('(', 33) + "1" + new string(')', 33)));
        Resource(() => Evaluate("1" + string.Concat(Enumerable.Repeat(" + 1", 33))));
        Resource(() => ManagedExpression.Symbols(new string('a', 65), new()));
        string balanced = "1";
        for (int i = 0; i < 8; ++i) balanced = "(" + balanced + ")+ (" + balanced + ")";
        Resource(() => Evaluate(balanced[..Math.Min(balanced.Length, 2048)]));
    }

    [Fact]
    public void FunctionCountCallDepthAndTotalEvaluationWorkAreBounded()
    {
        var many = new JsonObject();
        for (int i = 0; i < 65; ++i) many["f" + i] = Function("1");
        Resource(() => ManagedExpression.ValidateFunctions(many));
        var chain = new JsonObject();
        for (int i = 0; i < 17; ++i) chain["f" + i] = Function(i == 16 ? "x" : $"f{i + 1}(x)", "x");
        Resource(() => ManagedExpression.ValidateFunctions(chain));
        var work = new JsonObject();
        for (int i = 0; i < 13; ++i) work["f" + i] = Function(i == 12 ? "x" : $"f{i + 1}(x) + f{i + 1}(x)", "x");
        Resource(() => Evaluate("f0(1)", work));
    }

    private static JsonObject Function(string body, params string[] parameters) => new()
    {
        ["parameters"] = new JsonArray(parameters.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()),
        ["expression"] = body
    };
    private static void Resource(Action action) => Assert.Equal(5, Assert.Throws<VisionException>(action).Code);
}
