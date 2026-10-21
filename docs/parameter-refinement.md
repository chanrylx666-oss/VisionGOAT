# 算子精细参数与调试（2026-10-19）

继续打开 `JPVision.sln`，选择 Debug / x64，生成后用 `JPVision.Studio.Wpf` 按 F5 运行。此次只更新源码和普通调试构建，不生成独立版本 EXE、发布目录或安装包。

## 参数面板

选中算子，在“算子参数”页调整。普通参数按输入与通道、定位与跟随、卡尺采样、边缘提取、拟合与剔除、匹配搜索与精度、数量判定、尺寸公差及显示与标记分组。点击分组标题展开／收起，同一算子类型会保留本次打开程序后的折叠状态。

每项显示范围、步长、单位和用途。数字可直接输入，按 Enter 或离开输入框提交，也可用 +／− 或键盘上／下方向键微调。↺ 恢复该项默认值，参与撤销／重做；不会清除 ROI、模板或其他参数。非法数值明确提示范围，并把输入框恢复为原来的有效值。Runtime 仍保留校验。

数字步长不限制直接输入的精度。核尺寸、卡尺宽度等另有奇数约束。核高度的 0 表示跟随核宽度，加号从 0 调到 1，再按 1、3、5 等调整。

“线宽／标记线宽”是画笔粗细，只允许 0.5～10。“结果圆环带宽”是拟合圆周周围的输出环带，允许 0；设为 0 并执行，只显示拟合圆周。带宽为正数时会出现拟合圆、内边界和外边界。“本次运行输入”的 ExpectedCount 显示为“预期目标数量”，用于绑定的数量判定，不能强制找出指定数量的目标。

## 新增算法控制项

| 算子 | 参数 | 范围与默认行为 |
| --- | --- | --- |
| 灰度转换 | `channel` 灰度来源 | 默认 luminance 亮度加权；blue／green／red 提取 BGR 通道。灰度输入保持原值 |
| 高斯滤波 | `kernel`、`kernelHeight` | 宽为 1～101 奇数，默认 5；高为 0 或 1～101 奇数，默认 0 跟随宽度 |
| 高斯滤波 | `sigma`、`sigmaY` | X、Y 标准差 0～100；X 默认 1，Y 默认 0 跟随 X；X 为 0 时由核尺寸估计 |
| 高斯滤波 | `borderMode` | 默认 reflect101 不重复边界的镜像；也可 reflect、replicate、constant；常量边界补 0 |
| 二值化 | `method` | 默认 fixed 固定阈值；新增 otsu、triangle、adaptiveMean、adaptiveGaussian |
| 二值化 | `blockSize`、`offset` | 局部窗口为 3～255 奇数，默认 15；C 为 -255～255，默认 2；局部阈值 = 邻域均值 − C |
| 形态学 | `kernelShape`、`kernelHeight` | 默认 rectangle 矩形，也可 ellipse、cross；高默认 0 跟随原核宽，支持独立奇数高度 |
| 形态学 | `operation` | 保留 open、close、erode、dilate，新增 gradient、topHat、blackHat |
| 形态学 | `borderMode`、`borderValue` | 默认常量自动边界，保留原行为；可选复制或镜像。常量值只允许 -1 自动、0 黑色、255 白色，保持二值掩膜 |
| 圆／线卡尺、双边卡尺 | `projectionStatistic` | mean 默认均值；median 对切向窗口取中位数，抑制较窄的亮暗划痕 |
| 圆／线卡尺、双边卡尺 | `subpixelRefinement` | 默认 true，保留梯度峰值抛物线插值；关闭后使用整数采样位置 |
| 圆查找自动模式 | `preBlurKernel`、`preBlurSigma` | 核为 1～31 奇数，默认 5；Sigma 为 0～10，默认 1；单位为检测工作图像素，与卡尺平滑分开 |
| 直线查找自动模式 | `edgeAperture`、`edgeL2Gradient` | Canny 核可为 3／5／7，默认 3；L2 梯度默认 false，保持原有幅值计算 |

二值化仍要求 Gray8 输入，彩色图先接灰度转换。自动阈值 Otsu／Triangle 只统计 ROI 内有效像素，不把圆形、多边形、圆环包围框外的像素纳入直方图。输出仍是 0／255。局部阈值采用 ROI 裁剪图上的邻域，后续仅使用 ROI 内有效像素。

界面按所选方式显示对应参数：固定阈值显示数值，自动阈值隐藏无效的手工阈值，局部阈值显示窗口与偏移；圆／线自动检测和卡尺参数互不混用。快速匹配、快速定位、旋转尺度匹配沿用已有搜索算法，精度、候选数、重叠抑制等原有参数增加分组、说明和细步进；本轮没有改变匹配算法。

## 兼容与开发

组件 ID、已有参数名、配方格式和 C ABI 保持。旧配方缺省参数仍为亮度灰度、原有方形滤波核、固定阈值、矩形形态学核、卡尺均值投影和开启亚像素插值。原点、ROI 有效掩膜、单一“全部结果”端口、绿色结果及 WPF SDK 调用沿用。

新增组件可给参数描述追加 `group`、`description`、`unit`、`step`，WPF 优先使用；常见参数使用 `ParameterEditor` 内置说明。`odd_parameter(...)` 提供奇数窗口的 UI 元数据，算法仍须有原生校验，例如 `require_odd_kernel`。这些是组件目录字段，不是写入配方 parameters 的额外算法参数。

NativeRuntime 的 NMake IntelliSense 包含路径补入 core/components，并与 CMake 使用相同的常用预处理定义。C++20 和 OpenCV include 原路径保留。头文件红线应结合生成结果判断；若 VS 仍保留旧解析状态，重新加载项目，并从 `JPVision.NativeRuntime` 项目打开文件。

## 验证位置

`RefinedParameterTests.cs` 使用真实 Runtime 比较像素、窗口方向、边界、自动阈值、ROI 统计、形态学核、卡尺抗划痕投影、亚像素位置以及 Canny 选项。`ParameterEditorTests.cs` 检查范围、奇数／0 哨兵、有限数值、步进、条件显示、扩展说明和中文错误。

窗口回归增加 parameters-threshold、parameters-gaussian、parameters-morphology、parameters-caliper，实际触发选择、输入、加减、恢复默认和撤销事件，再保存、重载并执行。证据在 artifacts/tests/refined-parameters-wpf 和 refined-parameters-final.trx；这些属于调试验证产物。
