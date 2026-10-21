# 简易 OpenCV 组件开发

当前源码自动提供统一图像入口/出口与独立结果上下文，原 typed 接口保留兼容。新增组件仍声明真实算法类型；调用方主要连 Image，转换、上下文及 Overlay 由公共执行层处理。见 [统一图像流程](image-pipeline.md)。

JP-Vision 使用一个原生组件注册表：显示名称、分类、端口、参数、算法和特殊校验放在同一次注册中。WPF 工具箱、参数面板、连线检查和 Runtime 都读取它。0.4.0 为 Image/Mask 输入组件自动添加模块 roi 参数与执行包装；算法可使用 Image.valid 有效像素掩膜，不能只按包围框输出圆形/多边形外的结果。常见图像处理组件仍可只在 `native/core/components/custom_components.cpp` 注册 OpenCV 回调。

## 增加普通图像处理组件

在 `register_custom_components` 中增加以下注册。文件已经有可运行的 MedianBlur 示例。

```cpp
registry.add_image(
    "InvertImage", "图像反色", "预处理", Json::array(),
    [](const cv::Mat &image, const Json &) {
        cv::Mat output;
        cv::bitwise_not(image, output);
        return output;
    });
```

`add_image` 自动声明 Image 输入、Image 输出，保留 ROI 原点，并检查输出尺寸和 Gray8/Bgr24 格式。算法读输入、写独立输出；不要原地修改输入或通过浅拷贝修改共享像素，因为同一图像可能被多个分支使用。改变尺寸/坐标的算法使用下面的完整注册方式，明确处理原点。

有参数时使用 `parameter(name, label, type, default, min, max, choices, bindable)`。数值给范围、枚举给 choices；显式 bindable=true 允许公开输入或目录声明的上游标量字段。跨参数关系放在校验回调。多输入用 executeInputs，Result 标量字段在 descriptor.fields 声明类型和 nullable。支持不可用参数的组件需声明 handlesUnavailableParameters 并先处理 `_unavailable`，否则执行器跳过，严禁执行占位常量。详见 [本轮契约](m0-m2-implementation.md)。

2026-10-19 起可在参数描述中增加 group、description、unit、step，分别覆盖 WPF 分组、说明、单位与微调步长。奇数窗口可使用 `odd_parameter(name, label, default, minimum, maximum, allowZero=false)`，并在原生校验中检查奇数；allowZero=true 用于“0 跟随其他尺寸”场景。详见 [精细参数](parameter-refinement.md)。

```cpp
registry.add_image(
    "MedianBlur", "中值滤波", "预处理",
    Json::array({parameter("kernel", "核尺寸（奇数）", "integer", 3, 3, 31)}),
    [](const cv::Mat &image, const Json &p) {
        cv::Mat output;
        cv::medianBlur(image, output, p.at("kernel").get<int>());
        return output;
    }, require_odd_kernel);
```

MedianBlur 已注册，上例是现有代码说明，不能再重复注册同一 type。type 是配方保存的稳定标识，中文名称可以调整。已经使用的参数名、端口名和行为不要直接改掉；不兼容变化使用新的 type，保留原组件。

```powershell
.\scripts\build.ps1 -Configuration Release
.\scripts\test.ps1 -Configuration Release
```

构建前正常退出正在使用这些开发产物的 Studio/WPF，保留未保存的配方。在 Visual Studio 中写组件源码；生成解决方案或执行 build.ps1，先构建 MSVC Runtime，再构建 WPF Studio。重新启动 Studio 后组件自动出现在分类中。WPF 更新 SDK 包或对应 Runtime DLL 后可执行含新组件的配方。

## 检测、统计与特殊类型

`registry.add(component(...))` 是完整注册方式，见 `geometry_components.cpp`。执行回调签名为 `Value(const Value *input, const Json &parameters, const Image &original)`；可选校验回调为 `void(const Json &parameters)`。参数已经解析默认值和公开输入绑定。

