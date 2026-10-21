# 不规则边缘点集与点到线距离：实现约定

## 目标和范围

用户选择“绘制搜索区域，自动沿图像边缘提取点集”。在长条工件的不规则边界上保留真实边缘采样点，不将它们强制拟合为直线或圆；将这些点与现有直线查找的拟合结果组合，得到每个点到无限直线的垂距及统计。沿用 WPF + C++ OpenCV、单个可见图像输出、独立绿色标记、离线授权和 VS F5 源码开发方式。

本次增加两个工具：`FindEdgePoints`（边缘点集）和 `MeasurePointLineDistances`（点集到线距离）。先支持矩形／旋转矩形搜索带；不新增手工逐点绘制、闭合任意曲线拟合、像素到毫米标定或相机驱动。

## 业务流程

可以串联“外部图像 → 直线查找 → 边缘点集 → 点集到线距离”，也可以先取点再找线。测量模块既支持同一图像携带点集与直线，也支持点集图像和参考线图像分别接入。结果来自同次运行的同一坐标平面，不重新识别图像替换已经找到的参考线。

沿搜索带宽度方向采样，法向跨越高度搜边。站数 `floor(width / sampleSpacing) + 1`，第 i 站位置为 `-width/2 + i*sampleSpacing`；反向搜索只改变法向，保持站序。每站只保留按极性和第一／最后／最强规则选出的真实边缘点。未检出站不生成坐标，也不连接跨越缺口的折线。

## 参数与输出

边缘点集参数包含 ROI、采样间距、投影奇数宽度、平滑 Sigma、边缘阈值、极性、选边方式、搜索反向、均值／中位数投影、亚像素开关、最小覆盖率、最大点数及显示选项。默认绿色 `#00FF00`，像素保持干净。无 ROI 时返回明确空结果，不自动在全图找任意边。

结果 JSON `resultKind=edgePointSet`，含 `dataAvailable/valid/reason/count/stationCount/missedCount/coverage`、坐标平面和 `points[{index,stationIndex,x,y,gradient,pathPosition}]`。`pointSet:PointSet` 隐藏输出为同一结构，进入图像上下文；Result、count 和独立 overlay 可供 SDK 或参数订阅读取。

测量模块的 `input` 为点集图像，`line` 为可选参考线图像；只有 `input` 时使用它同时携带的直线。`lineIndex` 按直线子序选择。距离按无限直线正交投影计算；规范参考方向为 dx>0，或 dx≈0 时 dy>0，再以单位法向计算有符号距离。统计使用绝对垂距。

结果 `resultKind=pointLineDistances, operation=pointSetToLine, unit=px`，每项包括点索引、原点、投影点、距离和有符号距离。输出最小值、最大值、均值、RMS、标准差、极差、对应极值点和参考直线。`statistic` 选择供 `value` 与公差判定使用的统计项，默认最大值。图中抽样显示测距线，但完整距离数组不抽样。

## 坐标、缺失与资源边界

- 点和直线使用当前平面的绝对像素坐标；裁剪图的原点只加一次。原图和展开平面、两个独立展开分支不得混用。
- 最新空点集遮盖先前点集；未检出、覆盖不足、缺少参考线或参数不可用时统计为空、判定未知，不使用 0 或上一帧结果。
- 覆盖不足可保留真实点供诊断，不能将其冒充通过的点集。数据不可用时点数组为空。
- 最多 10000 个采样站、16 Mi 个采样工作量；超限拒绝，不静默截断。图形预览可减少绘制密度，实际点集和测量输出保留完整。
- 使用现有卡尺投影、峰值筛选和亚像素实现，公开最小采样 helper，避免复制边缘算法。

## 结构与代码风格

原生算法位于 `native/core/components/edge_point_components.{h,cpp}`；共享投影 helper 复用 `caliper_components.cpp`。注册和流水线仍位于原有组件注册与 `image_pipeline.cpp`。WPF 增量采用 `MainWindow.EdgePoints.cs` 等现有 partial 模式，数值解析、ROI 和订阅复用现有编辑模型。SDK 和测试分别新增独立文件。

遵循 C++20、项目 `.clang-format`，原生边界使用 `require`；C# 使用现有 records、`JsonElement` 和 xUnit 风格。示例契约：

```json
{ "index": 0, "stationIndex": 0, "x": 100.25, "y": 80.5,
  "projectionX": 100.25, "projectionY": 150.0,
  "distance": 69.5, "signedDistance": -69.5 }
```

## 验证和命令

用合成波浪边缘及独立直线验证逐点几何、旋转搜索带、裁剪原点、缺失边段、极性、两种执行顺序、单／双图像输入、空帧与覆盖不足、坐标平面、资源上限和参数订阅。WPF 窗口验证实际绘制、参数调整、距离列表、保存重载、绿色标记和干净像素；不声称已完成实物精度验证。

```powershell
& 'C:/Program Files/Microsoft Visual Studio/18/Community/MSBuild/Current/Bin/MSBuild.exe' JPVision.sln /t:Build /p:Configuration=Debug /p:Platform=x64 /restore /m:1 /nologo /verbosity:minimal
dotnet test managed/JPVision.Tests/JPVision.Tests.csproj -c Debug --no-build --no-restore --filter 'FullyQualifiedName~EdgePointDistanceTests'
. ./scripts/common.ps1
Initialize-JpvEnvironment -RuntimeOnly
& $JpvCtest --test-dir artifacts/build/runtime -C Debug --output-on-failure
```

只修改授权范围内源码和文档，运行必要回归；不读取私钥，不修改第三方库，不打包或发布。新结果类型通过加法扩展，不改变原有圆、线、测距及 WPF SDK 契约。
