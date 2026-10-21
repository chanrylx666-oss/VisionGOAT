using System.Text.Json;
using System.Text.Json.Nodes;

namespace JPVision.Editor.Core;

public sealed record PortSpec(string Id, string Type, string Label, bool Hidden = false,
    IReadOnlyList<string>? AcceptedTypes = null, string? ValueType = null)
{
    public bool Accepts(string type) => Type == type || AcceptedTypes?.Contains(type) == true;
}
public sealed record ResultFieldSpec(string Port, string Field, string Type, string Label, bool Nullable);
public sealed class NodeSpec
{
    public string Type
    {
        get;
    }
    public string Label
    {
        get;
    }
    public string Category
    {
        get;
    }
    public int Version
    {
        get;
    }
    public IReadOnlyList<PortSpec> Inputs
    {
        get;
    }
    public IReadOnlyList<PortSpec> Outputs
    {
        get;
    }
    public IReadOnlyList<PortSpec> VisibleOutputs
    {
        get;
    }
    public IReadOnlyList<PortSpec> VisibleInputs { get; }
    public bool ImagePipeline { get; }
    public IReadOnlyList<IReadOnlyList<string>> InputGroups { get; }
    public bool ToolboxVisible
    {
        get;
    }
    public JsonArray Parameters
    {
        get;
    }
    public IReadOnlyList<ResultFieldSpec> ScalarFields { get; }
    public NodeSpec(JsonElement value)
    {
        Type = value.GetProperty("type").GetString()!;
        Label = value.GetProperty("label").GetString()!;
        Category = value.GetProperty("category").GetString()!;
        Version = value.GetProperty("version").GetInt32();
        ToolboxVisible = !value.TryGetProperty("toolboxVisible", out var toolboxVisible) || toolboxVisible.GetBoolean();
        ImagePipeline = value.TryGetProperty("imagePipeline", out var pipeline) && pipeline.GetBoolean();
        InputGroups = value.TryGetProperty("inputGroups", out var inputGroups)
            ? inputGroups.EnumerateArray().Select(g => (IReadOnlyList<string>)g.EnumerateArray().Select(p => p.GetString()!).ToArray()).ToArray()
            : Array.Empty<IReadOnlyList<string>>();
        IReadOnlyList<PortSpec> Ports(string key) => value.GetProperty(key).EnumerateArray().Select(p =>
            new PortSpec(p.GetProperty("id").GetString()!, p.GetProperty("type").GetString()!, p.GetProperty("label").GetString()!,
                p.TryGetProperty("hidden", out var hidden) && hidden.GetBoolean(),
                p.TryGetProperty("acceptedTypes", out var accepted) ? accepted.EnumerateArray().Select(t => t.GetString()!).ToArray() : null,
                p.TryGetProperty("valueType", out var legacy) ? legacy.GetString() : null)).ToArray();
        Inputs = Ports("inputs");
        Outputs = Ports("outputs");
        VisibleOutputs = Outputs.Where(p => !p.Hidden).ToArray();
        VisibleInputs = Inputs.Where(p => !p.Hidden).ToArray();
        Parameters = JsonNode.Parse(value.GetProperty("parameters").GetRawText())!.AsArray();
        var fields = Outputs.Where(p => p.Type is "Int" or "Double" or "Bool" or "String")
            .Select(p => new ResultFieldSpec(p.Id, "", p.Type, p.Label, false)).ToList();
        if (value.TryGetProperty("fields", out var definitions))
            foreach (var port in definitions.EnumerateObject())
                foreach (var field in port.Value.EnumerateObject())
                    fields.Add(new(port.Name, field.Name, field.Value.GetProperty("type").GetString()!,
                        field.Value.GetProperty("label").GetString()!, field.Value.GetProperty("nullable").GetBoolean()));
        ScalarFields = fields;
    }
    public JsonObject Defaults() => new(Parameters.Select(p => KeyValuePair.Create(p!["name"]!.GetValue<string>(), (p["editorDefault"] ?? p["default"])?.DeepClone())));
    public override string ToString() => Label;
}
public sealed class NodeCatalog
{
    private readonly Dictionary<string, NodeSpec> types;
    public NodeCatalog(JsonElement values) => types = values.EnumerateArray().Select(v => new NodeSpec(v)).ToDictionary(n => n.Type);
    public IEnumerable<NodeSpec> All => types.Values;
    public NodeSpec this[string type] => types.TryGetValue(type, out var node) ? node : throw new InvalidOperationException("未知算子：" + type);
}
public readonly record struct EditorPoint(double X, double Y);
