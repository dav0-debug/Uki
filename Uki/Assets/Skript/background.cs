using System;
using System.Runtime.InteropServices;
using UnityEngine;

public class TrayManager : MonoBehaviour
{
    [Header("Поведение окна")]
    [Tooltip("Не активировать окно при показе (тихий режим)")]
    public bool noActivate = true;

    [Tooltip("Скрывать окно при потере фокуса")]
    public bool hideOnFocusLost = true;

    [Tooltip("Скрыть из панели задач и Alt-Tab")]
    public bool hideFromTaskbar = true;

    // ─── WinAPI ─────────────────────────────────────────────
    [DllImport("user32.dll")]
    private static extern IntPtr GetActiveWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    // ─── Константы ──────────────────────────────────────────
    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;
    private const int SW_SHOWNOACTIVATE = 4;   // показать БЕЗ активации
    private const int SW_MINIMIZE = 6;

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;  // скрыть из taskbar/alt-tab
    private const int WS_EX_NOACTIVATE = 0x08000000;  // не активировать окно

    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    private IntPtr _hwnd;

    void Start()
    {
        Application.runInBackground = true;

        _hwnd = GetActiveWindow();
        if (_hwnd == IntPtr.Zero)
        {
            Debug.LogError("[TrayManager] HWND не получен.");
            return;
        }

        ApplyWindowFlags();

        // Стартуем скрытыми (в трее)
        HideWindow();
    }

    private void ApplyWindowFlags()
    {
        int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);

        if (hideFromTaskbar) ex |= WS_EX_TOOLWINDOW;
        if (noActivate) ex |= WS_EX_NOACTIVATE;

        SetWindowLong(_hwnd, GWL_EXSTYLE, ex);

        Debug.Log($"[TrayManager] Флаги применены. ToolWindow={hideFromTaskbar}, NoActivate={noActivate}");
    }

    // ─── Управление видимостью ──────────────────────────────
    public void HideWindow()
    {
        ShowWindow(_hwnd, SW_HIDE);
    }

    /// Показать окно БЕЗ активации (не забирает фокус у других приложений)
    public void ShowWithoutActivating()
    {
        ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    public void ShowAndActivate()
    {
        ShowWindow(_hwnd, SW_SHOW);
        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
    }

    public void MinimizeToTray()
    {
        ShowWindow(_hwnd, SW_HIDE);
    }

    public bool IsVisible()
    {
        return IsWindowVisible(_hwnd);
    }

    void OnApplicationFocus(bool hasFocus)
    {
        if (hideOnFocusLost && !hasFocus)
        {
            HideWindow();
            Debug.Log("[TrayManager] Потерян фокус — окно скрыто в трей.");
        }
    }

    void OnApplicationQuit()
    {
        // Снимаем флаги, чтобы не мешать системе
        if (_hwnd == IntPtr.Zero) return;
        int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        ex &= ~WS_EX_TOOLWINDOW;
        ex &= ~WS_EX_NOACTIVATE;
        SetWindowLong(_hwnd, GWL_EXSTYLE, ex);
    }
}