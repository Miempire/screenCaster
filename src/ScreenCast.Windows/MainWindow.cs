using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ScreenCast.Windows.Capture;
using ScreenCast.Windows.Input;
using ScreenCast.Windows.Media;
using ScreenCast.Windows.Render;
using ScreenCast.Windows.Session;

namespace ScreenCast.Windows;

/// <summary>
/// The unified client window. Host mode streams this desktop and injects remote input; viewer mode
/// connects to a host and shows its screen while forwarding this window's mouse and keyboard.
/// </summary>
/// <remarks>
/// <para>
/// Keyboard state is tracked and released on focus loss, disconnect and window close. A remote
/// session that ends while the user holds Ctrl must not leave the host thinking Ctrl is down.
/// </para>
/// <para>
/// Pointer coordinates are normalized against the rendered picture, not the window, so letterbox
/// bars and window scaling do not shift the remote cursor.
/// </para>
/// </remarks>
public sealed class MainWindow : Window
{
    private static readonly byte[] DefaultSecret = Convert.FromHexString(
        "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");

    private static readonly byte[] DefaultSessionId = Convert.FromHexString("A1A2A3A4A5A6A7A8A9AAABACADAEAFB0");

    private readonly ComboBox _role = new() { Width = 140 };
    private readonly ComboBox _displays = new() { MinWidth = 320, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _refresh = new() { Content = "刷新显示器", Padding = new Thickness(12, 6, 12, 6) };
    private readonly Button _start = new() { Content = "开始共享", Padding = new Thickness(12, 6, 12, 6) };
    private readonly TextBox _peerAddress = new() { Width = 160, Text = "127.0.0.1" };
    private readonly TextBox _peerVideoPort = new() { Width = 70, Text = "0" };
    private readonly TextBox _peerControlPort = new() { Width = 70, Text = "0" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
    private readonly TextBox _log = new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Height = 96,
        FontFamily = new FontFamily("Consolas"),
        FontSize = 12,
    };

    private readonly VideoSurface _surface = new();
    private readonly Border _videoHost = new()
    {
        Background = Brushes.Black,
        MinHeight = 320,
        Child = null,
    };

    private HostSession? _host;
    private ViewerSession? _viewer;
    private bool _busy;
    private bool _inputEnabled;
    private bool _releasingAll;

    public MainWindow(string mode)
    {
        Title = "ScreenCast";
        Width = 1180;
        Height = 800;
        MinWidth = 820;
        MinHeight = 560;

        _role.Items.Add("Host（被控）");
        _role.Items.Add("Viewer（控制）");
        _role.SelectedIndex = mode == "viewer" ? 1 : 0;

        var root = new DockPanel { Margin = new Thickness(16) };
        Content = root;

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(new TextBlock
        {
            Text = "ScreenCast",
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 20, 0),
        });
        titleRow.Children.Add(new TextBlock
        {
            Text = "角色",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        });
        titleRow.Children.Add(_role);
        header.Children.Add(titleRow);
        header.Children.Add(_status);

        var hostPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        hostPanel.Children.Add(_displays);
        hostPanel.Children.Add(_refresh);
        hostPanel.Children.Add(new Border { Width = 8 });
        hostPanel.Children.Add(_start);

        var viewerPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        viewerPanel.Children.Add(new TextBlock { Text = "主机", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        viewerPanel.Children.Add(_peerAddress);
        viewerPanel.Children.Add(new TextBlock { Text = "视频端口", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 6, 0) });
        viewerPanel.Children.Add(_peerVideoPort);
        viewerPanel.Children.Add(new TextBlock { Text = "控制端口", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 6, 0) });
        viewerPanel.Children.Add(_peerControlPort);

        var controls = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        controls.Children.Add(hostPanel);
        controls.Children.Add(viewerPanel);
        DockPanel.SetDock(controls, Dock.Top);
        root.Children.Add(controls);

        var footer = new StackPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        footer.Children.Add(_log);
        root.Children.Add(footer);

        _videoHost.Child = _surface.Element;
        root.Children.Add(_videoHost);

        _role.SelectionChanged += (_, _) => UpdateRole();
        _refresh.Click += async (_, _) => await RefreshDisplaysAsync();
        _start.Click += async (_, _) => await ToggleSessionAsync();
        _peerVideoPort.TextChanged += (_, _) => UpdateRole();
        _peerControlPort.TextChanged += (_, _) => UpdateRole();
        Loaded += async (_, _) => await RefreshDisplaysAsync();
        Closing += (_, _) => ShutdownSession();
        Deactivated += (_, _) => ReleaseRemoteInput();
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewKeyUp += OnPreviewKeyUp;

        _surface.Element.MouseMove += OnSurfaceMouseMove;
        _surface.Element.MouseDown += OnSurfaceMouseDown;
        _surface.Element.MouseUp += OnSurfaceMouseUp;
        _surface.Element.MouseWheel += OnSurfaceMouseWheel;
        _surface.Element.MouseLeave += (_, _) => ReleaseRemoteInput();
        _surface.Element.Focusable = true;
        _surface.Element.MouseLeftButtonDown += (_, _) => _surface.Element.Focus();
        _surface.Element.MouseLeftButtonDown += OnSurfaceDoubleClick;

        UpdateRole();
    }

