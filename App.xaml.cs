using System.Windows;

namespace AudioStreamer;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Nudge the scheduler so the capture / UDP-receive loops are less likely to be preempted under
        // load, which reduces audio jitter. AboveNormal is deliberate: High risks starving the OS audio
        // service (audiodg) and the UI, and RealTime needs admin. Best-effort — never block startup.
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            process.PriorityClass = System.Diagnostics.ProcessPriorityClass.AboveNormal;
        }
        catch { /* priority is a nicety, not a requirement */ }

        base.OnStartup(e);
    }
}
