using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Contracts;
using ArcGIS.Desktop.Core;
using System.Collections.ObjectModel;
using System.Windows.Input;
using System.IO;
using System;
using System.Linq;
using Microsoft.Win32;
using System.ComponentModel;
using System.Windows;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Text;

namespace ArcGisProAppYolo.DockPanes
{
    internal class YoloDockPaneViewModel : DockPane
    {
        private const string _dockPaneID = "ArcGisProAppYolo_YoloDockPane";
        private readonly string _settingsPath;
        private UserSettingsData _settingsData = new UserSettingsData();
        private CancellationTokenSource _runCts;
        private const int MaxModelHistoryItems = 20;

        protected YoloDockPaneViewModel() 
        {
            Tools.Logger.Log("YoloDockPaneViewModel constructor started");
            _settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ESRI", "ArcGISPro", "opp_yolo_tool", "ui_settings.json");
            OrthoList = new ObservableCollection<string>();
            KnownModels = new ObservableCollection<ModelHistoryItem>();
            LoadProjectInfo();
            LoadUserSettings();
            Tools.Logger.Log("YoloDockPaneViewModel constructor completed");
        }

        /// <summary>
        /// Show the DockPane.
        /// </summary>
        internal static void Show()
        {
            Tools.Logger.Log("DockPane Show() called");
            var pane = FrameworkApplication.DockPaneManager.Find(_dockPaneID);
            if (pane == null)
            {
                Tools.Logger.Log("DockPane not found by id: " + _dockPaneID);
                return;
            }
            pane.Activate();
            Tools.Logger.Log("DockPane activated");
        }

        internal static void HidePane()
        {
            try
            {
                var pane = FrameworkApplication.DockPaneManager.Find(_dockPaneID);
                if (pane == null)
                    return;
                if (pane.IsVisible)
                    pane.Hide();
            }
            catch (Exception ex)
            {
                Tools.Logger.Log($"HidePane failed: {ex}");
            }
        }

        private static string NormalizeMaskMode(string value)
        {
            if (string.Equals(value, "union", StringComparison.OrdinalIgnoreCase))
                return "union";
            return "largest";
        }

        private void SetMaskMode(string value)
        {
            var normalized = NormalizeMaskMode(value);
            if (string.Equals(_maskMode, normalized, StringComparison.OrdinalIgnoreCase))
                return;

            _maskMode = normalized;
            NotifyPropertyChanged(nameof(MaskMode));
            NotifyPropertyChanged(nameof(MaskModeLargest));
            NotifyPropertyChanged(nameof(MaskModeUnion));
        }

