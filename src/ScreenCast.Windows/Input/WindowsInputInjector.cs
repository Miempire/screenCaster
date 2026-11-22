using System.Runtime.InteropServices;

namespace ScreenCast.Windows.Input;

/// <summary>
/// Injects remote input into this machine with <c>SendInput</c>.
/// </summary>
/// <remarks>
/// <para>
/// The injector only acts while a lease is held, and it tracks every key and button it pressed so
/// <see cref="ReleaseAll"/> can undo exactly that set. A dropped connection must never leave a
/// modifier stuck down on the host: that is the difference between "remote desktop" and "I have to
/// reboot my PC".
/// </para>
/// <para>
/// Injected events are marked with <c>LLKHF_INJECTED</c> by the OS, so a host that also captures
/// local input can tell its own injections apart and avoid an echo loop.
/// </para>
/// </remarks>
public sealed class WindowsInputInjector : IWindowsInputAdapter
{
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventScanCode = 0x0008;
    private const uint KeyEventExtendedKey = 0x0001;
    private const uint MouseEventMove = 0x0001;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint MouseEventRightDown = 0x0008;
    private const uint MouseEventRightUp = 0x0010;
    private const uint MouseEventMiddleDown = 0x0020;
    private const uint MouseEventMiddleUp = 0x0040;
    private const uint MouseEventWheel = 0x0800;
    private const uint MouseEventHWheel = 0x1000;
    private const uint MouseEventVirtualDesk = 0x4000;
    private const uint MouseEventAbsolute = 0x8000;

    private readonly object _gate = new();
    private readonly HashSet<ushort> _pressedKeys = [];
    private readonly HashSet<MouseButtonId> _pressedButtons = [];
    private string? _leaseOwner;
    private bool _disposed;

    public string? LeaseOwner
    {
        get { lock (_gate) { return _leaseOwner; } }
    }

    public int PressedKeyCount
    {
        get { lock (_gate) { return _pressedKeys.Count; } }
    }

    /// <summary>Take the input lease. Only one viewer may control the machine at a time.</summary>
    public void AcquireLease(string viewerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(viewerId);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_leaseOwner is not null && _leaseOwner != viewerId)
            {
                throw new InvalidOperationException($"输入租约已被 {_leaseOwner} 持有。");
            }

