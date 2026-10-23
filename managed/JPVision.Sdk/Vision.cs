using System.Runtime.InteropServices;
using System.Text.Json;

namespace JPVision.Sdk;

public class VisionException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}
public sealed class VisionLicenseException(int code, string message) : VisionException(code, message);
public enum VisionPixelFormat
{
    Gray8 = 1, Bgr24 = 2
}
public enum ExecutionStatus
{
    Succeeded, Failed, Cancelled, TimedOut
}
public enum InspectionDecision
{
    Unknown, Pass, Fail
}
public record FrameInfo(int Width, int Height, int Stride, VisionPixelFormat PixelFormat, int OriginX, int OriginY);
public record LicenseInfo(string Status, int Code, string? LicenseId, string? Customer, string? Message, string Store)
{
    public bool IsValid => Code == 0 && Status == "Valid";
}

public static class VisionLicensing
{
    private static LicenseInfo Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var value = doc.RootElement;
        string? Optional(string key) => value.TryGetProperty(key, out var p) ? p.GetString() : null;
        return new(value.GetProperty("status").GetString()!, value.GetProperty("code").GetInt32(), Optional("licenseId"), Optional("customer"), Optional("message"), Optional("store") ?? "");
    }
    public static LicenseInfo GetStatus(string? store = null) => Parse(Native.TakeString(Native.LicenseStatus(store, "runtime", out var text), text));
    /// <summary>Checks the editor feature in the same signed, machine-bound license store.</summary>
    public static LicenseInfo GetEditorStatus(string? store = null) => Parse(Native.TakeString(Native.LicenseStatus(store, "studio", out var text), text));
    public static LicenseInfo Import(string file, string? store = null) => Parse(Native.TakeString(Native.LicenseImport(store, Path.GetFullPath(file), out var text), text));
    public static string GetMachineRequest() => Native.TakeString(Native.MachineRequest(out var text), text);
}

