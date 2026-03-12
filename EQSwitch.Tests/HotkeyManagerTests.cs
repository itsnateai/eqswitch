using Xunit;
using EQSwitch.Core;

namespace EQSwitch.Tests;

public class HotkeyManagerTests
{
    [Theory]
    [InlineData("1", 0x31u)]
    [InlineData("A", 0x41u)]
    [InlineData("Z", 0x5Au)]
    [InlineData("F1", 0x70u)]
    [InlineData("F12", 0x7Bu)]
    [InlineData("TAB", 0x09u)]
    [InlineData("ESCAPE", 0x1Bu)]
    [InlineData("ESC", 0x1Bu)]
    [InlineData("SPACE", 0x20u)]
    [InlineData(@"\", NativeMethods.VK_OEM_5)]
    [InlineData("]", NativeMethods.VK_OEM_6)]
    [InlineData("[", NativeMethods.VK_OEM_4)]
    [InlineData("NUMPAD0", 0x60u)]
    [InlineData("NUMPAD9", 0x69u)]
    public void ResolveVK_KnownKeys_ReturnsCorrectCode(string key, uint expectedVk)
    {
        Assert.Equal(expectedVk, HotkeyManager.ResolveVK(key));
    }

    [Theory]
    [InlineData("a", 0x41u)]  // lowercase should work
    [InlineData("  F5  ", 0x74u)]  // whitespace trimmed
    public void ResolveVK_CaseInsensitiveAndTrimmed(string key, uint expectedVk)
    {
        Assert.Equal(expectedVk, HotkeyManager.ResolveVK(key));
    }

    [Theory]
    [InlineData("")]
    [InlineData("INVALID")]
    [InlineData("F13")]
    public void ResolveVK_UnknownKeys_ReturnsZero(string key)
    {
        Assert.Equal(0u, HotkeyManager.ResolveVK(key));
    }
}
