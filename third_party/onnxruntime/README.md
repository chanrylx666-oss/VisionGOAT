# ONNX Runtime native headers

Unmodified headers and license texts from the official `Microsoft.ML.OnnxRuntime` 1.24.4 NuGet package:
https://api.nuget.org/v3-flatcontainer/microsoft.ml.onnxruntime/1.24.4/microsoft.ml.onnxruntime.1.24.4.nupkg

Package SHA-256: `4b978d5065b85e7004b6c6f60ca494bd978fbe6836cbf0a0b52d82b61ab99638`.

The optional Windows x64 CPU DLLs live under ignored `artifacts/dependencies/onnxruntime/win-x64` and are installed by `scripts/setup-deep-learning.ps1`. They are loaded lazily only for deep-learning inference. No ONNX Runtime import library is linked into the vision runtime.
