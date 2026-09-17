using System.Windows.Controls;

namespace AvasRoutingApp.Views
{
    /// <summary>
    /// Interaction logic for OverlaySidebarView.xaml
    /// </summary>
    public partial class OverlaySidebarView : UserControl
    {
        public event System.EventHandler? CloseRequested;

        public OverlaySidebarView()
        {
            InitializeComponent();
        }

        private void BtnCloseSidebar_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            CloseRequested?.Invoke(this, System.EventArgs.Empty);
        }
    }
}
