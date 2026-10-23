# Long-strip point-set benchmark

By default this development tool uses a deterministic **10996 × 300 BGR** image, a **2751-station** edge search strip (spacing 4, height 118.3, projection width 5), median filtering, a fitted reference line and per-point distances. An optional mode reads an explicitly supplied real recipe and image. It does not access cameras or cloud accounts, and never modifies source recipes or images.

The existing `artifacts/activation/current.vlic` is imported into a GUID test license store. No license issuer or private key is used. Compilation/loading are outside the timed section; one recipe/session is reused. Normal mode enables the same retained intermediate results as the editor. Run `--mode lean` for a separate comparison with full debug retention disabled; published point/distance outputs remain available.

```powershell
dotnet build tools/PointSetBenchmark/PointSetBenchmark.csproj -c Debug `
  --artifacts-path D:/open500/JP-Vision/extra/pointset-benchmark/build

& extra/pointset-benchmark/build/bin/PointSetBenchmark/debug/PointSetBenchmark.exe `
  --repo D:/open500/JP-Vision --out D:/open500/JP-Vision/artifacts/tests/pointset-baseline `
  --configuration Debug --iterations 5 --warmup 3 --mode debug
```

Rebuild the native runtime and the tool before testing a new binary. Rebuilding only the managed tool does **not** compile native sources. For Release, build both native and managed configurations and use the `release` output directory. Do not rebuild/replace the tool's output files while its process is running.

`report.json` records native and SDK DLL SHA-256, per-run wall/native/node times, managed allocations and result JSON byte counts. Wall time includes native execution and SDK result retrieval/parse, but excludes validation, extra JSON size counting, canonical output construction, image hashing, UI painting and camera acquisition. Native memory is not covered by the managed allocation metric.

`canonical-output.json` retains all edge points, distance values, statistics and overlays, plus the processed-image pixel hash. Volatile run/node timing and image-pipeline frame identifiers are excluded. Its SHA-256 must match between baseline and optimized runs when testing behavior-preserving changes. The returned detector image is checked against its median-filtered input to catch overlays being burnt into pixels.

The synthetic signal is smooth and uses a 3-pixel median kernel. It is a repeatable software benchmark, not a substitute for measuring the user's original texture, kernel settings, recipe and image. The four stations outside valid image sampling naturally yield fewer measured points (currently 2747), while the requested station count remains 2751.

To test an existing recipe without changing its parameters or public outputs:

```powershell
& extra/pointset-benchmark/build/bin/PointSetBenchmark/debug/PointSetBenchmark.exe `
  --repo D:/open500/JP-Vision --out D:/open500/JP-Vision/artifacts/tests/pointset-real-baseline `
  --recipe C:/path/process.vrecipe/recipe.json --image C:/path/original.jpg `
  --configuration Debug --iterations 5 --warmup 1 --mode debug
```

Real mode requires exactly one FindEdgePoints, MeasurePointLineDistances and MedianBlur node. It reads their retained results through node IDs instead of appending public aliases, and therefore currently requires debug retention. JPEG/PNG/TIFF decoding and BGR24 conversion match Studio and happen outside the timer. Source hashes, image size, result counts and all numeric `*Ms` node timing fields are reported. Dynamic frame prefixes in unwrapped coordinate-plane identifiers are normalized only in the comparison snapshot; runtime data is untouched. Run baseline and optimized passes with the same tool, native configuration, inputs, and retention settings while other build/test jobs are idle.

For Profile, build the native `RelWithDebInfo` runtime with `scripts/build-native.ps1 -Configuration Profile`, then build the tool with `-c Profile`. The managed tool and SDK retain Debug symbols and unoptimized C#; the native runtime and OpenCV use optimized code. Record this as Profile rather than comparing it as though it were an unchanged Debug build. The same-configuration Debug and Release comparisons isolate the source/SDK optimization gains from the much larger benefit of optimized native compilation. See `docs/performance-pointsets.md` for the measured example and Visual Studio usage.

Use `--backend managed` or `--backend native` to compare the C# workflow with the legacy C++ workflow using the same current SDK and native DLL; `--backend default` uses the SDK default (currently Managed). The report records the effective backend. Compare `medianWallMs` for end-to-end SDK delivery. `medianExecutionMs` is the backend's reported workflow time: Managed includes per-node result transfers, while Native excludes its final result export. `medianNativeMs` remains only as a historical report-field alias and must not be interpreted as pure native kernel time for Managed.
