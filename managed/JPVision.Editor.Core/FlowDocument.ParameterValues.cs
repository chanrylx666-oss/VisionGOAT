using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Globalization;

namespace JPVision.Editor.Core;

public sealed partial class FlowDocument
{
    private static bool IsValueBinding(JsonNode? value) => value is JsonObject o &&
        (o.ContainsKey("node") || o.ContainsKey("input") || o.ContainsKey("variable") || o.ContainsKey("expression"));

    private static IEnumerable<(string Path, JsonObject Binding)> ValueBindings(JsonNode? value, string path = "")
    {
        if (IsValueBinding(value)) { yield return (path, value!.AsObject()); yield break; }
        if (value is JsonObject o)
            foreach (var item in o)
                foreach (var binding in ValueBindings(item.Value, path.Length == 0 ? item.Key : path + "." + item.Key)) yield return binding;
        if (value is JsonArray a)
            for (int i = 0; i < a.Count; i++)
                foreach (var binding in ValueBindings(a[i], path + "." + i)) yield return binding;
    }

    private static IEnumerable<JsonObject> FieldBindings(JsonNode? value)
    {
        if (value is JsonObject o)
        {
            if (IsFieldBinding(o)) yield return o;
            foreach (var item in o)
                foreach (var binding in FieldBindings(item.Value)) yield return binding;
        }
        else if (value is JsonArray a)
            foreach (var item in a)
                foreach (var binding in FieldBindings(item)) yield return binding;
    }

    private IEnumerable<string> ParameterDependencies(string id) =>
        FieldBindings(Node(id)["parameters"]).Concat(FieldBindings(Node(id)["variables"]))
            .Select(binding => Text(binding, "node")).Distinct();

    private static JsonNode? AtPath(JsonNode? root, IEnumerable<string> parts)
    {
        foreach (string part in parts)
        {
            if (root is JsonObject o) root = o[part];
            else if (root is JsonArray a && int.TryParse(part, out int index) && index >= 0 && index < a.Count) root = a[index];
            else return null;
        }
        return root;
    }

    public JsonNode? GetParameterPath(string id, string path) => AtPath(Node(id)["parameters"], ParameterParts(path));

    private static string[] ParameterParts(string path)
    {
        var parts = path.Split('.');
        if (parts.Length == 0 || parts.Length > 5 || parts.Any(p => p.Length == 0)) throw new InvalidOperationException("参数路径无效：" + path);
        return parts;
    }

    public JsonObject GetParameterDefinition(string id, string path) => ParameterDefinition(Definition, id, path);

    private JsonObject ParameterDefinition(JsonObject recipe, string id, string path)
    {
        string[] parts = ParameterParts(path);
        var node = recipe["nodes"]!.AsArray().First(n => Id(n!) == id)!;
        var definition = Catalog[Text(node, "type")].Parameters.FirstOrDefault(p => Text(p!, "name") == parts[0])?.AsObject()
            ?? throw new InvalidOperationException("未知参数：" + path);
        if (parts.Length == 1) return definition.DeepClone().AsObject();
        if (Text(definition, "type") != "roi") throw new InvalidOperationException("只有 ROI 数字字段支持嵌套参数路径：" + path);
        var roi = node["parameters"]?[parts[0]] ?? definition["default"];
        string shape = roi?["shape"]?.GetValue<string>() ?? "none", field = parts[1];
        string[] allowed = shape switch
        {
            "rectangle" => new[] { "x", "y", "width", "height" },
            "circle" => new[] { "cx", "cy", "radius" },
            "annulus" => new[] { "cx", "cy", "innerRadius", "outerRadius", "startAngle", "endAngle" },
            "rotatedRectangle" => new[] { "cx", "cy", "width", "height", "angleDegrees" },
            "polygon" => new[] { "points" }, _ => Array.Empty<string>()
        };
        bool polygon = field == "points" && parts.Length == 4 && roi?["points"] is JsonArray points &&
            int.TryParse(parts[2], out int index) && index >= 0 && index < points.Count && parts[3] is "0" or "1";
        if (!allowed.Contains(field) || !(parts.Length == 2 && field != "points" || polygon))
            throw new InvalidOperationException("未知 ROI 数字字段：" + path);
        var current = AtPath(node["parameters"], parts);
        var fallback = IsValueBinding(current) ? current?["fallback"] : current;
        bool coordinate = field is "x" or "y" or "cx" or "cy" || polygon;
        bool angle = field is "startAngle" or "endAngle" or "angleDegrees";
        bool radius = field is "radius" or "innerRadius" or "outerRadius";
        bool reference = roi?["coordinateSpace"]?.GetValue<string>() == "reference";
        double minimum = angle ? -360 : coordinate && reference ? -65536 : 0;
        double maximum = angle ? 720 : radius ? 16384 : coordinate && reference ? 65536 : 32768;
        return new JsonObject
        {
            ["name"] = path, ["label"] = path, ["type"] = "number", ["bindable"] = true,
            ["min"] = minimum, ["max"] = maximum,
            ["default"] = fallback?.DeepClone() ?? JsonValue.Create(RoiPreviewDefault(parts.Skip(1).ToArray()))
        };
    }

