using JPVision.Editor.Core;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace JPVision.Tests;

public sealed class CaliperLayoutTests
{
    private static JsonObject Boundary(int count = 4) => new() { ["caliperPlacement"] = "roiBoundary", ["caliperCount"] = count };
    private static void Close(double expected, double actual) => Assert.InRange(actual, expected - 1e-8, expected + 1e-8);

    [Fact]
    public void CircleBoundaryUsesFractionalCenterAndTheDrawnRadius()
    {
        var windows = CaliperLayout.Create(RoiRegion.Circle(100.25, 120.75, 30.5), Boundary(), true, 300, 300);
        Assert.Equal(4, windows.Count);
        Close(130.75, windows[0].X);
        Close(120.75, windows[0].Y);
        Close(100.25, windows[1].X);
        Close(151.25, windows[1].Y);
        Close(69.75, windows[2].X);
        Close(90.25, windows[3].Y);
        foreach (var window in windows)
        {
            Close(30.5, Math.Sqrt(Math.Pow(window.X - 100.25, 2) + Math.Pow(window.Y - 120.75, 2)));
            Close(1, Math.Sqrt(window.NormalX * window.NormalX + window.NormalY * window.NormalY));
            Assert.Equal(5, window.Width);
            Assert.Equal(60, window.Height);
        }
    }

    [Fact]
    public void MovingAndResizingACircleMovesTheCalipers()
    {
        var roi = RoiRegion.Circle(100.25, 120.75, 30.5);
        var original = CaliperLayout.Create(roi, Boundary(), true, 300, 300);
        var moved = CaliperLayout.Create(roi.Translate(17.5, -9.25), Boundary(), true, 300, 300);
        var resized = CaliperLayout.Create(RoiRegion.Circle(roi.X, roi.Y, 44.25), Boundary(), true, 300, 300);
        for (int i = 0; i < original.Count; i++)
        {
            Close(original[i].X + 17.5, moved[i].X);
            Close(original[i].Y - 9.25, moved[i].Y);
            Close(roi.X + 44.25 * original[i].NormalX, resized[i].X);
            Close(roi.Y + 44.25 * original[i].NormalY, resized[i].Y);
        }
    }

    [Fact]
    public void AnArcIncludesBothEndpointsWithoutDuplicatingAFullCircleEndpoint()
    {
        var p = Boundary();
        p["startAngle"] = 30;
        p["endAngle"] = 150;
        var arc = CaliperLayout.Create(RoiRegion.Circle(100, 100, 30), p, true, 300, 300);
        Assert.Equal(4, arc.Count);
        for (int i = 0; i < 4; i++)
        {
            double angle = (30 + 40 * i) * Math.PI / 180;
            Close(100 + 30 * Math.Cos(angle), arc[i].X);
            Close(100 + 30 * Math.Sin(angle), arc[i].Y);
        }
        var full = CaliperLayout.Create(RoiRegion.Circle(100, 100, 30), Boundary(), true, 300, 300);
        Assert.NotEqual(full[0], full[^1]);
    }

    [Theory]
    [InlineData("top", 35, 20, 0, -1)]
    [InlineData("right", 210, 32.5, 1, 0)]
    [InlineData("bottom", 185, 120, 0, 1)]
    [InlineData("left", 10, 107.5, -1, 0)]
    public void RectangleEdgesUseTheirExactDrawnBoundaryAndOutwardNormal(string edge, double x, double y, double nx, double ny)
    {
        var p = Boundary();
        p["roiEdge"] = edge;
        var windows = CaliperLayout.Create(RoiRegion.Rectangle(10, 20, 200, 100), p, false, 300, 300);
        Assert.Equal(4, windows.Count);
        Close(x, windows[0].X);
        Close(y, windows[0].Y);
        foreach (var window in windows)
        {
            Close(nx, window.NormalX);
            Close(ny, window.NormalY);
            if (edge is "top" or "bottom")
                Assert.InRange(window.X, 10.001, 209.999);
            else
                Assert.InRange(window.Y, 20.001, 119.999);
        }
    }

    [Fact]
    public void MovingAndResizingARectangleUpdatesItsSelectedEdge()
    {
        var roi = RoiRegion.Rectangle(10, 20, 100, 80);
        var original = CaliperLayout.Create(roi, Boundary(), false, 300, 300);
        var moved = CaliperLayout.Create(roi.Translate(7, 11), Boundary(), false, 300, 300);
        var resized = CaliperLayout.Create(RoiRegion.Rectangle(10, 20, 200, 80), Boundary(), false, 300, 300);
        Close(22.5, original[0].X);
        Close(35, resized[0].X);
        for (int i = 0; i < original.Count; i++)
        {
            Close(original[i].X + 7, moved[i].X);
            Close(original[i].Y + 11, moved[i].Y);
            Close(20, resized[i].Y);
        }
    }

