using JPVision.Editor.Core;
using System.Text.Json.Nodes;
using Xunit;

namespace JPVision.Tests;

public sealed class TemplateBankTests
{
    private static JsonObject Model() => new()
    {
        ["width"] = 8, ["height"] = 8,
        ["grayBase64"] = Convert.ToBase64String(Enumerable.Range(0, 64).Select(i => (byte)(i * 4)).ToArray())
    };

    [Fact]
    public void LegacyUpgradeAndReturnedModelsNeverMutateTheOriginalRecipe()
    {
        var original = Model(); string before = original.ToJsonString();
        var entries = TemplateBank.Read(original);
        Assert.Equal("template1", entries.Single().Id);
        entries[0].Model["width"] = 123;
        var normalized = TemplateBank.Normalize(original);
        normalized["templates"]![0]!["model"]!["height"] = 123;
        Assert.Equal(before, original.ToJsonString());
        Assert.Single(TemplateBank.Read(original));
        Assert.Empty(TemplateBank.Read(new JsonObject()));
    }

    [Fact]
    public void EditingPreservesIdentityAndUndoSnapshotsIncludingNestedPixels()
    {
        var bank = TemplateBank.Add(Model(), Model()); string before = bank.ToJsonString();
        var entries = TemplateBank.Read(bank);
        string id = entries[1].Id;
        var renamed = TemplateBank.Rename(bank, id, "较大的工件");
        var disabled = TemplateBank.SetEnabled(renamed, id, false);
        var replacement = Model(); replacement["grayBase64"] = Convert.ToBase64String(Enumerable.Range(0, 64).Select(i => (byte)(255 - i * 4)).ToArray());
        var replaced = TemplateBank.Replace(disabled, id, replacement);
        replacement["width"] = 500;
        var selected = TemplateBank.Read(replaced)[1];
        Assert.Equal(id, selected.Id); Assert.Equal("较大的工件", selected.Name); Assert.False(selected.Enabled);
        Assert.Equal(8, selected.Model["width"]!.GetValue<int>());
        Assert.Equal(before, bank.ToJsonString());
        var removed = TemplateBank.Remove(replaced, entries[0].Id);
        Assert.Single(TemplateBank.Read(removed));
        Assert.Empty(TemplateBank.Read(TemplateBank.Remove(removed, id)));
    }

    [Fact]
    public void ImportAppendsAndResolvesIdentityCollisionsWithoutOverwritingModels()
    {
        var existing = TemplateBank.Normalize(Model());
        string before = existing.ToJsonString();
        var merged = TemplateBank.Import(existing, JsonNode.Parse(before)!.AsObject());
        var entries = TemplateBank.Read(merged);
        Assert.Equal(2, entries.Count);
        Assert.Equal("template1", entries[0].Id); Assert.NotEqual(entries[0].Id, entries[1].Id);
        Assert.Equal(before, existing.ToJsonString());
        var roundTrip = TemplateBank.Read(JsonNode.Parse(merged.ToJsonString())!.AsObject());
        Assert.Equal(entries.Select(item => item.Id), roundTrip.Select(item => item.Id));
        Assert.Equal(entries.Select(item => item.Model.ToJsonString()), roundTrip.Select(item => item.Model.ToJsonString()));
    }

    [Fact]
    public void FullBankLimitChecksBeforeAnyMutationAndCountsDisabledTemplates()
    {
        var bank = new JsonObject();
        for (int i = 0; i < TemplateBank.MaximumTemplates; i++) bank = TemplateBank.Add(bank, Model());
        bank = TemplateBank.SetEnabled(bank, TemplateBank.Read(bank)[0].Id, false);
        string before = bank.ToJsonString();
        Assert.Throws<ArgumentException>(() => TemplateBank.Add(bank, Model()));
        Assert.Throws<ArgumentException>(() => TemplateBank.Import(bank, Model()));
        Assert.Equal(before, bank.ToJsonString());
    }

    [Theory]
    [InlineData("outer-field")] [InlineData("duplicate-id")] [InlineData("invalid-id")] [InlineData("long-id")]
    [InlineData("missing-name")] [InlineData("empty-name")] [InlineData("long-utf8-name")]
    [InlineData("enabled-type")] [InlineData("unknown-entry-field")] [InlineData("disabled-invalid-model")]
    public void BankSchemaValidatesEveryStoredEntry(string invalid)
    {
        var bank = TemplateBank.Add(Model(), Model());
        var entries = bank["templates"]!.AsArray(); var item = entries[1]!.AsObject();
        switch (invalid)
        {
            case "outer-field": bank["other"] = true; break;
            case "duplicate-id": item["id"] = entries[0]!["id"]!.DeepClone(); break;
            case "invalid-id": item["id"] = "template / 1"; break;
            case "long-id": item["id"] = new string('a', 65); break;
            case "missing-name": item.Remove("name"); break;
            case "empty-name": item["name"] = "  "; break;
            case "long-utf8-name": item["name"] = new string('模', 43); break;
            case "enabled-type": item["enabled"] = "yes"; break;
            case "unknown-entry-field": item["unknown"] = 3; break;
            case "disabled-invalid-model": item["enabled"] = false; item["model"]!["width"] = 0; break;
        }
        Assert.Throws<ArgumentException>(() => TemplateBank.Read(bank));
    }

    [Theory]
    [InlineData("pixel-length")] [InlineData("pixel-base64")] [InlineData("mask-length")]
    [InlineData("mask-values")] [InlineData("mask-count")] [InlineData("contrast")]
    [InlineData("unknown-field")] [InlineData("size")] [InlineData("missing-field")]
    public void ModelsCheckPixelsMaskAndActualValidContrast(string invalid)
    {
        var model = Model();
        switch (invalid)
        {
            case "pixel-length": model["grayBase64"] = "AQID"; break;
            case "pixel-base64": model["grayBase64"] = new string('?', 88); break;
            case "mask-length": model["maskBase64"] = "AQID"; break;
            case "mask-values": model["maskBase64"] = Convert.ToBase64String(Enumerable.Repeat((byte)1, 64).ToArray()); break;
            case "mask-count": model["maskBase64"] = Convert.ToBase64String(Enumerable.Range(0, 64).Select(i => i < 15 ? (byte)255 : (byte)0).ToArray()); break;
            case "contrast": model["grayBase64"] = Convert.ToBase64String(new byte[64]); break;
            case "unknown-field": model["x"] = 0; break;
            case "size": model["width"] = 513; break;
            case "missing-field": model.Remove("height"); break;
        }
        Assert.Throws<ArgumentException>(() => TemplateBank.Read(model));
    }

    [Fact]
    public void DisabledOnlyBankCanBeEditedAndImportDoesNotPretendItIsExecutable()
    {
        var bank = TemplateBank.Normalize(Model());
        var disabled = TemplateBank.SetEnabled(bank, "template1", false);
        Assert.False(TemplateBank.Read(disabled).Single().Enabled);
        Assert.False(TemplateBank.Read(TemplateBank.Import(new JsonObject(), disabled)).Single().Enabled);
        Assert.True(TemplateBank.Read(TemplateBank.SetEnabled(disabled, "template1", true)).Single().Enabled);
    }
}
