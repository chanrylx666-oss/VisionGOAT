# JP-Vision 云端缺陷分割训练

本目录提供可在租用的 RTX 5080 上运行的批处理训练源码。第一条可执行路线是**有监督的二分类缺陷分割**：输入干净原图和逐像素缺陷标注，训练轻量 U-Net，导出 JP-Vision 支持的 ONNX 模型包。本目录不登录云账号、不租用机器、不上传数据，也不会替用户停止云实例。

如果只有良品图、没有缺陷掩膜，应另选异常检测训练路线。当前训练脚本不能凭空从良品训练得到“划痕／缺口／异物”的类别；它只区分 `background` 和 `defect`。生产精度必须在独立批次的数据上评估。

## 1. 先在本地检查数据

复制 `dataset.example.json`，将三个集合替换为真实文件，所有路径都相对 `--root`，使用 `/` 分隔。每个样本必须包含 `image`、`mask`、`group`：

- `image`：单帧 8-bit RGB 或灰度原图；训练时统一转为 RGB，不接受绘制了绿色标记的截图。
- `mask`：相同尺寸的单帧 Gray8 图。背景为 0，缺陷为 255；良品使用全黑掩膜。
- `group`：同一工件、同一连续拍摄批次应使用同一组名。训练、验证、测试集合的组不能重叠。实际分组粒度须覆盖会导致数据泄漏的业务关系，不能只给相邻帧换文件名。

三个集合都必须有样本，训练集至少有一个缺陷样本。脚本拒绝组交叉、同一图像文件重复、跨集合完全相同的图像文件字节、路径逃逸、符号链接／重解析路径、损坏图像、掩膜尺寸不一致和非二值掩膜。重编码过的重复图无法靠文件字节哈希识别，仍需正确分组。`sourceImages: "raw"` 是数据准备者的声明，程序无法可靠判断图像是否已经叠加标记。

只做检查不需要安装 PyTorch：

```powershell
python -m pip install Pillow==11.2.1
python train.py validate-dataset --manifest D:/dataset/dataset.json --root D:/dataset
```

检查输出包含数据集版本、各集合数量、缺陷图数量和 SHA-256。图像和掩膜内容参与数据集哈希，修改像素会改变版本身份。单图上限与本地视觉核心一致：单轴 32768、总像素 32 Mi。数据校验不会把原图缩小。

## 2. 准备云端 RTX 5080 环境