        private static string NormalizeModelPath(string value)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(value))
                    return string.Empty;
                return Path.GetFullPath(value.Trim());
            }
            catch
            {
                return value?.Trim() ?? string.Empty;
            }
        }

        private void SyncSelectedKnownModelByPath(string value)
        {
            var normalized = NormalizeModelPath(value);
            var match = KnownModels.FirstOrDefault(m => string.Equals(NormalizeModelPath(m?.FullPath), normalized, StringComparison.OrdinalIgnoreCase));
            if (!ReferenceEquals(_selectedKnownModel, match))
            {
                _selectedKnownModel = match;
                NotifyPropertyChanged(nameof(SelectedKnownModel));
            }
        }

        private void RememberModelPath(string path)
        {
            var normalized = NormalizeModelPath(path);
            if (string.IsNullOrWhiteSpace(normalized))
                return;
            if (!File.Exists(normalized))
                return;

            for (int i = KnownModels.Count - 1; i >= 0; i--)
            {
                if (string.Equals(NormalizeModelPath(KnownModels[i]?.FullPath), normalized, StringComparison.OrdinalIgnoreCase))
                    KnownModels.RemoveAt(i);
            }

            KnownModels.Insert(0, new ModelHistoryItem { FullPath = normalized });

            while (KnownModels.Count > MaxModelHistoryItems)
                KnownModels.RemoveAt(KnownModels.Count - 1);

            SyncSelectedKnownModelByPath(normalized);
        }

        private void LoadProjectInfo()
        {
            Tools.Logger.Log("LoadProjectInfo started");
            try
            {
                var prevSelected = SelectedOrtho;
                ProjectUri = Project.Current?.URI ?? string.Empty;
                Tools.Logger.Log($"ProjectUri: {ProjectUri}");
                NotifyPropertyChanged(nameof(ProjectUri));

                // Очистка списка перед повторной загрузкой
                OrthoList.Clear();

                if (!string.IsNullOrEmpty(ProjectUri))
                {
                    var projectDir = Path.GetDirectoryName(ProjectUri);
                    var orthoRoot = Path.Combine(projectDir, "OrthoMapping");
                    if (Directory.Exists(orthoRoot))
                    {
                        var dirs = Directory.GetDirectories(orthoRoot);
                        foreach (var d in dirs)
                        {
                            OrthoList.Add(Path.GetFileName(d));
                        }
                        if (OrthoList.Count == 0)
                        {
                            var files = Directory.GetFiles(orthoRoot);
                            foreach (var f in files)
                            {
                                OrthoList.Add(Path.GetFileName(f));
                            }
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(prevSelected) && OrthoList.Contains(prevSelected))
                    SelectedOrtho = prevSelected;
                else if (OrthoList.Count > 0)
                    SelectedOrtho = OrthoList[0];
                else
                    SelectedOrtho = string.Empty;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading project info: {ex.Message}");
                Tools.Logger.Log($"Error loading project info: {ex}");
            }

            IsRunEnabled = OrthoList.Count > 0;
            Tools.Logger.Log($"LoadProjectInfo completed. OrthoList count: {OrthoList.Count}, IsRunEnabled: {IsRunEnabled}");
        }

        #region Properties

        private int _tileSize = 640;
        public int TileSize 
        { 
            get => _tileSize; 
            set => SetProperty(ref _tileSize, value, () => TileSize); 
        }

        private int _overlapPercent = 30;
        public int OverlapPercent 
        { 
            get => _overlapPercent; 
            set => SetProperty(ref _overlapPercent, value, () => OverlapPercent); 
        }

        private string _projectUri;
        public string ProjectUri 
        { 
            get => _projectUri; 
            set => SetProperty(ref _projectUri, value, () => ProjectUri); 
        }

        public ObservableCollection<string> OrthoList { get; }

        public ObservableCollection<ModelHistoryItem> KnownModels { get; }

        private string _selectedOrtho;
        public string SelectedOrtho 
        { 
            get => _selectedOrtho; 
            set => SetProperty(ref _selectedOrtho, value, () => SelectedOrtho); 
        }

        private string _modelPath;
        public string ModelPath 
        { 
            get => _modelPath; 
            set
            {
                var normalized = NormalizeModelPath(value);
                if (SetProperty(ref _modelPath, normalized, () => ModelPath))
                {
                    SyncSelectedKnownModelByPath(normalized);
                }
            }
        }

        private ModelHistoryItem _selectedKnownModel;
        public ModelHistoryItem SelectedKnownModel
        {
            get => _selectedKnownModel;
            set
            {
                if (SetProperty(ref _selectedKnownModel, value, () => SelectedKnownModel))
                {
                    var selectedPath = NormalizeModelPath(value?.FullPath);
                    if (!string.IsNullOrWhiteSpace(selectedPath)
                        && !string.Equals(ModelPath, selectedPath, StringComparison.OrdinalIgnoreCase))
                    {
                        ModelPath = selectedPath;
                    }
                }
            }
        }

        private string _maskMode = "largest";
        public bool MaskModeLargest
        {
            get => string.Equals(_maskMode, "largest", StringComparison.OrdinalIgnoreCase);
            set
            {
                if (!value)
                    return;
                SetMaskMode("largest");
            }
        }

        public bool MaskModeUnion
        {
            get => string.Equals(_maskMode, "union", StringComparison.OrdinalIgnoreCase);
            set
            {
                if (!value)
                    return;
                SetMaskMode("union");
            }
        }

        public string MaskMode => _maskMode;

        private double _confidence = 0.5;
        public double Confidence 
        { 
            get => _confidence; 
            set => SetProperty(ref _confidence, value, () => Confidence); 
        }

        private bool _outputPoints = true;
        public bool OutputPoints 
        { 
            get => _outputPoints; 
            set => SetProperty(ref _outputPoints, value, () => OutputPoints); 
        }

        private bool _outputMasks = false;
        public bool OutputMasks 
        { 
            get => _outputMasks; 
            set => SetProperty(ref _outputMasks, value, () => OutputMasks); 
        }

        private bool _outputBBoxes = true;
        public bool OutputBBoxes 
        { 
            get => _outputBBoxes; 
            set => SetProperty(ref _outputBBoxes, value, () => OutputBBoxes); 
        }

        private bool _useSahi = true;
        public bool UseSahi
        {
            get => _useSahi;
            set => SetProperty(ref _useSahi, value, () => UseSahi);
        }

        private bool _isRunEnabled = false;
        public bool IsRunEnabled 
        { 
            get => _isRunEnabled; 
            set => SetProperty(ref _isRunEnabled, value, () => IsRunEnabled); 
        }

        private bool _isRunning = false;
        public bool IsRunning 
        { 
            get => _isRunning; 
            set 
            { 
                SetProperty(ref _isRunning, value, () => IsRunning);
                NotifyPropertyChanged(nameof(RunButtonText));
                ((RelayCommand)RunCommand)?.RaiseCanExecuteChanged();
            }
        }

        public string RunButtonText => IsRunning ? "Cancel" : "Run Detection";

        private string _progressText = string.Empty;
        public string ProgressText 
        { 
            get => _progressText; 
            set => SetProperty(ref _progressText, value, () => ProgressText); 
        }

        private readonly StringBuilder _progressLogBuilder = new StringBuilder(16 * 1024);

        private void ResetProgressLog(string initialLine = null)
        {
            _progressLogBuilder.Clear();
            if (!string.IsNullOrWhiteSpace(initialLine))
                _progressLogBuilder.AppendLine(initialLine);
            ProgressText = _progressLogBuilder.ToString();
        }

        private void AppendProgressLine(string line)
        {
            if (line == null)
                return;

            if (_progressLogBuilder.Length > 200_000)
            {
                _progressLogBuilder.Clear();
                _progressLogBuilder.AppendLine("...log truncated...");
            }

            _progressLogBuilder.AppendLine(line);
            ProgressText = _progressLogBuilder.ToString();
        }

        public sealed class ModelHistoryItem
        {
            public string FullPath { get; set; } = string.Empty;
            public string DisplayName => Path.GetFileName(FullPath ?? string.Empty);
            public override string ToString() => DisplayName;
        }

        #endregion

        #region Commands

        private ICommand _runCommand;
        public ICommand RunCommand => _runCommand ??= new RelayCommand((o) => OnRunOrCancel(), (o) => IsRunEnabled);

        private ICommand _browseCommand;
        public ICommand BrowseCommand => _browseCommand ??= new RelayCommand((o) => OnBrowse());

        private ICommand _refreshOrthoListCommand;
        public ICommand RefreshOrthoListCommand => _refreshOrthoListCommand ??= new RelayCommand((o) => LoadProjectInfo());

        #endregion

        #region Command Handlers

        private void OnRunOrCancel()
        {
            if (IsRunning)
            {
                RequestCancel();
                return;
            }

            _ = RunAsync();
        }

        private void RequestCancel()
        {
            try
            {
                var cts = _runCts;
                if (cts == null || cts.IsCancellationRequested)
                    return;

                Tools.Logger.Log("INFO: Cancellation requested by user.");
                ProgressText = "Cancelling...";
                cts.Cancel();
            }
            catch (Exception ex)
            {
                Tools.Logger.Log($"ERROR: Failed to request cancellation: {ex}");
            }
        }

        private async System.Threading.Tasks.Task RunAsync()
        {
            if (!IsRunEnabled || IsRunning) return;

            ModelPath = NormalizeModelPath(ModelPath);
            if (!string.IsNullOrWhiteSpace(ModelPath))
            {
                RememberModelPath(ModelPath);
            }

            _runCts?.Dispose();
            _runCts = new CancellationTokenSource();
            var cancellationToken = _runCts.Token;

            IsRunning = true;
            ResetProgressLog("Preparing...");

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                string projectUri = Project.Current?.URI ?? string.Empty;
                if (string.IsNullOrEmpty(projectUri) || string.IsNullOrEmpty(SelectedOrtho))
                {
                    AppendProgressLine("Project or Ortho not selected.");
                    return;
                }

                string projectDir = Path.GetDirectoryName(projectUri);
                Tools.Logger.Log($"Project directory: {projectDir}");
                Tools.Logger.Log($"Selected ortho: '{SelectedOrtho}'");

                // Папка ортофотоплана
                string orthoFolder = Path.Combine(projectDir, "OrthoMapping", SelectedOrtho);
                Tools.Logger.Log($"Ortho folder: {orthoFolder}");

                // Файлы ортофото ищем в Products/Orthos
                string orthoProductsFolder = Path.Combine(orthoFolder, "Products", "Orthos");
                Tools.Logger.Log($"Searching ortho files in: {orthoProductsFolder}");
                string orthoImagePath = null;

                if (Directory.Exists(orthoProductsFolder))
                {
                    var exts = new[] { ".tif", ".tiff", ".img", ".jpg", ".jpeg", ".png", ".vrt" };
                    var files = Directory.GetFiles(orthoProductsFolder, "*.*", SearchOption.TopDirectoryOnly)
                        .Where(f => exts.Contains(Path.GetExtension(f).ToLowerInvariant())).ToArray();
                    if (files.Length > 0)
                    {
                        orthoImagePath = files[0];
                        Tools.Logger.Log($"Ortho found: {orthoImagePath}");
                    }
                }
                else
                {
                    Tools.Logger.Log($"Products/Orthos folder not found: {orthoProductsFolder}");
                }

                if (string.IsNullOrEmpty(orthoImagePath) || !File.Exists(orthoImagePath))
                {
                    AppendProgressLine("Ortho image not found in selected Ortho.");
                    return;
                }

                AppendProgressLine("Preparing Tiles folder...");
                // SelectedOrtho - это папка ортофотоплана как есть
                var eomwFolder = Path.Combine(projectDir, "OrthoMapping", SelectedOrtho);
                Tools.Logger.Log($"EOMW folder: {eomwFolder}");
                Directory.CreateDirectory(eomwFolder);
                var tilesRoot = Tools.TileGenerator.PrepareTilesFolderInEomw(eomwFolder, TileSize);
                Tools.Logger.Log($"Tiles root: {tilesRoot}");

                var useExistingTiles = false;
                var imagesFolder = Path.Combine(tilesRoot, "Images");
                var existingTilesFound = HasExistingTiles(imagesFolder);
                if (existingTilesFound)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var answer = MessageBox.Show(
                        $"Тайлы для размера {TileSize}px уже существуют. Использовать существующие?\n\nДа — использовать существующие\nНет — сформировать заново\nОтмена — прервать операцию",
                        "Тайлы уже существуют",
                        MessageBoxButton.YesNoCancel,
                        MessageBoxImage.Question);

                    if (answer == MessageBoxResult.Cancel)
                    {
                        AppendProgressLine("Операция отменена пользователем.");
                        return;
                    }

                    if (answer == MessageBoxResult.Yes)
                    {
                        useExistingTiles = true;
                        Tools.Logger.Log($"Using existing tiles for size {TileSize}px: {tilesRoot}");
                    }
                    else
                    {
                        try
                        {
                            Directory.Delete(tilesRoot, true);
                            tilesRoot = Tools.TileGenerator.PrepareTilesFolderInEomw(eomwFolder, TileSize);
                            Tools.Logger.Log($"Recreated tiles folder for size {TileSize}px: {tilesRoot}");
                        }
                        catch (Exception ex)
                        {
                            AppendProgressLine($"Failed to recreate tiles folder: {ex.Message}");
                            Tools.Logger.Log($"Failed to recreate tiles folder: {ex}");
                            return;
                        }
                    }
                }

                var ok = true;
                if (!useExistingTiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AppendProgressLine("Generating tiles (this may take a while)...");
                    ok = await Tools.TileGenerator.GenerateTilesAsync(orthoImagePath, tilesRoot, TileSize, OverlapPercent, cancellationToken);
                }
                else
                {
                    AppendProgressLine("Using existing tiles.");
                }

                if (ok)
                {
                    AppendProgressLine("Tiles generation completed.");

                    AppendProgressLine("Running predictions...");

                    string predictScript = null;
                    var projectRoot = Path.GetDirectoryName(Project.Current?.URI ?? string.Empty);
                    var projectScript = Path.Combine(projectRoot ?? string.Empty, "opp_yolo_tool", "predict_module.py");
                    var appDataPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "ESRI", "ArcGISPro", "opp_yolo_tool", "predict_module.py");

                    if (File.Exists(projectScript))
                    {
                        predictScript = projectScript;
                        Tools.Logger.Log($"Using predict_module.py from project: {predictScript}");
                    }
                    else if (File.Exists(appDataPath))
                    {
                        predictScript = appDataPath;
                        Tools.Logger.Log($"Using predict_module.py from AppData: {predictScript}");
                    }
                    else
                    {
                        var asmFolder = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                        predictScript = Path.GetFullPath(Path.Combine(asmFolder ?? string.Empty, "..", "..", "opp_yolo_tool", "predict_module.py"));
                    }

                    if (File.Exists(predictScript))
                    {
                        var outputsList = new System.Collections.Generic.List<string>();
                        if (OutputPoints) outputsList.Add("point");
                        if (OutputMasks) outputsList.Add("mask");
                        if (OutputBBoxes) outputsList.Add("bbox");
                        var outputsArg = string.Join(",", outputsList);

                        SaveUserSettings();

                        // Ensure model path exists
                        if (string.IsNullOrEmpty(ModelPath) || !File.Exists(ModelPath))
                        {
                            AppendProgressLine("Model file not specified or not found.");
                            Tools.Logger.Log($"ModelPath invalid: {ModelPath}");
                        }
                        else
                        {
                            // Use invariant culture for numeric args to ensure dot decimal separator
                            var confidenceStr = Confidence.ToString(System.Globalization.CultureInfo.InvariantCulture);
                            var sahiArg = UseSahi ? " --use-sahi" : " --no-sahi";
                            var args = $"--tiles-dir \"{tilesRoot}\" --model \"{ModelPath}\" --confidence {confidenceStr} --tile-size {TileSize} --outputs {outputsArg} --mask-mode {MaskMode}{sahiArg}";
                            Tools.Logger.Log($"Running predict_module with args: {args}");

                            var stdout = new System.Text.StringBuilder();
                            var stderr = new System.Text.StringBuilder();
                            string experimentDir = null;

                            cancellationToken.ThrowIfCancellationRequested();
                            var exit = await Tools.PythonRunner.RunPythonScriptAsync(null, predictScript, args,
                                (s) =>
                                {
                                    stdout.AppendLine(s);
                                    AppendProgressLine(s);
                                    if (!string.IsNullOrWhiteSpace(s) && s.StartsWith("EXPERIMENT_DIR=", StringComparison.OrdinalIgnoreCase))
                                    {
                                        experimentDir = s.Substring("EXPERIMENT_DIR=".Length).Trim();
                                    }
                                },
                                (e) => { stderr.AppendLine(e); AppendProgressLine(e); },
                                cancellationToken);

                            // write predict logs to experiment folder (fallback: Detection_Results root)
                            try
                            {
                                var predictLogsFolder = experimentDir;
                                if (string.IsNullOrWhiteSpace(predictLogsFolder) || !Directory.Exists(predictLogsFolder))
                                {
                                    predictLogsFolder = Path.Combine(eomwFolder, "Detection_Results");
                                }
                                Directory.CreateDirectory(predictLogsFolder);
                                File.WriteAllText(Path.Combine(predictLogsFolder, "predict_stdout.log"), stdout.ToString());
                                File.WriteAllText(Path.Combine(predictLogsFolder, "predict_stderr.log"), stderr.ToString());
                            }
                            catch { }

                            if (exit == 0)
                            {
                                var detectionFolder = !string.IsNullOrWhiteSpace(experimentDir) && Directory.Exists(experimentDir)
                                    ? experimentDir
                                    : Path.Combine(eomwFolder, "Detection_Results");
                                Tools.Logger.Log($"Detection results folder: {detectionFolder}");
                                AppendProgressLine("Predictions completed.");
                            }
                            else if (exit == -2)
                            {
                                AppendProgressLine("Operation cancelled.");
                                Tools.Logger.Log("INFO: Prediction process cancelled by user.");
                            }
                            else
                            {
                                AppendProgressLine($"Predictions failed (exit {exit}). See logs.");
                            }
                        }

                    }
                    else
                    {
                        AppendProgressLine("predict_module.py not found; skipping predictions.");
                    }
                }
                else
                {
                    AppendProgressLine("Tiles generation failed. See logs in Tiles folder.");
                }
            }
            catch (OperationCanceledException)
            {
                AppendProgressLine("Operation cancelled.");
                Tools.Logger.Log("INFO: RunDetection operation cancelled.");
            }
            catch (Exception ex)
            {
                AppendProgressLine($"Error: {ex.Message}");
                Tools.Logger.Log($"Error in RunAsync: {ex}");
            }
            finally
            {
                _runCts?.Dispose();
                _runCts = null;
                IsRunning = false;
            }
        }

        private void OnBrowse()
        {
            var dlg = new OpenFileDialog();
            dlg.Filter = "Model files (*.pt;*.onnx)|*.pt;*.onnx|All files (*.*)|*.*";
            dlg.Title = "Select YOLO model file";
            var res = dlg.ShowDialog();
            if (res == true)
            {
                ModelPath = NormalizeModelPath(dlg.FileName);
                RememberModelPath(ModelPath);
                SaveUserSettings();
            }
        }

        private static bool HasExistingTiles(string imagesFolder)
        {
            if (!Directory.Exists(imagesFolder))
                return false;

            var exts = new[] { ".png", ".jpg", ".jpeg", ".tif", ".tiff" };
            return Directory.EnumerateFiles(imagesFolder, "*.*", SearchOption.TopDirectoryOnly)
                .Any(f => exts.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));
        }

        #endregion

        #region User Settings

        private sealed class UserSettingsData
        {
            public GlobalSettings Global { get; set; } = new GlobalSettings();
            public Dictionary<string, ProjectSettings> Projects { get; set; } = new Dictionary<string, ProjectSettings>(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class GlobalSettings
        {
            public string ModelPath { get; set; } = string.Empty;
            public List<string> ModelHistory { get; set; } = new List<string>();
            public int TileSize { get; set; } = 640;
            public int OverlapPercent { get; set; } = 30;
            public bool OutputPoints { get; set; } = true;
            public bool OutputMasks { get; set; } = true;
            public bool OutputBBoxes { get; set; } = true;
            public bool UseSahi { get; set; } = true;
            public string MaskMode { get; set; } = "largest";
        }

        private sealed class ProjectSettings
        {
            public string SelectedOrtho { get; set; } = string.Empty;
        }

        private string GetCurrentProjectKey()
        {
            try
            {
                var uri = Project.Current?.URI ?? string.Empty;
                if (string.IsNullOrWhiteSpace(uri))
                    return string.Empty;
                var dir = Path.GetDirectoryName(uri) ?? string.Empty;
                return Path.GetFullPath(dir).ToLowerInvariant();
            }
            catch
            {
                return string.Empty;
            }
        }

        private void LoadUserSettings()
        {
            try
            {
                if (File.Exists(_settingsPath))
                {
                    var json = File.ReadAllText(_settingsPath);
                    var loaded = JsonSerializer.Deserialize<UserSettingsData>(json);
                    if (loaded != null)
                        _settingsData = loaded;
                }
            }
            catch (Exception ex)
            {
                Tools.Logger.Log($"LoadUserSettings error: {ex}");
            }

            var g = _settingsData.Global ?? new GlobalSettings();
            _settingsData.Global = g;

            KnownModels.Clear();
            if (g.ModelHistory != null)
            {
                foreach (var item in g.ModelHistory)
                {
                    var path = NormalizeModelPath(item);
                    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                        continue;
                    if (KnownModels.Any(m => string.Equals(NormalizeModelPath(m.FullPath), path, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    KnownModels.Add(new ModelHistoryItem { FullPath = path });
                }
            }

            if (!string.IsNullOrWhiteSpace(g.ModelPath))
                ModelPath = NormalizeModelPath(g.ModelPath);
            else if (KnownModels.Count > 0)
                ModelPath = KnownModels[0].FullPath;

            TileSize = g.TileSize > 0 ? g.TileSize : TileSize;
            OverlapPercent = Math.Max(0, Math.Min(95, g.OverlapPercent));
            OutputPoints = g.OutputPoints;
            OutputMasks = g.OutputMasks;
            OutputBBoxes = g.OutputBBoxes;
            UseSahi = g.UseSahi;
            SetMaskMode(g.MaskMode);

            if (!string.IsNullOrWhiteSpace(ModelPath))
                RememberModelPath(ModelPath);

            var projectKey = GetCurrentProjectKey();
            if (!string.IsNullOrWhiteSpace(projectKey)
                && _settingsData.Projects != null
                && _settingsData.Projects.TryGetValue(projectKey, out var p)
                && !string.IsNullOrWhiteSpace(p?.SelectedOrtho))
            {
                if (OrthoList.Contains(p.SelectedOrtho))
                    SelectedOrtho = p.SelectedOrtho;
            }

            if (string.IsNullOrWhiteSpace(SelectedOrtho) && OrthoList.Count > 0)
                SelectedOrtho = OrthoList[0];
        }

        private void SaveUserSettings()
        {
            try
            {
                _settingsData ??= new UserSettingsData();
                _settingsData.Global ??= new GlobalSettings();
                _settingsData.Projects ??= new Dictionary<string, ProjectSettings>(StringComparer.OrdinalIgnoreCase);

                _settingsData.Global.ModelPath = ModelPath ?? string.Empty;
                _settingsData.Global.ModelHistory = KnownModels
                    .Select(m => NormalizeModelPath(m?.FullPath))
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(MaxModelHistoryItems)
                    .ToList();
                _settingsData.Global.TileSize = TileSize;
                _settingsData.Global.OverlapPercent = OverlapPercent;
                _settingsData.Global.OutputPoints = OutputPoints;
                _settingsData.Global.OutputMasks = OutputMasks;
                _settingsData.Global.OutputBBoxes = OutputBBoxes;
                _settingsData.Global.UseSahi = UseSahi;
                _settingsData.Global.MaskMode = MaskMode;

                var projectKey = GetCurrentProjectKey();
                if (!string.IsNullOrWhiteSpace(projectKey))
                {
                    if (!_settingsData.Projects.TryGetValue(projectKey, out var p) || p == null)
                    {
                        p = new ProjectSettings();
                        _settingsData.Projects[projectKey] = p;
                    }
                    p.SelectedOrtho = SelectedOrtho ?? string.Empty;
                }

                var dir = Path.GetDirectoryName(_settingsPath);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);

                var json = JsonSerializer.Serialize(_settingsData, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_settingsPath, json);
            }
            catch (Exception ex)
            {
                Tools.Logger.Log($"SaveUserSettings error: {ex}");
            }
        }

        #endregion
    }
}
