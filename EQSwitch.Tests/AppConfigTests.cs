using Xunit;
using EQSwitch.Config;

namespace EQSwitch.Tests;

public class AppConfigTests
{
    [Fact]
    public void Validate_DefaultConfig_UnchangedByValidation()
    {
        var config = new AppConfig();
        config.Validate();

        Assert.Equal("eqgame", config.EQProcessName);
        Assert.Equal(500, config.PollingIntervalMs);
        Assert.Equal(2, config.Layout.Columns);
        Assert.Equal(2, config.Layout.Rows);
        Assert.Equal(0, config.Layout.TargetMonitor);
        Assert.Equal(0, config.Layout.TopOffset);
        Assert.Equal("High", config.Affinity.ActivePriority);
        Assert.Equal(3, config.Affinity.LaunchRetryCount);
        Assert.Equal(2000, config.Affinity.LaunchRetryDelayMs);
        Assert.Equal(2, config.Launch.NumClients);
        Assert.Equal(3000, config.Launch.LaunchDelayMs);
        Assert.Equal(15000, config.Launch.FixDelayMs);
        Assert.Equal(245, config.Pip.Opacity);
        Assert.Equal(3, config.Pip.MaxWindows);
        Assert.Equal(320, config.Pip.CustomWidth);
        Assert.Equal(240, config.Pip.CustomHeight);
    }

    [Fact]
    public void Validate_ClampsPollingInterval_TooLow()
    {
        var config = new AppConfig { PollingIntervalMs = 10 };
        config.Validate();
        Assert.Equal(100, config.PollingIntervalMs);
    }

    [Fact]
    public void Validate_ClampsPollingInterval_TooHigh()
    {
        var config = new AppConfig { PollingIntervalMs = 99999 };
        config.Validate();
        Assert.Equal(10000, config.PollingIntervalMs);
    }

    [Fact]
    public void Validate_ClampsLayoutColumns()
    {
        var config = new AppConfig();
        config.Layout.Columns = 0;
        config.Validate();
        Assert.Equal(1, config.Layout.Columns);

        config.Layout.Columns = 10;
        config.Validate();
        Assert.Equal(4, config.Layout.Columns);
    }

    [Fact]
    public void Validate_ClampsLayoutRows()
    {
        var config = new AppConfig();
        config.Layout.Rows = -5;
        config.Validate();
        Assert.Equal(1, config.Layout.Rows);

        config.Layout.Rows = 100;
        config.Validate();
        Assert.Equal(4, config.Layout.Rows);
    }

    [Fact]
    public void Validate_ClampsTopOffset()
    {
        var config = new AppConfig();
        config.Layout.TopOffset = -500;
        config.Validate();
        Assert.Equal(-200, config.Layout.TopOffset);

        config.Layout.TopOffset = 500;
        config.Validate();
        Assert.Equal(200, config.Layout.TopOffset);
    }

    [Fact]
    public void Validate_KeepsAffinityDefaults()
    {
        var config = new AppConfig();
        config.Validate();
        Assert.Equal("High", config.Affinity.ActivePriority);
        Assert.Equal("High", config.Affinity.BackgroundPriority);
    }

    [Fact]
    public void Validate_ClampsLaunchNumClients()
    {
        var config = new AppConfig();
        config.Launch.NumClients = 0;
        config.Validate();
        Assert.Equal(1, config.Launch.NumClients);

        config.Launch.NumClients = 20;
        config.Validate();
        Assert.Equal(8, config.Launch.NumClients);
    }

    [Fact]
    public void Validate_ClampsPipOpacity()
    {
        var config = new AppConfig();
        config.Pip.Opacity = 0;
        config.Validate();
        Assert.Equal(10, config.Pip.Opacity);
    }

    [Fact]
    public void Validate_ClampsPipCustomDimensions()
    {
        var config = new AppConfig();
        config.Pip.CustomWidth = 10;
        config.Pip.CustomHeight = 10;
        config.Validate();
        Assert.Equal(100, config.Pip.CustomWidth);
        Assert.Equal(75, config.Pip.CustomHeight);
    }

    [Fact]
    public void Validate_ResetsEmptyProcessName()
    {
        var config = new AppConfig { EQProcessName = "  " };
        config.Validate();
        Assert.Equal("eqgame", config.EQProcessName);
    }

    [Fact]
    public void Validate_RecreatesNullNestedObjects()
    {
        var config = new AppConfig();
        // Simulate corrupt JSON that nulled nested objects
        config.Layout = null!;
        config.Affinity = null!;
        config.Launch = null!;
        config.Pip = null!;
        config.Characters = null!;

        config.Validate();

        Assert.NotNull(config.Layout);
        Assert.NotNull(config.Affinity);
        Assert.NotNull(config.Launch);
        Assert.NotNull(config.Pip);
        Assert.NotNull(config.Characters);
    }

    [Fact]
    public void PipConfig_GetSize_Presets()
    {
        var pip = new PipConfig();

        pip.SizePreset = "Small";
        Assert.Equal((200, 150), pip.GetSize());

        pip.SizePreset = "Medium";
        Assert.Equal((320, 240), pip.GetSize());

        pip.SizePreset = "Large";
        Assert.Equal((400, 300), pip.GetSize());

        pip.SizePreset = "XL";
        Assert.Equal((480, 360), pip.GetSize());

        pip.SizePreset = "XXL";
        Assert.Equal((640, 480), pip.GetSize());
    }

    [Fact]
    public void PipConfig_GetSize_CustomUsesCustomDimensions()
    {
        var pip = new PipConfig
        {
            SizePreset = "Custom",
            CustomWidth = 500,
            CustomHeight = 400
        };
        Assert.Equal((500, 400), pip.GetSize());
    }

    [Fact]
    public void PipConfig_GetBorderColor_MapsCorrectly()
    {
        var pip = new PipConfig();

        pip.BorderColor = "Green";
        Assert.Equal(Color.FromArgb(34, 180, 85), pip.GetBorderColor());

        pip.BorderColor = "Blue";
        Assert.Equal(Color.FromArgb(60, 140, 230), pip.GetBorderColor());

        pip.BorderColor = "Red";
        Assert.Equal(Color.FromArgb(220, 50, 50), pip.GetBorderColor());

        pip.BorderColor = "Black";
        Assert.Equal(Color.Black, pip.GetBorderColor());
    }

    [Fact]
    public void PipConfig_GetBorderColor_UnknownDefaultsToGreen()
    {
        var pip = new PipConfig { BorderColor = "Purple" };
        Assert.Equal(Color.FromArgb(34, 180, 85), pip.GetBorderColor());
    }

    [Fact]
    public void CharacterProfile_DisplayName_WithClass()
    {
        var profile = new CharacterProfile { Name = "Gandalf", Class = "Wizard" };
        Assert.Equal("Gandalf (Wizard)", profile.DisplayName);
    }

    [Fact]
    public void CharacterProfile_DisplayName_WithoutClass()
    {
        var profile = new CharacterProfile { Name = "Gandalf", Class = "" };
        Assert.Equal("Gandalf", profile.DisplayName);
    }
}
