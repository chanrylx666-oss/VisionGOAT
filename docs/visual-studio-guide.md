# Visual Studio 开发与发布（0.7.2）

编辑器已改为 C# WPF；核心继续使用 MSVC C++／OpenCV 5。打开根目录 `JPVision.sln`，选择 **Debug / x64** 或 **Release / x64**。本机验证工具为 Visual Studio 2026 Community、MSVC v145、.NET SDK 10.0.401。需要“.NET 桌面开发”“使用 C++ 的桌面开发”和 CMake 组件，默认流程不再需要 Qt。

2026-10-16 起按用户要求直接在 VS 开发、F5 运行源码。更新交付为源文件、说明和普通构建验证；独立版本 EXE、publish 目录、安装包和 NuGet 需明确要求发布才生成。普通 VS 生成的 bin／obj 和 artifacts/native 用于调试，历史 local-debug 目录不代表当前源码。build.ps1 默认只 build，显式加 -PublishStudio 才额外 publish。

| 项目 | 作用 |
| --- | --- |
| JP-Vision.Studio.Wpf | WPF 可执行编辑器；设为启动项目，F5 调试 |
| JPVision.Editor.Core | 流程、类型连线、保存、撤销、ROI 与模板编码 |
| JPVision.NativeRuntime | 原生源文件和 IntelliSense；构建调用 `scripts/build-native.ps1` |
| JPVision.Sdk / JPVision.Wpf | net8 SDK 与现有工程可复用的图像显示控件 |
| WpfSample | 工业 WPF 工程调用范例 |
| JPVision.Tests | 使用真实 C++ 核心的 SDK 与编辑模型测试 |
| LicenseIssuer | 开发者签发工具，不能发给客户 |

先生成解决方案，再启动 `JPVision.Studio.Wpf`。核心项目是 MSVC/CMake 构建包装，JPVision.sln 已实际声明 SDK 对核心的 ProjectDependencies；生成顺序保证当前 DLL 在托管输出中。普通调试输出位于 managed/JPVision.Studio.Wpf/bin/Debug/net10.0-windows。Debug 核心的 PDB 会复制到编辑器目录；编辑器启用托管与原生混合调试，可在 `native/core/components` 的算法内下断点。C++ 边界仍使用 opaque handle、UTF-8 JSON 和像素字节，不直接交换 cv::Mat 或 STL 对象。

界面布局在 `managed/JPVision.Studio.Wpf/MainWindow.xaml`，主题在 `Theme.xaml`，画布在 `GraphView.cs`，图像、ROI 与卡尺显示在 `ImageEditorView.cs`；执行与生命周期、参数结果面板、授权和接口窗口分别在 MainWindow 的 partial 源文件中。新增简单 OpenCV 算子可在 `native/core/components/custom_components.cpp` 注册描述和算法，重建后工具箱与参数控件自动出现。

## 客户端发布

生成 Release / x64 后，右键 `JPVision.Studio.Wpf` → 发布，选 `FolderProfile`。该配置输出自带 .NET 10 的 Windows x64 文件夹；不要勾选裁剪或 NativeAOT。单独发布时应附与 MSVC 匹配的 x64 VC++ 运行环境；pack.ps1 的完整客户端包已附 app-local release CRT。完整交付通过以下命令生成，含 WPF 编辑器、集成范例、NuGet、说明及校验清单，并检查没有许可证、私钥或签发器：

```powershell
cd D:\open500\JP-Vision
.\scripts\build.ps1 -Configuration Release
.\scripts\test.ps1 -Configuration Release
.\scripts\pack.ps1
```

客户端路径为 `artifacts/release/JP-Vision-0.7.2/Studio/JP-Vision.exe`。普通 Release 开发输出为 managed/JPVision.Studio.Wpf/bin/Release/net10.0-windows，需要本机 .NET 10 Windows Desktop Runtime；artifacts/studio-wpf/Release 仅由显式 -PublishStudio 生成。NuGet 的 SDK 目标仍是 net8，不要求现有 WPF 工程升级到 net10；所有宿主均应使用 x64。

发布配置依据微软的 [WPF 应用生命周期](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/app-development/application-management-overview)、[.NET 发布](https://learn.microsoft.com/en-us/dotnet/core/deploying/)与 [C++ Makefile 项目](https://learn.microsoft.com/en-us/cpp/build/reference/creating-a-makefile-project?view=msvc-180)说明。

原 Qt 源码和历史产物保留作迁移参考，入口为 `scripts/build-qt-legacy.ps1` / `test-qt-legacy.ps1`。它们不再是 0.7.0 客户端的默认构建、测试或发布路径。
