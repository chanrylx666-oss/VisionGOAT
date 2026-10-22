# 深度学习首阶段验证记录（2026-10-22）

本次交付是源码和普通 Debug 构建，没有生成独立发布包，没有租用云实例或上传生产数据。

| 验证 | 结果 | 证据 |
|---|---|---|
| 首轮完整托管回归 | 900/900 通过 | `artifacts/tests/deep-learning-regression.trx` |
| 最终深度学习专项（包含审查修复） | 68/68 通过 | `artifacts/tests/deep-learning-final.trx` |
| 最终原生 CTest | 3/3 通过 | `artifacts/build/runtime/Testing/Temporary/LastTest.log` |
| 训练源码工具 | 22/22 通过，含实际 CPU 训练／导出／停止／恢复 | `artifacts/tests/deep-learning-training/verification.json` |
| WPF 最终场景 | 深度学习、普通三节点链、启动空流程均通过 | `artifacts/tests/final-*.png.json` |
| WPF Debug 构建 | 0 警告、0 错误 | `artifacts/tests/deep-learning-wpf-build.log` |
| 真实导出模型→原生推理 | 导入成功、8 块、结果可读、原图像素不变 | `artifacts/tests/dl-trained-export-final/verification.json` |
| 缺少可选 ORT／恢复重试 | 普通 CV 正常；DL 明确失败；同进程恢复 DLL 后成功 | 上述独立进程记录与专项测试 |

首轮完整回归与最后专项存在重叠，不能把它们相加当作不同测试总数。

训练验证使用小型合成数据和 CPU。独立导出检查运行 ONNX checker 与三次数值探针，PyTorch 与 CPU ONNX Runtime 最大绝对误差为 `5.960464477539063e-08`。该证据证明导出和推理兼容，**不证明工件缺陷精度**；一轮合成训练的缺陷 Dice 为 0，不能用于生产。

独立进程测试还暴露并修复了一个可选依赖失败路径：在调用 `Ort::InitApi` 之前构造 `Ort::Env(nullptr)`，其析构仍可能访问未初始化 API。当前用空 `unique_ptr` 延后构造，缺少 DLL 时不会进入 ORT 包装对象的析构；恢复后允许重试。

尚未验证：云端 RTX 5080 的具体驱动／容器、真实工件漏检与误检、工业大图的生产节拍、GPU 本地推理。首版仅输出区域和包围框图形，没有独立掩膜或热力图预览。取消仍在算子边界响应。
