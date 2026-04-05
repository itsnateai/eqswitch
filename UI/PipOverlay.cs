using EQSwitch.Config;
using EQSwitch.Core;
using EQSwitch.Models;

namespace EQSwitch.UI;

/// <summary>
/// Picture-in-Picture overlay window using DWM thumbnails.
/// Shows live previews of background EQ client windows.
///
/// Features:
/// - Click-through (WS_EX_TRANSPARENT)
/// - Always-on-top
/// - Ctrl+drag repositioning
/// - Auto-swap sources when active client changes
/// - Auto-destroy when <2 windows remain
/// </summary>
public class PipOverlay : Form
{
    private const int RefreshIntervalMs = 500;

    private readonly AppConfig _config;
    private readonly List<IntPtr> _thumbnailIds = new();
    private readonly List<IntPtr> _sourceWindows = new();
    private readonly System.Windows.Forms.Timer _refreshTimer;

    // Border rendering — paint border color only in padding area, keep black behind thumbnails
    private readonly Color _borderColor;
    private readonly int _borderPad;

    // Ctrl+drag state
    private bool _dragging;
    private Point _dragStart;

    public PipOverlay(AppConfig config)
    {
        _config = config;

        // Borderless, always-on-top, no taskbar button
        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;

        var (w, h) = config.Pip.GetSize();
        var maxWin = Math.Clamp(config.Pip.MaxWindows, 1, 3);
        bool horizontal = config.Pip.IsHorizontal;

        // Flush thumbnails — border only on outside edges
        int borderPad = config.Pip.ShowBorder ? Math.Clamp(config.Pip.BorderThickness, 1, 10) : 0;
        if (horizontal)
            Size = new Size(borderPad + w * maxWin + borderPad, h + borderPad * 2);
        else
            Size = new Size(w + borderPad * 2, borderPad + h * maxWin + borderPad);

        // Default position: top-right corner
        var screen = (Screen.PrimaryScreen ?? Screen.AllScreens.FirstOrDefault())?.WorkingArea
                     ?? new Rectangle(0, 0, 1920, 1080);
        Location = new Point(screen.Right - Width - 10, screen.Top + 10);

        // Restore saved position if available, clamped to visible screen
        if (config.Pip.SavedPositions.Count > 0 && config.Pip.SavedPositions[0].Length >= 2)
        {
            Location = new Point(config.Pip.SavedPositions[0][0], config.Pip.SavedPositions[0][1]);
            ClampToScreen();
        }

        BackColor = Color.Black;
        _borderColor = config.Pip.ShowBorder ? config.Pip.GetBorderColor() : Color.Empty;
        _borderPad = borderPad;

        // Make the layered window opaque so BackColor renders as the border
        HandleCreated += (_, _) =>
            NativeMethods.SetLayeredWindowAttributes(Handle, 0, 255, NativeMethods.LWA_ALPHA);

        // Ctrl+drag support
        MouseDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseUp += OnMouseUp;

        _refreshTimer = new System.Windows.Forms.Timer { Interval = RefreshIntervalMs };
        _refreshTimer.Tick += (_, _) => RefreshIfNeeded();
        _refreshTimer.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_borderPad <= 0 || _borderColor.IsEmpty) return;

