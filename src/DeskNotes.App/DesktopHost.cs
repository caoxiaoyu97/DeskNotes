using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace DeskNotes.App;

/// <summary>
/// Keeps the note sitting on the desktop layer without reparenting it.
///
/// Explorer exposes no supported way to host a foreign child window, and on
/// current Windows builds a child of Progman is reported visible but never
/// composited, while also being unable to receive keyboard focus. The note
/// therefore stays a normal top-level window pinned to the bottom of the
/// z-order: ordinary programs cover it, the desktop stays below it, and typing
/// keeps working because it is still a regular, activatable window.
/// </summary>
public sealed class DesktopHost : IDisposable
{
    private const int GwlExStyle = -20;
    private const long ExToolWindow = 0x80, ExAppWindow = 0x40000, ExTopmost = 0x8;
    private const uint NoSize = 0x0001, NoMove = 0x0002, NoActivate = 0x0010;
    private const uint SwShowNoActivate = 4;
    private const int HotkeyId = 0x4D4E;
    private static readonly IntPtr HwndBottom = new(1), HwndTop = IntPtr.Zero;

    private readonly Window _window;
    private readonly Action _quickAdd;
    private readonly HwndSource _messages;
    private readonly DispatcherTimer _timer;
    private readonly uint _taskbarCreated;
    private IntPtr _hwnd;
    private bool _hotkey, _wanted, _disposed;
    private string _hotkeyWarning = "";

    public string Status { get; private set; } = "普通窗口";
    public event Action<string>? StatusChanged;

    public DesktopHost(Window window, Action quickAdd)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _quickAdd = quickAdd ?? throw new ArgumentNullException(nameof(quickAdd));
        _window.Dispatcher.VerifyAccess();
        // A separate hidden HWND owns the global hotkey so it survives whatever
        // happens to the note window itself.
        _messages = new HwndSource(new HwndSourceParameters("DeskNotes.DesktopHost.Messages")
        {
            WindowStyle = 0, ExtendedWindowStyle = (int)ExToolWindow,
            Width = 0, Height = 0, ParentWindow = IntPtr.Zero
        });
        _messages.AddHook(MessageHook);
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _hotkey = RegisterHotKey(_messages.Handle, HotkeyId, 0x4000 | 0x0001 | 0x0002, 0x4E);
        if (!_hotkey)
            _hotkeyWarning = $"；Ctrl+Alt+N 不可用（Win32 {Marshal.GetLastWin32Error()}）";
        _timer = new DispatcherTimer(DispatcherPriority.Background, _window.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(700)
        };
        _timer.Tick += Tick;
        Publish("普通窗口");
    }

    /// <summary>Pins the note to the desktop layer. Returns false only if no window handle exists yet.</summary>
    public bool Attach()
    {
        Verify();
        if (!Ensure()) return false;
        _wanted = true;
        _window.Topmost = false;
        WriteLong(_hwnd, GwlExStyle, (ReadLong(_hwnd, GwlExStyle) | ExToolWindow) & ~(ExAppWindow | ExTopmost));
        Pin();
        _timer.Start();
        Publish(DesktopStatus);
        return true;
    }

    /// <summary>Returns to an ordinary floating window that behaves like any other program.</summary>
    public void Detach()
    {
        Verify();
        _wanted = false;
        _timer.Stop();
        if (_hwnd != IntPtr.Zero && IsWindow(_hwnd))
        {
            _window.Topmost = false;
            WriteLong(_hwnd, GwlExStyle, ReadLong(_hwnd, GwlExStyle) & ~ExTopmost);
            SetWindowPos(_hwnd, HwndTop, 0, 0, 0, 0, NoMove | NoSize | NoActivate);
        }
        Publish("普通窗口");
    }

    /// <summary>Call from a mouse-down drag region instead of Window.DragMove.</summary>
    public void BeginDrag()
    {
        Verify();
        try { _window.DragMove(); }
        catch (InvalidOperationException) { /* button already released */ }
    }

    /// <summary>Current window bounds in WPF device-independent units.</summary>
    public Rect GetScreenBounds()
    {
        Verify();
        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd)) return Rect.Empty;
        return new Rect(_window.Left, _window.Top, _window.ActualWidth, _window.ActualHeight);
    }

    private string DesktopStatus => "已贴在桌面 · 其他程序会盖住它" + _hotkeyWarning;

    private bool Ensure()
    {
        if (_hwnd != IntPtr.Zero && IsWindow(_hwnd)) return true;
        _hwnd = new WindowInteropHelper(_window).EnsureHandle();
        return _hwnd != IntPtr.Zero;
    }

    private void Pin() => SetWindowPos(_hwnd, HwndBottom, 0, 0, 0, 0, NoMove | NoSize | NoActivate);

    private void Tick(object? sender, EventArgs e)
    {
        if (_disposed || !_wanted) return;
        if (!Ensure()) return;
        // "Show Desktop" hides ordinary windows; bring the note back without
        // stealing focus, then park it at the bottom again.
        if (IsIconic(_hwnd) || !IsWindowVisible(_hwnd))
        {
            ShowWindow(_hwnd, SwShowNoActivate);
            Pin();
            Publish(DesktopStatus);
            return;
        }
        // While the note is in the foreground the user is working in it, so leave
        // it alone; otherwise keep it parked below every ordinary program.
        if (GetForegroundWindow() != _hwnd) Pin();
    }

    private IntPtr MessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_disposed) return IntPtr.Zero;
        if (msg == 0x0312 && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            _window.Dispatcher.BeginInvoke(new Action(() => { if (!_disposed) _quickAdd(); }));
        }
        else if (_taskbarCreated != 0 && (uint)msg == _taskbarCreated && _wanted)
            _window.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_disposed || !_wanted || !Ensure()) return;
                ShowWindow(_hwnd, SwShowNoActivate);
                Pin();
                Publish(DesktopStatus);
            }));
        return IntPtr.Zero;
    }

    private void Publish(string text)
    {
        if (Status == text) return;
        Status = text;
        _window.Dispatcher.BeginInvoke(new Action(() => { if (!_disposed) StatusChanged?.Invoke(text); }));
    }

    private void Verify()
    {
        _window.Dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        _window.Dispatcher.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        _wanted = false;
        _timer.Stop();
        _timer.Tick -= Tick;
        if (_hotkey) { UnregisterHotKey(_messages.Handle, HotkeyId); _hotkey = false; }
        _messages.RemoveHook(MessageHook);
        _messages.Dispose();
    }

    private static long ReadLong(IntPtr hwnd, int index) => IntPtr.Size == 8 ? GetWindowLongPtr(hwnd, index).ToInt64() : (uint)GetWindowLong(hwnd, index);
    private static void WriteLong(IntPtr hwnd, int index, long value)
    {
        if (IntPtr.Size == 8) SetWindowLongPtr(hwnd, index, new IntPtr(value));
        else SetWindowLong(hwnd, index, unchecked((int)value));
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
}
