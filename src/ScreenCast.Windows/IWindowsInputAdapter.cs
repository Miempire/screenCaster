namespace ScreenCast.Windows;

// Contract only. No implementation is registered or invoked by this prototype.
// A future SendInput adapter must require an explicit Host input lease and release held keys on exit.
public interface IWindowsInputAdapter : IDisposable
{
    void AcquireLease(string viewerId);
    void SendScanCode(ushort scanCode, bool extended, bool isDown);
    void MovePointer(ushort normalizedX, ushort normalizedY);
    void ReleaseAll();
}
