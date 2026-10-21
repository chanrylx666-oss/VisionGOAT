#include "parameter_expression.h"
#include <algorithm>
#include <charconv>
#include <cmath>
#include <map>
#include <numbers>
#include <optional>
#include <string_view>
#include <utility>

namespace jp {
namespace {
constexpr size_t MaximumExpressionLength = 2048;
constexpr size_t MaximumNodes = 256;
constexpr size_t MaximumDepth = 32;
constexpr size_t MaximumSteps = 4096;
constexpr size_t MaximumCallDepth = 16;

bool identifier_start(char c) {
    return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_';
}
bool identifier_continue(char c) {
    return identifier_start(c) || (c >= '0' && c <= '9');
}
bool simple_identifier(const std::string &value) {
    return !value.empty() && value.size() <= 64 && identifier_start(value.front()) &&
           std::all_of(value.begin() + 1, value.end(), identifier_continue);
}
const std::map<std::string, size_t> &builtins() {
    static const std::map<std::string, size_t> values{
        {"abs", 1},   {"min", 2},  {"max", 2}, {"clamp", 3}, {"sqrt", 1}, {"pow", 2},   {"round", 1},
        {"floor", 1}, {"ceil", 1}, {"sin", 1}, {"cos", 1},   {"tan", 1},  {"atan2", 2}, {"hypot", 2}};
    return values;
}
bool reserved_name(const std::string &name) {
    return name == "pi" || name == "e" || name == "true" || name == "false" || name == "global" ||
           name == "local" || builtins().contains(name);
}
enum class Kind { Number, Boolean, Symbol, Unary, Binary, Conditional, Call };
struct Node {
    Kind kind;
    std::string text;
    std::vector<size_t> children;
    double number = 0;
    bool boolean = false;
    size_t depth = 1;
};
struct Expression {
    std::vector<Node> nodes;
    size_t root = 0;
};
class Parser {
    std::string_view source;
    size_t position = 0, nesting = 0, unaryNesting = 0;
    Expression result;

