using JPVision.Editor.Core;
using JPVision.Sdk;
using Microsoft.Win32;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace JPVision.Studio;

public partial class MainWindow
{
    private readonly Dictionary<string, string> selectedTemplates = new();
    private bool UsesTemplateBank(string id) => flow!.Spec(id).Type is "FastTemplateMatch" or "FastLocate";
    private JsonObject StoredTemplate(string id) => flow!.Node(id)["parameters"]?["model"] as JsonObject ?? new();

    private string? SelectedTemplateId(string id, IReadOnlyList<TemplateBankItem> entries)
    {
        if (selectedTemplates.TryGetValue(id, out string? selected) && entries.Any(item => item.Id == selected)) return selected;
        selected = entries.FirstOrDefault()?.Id;
        if (selected is not null) selectedTemplates[id] = selected;
        else selectedTemplates.Remove(id);
        return selected;
    }

    private static BitmapSource TemplateThumbnail(JsonObject model)
    {
        int w = model["width"]!.GetValue<int>(), h = model["height"]!.GetValue<int>();
        byte[] pixels = Convert.FromBase64String(model["grayBase64"]!.GetValue<string>());
        if (model["maskBase64"] is JsonNode node)
        {
            var mask = Convert.FromBase64String(node.GetValue<string>());
            for (int i = 0; i < pixels.Length; i++) if (mask[i] == 0) pixels[i] = 0;
        }
        var image = BitmapSource.Create(w, h, 96, 96, PixelFormats.Gray8, null, pixels, w);
        image.Freeze();
        return image;
    }

    private void BuildTemplatePanel(string id)
    {
        if (UsesTemplateBank(id)) BuildTemplateBankPanel(id);
        else BuildSingleTemplatePanel(id);
    }

    private void BuildSingleTemplatePanel(string id)
    {
        Header(Parameters, "内置模板绘制");
        var model = StoredTemplate(id);
        if (model.Count > 0)
        {
            TemplateBank.ValidateModel(model);
            Parameters.Children.Add(new Border { BorderBrush = (Brush)FindResource("Border"), BorderThickness = new Thickness(1), Padding = new Thickness(5), Margin = new Thickness(0, 0, 0, 8), Height = 92, Child = new Image { Source = TemplateThumbnail(model), Stretch = Stretch.Uniform } });
            Parameters.Children.Add(Note($"已学习 · {model["width"]} × {model["height"]} 像素 · {(model.ContainsKey("maskBase64") ? "含形状掩膜" : "矩形模板")}"));
        }
        else Parameters.Children.Add(Note("尚未学习。模板须包含物品边缘和少量背景，纯色区域无法匹配。"));
        BuildTemplateDrawingControls(id, false);
        BuildTemplateFileControls(id, model.Count > 0);
        var clear = new Button { Content = "清除模板", HorizontalAlignment = HorizontalAlignment.Left };
        clear.Click += (_, _) => Safe(() => StoreTemplate(id, new JsonObject()));
        Parameters.Children.Add(clear);
        Parameters.Children.Add(Note("模板区域与检测 ROI 独立。灰度模板匹配可设置角度与等比例缩放范围。"));
    }

