using JPVision.Sdk;
using System.Text.Json;

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: PackageConsumer <recipe-directory> [license-store]");
    return 2;
}
try
{
    using var engine = new VisionEngine(args.Length > 1 ? args[1] : null);
    using var recipe = engine.LoadRecipe(args[0]);
    await using var session = recipe.CreateSession();
    string recipeId = recipe.Description.GetProperty("recipeId").GetString()!;
    bool processing = recipe.Description.GetProperty("kind").GetString() == "processing";
    bool geometric = recipeId != "jp-parts-count";
    bool lines = recipeId == "jp-lines";
    bool lineCaliper=recipeId=="jp-line-caliper",circleCaliper=recipeId=="jp-circle-caliper"||recipeId=="jp-circle-caliper-measurement",template=recipeId=="jp-template-match";
    int width = geometric ? 640 : 120, height = geometric ? 360 : 50;
    bool fast=recipeId is "jp-fast-match" or "jp-fast-locate";
    int stride = width * 3 + 8, expected = lines ? 6 : lineCaliper||circleCaliper||recipeId=="jp-fast-locate"?1:3;
    byte[] pixels = new byte[stride * height];
    for (int y = 0; y < height; ++y)
        for (int x = 0; x < width; ++x)
        {
            bool white = false;
            for (int n = 0; n < 3; ++n)
                white |= geometric ? (lines ? x >= 100 && x <= 540 && Math.Abs(y - (90 + n * 90)) <= 2 :
                    (x - (120 + n * 180)) * (x - (120 + n * 180)) + (y - 180) * (y - 180) <= 48 * 48) :
                    x >= 5 + n * 40 && x < 25 + n * 40 && y >= 10 && y < 30;
            if(lineCaliper)white=y>=180;
            for (int c = 0; c < 3; ++c)
                pixels[y * stride + x * 3 + c] = white ? (byte)235 : (byte)18;
        }
    using var frame = VisionFrame.CopyFrom(pixels, width, height, stride, VisionPixelFormat.Bgr24);
    using var result = await session.RunAsync(frame, new RunRequest
    {
        RunId = "nuget-consumer", FrameId = "known-fixture",
        Inputs = recipe.Description.GetProperty("inputs").TryGetProperty("ExpectedCount", out _)
            ? new Dictionary<string, object?> { ["ExpectedCount"] = expected } : new Dictionary<string, object?>()
    });
    if (processing)
    {
        using var gray = result.GetOutputImage("GrayImage");
        if (result.Status != ExecutionStatus.Succeeded || result.Decision != InspectionDecision.Unknown ||
            gray.Info.PixelFormat != VisionPixelFormat.Gray8 || gray.Info.Width != width || gray.Info.Height != height)
            throw new InvalidOperationException("NuGet processing output / decision differs.");
        Console.WriteLine(JsonSerializer.Serialize(new { packageConsumer = true, recipeId, decision = result.Decision.ToString(),
            pixelFormat = gray.Info.PixelFormat.ToString(), runtime = VisionEngine.Version, directory = AppContext.BaseDirectory }));
        return 0;
    }
    if (result.Status != ExecutionStatus.Succeeded || result.Decision != InspectionDecision.Pass || result.GetOutput<int>("ObjectCount") != expected)
        throw new InvalidOperationException("Known fixture did not produce the expected count / Pass.");
    if (recipeId == "jp-circles" && (result.GetCircles().Count != 3 || result.GetCircles().Any(c => Math.Abs(c.Radius - 48) > 3)))
        throw new InvalidOperationException("NuGet circle results differ from the fixture.");
    if(fast) {
        var measured=result.GetMeasurement<VisionMatch>("MatchMeasurement");
        if(measured.Count!=expected||measured.Selected is null||measured.Selected.Score<.8||result.GetMatches().Count!=expected)
            throw new InvalidOperationException("NuGet fast matching measurements differ.");
    }
    if (lines && (result.GetLines().Count != 6 || result.GetLines().Any(l => l.Length < 400 || Math.Abs(l.AngleDegrees) > 2)))
        throw new InvalidOperationException("NuGet line results differ from the fixture.");
    if (recipeId == "jp-regions" && (result.GetRegions().Count != 3 || result.GetRegions().Any(r => r.Area < 6000 || !r.CentroidValid)))
        throw new InvalidOperationException("NuGet region results differ from the fixture.");
    if(lineCaliper&&(result.GetLines().Count!=1||result.GetLines()[0].EdgePoints.Count<10||Math.Abs(result.GetLines()[0].Y1-179.5)>1))
        throw new InvalidOperationException("NuGet line caliper result differs.");
    if(circleCaliper&&(result.GetCircles().Count!=1||result.GetCircles()[0].EdgePoints.Count<20||Math.Abs(result.GetCircles()[0].Radius-48)>1))
        throw new InvalidOperationException("NuGet circle caliper result differs.");
    if(template&&(result.GetMatches().Count!=3||result.GetMatches().Any(m=>m.Score<.95||m.Corners.Count!=4)))
        throw new InvalidOperationException("NuGet template match result differs.");
    if (recipeId is "jp-circle-measurement" or "jp-circle-caliper-measurement")
    {
        var measurement = result.GetMeasurement<VisionCircle>("CircleMeasurement");
        var node = result.GetNodeMeasurement<VisionCircle>("detect");
        if (measurement.Count != expected || !measurement.SelectedValid || measurement.Selected is null ||
            Math.Abs(measurement.Selected.X - 120) > 2 || Math.Abs(measurement.Selected.Radius - 48) > 2 ||
            node.Count != expected || measurement.Selected.Diameter != 2 * measurement.Selected.Radius)
            throw new InvalidOperationException("NuGet direct circle measurement differs.");
        if (circleCaliper && (measurement.Caliper?.RequestedCount != 48 || measurement.Caliper.RejectedCount < 4))
            throw new InvalidOperationException("NuGet caliper rejection diagnostics differ.");
    }
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        packageConsumer = true, recipeId, count = result.GetOutput<int>("ObjectCount"),
        decision = result.Decision.ToString(), runtime = VisionEngine.Version, directory = AppContext.BaseDirectory
    }));
    return 0;
}
catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
