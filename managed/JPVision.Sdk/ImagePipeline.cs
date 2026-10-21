using System.Text.Json;

namespace JPVision.Sdk;

public sealed partial class InspectionResult
{
    /// <summary>Current-frame metadata and independent overlays carried by a published Image output.
    /// Pixel access remains GetOutputImage; overlays are never burned into those pixels.</summary>
    public JsonElement GetImagePipeline(string name)
    {
        if (Status != ExecutionStatus.Succeeded)
            throw new InvalidOperationException("Image pipeline results require a succeeded run.");
        var output = Outputs.GetProperty(name);
        if (output.ValueKind != JsonValueKind.Object || !output.TryGetProperty("pipeline", out var pipeline) ||
            pipeline.ValueKind != JsonValueKind.Object || !pipeline.TryGetProperty("results", out var results) ||
            results.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"Output '{name}' is not an image with attached results.");
        return pipeline.Clone();
    }

    /// <summary>Read the latest attached result of a type (Features, Contours, PointSet, Measurement, Result, Pose,
    /// Int, Bool or Decision). A later empty detection replaces earlier geometry.</summary>
    public T GetImageAttachedOutput<T>(string name, string type)
    {
        var results = GetImagePipeline(name).GetProperty("results");
        if (!results.TryGetProperty(type, out var value))
            throw new KeyNotFoundException($"Image '{name}' has no attached '{type}' result in this run.");
        return value.Deserialize<T>(GeometryJsonOptions)!;
    }

    private JsonElement ResolvePublishedValue(string name, string type)
    {
        var value = Outputs.GetProperty(name);
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("pipeline", out var pipeline))
        {
            if (!pipeline.GetProperty("results").TryGetProperty(type, out var attached))
                throw new KeyNotFoundException($"Image '{name}' has no attached '{type}' result in this run.");
            return attached;
        }
        return value;
    }
}