    public void SetParameterPath(string id, string path, JsonNode? value)
    {
        _ = GetParameterDefinition(id, path);
        string[] parts = ParameterParts(path);
        var candidate = Definition.DeepClone().AsObject();
        var parameters = candidate["nodes"]!.AsArray().First(n => Id(n!) == id)!["parameters"]!;
        JsonNode parent = AtPath(parameters, parts.Take(parts.Length - 1)) ?? throw new InvalidOperationException("参数路径不存在：" + path);
        void Assign(JsonNode target)
        {
            if (target is JsonObject o) o[parts[^1]] = value?.DeepClone();
            else if (target is JsonArray a && int.TryParse(parts[^1], out int index) && index >= 0 && index < a.Count) a[index] = value?.DeepClone();
            else throw new InvalidOperationException("参数路径不存在：" + path);
        }
        Assign(parent);
        ValidateValueDefinitions(candidate);
        Change(() => Assign(AtPath(Node(id)["parameters"], parts.Take(parts.Length - 1))!));
    }

    /// <summary>A drawing-only snapshot. Fallbacks never replace subscriptions in the saved recipe or during execution.</summary>
    public RoiRegion ResolvePreviewRoi(string id, string roiKey = "roi") => PreviewRoi(Definition, id, roiKey);

    private RoiRegion PreviewRoi(JsonObject recipe, string id, string roiKey)
    {
        var node = recipe["nodes"]!.AsArray().First(n => Id(n!) == id)!;
        var definition = Catalog[Text(node, "type")].Parameters.FirstOrDefault(p => Text(p!, "name") == roiKey);
        var value = (node["parameters"]?[roiKey] ?? definition?["default"])?.DeepClone();
        if (value is not JsonObject roi) return RoiRegion.None;
        foreach (var item in ValueBindings(roi).ToArray())
        {
            string[] parts = item.Path.Split('.');
            double fallback = RoiPreviewDefault(parts);
            JsonNode numeric = item.Binding["fallback"]?.DeepClone() ?? JsonValue.Create(fallback)!;
            JsonNode parent = AtPath(roi, parts.Take(parts.Length - 1))!;
            if (parent is JsonObject o) o[parts[^1]] = numeric;
            else ((JsonArray)parent)[int.Parse(parts[^1])] = numeric;
        }
        return RoiRegion.FromJson(roi);
    }

    private static double RoiPreviewDefault(string[] parts)
    {
        if (parts[0] == "points" && parts.Length == 3)
            return parts[1] == "1" || parts[1] == "2" && parts[2] == "1" ? 100 : 10;
        return parts[^1] switch
        {
            "x" or "y" or "cx" or "cy" => 160, "outerRadius" => 100, "radius" => 60,
            "width" or "height" => 50, "endAngle" => 360, _ => 0
        };
    }

