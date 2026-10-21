# M0～M2 开发记录

2026-10-21 曲线修复增量：边缘点集新增去趋势跳点剔除、局部稳健平滑和受跨度／掩膜约束的内部缺段 PCHIP 补全，保留原始坐标并区分实测／补全。测距默认使用实测合格原始点，显式切换后使用修正／补全点集。修复交替锯齿的中值相位翻转，并压缩原始模式冗余数据以保留最大测站能力；不提高预算、不截断。新增25项、最大图幅相关27/27、完整托管851/851、CTest3/3、WPF10/10通过；正常Debug与测试库哈希已同步，见verification.md。操作edge-reconstruction.md；仍用VS F5源码，不制作独立包。

2026-10-21 性能增量：WPF 复用未改动的编译配方／会话，参数变化重建，每次读取本帧输入；准备及执行转到后台，移除重复整图显示。主区新增“实时画面”，与相机管理窗共享全分辨率位图，100 ms 拉最新帧并保留缩放；执行／教学固定图像，手动返回实时，停流／故障清图。缓存 9/9、完整托管 826/826、CTest 3/3、WPF 12/12 通过。5K 合成图 Debug 基准位图准备 46.23→10.90 ms，轻量灰度流程 SDK 端到端 10.28→3.08 ms；详细范围见 performance.md。未加入多进程、未连接硬件复测，普通 Debug 已同步，继续 VS F5，不制作独立包。

2026-10-20 相机增量：WPF 图像源新增“全局相机”和独立 MVS GigE 管理窗，单次流程所有 ExternalImage 使用同一张触发请求后收到的帧；实时预览与固定检测／教学图分开。可按 SN 连接／启停，停流读取与写入真实范围曝光／增益／帧率，配置只保存本机 SourceMode 与 Serial。net8 Acquisition 与独立可选桥接接好，缺依赖不影响文件图。模拟 27 项、完整托管 817/817、CTest 3/3、WPF 11/11 通过；实机 ME2P-2622-4GM-P 完成两轮各 8 张 5120×5120 Gray8 取图并断开，未写曝光参数或系统网络。MVS 应先断开设备控制连接。正常 VS Debug 已同步；操作 camera-source.md，证据 verification.md。长稳、拔线、彩色／高位深实测、大恒、硬件触发、多相机仍待做，不制作独立发布包。

2026-10-20 Blob 增量：检测工具箱新增 BlobAnalysis，内置分割／4与8邻域／ROI／区域与形状筛选／排序和数量判定，输出实际像素面积、含孔洞像素边界周长、质心、包围框等，支持订阅及单一干净图像端口传递。SDK GetBlobAnalysis 与完整区域表接好，原始候选 100000、合格区域 10000 的资源限制不截断。完整托管 790/790（Blob 45项）、CTest 3/3、相关 WPF 8/8 通过，正常 VS Debug 输出同步。操作见 blob-analysis.md，证据 verification.md；仍以源码 F5 运行，不制作独立包。

2026-10-20 直线卡尺增量：数量上限 32768、剔除上限 32766、A/B 直线同步，圆侧保持 256。新增真实站间距、全部计数、初始／最终残差与失败建议；修复全宽 ROI 预览为空，窗口预览最多 256 但不减少运行采样。高密度查询去除平方级耗时，拟合 128 轮资源保护及所有路径 16 Mi 预算生效。完整托管 745/745、CTest 3/3、相关 WPF 17/17 通过；指南 line-caliper-density.md，证据 verification.md。继续 VS 源码 F5，不打包独立程序。

2026-10-20 边缘点集数值范围增量：间距扩至 32768 px、切向投影宽至 255 px 奇数、搜索高度至 32768 px、测站上限至 32769，下游点集到线距离同步。保留默认参数、物理梯度／比例范围及采样预算；Inspector 明确范围作用并实时估算真实站数、所需有效点数与超限建议。完整托管 711/711、CTest 3/3 及受影响 WPF 8/8 通过，正常 VS Debug 输出已同步；说明见 edge-points.md，最终报告见 verification.md。

2026-10-20 ROI 边界修复：普通算子的五类 ROI 取有效图像交集，圆／直线／边缘点及双边卡尺保留原几何逐点采样；贴边和部分超界不再直接终止执行，完全无有效区域返回不可用。新增 10918×287 全宽与轻斜条带、原点／展开平面／掩膜／订阅及内置测量回归。托管 684/684、CTest 3/3 通过，正常 VS Debug/x64 构建已同步。范围说明见 roi-bounds.md，验证见 verification.md；继续使用 VS 源码 F5，不制作独立发布包。

