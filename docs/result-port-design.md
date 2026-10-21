# 单一结果端口与工具箱精简

## 约定

MeasureCircleDistance（圆圆距离）在既有组件上新增 distanceMode=centerDistance/edgeDistance，默认圆心距兼容旧配方；first/second 的平铺圆几何不改，operation 根据方式为 circleCenterDistance/circleEdgeDistance。新增 MeasureLineCircleDistance（线圆距离），first 为直线 Features、second 为圆 Features；结果 operation=lineCircleDistance、几何源分别为 {kind:line,line:{…}} 与 {kind:circle,circle:{centerX,centerY,radius}}。两者各保留唯一可见 Result 以及隐藏 Measurement／Decision／Overlay 投影。

两类结果同时提供 centerDistance、edgeDistance、distanceMode、distanceDefinition、distanceSegment 与 relation，value／公差使用所选方式。圆圆边缘间距为 max(D−rA−rB,|rA−rB|−D,0)，线圆边缘间距为 max(D−r,0)。零距交点必须落在两条实际几何上，不以内部投影点冒充圆周；严格同心圆取 +X 方向示意。失选／不可用时新距离及测量段为空，真实的相交零距离有效。新增元数据和 SDK 属性可空，旧圆心距 JSON 仍可读取；GetGeometricMeasurement 提供两类测量的统一强类型入口。

结果图形默认色集中在 native/core/graph.h 的 DefaultResultColor=#00FF00。拟合点与其圆／直线使用同一结果颜色；检测器、轮廓／几何标记、位置修正轴、双边卡尺和距离测量均使用绿色默认值。SDK／Studio 缺省图元颜色同为绿色；显式指定的合法配方颜色继续生效，编辑画布与 ROI 状态不套用结果色。

MeasureLineDistance（线线距离）使用 first/second 两个 Features 输入，result 是唯一可见端口，measurement/decision/overlay 为隐藏投影。MeasureGeometry 增加 operation=lineDistance，两者共用计算回调及输出契约。result 保持 geometryMeasurement，新增 distanceDefinition=firstMidpointToSecondLine、lineAngleDegrees、parallelAngleToleranceDegrees 和 distanceSegment={start:{x,y},end:{x,y}}。value 是 A 中点至 B 无限直线的垂距，单位 px；方向相反仍按 0～90° 最小夹角比较。默认容差 1°、可调 0～10°，超限时 valid=false、value=null、distanceSegment=null、decision={status:Unknown,reason:NonParallelLines}，仍保留源直线和实测夹角。缺目标／参数不可用时也保持空值及三态判定，不生成虚假测量线。源直线和测量线端点均为原图坐标。

SDK GetGeometricMeasurement 返回 VisionGeometricMeasurement，新增字段可选，DistanceSegment 为 VisionDistanceSegment。value、lineAngleDegrees 及测距端点坐标登记为可空 Double 字段供绑定。WPF 直接显示定义、距离、夹角、两端点和公差判定；完整 JSON 可通过既有按钮导出。

FindLines 新增 method=caliper，与旧 LineCaliper 共用真实采样／拟合函数；method 原生缺省 automatic 保留旧配方，编辑器新节点缺省 caliper。caliperPlacement=roiCenter 使用 rectangle／rotatedRectangle 的中心线及高度，reverseSearch 反转搜索法线。带宽方向是 ROI 高度，projectionWidth 是每把卡尺沿切向的奇数投影宽度，两者独立。ROI 搜索带不裁掉其两侧采样窗口，但保留上游有效像素掩膜。

卡尺拟合直线仍从唯一 result 口输出，features 中提供 Line 的端点、中点、长度、角度、方程、RMS 与 edgePoints；measurement.caliper 提供窗口和拟合诊断。C# 可用 GetResult<VisionLine>(publicName) 读取，未找到时 Selected=null。Fixture 会保留中心线排列，旋转与缩放旋转矩形及搜索高度；轴对齐参考矩形先转为旋转矩形，避免退化成多边形边采样。

圆卡尺诊断在 measurement.caliper 增加可选字段：fitSucceeded、fitFailureReason、minimumRequiredPoints、requestedRemovalCount、maximumDeviation、fitIterations。失败代码包括 InsufficientEdgePoints、InsufficientInliers、DegenerateGeometry、NumericalFailure、InvalidCircle、UnavailableSamplingArea；成功时失败代码为空串。maximumDeviation 是本次有效的原图像素阈值，Fixture 变换后记录实际缩放值。C# VisionCaliperDetails 对新增字段使用可空属性，旧 Runtime 或非圆卡尺缺失字段时保持 null，不假定成功。空目标仍是 count=0、features=[]、selected=null；拒绝用 ROI 或失败候选构造圆环。

