# C# / WPF 集成

当前源码提供可选 net8.0 `JPVision.Acquisition`：`MvsCameraProvider` 经独立原生桥接使用已安装的海康 MVS，`GlobalCameraService.CaptureNextAsync` 等待业务请求后的新帧，通过 `CameraFrame.ToVisionFrame()` 交给现有 Session。全局预览与检测结果分开，停止／故障不会回退到旧预览帧；`RunRequest.FrameId` 可保存 SN、帧号与接收 QPC。仅支持单台海康 GigE 自由连续采集，完整项目引用方式、代码与超时／Busy 语义见 [全局相机图像源与 SDK](camera-source.md)。缺少 MVS 不影响文件图像运行；历史 NuGet 不包含此增量。

`GetBlobAnalysis(name)` 可读取公开 Blob Result 或图像附带的 Blob 结果，返回候选／筛除／合格数量、可空总面积、完整 `VisionRegion` 列表、选中区域和标记。Blob 面积为真实前景像素数，周长为含孔洞的像素边界周长；新增区域属性 `AreaKind`、`AspectRatio`、`Extent`、`TouchesBoundary` 可空，保留旧轮廓结果兼容。操作、空结果语义和代码见 [Blob 分析与 SDK](blob-analysis.md)。

`GetEdgePoints` 可读取有序真实边缘点；`GetPointLineDistances` 可读取完整逐点垂距、投影、有符号距离和统计，均支持结果或图像公开输出。类型、业务连接方式和代码示例见 [边缘点集与距离集](edge-points.md)。

`RunRequest.Variables` 可按本次执行覆盖配方已声明的全局变量；局部变量及计算函数由运行核心逐帧求值。`GetGlobalVariable<T>`、`GetLocalVariable<T>`、`GetResolvedParameters` 可读取实际变量和参数；图像基准 ROI 返回模块输入局部坐标，教学参考 ROI 保留声明的参考坐标。不存在的目标不会沿用历史坐标。使用示例和配方格式见 [参数订阅、变量与计算函数](parameter-subscriptions.md)。

新增圆环展开的 GetAnnularMapping(imageOutput).MapToSource(x,y)，可将展开平面的像素位置映射到直接上游源图；裁剪后保留展开平面的原点偏移。展开后的检测/测量包 CoordinateSpace 为 annularUnwrapped，原图仍为 original，不能混合不同坐标平面测距。详见 [圆环展开](annular-unwrap.md)。

当前源码新增统一 Image 流程：公开 Image 可同时通过 GetOutputImage 取干净像素、GetOverlays 取独立标记，检测与测量 getter 可读取图像附带的数据。测量模块支持单图 A/B ROI 内置真实卡尺检测。详见 [统一图像流程](image-pipeline.md)。

## 圆圆与线圆距离（源码增量）

MeasureCircleDistance 现显示为“圆圆距离”，默认 distanceMode=centerDistance；切换 edgeDistance 测两条圆周最短间距。旧配方缺少此参数仍测圆心距，原 first/second 平铺圆几何和 GetGeometryMeasurement 保持兼容。新 MeasureLineCircleDistance 的 first 为直线、second 为圆，默认圆心到无限直线的垂距，也可切换为圆周最短间距。

两类测量均可使用统一读取方法，继续引用当前源码 SDK 与配套 Runtime：

```csharp
using var result = await session.RunAsync(frame);
if (result.Status == ExecutionStatus.Succeeded)
{
    var m = result.GetGeometricMeasurement("Distance");
    double? selectedPx = m.Valid ? m.Value : null;
    double? centerPx = m.CenterDistance;
    double? edgePx = m.EdgeDistance;
    VisionDistanceSegment? segment = m.DistanceSegment;
    var circle = m.Second?.Circle; // 圆心、半径；线圆的 First.Line 为拟合线
    var graphics = result.GetOverlays("Overlay"); // 在本次 frame 上显示
}
```

新字段 distanceMode、distanceDefinition、centerDistance、edgeDistance、distanceSegment、relation 均随公开 Result 返回，Debug=false 可读取。两种数值始终同时输出，value 和 decision 使用所选方式；centerDistance、edgeDistance 及测距端点坐标可绑定下游参数。图形默认 #00FF00。

