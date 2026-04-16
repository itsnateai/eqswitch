// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 itsnateai

using System.Runtime.InteropServices;

namespace EQSwitch.Core;

/// <summary>
/// Manages global hotkeys using RegisterHotKey/UnregisterHotKey.
///
/// Unlike AHK where hotkeys "just work", Win32 global hotkeys require a hidden
/// window to receive WM_HOTKEY messages. This class creates that window and
/// routes hotkey events to registered callbacks.
///
/// No more mystery box flashes — this is a proper message-only window.
/// </summary>
public class HotkeyManager : IDisposable
{
    private readonly Dictionary<int, Action> _hotkeyActions = new();
    private readonly HotkeyMessageWindow _messageWindow;
    private int _nextId = 1;

    public HotkeyManager()
    {
        _messageWindow = new HotkeyMessageWindow(this);
    }

    /// <summary>
    /// Register a global hotkey.
    /// </summary>
    /// <param name="hotkeyString">Format: "Modifier+Key" e.g. "Alt+1", "Ctrl+Shift+F1"</param>
    /// <param name="callback">Action to invoke when hotkey is pressed</param>
    /// <returns>Hotkey ID, or -1 if registration failed</returns>
    public int Register(string hotkeyString, Action callback)
    {
        if (!TryParseHotkey(hotkeyString, out uint modifiers, out uint vk))
        {
            Core.FileLogger.Warn($"Failed to parse hotkey: {hotkeyString}");
            return -1;
        }

        int id = _nextId;
        _nextId = _nextId < int.MaxValue ? _nextId + 1 : 1;

        // MOD_NOREPEAT prevents repeated firing when key is held
        modifiers |= NativeMethods.MOD_NOREPEAT;

        bool result = NativeMethods.RegisterHotKey(_messageWindow.Handle, id, modifiers, vk);
        if (!result)
        {
            Core.FileLogger.Warn($"RegisterHotKey failed for: {hotkeyString}, error={Marshal.GetLastWin32Error()}");
            return -1;
        }

        _hotkeyActions[id] = callback;
        return id;
    }

    /// <summary>
    /// Unregister a hotkey by its ID.
    /// </summary>
    public void Unregister(int id)
    {
        NativeMethods.UnregisterHotKey(_messageWindow.Handle, id);
        _hotkeyActions.Remove(id);
    }

    /// <summary>
    /// Unregister all hotkeys.
    /// </summary>
    public void UnregisterAll()
    {
        foreach (var id in _hotkeyActions.Keys.ToList())
        {
            NativeMethods.UnregisterHotKey(_messageWindow.Handle, id);
        }
        _hotkeyActions.Clear();
        _nextId = 1;
    }

    internal void OnHotkeyPressed(int id)
    {
        if (_hotkeyActions.TryGetValue(id, out var action))
        {
            try
            {
                action.Invoke();
            }
            catch (Exception ex)
            {
                Core.FileLogger.Error($"Hotkey callback error (ID {id})", ex);
            }
        }
    }

    /// <summary>
    /// Parse a hotkey string like "Alt+1" or "Ctrl+Shift+F5" into Win32 components.
    /// </summary>
    private static bool TryParseHotkey(string hotkeyString, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;

        if (string.IsNullOrWhiteSpace(hotkeyString))
            return false;

        var parts = hotkeyString.Split('+', StringSplitOptions.TrimEntries);

        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToUpperInvariant())
            {
                case "ALT": modifiers |= NativeMethods.MOD_ALT; break;
                case "CTRL":
                case "CONTROL": modifiers |= NativeMethods.MOD_CONTROL; break;
                case "SHIFT": modifiers |= NativeMethods.MOD_SHIFT; break;
                default: return false;
            }
        }

        // Last part is the key itself
        string key = parts[^1].ToUpperInvariant();
        vk = KeyNameToVK(key);

        return vk != 0;
    }

    /// <summary>
    /// Map key names to virtual key codes.
    /// </summary>
    private static uint KeyNameToVK(string key) => key switch
    {
        // Number row
        "0" => 0x30, "1" => 0x31, "2" => 0x32, "3" => 0x33, "4" => 0x34,
        "5" => 0x35, "6" => 0x36, "7" => 0x37, "8" => 0x38, "9" => 0x39,

        // Letters
        "A" => 0x41, "B" => 0x42, "C" => 0x43, "D" => 0x44, "E" => 0x45,
        "F" => 0x46, "G" => 0x47, "H" => 0x48, "I" => 0x49, "J" => 0x4A,
        "K" => 0x4B, "L" => 0x4C, "M" => 0x4D, "N" => 0x4E, "O" => 0x4F,
        "P" => 0x50, "Q" => 0x51, "R" => 0x52, "S" => 0x53, "T" => 0x54,
        "U" => 0x55, "V" => 0x56, "W" => 0x57, "X" => 0x58, "Y" => 0x59,
        "Z" => 0x5A,

        // Function keys
        "F1" => 0x70, "F2" => 0x71, "F3" => 0x72, "F4" => 0x73, "F5" => 0x74,
        "F6" => 0x75, "F7" => 0x76, "F8" => 0x77, "F9" => 0x78, "F10" => 0x79,
        "F11" => 0x7A, "F12" => 0x7B,

        // Special keys
        "TAB" => 0x09, "ESCAPE" or "ESC" => 0x1B, "SPACE" => 0x20,
        "BACKQUOTE" or "`" or "TILDE" => 0xC0,

        // OEM keys (US keyboard layout)
        @"\" or "BACKSLASH" or "OEM_5" => NativeMethods.VK_OEM_5,
        "]" or "OEM_6" => NativeMethods.VK_OEM_6,
        "[" or "OEM_4" => NativeMethods.VK_OEM_4,

        // Numpad
        "NUMPAD0" => 0x60, "NUMPAD1" => 0x61, "NUMPAD2" => 0x62,
        "NUMPAD3" => 0x63, "NUMPAD4" => 0x64, "NUMPAD5" => 0x65,
        "NUMPAD6" => 0x66, "NUMPAD7" => 0x67, "NUMPAD8" => 0x68,
        "NUMPAD9" => 0x69,

        _ => 0
    };

    /// <summary>
    /// Resolve a key name string to a VK code. Used by KeyboardHookManager
    /// to register single-key bindings from config strings.
    /// </summary>
    public static uint ResolveVK(string keyName) => KeyNameToVK(keyName.Trim().ToUpperInvariant());

    public void Dispose()
    {
        UnregisterAll();
        _messageWindow.DestroyHandle();
    }

    /// <summary>
    /// Hidden NativeWindow that receives WM_HOTKEY messages.
    /// This replaces AHK's invisible hotkey listener.
    /// </summary>
    private class HotkeyMessageWindow : NativeWindow
    {
        private readonly HotkeyManager _manager;

        public HotkeyMessageWindow(HotkeyManager manager)
        {
            _manager = manager;
            var cp = new CreateParams
            {
                Caption = "EQSwitch_HotkeyReceiver",
                // HWND_MESSAGE parent = message-only window (no visible UI)
                Parent = new IntPtr(-3)
            };
            CreateHandle(cp);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == NativeMethods.WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                _manager.OnHotkeyPressed(id);
            }
            base.WndProc(ref m);
        }
    }
}
