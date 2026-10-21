using JPVision.Sdk;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace JPVision.Tests;

public sealed class AdvancedTests : IDisposable
{
    private readonly string store;
    public AdvancedTests()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CMakeLists.txt"))) dir = dir.Parent;
        string root = dir?.FullName ?? throw new InvalidOperationException("Project root missing.");
        store = Path.Combine(root, "artifacts", "tests", Guid.NewGuid().ToString());
        VisionLicensing.Import(Path.Combine(root, "artifacts", "activation", "current.vlic"), store);
    }
    private JsonObject Definition(string type, JsonObject parameters, int expected)
    {
        JsonObject Node(string id, string name, JsonObject? p = null) => new() { ["id"] = id, ["type"] = name, ["version"] = 1, ["parameters"] = p ?? new() };
        JsonObject Edge(string a, string port, string b) => new() { ["fromNode"] = a, ["fromPort"] = port, ["toNode"] = b, ["toPort"] = "input" };
        JsonObject Output(string node, string port, string dataType) => new() { ["node"] = node, ["port"] = port, ["type"] = dataType };
        return new() {
            ["schemaVersion"] = 1, ["minimumRuntimeAbi"] = 1, ["kind"] = "inspection", ["recipeId"] = "advanced-test", ["recipeVersion"] = 1,
            ["nodes"] = new JsonArray(Node("source", "ExternalImage"), Node("detect", type, parameters), Node("count", "CountFeatures"), Node("judge", "CompareNumber", new() { ["expected"] = expected }), Node("overlay", "BuildFeatureOverlay")),
            ["connections"] = new JsonArray(Edge("source", "image", "detect"), Edge("detect", "features", "count"), Edge("count", "value", "judge"), Edge("detect", "features", "overlay")),
            ["inputs"] = new JsonObject { ["InputImage"] = new JsonObject { ["type"] = "Image", ["required"] = true } },
            ["outputs"] = new JsonObject { ["Features"] = Output("detect", "features", "Features"), ["ObjectCount"] = Output("count", "value", "Int"), ["InspectionPassed"] = Output("judge", "value", "Bool"), ["ResultOverlay"] = Output("overlay", "overlay", "Overlay") },
            ["decisionOutput"] = "InspectionPassed", ["resources"] = new JsonArray()
        };
    }
    private async Task<InspectionResult> Run(string type, JsonObject parameters, Func<int,int,byte> pixels, int expected = 1)
    {
        using var engine = new VisionEngine(store);
        string path = Path.Combine(store, Guid.NewGuid() + ".vrecipe"); Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "recipe.json"), VisionEngine.SealRecipe(Definition(type, parameters, expected).ToJsonString()));
        using var recipe = engine.LoadRecipe(path); await using var session = recipe.CreateSession();
        byte[] bytes = new byte[360 * 240];
        for (int y = 0; y < 240; ++y) for (int x = 0; x < 360; ++x) bytes[y * 360 + x] = pixels(x,y);
        using var frame = VisionFrame.CopyFrom(bytes, 360, 240, 360, VisionPixelFormat.Gray8);
        return await session.RunAsync(frame, new() { Debug = true });
    }
    private static JsonObject Rect(int x, int y, int w, int h) => new() { ["shape"] = "rectangle", ["x"] = x, ["y"] = y, ["width"] = w, ["height"] = h };
    private static JsonObject Circle(double x, double y, double radius) => new() { ["shape"] = "circle", ["cx"] = x, ["cy"] = y, ["radius"] = radius };
    private static JsonObject CircleParameters(JsonObject roi) => new() { ["roi"] = roi, ["minimumRadius"] = 15, ["maximumRadius"] = 25, ["minimumDistance"] = 30, ["voteThreshold"] = 15 };
    [Theory]
    [InlineData("rectangle")]
    [InlineData("circle")]
    [InlineData("polygon")]
    public async Task ModuleRoiUsesActualShapeAndOriginalCoordinates(string shape)
    {
        JsonObject roi = shape switch {
            "circle" => Circle(180,120,65),
            "polygon" => new() { ["shape"] = "polygon", ["points"] = new JsonArray(new JsonArray(115,55), new JsonArray(245,55), new JsonArray(245,110), new JsonArray(205,110), new JsonArray(205,185), new JsonArray(115,185)) },
            _ => Rect(130,70,100,100)
        };
        using var result = await Run("FindCircles", CircleParameters(roi), (x,y) => ((x-180)*(x-180)+(y-120)*(y-120) <= 400 || (x-230)*(x-230)+(y-170)*(y-170) <= 400) ? (byte)240 : (byte)15);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status); Assert.Equal(InspectionDecision.Pass, result.Decision);
        var circle = Assert.Single(result.GetCircles()); Assert.InRange(circle.X,177,183); Assert.InRange(circle.Y,117,123);
    }
    [Fact]
    public async Task LineCaliperMeasuresKnownEdgeWithPolarityAndPoints()
    {
        using var result = await Run("LineCaliper", new() { ["roi"] = Rect(40,80,260,80), ["searchLength"] = 60, ["polarity"] = "darkToLight" }, (x,y) => y >= 120 ? (byte)220 : (byte)20);
        Assert.Equal(InspectionDecision.Pass, result.Decision);
        var line = Assert.Single(result.GetLines()); Assert.InRange(line.Y1,118.8,120.2); Assert.InRange(line.Y2,118.8,120.2);
        Assert.True(line.EdgePoints.Count>=10);Assert.InRange(line.RmsError,0,.5);Assert.All(line.EdgePoints,p=>{Assert.True(p.X>40);Assert.InRange(p.Y,118.8,120.2);});
        var feature = Assert.Single(result.GetOutput<JsonElement[]>("Features")); Assert.True(feature.GetProperty("edgePoints").GetArrayLength() >= 10);
        Assert.InRange(feature.GetProperty("rmsError").GetDouble(),0,0.5);
    }
    [Fact]
    public async Task WrongCaliperPolarityDoesNotReturnAnEdge()
    {
        using var result = await Run("LineCaliper", new() { ["roi"] = Rect(40,80,260,80), ["searchLength"] = 60, ["polarity"] = "lightToDark" }, (x,y) => y >= 120 ? (byte)220 : (byte)20, 0);
        Assert.Equal(InspectionDecision.Pass,result.Decision); Assert.Empty(result.GetLines());
    }
    [Fact]
    public async Task CircleCaliperFitsKnownBoundary()
    {
        using var result = await Run("CircleCaliper", new() { ["roi"] = Circle(180,120,70), ["radius"] = 45, ["searchLength"] = 30, ["polarity"] = "lightToDark" }, (x,y) => (x-180)*(x-180)+(y-120)*(y-120) <= 2500 ? (byte)230 : (byte)15);
        Assert.Equal(InspectionDecision.Pass,result.Decision); var circle = Assert.Single(result.GetCircles());
        Assert.InRange(circle.X,179,181); Assert.InRange(circle.Y,119,121); Assert.InRange(circle.Radius,49,51);
        Assert.True(circle.EdgePoints.Count>=20);
        var feature = Assert.Single(result.GetOutput<JsonElement[]>("Features")); Assert.True(feature.GetProperty("edgePoints").GetArrayLength() >= 20);
    }
    private static byte Pattern(int x,int y) => (byte)(20 + (x*37+y*19+x*y*7)%210);
    private static JsonObject Model()
    {
        byte[] pixels = new byte[20*16]; for (int y=0;y<16;++y) for(int x=0;x<20;++x) pixels[y*20+x]=Pattern(x,y);
        return new() { ["width"]=20,["height"]=16,["grayBase64"]=Convert.ToBase64String(pixels) };
    }
    [Fact]
    public async Task TemplateMatchingReturnsMultipleLocationsAndFiniteScores()
    {
        using var result = await Run("TemplateMatch", new() { ["model"]=Model(),["scoreThreshold"]=0.95,["maximumMatches"]=5 }, (x,y) => x>=60&&x<80&&y>=70&&y<86 ? Pattern(x-60,y-70) : x>=220&&x<240&&y>=130&&y<146 ? Pattern(x-220,y-130) : (byte)10, 2);
        Assert.Equal(InspectionDecision.Pass,result.Decision); var matches=result.GetOutput<JsonElement[]>("Features"); Assert.Equal(2,matches.Length);
        Assert.Contains(matches,m=>m.GetProperty("x").GetDouble()==60&&m.GetProperty("y").GetDouble()==70);
        Assert.All(matches,m=> { Assert.Equal("match",m.GetProperty("kind").GetString()); Assert.InRange(m.GetProperty("score").GetDouble(),0.95,1.000001); Assert.Equal(4,m.GetProperty("corners").GetArrayLength()); });
        Assert.All(result.GetMatches(),m=> {Assert.Equal(4,m.Corners.Count);Assert.InRange(m.Score,.95,1);Assert.True(m.Corners[0].X>=60);});
        Assert.All(result.GetOutput<VisionMatch[]>("Features"),m=>Assert.True(m.Corners[0].X>=60));
        var measured = result.GetNodeMeasurement<VisionMatch>("detect");
        Assert.Equal(2, measured.Count); Assert.InRange(measured.Selected!.Score, .95, 1); Assert.Equal(4, measured.Selected.Corners.Count);
    }
    [Fact]
    public async Task TemplateRotationAndSearchRoiExcludeOtherTargets()
    {
        using var result = await Run("TemplateMatch",new() { ["model"]=Model(),["roi"]=Rect(100,50,80,90),["angleMinimum"]=0,["angleMaximum"]=90,["angleStep"]=90,["scoreThreshold"]=0.95 }, (x,y) => x>=130&&x<146&&y>=80&&y<100 ? Pattern(y-80,15-(x-130)) : x>=250&&x<270&&y>=100&&y<116 ? Pattern(x-250,y-100) : (byte)10);
        Assert.Equal(InspectionDecision.Pass,result.Decision); var match=Assert.Single(result.GetOutput<JsonElement[]>("Features")); Assert.InRange(match.GetProperty("angleDegrees").GetDouble(),89.9,90.1);
        Assert.InRange(match.GetProperty("centerX").GetDouble(),137,139); Assert.InRange(match.GetProperty("centerY").GetDouble(),89,91);
    }
    [Fact]
    public async Task FlatImageDoesNotCreateTemplateFalseMatches()
    {
        using var result=await Run("TemplateMatch",new() { ["model"]=Model(),["scoreThreshold"]=0.8 }, (x,y)=>(byte)100,0);
        Assert.Equal(InspectionDecision.Pass,result.Decision); Assert.Empty(result.GetOutput<JsonElement[]>("Features"));
    }
    [Fact]
    public void InvalidPolygonAndUntrainedTemplateAreRejected()
    {
        using var engine=new VisionEngine(store);
        var polygon = new JsonObject { ["shape"]="polygon",["points"]=new JsonArray(new JsonArray(20,20),new JsonArray(100,100),new JsonArray(100,20),new JsonArray(20,100)) };
        Assert.Throws<VisionException>(()=>engine.ValidateRecipe(VisionEngine.SealRecipe(Definition("FindCircles",CircleParameters(polygon),1).ToJsonString())));
        Assert.Throws<VisionException>(()=>engine.ValidateRecipe(VisionEngine.SealRecipe(Definition("TemplateMatch",new(),1).ToJsonString())));
    }
    [Fact]
    public void ConstantTemplateAndExcessivePoseSearchAreRejected()
    {
        using var engine=new VisionEngine(store);
        var constant=new JsonObject{["width"]=20,["height"]=16,["grayBase64"]=Convert.ToBase64String(new byte[320])};
        Assert.Throws<VisionException>(()=>engine.ValidateRecipe(VisionEngine.SealRecipe(Definition("TemplateMatch",new(){["model"]=constant},1).ToJsonString())));
        Assert.Throws<VisionException>(()=>engine.ValidateRecipe(VisionEngine.SealRecipe(Definition("TemplateMatch",new(){["model"]=Model(),["angleMinimum"]=-180,["angleMaximum"]=180,["angleStep"]=.25},1).ToJsonString())));
    }
    [Fact]
    public async Task NonRectangularTemplateMaskAndBrightnessOffsetRemainMatched()
    {
        var model=Model();byte[] mask=new byte[320];for(int y=0;y<16;++y)for(int x=0;x<20;++x)if((x-10)*(x-10)+(y-8)*(y-8)<=49)mask[y*20+x]=255;model["maskBase64"]=Convert.ToBase64String(mask);
        using var result=await Run("TemplateMatch",new(){["model"]=model,["scoreThreshold"]=.99},(x,y)=> x>=60&&x<80&&y>=70&&y<86?(byte)(Pattern(x-60,y-70)+20):(byte)10);
        Assert.Equal(InspectionDecision.Pass,result.Decision);var found=Assert.Single(result.GetMatches());Assert.InRange(found.Score,.99,1);Assert.Equal(60,found.X);
    }
    [Fact]
    public async Task PartiallyOutsideModuleRoiRunsOnItsVisibleIntersection()
    {
        using var result=await Run("FindCircles",CircleParameters(Circle(350,120,60)),(x,y)=>(byte)30);
        Assert.Equal(ExecutionStatus.Succeeded,result.Status);Assert.Equal(InspectionDecision.Fail,result.Decision);Assert.Empty(result.GetCircles());
    }
    [Fact]
    public async Task ModuleRoiDoesNotAffectParallelBranch()
    {
        using var engine=new VisionEngine(store);
        var definition=Definition("FindCircles",CircleParameters(Circle(180,120,65)),1);
        definition["nodes"]!.AsArray().Add(new JsonObject{["id"]="full",["type"]="FindCircles",["version"]=1,["parameters"]=new JsonObject{["minimumRadius"]=15,["maximumRadius"]=25,["minimumDistance"]=30,["voteThreshold"]=15}});
        definition["connections"]!.AsArray().Add(new JsonObject{["fromNode"]="source",["fromPort"]="image",["toNode"]="full",["toPort"]="input"});
        definition["outputs"]!.AsObject()["AllCircles"]=new JsonObject{["node"]="full",["port"]="features",["type"]="Features"};
        string path=Path.Combine(store,"parallel.vrecipe");Directory.CreateDirectory(path);File.WriteAllText(Path.Combine(path,"recipe.json"),VisionEngine.SealRecipe(definition.ToJsonString()));
        byte[] pixels=new byte[360*240];for(int y=0;y<240;++y)for(int x=0;x<360;++x)pixels[y*360+x]=(x-180)*(x-180)+(y-120)*(y-120)<=400||(x-230)*(x-230)+(y-170)*(y-170)<=400?(byte)240:(byte)15;
        using var frame=VisionFrame.CopyFrom(pixels,360,240,360,VisionPixelFormat.Gray8);using var recipe=engine.LoadRecipe(path);await using var session=recipe.CreateSession();using var result=await session.RunAsync(frame);
        Assert.Single(result.GetCircles());Assert.Equal(2,result.GetCircles("AllCircles").Count);
    }
    public void Dispose() { if(Directory.Exists(store)) Directory.Delete(store,true); }
}
