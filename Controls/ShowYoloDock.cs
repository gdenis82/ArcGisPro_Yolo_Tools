using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Contracts;
using ArcGisProAppYolo.DockPanes;
using System;

namespace ArcGisProAppYolo.Controls
{
    internal class ShowYoloDock : Button
    {
        protected override void OnClick()
        {
            ArcGisProAppYolo.Tools.Logger.Log("ShowYoloDock clicked");

            try
            {
                ArcGisProAppYolo.DockPanes.YoloDockPaneViewModel.Show();
                ArcGisProAppYolo.Tools.Logger.Log("YoloDockPaneViewModel.Show() invoked");
            }
            catch (Exception ex)
            {
                ArcGisProAppYolo.Tools.Logger.Log($"Error invoking Show(): {ex}");
                System.Diagnostics.Debug.WriteLine($"ERROR: {ex}");
                throw;
            }
        }
    }
}
