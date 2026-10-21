#pragma once
#include <QJsonArray>
#include <QMap>
#include <QtNodes/AbstractGraphModel>
#include <QtNodes/NodeData>
#include <QtNodes/StyleCollection>
#include <map>

class GraphModel : public QtNodes::AbstractGraphModel {
    Q_OBJECT
    struct Node {
        QJsonObject business;
        QPointF position;
        QSize size;
    };
    std::map<QtNodes::NodeId, Node> nodes_;
    std::unordered_set<QtNodes::ConnectionId> edges_;
    QtNodes::NodeId next_ = 0;
    QJsonArray catalog_;
    QJsonObject metadata_, trace_;
    bool loading_ = false;
    void populate(const QJsonObject &recipe, const QJsonObject &layout);

  public:
    explicit GraphModel(QJsonArray catalog, QObject *parent = nullptr) : AbstractGraphModel() {
        setParent(parent);
        catalog_ = catalog;
    }
    QJsonObject descriptor(const QString &type) const;
    QJsonObject business(QtNodes::NodeId id) const;
    QString stableId(QtNodes::NodeId id) const;
    QtNodes::NodeId visualId(const QString &stable) const;
    void setParameters(QtNodes::NodeId id, const QJsonObject &parameters);
    void setInterfaces(QJsonObject inputs, QJsonObject outputs, QString decision, QString kind = {});
    QJsonObject recipe() const;
    QJsonObject executionRecipe() const;
    QString publicInterfaceIssue() const;
    QJsonObject layout() const;
    void load(const QJsonObject &recipe, const QJsonObject &layout);
    QJsonObject interfaces() const {
        return metadata_;
    }
    void markSaved(const QJsonObject &sealed) {
        metadata_["contentHash"] = sealed["contentHash"];
        metadata_["recipeVersion"] = sealed["recipeVersion"];
    }
    void setTrace(const QJsonObject &trace);
    QtNodes::NodeId newNodeId() override {
        return next_++;
    }
    std::unordered_set<QtNodes::NodeId> allNodeIds() const override;
    std::unordered_set<QtNodes::ConnectionId> allConnectionIds(QtNodes::NodeId id) const override;
    std::unordered_set<QtNodes::ConnectionId> connections(QtNodes::NodeId id, QtNodes::PortType type,
                                                          QtNodes::PortIndex index) const override;
    bool connectionExists(QtNodes::ConnectionId id) const override {
        return edges_.contains(id);
    }
    QtNodes::NodeId addNode(QString const type = {}) override;
    bool connectionPossible(QtNodes::ConnectionId id) const override;
    void addConnection(QtNodes::ConnectionId id) override;
    bool nodeExists(QtNodes::NodeId id) const override {
        return nodes_.contains(id);
    }
    QVariant nodeData(QtNodes::NodeId id, QtNodes::NodeRole role) const override;
    bool setNodeData(QtNodes::NodeId id, QtNodes::NodeRole role, QVariant value) override;
    QVariant portData(QtNodes::NodeId id, QtNodes::PortType type, QtNodes::PortIndex index,
                      QtNodes::PortRole role) const override;
    bool setPortData(QtNodes::NodeId, QtNodes::PortType, QtNodes::PortIndex, const QVariant &,
                     QtNodes::PortRole) override {
        return false;
    }
    bool deleteConnection(QtNodes::ConnectionId id) override;
    bool deleteNode(QtNodes::NodeId id) override;
    QJsonObject saveNode(QtNodes::NodeId id) const override;
    void loadNode(const QJsonObject &node) override;
    bool loopsEnabled() const override {
        return false;
    }
  Q_SIGNALS:
    void documentChanged();
};
