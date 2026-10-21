# Editor performance benchmark

Run from the repository root after the normal native Debug build:

```powershell
./scripts/benchmark-editor.ps1 -Iterations 30
```

The script builds this harness and its managed references into a private GUID directory under `artifacts/tests/editor-performance/`. It does not rebuild the native runtime, replace the normal VS output, or access a camera. It imports `artifacts/activation/current.vlic` into its private test store through the normal licensing API; no signing key is required.

Each mode warms three times, then the old and optimized modes alternate for at least 20 measured iterations. Both use the same 5120 × 5120 Gray8 image and verify output dimensions and the synthetic disk's exact Blob pixel area. The optimized execution path calls the production `EditorExecutionCache`; the optimized preview path compiles the production `CameraPreviewFrame` helper.

The report includes medians, P95, individual samples and the native DLL hash. Wall time covers the measured preparation, execution, result verification and disposal. Preview measurements cover bitmap preparation on an STA thread, excluding acquisition, UI composition and actual rendering. Managed allocation counts do not include native/OpenCV/WPF image memory or the already acquired camera buffer. These are warm Debug measurements on the current machine, not a camera FPS or production cycle-time guarantee.