    /// <summary>Toggle fullscreen on a double click, the way every remote desktop client does it.</summary>
    private void OnSurfaceDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2)
        {
            return;
        }

        if (WindowStyle == WindowStyle.None)
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = WindowState.Normal;
            ResizeMode = ResizeMode.CanResize;
        }
        else
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }

        e.Handled = true;
    }

    private bool IsHostMode => _role.SelectedIndex == 0;

    private void Log(string message)
    {
        _log.AppendText($"[{DateTimeOffset.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        _log.ScrollToEnd();
    }

    private void UpdateRole()
    {
        bool host = IsHostMode;
        bool running = _host is not null || _viewer is not null;

        _displays.Visibility = host ? Visibility.Visible : Visibility.Collapsed;
        _refresh.Visibility = host ? Visibility.Visible : Visibility.Collapsed;
        _start.Content = host ? "开始共享" : "连接";
        _start.IsEnabled = !_busy;
        _peerAddress.IsEnabled = !host && !running;
        _peerVideoPort.IsEnabled = !host && !running;
        _peerControlPort.IsEnabled = !host && !running;
        _role.IsEnabled = !running;

        if (running)
        {
            _start.Content = "断开";
            _start.IsEnabled = true;
            return;
        }

        _status.Text = host
            ? "Host：共享本机桌面并接受远程鼠标键盘。开始后把显示的端口告诉控制端。"
            : "Viewer：填入主机的视频端口与控制端口后连接。连接成功后在此窗口操作远端。";
    }

    private async Task RefreshDisplaysAsync()
    {
        _busy = true;
        try
        {
            var displays = await Task.Run(CaptureDiagnostics.EnumerateDisplays);
            _displays.ItemsSource = displays;
            _displays.SelectedIndex = 0;
            Log($"显示器枚举完成：{displays.Count} 个。");
        }
        catch (Exception ex)
        {
            _displays.ItemsSource = null;
            Log($"显示器枚举失败：{ex.Message} (HRESULT 0x{ex.HResult:X8})");
        }
        finally
        {
            _busy = false;
            UpdateRole();
        }
    }

    private async Task ToggleSessionAsync()
    {
        if (_host is not null || _viewer is not null)
        {
            ShutdownSession();
            Log("会话已断开。");
            return;
        }

        try
        {
            if (IsHostMode)
            {
                StartHost();
            }
            else
            {
                StartViewer();
            }
        }
        catch (Exception ex)
        {
            Log($"启动失败：{ex.GetType().Name}: {ex.Message} (HRESULT 0x{ex.HResult:X8})");
            ShutdownSession();
        }

        await Task.CompletedTask;
    }

    private void StartHost()
    {
        if (_displays.SelectedItem is not DisplayInfo display)
        {
            Log("请先选择一个显示器。");
            return;
        }

        var settings = H264EncoderSettings.Default720p30;
        _host = new HostSession(display, settings, DefaultSecret, DefaultSessionId);
        _host.StatsUpdated += stats => Dispatcher.BeginInvoke(() =>
        {
            var peer = _host?.PeerControl;
            _status.Text = $"Host 运行中 — 视频端口 {_host!.VideoPort}，控制端口 {_host.ControlPort}　" +
                (peer is null ? "等待控制端连接…" : $"已连接 {peer}") +
                $"　采集 {stats.CapturedFrames}，编码 {stats.EncodedFrames}，关键帧 {stats.KeyFrames}，" +
                $"发送 {stats.SentBytes / 1024} KB，输入租约 {stats.InputLeaseOwner ?? "无"}";
        });

        // Wait mode: the host binds its ports and streams to whoever shows up. The operator only has
        // to tell the viewer which two ports to connect to, so the host can start first and there is
        // no need to know the viewer's address in advance.
        int expectedPeerVideoPort = 0;
        if (int.TryParse(_peerVideoPort.Text, out int parsedPeerVideo) && parsedPeerVideo > 0)
        {
            expectedPeerVideoPort = parsedPeerVideo;
        }

        _host.StartWaitingForPeer(expectedPeerVideoPort, Log);
        Log($"Host 已启动并等待控制端：视频端口 {_host.VideoPort}，控制端口 {_host.ControlPort}。");
        Log("在控制端填入本机 IP 与这两个端口即可连接。若对端视频端口未知，请在下方填写后重新开始。");
        _status.Text = $"Host 等待连接 — 视频端口 {_host.VideoPort}，控制端口 {_host.ControlPort}";
        UpdateRole();
    }

    private void StartViewer()
    {
        if (!IPAddress.TryParse(_peerAddress.Text, out var address))
        {
            Log("主机地址无效。");
            return;
        }

        if (!int.TryParse(_peerVideoPort.Text, out int videoPort) || videoPort <= 0
            || !int.TryParse(_peerControlPort.Text, out int controlPort) || controlPort <= 0)
        {
            Log("请填写有效的视频端口与控制端口。");
            return;
        }

        _viewer = new ViewerSession(DefaultSecret, DefaultSessionId);
        _viewer.FrameDecoded += (width, height, bgra) => _surface.Present(width, height, bgra);
        _viewer.StatsUpdated += stats => Dispatcher.BeginInvoke(() =>
        {
            _status.Text = $"Viewer 运行中 — {stats.Width}x{stats.Height}，{stats.Fps:F1} fps，" +
                $"解码 {stats.DecodedFrames}，丢帧 {stats.DroppedFrames}，拒绝 {stats.VideoDatagramsRejected}，" +
                $"解码渲染 {stats.DecodeRenderMs:F2} ms" +
                (string.IsNullOrEmpty(stats.LastError) ? string.Empty : $"　错误：{stats.LastError}");
        });

        _viewer.Start(new IPEndPoint(address, videoPort), new IPEndPoint(address, controlPort));
        _viewer.SendLease(true);
        _viewer.RequestKeyFrame();
        _inputEnabled = true;
        Log($"Viewer 已连接 {address}:{videoPort}（控制 {controlPort}）。点击画面后键盘鼠标将转发到远端。");
        UpdateRole();
    }

    private void ShutdownSession()
    {
        _inputEnabled = false;
        if (_viewer is not null)
        {
            try
            {
                _viewer.SendReleaseAll();
                _viewer.SendLease(false);
            }
            catch (Exception)
            {
                // The session may already be gone; releasing is best effort.
            }

            _viewer.FrameDecoded -= (_, _, _) => { };
            _viewer.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _viewer = null;
        }

        if (_host is not null)
        {
            _host.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _host = null;
        }

        _surface.Clear();
        _status.Text = "未连接。";
        UpdateRole();
    }

    private void ReleaseRemoteInput()
    {
        if (_viewer is null || !_inputEnabled || _releasingAll)
        {
            return;
        }

        _releasingAll = true;
        try
        {
            _viewer.SendReleaseAll();
        }
        catch (Exception)
        {
            // Losing the session while releasing is acceptable.
        }
        finally
        {
            _releasingAll = false;
        }
    }

    private void OnSurfaceMouseMove(object sender, MouseEventArgs e)
    {
        if (_viewer is null || !_inputEnabled)
        {
            return;
        }

        var point = e.GetPosition(_surface.Element);
        if (!_surface.TryMapToSource(point, out int x, out int y))
        {
            return;
        }

        _viewer.SendMouseMove(
            InputCodec.Normalize(x, _surface.FrameWidth),
            InputCodec.Normalize(y, _surface.FrameHeight));
    }

    private void OnSurfaceMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_viewer is null || !_inputEnabled)
        {
            return;
        }

        _surface.Element.Focus();
        var button = e.ChangedButton switch
        {
            MouseButton.Left => MouseButtonId.Left,
            MouseButton.Right => MouseButtonId.Right,
            MouseButton.Middle => MouseButtonId.Middle,
            _ => (MouseButtonId?)null,
        };
        if (button is { } id)
        {
            _viewer.SendMouseButton(id, true);
        }
    }

    private void OnSurfaceMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_viewer is null || !_inputEnabled)
        {
            return;
        }

        var button = e.ChangedButton switch
        {
            MouseButton.Left => MouseButtonId.Left,
            MouseButton.Right => MouseButtonId.Right,
            MouseButton.Middle => MouseButtonId.Middle,
            _ => (MouseButtonId?)null,
        };
        if (button is { } id)
        {
            _viewer.SendMouseButton(id, false);
        }
    }

    private void OnSurfaceMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_viewer is null || !_inputEnabled)
        {
            return;
        }

        _viewer.SendWheel(0, (short)Math.Clamp(e.Delta, short.MinValue, short.MaxValue));
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewer is null || !_inputEnabled)
        {
            return;
        }

        // Hardware scan codes are what the host injects, so translate through the virtual key
        // rather than sending WPF's key enum, which has no scan-code meaning.
        int virtualKey = KeyInterop.VirtualKeyFromKey(e.Key == Key.System ? e.SystemKey : e.Key);
        if (virtualKey == 0)
        {
            return;
        }

        uint scanCode = MapVirtualKey((uint)virtualKey, 0);
        if (scanCode == 0)
        {
            return;
        }

        bool extended = IsExtendedKey((uint)virtualKey);
        _viewer.SendKey((ushort)scanCode, extended, true);
        e.Handled = true;
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (_viewer is null || !_inputEnabled)
        {
            return;
        }

        int virtualKey = KeyInterop.VirtualKeyFromKey(e.Key == Key.System ? e.SystemKey : e.Key);
        if (virtualKey == 0)
        {
            return;
        }

        uint scanCode = MapVirtualKey((uint)virtualKey, 0);
        if (scanCode == 0)
        {
            return;
        }

        _viewer.SendKey((ushort)scanCode, IsExtendedKey((uint)virtualKey), false);
        e.Handled = true;
    }

    /// <summary>
    /// The extended-key flag is not derivable from the scan code alone: it distinguishes keys that
    /// share a scan code with their numpad twin (arrows, Insert/Delete/Home/End/PgUp/PgDn, right
    /// Ctrl/Alt, numpad Enter, the Windows keys).
    /// </summary>
    private static bool IsExtendedKey(uint virtualKey) => virtualKey is
        0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 // PgUp PgDn End Home arrows
        or 0x2D or 0x2E                                            // Insert Delete
        or 0x5B or 0x5C                                            // Win keys
        or 0x5D                                                    // Apps
        or 0xA3 or 0xA5                                            // right Ctrl, right Alt
        or 0x90;                                                   // NumLock

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);
}