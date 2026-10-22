using JPVision.Editor.Core;
using JPVision.Sdk;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace JPVision.Studio;

public partial class MainWindow
{
    private async Task SmokeOutputRetentionAsync(string scenario)
    {
        if (scenario == "output-retention-save")
        {
            flow!.Add("ExternalImage", 30, 100, "source");
            flow.Add("ToGray", 300, 100, "gray");
            flow.Connect("source", "image", "gray", "input");
            Graph.Select("gray");
            Require(flow.Definition["outputs"]!.AsObject().Count == 0, "new graph already has public outputs");
            await BusyAsync(RunOnceAsync);
            var savedRun = result!;
            recipeDirectory = CapturePath + ".vrecipe";
            SaveRecipe(false);
            Require(flow.Definition["outputs"]!.AsObject().Count > 0, "saving did not materialize automatic outputs");
            var position = flow.Positions["gray"];
            flow.Move("gray", new EditorPoint(position.X + .25, position.Y));
            Require(ReferenceEquals(result, savedRun), "layout move after saving discarded the completed run");
            Graph.Select("source"); Graph.Select("gray");
            Require(ReferenceEquals(result, savedRun) && Graph.NodeStates.Count == 2 && ImageView.Info?.PixelFormat == VisionPixelFormat.Gray8,
                "saved output image disappeared when switching nodes");
            return;
        }
        await SmokeEdgePointsAsync();
        flow!.Change(() => flow.Definition["inputs"]!["ExpectedCount"] = new JsonObject
            { ["type"] = "Int", ["required"] = false, ["default"] = 3 });
        runInputs["ExpectedCount"] = 3L;
        await BusyAsync(RunOnceAsync);
        var completed = result!;
        string runId = completed.RunId;
        string output = completed.GetOutput<JsonElement>("Distances").GetRawText();
        string definition = flow.Definition.ToJsonString();
        byte[] image;
        using (var frame = completed.GetOutputImage("DistanceImage")) image = frame.CopyPixels();
        void VerifyRetained(string action)
        {
            Require(ReferenceEquals(result, completed) && result?.RunId == runId,
                action + " discarded or re-executed the completed run");
            Require(Graph.NodeStates.Count == 4 && result!.GetOutput<JsonElement>("Distances").GetRawText() == output,
                action + " discarded node states or numeric results");
            using var retained = result!.GetOutputImage("DistanceImage");
            Require(retained.CopyPixels().SequenceEqual(image), action + " changed retained output pixels");
            Graph.Select("distance");
            Require(ImageView.Overlays?.GetArrayLength() > 0 && NodeState.Text.StartsWith("完成", StringComparison.Ordinal),
                action + " lost the displayed annotations or completion state");
            VerifyEdgePoints();
        }
        for (int pass = 0; pass < 3; pass++)
        {
            foreach (string node in new[] { "edge", "line", "source", "distance" })
            {
                Graph.Select(node);
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Require(ReferenceEquals(result, completed), "selecting " + node + " cleared output");
            }
        }
        VerifyRetained("repeated selection");
        if (scenario == "output-retention-layout")
        {
            flow.MarkSaved();
            var before = flow.Positions["edge"];
            flow.Move("edge", new EditorPoint(before.X + .25, before.Y + .25));
            Require(flow.Dirty && flow.Definition.ToJsonString() == definition, "layout move was not layout-only");
            VerifyRetained("small click movement");
            flow.Move("edge", new EditorPoint(before.X + 80, before.Y + 30));
            VerifyRetained("node arrangement");
            flow.Undo(); VerifyRetained("undo layout");
            flow.Redo(); VerifyRetained("redo layout");
            // Reapplying the same effective algorithm parameter must also be a harmless no-op.
            flow.SetParameter("distance", "displayEveryNth", flow.Node("distance")["parameters"]!["displayEveryNth"]!.DeepClone());
            VerifyRetained("unchanged parameter");
            flow.SetParameter("distance", "displayEveryNth", JsonValue.Create(11));
            Require(result is null && Graph.NodeStates.Count == 0 && ImageView.Overlays is null,
                "a real algorithm edit retained stale results");
        }
        else
        {
            var edit = InspectorControls<TextBox>().Single(t => t.Tag as string == "run-input:ExpectedCount");
            void LoseFocus(TextBox box) => box.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, box, Graph)
                { RoutedEvent = Keyboard.LostKeyboardFocusEvent });
            LoseFocus(edit); VerifyRetained("unchanged runtime input focus");
            edit = InspectorControls<TextBox>().Single(t => t.Tag as string == "run-input:ExpectedCount");
            edit.Text = "003"; LoseFocus(edit); VerifyRetained("equivalent runtime input formatting");
            edit = InspectorControls<TextBox>().Single(t => t.Tag as string == "run-input:ExpectedCount");
            edit.Text = "4"; LoseFocus(edit);
            Require(result is null && Graph.NodeStates.Count == 0, "changed runtime input retained stale results");
            Require((long)runInputs["ExpectedCount"]! == 4, "changed runtime input was not saved");
            await BusyAsync(RunOnceAsync);
            string reopened = CapturePath + ".vrecipe";
            Directory.CreateDirectory(reopened);
            File.WriteAllText(Path.Combine(reopened, "recipe.json"), VisionEngine.SealRecipe(flow.Definition.ToJsonString()));
            LoadRecipe(reopened);
            Require(result is null && Convert.ToInt64(runInputs["ExpectedCount"]) == 3,
                "reopening an identical recipe retained results for the old runtime inputs");
        }
        await BusyAsync(RunOnceAsync); VerifyEdgePoints();
    }
}