    public void SetGlobalVariables(JsonObject variables) => SetValueDefinitions(null, "variables", variables);
    public void SetLocalVariables(string id, JsonObject variables) => SetValueDefinitions(id, "variables", variables);
    public void SetFunctions(JsonObject functions) => SetValueDefinitions(null, "functions", functions);
    public void SetVariableDefinitions(JsonObject globals, JsonObject functions, string? nodeId = null, JsonObject? locals = null)
    {
        void Assign(JsonObject recipe)
        {
            recipe["variables"] = globals.DeepClone();
            recipe["functions"] = functions.DeepClone();
            if (nodeId is not null && locals is not null)
                recipe["nodes"]!.AsArray().First(n => Id(n!) == nodeId)!["variables"] = locals.DeepClone();
        }
        var candidate = Definition.DeepClone().AsObject();
        Assign(candidate);
        ValidateValueDefinitions(candidate);
        Change(() => Assign(Definition));
    }
    private void SetValueDefinitions(string? id, string key, JsonObject value)
    {
        var candidate = Definition.DeepClone().AsObject();
        JsonObject target = id is null ? candidate : candidate["nodes"]!.AsArray().First(n => Id(n!) == id)!.AsObject();
        target[key] = value.DeepClone();
        ValidateValueDefinitions(candidate);
        Change(() => (id is null ? Definition : Node(id))[key] = value.DeepClone());
    }

