using System.Text.Json.Nodes;
using JPVision.Sdk;
using JPVision.Sdk.Execution;
using Xunit;

namespace JPVision.Tests;

/// <summary>Business execution tests do not need native images, licensing or camera hardware.</summary>
public sealed class ManagedBindingsTests
{
    private static JsonObject Recipe() => JsonNode.Parse("""
        {"schemaVersion":1,"kind":"processing","recipeId":"managed","recipeVersion":1,
         "inputs":{"InputImage":{"type":"Image"}},"outputs":{"Output":{"node":"target","port":"image","type":"Image"}},
         "decisionOutput":"","nodes":[{"id":"source","type":"Source","version":1,"parameters":{}},
             {"id":"target","type":"Target","version":1,"parameters":{}}],"connections":[]}
        """)!.AsObject();

    private static JsonArray Catalog() => JsonNode.Parse("""
        [{"type":"Source","version":1,"inputs":[],
          "outputs":[{"id":"result","type":"Result"},{"id":"count","type":"Int"}],"parameters":[],
          "fields":{"result":{"measurement.centerX":{"type":"Double","nullable":true,"validWhen":"measurement.valid"},
                              "radius":{"type":"Double","nullable":true}}}},
         {"type":"Target","version":1,"inputs":[],"outputs":[{"id":"image","type":"Image"}],
          "parameters":[{"name":"roi","type":"roi","default":{"shape":"none"}},
                        {"name":"value","type":"number","default":0.0,"min":-100000,"max":100000,"bindable":true},
                        {"name":"count","type":"integer","default":0,"min":0,"max":10000,"bindable":true},
                        {"name":"roiReference","type":"enum","default":"image","choices":["image","fixture"]},
                        {"name":"positionReference","type":"enum","default":"image","choices":["image","auto","matchCenter"]}]}]
        """)!.AsArray();

    private static JsonObject Target(JsonObject recipe) => (JsonObject)recipe["nodes"]![1]!;
    private static JsonObject Field(string field = "measurement.centerX") => new()
    { ["node"] = "source", ["port"] = "result", ["field"] = field };
    private static JsonObject Expression(string expression, JsonObject? arguments = null) => new()
    { ["expression"] = expression, ["arguments"] = arguments ?? new() };

    private static ManagedParameterResolution Resolve(ManagedExecutionPlan plan,
        Func<string, string, string, JsonNode?>? lookup = null, ManagedInputCoordinates? input = null,
        Func<string, bool>? skipped = null) => ManagedBindings.Resolve(plan.Nodes["target"], new(), plan,
        lookup, skipped, input, ManagedBindings.Globals(plan.Definition, new()));

    [Fact]
    public void HiddenLocalSubscriptionsParticipateInOrderAndReferenceCounts()
    {
        var recipe = Recipe();
        Target(recipe)["variables"] = new JsonObject { ["x"] = new JsonObject { ["type"] = "Double", ["value"] = Field() } };
        Target(recipe)["parameters"]!["value"] = new JsonObject { ["variable"] = "x", ["scope"] = "local" };
        var plan = ManagedExecutionPlan.Create(recipe, Catalog());
        Assert.Equal(new[] { "source", "target" }, plan.Order);
        Assert.Equal(1, plan.Uses["source"]);
        Assert.Equal(1, plan.Uses["target"]);
        Assert.Equal(("source", "result"), plan.ParameterIncoming["target"]["variables.x.value"]);
        recipe["nodes"]![0]!["id"] = "changed";
        Assert.True(plan.Nodes.ContainsKey("source"));
    }

    [Fact]
    public void EvenUnpublishedCyclesAreRejected()
    {
        var recipe = Recipe();
        recipe["nodes"]![0]!["parameters"] = new JsonObject { ["unused"] = new JsonObject { ["node"] = "source", ["port"] = "count", ["field"] = "" } };
        Assert.Contains("cycle", Assert.Throws<VisionException>(() => ManagedExecutionPlan.Create(recipe, Catalog())).Message);
    }

