using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
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

    private readonly RadioButton _hostTab = new() { Content = "被控端", GroupName = "role" };
    private readonly RadioButton _viewerTab = new() { Content = "控制端", GroupName = "role" };
    private readonly TextBlock _modeHint = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 0, 0), FontSize = 12 };
    private readonly TextBlock _title = new();
    private readonly TextBlock _codeHint = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 8, 0, 0) };
    private readonly ComboBox _displays = new() { MinWidth = 260, Margin = new Thickness(0, 0, 8, 0) };
    private readonly StackPanel _displayRow = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
    private readonly Button _refresh = new() { Content = "刷新显示器" };
    private readonly Button _start = new() { Content = "开始共享", MinWidth = 148, Height = 44 };
    private readonly TextBox _peerAddress = new() { Width = 150, Text = "127.0.0.1" };
    private readonly TextBox _peerVideoPort = new() { Width = 72 };
    private readonly TextBox _peerControlPort = new() { Width = 72 };
    private readonly CheckBox _serverMode = new()
    {
        Content = "使用服务器配对",
        IsChecked = true,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly TextBlock _serverLabel = new()
    {
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(14, 0, 0, 0),
        FontSize = 12,
    };
    private readonly TextBlock _codeLabel = new() { Text = "连接码" };
    private readonly TextBox _codeInput = new() { MaxLength = 11, Height = 58 };
    private readonly TextBlock _codeWatermark = new()
    {
        Text = "输入 9 位连接码",
        FontSize = 15,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed,
    };
    private readonly TextBlock _codeDisplay = new()
    {
        FontFamily = new FontFamily("Consolas"),
        FontSize = 44,
        FontWeight = FontWeights.Bold,
        Margin = new Thickness(0, 8, 0, 0),
    };
    private readonly StackPanel _directPanel = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly TextBox _log = new() { Height = 120 };
    private readonly Border _logBox = new() { Visibility = Visibility.Collapsed };
    private readonly Button _logToggle = new() { Content = "运行日志" };
    private readonly StackPanel _advancedBox = new() { Visibility = Visibility.Collapsed };
    private readonly Button _advancedToggle = new() { Content = "高级设置", VerticalAlignment = VerticalAlignment.Center };

    private readonly VideoSurface _surface = new();
    private readonly Border _videoHost = new()
    {
        MinHeight = 280,
        Padding = new Thickness(0),
        ClipToBounds = true,
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
        Title = "ScreenCast 远程桌面";
        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(1240, workArea.Width - 60);
        Height = Math.Min(840, workArea.Height - 60);
        MinWidth = 960;
        MinHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        Background = Tone("AppBackground");
        Foreground = Tone("TextBrush");
        FontFamily = new FontFamily("Microsoft YaHei UI");

        _hostTab.IsChecked = mode != "viewer";
        _viewerTab.IsChecked = mode == "viewer";
        _hostTab.Style = Res("SegmentRadio");
        _viewerTab.Style = Res("SegmentRadio");
        _modeHint.Foreground = Tone("TextDimBrush");
        _title.Foreground = Tone("TextBrush");
        _title.FontSize = 25;
        _title.FontWeight = FontWeights.SemiBold;
        _codeHint.Foreground = Tone("TextDimBrush");
        _codeDisplay.Foreground = Tone("AccentBrush");
        _codeDisplay.HorizontalAlignment = HorizontalAlignment.Center;
        _displays.Style = Res("DarkComboBox");
        _refresh.Style = Res("SecondaryButton");
        _start.Style = Res("PrimaryButton");
        _peerAddress.Style = Res("DarkTextBox");
        _peerVideoPort.Style = Res("DarkTextBox");
        _peerControlPort.Style = Res("DarkTextBox");
        _serverMode.Style = Res("SwitchCheckBox");
        _serverLabel.Foreground = Tone("TextDimBrush");
        _codeInput.Style = Res("CodeInput");
        _codeWatermark.Foreground = Tone("TextDimBrush");
        _status.Foreground = Tone("TextDimBrush");
        _log.Style = Res("LogTextBox");
        _logToggle.Style = Res("LinkButton");
        _advancedToggle.Style = Res("LinkButton");
        _logToggle.HorizontalAlignment = HorizontalAlignment.Left;
        _advancedToggle.Margin = new Thickness(18, 0, 0, 0);

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(248) });
        root.ColumnDefinitions.Add(new ColumnDefinition());
        Content = root;

        var rail = new Border { Background = Tone("SurfaceBrush"), Padding = new Thickness(22, 26, 22, 22) };
        Grid.SetColumn(rail, 0);
        root.Children.Add(rail);

        var railGrid = new Grid();
        railGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        railGrid.RowDefinitions.Add(new RowDefinition());
        railGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rail.Child = railGrid;

        var railTop = new StackPanel();
        var mark = new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(11),
            Background = Tone("AccentBrush"),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        mark.Child = new TextBlock
        {
            Text = "S",
            Foreground = Tone("AppBackground"),
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        railTop.Children.Add(mark);
        railTop.Children.Add(new TextBlock
        {
            Text = "ScreenCast",
            Foreground = Tone("TextBrush"),
            FontSize = 19,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 16, 0, 0),
        });
        railTop.Children.Add(new TextBlock
        {
            Text = "远程桌面",
            Foreground = Tone("TextDimBrush"),
            FontSize = 12,
            Margin = new Thickness(0, 2, 0, 0),
        });
        railTop.Children.Add(new TextBlock
        {
            Text = "工作模式",
            Style = Res("SectionLabel"),
            Margin = new Thickness(2, 34, 0, 10),
        });
        railTop.Children.Add(_hostTab);
        railTop.Children.Add(_viewerTab);
        railTop.Children.Add(_modeHint);
        railGrid.Children.Add(railTop);

        var endpoint = new Border
        {
            Background = Tone("SurfaceAltBrush"),
            BorderBrush = Tone("LineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12),
        };
        var endpointStack = new StackPanel();
        var endpointRow = new StackPanel { Orientation = Orientation.Horizontal };
        endpointRow.Children.Add(new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = Tone("AccentBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        endpointRow.Children.Add(new TextBlock
        {
            Text = "信令服务器",
            Foreground = Tone("TextDimBrush"),
            FontSize = 11,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        endpointStack.Children.Add(endpointRow);
        endpointStack.Children.Add(new TextBlock
        {
            Text = $"{SignalingServerAddress.Host}:{SignalingServerAddress.Port}",
            Foreground = Tone("TextBrush"),
            FontSize = 13,
            Margin = new Thickness(0, 6, 0, 0),
        });
        endpoint.Child = endpointStack;
        Grid.SetRow(endpoint, 2);
        railGrid.Children.Add(endpoint);

        var main = new Grid { Margin = new Thickness(30, 26, 30, 26) };
        Grid.SetColumn(main, 1);
        root.Children.Add(main);
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        main.RowDefinitions.Add(new RowDefinition());
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid();
        var headerLeft = new StackPanel();
        headerLeft.Children.Add(_title);
        headerLeft.Children.Add(new TextBlock
        {
            Text = "点对点传输，画面不经过服务器",
            Foreground = Tone("TextDimBrush"),
            FontSize = 12,
            Margin = new Thickness(0, 4, 0, 0),
        });
        header.Children.Add(headerLeft);
        var secure = new Border
        {
            Background = Tone("SurfaceAltBrush"),
            BorderBrush = Tone("LineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 7, 12, 7),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
        };
        var secureRow = new StackPanel { Orientation = Orientation.Horizontal };
        secureRow.Children.Add(new Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = Tone("AccentBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        secureRow.Children.Add(new TextBlock
        {
            Text = "端到端加密",
            Foreground = Tone("TextDimBrush"),
            FontSize = 11,
            Margin = new Thickness(7, 0, 0, 0),
        });
        secure.Child = secureRow;
        header.Children.Add(secure);
        main.Children.Add(header);

        var hero = new Border
        {
            Style = Res("Card"),
            Padding = new Thickness(24, 22, 24, 22),
            Margin = new Thickness(0, 20, 0, 0),
        };
        var heroStack = new StackPanel();
        heroStack.Children.Add(_codeLabel);
        _codeLabel.Style = Res("SectionLabel");
        heroStack.Children.Add(_codeDisplay);
        var codeWrap = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        codeWrap.Children.Add(_codeInput);
        codeWrap.Children.Add(_codeWatermark);
        heroStack.Children.Add(codeWrap);
        heroStack.Children.Add(_codeHint);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 18, 0, 0) };
        actions.Children.Add(_start);
        actions.Children.Add(_advancedToggle);
        heroStack.Children.Add(actions);

        _advancedBox.Margin = new Thickness(0, 16, 0, 0);
        _displayRow.Children.Add(new TextBlock
        {
            Text = "显示器",
            Foreground = Tone("TextDimBrush"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        });
        _displayRow.Children.Add(_displays);
        _displayRow.Children.Add(_refresh);
        _advancedBox.Children.Add(_displayRow);

        var serverRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        serverRow.Children.Add(_serverMode);
        serverRow.Children.Add(_serverLabel);
        _advancedBox.Children.Add(serverRow);

        _peerAddress.Margin = new Thickness(8, 0, 0, 0);
        _peerVideoPort.Margin = new Thickness(8, 0, 0, 0);
        _peerControlPort.Margin = new Thickness(8, 0, 0, 0);
        _directPanel.Children.Add(new TextBlock
        {
            Text = "主机",
            Foreground = Tone("TextDimBrush"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        });
        _directPanel.Children.Add(_peerAddress);
        _directPanel.Children.Add(new TextBlock
        {
            Text = "视频端口",
            Foreground = Tone("TextDimBrush"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
        });
        _directPanel.Children.Add(_peerVideoPort);
        _directPanel.Children.Add(new TextBlock
        {
            Text = "控制端口",
            Foreground = Tone("TextDimBrush"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
        });
        _directPanel.Children.Add(_peerControlPort);
        _advancedBox.Children.Add(_directPanel);
        heroStack.Children.Add(_advancedBox);

        var statusCard = new Border
        {
            Background = Tone("SurfaceAltBrush"),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 18, 0, 0),
        };
        statusCard.Child = _status;
        heroStack.Children.Add(statusCard);

        hero.Child = heroStack;
        Grid.SetRow(hero, 1);
        main.Children.Add(hero);

        var videoGrid = new Grid();
        var emptyState = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        emptyState.Children.Add(new TextBlock
        {
            Text = "等待画面",
            Foreground = Tone("TextBrush"),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        emptyState.Children.Add(new TextBlock
        {
            Text = "连接成功后在此显示远程桌面",
            Foreground = Tone("TextDimBrush"),
            FontSize = 12,
            Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        videoGrid.Children.Add(emptyState);
        videoGrid.Children.Add(_surface.Element);
        _videoHost.Background = new SolidColorBrush(Color.FromRgb(5, 7, 11));
        _videoHost.Style = Res("Card");
        _videoHost.Margin = new Thickness(0, 18, 0, 0);
        _videoHost.Child = videoGrid;
        Grid.SetRow(_videoHost, 2);
        main.Children.Add(_videoHost);

        var logArea = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        logArea.Children.Add(_logToggle);
        _logBox.Background = Tone("SurfaceAltBrush");
        _logBox.CornerRadius = new CornerRadius(12);
        _logBox.Padding = new Thickness(8, 6, 8, 6);
        _logBox.Margin = new Thickness(0, 8, 0, 0);
        _logBox.Child = _log;
        logArea.Children.Add(_logBox);
        Grid.SetRow(logArea, 3);
        main.Children.Add(logArea);

        _hostTab.Checked += (_, _) => UpdateRole();
        _viewerTab.Checked += (_, _) => UpdateRole();
        _serverMode.Checked += (_, _) => UpdateRole();
        _serverMode.Unchecked += (_, _) => UpdateRole();
        _refresh.Click += async (_, _) => await RefreshDisplaysAsync();
        _start.Click += async (_, _) => await ToggleSessionAsync();
        _advancedToggle.Click += (_, _) =>
        {
            _advancedBox.Visibility = _advancedBox.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
        };
        _logToggle.Click += (_, _) =>
        {
            bool open = _logBox.Visibility != Visibility.Visible;
            _logBox.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            _logToggle.Content = open ? "收起日志" : "运行日志";
        };
        _peerVideoPort.TextChanged += (_, _) => UpdateRole();
        _peerControlPort.TextChanged += (_, _) => UpdateRole();
        _codeInput.TextChanged += (_, _) => FormatCodeInput();
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

        SourceInitialized += (_, _) =>
        {
            int dark = 1;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int));
        };

        _codeInput.Text = DeviceIdentity.LastPairedCode;
        FormatCodeInput();
        UpdateRole();
    }

    private Style Res(string key) => (Style)FindResource(key);

    private Brush Tone(string key) => (Brush)FindResource(key);

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

    private bool IsHostMode => _hostTab.IsChecked == true;

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

        _title.Text = host ? "共享这台电脑" : "连接远程设备";
        _modeHint.Text = host
            ? "允许他人通过连接码操作本机桌面。"
            : "输入对方提供的连接码，远程操作对方桌面。";
        _serverLabel.Text = $"服务器 {SignalingServerAddress.Host}:{SignalingServerAddress.Port}";
        _displayRow.Visibility = host ? Visibility.Visible : Visibility.Collapsed;
        _refresh.IsEnabled = host && !running;
        _codeLabel.Visibility = server ? Visibility.Visible : Visibility.Collapsed;
        _codeInput.Visibility = !host && server ? Visibility.Visible : Visibility.Collapsed;
        _codeInput.IsEnabled = !running;
        _codeWatermark.Visibility = !host && server && _codeInput.Text.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        _codeDisplay.Visibility = host && server ? Visibility.Visible : Visibility.Collapsed;
        bool hasCode = host && server && _inviteCode.Length == 9;
        _codeDisplay.Text = hasCode ? FormatCode(_inviteCode) : "— — —";
        _codeDisplay.FontSize = hasCode ? 44 : 30;
        _codeDisplay.Foreground = hasCode ? Tone("AccentBrush") : Tone("TextDimBrush");
        _codeHint.Text = server
            ? host
                ? "连接码长期有效，控制端输入该码即可连接。"
                : "输入被控端显示的 9 位连接码。"
            : host
                ? "直连模式下需要把本机 IP 与端口告诉控制端。"
                : "直连模式需要填写主机的 IP 与端口。";
        _directPanel.Visibility = !host && !server ? Visibility.Visible : Visibility.Collapsed;
        _peerAddress.IsEnabled = !host && !server && !running;
        _peerVideoPort.IsEnabled = !host && !server && !running;
        _peerControlPort.IsEnabled = !host && !server && !running;
        _hostTab.IsEnabled = !running;
        _viewerTab.IsEnabled = !running;
        _serverMode.IsEnabled = !running;
        _start.Content = running ? "断开" : host ? "开始共享" : "连接";
        _start.IsEnabled = running || !_busy;
        _start.Background = running ? Tone("DangerBrush") : Tone("AccentBrush");
        _start.Foreground = running ? Brushes.White : Tone("AppBackground");

        if (running)
        {
            return;
        }

        _status.Text = host
            ? server
                ? "等待控制端连接。"
                : "点击开始共享，然后把本机 IP 与端口告诉控制端。"
            : server
                ? "输入连接码后点击连接。"
                : "填写主机地址与端口后点击连接。";
    }

    private static string FormatCode(string code) =>
        code.Length == 9 ? $"{code[..3]} {code[3..6]} {code[6..]}" : code;

    private bool _formattingCode;

    private void FormatCodeInput()
    {
        if (_formattingCode)
        {
            return;
        }

        _formattingCode = true;
        try
        {
            string digits = new string(_codeInput.Text.Where(char.IsAsciiDigit).ToArray());
            if (digits.Length > 9)
            {
                digits = digits[..9];
            }

            string formatted = digits.Length > 6
                ? $"{digits[..3]} {digits[3..6]} {digits[6..]}"
                : digits.Length > 3
                    ? $"{digits[..3]} {digits[3..]}"
                    : digits;
            if (_codeInput.Text != formatted)
            {
                _codeInput.Text = formatted;
                _codeInput.CaretIndex = formatted.Length;
            }

            _codeWatermark.Visibility = digits.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _formattingCode = false;
        }
    }

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
        string? failure = null;
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
            failure = "连接失败，请检查网络或服务器状态后重试。";
            Log($"启动失败：{ex.GetType().Name}: {ex.Message} (HRESULT 0x{ex.HResult:X8})");
            ShutdownSession();
        }
        finally
        {
            _busy = false;
            UpdateRole();
        }

        if (failure is not null)
        {
            _status.Text = failure;
            OpenLog();
        }
    }

    private void OpenLog()
    {
        _logBox.Visibility = Visibility.Visible;
        _logToggle.Content = "收起日志";
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
        if (!request.SessionId.Equals(_signalingView.SessionId))
        {
            if (_mediaStarted) return;
            _signalingView = request;
        }
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
            DeviceIdentity.SaveLastPairedCode(code);
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
        if (_signalingView is not null && !view.SessionId.Equals(_signalingView.SessionId)) return;

        if (IsHostMode)
        {
            if (_host is not null)
            {
                _host.DisposeAsync().AsTask().GetAwaiter().GetResult();
                _host = null;
                _surface.Clear();
            }

            _accepted = false;
            _mediaStarted = false;
            _inputEnabled = false;
            Log("控制端已离开，等待下一次连接。");
            _status.Text = $"等待控制端输入连接码 {FormatCode(_inviteCode)}…";
            UpdateRole();
            return;
        }

        Log("信令会话已关闭。");
        ShutdownSession();
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

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);
}