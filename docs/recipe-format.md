# 配方、算子与复现包

2026-10-15 源码新增 Pose／Fixture、多输入、标量字段引用、Decision 及高级 ROI，详见 [M0～M2 契约](m0-m2-implementation.md)。schemaVersion/C ABI 保持 1；新节点／形状在旧 Runtime 明确拒绝。inspection 的 decisionOutput 现可为 Bool 或 Decision，下文版本章节保留历史格式背景。

## 0.4.0 模块 ROI 与模板

Image/Mask 输入模块增加可选 `parameters.roi`，旧配方缺省为 `{shape:"none"}`。矩形为 `{shape:"rectangle",x,y,width,height}`，圆形为 `{shape:"circle",cx,cy,radius}`，多边形为 `{shape:"polygon",points:[[x,y],...]}`。坐标是上游图像的局部像素坐标；多边形限 3–64 个不自交顶点。Image 内部携带原图原点与有效掩膜，公开几何仍为原图坐标。

TemplateMatch 的 `parameters.model` 内嵌 `{width,height,grayBase64,maskBase64?}`，宽高 4–512，Gray8/0或255掩膜均按行紧密排列；它随算法摘要保存，不依赖模板源路径。未学习模型可保存草稿，执行验证时拒绝。角度/尺度参数、分数阈值、数量和重叠阈值均是配方参数，见 [操作说明](studio-guide.md)。

卡尺保留 circle/line 的既有 Features 类型，增加 method=caliper、edgePoints=[{x,y},...] 和 rmsError。模板项 kind=match，包含边界 x/y/width/height、centerX/Y、angleDegrees、scale、score 和 corners=[{x,y},...]。既有计数和 Overlay 模块可复用；SDK 提供 GetMatches、EdgePoints 和 RmsError，C ABI 仍为 1。

## 文件与摘要

目录型包 `name.vrecipe/` 包含 `recipe.json`、`layout.json`，以及按需存在的资源目录。Runtime 只读取业务清单和声明的资源，不读取布局。

schemaVersion=1，minimumRuntimeAbi=1。0.4.1 起 kind 支持 inspection 和 processing；既有 inspection 配方不变。清单包含 recipeId、recipeVersion、contentHash、inputs、outputs、decisionOutput、nodes、connections、resources。

每个节点使用稳定业务 id、type、version=1 和 parameters；连接使用 fromNode/fromPort/toNode/toPort。端口 id 独立于画布上的序号与位置。公开输出以业务名映射 `{node,port,type,unit?}`，可执行清单需要 1–64 个公开输出。inspection 必须选择 Bool 类型的公开 decisionOutput；processing 的 decisionOutput 必须为空字符串，成功也保持 decision=Unknown，仅通过 executionStatus 和公开输出描述处理结果。配方 Description 返回 kind，运行 JSON 返回 recipeKind。

contentHash 是 canonical JSON 的 SHA-256：节点按 id 排序、连接按稳定内容排序，包含算法、连线、接口、判定、资源、格式和 ABI；不包含画布布局、recipeId、recipeVersion。Studio 在算法内容变化时提高 recipeVersion；纯移动画布或重复保存不改变版本与摘要。

SDK 的 `VisionEngine.SealRecipe` 可重新计算摘要，但不代表配方可执行；LoadRecipe/ValidateRecipe 仍验证结构、参数、子图和资源。业务内容被修改但没有重算摘要的配方，Runtime 拒绝加载。

未连接完整的草稿可在 Studio 保存重新打开。发布或执行需要通过真实 Runtime 校验；文件中的任何“已就绪”标记都不是执行依据。未知节点/版本、重复 id、环路、端口类型不匹配和非法结构拒绝打开；未连线与尚未完成输出绑定的草稿可继续编辑。

## 输入、输出、坐标

首版正好一个 Image 输入，外部像素通过 RunAsync 单独传入；Image 的输入名称由 ExternalImage 的 input 参数指定。标量支持 Int、Double、Bool、String。显式输入优先于 default；required 默认为 true。可选且未使用的无默认标量可缺省，节点需要绑定的输入必须有有效值。

