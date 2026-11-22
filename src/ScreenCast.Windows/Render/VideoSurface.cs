using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenCast.Windows.Render;

/// <summary>
/// Renders decoded BGRA frames into a WPF <see cref="Image"/>.
/// </summary>
/// <remarks>
/// <para>
/// The bitmap is recreated only when the frame size changes; steady-state rendering writes pixels
/// into the existing buffer, which avoids a per-frame allocation and a per-frame layout pass.
/// </para>
/// <para>
/// Frames arrive on the decode thread, so every update is marshalled to the dispatcher. Writing a
/// <see cref="WriteableBitmap"/> from two threads is not allowed and produces torn or blank output.
/// </para>
/// <para>
/// The image is stretched uniformly with letterboxing: the remote desktop must keep its aspect ratio
/// or the pointer coordinates stop matching what the user sees.
/// </para>
/// </remarks>
public sealed class VideoSurface
{
    private readonly Image _image = new()
    {
        Stretch = Stretch.Uniform,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        SnapsToDevicePixels = true,
    };

    private WriteableBitmap? _bitmap;
    private int _width;
    private int _height;

    public VideoSurface()
    {
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.LowQuality);
    }

    public UIElement Element => _image;

    public int FrameWidth => _width;

    public int FrameHeight => _height;

    public long FramesRendered { get; private set; }

    /// <summary>Present one BGRA frame. Safe to call from any thread.</summary>
    public void Present(int width, int height, byte[] bgra)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4)
        {
            return;
        }

        var dispatcher = _image.Dispatcher;
        if (dispatcher.CheckAccess())
        {
            PresentCore(width, height, bgra);
        }
        else
        {
            dispatcher.BeginInvoke(() => PresentCore(width, height, bgra));
        }
    }

    private void PresentCore(int width, int height, byte[] bgra)
    {
        if (_bitmap is null || _width != width || _height != height)
        {
            _width = width;
            _height = height;
            _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            _image.Source = _bitmap;
        }

        _bitmap.WritePixels(new Int32Rect(0, 0, width, height), bgra, width * 4, 0);
        FramesRendered++;
    }

    /// <summary>Clear the last frame, for example when the session ends.</summary>
    public void Clear()
    {
        var dispatcher = _image.Dispatcher;
        if (!dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(Clear);
            return;
        }

        _bitmap = null;
        _image.Source = null;
        _width = 0;
        _height = 0;
    }

    /// <summary>
    /// Map a point in the displayed image back to source pixels, accounting for the uniform stretch.
    /// Returns false when the point is inside the letterbox bars rather than the picture.
    /// </summary>
    public bool TryMapToSource(Point pointInImage, out int sourceX, out int sourceY)
    {
        sourceX = 0;
        sourceY = 0;
        if (_width <= 0 || _height <= 0)
        {
            return false;
        }

        double availableWidth = _image.ActualWidth;
        double availableHeight = _image.ActualHeight;
        if (availableWidth <= 0 || availableHeight <= 0)
        {
            return false;
        }

        double scale = Math.Min(availableWidth / _width, availableHeight / _height);
        double drawnWidth = _width * scale;
        double drawnHeight = _height * scale;
        double offsetX = (availableWidth - drawnWidth) / 2;
        double offsetY = (availableHeight - drawnHeight) / 2;

        double x = (pointInImage.X - offsetX) / scale;
        double y = (pointInImage.Y - offsetY) / scale;
        if (x < 0 || y < 0 || x >= _width || y >= _height)
        {
            return false;
        }

        sourceX = (int)x;
        sourceY = (int)y;
        return true;
    }
}