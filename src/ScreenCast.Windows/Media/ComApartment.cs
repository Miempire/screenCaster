using System.Runtime.InteropServices;

namespace ScreenCast.Windows.Media;

/// <summary>
/// Media Foundation MFTs need COM on the thread that creates and drives them. The capture/encode
/// worker owns that thread; the WPF UI thread is STAs, so the worker explicitly enters the MTA.
/// </summary>
public static class ComApartment
{
    private const uint CoInitMultithreaded = 0x0;
    private const int SFalse = 1;

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();

    /// <summary>Enters the multithreaded apartment. Dispose to balance the call when this scope initialized COM.</summary>
    public static IDisposable EnterMultithreaded()
    {
        var result = CoInitializeEx(IntPtr.Zero, CoInitMultithreaded);
        if (result is not (0 or SFalse))
            Marshal.ThrowExceptionForHR(result);
        return new Scope(result == 0);
    }

    private sealed class Scope(bool ownsInitialization) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed || !ownsInitialization)
                return;
            _disposed = true;
            CoUninitialize();
        }
    }
}