2026-10-19 不规则边缘增量：新增 FindEdgePoints 与 MeasurePointLineDistances。搜索带内复用卡尺投影获取有序真实点，保留曲线起伏与缺段；点集可随干净图像与拟合直线串联或双路输入，输出完整逐点垂距、有符号距离、垂足、六项统计及公差。WPF 支持绘制／调整、坐标订阅、采样预览和虚拟化结果表，SDK 提供类型化读取；搜索带可跟随位置修正，部分／完全越界按实际采样处理。操作见 edge-points.md，设计与边界见 edge-point-distance-design.md，验证记录见 verification.md。继续使用 VS 源码 F5，不制作独立发布包。

2026-10-19 参数数据流增量：ROI 及 A/B 坐标支持递归订阅，配方增加全局变量、算子局部变量、纯计算函数；WPF 提供结构化管理与来源／表达式选择，SDK 增加每次全局覆盖和实际参数读取。引用参与调度、循环校验、保存、哈希、复制及预览，未找到目标时跳过而非取旧值；原图与展开平面不能混用，图像原点转换只执行一次。操作见 parameter-subscriptions.md，最终验证见 verification.md；继续使用 VS F5 源码开发，不制作独立发布包。

2026-10-19 圆环展开增量：新增图像变换工具 AnnularUnwrap 及圆环专用参数/绘制面板，输出矩形图像支持后续处理；独立展开坐标平面及源图映射贯穿 ROI/图像处理/检测/测量。旧原图流程不改坐标语义，跨平面几何不能混合测量。完整托管 572/572、CTest 2/2，正常 Debug 构建完成。WPF 及证据见 verification.md 与 artifacts/tests/annular-verification.json；操作见 docs/annular-unwrap.md。仍以 VS F5 源码开发，不发布新包。

2026-10-19 后续增量：统一图像链已实现。全工具提供一个可见 Image 输出，旧 typed 输出保留隐藏；普通图像可内部阈值/提轮廓/面积筛选/区域分析，掩膜是真实处理像素。四种几何测量内置独立 A/B ROI 和完整卡尺参数，实际检测后测量；旧双输入配方兼容且拒绝混用来源。图像携带本帧只读结果及独立叠加，绿色标记不烧录到像素；匹配 Pose 可随单 Image 接入位置修正并教学。共享源图分割和轮廓掩膜的缓冲区复用问题已通过真实像素测试复现并修复，孔洞保留；同几何标记采用下游样式、不重复绘制。最终验证记录见 verification.md 和 artifacts/tests/image-pipeline-verification.json。操作与 SDK 见 image-pipeline.md，仍以 VS 源码 F5 开发。

2026-10-19 已完成算子精细参数：灰度通道、独立高斯核／Sigma／边界、自动及局部阈值、形态学核形状／独立高／边界与三种新操作、圆线／双边卡尺投影统计与亚像素开关、自动圆前平滑和自动线梯度选项。统一参数分组、范围／步长／单位说明、微调与单项恢复，保留旧配方行为、单结果端口、绿色标记和 VS 源码调试。补测并修复自动阈值的非矩形 ROI 统计，约束只作用于对应组件；原 35 个窗口流程加 4 个新参数流程通过。最终托管 501/501、CTest 2/2、WPF 39/39；记录 artifacts/tests/refined-parameters-verification.json，操作 docs/parameter-refinement.md。未进行独立发布或真实硬件精度验证。

当前工作方式（2026-10-16 用户要求）：通过 VS 打开 JPVision.sln、Debug / x64、F5 运行源码；后续更新交付源码、说明和普通构建验证，独立版本 EXE／publish／安装包／NuGet 需明确发布要求。build.ps1 默认不 publish，test.ps1 使用普通 bin 输出；普通 VS 调试产物仍正常生成。结果图形默认绿色 #00FF00，保留显式配方颜色。

圆圆／线圆距离已完成：MeasureCircleDistance 在原组件上扩展并显示“圆圆距离”，默认圆心距，兼容旧配方与 SDK；新增 MeasureLineCircleDistance，默认圆心到无限直线的垂距。两者可切换圆周最短间距，同时输出两种数值、原图端点、几何关系、三态公差和绿色图形。相交零距、包含、同心、旋转直线、缺目标、字段绑定与 SDK 边界均已验证。VS 解决方案实际补上 SDK→NativeRuntime 依赖，并用 VS MSBuild 成功构建全部项目。完整托管 450/450、CTest 2/2、WPF 窗口流程 35/35；证据 artifacts/tests/circular-distance-verification.json。示例见 samples/recipes/circle-distance.vrecipe、line-circle-distance.vrecipe 与 samples/images/circular-distance.png。本轮未生成独立版本目录、发布包或 NuGet，未复跑历史 net8 宿主。