当前示例：InputImage、ExpectedCount，返回 ObjectCount、InspectionPassed、ResultOverlay。SDK 不依赖这些固定名称，可读取 Description 并按自己的业务名称取输出。WpfSample 专门展示计数型配方，使用这些示例名称；通用宿主应根据自有契约取结果。

CropRect 的 x/y/width/height 相对于其输入图像。输出图像带累加的 originX/originY；轮廓和叠加图元返回原图坐标，显示裁剪图时减去 origin。Qt 框选 ROI 按上游图像局部坐标保存，嵌套 ROI 按已声明的上游裁剪还原几何范围。

## 算子

| 类型 | 输入 → 输出 | 主要参数 |
|---|---|---|
| ExternalImage | 无 → Image | 公开图像 input 名称 |
| ToGray | Image → Image | Gray8 直通 / Bgr24 转灰度 |
| GaussianBlur | Image → Image | kernel 奇数 1–101，sigma 0–100 |
| Threshold | Image → Mask | value 0–255，invert；输入必须 Gray8 |
| Morphology | Mask → Mask | open/close/erode/dilate，奇数 kernel，iterations |
| CropRect | Image → Image | 输入范围内的矩形 |
| FindContours | Mask → Contours | 外轮廓、简化点 |
| FilterContoursByArea | Contours → Contours | minimum / maximum，单位 px² |
| CountContours | Contours → Int | 条数 |
| CompareNumber | Int → Bool | expected 非负整数，可绑定公开 Int |
| BuildContourOverlay | Contours → Overlay | color=#RRGGBB、lineWidth |
| MedianBlur | Image → Image | kernel 奇数 3–31 |
| FindCircles | Image → Features | dp、minimumDistance、edgeThreshold、voteThreshold、半径范围 |
| FindLines | Image → Features | edgeLow/edgeHigh、voteThreshold、minimumLength、maximumGap |
| AnalyzeContours | Contours → Features | 面积、周长、质心、包围框、圆形度 |
| CountFeatures | Features → Int | 圆/线段/区域的结果条数 |
| BuildFeatureOverlay | Features → Overlay | color、lineWidth；圆/线段/矩形图元 |

catalog 是属性面板和端口类型的统一来源；参数值和范围由原生核心再次验证。Image 端口本身不区分 Gray/Bgr，Threshold 在运行时拒绝 Bgr；正确流程需要 ToGray。没有隐式把不兼容像素格式当二值图处理。

0.2.0 新增 Features 端口类型，序列化为包含 kind 字段的数组。圆项保留 `{kind:"circle",x,y,radius}`，0.6.0 新增 centerX/Y、diameter、area、perimeter；线段项保留端点、长度、角度并新增 centerX/Y；区域项包含 kind=region、area、perimeter、centroidX/Y、centroidValid、circularity、x/y/width/height。坐标均为原图坐标。调试执行返回 debugOverlays[nodeId]；正式标记可直接公开检测算子的 overlay 端口，既有 BuildFeatureOverlay 仍可复用。

六个检测／测量算子新增 measurement（Measurement）、count（Int）、found（Bool）、passed（Bool）、overlay（Overlay），原 features 端口仍为索引 0。Measurement 对象固定包含 featureKind/count/found/passed/selectedIndex/selectedValid/selected；无有效选择时 selected=null。卡尺还包含 caliper 统计及 rejectedPoints。所有执行过的检测算子的当前测量放在结果 measurements[nodeId]，无需 Debug。失败任务清空公开输出及 measurements，轻量测量记录总预算 32 MiB。

FindCircles 新增 method=automatic/caliper，默认 automatic 兼容旧配方；caliper 模式复用 CircleCaliper 的采样与拟合，卡尺强度参数名为 caliperThreshold，自动找圆仍使用原 edgeThreshold。卡尺剔除参数 removePointCount 默认 0，最大拟合偏差自动剔除仍启用。卡尺高度参数 searchLength，宽度参数 projectionWidth（奇数 1–63）；请求数量 caliperCount 4–256，剩余点必须满足最低点数和 minimumCoverage。详细契约见 [集成测量设计](integrated-measurements-design.md)。