    [Fact]
    public void SupplementaryUnicodeNamesFollowNativeUtf8Order()
    {
        const string bmp = "\uE000", supplementary = "\U0001F600";
        Assert.True(StringComparer.Ordinal.Compare(bmp, supplementary) > 0);
        Assert.True(ManagedExecutionPlan.Comparer.Compare(bmp, supplementary) < 0);
        var recipe = Recipe();
        recipe["nodes"] = new JsonArray(new JsonObject { ["id"] = supplementary, ["type"] = "Source", ["version"] = 1, ["parameters"] = new JsonObject() },
            new JsonObject { ["id"] = bmp, ["type"] = "Source", ["version"] = 1, ["parameters"] = new JsonObject() });
        recipe["outputs"] = new JsonObject
        {
            [supplementary] = new JsonObject { ["node"] = supplementary, ["port"] = "result", ["type"] = "Result" },
            [bmp] = new JsonObject { ["node"] = bmp, ["port"] = "result", ["type"] = "Result" }
        };
        var plan = ManagedExecutionPlan.Create(recipe, Catalog());
        Assert.Equal(new[] { bmp, supplementary }, plan.Order);
        Assert.Equal(new[] { bmp, supplementary }, plan.Nodes.Keys);
    }

    [Fact]
    public void OnlyReachableNodesNeedCompleteInputGroups()
    {
        var recipe = Recipe();
        var catalog = Catalog();
        catalog[0]!["inputs"] = new JsonArray(new JsonObject { ["id"] = "input", ["type"] = "Image" });
        catalog[0]!["inputGroups"] = new JsonArray(new JsonArray("input"));
        Assert.Equal(new[] { "target" }, ManagedExecutionPlan.Create(recipe, catalog).Order);
        Target(recipe)["parameters"]!["count"] = new JsonObject { ["node"] = "source", ["port"] = "count", ["field"] = "" };
        Assert.Contains("complete legacy", Assert.Throws<VisionException>(() => ManagedExecutionPlan.Create(recipe, catalog)).Message);
    }

    [Fact]
    public void GlobalOverridesAreTypedAndNeverMutateTheRecipe()
    {
        var recipe = Recipe();
        recipe["variables"] = new JsonObject { ["offset"] = new JsonObject { ["type"] = "Double", ["value"] = 2.5 } };
        Assert.Equal(9d, ManagedBindings.Globals(recipe, new() { ["offset"] = 9 })["offset"]!.GetValue<int>());
        Assert.Equal(2.5, ManagedBindings.Globals(recipe, new())["offset"]!.GetValue<double>());
        Assert.Throws<ArgumentException>(() => ManagedBindings.Globals(recipe, new() { ["unknown"] = 4 }));
        Assert.Throws<ArgumentException>(() => ManagedBindings.Globals(recipe, new() { ["offset"] = "bad" }));
    }

    [Fact]
    public void BoundPositionsConvertToInputCoordinatesButRadiiDoNot()
    {
        var recipe = Recipe();
        Target(recipe)["parameters"]!["roi"] = new JsonObject { ["shape"] = "circle", ["cx"] = Field(), ["cy"] = 20,
            ["radius"] = Field("radius") };
        var plan = ManagedExecutionPlan.Create(recipe, Catalog());
        JsonNode? Lookup(string _, string __, string field) => field switch
        {
            "measurement.centerX" => JsonValue.Create(120.5), "radius" => JsonValue.Create(13.25),
            "measurement.valid" => JsonValue.Create(true), "coordinateSpace" => JsonValue.Create("original"), _ => null
        };
        var resolved = Resolve(plan, Lookup, new(100, 10));
        Assert.Equal(20.5, resolved.Parameters["roi"]!["cx"]!.GetValue<double>());
        Assert.Equal(13.25, resolved.Parameters["roi"]!["radius"]!.GetValue<double>());
        Assert.False(resolved.Deferred);
    }

