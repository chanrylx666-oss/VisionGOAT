using JPVision.Sdk;
using JPVision.Wpf;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // A real .NET 8 host: no editor assembly, major-version roll-forward or license bypass.
        if (args.Length != 4) { Console.Error.WriteLine("root recipe image capture.png"); return 2; }
        string root = Path.GetFullPath(args[0]);
        string store = Path.Combine(root, "artifacts", "tests", "net8-host-" + Guid.NewGuid());
        try
        {
            if (Environment.Version.Major != 8) throw new InvalidOperationException("Probe must run on .NET 8.");
            VisionLicensing.Import(Path.Combine(root, "artifacts", "activation", "current.vlic"), store);
            var decoder = new PngBitmapDecoder(new Uri(Path.GetFullPath(args[2])), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var bitmap = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Gray8, null, 0);
            byte[] pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight]; bitmap.CopyPixels(pixels, bitmap.PixelWidth, 0);
            using var frame = VisionFrame.CopyFrom(pixels, bitmap.PixelWidth, bitmap.PixelHeight, bitmap.PixelWidth, VisionPixelFormat.Gray8);
            using var engine = new VisionEngine(store); using var recipe = engine.LoadRecipe(args[1]); var session = recipe.CreateSession();
            InspectionResult result;
            try { result = session.RunAsync(frame).GetAwaiter().GetResult(); }
            finally { session.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            using (result)
            {
                var measurement = result.GetGeometryMeasurement("Distance"); var circles = result.GetResult<VisionCircle>("CircleA");
                if (result.Status != ExecutionStatus.Succeeded || result.Decision != InspectionDecision.Pass || !measurement.Valid ||
                    measurement.Value is not double distance || distance < 68 || distance > 71 || circles.Count != 1 || circles.Measurement.Fixture is null)
                    throw new InvalidOperationException("Real fixture/distance/tolerance chain failed in net8 host.");
                var overlays = result.GetOverlays("Overlay").ToList();
                if (overlays.Count < 2) throw new InvalidOperationException("Missing measured overlay.");
                overlays.Add(new VisionOverlay("annulus", Array.Empty<VisionPoint>(), "#FF3038", 2, false, 150, 100, "")
                    { InnerRadius = 20, OuterRadius = 35, StartAngle = 300, EndAngle = 420 });
                using var output = result.GetOutputImage("CircleA");
                var view = new VisionView { Width = 960, Height = 600 }; view.PresentCopy(output, overlays);
                view.Measure(new Size(960, 600)); view.Arrange(new Rect(0, 0, 960, 600)); view.UpdateLayout(); view.Fit();
                var render = new RenderTargetBitmap(960, 600, 96, 96, PixelFormats.Pbgra32); render.Render(view);
                byte[] rendered = new byte[960 * 600 * 4]; render.CopyPixels(rendered, 960 * 4, 0);
                int redPixels = 0; for (int i = 0; i < rendered.Length; i += 4) if (rendered[i + 2] > 180 && rendered[i + 1] < 100) redPixels++;
                if (redPixels < 100) throw new InvalidOperationException("WPF image overlays were not rendered.");
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(render));
                using (var file = File.Create(args[3])) encoder.Save(file);
                var benchmark = Benchmark(recipe, frame);
                string report = JsonSerializer.Serialize(new { framework = RuntimeInformation.FrameworkDescription,
                    cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", "unavailable"), image = frame.Info, distance, decision = result.Decision.ToString(),
                    fixture = circles.Measurement.Fixture, overlays = overlays.Count, redPixels, benchmark });
                File.WriteAllText(args[3] + ".json", report); Console.WriteLine(report);
            }
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { if (Directory.Exists(store)) Directory.Delete(store, true); }
    }
    private static object[] Benchmark(VisionRecipe recipe, VisionFrame frame)
    {
        var info = frame.Info; var pixels = Enumerable.Repeat((byte)15, info.Stride * info.Height).ToArray();
        using var empty = VisionFrame.CopyFrom(pixels, info.Width, info.Height, info.Stride, info.PixelFormat);
        var session = recipe.CreateSession();
        try
        {
            var samples = new List<object>();
            foreach (bool found in new[] { true, false })
            {
                var times = new List<double>();
                for (int i = 0; i < 33; i++)
                {
                    long start = Stopwatch.GetTimestamp(); using var result = session.RunAsync(found ? frame : empty).GetAwaiter().GetResult();
                    if (result.Status != ExecutionStatus.Succeeded || result.GetGeometryMeasurement("Distance").Valid != found ||
                        result.Decision != (found ? InspectionDecision.Pass : InspectionDecision.Unknown)) throw new InvalidOperationException("Repeated frame changed result validity.");
                    if (i >= 3) times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                }
                times.Sort(); var process = Process.GetCurrentProcess(); process.Refresh();
                samples.Add(new { found, runs = times.Count, p50Ms = times[14], p95Ms = times[28],
                    processPeakWorkingSet = process.PeakWorkingSet64, processPrivateBytes = process.PrivateMemorySize64,
                    native = VisionEngine.GetDiagnostics() });
            }
            return samples.ToArray();
        }
        finally { session.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }
}