    [[noreturn]] void invalid(const std::string &message) const {
        throw Error(JPV_INVALID_RECIPE, "Expression at " + std::to_string(position + 1) + ": " + message);
    }
    void whitespace() {
        while (position < source.size() && (source[position] == ' ' || source[position] == '\t' ||
                                            source[position] == '\r' || source[position] == '\n'))
            ++position;
    }
    bool accept(std::string_view text) {
        whitespace();
        if (source.substr(position, text.size()) != text)
            return false;
        position += text.size();
        return true;
    }
    void expect(std::string_view text) {
        if (!accept(text))
            invalid("Expected '" + std::string(text) + "'.");
    }
    size_t add(Node node) {
        require(result.nodes.size() < MaximumNodes, JPV_RESOURCE_LIMIT, "Expression node limit exceeded.");
        for (auto child : node.children)
            node.depth = std::max(node.depth, result.nodes[child].depth + 1);
        require(node.depth <= MaximumDepth, JPV_RESOURCE_LIMIT, "Expression depth limit exceeded.");
        result.nodes.push_back(std::move(node));
        return result.nodes.size() - 1;
    }
    size_t conditional() {
        require(++nesting <= MaximumDepth, JPV_RESOURCE_LIMIT, "Expression nesting limit exceeded.");
        size_t condition = logical_or();
        if (accept("?")) {
            size_t yes = conditional();
            expect(":");
            size_t no = conditional();
            condition = add({Kind::Conditional, "", {condition, yes, no}});
        }
        --nesting;
        return condition;
    }
    template <typename Next> size_t binary(Next next, std::initializer_list<std::string_view> operators) {
        size_t left = (this->*next)();
        while (true) {
            std::optional<std::string_view> operation;
            for (auto candidate : operators)
                if (accept(candidate)) {
                    operation = candidate;
                    break;
                }
            if (!operation)
                return left;
            size_t right = (this->*next)();
            left = add({Kind::Binary, std::string(*operation), {left, right}});
        }
    }
    size_t logical_or() {
        return binary(&Parser::logical_and, {"||"});
    }
    size_t logical_and() {
        return binary(&Parser::equality, {"&&"});
    }
    size_t equality() {
        return binary(&Parser::comparison, {"==", "!="});
    }
    size_t comparison() {
        return binary(&Parser::sum, {"<=", ">=", "<", ">"});
    }
    size_t sum() {
        return binary(&Parser::product, {"+", "-"});
    }
    size_t product() {
        return binary(&Parser::unary, {"*", "/", "%"});
    }
    size_t unary() {
        require(++unaryNesting <= MaximumDepth, JPV_RESOURCE_LIMIT, "Expression unary depth limit exceeded.");
        size_t value;
        if (accept("!"))
            value = add({Kind::Unary, "!", {unary()}});
        else if (accept("+"))
            value = add({Kind::Unary, "+", {unary()}});
        else if (accept("-"))
            value = add({Kind::Unary, "-", {unary()}});
        else
            value = primary();
        --unaryNesting;
        return value;
    }
    size_t number() {
        whitespace();
        size_t start = position;
        bool digits = false;
        while (position < source.size() && source[position] >= '0' && source[position] <= '9') {
            ++position;
            digits = true;
        }
        if (position < source.size() && source[position] == '.') {
            ++position;
            while (position < source.size() && source[position] >= '0' && source[position] <= '9') {
                ++position;
                digits = true;
            }
        }
        if (!digits)
            invalid("Expected a number.");
        if (position < source.size() && (source[position] == 'e' || source[position] == 'E')) {
            ++position;
            if (position < source.size() && (source[position] == '+' || source[position] == '-'))
                ++position;
            size_t exponent = position;
            while (position < source.size() && source[position] >= '0' && source[position] <= '9')
                ++position;
            if (position == exponent)
                invalid("Invalid exponent.");
        }
        double value = 0;
        auto parsed = std::from_chars(source.data() + start, source.data() + position, value);
        if (parsed.ec != std::errc{} || parsed.ptr != source.data() + position || !std::isfinite(value))
            invalid("Number must be finite and representable.");
        return add({Kind::Number, "", {}, value});
    }
    std::string identifier() {
        whitespace();
        size_t start = position;
        if (position >= source.size() || !identifier_start(source[position]))
            invalid("Expected a symbol.");
        while (position < source.size() && identifier_continue(source[position]))
            ++position;
        require(position - start <= 64, JPV_RESOURCE_LIMIT, "Expression symbol segment is too long.");
        if (position < source.size() && source[position] == '.') {
            ++position;
            size_t member = position;
            if (position >= source.size() || !identifier_start(source[position]))
                invalid("Expected a member name.");
            while (position < source.size() && identifier_continue(source[position]))
                ++position;
            require(position - member <= 64, JPV_RESOURCE_LIMIT, "Expression symbol segment is too long.");
        }
        return std::string(source.substr(start, position - start));
    }
    size_t primary() {
        if (accept("(")) {
            size_t value = conditional();
            expect(")");
            return value;
        }
        whitespace();
        if (position >= source.size())
            invalid("Expected a value.");
        char c = source[position];
        if ((c >= '0' && c <= '9') || c == '.')
            return number();
        std::string name = identifier();
        if (accept("(")) {
            if (name.find('.') != std::string::npos)
                invalid("Function names cannot contain a member access.");
            std::vector<size_t> arguments;
            if (!accept(")")) {
                do {
                    require(arguments.size() < 16, JPV_RESOURCE_LIMIT,
                            "Expression call argument limit exceeded.");
                    arguments.push_back(conditional());
                } while (accept(","));
                expect(")");
            }
            return add({Kind::Call, std::move(name), std::move(arguments)});
        }
        if (name == "true" || name == "false")
            return add({Kind::Boolean, "", {}, 0, name == "true"});
        if (name == "pi" || name == "e")
            return add({Kind::Number, "", {}, name == "pi" ? std::numbers::pi : std::numbers::e});
        return add({Kind::Symbol, std::move(name), {}});
    }