public sealed class VisionFrame : IDisposable
{
    internal FrameHandle Handle
    {
        get;
    }
    public FrameInfo Info
    {
        get;
    }
    internal VisionFrame(IntPtr pointer)
    {
        Handle = new(pointer);
        try
        {
            var info = NativeImageInfo.Empty;
            Native.Check(Native.FrameInfo(Handle, ref info));
            Info = new((int)info.Width, (int)info.Height, (int)info.Stride, (VisionPixelFormat)info.PixelFormat, info.OriginX, info.OriginY);
        }
        catch { Handle.Dispose(); throw; }
    }
    public static VisionFrame CopyFrom(ReadOnlySpan<byte> pixels, int width, int height, int stride, VisionPixelFormat format)
        => CopyFrom(pixels, width, height, stride, format, 0, 0);
    /// <summary>Copy an image whose top-left has the given original-image pixel coordinates.</summary>
    public static unsafe VisionFrame CopyFrom(ReadOnlySpan<byte> pixels, int width, int height, int stride, VisionPixelFormat format, int originX, int originY)
    {
        if (width <= 0 || height <= 0 || stride <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        var info = NativeImageInfo.Empty;
        info.Width = (uint)width;
        info.Height = (uint)height;
        info.Stride = (uint)stride;
        info.PixelFormat = (uint)format;
        info.Length = (ulong)pixels.Length;
        info.OriginX = originX;
        info.OriginY = originY;
        fixed (byte* data = pixels)
        {
            Native.Check(Native.FrameCopy(in info, data, out var pointer));
            return new(pointer);
        }
    }
    public unsafe byte[] CopyPixels()
    {
        byte[] pixels = new byte[checked(Info.Stride * Info.Height)];
        fixed (byte* data = pixels)
            Native.Check(Native.FramePixels(Handle, data, (ulong)pixels.Length));
        return pixels;
    }
    public void Dispose() => Handle.Dispose();
}

public sealed class VisionEngine : IDisposable
{
    private readonly EngineHandle handle;
    public static string Version => Marshal.PtrToStringUTF8(Native.Version())!;
    /// <summary>Native component descriptors, typed ports, parameters and defaults.</summary>
    public static JsonElement GetNodeCatalog()
    {
        using var document = JsonDocument.Parse(Native.TakeString(Native.NodeCatalog(out var text), text));
        return document.RootElement.Clone();
    }
    public static JsonElement GetDiagnostics()
    {
        using var document = JsonDocument.Parse(Native.TakeString(Native.Diagnostics(out var text), text));
        return document.RootElement.Clone();
    }
    public VisionEngine(string? licenseStore = null)
    {
        Native.Check(Native.EngineCreate(JsonSerializer.Serialize(new
        {
            licenseStore = licenseStore ?? ""
        }), out var pointer));
        handle = new(pointer);
    }
    public VisionRecipe LoadRecipe(string directory)
    {
        Native.Check(Native.RecipeLoad(handle, Path.GetFullPath(directory), out var pointer));
        return new(pointer);
    }
    public string ValidateRecipe(string json) => Native.TakeString(Native.RecipeValidate(handle, json, out var text), text);
    public static string SealRecipe(string json) => Native.TakeString(Native.RecipeSeal(json, out var text), text);
    public void Dispose() => handle.Dispose();
}

public sealed class VisionRecipe : IDisposable
{
    private readonly RecipeHandle handle;
    internal VisionRecipe(IntPtr pointer) => handle = new(pointer);
    public VisionSession CreateSession()
    {
        Native.Check(Native.SessionCreate(handle, out var pointer));
        return new(pointer);
    }
    public JsonElement Description
    {
        get
        {
            using var document = JsonDocument.Parse(Native.TakeString(Native.RecipeDescription(handle, out var text), text));
            return document.RootElement.Clone();
        }
    }
    public void Dispose() => handle.Dispose();
}

public sealed record RunRequest
{
    public string RunId { get; init; } = Guid.NewGuid().ToString();
    public string FrameId { get; init; } = Guid.NewGuid().ToString();
    public IReadOnlyDictionary<string, object?> Inputs { get; init; } = new Dictionary<string, object?>();
    /// <summary>Overrides declared global variables for this run only. The saved recipe is unchanged.</summary>
    public IReadOnlyDictionary<string, object?> Variables { get; init; } = new Dictionary<string, object?>();
    public bool Debug
    {
        get; init;
    }
    /// <summary>Retain this executed node's full output for an editor preview, even without Debug.</summary>
    public string PreviewNodeId { get; init; } = "";
    public int TimeoutMs
    {
        get; init;
    }
}

public sealed class VisionSession : IDisposable, IAsyncDisposable
{
    private readonly SessionHandle handle;
    private readonly object gate = new();
    private bool disposed;
    private Task<InspectionResult>? active;
    internal VisionSession(IntPtr pointer) => handle = new(pointer);
    public Task<InspectionResult> RunAsync(VisionFrame frame, RunRequest? request = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        cancellationToken.ThrowIfCancellationRequested();
        request ??= new();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (active is { IsCompleted: false })
                throw new VisionException(3, "This session is already running.");
            string json = JsonSerializer.Serialize(new
            {
                runId = request.RunId,
                frameId = request.FrameId,
                inputs = request.Inputs,
                variables = request.Variables,
                debug = request.Debug,
                previewNodeId = request.PreviewNodeId,
                timeoutMs = request.TimeoutMs
            });
            Native.Check(Native.RunCreate(handle, frame.Handle, json, out var pointer));
            active = ExecuteAsync(new RunHandle(pointer), cancellationToken);
            return active;
        }
    }
    private static async Task<InspectionResult> ExecuteAsync(RunHandle run, CancellationToken token)
    {
        using (run)
        using (token.Register(() => Native.RunCancel(run)))
        {
            return await Task.Run(() =>
            {
                Native.Check(Native.RunExecute(run, out var pointer));
                var resultHandle = new ResultHandle(pointer);
                try
                {
                    var json = Native.TakeJson(Native.ResultJson(resultHandle, out var text), text);
                    var result = new InspectionResult(resultHandle, json);
                    if (result.Status == ExecutionStatus.Cancelled)
                        throw new OperationCanceledException("Native run safely cancelled.", token);
                    if (result.Status == ExecutionStatus.TimedOut)
                        throw new TimeoutException("Native run completed after its deadline.");
                    resultHandle = null!;
                    return result;
                }
                finally { resultHandle?.Dispose(); }
            }).ConfigureAwait(false);
        }
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            handle.Dispose();
        }
    }
    public async ValueTask DisposeAsync()
    {
        Task<InspectionResult>? task;
        lock (gate)
        {
            Dispose();
            task = active;
        }
        if (task is not null)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (TimeoutException) { }
        }
    }
}

