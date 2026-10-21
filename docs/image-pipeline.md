# 统一图像流程与模块内测量

本轮源码变更以“外部图像 → 任意处理/检测模块 → 后续模块”为主要接法。新界面每个模块右侧显示一个“图像与结果”端口；检测和测量保留实际图像像素，圆、线、数值和绿色标记独立携带。旧配方保存的 Features、Contours、Mask、Result 等端口仍可加载，已有公开输出名称和 C ABI 不变。

## 图像与结果

- 二值化接受 Gray8/Bgr24，内部完成灰度转换，输出真实二值图像。
- 形态学接受二值图像，普通图像先按模块的“图像分割”参数分割。二值图像不会重复阈值化；高斯滤波后重新视为普通灰度图像。
- 轮廓提取、轮廓面积筛选、区域形状分析可直接接图像，内部完成分割及轮廓提取。面积筛选输出实际筛选后的掩膜。每次重新提取当前像素中的轮廓，避免使用处理前的旧轮廓。
- 圆、直线、模板检测及测量图像不烧录标记。后续滤波/检测看到干净像素；显示层从独立 Overlay 绘制，默认颜色 #00FF00。
- 同一几何标记在下游重新设置颜色/线宽时采用下游样式，不重复叠画。某次测量无效时不显示旧测量线，但可保留本次图像上游仍有效的检测标记。
- 每次运行有独立上下文，只携带该帧已执行模块的数据。后一次空检测覆盖前一次同类结果，不能回退到旧帧或较早的非空结果。
- 模板匹配失败的图像保持不可用，后续不能偷偷对原图继续检测。位置修正从图像携带的有效 Pose 或旧显式 Pose 输入读取位姿；没有有效位姿时为不可用。

像素坐标始终保留输入图像原点，几何/测量值使用原图坐标。定位基准 ROI 使用既有 fixture 变换。

## 单图 A/B 测量

将外部图像或上游处理图像接到测量模块的“输入图像”。在模块内依次选择对象 A、B，绘制/调整各自 ROI。圆圆距离使用两个圆形/圆环 ROI；线线距离使用两个矩形/旋转矩形卡尺搜索带，也可选择多边形指定边；线圆距离的 A 为直线、B 为圆。几何测量根据操作选用圆心或拟合直线。

两侧独立设置卡尺数量、投影宽度、搜索长度、平滑 Sigma、阈值、极性、选边方式、剔除点数、最大偏差、最小覆盖率、投影统计和亚像素。矩形直线搜索带的高度决定搜索长度。算法复用既有圆/直线卡尺，从真实边缘点剔除、拟合后测量；绘制的 ROI 本身不作为测量结果。

未绘制 ROI、某侧没有有效边缘、平行角度超限等情况返回无效值和 Unknown，仍可显示本次干净图像供调整。圆圆默认圆心距、线圆默认圆心到直线垂距，可切换圆周间距；线线默认平行边垂直间距，沿用角度容差及公差判定。

旧的“两个检测结果 → 测量 A/B”仍可执行。切换为图像模式时编辑器在一次撤销事务内清除旧 A/B 连线；运行时拒绝混合两种来源，避免对象来源不明确。

## WPF SDK

仍使用当前源码 SDK 和配套原生 Runtime，正常 VS Debug/x64 构建即可，无需发布独立安装程序。公开 Image 输出名称可由业务工程自定，例如 OutputImage：

```csharp
using var result = await session.RunAsync(frame);
if (result.Status == ExecutionStatus.Succeeded)
{
    using var cleanImage = result.GetOutputImage("OutputImage");
    var graphics = result.GetOverlays("OutputImage"); // 独立绿色标记
    var pipeline = result.GetImagePipeline("OutputImage");
    // 该输出链中存在圆查找时：
    // var circles = result.GetCircles("OutputImage");
    // 该输出链中存在几何测量时：
    // var measurement = result.GetGeometricMeasurement("OutputImage");
}
```

GetResult<T>、GetMeasurement<T>、GetCircles/GetLines/GetRegions/GetMatches、GetGeometricMeasurement 同时支持原公开 typed 输出和新 Image 携带的最新对应数据。链中不存在该类型时明确提示缺少结果，不能视为零。GetImageAttachedOutput<T>(name, type) 可读取 Features、Contours、Measurement、Result、Pose、Int、Bool、Decision 等最新附带值。GetImagePipeline 返回该帧身份、原图坐标约定、模块数量、最新各类型值与独立标记。

## 组件扩展与资源限制

注册时仍声明算法真实输入/输出类型。公共包装添加 Image 兼容入口和可见 Image 出口；真实 typed 端口保持隐藏用于旧配方、标量绑定及 SDK。acceptedTypes 由编辑器和 Runtime 同时校验，valueType 记录算法实际所需类型。双来源模块用 inputGroups 声明完整合法输入组合，不能只检查端口数。

ImagePipelineContext 为每次运行独立、只读的共享记录。记录不持有像素或 sourceImage 反向引用，防止保留全部中间图像/形成循环；分支合流检查同帧及同节点数据一致性。上下文最多 1024 个阶段、128 MiB，且纳入原有调试/结果预算。独立标记不会改变图像像素。