  public:
    explicit Parser(const std::string &expression) : source(expression) {
        require(!expression.empty(), JPV_INVALID_RECIPE, "Expression cannot be empty.");
        require(expression.size() <= MaximumExpressionLength, JPV_RESOURCE_LIMIT,
                "Expression length limit exceeded.");
    }
    Expression parse() {
        result.root = conditional();
        whitespace();
        if (position != source.size())
            invalid("Unexpected token.");
        return std::move(result);
    }
};
struct Function {
    std::vector<std::string> parameters;
    Expression body;
};
using Functions = std::map<std::string, Function>;

void validate_calls(const Expression &expression, const Functions &functions) {
    for (const auto &node : expression.nodes) {
        if (node.kind != Kind::Call)
            continue;
        auto builtin = builtins().find(node.text);
        auto custom = functions.find(node.text);
        require(builtin != builtins().end() || custom != functions.end(), JPV_INVALID_RECIPE,
                "Unknown expression function: " + node.text);
        size_t arity = builtin != builtins().end() ? builtin->second : custom->second.parameters.size();
        require(node.children.size() == arity, JPV_INVALID_RECIPE,
                "Wrong argument count for expression function: " + node.text);
    }
}
Functions compile_functions(const Json &definitions) {
    require(definitions.is_object(), JPV_INVALID_RECIPE, "Functions must be an object.");
    require(definitions.size() <= 64, JPV_RESOURCE_LIMIT, "Function count limit exceeded.");
    Functions functions;
    for (const auto &[name, definition] : definitions.items()) {
        require(simple_identifier(name) && !reserved_name(name), JPV_INVALID_RECIPE,
                "Invalid or reserved function name: " + name);
        require(definition.is_object() && definition.size() == 2 && definition.contains("parameters") &&
                    definition.contains("expression") && definition["parameters"].is_array() &&
                    definition["expression"].is_string(),
                JPV_INVALID_RECIPE, "Function requires parameters and expression: " + name);
        require(definition["parameters"].size() <= 16, JPV_RESOURCE_LIMIT,
                "Function parameter count limit exceeded.");
        Function function;
        std::set<std::string> unique;
        for (const auto &parameter : definition["parameters"]) {
            require(parameter.is_string(), JPV_INVALID_RECIPE, "Function parameter name must be a string.");
            std::string key = parameter.get<std::string>();
            require(simple_identifier(key) && !reserved_name(key) && unique.insert(key).second,
                    JPV_INVALID_RECIPE, "Invalid, duplicate, or reserved function parameter: " + key);
            function.parameters.push_back(key);
        }
        function.body = Parser(definition["expression"].get<std::string>()).parse();
        for (const auto &node : function.body.nodes)
            if (node.kind == Kind::Symbol)
                require(unique.contains(node.text), JPV_INVALID_RECIPE,
                        "Pure function may only access its parameters: " + node.text);
        functions.emplace(name, std::move(function));
    }
    for (const auto &[name, function] : functions)
        validate_calls(function.body, functions);
    std::map<std::string, int> states;
    std::map<std::string, size_t> depths;
    std::function<size_t(const std::string &)> visit = [&](const std::string &name) -> size_t {
        require(states[name] != 1, JPV_INVALID_RECIPE, "Recursive expression functions are not allowed.");
        if (states[name] == 2)
            return depths.at(name);
        states[name] = 1;
        size_t depth = 1;
        for (const auto &node : functions.at(name).body.nodes)
            if (node.kind == Kind::Call && functions.contains(node.text))
                depth = std::max(depth, visit(node.text) + 1);
        require(depth <= MaximumCallDepth, JPV_RESOURCE_LIMIT, "Function call depth limit exceeded.");
        states[name] = 2;
        depths[name] = depth;
        return depth;
    };
    for (const auto &[name, function] : functions)
        visit(name);
    return functions;
}
double number_value(const Json &value) {
    require(value.is_number(), JPV_INVALID_ARGUMENT, "Expression requires a numeric value.");
    double result = value.get<double>();
    require(std::isfinite(result), JPV_INVALID_ARGUMENT, "Expression number must be finite.");
    return result;
}
bool boolean_value(const Json &value) {
    require(value.is_boolean(), JPV_INVALID_ARGUMENT, "Expression condition must be Boolean.");
    return value.get<bool>();
}
Json finite(double value) {
    require(std::isfinite(value), JPV_INVALID_ARGUMENT, "Expression arithmetic overflow or domain error.");
    return value;
}
struct Evaluation {
    const Functions &functions;
    size_t steps = 0, calls = 0;