    [Fact]
    public void MissingFieldsKeepFallbacksOnlyAsUnavailablePlaceholders()
    {
        var recipe = Recipe();
        var binding = Field(); binding["fallback"] = 77;
        Target(recipe)["parameters"]!["value"] = binding;
        Target(recipe)["variables"] = new JsonObject { ["missing"] = new JsonObject { ["type"] = "Double", ["value"] = Field() } };
        var resolved = Resolve(ManagedExecutionPlan.Create(recipe, Catalog()), (_, _, _) => null);
        Assert.Equal(77, resolved.Parameters["value"]!.GetValue<int>());
        Assert.Null(resolved.Locals["missing"]);
        Assert.Equal(new[] { "local.missing", "value" }, resolved.Parameters["_unavailable"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public void LazyBranchesDoNotReadMissingUpstreamResults()
    {
        var recipe = Recipe();
        Target(recipe)["parameters"]!["value"] = Expression("true ? 7 : missing", new() { ["missing"] = Field() });
        int reads = 0;
        var resolved = Resolve(ManagedExecutionPlan.Create(recipe, Catalog()), (_, _, _) => { ++reads; return null; });
        Assert.Equal(7d, resolved.Parameters["value"]!.GetValue<double>());
        Assert.Equal(0, reads);
        Assert.False(resolved.Parameters.ContainsKey("_unavailable"));
    }

    [Fact]
    public void ProvenanceCannotCrossImagePlanesOrDoubleApplyPositioning()
    {
        var recipe = Recipe();
        Target(recipe)["parameters"]!["roi"] = new JsonObject { ["shape"] = "circle", ["cx"] = Field(), ["cy"] = 20, ["radius"] = 10 };
        JsonNode? Lookup(string _, string __, string field) => field switch
        {
            "measurement.centerX" => JsonValue.Create(120d), "measurement.valid" => JsonValue.Create(true),
            "coordinateSpace" => JsonValue.Create("original"), _ => null
        };
        Assert.Contains("coordinate plane", Assert.Throws<ArgumentException>(() => Resolve(
            ManagedExecutionPlan.Create(recipe, Catalog()), Lookup, new(0, 0, "annular", "frame/unwrap"))).Message);
        var catalog = Catalog(); catalog[1]!["type"] = "FindCircles"; Target(recipe)["type"] = "FindCircles";
        Target(recipe)["parameters"]!["positionReference"] = "auto";
        Assert.Contains("automatic match", Assert.Throws<ArgumentException>(() => Resolve(
            ManagedExecutionPlan.Create(recipe, catalog), Lookup, new(0, 0, HasMatchPosition: true))).Message);
    }

    [Theory]
    [InlineData(2.5, 3)]
    [InlineData(-2.5, -3)]
    public void RectangleBoundsUseTheLegacyAwayFromZeroRounding(double value, long expected)
    {
        var recipe = Recipe();
        Target(recipe)["parameters"]!["roi"] = new JsonObject { ["shape"] = "rectangle", ["coordinateSpace"] = "reference",
            ["x"] = value, ["y"] = 1, ["width"] = 20, ["height"] = 10 };
        var resolved = Resolve(ManagedExecutionPlan.Create(recipe, Catalog()));
        Assert.Equal(expected, resolved.Parameters["roi"]!["x"]!.GetValue<long>());
    }

    [Fact]
    public void FractionalExpressionsCannotEnterIntegerParameters()
    {
        var recipe = Recipe();
        Target(recipe)["parameters"]!["count"] = Expression("5 / 2");
        var plan = ManagedExecutionPlan.Create(recipe, Catalog());
        var deferred = Resolve(plan);
        Assert.True(deferred.Deferred);
        Assert.Contains("exact integer", Assert.Throws<VisionException>(() => Resolve(plan, (_, _, _) => null)).Message);
        plan.Nodes["target"]["parameters"]!["count"] = Expression("6 / 2");
        Assert.Equal(3L, Resolve(plan, (_, _, _) => null).Parameters["count"]!.GetValue<long>());
    }

    [Fact]
    public void LocalCyclesAndOversizedUtf8VariablesAreRejected()
    {
        var recipe = Recipe();
        Target(recipe)["variables"] = new JsonObject
        {
            ["a"] = new JsonObject { ["type"] = "Double", ["value"] = Expression("local.b") },
            ["b"] = new JsonObject { ["type"] = "Double", ["value"] = Expression("local.a") }
        };
        Assert.Contains("cycle", Assert.Throws<VisionException>(() => Resolve(ManagedExecutionPlan.Create(recipe, Catalog()))).Message);
        recipe["variables"] = new JsonObject { ["text"] = new JsonObject { ["type"] = "String", ["value"] = new string('汉', 86) } };
        Assert.Throws<VisionException>(() => ManagedBindings.Globals(recipe, new()));
    }
}