当前支持单输入和多输入：`component` 帮助函数创建固定 input 端口，execute 回调返回主值；多输入组件声明 inputs 并使用 executeInputs，不能同时保留 execute。额外输出由 Component.expand／expandInputs 返回 `PortValues`（端口 ID → Value），主值绑定 outputs[0]。可参考 measurement_components.cpp 的双输入距离算子。只改目录但没有对应值会明确失败。执行器按 node + port 读取边、公开结果，并在源节点最后一次引用释放全部端口；框架负责节点诊断、Failed/Unknown、取消、超时、生命周期和授权。

不同工位会话可能并行调用同一个计算回调。临时 cv::Mat、容器和计数器放在回调内部，不使用可变静态/全局工作缓冲；参数和输入作为只读数据使用。

已有值类型：Image、Contours、Features、Int、Double、Bool、Overlay、Measurement。Mask 使用 Image 像素载体，但在目录中有独立端口类型。Contours 在内部保存局部点和 ROI 原点；Features 中的圆、线段和区域已经是原图坐标。Overlay 和 Measurement 使用不同的目录类型及受约束 JSON 契约，并不是任意 JSON 的通用连线端口。

新增几何算法可以返回 `Features`，复用 `CountFeatures` 和 `BuildFeatureOverlay`。例如返回 `CircleFeature{x, y, radius}` 或 `LineFeature{x1, y1, x2, y2, length, angleDegrees}`。已有几何类型的序列化和 Qt/WPF 图元显示无需重复开发。全新的值类型需要补充 Value、序列化、显示和 SDK 契约，不能只把任意 JSON 宣称为某个现有类型。

六个内置几何算子自动集成 measurement/count/found/passed/overlay。新增同类算子可在注册前设 `definition.descriptor["featureKind"] = "circle"`（或 line/region/match），注册表会补入参数、校验和额外输出。主输出必须是统一几何类型的 Features；没有声明 kind 的自定义 Features 组件仍保留单输出，不会被误当作区域测量。卡尺共用 circle_caliper_parameters / validate_circle_caliper / find_circle_caliper，FindCircles 的 caliper 模式复用相同实现。

## 已提供的图像与几何组件

| 组件 | OpenCV 实现 | 结果/用途 |
|---|---|---|
| MedianBlur | medianBlur | 去除椒盐噪声，演示单文件注册 |
| FindCircles | GaussianBlur + HoughCircles 或模块内卡尺 + SVD 拟合 | 圆心、半径、直径、面积、周长、计数、数量判定和标记 |
| FindLines | Canny + HoughLinesP 或卡尺拟合 | 两端点、长度、角度、方程及拟合诊断 |
| Morphology | morphologyEx | 开/闭运算、腐蚀、膨胀 |
| AnalyzeContours | contourArea、arcLength、moments、boundingRect | 面积、周长、质心、包围框、圆形度 |
| CountFeatures | 结果数组计数 | 接入 CompareNumber 判定 |
| BuildFeatureOverlay | 独立矢量图元 | 圆、线段、矩形显示，不改原图 |

圆/线组件自动接受 Gray8/Bgr24。圆查找自动模式默认 5×5、Sigma=1 高斯平滑，可通过 preBlurKernel／preBlurSigma 调整；结合 ROI、半径范围、圆心间距和阈值进行像素级 Hough 定位。卡尺模式可调投影窗、均值／中位数、平滑、亚像素插值、边缘选择和剔除，返回保留边缘点及 RMS；当前没有毫米标定。自动找线可能检测出粗线的两条边；单边精细测量使用卡尺拟合。角度按图像坐标系从 +X 方向计算，Y 向下，单位度；端点按 (x,y) 顺序规范化。

Hough 检测工作图最多 4,194,304 像素，结果最多 10,000 项。0.7.1 默认 `largeImageMode="adaptive"`：输入或模块 ROI 超过工作图上限时，以 INTER_AREA 缩小检测图，并将圆心、半径、线端点和长度换算回原图像素，保留上游 origin。原图与预览不缩小，半径范围、圆心间距、线长度与间隙参数仍按原图像素填写；测量输出中的 detection 记录输入及工作图尺寸、缩放比例。`largeImageMode="original"` 保持原图分辨率，超限时仍报出明确错误，请缩小模块 ROI 或接 CropRect。降采样并非原图精细拟合，小目标可能丢失，精确测量使用小范围原图 ROI 或卡尺。空结果是成功的空数组，可以用 ExpectedCount=0 判定。执行失败时公开结果清空、判定 Unknown。