集成检测模块在画布上显示 `result: Result`，标签“全部结果”。实际值是完整 JSON 对象：`featureKind`、`features`、`measurement`、`count`、`found`、`passed`、`overlay`。几何坐标继续使用原图像素坐标；measurement 中的选中对象、卡尺和检测诊断保持原有契约。空结果提供空 features 与 selected=null。

原 Features 主输出与 Measurement、Int、Bool、Overlay 辅助输出仍在目录中，标记 hidden=true。算法主返回值不变，Result 使用现有 Json 载体；无需修改 C ABI 1 或旧配方 schema。

新配方自动公开可见 Result 输出；已有明确公开名称、字段类型和 Bool 判定不改写。旧连线在画布上汇集到唯一结果口，保存的端口 ID 仍可被旧流程解析。新连线到 Features、Measurement、Int 或 Overlay 输入时，编辑器选择同类型字段；Bool 有 found/passed 两个候选，须由用户选择。直接 Result 到 Result 连接保留整个包。原生执行器继续严格验证端口类型，不把任意 JSON 静默转换为几何值。

SDK `GetResult<T>(publicName)` 和 `GetNodeResult<T>(nodeId)` 返回 VisionResultPacket<T>，其中 T 是 VisionCircle、VisionLine、VisionRegion 或 VisionMatch。公开结果不依赖调试保留，节点完整结果需要 Debug/Preview。失败执行不能读取结果；packet 及每次保留／公开复制计入复杂结果预算。

## 当前源码：图像上下文与匹配后测量

Image 输入的集成检测器追加隐藏 image:Image 输出，Result JSON 增加 image 元数据（available、padded、尺寸、像素格式和 origin）。关联像素在 Runtime 内保持；Result→Image 连线只在该真实隐藏输出存在时允许，保存 fromPort=image。普通没有图像上下文的 Result 不能伪装为图像。AnalyzeContours 输入为 Contours，仍不声明 Image 投影。

匹配按 resultIndex 选择当前目标，image.matchPosition 提供原图中心、角度及尺度；实际 Image 同时保存这份定位上下文，后续预处理／裁剪保持它。downstreamImageMode=source 返回匹配器搜索 ROI 之前的实际输入，target 返回选中匹配轴向框，downstreamPadding=0～2048 px 只用于 target。新编辑器默认 source，原生缺省 target 保留旧配方。扩边固定画布，越过原图部分无效；源父掩膜始终生效。无目标／越界索引 available=false 且清除 matchPosition，下游返回空结果。

FindCircles 的 positionReference=image 保持旧局部 ROI，matchCenter 必须有有效匹配，auto 有定位时跟随、否则使用局部 ROI。新编辑器默认 auto，旧配方原生缺省 image。跟随模式圆心=匹配中心+matchOffsetX/Y，圆半径取所绘 ROI 或 matchSearchRadius；caliper 强制沿该参考圆布置。measurement.position 记录原图模板中心及搜索中心。匹配中心只是初始定位，实际 features 圆心必须来自检测／拟合。

每个圆 feature 增加 circle（实测轮廓）与 outputAnnulus（原图圆心、内／外半径、0～360°）。输出带宽由 outputAnnulusWidth 控制，默认 4 px，可设 0 表示退化为圆周；这不是材料内外径测量。caliper.searchAnnulus 独立记录实际参考采样区域。overlay 中 annulus 是独立图形类型，拟合 circle 仍单独保留；中心十字线也是标记，不增加检测数量。

`GetOutputImage(publicResultName)` 可取得公开 Result 的关联图像，无须 Debug；旧公开 Image 行为不变。`GetInputImage(nodeId)` 取得保留的检测器实际输入，`GetIntermediateImage(nodeId)` 对匹配器按 downstreamImageMode 取得原图或目标图，两者均按 Debug/Preview 保留。新输入图像函数是 C ABI 1 的增量导出，需使用同次编译的 SDK 与 Runtime。

## 工具箱

通过 toolboxVisible=false 隐藏 CropRect、CircleCaliper、CountFeatures、BuildFeatureOverlay、CompareNumber、CountContours。它们已在检测模块的 ROI、圆卡尺、数量与标记参数中提供；注册、SDK 和旧节点执行保持。LineCaliper 等有独立用途的算法仍显示。BuildContourOverlay 能输出真实轮廓点与可调线型，继续保留。

## 验证范围

增加纯编辑器模型、SDK JSON 边界和真实 native 结果包测试，并增加 WPF single-result 场景：单口显示、内置模块隐藏、完整公开数据、typed 字段连接和保存重载。构建与验证记录见 verification.md。本次不生成安装包、ZIP 或 NuGet；既有 0.7.2 客户包保持原样。
