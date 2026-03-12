using Xunit;
using EQSwitch.Models;

namespace EQSwitch.Tests;

public class EQClientTests
{
    [Fact]
    public void ResolveCharacterName_WithDash_ExtractsName()
    {
        var client = new EQClient { WindowTitle = "EverQuest - Gandalf" };
        client.ResolveCharacterName();
        Assert.Equal("Gandalf", client.CharacterName);
    }

    [Fact]
    public void ResolveCharacterName_NoDash_NoCharacterName()
    {
        var client = new EQClient { WindowTitle = "EverQuest" };
        client.ResolveCharacterName();
        Assert.Null(client.CharacterName);
    }

    [Fact]
    public void ResolveCharacterName_EmptyTitle_NoCharacterName()
    {
        var client = new EQClient { WindowTitle = "" };
        client.ResolveCharacterName();
        Assert.Null(client.CharacterName);
    }

    [Fact]
    public void ResolveCharacterName_MultipleDashes_TakesAfterFirst()
    {
        var client = new EQClient { WindowTitle = "EverQuest - Gandalf - the Grey" };
        client.ResolveCharacterName();
        Assert.Equal("Gandalf - the Grey", client.CharacterName);
    }

    [Fact]
    public void ToString_WithCharacterName_FormatsCorrectly()
    {
        var client = new EQClient { CharacterName = "Gandalf", ProcessId = 1234, SlotIndex = 0 };
        Assert.Equal("Gandalf (PID: 1234)", client.ToString());
    }

    [Fact]
    public void ToString_WithoutCharacterName_UsesSlotIndex()
    {
        var client = new EQClient { ProcessId = 1234, SlotIndex = 2 };
        Assert.Equal("Client 3 (PID: 1234)", client.ToString());
    }
}
