using System.Windows.Forms;

namespace DeadlockAverageSpeedOverlay;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new SpeedOverlayForm());
    }
}