    [Fact]
    public void ReversingPolygonVerticesKeepsTheSameGeometricEdgeAndOutwardNormal()
    {
        var points = new[] { new EditorPoint(20, 20), new EditorPoint(120, 20), new EditorPoint(100, 100), new EditorPoint(20, 80) };
        var p = Boundary();
        p["roiEdgeIndex"] = 1;
        var clockwise = CaliperLayout.Create(RoiRegion.Polygon(points), p, false, 300, 300);
        var reversed = CaliperLayout.Create(RoiRegion.Polygon(points.Reverse()), p, false, 300, 300);
        double length = Math.Sqrt(20 * 20 + 80 * 80);
        Assert.Equal(4, clockwise.Count);
        Assert.Equal(4, reversed.Count);
        for (int i = 0; i < clockwise.Count; i++)
        {
            Close(clockwise[i].X, reversed[^(i + 1)].X);
            Close(clockwise[i].Y, reversed[^(i + 1)].Y);
            Close(80 / length, clockwise[i].NormalX);
            Close(20 / length, clockwise[i].NormalY);
            Close(clockwise[i].NormalX, reversed[i].NormalX);
            Close(clockwise[i].NormalY, reversed[i].NormalY);
        }
    }

    [Fact]
    public void PolygonEdgeIndexIncludesTheClosingEdgeAndAvoidsBoundingBoxSubstitution()
    {
        var roi = RoiRegion.Polygon(new[] { new EditorPoint(20, 20), new EditorPoint(120, 20), new EditorPoint(100, 100), new EditorPoint(30, 80) });
        var p = Boundary();
        p["roiEdgeIndex"] = 3;
        var windows = CaliperLayout.Create(roi, p, false, 300, 300);
        Assert.Equal(4, windows.Count);
        Close(28.75, windows[0].X);
        Close(72.5, windows[0].Y);
        Assert.True(windows[0].NormalX < 0);
        Assert.True(windows[0].NormalY > 0);
        p["roiEdgeIndex"] = 4;
        Assert.Empty(CaliperLayout.Create(roi, p, false, 300, 300));
    }

    [Fact]
    public void MovingAPolygonMovesItsActualSelectedEdge()
    {
        var roi = RoiRegion.Polygon(new[] { new EditorPoint(20, 20), new EditorPoint(120, 20), new EditorPoint(100, 100) });
        var p = Boundary();
        p["roiEdgeIndex"] = 1;
        var original = CaliperLayout.Create(roi, p, false, 300, 300);
        var moved = CaliperLayout.Create(roi.Translate(8.25, 17.5), p, false, 300, 300);
        for (int i = 0; i < original.Count; i++)
        {
            Close(original[i].X + 8.25, moved[i].X);
            Close(original[i].Y + 17.5, moved[i].Y);
            Close(original[i].NormalX, moved[i].NormalX);
            Close(original[i].NormalY, moved[i].NormalY);
        }
    }

    [Fact]
    public void MissingPlacementPreservesLegacyManualCircleAndLineGuides()
    {
        var p = new JsonObject { ["caliperCount"] = 4, ["radius"] = 30, ["angleDegrees"] = 90, ["guideLength"] = 40 };
        var circle = CaliperLayout.Create(RoiRegion.None, p, true, 200, 100);
        Close(129.5, circle[0].X);
        Close(49.5, circle[0].Y);
        var line = CaliperLayout.Create(RoiRegion.None, p, false, 200, 100);
        Close(99.5, line[0].X);
        Close(29.5, line[0].Y);
        Close(69.5, line[^1].Y);
        Close(-1, line[0].NormalX);
        Close(0, line[0].NormalY);
    }

    [Fact]
    public void LegacyGuideUsesNativeInclusiveBoundingBoxesForFractionalCirclesAndPolygons()
    {
        var p = new JsonObject { ["caliperCount"] = 4 };
        var circle = CaliperLayout.Create(RoiRegion.Circle(100.25, 90.75, 20.5), p, true, 300, 300);
        // Native circle bounds: left 79, right-exclusive 122, top 70, bottom-exclusive 113.
        Close(115.75, circle[0].X);
        Close(91, circle[0].Y);
        var polygon = RoiRegion.Polygon(new[] { new EditorPoint(20.2, 30.4), new EditorPoint(60.8, 30.4), new EditorPoint(20.2, 70.9) });
        var guide = CaliperLayout.Create(polygon, p, true, 300, 300);
        Close(55.875, guide[0].X);
        Close(50.5, guide[0].Y);
        var rectangleLine = CaliperLayout.Create(RoiRegion.Rectangle(10, 20, 100, 80), p, false, 300, 300);
        Close(14.5, rectangleLine[0].X);
        Close(104.5, rectangleLine[^1].X);
        Close(59.5, rectangleLine[0].Y);
    }

