#include "image_pipeline.h"
#include <algorithm>
#include <limits>
#include <opencv2/imgproc.hpp>
#include <unordered_map>

namespace jp {
namespace {
constexpr size_t MaxPipelineStages = 1024;
constexpr size_t MaxPipelineBytes = 128ULL * 1024 * 1024;
constexpr size_t MaxPipelineOverlayItems = 1000000;
std::atomic<uint64_t> nextFrameIdentity{1}, nextStageSequence{1};

// These records are only constructed here and are exposed through const shared ownership.
// Their Values are restricted to non-image alternatives; sourceImage is removed before insertion.
struct ImagePipelineStage {
    std::string nodeId, componentType;
    uint64_t sequence = 0;
    std::map<std::string, Value> values;
    Value overlay = Json::array();
    size_t bytes = 0;
};
} // namespace

struct ImagePipelineContext {
    uint64_t frameId = 0;
    std::string coordinatePlane;
    std::vector<std::shared_ptr<const ImagePipelineStage>> stages;
    std::map<std::string, const Value *> latest;
    size_t bytes = 0, tableBytes = 0;
};

namespace {
size_t json_bytes(const Json &value) {
    // Include the tree overhead as well as serialized strings/numbers. This is deliberately
    // conservative, and is computed once per new stage, never once per downstream image.
    size_t bytes = sizeof(Json);
    if (value.is_string())
        return bytes + value.get_ref<const std::string &>().capacity();
    if (value.is_object()) {
        for (auto it = value.begin(); it != value.end(); ++it)
            bytes += 3 * sizeof(void *) + it.key().capacity() + json_bytes(it.value());
    } else if (value.is_array()) {
        bytes += value.size() * sizeof(Json);
        for (const auto &item : value)
            bytes += json_bytes(item);
    }
    return bytes;
}

size_t value_bytes(const Value &value) {
    return std::visit(
        [](const auto &item) -> size_t {
            using T = std::decay_t<decltype(item)>;
            if constexpr (std::is_same_v<T, Image>) {
                throw Error(JPV_INTERNAL_ERROR, "Image pipeline context cannot retain image pixels.");
            } else if constexpr (std::is_same_v<T, Features>) {
                require(!item.sourceImage, JPV_INTERNAL_ERROR,
                        "Image pipeline feature context cannot retain a source image.");
                size_t bytes = sizeof(T) + item.items.capacity() * sizeof(Feature);
                for (const auto &feature : item.items)
                    std::visit(
                        [&](const auto &geometry) {
                            using F = std::decay_t<decltype(geometry)>;
                            if constexpr (std::is_same_v<F, CircleFeature> || std::is_same_v<F, LineFeature>)
                                bytes += geometry.edgePoints.capacity() * sizeof(cv::Point2d);
                        },
                        feature);
                return bytes + json_bytes(item.diagnostics) + json_bytes(item.detection) +
                       json_bytes(item.position) + json_bytes(item.fixture);
            } else if constexpr (std::is_same_v<T, Contours>) {
                require(!item.sourceImage, JPV_INTERNAL_ERROR,
                        "Image pipeline contour context cannot retain a source image.");
                size_t bytes = sizeof(T) + item.items.capacity() * sizeof(std::vector<cv::Point>);
                for (const auto &contour : item.items)
                    bytes += contour.capacity() * sizeof(cv::Point);
                return bytes;
            } else if constexpr (std::is_same_v<T, Json>)
                return json_bytes(item);
            else if constexpr (std::is_same_v<T, PoseValue>)
                return sizeof(T) + item.modelHash.capacity() + item.reason.capacity();
            else
                return sizeof(T);
        },
        value);
}

void strip_image_contexts(Json &value) {
    if (value.is_object()) {
        for (auto it = value.begin(); it != value.end(); ++it) {
            if (it.key() == "image" && it.value().is_object()) {
                // A result packet's image metadata must not embed a complete upstream context.
                it.value().erase("pipeline");
                it.value().erase("context");
            }
            strip_image_contexts(it.value());
        }
    } else if (value.is_array())
        for (auto &item : value)
            strip_image_contexts(item);
}

Value context_value(const Value &value) {
    require(!std::holds_alternative<Image>(value), JPV_INTERNAL_ERROR,
            "Image pipeline context cannot retain an image.");
    Value copy = value;
    if (auto features = std::get_if<Features>(&copy)) {
        if (features->sourceImage) {
            features->coordinateSpace = features->sourceImage->coordinateSpace;
            features->coordinatePlane = features->sourceImage->coordinatePlane;
        }
        features->sourceImage.reset();
        if (!features->dataAvailable)
            features->items.clear();
    } else if (auto contours = std::get_if<Contours>(&copy)) {
        contours->sourceImage.reset();
        if (!contours->dataAvailable)
            contours->items.clear();
    } else if (auto json = std::get_if<Json>(&copy))
        strip_image_contexts(*json);
    return copy;
}

const Image *value_image(const Value &value) {
    if (auto image = std::get_if<Image>(&value))
        return image;
    if (auto features = std::get_if<Features>(&value))
        return features->sourceImage.get();
    if (auto contours = std::get_if<Contours>(&value))
        return contours->sourceImage.get();
    return nullptr;
}

Json serialize_context_value(const Value &value) {
    return std::visit(
        [](const auto &item) -> Json {
            using T = std::decay_t<decltype(item)>;
            if constexpr (std::is_same_v<T, Image>)
                throw Error(JPV_INTERNAL_ERROR, "Image retained in image pipeline context.");
            else if constexpr (std::is_same_v<T, Features>)
                return feature_values(item);
            else if constexpr (std::is_same_v<T, Contours>) {
                Json contours = Json::array();
                for (const auto &contour : item.items) {
                    Json points = Json::array();
                    for (const auto &point : contour)
                        points.push_back({point.x + item.x, point.y + item.y});
                    contours.push_back(std::move(points));
                }
                return contours;
            } else if constexpr (std::is_same_v<T, PoseValue>)
                return pose_metadata(item);
            else
                return Json(item);
        },
        value);
}

Image contour_image(const Contours &contours, const Image &source) {
    Image image = source;
    require(!source.pixels.empty(), JPV_INTERNAL_ERROR, "Contour output has no associated image.");
    // MatExpr assignment can reuse an existing buffer. Detach before creating the selection mask,
    // or drawing filled contours also overwrites the source mask and destroys its holes.
    image.pixels.release();
    image.pixels = cv::Mat::zeros(source.pixels.size(), CV_8UC1);
    image.binaryMask = true;
    image.available = source.available && contours.dataAvailable;
    if (!image.available) {
        if (image.unavailableReason.empty())
            image.unavailableReason = "UnavailableContours";
        return image;
    }
    if (!contours.items.empty())
        cv::drawContours(image.pixels, contours.items, -1, cv::Scalar(255), cv::FILLED, cv::LINE_8,
                         cv::noArray(), std::numeric_limits<int>::max(),
                         {contours.x - source.x, contours.y - source.y});
    // Preserve holes and the actual foreground of an input binary mask rather than filling
    // everything inside an external contour. Area filtering still removes rejected regions.
    if (source.binaryMask && source.pixels.type() == CV_8UC1)
        cv::bitwise_and(image.pixels, source.pixels, image.pixels);
    if (!source.valid.empty()) {
        require(source.valid.size() == image.pixels.size() && source.valid.type() == CV_8UC1,
                JPV_INTERNAL_ERROR, "Contour output validity mask does not match its image.");
        cv::bitwise_and(image.pixels, source.valid, image.pixels);
    }
    return image;
}

Json contour_overlay(const Contours &contours, const Json &parameters) {
    Json overlay = Json::array();
    const auto color = parameters.value("color", std::string(DefaultResultColor));
    const auto width = parameters.value("lineWidth", 2.0);
    if (!contours.dataAvailable)
        return overlay;
    for (const auto &contour : contours.items) {
        Json points = Json::array();
        for (const auto &point : contour)
            points.push_back({point.x + contours.x, point.y + contours.y});
        overlay.push_back({{"type", "polyline"},
                           {"points", std::move(points)},
                           {"closed", true},
                           {"color", color},
                           {"lineWidth", width}});
    }
    return overlay;
}

bool same_pixels(const Image &a, const Image &b) {
    return a.pixels.type() == b.pixels.type() && a.pixels.datastart == b.pixels.datastart;
}

bool binary_output(const std::string &componentType, const std::string &outputType, const Image &image,
                   const Image &source) {
    if (image.pixels.type() != CV_8UC1)
        return false;
    if (outputType == "Mask")
        return true;
    if (componentType == "GaussianBlur")
        return false;
    if (componentType == "Threshold" || componentType == "Morphology" || componentType == "FindContours" ||
        componentType == "FilterContoursByArea")
        return true;
    if (componentType == "CropRect" || componentType == "ToGray" || componentType == "MedianBlur")
        return source.binaryMask;
    return image.binaryMask || (source.binaryMask && same_pixels(image, source));
}

Features selected_measurement_features(const InputValues &inputs, const Json &parameters) {
    Features selected;
    selected.dataAvailable = true;
    for (const auto &[port, parameter] :
         {std::pair{"first", "firstIndex"}, std::pair{"second", "secondIndex"}}) {
        auto input = inputs.find(port);
        const Features *features = input == inputs.end() ? nullptr : std::get_if<Features>(input->second);
        auto index = parameters.value(parameter, 0);
        if (!features || !features->dataAvailable || index < 0 || size_t(index) >= features->items.size()) {
            selected.items.clear();
            selected.dataAvailable = false;
            return selected;
        }
        selected.items.push_back(features->items[index]);
    }
    return selected;
}
} // namespace

Image begin_image_pipeline(const Image &image) {
    Image frame = image;
    auto context = std::make_shared<ImagePipelineContext>();
    context->frameId = nextFrameIdentity.fetch_add(1);
    context->coordinatePlane = image.coordinatePlane;
    require(context->frameId != 0, JPV_RESOURCE_LIMIT, "Image pipeline frame identity exhausted.");
    context->tableBytes = sizeof(ImagePipelineContext);
    context->bytes = context->tableBytes;
    frame.pipelineContext = std::move(context);
    return frame;
}

void complete_image_pipeline(const std::string &nodeId, const Component &component, const InputValues &inputs,
                             PortValues &ports, const Json &parameters, const Image &frame) {
    const auto &descriptor = component.descriptor;
    const auto type = descriptor.at("type").get<std::string>();
    const auto primary = descriptor.at("outputs")[0].at("id").get<std::string>();
    const auto primaryType = descriptor.at("outputs")[0].at("type").get<std::string>();
    require(ports.contains(primary), JPV_INTERNAL_ERROR, "Image pipeline requires the primary output.");
    require(frame.pipelineContext != nullptr, JPV_INTERNAL_ERROR,
            "Begin image pipeline once before executing the graph.");

    std::vector<const Image *> sources;
    for (const auto &input : descriptor.at("inputs")) {
        auto found = inputs.find(input.at("id").get<std::string>());
        if (found != inputs.end())
            if (auto image = value_image(*found->second))
                sources.push_back(image);
    }
    for (const auto &[port, value] : inputs)
        if (auto image = value_image(*value);
            image && std::find(sources.begin(), sources.end(), image) == sources.end())
            sources.push_back(image);

    const Image *source = sources.empty() ? &frame : sources.front();
    if (auto direct = inputs.find("input"); direct != inputs.end())
        if (auto image = std::get_if<Image>(direct->second))
            source = image;
    // Legacy measurements combine coordinates from two detectors; display their full accepted
    // frame. Their individual ROI/crop images are still used for provenance and typed inputs.
    if (primaryType == "Result" && inputs.contains("first") && inputs.contains("second") &&
        !inputs.contains("input") && source->coordinatePlane == frame.coordinatePlane)
        source = &frame;

    for (const auto *image : sources)
        require(image->coordinatePlane == source->coordinatePlane &&
                    image->coordinateSpace == source->coordinateSpace,
                JPV_INVALID_ARGUMENT, "Cannot combine geometry from different image coordinate planes.");

    const bool coordinateTransform = descriptor.value("coordinateTransform", false);

    std::unordered_map<std::string, std::shared_ptr<const ImagePipelineStage>> inherited;
    auto inherit = [&](const Image &image) {
        if (!image.pipelineContext)
            return;
        require(image.pipelineContext->frameId == frame.pipelineContext->frameId, JPV_INVALID_ARGUMENT,
                "Image pipeline inputs belong to different frames.");
        require(image.pipelineContext->coordinatePlane == source->coordinatePlane, JPV_INVALID_ARGUMENT,
                "Cannot combine geometry from different image coordinate planes.");
        for (const auto &stage : image.pipelineContext->stages) {
            auto found = inherited.find(stage->nodeId);
            require(found == inherited.end() || found->second == stage, JPV_INVALID_ARGUMENT,
                    "Image pipeline contains conflicting results for the same node.");
            inherited[stage->nodeId] = stage;
        }
    };
    // A nonlinear transform starts a new plane. Its source geometry cannot be reused as if
    // translation alone placed it on the rectangular output.
    if (!coordinateTransform) {
        inherit(*source);
        for (const auto *image : sources)
            inherit(*image);
        for (const auto &[port, value] : ports)
            if (auto image = value_image(value))
                inherit(*image);
    }

    Image projection = *source;
    if (auto existing = ports.find("image");
        existing != ports.end() && std::holds_alternative<Image>(existing->second))
        projection = std::get<Image>(existing->second);
    else if (auto image = std::get_if<Image>(&ports.at(primary)))
        projection = *image;
    else if (auto contours = std::get_if<Contours>(&ports.at(primary)))
        projection = contour_image(*contours, contours->sourceImage ? *contours->sourceImage : *source);
    else if (auto features = std::get_if<Features>(&ports.at(primary)); features && features->sourceImage)
        projection = *features->sourceImage;
    if (coordinateTransform) {
        projection.coordinateSpace =
            descriptor.value("outputCoordinateSpace", std::string("annularUnwrapped"));
        projection.coordinatePlane = std::to_string(frame.pipelineContext->frameId) + "/" + nodeId;
        projection.matchPosition.reset();
        projection.fixture.reset();
    } else
        copy_image_coordinates(projection, *source);
    projection.binaryMask = binary_output(type, primaryType, projection, *source);

    // Stamp the actual plane onto structured results before sharing/serializing them. Legacy
    // original-plane packets retain the same numeric values and coordinateSpace.
    std::function<void(Json &)> stamp = [&](Json &packet) {
        if (packet.is_array()) {
            for (auto &child : packet)
                stamp(child);
            return;
        }
        if (!packet.is_object())
            return;
        // Nested circles, annuli and fixture metadata describe this same plane. Source mapping
        // uses sourceCoordinateSpace instead, and must retain that explicit source reference.
        if (packet.contains("coordinateSpace")) {
            packet["coordinateSpace"] = projection.coordinateSpace;
            if (!projection.coordinatePlane.empty())
                packet["coordinatePlane"] = projection.coordinatePlane;
        }
        for (auto it = packet.begin(); it != packet.end(); ++it)
            if (it.key() != "coordinateMapping" && it.key() != "pipeline")
                stamp(it.value());
    };
    for (const auto &output : descriptor.at("outputs")) {
        const auto port = output.at("id").get<std::string>();
        if (!ports.contains(port))
            continue;
        if (auto pose = std::get_if<PoseValue>(&ports.at(port))) {
            pose->coordinateSpace = projection.coordinateSpace;
            pose->coordinatePlane = projection.coordinatePlane;
        }
        if (output.at("type") == "Result" || output.at("type") == "Measurement" ||
            output.at("type") == "PointSet") {
            if (auto packet = std::get_if<Json>(&ports.at(port)); packet && packet->is_object()) {
                (*packet)["coordinateSpace"] = projection.coordinateSpace;
                stamp(*packet);
                for (const auto *key : {"measurement", "image", "fixture", "pose"})
                    if (packet->contains(key) && (*packet)[key].is_object()) {
                        (*packet)[key]["coordinateSpace"] = projection.coordinateSpace;
                        stamp((*packet)[key]);
                    }
            }
        }
    }

    Json ownOverlay = Json::array();
    if (auto overlay = ports.find("overlay");
        overlay != ports.end() && std::holds_alternative<Json>(overlay->second))
        ownOverlay = std::get<Json>(overlay->second);
    else if (auto result = std::get_if<Json>(&ports.at(primary));
             result && result->is_object() && result->contains("overlay"))
        ownOverlay = result->at("overlay");
    else if (auto features = std::get_if<Features>(&ports.at(primary)))
        ownOverlay =
            feature_overlays(*features, parameters.value("overlayColor", std::string(DefaultResultColor)),
                             parameters.value("overlayLineWidth", 2.0));
    else if (auto contours = std::get_if<Contours>(&ports.at(primary)))
        ownOverlay = contour_overlay(*contours, parameters);
    require(ownOverlay.is_array() && ownOverlay.size() <= MaxPipelineOverlayItems, JPV_RESOURCE_LIMIT,
            "Image pipeline overlay budget exceeded.");

    auto stage = std::make_shared<ImagePipelineStage>();
    stage->nodeId = nodeId;
    stage->componentType = type;
    stage->sequence = nextStageSequence.fetch_add(1);
    stage->overlay = std::move(ownOverlay);
    for (const auto &output : descriptor.at("outputs")) {
        const auto outputType = output.at("type").get<std::string>();
        const auto port = output.at("id").get<std::string>();
        if (outputType == "Image" || outputType == "Mask" || outputType == "Overlay")
            continue;
        auto value = ports.find(port);
        if (value != ports.end() && !stage->values.contains(outputType))
            stage->values.emplace(outputType, context_value(value->second));
    }
    if (!stage->values.contains("Features") && inputs.contains("first") && inputs.contains("second")) {
        auto selected = selected_measurement_features(inputs, parameters);
        selected.coordinateSpace = projection.coordinateSpace;
        selected.coordinatePlane = projection.coordinatePlane;
        stage->values.emplace("Features", std::move(selected));
    }
    stage->bytes =
        sizeof(ImagePipelineStage) + nodeId.capacity() + type.capacity() + value_bytes(stage->overlay);
    for (const auto &[valueType, value] : stage->values)
        stage->bytes += 4 * sizeof(void *) + valueType.capacity() + value_bytes(value);
    require(stage->bytes <= MaxPipelineBytes, JPV_RESOURCE_LIMIT,
            "Image pipeline result context exceeds 128 MiB.");
    inherited[nodeId] = stage;
    require(inherited.size() <= MaxPipelineStages, JPV_RESOURCE_LIMIT,
            "Image pipeline stage limit exceeded.");

    auto context = std::make_shared<ImagePipelineContext>();
    context->frameId = frame.pipelineContext->frameId;
    context->coordinatePlane = projection.coordinatePlane;
    context->stages.reserve(inherited.size());
    for (auto &[id, record] : inherited)
        context->stages.push_back(std::move(record));
    std::sort(context->stages.begin(), context->stages.end(),
              [](const auto &a, const auto &b) { return a->sequence < b->sequence; });
    size_t overlayItems = 0;
    for (const auto &record : context->stages) {
        context->bytes += record->bytes;
        overlayItems += std::get<Json>(record->overlay).size();
        for (const auto &[valueType, value] : record->values)
            context->latest[valueType] = &value;
        context->latest["Overlay"] = &record->overlay;
    }
    context->tableBytes = sizeof(ImagePipelineContext) +
                          context->stages.capacity() * sizeof(std::shared_ptr<const ImagePipelineStage>);
    context->tableBytes += context->coordinatePlane.capacity();
    for (const auto &[valueType, value] : context->latest)
        context->tableBytes += 4 * sizeof(void *) + valueType.capacity();
    context->bytes += context->tableBytes;
    require(context->bytes <= MaxPipelineBytes && overlayItems <= MaxPipelineOverlayItems, JPV_RESOURCE_LIMIT,
            "Image pipeline result context budget exceeded.");
    projection.pipelineContext = context;

    for (const auto &output : descriptor.at("outputs")) {
        const auto outputType = output.at("type").get<std::string>();
        const auto port = output.at("id").get<std::string>();
        if (outputType == "Image" || outputType == "Mask") {
            if (!ports.contains(port))
                ports.emplace(port, projection);
            auto &image = std::get<Image>(ports.at(port));
            image.pipelineContext = context;
            copy_image_coordinates(image, projection);
            image.binaryMask = binary_output(type, outputType, image, *source);
        } else if (outputType == "Overlay" && !ports.contains(port))
            ports.emplace(port, image_context_overlay(projection));
    }
    // Associate typed results with the clean projection as well, but never retain that reference
    // in a stage. A later region analysis can therefore keep the filtered mask, origin and validity.
    for (auto &[port, value] : ports)
        if (auto contours = std::get_if<Contours>(&value))
            contours->sourceImage = std::make_shared<Image>(projection);
        else if (auto features = std::get_if<Features>(&value)) {
            // Keep the detector's actual pre-ROI pixels, including its full input when a match
            // projects a crop. Only associate the newly completed context so legacy Features
            // connections carry this detector's result as well as its original image.
            Image associated = features->sourceImage ? *features->sourceImage : *source;
            associated.pipelineContext = context;
            copy_image_coordinates(associated, projection);
            features->coordinateSpace = projection.coordinateSpace;
            features->coordinatePlane = projection.coordinatePlane;
            features->sourceImage = std::make_shared<Image>(std::move(associated));
        }
}

const Value *image_context_value(const Image &image, const std::string &type) {
    if (!image.pipelineContext)
        return nullptr;
    auto found = image.pipelineContext->latest.find(type);
    return found == image.pipelineContext->latest.end() ? nullptr : found->second;
}

Json image_context_overlay(const Image &image) {
    Json overlay = Json::array();
    if (!image.pipelineContext || !image.available)
        return overlay;
    std::unordered_map<std::string, size_t> positions;
    for (const auto &stage : image.pipelineContext->stages)
        for (const auto &item : std::get<Json>(stage->overlay)) {
            // A marker module can restyle an upstream fit. Keep one geometric annotation,
            // with the last explicit style, rather than drawing both versions on top of it.
            Json geometry = item;
            if (geometry.is_object()) {
                geometry.erase("color");
                geometry.erase("lineWidth");
            }
            auto [position, inserted] = positions.emplace(geometry.dump(), overlay.size());
            if (inserted)
                overlay.push_back(item);
            else
                overlay[position->second] = item;
        }
    return overlay;
}

Json image_context_metadata(const Image &image) {
    if (!image.pipelineContext)
        return nullptr;
    const auto &context = *image.pipelineContext;
    Json results = Json::object();
    for (const auto &[type, value] : context.latest)
        if (type != "Overlay")
            results[type] = serialize_context_value(*value);
    return {{"frameId", context.frameId},
            {"coordinateSpace", image.coordinateSpace},
            {"coordinatePlane", image.coordinatePlane},
            {"stageCount", context.stages.size()},
            {"lastNodeId", context.stages.empty() ? "" : context.stages.back()->nodeId},
            {"results", std::move(results)},
            {"overlay", image_context_overlay(image)}};
}

size_t image_context_bytes(const Image &image) {
    return image.pipelineContext ? image.pipelineContext->bytes : 0;
}

void image_context_payloads(const Image &image, const std::function<void(const void *, size_t)> &account) {
    if (!image.pipelineContext)
        return;
    account(image.pipelineContext.get(), image.pipelineContext->tableBytes);
    for (const auto &stage : image.pipelineContext->stages)
        account(stage.get(), stage->bytes);
}
} // namespace jp