此前线线距离：历史 Debug 为 artifacts/local-debug/line-distance/JP-Vision.exe，其功能已包含在当前源码。新增 MeasureLineDistance，两个独立直线查找的结果接 A／B；原图像素垂距、测距起终点、夹角、三态公差、图形与 JSON 导出已贯通 WPF／SDK。用户确认按平行边间距，夹角超过容差输出无效；默认 1°，容差内轻微倾斜固定测量 A 中点到 B 无限直线的垂距。该阶段完整托管 418/418、CTest 2/2、WPF 窗口流程 29/29；示例、界面和业务边界见 studio-guide.md、verification.md，文件核对见 artifacts/tests/line-distance-verification.json。未打包、未改授权，未复跑历史 net8 宿主。

此前直线卡尺集成：历史 Debug 为 artifacts/local-debug/line-caliper/JP-Vision.exe，其功能已包含在当前 line-distance 版中。FindLines 新建节点默认卡尺式长条 ROI，支持两端点／高度／中心调整、反向搜索、实际采样点剔除及拟合直线；直接输出端点、中点、长度、角度、方程、RMS 和诊断。原生缺省仍为 automatic，旧配方保持兼容。Fixture 保留中心线与条带尺寸，轴对齐及旋转参考条带均已验证；直线拟合同时修复了集中异常点误删问题。398/398 托管、CTest 2/2、26/26 WPF 窗口流程通过；最后的颜色／标签显示复核另跑 2 个新窗口场景。证据及操作见 verification.md、studio-guide.md。

此前圆拟合修复：历史 Debug 为 artifacts/local-debug/circle-refit/JP-Vision.exe，也包含在当前 line-caliper 版中。已复现 60 个正常圆周点＋4 个集中异常点，却因旧圆的偏差筛选误删到只剩 8 点的问题。加入稳健参考圆、指定剔点后的重拟合、按保留点数有界迭代；无目标时也显示点数和明确拟合失败原因，SDK 提供可空的拟合诊断。该阶段 9 项相关红绿、384/384 托管、CTest 2/2、23/23 WPF 窗口流程通过。

此前内孔教学：历史 Debug 为 artifacts/local-debug/circle-target/JP-Vision.exe，也包含在当前 circle-refit 版中。用户使用全图自动模式及三个小圆示例参数测大内孔，已增加前置半径／直径范围和“绘制内孔圆（亮 → 暗）”快捷教学，指定圆周后沿边采样、剔除并拟合单圆。5120×5120 大孔＋24 小孔窗口流程验证只输出目标大孔；该阶段 375/375 托管、21/21 WPF 流程通过。操作和合成数据边界见 studio-guide.md、verification.md。

此前圆环显示修复：卡尺剔除后的拟合几何已经正确，旧“几何结果标记”漏画结果环，已统一传递；该修复也包含在上述新 Debug 中。历史快照 artifacts/local-debug/circle-annulus/JP-Vision.exe，375/375 托管、CTest 2/2、3 个受影响 WPF 窗口流程通过。下文保留 M0～M2 交付快照。

目标与验收约定：next-iteration-prompt.md。用户约束：只更新源码和本地 Debug；保留离线机器许可证；不打包、不覆盖正在使用的程序。2026-10-14 的工作停止时间为 16:55，用户选择次日手动“继续”；2026-10-15 已明确恢复。

## 当前交付（2026-10-15）

本轮已落地：原生多输入／标量字段依赖、Pose／PositionFixture 教学与相似变换、旋转矩形及圆环／扇环 ROI、圆与直线卡尺跟随、两圆中心距、双边卡尺、点点／点线／夹角／交点、三态公差、输入来源选择、运行到选中、复制重映射及撤销重做。使用说明与业务边界见 m0-m2-implementation.md。

最终 Debug：artifacts/local-debug/m0-m2/JP-Vision.exe。Native、Studio、真实 net8 WPF 宿主构建通过；Studio／宿主 0 警告 0 错误，三个目录配套 Runtime 摘要相同。

