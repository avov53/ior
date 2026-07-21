using System.Windows.Forms;

namespace HappBox;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var instance = new SingleInstanceCoordinator();
        if (!instance.IsPrimary)
        {
            instance.SignalPrimary();
            return;
        }

        using var form = new MainForm();
        instance.Listen(() =>
        {
            if (form.IsDisposed || !form.IsHandleCreated) return;
            form.BeginInvoke(new Action(form.RestoreFromTray));
        });
        Application.Run(form);
    }
}
