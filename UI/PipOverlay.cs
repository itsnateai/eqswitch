// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 itsnateai

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
/// - Ctrl+edge/corner drag resizing (16:9 locked)
/// - Auto-swap sources when active client changes
/// - Auto-hide when no background clients
/// </summary>
public class PipOverlay : Form
{
    private const int RefreshIntervalMs = 500;
    private const int ResizeZone = 8; // pixels from edge for resize detection
    private const double AspectRatio = 16.0 / 9.0;
    private const int MinThumbnailWidth = 128;

    private readonly AppConfig _config;
    private readonly List<IntPtr> _thumbnailIds = new();
    private readonly List<IntPtr> _sourceWindows = new();
    private readonly System.Windows.Forms.Timer _refreshTimer;

    // Border rendering — paint border color only in padding area, keep black behind thumbnails
    private readonly Color _borderColor;
    private readonly int _borderPad;
    private const int InnerGap = 1; // 1px divider between PiPs

    // Ctrl+drag state
    private bool _dragging;
    private Point _dragStart;

    // Ctrl+edge resize state
    private enum ResizeEdge { None, N, S, E, W, NE, NW, SE, SW }
    private ResizeEdge _resizeEdge = ResizeEdge.None;
    private bool _resizing;
    private Point _resizeStart;
    private Size _resizeStartSize;
    private Point _resizeStartLoc;

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

        // Outer border + 1px inner gaps between PiPs
        int borderPad = config.Pip.ShowBorder ? Math.Clamp(config.Pip.BorderThickness, 1, 10) : 0;
        int gaps = borderPad > 0 ? InnerGap * (maxWin - 1) : 0;
        if (horizontal)
            Size = new Size(borderPad * 2 + w * maxWin + gaps, h + borderPad * 2);
        else
            Size = new Size(w + borderPad * 2, borderPad * 2 + h * maxWin + gaps);

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
        {
            NativeMethods.SetLayeredWindowAttributes(Handle, 0, 255, NativeMethods.LWA_ALPHA);
            // Disable DWM rounding — we use a GDI region for a larger radius
            int pref = NativeMethods.DWMWCP_DONOTROUND;
            NativeMethods.DwmSetWindowAttribute(Handle,
                NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
            ApplyRoundedRegion();
        };

        Resize += (_, _) => ApplyRoundedRegion();

        // Ctrl+drag support
        MouseDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseUp += OnMouseUp;

        _refreshTimer = new System.Windows.Forms.Timer { Interval = RefreshIntervalMs };
        _refreshTimer.Tick += (_, _) => RefreshIfNeeded();
        _refreshTimer.Start();
    }

    private const int CornerRadius = 16;

    private void ApplyRoundedRegion()
    {
        var rgn = NativeMethods.CreateRoundRectRgn(
            0, 0, Width + 1, Height + 1, CornerRadius, CornerRadius);
        var oldRegion = Region;
        Region = Region.FromHrgn(rgn);
        NativeMethods.DeleteObject(rgn);
        oldRegion?.Dispose(); // WinForms Region setter does NOT dispose the old one
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_borderPad <= 0 || _borderColor.IsEmpty) return;