public readonly record struct VisionPoint(double X, double Y);
public sealed record VisionOverlay(string Kind, IReadOnlyList<VisionPoint> Points, string Color, double LineWidth, bool Closed, double X, double Y, string Text, double Width = 0, double Height = 0, double RadiusX = 0, double RadiusY = 0)
{
    public double InnerRadius { get; init; }
    public double OuterRadius { get; init; }
    public double StartAngle { get; init; }
    public double EndAngle { get; init; } = 360;
}

public sealed partial class InspectionResult : IDisposable
{
    private static readonly JsonSerializerOptions GeometryJsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly ResultHandle handle;
    public JsonElement Data
    {
        get;
    }
    public ExecutionStatus Status
    {
        get;
    }
    public InspectionDecision Decision
    {
        get;
    }
    public double ElapsedMs => Data.GetProperty("elapsedMs").GetDouble();
    public string RunId => Data.GetProperty("runId").GetString()!;
    public JsonElement Outputs => Data.GetProperty("outputs");
    internal InspectionResult(ResultHandle handle, string json)
        : this(handle, ParseOwnedJson(json)) { }
    private static JsonElement ParseOwnedJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
    internal InspectionResult(ResultHandle handle, JsonElement ownedData)
    {
        this.handle = handle;
        Data = ownedData;
        Status = Enum.Parse<ExecutionStatus>(Data.GetProperty("executionStatus").GetString()!);
        Decision = Enum.Parse<InspectionDecision>(Data.GetProperty("decision").GetString()!);
    }
    public T GetOutput<T>(string name) => Outputs.GetProperty(name).Deserialize<T>(GeometryJsonOptions)!;
    /// <summary>Read a published geometry measurement without retaining debug images.</summary>
    public VisionGeometryMeasurement GetGeometryMeasurement(string name)
    {
        if (Status != ExecutionStatus.Succeeded)
            throw new InvalidOperationException("Geometry measurements are available only for succeeded runs.");
        var json = ResolvePublishedValue(name, "Measurement");
        var value = json.Deserialize<VisionGeometryMeasurement>(GeometryJsonOptions)!;
        if (value is not null)
            value = value with
            {
                First = value.First is null ? null : value.First with { CoordinateSpace = value.CoordinateSpace },
                Second = value.Second is null ? null : value.Second with { CoordinateSpace = value.CoordinateSpace }
            };
        if (value is null || value.ResultKind != "geometryMeasurement" || value.Operation is not ("circleCenterDistance" or "circleEdgeDistance") ||
            value.CoordinateSpace is not ("original" or "annularUnwrapped") || value.Unit != "px" || value.Decision is null ||
            !Enum.IsDefined(value.Decision.Status) || value.FirstIndex < 0 || value.SecondIndex < 0 ||
            !double.IsFinite(value.LowerLimit) || !double.IsFinite(value.UpperLimit) || value.ParametersAvailable && value.LowerLimit > value.UpperLimit ||
            value.Overlay.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Invalid geometry measurement contract: " + name);
        if (value.Valid)
        {
            if (!value.ParametersAvailable || value.Value is not double number || !double.IsFinite(number) || number < 0 ||
                value.First is null || value.Second is null || !Finite(value.First) || !Finite(value.Second))
                throw new InvalidDataException("Valid geometry measurement requires finite circles and a value: " + name);
        }
        else if (value.Value is not null || value.First is not null || value.Second is not null ||
                 value.Decision.Status != InspectionDecision.Unknown)
            throw new InvalidDataException("Unavailable geometry measurement must have null values and Unknown decision: " + name);
        if (value.DistanceMode is not null || value.Operation == "circleEdgeDistance")
        {
            ValidateCircularDistances(json, value.Valid, value.Value, value.DistanceMode, value.CenterDistance, value.EdgeDistance, value.DistanceSegment);
            if (value.Operation != (value.DistanceMode == "edgeDistance" ? "circleEdgeDistance" : "circleCenterDistance") ||
                value.DistanceDefinition != (value.DistanceMode == "edgeDistance" ? "circleBoundaries" : "circleCenters"))
                throw new InvalidDataException("Circle distance mode and definition do not match.");
        }
        return value;
        static bool Finite(VisionCircleGeometry circle) => double.IsFinite(circle.CenterX) &&
            double.IsFinite(circle.CenterY) && double.IsFinite(circle.Radius) && circle.Radius > 0;
    }
    /// <summary>Read an executed node's retained output port. Scalars and image metadata are always retained.</summary>
    public T GetNodeOutput<T>(string nodeId, string portId)
    {
        if (Status != ExecutionStatus.Succeeded)
            throw new InvalidOperationException("Node outputs are available only for succeeded runs.");
        if (!Data.TryGetProperty("nodeOutputs", out var nodes) || !nodes.TryGetProperty(nodeId, out var ports) || !ports.TryGetProperty(portId, out var value))
            throw new KeyNotFoundException($"Output '{nodeId}/{portId}' is not retained; enable Debug or select the node for preview.");
        return value.Deserialize<T>(GeometryJsonOptions)!;
    }
    public IReadOnlyList<VisionCircle> GetCircles(string name = "Features") => ReadFeatures<VisionCircle>(name, "circle");
    public IReadOnlyList<VisionLine> GetLines(string name = "Features") => ReadFeatures<VisionLine>(name, "line");
    public IReadOnlyList<VisionRegion> GetRegions(string name = "Features") => ReadFeatures<VisionRegion>(name, "region");
    public IReadOnlyList<VisionMatch> GetMatches(string name = "Features") => ReadFeatures<VisionMatch>(name, "match");
    /// <summary>Read an explicitly published Measurement port. Available without Debug.</summary>
    public VisionMeasurement<T> GetMeasurement<T>(string name) where T : class
    {
        if (Status != ExecutionStatus.Succeeded)
            throw new InvalidOperationException("Measurements are available only for succeeded runs.");
        return ReadMeasurement<T>(ResolvePublishedValue(name, "Measurement"), name);
    }
    /// <summary>Read any executed detector's measurement by stable recipe node ID. Available without Debug.</summary>
    public VisionMeasurement<T> GetNodeMeasurement<T>(string nodeId) where T : class
    {
        if (Status != ExecutionStatus.Succeeded)
            throw new InvalidOperationException("Measurements are available only for succeeded runs.");
        if (!Data.TryGetProperty("measurements", out var values) || !values.TryGetProperty(nodeId, out var value))
            throw new KeyNotFoundException($"Node '{nodeId}' has no measurement in this run.");
        return ReadMeasurement<T>(value, nodeId);
    }
    /// <summary>Read an explicitly published Result port, including features, measurement and overlays. Available without Debug.</summary>
    public VisionResultPacket<T> GetResult<T>(string name) where T : class
    {
        if (Status != ExecutionStatus.Succeeded)
            throw new InvalidOperationException("Result packets are available only for succeeded runs.");
        return ReadResult<T>(ResolvePublishedValue(name, "Result"), name);
    }
    /// <summary>Read an executed detector's retained Result port. Enable Debug or select this node for preview.</summary>
    public VisionResultPacket<T> GetNodeResult<T>(string nodeId) where T : class
    {
        if (Status != ExecutionStatus.Succeeded)
            throw new InvalidOperationException("Result packets are available only for succeeded runs.");
        if (!Data.TryGetProperty("nodeOutputs", out var nodes) || !nodes.TryGetProperty(nodeId, out var ports) || !ports.TryGetProperty("result", out var value))
            throw new KeyNotFoundException($"Output '{nodeId}/result' is not retained; enable Debug or select the node for preview.");
        return ReadResult<T>(value, nodeId + "/result");
    }
    private VisionResultPacket<T> ReadResult<T>(JsonElement value, string name) where T : class
    {
        InvalidDataException Invalid(string reason) => new($"Result packet '{name}' {reason}.");
        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("measurement", out var rawMeasurement) || rawMeasurement.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("featureKind", out var kind) || kind.ValueKind != JsonValueKind.String ||
            !value.TryGetProperty("features", out var rawFeatures) || rawFeatures.ValueKind != JsonValueKind.Array ||
            !value.TryGetProperty("overlay", out var overlays) || overlays.ValueKind != JsonValueKind.Array ||
            !value.TryGetProperty("count", out var rawCount) || rawCount.ValueKind != JsonValueKind.Number || !rawCount.TryGetInt32(out int count) ||
            !value.TryGetProperty("found", out var rawFound) || rawFound.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !value.TryGetProperty("passed", out var rawPassed) || rawPassed.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw Invalid("is missing a required field or contains an invalid field type");

        if (!rawMeasurement.TryGetProperty("featureKind", out var measurementKind) || measurementKind.ValueKind != JsonValueKind.String ||
            !rawMeasurement.TryGetProperty("count", out var measurementCount) || measurementCount.ValueKind != JsonValueKind.Number || !measurementCount.TryGetInt32(out _) ||
            !rawMeasurement.TryGetProperty("found", out var measurementFound) || measurementFound.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !rawMeasurement.TryGetProperty("passed", out var measurementPassed) || measurementPassed.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !rawMeasurement.TryGetProperty("selectedIndex", out var selectedIndex) || selectedIndex.ValueKind != JsonValueKind.Number || !selectedIndex.TryGetInt32(out _) ||
            !rawMeasurement.TryGetProperty("selectedValid", out var selectedValid) || selectedValid.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !rawMeasurement.TryGetProperty("selected", out _))
            throw Invalid("contains an incomplete measurement");

        var measurement = ReadMeasurement<T>(rawMeasurement, name + "/measurement");
        string featureKind = measurement.FeatureKind;
        bool found = rawFound.GetBoolean(), passed = rawPassed.GetBoolean();
        if (kind.GetString() != featureKind || count < 0 || count != rawFeatures.GetArrayLength() || count != measurement.Count ||
            found != (count > 0) || found != measurement.Found || passed != measurement.Passed)
            throw Invalid("has inconsistent kind, count or status fields");
        if (measurement.SelectedIndex < 0 || measurement.SelectedValid != (measurement.SelectedIndex < count))
            throw Invalid("has inconsistent selection validity");

        var features = new List<T>(count);
        foreach (var item in rawFeatures.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("kind", out var actual) ||
                actual.ValueKind != JsonValueKind.String || actual.GetString() != featureKind)
                throw Invalid("contains a different feature kind; expected " + featureKind);
            ValidateResultFeature(item, featureKind, name);
            features.Add(item.Deserialize<T>(GeometryJsonOptions) ?? throw Invalid("contains a null feature"));
        }
        if (measurement.SelectedValid)
        {
            var selected = rawMeasurement.GetProperty("selected");
            if (selected.ValueKind != JsonValueKind.Object || !selected.TryGetProperty("kind", out var selectedKind) ||
                selectedKind.ValueKind != JsonValueKind.String || selectedKind.GetString() != featureKind)
                throw Invalid("contains a different selected feature kind; expected " + featureKind);
            ValidateResultFeature(selected, featureKind, name + "/selected");
            if (JsonSerializer.Serialize(measurement.Selected, GeometryJsonOptions) !=
                JsonSerializer.Serialize(features[measurement.SelectedIndex], GeometryJsonOptions))
                throw Invalid("contains a selected target that does not match features[selectedIndex]");
            measurement = measurement with { Selected = features[measurement.SelectedIndex] };
        }
        JsonElement? image = null;
        if (value.TryGetProperty("image", out var imageMetadata))
        {
            if (imageMetadata.ValueKind != JsonValueKind.Object)
                throw Invalid("contains invalid associated image metadata");
            image = imageMetadata.Clone();
        }
        return new VisionResultPacket<T>
        {
            CoordinateSpace = value.TryGetProperty("coordinateSpace", out var space) ? space.GetString()! : "original",
            CoordinatePlane = value.TryGetProperty("coordinatePlane", out var plane) ? plane.GetString() : null,
            FeatureKind = featureKind, Features = features.AsReadOnly(), Measurement = measurement,
            Count = count, Found = found, Passed = passed, Overlay = overlays.Clone(), Image = image
        };
    }
    private static void ValidateResultFeature(JsonElement feature, string kind, string name)
    {
        InvalidDataException Invalid(string field) => new($"Result packet '{name}' has an invalid or missing {kind} field '{field}'.");
        double Number(string field)
        {
            if (!feature.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.Number ||
                !value.TryGetDouble(out double number) || !double.IsFinite(number))
                throw Invalid(field);
            return number;
        }
        void Numbers(params string[] fields) { foreach (string field in fields) Number(field); }
        void Positive(string field) { if (Number(field) <= 0) throw Invalid(field); }
        void NonNegative(string field) { if (Number(field) < 0) throw Invalid(field); }
        void Points(string field, int? count = null)
        {
            if (!feature.TryGetProperty(field, out var points) || points.ValueKind != JsonValueKind.Array ||
                count is int required && points.GetArrayLength() != required)
                throw Invalid(field);
            foreach (var point in points.EnumerateArray())
                foreach (string axis in new[] { "x", "y" })
                    if (point.ValueKind != JsonValueKind.Object || !point.TryGetProperty(axis, out var value) ||
                        value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double coordinate) || !double.IsFinite(coordinate))
                        throw Invalid(field + "/" + axis);
        }
        switch (kind)
        {
            case "circle": Numbers("x", "y"); Positive("radius"); break;
            case "line": Numbers("x1", "y1", "x2", "y2", "angleDegrees"); NonNegative("length"); break;
            case "match":
                Numbers("x", "y", "centerX", "centerY", "angleDegrees", "score");
                Positive("width"); Positive("height"); Positive("scale"); Points("corners", 4);
                break;
            case "region":
                Numbers("centroidX", "centroidY"); NonNegative("area"); NonNegative("perimeter"); NonNegative("circularity");
                foreach (string field in new[] { "x", "y", "width", "height" })
                    if (!feature.TryGetProperty(field, out var integer) || integer.ValueKind != JsonValueKind.Number ||
                        !integer.TryGetInt32(out int number) || (field is "width" or "height") && number < 1)
                        throw Invalid(field);
                if (!feature.TryGetProperty("centroidValid", out var valid) || valid.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw Invalid("centroidValid");
                break;
        }
        if (feature.TryGetProperty("edgePoints", out var edgePoints))
        {
            Points("edgePoints");
            if (edgePoints.GetArrayLength() > 0) NonNegative("rmsError");
        }
        if (feature.TryGetProperty("rmsError", out _)) NonNegative("rmsError");
    }
    private VisionMeasurement<T> ReadMeasurement<T>(JsonElement value, string name) where T : class
    {
        if (Status != ExecutionStatus.Succeeded)
            throw new InvalidOperationException("Measurements are available only for succeeded runs.");
        string kind = typeof(T) == typeof(VisionCircle) ? "circle" : typeof(T) == typeof(VisionLine) ? "line"
            : typeof(T) == typeof(VisionRegion) ? "region" : typeof(T) == typeof(VisionMatch) ? "match"
            : throw new ArgumentException("Measurement type must be VisionCircle, VisionLine, VisionRegion or VisionMatch.");
        if (!value.TryGetProperty("featureKind", out var actual) || actual.GetString() != kind)
            throw new InvalidDataException($"Measurement '{name}' contains a different feature kind; expected {kind}.");
        var measurement = value.Deserialize<VisionMeasurement<T>>(GeometryJsonOptions)!;
        if (measurement.SelectedValid != (measurement.Selected is not null))
            throw new InvalidDataException($"Measurement '{name}' has inconsistent selection validity.");
        return measurement;
    }
    private IReadOnlyList<T> ReadFeatures<T>(string name, string kind)
    {
        if (Status != ExecutionStatus.Succeeded)
            throw new InvalidOperationException("Feature outputs are available only for succeeded runs.");
        var output = ResolvePublishedValue(name, "Features");
        var values = new List<T>();
        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("kind", out var actual) || actual.GetString() != kind)
                throw new InvalidDataException($"Output '{name}' contains a different feature kind; expected {kind}.");
            values.Add(item.Deserialize<T>(GeometryJsonOptions)!);
        }
        return values.AsReadOnly();
    }
    public IReadOnlyList<VisionOverlay> GetOverlays(string name = "ResultOverlay")
    {
        if (!Outputs.TryGetProperty(name, out var values))
            return Array.Empty<VisionOverlay>();
        if (values.ValueKind == JsonValueKind.Object && values.TryGetProperty("pipeline", out var pipeline))
            values = pipeline.GetProperty("overlay");
        var overlays = new List<VisionOverlay>();
        foreach (var item in values.EnumerateArray())
        {
            var points = new List<VisionPoint>();
            if (item.TryGetProperty("points", out var p))
                foreach (var point in p.EnumerateArray())
                    points.Add(new(point[0].GetDouble(), point[1].GetDouble()));
            double Number(string key, double fallback = 0) => item.TryGetProperty(key, out var value) ? value.GetDouble() : fallback;
            string Text(string key, string fallback = "") => item.TryGetProperty(key, out var value) ? value.GetString()! : fallback;
            overlays.Add(new(Text("type"), points.AsReadOnly(), Text("color", "#00FF00"), Number("lineWidth", 2), item.TryGetProperty("closed", out var closed) && closed.GetBoolean(), Number("x"), Number("y"), Text("text"), Number("width"), Number("height"), Number("radiusX", Number("radius")), Number("radiusY", Number("radius")))
            { InnerRadius = Number("innerRadius"), OuterRadius = Number("outerRadius"), StartAngle = Number("startAngle"), EndAngle = Number("endAngle", 360) });
        }
        return overlays.AsReadOnly();
    }
    public VisionFrame GetIntermediateImage(string nodeId)
    {
        Native.Check(Native.ResultFrame(handle, nodeId, out var pointer));
        return new(pointer);
    }
    /// <summary>Read a retained detector input, before its own ROI or matched-target projection.</summary>
    public VisionFrame GetInputImage(string nodeId)
    {
        try
        {
            Native.Check(Native.ResultInputFrame(handle, nodeId, out var pointer));
            return new(pointer);
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new NotSupportedException("GetInputImage requires the matching current JP-Vision Runtime DLL.", ex);
        }
    }
    public VisionFrame GetOutputImage(string name)
    {
        Native.Check(Native.ResultOutputFrame(handle, name, out var pointer));
        return new(pointer);
    }
    public void Dispose() => handle.Dispose();
}
