# JP-Vision 0.7.2

源码学习入口：[代码逻辑与算子算法导读](docs/code-and-algorithms-guide.md)，覆盖执行链、32 类算子的实际算法、参数与结果传递、相机采集及扩展步骤；另提供 [PDF 阅读版](docs/JP-Vision代码逻辑与算子算法导读.pdf) 和 [Word 版](docs/JP-Vision代码逻辑与算子算法导读.docx)。

2026-10-21“边缘点集”新增曲线修复：局部趋势剔除跳点、稳健平滑、按左右可靠边缘补内部短缺段，保留原始坐标并区分实测／补全。点到线距离默认只测实测合格原始点，也可明确切换使用修正／补全点集。操作与算法边界见 [边缘曲线修复](docs/edge-reconstruction.md)。

2026-10-21 源码优化配方复用和大图显示：主窗口图像区可直接观看全局相机实时画面，保持缩放并与管理窗共享位图；执行／教学后固定结果，手动选择“实时画面”返回。重复执行复用未改动的配方和会话，修改参数后重建，日志增加分阶段耗时。方案、限制和复测方式见 [性能与实时预览](docs/performance.md)。

2026-10-20 源码新增海康 MVS GigE“全局相机”图像源：所有外部图像节点共享一次执行的新帧，支持按 SN 连接、连续预览、固定图像教学及业务触发检测。已用 ME2P-2622-4GM-P 完成两个 5120×5120 Gray8 取流周期，操作、可选依赖、范围与 SDK 用法见 [全局相机图像源](docs/camera-source.md)。

新增“检测 → Blob 分析”：直接接入图像，在模块内二值分割、连通区域分析并按面积、尺寸、形状和边界筛选，输出区域表、质心、包围框、像素面积和独立绿色标记。支持参数订阅及 WPF `GetBlobAnalysis`，见 [Blob 分析操作与 SDK](docs/blob-analysis.md)。

新增“检测 → 边缘点集”和“测量 → 点集到线距离”：在不规则边缘上绘制搜索带，自动提取有序真实点，再与已有直线查找结果计算逐点垂距，输出完整距离数组、极值、均值及波动统计。见 [边缘点集与测距操作](docs/edge-points.md)。

新增参数订阅、全局／算子局部变量与纯计算函数。ROI 圆心、半径、多边形顶点及 A/B 测量坐标可订阅本次上游结果；例如圆查找圆心直接带动圆环展开。通过“编辑 → 变量与计算函数”管理，表达式支持偏移、换算、条件判断与自定义函数；SDK 可每次运行覆盖全局值。见 [参数订阅、变量与函数](docs/parameter-subscriptions.md)。

新增“图像变换 → 圆环展开”：可绘制圆环/扇环或直接调整圆心、半径、角度，支持展开方向、径向顺序、尺寸及插值；输出矩形图像可继续处理，并保留回到源图平面的坐标映射。见 [圆环展开操作与 SDK](docs/annular-unwrap.md)。

2026-10-19 源码新增统一“图像与结果”输入输出：普通图像可直连轮廓、形态学、检测和测量模块，检测数据与独立绿色标记随干净图像传递。测量模块内置 A/B ROI，复用真实圆/直线卡尺检测后计算距离；位置修正可从图像读取定位位姿。旧 typed 配方和 SDK 输出继续兼容。见 [统一图像流程与 A/B 测量](docs/image-pipeline.md)。

2026-10-19 源码新增参数分组、范围与用途说明、数值细步进和单项恢复默认；扩展滤波、二值化、形态学、圆线检测及卡尺投影精度选项。旧配方缺省行为保持，参见 [算子精细参数](docs/parameter-refinement.md)。

当前开发方式为 **Visual Studio 直接运行源码**：打开 `JPVision.sln`，选择 Debug / x64，将 `JPVision.Studio.Wpf` 设为启动项目后 F5。后续更新以源码、文档和普通构建验证为交付；独立发布、版本 EXE、安装包和 NuGet 仅在明确要求发布时生成。解决方案已补充 SDK 对原生核心的构建依赖。

已支持“线线距离”“圆圆距离”“线圆距离”：圆圆默认圆心距，线圆默认圆心到无限直线的垂距，均可切换为圆周最短间距，并同时输出两种数值、测量端点、公差判定及图形。测量结果的线、点、圆环和文字默认绿色 `#00FF00`；现有配方显式指定的颜色保留。操作和 SDK 契约见 [编辑器指南](docs/studio-guide.md)。

面向 C# WPF 工业工程的机器视觉平台：**C# WPF 编辑器 + C++ OpenCV 核心 + net8 SDK**。含圆／直线、形态学、轮廓、卡尺、模板匹配和快速定位，采用红黑白工业界面及“红焰三角之眼”图标。

2026-10-15 源码新增位置修正、旋转矩形／圆环／扇环 ROI、中心距／双边宽度／几何测量、三态公差、上游字段绑定及运行到选中。真正的 .NET 8 WPF 宿主已跑通定位→两圆→中心距→判定。用法和边界见 [M0～M2 实现](docs/m0-m2-implementation.md)；只更新本地 Debug，旧客户包不包含这些增量。

0.7.0 将原 Qt 界面迁移到 WPF，并提供 Visual Studio 解决方案。可拖入组件、连接类型端口、编辑参数、绘制矩形／圆形／多边形 ROI、调整卡尺数量及宽高、剔除点数，直接读取算子图像、测量值、数量与判定。快速模块内置模板绘制、学习、导入和预览；模型内嵌配方，现有 WPF 工程可直接加载。新建三节点图像链无需再连接结果模块；删除节点后不会继续因旧公开输出引用而阻止当前流程调试。

