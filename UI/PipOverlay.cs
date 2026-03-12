using System.Diagnostics;
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
    private readonly AppConfig _config;
    private readonly List<IntPtr> _thumbnailIds = new();
    private readonly List<IntPtr> _sourceWindows = new();
    private readonly System.Windows.Forms.Timer _refreshTimer;

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

        // Stack PiP windows vertically with a small gap
        int gap = config.Pip.ShowBorder ? 4 : 2;
        Size = new Size(w + (config.Pip.ShowBorder ? 4 : 0), (h + gap) * maxWin);

        // Default position: top-right corner
        var screen = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(screen.Right - Width - 10, screen.Top + 10);

        // Restore saved position if available
        if (config.Pip.SavedPositions.Count > 0 && config.Pip.SavedPositions[0].Length >= 2)
        {
            Location = new Point(config.Pip.SavedPositions[0][0], config.Pip.SavedPositions[0][1]);
        }

        BackColor = config.Pip.ShowBorder ? config.Pip.GetBorderColor() : Color.Black;

        // Ctrl+drag support
        MouseDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseUp += OnMouseUp;

        // 500ms refresh timer to update thumbnail sources
        _refreshTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _refreshTimer.Tick += (_, _) => RefreshIfNeeded();
        _refreshTimer.Start();
    }

    /// <summary>
    /// Make the window click-through and layered (for transparency).
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
        // Get background windows (exclude the active one)
        var backgrounds = clients
            .Where(c => c != activeClient && NativeMethods.IsWindow(c.WindowHandle))
            .Take(_config.Pip.MaxWindows)
            .ToList();

        if (backgrounds.Count == 0)
        {
            UnregisterAll();
            return;
        }

        // Check if sources changed
        bool changed = backgrounds.Count != _sourceWindows.Count;
        if (!changed)
        {
            for (int i = 0; i < backgrounds.Count; i++)
            {
                if (backgrounds[i].WindowHandle != _sourceWindows[i])
                {
                    changed = true;
                    break;
                }
            }
        }

        if (!changed) return;

        // Rebuild thumbnails
        UnregisterAll();

        var (w, h) = _config.Pip.GetSize();
        int borderPad = _config.Pip.ShowBorder ? 2 : 0;
        int gap = _config.Pip.ShowBorder ? 4 : 2;

        for (int i = 0; i < backgrounds.Count; i++)
        {
            var srcHwnd = backgrounds[i].WindowHandle;
            int hr = NativeMethods.DwmRegisterThumbnail(Handle, srcHwnd, out IntPtr thumbId);
            if (hr != 0)
            {
                Debug.WriteLine($"PiP: DwmRegisterThumbnail failed (0x{hr:X}) for {backgrounds[i]}");
                continue;
            }

            int yPos = i * (h + gap) + borderPad;

            var props = new NativeMethods.DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = NativeMethods.DWM_TNP_RECTDESTINATION
                        | NativeMethods.DWM_TNP_VISIBLE
                        | NativeMethods.DWM_TNP_OPACITY
                        | NativeMethods.DWM_TNP_SOURCECLIENTAREAONLY,
                rcDestination = new NativeMethods.RECT
                {
                    Left = borderPad,
                    Top = yPos,
                    Right = borderPad + w,
                    Bottom = yPos + h
                },
                opacity = _config.Pip.Opacity,
                fVisible = true,
                fSourceClientAreaOnly = true
            };

            NativeMethods.DwmUpdateThumbnailProperties(thumbId, ref props);
            _thumbnailIds.Add(thumbId);
            _sourceWindows.Add(srcHwnd);

            Debug.WriteLine($"PiP: registered thumbnail for {backgrounds[i]}");
        }

        // Resize the overlay to fit actual number of thumbnails
        Size = new Size(
            w + (borderPad * 2),
            (h + gap) * _thumbnailIds.Count + borderPad
        );
    }

    private void RefreshIfNeeded()
    {
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
        if (_config.Pip.SavedPositions.Count == 0)
            _config.Pip.SavedPositions.Add(new[] { Location.X, Location.Y });
        else
            _config.Pip.SavedPositions[0] = new[] { Location.X, Location.Y };

        ConfigManager.Save(_config);
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
