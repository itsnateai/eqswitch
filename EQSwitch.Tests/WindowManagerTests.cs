using Xunit;
using EQSwitch.Config;
using EQSwitch.Core;
using EQSwitch.Models;
using Moq;

namespace EQSwitch.Tests;

public class WindowManagerTests
{
    private static EQClient MakeClient(int slot, int pid = 0, IntPtr? hwnd = null)
    {
        return new EQClient
        {
            SlotIndex = slot,
            ProcessId = pid > 0 ? pid : (slot + 100),
            WindowHandle = hwnd ?? new IntPtr(slot + 1000),
            WindowTitle = $"EverQuest - Char{slot}"
        };
    }

    private static Mock<IWindowsApi> MockApi(bool isWindow = true, bool isHung = false)
    {
        var mock = new Mock<IWindowsApi>();
        mock.Setup(a => a.IsWindow(It.IsAny<IntPtr>())).Returns(isWindow);
        mock.Setup(a => a.IsHungAppWindow(It.IsAny<IntPtr>())).Returns(isHung);
        mock.Setup(a => a.ShowWindow(It.IsAny<IntPtr>(), It.IsAny<int>())).Returns(true);
        mock.Setup(a => a.SetForegroundWindow(It.IsAny<IntPtr>())).Returns(true);
        mock.Setup(a => a.SetWindowPos(It.IsAny<IntPtr>(), It.IsAny<IntPtr>(),
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<uint>())).Returns(true);
        mock.Setup(a => a.GetWindowLongPtr(It.IsAny<IntPtr>(), It.IsAny<int>())).Returns(IntPtr.Zero);
        mock.Setup(a => a.SetWindowLongPtr(It.IsAny<IntPtr>(), It.IsAny<int>(), It.IsAny<IntPtr>())).Returns(IntPtr.Zero);
        return mock;
    }

    // ─── CycleNext ──────────────────────────────────────────────────

    [Fact]
    public void CycleNext_EmptyList_ReturnsNull()
    {
        var wm = new WindowManager(new AppConfig(), MockApi().Object);
        Assert.Null(wm.CycleNext(Array.Empty<EQClient>(), null));
    }

    [Fact]
    public void CycleNext_NoCurrent_ReturnsFirst()
    {
        var api = MockApi();
        var wm = new WindowManager(new AppConfig(), api.Object);
        var clients = new[] { MakeClient(0), MakeClient(1), MakeClient(2) };

        var result = wm.CycleNext(clients, null);
        Assert.Same(clients[0], result);
    }

    [Fact]
    public void CycleNext_FromFirst_ReturnsSecond()
    {
        var api = MockApi();
        var wm = new WindowManager(new AppConfig(), api.Object);
        var clients = new[] { MakeClient(0), MakeClient(1), MakeClient(2) };

        var result = wm.CycleNext(clients, clients[0]);
        Assert.Same(clients[1], result);
    }

    [Fact]
    public void CycleNext_FromLast_WrapsToFirst()
    {
        var api = MockApi();
        var wm = new WindowManager(new AppConfig(), api.Object);
        var clients = new[] { MakeClient(0), MakeClient(1), MakeClient(2) };

        var result = wm.CycleNext(clients, clients[2]);
        Assert.Same(clients[0], result);
    }

    [Fact]
    public void CycleNext_SingleClient_ReturnsSame()
    {
        var api = MockApi();
        var wm = new WindowManager(new AppConfig(), api.Object);
        var clients = new[] { MakeClient(0) };

        var result = wm.CycleNext(clients, clients[0]);
        Assert.Same(clients[0], result);
    }

    // ─── CyclePrev ──────────────────────────────────────────────────

    [Fact]
    public void CyclePrev_EmptyList_ReturnsNull()
    {
        var wm = new WindowManager(new AppConfig(), MockApi().Object);
        Assert.Null(wm.CyclePrev(Array.Empty<EQClient>(), null));
    }

    [Fact]
    public void CyclePrev_FromFirst_WrapsToLast()
    {
        var api = MockApi();
        var wm = new WindowManager(new AppConfig(), api.Object);
        var clients = new[] { MakeClient(0), MakeClient(1), MakeClient(2) };

        var result = wm.CyclePrev(clients, clients[0]);
        Assert.Same(clients[2], result);
    }

