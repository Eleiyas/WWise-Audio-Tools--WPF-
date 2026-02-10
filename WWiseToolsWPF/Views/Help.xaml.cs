using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Navigation;

namespace WWiseToolsWPF.Views
{
    public partial class Help : UserControl
    {
        public Help()
        {
            InitializeComponent();
        }

        private void OnLinkClick(object sender, RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = e.Uri.AbsoluteUri,
                UseShellExecute = true
            });

            e.Handled = true;
        }
    }
}