        // Paint border color only in the padding strips around the edges
        using var brush = new SolidBrush(_borderColor);
        int w = ClientSize.Width, h = ClientSize.Height, p = _borderPad;
        e.Graphics.FillRectangle(brush, 0, 0, w, p);           // top
        e.Graphics.FillRectangle(brush, 0, h - p, w, p);       // bottom
        e.Graphics.FillRectangle(brush, 0, p, p, h - p * 2);   // left
        e.Graphics.FillRectangle(brush, w - p, p, p, h - p * 2); // right
    }

    /// <summary>
    /// Make the window layered, click-through, and non-activating.
    /// WS_EX_TRANSPARENT ensures clicks pass through to windows beneath.
    /// Ctrl+drag repositioning is handled by temporarily removing WS_EX_TRANSPARENT.
    /// </summary>
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= NativeMethods.WS_EX_LAYERED
                        | NativeMethods.WS_EX_TRANSPARENT
                        | NativeMethods.WS_EX_TOOLWINDOW
                        | NativeMethods.WS_EX_TOPMOST
                        | NativeMethods.WS_EX_NOACTIVATE;
            return cp;
        }
    }

    /// <summary>
    /// Register DWM thumbnails for the given background windows.
    /// Call this after showing the overlay and whenever the active client changes.
    /// </summary>
    public void UpdateSources(IReadOnlyList<EQClient> clients, EQClient? activeClient)
    {
        // Fast path: check if sources changed WITHOUT allocating a list first.
        // This fires on every foreground change across the desktop (event-driven hook),
        // so avoiding allocation on the common no-change path matters.
        int maxWin = _config.Pip.MaxWindows;
        int bgCount = 0;
        bool changed = false;

        foreach (var c in clients)
        {
            if (c == activeClient || !NativeMethods.IsWindow(c.WindowHandle)) continue;
            if (bgCount < maxWin)
            {
                // Compare against current source windows as we go
                if (!changed && (bgCount >= _sourceWindows.Count || c.WindowHandle != _sourceWindows[bgCount]))
                    changed = true;
                bgCount++;
            }
            else break;
        }

        // Count mismatch means sources changed
        if (bgCount != _sourceWindows.Count) changed = true;

        if (bgCount == 0)
        {
            UnregisterAll();
            return;
        }

        if (!changed) return;

        // Rebuild thumbnails — only now do we need the actual window handles
        UnregisterAll();

        var (w, h) = _config.Pip.GetSize();
        int borderPad = _config.Pip.ShowBorder ? Math.Clamp(_config.Pip.BorderThickness, 1, 10) : 0;
        bool horizontal = _config.Pip.IsHorizontal;
        int idx = 0;

        foreach (var client in clients)
        {
            if (client == activeClient || !NativeMethods.IsWindow(client.WindowHandle)) continue;
            if (idx >= maxWin) break;

            var srcHwnd = client.WindowHandle;
            int hr = NativeMethods.DwmRegisterThumbnail(Handle, srcHwnd, out IntPtr thumbId);
            if (hr != 0)
            {
                FileLogger.Warn($"PiP: DwmRegisterThumbnail failed ({MapDwmError(hr)}) for {client}");
                continue;
            }

            int xPos, yPos;
            if (horizontal)
            {
                xPos = idx * w + borderPad;
                yPos = borderPad;
            }
            else
            {
                xPos = borderPad;
                yPos = idx * h + borderPad;
            }

            // Query source size and letterbox to preserve aspect ratio
            var destRect = new NativeMethods.RECT { Left = xPos, Top = yPos, Right = xPos + w, Bottom = yPos + h };
            if (NativeMethods.DwmQueryThumbnailSourceSize(thumbId, out var srcSize) == 0
                && srcSize.cx > 0 && srcSize.cy > 0)
            {
                double srcAspect = (double)srcSize.cx / srcSize.cy;
                double dstAspect = (double)w / h;

                if (dstAspect > srcAspect)
                {
                    // Pillarbox: destination is wider than source — shrink width
                    int fitW = (int)(h * srcAspect);
                    int pad = (w - fitW) / 2;
                    destRect.Left = xPos + pad;
                    destRect.Right = xPos + pad + fitW;
                }
                else if (dstAspect < srcAspect)
                {
                    // Letterbox: destination is taller than source — shrink height
                    int fitH = (int)(w / srcAspect);
                    int pad = (h - fitH) / 2;
                    destRect.Top = yPos + pad;
                    destRect.Bottom = yPos + pad + fitH;
                }
            }

            var props = new NativeMethods.DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = NativeMethods.DWM_TNP_RECTDESTINATION
                        | NativeMethods.DWM_TNP_VISIBLE
                        | NativeMethods.DWM_TNP_OPACITY
                        | NativeMethods.DWM_TNP_SOURCECLIENTAREAONLY,
                rcDestination = destRect,
                opacity = _config.Pip.Opacity,
                fVisible = true,
                fSourceClientAreaOnly = true
            };

            int updateHr = NativeMethods.DwmUpdateThumbnailProperties(thumbId, ref props);
            if (updateHr != 0)
                FileLogger.Warn($"PiP: DwmUpdateThumbnailProperties failed ({MapDwmError(updateHr)}) for {client}");
            _thumbnailIds.Add(thumbId);
            _sourceWindows.Add(srcHwnd);

            FileLogger.Info($"PiP: registered thumbnail for {client}");
            idx++;
        }

        // Resize the overlay to fit actual number of thumbnails.
        // Anchor to the edge so the overlay grows/shrinks toward the origin —
        // vertical: anchors bottom edge, horizontal: anchors right edge.
        int oldWidth = Width;
        int oldHeight = Height;
        int newWidth, newHeight;
        int count = _thumbnailIds.Count;
        if (horizontal)
        {
            newWidth = borderPad + w * count + borderPad;
            newHeight = h + borderPad * 2;
            Size = new Size(newWidth, newHeight);
            if (oldWidth != newWidth)
            {
                Location = new Point(Location.X + (oldWidth - newWidth), Location.Y);
                ClampToScreen();
            }
        }
        else
        {
            newWidth = w + borderPad * 2;
            newHeight = borderPad + h * count + borderPad;
            Size = new Size(newWidth, newHeight);
            if (oldHeight != newHeight)
            {
                Location = new Point(Location.X, Location.Y + (oldHeight - newHeight));
                ClampToScreen();
            }
        }
    }

    private bool _ctrlHeld;

    private void RefreshIfNeeded()
    {
        // Dynamic click-through toggle: remove WS_EX_TRANSPARENT when Ctrl is held
        // so the overlay accepts mouse input for drag repositioning
        bool ctrlNow = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0;
        if (ctrlNow != _ctrlHeld)
        {
            _ctrlHeld = ctrlNow;
            long exStyle = NativeMethods.GetWindowLongPtr(Handle, NativeMethods.GWL_EXSTYLE).ToInt64();
            if (ctrlNow)
                exStyle &= ~(long)NativeMethods.WS_EX_TRANSPARENT;
            else
                exStyle |= NativeMethods.WS_EX_TRANSPARENT;
            NativeMethods.SetWindowLongPtr(Handle, NativeMethods.GWL_EXSTYLE, (IntPtr)exStyle);
        }

        // Check if any source windows are gone
        for (int i = _sourceWindows.Count - 1; i >= 0; i--)
        {
            if (!NativeMethods.IsWindow(_sourceWindows[i]))
            {
                if (i < _thumbnailIds.Count)
                    NativeMethods.DwmUnregisterThumbnail(_thumbnailIds[i]);
                _thumbnailIds.RemoveAt(i);
                _sourceWindows.RemoveAt(i);
            }
        }
    }

    private void UnregisterAll()
    {
        foreach (var id in _thumbnailIds)
            NativeMethods.DwmUnregisterThumbnail(id);
        _thumbnailIds.Clear();
        _sourceWindows.Clear();
    }

    /// <summary>
    /// PIDs of the source windows currently being thumbnailed.
    /// Handles of the source windows currently being thumbnailed.
    /// </summary>
    public IReadOnlyList<IntPtr> SourceWindows => _sourceWindows;

    private static string MapDwmError(int hr) => unchecked((uint)hr) switch
    {
        0x80263001 => "DWM_E_COMPOSITIONDISABLED",
        0x80070006 => "E_HANDLE (invalid handle)",
        0x80070057 => "E_INVALIDARG",
        _ => $"0x{hr:X}"
    };

    // ─── Ctrl+Drag ────────────────────────────────────────────────

    private void OnMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && (ModifierKeys & Keys.Control) == Keys.Control)
        {
            _dragging = true;
            _dragStart = e.Location;
        }
    }

    private void OnMouseMove(object? sender, MouseEventArgs e)
    {
        if (_dragging)
        {
            Location = new Point(
                Location.X + e.X - _dragStart.X,
                Location.Y + e.Y - _dragStart.Y);
        }
    }

    private void OnMouseUp(object? sender, MouseEventArgs e)
    {
        if (_dragging)
        {
            _dragging = false;
            // Save position to config
            SavePosition();
        }
    }

    private void SavePosition()
    {
        ClampToScreen();

        if (_config.Pip.SavedPositions.Count == 0)
            _config.Pip.SavedPositions.Add(new[] { Location.X, Location.Y });
        else
            _config.Pip.SavedPositions[0] = new[] { Location.X, Location.Y };

        ConfigManager.Save(_config);
    }

    /// <summary>
    /// Ensure the overlay stays fully within the nearest screen's working area.
    /// </summary>
    private void ClampToScreen()
    {
        var screen = Screen.FromRectangle(Bounds).WorkingArea;
        // Guard against PiP being wider/taller than the screen (XXL preset on small monitor)
        int maxX = Math.Max(screen.Left, screen.Right - Width);
        int maxY = Math.Max(screen.Top, screen.Bottom - Height);
        int x = Math.Clamp(Location.X, screen.Left, maxX);
        int y = Math.Clamp(Location.Y, screen.Top, maxY);
        Location = new Point(x, y);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refreshTimer.Stop();
            _refreshTimer.Dispose();
            UnregisterAll();
        }
        base.Dispose(disposing);
    }
}