    [Fact]
    public void CyclePrev_FromLast_ReturnsPrevious()
    {
        var api = MockApi();
        var wm = new WindowManager(new AppConfig(), api.Object);
        var clients = new[] { MakeClient(0), MakeClient(1), MakeClient(2) };

        var result = wm.CyclePrev(clients, clients[2]);
        Assert.Same(clients[1], result);
    }

    // ─── SwitchToClient ─────────────────────────────────────────────

    [Fact]
    public void SwitchToClient_InvalidWindow_ReturnsFalse()
    {
        var api = MockApi(isWindow: false);
        var wm = new WindowManager(new AppConfig(), api.Object);
        Assert.False(wm.SwitchToClient(MakeClient(0)));
    }

    [Fact]
    public void SwitchToClient_HungWindow_ReturnsFalse()
    {
        var api = MockApi(isHung: true);
        var wm = new WindowManager(new AppConfig(), api.Object);
        Assert.False(wm.SwitchToClient(MakeClient(0)));
    }

    [Fact]
    public void SwitchToClient_ValidWindow_RestoresAndFocuses()
    {
        var api = MockApi();
        var wm = new WindowManager(new AppConfig(), api.Object);
        var client = MakeClient(0);

        Assert.True(wm.SwitchToClient(client));
        api.Verify(a => a.ShowWindow(client.WindowHandle, NativeMethods.SW_RESTORE), Times.Once);
        api.Verify(a => a.SetForegroundWindow(client.WindowHandle), Times.Once);
    }

    // ─── ArrangeWindows (Grid Math) ─────────────────────────────────

    [Fact]
    public void ArrangeWindows_EmptyList_NoAction()
    {
        var api = MockApi();
        var wm = new WindowManager(new AppConfig(), api.Object);
        wm.ArrangeWindows(Array.Empty<EQClient>());
        api.Verify(a => a.SetWindowPos(It.IsAny<IntPtr>(), It.IsAny<IntPtr>(),
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<uint>()), Times.Never);
    }

    [Fact]
    public void ArrangeWindows_2x2Grid_CorrectPositions()
    {
        var api = MockApi();
        api.Setup(a => a.GetAllMonitorWorkAreas()).Returns(new List<WinRect>
        {
            new() { Left = 0, Top = 0, Right = 1920, Bottom = 1080 }
        });

        var config = new AppConfig();
        config.Layout.Columns = 2;
        config.Layout.Rows = 2;
        config.Layout.TopOffset = 0;
        config.Layout.Mode = "single";

        var wm = new WindowManager(config, api.Object);
        var clients = new[] { MakeClient(0), MakeClient(1), MakeClient(2), MakeClient(3) };
        wm.ArrangeWindows(clients);

        // 1920/2 = 960 per cell width, 1080/2 = 540 per cell height
        // Window 0: (0,0), Window 1: (960,0), Window 2: (0,540), Window 3: (960,540)
        api.Verify(a => a.SetWindowPos(clients[0].WindowHandle, IntPtr.Zero, 0, 0, 960, 540, It.IsAny<uint>()));
        api.Verify(a => a.SetWindowPos(clients[1].WindowHandle, IntPtr.Zero, 960, 0, 960, 540, It.IsAny<uint>()));
        api.Verify(a => a.SetWindowPos(clients[2].WindowHandle, IntPtr.Zero, 0, 540, 960, 540, It.IsAny<uint>()));
        api.Verify(a => a.SetWindowPos(clients[3].WindowHandle, IntPtr.Zero, 960, 540, 960, 540, It.IsAny<uint>()));
    }

    [Fact]
    public void ArrangeWindows_WithTopOffset_AppliesOffset()
    {
        var api = MockApi();
        api.Setup(a => a.GetAllMonitorWorkAreas()).Returns(new List<WinRect>
        {
            new() { Left = 0, Top = 0, Right = 1920, Bottom = 1080 }
        });

        var config = new AppConfig();
        config.Layout.Columns = 1;
        config.Layout.Rows = 1;
        config.Layout.TopOffset = 30;
        config.Layout.Mode = "single";

        var wm = new WindowManager(config, api.Object);
        var clients = new[] { MakeClient(0) };
        wm.ArrangeWindows(clients);

        api.Verify(a => a.SetWindowPos(clients[0].WindowHandle, IntPtr.Zero, 0, 30, 1920, 1080, It.IsAny<uint>()));
    }

