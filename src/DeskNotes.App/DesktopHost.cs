using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace DeskNotes.App;

/// <summary>
/// UI-thread-only desktop embedding. Explorer's desktop hierarchy is undocumented;
/// shell destruction recovery is best effort, not a guarantee of HWND survival.
/// </summary>
public sealed class DesktopHost : IDisposable
{
    private const int GwlStyle = -16, GwlExStyle = -20;
    private const long WsChild = 0x40000000, WsPopup = 0x80000000;
    private const long ExToolWindow = 0x80, ExAppWindow = 0x40000, ExTopmost = 8;
    private const uint NoSize = 1, NoMove = 2, NoZOrder = 4, NoActivate = 0x10, FrameChanged = 0x20;
    private const int HotkeyId = 0x4D4E;
    private readonly Window _window;
    private readonly Action _quickAdd;
    private readonly HwndSource _messages;
    private readonly DispatcherTimer _timer;
    private readonly WinEventProc _shellEvent;
    private readonly uint _taskbarCreated;
    private HwndSource? _noteSource;
    private IntPtr _hwnd, _parent, _defView, _eventHook;
    private uint _parentProcess;
    private long _floatingStyle, _floatingExStyle;
    private bool _savedStyle, _wanted, _attached, _disposed, _busy, _dragging, _hotkey, _windowLost;
    private PointNative _dragStart;
    private RectNative _dragBounds, _lastBounds;
    private string _hotkeyWarning = "";

    public string Status { get; private set; } = "悬浮模式";
    public event Action<string>? StatusChanged;
    /// <summary>
    /// Raised once, synchronously on the UI thread when native destruction starts
    /// (WM_DESTROY, with WM_NCDESTROY/health checks as fallbacks), before WPF Closed
    /// when the destruction hook is reached. Set a recovery flag in this handler,
    /// then queue replacement of both Window and host with Dispatcher.BeginInvoke;
    /// do not recreate windows or pump messages inside this native callback.
    /// This also reports deliberate window destruction unless Dispose was called
    /// first. On Quit, set an application-level quitting flag and Dispose before
    /// Close/Shutdown; the queued recovery must recheck that flag. Closed may still
    /// Dispose normally. Use OnExplicitShutdown if losing the last window must not
    /// shut down the application before queued recovery can run.
    /// </summary>
    public event Action? WindowLost;

    public DesktopHost(Window window, Action quickAdd)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _quickAdd = quickAdd ?? throw new ArgumentNullException(nameof(quickAdd));
        _window.Dispatcher.VerifyAccess();
        // An invisible, independent top-level HWND receives shell broadcasts as well
        // as WM_HOTKEY. Never make this HWND a child or owner of the note/Explorer.
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
        _shellEvent = ShellEvent;
        _eventHook = SetWinEventHook(0x8001, 0x8001, IntPtr.Zero, _shellEvent, 0, 0, 2);
        _timer = new DispatcherTimer(DispatcherPriority.Background, _window.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _timer.Tick += HealthTick;
        _timer.Start();
        Publish("悬浮模式");
    }

    public bool Attach()
    {
        Verify();
        _wanted = true;
        return TryAttach();
    }

    public void Detach()
    {
        Verify();
        _wanted = false;
        EndDrag();
        var restored = RestoreFloating();
        if (!_windowLost) Publish(restored ? "悬浮模式" : "恢复悬浮失败");
    }

    /// <summary>Call from a left-button drag region; do not also call Window.DragMove().</summary>
    public void BeginDrag()
    {
        Verify();
        if (!EnsureNote() || (GetAsyncKeyState(1) & 0x8000) == 0)
            return;
        if (!GetCursorPos(out _dragStart) || !GetWindowRect(_hwnd, out _dragBounds))
            return;
        SetCapture(_hwnd);
        _dragging = GetCapture() == _hwnd;
    }

