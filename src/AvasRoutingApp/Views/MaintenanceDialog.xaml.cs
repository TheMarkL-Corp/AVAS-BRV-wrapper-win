using System.Windows;
using AvasRoutingApp.ViewModels;

namespace AvasRoutingApp.Views
{
    /// <summary>
    /// Interaction logic for MaintenanceDialog.xaml
    /// </summary>
    public partial class MaintenanceDialog : Window
    {
        private readonly MaintenanceViewModel _viewModel;

        public MaintenanceDialog(MaintenanceViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            DataContext = _viewModel;

            _viewModel.ConfirmActionCallback = PromptConfirmation;
            _viewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(MaintenanceViewModel.SelectedTarget))
                {
                    SyncTargetRadioButtons();
                }
            };
        }

        private void SyncTargetRadioButtons()
        {
            if (RbChip0 == null || RbChip1 == null || RbBoth == null) return;
            switch (_viewModel.SelectedTarget)
            {
                case TargetChipSelection.Chip0:
                    RbChip0.IsChecked = true;
                    break;
                case TargetChipSelection.Chip1:
                    RbChip1.IsChecked = true;
                    break;
                case TargetChipSelection.Both:
                    RbBoth.IsChecked = true;
                    break;
            }
        }

        private bool PromptConfirmation()
        {
            var result = MessageBox.Show(
                this,
                "Warning: This maintenance operation will interrupt active video streaming and reboot or restore hardware settings. Are you sure you wish to proceed?",
                "Confirm Hardware Operation",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            return result == MessageBoxResult.Yes;
        }

        private void RbChip0_Checked(object sender, RoutedEventArgs e)
        {
            if (_viewModel != null) _viewModel.SelectedTarget = TargetChipSelection.Chip0;
        }

        private void RbChip1_Checked(object sender, RoutedEventArgs e)
        {
            if (_viewModel != null) _viewModel.SelectedTarget = TargetChipSelection.Chip1;
        }

        private void RbBoth_Checked(object sender, RoutedEventArgs e)
        {
            if (_viewModel != null) _viewModel.SelectedTarget = TargetChipSelection.Both;
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
