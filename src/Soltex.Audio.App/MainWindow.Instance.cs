using System.Windows;

namespace Soltex.Audio.App;

public partial class MainWindow
{
    internal void ShowMixer()
    {
        if (_closing || _closed) return;
        Show(); WindowState = WindowState.Normal; Activate(); UpdatePresentation();
    }
    internal void QuitMixer() { _quit = true; Close(); }
}
