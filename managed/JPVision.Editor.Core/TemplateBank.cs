using System.Text;
using System.Text.Json.Nodes;

namespace JPVision.Editor.Core;

public sealed record TemplateBankItem(string Id, string Name, bool Enabled, JsonObject Model);

/// <summary>Immutable recipe edits for fast matching's template bank. Legacy models remain importable.</summary>
public static class TemplateBank
{
    public const int MaximumTemplates = 8;
    public const long MaximumFileBytes = 8 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static bool IsBank(JsonObject stored) => stored.ContainsKey("templates");

    public static IReadOnlyList<TemplateBankItem> Read(JsonObject stored)
    {
        if (stored.Count == 0) return Array.Empty<TemplateBankItem>();
        if (!IsBank(stored))
        {
            ValidateModel(stored);
            return new[] { new TemplateBankItem("template1", "模板1", true, (JsonObject)stored.DeepClone()) };
        }
        if (stored.Count != 1 || stored["templates"] is not JsonArray templates || templates.Count > MaximumTemplates)
            throw new ArgumentException($"模板库只能包含 templates 数组，最多 {MaximumTemplates} 个模板。");
        var result = new List<TemplateBankItem>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in templates)
        {
            if (node is not JsonObject item || item.Count != 4 || item.Any(p => p.Key is not ("id" or "name" or "enabled" or "model")) ||
                item["id"] is not JsonValue idValue || !idValue.TryGetValue<string>(out var id) ||
                item["name"] is not JsonValue nameValue || !nameValue.TryGetValue<string>(out var name) ||
                item["enabled"] is not JsonValue enabledValue || !enabledValue.TryGetValue<bool>(out bool enabled) || item["model"] is not JsonObject model)
                throw new ArgumentException("模板条目需要 id、name、enabled 和 model 四个字段。");
            if (id.Length is < 1 or > 64 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')) || !ids.Add(id))
                throw new ArgumentException("模板 ID 须为 1～64 个字母、数字、下划线或连字符，并且不能重复。");
            ValidateName(name);
            ValidateModel(model); // Disabled templates must also be safe and complete for later enabling.
            result.Add(new(id, name, enabled, (JsonObject)model.DeepClone()));
        }
        return result;
    }

    public static JsonObject Normalize(JsonObject stored) => Write(Read(stored));

    public static JsonObject Add(JsonObject stored, JsonObject model, string? name = null)
    {
        ValidateModel(model);
        var entries = Read(stored).ToList();
        if (entries.Count >= MaximumTemplates) throw new ArgumentException($"最多可保存 {MaximumTemplates} 个模板，请先删除不需要的模板。");
        name ??= NextName(entries);
        ValidateName(name);
        entries.Add(new(NewId(), name, true, model));
        return Write(entries);
    }

    public static JsonObject Replace(JsonObject stored, string id, JsonObject model)
    {
        ValidateModel(model);
        return Edit(stored, id, item => item with { Model = model });
    }

    public static JsonObject Rename(JsonObject stored, string id, string name)
    {
        ValidateName(name);
        return Edit(stored, id, item => item with { Name = name });
    }

    public static JsonObject SetEnabled(JsonObject stored, string id, bool enabled) => Edit(stored, id, item => item with { Enabled = enabled });

    public static JsonObject Remove(JsonObject stored, string id)
    {
        var entries = Read(stored).ToList();
        if (entries.RemoveAll(item => item.Id == id) == 0) throw new ArgumentException("选中的模板已不存在。");
        return Write(entries);
    }

    /// <summary>Append a model or bank without overwriting existing models; imported colliding IDs get new stable IDs.</summary>
    public static JsonObject Import(JsonObject stored, JsonObject imported)
    {
        var entries = Read(stored).ToList();
        var incoming = Read(imported);
        if (incoming.Count == 0) throw new ArgumentException("导入文件没有模板。");
        if (entries.Count + incoming.Count > MaximumTemplates) throw new ArgumentException($"导入后超过 {MaximumTemplates} 个模板，请先删除不需要的模板。");
        var ids = entries.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var item in incoming)
        {
            string id = item.Id;
            while (!ids.Add(id)) id = NewId();
            entries.Add(item with { Id = id });
        }
        return Write(entries);
    }

    public static void ValidateModel(JsonObject model)
    {
        if (model.Any(p => p.Key is not ("width" or "height" or "grayBase64" or "maskBase64")) ||
            model["width"] is not JsonValue widthValue || !widthValue.TryGetValue<int>(out int width) ||
            model["height"] is not JsonValue heightValue || !heightValue.TryGetValue<int>(out int height) ||
            width is < 4 or > 512 || height is < 4 or > 512)
            throw new ArgumentException("模板宽高须为 4～512，且不能包含未知字段。");
        int size = width * height;
        byte[] pixels = Decode(model["grayBase64"], size, "模板像素");
        byte[]? mask = model.ContainsKey("maskBase64") ? Decode(model["maskBase64"], size, "模板掩膜") : null;
        if (mask is not null && mask.Any(value => value is not (0 or 255))) throw new ArgumentException("模板掩膜只允许 0／255。");
        double sum = 0, squares = 0;
        int count = 0;
        for (int i = 0; i < size; i++)
        {
            if (mask is not null && mask[i] == 0) continue;
            count++;
            sum += pixels[i];
            squares += pixels[i] * pixels[i];
        }
        if (count < 16 || squares / count - Math.Pow(sum / count, 2) < 1)
            throw new ArgumentException("模板至少需要 16 个有效像素和灰度对比；请包含物品边缘及少量背景。");
    }

    private static byte[] Decode(JsonNode? value, int size, string label)
    {
        if (value is not JsonValue textValue || !textValue.TryGetValue<string>(out var text) || text.Length != (size + 2) / 3 * 4)
            throw new ArgumentException(label + "长度不匹配。");
        var bytes = new byte[size];
        if (!Convert.TryFromBase64String(text, bytes, out int written) || written != size)
            throw new ArgumentException(label + "编码无效。");
        return bytes;
    }

    private static void ValidateName(string name)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(name) || StrictUtf8.GetByteCount(name) > 128)
                throw new ArgumentException("模板名称不能为空，且 UTF-8 长度不能超过 128 字节。");
        }
        catch (EncoderFallbackException) { throw new ArgumentException("模板名称包含无效字符。"); }
    }

    private static JsonObject Edit(JsonObject stored, string id, Func<TemplateBankItem, TemplateBankItem> update)
    {
        var entries = Read(stored).ToList();
        int index = entries.FindIndex(item => item.Id == id);
        if (index < 0) throw new ArgumentException("选中的模板已不存在。");
        entries[index] = update(entries[index]);
        return Write(entries);
    }

    private static string NewId() => "t_" + Guid.NewGuid().ToString("N");
    private static string NextName(IEnumerable<TemplateBankItem> entries)
    {
        var names = entries.Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        int index = 1;
        while (names.Contains("模板" + index)) index++;
        return "模板" + index;
    }

    private static JsonObject Write(IEnumerable<TemplateBankItem> entries) => new()
    {
        ["templates"] = new JsonArray(entries.Select(item => (JsonNode)new JsonObject
        {
            ["id"] = item.Id, ["name"] = item.Name, ["enabled"] = item.Enabled, ["model"] = item.Model.DeepClone()
        }).ToArray())
    };
}
