#pragma once
#include "common.h"
#include <functional>
#include <set>

namespace jp {
// Expressions are bounded pure numeric/Boolean formulas. Symbols are resolved by the
// caller from this execution's parameters or result fields, never by script evaluation.
std::set<std::string> expression_symbols(const std::string &expression, const Json &functions);
void validate_functions(const Json &functions);
Json evaluate_expression(const std::string &expression,
                         const std::function<Json(const std::string &)> &lookup, const Json &functions);
} // namespace jp
