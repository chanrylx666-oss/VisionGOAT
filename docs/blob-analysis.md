# Blob 分析

在 **检测 → Blob 分析** 添加算子，直接连接图像，即可在模块内完成二值分割、连通区域分析和筛选，输出区域数量、像素面积、质心、包围框及形状数据。组件 ID 为 `BlobAnalysis`。

本功能随当前源码与配套 SDK／原生运行库使用。开发方式仍为 Visual Studio 的 Debug / x64、F5 运行源码；没有新增独立发布包。

## 在编辑器中使用

1. 将“外部图像”或上游模块的图像输出连接到“Blob 分析”。也可接入已经二值化的图像。
2. 在算子参数内绘制或调整矩形、旋转矩形、圆形、圆环或多边形 ROI；选择全图时搜索整个输入图。
3. 选择固定阈值、Otsu 或 Triangle 分割。固定方式使用“图像分割阈值”，范围 0～255、默认 128；大于阈值的像素为白色前景。暗目标可启用“反向图像分割”。
4. 选择 4 或 8 邻域，再调整区域面积、包围框宽高、圆度、宽高比、填充率和排除边界条件。
5. 执行后，在“运行结果”查看候选数、筛除数、合格区域数、总面积和完整区域表。可通过横向滚动查看质心、包围框和形状数据。
6. 按需要设置排序、当前区域索引及合格数量上下限。后续算子可直接订阅选中区域的数据，或继续连接携带结果的图像。

普通 Gray8／Bgr24 图像先转换为灰度再分割；Bgr24 输入的输出仍保留原有干净彩色图像。已有二值掩膜会沿用其前景，不再次阈值分割或反相；此时分割方式、阈值和反向设置不改变现有掩膜。

4 邻域仅连接上下左右像素，8 邻域还连接对角像素。同一组对角接触的小区域可能因此得到不同数量；下拉框仅提供有效的 4、8 两种值。

ROI 使用当前模块输入图的局部坐标，允许贴边或部分越界，仅分析有效交集。ROI 限制搜索范围，不将检测图幅永久裁小；向下游传递的是上游干净图像及本次结果。完全没有有效 ROI 像素时返回不可用结果，不回退全图。

## 筛选和区域选择

所有筛选范围均**包含上下限**，同一个区域必须同时满足全部条件；每组最小值不能大于最大值。

| 条件 | 参数范围和默认值 | 含义 |
| --- | --- | --- |
| 像素面积 | 1～33554432 px²；默认覆盖整个范围 | 实际前景像素数，孔洞不计入 |
| 包围框宽度／高度 | 各为 1～32768 px；默认覆盖整个范围 | 轴对齐包围框的像素宽高 |
| 网格圆度 | 0～1；默认覆盖整个范围 | 按像素边界周长计算，见下文 |
| 宽高比 | 0～32768；默认覆盖整个范围 | 包围框宽度 / 高度 |
| 矩形填充率 | 0～1；默认覆盖整个范围 | 像素面积 / 包围框面积；0.5 表示 50% |
| 排除接触有效边界的区域 | 默认关闭 | 排除触及输入图边缘或 ROI 有效掩膜边界的区域 |

“排除边界”检查的是图像／ROI 的有效采集边界。区域周围的正常黑色背景以及内部孔洞，不会单独导致区域被判为接触有效边界。圆形、多边形 ROI 的掩膜边界也参与检查。

先完成筛选，再按面积降序／升序、质心 X／Y 升序或连通标签顺序排序。默认面积从大到小。“当前区域索引”范围为 0～9999，从 **0** 开始，用于选择下游订阅和单个区域测量，不截断完整区域数组；索引超出本次数量时，选中区域为空。

“合格数量下限／上限”范围为 0～10000，默认 1～10000，只判断筛选后的数量是否在范围内，不会强制找到指定数量。`Found` 表示列表非空，`Passed` 表示有效数据的数量符合设置；两者不是同一个含义。

## 面积、周长与坐标

**像素面积**等于区域内的白色前景像素数。比如 40×30 的实心矩形中有 10×10 的黑孔，面积为 `1200−100=1100 px²`，填充率为 `1100/1200`。这与“区域形状分析”按轮廓多边形计算的面积语义不同；Blob 输出 `areaKind="pixelCount"`。

**像素边界周长**是前景与非同一区域相邻的网格像素边长之和，使用四个像素边计数，包含孔洞边界。上述矩形带孔的周长为 `2×(40+30)+2×(10+10)=180 px`。结果标记 `perimeterMethod="pixelEdges"`；它不等于对轮廓折线求得的欧氏长度。

**网格圆度**为 `4π×像素面积 / 像素边界周长²`。由于周长采用上述网格定义，不能直接套用其他平台的欧氏轮廓圆度阈值；应根据实际样品设定范围。孔洞会降低面积、增加周长，从而影响圆度。

质心是全部真实前景像素坐标的平均值。包围框返回左上角 X/Y 和宽高。结果坐标计入当前输入图原点；经过圆环展开后，结果仍在独立的展开图像素平面中，不自动变成原图或毫米坐标。详见 [圆环展开](annular-unwrap.md)。