## 资源与复现

resources 为 `{path,sha256}` 数组。路径必须相对配方根目录，不能越界，实际内容必须匹配 SHA-256；单文件 ≤32MiB，总条目 ≤128。现有 20 个算法不依赖外部模型文件；TemplateMatch 的像素与掩膜内嵌参数，资源契约用于配方附属资产。

Studio 每次执行建立已封存的快照。复现导出来自该任务的快照、实际请求与原图，包含 `recipe.vrecipe/`（含资源）、`request.json`、`input.png` 和 `run.json`，不混入执行后编辑的配方，也不导出许可或私钥。

正常激活后，可通过 RuntimeCli 或 SDK 加载复现配方与图像，再按 request.json 的输入重跑。复现运行号可保持或替换，算法结果一致性应比较输出和判定，不比较耗时。首版样例是确定性的整数计数和像素轮廓；复现不是输入图的无损相机原始格式归档系统。

## 0.7.0 当前流程预览

`previewNodes` 是可选稳定节点 ID 数组，最多 1024 项；其内容纳入 contentHash。运行计划会执行这些节点及祖先，同时保留已有公开输出和判定。请求的 `previewNodeId` 必须位于本次执行计划中；仅保留图像不改变配方公开接口。

运行 JSON 新增 `executionOrder` 与 `nodeOutputs[nodeId][portId]`。失败会清空 nodeOutputs、measurements 与 outputs，判定 Unknown。图像元数据和标量常驻；复杂完整输出在 Debug 或选中预览时保留，计入调试预算。掩膜预览额外包含 foregroundPixels。

快速匹配沿用 model 的 width/height/grayBase64/maskBase64，参数包括 scoreThreshold、searchDownsample（0/1/2/4/8）、candidateLimit、overlapThreshold；多目标模块另有 maximumMatches，两个模块均集成六输出端口。layout.json 的 templateRegions 保存绘制区域，不影响算法摘要；真正执行使用 parameters.model。

## 0.7.2 ROI 边界卡尺

参数 caliperPlacement 为 guide / roiBoundary，原生默认 guide；目录中的 editorDefault=roiBoundary 仅控制新 WPF 节点初始值。FindCircles 的 caliper 模式及 CircleCaliper 接受 circle ROI；LineCaliper 接受 rectangle/polygon，以 roiEdge（top/right/bottom/left）或 roiEdgeIndex（零基闭合边序号）选边。

roiBoundary 的本模块 ROI 是参考路径，窗口跨边界采样，保留上游有效掩膜。guide 保持原裁剪／指引几何。矩形参考边按 x+width/y+height 绘制几何，线站位为 (i+0.5)/caliperCount 避开端点；外法线决定边缘极性，polygon 绕向反转不改变内外方向。caliper.guides 为原图坐标窗口数组，含 x/y/normalX/normalY/width/height，height=2*ceil(searchLength/2)；caliper.placement 标注模式。没有目标时仍只有诊断，selected=null。

## 0.7.1 大图检测

FindCircles 自动模式及 FindLines 的 parameters.largeImageMode 可选 adaptive / original，缺省 adaptive。模块 ROI 裁剪后超过 4,194,304 像素时，adaptive 将工作图缩小到此上限，并还原原图像素坐标和尺寸；original 超限返回资源错误。原图像素参数含 minimumRadius/maximumRadius/minimumDistance 和 minimumLength/maximumGap。measurement.detection 包含 downsampled、inputWidth/inputHeight、workWidth/workHeight、scaleX/scaleY；卡尺仍使用 measurement.caliper。降采样不保证小目标或精确定位，精密场景用小 ROI 原图模式或卡尺。旧配方省略该参数无需修改，已有算法摘要仍基于其声明参数。
