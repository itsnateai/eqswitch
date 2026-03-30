using Xunit;
using EQSwitch.Models;

namespace EQSwitch.Tests;

public class EQClientTests
{
    [Fact]
    public void ToString_FormatsWithSlotIndex()
    {
        var client = new EQClient { ProcessId = 1234, SlotIndex = 0 };
        Assert.Equal("Client 1 (PID: 1234)", client.ToString());
    }

    [Fact]
    public void ToString_SlotIndex2_ShowsClient3()
    {
        var client = new EQClient { ProcessId = 1234, SlotIndex = 2 };
        Assert.Equal("Client 3 (PID: 1234)", client.ToString());
    }
}
