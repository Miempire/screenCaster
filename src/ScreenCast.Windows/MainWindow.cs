using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ScreenCast.Core.Signaling;
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
    private readonly CheckBox _serverMode = new()
    {
        Content = "使用服务器配对",
        IsChecked = true,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly TextBlock _serverLabel = new()
    {
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(16, 0, 0, 0),
        Foreground = Brushes.DimGray,
    };
    private readonly TextBox _codeInput = new() { Width = 150, MaxLength = 9, FontSize = 18 };
    private readonly TextBlock _codeDisplay = new() { FontSize = 24, FontWeight = FontWeights.SemiBold };
    private readonly StackPanel _directPanel = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
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
    private SignalingClient? _signaling;
    private SessionView? _signalingView;
    private DisplayInfo? _pendingDisplay;
    private string _inviteCode = string.Empty;
    private bool _accepted;
    private bool _mediaStarted;
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
        titleRow.Children.Add(new Border { Width = 12 });
        titleRow.Children.Add(_serverMode);
        titleRow.Children.Add(_serverLabel);
        header.Children.Add(titleRow);
        header.Children.Add(_status);

        var hostPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        hostPanel.Children.Add(_displays);
        hostPanel.Children.Add(_refresh);
        hostPanel.Children.Add(new Border { Width = 8 });
        hostPanel.Children.Add(_start);

        var codePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        codePanel.Children.Add(new TextBlock { Text = "连接码", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        codePanel.Children.Add(_codeInput);
        codePanel.Children.Add(_codeDisplay);

        _directPanel.Children.Add(new TextBlock { Text = "主机", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        _directPanel.Children.Add(_peerAddress);
        _directPanel.Children.Add(new TextBlock { Text = "视频端口", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 6, 0) });
        _directPanel.Children.Add(_peerVideoPort);
        _directPanel.Children.Add(new TextBlock { Text = "控制端口", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 6, 0) });
        _directPanel.Children.Add(_peerControlPort);

        var controls = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        controls.Children.Add(hostPanel);
        controls.Children.Add(codePanel);
        controls.Children.Add(_directPanel);
        DockPanel.SetDock(controls, Dock.Top);
        root.Children.Add(controls);

        var footer = new StackPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        footer.Children.Add(_log);
        root.Children.Add(footer);

        _videoHost.Child = _surface.Element;
        root.Children.Add(_videoHost);

        _role.SelectionChanged += (_, _) => UpdateRole();
        _serverMode.Checked += (_, _) => UpdateRole();
        _serverMode.Unchecked += (_, _) => UpdateRole();
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
        bool server = _serverMode.IsChecked == true;
        bool running = _host is not null || _viewer is not null || _signaling is not null;

        _serverLabel.Text = $"服务器 {SignalingServerAddress.Host}:{SignalingServerAddress.Port}";
        _displays.Visibility = host ? Visibility.Visible : Visibility.Collapsed;
        _refresh.Visibility = host ? Visibility.Visible : Visibility.Collapsed;
        _start.Content = running ? "断开" : host ? "开始共享" : "连接";
        _start.IsEnabled = running || !_busy;
        _serverMode.IsEnabled = !running;
        _role.IsEnabled = !running;
        _codeInput.Visibility = !host && server ? Visibility.Visible : Visibility.Collapsed;
        _codeInput.IsEnabled = !running;
        _codeDisplay.Visibility = host && server ? Visibility.Visible : Visibility.Collapsed;
        _codeDisplay.Text = host && server
            ? (_inviteCode.Length == 9 ? FormatCode(_inviteCode) : "点击“开始共享”获取连接码")
            : string.Empty;
        _directPanel.Visibility = !host && !server ? Visibility.Visible : Visibility.Collapsed;
        _peerAddress.IsEnabled = !host && !server && !running;
        _peerVideoPort.IsEnabled = !host && !server && !running;
        _peerControlPort.IsEnabled = !host && !server && !running;

        if (running)
        {
            return;
        }

        if (server)
        {
            _status.Text = host
                ? "Host：开始共享后显示 9 位连接码，控制端输入该码即可连接。"
                : "Viewer：输入被控端显示的 9 位连接码，由服务器自动配对。";
            return;
        }

        _status.Text = host
            ? "Host（直连）：共享本机桌面并接受远程鼠标键盘。开始后把 IP 与端口告诉控制端。"
            : "Viewer（直连）：填入主机的视频端口与控制端口后连接。连接成功后在此窗口操作远端。";
    }

    private static string FormatCode(string code) =>
        code.Length == 9 ? $"{code[..3]} {code[3..6]} {code[6..]}" : code;

    private static byte[] SessionBytes(SignalingId id)
    {
        byte[] bytes = new byte[SignalingId.Size];
        id.WriteTo(bytes);
        return bytes;
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
        if (_host is not null || _viewer is not null || _signaling is not null)
        {
            ShutdownSession();
            Log("会话已断开。");
            return;
        }

        _busy = true;
        UpdateRole();
        try
        {
            bool server = _serverMode.IsChecked == true;
            if (IsHostMode)
            {
                if (server) await StartHostSignalingAsync();
                else StartHost();
            }
            else
            {
                if (server) await StartViewerSignalingAsync();
                else StartViewer();
            }
        }
        catch (Exception ex)
        {
            Log($"启动失败：{ex.GetType().Name}: {ex.Message} (HRESULT 0x{ex.HResult:X8})");
            ShutdownSession();
        }
        finally
        {
            _busy = false;
            UpdateRole();
        }
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
        HookHostStats();

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
        HookViewerStats();

        _viewer.Start(new IPEndPoint(address, videoPort), new IPEndPoint(address, controlPort));
        _viewer.SendLease(true);
        _viewer.RequestKeyFrame();
        _inputEnabled = true;
        Log($"Viewer 已连接 {address}:{videoPort}（控制 {controlPort}）。点击画面后键盘鼠标将转发到远端。");
        UpdateRole();
    }

    private void HookHostStats()
    {
        _host!.StatsUpdated += stats => Dispatcher.BeginInvoke(() =>
        {
            if (_host is null) return;
            var peer = _host.PeerControl;
            _status.Text = $"Host 运行中 — 视频端口 {_host.VideoPort}，控制端口 {_host.ControlPort}　" +
                (peer is null ? "等待控制端连接…" : $"已连接 {peer}") +
                $"　采集 {stats.CapturedFrames}，编码 {stats.EncodedFrames}，关键帧 {stats.KeyFrames}，" +
                $"发送 {stats.SentBytes / 1024} KB，输入租约 {stats.InputLeaseOwner ?? "无"}";
        });
    }

    private void HookViewerStats()
    {
        _viewer!.FrameDecoded += (width, height, bgra) => _surface.Present(width, height, bgra);
        _viewer.StatsUpdated += stats => Dispatcher.BeginInvoke(() =>
        {
            _status.Text = $"Viewer 运行中 — {stats.Width}x{stats.Height}，{stats.Fps:F1} fps，" +
                $"解码 {stats.DecodedFrames}，丢帧 {stats.DroppedFrames}，拒绝 {stats.VideoDatagramsRejected}，" +
                $"解码渲染 {stats.DecodeRenderMs:F2} ms" +
                (string.IsNullOrEmpty(stats.LastError) ? string.Empty : $"　错误：{stats.LastError}");
        });
    }

    private async Task StartHostSignalingAsync()
    {
        if (_displays.SelectedItem is not DisplayInfo display)
        {
            Log("请先选择一个显示器。");
            return;
        }

        string deviceId = DeviceIdentity.GetOrCreate();
        var client = new SignalingClient(SignalingServerAddress.Host, SignalingServerAddress.Port, deviceId, DeviceRoleRules.All);
        client.JoinRequested += view => Dispatcher.BeginInvoke(new Action(() => _ = AcceptViewerAsync(client, view)));
        client.PeerKeyReceived += message => Dispatcher.BeginInvoke(new Action(() => _ = HandleHostPeerKeyAsync(message)));
        client.SessionClosed += view => Dispatcher.BeginInvoke(new Action(() => HandleSignalingClosed(view)));
        client.Faulted += exception => Dispatcher.BeginInvoke(new Action(() => Log($"信令错误：{exception.Message}")));

        try
        {
            await client.StartAsync();
            SessionView created = await client.CreateSessionAsync();
            _signaling = client;
            _signalingView = created;
            _pendingDisplay = display;
            _inviteCode = created.InviteCode;
            _accepted = false;
            _mediaStarted = false;
            Log($"信令已连接 {SignalingServerAddress.Host}:{SignalingServerAddress.Port}，连接码 {FormatCode(_inviteCode)}。");
            _status.Text = $"等待控制端输入连接码 {FormatCode(_inviteCode)}…";
            UpdateRole();
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    private async Task AcceptViewerAsync(SignalingClient client, SessionView request)
    {
        if (_signalingView is null || _accepted || _pendingDisplay is null) return;
        if (!request.SessionId.Equals(_signalingView.SessionId)) return;
        _accepted = true;
        try
        {
            SessionView accepted = await client.AcceptSessionAsync(request.SessionId);
            byte[] sessionId = SessionBytes(accepted.SessionId);
            byte[] secret = SignalingKeys.DeriveMediaSecret(_inviteCode, sessionId);
            _host = new HostSession(_pendingDisplay, H264EncoderSettings.Default720p30, secret, sessionId);
            HookHostStats();
            var info = new PeerMediaInfo(
                PeerMediaInfo.HostRole,
                (ushort)_host.VideoPort,
                (ushort)_host.ControlPort,
                PeerMediaInfo.LocalIPv4Addresses());
            await client.SendPeerKeyAsync(SignalingMessageType.PeerKeyOffer, accepted.SessionId, info.Encode());
            _signalingView = accepted;
            _status.Text = "控制端已加入，正在交换媒体地址…";
            Log($"控制端已加入，发送媒体端口 {_host.VideoPort}/{_host.ControlPort}。");
        }
        catch (Exception ex)
        {
            Log($"接受控制端失败：{ex.Message}");
            ShutdownSession();
        }
    }

    private async Task HandleHostPeerKeyAsync(PeerKeyMessage message)
    {
        if (_host is null || _signalingView is null || _mediaStarted) return;
        if (message.Type != SignalingMessageType.PeerKeyAnswer) return;
        if (!message.SessionId.Equals(_signalingView.SessionId)) return;

        try
        {
            PeerMediaInfo peer = PeerMediaInfo.Decode(message.Payload);
            IPAddress address = PeerMediaInfo.ChoosePeerAddress(peer, _signalingView.ViewerEndpoint, _signalingView.HostEndpoint);
            _host.Start(new IPEndPoint(address, peer.VideoPort), new IPEndPoint(address, peer.ControlPort));
            _mediaStarted = true;
            Log($"Host 已连接控制端 {address}:{peer.VideoPort}（控制 {peer.ControlPort}）。");
        }
        catch (Exception ex)
        {
            Log($"媒体连接失败：{ex.Message}");
            ShutdownSession();
        }

        await Task.CompletedTask;
    }

    private async Task StartViewerSignalingAsync()
    {
        string code = new string(_codeInput.Text.Where(char.IsAsciiDigit).ToArray());
        if (code.Length != 9)
        {
            Log("请输入 9 位连接码。");
            return;
        }

        string deviceId = DeviceIdentity.GetOrCreate();
        var client = new SignalingClient(SignalingServerAddress.Host, SignalingServerAddress.Port, deviceId, DeviceRoleRules.All);
        var offered = new TaskCompletionSource<PeerKeyMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.PeerKeyReceived += message =>
        {
            if (message.Type == SignalingMessageType.PeerKeyOffer) offered.TrySetResult(message);
        };
        client.SessionClosed += view => Dispatcher.BeginInvoke(new Action(() => HandleSignalingClosed(view)));
        client.Faulted += exception => Dispatcher.BeginInvoke(new Action(() => Log($"信令错误：{exception.Message}")));

        try
        {
            await client.StartAsync();
            SessionView joined = await client.JoinSessionAsync(code);
            _signaling = client;
            _signalingView = joined;
            _inviteCode = code;
            _mediaStarted = false;
            _status.Text = "已找到被控端，正在交换媒体地址…";
            Log($"已加入会话，等待主机媒体信息（{SignalingServerAddress.Host}）。");

            PeerKeyMessage offer = await offered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await StartViewerMediaAsync(client, joined, offer);
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    private async Task StartViewerMediaAsync(SignalingClient client, SessionView joined, PeerKeyMessage offer)
    {
        PeerMediaInfo host = PeerMediaInfo.Decode(offer.Payload);
        byte[] sessionId = SessionBytes(joined.SessionId);
        byte[] secret = SignalingKeys.DeriveMediaSecret(_inviteCode, sessionId);
        _viewer = new ViewerSession(secret, sessionId);
        HookViewerStats();

        var info = new PeerMediaInfo(
            PeerMediaInfo.ViewerRole,
            (ushort)_viewer.VideoPort,
            (ushort)_viewer.ControlPort,
            PeerMediaInfo.LocalIPv4Addresses());
        await client.SendPeerKeyAsync(SignalingMessageType.PeerKeyAnswer, joined.SessionId, info.Encode());

        IPAddress address = PeerMediaInfo.ChoosePeerAddress(host, joined.HostEndpoint, joined.ViewerEndpoint);
        _viewer.Start(new IPEndPoint(address, host.VideoPort), new IPEndPoint(address, host.ControlPort));
        _viewer.SendLease(true);
        _viewer.RequestKeyFrame();
        _inputEnabled = true;
        _mediaStarted = true;
        Log($"Viewer 已连接 {address}:{host.VideoPort}（控制 {host.ControlPort}）。点击画面后键盘鼠标将转发到远端。");
        UpdateRole();
    }

    private void HandleSignalingClosed(SessionView view)
    {
        if (_mediaStarted)
        {
            Log("信令会话已结束，媒体连接继续。");
            return;
        }

        Log("信令会话已关闭。");
        bool hostWaiting = IsHostMode && _host is null;
        ShutdownSession();
        if (hostWaiting)
        {
            Log("连接码已过期，正在刷新…");
            _ = RefreshHostCodeAsync();
        }
    }

    private async Task RefreshHostCodeAsync()
    {
        try
        {
            await StartHostSignalingAsync();
        }
        catch (Exception ex)
        {
            Log($"刷新连接码失败：{ex.Message}");
            ShutdownSession();
        }
    }

    private void ShutdownSession()
    {
        _inputEnabled = false;
        if (_signaling is not null)
        {
            _signaling.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _signaling = null;
        }

        _signalingView = null;
        _inviteCode = string.Empty;
        _accepted = false;
        _mediaStarted = false;

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