            _leaseOwner = viewerId;
        }
    }

    public void ReleaseLease(string viewerId)
    {
        lock (_gate)
        {
            if (_leaseOwner != viewerId)
            {
                return;
            }

            ReleaseAllLocked();
            _leaseOwner = null;
        }
    }

    public void SendScanCode(ushort scanCode, bool extended, bool isDown)
    {
        lock (_gate)
        {
            if (_disposed || _leaseOwner is null || scanCode == 0)
            {
                return;
            }

            if (isDown)
            {
                _pressedKeys.Add(scanCode);
            }
            else
            {
                _pressedKeys.Remove(scanCode);
            }

            var input = new Input
            {
                Type = InputKeyboard,
                Union = new InputUnion
                {
                    Keyboard = new KeyboardInput
                    {
                        VirtualKey = 0,
                        ScanCode = scanCode,
                        Flags = KeyEventScanCode
                            | (extended ? KeyEventExtendedKey : 0)
                            | (isDown ? 0 : KeyEventKeyUp),
                        Time = 0,
                        ExtraInfo = UIntPtr.Zero,
                    },
                },
            };
            Send(ref input);
        }
    }

    public void MovePointer(ushort normalizedX, ushort normalizedY)
    {
        lock (_gate)
        {
            if (_disposed || _leaseOwner is null)
            {
                return;
            }

            MovePointerLocked(normalizedX, normalizedY);
        }
    }

    private static void MovePointerLocked(ushort normalizedX, ushort normalizedY)
    {
        // Absolute coordinates use the 0..65535 virtual-desktop space, which is exactly what the
        // wire format carries, so no extra scaling is needed here.
        var input = new Input
        {
            Type = InputMouse,
            Union = new InputUnion
            {
                Mouse = new MouseInput
                {
                    Dx = normalizedX,
                    Dy = normalizedY,
                    MouseData = 0,
                    Flags = MouseEventMove | MouseEventAbsolute | MouseEventVirtualDesk,
                    Time = 0,
                    ExtraInfo = UIntPtr.Zero,
                },
            },
        };
        Send(ref input);
    }

    public void SendMouseButton(MouseButtonId button, bool isDown)
    {
        lock (_gate)
        {
            if (_disposed || _leaseOwner is null)
            {
                return;
            }

            if (isDown)
            {
                _pressedButtons.Add(button);
            }
            else
            {
                _pressedButtons.Remove(button);
            }

            uint flag = button switch
            {
                MouseButtonId.Left => isDown ? MouseEventLeftDown : MouseEventLeftUp,
                MouseButtonId.Right => isDown ? MouseEventRightDown : MouseEventRightUp,
                MouseButtonId.Middle => isDown ? MouseEventMiddleDown : MouseEventMiddleUp,
                _ => 0,
            };
            if (flag == 0)
            {
                return;
            }

            var input = new Input
            {
                Type = InputMouse,
                Union = new InputUnion
                {
                    Mouse = new MouseInput
                    {
                        Dx = 0,
                        Dy = 0,
                        MouseData = 0,
                        Flags = flag,
                        Time = 0,
                        ExtraInfo = UIntPtr.Zero,
                    },
                },
            };
            Send(ref input);
        }
    }

    public void SendWheel(short deltaX, short deltaY)
    {
        lock (_gate)
        {
            if (_disposed || _leaseOwner is null)
            {
                return;
            }

            if (deltaY != 0)
            {
                SendMouseData(MouseEventWheel, deltaY);
            }

            if (deltaX != 0)
            {
                SendMouseData(MouseEventHWheel, deltaX);
            }
        }
    }

    private static void SendMouseData(uint flags, short delta)
    {
        var input = new Input
        {
            Type = InputMouse,
            Union = new InputUnion
            {
                Mouse = new MouseInput
                {
                    Dx = 0,
                    Dy = 0,
                    MouseData = unchecked((uint)delta),
                    Flags = flags,
                    Time = 0,
                    ExtraInfo = UIntPtr.Zero,
                },
            },
        };
        Send(ref input);
    }

    /// <summary>Release every key and button this injector pressed. Safe to call at any time.</summary>
    public void ReleaseAll()
    {
        lock (_gate)
        {
            ReleaseAllLocked();
        }
    }

    private void ReleaseAllLocked()
    {
        foreach (var scanCode in _pressedKeys.ToArray())
        {
            var input = new Input
            {
                Type = InputKeyboard,
                Union = new InputUnion
                {
                    Keyboard = new KeyboardInput
                    {
                        VirtualKey = 0,
                        ScanCode = scanCode,
                        Flags = KeyEventScanCode | KeyEventKeyUp,
                        Time = 0,
                        ExtraInfo = UIntPtr.Zero,
                    },
                },
            };
            Send(ref input);
        }

        _pressedKeys.Clear();

        foreach (var button in _pressedButtons.ToArray())
        {
            uint flag = button switch
            {
                MouseButtonId.Left => MouseEventLeftUp,
                MouseButtonId.Right => MouseEventRightUp,
                MouseButtonId.Middle => MouseEventMiddleUp,
                _ => 0,
            };
            if (flag == 0)
            {
                continue;
            }

            var input = new Input
            {
                Type = InputMouse,
                Union = new InputUnion
                {
                    Mouse = new MouseInput
                    {
                        Dx = 0,
                        Dy = 0,
                        MouseData = 0,
                        Flags = flag,
                        Time = 0,
                        ExtraInfo = UIntPtr.Zero,
                    },
                },
            };
            Send(ref input);
        }

        _pressedButtons.Clear();
    }

    private static void Send(ref Input input)
    {
        // A failed injection must not throw into the receive loop; the caller counts errors instead.
        _ = SendInput(1, ref input, Marshal.SizeOf<Input>());
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            ReleaseAllLocked();
            _leaseOwner = null;
            _disposed = true;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, ref Input inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput Mouse;

        [FieldOffset(0)]
        public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }
}