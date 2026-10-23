# JP-Vision 验证记录

## 当前源码：多模板快速匹配与尺寸搜索（2026-10-23）

快速匹配／快速定位新增模板库、全库最佳匹配、等比例尺度搜索及模板身份输出。旧单模板保持兼容；模板库管理和 WPF 编辑逻辑使用 C#，计算复用 OpenCV 原生热点。更新后完整 Profile **1110/1110**、受影响 Debug **275/275**、原生两配置各 **3/3** 通过，WPF 两配置及整个 VS Profile/x64 构建通过。9 项界面场景和双配置模板库面板通过，包含运行后切换模板仍保留输出。原始失败、测试边界、操作与证据路径见 [多模板快速匹配](fast-template-bank.md#验证记录2026-10-23)。只交付源码和普通构建，没有生成发布包。

## 当前源码：C# 业务执行与原生视觉内核（2026-10-23）

默认 `VisionEngine` 改由 C# 执行 DAG、参数订阅、变量／表达式、状态汇总、公开输出和判定，原生层按节点执行 OpenCV 算法并管理图像、授权和资源预算。保留显式 Native 后端用于兼容与对照。UTF-8 结果切片共享端口和公开别名，最终结果独立拥有内存；增加并发释放、错误状态、非法 JSON、失败后诊断保留和两后端结果对照测试。

