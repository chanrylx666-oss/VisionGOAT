using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using static JPVision.Sdk.Execution.ManagedExecutionPlan;

namespace JPVision.Sdk.Execution;

/// <summary>
/// Owns recipe business execution in C#. Each native call invokes one explicitly selected image
/// kernel; native code retains pixel buffers and values until this scheduler releases their last use.
/// </summary>
internal static class ManagedExecution
{
    public static InspectionResult Execute(RunHandle run, ManagedExecutionPlan plan, JsonObject request)
    {
        var clock = Stopwatch.StartNew();
        using var buffers = new NativeJsonBuffers();
        var outputs = new SortedDictionary<string, RawKernelJson>(Comparer);
        var measurements = new SortedDictionary<string, RawKernelJson>(Comparer);
        var nodeOutputs = new SortedDictionary<string, RawKernelJson>(Comparer);
        var portValues = new Dictionary<string, IReadOnlyDictionary<string, RawKernelJson>>(StringComparer.Ordinal);
        var debugValues = new SortedDictionary<string, RawKernelJson>(Comparer);
        var debugOverlays = new SortedDictionary<string, RawKernelJson>(Comparer);
        var traces = new SortedDictionary<string, Dictionary<string, object?>>(Comparer);
        foreach (string id in plan.Order)
            traces[id] = new() { ["status"] = "Pending", ["elapsedMs"] = 0d };
        var data = new Dictionary<string, object?>
        {
            ["executionStatus"] = "Succeeded", ["decision"] = "Unknown",
            ["runId"] = Text(request, "runId"), ["frameId"] = Text(request, "frameId"),
            ["recipeId"] = plan.Definition["recipeId"], ["recipeVersion"] = plan.Definition["recipeVersion"],
            ["recipeKind"] = plan.Definition["kind"], ["contentHash"] = plan.Definition["contentHash"],
            ["runtimeVersion"] = VisionEngine.Version, ["opencvVersion"] = "",
            ["inputs"] = new JsonObject(), ["variables"] = new JsonObject(),
            ["outputs"] = outputs, ["measurements"] = measurements, ["nodes"] = traces,
            ["executionOrder"] = plan.Order, ["nodeOutputs"] = nodeOutputs, ["error"] = null
        };
        var uses = new Dictionary<string, int>(plan.Uses, StringComparer.Ordinal);
        string current = "";
        KernelHandle? kernel = null;
        bool failed = false;
        ResultHandle? result = null;
        try
        {
            // Admission, license validation, cancellation and budgets remain enforced by the
            // native host. No image is copied into managed memory for graph orchestration.
            int beginStatus = Native.KernelBegin(run, out var pointer, out var text);
            if (pointer != IntPtr.Zero) kernel = new(pointer);
            var info = Native.TakeJson(beginStatus, text);
            if (kernel is null) throw new InvalidDataException("Missing native kernel context.");
            var publicValues = JsonNode.Parse(info.GetProperty("inputs").GetRawText())!.AsObject();
            var globals = ManagedBindings.Globals(plan.Definition,
                request["variables"] as JsonObject ?? new JsonObject());
            data["inputs"] = publicValues;
            data["variables"] = globals;
            data["runtimeVersion"] = info.GetProperty("runtimeVersion");
            data["opencvVersion"] = info.GetProperty("opencvVersion");
            bool retainedAll = request["debug"]?.GetValue<bool>() == true;
            string preview = Text(request, "previewNodeId");

            bool Skipped(string id) => traces.TryGetValue(id, out var trace) && trace["status"] as string == "Skipped";
            foreach (string id in plan.Order)
            {
                current = id;
                var nodeClock = Stopwatch.StartNew();
                var trace = traces[id];
                trace["status"] = "Running";
                var frame = Native.TakeJson(Native.KernelInput(kernel, id, out text), text);
                var coordinates = new ManagedInputCoordinates(frame.GetProperty("originX").GetInt32(),
                    frame.GetProperty("originY").GetInt32(), frame.GetProperty("coordinateSpace").GetString()!,
                    frame.GetProperty("coordinatePlane").GetString()!, frame.GetProperty("hasFixture").GetBoolean(),
                    frame.GetProperty("hasMatchPosition").GetBoolean());
                var fields = new Dictionary<(string Node, string Port, string Field), JsonNode?>();
                JsonNode? Field(string source, string port, string field)
                {
                    var key = (source, port, field);
                    if (!fields.TryGetValue(key, out var value))
                    {
                        var encoded = Native.TakeJson(Native.KernelField(kernel, source, port, field, out var raw), raw);
                        value = JsonNode.Parse(encoded.GetRawText());
                        fields.Add(key, value);
                    }
                    return value?.DeepClone();
                }
                var node = plan.Nodes[id];
                var resolved = ManagedBindings.Resolve(node, publicValues, plan, Field, Skipped, coordinates, globals);
                double bindingMs = nodeClock.Elapsed.TotalMilliseconds;
                var response = buffers.Take(Native.KernelStep(kernel, id, resolved.Parameters.ToJsonString(), out text), text);
                var step = RawKernelJson.ReadStep(response);
                var ports = step.Ports;
                nodeOutputs[id] = ports;
                portValues[id] = step.PortValues;
                if (step.PortValues.TryGetValue("measurement", out var measurement)) measurements[id] = measurement;
                if (retainedAll || id == preview)
                {
                    string primary = Text((JsonObject)((JsonArray)plan.Descriptor(id)["outputs"]!)[0]!, "id");
                    var primaryValue = step.PortValues[primary];
                    string primaryType = Text((JsonObject)((JsonArray)plan.Descriptor(id)["outputs"]!)[0]!, "type");
                    debugValues[id] = primaryType == "Mask" ? PublicMask(primaryValue) : primaryValue;
                    data["debugValues"] = debugValues;
                    if (step.PortValues.TryGetValue("overlay", out var overlay)) debugOverlays[id] = overlay;
                    else if (step.Envelope.TryGetValue("debugOverlay", out overlay)) debugOverlays[id] = overlay;
                    if (debugOverlays.Count != 0) data["debugOverlays"] = debugOverlays;
                }
                bool skipped = step.Envelope["skipped"].Boolean();
                double elapsed = nodeClock.Elapsed.TotalMilliseconds;
                double prepare = bindingMs + step.Envelope["prepareMs"].Number();
                double execute = step.Envelope["executeMs"].Number();
                trace["status"] = skipped ? "Skipped" : "Succeeded";
                trace["elapsedMs"] = elapsed;
                trace["prepareMs"] = prepare;
                trace["executeMs"] = execute;
                trace["resultMs"] = Math.Max(0, elapsed - prepare - execute);
                trace["localVariables"] = resolved.Locals;
                trace["resolvedParameters"] = Snapshot(node, resolved.Parameters);
                var unavailable = step.Envelope["unavailable"];
                bool hasUnavailable = unavailable.ToElement().GetArrayLength() != 0;
                if (hasUnavailable)
                {
                    trace["unavailableParameters"] = unavailable;
                    trace["resolvedParameters"] = null;
                }
                if (skipped)
                    trace["reason"] = hasUnavailable ? "UnavailableParameterOrInput" : "UnavailableSelection";
                foreach (var source in plan.Incoming[id].Values.Concat(plan.ParameterIncoming[id].Values))
                    if (--uses[source.Node] == 0) Native.Check(Native.KernelReleaseNode(kernel, source.Node));
                // An executed preview-only terminal has no downstream consumer or published output.
                if (uses[id] == 0) Native.Check(Native.KernelReleaseNode(kernel, id));
            }
            var published = new Dictionary<(string Node, string Port), RawKernelJson>();
            foreach (var output in ((JsonObject)plan.Definition["outputs"]!).OrderBy(p => p.Key, Comparer))
            {
                string id = Text(output.Value!, "node"), port = Text(output.Value!, "port");
                var key = (id, port);
                bool cached = published.TryGetValue(key, out var value);
                if (!cached && portValues.TryGetValue(id, out var ports)) cached = ports.TryGetValue(port, out value);
                // Every alias still passes through native budget accounting and image retention.
                if (cached) Native.TakeJson(Native.KernelPublish(kernel, output.Key, 0, out text), text);
                else value = RawKernelJson.ReadValue(buffers.Take(Native.KernelPublish(kernel, output.Key, 1, out text), text));
                if (Text(output.Value!, "type") == "Mask") value = PublicMask(value!);
                outputs[output.Key] = value!;
                published.TryAdd(key, value!);
                if (--uses[id] == 0) Native.Check(Native.KernelReleaseNode(kernel, id));
            }
            data["decision"] = Decision(plan, outputs, traces);
        }
        catch (Exception error)
        {
            if (kernel is null) throw;
            failed = true;
            int code = error is VisionException known ? known.Code : error is ArgumentException ? 1 : 6;
            string status = code == 7 ? "Cancelled" : code == 8 ? "TimedOut" : "Failed";
            data["executionStatus"] = status;
            data["decision"] = "Unknown";
            outputs.Clear(); measurements.Clear(); nodeOutputs.Clear(); portValues.Clear();
            data["error"] = new { code, nodeId = current, message = error.Message };
            foreach (var trace in traces)
                if (trace.Key == current) trace.Value["status"] = status;
                else if (trace.Value["status"] as string == "Pending") trace.Value["status"] = "Skipped";
        }
        try
        {
            if (kernel is null) throw new InvalidDataException("Native kernel initialization failed before creating a result.");
            Native.Check(Native.KernelFinish(kernel, failed ? 1 : 0, out var pointer));
            result = new(pointer);
            data["elapsedMs"] = clock.Elapsed.TotalMilliseconds;
            var inspection = new InspectionResult(result, JsonSerializer.SerializeToElement(data));
            result = null;
            return inspection;
        }
        finally { result?.Dispose(); kernel?.Dispose(); }
    }

