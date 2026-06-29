using ArcGIS.Desktop.Framework.Contracts;
using ArcGisProAppYolo.DockPanes;
using System;

namespace ArcGisProAppYolo.Controls
{
    internal class ShowCreateDatasetDock : Button
    {
        protected override void OnClick()
        {
            ArcGisProAppYolo.Tools.Logger.Log("INFO: ShowCreateDatasetDock clicked");

            try
            {
                CreateDatasetDockPaneViewModel.Show();
                ArcGisProAppYolo.Tools.Logger.Log("INFO: CreateDatasetDockPaneViewModel.Show() invoked");
            }
            catch (Exception ex)
            {
                ArcGisProAppYolo.Tools.Logger.Log($"ERROR: Failed to open Create Dataset pane: {ex}");
                throw;
            }
        }
    }
}
