using Xunit;
using System.Text.Json;
using EQSwitch.Config;

namespace EQSwitch.Tests;

public class ConfigManagerTests
{
    [Fact]
    public void AppConfig_RoundTrip_Serialization()
    {
        var original = new AppConfig
        {
            EQPath = @"D:\Games\EQ",
            PollingIntervalMs = 750,
            Layout = new WindowLayout { Columns = 3, Rows = 2, TopOffset = 25 },
            Affinity = new AffinityConfig { ActivePriority = "High" }
        };

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        var json = JsonSerializer.Serialize(original, options);
        var restored = JsonSerializer.Deserialize<AppConfig>(json, options)!;

        Assert.Equal(original.EQPath, restored.EQPath);
        Assert.Equal(original.PollingIntervalMs, restored.PollingIntervalMs);
        Assert.Equal(original.Layout.Columns, restored.Layout.Columns);
        Assert.Equal(original.Layout.Rows, restored.Layout.Rows);
        Assert.Equal(original.Layout.TopOffset, restored.Layout.TopOffset);
        Assert.Equal(original.Affinity.ActivePriority, restored.Affinity.ActivePriority);
    }

    [Fact]
    public void AppConfig_WithCharacters_RoundTrips()
    {
        var original = new AppConfig();
        original.Characters.Add(new CharacterProfile
        {
            Name = "Gandalf",
            Class = "Wizard",
            SlotIndex = 0,
            PriorityOverride = "High"
        });

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        var json = JsonSerializer.Serialize(original, options);
        var restored = JsonSerializer.Deserialize<AppConfig>(json, options)!;

        Assert.Single(restored.Characters);
        Assert.Equal("Gandalf", restored.Characters[0].Name);
        Assert.Equal("Wizard", restored.Characters[0].Class);
        Assert.Equal("High", restored.Characters[0].PriorityOverride);
    }

    [Fact]
    public void AppConfig_NullPriorityOverride_NotSerialized()
    {
        var config = new AppConfig();
        config.Characters.Add(new CharacterProfile { Name = "Test", PriorityOverride = null });

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        var json = JsonSerializer.Serialize(config, options);
        Assert.DoesNotContain("affinityOverride", json);
    }

    [Fact]
    public void AppConfig_CorruptJson_DeserializesToNull()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AppConfig>("not json at all", options));
    }

    [Fact]
    public void AppConfig_EmptyJson_DeserializesToDefaults()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var config = JsonSerializer.Deserialize<AppConfig>("{}", options)!;

        Assert.True(config.IsFirstRun);
        Assert.Equal("eqgame", config.EQProcessName);
        Assert.Equal(2, config.Layout.Columns);
    }

    [Fact]
    public void AppConfig_PipSavedPositions_RoundTrips()
    {
        var config = new AppConfig();
        config.Pip.SavedPositions.Add(new[] { 100, 200 });
        config.Pip.SavedPositions.Add(new[] { 300, 400 });

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        var json = JsonSerializer.Serialize(config, options);
        var restored = JsonSerializer.Deserialize<AppConfig>(json, options)!;

        Assert.Equal(2, restored.Pip.SavedPositions.Count);
        Assert.Equal(new[] { 100, 200 }, restored.Pip.SavedPositions[0]);
        Assert.Equal(new[] { 300, 400 }, restored.Pip.SavedPositions[1]);
    }
}
