using JPVision.Editor.Core;
using JPVision.Sdk;
using System.Text.Json.Nodes;
using Xunit;

namespace JPVision.Tests;

public sealed class CirclePositioningTests
{
    private static FrameInfo Frame(int x = 900, int y = -80) => new(600, 400, 600, VisionPixelFormat.Gray8, x, y);
    private static JsonObject Parameters(string reference = "auto") => new()
    { ["positionReference"] = reference, ["matchOffsetX"] = -28.0, ["matchOffsetY"] = 3.0, ["matchSearchRadius"] = 20.0 };
    private static VisionMatchPosition Match => new(1027, 39, 0, 1);

    [Fact]
    public void ActualInputOriginAndMatchCenterDefineTheDrawnRoi()
    {
        var roi = CirclePositioning.Resolve(RoiRegion.Circle(1, 2, 18.5), Parameters(), Match, Frame());
        Assert.Equal(RoiRegion.Circle(99, 122, 18.5), roi);
        var translated = CirclePositioning.Resolve(roi, Parameters(), Match with { CenterX = 1127, CenterY = 69 }, Frame());
        Assert.Equal(RoiRegion.Circle(199, 152, 18.5), translated);
    }
    [Fact]
    public void TemplateCropAndFullSourceUseTheSameOriginalCenter()
    {
        var original = CirclePositioning.Resolve(RoiRegion.None, Parameters(), Match, Frame());
        var crop = CirclePositioning.Resolve(RoiRegion.None, Parameters(), Match, Frame(963, -9));
        Assert.Equal(original.X + 900, crop.X + 963);
        Assert.Equal(original.Y - 80, crop.Y - 9);
        Assert.Equal(20, crop.Radius);
    }
    [Fact]
    public void DrawingRecordsOffsetsInsteadOfFreezingTheCurrentFrame()
    {
        var offset = CirclePositioning.Offset(RoiRegion.Circle(99, 122, 18.5), Match, Frame());
        Assert.Equal((-28.0, 3.0), offset);
    }
    [Theory]
    [InlineData(null)] [InlineData("image")]
    public void LegacyAndManualRecipesKeepTheirRoi(string? reference)
    {
        var p = Parameters(); if (reference is null) p.Remove("positionReference"); else p["positionReference"] = reference;
        var roi = RoiRegion.Circle(10, 20, 5);
        Assert.Equal(roi, CirclePositioning.Resolve(roi, p, Match, Frame()));
    }
    [Fact]
    public void NoMatchDoesNotReuseASavedPosition()
    {
        var roi = RoiRegion.Circle(10, 20, 5);
        Assert.Equal(roi, CirclePositioning.Resolve(roi, Parameters(), null, Frame()));
        Assert.False(CirclePositioning.UsesMatch(Parameters(), null));
    }
    [Fact]
    public void PositionedCalipersRemainVisibleWhenReferenceCircleCrossesImageEdge()
    {
        var roi = RoiRegion.Circle(5, 30, 18.5);
        var p = new JsonObject { ["caliperPlacement"] = "roiBoundary", ["caliperCount"] = 32, ["searchLength"] = 16 };
        Assert.Empty(CaliperLayout.Create(roi, p, true, 100, 100));
        Assert.Equal(32, CaliperLayout.Create(roi, p, true, 100, 100, allowPartialCircle: true).Count);
    }
    [Fact]
    public void EditingAPartiallyOutsideCircleKeepsAValidStaticRecipeAndExactOffset()
    {
        var actual = RoiRegion.Circle(-5, 30, 18.5);
        var stored = CirclePositioning.StoredCircle(actual);
        Assert.Equal(0, stored.X);
        var match = new VisionMatchPosition(20, 40, 0, 1);
        var frame = Frame(0, 0);
        var offset = CirclePositioning.Offset(actual, match, frame);
        var p = Parameters(); p["matchOffsetX"] = offset.X; p["matchOffsetY"] = offset.Y;
        Assert.Equal(actual, CirclePositioning.Resolve(stored, p, match, frame));
        Assert.Equal(actual, CirclePositioning.Move(actual, 0, 0));
        Assert.Equal(-2, CirclePositioning.Move(actual, 3, 0).X);
    }
}