圆圆 operation 为 circleCenterDistance 或 circleEdgeDistance，原 first/second JSON 不改形状。GetGeometricMeasurement 将旧平铺圆数据适配到 First.Circle／Second.Circle，旧 GetGeometryMeasurement 仍返回原圆字段；旧 Runtime 缺少新增元数据时，新属性为 null。线圆 operation=lineCircleDistance，first={kind:line,line:{…}}，second={kind:circle,circle:{centerX,centerY,radius}}。SDK 会校验有限非负距离、所选方式与 value 一致、有效端点及正半径。

长度为原图 px。圆周相交／相切时边缘间距为有效的 0；包含／同心圆返回圆周之间的真实间隙。未找到或参数不可用时 Valid=false、距离和测量线为空、Decision.Unknown，不能把空值当作测得 0。完整业务定义见 [编辑器指南](studio-guide.md)。

## 线线距离（本地源码增量）

MeasureLineDistance 接收两个 Features，编辑器会将直线查找的可见 Result 连接映射到 Features。公开 result 为 Distance、overlay 为 Overlay，decision 为 Decision；这些公开名称可由业务工程自定义。MeasureGeometry 的 operation=lineDistance 使用相同实现和 JSON 契约。只更新项目引用的 SDK 和配套原生 DLL，历史 NuGet 不包含此功能。

```csharp
using var result = await session.RunAsync(frame);
if (result.Status == ExecutionStatus.Succeeded)
{
    var measurement = result.GetGeometricMeasurement("Distance");
    double? distancePx = measurement.Valid ? measurement.Value : null;
    VisionDistanceSegment? segment = measurement.DistanceSegment;
    double? acuteAngle = measurement.LineAngleDegrees;
    var overlays = result.GetOverlays("Overlay"); // 在本次输入 frame 上显示
    // segment.Start/End 为原图坐标；无效时 segment 和 distancePx 均为空。
    // 用 Decision.Status / Reason 区分公差结果及 NonParallelLines 等原因。
}
```

新增可选属性 DistanceDefinition、LineAngleDegrees、ParallelAngleToleranceDegrees、DistanceSegment；其他几何算子缺少这些字段时仍正常读取。lineDistance 的定义为 firstMidpointToSecondLine：A 的拟合线段中点到 B 无限直线的垂距，轻微倾斜时的测量位置固定在 A 中点。角度容差默认 1°、范围 0～10°；超过容差或缺少直线时 Valid=false、Value=null、Decision.Unknown，不得把空值转换为检测通过的 0。长度与端点为原图像素，尚无毫米标定。

同次运行的公开 Result、Overlay 和 Data JSON 在 Debug=false 时也可读取；内部未公开的节点完整结果仍遵循调试／预览保留规则。下游字段绑定可选 result.value、result.lineAngleDegrees、result.distanceSegment.start.x/y 和 result.distanceSegment.end.x/y，坐标均为原图坐标。图像继续使用宿主传入的 frame。

## 引用与支持

SDK 为 net8.0，WPF 控件为 net8.0-windows；源码已在真正的 .NET 8.0.27 Windows x64 宿主执行 Fixture／两圆／距离／公差并渲染 WPF。net6.0 不可直接引用。新功能只在源码和本地 Debug，未更新历史 NuGet，现阶段使用项目引用；复现见 [M0～M2 契约](m0-m2-implementation.md)。下面 NuGet 示例是历史版本用法，不包含本轮增量。

将 `artifacts/packages` 加为本地 NuGet 源，在 SDK-style WPF 工程中添加：

```xml
<PropertyGroup>
  <PlatformTarget>x64</PlatformTarget>
  <RuntimeIdentifier>win-x64</RuntimeIdentifier>
</PropertyGroup>
<ItemGroup>
  <PackageReference Include="JPVision.Sdk" Version="0.7.0" />
  <PackageReference Include="JPVision.Wpf" Version="0.7.0" />
</ItemGroup>
```

