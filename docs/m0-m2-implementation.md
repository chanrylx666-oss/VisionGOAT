# M0～M2：定位、ROI 与集成测量

2026-10-15 源码增量，对应 next-iteration-prompt.md。版本仍标识 0.7.2，旧客户包不包含这些功能；本轮仅生成独立 Debug。

## 操作闭环

1. 外部图像／灰度处理连接 TemplateMatch；模板与搜索 ROI 分开设置。旋转和尺度搜索使用此入口，快速匹配／快速定位继续面向主要平移场景。
2. 加入 PositionFixture，分别连接测量 Image 和匹配 Pose（从“全部结果”投影），点击“使用当前匹配建立基准”。图像不必是模板裁图。
3. 从位置修正分出两个圆查找，选择“卡尺拟合”和“跟随位置修正”，在参考图绘制两个测量 ROI。模板中心、圆心互相独立，圆可位于模板框外。
4. 两个圆连接 MeasureCircleDistance 的 A/B 输入；设置绝对上下限或名义值与上下偏差。
5. 在公开输出窗口发布距离 Result、尺寸 Decision，以及需要的 Overlay。SDK 使用相同配方独立执行，不依赖编辑器。

| 能力 | 主要实现位置 | 当前边界 |
|---|---|---|
| 多输入与字段依赖 | native/core/graph.cpp | 执行完整输入集合；外部保持一个 Image；来源均为本次 Run |
| 位置修正 | native/core/fixture.cpp、Editor.Core/FixturePositioning.cs | 平移、旋转、等比缩放，不做任意仿射 |
| 高级 ROI | native/core/roi.cpp、ImageEditorView.Arcs.cs | 旋转矩形、圆环／扇环，绘制、数值编辑、掩膜、保存、渲染贯通 |
| 双边卡尺／宽度 | components/caliper_components.cpp | 真实两边、极性、方向、宽度／中心／对比度、投影诊断，不冒充通用壁厚 |
| 几何测量 | components/measurement_components.cpp | 点点、点到无限直线、最小线夹角、无限线交点、两圆中心距 |
| 集成公差 | components/measurement.h | 包含上下限端点，按未舍入数值判断，Pass／Fail／Unknown |
| 输入与参数绑定 | FlowDocument.Bindings.cs、MainWindow.Bindings.cs | 来源下拉、字段选择、运行到选中、复制重映射、撤销重做 |
| SDK／WPF | Sdk/Fixture.cs、GeometricMeasurement.cs、Net8.WpfProbe | 真正的 .NET 8 Windows 宿主执行与图形渲染 |

## 坐标、ROI 与卡尺

公开几何统一为原图像素：X 向右、Y 向下，角度为度，顺时针为正。参考点按 `T_current * inverse(T_reference)` 映射。origin 只表示裁图平移，完整变换另外保存在 fixture。图、Pose、Features 只能从同一不可变计划／输入帧派生，由本次 Run 的 runId/frameId 标识；没有注入其他帧 Features 的接口。

ROI 编辑逆变换保存到参考坐标，每帧从基准计算，不在上一帧 ROI 上累乘。模板改变需重新教学；基准改变后，带旧 referenceHash 的 ROI 拒绝执行并提示重画。

```json
{"shape":"rotatedRectangle","cx":180,"cy":120,"width":80,"height":30,"angleDegrees":30}
{"shape":"annulus","cx":180,"cy":120,"innerRadius":50,"outerRadius":78,"startAngle":300,"endAngle":540}
```

扇环跨度 10～360 度，跨 0 度使用 endAngle>360；零跨度非法。Fixture ROI 可附 coordinateSpace=reference 和 referenceHash。部分越界尊重有效掩膜；边界卡尺在路径两侧采样，不把补黑边识别为边缘。

卡尺数量不缩放，宽长随等比尺度改变；投影宽归一为奇数，高度为偶数，诊断记录真实窗口及 validSamplingCalipers。完全没有有效采样区或有效窗口少于要求时 dataAvailable=false，不能用于期望零目标的通过判定。双边 ROI 的宽为投影宽、高为搜索长；候选宽度范围随 Fixture 缩放，灰度阈值和尺寸公差不缩放。每节点采样预算最多 16 Mi 次，实际投影宽最多 631；双边各保留最多 128 个候选并记录截断。