    /// <summary>
    /// Returns native screen bounds in WPF DIPs using this presentation source's
    /// device transform (including negative screen coordinates). Returns Rect.Empty
    /// if the HWND or presentation target is unavailable; callers should then keep
    /// their previously saved bounds. Must be called on the UI thread.
    /// </summary>
    public Rect GetScreenBounds()
    {
        Verify();
        if (!EnsureNote()) return Rect.Empty;
        if (!GetWindowRect(_hwnd, out var bounds))
        {
            if (!IsWindow(_hwnd)) MarkWindowLost();
            return Rect.Empty;
        }
        var target = _noteSource?.CompositionTarget;
        if (target == null) return Rect.Empty;
        _lastBounds = bounds;
        var transform = target.TransformFromDevice;
        return new Rect(
            transform.Transform(new Point(bounds.Left, bounds.Top)),
            transform.Transform(new Point(bounds.Right, bounds.Bottom)));
    }

    private bool NoteAlive()
    {
        if (_windowLost) return false;
        if (_hwnd == IntPtr.Zero) return false;
        if (IsWindow(_hwnd) && _noteSource is { IsDisposed: false }) return true;
        MarkWindowLost();
        return false;
    }

    private void MarkWindowLost()
    {
        if (_disposed || _windowLost) return;
        _windowLost = true;
        _wanted = _attached = _dragging = false;
        _parent = _defView = IntPtr.Zero;
        _timer.Stop();
        Publish("便签窗口已失效，请重建");
        // Synchronous by design: WPF may raise Closed and dispose this host before
        // a queued notification runs. State is latched first to prevent reentry
        // and WM_NCDESTROY/polling from producing duplicate recovery requests.
        WindowLost?.Invoke();
    }

    private bool EnsureNote()
    {
        if (_windowLost) return false;
        if (_hwnd != IntPtr.Zero)
            return NoteAlive();
        _hwnd = new WindowInteropHelper(_window).EnsureHandle();
        _noteSource = HwndSource.FromHwnd(_hwnd);
        if (_noteSource == null)
            return false;
        _noteSource.AddHook(NoteHook);
        return true;
    }

    private bool TryAttach()
    {
        if (_busy || _disposed || _windowLost)
            return false;
        _busy = true;
        try
        {
            if (!EnsureNote())
                return false;
            if (_attached && ParentHealthy())
                return true;
            if (_attached && !RestoreFloating())
                throw new InvalidOperationException("无法脱离原桌面");
            FindDesktop(out var parent, out var defView);
            if (parent == IntPtr.Zero)
                throw new InvalidOperationException("桌面尚未就绪");
            if (!GetWindowRect(_hwnd, out var bounds))
                throw NativeError("GetWindowRect");
            _lastBounds = bounds;
            if (!_savedStyle)
            {
                _floatingStyle = ReadLong(_hwnd, GwlStyle) & ~WsChild;
                _floatingExStyle = (ReadLong(_hwnd, GwlExStyle) | ExToolWindow) & ~(ExAppWindow | ExTopmost);
                _savedStyle = true;
            }
            _window.Topmost = false;
            Check(SetWindowPos(_hwnd, new IntPtr(-2), 0, 0, 0, 0, NoMove | NoSize | NoActivate), "Remove topmost");
            WriteLong(_hwnd, GwlStyle, (ReadLong(_hwnd, GwlStyle) | WsChild) & ~WsPopup);
            WriteLong(_hwnd, GwlExStyle, (ReadLong(_hwnd, GwlExStyle) | ExToolWindow) & ~(ExAppWindow | ExTopmost));
            ChangeParent(_hwnd, parent);
            _parent = parent;
            _defView = defView;
            GetWindowThreadProcessId(parent, out _parentProcess);
            _attached = true;
            // GetWindowRect, ScreenToClient and SetWindowPos use the same calling
            // thread DPI context. Never mix these native coordinates with WPF DIPs.
            Place(bounds, parent, true);
            Publish("已附着桌面" + (_eventHook == IntPtr.Zero ? "；仅轮询检测" : ""));
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException)
        {
            var restored = RestoreFloating();
            if (!_windowLost)
            {
                var detail = ex is Win32Exception native ? $"{native.Message}（Win32 {native.NativeErrorCode}）"
                    : ex is ArgumentException ? "窗口参数无效" : "桌面暂不可用";
                Publish($"{(restored ? "悬浮模式" : "桌面恢复失败")}：{detail}；等待重连");
            }
            return false;
        }
        finally { _busy = false; }
    }

