#include "GraphModel.h"
#include <QJsonDocument>
#include <QUuid>
#include <functional>
#include <set>

using namespace QtNodes;
QJsonObject GraphModel::descriptor(const QString &type) const {
    for (const auto &node : catalog_)
        if (node.toObject()["type"].toString() == type)
            return node.toObject();
    return {};
}
QJsonObject GraphModel::business(NodeId id) const {
    auto it = nodes_.find(id);
    return it == nodes_.end() ? QJsonObject() : it->second.business;
}
QString GraphModel::stableId(NodeId id) const {
    return business(id)["id"].toString();
}
NodeId GraphModel::visualId(const QString &stable) const {
    for (const auto &[id, node] : nodes_)
        if (node.business["id"].toString() == stable)
            return id;
    return InvalidNodeId;
}
std::unordered_set<NodeId> GraphModel::allNodeIds() const {
    std::unordered_set<NodeId> ids;
    for (const auto &[id, node] : nodes_)
        ids.insert(id);
    return ids;
}
std::unordered_set<ConnectionId> GraphModel::allConnectionIds(NodeId id) const {
    std::unordered_set<ConnectionId> result;
    for (const auto &e : edges_)
        if (e.inNodeId == id || e.outNodeId == id)
            result.insert(e);
    return result;
}
std::unordered_set<ConnectionId> GraphModel::connections(NodeId id, PortType type, PortIndex index) const {
    std::unordered_set<ConnectionId> result;
    for (const auto &e : edges_)
        if ((type == PortType::In && e.inNodeId == id && e.inPortIndex == index) ||
            (type == PortType::Out && e.outNodeId == id && e.outPortIndex == index))
            result.insert(e);
    return result;
}
NodeId GraphModel::addNode(const QString type) {
    auto meta = descriptor(type);
    if (meta.isEmpty())
        return InvalidNodeId;
    auto id = newNodeId();
    QJsonObject parameters;
    for (const auto &entry : meta["parameters"].toArray()) {
        auto p = entry.toObject();
        parameters[p["name"].toString()] = p["default"];
    }
    nodes_[id] = {
        {{"id", "n" + QString::number(id) + "-" + QUuid::createUuid().toString(QUuid::Id128).left(8)},
         {"type", type},
         {"version", 1},
         {"parameters", parameters}},
        QPointF(),
        QSize()};
    Q_EMIT nodeCreated(id);
    if (!loading_)
        Q_EMIT documentChanged();
    return id;
}
bool GraphModel::connectionPossible(ConnectionId e) const {
    if (!nodeExists(e.outNodeId) || !nodeExists(e.inNodeId) || e.outNodeId == e.inNodeId ||
        connectionExists(e) || !connections(e.inNodeId, PortType::In, e.inPortIndex).empty())
        return false;
    auto out = descriptor(business(e.outNodeId)["type"].toString())["outputs"].toArray(),
         in = descriptor(business(e.inNodeId)["type"].toString())["inputs"].toArray();
    if (e.outPortIndex >= uint(out.size()) || e.inPortIndex >= uint(in.size()) ||
        out[int(e.outPortIndex)].toObject()["type"] != in[int(e.inPortIndex)].toObject()["type"])
        return false;
    std::set<NodeId> seen;
    std::function<bool(NodeId)> reaches = [&](NodeId id) {
        if (id == e.outNodeId)
            return true;
        if (!seen.insert(id).second)
            return false;
        for (const auto &edge : edges_)
            if (edge.outNodeId == id && reaches(edge.inNodeId))
                return true;
        return false;
    };
    return !reaches(e.inNodeId);
}
void GraphModel::addConnection(ConnectionId id) {
    if (!connectionPossible(id))
        return;
    edges_.insert(id);
    Q_EMIT connectionCreated(id);
    if (!loading_)
        Q_EMIT documentChanged();
}
QVariant GraphModel::nodeData(NodeId id, NodeRole role) const {
    auto it = nodes_.find(id);
    if (it == nodes_.end())
        return {};
    const auto &node = it->second;
    auto meta = descriptor(node.business["type"].toString());
    switch (role) {
    case NodeRole::Type:
        return node.business["type"].toString();
    case NodeRole::Position:
        return node.position;
    case NodeRole::Size:
        return node.size;
    case NodeRole::CaptionVisible:
        return true;
    case NodeRole::Caption:
        return meta["label"].toString();
    case NodeRole::Style: {
        auto style = StyleCollection::nodeStyle().toJson(), values = style["NodeStyle"].toObject();
        auto state = trace_[stableId(id)].toObject()["status"].toString();
        if (state == "Succeeded")
            values["NormalBoundaryColor"] = "#44D7B6";
        else if (state == "Failed")
            values["NormalBoundaryColor"] = "#F57681";
        style["NodeStyle"] = values;
        return style.toVariantMap();
    }
    case NodeRole::InternalData:
        return node.business;
    case NodeRole::InPortCount:
        return uint(meta["inputs"].toArray().size());
    case NodeRole::OutPortCount:
        return uint(meta["outputs"].toArray().size());
    default:
        return {};
    }
}
bool GraphModel::setNodeData(NodeId id, NodeRole role, QVariant value) {
    if (!nodes_.contains(id))
        return false;
    if (role == NodeRole::Position) {
        nodes_.at(id).position = value.value<QPointF>();
        Q_EMIT nodePositionUpdated(id);
        if (!loading_)
            Q_EMIT documentChanged();
        return true;
    }
    if (role == NodeRole::Size) {
        nodes_.at(id).size = value.value<QSize>();
        return true;
    }
    return false;
}
QVariant GraphModel::portData(NodeId id, PortType type, PortIndex index, PortRole role) const {
    auto ports =
        descriptor(business(id)["type"].toString())[type == PortType::In ? "inputs" : "outputs"].toArray();
    if (index >= uint(ports.size()))
        return {};
    auto port = ports[int(index)].toObject();
    switch (role) {
    case PortRole::DataType:
        return QVariant::fromValue(NodeDataType{port["type"].toString(), port["type"].toString()});
    case PortRole::ConnectionPolicyRole:
        return QVariant::fromValue(type == PortType::In ? ConnectionPolicy::One : ConnectionPolicy::Many);
    case PortRole::CaptionVisible:
        return true;
    case PortRole::Caption:
        return type == PortType::Out && ports.size() > 1
                   ? port["label"].toString() + " · " + port["type"].toString()
                   : port["type"].toString();
    default:
        return {};
    }
}
bool GraphModel::deleteConnection(ConnectionId id) {
    if (!edges_.erase(id))
        return false;
    Q_EMIT connectionDeleted(id);
    if (!loading_)
        Q_EMIT documentChanged();
    return true;
}
bool GraphModel::deleteNode(NodeId id) {
    if (!nodes_.contains(id))
        return false;
    for (const auto &e : allConnectionIds(id))
        deleteConnection(e);
    nodes_.erase(id);
    Q_EMIT nodeDeleted(id);
    if (!loading_)
        Q_EMIT documentChanged();
    return true;
}
QJsonObject GraphModel::saveNode(NodeId id) const {
    auto node = nodes_.at(id);
    return {{"id", int(id)},
            {"position", QJsonObject{{"x", node.position.x()}, {"y", node.position.y()}}},
            {"internal-data", node.business}};
}
void GraphModel::loadNode(const QJsonObject &data) {
    auto id = NodeId(data["id"].toInt());
    auto object = data["internal-data"].toObject();
    if (descriptor(object["type"].toString()).isEmpty())
        return;
    if (visualId(object["id"].toString()) != InvalidNodeId)
        object["id"] = "n" + QString::number(id) + "-" + QUuid::createUuid().toString(QUuid::Id128).left(8);
    auto position = data["position"].toObject();
    nodes_[id] = {object, {position["x"].toDouble(), position["y"].toDouble()}, QSize()};
    next_ = std::max(next_, id + 1);
    Q_EMIT nodeCreated(id);
    if (!loading_)
        Q_EMIT documentChanged();
}
void GraphModel::setParameters(NodeId id, const QJsonObject &parameters) {
    if (!nodes_.contains(id))
        return;
    nodes_.at(id).business["parameters"] = parameters;
    Q_EMIT nodeUpdated(id);
    if (!loading_)
        Q_EMIT documentChanged();
}
void GraphModel::setInterfaces(QJsonObject inputs, QJsonObject outputs, QString decision, QString kind) {
    metadata_["inputs"] = inputs;
    metadata_["outputs"] = outputs;
    metadata_["decisionOutput"] = decision;
    if (!kind.isEmpty())
        metadata_["kind"] = kind;
    Q_EMIT documentChanged();
}
QJsonObject GraphModel::recipe() const {
    auto recipe = metadata_;
    QJsonArray nodes, edges;
    for (const auto &[id, node] : nodes_)
        nodes.append(node.business);
    for (const auto &e : edges_) {
        auto out = descriptor(business(e.outNodeId)["type"].toString())["outputs"]
                       .toArray()[int(e.outPortIndex)]
                       .toObject();
        auto in = descriptor(business(e.inNodeId)["type"].toString())["inputs"]
                      .toArray()[int(e.inPortIndex)]
                      .toObject();
        edges.append(QJsonObject{{"fromNode", stableId(e.outNodeId)},
                                 {"fromPort", out["id"]},
                                 {"toNode", stableId(e.inNodeId)},
                                 {"toPort", in["id"]}});
    }
    recipe["nodes"] = nodes;
    recipe["connections"] = edges;
    return recipe;
}
QString GraphModel::publicInterfaceIssue() const {
    auto outputs = metadata_.value("outputs").toObject();
    for (auto it = outputs.begin(); it != outputs.end(); ++it) {
        auto output = it.value().toObject();
        auto id = visualId(output.value("node").toString());
        if (!nodeExists(id))
            return "公开输出“" + it.key() + "”引用了已删除的节点。";
        bool valid = false;
        for (auto value : descriptor(business(id).value("type").toString()).value("outputs").toArray()) {
            auto port = value.toObject();
            if (port["id"] == output["port"] && port["type"] == output["type"])
                valid = true;
        }
        if (!valid)
            return "公开输出“" + it.key() + "”的端口或类型已失效。";
    }
    if (!outputs.isEmpty() && metadata_.value("kind") == "inspection") {
        auto decision = metadata_.value("decisionOutput").toString();
        if (!outputs.contains(decision) || outputs.value(decision).toObject().value("type") != "Bool")
            return "检测判定输出已失效。";
    }
    return {};
}
QJsonObject GraphModel::executionRecipe() const {
    auto result = recipe();
    if (nodes_.empty())
        throw std::runtime_error("请先添加外部图像和处理模块，再连接端口。");
    bool configured = !result.value("outputs").toObject().isEmpty() && publicInterfaceIssue().isEmpty();
    if (configured) {
        // Execute new terminal branches for the editor while retaining the published contract.
        std::set<QtNodes::NodeId> covered;
        std::function<void(QtNodes::NodeId)> visit = [&](QtNodes::NodeId id) {
            if (!covered.insert(id).second)
                return;
            for (auto edge : allConnectionIds(id))
                if (edge.inNodeId == id)
                    visit(edge.outNodeId);
        };
        for (auto value : result.value("outputs").toObject())
            visit(visualId(value.toObject().value("node").toString()));
        QJsonArray targets;
        for (const auto &[id, node] : nodes_) {
            bool terminal = true;
            for (auto edge : allConnectionIds(id))
                if (edge.outNodeId == id)
                    terminal = false;
            if (terminal && !covered.contains(id))
                targets.append(stableId(id));
        }
        if (!targets.isEmpty())
            result["previewNodes"] = targets;
        return result;
    }
    // Preview every terminal branch without changing the saved WPF interface.
    // A preview has no inspection decision, even when a branch produces Bool.
    QJsonObject outputs;
    for (const auto &[id, node] : nodes_) {
        bool terminal = true;
        for (const auto &edge : allConnectionIds(id))
            if (edge.outNodeId == id) {
                terminal = false;
                break;
            }
        if (!terminal)
            continue;
        for (const auto &value :
             descriptor(node.business.value("type").toString()).value("outputs").toArray()) {
            auto port = value.toObject();
            auto name = "Preview_" + stableId(id) + "_" + port.value("id").toString();
            outputs[name] =
                QJsonObject{{"node", stableId(id)}, {"port", port.value("id")}, {"type", port.value("type")}};
        }
    }
    if (outputs.isEmpty() || outputs.size() > 64)
        throw std::runtime_error("调试流程需要 1–64 个末端输出；请在“编辑 → 公开输入输出”中选择输出。");
    result["kind"] = "processing";
    result["decisionOutput"] = "";
    result["outputs"] = outputs;
    if (!publicInterfaceIssue().isEmpty())
        result["previewReason"] = publicInterfaceIssue();
    return result;
}
QJsonObject GraphModel::layout() const {
    QJsonObject positions;
    for (const auto &[id, node] : nodes_)
        positions[stableId(id)] = QJsonObject{{"x", node.position.x()}, {"y", node.position.y()}};
    return {{"positions", positions}};
}
void GraphModel::load(const QJsonObject &recipe, const QJsonObject &layout) {
    // Parse into a separate document. A malformed open must not destroy edits.
    GraphModel candidate(catalog_);
    candidate.populate(recipe, layout);
    nodes_.swap(candidate.nodes_);
    edges_.swap(candidate.edges_);
    metadata_ = candidate.metadata_;
    next_ = candidate.next_;
    trace_ = {};
    Q_EMIT modelReset();
}
void GraphModel::populate(const QJsonObject &recipe, const QJsonObject &layout) {
    if (recipe["schemaVersion"].toInt() != 1 || recipe["minimumRuntimeAbi"].toInt(1) != 1 ||
        (recipe["kind"] != "inspection" && recipe["kind"] != "processing") ||
        recipe["recipeId"].toString().isEmpty() || recipe["recipeVersion"].toInt() < 1 ||
        !recipe["inputs"].isObject() || !recipe["outputs"].isObject() || !recipe["nodes"].isArray() ||
        !recipe["connections"].isArray())
        throw std::runtime_error("配方结构或版本无效。");
    if (recipe["nodes"].toArray().size() > 1024 || recipe["connections"].toArray().size() > 4096)
        throw std::runtime_error("配方节点或连接超过上限。");
    loading_ = true;
    nodes_.clear();
    edges_.clear();
    trace_ = {};
    next_ = 0;
    metadata_ = recipe;
    metadata_.remove("nodes");
    metadata_.remove("connections");
    auto positions = layout["positions"].toObject();
    for (const auto &item : recipe["nodes"].toArray()) {
        auto node = item.toObject();
        auto meta = descriptor(node["type"].toString());
        if (meta.isEmpty() || node["version"].toInt() != 1)
            throw std::runtime_error("配方包含未知算子或不支持的版本。");
        auto stable = node["id"].toString();
        if (stable.isEmpty() || stable.size() > 128 || visualId(stable) != InvalidNodeId ||
            !node["parameters"].isObject())
            throw std::runtime_error("节点 ID 重复、为空或参数结构无效。");
        auto position = positions[stable].toObject();
        auto id = newNodeId();
        nodes_[id] = {node, {position["x"].toDouble(), position["y"].toDouble()}, QSize()};
    }
    for (const auto &item : recipe["connections"].toArray()) {
        auto e = item.toObject();
        auto out = visualId(e["fromNode"].toString()), in = visualId(e["toNode"].toString());
        if (out == InvalidNodeId || in == InvalidNodeId) {
            loading_ = false;
            throw std::runtime_error("配方包含不存在的连接端点。");
        }
        auto outPorts = descriptor(business(out)["type"].toString())["outputs"].toArray(),
             inPorts = descriptor(business(in)["type"].toString())["inputs"].toArray();
        int op = -1, ip = -1;
        for (int i = 0; i < outPorts.size(); ++i)
            if (outPorts[i].toObject()["id"] == e["fromPort"])
                op = i;
        for (int i = 0; i < inPorts.size(); ++i)
            if (inPorts[i].toObject()["id"] == e["toPort"])
                ip = i;
        if (op < 0 || ip < 0 || !connectionPossible({out, uint(op), in, uint(ip)})) {
            loading_ = false;
            throw std::runtime_error("配方连接类型不匹配或存在环路。");
        }
        edges_.insert({out, uint(op), in, uint(ip)});
    }
    loading_ = false;
}
void GraphModel::setTrace(const QJsonObject &trace) {
    trace_ = trace;
    for (const auto &[id, node] : nodes_)
        Q_EMIT nodeUpdated(id);
}
