using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;

namespace JPVision.Studio;

public partial class MainWindow
{
    private delegate bool RecipeDialogWindowVisitor(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll", EntryPoint = "EnumThreadWindows")]
    private static extern bool EnumerateRecipeDialogWindows(uint thread, RecipeDialogWindowVisitor visitor, IntPtr parameter);
    [DllImport("kernel32.dll", EntryPoint = "GetCurrentThreadId")]
    private static extern uint RecipeDialogThreadId();
    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
    private static extern uint RecipeDialogWindowProcess(IntPtr window, out uint processId);
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RecipeDialogClassName(IntPtr window, StringBuilder name, int capacity);
    [DllImport("user32.dll", EntryPoint = "PostMessageW", ExactSpelling = true)]
    private static extern bool CloseRecipeDialogWindow(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    private void SmokeRecipeOpenDialog()
    {
        string before = flow!.Definition.ToJsonString();
        string title = RecipeTitle.Text;
        string? directory = recipeDirectory;
        int cancelled = 0;
        uint thread = RecipeDialogThreadId();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        timer.Tick += (_, _) => EnumerateRecipeDialogWindows(thread, (window, _) =>
        {
            RecipeDialogWindowProcess(window, out uint process);
            var name = new StringBuilder(64);
            RecipeDialogClassName(window, name, name.Capacity);
            // Only dismiss this smoke process's own native dialog on the owning UI thread.
            if (process == Environment.ProcessId && name.ToString() == "#32770")
            {
                cancelled++;
                CloseRecipeDialogWindow(window, 0x0010, IntPtr.Zero, IntPtr.Zero);
            }
            return true;
        }, IntPtr.Zero);
        timer.Start();
        try
        {
            var dialog = CreateRecipeOpenDialog();
            Require(dialog.ShowDialog(this) == false && cancelled > 0, "recipe dialog did not open and cancel normally");
        }
        finally { timer.Stop(); }
        Require(flow.Definition.ToJsonString() == before && recipeDirectory == directory && RecipeTitle.Text == title,
            "cancelling the recipe dialog changed the current recipe");

        string sample = Path.Combine(AppContext.BaseDirectory, "samples", "recipes", "circles.vrecipe");
        string manifest = Path.Combine(sample, "recipe.json");
        using var expected = JsonDocument.Parse(File.ReadAllText(manifest));
        int nodes = expected.RootElement.GetProperty("nodes").GetArrayLength();
        foreach (string path in new[] { sample, manifest })
        {
            LoadRecipe(path);
            Require(flow.Nodes.Count == nodes && nodes > 0 && !flow.Dirty && recipeDirectory == sample &&
                RecipeTitle.Text == "circles.vrecipe", "recipe folder or JSON did not load cleanly");
        }
        StatusText.Text = "● 配方打开验证通过 · 文件窗口、取消、文件夹和 JSON";
        Log("配方打开验证通过：Windows 文件窗口正常，取消不改动配方，目录和 JSON 均可加载。");
    }
}
