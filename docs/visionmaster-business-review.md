# VisionMaster 4.4 业务逻辑参考与 JP-Vision 修复

## 参考证据与范围

用户指定本机 `C:/Program Files/VisionMaster4.4.0`。安装信息为 VM V4.4.0 Build20260527，算法 SDK V4.4.3（Applications/VersionInfo.config）。本次读取随软件提供的 SDK 头文件、模块 XML 和官方手册，未运行 VisionMaster 页面或反编译其算法。REA doctor 确认当前 Codex 未连接 REA，且没有配置 native 分析引擎；业务接口证据足以推进本次审查，因此没有修改 MCP 配置或安装引擎。

以下为接口／配置观察，不等于对内部实现或所有版本的运行行为认证：

| 业务机制 | 本机 4.4 证据 | 对 JP-Vision 的影响 |
|---|---|---|
| 图像与定位结果分开 | [圆查找输入图像](<C:/Program Files/VisionMaster4.4.0/Development/V4.x/Includes/IVmCircleFind.h:108>)；[图像来源绑定](<C:/Program Files/VisionMaster4.4.0/Applications/Module(sp)/Group.xml:19>) | 一个可见端口也须能转交实际像素，不能把 JSON 当图像 |
| 搜索范围与训练模板分开 | [快速匹配全图选项](<C:/Program Files/VisionMaster4.4.0/Applications/Module(sp)/x64/Location/IMVSFastFeatureMatchModu/IMVSFastFeatureMatchModuAlgorithmTab.xml:26>)，同文件第 60 行独立模板页 | 模板大小限制不得变成搜索图大小限制；切换全图不重学模板 |
| 位置修正有基准与本次位姿 | [创建基准](<C:/Program Files/VisionMaster4.4.0/Development/V4.x/Includes/IVmFixture.h:50>)；第 73、93 行分别为基准与运行位置 | 后续 ROI 必须有明确坐标系；不能只传一个圆心或把结果丢回全图 |
| 显式图像修正也是独立能力 | [图像修正输入](<C:/Program Files/VisionMaster4.4.0/Development/V4.x/Includes/IVmImageFixture.h:34>)、位置修正输入第 37 行、输出图像第 60 行 | 裁图与旋转／尺度归一化不同，不能用仅平移 origin 的图冒充完整位姿变换 |
| 匹配数量与单目标索引分开 | [匹配结果数量](<C:/Program Files/VisionMaster4.4.0/Development/V4.x/Includes/IVmFastFeatureMatch.h:543>)、零基索引第 549 行 | 明确处理 resultIndex 对应的目标；逐个目标循环另有语义 |
| 匹配点是独立定位结果 | [GetMatchPoint](<C:/Program Files/VisionMaster4.4.0/Development/V4.x/Includes/IVmFastFeatureMatch.h:476>) | JP-Vision 将模板框中心作为默认定位点，直接提供原图 centerX/Y；不把匹配框左上角当圆心 |
| 圆模块集成测量与卡尺诊断 | [卡尺数量](<C:/Program Files/VisionMaster4.4.0/Development/V4.x/Includes/IVmCircleFind.h:170>)、圆心第 440 行、半径第 444 行、卡尺窗口第 481 行 | 保持单模块调参、真实几何、采样／剔除诊断与结果显示 |
| 拟合圆、输出圆环与搜索区域分开 | [GetROIAnnulus](<C:/Program Files/VisionMaster4.4.0/Development/V4.x/Includes/IVmCircleFind.h:430>)、GetFitArc 第 465 行、GetOutputCircleAnnulus 第 470 行；VmModuleResultBase.h 第 114 行的 Annulus 字段 | JP-Vision 分别输出 circle、outputAnnulus 与 caliper.searchAnnulus；配置圆环宽度不能冒充实测材料厚度 |
| 流程执行与集成接口独立 | [单次执行](<C:/Program Files/VisionMaster4.4.0/Development/V4.x/Includes/IVmProcedure.h:314>)、连续执行第 338 行、结果回调第 416 行 | 编辑器与 WPF 宿主共享可加载配方的执行核心；回调／UI 线程须有明确边界 |

