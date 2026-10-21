using System.Text.Json;

namespace JPVision.Sdk;

public sealed partial class InspectionResult
{
    /// <summary>The accepted global value for this run, including any RunRequest override.</summary>
    public T GetGlobalVariable<T>(string name) => Data.GetProperty("variables").GetProperty(name).Deserialize<T>()!;

    /// <summary>A local value evaluated by an executed node. Missing upstream data is unavailable, never zero.</summary>
    public T GetLocalVariable<T>(string nodeId, string name)
    {
        var value = Data.GetProperty("nodes").GetProperty(nodeId).GetProperty("localVariables").GetProperty(name);
        if (value.ValueKind == JsonValueKind.Null)
            throw new InvalidOperationException("Local variable is unavailable in this execution: " + nodeId + "." + name);
        return value.Deserialize<T>()!;
    }

    /// <summary>Resolved ROI and subscribed scalar values. Image-reference ROI positions are input-local;
    /// reference-coordinate ROI values retain their explicitly declared fixture reference.
    /// Returns null when this execution could not resolve the node's parameters.</summary>
    public JsonElement? GetResolvedParameters(string nodeId)
    {
        var node = Data.GetProperty("nodes").GetProperty(nodeId);
        return node.TryGetProperty("resolvedParameters", out var parameters) && parameters.ValueKind == JsonValueKind.Object
            ? parameters : null;
    }
}
