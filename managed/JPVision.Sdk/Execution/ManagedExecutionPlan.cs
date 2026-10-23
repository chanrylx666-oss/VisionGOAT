using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json.Nodes;

namespace JPVision.Sdk.Execution;

/// <summary>The business execution graph. Native code owns image buffers and operator kernels.</summary>
internal sealed class ManagedExecutionPlan
{
    internal static StringComparer Comparer => Utf8OrdinalComparer.Instance;
    public JsonObject Definition { get; }
    public SortedDictionary<string, JsonObject> Nodes { get; } = new(Comparer);
    public IReadOnlyList<string> Order { get; private set; } = Array.Empty<string>();
    public SortedDictionary<string, SortedDictionary<string, (string Node, string Port)>> Incoming { get; } = new(Comparer);
    public SortedDictionary<string, SortedDictionary<string, (string Node, string Port)>> ParameterIncoming { get; } = new(Comparer);
    public SortedDictionary<string, int> Uses { get; } = new(Comparer);
    public SortedDictionary<string, JsonObject> Catalog { get; } = new(Comparer);

    private ManagedExecutionPlan(JsonObject definition) => Definition = definition.DeepClone().AsObject();

    public static ManagedExecutionPlan Create(JsonObject definition, JsonArray catalog)
    {
        var plan = new ManagedExecutionPlan(definition);
        foreach (var item in catalog)
        {
            var descriptor = Object(item, "Invalid component descriptor.");
            plan.Catalog.Add(Text(descriptor, "type"), descriptor.DeepClone().AsObject());
        }
        var recipe = plan.Definition;
        var nodes = recipe["nodes"] as JsonArray ?? throw Invalid("Recipe graph is missing.");
        var connections = recipe["connections"] as JsonArray ?? throw Invalid("Recipe graph is missing.");
        Require(nodes.Count <= 1024 && connections.Count <= 4096, "Graph size limit exceeded.", 5);
        Require(recipe["inputs"] is JsonObject inputs && inputs.Count <= 64 &&
            recipe["outputs"] is JsonObject outputs && outputs.Count is > 0 and <= 64,
            "Public interfaces are invalid.");
        foreach (var item in nodes)
        {
            var node = Object(item, "Invalid node.");
            string id = Text(node, "id"), type = Text(node, "type");
            Require(id.Length > 0 && Encoding.UTF8.GetByteCount(id) <= 128 && !plan.Nodes.ContainsKey(id), "Invalid or duplicate node ID.");
            Require(plan.Catalog.TryGetValue(type, out var descriptor), "Unknown component: " + type);
            Require(JsonNode.DeepEquals(node["version"], descriptor!["version"]), "Unsupported node version.");
            plan.Nodes.Add(id, node);
            plan.Incoming[id] = new(Comparer);
            plan.ParameterIncoming[id] = ManagedBindings.Dependencies(node);
            plan.Uses[id] = 0;
        }
        int bindingCount = connections.Count + plan.ParameterIncoming.Values.Sum(v => v.Count);
        Require(bindingCount <= 4096, "Graph binding limit exceeded.", 5);
        foreach (var dependencies in plan.ParameterIncoming.Values)
            foreach (var source in dependencies.Values)
                Require(plan.Nodes.ContainsKey(source.Node), "Parameter binding refers to missing/self node.");
        foreach (var item in connections)
        {
            var edge = Object(item, "Invalid connection.");
            string from = Text(edge, "fromNode"), to = Text(edge, "toNode"),
                output = Text(edge, "fromPort"), input = Text(edge, "toPort");
            Require(plan.Nodes.ContainsKey(from) && plan.Nodes.ContainsKey(to) && from != to, "Connection endpoint is invalid.");
            var source = FindPort(plan.Descriptor(from), "outputs", output);
            var target = FindPort(plan.Descriptor(to), "inputs", input);
            string type = Text(source, "type");
            Require(Text(target, "type") == type || target["acceptedTypes"] is JsonArray accepted &&
                accepted.Any(t => t?.GetValue<string>() == type), "Connection data types do not match.");
            Require(!plan.Incoming[to].ContainsKey(input), "Input port already has a connection.");
            plan.Incoming[to].Add(input, (from, output));
        }
        var states = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = new List<string>();
        void Visit(string id, bool record)
        {
            states.TryGetValue(id, out int state);
            Require(state != 1, "Graph contains a cycle.");
            if (state == 2) return;
            states[id] = 1;
            foreach (var source in plan.Incoming[id].Values) Visit(source.Node, record);
            foreach (var source in plan.ParameterIncoming[id].Values) Visit(source.Node, record);
            states[id] = 2;
            if (record) order.Add(id);
        }
        // Disconnected graphs must not hide cycles, even if they do not contribute an output.
        foreach (string id in plan.Nodes.Keys) Visit(id, false);
        states.Clear();
        foreach (var item in ((JsonObject)recipe["outputs"]!).OrderBy(p => p.Key, Comparer))
        {
            var output = Object(item.Value, "Invalid public output.");
            string id = Text(output, "node");
            Require(plan.Nodes.ContainsKey(id), "Output refers to missing node.");
            var port = FindPort(plan.Descriptor(id), "outputs", Text(output, "port"));
            Require(Text(port, "type") == Text(output, "type"), "Public output type mismatch.");
            ++plan.Uses[id];
            Visit(id, true);
        }
        string kind = Text(recipe, "kind"), decision = Text(recipe, "decisionOutput");
        if (kind == "inspection")
        {
            var decisionPort = recipe["outputs"]?[decision] as JsonObject;
            Require(decisionPort is not null && Text(decisionPort, "type") is "Bool" or "Decision",
                "Inspection decision must bind a Bool or Decision output.");
        }
        else
            Require(kind == "processing" && decision.Length == 0, "Processing recipes must not declare an inspection decision.");
        var targets = recipe["previewNodes"] as JsonArray ?? new JsonArray();
        Require((!recipe.ContainsKey("previewNodes") || recipe["previewNodes"] is JsonArray) && targets.Count <= 1024,
            "Invalid preview node list.");
        foreach (var target in targets)
        {
            string id = target is JsonValue value && value.TryGetValue<string>(out var name) ? name : "";
            Require(plan.Nodes.ContainsKey(id), "Preview refers to missing node.");
            Visit(id, true);
        }
        foreach (string id in order)
        {
            var descriptor = plan.Descriptor(id);
            if (descriptor["inputGroups"] is JsonArray groups)
            {
                Require(groups.OfType<JsonArray>().Any(g => g.Count == plan.Incoming[id].Count &&
                    g.All(p => plan.Incoming[id].ContainsKey(p!.GetValue<string>()))),
                    "Connect either the image input or the complete legacy object inputs on node: " + id);
            }
            else
                foreach (var port in (JsonArray)descriptor["inputs"]!)
                    Require(plan.Incoming[id].ContainsKey(Text(port!, "id")), "Required input not connected on node: " + id);
            foreach (var source in plan.Incoming[id].Values) ++plan.Uses[source.Node];
            foreach (var source in plan.ParameterIncoming[id].Values) ++plan.Uses[source.Node];
        }
        plan.Order = order.AsReadOnly();
        return plan;
    }

    public JsonObject Descriptor(string nodeId) => Catalog[Text(Nodes[nodeId], "type")];

    internal static JsonObject FindPort(JsonObject descriptor, string direction, string id) =>
        (descriptor[direction] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(p => Text(p, "id") == id)
        ?? throw Invalid("Unknown " + direction + " port: " + id);

    internal static JsonObject Object(JsonNode? node, string message) => node as JsonObject ?? throw Invalid(message);
    internal static string Text(JsonNode node, string key, string fallback = "") =>
        node[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : fallback;
    internal static VisionException Invalid(string message, int code = 2) => new(code, message);
    internal static void Require([DoesNotReturnIf(false)] bool condition, string message, int code = 2)
    {
        if (condition) return;
        if (code == 1) throw new ArgumentException(message);
        throw Invalid(message, code);
    }
}
