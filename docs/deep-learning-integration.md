# JP-Vision 深度学习接入

## 当前目标与边界

云端租用 RTX 5080 训练，本地 Windows 运行缺陷分割模型。保留现有 C++ OpenCV 核心、WPF 编辑器、SDK、离线机器授权、ROI 和坐标订阅。此次先完成独立模型仓库、可选 CPU 推理、算子与训练脚本。云平台、预算及实际数据尚未提供，不创建付费实例，不上传生产图像，不宣称已有缺陷识别精度。

RTX 5080 的云端镜像需支持 Blackwell。PyTorch 2.7 已提供 Blackwell 和 CUDA 12.8 支持；镜像还需通过实际 GPU 运算、ONNX 导出和 CPU 推理一致性验证，不能只依据 `nvidia-smi` 判断训练环境就绪。官方依据：[PyTorch 2.7](https://pytorch.org/blog/pytorch-2-7/)。

## 模型契约 v1

模型包是同一目录中的 `manifest.json` 和 `model.onnx`。不接受执行脚本、压缩包、外部权重文件或配方内 Base64 模型。仓库默认 `%LOCALAPPDATA%/JP-Vision/Models`；每个版本目录名为原始 manifest 字节的 SHA-256，即 `modelKey`。manifest 又固定模型的 SHA-256，因此预处理和权重一起锁定。模型文件最多 512 MiB，manifest 最多 64 KiB，不扩大已有配方资源上限。

```json
{
  "schemaVersion": 1,
  "modelId": "part-defect",
  "version": "1.0.0",
  "task": "binarySegmentation",
  "modelFile": "model.onnx",
  "modelSha256": "替换为64位小写SHA256",
  "input": {
    "name": "image", "width": 512, "height": 512,
    "channels": 3, "layout": "NCHW", "dtype": "float32",
    "color": "RGB", "scale": 0.00392156862745098,
    "mean": [0, 0, 0], "std": [1, 1, 1]
  },
  "output": {
    "name": "scores", "layout": "NCHW", "dtype": "float32",
    "activation": "none", "scoreKind": "probability"
  },
  "labels": ["background", "defect"]
}
```

v1 限定一个 float32 NCHW 图像输入 `[1,C,H,W]` 和一个 float32 缺陷评分图输出 `[1,1,H,W]`，C 为 1 或 3，尺寸固定为 32–2048。灰度图转 RGB 或 BGR 转 RGB 后，按 `(pixel * scale - mean) / std` 预处理；不隐式改变训练时的归一化。输出可显式指定 sigmoid；`probability` 必须处于 [0,1]，`anomaly` 是非负异常分值，不能解释成校准概率。

单个版本绑定预处理和模型权重。导入后如需修改归一化、张量尺寸或名称，应重新生成清单并导入新版本，不能直接修改仓库文件。模型目录路径随配方保存；把配方移到其他电脑时也需要导入相同模型，并重新选择当地模型库。

`anomalySegmentation` 只表示推理接口可接收单通道异常图，不代表此版本已实现异常检测训练。训练脚本先支持有像素标注的二分类分割。没有缺陷标注时，应另行选择并验证异常检测训练路线，不能把正常图当作全部背景直接训练后宣称能找任意缺陷。

## 本地推理与数据流

1. WPF 导入 manifest，校验内容、模型哈希与文件边界，原子安装到模型仓库；配方保存 `modelRoot`、`modelKey`。
2. 执行 `DeepLearningDefect` 时才加载同目录可选 ONNX Runtime DLL，检查 ONNX 张量名称、类型、形状及实际输出。导入完整性校验与实际推理兼容性校验分别显示。
3. 当前 ROI 以原始分辨率切块，边界填充、重叠评分融合，避免把整幅 5120×5120 或长条图缩到 512 后丢失细缺陷。有效 ROI 外的像素不参与统计。
4. 阈值分割与连通区域分析生成缺陷数量、像素面积、质心、包围框和评分，坐标保留当前输入平面与原点。展开图仍属于展开坐标平面。
5. 输出 Image 保留干净像素，绿色 `#00FF00` 标记独立传递。结果也可通过 SDK 读取，不要求使用编辑器。v1 不通过 JSON 传逐像素热图。

编辑器操作：从“深度学习”分类插入“深度学习缺陷检测”，连接上游图像，在参数面板点击“导入模型…”选择 `manifest.json`；也可“列出已安装模型”后选择。绘制检测 ROI、设置阈值、最小缺陷面积和重叠宽度后执行。在运行结果中查看缺陷表。首版标记为缺陷包围框；独立掩膜／热力图预览端口尚未实现。

SDK 在既有 `VisionSession.RunAsync` 之后读取：

```csharp
using var result = await session.RunAsync(frame);
if (result.Status == ExecutionStatus.Succeeded)
{
    // 名称为配方公开的 Result 或 Image 输出名称。
    var defects = result.GetDeepLearningDefects("Defects");
    if (defects.DataAvailable)
        foreach (var defect in defects.Features)
            Console.WriteLine($"{defect.CentroidX}, {defect.CentroidY}, {defect.Area}, {defect.Score}");
}
```

`ScoreKind` 区分概率和异常分值；`Passed` 为完成推理且没有达到筛选条件的缺陷。整条配方的总判定仍由公开决策输出决定，processing 配方的总判定可以是 Unknown，不能直接当成工件合格。

无缺陷是成功执行且 `count=0, passed=true`；缺模型、推理失败、无效输入不能变成合格，也不能显示前一帧结果。推理会话复用但不缓存检测结果。当前图执行器的取消检查在算子边界，因此长算子不能承诺立即中止；后续应增加分块检查点和运行选项终止。

## 云端训练流程

训练源码位于 `tools/deep-learning`，不依赖特定云平台控制台。先在选定平台租用支持相应驱动的 RTX 5080，使用固定镜像及依赖，验证 CUDA 运算，再准备数据并启动单个训练作业。平台 API 接入需等平台、预算、权限及数据上传范围确定后完成。

数据按工件或采集批次分配 train/validation/test，之后才切块；相邻切块不能跨数据集。使用无叠加标记的原图，缺陷 mask 为真实标注。合成模型仅用于验证软件链路，不代表实物检测能力。

输出权重、训练配置、独立测试报告、ONNX、数值一致性报告和上述 manifest。回传模型包后在 JP-Vision 导入，用真实测试图检查漏检、误检、边界拼接和耗时，再决定是否上线。

训练进程的时间上限只停止训练，不等于停止云实例计费。平台未接入前需要在控制台确认关机/释放实例，保留模型、日志和数据。后续平台适配必须记录作业 ID、资源所有权、预算与取消状态，重试不得重复创建付费资源。

## 构建与验证

可选 CPU 环境：`powershell -File scripts/setup-deep-learning.ps1`。随后在 VS 以 Debug/x64 重建 native 与 WPF 项目并 F5。缺少可选 DLL 不影响普通视觉算子。只更新源码及 Debug 输出，不另行发布新版 EXE。

验收覆盖模型篡改与错误契约、实际 ONNX 执行、原图像素保持、ROI/原点映射、大图分块拼接、无缺陷及运行失败、SDK 结果读取和现有流程回归。真实训练耗时与精度需等数据和云环境齐备后测量。

本次实际结果、证据文件及限制见 [首阶段验证记录](deep-learning-validation.md)。训练源码中的完整命令见 [云端训练步骤](../tools/deep-learning/README.md)。