    private bool RestoreFloating()
    {
        if (_hwnd == IntPtr.Zero)
            return true;
        if (!NoteAlive()) return false;
        try
        {
            var bounds = GetWindowRect(_hwnd, out var current) ? current : _lastBounds;
            // SetParent(NULL) precedes removing WS_CHILD, as required by SetParent.
            ChangeParent(_hwnd, IntPtr.Zero);
            WriteLong(_hwnd, GwlStyle, _savedStyle ? _floatingStyle : ReadLong(_hwnd, GwlStyle) & ~WsChild);
            WriteLong(_hwnd, GwlExStyle, _savedStyle ? _floatingExStyle : (ReadLong(_hwnd, GwlExStyle) | ExToolWindow) & ~(ExAppWindow | ExTopmost));
            _window.Topmost = false;
            Check(SetWindowPos(_hwnd, new IntPtr(-2), 0, 0, 0, 0, NoMove | NoSize | NoActivate), "Remove topmost");
            Place(bounds, IntPtr.Zero, false);
            _attached = false;
            _parent = _defView = IntPtr.Zero;
            return true;
        }
        catch (Win32Exception) { return false; }
    }

    private void Place(RectNative bounds, IntPtr parent, bool raise)
    {
        var origin = new PointNative { X = bounds.Left, Y = bounds.Top };
        if (parent != IntPtr.Zero)
            Check(ScreenToClient(parent, ref origin), "ScreenToClient");
        Check(SetWindowPos(_hwnd, IntPtr.Zero, origin.X, origin.Y,
            bounds.Right - bounds.Left, bounds.Bottom - bounds.Top,
            NoActivate | FrameChanged | (raise ? 0u : NoZOrder)), "SetWindowPos");
    }

    private bool ParentHealthy()
    {
        if (!NoteAlive() || !IsWindow(_parent) || GetParent(_hwnd) != _parent ||
            !IsWindowVisible(_parent) || !IsWindow(_defView) || GetParent(_defView) != _parent)
            return false;
        GetWindowThreadProcessId(_parent, out var pid);
        return pid == _parentProcess && GetShellWindow() != IntPtr.Zero;
    }

    private static void FindDesktop(out IntPtr parent, out IntPtr defView)
    {
        var shell = GetShellWindow();
        GetWindowThreadProcessId(shell, out var shellPid);
        IntPtr worker = IntPtr.Zero, workerView = IntPtr.Zero, progman = IntPtr.Zero, progmanView = IntPtr.Zero;
        if (shell != IntPtr.Zero)
        {
            EnumWindows((hwnd, _) =>
            {
                GetWindowThreadProcessId(hwnd, out var pid);
                if (pid != shellPid || !IsWindowVisible(hwnd)) return true;
                var name = new StringBuilder(64);
                GetClassName(hwnd, name, name.Capacity);
                var view = FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (view == IntPtr.Zero) return true;
                if (name.ToString() == "WorkerW") { worker = hwnd; workerView = view; }
                if (name.ToString() == "Progman") { progman = hwnd; progmanView = view; }
                return true;
            }, IntPtr.Zero);
        }
        // Do not use the wallpaper WorkerW behind DefView: it cannot receive note
        // input through the icon view. HWND_TOP here is sibling z-order, not topmost.
        parent = worker != IntPtr.Zero ? worker : progman;
        defView = worker != IntPtr.Zero ? workerView : progmanView;
    }

    private void HealthTick(object? sender, EventArgs e)
    {
        if (_disposed || _busy || _windowLost) return;
        if (_hwnd != IntPtr.Zero && !NoteAlive()) return;
        if (_dragging && (GetAsyncKeyState(1) & 0x8000) == 0) EndDrag();
        if (_attached && ParentHealthy())
        {
            if (GetWindowRect(_hwnd, out var bounds)) _lastBounds = bounds;
            return;
        }
        if (_wanted) TryAttach();
    }

