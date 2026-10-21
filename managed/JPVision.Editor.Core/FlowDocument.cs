using System.Text.Json.Nodes;

namespace JPVision.Editor.Core;

/// <summary>UI-independent typed graph, persistent interfaces and bounded undo history.</summary>
public sealed partial class FlowDocument
{
    public NodeCatalog Catalog
    {
        get;
    }
    public JsonObject Definition
    {
        get; private set;
    }
    public Dictionary<string, EditorPoint> Positions { get; } = new();
    public Dictionary<string, RoiRegion> TemplateRegions { get; } = new();
    public JsonArray Nodes => Definition["nodes"]!.AsArray();
    public JsonArray Connections => Definition["connections"]!.AsArray();
    public event Action? Changed;
    private readonly List<(string Recipe, string Layout)> undo = new(), redo = new();
    private string baseline = "";
    public bool Dirty => Snapshot() != baseline;
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public FlowDocument(NodeCatalog catalog)
    {
        Catalog = catalog;
        Definition = Empty();
        MarkSaved();
    }
    private static JsonObject Empty() => new()
    {
        ["schemaVersion"] = 1,
        ["minimumRuntimeAbi"] = 1,
        ["recipeId"] = "jp-" + Guid.NewGuid().ToString("N"),
        ["recipeVersion"] = 1,
        ["kind"] = "processing",
        ["decisionOutput"] = "",
        ["inputs"] = new JsonObject { ["InputImage"] = new JsonObject { ["type"] = "Image", ["required"] = true } },
        ["outputs"] = new JsonObject(),
        ["nodes"] = new JsonArray(),
        ["connections"] = new JsonArray(),
        ["resources"] = new JsonArray()
    };
    public JsonObject Node(string id) => Nodes.OfType<JsonObject>().FirstOrDefault(n => Id(n) == id) ?? throw new InvalidOperationException("节点不存在：" + id);
    public NodeSpec Spec(string id) => Catalog[Node(id)["type"]!.GetValue<string>()];
    public static string Id(JsonNode n) => n["id"]!.GetValue<string>();
    private string Snapshot() => Definition.ToJsonString() + LayoutJson().ToJsonString();
    public void MarkSaved() => baseline = Snapshot();
    public JsonObject LayoutJson() => new() { ["positions"] = new JsonObject(Positions.Select(p => KeyValuePair.Create<string, JsonNode?>(p.Key, new JsonObject { ["x"] = p.Value.X, ["y"] = p.Value.Y }))), ["templateRegions"] = new JsonObject(TemplateRegions.Select(p => KeyValuePair.Create<string, JsonNode?>(p.Key, p.Value.ToJson()))) };
    private (string, string) State() => (Definition.ToJsonString(), LayoutJson().ToJsonString());
    private void Remember()
    {
        undo.Add(State());
        redo.Clear();
        while (undo.Count > 60 || undo.Sum(s => s.Recipe.Length + s.Layout.Length) > 20 * 1024 * 1024)
            undo.RemoveAt(0);
    }
    public void Change(Action action)
    {
        Remember();
        action();
        Changed?.Invoke();
    }
    public void Reset()
    {
        Definition = Empty();
        Positions.Clear();
        TemplateRegions.Clear();
        undo.Clear();
        redo.Clear();
        MarkSaved();
        Changed?.Invoke();
    }
    public void Load(string json, string? layout = null)
    {
        var value = JsonNode.Parse(json)?.AsObject() ?? throw new ArgumentException("配方内容为空。");
        if (value["schemaVersion"]?.GetValue<int>() != 1 || value["nodes"] is not JsonArray nodes || value["connections"] is not JsonArray)
            throw new ArgumentException("配方格式或版本不受支持。");
        if (nodes.Count > 1024 || value["connections"]!.AsArray().Count > 4096)
            throw new ArgumentException("流程超过节点或连接数量上限。");
        if (value["inputs"] is not JsonObject || value["outputs"] is not JsonObject || value["resources"] is not JsonArray)
            throw new ArgumentException("配方接口或资源清单无效。");
        var ids = new HashSet<string>();
        foreach (var node in nodes)
        {
            _ = Catalog[node!["type"]!.GetValue<string>()];
            if (!ids.Add(Id(node)))
                throw new ArgumentException("存在重复节点。");
        }
        var types = nodes.ToDictionary(n => Id(n!), n => Catalog[n!["type"]!.GetValue<string>()]);
        var occupied = new HashSet<(string, string)>();
        var indegrees = ids.ToDictionary(id => id, _ => 0);
        var outgoing = ids.ToDictionary(id => id, _ => new List<string>());
        foreach (var edge in value["connections"]!.AsArray())
        {
            if (edge is not JsonObject e)
                throw new ArgumentException("连接格式无效。");
            string from = Text(e, "fromNode"), to = Text(e, "toNode"), outPort = Text(e, "fromPort"), inPort = Text(e, "toPort");
            if (!types.ContainsKey(from) || !types.ContainsKey(to) || from == to)
                throw new ArgumentException("连接引用了不存在的节点或自身。");
            var a = types[from].Outputs.FirstOrDefault(p => p.Id == outPort);
            var b = types[to].Inputs.FirstOrDefault(p => p.Id == inPort);
            if (a is null || b is null || !b.Accepts(a.Type) || !occupied.Add((to, inPort)))
                throw new ArgumentException("连接的端口、类型或输入占用状态无效。");
            indegrees[to]++;
            outgoing[from].Add(to);
        }
        try { ValidateValueDefinitions(value); }
        catch (InvalidOperationException error) { throw new ArgumentException(error.Message, error); }
        var ready = new Queue<string>(ids.Where(id => indegrees[id] == 0));
        int visited = 0;
        while (ready.TryDequeue(out string? id))
        {
            visited++;
            foreach (string target in outgoing[id])
            if (--indegrees[target] == 0)
                ready.Enqueue(target);
        }
        if (visited != ids.Count)
            throw new ArgumentException("流程中存在循环。");
        foreach (var node in nodes)
        {
            if (node!["parameters"] is not JsonObject parameters)
                throw new ArgumentException("节点参数无效。");
        }
        var positions = new Dictionary<string, EditorPoint>();
        var layoutObject = string.IsNullOrWhiteSpace(layout) ? null : JsonNode.Parse(layout);
        var loaded = layoutObject?["positions"]?.AsObject();
        var regions = new Dictionary<string, RoiRegion>();
        if (layoutObject?["templateRegions"] is JsonObject templateRegions)
        foreach (var r in templateRegions)
        if (ids.Contains(r.Key))
            regions[r.Key] = RoiRegion.FromJson(r.Value);
        for (int i = 0; i < nodes.Count; i++)
        {
            string id = Id(nodes[i]!);
            var p = loaded?[id];
            double x = p?["x"]?.GetValue<double>() ?? 40 + (i % 4) * 230, y = p?["y"]?.GetValue<double>() ?? 60 + (i / 4) * 190;
            if (!double.IsFinite(x) || !double.IsFinite(y) || Math.Abs(x) > 1000000 || Math.Abs(y) > 1000000)
                throw new ArgumentException("节点位置无效。");
            positions[id] = new(x, y);
        }
        value.Remove("contentHash");
        value.Remove("previewNodes");
        value.Remove("previewReason");
        Definition = value;
        Positions.Clear();
        foreach (var p in positions)
            Positions[p.Key] = p.Value;
        TemplateRegions.Clear();
        foreach (var r in regions)
            TemplateRegions[r.Key] = r.Value;
        undo.Clear();
        redo.Clear();
        MarkSaved();
        Changed?.Invoke();
    }
    public void Undo()
    {
        if (!CanUndo)
            return;
        redo.Add(State());
        var s = undo[^1];
        undo.RemoveAt(undo.Count - 1);
        Restore(s);
    }
    public void Redo()
    {
        if (!CanRedo)
            return;
        undo.Add(State());
        var s = redo[^1];
        redo.RemoveAt(redo.Count - 1);
        Restore(s);
    }
    private void Restore((string Recipe, string Layout) s)
    {
        Definition = JsonNode.Parse(s.Recipe)!.AsObject();
        Positions.Clear();
        foreach (var p in JsonNode.Parse(s.Layout)!["positions"]!.AsObject())
            Positions[p.Key] = new(p.Value!["x"]!.GetValue<double>(), p.Value["y"]!.GetValue<double>());
        TemplateRegions.Clear();
        if (JsonNode.Parse(s.Layout)?["templateRegions"] is JsonObject regions)
        foreach (var r in regions)
            TemplateRegions[r.Key] = RoiRegion.FromJson(r.Value);
        Changed?.Invoke();
    }
    public string Add(string type, double x, double y, string? stableId = null)
    {
        var spec = Catalog[type];
        string id = stableId ?? "n" + Guid.NewGuid().ToString("N")[..12];
        if (Nodes.Any(n => Id(n!) == id))
            throw new InvalidOperationException("重复节点标识。");
        Change(() => { Nodes.Add(new JsonObject { ["id"] = id, ["type"] = type, ["version"] = spec.Version, ["parameters"] = spec.Defaults() }); Positions[id] = new(x, y); });
        return id;
    }
    public void Move(string id, EditorPoint point) => Change(() => Positions[id] = point);
    public void SetParameter(string id, string key, JsonNode? value)
        => SetParameterPath(id, key, value);
    public void SetTemplateRegion(string id, RoiRegion region) => Change(() => TemplateRegions[id] = region);
    public void Delete(string id) => Change(() =>
    {
        for (int i = Connections.Count - 1; i >= 0; i--)
        if (Text(Connections[i]!, "fromNode") == id || Text(Connections[i]!, "toNode") == id)
            Connections.RemoveAt(i);
        var node = Node(id);
        Nodes.Remove(node);
        Positions.Remove(id);
        TemplateRegions.Remove(id);
        var outputs = Definition["outputs"]!.AsObject();
        foreach (var key in outputs.Where(p => Text(p.Value!, "node") == id).Select(p => p.Key).ToArray())
            outputs.Remove(key);
        string decision = Text(Definition, "decisionOutput");
        if (!outputs.ContainsKey(decision))
        {
            Definition["kind"] = "processing";
            Definition["decisionOutput"] = "";
        }
    });
    internal static string Text(JsonNode node, string key) => node[key]?.GetValue<string>() ?? "";
    /// <summary>Returns compatible source ports, including typed fields projected from a Result output.</summary>
    public IReadOnlyList<PortSpec> GetConnectionCandidates(string from, string output, string to, string input)
    {
        var source = Spec(from);
        var a = source.Outputs.FirstOrDefault(p => p.Id == output);
        var b = Spec(to).Inputs.FirstOrDefault(p => p.Id == input);
        if (a is null || b is null)
            return Array.Empty<PortSpec>();
        if (b.Accepts(a.Type))
            return new[] { a };
        if (a.Type is not ("Result" or "Image"))
            return Array.Empty<PortSpec>();
        if (a.Type == "Result")
        {
            string expected = b.ValueType ?? b.Type;
            var preferred = source.Outputs.Where(p => p.Id != a.Id && p.Type == expected).ToArray();
            if (preferred.Length > 0) return preferred;
            return source.Outputs.Where(p => p.Id != a.Id && b.Accepts(p.Type)).ToArray();
        }
        return source.Outputs.Where(p => p.Hidden && b.Accepts(p.Type)).ToArray();
    }
    public void Connect(string from, string output, string to, string input)
    {
        var candidates = GetConnectionCandidates(from, output, to, input);
        if (candidates.Count == 0)
            throw new InvalidOperationException("端口类型不同，无法连接。");
        if (candidates.Count > 1)
            throw new InvalidOperationException("结果包含多个匹配字段（" + string.Join("、", candidates.Select(p => p.Label)) + "），请选择字段后连接。");
        string sourcePort = candidates[0].Id;
        if (Connections.Any(e => Text(e!, "toNode") == to && Text(e!, "toPort") == input))
            throw new InvalidOperationException("输入端口已连接。右键输入端口可断开。");
        if (Ancestors(from).Contains(to))
            throw new InvalidOperationException("连接会产生循环。");
        Change(() => { ClearAlternativeInputs(to, input); Connections.Add(new JsonObject { ["fromNode"] = from, ["fromPort"] = sourcePort, ["toNode"] = to, ["toPort"] = input }); });
    }
    private void ClearAlternativeInputs(string to, string input)
    {
        var groups = Spec(to).InputGroups.Where(g => g.Contains(input)).ToArray();
        if (groups.Length == 0) return;
        var allowed = groups.SelectMany(g => g).ToHashSet();
        for (int i = Connections.Count - 1; i >= 0; i--)
            if (Text(Connections[i]!, "toNode") == to && !allowed.Contains(Text(Connections[i]!, "toPort"))) Connections.RemoveAt(i);
    }
    public void Disconnect(string to, string input) => Change(() => { for (int i = Connections.Count - 1; i >= 0; i--) if (Text(Connections[i]!, "toNode") == to && Text(Connections[i]!, "toPort") == input) Connections.RemoveAt(i); });
    public string PublicInterfaceIssue()
    {
        foreach (var output in Definition["outputs"]!.AsObject())
        {
            var binding = output.Value!;
            string id = Text(binding, "node");
            if (!Nodes.Any(n => Id(n!) == id))
                return "公开输出引用已删除的节点：" + output.Key;
            if (!Spec(id).Outputs.Any(p => p.Id == Text(binding, "port") && p.Type == Text(binding, "type")))
                return "公开输出端口或类型不匹配：" + output.Key;
        }
        if (Text(Definition, "kind") == "inspection" && Definition["outputs"]![Text(Definition, "decisionOutput")]?["type"]?.GetValue<string>() is not ("Bool" or "Decision"))
            return "检测判定未绑定有效的 Bool 或 Decision 输出。";
        return "";
    }
    public string[] Terminals()
    {
        var used = Nodes.SelectMany(n => Dependencies(Id(n!))).ToHashSet();
        return Nodes.Select(n => Id(n!)).Where(id => !used.Contains(id)).ToArray();
    }
    public JsonObject PublishedRecipe()
    {
        if (Nodes.Count == 0)
            throw new InvalidOperationException("请先添加并连接算子。");
        var clone = Definition.DeepClone().AsObject();
        clone.Remove("contentHash");
        clone.Remove("previewNodes");
        clone.Remove("previewReason");
        if (clone["outputs"]!.AsObject().Count == 0 || PublicInterfaceIssue().Length > 0)
        {
            clone["outputs"] = new JsonObject();
            clone["kind"] = "processing";
            clone["decisionOutput"] = "";
            foreach (string id in Terminals())
            foreach (var port in AutomaticOutputs(Spec(id)))
                clone["outputs"]![id + "_" + port.Id] = new JsonObject { ["node"] = id, ["port"] = port.Id, ["type"] = port.Type };
        }
        return clone;
    }
    private static IEnumerable<PortSpec> AutomaticOutputs(NodeSpec spec) => spec.VisibleOutputs.Concat(
        spec.ImagePipeline ? spec.Outputs.Where(p => p.Hidden && p.Type is "Result" or "Measurement" or "Decision") : Array.Empty<PortSpec>());
    public JsonObject ExecutionRecipe()
    {
        var clone = PublishedRecipe();
        var covered = new HashSet<string>();
        var pending = new Stack<string>(clone["outputs"]!.AsObject().Select(p=>Text(p.Value!,"node")));
        while(pending.TryPop(out string? id)) {
            if(!covered.Add(id)) continue;
            foreach(var source in Dependencies(id)) pending.Push(source);
        }
        var targets=Terminals().Where(id=>!covered.Contains(id)).ToArray();
        if(targets.Length>0) clone["previewNodes"] = new JsonArray(targets.Select(id => JsonValue.Create(id)).ToArray());
        return clone;
    }
    public JsonObject InputRecipe(string nodeId)
    {
        var incoming = Connections.FirstOrDefault(e => Text(e!, "toNode") == nodeId && Spec(Text(e!, "fromNode")).Outputs.Any(p => p.Id == Text(e!, "fromPort") && p.Type is "Image" or "Mask"));
        if (incoming is null)
            throw new InvalidOperationException("请先将图像输入端口连接到上游算子。");
        return BindingRecipe(nodeId, Text(incoming, "toPort"));
    }
    public JsonObject BindingRecipe(string nodeId, string inputPort)
    {
        var incoming = Connections.FirstOrDefault(e => Text(e!, "toNode") == nodeId && Text(e!, "toPort") == inputPort)
            ?? throw new InvalidOperationException("请连接输入端口：" + inputPort);
        string source = Text(incoming, "fromNode"), port = Text(incoming, "fromPort");
        var ancestors = new HashSet<string>();
        void Visit(string id)
        {
            if (!ancestors.Add(id))
                return;
            foreach (var upstream in Dependencies(id))
                Visit(upstream);
        }
        Visit(source);
        var subscriptionSources = ParameterDependencies(nodeId).ToArray();
        foreach (string dependency in subscriptionSources) Visit(dependency);
        var clone = Definition.DeepClone().AsObject();
        clone.Remove("contentHash");
        clone.Remove("previewNodes");
        clone["nodes"] = new JsonArray(Nodes.Where(n => ancestors.Contains(Id(n!))).Select(n => n!.DeepClone()).ToArray());
        clone["connections"] = new JsonArray(Connections.Where(e => ancestors.Contains(Text(e!, "toNode"))).Select(e => e!.DeepClone()).ToArray());
        clone["outputs"] = new JsonObject { ["InputPreview"] = new JsonObject { ["node"] = source, ["port"] = port, ["type"] = Spec(source).Outputs.First(p => p.Id == port).Type } };
        clone["kind"] = "processing";
        clone["decisionOutput"] = "";
        if (subscriptionSources.Length > 0)
            clone["previewNodes"] = new JsonArray(subscriptionSources.Select(id => JsonValue.Create(id)).ToArray());
        return clone;
    }
    public void Publish(string name, string id, string port, bool decision)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128)
            throw new InvalidOperationException("请输入 1～128 字符的输出名称。");
        var spec = Spec(id).Outputs.First(p => p.Id == port);
        if (decision && spec.Type is not ("Bool" or "Decision"))
            throw new InvalidOperationException("检测判定必须使用 Bool 或 Decision 端口。");
        Change(() =>
        {
            Definition["outputs"]![name] = new JsonObject { ["node"] = id, ["port"] = port, ["type"] = spec.Type };
            if (decision)
            {
                Definition["kind"] = "inspection";
                Definition["decisionOutput"] = name;
            }
        });
    }
}