授权沿用签名许可证、绑定电脑、永久离线有效，无登录；只有开发者签发了许可证的客户能使用编辑器与运行库。客户安装包不含许可证、签发工具或私钥。本机已有通过正常签发导入的 JP 授权。

0.7.1 修复导入大图后圆／直线查找因 4MP 上限失败的问题：默认只缩小霍夫检测工作图，原图、几何坐标和尺寸保持原图像素；可切换原图模式，配合模块 ROI 做精确检测。日志与测量结果提供实际工作分辨率，原生错误显示算子名称及中文处理建议。降采样会降低小目标及边缘定位分辨率，精确测量应使用小范围原图 ROI 或卡尺。

0.7.2 增加卡尺跟随 ROI 边界：圆查找的卡尺模式／圆卡尺沿圆周，直线卡尺沿矩形或多边形的指定边；拖动、缩放或修改顶点时预览同步。新卡尺节点默认跟随，旧配方保留手动指引，可在“卡尺排列”切换。宽度只提供 1～63 的奇数，运行结果与 SDK 可读取实际采样窗口。

当前源码将检测模块右侧合并为一个“全部结果”端口，传递全部目标、选中测量、数量、判定和图形标记。内置 ROI、圆卡尺、计数、数量判定和几何标记从工具箱隐藏，旧配方保持兼容。WPF 宿主可使用 `GetResult<T>` 一次读取，见 [结果端口](docs/result-port-design.md)。本次只更新源码和本地调试程序，已有 0.7.2 客户包不包含此新增功能。

当前源码支持“快速匹配 → 圆查找”：原图像素与模板中心坐标一起传递，圆 ROI／卡尺跟随本次定位，拟合输出真实圆心、半径、直径和可调宽度的圆环。待测圆也可以位于模板框之外。搜索支持 32 Mi 像素全图，未匹配返回空结果；旧配方保留裁图和手动 ROI 默认。操作见 [编辑器指南](docs/studio-guide.md)，参考本机 VisionMaster 4.4 的接口／模块配置证据及功能边界见 [业务逻辑参考](docs/visionmaster-business-review.md)。

## 运行与开发

工作区为 `D:\open500\JP-Vision`，原 OpenCV 分发 `D:\open500\bin / include / lib` 保留。打开 **JPVision.sln**，配置 **x64**，将 **JPVision.Studio.Wpf** 设为启动项目；具体说明见 [Visual Studio 开发发布](docs/visual-studio-guide.md)。

```powershell
cd D:\open500\JP-Vision
.\scripts\build-native.ps1 -Configuration Debug
dotnet build managed/JPVision.Studio.Wpf/JPVision.Studio.Wpf.csproj -c Debug
dotnet test managed/JPVision.Tests/JPVision.Tests.csproj -c Debug
```

需要 Visual Studio 2026／MSVC v145、CMake、.NET SDK 10 和现有 OpenCV 5。默认构建无需 Qt。脚本 build.ps1 默认只生成普通项目输出；显式加 -PublishStudio 才生成额外的发布目录。测试需要已有正常签发的开发许可证与负例签发工具；没有免授权模式。已有信任根时不能重新生成密钥替换；开发者私钥位于项目外的 Windows DPAPI 受保护目录。

打开图像后，把“外部图像”连接到处理／检测模块，点执行并选择节点。在右侧“运行结果”读取该节点端口值。ROI 和模板均在所选模块内编辑，模板与搜索 ROI 独立保存。Ctrl+S 保存为 `.vrecipe` 文件夹；正常图像处理的判定是 Unknown，只有明确配置 Bool 检测输出时才生成 Pass／Fail。

## WPF 集成和交付

SDK 包为 `artifacts/packages/JPVision.Sdk.0.7.2.nupkg`，图像显示控件包为 `JPVision.Wpf.0.7.2.nupkg`，固定 Windows x64。宿主通过加载配方、传入 Gray8／Bgr24 图像、调用 RunAsync、读取 GetMeasurement / GetNodeMeasurement 和 GetNodeOutput 获得结果；详见 [SDK 集成](docs/sdk-integration.md)。无须 Qt，也无须再学习已内嵌的模板。

`pack.ps1` 生成自带 .NET 10 的 WPF 编辑器、框架依赖的 WPF 范例、文件摘要清单及客户端 ZIP；只包含 Release 产物。范例需要 .NET 10 Windows Desktop Runtime，原生 DLL 需要匹配 MSVC 的 x64 VC++ Runtime。隔离的 PackageConsumer 仅引用 NuGet，移除开发 Qt／OpenCV PATH 后验证全部示例配方。

## 文档

- [编辑器、参数、ROI 和模板操作](docs/studio-guide.md)
- [海康 GigE 全局相机图像源](docs/camera-source.md)
- [参数订阅、变量与计算函数](docs/parameter-subscriptions.md)
- [不规则边缘点集与点到线距离](docs/edge-points.md)
- [Blob 分析与区域筛选](docs/blob-analysis.md)
- [Visual Studio 开发与发布](docs/visual-studio-guide.md)
- [架构与资源契约](docs/architecture.md)
- [WPF / SDK 集成](docs/sdk-integration.md)
- [离线授权与签发](docs/licensing.md)
- [配方格式](docs/recipe-format.md)
- [简易 OpenCV 算子扩展](docs/component-authoring.md)
- [实际验证](docs/verification.md)
- [第三方组件说明](THIRD-PARTY-NOTICES.md)

当前相机实现为单台海康 MVS GigE 自由连续采集与业务触发检测；大恒、相机硬件触发、多相机和自动重连仍待实现，边界与历史设计见 [相机采集设计](docs/camera-acquisition-design.md)。当前未连接生产产线，也未实现 AI、GPU、3D 或原生故障进程隔离。原 Qt 源码和历史发行版保留作迁移参考。