圆环／扇环搜索输出实际拟合的圆，没有冒充圆弧起止点识别；输出圆环是配置的显示带宽，不是材料厚度。线新增可选 equation={a,b,c}，表示原图 a*x+b*y+c=0，a²+b²=1，首个非零法向分量为正。点线距离用无限直线，夹角为 0～90 度；平行／近似平行交点返回无效与 null，不输出假原点。

## 绑定与不可用结果

显式 bindable 参数可使用常量、公开输入，或目录声明的上游标量字段。右侧“绑定结果字段／公开输入”选择，例如：

```json
"upperLimit": {"node":"reference","port":"result","field":"value"}
```

隐藏标量端口使用空 field。允许 Int→number，不允许 Double→integer，不接受任意 JSON 路径。绑定参与类型校验、环路、祖先、摘要和引用计数。运行到选中节点执行其完整依赖，不执行无关草稿。Shift 单击多选、Ctrl+D 复制：重映射内部连线与字段，保留外部来源及公开输出名；拖动和删除仍按当前单节点处理。删除来源保留明确失效绑定，重新选择／改常量或撤销可修复。

匹配自身成功执行但没有目标，仍是有效的空检测，显式期望数量 0 可 Pass；它下游需要匹配图像／位姿的测量没有有效输入，应跳过并为 Unknown。不存在的选中目标／字段不会回退目标 0、全图或上次值。依赖不可用参数的节点为 Skipped；测量 valid=false、value=null、decision=Unknown。parametersAvailable=false 时，SDK 的 limit 占位数字不能参与判断；unavailableParameters 列出未解析来源。旧 passed 保持数量规则原义，dataAvailable=false 不能当成正常零目标。

inspection 可以用 Bool 或 Decision 公开输出作为 decisionOutput。旧 Bool 成功路径保留 Pass/Fail；其必要祖先跳过时总判定 Unknown。未启用公差也是 Unknown，错误／取消／超时清空公开结果。

## SDK 与复现

输出名由用户配置，不固定节点 ID。例如：

```csharp
using var result = await session.RunAsync(frame, new RunRequest { Debug = false });
var distance = result.GetGeometryMeasurement("Distance"); // 两圆中心距
if (distance.ParametersAvailable && distance.Valid)
    Console.WriteLine($"{distance.Value} px; {distance.Decision.Status}");
var width = result.GetGeometricMeasurement("Width"); // 双边、点线、夹角、交点
var fixture = result.GetResult<VisionCircle>("CircleA").Measurement.Fixture;
using var image = result.GetOutputImage("CircleA");
viewer.PresentCopy(image, result.GetOverlays("Overlay")); // UI Dispatcher
```

GetGeometryMeasurement 与 GetGeometricMeasurement 各读取上述对应契约。VisionFixture 提供正／逆点变换。旧端口、默认值、平移跟随和 ROI 仍可读；新节点／形状在旧 Runtime 明确拒绝，使用配套 DLL。C ABI 保持 1。

Debug 编辑器：artifacts/local-debug/m0-m2/JP-Vision.exe。真实 net8 测试工具为 managed/JPVision.Net8.WpfProbe，引用 SDK／WPF，不引用 Studio，不绕过许可。输入为 fixture-circle-distance 窗口流程保存的配方与 PNG：

```powershell
dotnet build managed/JPVision.Net8.WpfProbe/JPVision.Net8.WpfProbe.csproj -c Debug -o artifacts/local-debug/net8-host
./artifacts/local-debug/net8-host/JPVision.Net8.WpfProbe.exe D:/open500/JP-Vision D:/open500/JP-Vision/artifacts/local-debug/m0-m2/fixture-circle-distance-saved.vrecipe D:/open500/JP-Vision/artifacts/local-debug/m0-m2/fixture-circle-distance.input.png D:/open500/JP-Vision/artifacts/local-debug/net8-host/fixture-overlay.png
```

模型按完整摘要与解码版本缓存只读灰度／掩膜，最多 16 个／8 MiB，不缓存跨帧位置。原有内存预算、Busy、所有权和节点边界取消保留。GUI 检查通过实际 WPF 窗口内部流程驱动，非外部鼠标自动化、相机实测或产线精度验收。准确通过数量及最终产物见 next-iteration-status.md、verification.md。

M3～M6 的集成 Blob、ForEach、海康／大恒 GigE、多相机、标定、OCR 和通讯尚未实施；没有毫米坐标、任意仿射或跨帧身份追踪。商业软件精度／速度等效和实物重复性未承诺。
