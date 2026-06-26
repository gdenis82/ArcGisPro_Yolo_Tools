using System.Windows.Controls;

namespace ArcGisProAppYolo.DockPanes
{
    /// <summary>
    /// Interaction logic for YoloDockPaneView.xaml
    /// </summary>
    public partial class YoloDockPaneView : UserControl
    {
        public YoloDockPaneView()
        {
            InitializeComponent();
            Tools.Logger.Log("YoloDockPaneView constructor called");
        }
    }
}