右侧订阅按钮可选择 Blob 的总面积、数量，或选中区域的质心 X/Y、包围框 X/Y／宽高、面积、周长、圆度、宽高比、填充率和边界状态。例如用 `measurement.selected.centroidX`、`measurement.selected.centroidY` 驱动后续模块的 ROI。选中区域不存在时订阅不可用，不沿用上一帧坐标；操作见 [参数订阅](parameter-subscriptions.md)。

## 输出图像、绿色标记和空结果

每个区域在完整结果中包含面积、质心、包围框、周长、圆度、宽高比、填充率和是否贴边。根结果还包含 `candidateCount`、`rejectedCount`、`count`、`totalArea`、`found`、`passed`、`measurement` 和 `overlay`。

包围框、质心默认显示，标记颜色为绿色 `#00FF00`，默认线宽 2，可调整为 0.5～10；编号和面积文字可单独开启。标记与图像像素分开传递，关闭标记不会改变区域数据，也不会修改原图像素。

| 状态 | 结果含义 | 使用方式 |
| --- | --- | --- |
| 有效、找到区域 | `DataAvailable=true`，完整区域数组及面积 | 按选中区域或完整列表继续处理 |
| 有效、筛选后为空 | `DataAvailable=true`，`Count=0`、`Features=[]`、`TotalArea=0` | 正常零目标；数量判定取决于是否允许 0 |
| 输入／ROI／订阅不可用 | `DataAvailable=false`，`Features=[]`、`TotalArea=null` | 数量判定视为未知；读取原因并检查上游，不用 0 代替缺失数据 |
| 执行报错 | `Status` 不为 `Succeeded` | 先处理运行错误，不继续读取或使用旧结果 |

候选数是二值分割后、各项筛选前的连通区域数量，筛除数包含面积、形状、尺寸及边界条件排除的区域。空结果会清除当前区域与标记，不保留上次检测结果。

## 资源范围

输入整体最多 **32 Mi 像素，即 33554432 像素**。原始连通候选最多 **100000 个**，筛选后接受的区域最多 **10000 个**；超过容量明确报错，不静默截断。

原始候选上限在区域筛选前检查。大量噪声造成候选超限时，应先调整分割、去噪或缩小 ROI；仅提高面积下限不能绕过原始候选上限。接受区域过多时，可加强筛选。结果数量设置的“合格上限”与资源容量不同：把数量合格上限调小不会截断实际输出。

## WPF / SDK 读取

引用当前源码的 `JPVision.Sdk` 与配套 Runtime。将 Blob 的 `result` 公开为 `BlobResult`、`image` 公开为 `BlobImage`；这些名称可由配方自行定义。`GetBlobAnalysis(name)` 可读取公开 Result，也可读取公开图像携带的最新 Result；该结果必须是 Blob 分析结果。若图像后面又运行了其他结果算子，建议直接公开 Blob 自身的输出。

以下代码位于已有 `session`、输入 `frame` 和 WPF `VisionDisplay` 的运行回调中；`VisionDisplay` 为 `JPVision.Wpf.VisionView`。显示代码应在 WPF UI 线程执行。

```csharp
using JPVision.Sdk;

using var result = await session.RunAsync(frame, new RunRequest { Debug = false });
if (result.Status != ExecutionStatus.Succeeded)
    return;

VisionBlobAnalysis blobs = result.GetBlobAnalysis("BlobResult");
// 也可：result.GetBlobAnalysis("BlobImage")。
if (!blobs.DataAvailable)
{
    System.Diagnostics.Debug.WriteLine(blobs.Reason);
    return;
}

double totalAreaPx = blobs.TotalArea!.Value;
foreach (VisionRegion region in blobs.Features)
{
    System.Diagnostics.Debug.WriteLine(
        $"面积={region.Area}, 质心=({region.CentroidX}, {region.CentroidY}), " +
        $"包围框=({region.X}, {region.Y}, {region.Width}, {region.Height})");
}

VisionRegion? selected = blobs.Measurement.Selected;
double? selectedX = selected?.CentroidX;
double? selectedY = selected?.CentroidY;

using var cleanImage = result.GetOutputImage("BlobImage");
VisionDisplay.PresentCopy(cleanImage, result.GetOverlays("BlobImage"));
```

`VisionBlobAnalysis` 提供完整区域列表、选中区域测量、候选／筛除／合格数量、可空总面积、原因及独立标记。`VisionRegion` 新增可空属性 `AreaKind`、`AspectRatio`、`Extent`、`TouchesBoundary`；Blob 中这些属性有值，既有轮廓区域结果可以仍为 `null`，不改变旧轮廓面积语义。

SDK 会检查区域几何、像素面积、周长、形状比例和总面积的一致性；不能将不同版本的运行库或其他类型的结果当作 Blob 包读取。更多生命周期和授权接入见 [SDK 集成](sdk-integration.md)。
