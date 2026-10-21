using System.Text.Json.Serialization;

namespace JPVision.Sdk;

/// <summary>One detector's current measurement. Selected is null when no target exists at SelectedIndex.</summary>
public sealed record VisionMeasurement<T> where T : class
{
    [JsonPropertyName("coordinateSpace")] public string CoordinateSpace { get; init; } = "original";
    [JsonPropertyName("coordinatePlane")] public string? CoordinatePlane { get; init; }
    [JsonPropertyName("dataAvailable")] public bool DataAvailable { get; init; } = true;
    [JsonPropertyName("featureKind")] public string FeatureKind { get; init; } = "";
    [JsonPropertyName("count")] public int Count { get; init; }
    [JsonPropertyName("found")] public bool Found { get; init; }
    /// <summary>Only the configured inclusive count range; not a precision or whole-recipe decision.</summary>
    [JsonPropertyName("passed")] public bool Passed { get; init; }
    [JsonPropertyName("selectedIndex")] public int SelectedIndex { get; init; }
    [JsonPropertyName("selectedValid")] public bool SelectedValid { get; init; }
    [JsonPropertyName("selected")] public T? Selected { get; init; }
    [JsonPropertyName("caliper")] public VisionCaliperDetails? Caliper { get; init; }
    /// <summary>Hough working resolution. Null for calipers and runtime versions before 0.7.1.</summary>
    [JsonPropertyName("detection")] public VisionDetectionResolution? Detection { get; init; }
    /// <summary>
    /// Upstream match center used to position this search, distinct from the measured target center.
    /// Null when no match-based position reference is used or when reading an older runtime result.
    /// </summary>
    [JsonPropertyName("position")] public VisionPositionReference? Position { get; init; }
    [JsonPropertyName("fixture")] public VisionFixture? Fixture { get; init; }
}

/// <summary>Original-image template center plus the configured translation to the search center.</summary>
public sealed record VisionPositionReference(
    [property: JsonPropertyName("source"), JsonRequired] string Source,
    [property: JsonPropertyName("centerX"), JsonRequired] double CenterX,
    [property: JsonPropertyName("centerY"), JsonRequired] double CenterY,
    [property: JsonPropertyName("offsetX"), JsonRequired] double OffsetX,
    [property: JsonPropertyName("offsetY"), JsonRequired] double OffsetY,
    [property: JsonPropertyName("searchCenterX"), JsonRequired] double SearchCenterX,
    [property: JsonPropertyName("searchCenterY"), JsonRequired] double SearchCenterY,
    [property: JsonPropertyName("angleDegrees"), JsonRequired] double AngleDegrees,
    [property: JsonPropertyName("scale"), JsonRequired] double Scale,
    [property: JsonPropertyName("transform"), JsonRequired] string Transform);

/// <summary>Working resolution of automatic Hough detection; output coordinates remain original pixels.</summary>
public sealed record VisionDetectionResolution
{
    [JsonPropertyName("downsampled")] public bool Downsampled { get; init; }
    [JsonPropertyName("inputWidth")] public int InputWidth { get; init; }
    [JsonPropertyName("inputHeight")] public int InputHeight { get; init; }
    [JsonPropertyName("workWidth")] public int WorkWidth { get; init; }
    [JsonPropertyName("workHeight")] public int WorkHeight { get; init; }
    [JsonPropertyName("scaleX")] public double ScaleX { get; init; }
    [JsonPropertyName("scaleY")] public double ScaleY { get; init; }
}

/// <summary>Caliper sampling diagnostics, including runs where too few points remain to fit a shape.</summary>
public sealed record VisionCaliperDetails
{
    /// <summary>guide is manual, roiBoundary follows an ROI edge, roiCenter follows a line-search strip center.</summary>
    [JsonPropertyName("placement")] public string Placement { get; init; } = "guide";
    /// <summary>Actual requested sampling windows in original image coordinates; present in roiBoundary mode.</summary>
    [JsonPropertyName("guides")] public IReadOnlyList<VisionCaliperWindow> Guides { get; init; } = Array.Empty<VisionCaliperWindow>();
    [JsonPropertyName("requestedCount")] public int RequestedCount { get; init; }
    [JsonPropertyName("sampledCount")] public int SampledCount { get; init; }
    [JsonPropertyName("retainedCount")] public int RetainedCount { get; init; }
    [JsonPropertyName("rejectedCount")] public int RejectedCount { get; init; }
    [JsonPropertyName("missingCount")] public int MissingCount { get; init; }
    /// <summary>Windows with usable image samples; not the number of detected or retained edges.</summary>
    [JsonPropertyName("validSamplingCalipers")] public int? ValidSamplingCalipers { get; init; }
    [JsonPropertyName("rejectedPoints")] public IReadOnlyList<VisionPoint> RejectedPoints { get; init; } = Array.Empty<VisionPoint>();
    /// <summary>Circle/line-fit acceptance; null when older runtimes do not report it.</summary>
    [JsonPropertyName("fitSucceeded")] public bool? FitSucceeded { get; init; }
    /// <summary>Empty on success, otherwise an explicit reason such as InsufficientInliers.</summary>
    [JsonPropertyName("fitFailureReason")] public string? FitFailureReason { get; init; }
    [JsonPropertyName("minimumRequiredPoints")] public int? MinimumRequiredPoints { get; init; }
    [JsonPropertyName("requestedRemovalCount")] public int? RequestedRemovalCount { get; init; }
    /// <summary>Maximum accepted distance from the fitted circle or line, in its image-plane pixels.</summary>
    [JsonPropertyName("maximumDeviation")] public double? MaximumDeviation { get; init; }
    [JsonPropertyName("fitIterations")] public int? FitIterations { get; init; }
    /// <summary>Bound on line fitting refinement; reaching it produces an invalid fit.</summary>
    [JsonPropertyName("fitIterationLimit")] public int? FitIterationLimit { get; init; }
    /// <summary>Largest perpendicular residual of the initial line fitted to all sampled points.</summary>
    [JsonPropertyName("initialMaximumDeviation")] public double? InitialMaximumDeviation { get; init; }
    [JsonPropertyName("initialRmsError")] public double? InitialRmsError { get; init; }
    /// <summary>Residuals of the retained-point candidate, also available for a rejected fit when possible.</summary>
    [JsonPropertyName("maximumRetainedDeviation")] public double? MaximumRetainedDeviation { get; init; }
    [JsonPropertyName("finalRmsError")] public double? FinalRmsError { get; init; }
    /// <summary>Distance between caliper stations in pixels of the current input plane.</summary>
    [JsonPropertyName("effectiveSamplingSpacing")] public double? EffectiveSamplingSpacing { get; init; }
    /// <summary>Requested annular sampling region, distinct from the fitted circle's OutputAnnulus.</summary>
    [JsonPropertyName("searchAnnulus")] public VisionAnnulus? SearchAnnulus { get; init; }
}

/// <summary>Caliper center, unit sampling direction, projection width and effective search span (pixels).</summary>
public sealed record VisionCaliperWindow
{
    [JsonPropertyName("x")] public double X { get; init; }
    [JsonPropertyName("y")] public double Y { get; init; }
    [JsonPropertyName("normalX")] public double NormalX { get; init; }
    [JsonPropertyName("normalY")] public double NormalY { get; init; }
    [JsonPropertyName("width")] public double Width { get; init; }
    [JsonPropertyName("height")] public double Height { get; init; }
}