    private static bool ValidIdentifier(string name) => Regex.IsMatch(name, @"\A[A-Za-z_][A-Za-z0-9_]{0,63}\z", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> ExpressionReserved = new(StringComparer.Ordinal)
    { "global", "local", "pi", "e", "true", "false", "abs", "min", "max", "sqrt", "pow", "round", "floor", "ceil", "sin", "cos", "tan", "atan2", "hypot", "clamp" };
    private static string ParameterType(string variableType) => variableType switch
    { "Double" => "number", "Int" => "integer", "Bool" => "bool", "String" => "string", _ => throw new InvalidOperationException("变量类型须为 Double、Int、Bool 或 String。") };

    private void ValidateValueDefinitions(JsonObject recipe)
    {
        var nodes = recipe["nodes"]!.AsArray();
        var globals = VariableDefinitions(recipe["variables"], 64);
        var functions = recipe["functions"] as JsonObject;
        if (recipe.ContainsKey("functions") && functions is null || functions?.Count > 64) throw new InvalidOperationException("函数定义无效或超过 64 个。");
        foreach (var item in globals) ValidateLiteral(item.Value!["value"], ParameterType(Text(item.Value, "type")));
        ValidateFunctions(functions);
        foreach (var nodeValue in nodes)
        {
            var node = nodeValue!.AsObject(); string id = Id(node);
            if (node["parameters"] is not JsonObject parameters) throw new InvalidOperationException("节点参数无效。");
            var locals = VariableDefinitions(node["variables"], 32);
            foreach (var local in locals)
            {
                var schema = new JsonObject { ["name"] = "local." + local.Key, ["type"] = ParameterType(Text(local.Value!, "type")), ["bindable"] = true };
                ValidateBoundValue(recipe, id, "local." + local.Key, local.Value!["value"], schema, globals, locals);
            }
            foreach (var parameter in parameters)
                foreach (var binding in ValueBindings(parameter.Value, parameter.Key))
                    ValidateBoundValue(recipe, id, binding.Path, binding.Binding, ParameterDefinition(recipe, id, binding.Path), globals, locals);
            foreach (var p in Catalog[Text(node, "type")].Parameters.Where(p => Text(p!, "type") == "roi"))
                if (parameters[Text(p!, "name")] is not null) _ = PreviewRoi(recipe, id, Text(p!, "name"));
            CheckLocalCycles(locals);
        }
        var ids = nodes.Select(n => Id(n!)).ToHashSet();
        var visited = new HashSet<string>(); var active = new HashSet<string>();
        void Visit(string id)
        {
            if (active.Contains(id)) throw new InvalidOperationException("参数订阅会使流程产生循环。");
            if (!visited.Add(id)) return;
            active.Add(id);
            var node = nodes.First(n => Id(n!) == id)!;
            var sources = recipe["connections"]!.AsArray().Where(e => Text(e!, "toNode") == id).Select(e => Text(e!, "fromNode"))
                .Concat(FieldBindings(node["parameters"]).Concat(FieldBindings(node["variables"])).Select(b => Text(b, "node"))).Distinct();
            foreach (string source in sources)
            {
                if (!ids.Contains(source)) throw new InvalidOperationException("参数绑定引用已删除的节点：" + source);
                Visit(source);
            }
            active.Remove(id);
        }
        foreach (string id in ids) Visit(id);
    }

    private static JsonObject VariableDefinitions(JsonNode? value, int limit)
    {
        if (value is null) return new();
        if (value is not JsonObject variables || variables.Count > limit) throw new InvalidOperationException("变量定义无效或数量超过 " + limit + " 个。");
        foreach (var item in variables)
        {
            if (!ValidIdentifier(item.Key) || item.Value is not JsonObject definition || definition.Count != 2 || !definition.ContainsKey("type") || !definition.ContainsKey("value"))
                throw new InvalidOperationException("变量名称或定义无效：" + item.Key);
            _ = ParameterType(Text(item.Value, "type"));
        }
        return variables;
    }

    private static void ValidateLiteral(JsonNode? value, string type)
    {
        if (value is not JsonValue scalar || (type switch
        {
            "number" => !double.TryParse(scalar.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double n) || !double.IsFinite(n),
            "integer" => !(scalar.TryGetValue<long>(out _) || scalar.TryGetValue<int>(out _) ||
                double.TryParse(scalar.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double integer) && double.IsFinite(integer) && Math.Truncate(integer) == integer && Math.Abs(integer) <= 9007199254740991.0),
            "bool" => !scalar.TryGetValue<bool>(out _), "string" => !scalar.TryGetValue<string>(out string? text) || text is null || System.Text.Encoding.UTF8.GetByteCount(text) > 256, _ => true
        })) throw new InvalidOperationException("变量字面值与声明类型不匹配。");
    }

    private void ValidateBoundValue(JsonObject recipe, string id, string path, JsonNode? value, JsonObject schema, JsonObject globals, JsonObject locals)
    {
        if (!IsValueBinding(value)) { ValidateLiteral(value, Text(schema, "type")); return; }
        var binding = value!.AsObject();
        if (schema["bindable"]?.GetValue<bool>() != true) throw new InvalidOperationException("参数不支持订阅：" + path);
        if (binding["fallback"] is JsonNode fallback) ValidateLiteral(fallback, Text(schema, "type"));
        if (IsFieldBinding(binding)) { ValidateFieldBinding(recipe, id, path, binding, schema); return; }
        if (binding.ContainsKey("input"))
        {
            if (binding.Any(p => p.Key is not ("input" or "fallback")) || binding["input"] is not JsonValue) throw new InvalidOperationException("运行输入绑定格式无效。");
            var input = recipe["inputs"]?[Text(binding, "input")];
            if (input is null || !Compatible(Text(schema, "type"), Text(input, "type"))) throw new InvalidOperationException("运行输入不存在或类型不匹配。");
            return;
        }
        if (binding.ContainsKey("variable"))
        {
            if (binding.Any(p => p.Key is not ("variable" or "scope" or "fallback")) || binding["variable"] is not JsonValue || Text(binding, "scope") is not ("global" or "local")) throw new InvalidOperationException("变量订阅格式无效。");
            var variable = (Text(binding, "scope") == "global" ? globals : locals)[Text(binding, "variable")];
            if (variable is null || !Compatible(Text(schema, "type"), Text(variable, "type"))) throw new InvalidOperationException("变量不存在或类型不匹配：" + Text(binding, "variable"));
            return;
        }
        if (!binding.ContainsKey("expression") || binding.Any(p => p.Key is not ("expression" or "arguments" or "fallback")) || binding["expression"] is not JsonValue)
            throw new InvalidOperationException("参数订阅格式无效。");
        string expression = Text(binding, "expression");
        if (string.IsNullOrWhiteSpace(expression) || expression.Length > 2048) throw new InvalidOperationException("表达式须为 1～2048 字符。");
        if (binding["arguments"] is not null and not JsonObject || binding["arguments"]?.AsObject().Count > 32) throw new InvalidOperationException("表达式参数定义无效或超过 32 个。");
        foreach (var argument in binding["arguments"]?.AsObject() ?? new())
        {
            if (!ValidIdentifier(argument.Key) || ExpressionReserved.Contains(argument.Key)) throw new InvalidOperationException("表达式参数名称无效：" + argument.Key);
            var argumentSchema = new JsonObject { ["type"] = InferValueType(recipe, id, argument.Value, globals, locals), ["bindable"] = true };
            ValidateBoundValue(recipe, id, path, argument.Value, argumentSchema, globals, locals);
        }
        foreach (Match reference in Regex.Matches(StripStrings(expression), @"\b(global|local)\.([A-Za-z_][A-Za-z0-9_]*)"))
            if (!(reference.Groups[1].Value == "global" ? globals : locals).ContainsKey(reference.Groups[2].Value))
                throw new InvalidOperationException("表达式引用不存在的变量：" + reference.Value);
        ValidateExpressionSymbols(expression, binding["arguments"]?.AsObject().Select(p => p.Key) ?? Array.Empty<string>(), recipe["functions"] as JsonObject, true);
    }

    private string InferValueType(JsonObject recipe, string id, JsonNode? value, JsonObject globals, JsonObject locals)
    {
        if (value is JsonObject o)
        {
            if (o.ContainsKey("expression") && o["fallback"] is JsonValue fallback)
                return fallback.TryGetValue<bool>(out _) ? "bool" : fallback.TryGetValue<string>(out _) ? "string" : "number";
            if (IsFieldBinding(o))
            {
                var node = recipe["nodes"]!.AsArray().FirstOrDefault(n => Id(n!) == Text(o, "node"));
                var field = node is null ? null : Catalog[Text(node, "type")].ScalarFields.FirstOrDefault(f => f.Port == Text(o, "port") && f.Field == Text(o, "field"));
                return field is null ? "number" : ParameterType(field.Type);
            }
            if (o.ContainsKey("input")) return ParameterType(Text(recipe["inputs"]?[Text(o, "input")] ?? new JsonObject(), "type"));
            if (o.ContainsKey("variable")) return ParameterType(Text((Text(o, "scope") == "global" ? globals : locals)[Text(o, "variable")] ?? new JsonObject(), "type"));
            return "number";
        }
        if (value is JsonValue scalar && scalar.TryGetValue<bool>(out _)) return "bool";
        if (value is JsonValue text && text.TryGetValue<string>(out _)) return "string";
        return "number";
    }

    private static string StripStrings(string expression) => Regex.Replace(expression, "\"(?:\\\\.|[^\"\\\\])*\"|'(?:\\\\.|[^'\\\\])*'", " ");

    private static void ValidateExpressionSymbols(string expression, IEnumerable<string> arguments, JsonObject? functions, bool allowVariables)
    {
        string stripped = StripStrings(expression);
        var aliases = arguments.ToHashSet(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(stripped, @"(?<![A-Za-z0-9_])([A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)?)"))
        {
            string name = match.Groups[1].Value;
            bool call = stripped[(match.Index + match.Length)..].TrimStart().StartsWith('(');
            if (call)
            {
                if ((!ExpressionReserved.Contains(name) || name is "global" or "local" or "pi" or "e" or "true" or "false") && functions?.ContainsKey(name) != true)
                    throw new InvalidOperationException("表达式引用不存在的函数：" + name);
            }
            else if (!aliases.Contains(name) && name is not ("pi" or "e" or "true" or "false") &&
                !(allowVariables && (name.StartsWith("global.", StringComparison.Ordinal) || name.StartsWith("local.", StringComparison.Ordinal))))
                throw new InvalidOperationException("表达式引用不存在的参数：" + name);
        }
    }

    private static void CheckLocalCycles(JsonObject locals)
    {
        var done = new HashSet<string>(); var active = new HashSet<string>();
        void Visit(string name)
        {
            if (active.Contains(name)) throw new InvalidOperationException("局部变量之间存在循环引用：" + name);
            if (!done.Add(name)) return;
            active.Add(name);
            foreach (var binding in ValueBindings(locals[name]!["value"]))
            {
                void Follow(JsonObject b)
                {
                    if (Text(b, "scope") == "local" && b.ContainsKey("variable")) Visit(Text(b, "variable"));
                    if (b["expression"] is JsonValue)
                        foreach (Match match in Regex.Matches(StripStrings(Text(b, "expression")), @"\blocal\.([A-Za-z_][A-Za-z0-9_]*)")) Visit(match.Groups[1].Value);
                    foreach (var argument in b["arguments"]?.AsObject() ?? new())
                        foreach (var nested in ValueBindings(argument.Value)) Follow(nested.Binding);
                }
                Follow(binding.Binding);
            }
            active.Remove(name);
        }
        foreach (var variable in locals) Visit(variable.Key);
    }

    private static void ValidateFunctions(JsonObject? functions)
    {
        if (functions is null) return;
        var calls = new Dictionary<string, string[]>();
        foreach (var item in functions)
        {
            if (!ValidIdentifier(item.Key) || ExpressionReserved.Contains(item.Key) || item.Value is not JsonObject definition || definition.Count != 2 || definition["parameters"] is not JsonArray parameters || parameters.Count > 16 || definition["expression"] is not JsonValue)
                throw new InvalidOperationException("函数名称或定义无效：" + item.Key);
            string expression = Text(definition, "expression");
            if (string.IsNullOrWhiteSpace(expression) || expression.Length > 2048) throw new InvalidOperationException("函数表达式须为 1～2048 字符。");
            var names = parameters.Select(p => p is JsonValue scalar && scalar.TryGetValue<string>(out string? n) ? n : "").ToArray();
            if (names.Any(n => !ValidIdentifier(n) || ExpressionReserved.Contains(n)) || names.Distinct().Count() != names.Length) throw new InvalidOperationException("函数参数名称无效或重复。");
            string stripped = StripStrings(expression);
            if (Regex.IsMatch(stripped, @"\b(global|local)\.")) throw new InvalidOperationException("纯计算函数只能访问自己的形参。");
            ValidateExpressionSymbols(expression, names, functions, false);
            calls[item.Key] = Regex.Matches(stripped, @"\b([A-Za-z_][A-Za-z0-9_]*)\s*\(").Select(m => m.Groups[1].Value).Where(functions.ContainsKey).Distinct().ToArray();
        }
        var depths = new Dictionary<string, int>(); var active = new HashSet<string>();
        int Depth(string name)
        {
            if (active.Contains(name)) throw new InvalidOperationException("函数之间不允许递归调用。");
            if (depths.TryGetValue(name, out int known)) return known;
            active.Add(name);
            int depth = 1;
            foreach (string call in calls[name]) depth = Math.Max(depth, 1 + Depth(call));
            active.Remove(name);
            if (depth > 16) throw new InvalidOperationException("函数调用深度超过 16 层。");
            return depths[name] = depth;
        }
        foreach (string name in calls.Keys) _ = Depth(name);
    }
}