    private void ShellEvent(IntPtr hook, uint evt, IntPtr hwnd, int objectId, int childId, uint threadId, uint time)
    {
        // Out-of-context notifications run on this dispatcher thread. They may
        // arrive after destruction; never claim that they can prevent every race.
        if (_disposed || !_attached || _busy || objectId != 0 || childId != 0 ||
            (hwnd != _parent && hwnd != _defView)) return;
        EndDrag();
        var restored = RestoreFloating();
        if (!_windowLost) Publish(restored ? "桌面已变更，暂时悬浮并重连" : "恢复悬浮失败");
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
            _window.Dispatcher.BeginInvoke(new Action(() => { if (!_disposed && _wanted) TryAttach(); }));
        return IntPtr.Zero;
    }

    private IntPtr NoteHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_disposed) return IntPtr.Zero;
        if (msg is 0x0002 or 0x0082) // WM_DESTROY / WM_NCDESTROY; let WPF finish destruction.
        {
            MarkWindowLost();
            return IntPtr.Zero;
        }
        if (msg == 0x0200 && _dragging && GetCursorPos(out var cursor))
        {
            var origin = new PointNative
            {
                X = _dragBounds.Left + cursor.X - _dragStart.X,
                Y = _dragBounds.Top + cursor.Y - _dragStart.Y
            };
            var parent = GetParent(_hwnd);
            if (!_attached || (parent != IntPtr.Zero && ScreenToClient(parent, ref origin)))
                SetWindowPos(_hwnd, IntPtr.Zero, origin.X, origin.Y, 0, 0, NoSize | NoZOrder | NoActivate);
            handled = true;
        }
        else if (msg is 0x0202 or 0x001F or 0x0215 || (msg == 0x0100 && wParam.ToInt32() == 0x1B))
            EndDrag();
        return IntPtr.Zero;
    }

    private void EndDrag()
    {
        _dragging = false;
        if (_hwnd != IntPtr.Zero && GetCapture() == _hwnd) ReleaseCapture();
    }

    private void Publish(string text)
    {
        text += _hotkeyWarning;
        if (Status == text) return;
        Status = text;
        // Queue delivery outside native callbacks and native state transitions.
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
        _timer.Tick -= HealthTick;
        EndDrag();
        RestoreFloating();
        if (_eventHook != IntPtr.Zero) { UnhookWinEvent(_eventHook); _eventHook = IntPtr.Zero; }
        if (_noteSource is { IsDisposed: false }) _noteSource.RemoveHook(NoteHook);
        if (_hotkey) { UnregisterHotKey(_messages.Handle, HotkeyId); _hotkey = false; }
        _messages.RemoveHook(MessageHook);
        _messages.Dispose();
        GC.KeepAlive(_shellEvent);
    }

    private static Win32Exception NativeError(string operation) => new(Marshal.GetLastWin32Error(), operation);
    private static void Check(bool ok, string operation) { if (!ok) throw NativeError(operation); }
    private static void ChangeParent(IntPtr hwnd, IntPtr parent)
    {
        Marshal.SetLastPInvokeError(0);
        var previous = SetParent(hwnd, parent);
        if (previous == IntPtr.Zero && Marshal.GetLastWin32Error() != 0) throw NativeError("SetParent");
    }
    private static long ReadLong(IntPtr hwnd, int index) => IntPtr.Size == 8 ? GetWindowLongPtr(hwnd, index).ToInt64() : (uint)GetWindowLong(hwnd, index);
    private static void WriteLong(IntPtr hwnd, int index, long value)
    {
        Marshal.SetLastPInvokeError(0);
        var previous = IntPtr.Size == 8 ? SetWindowLongPtr(hwnd, index, new IntPtr(value)) : new IntPtr(SetWindowLong(hwnd, index, unchecked((int)value)));
        if (previous == IntPtr.Zero && Marshal.GetLastWin32Error() != 0) throw NativeError("SetWindowLongPtr");
    }

    [StructLayout(LayoutKind.Sequential)] private struct PointNative { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RectNative { public int Left, Top, Right, Bottom; }
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);
    private delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int objectId, int childId, uint threadId, uint time);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetParent(IntPtr hwnd, IntPtr parent);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(IntPtr hwnd, out RectNative rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool ScreenToClient(IntPtr hwnd, ref PointNative point);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out PointNative point);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetCapture();
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
}
