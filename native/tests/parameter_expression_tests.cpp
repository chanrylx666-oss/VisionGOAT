#include "../core/parameter_expression.h"
#include <cmath>
#include <iostream>
#include <limits>

namespace {
int checks = 0;
void expect(bool condition, const std::string &message) {
    ++checks;
    if (!condition)
        throw std::runtime_error(message);
}
template <typename Action> void error(int code, Action action, const std::string &message) {
    ++checks;
    try {
        action();
    } catch (const jp::Error &failure) {
        if (failure.code == code)
            return;
        throw std::runtime_error(message + ": wrong error " + std::to_string(failure.code) + " " +
                                 failure.what());
    }
    throw std::runtime_error(message + ": unexpectedly accepted");
}
using jp::Json;
Json empty = Json::object();
Json no_symbol(const std::string &) {
    return nullptr;
}
Json evaluate(const std::string &expression, const Json &functions = Json::object()) {
    return jp::evaluate_expression(expression, no_symbol, functions);
}
void numeric(const std::string &expression, double expected) {
    auto value = evaluate(expression);
    expect(value.is_number() && std::abs(value.get<double>() - expected) <= 1e-10,
           "Wrong numeric result: " + expression);
}
void boolean(const std::string &expression, bool expected) {
    auto value = evaluate(expression);
    expect(value.is_boolean() && value.get<bool>() == expected, "Wrong Boolean result: " + expression);
}
} // namespace

