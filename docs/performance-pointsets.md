# JP-Vision 点集流程性能

## 本次优化

针对圆查找 → 圆环展开 → 中值滤波 → 边缘点集／直线查找 → 点集到线距离的流程，保留原配方的 ROI、滤波核、采样间距、亚像素设置和全部测量点。

- 卡尺参数和高斯核按一次算子执行准备，测站复用投影、平滑、梯度和候选边缘缓冲；双线性采样使用图像行指针，仍检查全部四个邻域像素和有效掩膜。
- 点到线距离先在原生数值数组中计算，再统一生成 JSON；保持点顺序、原始／修正点选择、极值相同距离时的选择顺序及全部逐点测量数据。
- 图像上下文保存不可变阶段，缓存叠加图形的去重键和最终显示顺序；同内容的 Result／PointSet 在内部共享，公开字段和 JSON 形状保持兼容。
- 节点端口转换和编码字节数按端口复用，公开输出别名复用已有结果；每份公开／调试输出仍计入原有预算。新增 `prepareMs`、`executeMs`、`resultMs`，分别记录准备、计算、结果处理耗时，原 `elapsedMs` 保留。
- SDK 直接从原生 UTF-8 缓冲区解析为持有数据的 `JsonElement`，随后释放原生缓冲区，减少中间 UTF-16 字符串和重复 JSON 复制。关闭运行结果后，已经取得的 `Data` 仍可读取。

节点时间中的 `executeMs` 包括算子回调本身以及该回调内部的结果构造；`resultMs` 包括端口展开、图像上下文整理、预算核算和调试输出保留。因此节点时间不能直接等同于 OpenCV 内核耗时。

此流程中，边缘点与距离结果的封装和跨语言传递占用明显时间，多进程还会增加图像与结果传输成本，因此本轮先减少重复计算与数据复制。没有通过减少测站、缩小滤波核或丢弃结果来提速。

## 实际配方验证（2026-10-23）

输入为桌面的 `process.vrecipe/recipe.json` 和 `20260920154524.jpg`，原图 5120 × 5120；圆环展开图约 10996 × 300，中值滤波核为 5，边缘搜索带请求 2751 个测站，输出 2748 个点及 2748 份距离。保留完整中间结果，原配方与原图文件哈希在测试前后均未变化。

每组复用同一配方／会话，预热一次后测五次，取中位数；计时期间没有同时运行构建或测试。SDK 调用时间包含原生执行、最终 JSON 导出和托管解析，不含 JPEG 解码、配方编译、相机取图、结果校验和界面绘制。这里的数值是本机这份流程的观测值，不代表所有图像或机器的固定耗时。

| 版本／配置 | SDK 调用中位数 | 原生执行中位数 |
| --- | ---: | ---: |
| 原版 Debug | 10692.83 ms | 8785.07 ms |
| 优化后 Debug | 10229.70 ms | 8429.07 ms |
| 原版 Release | 1194.28 ms | 824.80 ms |
| 优化后 Release | 1232.16 ms | 826.30 ms |
| 优化后 Profile | 1256.77 ms | 805.74 ms |

结论：同为 Debug 时，当前流程总调用时间降低约 4.3%；同为 Release 的结果没有体现稳定的总耗时收益，数十毫秒差异处于本轮波动范围。日常用 Profile 替代原 Debug 可从约 10.69 秒降至约 1.26 秒，主要收益来自优化版原生代码与 OpenCV，不能将这约 8.5 倍变化全部归因于算法改写。

Debug 组的单次调用托管分配中位数由约 213.96 MB 降至 146.49 MB，减少约 67.47 MB（64.3 MiB，31.5%）。这是分配量，不能等同于进程峰值内存；不包含原生分配，也会受到托管池复用影响。返回 JSON 仍约 33.7 MB，完整调试输出的构造／传递仍是后续可优化的部分。

五组规范化业务结果的 SHA-256 均为 `EBD1E25A91249317BE4041B90F5EBA05B4E1F7C713C4178669A8D91426C1221A`。比较包含全部边缘点、逐点距离、统计量、叠加图形及处理后图像像素哈希；只排除耗时、运行标识及每帧动态坐标平面前缀。检测与测量仍返回干净图像，绿色图形单独叠加。

证据位于 `artifacts/tests/pointset-performance/final-comparison.json` 及五组 `final-*/report.json`，其中保存原生／SDK 二进制及输入文件哈希。可复用的基准程序位于 `tools/PointSetBenchmark`。

验证通过：Debug 与 Profile 各 929 项托管测试、两配置各 3 项原生测试、6 项 Profile 界面回归，以及旧／新卡尺采样器 1728 组配置、41472 次逐位一致性对照。VS 的 Profile／x64 完整解决方案构建成功。界面检查覆盖点集、曲线修复、图像处理链，以及移动／切换节点和保存时的结果保留。

## Visual Studio Profile 配置

在 Visual Studio 工具栏选择 `Profile` 和 `Any CPU` 或 `x64`，启动项目仍使用 `JPVision.Studio.Wpf`，按 F5 运行源码。

`Profile` 为 C# 保留 `Optimize=false`、`DEBUG`／`TRACE` 条件编译常量以及 portable PDB；托管输出独立放在 `bin/Profile/`。C++ 使用 `RelWithDebInfo`，启用优化并生成原生 PDB，链接 Release 版 `opencv_world500.dll`。原生 DLL、PDB 和可选相机模块保存在 `artifacts/native/RelWithDebInfo/`，通过项目引用复制到 WPF 的 Profile 输出目录。既有 Debug、Release 配置保持原来的映射，性能比较需要明确记录实际配置与 DLL 哈希。

也可从终端构建，不执行发布、打包或许可证签发：

```powershell
./scripts/build-native.ps1 -Configuration Profile
dotnet build managed/JPVision.Studio.Wpf/JPVision.Studio.Wpf.csproj -c Profile
```

原生配置可显式覆盖。例如 `dotnet build ... -c Profile -p:JpvNativeConfiguration=Debug` 配合 `./scripts/build-native.ps1 -Configuration Debug`，会使用 Debug 原生 DLL 和 Debug OpenCV。`JpvNativeConfiguration` 支持 `Debug`、`Release`、`RelWithDebInfo`；脚本也接受同名参数。覆盖时应先构建对应原生配置，避免加载不同版本的文件。

Profile 中 C# 仍可按 Debug 方式调试；优化后的 C++ 可使用 PDB 定位调用栈和源码，但编译器可能内联、重排或消除局部变量，不能保证逐语句单步与 Debug 完全一致。需要详细调试原生算法时，切回 Debug 配置。