区域分析使用外轮廓，面积是多边形面积，不是白像素数量，孔洞不扣除。20×20 实心像素方块的轮廓面积为 361，周长 76。圆形度为 `4π×area/perimeter²`。零面积轮廓使用包围框中心作为有限的回退位置，centroidValid=false，不能当作有效矩质心。

## 可直接运行的配方

Studio 菜单“文件 → 示例配方”包含圆查找、直线查找、形态学与区域分析。圆和直线示例的流程均为：

```text
外部图像 → 几何检测 → 几何目标计数 → 数量判定
                  └→ 几何结果标记
```

区域示例为：外部图像 → 灰度 → 二值化 → 形态学 → 提取轮廓 → 区域形状分析，再连接计数和标记。示例目录在 `samples/recipes/{circles,lines,regions}.vrecipe`，配套图在 `samples/images`。重建演示资产可运行 `scripts/create-component-samples.ps1`，需要先生成 Release Runtime。

## WPF 调用

```csharp
using var engine = new VisionEngine();
using var recipe = engine.LoadRecipe(@"C:\Recipes\circles.vrecipe");
await using var session = recipe.CreateSession();
using var frame = VisionFrame.CopyFrom(bytes, width, height, stride, VisionPixelFormat.Bgr24);
using var result = await session.RunAsync(frame);
if (result.Status == ExecutionStatus.Succeeded)
{
    IReadOnlyList<VisionCircle> circles = result.GetCircles("Features");
    // 直线配方用 GetLines；区域配方用 GetRegions。
    imageView.PresentCopy(frame, result.GetOverlays("ResultOverlay"));
}
```

这些方法返回原图像素坐标和托管数值记录，不依赖 Qt，不暴露 cv::Mat。用错误的非空特征类型调用会抛 InvalidDataException；失败任务不能读取几何结果。也可通过 `GetOutput<T>(公开名称)` 读取自定义托管类型。公开名称可以修改，不要求固定叫 Features。

原有机器绑定的离线许可证继续覆盖编辑器和原生执行入口。新增组件通过相同执行器调用，无需单独维护组件密码。

## 对应源码

- `component.h/.cpp`：注册表和便捷 API。
- `basic_components.cpp`：原有 11 个组件。
- `geometry_components.cpp`：圆、线、区域和通用计数/标记。
- `custom_components.cpp`：今后简单 OpenCV 组件的添加位置。
- `managed/JPVision.Sdk/Geometry.cs`：C# 几何结果记录。
- `roi.h/.cpp`：形状验证、包围框、有效像素掩膜及原点传播。
- `caliper_components.cpp`：投影、梯度、插值与线/圆拟合。
- `template_components.cpp`：模型校验、带掩膜的去均值相关、角度/尺度搜索和重叠抑制。

卡尺输出的 circle/line 项额外有 method=caliper、edgePoints 和 rmsError，模板项 kind=match，包含 x/y/width/height、centerX/Y、angleDegrees、scale、score 和四个 corners。CountFeatures 与 BuildFeatureOverlay 可直接复用，见 [编辑器操作](studio-guide.md)。模板相关运算依据官方 [OpenCV 模板匹配说明](https://github.com/opencv/opencv/blob/5.x/doc/tutorials/imgproc/histograms/template_matching/template_matching.markdown)。

接口依据以本机 OpenCV 5.0.0 头文件为准；官方 [Hough 圆教程](https://github.com/opencv/opencv/blob/5.x/doc/tutorials/imgproc/imgtrans/hough_circle/hough_circle.markdown) 和 [Hough/Canny 参数说明](https://docs.opencv.org/4.13.0/dd/d1a/group__imgproc__feature.html) 可辅助调参。