官方 [VM 用户手册](https://pinfo.hikrobotics.com/hkws/unzip/20231101163730_26101_doc/pdf.pdf) 的模板匹配章节也描述匹配与位置修正、测量模块配合使用。本机 SDK 示例目标为 .NET Framework 4.6.1，不能据此推断它已支持现代 .NET；JP-Vision 继续采用自己的 net8 SDK 和 WPF 界面。

## 已确认冲突与修复

1. Result 只有 JSON、没有图像投影，编辑器拒绝 Result→Image。新增真实隐藏 Image 字段，由唯一可见结果口转交，保存 fromPort=image，原生继续严格按类型验证。
2. 图像导入支持 32 Mi 像素，而快速匹配只支持 16 Mi。现在在导入上限内支持全图搜索，评分采用有限分块，不按整图创建多张浮点评分图。
3. 检测 ROI 的 ShapeChoice 默认没有开启全图项。启用全图项，选中即清除搜索 ROI；模板模型和模板绘制区域保持。
4. source frame 与 selected crop 混用会让 ROI 画在错误坐标中。匹配器显示自身实际输入和全部匹配；圆查找编辑／显示当前选中目标图，原点跟随物品移动，测量回到原图坐标。
5. 无匹配或索引越界不能回退原图。传递 available=false，下游正常返回空检测和 selected=null；图像 Getter 明确拒绝读取，不复用上一帧位置。
6. 分块接缝附近的近重复峰可能占满全局候选池。先按重叠阈值合并邻近候选，再限制全局数量；保留评分最高的窗口。
7. 黑色补边可能让直线查找误检图像边缘。仅在补边图上排除需要无效邻域的 Canny 边缘，保留有效图像里的真实线；不修改共享源掩膜。

## 当前操作流程

外部图像 → 快速匹配／快速定位 → 圆查找。

先学习物品模板，检测 ROI 选“全图”或绘制搜索范围。将匹配器“全部结果”连到圆查找图像输入，选择 resultIndex 对应目标。新建匹配节点默认“原图＋定位中心”，新建圆查找默认“自动跟随上游匹配中心”：无绘制 ROI 时使用定位搜索半径，有圆 ROI 时使用其半径；中心每帧由本次匹配中心加 matchOffsetX/Y 确定。绘制／拖动圆 ROI 同时保存相对模板中心偏移，模板只覆盖特征的一部分也能定位模板之外的待测圆。

切换圆查找 method=caliper 时，定位模式将卡尺沿实时参考圆布置；拟合圆心由实际采样边缘计算，允许它与搜索中心不同。圆输出 centerX/Y、radius、diameter、circle 轮廓、outputAnnulus（实测圆心与半径加配置宽度的输出区域）、caliper.searchAnnulus（采样参考区域）和边缘／剔除／误差诊断，保持唯一 Result 口。输出圆环可绘制、导出或由 SDK 读取，不是同时测量工件内外两条边。

旧配方未声明 downstreamImageMode 时仍使用 target 裁图；未声明 positionReference 时仍按 image 局部 ROI 执行。旧配方要启用新定位功能，分别切换“后续检测图像”和“定位基准”。source 图是匹配前的实际处理图，可已有前序 ROI／灰度转换，不会绕过父掩膜恢复未经处理的外部图。

匹配图投影保持原图方向和像素尺度。扩边靠近原图边界时使用固定画布、无效区掩膜和稳定 origin；不会因截掉左侧扩边而改变 ROI 相对物品的位置。源像素来自实际前序处理结果，父掩膜继续生效，匹配模块自身搜索 ROI 不变成后续测量的硬边界。

## 与完整 VisionMaster Fixture 的区别

本次实现明确传递原图匹配中心，支持平移参考下的圆 ROI、卡尺和原图测量，单次处理选中的一个目标。偏移是原图 X/Y 轴向像素值，半径也是原图像素；保留匹配角度／尺度用于诊断，但不自动旋转偏移或缩放圆。完整“基准／运行位姿 + ROI 仿射修正”、Group 多目标迭代、跨帧身份追踪仍未实现。

粗搜索为有候选上限的启发式筛选，原分辨率回退在全局无有效粗候选时触发，不能宣称找回每个可能漏掉的物品。需要更严格的搜索时设置粗搜索倍数为 1。取消／超时仍在算子执行边界生效，不承诺每个分块内立即中断或固定帧率。

验证记录见 [verification.md](verification.md)，本次只更新源码和本地调试程序，不打包。
