#include "parameter_bindings.h"
#include "components/component.h"
#include "parameter_expression.h"
#include <algorithm>
#include <cmath>
#include <cctype>
#include <set>

namespace jp {
namespace {
bool identifier(const std::string &name) {
    if (name.empty() || name.size() > 64 ||
        !(std::isalpha(static_cast<unsigned char>(name[0])) || name[0] == '_'))
        return false;
    return std::all_of(name.begin(), name.end(),
                       [](unsigned char c) { return c < 128 && (std::isalnum(c) || c == '_'); });
}
bool binding(const Json &value) {
    return value.is_object() && (value.contains("node") || value.contains("input") ||
                                 value.contains("variable") || value.contains("expression"));
}
std::string scalar_type(const Json &value) {
    return value.is_boolean()          ? "Bool"
           : value.is_number_integer() ? "Int"
           : value.is_number()         ? "Double"
           : value.is_string()         ? "String"
                                       : "";
}
Json checked_scalar(Json value, const std::string &type, const std::string &label, int code) {
    if (type == "Int" && value.is_number_float()) {
        double number = value.get<double>();
        require(std::isfinite(number) && std::trunc(number) == number &&
                    std::abs(number) <= 9007199254740991.0,
                code, "Integer variable/parameter requires an exact integer: " + label);
        value = static_cast<int64_t>(number);
    }
    require((type == "Int" && value.is_number_integer()) ||
                (type == "Double" && value.is_number() && std::isfinite(value.get<double>())) ||
                (type == "Bool" && value.is_boolean()) ||
                (type == "String" && value.is_string() && value.get<std::string>().size() <= 256),
            code, "Variable/parameter type mismatch: " + label);
    return value;
}
void variable_definitions(const Json &definitions, size_t limit, bool literal) {
    require(definitions.is_object() && definitions.size() <= limit, JPV_INVALID_RECIPE,
            "Variable table is invalid or too large.");
    for (auto it = definitions.begin(); it != definitions.end(); ++it) {
        const auto &definition = it.value();
        require(identifier(it.key()) && definition.is_object() && definition.size() == 2 &&
                    definition.contains("type") && definition.at("type").is_string() &&
                    definition.contains("value"),
                JPV_INVALID_RECIPE, "Variable requires a valid name, type and value.");
        auto type = definition.at("type").get<std::string>();
        require(type == "Int" || type == "Double" || type == "Bool" || type == "String", JPV_INVALID_RECIPE,
                "Unsupported variable type.");
        if (literal || !binding(definition.at("value")))
            checked_scalar(definition.at("value"), type, it.key(), JPV_INVALID_RECIPE);
    }
}
Json field_definition(const Json &meta, const std::string &port, const std::string &field) {
    auto found = std::find_if(meta.at("outputs").begin(), meta.at("outputs").end(),
                              [&](const Json &p) { return p.at("id") == port; });
    require(found != meta.at("outputs").end(), JPV_INVALID_RECIPE, "Unknown binding output port: " + port);
    std::string type = found->at("type");
    if (field.empty() && (type == "Int" || type == "Double" || type == "Bool" || type == "String"))
        return {{"type", type}, {"nullable", false}};
    require(field.size() <= 128 && meta.contains("fields") && meta.at("fields").contains(port) &&
                meta.at("fields").at(port).contains(field),
            JPV_INVALID_RECIPE, "Unknown scalar result field: " + port + "." + field);
    return meta.at("fields").at(port).at(field);
}
struct Frame {
    std::string space, plane;
};
void merge_frames(std::vector<Frame> &target, const std::vector<Frame> &source) {
    for (const auto &frame : source) {
        if (std::none_of(target.begin(), target.end(), [&](const Frame &known) {
                return known.space == frame.space && known.plane == frame.plane;
            })) {
            require(target.size() < 64, JPV_RESOURCE_LIMIT, "Expression coordinate plane limit exceeded.");
            target.push_back(frame);
        }
    }
}
struct Scalar {
    Json value;
    std::string type;
    std::vector<Frame> frames;
    bool position = false, unavailable = false, deferred = false;
};
std::string leaf_name(const std::string &field) {
    auto dot = field.find_last_of('.');
    return field.substr(dot == std::string::npos ? 0 : dot + 1);
}
bool position_field(const std::string &field) {
    auto name = leaf_name(field);
    return name == "x" || name == "y" || name == "x1" || name == "y1" || name == "x2" || name == "y2" ||
           name == "centerX" || name == "centerY" || name == "centroidX" || name == "centroidY";
}
bool geometric_field(const std::string &field) {
    auto name = leaf_name(field);
    return position_field(field) || name == "radius" || name == "diameter" || name == "length" ||
           name == "width" || name == "height" || name == "innerRadius" || name == "outerRadius";
}
void dependency_walk(const Json &value, const std::string &path,
                     std::map<std::string, std::pair<std::string, std::string>> &result, size_t depth = 0) {
    require(depth <= 40, JPV_RESOURCE_LIMIT, "Parameter binding nesting is too deep.");
    if (value.is_object()) {
        if (value.contains("node") && value.at("node").is_string() && value.contains("port") &&
            value.at("port").is_string())
            result[path] = {value.at("node"), value.at("port")};
        else
            for (auto it = value.begin(); it != value.end(); ++it)
                dependency_walk(it.value(), path + "." + it.key(), result, depth + 1);
    } else if (value.is_array())
        for (size_t i = 0; i < value.size(); ++i)
            dependency_walk(value[i], path + "." + std::to_string(i), result, depth + 1);
}
class Resolver {
    const Json &node, &publicValues, &globals;
    const Plan &plan;
    const std::map<std::string, PortValues> *values;
    const Json *traces;
    const Image *input;
    Json functions, definitions;
    std::map<std::string, Scalar> locals;
    std::set<std::string> visiting;
    std::set<std::string> geometricRois;
    size_t steps = 0;