        // Fill entire background with border color — eliminates any subpixel gaps
        // between DWM thumbnails and border edges. Thumbnails render on top.
        using var brush = new SolidBrush(_borderColor);
        e.Graphics.FillRectangle(brush, 0, 0, ClientSize.Width, ClientSize.Height);
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
            if (Visible) Hide();
            return;
        }

        if (!Visible) Show();

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

            int gap = (borderPad > 0 && idx > 0) ? InnerGap : 0;
            int xPos, yPos;
            if (horizontal)
            {
                xPos = borderPad + idx * w + (idx > 0 ? idx * InnerGap : 0);
                yPos = borderPad;
            }
            else
            {
                xPos = borderPad;
                yPos = borderPad + idx * h + (idx > 0 ? idx * InnerGap : 0);
            }

            var destRect = new NativeMethods.RECT
            {
                Left = xPos, Top = yPos,
                Right = xPos + w, Bottom = yPos + h
            };

            FileLogger.Info($"PiP: thumb[{idx}] dest=({destRect.Left},{destRect.Top})-({destRect.Right},{destRect.Bottom}) overlay={Width}x{Height} bp={borderPad}");

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
        int innerGaps = borderPad > 0 ? InnerGap * Math.Max(count - 1, 0) : 0;
        if (horizontal)
        {
            newWidth = borderPad * 2 + w * count + innerGaps;
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
            newHeight = borderPad * 2 + h * count + innerGaps;
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
        if (IsDisposed || !IsHandleCreated) return;

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
        int beforeCount = _sourceWindows.Count;
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

        // A source window disappeared — resize overlay to fit remaining
        if (_sourceWindows.Count != beforeCount)
        {
            if (_sourceWindows.Count == 0)
            {
                Hide();
                return;
            }

            var (w, h) = _config.Pip.GetSize();
            int bp = _borderPad;
            bool horizontal = _config.Pip.IsHorizontal;
            int count = _sourceWindows.Count;
            int oldW = Width, oldH = Height;

            int ig = bp > 0 ? InnerGap * Math.Max(count - 1, 0) : 0;
            if (horizontal)
            {
                Size = new Size(bp * 2 + w * count + ig, h + bp * 2);
                Location = new Point(Location.X + (oldW - Width), Location.Y);
            }
            else
            {
                Size = new Size(w + bp * 2, bp * 2 + h * count + ig);
                Location = new Point(Location.X, Location.Y + (oldH - Height));
            }

            // Reposition remaining thumbnails
            for (int i = 0; i < _thumbnailIds.Count; i++)
            {
                int xPos = horizontal ? bp + i * w + (i > 0 ? i * InnerGap : 0) : bp;
                int yPos = horizontal ? bp : bp + i * h + (i > 0 ? i * InnerGap : 0);
                var destRect = new NativeMethods.RECT
                {
                    Left = xPos, Top = yPos,
                    Right = xPos + w, Bottom = yPos + h
                };
                var props = new NativeMethods.DWM_THUMBNAIL_PROPERTIES
                {
                    dwFlags = NativeMethods.DWM_TNP_RECTDESTINATION,
                    rcDestination = destRect
                };
                int hr = NativeMethods.DwmUpdateThumbnailProperties(_thumbnailIds[i], ref props);
                if (hr != 0)
                    FileLogger.Warn($"PiP: DwmUpdateThumbnailProperties failed in refresh ({MapDwmError(hr)})");
            }

            Invalidate();
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

    // ─── Ctrl+Drag (move) and Ctrl+Edge Drag (resize) ──────────────

    private static ResizeEdge HitTestEdge(Point pt, Size size)
    {
        bool left   = pt.X < ResizeZone;
        bool right  = pt.X >= size.Width - ResizeZone;
        bool top    = pt.Y < ResizeZone;
        bool bottom = pt.Y >= size.Height - ResizeZone;

        return (left, right, top, bottom) switch
        {
            (true, _, true, _)  => ResizeEdge.NW,
            (true, _, _, true)  => ResizeEdge.SW,
            (_, true, true, _)  => ResizeEdge.NE,
            (_, true, _, true)  => ResizeEdge.SE,
            (true, _, _, _)     => ResizeEdge.W,
            (_, true, _, _)     => ResizeEdge.E,
            (_, _, true, _)     => ResizeEdge.N,
            (_, _, _, true)     => ResizeEdge.S,
            _                   => ResizeEdge.None
        };
    }

    private static IntPtr CursorForEdge(ResizeEdge edge) => edge switch
    {
        ResizeEdge.N or ResizeEdge.S     => NativeMethods.LoadCursor(IntPtr.Zero, NativeMethods.IDC_SIZENS),
        ResizeEdge.E or ResizeEdge.W     => NativeMethods.LoadCursor(IntPtr.Zero, NativeMethods.IDC_SIZEWE),
        ResizeEdge.NW or ResizeEdge.SE   => NativeMethods.LoadCursor(IntPtr.Zero, NativeMethods.IDC_SIZENWSE),
        ResizeEdge.NE or ResizeEdge.SW   => NativeMethods.LoadCursor(IntPtr.Zero, NativeMethods.IDC_SIZENESW),
        _                                => NativeMethods.LoadCursor(IntPtr.Zero, NativeMethods.IDC_ARROW)
    };

    private void OnMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || (ModifierKeys & Keys.Control) != Keys.Control)
            return;

        var edge = HitTestEdge(e.Location, Size);
        if (edge != ResizeEdge.None)
        {
            // Start resize drag
            _resizing = true;
            _resizeEdge = edge;
            _resizeStart = PointToScreen(e.Location);
            _resizeStartSize = Size;
            _resizeStartLoc = Location;
        }
        else
        {
            // Start move drag
            _dragging = true;
            _dragStart = e.Location;
        }
    }

    private void OnMouseMove(object? sender, MouseEventArgs e)
    {
        bool ctrlHeld = (ModifierKeys & Keys.Control) == Keys.Control;

        if (_resizing)
        {
            var screenPt = PointToScreen(e.Location);
            int dx = screenPt.X - _resizeStart.X;
            int dy = screenPt.Y - _resizeStart.Y;
            ApplyResize(dx, dy);
            return;
        }

        if (_dragging)
        {
            Location = new Point(
                Location.X + e.X - _dragStart.X,
                Location.Y + e.Y - _dragStart.Y);
            return;
        }

        // Update cursor when Ctrl is held and hovering edges
        if (ctrlHeld)
        {
            var edge = HitTestEdge(e.Location, Size);
            NativeMethods.SetCursor(CursorForEdge(edge));
        }
    }

    private void OnMouseUp(object? sender, MouseEventArgs e)
    {
        if (_resizing)
        {
            _resizing = false;
            _resizeEdge = ResizeEdge.None;
            SaveSizeAndPosition();
            return;
        }

        if (_dragging)
        {
            _dragging = false;
            SavePosition();
        }
    }

    private void ApplyResize(int dx, int dy)
    {
        int maxWin = Math.Clamp(_config.Pip.MaxWindows, 1, 3);
        bool horizontal = _config.Pip.IsHorizontal;
        int bp = _borderPad;

        // Determine new thumbnail width from the drag delta.
        // For edges that affect width, use dx; for height-only edges, derive from dy.
        int startW = _resizeStartSize.Width;
        int startH = _resizeStartSize.Height;

        int newW = startW;
        int newH = startH;

        // Apply delta based on which edge is being dragged
        switch (_resizeEdge)
        {
            case ResizeEdge.E or ResizeEdge.NE or ResizeEdge.SE:
                newW = startW + dx;
                break;
            case ResizeEdge.W or ResizeEdge.NW or ResizeEdge.SW:
                newW = startW - dx;
                break;
            case ResizeEdge.N:
                newH = startH - dy;
                break;
            case ResizeEdge.S:
                newH = startH + dy;
                break;
        }

        // Derive single-thumbnail size from total overlay size
        int thumbW, thumbH;
        if (horizontal)
        {
            thumbW = (newW - bp * 2) / maxWin;
            thumbH = (int)(thumbW / AspectRatio);
        }
        else
        {
            // For height-only edges (N/S) on vertical layout, derive from height
            if (_resizeEdge is ResizeEdge.N or ResizeEdge.S)
            {
                int thumbCount = Math.Max(_sourceWindows.Count, 1);
                int igV = bp > 0 ? InnerGap * Math.Max(thumbCount - 1, 0) : 0;
                thumbH = (newH - bp * 2 - igV) / thumbCount;
                thumbW = (int)(thumbH * AspectRatio);
            }
            else
            {
                thumbW = newW - bp * 2;
                thumbH = (int)(thumbW / AspectRatio);
            }
        }

        // Enforce minimum
        if (thumbW < MinThumbnailWidth) thumbW = MinThumbnailWidth;
        thumbH = (int)(thumbW / AspectRatio);
        if (thumbH < 1) thumbH = 1;

        // Calculate new overlay size (include inner gaps between PiPs)
        int thumbCount2 = Math.Max(_sourceWindows.Count, 1);
        int ig = bp > 0 ? InnerGap * Math.Max(thumbCount2 - 1, 0) : 0;
        Size newSize;
        if (horizontal)
            newSize = new Size(bp * 2 + thumbW * maxWin + (bp > 0 ? InnerGap * (maxWin - 1) : 0), thumbH + bp * 2);
        else
            newSize = new Size(thumbW + bp * 2, bp * 2 + thumbH * thumbCount2 + ig);

        // Anchor the edge opposite to the one being dragged
        var newLoc = _resizeStartLoc;
        switch (_resizeEdge)
        {
            case ResizeEdge.W or ResizeEdge.NW or ResizeEdge.SW:
                newLoc.X = _resizeStartLoc.X + (_resizeStartSize.Width - newSize.Width);
                break;
            case ResizeEdge.N or ResizeEdge.NW or ResizeEdge.NE:
                newLoc.Y = _resizeStartLoc.Y + (_resizeStartSize.Height - newSize.Height);
                break;
        }

        Size = newSize;
        Location = newLoc;

        // Update DWM thumbnail destination rects
        UpdateThumbnailRects(thumbW, thumbH);
    }

    private void UpdateThumbnailRects(int thumbW, int thumbH)
    {
        bool horizontal = _config.Pip.IsHorizontal;
        int bp = _borderPad;

        for (int i = 0; i < _thumbnailIds.Count; i++)
        {
            int xPos, yPos;
            if (horizontal) { xPos = bp + i * thumbW + (i > 0 ? i * InnerGap : 0); yPos = bp; }
            else { xPos = bp; yPos = bp + i * thumbH + (i > 0 ? i * InnerGap : 0); }

            var destRect = new NativeMethods.RECT
            {
                Left = xPos, Top = yPos,
                Right = xPos + thumbW, Bottom = yPos + thumbH
            };

            var props = new NativeMethods.DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = NativeMethods.DWM_TNP_RECTDESTINATION,
                rcDestination = destRect
            };
            int hr = NativeMethods.DwmUpdateThumbnailProperties(_thumbnailIds[i], ref props);
            if (hr != 0)
                FileLogger.Warn($"PiP: DwmUpdateThumbnailProperties failed in reposition ({MapDwmError(hr)})");
        }

        Invalidate(); // repaint border
    }

    private void SaveSizeAndPosition()
    {
        ClampToScreen();

        // Derive single-thumbnail dimensions from current overlay size
        int bp = _borderPad;
        int maxWin = Math.Clamp(_config.Pip.MaxWindows, 1, 3);
        int thumbW, thumbH;
        if (_config.Pip.IsHorizontal)
        {
            thumbW = (Width - bp * 2) / maxWin;
            thumbH = Height - bp * 2;
        }
        else
        {
            thumbW = Width - bp * 2;
            int count = Math.Max(_sourceWindows.Count, 1);
            thumbH = (Height - bp * 2) / count;
        }

        // Save as Custom preset
        _config.Pip.SizePreset = "Custom";
        _config.Pip.CustomWidth = Math.Max(thumbW, MinThumbnailWidth);
        _config.Pip.CustomHeight = Math.Max(thumbH, 1);

        // Save position
        if (_config.Pip.SavedPositions.Count == 0)
            _config.Pip.SavedPositions.Add(new[] { Location.X, Location.Y });
        else
            _config.Pip.SavedPositions[0] = new[] { Location.X, Location.Y };

        ConfigManager.Save(_config);
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
