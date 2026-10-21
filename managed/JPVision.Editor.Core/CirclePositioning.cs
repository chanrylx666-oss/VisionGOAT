using JPVision.Sdk;
using System.Globalization;
using System.Text.Json.Nodes;

namespace JPVision.Editor.Core;

/// <summary>Translation-only circle reference, using original-image match coordinates.</summary>
public static class CirclePositioning
{
    public static bool UsesMatch(JsonObject parameters, VisionMatchPosition? match) =>
        match is not null && parameters["positionReference"]?.GetValue<string>() is "auto" or "matchCenter";

    public static RoiRegion Resolve(RoiRegion roi, JsonObject parameters, VisionMatchPosition? match, FrameInfo frame)
    {
        if (!UsesMatch(parameters, match) || roi.Shape is not ("circle" or "annulus" or "none")) return roi;
        if (roi.Shape == "annulus") return RoiRegion.Annulus(match!.CenterX - frame.OriginX + Number(parameters, "matchOffsetX", 0),
            match.CenterY - frame.OriginY + Number(parameters, "matchOffsetY", 0), roi.InnerRadius, roi.Radius, roi.StartAngle, roi.EndAngle);
        double radius = roi.Shape == "circle" ? roi.Radius : Number(parameters, "matchSearchRadius", 80);
        return RoiRegion.Circle(match!.CenterX - frame.OriginX + Number(parameters, "matchOffsetX", 0),
            match.CenterY - frame.OriginY + Number(parameters, "matchOffsetY", 0), radius);
    }

    /// <summary>Records a drawn circle's offset; it follows the next selected match.</summary>
    public static (double X, double Y) Offset(RoiRegion drawn, VisionMatchPosition match, FrameInfo frame) =>
        (drawn.X + frame.OriginX - match.CenterX, drawn.Y + frame.OriginY - match.CenterY);

    /// <summary>Static recipe ROI stays valid even when this execution's positioned center is outside the image.</summary>
    public static RoiRegion StoredCircle(RoiRegion drawn) =>
        drawn.Shape == "annulus" ? RoiRegion.Annulus(Math.Clamp(drawn.X, 0, 32768), Math.Clamp(drawn.Y, 0, 32768), drawn.InnerRadius, drawn.Radius, drawn.StartAngle, drawn.EndAngle) :
        RoiRegion.Circle(Math.Clamp(drawn.X, 0, 32768), Math.Clamp(drawn.Y, 0, 32768), drawn.Radius);

    /// <summary>Positioned circle drags preserve the requested displacement at an image boundary.</summary>
    public static RoiRegion Move(RoiRegion original, double dx, double dy) => original.Translate(dx, dy);

    private static double Number(JsonObject p, string name, double fallback) =>
        p[name] is { } value ? double.Parse(value.ToJsonString(), CultureInfo.InvariantCulture) : fallback;
}