使用支持 Blackwell 的驱动和 CUDA 框架组合。这里固定使用 **PyTorch 2.7.1 + cu128**；该版本不是“当前最新版”，而是明确的兼容基线。PyTorch 官方列出了 [2.7.1 的 cu128 安装命令](https://pytorch.org/get-started/previous-versions/#v271)，[2.7 发布说明](https://pytorch.org/blog/pytorch-2-7/)说明了 Blackwell 和 CUDA 12.8 支持。宿主驱动和容器 GPU 透传仍需实际验证，不能仅凭镜像名称认定可用。

可在已租用的 GPU 机器上构建容器：

```bash
docker build -t jp-vision-training:torch2.7.1-cu128 .
docker run --rm --gpus all jp-vision-training:torch2.7.1-cu128 --help
```

`Dockerfile` 从 Python 3.11 基础镜像安装官方 CUDA wheel 和固定版本的 NumPy、Pillow、ONNX、ONNX Runtime。训练只使用 PyTorch CUDA；导出后用 **CPU ONNX Runtime** 验证与 JP-Vision 本地部署的数值一致性，不在同一环境混装 CPU/GPU ORT。

如果云平台已经提供 Python 环境，可建立虚拟环境，然后执行：

```bash
python -m pip install torch==2.7.1 --index-url https://download.pytorch.org/whl/cu128
python -m pip install -r requirements.txt
```

请先用平台提供的 `nvidia-smi` 确认 RTX 5080，再测试真实 CUDA 运算。`train` 默认 `--device cuda`，无法运行 CUDA 时明确报错，不会静默降级到 CPU 消耗租用时长。

## 3. 训练

将已校验的数据复制到自己授权的云端目录；涉及工厂图片时先确认上传范围和平台存储策略。数据、账号和预算未确定前，不创建付费实例。

```bash
docker run --rm --gpus all --network none \
  -v /data/part-A:/dataset:ro \
  -v /work/jp-vision-jobs:/results \
  jp-vision-training:torch2.7.1-cu128 train \
  --manifest /dataset/dataset.json --root /dataset \
  --output /results/job-001 \
  --model-id part-A-defect --model-version 1 \
  --tile-size 512 --overlap 64 --batch-size 4 \
  --epochs 30 --max-minutes 60 --threshold 0.5
```

输出目录必须为空。原图在完成工件／批次分组之后才生成训练块，不把相邻块分别放入训练和测试集合。图像按原始像素切块，重叠区域采用均值拼接；不足一个块的边缘补黑，补齐像素不参与损失和评估。10918×287 这类长条图可沿长边切块，窄边补齐，避免整幅压缩丢失细小缺陷。

模型为两级下采样的轻量 U-Net（16/32/64 通道），采用 BCEWithLogits 加 Dice 损失。`--positive-weight` 可提高稀少缺陷像素的 BCE 权重，默认 1。不会自动下载预训练权重；样本少时效果可能有限。验证集用于选择最佳 Dice 的权重，测试集只在最终评估时使用。

训练每个完整 epoch 保存 `checkpoint.pt`，Ctrl+C／SIGTERM 或到达时限时也会保存当前权重。检查点只保存张量和基本元数据；恢复显式使用 [torch.load(weights_only=True)](https://docs.pytorch.org/docs/2.7/generated/torch.load.html)，不加载自定义 Python 对象。只能使用自己可信的检查点。

```bash
python train.py train --manifest /dataset/dataset.json --root /dataset \
  --output /results/job-002 --resume /results/job-001/checkpoint.pt \
  --model-id part-A-defect --model-version 1 \
  --tile-size 512 --overlap 64 --epochs 30 --max-minutes 60
```

恢复时数据哈希、块大小、重叠宽度、batch 大小、损失权重、学习率、阈值和 seed 必须一致。`epochs` 是目标总 epoch 数；未完成的 epoch 会重新遍历，恢复不是逐 batch 完全确定的续跑。数据校验发生在训练计时之前；取消和时限在批次、评估分块及导出阶段之间响应，不能强行中断正在运行的单次 CUDA／ONNX 调用。

**`--max-minutes` 只限制本训练进程的工作时间，不能限制云账单。** 超时时退出码为 75，`training-report.json` 的状态为 `paused`，没有完成验证的模型包不会作为成功模型发布。成功为 0，参数／数据错误为 2。下载结果后必须在云平台停止或释放自己创建的实例，存储费用也需单独核对。不要删除其他任务的实例或数据。

## 4. 下载模型并导入 JP-Vision

成功训练目录包含：

```text
dataset-audit.json       数据版本、分组和数量
run-config.json          运行参数
checkpoint.pt            可恢复权重
training-report.json     实际训练、验证、独立测试结果
bundle/
  model.onnx
  manifest.json
  export-validation.json
```

`bundle` 只在 ONNX checker、固定张量签名校验和三个 CPU 数值一致性测试全部通过后发布。模型输入为 `image: float32[1,3,H,W]`、RGB、`像素 / 255`；输出为 `scores: float32[1,1,H,W]`、Sigmoid 后的缺陷得分。该得分不是经过概率校准的生产合格率。详情参见 [PyTorch 2.7 ONNX 导出文档](https://docs.pytorch.org/docs/2.7/onnx_torchscript.html)。

将整个 `bundle` 文件夹下载到本机，在 JP-Vision 的“深度学习缺陷”算子内导入 `manifest.json`。本地仓库用清单原始字节 SHA-256 固定模型身份，并核对 `model.onnx` 的哈希。配方不存储模型二进制，也不需要生产运行时连接云端。

本地检测参数应先使用与评估一致的阈值和重叠宽度，再结合最小面积等后处理评估误检、漏检。报告中的像素精确率／召回率／Dice 和“良品图中任意一个误检像素”的图级误检数是明确定义的基础指标，未应用本地算子的面积过滤，不等于最终产线验收指标。没有正例或预测时无法定义的比率输出 `null`。

## 5. 开发验证

```bash
python -m unittest discover -s . -p 'test_*.py' -v
python -m py_compile dataset.py train.py
```

这些数据／切块测试不需要 Torch，也不代表 RTX 5080 已经训练成功。若执行小型合成数据训练，只能证明训练、导出和模型包链路可运行，不能用合成数据精度代替真实工件测试。
