# 全局相机图像源（2026-10-21 源码）

相机后端与相机品牌分开：当前程序实现的是 **MVS 适配器**。官方手册已确认本机 `ME2P-2622-4GM-P` 属于大恒图像 MERCURY2 系列，原厂配套 **Galaxy SDK**；可以新增 Galaxy 适配器以移除 MVS 依赖并复用现有全局采集契约与算法流程，当前尚未实现该适配器。型号见 [大恒官方手册](https://dahengimaging.com/downloads/MERCURY2%20GigE%20Cameras%20User%20Manual_V2.0.1.pdf)，软件见 [Galaxy 官方下载页](https://en.daheng-imaging.com/list-59-1.html)。不打开 MVS 客户端与不依赖 MVS SDK 是两种不同情况。

若 VS 报 `JPVision.Camera.Mvs.dll` 加载失败、`0x8007007E`，先检查启动输出目录是否有这个 JP-Vision 自有适配库，再区分依赖加载失败和 MVS 自身的错误。首次构建晚生成 DLL 的复制时序已修复，构建回归为 `scripts/test-camera-build.ps1`；这类本地 DLL 缺失不能直接归因于未安装 MVS。

当前 WPF 编辑器已接入 MVS GigE 相机，支持按序列号连接、连续取流预览、取固定图用于 ROI／模板教学，以及执行流程时等待新帧。所有 `ExternalImage` 节点共享本次全局图像源；一次流程执行只取得一张检测帧，各图像源节点使用同一张图。文件图像仍可独立使用。

当前只实现单台 GigE 的自由连续采集（`TriggerMode=Off`）和业务触发检测。大恒 Galaxy、相机硬件／软件曝光触发、多相机同时采集和自动重连尚未实现。“执行”触发算法处理，不是向相机发送曝光触发命令。

## 在编辑器中使用

1. 在 MVS 中停止取流并**断开相机连接**，释放设备控制权限；仅停止显示或停止取流可能仍保留连接。
2. 从工具栏“相机管理”或“设备 → 全局相机管理…”打开管理窗，点“枚举设备”。列表显示型号、序列号和当前 IP，按目标 SN 选择并点“连接”；IP 仅用于诊断，不作为保存的设备标识。
3. 点“开始预览”。相机持续取流，主窗口每 100 ms 拉取一次最新帧，管理窗共享同一预览位图，最多刷新约 10 次／秒；这是显示刷新上限，不是将设备采集帧率固定为 10 fps。状态显示接收帧数、当前取流阶段的平均接收 FPS、预览替换数及帧元数据。
4. 在工具栏或外部图像节点参数中，将“文件图像”切换成“全局相机”。这项选择作用于整个流程的外部图像节点，主窗口“图像与中间结果”区域会直接显示实时画面。同尺寸新帧保留当前缩放和拖动位置。
5. 调整 ROI 或学习模板前，点“取图用于教学”。主图固定为这次新取得的图像，之后预览继续更新，也不会改变教学坐标、正在绘制的 ROI 或模板像素。需要更新教学对象时再次明确取图。
6. 点“执行”／F5，或“执行到选中算子”。编辑器等待请求登记后收到的新帧，取帧超时为 5 秒，然后使用该帧执行流程。日志和状态保留 SN、实际帧号与接收时间；结果图固定为本次输入与标记，实时预览不会覆盖检测结果。需要返回预览时，在图像下拉框中选择“实时画面”。
7. “停止预览”停止取流并保留连接；“断开”释放设备。关闭管理窗只关闭预览视图，全局连接及取流继续由停止／断开控制；退出编辑器会等待采集资源释放。

同一时刻只接受一个等待检测帧的请求，忙时明确拒绝，不积压检测请求。超时、取消、停止、断开或采集故障不会把已有预览图当作新检测图，也不会输出一份新的成功结果。停止及重新开始会更换采集代际，前一代正在转换或已完成等待的帧不能进入新一代请求；故障后应先断开，再按原 SN 重新连接。

正在观看实时图时，停流、断开或故障会清空该实时图并提示状态；已固定的教学图和检测结果仍对应原输入帧。预览与执行路径、配方复用和基准复测见 [编辑器性能与实时预览](performance.md)。

## 采集参数

连接且停止取流后，可读取／写入设备支持的曝光时间（μs）、增益（dB）和采集帧率（fps）。界面显示设备实际报告的最小值、最大值和只读状态；不支持的节点显示“不支持”，不使用固定的假范围。写入后重新读取当前值与范围。

手动写曝光／增益时，设备若提供可写的对应自动模式节点，会先切换为 Off；写帧率时，设备若提供可写的帧率使能节点，会先启用，然后再次检查实际写权限及范围。参数必须停流设置。帧率同时受曝光时间、图幅、像素格式和 GigE 带宽限制，填写的帧率值不等于实际收到的 FPS。界面没有修改相机 IP、网卡或防火墙的功能。

## 本机配置和配方

本机图像源选择保存在 `%LocalAppData%\JP-Vision\camera.json`，与 `.vrecipe` 配方分开。当前仅保存源模式与首选序列号，实际 JSON 字段为：

```json
{ "SourceMode": "camera", "Serial": "GBZ25110055" }
```

`SourceMode` 为 `file` 或 `camera`。此文件不保存曝光、增益或帧率，也不保存活动连接。重新打开程序可以恢复模式和首选 SN，但不会自动连接、开始取流或换成其他相机。若首选 SN 不在枚举列表，需由操作员明确选择设备。打开文件图像会切回文件模式；在两种模式间切换会清理旧检测结果。

## C#／WPF 接入

当前源码新增独立的 net8.0 项目 `managed/JPVision.Acquisition`；消费工程使用项目引用，并保持 Windows x64。核心类型为 `MvsCameraProvider`、`GlobalCameraService` 和拥有自身像素缓冲的 `CameraFrame`。相机图像通过 `ToVisionFrame()` 复制到现有 SDK 帧，再送入长期复用的 `VisionSession`；相机驱动调用不进入各个算法节点。

在消费工程中引用 `managed/JPVision.Acquisition/JPVision.Acquisition.csproj`（会传递引用 `JPVision.Sdk`），如需现有 WPF 显示控件，再引用 `managed/JPVision.Wpf/JPVision.Wpf.csproj`。将相对路径改为该工程到源码目录的实际路径；先构建 MSVC x64 原生目标，使可选桥接出现在相同 Configuration 的输出目录。不要仅复制历史 SDK NuGet 后期待包含采集功能。

```csharp
using JPVision.Acquisition;
using JPVision.Sdk;

using var engine = new VisionEngine();
using var recipe = engine.LoadRecipe(@"C:\Recipes\parts.vrecipe");
await using var session = recipe.CreateSession();
await using var camera = new GlobalCameraService(new MvsCameraProvider());

var devices = await camera.EnumerateAsync(cancellationToken);
// 由操作员选择／确认 SN；不要按枚举序号自动切换生产设备。
string serial = "GBZ25110055";
if (!devices.Any(device => device.Serial == serial))
    throw new InvalidOperationException("未发现指定相机。");

await camera.ConnectAsync(serial, cancellationToken);
await camera.StartAsync(cancellationToken);

// 在业务检测请求到来后调用；不使用 LatestFrame 充当新触发图。
CameraFrame captured = await camera.CaptureNextAsync(
    TimeSpan.FromSeconds(5), cancellationToken);
using var frame = captured.ToVisionFrame();
using var result = await session.RunAsync(frame, new RunRequest
{
    FrameId = $"camera:{captured.Metadata.Serial}:{captured.Metadata.FrameNumber}:{captured.Metadata.ReceivedTimestamp}",
    TimeoutMs = 30000
}, cancellationToken);

// 先检查 result.Status，再读取公开输出；业务工程决定后续动作。
await camera.StopAsync(cancellationToken);
await camera.DisconnectAsync(cancellationToken);
```

`LatestFrame` 仅供最新预览快照；预览不自动运行配方。`CaptureNextAsync` 使用请求之后的主机接收单调时钟判新，超时抛 `TimeoutException`，取消遵循 `CancellationToken`，并发等待抛忙碌异常。停止、故障和代际检查在返回帧前再次确认，失败不回退到缓存图。原生接收 QPC 与 Windows `.NET Stopwatch` 使用同一单调时钟；`ReceivedTimestamp` 是主机接收计数，`DeviceTimestamp` 是设备时间，不能直接互减。它保证请求后收到的帧，不承诺严格的请求后曝光同步。

编辑器的 `RunRequest.FrameId` 使用 `camera:SN:帧号:接收QPC`，宿主可沿用此形式，将检测输入、结果和日志关联起来。帧号在重新开始采集时可能重置，不能单独作为跨采集周期的唯一标识。`CameraFrame.Pixels` 是只读快照，调用者不应修改底层缓冲；`ToVisionFrame()` 的结果按现有 SDK 规则释放。采集服务和算法运行均继续要求有效的机器绑定许可证。

## 可选依赖与资源边界

MSVC x64 原生构建会查找 `JPV_MVS_ROOT`（默认 `C:\Program Files (x86)\MVS\Development`）下的官方头文件；找到时生成独立 `JPVision.Camera.Mvs.dll`，输出到 `artifacts/native/<Configuration>`。未找到时跳过该可选目标，不阻止文件图像或算法核心构建。桥接不依赖 OpenCV Runtime，也不链接或复制厂商 SDK 导入库／运行库；运行时从已安装 MVS 的官方 Common Files 路径安全加载 x64 DLL。缺少桥接或 MVS 运行环境时，相机操作给出不可用提示，文件源继续工作。历史 NuGet 包不包含本轮采集增量。

每个 SDK 取帧缓冲都与释放配对，先复制或转换到 JP-Vision 拥有的 Gray8／Bgr24 缓冲再交给托管端；不把 SDK 缓冲指针交给 WPF 长期持有。单轴上限 32768，图幅上限 32 Mi 像素，帧缓冲上限 128 MiB。预览槽只保留最新一张，替换计数表示预览未消费的旧快照被替换，不等同于网络丢包。

## 已完成的实机验证与限制

2026-10-20 通过 MVS 接口连接 `ME2P-2622-4GM-P`、SN `GBZ25110055`、IP `169.254.162.253` 实测。报告 [camera-hardware-capture.json](../artifacts/tests/camera-hardware-capture.json) 记录了两个取流周期、每周期 8 个获取的 `5120×5120 Gray8` 帧及哈希，最终状态为 Disconnected，最新帧为空。此次短时验证不代表已完成长时间稳定性、拔线恢复、所有像素格式或其他机型验证。

本次读取曝光 `60000 μs`、范围 `14～1000000 μs`，增益 `0 dB`、范围 `0～16 dB`，帧率节点值 `4.5 fps`。这些是该设备当时报告的值，不是所有相机默认值，也不表示实际取流恒定为 4.5 fps。

首次打开在 MVS 仍保持连接时返回 `0x80000214`；官方 SDK 将它定义为 UDP 接收失败。SDK 日志显示申请独占控制权限时等待回包超时，用户在 MVS 停止取流并断开后，JP-Vision 重试打开及上述采集成功。使用前应释放其他客户端的连接，但不能将此错误码在所有场景都解释为“占用”；再次出现时应检查 SDK 日志、控制连接和网络状态。未修改系统网络、防火墙或相机 IP。