    Json run(const Expression &expression, size_t index,
             const std::function<Json(const std::string &)> &lookup) {
        require(++steps <= MaximumSteps, JPV_RESOURCE_LIMIT, "Expression evaluation step limit exceeded.");
        const Node &node = expression.nodes[index];
        auto child = [&](size_t i) { return run(expression, node.children[i], lookup); };
        switch (node.kind) {
        case Kind::Number:
            return node.number;
        case Kind::Boolean:
            return node.boolean;
        case Kind::Symbol: {
            Json value = lookup(node.text);
            require(value.is_number() || value.is_boolean(), JPV_INVALID_ARGUMENT,
                    "Unknown or non-scalar expression symbol: " + node.text);
            if (value.is_number())
                number_value(value);
            return value;
        }
        case Kind::Unary:
            if (node.text == "!")
                return !boolean_value(child(0));
            return finite((node.text == "-" ? -1 : 1) * number_value(child(0)));
        case Kind::Conditional:
            return child(boolean_value(child(0)) ? 1 : 2);
        case Kind::Binary: {
            Json left = child(0);
            if (node.text == "&&")
                return boolean_value(left) && boolean_value(child(1));
            if (node.text == "||")
                return boolean_value(left) || boolean_value(child(1));
            Json right = child(1);
            if (node.text == "==" || node.text == "!=") {
                bool equal;
                if (left.is_boolean() && right.is_boolean())
                    equal = left.get<bool>() == right.get<bool>();
                else
                    equal = number_value(left) == number_value(right);
                return node.text == "==" ? equal : !equal;
            }
            double a = number_value(left), b = number_value(right);
            if (node.text == "+")
                return finite(a + b);
            if (node.text == "-")
                return finite(a - b);
            if (node.text == "*")
                return finite(a * b);
            if (node.text == "/" || node.text == "%") {
                require(b != 0, JPV_INVALID_ARGUMENT, "Expression division by zero.");
                return finite(node.text == "/" ? a / b : std::fmod(a, b));
            }
            if (node.text == "<")
                return a < b;
            if (node.text == "<=")
                return a <= b;
            if (node.text == ">")
                return a > b;
            return a >= b;
        }
        case Kind::Call: {
            std::vector<Json> arguments;
            for (auto argument : node.children)
                arguments.push_back(run(expression, argument, lookup));
            auto custom = functions.find(node.text);
            if (custom != functions.end()) {
                require(++calls <= MaximumCallDepth, JPV_RESOURCE_LIMIT,
                        "Expression function call depth limit exceeded.");
                const auto &function = custom->second;
                auto parameters = [&](const std::string &name) -> Json {
                    auto found = std::find(function.parameters.begin(), function.parameters.end(), name);
                    require(found != function.parameters.end(), JPV_INVALID_RECIPE,
                            "Unknown function parameter: " + name);
                    return arguments[static_cast<size_t>(found - function.parameters.begin())];
                };
                Json value = run(function.body, function.body.root, parameters);
                --calls;
                return value;
            }
            std::vector<double> numbers;
            for (const auto &argument : arguments)
                numbers.push_back(number_value(argument));
            const auto &name = node.text;
            double a = numbers[0];
            if (name == "abs")
                return finite(std::abs(a));
            if (name == "min")
                return std::min(a, numbers[1]);
            if (name == "max")
                return std::max(a, numbers[1]);
            if (name == "clamp") {
                require(numbers[1] <= numbers[2], JPV_INVALID_ARGUMENT,
                        "Expression clamp minimum exceeds maximum.");
                return std::clamp(a, numbers[1], numbers[2]);
            }
            if (name == "sqrt") {
                require(a >= 0, JPV_INVALID_ARGUMENT,
                        "Expression square root requires a nonnegative number.");
                return finite(std::sqrt(a));
            }
            if (name == "pow")
                return finite(std::pow(a, numbers[1]));
            if (name == "round")
                return finite(std::round(a));
            if (name == "floor")
                return finite(std::floor(a));
            if (name == "ceil")
                return finite(std::ceil(a));
            if (name == "sin")
                return finite(std::sin(a));
            if (name == "cos")
                return finite(std::cos(a));
            if (name == "tan")
                return finite(std::tan(a));
            if (name == "atan2")
                return finite(std::atan2(a, numbers[1]));
            return finite(std::hypot(a, numbers[1]));
        }
        }
        throw Error(JPV_INTERNAL_ERROR, "Unknown expression node.");
    }
};
} // namespace

std::set<std::string> expression_symbols(const std::string &expression, const Json &definitions) {
    auto functions = compile_functions(definitions);
    auto parsed = Parser(expression).parse();
    validate_calls(parsed, functions);
    std::set<std::string> result;
    for (const auto &node : parsed.nodes)
        if (node.kind == Kind::Symbol)
            result.insert(node.text);
    return result;
}
void validate_functions(const Json &functions) {
    compile_functions(functions);
}
Json evaluate_expression(const std::string &expression,
                         const std::function<Json(const std::string &)> &lookup, const Json &definitions) {
    auto functions = compile_functions(definitions);
    auto parsed = Parser(expression).parse();
    validate_calls(parsed, functions);
    Evaluation evaluation{functions};
    return evaluation.run(parsed, parsed.root, lookup);
}
} // namespace jp