    private static JsonObject Snapshot(JsonObject node, JsonObject parameters)
    {
        var snapshot = new JsonObject();
        foreach (var item in parameters)
            if (item.Key is "roi" or "firstRoi" or "secondRoi" ||
                node["parameters"]?[item.Key] is JsonObject original &&
                (original.ContainsKey("node") || original.ContainsKey("variable") || original.ContainsKey("expression")))
                snapshot[item.Key] = item.Value?.DeepClone();
        return snapshot;
    }

    private static RawKernelJson PublicMask(RawKernelJson value) => value.WithoutProperty("foregroundPixels");

    private static string Decision(ManagedExecutionPlan plan, IReadOnlyDictionary<string, RawKernelJson> outputs,
        IReadOnlyDictionary<string, Dictionary<string, object?>> traces)
    {
        if (Text(plan.Definition, "kind") != "inspection") return "Unknown";
        string name = Text(plan.Definition, "decisionOutput");
        var binding = plan.Definition["outputs"]![name]!;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        bool SkippedAncestor(string id)
        {
            if (!visited.Add(id)) return false;
            return traces[id]["status"] as string == "Skipped" || plan.Incoming[id].Values.Any(p => SkippedAncestor(p.Node));
        }
        if (SkippedAncestor(Text(binding, "node"))) return "Unknown";
        var decision = outputs[name].ToElement();
        if (Text(binding, "type") == "Bool") return decision.GetBoolean() ? "Pass" : "Fail";
        string status = decision.GetProperty("status").GetString()!;
        if (status is not ("Pass" or "Fail" or "Unknown")) throw new VisionException(6, "Component produced an invalid Decision.");
        return status;
    }
}
