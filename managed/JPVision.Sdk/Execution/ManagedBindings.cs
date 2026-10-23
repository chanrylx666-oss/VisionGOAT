using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using static JPVision.Sdk.Execution.ManagedExecutionPlan;

namespace JPVision.Sdk.Execution;

internal sealed record ManagedInputCoordinates(int X, int Y, string CoordinateSpace = "original",
    string CoordinatePlane = "", bool HasFixture = false, bool HasMatchPosition = false);

internal sealed record ManagedParameterResolution(JsonObject Parameters, JsonObject Locals, bool Deferred);

/// <summary>Same-frame scalar subscriptions, variables and pure calculations, independent of image kernels.</summary>
internal static class ManagedBindings
{
    internal static bool IsBinding(JsonNode? value) => value is JsonObject o &&
        (o.ContainsKey("node") || o.ContainsKey("input") || o.ContainsKey("variable") || o.ContainsKey("expression"));

    private static bool Identifier(string name) => name.Length is > 0 and <= 64 &&
        (name[0] is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_') &&
        name.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_');

    private static bool TryString(JsonNode? node, out string value)
    {
        if (node is JsonValue scalar && scalar.TryGetValue<string>(out var text))
        {
            value = text;
            return true;
        }
        value = "";
        return false;
    }

    internal static bool TryNumber(JsonNode? node, out double value)
    {
        value = 0;
        return node is JsonValue scalar && scalar.GetValueKind() == JsonValueKind.Number &&
            double.TryParse(scalar.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }

    private static bool IsInteger(JsonNode? node) => node is JsonValue scalar &&
        (scalar.TryGetValue<long>(out _) || scalar.TryGetValue<int>(out _) || scalar.TryGetValue<short>(out _) ||
        scalar.TryGetValue<byte>(out _) || scalar.TryGetValue<uint>(out _) || scalar.TryGetValue<ulong>(out _));

    private static string ScalarType(JsonNode? value) => value?.GetValueKind() switch
    {
        JsonValueKind.True or JsonValueKind.False => "Bool",
        JsonValueKind.String => "String",
        JsonValueKind.Number => IsInteger(value) ? "Int" : "Double",
        _ => ""
    };

    internal static JsonNode? CheckedScalar(JsonNode? value, string type, string label, int code = 2)
    {
        if (type == "Int" && ScalarType(value) == "Double")
        {
            Require(TryNumber(value, out double number) && Math.Truncate(number) == number &&
                Math.Abs(number) <= 9007199254740991d,
                "Integer variable/parameter requires an exact integer: " + label, code);
            value = JsonValue.Create((long)number);
        }
        Require(type switch
        {
            "Int" => IsInteger(value),
            "Double" => TryNumber(value, out _),
            "Bool" => value?.GetValueKind() is JsonValueKind.True or JsonValueKind.False,
            "String" => TryString(value, out string text) && Encoding.UTF8.GetByteCount(text) <= 256,
            _ => false
        }, "Variable/parameter type mismatch: " + label, code);
        return value?.DeepClone();
    }

    private static JsonObject VariableDefinitions(JsonNode? raw, int limit, bool literal)
    {
        var definitions = raw as JsonObject;
        Require(definitions is not null && definitions.Count <= limit, "Variable table is invalid or too large.");
        foreach (var pair in definitions!.OrderBy(p => p.Key, Comparer))
        {
            var definition = pair.Value as JsonObject;
            Require(Identifier(pair.Key) && definition is not null && definition.Count == 2 &&
                TryString(definition["type"], out _) && definition.ContainsKey("value"),
                "Variable requires a valid name, type and value.");
            string type = Text(definition!, "type");
            Require(type is "Int" or "Double" or "Bool" or "String", "Unsupported variable type.");
            if (literal || !IsBinding(definition!["value"]))
                CheckedScalar(definition!["value"], type, pair.Key);
        }
        return definitions;
    }

    public static JsonObject Globals(JsonObject recipe, JsonObject overrides)
    {
        var definitions = VariableDefinitions(recipe.ContainsKey("variables") ? recipe["variables"] : new JsonObject(), 64, true);
        ManagedExpression.ValidateFunctions(recipe.ContainsKey("functions")
            ? Object(recipe["functions"], "Functions must be an object.") : new JsonObject());
        foreach (var pair in overrides.OrderBy(p => p.Key, Comparer))
            Require(definitions.ContainsKey(pair.Key), "Unknown global run variable: " + pair.Key, 1);
        var values = new JsonObject();
        foreach (var pair in definitions.OrderBy(p => p.Key, Comparer))
            values[pair.Key] = CheckedScalar(overrides.ContainsKey(pair.Key) ? overrides[pair.Key] : pair.Value!["value"],
                Text(pair.Value!, "type"), pair.Key, 1);
        return values;
    }

    public static SortedDictionary<string, (string Node, string Port)> Dependencies(JsonObject node)
    {
        var result = new SortedDictionary<string, (string Node, string Port)>(Comparer);
        void Walk(JsonNode? value, string path, int depth)
        {
            Require(depth <= 40, "Parameter binding nesting is too deep.", 5);
            if (value is JsonObject o)
            {
                if (TryString(o["node"], out string source) && TryString(o["port"], out string port))
                    result[path] = (source, port);
                else
                    foreach (var pair in o.OrderBy(p => p.Key, Comparer)) Walk(pair.Value, path + "." + pair.Key, depth + 1);
            }
            else if (value is JsonArray array)
                for (int i = 0; i < array.Count; ++i) Walk(array[i], path + "." + i, depth + 1);
        }
        Walk(node["parameters"], "parameters", 0);
        if (node.ContainsKey("variables")) Walk(node["variables"], "variables", 0);
        return result;
    }

    public static ManagedParameterResolution Resolve(JsonObject node, JsonObject publicValues, ManagedExecutionPlan plan,
        Func<string, string, string, JsonNode?>? fieldLookup, Func<string, bool>? skipped,
        ManagedInputCoordinates? input, JsonObject globals)
    {
        var descriptor = plan.Catalog[Text(node, "type")];
        var given = Object(node["parameters"], "Node parameters must be an object.");
        var specifications = (JsonArray)descriptor["parameters"]!;
        foreach (var parameter in given.OrderBy(p => p.Key, Comparer))
            Require(specifications.Any(p => Text(p!, "name") == parameter.Key), "Unknown node parameter: " + parameter.Key);
        var resolver = new Resolver(node, publicValues, plan, fieldLookup, skipped, input, globals);
        JsonObject locals = resolver.LocalSnapshot(), parameters = new();
        foreach (var raw in specifications)
        {
            var spec = (JsonObject)raw!;
            string name = Text(spec, "name");
            JsonNode? value = given.ContainsKey(name) ? given[name] : spec["default"];
            value = Text(spec, "type") == "roi" ? resolver.RoiValue(value, name) : resolver.ParameterValue(value, spec, name);
            // Complex ROI, trained model and fixture constraints remain checked by the native kernel.
            if (Text(spec, "type") is not ("roi" or "template" or "fixtureReference")) ValidatePrimitive(spec, value);
            parameters[name] = value?.DeepClone();
        }
        resolver.ValidateCoordinateReferences(parameters);
        if (resolver.Unavailable.Count != 0) parameters["_unavailable"] = resolver.Unavailable.DeepClone();
        return new(parameters, locals, resolver.Deferred);
    }

    private static void ValidatePrimitive(JsonObject spec, JsonNode? value)
    {
        string name = Text(spec, "name"), type = Text(spec, "type");
        if (type is "number" or "integer")
        {
            Require(type != "integer" || IsInteger(value), name + " must be an integer.", 1);
            Require(TryNumber(value, out double number) && TryNumber(spec["min"], out double minimum) &&
                TryNumber(spec["max"], out double maximum) && number >= minimum && number <= maximum,
                name + " is out of range.", type == "integer" ? 1 : 2);
        }
        else if (type == "bool")
            Require(value?.GetValueKind() is JsonValueKind.True or JsonValueKind.False, name + " must be Bool.");
        else
        {
            Require(TryString(value, out string text) && Encoding.UTF8.GetByteCount(text) <= 256, name + " must be a short string.");
            if (type == "enum")
                Require(spec["choices"] is JsonArray choices && choices.Any(p => JsonNode.DeepEquals(p, value)), name + " has unsupported value.");
            if (name is "color" or "overlayColor")
                Require(text.Length == 7 && text[0] == '#' && text.Skip(1).All(Uri.IsHexDigit), "Color must be #RRGGBB.");
        }
    }

    private sealed record CoordinateFrame(string Space, string Plane);
    private sealed class Scalar
    {
        public JsonNode? Value;
        public string Type = "";
        public List<CoordinateFrame> Frames = new();
        public bool Position, Unavailable, Deferred;
        public Scalar Copy() => new() { Value = Value?.DeepClone(), Type = Type, Frames = new(Frames),
            Position = Position, Unavailable = Unavailable, Deferred = Deferred };
    }
    private sealed class MissingValue : Exception { }

    private sealed class Resolver
    {
        private readonly JsonObject node, publicValues, globals, functions, definitions;
        private readonly ManagedExecutionPlan plan;
        private readonly Func<string, string, string, JsonNode?>? lookup;
        private readonly Func<string, bool>? skipped;
        private readonly ManagedInputCoordinates? input;
        private readonly Dictionary<string, Scalar> locals = new(StringComparer.Ordinal);
        private readonly HashSet<string> visiting = new(StringComparer.Ordinal), geometricRois = new(StringComparer.Ordinal);
        private int steps;
        public bool Deferred { get; private set; }
        public JsonArray Unavailable { get; } = new();

        public Resolver(JsonObject node, JsonObject publicValues, ManagedExecutionPlan plan,
            Func<string, string, string, JsonNode?>? lookup, Func<string, bool>? skipped,
            ManagedInputCoordinates? input, JsonObject globals)
        {
            this.node = node; this.publicValues = publicValues; this.plan = plan;
            this.lookup = lookup; this.skipped = skipped; this.input = input; this.globals = globals;
            functions = plan.Definition.ContainsKey("functions") ? Object(plan.Definition["functions"], "Functions must be an object.") : new();
            definitions = VariableDefinitions(node.ContainsKey("variables") ? node["variables"] : new JsonObject(), 32, false);
        }

        private Scalar Local(string name)
        {
            if (locals.TryGetValue(name, out var cached)) return cached.Copy();
            Require(definitions.ContainsKey(name), "Unknown local variable: " + name);
            Require(visiting.Add(name), "Local variable dependency cycle: " + name);
            var definition = (JsonObject)definitions[name]!;
            Scalar value = Resolve(definition["value"]);
            string type = Text(definition, "type");
            value.Value = !value.Deferred && !value.Unavailable ? CheckedScalar(value.Value, type, name) :
                type == "Bool" ? JsonValue.Create(false) : type == "String" ? JsonValue.Create("") : JsonValue.Create(0);
            value.Type = type;
            visiting.Remove(name);
            locals[name] = value;
            return value.Copy();
        }

        private Scalar Symbol(string name, JsonObject arguments)
        {
            if (name.StartsWith("global.", StringComparison.Ordinal))
            {
                string key = name[7..];
                Require(globals.ContainsKey(key), "Unknown global variable: " + key);
                return new() { Value = globals[key]?.DeepClone(), Type = ScalarType(globals[key]) };
            }
            if (name.StartsWith("local.", StringComparison.Ordinal)) return Local(name[6..]);
            Require(arguments.ContainsKey(name), "Unknown expression argument: " + name);
            return Resolve(arguments[name]);
        }

        private JsonObject FieldDefinition(JsonObject descriptor, string port, string field)
        {
            var definition = FindPort(descriptor, "outputs", port);
            string type = Text(definition, "type");
            if (field.Length == 0 && type is "Int" or "Double" or "Bool" or "String")
                return new() { ["type"] = type, ["nullable"] = false };
            Require(Encoding.UTF8.GetByteCount(field) <= 128 && descriptor["fields"]?[port]?[field] is JsonObject,
                "Unknown scalar result field: " + port + "." + field);
            return (JsonObject)descriptor["fields"]![port]![field]!;
        }

        private static string Leaf(string field) => field[(field.LastIndexOf('.') + 1)..];
        private static bool PositionField(string field) => Leaf(field) is "x" or "y" or "x1" or "y1" or "x2" or "y2" or "centerX" or "centerY" or "centroidX" or "centroidY";
        private static bool GeometricField(string field) => PositionField(field) || Leaf(field) is "radius" or "diameter" or "length" or "width" or "height" or "innerRadius" or "outerRadius";

        private Scalar Resolve(JsonNode? definition, int depth = 0)
        {
            Require(++steps <= 8192 && depth <= 32, "Parameter binding evaluation budget exceeded.", 5);
            if (!IsBinding(definition)) return new() { Value = definition?.DeepClone(), Type = ScalarType(definition) };
            var binding = (JsonObject)definition!;
            var allowed = new HashSet<string>(StringComparer.Ordinal) { "fallback" };
            var result = new Scalar();
            if (binding.ContainsKey("node"))
            {
                allowed.UnionWith(new[] { "node", "port", "field" });
                Require(TryString(binding["node"], out string source) && TryString(binding["port"], out _) &&
                    TryString(binding["field"], out _), "Invalid output field subscription.");
                string port = Text(binding, "port"), field = Text(binding, "field");
                Require(source != Text(node, "id") && plan.Nodes.ContainsKey(source), "Parameter binding refers to missing/self node.");
                var spec = FieldDefinition(plan.Descriptor(source), port, field);
                result.Type = Text(spec, "type");
                if (lookup is null) result.Deferred = true;
                else
                {
                    bool wasSkipped = skipped?.Invoke(source) == true;
                    result.Value = wasSkipped ? null : lookup(source, port, field)?.DeepClone();
                    if (spec.ContainsKey("validWhen") && lookup(source, port, Text(spec, "validWhen"))?.GetValueKind() != JsonValueKind.True)
                        result.Value = null;
                    if (result.Value is null)
                    {
                        Require(wasSkipped || spec["nullable"]?.GetValue<bool>() == true,
                            "Required scalar result field is missing: " + field, 6);
                        result.Unavailable = true;
                    }
                    else if (GeometricField(field) && TryString(lookup(source, port, "coordinateSpace"), out string space))
                    {
                        TryString(lookup(source, port, "coordinatePlane"), out string plane);
                        result.Frames.Add(new(space, plane));
                        result.Position = PositionField(field);
                    }
                }
            }
            else if (binding.ContainsKey("input"))
            {
                allowed.Add("input");
                Require(TryString(binding["input"], out string name) && publicValues.ContainsKey(name), "Unknown public scalar input binding.");
                result.Value = publicValues[name]?.DeepClone(); result.Type = ScalarType(result.Value);
            }
            else if (binding.ContainsKey("variable"))
            {
                allowed.UnionWith(new[] { "variable", "scope" });
                Require(TryString(binding["variable"], out string name) && TryString(binding["scope"], out _), "Variable binding requires name and scope.");
                string scope = Text(binding, "scope");
                Require(Identifier(name) && scope is "global" or "local", "Invalid variable subscription.");
                result = Symbol(scope + "." + name, new());
            }
            else
            {
                allowed.UnionWith(new[] { "expression", "arguments" });
                Require(TryString(binding["expression"], out string expression), "Expression must be a string.");
                var arguments = binding.ContainsKey("arguments") ? binding["arguments"] as JsonObject : new JsonObject();
                Require(arguments is not null && arguments.Count <= 32, "Invalid expression argument table.");
                foreach (string name in arguments!.Select(p => p.Key)) Require(Identifier(name), "Invalid expression argument name.");
                var names = ManagedExpression.Symbols(expression, functions);
                if (lookup is null)
                {
                    foreach (string name in names.OrderBy(n => n, Comparer)) Symbol(name, arguments);
                    foreach (var pair in arguments.OrderBy(p => p.Key, Comparer)) Resolve(pair.Value, depth + 1);
                    result.Deferred = true;
                }
                else
                {
                    try
                    {
                        result.Value = ManagedExpression.Evaluate(expression, name =>
                        {
                            var value = Symbol(name, arguments);
                            if (value.Unavailable) throw new MissingValue();
                            result.Position |= value.Position;
                            foreach (var frame in value.Frames)
                                if (!result.Frames.Contains(frame))
                                {
                                    Require(result.Frames.Count < 64, "Expression coordinate plane limit exceeded.", 5);
                                    result.Frames.Add(frame);
                                }
                            return value.Value;
                        }, functions);
                    }
                    catch (MissingValue) { result.Unavailable = true; }
                    result.Type = ScalarType(result.Value);
                }
            }
            foreach (string key in binding.Select(p => p.Key).OrderBy(n => n, Comparer)) Require(allowed.Contains(key), "Unknown subscription option: " + key);
            return result;
        }

        public JsonNode? ParameterValue(JsonNode? value, JsonObject spec, string path, bool roi = false, string axis = "")
        {
            if (!IsBinding(value)) return value?.DeepClone();
            var binding = (JsonObject)value!;
            Require(roi || spec["bindable"]?.GetValue<bool>() == true, "Parameter does not support subscription: " + path);
            Scalar resolved = Resolve(binding);
            string target = Text(spec, "type") switch { "number" => "Double", "integer" => "Int", "bool" => "Bool", _ => "String" };
            if (binding.ContainsKey("fallback")) CheckedScalar(binding["fallback"], target, path);
            if (resolved.Type.Length != 0)
                Require(resolved.Type == target || target == "Double" && resolved.Type == "Int" ||
                    target == "Int" && binding.ContainsKey("expression") && resolved.Type == "Double", "Parameter field type mismatch: " + path);
            if (resolved.Deferred || resolved.Unavailable)
            {
                Deferred |= resolved.Deferred;
                if (resolved.Unavailable) Unavailable.Add(path);
                if (binding.ContainsKey("fallback")) return CheckedScalar(binding["fallback"], target, path);
                JsonNode? fallback = spec["default"];
                if (fallback is not JsonObject o) return fallback?.DeepClone();
                if (TryString(o["input"], out string name) && publicValues.ContainsKey(name)) return publicValues[name]?.DeepClone();
                return target == "Bool" ? JsonValue.Create(false) : target == "String" ? JsonValue.Create("") : spec["min"]?.DeepClone() ?? JsonValue.Create(0);
            }
            if (roi && input is not null)
            {
                if (resolved.Frames.Count != 0)
                {
                    geometricRois.Add(path.Split('.')[0]);
                    Require(Text(spec, "coordinateSpace", "input") != "reference", "Geometry subscriptions require image ROI reference, not fixture reference.", 1);
                }
                foreach (var frame in resolved.Frames)
                    Require(frame.Space == input.CoordinateSpace && frame.Plane == input.CoordinatePlane,
                        "ROI subscription belongs to a different image coordinate plane: " + path, 1);
                if (axis.Length != 0 && resolved.Position)
                {
                    Require(Text(spec, "coordinateSpace", "input") != "reference", "Coordinate subscriptions require image ROI reference, not fixture reference.", 1);
                    TryNumber(resolved.Value, out double number);
                    resolved.Value = JsonValue.Create(number - (axis == "x" ? input.X : input.Y));
                }
            }
            return CheckedScalar(resolved.Value, target, path);
        }

        public void ValidateCoordinateReferences(JsonObject parameters)
        {
            foreach (string key in geometricRois.OrderBy(n => n, Comparer))
            {
                string reference = key == "firstRoi" ? "firstRoiReference" : key == "secondRoi" ? "secondRoiReference" : "roiReference";
                Require(Text(parameters, reference, "image") == "image", "Geometry subscriptions require image ROI reference; disable fixture positioning on this ROI.", 1);
                if (key == "roi" && Text(node, "type") == "FindCircles")
                {
                    string mode = Text(parameters, "positionReference", "image");
                    bool positioned = mode == "auto" ? input is not null && (input.HasFixture || input.HasMatchPosition) : mode != "image";
                    Require(!positioned, "Geometry subscriptions require image positioning; do not combine them with automatic match/fixture positioning.", 1);
                }
            }
        }

        public JsonObject RoiValue(JsonNode? value, string path)
        {
            var roi = Object(value, "ROI shape is missing.");
            Require(TryString(roi["shape"], out string shape), "ROI shape is missing.");
            var fields = new HashSet<string>(StringComparer.Ordinal) { "shape", "coordinateSpace", "referenceHash" };
            fields.UnionWith(shape switch
            {
                "rectangle" => new[] { "x", "y", "width", "height" },
                "circle" => new[] { "cx", "cy", "radius" },
                "annulus" => new[] { "cx", "cy", "innerRadius", "outerRadius", "startAngle", "endAngle" },
                "rotatedRectangle" => new[] { "cx", "cy", "width", "height", "angleDegrees" },
                "polygon" => new[] { "points" }, "none" => Array.Empty<string>(),
                _ => throw Invalid("Unsupported ROI shape.")
            });
            var result = roi.DeepClone().AsObject();
            foreach (var pair in roi.OrderBy(p => p.Key, Comparer))
            {
                string key = pair.Key;
                Require(fields.Contains(key), "Unknown ROI field: " + key);
                bool coordinate = key is "x" or "y" or "cx" or "cy", angle = key is "startAngle" or "endAngle" or "angleDegrees",
                    radius = key is "radius" or "innerRadius" or "outerRadius";
                if (coordinate || angle || radius || key is "width" or "height")
                {
                    bool reference = Text(roi, "coordinateSpace", "input") == "reference";
                    double minimum = angle ? -360 : coordinate && reference ? -65536 : 0,
                        maximum = angle ? 720 : radius ? 16384 : coordinate && reference ? 65536 : 32768;
                    var spec = new JsonObject
                    {
                        ["type"] = "number", ["default"] = key == "innerRadius" ? 0 : key == "outerRadius" ? 100 : radius ? 60 :
                            key == "endAngle" ? 360 : key is "width" or "height" ? 50 : coordinate ? 160 : 0,
                        ["min"] = minimum, ["max"] = maximum, ["coordinateSpace"] = Text(roi, "coordinateSpace", "input")
                    };
                    result[key] = ParameterValue(pair.Value, spec, path + "." + key, true,
                        key is "x" or "cx" ? "x" : key is "y" or "cy" ? "y" : "");
                }
                else if (key == "points")
                {
                    Require(pair.Value is JsonArray vertices && vertices.Count <= 64, "Polygon points are invalid.");
                    var points = (JsonArray)pair.Value!;
                    for (int i = 0; i < points.Count; ++i)
                    {
                        Require(points[i] is JsonArray vertex && vertex.Count == 2, "Polygon vertex must contain X and Y.");
                        for (int axis = 0; axis < 2; ++axis)
                        {
                            var spec = new JsonObject { ["type"] = "number", ["default"] = i == 1 || axis == 1 && i == 2 ? 100 : 10,
                                ["min"] = 0, ["max"] = 32768, ["coordinateSpace"] = Text(roi, "coordinateSpace", "input") };
                            result[key]![i]![axis] = ParameterValue(points[i]![axis], spec, path + ".points." + i + "." + axis, true, axis == 0 ? "x" : "y");
                        }
                    }
                }
            }
            if (shape == "rectangle")
                foreach (string key in new[] { "x", "y", "width", "height" })
                {
                    Require(TryNumber(result[key], out double number) && Math.Abs(number) <= 65536, "Rectangle ROI coordinate is invalid.");
                    result[key] = (long)Math.Round(number, MidpointRounding.AwayFromZero);
                }
            return result;
        }

        public JsonObject LocalSnapshot()
        {
            var result = new JsonObject();
            foreach (var pair in definitions.OrderBy(p => p.Key, Comparer))
            {
                Scalar value = Local(pair.Key);
                Deferred |= value.Deferred;
                if (value.Unavailable) Unavailable.Add("local." + pair.Key);
                result[pair.Key] = value.Deferred || value.Unavailable ? null : value.Value?.DeepClone();
            }
            return result;
        }
    }
}
