using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

[assembly: InternalsVisibleTo("JPVision.Tests")]

namespace JPVision.Sdk;

/// <summary>Bounded, pure recipe expressions. No CLR evaluation, reflection, I/O or scripting is allowed.</summary>
internal static class ManagedExpression
{
    private const int MaximumExpressionLength = 2048, MaximumNodes = 256, MaximumDepth = 32;
    private const int MaximumSteps = 4096, MaximumCallDepth = 16;
    private static readonly IReadOnlyDictionary<string, int> Builtins = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["abs"] = 1, ["min"] = 2, ["max"] = 2, ["clamp"] = 3, ["sqrt"] = 1, ["pow"] = 2,
        ["round"] = 1, ["floor"] = 1, ["ceil"] = 1, ["sin"] = 1, ["cos"] = 1, ["tan"] = 1,
        ["atan2"] = 2, ["hypot"] = 2
    };

    internal static void ValidateFunctions(JsonObject definitions) => CompileFunctions(definitions);

    internal static HashSet<string> Symbols(string expression, JsonObject definitions)
    {
        var functions = CompileFunctions(definitions);
        var parsed = new Parser(expression).Parse();
        ValidateCalls(parsed, functions);
        return parsed.Nodes.Where(n => n.Kind == Kind.Symbol).Select(n => n.Text).ToHashSet(StringComparer.Ordinal);
    }

    internal static JsonNode Evaluate(string expression, Func<string, JsonNode?> lookup, JsonObject definitions)
    {
        var functions = CompileFunctions(definitions);
        var parsed = new Parser(expression).Parse();
        ValidateCalls(parsed, functions);
        return new Evaluation(functions).Run(parsed, parsed.Root, lookup);
    }

    private static void Require(bool condition, int code, string message)
    {
        if (condition) return;
        if (code == 1) throw new ArgumentException(message);
        throw new VisionException(code, message);
    }
    private static bool IdentifierStart(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_';
    private static bool IdentifierContinue(char c) => IdentifierStart(c) || c is >= '0' and <= '9';
    private static bool SimpleIdentifier(string value) => value.Length is > 0 and <= 64 && IdentifierStart(value[0]) && value.Skip(1).All(IdentifierContinue);
    private static bool Reserved(string name) => name is "pi" or "e" or "true" or "false" or "global" or "local" || Builtins.ContainsKey(name);
    private static string? String(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : null;

    private enum Kind { Number, Boolean, Symbol, Unary, Binary, Conditional, Call }
    private sealed record Node(Kind Kind, string Text, int[] Children, double Number = 0, bool Boolean = false)
    {
        internal int Depth { get; set; } = 1;
    }
    private sealed record Expression(List<Node> Nodes, int Root);
    private sealed record Function(string[] Parameters, Expression Body);

    private sealed class Parser
    {
        private readonly string source;
        private readonly List<Node> nodes = new();
        private int position, nesting, unaryNesting;
        internal Parser(string expression)
        {
            Require(!string.IsNullOrEmpty(expression), 2, "Expression cannot be empty.");
            Require(Encoding.UTF8.GetByteCount(expression) <= MaximumExpressionLength, 5, "Expression length limit exceeded.");
            source = expression;
        }
        private VisionException Invalid(string message) => new(2,
            $"Expression at {Encoding.UTF8.GetByteCount(source.AsSpan(0, position)) + 1}: {message}");
        private void Whitespace()
        {
            while (position < source.Length && source[position] is ' ' or '\t' or '\r' or '\n') ++position;
        }
        private bool Accept(string text)
        {
            Whitespace();
            if (!source.AsSpan(position).StartsWith(text, StringComparison.Ordinal)) return false;
            position += text.Length;
            return true;
        }
        private void Expect(string text)
        {
            if (!Accept(text)) throw Invalid($"Expected '{text}'.");
        }
        private int Add(Node node)
        {
            Require(nodes.Count < MaximumNodes, 5, "Expression node limit exceeded.");
            foreach (int child in node.Children) node.Depth = Math.Max(node.Depth, nodes[child].Depth + 1);
            Require(node.Depth <= MaximumDepth, 5, "Expression depth limit exceeded.");
            nodes.Add(node);
            return nodes.Count - 1;
        }
        internal Expression Parse()
        {
            int root = Conditional();
            Whitespace();
            if (position != source.Length) throw Invalid("Unexpected token.");
            return new(nodes, root);
        }
        private int Conditional()
        {
            Require(++nesting <= MaximumDepth, 5, "Expression nesting limit exceeded.");
            int condition = LogicalOr();
            if (Accept("?"))
            {
                int yes = Conditional();
                Expect(":");
                int no = Conditional();
                condition = Add(new(Kind.Conditional, "", [condition, yes, no]));
            }
            --nesting;
            return condition;
        }
        private int Binary(Func<int> next, params string[] operators)
        {
            int left = next();
            while (true)
            {
                string? operation = null;
                foreach (string candidate in operators)
                    if (Accept(candidate)) { operation = candidate; break; }
                if (operation is null) return left;
                int right = next();
                left = Add(new(Kind.Binary, operation, [left, right]));
            }
        }
        private int LogicalOr() => Binary(LogicalAnd, "||");
        private int LogicalAnd() => Binary(Equality, "&&");
        private int Equality() => Binary(Comparison, "==", "!=");
        private int Comparison() => Binary(Sum, "<=", ">=", "<", ">");
        private int Sum() => Binary(Product, "+", "-");
        private int Product() => Binary(Unary, "*", "/", "%");
        private int Unary()
        {
            Require(++unaryNesting <= MaximumDepth, 5, "Expression unary depth limit exceeded.");
            int value;
            if (Accept("!")) value = Add(new(Kind.Unary, "!", [Unary()]));
            else if (Accept("+")) value = Add(new(Kind.Unary, "+", [Unary()]));
            else if (Accept("-")) value = Add(new(Kind.Unary, "-", [Unary()]));
            else value = Primary();
            --unaryNesting;
            return value;
        }
        private int Number()
        {
            Whitespace();
            int start = position;
            bool digits = false, nonzero = false;
            void Digits()
            {
                while (position < source.Length && source[position] is >= '0' and <= '9')
                {
                    nonzero |= source[position] != '0';
                    ++position;
                    digits = true;
                }
            }
            Digits();
            if (position < source.Length && source[position] == '.') { ++position; Digits(); }
            if (!digits) throw Invalid("Expected a number.");
            if (position < source.Length && source[position] is 'e' or 'E')
            {
                ++position;
                if (position < source.Length && source[position] is '+' or '-') ++position;
                int exponent = position;
                while (position < source.Length && source[position] is >= '0' and <= '9') ++position;
                if (position == exponent) throw Invalid("Invalid exponent.");
            }
            // from_chars rejects underflow; TryParse would silently turn 1e-999 into zero.
            if (!double.TryParse(source.AsSpan(start, position - start), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ||
                !double.IsFinite(value) || value == 0 && nonzero)
                throw Invalid("Number must be finite and representable.");
            return Add(new(Kind.Number, "", [], value));
        }
        private string Identifier()
        {
            Whitespace();
            int start = position;
            if (position >= source.Length || !IdentifierStart(source[position])) throw Invalid("Expected a symbol.");
            while (position < source.Length && IdentifierContinue(source[position])) ++position;
            Require(position - start <= 64, 5, "Expression symbol segment is too long.");
            if (position < source.Length && source[position] == '.')
            {
                ++position;
                int member = position;
                if (position >= source.Length || !IdentifierStart(source[position])) throw Invalid("Expected a member name.");
                while (position < source.Length && IdentifierContinue(source[position])) ++position;
                Require(position - member <= 64, 5, "Expression symbol segment is too long.");
            }
            return source[start..position];
        }
        private int Primary()
        {
            if (Accept("("))
            {
                int value = Conditional();
                Expect(")");
                return value;
            }
            Whitespace();
            if (position >= source.Length) throw Invalid("Expected a value.");
            if (source[position] is >= '0' and <= '9' or '.') return Number();
            string name = Identifier();
            if (Accept("("))
            {
                if (name.Contains('.')) throw Invalid("Function names cannot contain a member access.");
                var arguments = new List<int>();
                if (!Accept(")"))
                {
                    do
                    {
                        Require(arguments.Count < 16, 5, "Expression call argument limit exceeded.");
                        arguments.Add(Conditional());
                    } while (Accept(","));
                    Expect(")");
                }
                return Add(new(Kind.Call, name, arguments.ToArray()));
            }
            if (name is "true" or "false") return Add(new(Kind.Boolean, "", [], Boolean: name == "true"));
            if (name is "pi" or "e") return Add(new(Kind.Number, "", [], name == "pi" ? Math.PI : Math.E));
            return Add(new(Kind.Symbol, name, []));
        }
    }

    private static void ValidateCalls(Expression expression, IReadOnlyDictionary<string, Function> functions)
    {
        foreach (var node in expression.Nodes.Where(n => n.Kind == Kind.Call))
        {
            bool builtin = Builtins.TryGetValue(node.Text, out int arity);
            bool custom = functions.TryGetValue(node.Text, out var function);
            Require(builtin || custom, 2, "Unknown expression function: " + node.Text);
            if (!builtin) arity = function!.Parameters.Length;
            Require(node.Children.Length == arity, 2, "Wrong argument count for expression function: " + node.Text);
        }
    }
    private static Dictionary<string, Function> CompileFunctions(JsonObject definitions)
    {
        Require(definitions is not null, 2, "Functions must be an object.");
        Require(definitions!.Count <= 64, 5, "Function count limit exceeded.");
        var functions = new Dictionary<string, Function>(StringComparer.Ordinal);
        foreach (var (name, raw) in definitions.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            Require(SimpleIdentifier(name) && !Reserved(name), 2, "Invalid or reserved function name: " + name);
            Require(raw is JsonObject o && o.Count == 2 && o["parameters"] is JsonArray && String(o["expression"]) is not null,
                2, "Function requires parameters and expression: " + name);
            var definition = (JsonObject)raw!;
            var parameters = (JsonArray)definition["parameters"]!;
            Require(parameters.Count <= 16, 5, "Function parameter count limit exceeded.");
            var names = new List<string>();
            var unique = new HashSet<string>(StringComparer.Ordinal);
            foreach (var parameter in parameters)
            {
                string? key = String(parameter);
                Require(key is not null, 2, "Function parameter name must be a string.");
                Require(SimpleIdentifier(key!) && !Reserved(key!) && unique.Add(key!), 2,
                    "Invalid, duplicate, or reserved function parameter: " + key);
                names.Add(key!);
            }
            var body = new Parser(String(definition["expression"])!).Parse();
            foreach (var node in body.Nodes.Where(n => n.Kind == Kind.Symbol))
                Require(unique.Contains(node.Text), 2, "Pure function may only access its parameters: " + node.Text);
            functions.Add(name, new(names.ToArray(), body));
        }
        foreach (var function in functions.Values) ValidateCalls(function.Body, functions);
        var states = new Dictionary<string, int>(StringComparer.Ordinal);
        var depths = new Dictionary<string, int>(StringComparer.Ordinal);
        int Visit(string name)
        {
            int state = states.GetValueOrDefault(name);
            Require(state != 1, 2, "Recursive expression functions are not allowed.");
            if (state == 2) return depths[name];
            states[name] = 1;
            int depth = 1;
            foreach (var node in functions[name].Body.Nodes.Where(n => n.Kind == Kind.Call && functions.ContainsKey(n.Text)))
                depth = Math.Max(depth, Visit(node.Text) + 1);
            Require(depth <= MaximumCallDepth, 5, "Function call depth limit exceeded.");
            states[name] = 2;
            depths[name] = depth;
            return depth;
        }
        foreach (string name in functions.Keys) Visit(name);
        return functions;
    }

    private static double NumberValue(JsonNode? node)
    {
        bool numeric = false;
        double number = 0;
        if (node is JsonValue value)
        {
            if (value.TryGetValue<double>(out double d)) { number = d; numeric = true; }
            else if (value.TryGetValue<long>(out long l)) { number = l; numeric = true; }
            else if (value.TryGetValue<ulong>(out ulong u)) { number = u; numeric = true; }
            else if (value.TryGetValue<int>(out int i)) { number = i; numeric = true; }
            else if (value.TryGetValue<uint>(out uint ui)) { number = ui; numeric = true; }
            else if (value.TryGetValue<float>(out float f)) { number = f; numeric = true; }
            else if (value.TryGetValue<decimal>(out decimal dec)) { number = (double)dec; numeric = true; }
            else if (value.TryGetValue<short>(out short s)) { number = s; numeric = true; }
            else if (value.TryGetValue<ushort>(out ushort us)) { number = us; numeric = true; }
            else if (value.TryGetValue<byte>(out byte b)) { number = b; numeric = true; }
            else if (value.TryGetValue<sbyte>(out sbyte sb)) { number = sb; numeric = true; }
            else if (value.TryGetValue<JsonElement>(out var json) && json.ValueKind == JsonValueKind.Number)
            { number = json.GetDouble(); numeric = true; }
        }
        Require(numeric, 1, "Expression requires a numeric value.");
        Require(double.IsFinite(number), 1, "Expression number must be finite.");
        return number;
    }
    private static bool BooleanValue(JsonNode? value)
    {
        Require(value is JsonValue scalar && scalar.TryGetValue<bool>(out _), 1, "Expression condition must be Boolean.");
        return value!.GetValue<bool>();
    }
    private static JsonNode Finite(double value)
    {
        Require(double.IsFinite(value), 1, "Expression arithmetic overflow or domain error.");
        return JsonValue.Create(value)!;
    }
    private sealed class Evaluation(IReadOnlyDictionary<string, Function> functions)
    {
        private int steps, calls;
        internal JsonNode Run(Expression expression, int index, Func<string, JsonNode?> lookup)
        {
            Require(++steps <= MaximumSteps, 5, "Expression evaluation step limit exceeded.");
            var node = expression.Nodes[index];
            JsonNode Child(int i) => Run(expression, node.Children[i], lookup);
            switch (node.Kind)
            {
                case Kind.Number: return JsonValue.Create(node.Number)!;
                case Kind.Boolean: return JsonValue.Create(node.Boolean)!;
                case Kind.Symbol:
                    var value = lookup(node.Text);
                    if (value is JsonValue scalar && scalar.TryGetValue<bool>(out _)) return value.DeepClone();
                    try { NumberValue(value); }
                    catch (ArgumentException error) when (error.Message == "Expression requires a numeric value.")
                    { throw new ArgumentException("Unknown or non-scalar expression symbol: " + node.Text); }
                    return value!.DeepClone();
                case Kind.Unary:
                    if (node.Text == "!") return JsonValue.Create(!BooleanValue(Child(0)))!;
                    return Finite((node.Text == "-" ? -1 : 1) * NumberValue(Child(0)));
                case Kind.Conditional: return Child(BooleanValue(Child(0)) ? 1 : 2);
                case Kind.Binary:
                    var left = Child(0);
                    if (node.Text == "&&") return JsonValue.Create(BooleanValue(left) && BooleanValue(Child(1)))!;
                    if (node.Text == "||") return JsonValue.Create(BooleanValue(left) || BooleanValue(Child(1)))!;
                    var right = Child(1);
                    if (node.Text is "==" or "!=")
                    {
                        bool equal;
                        if (left is JsonValue lv && lv.TryGetValue<bool>(out bool lb) && right is JsonValue rv && rv.TryGetValue<bool>(out bool rb)) equal = lb == rb;
                        else equal = NumberValue(left) == NumberValue(right);
                        return JsonValue.Create(node.Text == "==" ? equal : !equal)!;
                    }
                    double a = NumberValue(left), b = NumberValue(right);
                    if (node.Text is "/" or "%") Require(b != 0, 1, "Expression division by zero.");
                    return node.Text switch
                    {
                        "+" => Finite(a + b), "-" => Finite(a - b), "*" => Finite(a * b),
                        "/" => Finite(a / b), "%" => Finite(a % b),
                        "<" => JsonValue.Create(a < b)!, "<=" => JsonValue.Create(a <= b)!,
                        ">" => JsonValue.Create(a > b)!, _ => JsonValue.Create(a >= b)!
                    };
                case Kind.Call:
                    var arguments = node.Children.Select(child => Run(expression, child, lookup)).ToArray();
                    if (functions.TryGetValue(node.Text, out var function))
                    {
                        Require(++calls <= MaximumCallDepth, 5, "Expression function call depth limit exceeded.");
                        JsonNode Parameters(string name)
                        {
                            int parameter = Array.IndexOf(function.Parameters, name);
                            Require(parameter >= 0, 2, "Unknown function parameter: " + name);
                            return arguments[parameter];
                        }
                        JsonNode result = Run(function.Body, function.Body.Root, Parameters);
                        --calls;
                        return result;
                    }
                    var numbers = arguments.Select(NumberValue).ToArray();
                    double x = numbers[0];
                    if (node.Text == "clamp") Require(numbers[1] <= numbers[2], 1, "Expression clamp minimum exceeds maximum.");
                    if (node.Text == "sqrt") Require(x >= 0, 1, "Expression square root requires a nonnegative number.");
                    return Finite(node.Text switch
                    {
                        "abs" => Math.Abs(x), "min" => numbers[1] < x ? numbers[1] : x,
                        "max" => x < numbers[1] ? numbers[1] : x, "clamp" => Math.Clamp(x, numbers[1], numbers[2]),
                        "sqrt" => Math.Sqrt(x), "pow" => Math.Pow(x, numbers[1]),
                        "round" => Math.Round(x, MidpointRounding.AwayFromZero),
                        "floor" => Math.Floor(x), "ceil" => Math.Ceiling(x), "sin" => Math.Sin(x),
                        "cos" => Math.Cos(x), "tan" => Math.Tan(x), "atan2" => Math.Atan2(x, numbers[1]),
                        _ => double.Hypot(x, numbers[1])
                    });
            }
            throw new VisionException(6, "Unknown expression node.");
        }
    }
}