SDK 包原生文件位于 runtimes/win-x64/native；发布 win-x64 时 NuGet 负责拷贝到消费端输出目录。[微软原生资产约定](https://learn.microsoft.com/en-us/nuget/create-packages/native-files-in-net-packages)。本次 PackageConsumer 使用独立包缓存、独立发布目录，并移除开发 Qt/OpenCV PATH 后实际执行。

## 初始化与工位

0.4.1 支持仅图像处理的 `kind=processing` 配方，例如 ExternalImage → ToGray。公开灰度图输出后，可用 `result.GetOutputImage("GrayImage")` 获取像素。处理成功时 Status=Succeeded、Decision=Unknown；只有 `kind=inspection` 配方具有合格判定。`recipe.Description.GetProperty("kind")` 可区分用途。文件图与已接入的海康 GigE 图像均通过同一帧契约运行，见 [全局相机图像源](camera-source.md)。

```csharp
using JPVision.Sdk;

var status = VisionLicensing.GetStatus();
if (!status.IsValid)
    throw new InvalidOperationException(status.Message);

using var engine = new VisionEngine();
using var recipe = engine.LoadRecipe(@"C:\Recipes\parts-count.vrecipe");
var contract = recipe.Description; // 已编译配方的稳定 inputs / outputs
await using var station = recipe.CreateSession();

using var frame = VisionFrame.CopyFrom(
    cameraBytes, width, height, positiveStride, VisionPixelFormat.Bgr24);
using var result = await station.RunAsync(frame, new RunRequest
{
    RunId = productionId,
    FrameId = capturedFrameId,
    Inputs = new Dictionary<string, object?> { ["ExpectedCount"] = 3 },
    Debug = false,
    TimeoutMs = 500
}, cancellationToken);

if (result.Status == ExecutionStatus.Succeeded)
{
    int count = result.GetOutput<int>("ObjectCount");
    bool passed = result.Decision == InspectionDecision.Pass;
    // 原有业务工程决定 PLC/MES 动作。
}
else
{
    // result.Data["error"] 包含 code / nodeId / message；禁止当成 Pass。
}
```

引擎、配方、工位会话在初始化或换型时创建，不要每帧加载配方。每个工位使用独立 Session；同一 Session 顺序 await，或者在 Busy 时按业务策略处理。相机图像为 BGRA/RGB/16-bit、负 stride 或其他布局时，先由宿主转换成支持格式。

RunAsync 返回前已完成原生任务接受和像素所有权转移，可在返回 Task 后 Dispose 输入 VisionFrame；C ABI 内部仍保活其复制的像素。建议以 using 范围直观管理。CancellationToken 取消和 TimeoutMs 超时在原生收尾后分别抛 OperationCanceledException / TimeoutException。Session.Dispose 请求取消；DisposeAsync 请求取消并等待，支持重复释放。

## 图像控件

```xml
<Window xmlns:vision="clr-namespace:JPVision.Wpf;assembly=JPVision.Wpf">
  <vision:VisionView x:Name="VisionDisplay" />
</Window>
```

```csharp
await Dispatcher.InvokeAsync(() =>
{
    VisionDisplay.PresentCopy(frame, result.GetOverlays("ResultOverlay"));
});
```

调用 PresentCopy 时 frame 和 result 仍需有效，复制完成后即可释放。鼠标滚轮缩放，左键拖动平移，Fit 恢复适应；ShowOverlays 控制图元显示，PixelPositionChanged 返回原图坐标和像素值。控件接受 polyline、line、rectangle、circle 和 text；显示坐标减去帧 origin，从而保持 ROI 叠加对齐。

调试时将 Debug 设为 true，`result.GetIntermediateImage(nodeId)` 取得保留的 Image/Mask；这是调试接口，正常生产取结果应使用稳定公开名称。公开 Image 输出通过 `result.GetOutputImage(name)` 取得帧，与中间图接口使用独立名称空间，避免节点与业务输出重名时取错图像。

## 激活和错误

几何配方通过 `GetCircles("Features")`、`GetLines("Features")`、`GetRegions("Features")` 读取托管结果；名称取决于公开输出配置。0.4.0 的卡尺仍使用 GetCircles/GetLines，额外有 EdgePoints 和 RmsError；模板通过 `GetMatches("Features")` 获取中心、角度、尺度、分数与 Corners。所有坐标为原图像素坐标。先检查 Succeeded，再读取结果并用独立 Overlay 输出显示。模块 ROI 与模板模型随配方保存，宿主无需 Qt、模板源文件或重复教学。完整示例见 [组件开发](component-authoring.md) 和 [编辑器操作](studio-guide.md)。

`JPVision.Wpf.LicensePanel` 可放入宿主窗口，提供状态、导出请求和导入许可；宿主订阅 StatusChanged 控制业务按钮。也可使用 `VisionLicensing.GetMachineRequest()` 和 `Import(file)` 自行构建 UI。Runtime 不弹窗、不强制退出宿主。

## 直接读取检测算子的测量结果

0.6.0 可用两节点示例 `circle-measurement.vrecipe`，无须额外接计数和标记节点。公开 `measurement/count/passed/overlay` 端口分别对应示例的 `CircleMeasurement/ObjectCount/InspectionPassed/ResultOverlay`；公开名称由配方决定，不能假定任意客户配方均使用相同名称。

```csharp
using var result = await session.RunAsync(frame, new RunRequest {
    Debug = false,
    Inputs = new Dictionary<string, object?> { ["ExpectedCount"] = 3 }
});
if (result.Status == ExecutionStatus.Succeeded) {
    var measurement = result.GetMeasurement<VisionCircle>("CircleMeasurement");
    // 也可按稳定节点 ID 读取，不需要公开 Measurement 端口：
    var nodeMeasurement = result.GetNodeMeasurement<VisionCircle>("detect");
    if (measurement.Selected is { } circle) {
        double x = circle.CenterX, y = circle.CenterY;
        double radius = circle.Radius, diameter = circle.Diameter;
        double area = circle.Area, perimeter = circle.Perimeter;
    }
    // measurement.Count / Found / Passed / SelectedIndex / SelectedValid
    // measurement.Caliper?.RequestedCount / RetainedCount / RejectedCount / MissingCount
    imageView.PresentCopy(frame, result.GetOverlays("ResultOverlay"));
}
```

`GetMeasurement<VisionLine/VisionRegion/VisionMatch>`、`GetNodeMeasurement<...>` 同样适用。公开几何数组仍使用 GetCircles/GetLines/GetRegions/GetMatches，提供完整多目标列表。选择索引从 0 开始，未找到目标或索引越界时 Selected=null；Found 只表示总列表非空，Passed 只表示数量处于模块指定范围。应先检查执行成功，再决定如何处理空测量。失败、超时、取消清空 measurements；节点没有执行或类型不匹配时 SDK 明确抛出异常。

`circle-caliper-measurement.vrecipe` 在圆查找模块内配置卡尺，示例 ExpectedCount=1；边缘点为剔除后的有效点，Caliper.RejectedPoints 提供被剔除的原图坐标。配置剔除点数与偏差阈值可能共同剔除更多点，实际数量以 RejectedCount 为准，MissingCount 表示没有采到边缘而不是剔除。RmsError 是保留点到拟合几何的 RMS 距离。坐标和尺寸仍是像素，未包含物理单位标定。

已有五参数 VisionFrame.CopyFrom 保持兼容；新增带 originX/originY 的七参数重载可传入裁剪图在原图中的原点。Runtime 会将模块 ROI 的局部偏移累计到输出坐标。

默认授权目录是机器级 ProgramData/JP-Vision。自定义 licenseStore 用于受控安装目录或测试，仍需要相同签名与真实机器绑定，不是绕过授权。签发失败、异机、权限不足应向操作员明确展示，不自动回退为假检测结果。

框架依赖部署需要宿主目标的 Windows Desktop Runtime，以及与编译工具匹配的 x64 VC++ Runtime；客户 WPF 不需要 Qt、MinGW、Qt Creator 或 LicenseIssuer。使用原生 .NET/C++ 同进程调用，不能提供硬实时或原生崩溃隔离保证。

## 0.7.0 算子直接结果与快速匹配

RunRequest 新增 PreviewNodeId，可在 Debug=false 时保留指定已执行节点的完整输出和图像。`result.GetNodeOutput<T>(nodeId, portId)` 读取端口结果；标量和图像元数据始终保留，完整 Features／Contours／Overlay 需 Debug 或本次选中预览。正式 outputs 的名称和类型继续保留，不能直接假定所有配方都叫 Features。

快速模块 `FastTemplateMatch` / `FastLocate` 使用相同 template model 编码，内嵌模型、ROI 和搜索参数随配方传入。公开 measurement 后用 `GetMeasurement<VisionMatch>(name)`，或通过 `GetNodeMeasurement<VisionMatch>(stableId)` 读取中心、位置框和分数。快速定位仅返回最佳单目标，空结果 Selected 为 null；角度固定 0、尺度固定 1。示例为 fast-match.vrecipe 和 fast-locate.vrecipe。

新 WPF 编辑器与工业宿主共享同一 SDK / 原生 DLL，C ABI 仍为 1；SDK net8 目标保持。GetEditorStatus 查询现有 studio 授权功能，GetStatus 查询 runtime 功能，均以真实签名及机器匹配为准。

## 当前源码：匹配中心与圆环输出

新配方配置匹配器 downstreamImageMode=source、圆查找 positionReference=auto（或 matchCenter），即可在同一流程转交实际输入像素和 resultIndex 对应的原图模板中心。圆 ROI 的半径与 matchOffsetX/Y 定义测量位置，method=caliper 时卡尺随参考圆布置；没有 ROI 时使用 matchSearchRadius。旧配方缺省 target / image，既有局部 ROI 不被重解释。

```csharp
using var result = await session.RunAsync(frame);
if (result.Status == ExecutionStatus.Succeeded)
{
    var packet = result.GetResult<VisionCircle>("CircleResult");
    var circle = packet.Measurement.Selected;
    if (circle is not null)
    {
        double x = circle.CenterX, y = circle.CenterY, diameter = circle.Diameter;
        VisionCircleGeometry contour = circle.Circle;
        VisionAnnulus? outputRing = circle.OutputAnnulus;
        VisionAnnulus? samplingRing = packet.Measurement.Caliper?.SearchAnnulus;
        VisionPositionReference? reference = packet.Measurement.Position;
    }
}
```

圆心／半径来自真实边缘检测，不能用匹配中心代替。OutputAnnulus 内外半径为拟合半径加减配置 outputAnnulusWidth/2，下限 0；它是输出区域，不是实测双边内外径。SearchAnnulus 是请求的径向采样带，离散卡尺的具体窗口以 Caliper.Guides 为准。ResultPacket.MatchPosition 从 image 元数据读取本帧匹配参考，无匹配／索引越界为 null；Image 元数据原文继续保留。

GetOverlays 增加 annulus 的 InnerRadius / OuterRadius / StartAngle / EndAngle，当前 JP-Vision 输出圆环为完整 360°，JPVision.Wpf.VisionView 与编辑器均可显示内外边界、拟合圆及圆心十字。宿主自定义渲染器应处理 annulus 类型或直接读取结果包 Overlay。新字段都是增量，旧 Runtime 未提供时可空；Circle 派生几何在旧 Runtime 也可使用。当前版本的原图偏移不自动旋转／缩放，Transform=translation。

## 0.7.1 大图检测

FindCircles 自动模式和 FindLines 新增 largeImageMode（adaptive / original），默认 adaptive。超过 4,194,304 像素时只缩小检测工作图，返回坐标、长度、半径仍为原图像素，并累加原点偏移。`GetNodeMeasurement<VisionCircle/VisionLine>(id).Detection` 提供 Downsampled、InputWidth/Height、WorkWidth/Height、ScaleX/Y；卡尺没有 Detection，继续读取 Caliper。原图、SDK 输入和预览大小不变。

自动缩小会降低小目标和边缘定位分辨率；有精度要求时配置模块 ROI，使用 original 或卡尺。original 超限仍以 Failed / Unknown 返回清空结果，错误中含算子 ID、输入尺寸和处理建议；宿主应先检查 Status，再读取测量。

## 0.7.2 ROI 边界卡尺

FindCircles（method=caliper）、CircleCaliper、LineCaliper 增加 caliperPlacement=guide/roiBoundary。原生缺省 guide，旧配方无需改动；新 WPF 编辑器节点的默认值为 roiBoundary，并随配方保存。圆跟随要求 circle ROI，直接以 cx/cy/radius 为参考；线跟随要求 rectangle/polygon ROI，使用 roiEdge=top/right/bottom/left 或 roiEdgeIndex（从 0 开始，最后一条边闭合回首点）。

跟随 ROI 是参考路径而非本模块硬裁剪。搜索窗跨参考边两侧，但仍尊重上游有效像素限制；不足点输出空测量，不复制 ROI 参数冒充找到目标。搜索方向统一从 ROI 内向外，多边形绘制顺序反向不会翻转该物理方向；线卡尺端点位于等分卡尺站位的拟合范围，不是 ROI 边端点。

`GetNodeMeasurement<T>(id).Caliper` 现有采样／剔除／缺失计数继续有效，并新增 Placement 与 Guides。每个 VisionCaliperWindow 含原图中心 X/Y、单位方向 NormalX/Y、Width 和 Height；Height 是实际跨度 `2*ceil(searchLength/2)`，Width 是奇数像素投影计数。guide 模式／旧 Runtime 的 Guides 为空。所有结果仍需先检查执行成功；没有新增许可证协议或 C ABI 版本。

## 当前源码：一个端口读取全部结果

此功能尚未重新打包；请引用当前 SDK 源码项目并使用同次编译的 Runtime DLL，旧 0.7.2 NuGet 不包含以下新 API。在编辑器“公开输出与判定”中，将检测节点的“全部结果”命名为 `ObjectResult`：

```csharp
using var result = await session.RunAsync(frame, new RunRequest { Debug = false });
if (result.Status == ExecutionStatus.Succeeded)
{
    var packet = result.GetResult<VisionCircle>("ObjectResult");
    foreach (var circle in packet.Features)
    {
        double x = circle.CenterX, y = circle.CenterY, radius = circle.Radius;
    }
    var selected = packet.Measurement.Selected; // 未找到或索引越界时为 null
    int count = packet.Count;
    bool found = packet.Found, countPassed = packet.Passed;
    imageView.PresentCopy(frame, packet.Overlay);
}
```

`VisionLine`、`VisionRegion`、`VisionMatch` 同样支持；`Measurement` 保留卡尺采样、剔除点和检测分辨率等诊断。`Overlay` 原样保留所有图元字段。`Passed` 是模块数量范围判定；配方业务判定仍读取 `result.Decision`。

`GetNodeResult<T>(nodeId)` 用于保留完整结果的调试节点，需要 `Debug=true` 或 `PreviewNodeId` 指向该节点；公开 `GetResult<T>(name)` 无须打开调试。完整包返回托管数据，可在释放 InspectionResult 后保留。旧公开字段、GetMeasurement、GetCircles/GetMatches 和 ABI 1 继续兼容，详见 [结果端口设计](result-port-design.md)。

## 当前源码：匹配图像上下文

匹配的 Result 还包含 `packet.Image` 元数据。先检查本次成功以及 `packet.Measurement.SelectedValid`，然后使用 `result.GetOutputImage(公开Result名称)` 获取选中物品的真实图像。无需增加可见端口或公开单独的 Image 字段；available=false 时 Getter 抛 ArgumentException。

```csharp
var matches = result.GetResult<VisionMatch>("MatchResult");
if (matches.Measurement.SelectedValid)
{
    using var targetImage = result.GetOutputImage("MatchResult");
    // 像素保持原图方向；targetImage.Info.OriginX/Y 指向原图中的裁图原点。
    imageView.PresentCopy(targetImage, matches.Overlay);
}
```

从编辑器配置 Match→Circle 后，Runtime 自动传递定位图像给圆查找；公开圆结果直接读取 `GetResult<VisionCircle>(name)`，无需宿主逐步手工裁图。当前为选中的一个目标；多目标独立测量需要明确迭代设计。模板／搜索 ROI／下游局部 ROI 分开保存。

调试时 `GetInputImage(nodeId)` 返回检测器在自身 ROI 之前的真实输入，匹配器为搜索源图，圆查找为已定位目标图；`GetIntermediateImage(matcherId)` 返回匹配投影图。需要 Debug=true 或对应 PreviewNodeId。来自当前源码的增量 C 导出和 Image 元数据尚未进入旧 0.7.2 NuGet，必须配套当前 Runtime DLL；图像 SafeHandle 保活像素，提取后可独立释放 InspectionResult。