    private void BuildTemplateBankPanel(string id)
    {
        Header(Parameters, "多模板库");
        var entries = TemplateBank.Read(StoredTemplate(id));
        string? selectedId = SelectedTemplateId(id, entries);
        Parameters.Children.Add(Note($"已保存 {entries.Count} / {TemplateBank.MaximumTemplates} 个 · 启用 {entries.Count(item => item.Enabled)} 个。搜索范围可选全图或检测 ROI；所有启用模板与尺度共同比较，只输出分数最高的一个。", false));
        var list = new ListBox { Tag = "template-bank:list", MaxHeight = 220, Margin = new Thickness(0, 0, 0, 8) };
        foreach (var item in entries)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new Image { Source = TemplateThumbnail(item.Model), Width = 58, Height = 46, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 9, 0) });
            var description = new StackPanel();
            description.Children.Add(new TextBlock { Text = item.Name, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 160 });
            description.Children.Add(new TextBlock { Text = $"{item.Model["width"]} × {item.Model["height"]} · {(item.Enabled ? "启用" : "停用")}", Foreground = (Brush)FindResource("Muted"), FontSize = 11 });
            row.Children.Add(description);
            var option = new ListBoxItem { Tag = item.Id, Content = row, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            list.Items.Add(option);
            if (item.Id == selectedId) list.SelectedItem = option;
        }
        list.SelectionChanged += (_, _) =>
        {
            if (buildingInspector || list.SelectedItem is not ListBoxItem { Tag: string next }) return;
            selectedTemplates[id] = next;
            BuildInspector(); // Selection is editor state only: do not invalidate results or add undo steps.
        };
        Parameters.Children.Add(list);
        if (entries.FirstOrDefault(item => item.Id == selectedId) is { } selected)
        {
            Parameters.Children.Add(Note("选中模板名称"));
            var name = new TextBox { Tag = "template-bank:name", Text = selected.Name, Margin = new Thickness(0, 0, 0, 7) };
            void CommitName()
            {
                if (!buildingInspector && name.Text != selected.Name)
                    Safe(() => StoreTemplate(id, TemplateBank.Rename(StoredTemplate(id), selected.Id, name.Text)));
            }
            name.LostKeyboardFocus += (_, _) => CommitName();
            name.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { CommitName(); e.Handled = true; } };
            Parameters.Children.Add(name);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var enabled = new CheckBox { Tag = "template-bank:enabled", Content = "参与匹配", IsChecked = selected.Enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            void Toggle() { if (!buildingInspector) Safe(() => StoreTemplate(id, TemplateBank.SetEnabled(StoredTemplate(id), selected.Id, enabled.IsChecked == true))); }
            enabled.Checked += (_, _) => Toggle(); enabled.Unchecked += (_, _) => Toggle();
            var delete = new Button { Tag = "template-bank:delete", Content = "删除选中", Padding = new Thickness(9, 5, 9, 5) };
            delete.Click += (_, _) => Safe(() => StoreTemplate(id, TemplateBank.Remove(StoredTemplate(id), selected.Id)));
            row.Children.Add(enabled); row.Children.Add(delete); Parameters.Children.Add(row);
        }
        else Parameters.Children.Add(Note("绘制模板区域后点击“学习为新模板”。可依次保存不同工件、外观或尺寸的模板。"));
        if (entries.Count > 0 && entries.All(item => !item.Enabled)) Parameters.Children.Add(Note("当前没有启用模板，执行前请至少启用一个。", false));
        BuildTemplateDrawingControls(id, true);
        BuildTemplateFileControls(id, entries.Count > 0);
        Header(Parameters, "模板尺寸搜索");
        var presets = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var preset in new[] { (Label: "固定尺寸 1:1", Min: 1.0, Max: 1.0, Tag: "fixed"), (Label: "尺寸浮动 ±10%", Min: .9, Max: 1.1, Tag: "variable") })
        {
            var button = new Button { Content = preset.Label, Tag = "template-scale:" + preset.Tag, Padding = new Thickness(6, 5, 6, 5) };
            button.Click += (_, _) => Safe(() => SetTemplateScalePreset(id, preset.Min, preset.Max));
            presets.Children.Add(button);
        }
        Parameters.Children.Add(presets);
        Parameters.Children.Add(Note("下方可精调最小／最大倍率与步长。1 为教学尺寸；0.9 为缩小到 90%，1.1 为放大到 110%。等比例尺寸搜索不改变分数阈值；步长越小越慢。所有启用模板 × 尺度最多 121 次搜索。"));
    }

    private void BuildTemplateDrawingControls(string id, bool bank)
    {
        var shape = ShapeChoice(flow!.TemplateRegions.GetValueOrDefault(id, RoiRegion.None).Shape, advanced: false);
        Parameters.Children.Add(shape);
        var row = new WrapPanel();
        var draw = new Button { Tag = "template-bank:draw", Content = "绘制模板", Padding = new Thickness(9, 6, 9, 6) };
        var learn = new Button { Tag = "template-bank:learn", Content = bank ? "学习为新模板" : "学习模板", Style = (Style)FindResource("PrimaryButton"), Padding = new Thickness(9, 6, 9, 6) };
        draw.Click += async (_, _) => await DrawRegionAsync(id, ShapeValue(shape), true);
        learn.Click += async (_, _) => await BusyAsync(() => LearnTemplateAsync(id));
        if (bank) learn.IsEnabled = TemplateBank.Read(StoredTemplate(id)).Count < TemplateBank.MaximumTemplates;
        row.Children.Add(draw); row.Children.Add(learn);
        if (bank)
        {
            var replace = new Button { Tag = "template-bank:replace", Content = "替换选中", Padding = new Thickness(9, 6, 9, 6), IsEnabled = TemplateBank.Read(StoredTemplate(id)).Count > 0 };
            replace.Click += async (_, _) => await BusyAsync(() => LearnTemplateAsync(id, true));
            row.Children.Add(replace);
        }
        Parameters.Children.Add(row);
    }

    private void BuildTemplateFileControls(string id, bool hasModel)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 0) };
        var import = new Button { Tag = "template-bank:import", Content = UsesTemplateBank(id) ? "导入并追加" : "导入", Padding = new Thickness(9, 5, 9, 5) };
        var export = new Button { Tag = "template-bank:export", Content = UsesTemplateBank(id) ? "导出模板库" : "导出", Padding = new Thickness(9, 5, 9, 5), IsEnabled = hasModel };
        import.Click += (_, _) => Safe(() => ImportTemplate(id)); export.Click += (_, _) => Safe(() => ExportTemplate(id));
        row.Children.Add(import); row.Children.Add(export); Parameters.Children.Add(row);
    }

    private void StoreTemplate(string id, JsonObject model)
    {
        retainInput = true;
        try { flow!.SetParameter(id, "model", model); }
        finally { retainInput = false; }
    }

    private void SetTemplateScalePreset(string id, double minimum, double maximum)
    {
        retainInput = true;
        try
        {
            flow!.Change(() =>
            {
                var values = flow.Node(id)["parameters"]!;
                values["scaleMinimum"] = minimum; values["scaleMaximum"] = maximum; values["scaleStep"] = .05;
            });
        }
        finally { retainInput = false; }
    }

    private Task LearnTemplateAsync(string id) => LearnTemplateAsync(id, false);
    private async Task LearnTemplateAsync(string id, bool replaceSelected)
    {
        if (flow is null || !flow.TemplateRegions.TryGetValue(id, out var region) || region.Shape == "none")
            throw new InvalidOperationException("请先在模块内绘制模板区域。");
        await PrepareInputAsync(id);
        var info = inputFrame!.Info;
        var model = TemplateLearning.Learn(inputFrame.CopyPixels(), info.Width, info.Height, info.Stride, info.PixelFormat, region);
        if (UsesTemplateBank(id))
        {
            JsonObject bank;
            if (replaceSelected)
            {
                string selected = SelectedTemplateId(id, TemplateBank.Read(StoredTemplate(id))) ?? throw new InvalidOperationException("请先选择要替换的模板。");
                bank = TemplateBank.Replace(StoredTemplate(id), selected, model);
            }
            else
            {
                bank = TemplateBank.Add(StoredTemplate(id), model);
                selectedTemplates[id] = bank["templates"]!.AsArray().Last()!["id"]!.GetValue<string>();
            }
            StoreTemplate(id, bank);
        }
        else StoreTemplate(id, model);
        ImageView.Template = region; ImageView.EditEnabled = true; ImageView.InvalidateVisual();
        Log($"模板已{(replaceSelected ? "替换" : "学习")} · {model["width"]} × {model["height"]} · 可直接执行");
    }

    private void ImportTemplate(string id)
    {
        EnsureEditor();
        var dialog = new OpenFileDialog { Filter = "模板或图像|*.vtemplate;*.json;*.png;*.bmp;*.jpg;*.jpeg;*.tif;*.tiff" };
        if (dialog.ShowDialog(this) != true) return;
        if (new FileInfo(dialog.FileName).Length > TemplateBank.MaximumFileBytes) throw new InvalidOperationException("模板文件不能超过 8 MiB。");
        JsonObject model;
        if (Path.GetExtension(dialog.FileName).ToLowerInvariant() is ".json" or ".vtemplate")
            model = JsonNode.Parse(File.ReadAllText(dialog.FileName)) as JsonObject ?? throw new ArgumentException("模板文件需要 JSON 对象。");
        else
        {
            using var stream = File.OpenRead(dialog.FileName);
            var bmp = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            if (bmp.PixelWidth > 512 || bmp.PixelHeight > 512) throw new InvalidOperationException("导入模板图像宽高不能超过 512 像素。");
            var gray = new FormatConvertedBitmap(bmp, PixelFormats.Gray8, null, 0);
            byte[] bytes = new byte[gray.PixelWidth * gray.PixelHeight]; gray.CopyPixels(bytes, gray.PixelWidth, 0);
            model = TemplateLearning.Learn(bytes, gray.PixelWidth, gray.PixelHeight, gray.PixelWidth, VisionPixelFormat.Gray8, RoiRegion.Rectangle(0, 0, gray.PixelWidth, gray.PixelHeight));
        }
        ImportTemplateModel(id, model);
        Log(UsesTemplateBank(id) ? "模板已校验并追加到模板库" : "模板已导入");
    }

    private void ImportTemplateModel(string id, JsonObject model)
    {
        if (UsesTemplateBank(id))
        {
            var bank = TemplateBank.Import(StoredTemplate(id), model);
            selectedTemplates[id] = bank["templates"]!.AsArray().Last()!["id"]!.GetValue<string>();
            StoreTemplate(id, bank);
        }
        else
        {
            if (TemplateBank.IsBank(model)) throw new ArgumentException("灰度模板匹配使用单模板；多模板文件请导入快速匹配或快速定位。");
            TemplateBank.ValidateModel(model); StoreTemplate(id, model);
        }
    }

    private void ExportTemplate(string id)
    {
        var model = UsesTemplateBank(id) ? TemplateBank.Normalize(StoredTemplate(id)) : StoredTemplate(id);
        if (UsesTemplateBank(id) ? TemplateBank.Read(model).Count == 0 : model.Count == 0) throw new InvalidOperationException("请先学习模板。");
        var dialog = new SaveFileDialog { Filter = "JP-Vision 模板|*.vtemplate", FileName = UsesTemplateBank(id) ? "templates.vtemplate" : "object.vtemplate" };
        if (dialog.ShowDialog(this) == true) WriteAtomic(dialog.FileName, model.ToJsonString());
    }
}