最终托管 Debug **1032/1032**、Profile **1032/1032**，原生 Debug、RelWithDebInfo 各 **3/3**。WPF 两配置和 VS 全解决方案 Profile/x64 构建通过；订阅、定位、展开、点集、修复、图像链与三种结果保留场景共 **9/9** 通过。原配方、原图未修改，两后端的 2748 个点、距离、标记及干净图像规范化摘要一致。实际两轮 Profile 中位耗时、分配代价、计时边界和证据路径见 [C# 业务执行层验证](managed-execution.md#验证与性能边界2026-10-23)。本次只验证源码及普通 VS 输出，没有制作新发布包。

## 当前源码：边缘曲线修复与补全（2026-10-21）

“边缘点集”新增原始／过滤／重建模式、邻域窗口、法向跳点容差、可选平滑和最大补全跨度。在搜索带局部坐标中做两轮去趋势中值剔除，平滑使用三角距离加权局部线性回归与两轮 Huber 降权；缺段用受跨度、搜索高度和有效掩膜约束的 PCHIP 插值，仅补内部测站。修复结果保留原始点、原始坐标和来源，补全不提高实测有效比例；无图像、无边缘或无可靠两侧时不生成虚假有效测量。默认“点集到线距离”只测实测合格原始坐标，明确切换后才使用修正／补全点集。

新增 **25** 项回归，覆盖真实像素尖峰、交替抖动、旋转／垂直边、短缺段、长缺段／端点不补、原始坐标追溯、实测覆盖、默认／显式测距、空帧恢复、裁剪原点、凹 ROI 掩膜与展开坐标。旧 DLL 三组旋转缺段用例先失败，记录 `artifacts/tests/edge-reconstruction-red.json` 所指的隔离目录。独立审查发现中值平滑只反转交替抖动相位，新增真实像素 RMS 回归确认失败，再由局部稳健回归修复；证据为该目录 `edge-jitter-red.trx`。

初次受影响测试 **92/93** 通过，最大 32768 px 宽度的原始模式因重复原始点和来源数据超出公共结果预算，旧失败保留 `edge-reconstruction-focused-before-compact.trx`。修复为原始 JSON 直接使用 `points`，省略可推导的逐点默认字段和重复 `rawPoints`；SDK 的 `RawPoints` 复用同一列表，不提高预算、不截断点集。修复后新功能及 10918／32768 px 大点集 **27/27** 通过，记录 `edge-reconstruction-compact.trx`。

最终 VS Debug/x64 源码构建通过，无编译警告／错误。核对发现测试目录曾保留较早重建的 DLL，已同步并使用与 VS 启动目录相同的 **34293874…** 运行库完成完整托管 **851/851**、CTest **3/3**；记录 `edge-reconstruction-final.trx`、`edge-reconstruction-test-sync.log`、`edge-reconstruction-ctest.log`。原生、SDK、Studio 和测试目录副本 SHA256 一致，见 `edge-reconstruction-native-hashes.json`。先前完整运行记录保留 `edge-reconstruction-final-before-sync.trx`，最终结果以同步后的记录为准。

实际 WPF **10/10** 场景通过：edge-reconstruction、edge-points、edge-points-ranges、roi-bounds、annular-unwrap、parameter-subscriptions、line-density、blob-analysis、image-process-chain、camera-main-live，汇总 `artifacts/tests/edge-reconstruction-gui.json`。新场景验证实际启用按钮、参数显隐／修改、来源和原始坐标表、默认实测测距切换到补全集、保存重载以及干净图像像素，截图 `edge-reconstruction-gui/edge-reconstruction.png` 已视查。使用合成图与模拟相机，没有据此声称已还原用户截图中的真实缺陷轮廓。

旧配方默认保持原始点集。窗口内大多数点均选错时，仅凭局部数据不能判定真实轮廓；需调整选边、ROI 或窗口。继续只交付源码和普通 Debug 输出，不制作独立发布包。用法、坐标来源和限制见 [边缘曲线修复](edge-reconstruction.md)。

## 当前源码：性能与主窗口实时画面（2026-10-21）

编辑器新增单配方 `EditorExecutionCache`，未修改配方重复执行复用计划／会话；参数、ROI、模板或连接变化按完整 JSON 失效重建。图像、输入、变量及预览请求逐次传入，不缓存结果；配方准备和执行接受在后台完成。主图与相机管理窗共享从相机自有像素直接准备的冻结位图，100 ms 拉取最新帧，保持同尺寸缩放／拖动。执行、教学和 ROI 固定输入，显式选择“实时画面”恢复；停流／断开／故障清空实时图。去除执行路径中的重复整图显示，并增加准备／执行／显示／总耗时日志。

VS Debug/x64 最终构建通过，无警告／错误。新增缓存 **9/9**、完整托管 **826/826**、CTest **3/3** 通过，记录 `artifacts/tests/editor-execution-cache.trx`、`editor-performance-final.trx`、`editor-performance-ctest.log`。缓存覆盖换图、运行输入／默认值、参数失效、非法配方无回退、Debug／Preview 不粘连、目录有界及所有权、取消恢复、并发拒绝、等待释放和旧结果独立可读。

实际 WPF **12/12** 场景通过，记录 `artifacts/tests/editor-performance-gui.json`：camera-main-live、camera-source、camera-close-empty、camera-close-stream、direct-results、fast-teaching、roi-bounds、annular-unwrap、parameter-subscriptions、line-density、blob-analysis、image-process-chain。新实时场景用模拟设备验证共享位图、缩放保留、检测及 ROI 固定、管理窗关闭后主图继续、停流／重启／故障清图；`editor-performance-gui/camera-main-live.live.json` 记录实时帧 7、固定教学帧 5，截图 `.live.png` 已视查。所有相机测试配置均位于自己的截图目录，未连接真实硬件或修改用户相机设置。

最终 Debug DLL 的 5120×5120 Gray8 合成图基准，预热 3 次后交替测量 30 次：预览位图准备中位数 **46.23 → 10.90 ms**、P95 **53.57 → 14.84 ms**；额外托管分配约 **25 MiB → 1192 字节**，不包含已有采集缓冲及非托管位图。外部图像→灰度 SDK 端到端中位数 **10.28 → 3.08 ms**，Blob 配方准备 **12.69 → 0.001 ms**；全图 Blob 本身仍为主要计算负担，未将波动归因于固定算法提速。图像尺寸和 Blob 面积 7068569 一致。完整数据、程序集及源码哈希在 `artifacts/tests/editor-performance/20261021-cache-preview-final/editor-performance.json`，可由 `scripts/benchmark-editor.ps1` 复测。

本轮未加入多进程，也未修改图像分辨率或测量精度参数。原生并发预算是多个独立会话的上限，单流程仍按依赖顺序执行；采集、显示和计算分开运行。各正常 Debug Runtime 副本 SHA256 一致（559BDDAB…），MVS 桥接副本也一致，见 `editor-performance-native-hashes.json`。仅更新源码和正常 Debug 输出，未制作独立发布包；用法及限制见 [性能与实时预览](performance.md)。

## 当前源码：首次构建遗漏相机适配 DLL（2026-10-20）

用户 VS 调试在加载 `JPVision.Camera.Mvs.dll` 时报告 `0x8007007E`。核对发现原生输出已有 DLL，但 Studio 启动目录缺失；Acquisition 的 FileList 在 15:18:11 仅包含当时已有的 PDB，原生 DLL 到 15:18:33 才完成。顶层 `None` 的 `Exists` 在求值阶段过滤掉尚未生成的 DLL，且 Acquisition 未直接等待 NativeRuntime 的解决方案依赖，导致本次生成结束后也未传递复制。此错误发生在 JP-Vision 自有适配层加载阶段，不能据此判定 MVS SDK 未安装。

改为 `GetCopyToOutputDirectoryItems` 前执行期收集带 TargetPath 的 ContentWithTargetPath，补 Acquisition 的原生解决方案依赖，并让 Acquisition／Studio 的 VS 快速检查交由 MSBuild 增量构建处理。缺桥接与桥接依赖加载失败现在分别提示。已将实际缺失 DLL/PDB 补回用户启动目录，DLL 与原生源文件 SHA256 一致；没有停止用户调试、替换已加载托管程序集或操作相机。新的托管诊断和工程修改在用户下一次停止调试、F5 重新构建后生效。

新增 `scripts/test-camera-build.ps1`，独立 GUID ArtifactsPath 验证 **4/4**：求值后生成资产仍传递复制、删除目标后增量恢复、更新源后的 DLL/PDB 哈希传播、原生目录不存在时文件帧正常工作且不加载相机桥接。四次构建无警告／错误，6 份 FileList 的输出均位于私有测试目录；文本复制 fixture 从不当作 DLL 加载。记录 `artifacts/tests/camera-copy-regression.json`，没有重建用户正在调试的正常 bin/obj。未将这次复制测试描述为新的相机取流实测。

## 当前源码：MVS 全局相机图像源（2026-10-20）

WPF 新增全局相机管理及文件／全局相机选择，所有 ExternalImage 节点共享一次检测的新帧。独立管理窗持续预览，主图和 ROI／模板使用固定快照；F5／执行到选中节点等待请求后收到的新帧，日志及 FrameId 关联 SN、帧号与接收 QPC。连接配置只保存本机源模式和 SN，不进入配方、不自动连接。独立 net8 Acquisition 与可选 MVS C ABI 桥接管理采集和 owned Gray8／Bgr24 帧；算法 Runtime 不依赖 MVS。停止、故障、超时、取消、代际变化不会用预览缓存完成检测。仅实现单台 GigE 连续采集与业务触发检测。

新增模拟采集定向 **27/27** 通过，完整托管 **817/817**、CTest **3/3** 通过，报告 `artifacts/tests/camera-acquisition-focused.trx`、`camera-final.trx`、`camera-ctest.log`。覆盖 borrowed-buffer 复制、padded rows、旧接收 QPC、单最新槽、并发等待、超时／取消恢复、停止期间迟到帧、断线和重连、序列号／未来时间错误、主动故障停驱动、许可、设备参数及真实算法运行所有权隔离。独立审查修正了故障未主动停流、已完成 TCS 跨代际交付与未连接服务关闭重入风险。VS Debug/x64 构建完成，无编译警告／错误。

实际 WPF **11/11** 场景通过，记录 `artifacts/tests/camera-gui.json`：camera-source、camera-close-empty、camera-close-stream、direct-results、fast-teaching、roi-bounds、annular-unwrap、parameter-subscriptions、line-density、blob-analysis、image-process-chain。新源场景使用模拟设备，通过实际按钮验证枚举、连接、停流曝光设置、F5 等待新帧、教学固定图与 ROI 不受预览覆盖、文件源恢复及独立配置。修复管理窗白底白字并加入小窗滚动；正常／小窗截图已视查，UTC 接收时间按本地时区显示的新断言已通过。两项关闭场景真实走 OnClosing，验证未连接或正在等帧时均正常退出、服务断开且已打开设备被释放，未绕过关闭流程。截图位于 `camera-gui/camera-source.camera.png` 和 `.camera-small.png`，关闭记录为相应 `.closed.json`。

实机通过 MVS 连接 **ME2P-2622-4GM-P / GBZ25110055 / 169.254.162.253**。`artifacts/tests/camera-hardware-capture.json` 记录两个启动／停止周期，每周期取得 8 帧 **5120×5120 Gray8**，校验每次接收 QPC 晚于请求、像素摘要及 ToVisionFrame 转换，停止清空 latest，最后 Disconnected。第二周期设备帧号从 1 重启，接收时钟和服务代际判新保持正确。保存的实机图 `camera-hardware-frame.png` 已查看；未根据当前曝光图判断工件检测精度。实机未写曝光／增益／帧率，不改网络、IP 或防火墙。SDK 日志两次停止统计 LostPackets=0、NetLostFrame=0，属于本次短时记录，不是长稳或所有格式认证。

最初 DLL 加载 126 已通过正规化绝对 Windows 安装路径修复；旧失败报告保留 `camera-hardware-list-before-path-fix.json`。MVS 仍连接时第一次打开返回 **0x80000214**（官方含义 UDP 接收失败），日志显示申请控制权限等待回包超时；用户明确停止取流并断开 MVS 后采集成功，旧报告 `camera-hardware-capture-before-mvs-disconnect.json`。没有把该通用网络错误统一映射为占用。最终 Runtime 与可选桥接各输出副本 SHA256 一致，见 `camera-native-hashes.json`；仅源码及正常 Debug 输出，没有独立发布包。实际彩色／高位深转换、拔线恢复、长时间稳定性、大恒、硬件曝光触发和多相机仍未实机验收。操作与 SDK 见 [全局相机图像源](camera-source.md)。

## 当前源码：Blob 连通区域分析（2026-10-20）

新增检测工具 `BlobAnalysis`，支持普通灰度／彩色图及已有二值掩膜，固定／Otsu／Triangle 阈值、暗目标反相、4／8 邻域、内置 ROI、面积／宽高／网格圆度／宽高比／填充率／边界筛选、排序、选中索引与数量判定。面积为实际前景像素数，孔洞不计入；`pixelEdges` 周长包含孔洞边界，圆度按该周长计算。输出完整区域列表、质心、包围框、统计与独立绿色标记，单一图像端口仍传递干净上游像素及结果。选中区域字段可订阅，缺失结果不回退旧值。SDK 新增 `GetBlobAnalysis`，旧轮廓面积及公共 C ABI 保持原语义。原始候选最多 100000、合格区域最多 10000，超限报错而不截断。

旧 DLL 的最小 Blob 用例因未知组件失败，记录 `artifacts/tests/blob-analysis-red.trx`。初始定向 **43/43** 通过；增加 SDK 周长整数及坐标平面一致性回归后，完整托管 **790/790** 通过，其中 Blob **45** 项，记录 `blob-analysis-final.trx`。覆盖孔洞、单像素、4／8 邻域、阈值和已有掩膜、全部筛选与排序、计数判定、ROI／裁剪／展开平面、空帧恢复、标量订阅、两级容量上限及隐藏 Features 统计保留。VS Debug/x64 与原生构建完成，无编译警告／错误；CTest **3/3**，记录 `blob-analysis-ctest.log`。

实际 WPF **8/8** 场景通过：`blob-analysis`、`image-process-chain`、`image-inline-circles`、`roi-bounds`、`annular-unwrap`、`parameter-subscriptions`、`line-density`、`edge-points`，汇总 `artifacts/tests/blob-analysis-gui.json`。新场景验证 ROI 绘制／编辑、阈值切换、贴边／面积／宽度筛选、空结果清表与恢复、保存重载、原点坐标与独立绿色标记。截图 `blob-analysis-gui/blob-analysis.png` 已视查，两个区域面积为 1100／1000、总面积 2100，带孔区域周长为 180。仅合成图和本机窗口验证，未据此声称实物精度或固定帧率。正常 Debug 原生库副本 SHA256 一致，记录 `blob-analysis-native-hashes.json`；交付源码与 Debug 输出，未制作独立程序包。操作及 SDK 用法见 [Blob 分析](blob-analysis.md)。

## 当前源码：高密度直线卡尺与拟合诊断（2026-10-20）

直线查找／直线卡尺与内置 A/B 直线的数量上限扩大至 32768，剔除点数至 32766；缺省数量仍为 24，圆卡尺及圆测量侧继续限制在 256。修复高密度诊断的平方级查找开销，直线拟合最多细化 128 轮，超限明确失败。补充初始／保留点候选的最大偏差和 RMS、真实采样间距及有效采样数量，WPF 区分采样成功与拟合成功。采样预算覆盖指引、中线和边界全部路径。另修复全宽／部分越界 ROI 在预览中被旧边界检查清空的问题；最多显示 256 窗口，实际使用完整设定数量。

修改前 1024 卡尺最小场景被旧上限拒绝，记录 `artifacts/tests/line-density-red.trx`。定向 **78/78** 通过；补充渲染索引与 SDK 可空诊断读取后，完整托管 **745/745**、CTest **3/3** 通过，记录 `line-density-final.trx` 与 `line-density-ctest.log`。VS Debug/x64 构建成功，无编译警告／错误。

实际 WPF **17/17** 场景通过（`artifacts/tests/line-density-gui.json`）。新 `line-density` 验证 10918×287、1024 个真实点、完整拟合、抽稀预览、资源与剔点冲突提示、台阶边真实采点后按容差拒绝，以及恢复与保存重载；成功和失败截图均已查看。首次界面失败记录保留在 `line-density-gui-before-fix.json`，含上述真实预览缺陷和两个旧文案断言，修复后全部通过。操作与 SDK 字段见 [直线卡尺密度与诊断](line-caliper-density.md)。仅源码与正常 Debug 输出，未制作独立发布包。

## 当前源码：边缘点集高像素参数范围（2026-10-20）

边缘点集采样间距扩展至 1～32768 px、切向投影宽度至 1～255 px（奇数）、搜索高度至 8～32768 px、测站数量上限至 2～32769；默认值与 Sigma、灰度梯度、比例含义保持。点集到线距离同步接收完整点数，SDK 无旧的 10000 点限制。Editor 估算与 WPF 数值／绘图范围同步，投影宽度选项读取组件描述；普通圆／直线卡尺继续保持原有范围。参数面板区分搜索带长度、高度、站间距及投影宽度，显示真实预计站数、最低有效点数和可执行状态，不再把超限预览误报成 0 站。16 Mi 采样预算保留。

旧 DLL 的间距 600 px 被上限 256 拒绝，记录 `artifacts/tests/edge-point-ranges-red.trx`。修改后新定向 **27/27** 通过，含搜索高度 4096／32768、宽度 255 均值及中值投影、10918／32768 全宽每像素采样及完整逐点距离、默认／物理范围、组合预算与实际估算。受影响 WPF **8/8** 通过，记录 `artifacts/tests/edge-point-ranges-gui.json`；新场景实际设置间距 512、投影宽 65、搜索高度 2304，验证超过数量／工作量时仍显示真实站数、恢复运行、保存重载和干净像素。截图 `edge-point-ranges-gui/edge-points-ranges.png` 已查看。VS Debug/x64 与原生构建无警告／错误，CTest **3/3**；操作及新范围见 [边缘点集](edge-points.md)。只更新源码和正常 Debug 输出，未制作独立发布包。

最终完整托管 **711/711** 通过（此前 684 项 + 新增 27 项），记录 `artifacts/tests/edge-point-ranges-final.trx`。原生、Studio 与 Tests 的运行库摘要一致，记录 `edge-point-ranges-native-hashes.json`；本轮汇总为 `edge-point-ranges-verification.json`。前一步 ROI 的 48 个完整界面场景通过后，本次参数范围变更重跑了上述 8 个受影响场景。

## 当前源码：高分辨率与贴边 ROI（2026-10-20）

修复普通算子的 ROI 完整包含限制与非矩形包围框额外一个像素造成的误报。图像处理使用有效交集，卡尺与参考型采样保持教学几何并忽略无效采样；完全无交集或有效掩膜全零时返回不可用，不扩充画布、不退回全图。保留独立裁剪的严格尺寸约束和现有图像／采样预算。

修改前 10918×287 全宽水平与轻斜卡尺均复现 `Module ROI lies outside its input image.`，报告 `artifacts/tests/roi-bounds-red.trx`（2/2 预期失败）。修改后定向 **38/38** 通过，含新 36 项与两条旧越界契约更新；完整托管 **684/684**、CTest **3/3** 通过。VS Debug/x64 构建成功且无编译警告／错误，原生、Studio 与 Tests 的运行库摘要一致。报告 `roi-bounds-focused.trx`、`roi-bounds-final.trx`、`roi-bounds-ctest.log`、`roi-bounds-build.log`。

回归覆盖五类 ROI 贴边／部分越界／完全越界、形状有效掩膜为零、裁剪原点与坐标订阅、独立展开平面、32768 全宽、真实圆／线／边缘点采样、无效区域不得形成假边缘、轮廓输入适配及内置 A/B 测量。实际 WPF 新场景 `roi-bounds` 验证源圆环展开为 10918×287、全宽倾斜卡尺与点集、部分裁剪及完全越界提示、恢复与保存重载；截图 `artifacts/tests/roi-bounds-gui/roi-bounds.png` 已查看。操作见 [ROI 边界说明](roi-bounds.md)。仅验证合成图像及本机流程，交付源码与正常 Debug 构建，没有生成独立发布包。

WPF 全部 **48/48** 场景通过，记录 `artifacts/tests/roi-bounds-gui.json`：47 个正常已授权场景执行成功，另一个空许可证场景按预期锁定编辑器。

## 当前源码：不规则边缘点集与点到线距离（2026-10-19）

新增 `FindEdgePoints` 和 `MeasurePointLineDistances`。完整托管测试 **648/648** 通过（原 607 项 + 新 41 项），记录 `artifacts/tests/edge-points-final.trx`；定向报告 `edge-points-focused.trx` 为 **41/41**。CTest **3/3** 通过。VS Debug/x64 解决方案构建成功，0 警告、0 错误，记录 `edge-points-build.log`；原生输出与 Studio／Tests 的运行库摘要一致。

WPF 窗口流程 **46/46** 通过（原 45 场景 + 新 `edge-points`），记录 `artifacts/tests/edge-points-gui.json`。新场景实际绘制搜索带、预览采样站、调整参数、连接独立真实拟合直线、读取完整 87 点与逐点垂距表、保存重载；验证绿色标记不写入像素，空帧不沿用结果。截图 `edge-points-gui/edge-points.png` 展示不规则曲线、参考线、垂线及统计。

回归覆盖旋转／矩形搜索带、裁剪原点、缺失测站不跨段连线、采样顺序、双路与串联上下文、极性、零阈值平图、统计项、公差、订阅缺失及超界 fallback、坐标平面拒绝、SDK 合约校验与采样预算。新增真实模板教学与 PositionFixture 场景，验证搜索带随旋转／平移、有效中心为负的部分可见范围及完全越界；修复矩形搜索带定位后形状不兼容和运行时重复检查原始坐标边界。汇总 `artifacts/tests/edge-points-verification.json`；操作见 [边缘点集](edge-points.md)。只验证合成图像和本机窗口，未声称实物精度验证；源码与正常 Debug 构建交付，未生成独立发行包。

## 当前源码：参数订阅、变量与计算函数（2026-10-19）

新增 ROI 嵌套数值订阅、配方全局／算子局部变量、纯计算函数及 WPF 管理／订阅界面。完整托管测试 **607/607** 通过（报告 `artifacts/tests/subscriptions-final.trx`），包括原有 572 项及本次 35 项回归；CTest **3/3** 通过，新增独立表达式契约测试。VS Debug/x64 解决方案生成成功，0 警告、0 错误，记录 `artifacts/tests/subscriptions-build.log`。

WPF 实际窗口流程 **45/45** 通过，记录 `artifacts/tests/subscriptions-gui.json`。新场景通过真实参数按钮、变量／函数管理窗和订阅弹窗建立圆心表达式、上游圆心 Y、局部外半径与内半径计算，验证原点 (700,-90) 换算、全局偏移更新、实际值显示和保存重载；已检查 `subscriptions-gui/parameter-subscriptions.png`。模板跟随、位置修正与全部原有 44 个窗口场景继续通过。

核心回归覆盖移动目标同帧订阅、空帧不使用 fallback／历史结果、裁剪原点、独立展开平面拒绝、禁止重复定位变换、变量运行覆盖隔离、字段／变量／函数循环、类型和除零错误、哈希、复制、局部引用的坐标来源去重及条件分支不误判 Unknown。原生输出与 Studio／Tests 的运行库摘要一致；汇总为 `artifacts/tests/subscriptions-verification.json`。采用合成图像验证，未声称生产相机或实物精度验证；仅更新源码与正常 Debug 构建，未生成独立发行包。

## 当前源码：圆环展开与独立坐标平面（2026-10-19）

新增 AnnularUnwrap，支持圆环/扇环、方向、径向顺序、自动或显式尺寸、最近邻/双线性、Gray8/Bgr24 和部分越界有效掩膜。模块可绘制/调整 ROI 并数值编辑圆心、半径与角度；展开图及下游预览明确标记展开图坐标。输入不被改写，展开不继承原图圆线、Pose、fixture 和旧 Overlay；输出保留可反算的坐标映射，下游处理与裁剪保持映射和独立平面。SDK 提供 GetAnnularMapping/MapToSource，检测及测量结果正确声明 annularUnwrapped，包括嵌套圆/圆环。相同展开分支内可测量，跨原图/展开图或两个独立展开分支的几何混接明确失败。

先以未知算子取得红测试记录 annular-unwrap-red.trx；真实采样用例覆盖四方向、同扇区反向、径向顺序、自动尺寸、单行、彩色、插值、非零原点、有效区和全越界、采样预算、标记隔离、SDK 坐标反算、下游裁剪与检测、展开图圆拟合及跨平面测量。完整托管 **572/572**（原 543 + 新 29）通过，记录 artifacts/tests/annular-final.trx；CTest **2/2** 通过。VS MSBuild Debug/x64 解决方案生成成功，记录 annular-build.log。最终 WPF **44/44** 通过，记录 annular-wpf.json，截图 annular-wpf/；新场景实际操作绘制、调整、数值控件、输出尺寸及插值，保存重载并逐像素检查展开结果。

已独立审查原生算法及坐标上下文合流、共享值生命周期和预算；发现嵌套圆/圆环空间声明仍为 original，已修复并用展开图真实圆检测覆盖。未验证实物重复精度或毫米标定，也不声明展开图像素距离等同原图物理距离。汇总见 artifacts/tests/annular-verification.json；操作见 annular-unwrap.md。未生成独立发布包。

## 当前源码：统一图像链与 A/B 内置检测（2026-10-19）

所有可见模块统一 Image 输入/输出；原 typed 端口继续支持旧配方。普通图像内部灰度分割、轮廓提取与筛选，输出实际掩膜；检测/测量输出干净图像、独立绿色标记及附带结果。四种几何测量支持单图 A/B ROI 真实卡尺检测，缺失数据为无效值/Unknown；PositionFixture 支持图像携带 Pose 或旧独立 Pose，两种路径均验证。SDK 原检测/测量 getter 可直接读取公开 Image 的附带值。

首轮新增用例 33/35 通过，孔洞用例失败，逐阶段公开图像定位到轮廓重建的 MatExpr 复用了源掩膜，填充轮廓改写了孔洞。分离目标缓冲区后保留原掩膜孔洞；同时修复内部阈值分割可能原地写共享 Gray8 图像的问题。增加四种模块共享输入逐像素不变验证、逐阶段孔洞验证、筛选后实际像素与原图坐标、改变掩膜后重新提取轮廓、同帧对象生命周期、旧双分支合流、当前帧空结果、Image SDK getter、单图 PositionFixture 有/无 Pose、下游标记改色去重等覆盖。

完整托管 **543/543** 通过，记录 artifacts/tests/image-pipeline-final.trx。CTest **2/2** 通过。正常 VS MSBuild Debug/x64 解决方案已成功构建，日志 artifacts/tests/image-pipeline-build.log。WPF 最终窗口 **43/43** 通过，记录 artifacts/tests/image-pipeline-wpf-final.json，包含新增单图测量、真实处理链与单图定位教学；截图位于 image-pipeline-wpf/。完整汇总及 Runtime 文件哈希见 image-pipeline-verification.json。

只更新源码与正常构建产物，没有制作独立发布包。实际工业相机、工件重复精度和物理毫米标定未在本轮验证。

## 当前源码：算子精细参数与参数面板（2026-10-19）

扩展灰度通道、高斯核宽高／双向 Sigma／边界、五种阈值方式、形态学核形状／核高／边界／梯度与顶帽黑帽、圆线与双边卡尺的均值／中位数投影和亚像素开关、自动圆平滑与自动线 Canny 选项。旧 ID、参数名、ABI、原图坐标及缺省算法行为保留。参数面板分组，显示范围、用途、单位和步长，支持数字微调、恢复单项默认及撤销。圆环 ROI 隐藏由 ROI 宽度和角度控制的无效手动指引项；线宽与结果圆环带宽分别说明。

真实像素回归发现 Otsu／Triangle 将多边形包围框内、ROI 外的亮像素纳入统计：refined-parameters-mask-red.trx 两项均为预期白像素实际为黑像素。改用 OpenCV 5 的 thresholdWithMask，仅统计有效 ROI，初始化掩膜外输出；原无掩膜固定阈值路径保持。另验证不同核的实际像素支持、边界、独立方向、中位数抑制窄划痕、亚像素开关、6 种 Canny 组合、输入不被修改及非零原点。二值形态学不接受 128 等非二值边界，自定义灰度工具不会继承此约束。

最终 VS MSBuild Debug / x64 解决方案生成成功，日志 artifacts/tests/refined-parameters-build.log。完整托管 **501/501**：artifacts/tests/refined-parameters-final.trx；原生 **CTest 2/2**。WPF 窗口 **39/39**，新增四项精细参数场景实际触发输入／选择／加减／恢复与撤销，再保存重载运行；原 35 项匹配、ROI、卡尺、拟合失败、定位和距离测量场景均通过。四项新场景在最终通用参数约束调整后再次运行。汇总和截图位于 artifacts/tests/refined-parameters-wpf.json 与 refined-parameters-wpf/，已目视查看二值化、形态学和卡尺参数截图。

NMake 包含路径补入 core/components，预处理定义与 CMake 一致，已读取 VS 生成后的属性确认 C++20／UTF-8 与 OpenCV include 路径。实际编译成功不代表已操作用户 VS 清除 IntelliSense 缓存。匹配核心算法未改动，未验证真实相机、物理测量精度或另一个 .NET 8 宿主；SDK 和 WPF 控件的 net8 目标已成功构建。无独立模型审查或发布包声明，用户仍直接 VS F5。操作见 parameter-refinement.md。

## 当前源码：圆圆／线圆距离、绿色结果及 VS 工作流（2026-10-16）

原 MeasureCircleDistance 扩展为“圆圆距离”，distanceMode 默认 centerDistance，保持既有组件 ID、平铺圆源和旧圆心距行为；可切换 edgeDistance。新增 MeasureLineCircleDistance，直线／圆两个 Features 输入，默认圆心到无限直线的垂距，也可测圆周最短间距。两类测量同时输出 centerDistance／edgeDistance，value、公差及距离段使用所选方式。圆周相交／相切测得有效 0，包含／同心圆测圆周间隙；零距端点在真实交点，不用内部投影冒充圆周。缺目标／失选／不可用时新数据为空、Unknown，不保留旧值。

结果默认色集中为 #00FF00，涵盖拟合圆、圆环、线、拟合点、距离及双边卡尺端点、轮廓、定位轴和图形文字；SDK／Studio 缺省图元同为绿色。显式颜色继续生效，测试保留 #123456 自定义色；示例旧默认色已更新并重新计算配方内容摘要。界面编辑状态与结果样式保持各自用途。

circular-distance-red.trx 的 16 个行为红例复现原版缺少模式、线圆组件和绿色默认。相关测量 65/65、SDK／颜色／错误文本相关 45/45 通过；最终完整托管 **450/450**：artifacts/tests/circular-distance-final.trx，原生 **CTest 2/2**。真实图像包含圆圆相离、相交、双向包含、同心／重合，线圆分离、相交、35°／竖直线，非零 origin=(900,-80)，失选、先有后无、精确公差边界、下游字段绑定；SDK 单独检查旧圆心距 JSON 与两个读取接口、缺失元数据、非法半径、错选数值和颜色缺省。

WPF 窗口流程 **35/35**：artifacts/tests/circular-distance-wpf.json。六个新场景实际驱动 ROI 绘制处理、距离方式 ComboBox、公差、两种数值显示、绿色叠加、空结果清除、JSON 导出和保存重载；其余 29 个回归既有图像链、模板、Fixture、ROI、卡尺和线线距离。场景截图与数据在 artifacts/tests/circular-distance-wpf。正常圆圆中心距 180.00000000049633 px、圆周间距 126.07215123439997 px；线圆中心距 110.00113874807404 px、圆周间距 78.03170720009065 px。截图已人工视觉检查绿色连线、端点和结果面板；这是应用内部 smoke 驱动的窗口验证，不声称外部鼠标自动化或实物精度验收。

按用户要求转为 VS 源码工作流：JPVision.sln 新增 SDK 对 NativeRuntime 的 ProjectDependencies。使用 Visual Studio 18 MSBuild，以 Debug / x64、普通 Build 成功构建整个解决方案，日志顺序先 NativeRuntime 后 SDK／Studio；Studio 普通 bin 目录所带 Runtime 摘要与本次 C++ 核心一致。build.ps1 的额外 publish 改为显式 -PublishStudio，test.ps1 指向普通 bin，两个脚本语法验证通过。本轮没有新增版本 EXE 目录、publish、安装包或 NuGet，也没有改动授权或关闭先前用户程序。

完整证据及关键源文件摘要：artifacts/tests/circular-distance-verification.json。SDK 的 net8 目标编译及旧 JSON 兼容已验证；本轮执行使用 net10 测试／Studio，未复跑此前独立 net8 宿主。源码在本任务内按几何、兼容性、默认值和 VS 构建顺序复核，未声称独立审查者。当前长度为原图像素，尚无毫米标定。

## 当前源码：两个直线查找的线线距离（2026-10-16）

新增 MeasureLineDistance（线线距离），并在 MeasureGeometry 增加 lineDistance 方式，两者共用几何计算与公差输出。测量 A 拟合线段中点至 B 无限直线的垂距，使用原图 px；以 0～90° 最小夹角检查平行容差，默认 1°、可调 0～10°。超限、缺线、索引越界与输入不可用时返回无效和空距离；真实重合线可以输出 0。新 SDK 字段可空且保留其他几何算子兼容，WPF 显示 A／B 端口、测距连线、距离、夹角、原图端点和判定，既有 JSON 导出保留完整数据。

line-distance-red.trx 的 13 项失败证明原版本缺少组件和测量方式。首轮实现的三个失败来自测试图像方向与配方不一致、以及测试误期望 ArgumentException（原生数值边界实际为 VisionException），修正测试后相关 58 项通过。补充真实像素用例验证水平／28°／竖直／−40°、反向卡尺、轻微倾斜、超限夹角、精确容差边界、未舍入公差判定、先有后无、重合零距、字段依赖、缺少／错误输入、独立裁图与原图 origin=(900,-80)。有限拟合线段不重叠时仍按无限平行线测量，不误报端点间的欧氏距离。

最终完整托管 **418/418**：artifacts/tests/line-distance-final.trx；原生 **CTest 2/2**；WPF 窗口流程 **29/29**：artifacts/tests/line-distance-wpf.json。三个新窗口场景为正常距离、3° 倾斜超限、先测到后空白；包含真实绘制／提交 ROI、调整角度容差、清除旧值、运行结果提示、JSON 导出数据和保存重载。其余 26 个窗口流程回归模板／圆拟合／Fixture／卡尺／图像链。窗口流程由应用内部 smoke 驱动实际窗口与处理函数，并检查渲染截图，不声称经过外部鼠标自动化或实物产线精度验收。

正常 25° 图像测得 71.9952185342509 px，夹角 0.0005408505562080691°；测距起点 (1121.6186926606279,61.03567859057355)、终点 (1091.1921954729032,126.28550434267751)。截图及导出在 artifacts/local-debug/line-distance/line-distance.png 与 line-distance.result.json，夹角超限图在同目录 line-distance-angle.png。samples/recipes/line-distance.vrecipe 和 samples/images/line-distance.png 来自这一已保存重载的窗口流程，可手动打开测试。

Native 与隔离 WPF Debug 均构建成功，WPF 0 警告／0 错误。程序 artifacts/local-debug/line-distance/JP-Vision.exe；文件摘要与核对记录 artifacts/tests/line-distance-verification.json，Studio 携带的 Runtime 与本次原生编译摘要一致。源码按计算、SDK 契约、显示与兼容性进行了本任务内复核；未声称第二名审查者。本轮没有重新运行历史 net8 宿主，只验证了 SDK 的 net8 目标编译与 net10 测试／Studio 执行。尚无毫米标定，未生成安装包、发布归档或 NuGet，未覆盖先前正在运行的程序。

## 当前源码：直线查找集成卡尺搜索带（2026-10-15）

FindLines 增加 method=caliper，与 LineCaliper 复用真实边缘采样和拟合；新编辑器节点默认 caliper／roiCenter，原生缺省 automatic 兼容旧配方。搜索带沿中心线放置窗口，ROI 高度统一驱动搜索高度，支持反向搜索；旋转矩形以两端点教学、端点／侧面／中心控制点调整，原 ROI 结构及单一 Result 输出保留。Fixture 保持中心线排列，轴对齐参考矩形先转换为旋转矩形，避免误变成多边形边采样。

line-strip-red.trx 复现旧 FindLines 缺少卡尺模式。真实像素检查覆盖水平、25°、竖直、−45°及反向选边、非零 origin=(500,-20)、空白图、编辑器默认值与旧原生默认值；预览与原生各个窗口的位置、法线、投影宽度和搜索高度一致。额外覆盖 Fixture 由参考 20°／1.2 变为 −40°／0.8、origin=(900,-80)，包括轴对齐与旋转参考条带，C# 预览与原生仍一致。

line-strip-outliers-red.trx 复现旧直线剔点逻辑被一侧 4 个错误采样拉偏的问题；现在以 Huber 稳健线选择剔点参考，剔点后重拟合、按点数递减迭代，最终保留点以 L2 拟合，并检查偏差。不指定剔点和指定剔除 4 点都能保留 28 个正确直线点。line-strip-focused.trx 中 51 项相关检查通过。

最终托管 398/398（artifacts/tests/line-caliper-final.trx）、原生 CTest 2/2、实际 WPF 窗口流程 26/26（artifacts/tests/line-caliper-wpf.json）通过。包含直线绘制按钮、取消、单步撤销重做、控制点几何、ROI／搜索高度同步、拟合结果、标记、空白负例及保存重载，也回归此前圆拟合／模板／Fixture 等流程。WPF 与原生 Debug 构建通过，WPF 0 警告／0 错误。最后仅调整预览颜色和结果标签，再跑 line-strip-roi／line-strip 显示检查通过。

程序和截图：artifacts/local-debug/line-caliper/JP-Vision.exe、line-strip-roi.png、line-strip.png、line-strip-empty.png。窗口为内部驱动实际 WPF 事件／共享编辑几何，不声称外部鼠标自动化或实物产线验收。合成灰度边缘约 25°，ROI 经端点调整后仍拟合约 24.998719°、RMS 0.021186 px，输出与参考 ROI 独立。只更新源码和本地 Debug，未打包或覆盖正在运行的旧程序。

## 当前源码：卡尺已有圆周点但没有拟合圆／圆环（2026-10-15）

已复现原生剔点顺序缺陷：64 个真实边缘采样中 60 点位于半径 64 的圆周，4 点集中于同一侧、半径 95。最大偏差 1 px、最小有效比例 0.8、指定剔除 4 点时，旧实现仍用受异常点拉偏的旧圆筛点，实际误剔除 56 点、只留 8 点，结果 count=0。circle-fit-refit-red.trx 保存失败证据；追加未指定／剔除不足的用例也在 circle-fit-consensus-red.trx 复现。

现在使用固定种子、有界三点候选的一致性估计选择参考圆，指定剔点后重拟合，再检查距离偏差；每次后续筛选后重新最小二乘拟合，最终圆的保留点均须满足最大偏差。取消固定 8 次上限，迭代只能在点数减少时继续，次数由至多 256 个采样点界定。未调整既有偏差阈值或最小有效比例，也未拿 ROI 或失败候选充当输出。

9 个真实像素相关用例覆盖 FindCircles／旧 CircleCaliper、指定剔除 0／1／3／4／6 点、无边缘和双半径非圆点集。circle-fit-consensus-green.trx 9/9。指定剔除 4 点的实际 WPF 结果为：64 点采样、4 点剔除、60 点保留，原图圆心 (1080.0011666,100.0000849)、半径 63.9526150、RMS 0.0173337 px；直接输出和下游标记均有圆环。非圆负例只留 32 点，未达到所需 52 点，返回 InsufficientInliers、selected=null、无圆或圆环。窗口已检查两类结果的计数／原因显示，以及保存重载。

最终 384/384 托管（artifacts/tests/circle-refit-final.trx）、CTest 2/2、23/23 实际 WPF 窗口流程（artifacts/tests/circle-refit-wpf.json）通过；原生与隔离 WPF Debug 构建通过，WPF 0 警告／0 错误。程序、截图、保存配方位于 artifacts/local-debug/circle-refit。SDK 新增可空的拟合状态／原因／阈值诊断字段，旧字段缺失保持 null。只修改源码、本地 Debug 与说明，没有打包、改授权或覆盖正在运行的 circle-annulus 实例。用户原图与本次实际参数未提供，合成点集结论不代替用户原图重跑或实物精度验证。

## 当前源码：指定内孔单圆教学（2026-10-15）

用户截图中为全图自动找圆；现有 circles.vrecipe 示例限制原图半径 35～60 px、预期数量 3，不能据此搜索大内孔。新增参数前置的找圆方式／半径说明，以及“绘制内孔圆（亮 → 暗）”：确认教学圆时原子切换到沿 ROI 边界的单圆卡尺，剔除后使用实际边缘重拟合，不更改霍夫算法或旧配方的自动检测含义。重画保留已调采样／拟合设置，业务预期数量不自动修改。

实际 WPF 窗口回归 large-inner-circle：导入 5120×5120 PNG，主孔真实半径 1400 px，外圈有 24 个半径 50 px 的小孔，四个方向有 20 px 局部凸起。先用原示例范围复现多个小孔候选，红例 large-inner-circle-red.png.error.txt 确认旧界面缺少指定内孔入口。绿例点击实际按钮，教学圆心 (2567,2555)、半径 1410，得到单圆心约 (2560.003216,2559.999786)、半径 1399.996527、直径 2799.993054、RMS 0.020844 px；64 点剔除 4 点、保留 60 点，下游标记环以最终拟合圆为中心。检查取消、Esc、单步撤销重做、保留调参、预览／原生卡尺一致及保存重载。

375/375 完整托管测试通过：artifacts/tests/circle-target-final.trx。21/21 实际 WPF 窗口流程通过：artifacts/tests/circle-target-wpf.json，包含上述新增流程及原有匹配、定位、ROI、卡尺、字段绑定等；隔离 Debug 构建 0 警告／0 错误。程序和截图在 artifacts/local-debug/circle-target。此轮仅修改 WPF 操作，不改 C++／SDK 算法；用户零件原始像素文件未提供，合成图结论不代替实物精度验收。未生成发布包。

## 当前源码：卡尺拟合结果圆环传递修复（2026-10-15，M0～M2 后续）

已复现并修复 BuildFeatureOverlay 只绘制圆周、遗漏 CircleFeature.outputAnnulusWidth 的问题。结果圆环统一由 feature_overlays 根据最终 CircleFeature 生成，直接结果包和下游标记各输出一份圆环；原先结果包的重复生成位置移除。旧接口不改名，目标数量取 count／Features，不能用 Overlay 图元个数计数。

真实像素红绿测试：360×360 Gray8、origin=(900,-80)，ROI 指引半径 68，真实主圆半径 64，四个采样方向有半径 74 的凸起。40 个采样点剔除 4 个后保留 36 点，重新拟合原图圆心约 (1080,100)、半径 63.9686488891；宽 24 的结果环内外半径为 51.9686488891／75.9686488891。标记和直接输出一致，带宽设为 0 时仅画圆周。未改变拟合算法，也未使用 ROI 半径充当结果。annulus-marker-red.trx 复现下游缺失，annulus-marker-green.trx 67 项相关检查通过。

最终完整托管测试 375/375（artifacts/tests/annulus-marker-final.trx），CTest 2/2；隔离 Debug 构建 0 警告／0 错误。实际 WPF 窗口 caliper-result-annulus、single-result、caliper-boundary-circle 均通过，包含单口分支、切换圆查找／下游标记、带宽变更、拟合量保持不变、保存重载；截图及 JSON 在 artifacts/local-debug/circle-annulus。程序为该目录下 JP-Vision.exe。只生成 Debug，未打包、未改动用户正在运行的旧实例。

## 当前源码：M0～M2 定位、ROI 与测量（2026-10-15）

最终 Native Debug、WPF Studio、net8 WPF 宿主构建通过；Studio／宿主 0 警告 0 错误。Runtime、Studio 隔离目录与宿主隔离目录的 Runtime SHA-256 相同。本地编辑器 artifacts/local-debug/m0-m2/JP-Vision.exe；没有打包或覆盖旧 Release。

托管完整检查 373/373（artifacts/tests/m0-m2-final-debug.trx），CTest 2/2。20/20 实际 WPF 窗口流程记录 artifacts/tests/m0-m2-wpf-final.json，截图／保存配方位于 artifacts/local-debug/m0-m2。使用实际窗口内部流程驱动，非外部鼠标自动化。覆盖旧配方、模板教学、大图、一个可见输出、圆／线边界卡尺、新高级 ROI、Fixture、双边宽度、参数绑定、运行到选中与无许可证锁定。

FixturePipelineTests 的原生真实像素检查包含非零参考角度／尺度、模板框外两孔、正负角度、0.8／1／1.2 缩放、非零 origin、搜索扇环、过期参考、无匹配、两类双边矩形及缩放候选宽度。卡尺／霍夫／图像处理／轮廓链的全越界检查均返回不可用与 Unknown；正常空匹配可单独按显式数量 0 判 Pass，缺失输入的下游不得 Pass。红例 outside-sampling-red.trx 曾复现旧误判；对应绿例与最终全套已通过。

真正的 .NET 8.0.27 Windows x64 独立宿主（managed/JPVision.Net8.WpfProbe）加载 GUI 保存配方／PNG，得到中心距 69.6169878364 px、Pass；读取强类型 Fixture、图像和 Overlay，RenderTargetBitmap 验证 650 个红色图形像素。截图和数据：artifacts/local-debug/net8-host/fixture-overlay.png(.json)，日志 artifacts/tests/net8-host-final.log。该项目不引用 Studio，不读取私钥，不绕过许可。

Debug 固定数据为 600×400 Gray8、30°／1.2 倍位姿，CPU Intel Core 5 210H；每组预热 3 次后测 30 次：

| 场景 | P50 | P95 |
|---|---:|---:|
| 有目标，定位＋两圆＋中心距＋公差 | 60.91 ms | 100.88 ms |
| 无目标，依赖跳过、Unknown | 36.85 ms | 48.26 ms |

这是 SDK 完整调用的本机 Debug 观测，非纯算法或固定 FPS 承诺；进程峰值工作集约 126.1 MiB，并非单节点峰值内存。两组 activeNativeHandles 保持 7（测量时仍持有输入／配方／结果等对象），activeNativeStrings 与 activeRuns 均为 0。原有 1000 次计数流水线资源检查也通过，详见 artifacts/tests/performance.json。

高级形状在数值、掩膜、卡尺预览、保存重载与 net8 WPF 图元上验证；模型缓存有摘要键和 16 项／8 MiB 上限。真实工件重复精度、相机实机、任意仿射、多目标迭代与标定未验收。详细行为见 [本轮实现](m0-m2-implementation.md)；下面章节保留历史验证背景。

## 当前源码：模板中心跟随与圆环输出（2026-10-14）

按用户要求核对本机 VisionMaster 4.4 的 GetMatchPoint、GetCircleCenter、GetCircleRadius、GetROIAnnulus、GetOutputCircleAnnulus 及 Annulus 字段。证据来自公开 SDK 声明／模块配置；没有反编译算法或运行厂商程序。所用 reverse-engineering 工具索引生成于 artifacts/tests/reverse-tool-index.md，本次不需要安装二进制分析引擎。

Native Debug、WPF 编辑器 Debug、net8 WPF 显示控件构建均通过，0 警告／0 错误；CTest 2/2，managed 全套 316/316（新增 native 定位 24、SDK 10、编辑模型 8），报告 artifacts/tests/match-center-debug-final.trx。新回归使用真实像素：3 类匹配器、模板框之外的圆、非零原点、前／后序裁剪及灰度转换、连续平移、多目标索引、不匹配、索引越界、缺定位输入、部分／全部越界圆 ROI、未画 ROI 搜索半径、旧配方缺省、真实拟合圆心与定位中心不同、输出／搜索圆环不同及输出带宽。

本地程序 artifacts/local-debug/match-center/JP-Vision.exe 通过 13 个实际窗口流程：新增 match-center-circle，加此前 12 个流程。新场景验证内置模板绘制与学习、全图搜索、单 Result 连线、准备输入后圆形选项、绘制自动保存偏移、物品移动后 ROI／卡尺跟随、负中心静态保存仍可执行、原图视图下未命中清空旧定位、空图预览及恢复检测、保存／重载。已查看 match-center-match-center-circle.png：模板中心 (284,198)，真实拟合圆心 (256,198)，R≈17.86746、D≈35.73492；输出环内外半径约 15.86746／19.86746。测量值来自实际边缘，模板中心只作为参考。

最初全套测试出现 4 项旧“标记总数”断言失败；改为分别核实 circle / annulus 数量和几何，保留旧 typed 连接及独立 BuildFeatureOverlay 行为。窗口 large-image 的同类总数断言也已更新并通过。独立审查指出并修复首次绘制形状、部分越界中心保存、拖动整框约束、被忽略的手动指引参数、原图视图定位缓存与局部输入画布的显示一致性。窗口测试调用实际 WPF 编辑／参数提交路径，未声称模拟外部真实鼠标或生产图精度验收。

编辑器携带 Runtime 与 artifacts/native/Debug 的 SHA-256 一致，汇总为 artifacts/tests/match-center-verification.json。只更新源码和独立本地 Debug 目录，未打包、未修改既有 0.7.2 客户包或替换用户正在运行的旧实例。当前位置修正为原图轴向平移；完整旋转／尺度 Fixture、Group 多目标迭代和工件双边内外径测量仍未实现。输出圆环宽度是参数，不是实测材料厚度。

## 当前源码：匹配后测量与全图搜索（2026-10-14）

按用户指定的 VisionMaster 4.4 本机 SDK 接口和模块 XML 审查业务链，观察／推断及范围记录于 visionmaster-business-review.md。修复前，真实 Match→Circle 因 Unknown outputs port:image 失败，纯 Core 在 matcher 已声明 Image 的模拟目录下仍无法投影；报告分别为 artifacts/tests/match-circle-before.trx、match-pipeline-graph-before.trx。全图匹配新增 15 项中 14 项因旧 16Mi 上限失败，1 项预算检查通过，报告 fast-full-image-before.trx。

进一步审查并复现了两个边界：平滑模板跨评分块接缝的重复峰把弱目标挤出候选池（应 3 个实际 2 个，fast-seam-before.trx）；补黑边的高亮有效像素被 FindLines 检出为原图 x=599、长度 95 的假线（match-boundary-check.trx）。修复候选池前重叠合并和补边图的 Canny 有效邻域保护，增加真实线保留与共享掩膜不被改写的检查。

最终 Debug 原生与 WPF 构建通过，0 警告／0 错误；CTest 2/2，SDK／模型／真实 Runtime 检查 274/274，报告 artifacts/tests/match-pipeline-debug-final.trx。覆盖选中目标、越界索引、同会话第二帧无匹配、父 ROI／valid、非零 origin、固定补边画布、公开 Result 像素、真实输入与裁图预览、读取后像素所有权、32Mi 上限、评分块接缝、非整除尺寸、实际 8 倍粗搜索与旧配方接口。

最终本地调试程序 artifacts/local-debug/match-pipeline/JP-Vision.exe 通过 12 个 WPF 窗口流程：match-circle、fast-full-image 及此前 10 个流程。新链执行真实模板绘制／学习、搜索 ROI → 全图下拉操作、Result → Image 连接、局部圆 ROI、卡尺、关闭全部中间结果后的源图／目标图预览、保存重载。match-circle 中原点 (63,71)、局部圆 (36,48) 得到原图圆心 (99,119)；已查看 match-pipeline-match-circle.png。大图窗口从实际 5120×5120 PNG 解码导入，搜索全图后定位右下目标并保存重载。

仅构建本机 Debug，不打包、不改已有 Release/NuGet，不替换用户正在运行的旧 Debug 实例。完整旋转／缩放 ROI Fixture、Group 多目标迭代、跨机器和产线验证不属于已通过的本次结果；当前链满足平移匹配后局部检测。取消仍在算子边界响应，粗候选搜索未承诺穷尽召回。

## 当前源码：单结果端口与工具箱精简（2026-10-14）

新增真实圆结果包测试先在旧 Runtime 上失败，错误为 Unknown outputs port: result；实现后通过。当前 Debug 原生构建与 WPF 构建成功，WPF 为 0 警告／0 错误；CTest 2/2、SDK／编辑模型 232/232，通过报告 artifacts/tests/single-result-debug.trx。新增 78 项分别覆盖结果包 native 14、编辑模型 14、SDK JSON 50；其中真实圆和快速匹配也验证了 typed SDK 几何与 overlay 原文一致性。

本地调试程序 artifacts/local-debug/single-result/JP-Vision.exe 通过 10 个 WPF 流程：single-result、direct-results、3 个 fast-teaching、caliper、large-image、3 个 caliper-boundary。新场景验证一个可见输出、内置 ROI 等组件隐藏、唯一字段投影、旧节点和连线仍显示、全包公开读取、实际数量判定、保存重载及立即显示圆心／半径；已查看 artifacts/local-debug/single-result-single-result.png。模型测试覆盖 Bool 的 found/passed 两项选择，没有声称用外部自动化点击菜单或穷尽鼠标手势。

VS 默认 Debug 目录也重新构建，所复制 Runtime 与 artifacts/native/Debug 的 SHA-256 一致。当前测试范围为本机 Debug；本次未重新生成 Release 客户包、ZIP、安装包或 NuGet，已有 0.7.2 发布产物未更新。

日期：2026-10-13。开发机：Windows 11 10.0.26200 / x64，MSVC 19.51 / VS 2026，OpenCV 5.0.0，Qt 6.12.0 MinGW，.NET SDK 10.0.401。记录仅描述实际执行结果。

## 构建与测试

- bootstrap.ps1 实际执行成功，QtNodes 提交与 JSON 摘要检查通过，生成本地 Qt Creator 预设；现有 OpenCV 分发包保留。
- build.ps1 -Configuration Release / Debug 实际执行成功。Runtime 为相应 MSVC 构建，OpenCV world500/world500d 分别匹配；Qt kit 只提供 RelWithDebInfo Qt DLL，Debug Studio 使用该 Qt DLL 与 Debug 应用/Runtime。
- 两配置 CTest 各 2/2 通过：OpenCV 探针与直接 C ABI 契约程序。覆盖已知像素、padding、缺失授权、任务接受后释放帧、Busy、预执行取消、旧任务迟到取消不影响下一任务、接受后超时、缺失判定与结构环路。
- 两配置 SDK xUnit 各 40/40 通过。原有 27 项覆盖真实 Pass/Fail、Gray/Bgr、输入覆盖、ROI/高斯/形态学、ROI 原图坐标、失败后成功、并发会话、运行中取消/Dispose、超时收尾、资源摘要/越界、错误许可与旧许可保护、并发导入、可选参数、自定义输出名称、中间图与公开图像重名、1000 次连续运行。
- 0.2.0 新增 13 项检查：Gray/Bgr 圆的原图坐标与半径、线段端点/长度/角度、区域面积/周长/质心/包围框/圆形度、空图像、跨参数范围、端口类型、单文件中值滤波的真实去噪与 ROI 保留、零面积轮廓回退、4MP Hough 限制、托管结果记录与错误类型拒绝。新增功能测试先在旧 Runtime 中失败，再通过实现验证。
- 0.3.0 Qt GraphTests 两配置各 11/11 框架项通过（9 项业务检查与初始化/清理）：文档/类型/环路/摘要检查、新组件连接与保存恢复、圆/线/矩形图元在 ROI 原点下的实际场景几何、ROI 移动/缩放/边界，以及 54.4% 分数缩放下移动保持像素尺寸。
- 两配置 Studio GUI 冒烟退出 0：真实 viewport drop，鼠标连接端口，撤销/重做，删除恢复，鼠标 ROI 框选与撤销，重复保存不增版本；从空白画布搭建 8 节点检测链，保存再打开，真实 OpenCV 检测 3 / Pass。
- Studio 导出真实执行快照的复现包，重新加载并真正执行，公开输出和判定与原任务一致。
- 两配置 WPF GUI 冒烟退出 0；加载 Studio 从空白画布生成的配方，传入同一张图像，配方摘要、数量和判定与 Studio 一致。原图和轮廓实际渲染，截图已查看。
- 两配置另跑圆、线、区域配方的 Qt/WPF GUI：分别为 3 个圆、6 条边缘线段、3 个区域，全部 Pass，配方摘要和全部几何字段一致（数值比较，容许 Qt JSON 将 442.0 显示为 442）。Qt 导出的几何配方复现包实际重跑，输出一致；WPF 显示托管几何数值和叠加图元。Release 圆/直线/区域截图已查看。
- 两配置未授权以及正确签名的异机/错误产品/未知 keyId/错误版本文件，Studio 激活页均阻止业务界面；4 个原生进程同时导入同一许可成功，持久化状态有效。

0.2.0 日志为 artifacts/test-release-0.2.0.log、test-debug-0.2.0.log、build-debug-0.2.0.log；Release 构建输出保存在任务工具记录。TRX 为 artifacts/tests/sdk-Release.trx、sdk-Debug.trx；图测试为 graph-Release.txt、graph-Debug.txt。GUI 截图与实际 JSON 结果在 artifacts/studio-Release.png、wpf-Release.png 及对应 Debug 文件；新配方截图为 studio-circles-Release.png、wpf-lines-Release.png、wpf-regions-Release.png 等。原图为 samples/images，Qt 生成的配方在 artifacts/editor-built-studio-Release.vrecipe。

## 0.3.0 界面、参数与 ROI

Release / Debug 已完成新界面构建和全部脚本检查，分别通过 2 项 CTest、40 项 SDK 测试和 11 项 Qt 框架检查；原有从空白画布搭建流程、几何配方、未授权阻止与复现包重跑全部通过。

实际 GUI 增加以下端到端操作：选中圆查找，通过右侧控件修改最小半径；自动插入 ROI，整体撤销并重做；鼠标框选、移动与边角缩放；通过 X 输入更新矩形、撤销重做；输入越界坐标，验证 X/宽度同时修正；保存并重新打开，执行得到区域内 2 个圆 / Pass。WPF 加载同一个保存配方，摘要、数量与全部原图几何字段一致。关闭调试结果后再次完成相同 ROI 操作，仍为 2 / Pass。

缩放移动回归先复现 330×160 的 ROI 变为 331×161，再修复为保持整数像素尺寸；尚未执行时的空结果读取也先用 GUI 检查复现，再修复为只读查询。证据为 artifacts/tests/roi-zoom-red.txt、artifacts/tests/roi-zoom-green.txt、artifacts/studio-result-empty-red.png.error.txt；构建和测试日志为 artifacts 下的 build-release-0.3.0.log、build-debug-0.3.0.log、build-debug-ui-state-0.3.0.log、test-release-0.3.0.log、test-debug-0.3.0.log。

新界面截图为 artifacts/studio-circles-Release.png、studio-roi-Release.png；WPF ROI 证据为 wpf-roi-Release.png，另有对应 Debug 文件与实际结果 JSON。截图显示的直方图由当前图像计算。无调试结果的 ROI 预览截图为 studio-roi-no-debug-Release.png。发布版继续使用用户原软件图标和正常机器绑定许可证，未增加免授权开关。

## 0.4.0 模块 ROI、卡尺与模板

Release / Debug 最终均通过 2/2 CTest、54/54 SDK 检查、12/12 Qt 框架检查，原有配方、从空白画布搭建流程、复现包和授权拦截保持通过。SDK 新增 14 项覆盖三种 ROI 的实际形状/原图坐标、平行分支隔离、越界 Failed/Unknown、卡尺的已知边缘/圆/错误极性/边缘点/RMS、模板的多目标、90°、搜索 ROI、非矩形模板掩膜/亮度偏移、空白负例、常量/未训练模型和搜索预算。

实际 Qt 操作覆盖模块 ROI 不改变图节点、矩形移动/八柄缩放、圆心移动/半径控制点、多边形逐点绘制/Enter 完成、数值修正、撤销/重做与保存重载。多边形圆查找配方由 WPF 重放为 2 / Pass。通过真实教学窗口框选并点击学习，撤销/重做、保存再打开后 Qt/WPF 都匹配 3 个目标；上游 ToGray 带圆形 ROI 的教学组合也在两配置中为 1 / Pass，全部原图四角和分数一致。卡尺样例两配置 Qt/WPF 均为 1 / Pass，包含相同边缘点与拟合误差。

圆形上游掩膜的教学组合曾复现为零匹配，修正为整数像素中心后通过。模板学习掩膜与上游有效范围相交，避免使用裁剪区域外的像素；模板像素/掩膜随配方保存。实际截图见 artifacts/studio-circle-caliper-Release.png、studio-line-caliper-Release.png、studio-taught-template-Release.png、studio-roi-Release.png、studio-taught-masked-template-Release.png 及对应 WPF/Debug 版本。日志为 build/test-release-0.4.0.log、build/test-debug-0.4.0.log 与 build-*-ui-0.4.0.log。

支持边界：一次一个模块 ROI；直线/圆卡尺的像素边缘拟合；带掩膜的灰度归一化相关及离散角度/尺度搜索。没有标定毫米测量、形状匹配、遮挡补偿或模板位姿驱动的 ROI 跟随。未执行教学时预览为原图/裁剪范围；上游滤波/分割执行后可用真实中间图重新教学。真实产线精度仍需相机和现场样本验收。

## 授权与秘密边界

开发者 owner.private 在当前用户的 .jp-vision/issuer 目录，采用 DPAPI CurrentUser 加密与账户专属 ACL，工作区扫描 private 文件数为 0。C# ECDSA P1363 签发 → 原生 CNG 验签通过。通过正常导入激活 ProgramData/JP-Vision，本机客户标识 JP；没有免授权开关或默认万能密码。

签名负例测试修改机器摘要、产品、keyId、版本；篡改原 payload、重复字段、损坏、超长、缺少运行功能均被拒绝。签名正确但缺少 runtime 功能的导入在写入前拒绝，保留旧许可；直接复制该签名文件也无法创建引擎。异机验证属于签名摘要模拟证据，不是第二台物理电脑实测。

## 性能与资源

对已加载的 120×50 Bgr24 已知三目标图预热 30 次，再顺序检测 1000 次（Debug=false），记录端到端耗时和每 100 次资源快照：

| 构建 | p50 | p95 | 最终相对私有内存变化 |
|---|---:|---:|---:|
| Release 0.4.0 | 0.3109 ms | 0.7858 ms | -4308992 bytes |
| Debug 0.4.0 | 2.4491 ms | 5.5148 ms | -106496 bytes |

在用原生 handle 维持 3（引擎、会话、输入帧），临时输出字符串 0，活动 Run 0。没有逐次累积趋势；这不替代所有内部分配或长周期生产验证。完整记录为 artifacts/tests/performance-Release.json、performance-Debug.json。结果是小型合成图的本机数据，不用于承诺未知相机分辨率或产线节拍。

## 打包和独立消费

pack.ps1 真实创建 0.2.0 的 JPVision.Sdk / JPVision.Wpf NuGet、Release 客户端 ZIP 和 SHA-256 文件清单。PackageConsumer 只 PackageReference SDK，不引用源码工程，从本地 NuGet 源恢复。每次打包使用新隔离缓存和发布目录；执行时 PATH 限制为 Windows 系统与 dotnet，计数、圆、直线、区域四个配方分别输出 3/3/6/3 与 Pass，并验证 SDK 托管几何记录。Runtime 版本为 JP-Vision/0.2.0 ABI/1 OpenCV/5.0.0。

客户端与 NuGet 清单检查排除 LicenseIssuer、测试程序、private/key/pem/pfx、机器请求和许可证；WPF 客户目录无 Qt DLL。最新发布目录记录在 artifacts/release/latest.txt，独立消费者在 artifacts/consumer/latest.txt。0.2.0 最终发布目录的圆/直线/区域配方共 6 次 Studio / WPF 冒烟均已退出 0，运行时移除开发 Qt/OpenCV PATH；88 个清单文件摘要验证通过，ZIP 93 项无签发秘密，发布 Studio/WPF 和消费端 Runtime SHA-256 均与本次构建一致。证据见 artifacts/tests/deployment-verification-0.2.0.json 与 pack-release-0.2.0.log。

0.3.0 已生成 artifacts/release/JP-Vision-0.3.0.zip、JPVision.Sdk.0.3.0.nupkg 和 JPVision.Wpf.0.3.0.nupkg。独立消费者在 artifacts/consumer/20261013-113850，四个流程仍为 3/3/6/3 与 Pass，Runtime 版本为 JP-Vision/0.3.0 ABI/1 OpenCV/5.0.0。发布目录另完成 9 次 GUI 检查：圆、线、区域的 Studio/WPF 配对 6 次，参数/可编辑 ROI 的 Studio/WPF 配对 2 次，以及未授权拦截 1 次；全部退出 0，均移除开发 Qt/OpenCV PATH。89 个清单文件摘要通过，ZIP 94 项未包含签发秘密或许可证，WPF 无 Qt DLL，三处 Runtime 摘要一致。证据见 artifacts/tests/deployment-verification-0.3.0.json、pack-release-0.3.0.log 和 artifacts/deployed-*-0.3.0.png。

## 支持矩阵与未验证项

0.4.0 发布包为 artifacts/release/JP-Vision-0.4.0.zip，NuGet 为 JPVision.Sdk / JPVision.Wpf 0.4.0。独立消费者 artifacts/consumer/20261013-133615 从新隔离包缓存恢复，七个配方依次得到 3/3/6/3/1/1/3 与 Pass。发布程序另做 11 次 GUI 检查（新卡尺/模板 6 次，模块 ROI 2 次，上游圆形 ROI 教学模板 2 次，未授权 1 次），全部退出 0，移除开发 Qt/OpenCV PATH，Qt/WPF 嵌套几何一致。104 个清单文件摘要通过，ZIP 109 项无签发秘密或许可证，WPF 无 Qt DLL，发布与独立消费的 Runtime 摘要一致。记录在 artifacts/tests/deployment-verification-0.4.0.json 和 pack-release-0.4.0.log。

| 项目 | 当前证据 |
|---|---|
| Windows 11 x64 / 当前开发机 | Debug、Release、GUI 与 NuGet 消费通过 |
| SDK net8.0 / WPF net8.0-windows | 编译通过；实际宿主为 .NET 10 |
| .NET 10 x64 WPF / 控制台 | 真实运行通过 |
| .NET 8 Runtime | 未实机运行，不能将目标框架编译当作运行证据 |
| .NET 6 / .NET Framework / x86 / Linux | 本版本不支持 |
| 第二台干净 PC / 不同 Windows 账户 | 未实测；隔离输出目录不能替代干净机器 |
| UUID 占位、备用主板 profile、真实硬件更换/重装 | 策略有实现，真实硬件场景未穷尽验证 |
| 私钥加密备份/恢复的互动口令往返 | 命令已实现，需开发者按备份策略本地执行 |
| 真实相机、PLC、MES、长期产线运行 | 未连接；由既有 C# 工程接入并现场验收 |

离线授权不能即时远程吊销，不能保证识别克隆 VM 或防止管理员修改二进制。取消/超时在 OpenCV 调用前后协作检查；首版无 IPC 崩溃隔离。发行前按第三方组件的实际分发许可保留相应文本与 Qt 源码信息。

## 固定来源

QtNodes 3.0.16：7c6341a66a8e46b8988140b9e60d892b6a3560b3。nlohmann/json 3.12.0：aaf127c04cb31c406e5b04a63f1ae89369fccde6d8fa7cdda1ed4f32dfc5de63。Qt 6.12.0 来自本机 kit；OpenCV 5.0.0 来自用户已有分发包，没有重建其完整供应链。源代码审查记录见 review.md。

## 0.4.1 外部图像直接调试与 processing 配方

- 原因：新建 inspection 草稿公开输出为空，原生接口校验拒绝；完整检测还要求 Bool 判定，阻止单纯的图像处理流程。
- 回归先失败：artifacts/processing-red.log 中新 processing 正例 3 项失败；WPF --processing-smoke 首次失败为 Unknown scalar input: ExpectedCount。
- 修复：草稿运行自动生成末端输出的 processing 快照；保存的公开接口不被修改。完整检测继续要求 Bool；processing 必须无 decisionOutput，成功 Decision=Unknown。公开用途可在接口对话框选择。
- Release / Debug 标准构建均成功；各自 CTest 2/2、SDK 60/60、QtTest 16/16，标准脚本的拖拽/连线/撤销、ROI、模板、既有授权负例与 WPF 重放均通过。日志为 artifacts/build-{release,debug}-0.4.1.log 与 test-{release,debug}-0.4.1.log。
- 两种构建都以实际 5120×5120 彩色 PNG 验证空公开输出草稿校验、保存/重载、执行、Gray 结果显示；关闭 debug 保留时末端图像仍可显示。
- 两种构建都运行公开 GrayImage 的 grayscale.vrecipe，Qt / WPF 均 Succeeded / Unknown、5120×5120 Gray8，内容摘要相同；WPF 显示“处理完成”并禁用未声明的 ExpectedCount。
- 像素回归包含带 padding 的 BGR 蓝/绿/红/白输入、Gray8 实际值；处理配方中的 true Bool 输出不能产生 Inspection Pass；不合法判定和断开的算法输入仍被拒绝。
- docs/camera-acquisition-design.md 已记录用户选择的海康/大恒 GigE、连续预览、触发时检测。本机 MVS 5.0.2 / SDK 4.8.1.2 已找到；相机驱动适配器和实际取流尚未实现。未连接相机、PLC 或生产设备。

0.4.1 最终发布验证：独立 NuGet 消费端 artifacts/consumer/20261013-144831 在无开发 Qt/OpenCV PATH 下执行 8 个配方，7 个检测配方均 Pass，灰度配方 Succeeded / Unknown / Gray8。客户端发布目录另外通过 Qt processing、WPF processing、空接口草稿关闭 debug、未授权锁定 4 个 GUI 检查。源 Runtime、发行 Studio、发行 WPF、NuGet 消费端 DLL 摘要一致：55273CBAE075A4C8DB66CB42D1B0D58F4F1FF96D6CD44D19BD140B9F47B7E512。未执行真实相机采集。

## 0.5.0 红黑白工业 UI 与红焰 Logo

用户参考：Downloads/JP-Vision机器视觉流程平台.png；Logo：Downloads/红焰三角之眼.png。复制 Logo 到 assets/jpVision.png，源与副本 SHA256 均为 DC8EF9E44FA1EB2F9653480F345B00C9925DB69C2E37CAD06A3CDD8B88735748，原文件未修改；Windows ICO 含 16/20/24/32/40/48/64/128/256 九个尺寸。

Qt/WPF 采用统一红黑白主题；流程节点端口几何与拖拽、连线规则不变；参数面板贯穿流程和图像两层。ROI 编辑线与控制柄为红色。图像和算法返回标记保留原始数据颜色，不将图像强制改为主题颜色。

Release / Debug 构建成功，各 CTest 2/2、SDK 60/60、QtTest 16/16；标准 test.ps1 中真实拖拽与连线、撤销/重做、矩形/圆形/多边形 ROI、模板教学、关闭中间结果保留、5120×5120 processing、授权负例、WPF 重放均通过。证据为 artifacts/build-{release,debug}-0.5.0.log、test-{release,debug}-0.5.0.log。

已查看真实窗口截图 industrial-studio-preview-0.5.0.png、industrial-wpf-preview-0.5.0.png；另以 --sample-smoke --compact-smoke 验证 1280×800 并检查 industrial-compact-0.5.0.png 的流程、图像、滚动参数和操作按钮。界面显示来自真实 OpenCV 执行结果。没有新增相机驱动、联网授权或登录，C ABI 保持 1。

0.5.0 最终发布验证：独立 NuGet 消费端 artifacts/consumer/20261013-152846，在移除开发 Qt/OpenCV PATH 后完成 8 配方调用，7 个检测配方 Pass，灰度 processing 配方 Unknown / Gray8。发行目录额外通过 Qt 圆查找、WPF 圆查找、模块 ROI 绘制/保存/重载、未授权锁定 4 个 GUI 检查。用 Windows Icon.ExtractAssociatedIcon 从 Qt 与 WPF 的 EXE 提取图标，实际为红焰 Logo；Qt EXE 图标图像已查看。源 Runtime、发行 Studio、发行 WPF、独立 NuGet 消费端 DLL 摘要一致：54B88D2368B630DB4765FECD9B06C6224B39CBF5CA0549E8C438C8BF52B30ADE。相机取流仍未实现。

## 0.6.0 检测算子直接测量与模块内卡尺

Release / Debug 构建均成功，各 CTest 2/2、SDK 76/76、QtTest 16/16；标准 test.ps1 的全部 GUI 检查通过。证据：artifacts/build-{release,debug}-0.6.0.log、test-{release,debug}-0.6.0.log。新增 MeasurementTests 16 项，使用真实 OpenCV 与真实许可证：两圆测量、六个端口分流、无 Debug 读取、原图原点与 ROI 偏移、空图／索引越界、数量边界、标记配置、卡尺尺寸与剔除预算、错误极性、下游失败清空结果，以及剔除坏边缘点确实改善拟合。旧测试继续覆盖直线、区域、模板和旧配方；额外断言其类型化节点测量。

测量 GUI 检查使用真实鼠标从 count 端口连接数量判定，撤销还原两个节点；切换自动找圆／卡尺拟合，修改卡尺数量 52、宽度 9、高度 48、剔除点数 6，保存重载同一配方。运行结果实际请求 52、采到 52、保留 46、剔除 6、缺失 0，RMS 约 0.072 px。卡尺高度／宽度修改重绘 104 个指引图元。Qt 关闭 Debug 后仍显示测量与公开标记；WPF 重放相同已编辑配方，contentHash、圆特征、卡尺统计、判定一致。GUI 还验证清除结果后旧摘要和详情入口失效，再次执行恢复新摘要。RMS 仅为合成图拟合指标，不是实物精度认证。

已查看真实 Release 截图 studio-measurement-Release.png、studio-integrated-caliper-Release.png、wpf-integrated-caliper-Release.png；标记文字使用紧凑半径／直径格式避免三目标相互覆盖，完整坐标保留在参数／结果和 SDK 中。红黑白 UI、Logo 和离线机器绑定授权延续原功能。C ABI 保持 1；相机实时取流仍为待实现设计。

最终 Release 源 Runtime SHA-256：7B2207B832B87FA3098CB31F343B521EB602B97CAA7BC86227BDAC7621733DE8。仅修改原生核心时，build.ps1 也会显式复制该配置的 Runtime / OpenCV DLL 到 Studio，避免打包过期运行库。

0.6.0 最终发行验证：独立 NuGet 消费端 artifacts/consumer/20261013-164830 在移除开发 Qt/OpenCV PATH 后通过 10 个配方（9 个检测 Pass，灰度 processing 为 Unknown/Gray8），其中两节点直接圆测量与圆查找内卡尺读取类型化 Measurement 成功。最终发行目录额外完成模块内卡尺改参／保存重载、WPF 同配方重放、无许可锁定 3 个 GUI 检查，均无开发 PATH。Qt 与 WPF 卡尺统计／contentHash 一致；源 Runtime、发行 Studio、发行 WPF、独立 NuGet 消费端 DLL 的 SHA-256 全部等于上段记录。发行包未携带许可证、机器请求或私钥。证据：artifacts/pack-release-0.6.0.log、deployed-caliper-0.6.0.png、deployed-caliper-wpf-0.6.0.png 及 artifacts/tests/deployed-*.err。

## 0.7.0 WPF 编辑器与 VS 发布（2026-10-14）

Visual Studio 2026 / v145 的 JPVision.sln 在 Release / x64 与 Debug / x64 下实际构建成功，0 警告／0 错误，日志为 artifacts/tests/vs-solution-{Release,Debug}.log。新默认构建使用 C# WPF 编辑器与 MSVC 核心，源码在 managed/JPVision.Studio.Wpf 和 managed/JPVision.Editor.Core；没有 Qt 构建或部署要求。运行库版本为 JP-Vision/0.7.0 ABI/1 OpenCV/5.0.0。

Release、Debug 各通过 CTest 2/2、SDK 与编辑模型 97/97。SDK 覆盖新增快速匹配／单目标定位、奇数像素平移与粗搜索 1/2/4、BGR／Gray、形状掩膜、ROI 与原图原点、空结果／未学习拒绝；新增流程测试覆盖旧公开输出失效、新分支、类型／占用／循环、教学子图、模板与 ROI 独立、保存重载及与 SDK 内容摘要一致。测试过程中发现并修复矩形 ROI 的 Int32→Double 读取失败，保留了失败和后续通过的证据。

实际 WPF 窗口工作流检查覆盖：三节点灰度／二值化直接结果，矩形／圆形／多边形模板学习后匹配两个目标，模型撤销、独立检测 ROI 与保存重载，圆查找内卡尺及剔除点（48 次采样、保留 46、剔除 2，圆心约 180/180、半径约 63.984）。自带流程检查调用与图像交互共用的区域提交路径；没有声称对所有真实鼠标手势做外部自动化。已查看最终窗口截图，确认红黑白主题、几何图形、结果数值与用户 Logo。

授权检查涵盖空存储、错误机器、错误产品、未知签发公钥、错误格式版本和缺少 studio 功能；这些状态均锁定 WPF 编辑器。沿用 studio/runtime 功能，不改变已有许可证协议；只检查 runtime 的工业宿主仍可使用仅授予 runtime 的许可证。没有免许可开关，也没有把许可证或签发器放进客户端。

发布版 Studio 是 win-x64 自带 .NET 10.0.12 的 WPF 程序，并附同版本 release VC145 CRT。移除开发 PATH、把 DOTNET_ROOT / DOTNET_ROOT_X64 指向不存在的目录后，发布窗口完成上述 5 个流程及未授权锁定检查。证据为 artifacts/deployed-wpf-*-0.7.0.png、JSON 与 artifacts/tests/deployed-wpf-*.err。独立 NuGet 消费端 artifacts/consumer/20261014-092101 使用独立包缓存，在无开发 Qt/OpenCV PATH 下通过 12 个示例（11 个检测 Pass、grayscale 为 Unknown/Gray8），包含 fast-match 和 fast-locate。

源 Runtime、发布 Studio、发布 WpfSample 和独立 NuGet 消费端的 SHA-256 均为 F0A15FAE337EBDE9C59139CCE90AB5E8538920E40D493511D04CC6C294E6BFE7。zip 和 manifest 的最终校验单独保存于 artifacts/tests/release-verification-0.7.0.json。

边界：当前 Windows 开发机实测，不等同于干净第二台 PC 或所有 DPI／显卡硬件的认证；实际海康／大恒 GigE 取流、触发设备和产线未接入。内嵌模板随配方可直接通过 SDK 复用；当前新编辑器不迁移外部资源配方的编辑，SDK 仍保留其校验与执行能力。快速模块仅平移搜索，不声明固定帧率或旋转缩放不变性。

## 0.7.2 ROI 边界卡尺（2026-10-14）

新增 native 回归在 0.7.1 上先得到 20 失败／1 旧 guide 模式通过，报告 artifacts/tests/caliper-boundary-before.trx。实现后，圆周／四条矩形边／CW与CCW多边形边／斜边、分数圆心、父 ROI 与 origin、上游 valid、图像边缘、不合法形状／边序号／越界及实际高度组合共 22 项 native 使用检查通过；17 项预览布局与 editorDefault 检查、5 项中文报错补充通过。

Release、Debug 均成功构建，0 警告／0 错误；两个配置各通过 CTest 2/2、SDK／模型 154/154、9 个编辑器窗口流程、6 个授权拒绝状态与 4 个工业集成范例。日志 artifacts/build-{release,debug}-0.7.2.log 和 artifacts/test-{release,debug}-0.7.2.log。

3 个新增窗口流程实际选择奇数宽度、绘制并修改 ROI、执行和保存重载。圆跟随验证圆心／半径变化、矩形逐一切换四条边、多边形移动顶点；运行后的窗口中心、原点、方向、宽度与实际高度逐项与 CaliperLayout 预览比较，误差不超过 1e-8。已查看 artifacts/wpf-editor-caliper-boundary-{circle,rectangle,polygon}-Release.png，确认卡尺位于圆周或指定边，且界面可选择模式和合法宽度，多边形有边编号。实时预览检查直接设置当前 ROI 数据并提交，未声称模拟所有鼠标手势。

上游有效区域依旧限制采样，点不足为成功空测量，错误为 Failed／Unknown；均不以 ROI 参数冒充结果。旧 guide 配方和此前大图、模板与直接结果回归继续通过；没有修改许可证协议或 C ABI。

## 0.7.1 大图霍夫检测（2026-10-14）

复现原错误：导入 5120×5120 图像后，FindCircles／FindLines 因 4Mi 像素霍夫上限返回 Failed／Unknown。新增大图测试在旧实现中 8 项全部失败，证据为 artifacts/tests/large-image-before.trx；图像导入并没有失败。修复默认 adaptive 工作图缩小、原图几何逆变换、ROI 掩膜／原点保留及中文错误显示后，新增 9 项大图与 4 项错误显示检查通过，最终定向报告 artifacts/tests/large-image-final-0.7.1.trx 为 13/13。

MSVC／WPF Release、Debug 构建均完成，清除新增可空引用警告后为 0 警告、0 错误；日志 artifacts/build-{release,debug}-0.7.1.log。两个配置各通过 CTest 2/2、SDK／编辑模型 110/110、6 个实际 WPF 窗口流程、6 个许可拒绝状态和 4 个工业集成范例，日志 artifacts/test-{release,debug}-0.7.1.log。

新窗口流程先将已知圆图保存为真实 5120×5120 PNG，再调用编辑器 LoadImage 解码导入：original 模式被拒绝并显示算子名称、输入尺寸及中文 ROI 建议；adaptive 找到两个圆，显示圆心／半径及叠加标记，预览仍是 5120×5120，工作图 2048×2048。保存重载后执行仍成功。Release 已知圆 (2157,1639,R80) 的输出约为 (2157,1639.5,R80.5)，测试要求原图像素误差不超过 5；截图及测量记录 artifacts/wpf-editor-large-image-{Release,Debug}.png 和对应 .json 已检查。

覆盖 Gray8／Bgr24、非紧密 stride、外部 origin、长线段长度／端点／角度、大多边形 ROI 掩膜与裁剪偏移、original 超限失败且无输出、小 ROI 原图成功、空图成功无虚假目标。检测工作图始终不超过 4,194,304 像素，卡尺继续使用独立采样诊断。小于上限的既有配方、卡尺、模板、快速定位与授权回归保持通过。

边界：adaptive 是有损检测分辨率转换，未在原图回拟合，不能据此声明亚像素精度或小目标必检。高精度场景使用较小 ROI 的原图模式或卡尺。当前通过的是已知图像和当前 Windows 开发机验证，未拿到此次报错对应的原始实物图做检测效果验收。
