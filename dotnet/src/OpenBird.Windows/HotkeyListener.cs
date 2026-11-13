using System.Runtime.InteropServices;
using OpenBird.Core;

namespace OpenBird.Windows;

/// <summary>A system-wide hotkey on its own message thread. Raises <see cref="Pressed"/> on that thread.</summary>
public sealed class HotkeyListener : IDisposable
{
    private const int HotkeyId = 0x4F42;   // "OB"
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private uint _threadId;
    private int _error;

    public event Action? Pressed;

    /// <summary>False when another program already owns the key combination.</summary>
    public bool Registered { get; private set; }
    public string Spec { get; }

    public HotkeyListener(string spec)
    {
        Spec = spec;
        var (modifiers, key) = Hotkey.Parse(spec);
        _thread = new Thread(() => Run(modifiers, key)) { IsBackground = true, Name = "OpenBird hotkey" };
        _thread.Start();
        _ready.Wait();
    }

    public string? FailureReason => Registered ? null
        : _error == 1409 ? $"'{Spec}' is already used by another program."
        : $"'{Spec}' could not be registered (Windows error {_error}).";

    private void Run(uint modifiers, uint key)
    {
        _threadId = Native.GetCurrentThreadId();
        Registered = Native.RegisterHotKey(IntPtr.Zero, HotkeyId, modifiers | 0x4000 /* MOD_NOREPEAT */, key);
        if (!Registered) _error = Marshal.GetLastWin32Error();
        _ready.Set();
        if (!Registered) return;

        while (Native.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            if (msg.message == Native.WM_HOTKEY && (int)msg.wParam == HotkeyId) Pressed?.Invoke();
        Native.UnregisterHotKey(IntPtr.Zero, HotkeyId);
    }

    public void Dispose()
    {
        if (Registered) Native.PostThreadMessageW(_threadId, Native.WM_QUIT, UIntPtr.Zero, IntPtr.Zero);
        _ready.Dispose();
    }
}