    [Fact]
    public void BoundaryModeRejectsMissingRoiWrongShapesAndInvalidIndices()
    {
        var p = Boundary();
        Assert.Empty(CaliperLayout.Create(RoiRegion.None, p, true, 300, 300));
        Assert.Empty(CaliperLayout.Create(RoiRegion.Rectangle(10, 20, 50, 60), p, true, 300, 300));
        Assert.Empty(CaliperLayout.Create(RoiRegion.Circle(100, 100, 30), p, false, 300, 300));
        p["roiEdge"] = "diagonal";
        Assert.Empty(CaliperLayout.Create(RoiRegion.Rectangle(10, 20, 50, 60), p, false, 300, 300));
        var roi = RoiRegion.Polygon(new[] { new EditorPoint(20, 20), new EditorPoint(120, 20), new EditorPoint(100, 100) });
        p["roiEdgeIndex"] = -1;
        Assert.Empty(CaliperLayout.Create(roi, p, false, 300, 300));
    }

    [Fact]
    public void InvalidEditingValuesNeverProduceNonFiniteWindowsOrUnboundedCounts()
    {
        var roi = RoiRegion.Circle(100, 100, 30);
        foreach (double count in new[] { double.NaN, double.PositiveInfinity, 3, 4.5, 257 })
        {
            var p = Boundary();
            p["caliperCount"] = JsonValue.Create(count);
            Assert.Empty(CaliperLayout.Create(roi, p, true, 300, 300));
        }
        var malformed = Boundary();
        malformed["caliperCount"] = "many";
        Assert.Empty(CaliperLayout.Create(roi, malformed, true, 300, 300));
        var dimensions = Boundary();
        dimensions["searchLength"] = JsonValue.Create(double.NaN);
        Assert.Empty(CaliperLayout.Create(roi, dimensions, true, 300, 300));
        var shortHeight = Boundary();
        shortHeight["searchLength"] = 8;
        Assert.Empty(CaliperLayout.Create(roi, shortHeight, true, 300, 300));
        shortHeight["searchLength"] = 24;
        shortHeight["sigma"] = 5;
        Assert.Empty(CaliperLayout.Create(roi, shortHeight, true, 300, 300));
        shortHeight["searchLength"] = 12;
        shortHeight["sigma"] = 1;
        Assert.Equal(4, CaliperLayout.Create(roi, shortHeight, true, 300, 300).Count);
        foreach (double sigma in new[] { -1, 5.1, double.NaN, double.PositiveInfinity })
        {
            var p = Boundary();
            p["sigma"] = JsonValue.Create(sigma);
            Assert.Empty(CaliperLayout.Create(roi, p, true, 300, 300));
        }
        foreach (double width in new[] { 0, 4, 5.5, 65, double.PositiveInfinity })
        {
            var p = Boundary();
            p["projectionWidth"] = JsonValue.Create(width);
            Assert.Empty(CaliperLayout.Create(roi, p, true, 300, 300));
        }
        Assert.Empty(CaliperLayout.Create(RoiRegion.Circle(300, 300, 30), Boundary(), true, 300, 300));
        var badRegion = new RoiRegion("polygon", 0, 0, 0, 0, 0, Array.Empty<EditorPoint>());
        Assert.Empty(CaliperLayout.Create(badRegion, Boundary(), false, 300, 300));
        Assert.Empty(CaliperLayout.Create(roi, Boundary(), true, 0, 300));
    }

    [Fact]
    public void FractionalSearchLengthShowsTheActualSampledExtent()
    {
        var p = Boundary();
        p["searchLength"] = 25.2;
        p["projectionWidth"] = 7;
        var windows = CaliperLayout.Create(RoiRegion.Circle(100, 100, 30), p, true, 300, 300);
        Assert.Equal(4, windows.Count);
        Assert.All(windows, window => { Assert.Equal(26, window.Height); Assert.Equal(7, window.Width); });
    }

    [Fact]
    public void NewEditorNodesUseEditorDefaultsWithoutChangingLoadedLegacyRecipes()
    {
        using var json = JsonDocument.Parse("""
        [{"type":"Caliper","label":"卡尺","category":"测量","version":1,"inputs":[],"outputs":[],"parameters":[
          {"name":"caliperPlacement","default":"guide","editorDefault":"roiBoundary"},
          {"name":"projectionWidth","default":5}]}]
        """);
        var catalog = new NodeCatalog(json.RootElement);
        var flow = new FlowDocument(catalog);
        flow.Add("Caliper", 10, 20, "measure");
        Assert.Equal("roiBoundary", flow.Node("measure")["parameters"]!["caliperPlacement"]!.GetValue<string>());
        Assert.Equal(5, flow.Node("measure")["parameters"]!["projectionWidth"]!.GetValue<int>());
        flow.SetParameter("measure", "caliperPlacement", JsonValue.Create("guide"));
        var legacy = new FlowDocument(catalog);
        legacy.Load(flow.Definition.ToJsonString());
        Assert.Equal("guide", legacy.Node("measure")["parameters"]!["caliperPlacement"]!.GetValue<string>());
        // Cloning defaults must not mutate the catalog's metadata.
        var defaults = catalog["Caliper"].Defaults();
        defaults["caliperPlacement"] = "guide";
        Assert.Equal("roiBoundary", catalog["Caliper"].Defaults()["caliperPlacement"]!.GetValue<string>());
    }
}