  public:
    bool deferred = false;
    Json unavailable = Json::array();
    Resolver(const Json &n, const Json &p, const Plan &pl, const std::map<std::string, PortValues> *v,
             const Json *t, const Image *i, const Json &g)
        : node(n), publicValues(p), globals(g), plan(pl), values(v), traces(t), input(i),
          functions(pl.recipe.value("functions", Json::object())),
          definitions(n.value("variables", Json::object())) {
        variable_definitions(definitions, 32, false);
    }
    Scalar local(const std::string &name) {
        if (locals.contains(name))
            return locals.at(name);
        require(definitions.contains(name), JPV_INVALID_RECIPE, "Unknown local variable: " + name);
        require(visiting.insert(name).second, JPV_INVALID_RECIPE, "Local variable dependency cycle: " + name);
        const auto &definition = definitions.at(name);
        auto value = resolve(definition.at("value"));
        std::string type = definition.at("type");
        if (!value.deferred && !value.unavailable)
            value.value = checked_scalar(value.value, type, name, JPV_INVALID_RECIPE);
        else
            value.value = type == "Bool" ? Json(false) : type == "String" ? Json("") : Json(0);
        value.type = type;
        visiting.erase(name);
        locals[name] = value;
        return value;
    }
    Scalar symbol(const std::string &name, const Json &arguments) {
        if (name.starts_with("global.")) {
            auto key = name.substr(7);
            require(globals.contains(key), JPV_INVALID_RECIPE, "Unknown global variable: " + key);
            return {globals.at(key), scalar_type(globals.at(key))};
        }
        if (name.starts_with("local."))
            return local(name.substr(6));
        require(arguments.contains(name), JPV_INVALID_RECIPE, "Unknown expression argument: " + name);
        return resolve(arguments.at(name));
    }
    Scalar resolve(const Json &definition, size_t depth = 0) {
        require(++steps <= 8192 && depth <= 32, JPV_RESOURCE_LIMIT,
                "Parameter binding evaluation budget exceeded.");
        if (!binding(definition))
            return {definition, scalar_type(definition)};
        std::set<std::string> allowed{"fallback"};
        Scalar result;
        if (definition.contains("node")) {
            allowed.insert({"node", "port", "field"});
            require(definition.contains("port") && definition.contains("field") &&
                        definition.at("node").is_string() && definition.at("port").is_string() &&
                        definition.at("field").is_string(),
                    JPV_INVALID_RECIPE, "Invalid output field subscription.");
            std::string source = definition.at("node"), port = definition.at("port"),
                        field = definition.at("field");
            require(source != node.at("id").get<std::string>() && plan.nodes.contains(source),
                    JPV_INVALID_RECIPE, "Parameter binding refers to missing/self node.");
            auto fieldSpec = field_definition(
                component_registry().get(plan.nodes.at(source).at("type")).descriptor, port, field);
            result.type = fieldSpec.at("type");
            if (!values)
                result.deferred = true;
            else {
                bool skipped = traces && traces->at(source).at("status") == "Skipped";
                const auto &sourceValue = values->at(source).at(port);
                result.value = skipped ? Json() : result_field(sourceValue, field);
                if (fieldSpec.contains("validWhen") &&
                    result_field(sourceValue, fieldSpec.at("validWhen")) != true)
                    result.value = nullptr;
                if (result.value.is_null()) {
                    require(skipped || fieldSpec.value("nullable", false), JPV_INTERNAL_ERROR,
                            "Required scalar result field is missing: " + field);
                    result.unavailable = true;
                } else if (geometric_field(field)) {
                    auto space = result_field(sourceValue, "coordinateSpace"),
                         plane = result_field(sourceValue, "coordinatePlane");
                    if (space.is_string()) {
                        result.frames.push_back(
                            {space.get<std::string>(), plane.is_string() ? plane.get<std::string>() : ""});
                        result.position = position_field(field);
                    }
                }
            }
        } else if (definition.contains("input")) {
            allowed.insert("input");
            require(definition.at("input").is_string() &&
                        publicValues.contains(definition.at("input").get<std::string>()),
                    JPV_INVALID_RECIPE, "Unknown public scalar input binding.");
            result.value = publicValues.at(definition.at("input").get<std::string>());
            result.type = scalar_type(result.value);
        } else if (definition.contains("variable")) {
            allowed.insert({"variable", "scope"});
            require(definition.at("variable").is_string() && definition.contains("scope") &&
                        definition.at("scope").is_string(),
                    JPV_INVALID_RECIPE, "Variable binding requires name and scope.");
            std::string name = definition.at("variable"), scope = definition.at("scope");
            require(identifier(name) && (scope == "global" || scope == "local"), JPV_INVALID_RECIPE,
                    "Invalid variable subscription.");
            result = symbol((scope == "global" ? "global." : "local.") + name, Json::object());
        } else {
            allowed.insert({"expression", "arguments"});
            require(definition.at("expression").is_string(), JPV_INVALID_RECIPE,
                    "Expression must be a string.");
            const auto arguments = definition.value("arguments", Json::object());
            require(arguments.is_object() && arguments.size() <= 32, JPV_INVALID_RECIPE,
                    "Invalid expression argument table.");
            for (auto it = arguments.begin(); it != arguments.end(); ++it)
                require(identifier(it.key()), JPV_INVALID_RECIPE, "Invalid expression argument name.");
            auto expression = definition.at("expression").get<std::string>();
            const auto names = expression_symbols(expression, functions);
            if (!values) {
                for (const auto &name : names)
                    (void)symbol(name, arguments);
                for (auto it = arguments.begin(); it != arguments.end(); ++it)
                    (void)resolve(it.value(), depth + 1);
                // Syntax and references are checked now; arithmetic uses actual run values only.
                result.deferred = true;
            } else {
                struct MissingValue {};
                try {
                    result.value = evaluate_expression(
                        expression,
                        [&](const std::string &name) {
                            auto value = symbol(name, arguments);
                            if (value.unavailable)
                                throw MissingValue{};
                            result.position |= value.position;
                            merge_frames(result.frames, value.frames);
                            return value.value;
                        },
                        functions);
                } catch (const MissingValue &) {
                    result.unavailable = true;
                }
                result.type = scalar_type(result.value);
            }
        }
        for (auto it = definition.begin(); it != definition.end(); ++it)
            require(allowed.contains(it.key()), JPV_INVALID_RECIPE,
                    "Unknown subscription option: " + it.key());
        return result;
    }
    Json parameter_value(const Json &value, const Json &spec, const std::string &path, bool roi = false,
                         const std::string &axis = "") {
        if (binding(value)) {
            require(roi || spec.value("bindable", false), JPV_INVALID_RECIPE,
                    "Parameter does not support subscription: " + path);
            auto resolved = resolve(value);
            auto type = spec.at("type").get<std::string>();
            std::string target = type == "number"    ? "Double"
                                 : type == "integer" ? "Int"
                                 : type == "bool"    ? "Bool"
                                                     : "String";
            if (value.contains("fallback"))
                (void)checked_scalar(value.at("fallback"), target, path, JPV_INVALID_RECIPE);
            if (!resolved.type.empty())
                require(resolved.type == target || (target == "Double" && resolved.type == "Int") ||
                            (target == "Int" && value.contains("expression") && resolved.type == "Double"),
                        JPV_INVALID_RECIPE, "Parameter field type mismatch: " + path);
            if (resolved.deferred || resolved.unavailable) {
                deferred |= resolved.deferred;
                if (resolved.unavailable)
                    unavailable.push_back(path);
                if (value.contains("fallback"))
                    return checked_scalar(value.at("fallback"), target, path, JPV_INVALID_RECIPE);
                auto fallback = spec.at("default");
                if (!fallback.is_object())
                    return fallback;
                if (fallback.contains("input") &&
                    publicValues.contains(fallback.at("input").get<std::string>()))
                    return publicValues.at(fallback.at("input").get<std::string>());
                return target == "Bool"     ? Json(false)
                       : target == "String" ? Json("")
                                            : spec.value("min", Json(0));
            }
            if (roi && input) {
                if (!resolved.frames.empty()) {
                    geometricRois.insert(path.substr(0, path.find('.')));
                    require(spec.value("coordinateSpace", "input") != "reference", JPV_INVALID_ARGUMENT,
                            "Geometry subscriptions require image ROI reference, not fixture reference.");
                }
                for (const auto &frame : resolved.frames)
                    require(frame.space == input->coordinateSpace && frame.plane == input->coordinatePlane,
                            JPV_INVALID_ARGUMENT,
                            "ROI subscription belongs to a different image coordinate plane: " + path);
                if (!axis.empty() && resolved.position) {
                    require(spec.value("coordinateSpace", "input") != "reference", JPV_INVALID_ARGUMENT,
                            "Coordinate subscriptions require image ROI reference, not fixture reference.");
                    resolved.value = resolved.value.get<double>() - (axis == "x" ? input->x : input->y);
                }
            }
            return checked_scalar(resolved.value, target, path, JPV_INVALID_RECIPE);
        }
        return value;
    }
    void validate_coordinate_references(const Json &parameters) const {
        for (const auto &key : geometricRois) {
            const std::string reference = key == "firstRoi"    ? "firstRoiReference"
                                          : key == "secondRoi" ? "secondRoiReference"
                                                               : "roiReference";
            require(parameters.value(reference, "image") == "image", JPV_INVALID_ARGUMENT,
                    "Geometry subscriptions require image ROI reference; disable fixture positioning on this "
                    "ROI.");
            if (key == "roi" && node.at("type") == "FindCircles") {
                auto mode = parameters.value("positionReference", "image");
                bool positioned =
                    mode == "auto" ? input && (input->fixture || input->matchPosition) : mode != "image";
                require(!positioned, JPV_INVALID_ARGUMENT,
                        "Geometry subscriptions require image positioning; do not combine them with "
                        "automatic match/fixture positioning.");
            }
        }
    }
    Json roi_value(const Json &roi, const std::string &path) {
        require(roi.is_object() && roi.contains("shape") && roi.at("shape").is_string(), JPV_INVALID_RECIPE,
                "ROI shape is missing.");
        Json result = roi;
        auto shape = roi.at("shape").get<std::string>();
        std::set<std::string> fields{"shape", "coordinateSpace", "referenceHash"};
        if (shape == "rectangle")
            fields.insert({"x", "y", "width", "height"});
        else if (shape == "circle")
            fields.insert({"cx", "cy", "radius"});
        else if (shape == "annulus")
            fields.insert({"cx", "cy", "innerRadius", "outerRadius", "startAngle", "endAngle"});
        else if (shape == "rotatedRectangle")
            fields.insert({"cx", "cy", "width", "height", "angleDegrees"});
        else if (shape == "polygon")
            fields.insert("points");
        else
            require(shape == "none", JPV_INVALID_RECIPE, "Unsupported ROI shape.");
        for (auto it = roi.begin(); it != roi.end(); ++it)
            require(fields.contains(it.key()), JPV_INVALID_RECIPE, "Unknown ROI field: " + it.key());
        for (auto it = roi.begin(); it != roi.end(); ++it) {
            auto key = it.key();
            bool coordinate = key == "x" || key == "y" || key == "cx" || key == "cy";
            bool angle = key == "startAngle" || key == "endAngle" || key == "angleDegrees";
            bool radius = key == "radius" || key == "innerRadius" || key == "outerRadius";
            if (coordinate || angle || radius || key == "width" || key == "height") {
                double minimum = angle                                                                ? -360
                                 : coordinate && roi.value("coordinateSpace", "input") == "reference" ? -65536
                                                                                                      : 0;
                double maximum = angle                                                                ? 720
                                 : radius                                                             ? 16384
                                 : coordinate && roi.value("coordinateSpace", "input") == "reference" ? 65536
                                                                                                      : 32768;
                Json spec{{"type", "number"},
                          {"default", key == "innerRadius"                ? 0
                                      : key == "outerRadius"              ? 100
                                      : radius                            ? 60
                                      : key == "endAngle"                 ? 360
                                      : key == "width" || key == "height" ? 50
                                      : coordinate                        ? 160
                                                                          : 0},
                          {"min", minimum},
                          {"max", maximum},
                          {"coordinateSpace", roi.value("coordinateSpace", "input")}};
                result[key] = parameter_value(it.value(), spec, path + "." + key, true,
                                              key == "x" || key == "cx"   ? "x"
                                              : key == "y" || key == "cy" ? "y"
                                                                          : "");
            } else if (key == "points") {
                require(it.value().is_array() && it.value().size() <= 64, JPV_INVALID_RECIPE,
                        "Polygon points are invalid.");
                for (size_t i = 0; i < it.value().size(); ++i) {
                    require(it.value()[i].is_array() && it.value()[i].size() == 2, JPV_INVALID_RECIPE,
                            "Polygon vertex must contain X and Y.");
                    for (size_t axis = 0; axis < 2; ++axis) {
                        Json spec{{"type", "number"},
                                  {"default", i == 1                ? 100
                                              : axis == 1 && i == 2 ? 100
                                                                    : 10},
                                  {"min", 0},
                                  {"max", 32768},
                                  {"coordinateSpace", roi.value("coordinateSpace", "input")}};
                        result[key][i][axis] = parameter_value(it.value()[i][axis], spec,
                                                               path + ".points." + std::to_string(i) + "." +
                                                                   std::to_string(axis),
                                                               true, axis == 0 ? "x" : "y");
                    }
                }
            }
        }
        // Cropping requires integer bounds. Preserve subpixel center values on all other shapes.
        if (shape == "rectangle")
            for (const char *key : {"x", "y", "width", "height"}) {
                double v = result.at(key).get<double>();
                require(std::isfinite(v) && std::abs(v) <= 65536, JPV_INVALID_RECIPE,
                        "Rectangle ROI coordinate is invalid.");
                result[key] = static_cast<int64_t>(std::llround(v));
            }
        return result;
    }
    Json local_snapshot() {
        Json result = Json::object();
        for (auto it = definitions.begin(); it != definitions.end(); ++it) {
            auto value = local(it.key());
            deferred |= value.deferred;
            if (value.unavailable)
                unavailable.push_back("local." + it.key());
            result[it.key()] = value.deferred || value.unavailable ? Json() : value.value;
        }
        return result;
    }
};
} // namespace
Json global_variable_values(const Json &recipe, const Json &overrides) {
    auto definitions = recipe.value("variables", Json::object());
    variable_definitions(definitions, 64, true);
    validate_functions(recipe.value("functions", Json::object()));
    require(overrides.is_object(), JPV_INVALID_ARGUMENT, "Run variables must be an object.");
    for (auto it = overrides.begin(); it != overrides.end(); ++it)
        require(definitions.contains(it.key()), JPV_INVALID_ARGUMENT,
                "Unknown global run variable: " + it.key());
    Json values = Json::object();
    for (auto it = definitions.begin(); it != definitions.end(); ++it)
        values[it.key()] =
            checked_scalar(overrides.contains(it.key()) ? overrides.at(it.key()) : it.value().at("value"),
                           it.value().at("type"), it.key(), JPV_INVALID_ARGUMENT);
    return values;
}
std::map<std::string, std::pair<std::string, std::string>> parameter_dependencies(const Json &node) {
    std::map<std::string, std::pair<std::string, std::string>> result;
    dependency_walk(node.at("parameters"), "parameters", result);
    if (node.contains("variables"))
        dependency_walk(node.at("variables"), "variables", result);
    return result;
}
ParameterResolution resolve_node_bindings(const Json &node, const Json &publicValues, const Plan &plan,
                                          const std::map<std::string, PortValues> *values, const Json *traces,
                                          const Image *input, const Json &globals,
                                          const std::function<void(const Json &, const Json &)> &validate) {
    const auto &meta = component_registry().get(node.at("type")).descriptor;
    const auto &given = node.at("parameters");
    require(given.is_object(), JPV_INVALID_RECIPE, "Node parameters must be an object.");
    for (auto it = given.begin(); it != given.end(); ++it)
        require(std::any_of(meta.at("parameters").begin(), meta.at("parameters").end(),
                            [&](const Json &p) { return p.at("name") == it.key(); }),
                JPV_INVALID_RECIPE, "Unknown node parameter: " + it.key());
    Resolver resolver(node, publicValues, plan, values, traces, input, globals);
    ParameterResolution result{Json::object(), resolver.local_snapshot()};
    for (const auto &p : meta.at("parameters")) {
        std::string name = p.at("name");
        Json value = given.contains(name) ? given.at(name) : p.at("default");
        bool before = resolver.deferred;
        auto unavailableCount = resolver.unavailable.size();
        value = p.at("type") == "roi" ? resolver.roi_value(value, name)
                                      : resolver.parameter_value(value, p, name);
        // A dynamic ROI is validated against actual same-frame values, not unrelated teaching placeholders.
        if (p.at("type") != "roi" || (!resolver.deferred && resolver.unavailable.empty()))
            validate(p, value);
        else if (!before && unavailableCount == resolver.unavailable.size() && !resolver.deferred)
            validate(p, value);
        result.parameters[name] = value;
    }
    result.deferred = resolver.deferred;
    resolver.validate_coordinate_references(result.parameters);
    if (!resolver.unavailable.empty())
        result.parameters["_unavailable"] = std::move(resolver.unavailable);
    return result;
}
} // namespace jp
