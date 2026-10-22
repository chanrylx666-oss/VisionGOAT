# Optional inference runtime probe

Development helper for the process-level regression in `DeepLearningRuntimeTests`.
It uses an existing signed development license; it never signs licenses or reads issuer keys.

```powershell
dotnet run --project tools/DeepLearningProbe/DeepLearningProbe.csproj -c Debug -- `
  D:/open500/JP-Vision D:/models/export/manifest.json `
  D:/open500/JP-Vision/artifacts/tests/my-isolated-probe
```

The helper owns its own executable output. Before any ORT API is initialized it temporarily
removes its own copy of `onnxruntime.dll`, checks an ordinary ToGray run still succeeds and
DeepLearningDefect explicitly fails, restores the file, then retries inference in the same
process. Native process termination makes the parent regression fail. Its `verification.json`
also records typed results and checks clean output pixels.

The test directory must be a child of the project's `artifacts/tests`. The model must use
the documented fixed NCHW contract and support overlap 16. Do not run concurrent instances
against this helper's same build output. Production detection accuracy is outside this probe.
