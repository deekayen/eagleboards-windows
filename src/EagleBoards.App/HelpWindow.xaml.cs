using System.Windows;

namespace EagleBoards.App;

public partial class HelpWindow : Window
{
    public HelpWindow()
    {
        InitializeComponent();
        Title = $"Help - Review Board Scheduler {AppVersion.Text}";
    }
}
