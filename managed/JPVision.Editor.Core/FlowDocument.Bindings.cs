using System.Text.Json.Nodes;

namespace JPVision.Editor.Core;

public sealed record ParameterSource(string Node, ResultFieldSpec Field, string Label);

public sealed partial class FlowDocument
{
    private static bool IsFieldBinding(JsonNode? value) => value is JsonObject o && o["node"] is JsonValue && o["field"] is JsonValue;
    public IEnumerable<string> Dependencies(string id) => Connections.Where(e => Text(e!, "toNode") == id).Select(e => Text(e!, "fromNode"))
        .Concat(ParameterDependencies(id)).Distinct();
    private HashSet<string> Ancestors(string target)
    {
        var result = new HashSet<string>(); var pending = new Stack<string>(); pending.Push(target);
        while (pending.TryPop(out string? id))
        {
            if (!result.Add(id)) continue;
            if (!Nodes.Any(n => Id(n!) == id)) throw new InvalidOperationException("参数绑定引用已删除的节点：" + id);
            foreach (string dependency in Dependencies(id)) pending.Push(dependency);
        }
        return result;
    }
    private static bool Compatible(string targetType, string sourceType) => targetType switch
    {
        "integer" => sourceType == "Int", "number" => sourceType is "Int" or "Double", "bool" => sourceType == "Bool",
        "string" or "enum" => sourceType == "String", _ => false
    };
    private void ValidateFieldBinding(JsonObject recipe, string id, string key, JsonObject binding, JsonObject? parameter = null)
    {
        var nodes = recipe["nodes"]!.AsArray();
        parameter ??= ParameterDefinition(recipe, id, key);
        if (binding.Count is < 3 or > 4 || binding.Any(p => p.Key is not ("node" or "port" or "field" or "fallback")) || binding["node"] is not JsonValue || binding["port"] is not JsonValue || binding["field"] is not JsonValue || parameter["bindable"]?.GetValue<bool>() != true)
            throw new InvalidOperationException("参数不支持此绑定：" + key);
        string source = Text(binding, "node"), port = Text(binding, "port"), field = Text(binding, "field");
        var sourceNode = nodes.FirstOrDefault(n => Id(n!) == source);
        if (sourceNode is null || source == id) throw new InvalidOperationException("参数绑定引用不存在的节点或自身。");
        var spec = Catalog[Text(sourceNode, "type")].ScalarFields.FirstOrDefault(f => f.Port == port && f.Field == field);
        if (spec is null || !Compatible(Text(parameter!, "type"), spec.Type)) throw new InvalidOperationException("绑定字段不存在或类型不匹配：" + field);
    }
    public IReadOnlyList<ParameterSource> ParameterSources(string id, string key)
    {
        var p = GetParameterDefinition(id, key);
        if (p["bindable"]?.GetValue<bool>() != true) return Array.Empty<ParameterSource>();
        return Nodes.Where(n => Id(n!) != id && !DependsOn(Id(n!), id))
            .SelectMany(n => Spec(Id(n!)).ScalarFields.Where(f => Compatible(Text(p, "type"), f.Type))
                .Select(f => new ParameterSource(Id(n!), f, $"{Spec(Id(n!)).Label} [{Id(n!)}] · {f.Label} ({f.Port}.{f.Field})"))).ToArray();
    }
    private bool DependsOn(string source, string target)
    {
        var visited = new HashSet<string>(); var pending = new Stack<string>(); pending.Push(source);
        while (pending.TryPop(out string? id))
        {
            if (id == target) return true;
            if (!visited.Add(id) || !Nodes.Any(n => Id(n!) == id)) continue;
            foreach (string dependency in Dependencies(id)) pending.Push(dependency);
        }
        return false;
    }
    public void ReplaceInput(string from, string output, string to, string input)
    {
        var candidates = GetConnectionCandidates(from, output, to, input);
        if (candidates.Count != 1 || DependsOn(from, to)) throw new InvalidOperationException("来源类型不匹配、含糊或会产生循环。");
        Change(() =>
        {
            ClearAlternativeInputs(to, input);
            for (int i = Connections.Count - 1; i >= 0; i--)
                if (Text(Connections[i]!, "toNode") == to && Text(Connections[i]!, "toPort") == input) Connections.RemoveAt(i);
            Connections.Add(new JsonObject { ["fromNode"] = from, ["fromPort"] = candidates[0].Id, ["toNode"] = to, ["toPort"] = input });
        });
    }
    public JsonObject RecipeToNode(string id)
    {
        var ancestors = Ancestors(id); var clone = Definition.DeepClone().AsObject();
        clone.Remove("contentHash"); clone.Remove("previewNodes"); clone.Remove("previewReason");
        clone["nodes"] = new JsonArray(Nodes.Where(n => ancestors.Contains(Id(n!))).Select(n => n!.DeepClone()).ToArray());
        clone["connections"] = new JsonArray(Connections.Where(e => ancestors.Contains(Text(e!, "toNode"))).Select(e => e!.DeepClone()).ToArray());
        clone["outputs"] = new JsonObject(AutomaticOutputs(Spec(id)).Select(p => KeyValuePair.Create<string, JsonNode?>("Selected_" + p.Id,
            new JsonObject { ["node"] = id, ["port"] = p.Id, ["type"] = p.Type })));
        clone["kind"] = "processing"; clone["decisionOutput"] = "";
        return clone;
    }
    public IReadOnlyList<string> DuplicateNodes(IEnumerable<string> selected)
    {
        var ids = selected.Distinct().ToArray();
        foreach (string id in ids) _ = Node(id);
        var map = ids.ToDictionary(id => id, _ => "n" + Guid.NewGuid().ToString("N")[..12]);
        var nodes = ids.Select(id => Node(id).DeepClone().AsObject()).ToArray();
        var edges = Connections.Where(e => map.ContainsKey(Text(e!, "toNode"))).Select(e => e!.DeepClone().AsObject()).ToArray();
        if (Nodes.Count + nodes.Length > 1024 || Connections.Count + edges.Length > 4096) throw new InvalidOperationException("复制后超过流程数量上限。");
        foreach (var node in nodes)
        {
            node["id"] = map[Id(node)];
            foreach (var binding in FieldBindings(node["parameters"]).Concat(FieldBindings(node["variables"])))
                if (map.TryGetValue(Text(binding, "node"), out string? remapped)) binding["node"] = remapped;
        }
        foreach (var edge in edges)
        {
            edge["toNode"] = map[Text(edge, "toNode")];
            if (map.TryGetValue(Text(edge, "fromNode"), out string? remapped)) edge["fromNode"] = remapped;
        }
        Change(() =>
        {
            foreach (var node in nodes) Nodes.Add(node);
            foreach (var edge in edges) Connections.Add(edge);
            foreach (string id in ids)
            {
                Positions[map[id]] = new(Positions[id].X + 45, Positions[id].Y + 45);
                if (TemplateRegions.TryGetValue(id, out var region)) TemplateRegions[map[id]] = RoiRegion.FromJson(region.ToJson());
            }
        });
        return ids.Select(id => map[id]).ToArray();
    }
}
