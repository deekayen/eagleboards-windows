using System.Windows;

namespace EagleBoards.App;

public partial class HelpWindow : Window
{
    public HelpWindow()
    {
        InitializeComponent();
        Title = $"Help - Eagle Board Scheduler {AppVersion.Text}";
    }
}
