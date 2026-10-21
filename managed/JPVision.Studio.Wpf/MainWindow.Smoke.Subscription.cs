using JPVision.Editor.Core;
using JPVision.Sdk;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;

namespace JPVision.Studio;

public partial class MainWindow
{
    private async Task SmokeParameterSubscriptionsAsync()
    {
        await SmokeAnnularUnwrapAsync();
        await UseSubscriptionDialogAsync(ShowVariablesDialog, dialog =>
        {
            var tabs = DialogControls<TabControl>(dialog).Single(t => Equals(t.Tag, "variables:tabs"));
            tabs.SelectedIndex = 0;
            Click("variables:add-global"); Set("variable:global:name", "OffsetX"); Set("variable:global:value", "0");
            tabs.SelectedIndex = 1;
            Click("variables:add-local"); Set("variable:local:name", "RingOuter"); Set("variable:local:value", "100");
            tabs.SelectedIndex = 2;
            Click("variables:add-function"); Set("function:name", "Offset"); Set("function:parameters", "x, dx"); Set("function:expression", "x + dx");
            Click("variables:save");
            void Click(string tag) => DialogControls<Button>(dialog).Single(b => Equals(b.Tag, tag)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            void Set(string tag, string value) => DialogControls<TextBox>(dialog).Single(t => Equals(t.Tag, tag)).Text = value;
        });
        Require(flow!.Definition["variables"]?["OffsetX"]?["type"]?.GetValue<string>() == "Double" && flow.Node("unwrap")["variables"]?["RingOuter"] is not null && flow.Definition["functions"]?["Offset"] is not null, "structured variables/functions dialog did not save");
        var circleX = flow.Spec("circle").ScalarFields.First(f => f.Port == "result" && f.Field.EndsWith(".centerX", StringComparison.Ordinal));
        var circleY = flow.Spec("circle").ScalarFields.First(f => f.Port == "result" && f.Field.EndsWith(".centerY", StringComparison.Ordinal));
        await Bind("roi.cx", 2, "Offset(cx, global.OffsetX)", circleX);
        await Bind("roi.cy", 1, null, circleY);
        await Bind("roi.outerRadius", 1, null, null, "本算子局部变量 · RingOuter");
        await Bind("roi.innerRadius", 2, "local.RingOuter - 60", null);
        Require(RoiHasSubscriptions("unwrap") && InspectorControls<TextBox>().Single(t => Equals(t.Tag, "annular:cx")).IsReadOnly, "subscribed coordinate is still editable");
        Require(!InspectorControls<Button>().Single(b => Equals(b.Tag, "annular:draw")).IsEnabled && !InspectorControls<Button>().Single(b => Equals(b.Tag, "annular:adjust")).IsEnabled, "drawing can overwrite ROI subscriptions");
        await BusyAsync(RunOnceAsync);
        var resolved = result!.Data.GetProperty("nodes").GetProperty("unwrap").GetProperty("resolvedParameters").GetProperty("roi");
        Require(Math.Abs(resolved.GetProperty("cx").GetDouble() - 180) < 1 && Math.Abs(resolved.GetProperty("cy").GetDouble() - 160) < 1, "circle center bindings did not convert original coordinates to input-local coordinates");
        Require(resolved.GetProperty("innerRadius").GetDouble() == 40 && resolved.GetProperty("outerRadius").GetDouble() == 100, "local variable/function expression was not resolved");
        flow.SetGlobalVariables(new JsonObject { ["OffsetX"] = new JsonObject { ["type"] = "Double", ["value"] = 10.0 } });
        await BusyAsync(RunOnceAsync);
        double current = result!.Data.GetProperty("nodes").GetProperty("unwrap").GetProperty("resolvedParameters").GetProperty("roi").GetProperty("cx").GetDouble();
        Require(Math.Abs(current - 190) < 1, "global offset did not update same-run subscribed center");
        Require(double.TryParse(InspectorControls<TextBox>().Single(t => Equals(t.Tag, "annular:cx")).Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double displayed) && Math.Abs(displayed - current) < .00001, "inspector is displaying fallback instead of actual resolved ROI");
        string saved = flow.Definition.ToJsonString(); flow.Load(saved); Graph.Select("unwrap"); await BusyAsync(RunOnceAsync);
        Require(flow.GetParameterPath("unwrap", "roi.cx")?["expression"]?.GetValue<string>() == "Offset(cx, global.OffsetX)", "saved/reloaded ROI lost expression subscription");
        InspectorTabs.SelectedIndex = 0;

        async Task Bind(string path, int modeIndex, string? expressionText, ResultFieldSpec? source, string? sourceLabel = null)
        {
            var button = InspectorControls<Button>().Single(b => Equals(b.Tag, "subscribe:" + path));
            await UseSubscriptionDialogAsync(() => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)), dialog =>
            {
                DialogControls<ComboBox>(dialog).Single(c => Equals(c.Tag, "binding:mode")).SelectedIndex = modeIndex;
                if (modeIndex == 1)
                {
                    var combo = DialogControls<ComboBox>(dialog).Single(c => Equals(c.Tag, "binding:source"));
                    Select(combo, sourceLabel ?? $"({source!.Port}.{source.Field})");
                }
                else
                {
                    DialogControls<TextBox>(dialog).Single(t => Equals(t.Tag, "binding:expression")).Text = expressionText!;
                    if (source is not null)
                    {
                        DialogControls<Button>(dialog).Single(b => Equals(b.Content, "＋ 添加结果别名")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        var alias = DialogControls<TextBox>(dialog).Single(t => Equals(t.ToolTip, "表达式中的别名")); alias.Text = "cx";
                        var combo = DialogControls<ComboBox>(dialog).Single(c => c.Tag is null); Select(combo, $"({source.Port}.{source.Field})");
                    }
                }
                DialogControls<Button>(dialog).Single(b => Equals(b.Tag, "binding:apply")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                void Select(ComboBox combo, string text)
                {
                    int index = combo.Items.Cast<object>().ToList().FindIndex(item => item.ToString()!.Contains(text, StringComparison.Ordinal));
                    Require(index >= 0, "subscription source not offered: " + text); combo.SelectedIndex = index;
                }
            });
            Require(IsDynamicParameter(flow.GetParameterPath("unwrap", path)), "subscription dialog did not apply " + path);
        }
    }

    private async Task UseSubscriptionDialogAsync(Action open, Action<Window> operate)
    {
        Exception? failure = null;
        var action = Dispatcher.BeginInvoke(new Action(() =>
        {
            var dialog = OwnedWindows.Cast<Window>().Single();
            try { operate(dialog); }
            catch (Exception ex) { failure = ex; }
            finally { if (dialog.IsVisible) dialog.Close(); }
        }));
        open(); await action;
        if (failure is not null) throw new InvalidOperationException("Subscription dialog smoke failed.", failure);
    }
    private static IEnumerable<T> DialogControls<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        IEnumerable<DependencyObject> children = root switch
        {
            Panel panel => panel.Children.Cast<DependencyObject>(),
            TabControl tabs => tabs.Items.Cast<DependencyObject>(),
            Decorator decorator when decorator.Child is not null => new[] { decorator.Child },
            ContentControl content when content.Content is DependencyObject child => new[] { child },
            _ => Array.Empty<DependencyObject>()
        };
        foreach (var child in children) foreach (var item in DialogControls<T>(child)) yield return item;
    }
}