int main() {
    try {
        numeric("1 + 2 * 3", 7);
        numeric("(1 + 2) * 3", 9);
        numeric("20 / 5 / 2", 2);
        numeric("10 - 3 - 2", 5);
        numeric("-2 * +3 + .5 + 1.5e1", 9.5);
        numeric("17 % 5", 2);
        numeric("abs(-3) + min(4, 5) + max(2, 7)", 14);
        numeric("clamp(100, 2, 10)", 10);
        numeric("sqrt(9) + pow(2, 3)", 11);
        numeric("round(-1.5) + floor(2.9) + ceil(3.1)", 4);
        numeric("sin(pi / 2) + cos(0) + tan(0)", 2);
        numeric("atan2(0, 1) + hypot(3, 4)", 5);
        numeric("e - e", 0);
        boolean("1 < 2 && 3 <= 3 && 5 > 4 && 4 >= 4", true);
        boolean("!false || false", true);
        boolean("1 + 1 == 2 && 3 != 4", true);
        boolean("true == true && false != true", true);
        numeric("false ? 1 / 0 : 2", 2);
        numeric("true ? 3 : sqrt(-1)", 3);
        numeric("false ? 1 : true ? 2 : 3", 2);
        boolean("false && (1 / 0 > 0)", false);
        boolean("true || (sqrt(-1) > 0)", true);
        auto symbols = jp::expression_symbols("centerX + global.OffsetX + local.shift + centerX", empty);
        expect(symbols == std::set<std::string>{"centerX", "global.OffsetX", "local.shift"},
               "Symbol discovery must include distinct external names.");
        std::string longName(64, 'a');
        expect(jp::expression_symbols(longName + "." + longName, empty) ==
                   std::set<std::string>{longName + "." + longName},
               "Two 64-character symbol segments must remain valid.");
        error(
            JPV_RESOURCE_LIMIT, [&] { jp::expression_symbols(longName + "a", empty); },
            "Symbol segment length bounded");
        int lookups = 0;
        auto resolved = jp::evaluate_expression(
            "x + global.OffsetX + local.shift",
            [&](const std::string &name) -> Json {
                ++lookups;
                if (name == "x")
                    return 20;
                if (name == "global.OffsetX")
                    return 3;
                if (name == "local.shift")
                    return 0.5;
                return nullptr;
            },
            empty);
        expect(resolved == 23.5 && lookups == 3, "Lookup must resolve exact qualified names.");
        lookups = 0;
        auto lazy = jp::evaluate_expression(
            "true ? 2 : missing",
            [&](const std::string &) -> Json {
                ++lookups;
                return nullptr;
            },
            empty);
        expect(lazy == 2 && lookups == 0, "Unselected conditional branch must not resolve missing data.");

        Json functions{{"offset", {{"parameters", {"x", "dx"}}, {"expression", "x + dx"}}},
                       {"adjust", {{"parameters", {"x"}}, {"expression", "offset(x, 5) * 2"}}},
                       {"positive", {{"parameters", {"x"}}, {"expression", "x > 0"}}},
                       {"guarded", {{"parameters", {"x"}}, {"expression", "x == 0 ? 0 : 1 / x"}}}};
        jp::validate_functions(functions);
        expect(evaluate("adjust(10)", functions) == 30,
               "Nonrecursive custom function composition must work.");
        expect(evaluate("positive(-2)", functions) == false, "Custom functions may return Boolean values.");
        expect(evaluate("guarded(0)", functions) == 0,
               "Pure function validation must not evaluate dummy inputs.");
        expect(jp::expression_symbols("adjust(x) + offset(global.X, 2)", functions) ==
                   std::set<std::string>{"global.X", "x"},
               "Function formal parameters must not appear as external dependencies.");

        for (const auto &text :
             {"", " ", "1 +", "(1 + 2", "1; 2", "1 = 2", "1 & 2", "1 | 2", "\"text\"", "x.y.z", "global.",
              "a[0]", "1e+", "1e999", "foo(1)", "sqrt(1, 2)", "min(1)", "global.method(1)", "sin(1,)"})
            error(
                JPV_INVALID_RECIPE, [&] { evaluate(text); },
                "Invalid syntax must be rejected: " + std::string(text));
        for (const auto &text : {"1 / 0", "1 % 0", "sqrt(-1)", "pow(-1, .5)", "pow(1e200, 2)",
                                 "1e308 * 1e308", "true + 1", "-false", "!1", "1 && true", "false || 1",
                                 "1 ? 2 : 3", "true == 1", "clamp(1, 5, 2)", "missing"})
            error(
                JPV_INVALID_ARGUMENT, [&] { evaluate(text); },
                "Invalid numeric use must fail: " + std::string(text));
        error(
            JPV_INVALID_ARGUMENT,
            [&] { jp::evaluate_expression("value", [](const std::string &) -> Json { return "1"; }, empty); },
            "String lookups cannot be numeric");
        error(
            JPV_INVALID_ARGUMENT,
            [&] {
                jp::evaluate_expression(
                    "value",
                    [](const std::string &) -> Json { return std::numeric_limits<double>::infinity(); },
                    empty);
            },
            "Nonfinite lookups must fail");
        for (const auto &definition :
             {Json::array(), Json{{"bad.name", {{"parameters", Json::array()}, {"expression", "1"}}}},
              Json{{"sqrt", {{"parameters", {"x"}}, {"expression", "x"}}}},
              Json{{"f", {{"parameters", {"x", "x"}}, {"expression", "x"}}}},
              Json{{"f", {{"parameters", {"pi"}}, {"expression", "pi"}}}},
              Json{{"f", {{"parameters", {"x"}}, {"expression", "global.X + x"}}}},
              Json{{"f", {{"parameters", {"x"}}, {"expression", "local.x + x"}}}},
              Json{{"f", {{"parameters", {"x"}}, {"expression", "other + x"}}}},
              Json{{"f", {{"parameters", {"x"}}, {"expression", "missing(x)"}}}},
              Json{{"f", {{"parameters", {"x"}}, {"expression", "f(x)"}}}},
              Json{{"f", {{"parameters", {"x"}}, {"expression", "g(x)"}}},
                   {"g", {{"parameters", {"x"}}, {"expression", "f(x)"}}}},
              Json{{"f", {{"parameters", {"x"}}, {"expression", "true ? x : f(x)"}}}},
              Json{{"f", {{"parameters", {"x"}}, {"expression", "x"}, {"unknown", 0}}}}})
            error(
                JPV_INVALID_RECIPE, [&] { jp::validate_functions(definition); }, "Unsafe function rejected");
        error(JPV_RESOURCE_LIMIT, [&] { evaluate(std::string(2049, ' ')); }, "Expression length bounded");
        error(JPV_RESOURCE_LIMIT, [&] { evaluate(std::string(33, '-') + "1"); }, "Unary depth bounded");
        error(
            JPV_RESOURCE_LIMIT, [&] { evaluate(std::string(33, '(') + "1" + std::string(33, ')')); },
            "Nesting depth bounded");
        std::string deep = "1";
        for (int i = 0; i < 33; ++i)
            deep += " + 1";
        error(JPV_RESOURCE_LIMIT, [&] { evaluate(deep); }, "Binary AST depth bounded");
        std::string huge = "1";
        for (int i = 0; i < 8; ++i)
            huge = "(" + huge + ")+ (" + huge + ")";
        // Build a balanced 511-node tree shorter than the character limit.
        error(JPV_RESOURCE_LIMIT, [&] { evaluate(huge.substr(0, 2048)); }, "AST node count bounded");
        Json many = Json::object();
        for (int i = 0; i < 65; ++i)
            many["f" + std::to_string(i)] = {{"parameters", Json::array()}, {"expression", "1"}};
        error(JPV_RESOURCE_LIMIT, [&] { jp::validate_functions(many); }, "Function count bounded");
        Json chain = Json::object();
        for (int i = 0; i < 17; ++i)
            chain["f" + std::to_string(i)] = {
                {"parameters", {"x"}}, {"expression", i == 16 ? "x" : "f" + std::to_string(i + 1) + "(x)"}};
        error(JPV_RESOURCE_LIMIT, [&] { jp::validate_functions(chain); }, "Custom call depth bounded");
        Json work = Json::object();
        for (int i = 0; i < 13; ++i)
            work["f" + std::to_string(i)] = {
                {"parameters", {"x"}},
                {"expression",
                 i == 12 ? "x" : "f" + std::to_string(i + 1) + "(x) + f" + std::to_string(i + 1) + "(x)"}};
        error(JPV_RESOURCE_LIMIT, [&] { evaluate("f0(1)", work); }, "Evaluation total work bounded");
        std::cout << "Parameter expressions: " << checks << " checks passed.\n";
        return 0;
    } catch (const std::exception &error) {
        std::cerr << "Parameter expression check failed: " << error.what() << '\n';
        return 1;
    }
}