- 托管完整回归 373/373：artifacts/tests/m0-m2-final-debug.trx。
- 原生 CTest 2/2；实际 WPF 窗口流程 20/20：artifacts/tests/m0-m2-wpf-final.json。含旧图像链、模板、5120×5120 大图、卡尺、Fixture、高级 ROI、字段绑定与无许可锁定。
- .NET 8.0.27 宿主不启动 Studio，读取同一配方与 PNG，中心距 69.6169878364 px，Decision=Pass，读取强类型 Fixture 并绘制叠加；有／无目标各 30 次重复，后者 Unknown，无旧值：artifacts/local-debug/net8-host/fixture-overlay.png.json。
- 原有 1000 次资源／句柄检查通过，性能记录 artifacts/tests/performance.json；未作产线精度／固定 FPS 声明。

复核修复了双边矩形 Fixture 变形与候选范围缩放、旧公开输入默认值改绑、参数不可用时的 SDK 状态、跨角度边界，以及全越界／有效采样不足误判期望零目标。成功的空匹配本身可按显式数量规则 Pass，依赖其图像／位姿的测量则跳过；轮廓／计数／旧 Bool 链同样传播 Unknown。红绿用例保留在 artifacts/tests。

采取分阶段源码复核、失败用例及独立宿主验证，未声称经过第二名审查者或实物产线验收。仅更新源码、文档与本地 Debug；没有生成发布包、安装包或 NuGet，没有改动私钥或签发授权。

## 已有增量

2026-10-14 已加入原生多输入回调，旧零／单输入回调保留兼容。新增 MeasureCircleDistance（两圆中心距）：两个 Features 输入、一个可见 Result 输出、显式目标索引、原图 px 坐标、包含端点的上下限公差。新增 Decision 输出的三态整流程判定，旧 Bool 判定与旧 passed 数量字段保持原义。C# 提供 GetGeometryMeasurement，WPF 直接显示测量 JSON。

证据：artifacts/tests/geometry-distance-debug-final.trx：328/328 通过；原生 Debug、隔离 WPF Debug 构建通过，CTest 2/2。2026-10-15 补跑 geometry-distance WPF 窗口流程通过，截图 artifacts/local-debug/geometry-distance-gui.png。本地程序 artifacts/local-debug/geometry-distance/JP-Vision.exe。

## 当日增量过程（历史记录）

2026-10-15 增量进展：PositionFixture 已实现独立 Image/Pose 输入、教学、参考／运行相似变换、模板摘要验证；圆与直线 ROI 可跟随。真实像素 Fixture 专项与既有结果包检查 32/32；实际 WPF fixture-circle-distance 完成教学、两个圆 ROI、30°／1.2 倍图像、卡尺位置一致性、保存重载。高级 ROI 专项与相关几何检查 41/41。双边卡尺和几何测量增量检查 44/44；后续完整回归尚待本次实现全部完成。

旋转矩形、圆环／扇环已贯通验证、掩膜、卡尺、编辑器绘制和数值编辑；ROI 保存参考摘要，基准变化会要求重画。几何测量支持点点／点线／夹角／交点，公差新增名义值与上下偏差。WPF／SDK 新类型已加入；仍在完善上游字段绑定、资源与真实 net8 宿主验证。最近代码包含 MeasureEdgePair 的关联图像扩展，正在构建验证，不能仅凭上面的旧增量报告宣称全套通过。

1. M0／M1：加入真实 Pose 类型和 PositionFixture 工具，分别绑定测量图像、定位来源；保存教学参考和模型摘要。实现同帧位姿、参考／运行变换、旧配方兼容。
2. M1：接入圆、直线的 Fixture ROI，缩放搜索窗、旋转角度；编辑器绘制时通过逆变换保存参考 ROI，每次从基准重算。
3. M1：旋转矩形与环形／扇环形 ROI 的验证、采样、绘制、数值编辑、保存／重载及 WPF 图形契约。
4. M2：集成双边卡尺、点点／点线／线线／交点测量，名义值与公差；按实际支持的几何发布结果。
5. M0／M2：上游字段绑定参与原生依赖调度和编辑器祖先、环路、撤销、删除；不可用值明确处理。
6. 完整真实模板→Fixture→两圆→中心距→公差闭环；回归旧配方、空结果、大图、资源所有权；核对真实 net8 宿主运行条件。

本轮 Fixture 仅支持平移／旋转／等比缩放；多目标 ForEach、相机实机取流、毫米标定、材料壁厚、OCR 仍属 M3～M6 后续路线图。
