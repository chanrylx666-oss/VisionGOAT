using JPVision.Editor.Core;
using JPVision.Sdk;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace JPVision.Studio;

public partial class MainWindow
{
    private async Task SmokeTemplateBankAsync()
    {
        const int width = 320, height = 180, patchWidth = 32, patchHeight = 24;
        byte[] a = new byte[patchWidth * patchHeight], b = new byte[a.Length];
        var random = new Random(3729); random.NextBytes(a); random.NextBytes(b);
        var pixels = Enumerable.Repeat((byte)15, width * height).ToArray();
        void Stamp(byte[] patch, int left, int top)
        {
            for (int y = 0; y < patchHeight; y++) Array.Copy(patch, y * patchWidth, pixels, (top + y) * width + left, patchWidth);
        }
        void SetSource()
        {
            source!.Dispose(); source = VisionFrame.CopyFrom(pixels, width, height, width, VisionPixelFormat.Gray8); ShowSource();
        }
        Stamp(a, 24, 40); Stamp(b, 170, 90); SetSource();
        flow!.Add("ExternalImage", 30, 90, "source"); flow.Add("FastTemplateMatch", 300, 90, "match");
        flow.Connect("source", "image", "match", "input"); Graph.Select("match");
        foreach (var position in new[] { (X: 24, Y: 40), (X: 170, Y: 90) })
        {
            await DrawRegionAsync("match", "rectangle", true);
            ImageView.CommitRegion(RoiRegion.Rectangle(position.X, position.Y, patchWidth, patchHeight), true);
            ImageView.EndEdit(); await BusyAsync(() => LearnTemplateAsync("match"));
        }
        var bank = TemplateBank.Read(StoredTemplate("match"));
        Require(bank.Count == 2 && bank.Select(item => item.Id).Distinct().Count() == 2, "learning a second template overwrote the first");
        string selectedId = bank[1].Id;
        var name = InspectorControls<TextBox>().Single(control => Equals(control.Tag, "template-bank:name"));
        name.Text = "工件 B";
        name.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, name, Graph) { RoutedEvent = Keyboard.LostKeyboardFocusEvent });
        Require(TemplateBank.Read(StoredTemplate("match"))[1].Name == "工件 B", "template rename was not persisted");
        await BusyAsync(() => LearnTemplateAsync("match", true));
        Require(TemplateBank.Read(StoredTemplate("match")).Count == 2 && TemplateBank.Read(StoredTemplate("match"))[1].Id == selectedId, "replacement changed template count or identity");
        var list = InspectorControls<ListBox>().Single(control => Equals(control.Tag, "template-bank:list"));
        string beforeSelect = flow.Definition.ToJsonString(); list.SelectedIndex = 0;
        Require(flow.Definition.ToJsonString() == beforeSelect, "template selection changed the recipe");
        InspectorControls<CheckBox>().Single(control => Equals(control.Tag, "template-bank:enabled")).IsChecked = false;
        Require(!TemplateBank.Read(StoredTemplate("match"))[0].Enabled, "template disabling was not persisted");
        InspectorControls<Button>().Single(control => Equals(control.Tag, "template-scale:variable")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(StoredTemplate("match")["templates"] is JsonArray && flow.Node("match")["parameters"]!["scaleMinimum"]!.GetValue<double>() == .9 && flow.Node("match")["parameters"]!["scaleMaximum"]!.GetValue<double>() == 1.1, "scale preset did not set both bounds");
        flow.Undo();
        Require(flow.Node("match")["parameters"]!["scaleMinimum"]!.GetValue<double>() == 1 && flow.Node("match")["parameters"]!["scaleMaximum"]!.GetValue<double>() == 1, "one undo did not restore the complete scale preset");
        // Import round trip uses the same validated append path as the file dialog.
        ImportTemplateModel("match", ModelCopy());
        Require(TemplateBank.Read(StoredTemplate("match")).Count == 3, "legacy template import replaced the bank");
        string imported = TemplateBank.Read(StoredTemplate("match"))[2].Id;
        selectedTemplates["match"] = imported; BuildInspector();
        InspectorControls<Button>().Single(control => Equals(control.Tag, "template-bank:delete")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(TemplateBank.Read(StoredTemplate("match")).Count == 2, "delete did not remove just the selected template");
        var persisted = JsonNode.Parse(flow.Definition.ToJsonString())!.AsObject();
        flow.Load(persisted.ToJsonString(), flow.LayoutJson().ToJsonString()); Graph.Select("match");
        Require(TemplateBank.Read(StoredTemplate("match"))[1].Name == "工件 B", "recipe round trip lost bank metadata");
        pixels = Enumerable.Repeat((byte)15, width * height).ToArray(); Stamp(b, 220, 100); SetSource();
        flow.SetParameter("match", "scoreThreshold", JsonValue.Create(.98));
        flow.Publish("Match", "match", "result", false); Graph.Select("match");
        await BusyAsync(RunOnceAsync);
        var packet = result!.GetNodeOutput<JsonElement>("match", "result");
        var selected = packet.GetProperty("measurement").GetProperty("selected");
        Require(result.Status == ExecutionStatus.Succeeded && packet.GetProperty("count").GetInt32() == 1 && selected.GetProperty("templateId").GetString() == selectedId && selected.GetProperty("templateName").GetString() == "工件 B", "bank did not output the winning template identity");
        Require(Math.Abs(selected.GetProperty("centerX").GetDouble() - 236) < 1 && Math.Abs(selected.GetProperty("centerY").GetDouble() - 112) < 1, "bank changed original image center coordinates");
        selectedTemplates["match"] = selectedId; BuildInspector();
        var completedResult = result;
        var completedDefinition = flow.Definition.ToJsonString();
        var completedList = InspectorControls<ListBox>().Single(control => Equals(control.Tag, "template-bank:list"));
        Require(completedList.Items.Count == 2, "bank UI lost stored models after running");
        completedList.SelectedIndex = 0;
        Require(ReferenceEquals(result, completedResult) && flow.Definition.ToJsonString() == completedDefinition && ImageView.Overlays?.GetArrayLength() > 0,
            "selecting another stored template erased completed outputs or changed the recipe");

        JsonObject ModelCopy() => (JsonObject)bank[1].Model.DeepClone();
    }
}
