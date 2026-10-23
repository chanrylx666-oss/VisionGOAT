# JP-Vision：C# 业务执行与原生视觉内核

## 决策与边界

2026-10-23，按“主要业务代码使用 C#、底层继续调用 OpenCV、计算热点保留 C++”调整默认执行架构。目标是让配方和业务规则可以在熟悉的 C# 工程中维护，同时保留现有算法、机器绑定许可证和已验证的图像坐标语义。

| 职责 | 当前入口 |
| --- | --- |
| WPF、工具箱、ROI 交互、配方编辑、相机服务 | 原有 C# Studio、Editor.Core、Acquisition |
| DAG 顺序、公开输出祖先、参数依赖、节点使用计数 | `managed/JPVision.Sdk/Execution/ManagedExecutionPlan.cs` |
| 全局／局部变量、参数订阅、同帧坐标追踪、ROI 原点转换 | `ManagedBindings.cs` |
| 有界表达式、条件短路、纯计算函数 | `ManagedExpression.cs` |
| 执行循环、节点状态、结果保留／发布、业务判定、错误汇总 | `ManagedExecution.cs` |
| 灰度、滤波、形态学、Blob、模板、圆线检测、展开及测量计算 | 原有 OpenCV／原生算子实现 |
| 密集卡尺采样、拟合、点集处理等计算热点 | 原有 C++ 算法组件 |
| cv::Mat、掩膜、特征、坐标平面和图像上下文的生命周期 | `native/core/kernel_execution.*` 及原图像内核 |
| 原生授权、配方完整性／资源路径与哈希、像素及结果预算 | 原有原生校验边界 |

没有另加 OpenCvSharp 或第二套 OpenCV 发行包。C# 使用现有 C ABI 以“算子”为单位调用计算内核，避免逐像素 P/Invoke，也不把原图在节点之间转成托管数组。原生算子描述仍是参数、类型、默认值及约束的共同来源。

## 运行过程

1. `VisionEngine.LoadRecipe` 先通过原生边界完成配方完整性、安全及算法兼容性校验，再从同一份已加载定义编译 C# DAG；不会重读磁盘上的另一份定义。
2. `VisionSession.RunAsync` 拍下请求参数，原生边界接纳本次帧并检查授权、Session Busy、进程并发预算和输入；每次执行仍独立，不复用历史检测值。
3. C# 按数据连接和字段订阅决定的顺序运行。它解析变量、表达式和当前输入坐标，通过 `vision_kernel_step` 明确执行一个节点；内核不自行决定下个节点。
4. 节点结果通过端口元数据返回。图像仍以原生句柄保留，C# 按最后一次使用释放临时原生端口值，并单独保留调试图片。大型点集和标记先以经过完整校验的 UTF-8 切片保留，同一端口的调试值和公开别名共享切片；最终一次生成拥有独立内存的 `InspectionResult.Data` 后释放原生字符串，避免每个节点先建立大型 JSON DOM。
5. C# 发布配方声明的输出并计算 Pass／Fail／Unknown，最后生成 `InspectionResult.Data`；原生结果容器只负责图像与授权检查。失败清空公开输出和测量，保留已完成节点的诊断结果。

变量和函数依旧是有界纯计算：无脚本执行、文件访问或反射执行入口。原生资源限制继续生效。取消与超时在算子边界协作处理，等待正在运行的 OpenCV 调用返回后再释放输入，不会强行终止计算线程。

## 如何维护

改运行规则、执行顺序、变量、订阅、状态或结果汇总时，优先修改上述 `Execution/*.cs`，并在 `JPVision.Tests` 中添加对应的 C# 行为测试。新增 OpenCV 算子或修改卡尺拟合内核时，继续修改原生算法组件及共同描述。图像坐标、有效掩膜或特征生命周期的变更需要同时验证图像流程测试。

已有 `new VisionEngine(licenseStore)`、配方目录、`RunAsync`、结果 getter、WPF 项目引用不变。`VisionEngine.ExecutionBackend`、配方和 Session 的同名只读属性可确认执行后端。

```csharp
using var engine = new VisionEngine(licenseStore); // Managed：默认 C# 业务执行
using var recipe = engine.LoadRecipe(recipeDirectory);
await using var session = recipe.CreateSession();
using var result = await session.RunAsync(frame, new RunRequest { Debug = true });
```

## 兼容入口

保留原 `vision_run_execute` 和 C++ 全流程实现，供既有 C ABI 调用方和结果对照使用。需要显式对照时使用 `new VisionEngine(licenseStore, VisionExecutionBackend.Native)`；它不改变配方内容。默认 Managed 不在遇到不支持的定义或错误时静默回退。

SDK 与原生 DLL 应成套构建、复制；默认 Managed 需要新版 `vision_recipe_definition` 和 `vision_kernel_*` 导出。业务迁移没有取消原生依赖，也没有放宽授权或配方校验。Profile 继续保留 C# 调试符号并使用优化的 C++／OpenCV；Debug 继续用于详细原生调试。

## 验证与性能边界（2026-10-23）

- 默认 Managed 路径的 Debug、Profile 各 **1032 项**托管测试全部通过；原生 Debug、RelWithDebInfo 各 **3 项**测试通过。
- Visual Studio 整个解决方案的 Profile / x64 构建，以及 WPF 的 Debug、Profile 构建通过。9 项 WPF 场景通过：订阅、模板中心定位圆、圆环展开、边缘点集、曲线修复、图像链，以及布局／输入／保存后保留结果。
- 原始 `process.vrecipe/recipe.json` 与 `20260920154524.jpg` 未修改。两后端均输出 2751 个测站、2748 个点和 2748 条距离；完整点集、距离、统计、标记及干净图像像素的规范化摘要一致：`EBD1E25A91249317BE4041B90F5EBA05B4E1F7C713C4178669A8D91426C1221A`。仅排除逐次计时、帧身份及其坐标平面前缀，不把检测点减少或降低精度作为加速方式。

实际 5120×5120 原图、原配方、保留中间结果，复用 Session；每组预热 1 次、测量 5 次，两轮交换运行顺序。比较同一对 SDK／原生 DLL。SDK 总耗时包含执行和最终结果组装，排除文件解码、WPF 显示及计时后的强类型校验：

| 后端 | 第一轮中位耗时 | 第二轮中位耗时 | 每次托管分配中位数 |
| --- | ---: | ---: | ---: |
| 原 C++ 流程执行 | 1685.8 ms | 1742.3 ms | 约 146.5 MB |
| C# 业务执行 + 同一原生内核 | 1497.2 ms | 1610.3 ms | 约 205.6 MB |

这组流程没有显示出迁移后的耗时退化，但样本有限且机器负载存在波动，不能推导“C# 总比 C++ 快”。两种后端的节点计时边界不同，应比较上述 SDK 总耗时，不直接比较 `ElapsedMs`。托管分配是全进程累计分配增量，不是存活内存或峰值，也不含原生分配；迁移后这一项仍高于原执行器。UTF-8 切片优化已把初版 Managed 的约 252.2 MB 降至约 205.6 MB，后续可继续优化最终结果组装。

明细保存在 `artifacts/tests/managed-runtime-final{,2}-{native,managed}/report.json`，测试日志为 `managed-runtime-raw-all-{debug,profile}-tests.log`，普通 VS 构建日志为 `managed-runtime-final-vs-profile-build.log`。历史性能文档中的原生时间保留原测量含义，不能直接当作当前 C# 流程时间。硬件相机、产线长时间压力和当前设备上的实际缺陷检测精度不在本次迁移验证范围内。