    [Fact]
    public void ArrangeWindows_MultiMonitor_OnePerMonitor()
    {
        var api = MockApi();
        api.Setup(a => a.GetAllMonitorWorkAreas()).Returns(new List<WinRect>
        {
            new() { Left = 0, Top = 0, Right = 1920, Bottom = 1080 },
            new() { Left = 1920, Top = 0, Right = 3840, Bottom = 1080 }
        });

        var config = new AppConfig();
        config.Layout.Mode = "multimonitor";
        config.Layout.TopOffset = 0;

        var wm = new WindowManager(config, api.Object);
        var clients = new[] { MakeClient(0), MakeClient(1) };
        wm.ArrangeWindows(clients);

        api.Verify(a => a.SetWindowPos(clients[0].WindowHandle, IntPtr.Zero, 0, 0, 1920, 1080, It.IsAny<uint>()));
        api.Verify(a => a.SetWindowPos(clients[1].WindowHandle, IntPtr.Zero, 1920, 0, 1920, 1080, It.IsAny<uint>()));
    }

    [Fact]
    public void ArrangeWindows_SkipsHungWindows()
    {
        var api = MockApi();
        api.Setup(a => a.GetAllMonitorWorkAreas()).Returns(new List<WinRect>
        {
            new() { Left = 0, Top = 0, Right = 1920, Bottom = 1080 }
        });
        // Second window is hung
        var hungHwnd = new IntPtr(9999);
        api.Setup(a => a.IsHungAppWindow(hungHwnd)).Returns(true);

        var config = new AppConfig();
        config.Layout.Columns = 2;
        config.Layout.Rows = 1;
        config.Layout.Mode = "single";

        var wm = new WindowManager(config, api.Object);
        var clients = new[] { MakeClient(0), MakeClient(1, hwnd: hungHwnd) };
        wm.ArrangeWindows(clients);

        // First window positioned, second skipped
        api.Verify(a => a.SetWindowPos(clients[0].WindowHandle, It.IsAny<IntPtr>(),
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<uint>()), Times.Once);
        api.Verify(a => a.SetWindowPos(hungHwnd, It.IsAny<IntPtr>(),
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<uint>()), Times.Never);
    }

    // ─── SwapWindows ────────────────────────────────────────────────

    [Fact]
    public void SwapWindows_LessThanTwo_NoAction()
    {
        var api = MockApi();
        var wm = new WindowManager(new AppConfig(), api.Object);
        wm.SwapWindows(new[] { MakeClient(0) });
        api.Verify(a => a.GetWindowRect(It.IsAny<IntPtr>(), out It.Ref<WinRect>.IsAny), Times.Never);
    }

    [Fact]
    public void SwapWindows_TwoClients_RotatesPositions()
    {
        var api = MockApi();
        var rect0 = new WinRect { Left = 0, Top = 0, Right = 960, Bottom = 540 };
        var rect1 = new WinRect { Left = 960, Top = 0, Right = 1920, Bottom = 540 };

        api.Setup(a => a.GetWindowRect(new IntPtr(1000), out It.Ref<WinRect>.IsAny))
            .Callback(new GetWindowRectCallback((IntPtr h, out WinRect r) => r = rect0))
            .Returns(true);
        api.Setup(a => a.GetWindowRect(new IntPtr(1001), out It.Ref<WinRect>.IsAny))
            .Callback(new GetWindowRectCallback((IntPtr h, out WinRect r) => r = rect1))
            .Returns(true);

        var wm = new WindowManager(new AppConfig(), api.Object);
        var clients = new[] { MakeClient(0), MakeClient(1) };
        wm.SwapWindows(clients);

        // Client 0 moves to Client 1's position, Client 1 moves to Client 0's position
        api.Verify(a => a.SetWindowPos(clients[0].WindowHandle, IntPtr.Zero, 960, 0, 960, 540, It.IsAny<uint>()));
        api.Verify(a => a.SetWindowPos(clients[1].WindowHandle, IntPtr.Zero, 0, 0, 960, 540, It.IsAny<uint>()));
    }

    private delegate void GetWindowRectCallback(IntPtr hwnd, out WinRect rect);
}
