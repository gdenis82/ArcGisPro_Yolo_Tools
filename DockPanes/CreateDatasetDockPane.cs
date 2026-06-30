using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Contracts;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Threading;
using System.ComponentModel;

namespace ArcGisProAppYolo.DockPanes
{
    internal class CreateDatasetDockPaneViewModel : DockPane
    {
        private const string DockPaneId = "ArcGisProAppYolo_CreateDatasetDockPane";
        private const int MaxPresetHistoryItems = 20;
        private readonly string _settingsPath;
        private bool _isAdjustingSplit;
        private bool _isRunning;
        private bool _isPreviewApplying;
        private bool _isUpdatingPreviewOperationSelection;
        private UserSettingsData _settingsData = new UserSettingsData();
        private int _previewOperationIndex = -1;
        private int _previewImageIndex = -1;
        private List<string> _previewImagePaths = new List<string>();

        protected CreateDatasetDockPaneViewModel()
        {
            Tools.Logger.Log("INFO: CreateDatasetDockPaneViewModel constructor started");

            _settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ESRI", "ArcGISPro", "opp_yolo_tool", "create_dataset_ui_settings.json");

            OrthoList = new ObservableCollection<string>();
            AnnotationLayers = new ObservableCollection<AnnotationLayerSelection>();
            BalanceMethods = new ObservableCollection<string> { "Median", "Average", "Minimum" };
            DatasetTypes = new ObservableCollection<string> { "Detection", "Segmentation", "OBB" };
            AvailablePresets = new ObservableCollection<string> { "None", "Light", "Standard", "Aggressive", "Outdoor Aerial" };

            GeometryAugmentations = new ObservableCollection<AugmentationOption>();
            ColorAugmentations = new ObservableCollection<AugmentationOption>();
            NoiseAugmentations = new ObservableCollection<AugmentationOption>();
            AdvancedAugmentations = new ObservableCollection<AugmentationOption>();
            EnabledPreviewOperations = new ObservableCollection<PreviewOperationItem>();

            InitAugmentationCollections();
            LoadProjectInfo();
            _ = RefreshAnnotationLayersAsync();
            LoadUserSettings();
            RecalculateEstimatedStatistics();

            Tools.Logger.Log("INFO: CreateDatasetDockPaneViewModel constructor completed");
        }

        internal static void Show()
        {
            var pane = FrameworkApplication.DockPaneManager.Find(DockPaneId);
            if (pane == null)
            {
                Tools.Logger.Log($"WARN: Create Dataset pane not found by id: {DockPaneId}");
                return;
            }

            pane.Activate();
        }

        internal static void HidePane()
        {
            try
            {
                var pane = FrameworkApplication.DockPaneManager.Find(DockPaneId);
                if (pane?.IsVisible == true)
                    pane.Hide();
            }
            catch (Exception ex)
            {
                Tools.Logger.Log($"ERROR: Hide Create Dataset pane failed: {ex}");
            }
        }

        #region Properties

        public ObservableCollection<string> OrthoList { get; }
        public ObservableCollection<AnnotationLayerSelection> AnnotationLayers { get; }
        public ObservableCollection<string> DatasetTypes { get; }
        public ObservableCollection<string> BalanceMethods { get; }
        public ObservableCollection<string> AvailablePresets { get; }

        public ObservableCollection<AugmentationOption> GeometryAugmentations { get; }
        public ObservableCollection<AugmentationOption> ColorAugmentations { get; }
        public ObservableCollection<AugmentationOption> NoiseAugmentations { get; }
        public ObservableCollection<AugmentationOption> AdvancedAugmentations { get; }
        public ObservableCollection<PreviewOperationItem> EnabledPreviewOperations { get; }

        private string _selectedOrtho = string.Empty;
        public string SelectedOrtho
        {
            get => _selectedOrtho;
            set => SetProperty(ref _selectedOrtho, value, () => SelectedOrtho);
        }

        private int _tileSize = 640;
        public int TileSize
        {
            get => _tileSize;
            set
            {
                if (SetProperty(ref _tileSize, value, () => TileSize))
                    UpdateValidationWarnings();
            }
        }

        private PreviewOperationItem _selectedPreviewOperation;
        public PreviewOperationItem SelectedPreviewOperation
        {
            get => _selectedPreviewOperation;
            set
            {
                if (SetProperty(ref _selectedPreviewOperation, value, () => SelectedPreviewOperation))
                {
                    if (_isUpdatingPreviewOperationSelection)
                        return;

                    if (value != null)
                        _ = ApplySelectedPreviewOperationAsync(value);
                }
            }
        }

        private double _overlap = 0.2;
        public double Overlap
        {
            get => _overlap;
            set
            {
                if (SetProperty(ref _overlap, value, () => Overlap))
                    UpdateValidationWarnings();
            }
        }

        private string _selectedDatasetType = "Detection";
        public string SelectedDatasetType
        {
            get => _selectedDatasetType;
            set => SetProperty(ref _selectedDatasetType, value, () => SelectedDatasetType);
        }

        private int _trainPercent = 70;
        public int TrainPercent
        {
            get => _trainPercent;
            set
            {
                var clamped = ClampPercent(value);
                if (SetProperty(ref _trainPercent, clamped, () => TrainPercent))
                {
                    RebalanceSplit(SplitField.Train);
                    UpdateValidationWarnings();
                }
            }
        }

        private int _valPercent = 20;
        public int ValPercent
        {
            get => _valPercent;
            set
            {
                var clamped = ClampPercent(value);
                if (SetProperty(ref _valPercent, clamped, () => ValPercent))
                {
                    RebalanceSplit(SplitField.Val);
                    UpdateValidationWarnings();
                }
            }
        }

        private int _testPercent = 10;
        public int TestPercent
        {
            get => _testPercent;
            set
            {
                var clamped = ClampPercent(value);
                if (SetProperty(ref _testPercent, clamped, () => TestPercent))
                {
                    RebalanceSplit(SplitField.Test);
                    UpdateValidationWarnings();
                }
            }
        }

        private int _backgroundLimit = 20;
        public int BackgroundLimit
        {
            get => _backgroundLimit;
            set => SetProperty(ref _backgroundLimit, Math.Max(0, value), () => BackgroundLimit);
        }

        private bool _backgroundLimitIsPercent = true;
        public bool BackgroundLimitIsPercent
        {
            get => _backgroundLimitIsPercent;
            set => SetProperty(ref _backgroundLimitIsPercent, value, () => BackgroundLimitIsPercent);
        }

        private bool _enableClassBalancing = true;
        public bool EnableClassBalancing
        {
            get => _enableClassBalancing;
            set => SetProperty(ref _enableClassBalancing, value, () => EnableClassBalancing);
        }

        private string _selectedBalanceMethod = "Median";
        public string SelectedBalanceMethod
        {
            get => _selectedBalanceMethod;
            set => SetProperty(ref _selectedBalanceMethod, value, () => SelectedBalanceMethod);
        }

        private bool _debugMode;
        public bool DebugMode
        {
            get => _debugMode;
            set => SetProperty(ref _debugMode, value, () => DebugMode);
        }

        private bool _applyToVal;
        public bool ApplyToVal
        {
            get => _applyToVal;
            set => SetProperty(ref _applyToVal, value, () => ApplyToVal);
        }

        private bool _applyToTest;
        public bool ApplyToTest
        {
            get => _applyToTest;
            set => SetProperty(ref _applyToTest, value, () => ApplyToTest);
        }

        private int _seed;
        public int Seed
        {
            get => _seed;
            set => SetProperty(ref _seed, value, () => Seed);
        }

        private bool _isAugmentationExpanded = true;
        public bool IsAugmentationExpanded
        {
            get => _isAugmentationExpanded;
            set => SetProperty(ref _isAugmentationExpanded, value, () => IsAugmentationExpanded);
        }

        private string _previewTile = string.Empty;
        public string PreviewTile
        {
            get => _previewTile;
            set => SetProperty(ref _previewTile, value, () => PreviewTile);
        }

        private string _previewTilesFolder = string.Empty;
        public string PreviewTilesFolder
        {
            get => _previewTilesFolder;
            set
            {
                if (SetProperty(ref _previewTilesFolder, value, () => PreviewTilesFolder))
                {
                    LoadPreviewImagesFromFolder(value);
                    SaveUserSettings();
                }
            }
        }

        private string _previewAppliedTransforms = "No preview generated.";
        public string PreviewAppliedTransforms
        {
            get => _previewAppliedTransforms;
            set => SetProperty(ref _previewAppliedTransforms, value, () => PreviewAppliedTransforms);
        }

        private string _originalPreviewImagePath = string.Empty;
        public string OriginalPreviewImagePath
        {
            get => _originalPreviewImagePath;
            set => SetProperty(ref _originalPreviewImagePath, value, () => OriginalPreviewImagePath);
        }

        private string _augmentedPreviewImagePath = string.Empty;
        public string AugmentedPreviewImagePath
        {
            get => _augmentedPreviewImagePath;
            set => SetProperty(ref _augmentedPreviewImagePath, value, () => AugmentedPreviewImagePath);
        }

        private string _previewImagePosition = "0/0";
        public string PreviewImagePosition
        {
            get => _previewImagePosition;
            set => SetProperty(ref _previewImagePosition, value, () => PreviewImagePosition);
        }

        private string _validationWarning = string.Empty;
        public string ValidationWarning
        {
            get => _validationWarning;
            set => SetProperty(ref _validationWarning, value, () => ValidationWarning);
        }

        private string _splitWarning = string.Empty;
        public string SplitWarning
        {
            get => _splitWarning;
            set => SetProperty(ref _splitWarning, value, () => SplitWarning);
        }

        private int _estimatedSourceTiles = 0;
        public int EstimatedSourceTiles
        {
            get => _estimatedSourceTiles;
            set
            {
                var safe = Math.Max(0, value);
                if (SetProperty(ref _estimatedSourceTiles, safe, () => EstimatedSourceTiles))
                    RecalculateEstimatedStatistics();
            }
        }

        private int _datasetMultiplier = 1;
        public int DatasetMultiplier
        {
            get => _datasetMultiplier;
            set => SetProperty(ref _datasetMultiplier, Math.Max(1, value), () => DatasetMultiplier);
        }

        private int _estimatedTotalAfterAugmentation;
        public int EstimatedTotalAfterAugmentation
        {
            get => _estimatedTotalAfterAugmentation;
            set => SetProperty(ref _estimatedTotalAfterAugmentation, Math.Max(0, value), () => EstimatedTotalAfterAugmentation);
        }

        private string _estimatedGenerationTime = "N/A";
        public string EstimatedGenerationTime
        {
            get => _estimatedGenerationTime;
            set => SetProperty(ref _estimatedGenerationTime, value, () => EstimatedGenerationTime);
        }

        private string _datasetSizeWarning = string.Empty;
        public string DatasetSizeWarning
        {
            get => _datasetSizeWarning;
            set => SetProperty(ref _datasetSizeWarning, value, () => DatasetSizeWarning);
        }

        private string _progressText = string.Empty;
        public string ProgressText
        {
            get => _progressText;
            set => SetProperty(ref _progressText, value, () => ProgressText);
        }

        public bool IsRunEnabled => !string.IsNullOrWhiteSpace(SelectedOrtho)
            && AnnotationLayers.Any(a => a.IsSelected);

        public string SelectedAnnotationLayersSummary
        {
            get
            {
                var selectedCount = AnnotationLayers.Count(a => a.IsSelected);
                return $"Выбрано слоёв аннотаций: {selectedCount}";
            }
        }

        public bool IsRunning
        {
            get => _isRunning;
            set
            {
                if (SetProperty(ref _isRunning, value, () => IsRunning))
                {
                    NotifyPropertyChanged(nameof(RunButtonText));
                    ((RelayCommand)RunCreateDatasetCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        public string RunButtonText => IsRunning ? "Running..." : "Create Dataset";

        public bool IsPreviewApplying
        {
            get => _isPreviewApplying;
            set
            {
                if (SetProperty(ref _isPreviewApplying, value, () => IsPreviewApplying))
                {
                    ((RelayCommand)ApplyRandomPreviewCommand)?.RaiseCanExecuteChanged();
                    ((RelayCommand)PreviewNextImageCommand)?.RaiseCanExecuteChanged();
                    ((RelayCommand)PreviewPrevImageCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        #endregion

        #region Commands

        private ICommand _refreshOrthoListCommand;
        public ICommand RefreshOrthoListCommand => _refreshOrthoListCommand ??= new RelayCommand(_ => LoadProjectInfo());

        private ICommand _refreshAnnotationLayersCommand;
        public ICommand RefreshAnnotationLayersCommand => _refreshAnnotationLayersCommand ??= new RelayCommand(async _ => await RefreshAnnotationLayersAsync());

        private ICommand _runCreateDatasetCommand;
        public ICommand RunCreateDatasetCommand => _runCreateDatasetCommand ??= new RelayCommand(async _ => await RunCreateDatasetAsync(), _ => IsRunEnabled && !IsRunning);

        private ICommand _resetAugmentationCommand;
        public ICommand ResetAugmentationCommand => _resetAugmentationCommand ??= new RelayCommand(o => ResetAugmentation(o as AugmentationOption));

        private ICommand _applyPresetCommand;
        public ICommand ApplyPresetCommand => _applyPresetCommand ??= new RelayCommand(o => ApplyBuiltInPreset(o as string));

        private ICommand _savePresetCommand;
        public ICommand SavePresetCommand => _savePresetCommand ??= new RelayCommand(_ => SavePresetToJson());

        private ICommand _loadPresetCommand;
        public ICommand LoadPresetCommand => _loadPresetCommand ??= new RelayCommand(_ => LoadPresetFromJson());

        private ICommand _exportHypCommand;
        public ICommand ExportHypCommand => _exportHypCommand ??= new RelayCommand(_ => ExportHypYaml());

        private ICommand _applyRandomPreviewCommand;
        public ICommand ApplyRandomPreviewCommand => _applyRandomPreviewCommand ??= new RelayCommand(async _ => await ApplyRandomPreviewAsync(), _ => !IsPreviewApplying);

        private ICommand _browsePreviewTileCommand;
        public ICommand BrowsePreviewTileCommand => _browsePreviewTileCommand ??= new RelayCommand(_ => BrowsePreviewTile());

        private ICommand _browsePreviewTilesFolderCommand;
        public ICommand BrowsePreviewTilesFolderCommand => _browsePreviewTilesFolderCommand ??= new RelayCommand(_ => BrowsePreviewTilesFolder());

        private ICommand _previewNextImageCommand;
        public ICommand PreviewNextImageCommand => _previewNextImageCommand ??= new RelayCommand(_ => MovePreviewImage(1), _ => !IsPreviewApplying);

        private ICommand _previewPrevImageCommand;
        public ICommand PreviewPrevImageCommand => _previewPrevImageCommand ??= new RelayCommand(_ => MovePreviewImage(-1), _ => !IsPreviewApplying);

        #endregion

        #region Split Logic

        private enum SplitField
        {
            Train,
            Val,
            Test
        }

        private static int ClampPercent(int value)
        {
            if (value < 0) return 0;
            if (value > 100) return 100;
            return value;
        }

        private void RebalanceSplit(SplitField changedField)
        {
            if (_isAdjustingSplit)
                return;

            try
            {
                _isAdjustingSplit = true;

                var changed = changedField == SplitField.Train ? TrainPercent : changedField == SplitField.Val ? ValPercent : TestPercent;
                var remaining = 100 - changed;
                if (remaining < 0)
                    remaining = 0;

                int other1;
                int other2;
                SplitField f1;
                SplitField f2;

                if (changedField == SplitField.Train)
                {
                    other1 = ValPercent;
                    other2 = TestPercent;
                    f1 = SplitField.Val;
                    f2 = SplitField.Test;
                }
                else if (changedField == SplitField.Val)
                {
                    other1 = TrainPercent;
                    other2 = TestPercent;
                    f1 = SplitField.Train;
                    f2 = SplitField.Test;
                }
                else
                {
                    other1 = TrainPercent;
                    other2 = ValPercent;
                    f1 = SplitField.Train;
                    f2 = SplitField.Val;
                }

                var sumOther = other1 + other2;
                double ratio1 = sumOther > 0 ? (double)other1 / sumOther : 0.5;
                double ratio2 = 1.0 - ratio1;

                var new1 = (int)Math.Round(remaining * ratio1, MidpointRounding.AwayFromZero);
                var new2 = (int)Math.Round(remaining * ratio2, MidpointRounding.AwayFromZero);

                var total = changed + new1 + new2;
                var delta = 100 - total;
                if (delta != 0)
                {
                    if (new1 >= new2)
                        new1 += delta;
                    else
                        new2 += delta;
                }

                ApplySplitFieldValue(f1, ClampPercent(new1));
                ApplySplitFieldValue(f2, ClampPercent(new2));

                EnsureSplitSumEquals100(changedField, f1, f2);
            }
            finally
            {
                _isAdjustingSplit = false;
                NotifyPropertyChanged(nameof(SplitSummary));
            }
        }

        private void ApplySplitFieldValue(SplitField field, int value)
        {
            if (field == SplitField.Train)
                SetProperty(ref _trainPercent, value, () => TrainPercent);
            else if (field == SplitField.Val)
                SetProperty(ref _valPercent, value, () => ValPercent);
            else
                SetProperty(ref _testPercent, value, () => TestPercent);
        }

        private void EnsureSplitSumEquals100(SplitField changed, SplitField firstRecalculated, SplitField secondRecalculated)
        {
            var sum = TrainPercent + ValPercent + TestPercent;
            if (sum == 100)
                return;

            var delta = 100 - sum;
            var firstValue = GetSplitFieldValue(firstRecalculated);
            var secondValue = GetSplitFieldValue(secondRecalculated);

            if (firstValue >= secondValue)
                ApplySplitFieldValue(firstRecalculated, ClampPercent(firstValue + delta));
            else
                ApplySplitFieldValue(secondRecalculated, ClampPercent(secondValue + delta));

            var finalSum = TrainPercent + ValPercent + TestPercent;
            if (finalSum != 100)
            {
                var changedValue = GetSplitFieldValue(changed);
                ApplySplitFieldValue(changed, ClampPercent(changedValue + (100 - finalSum)));
            }
        }

        private int GetSplitFieldValue(SplitField field)
        {
            if (field == SplitField.Train)
                return TrainPercent;
            if (field == SplitField.Val)
                return ValPercent;
            return TestPercent;
        }

        public string SplitSummary => $"Train/Val/Test = {TrainPercent}/{ValPercent}/{TestPercent} (sum: {TrainPercent + ValPercent + TestPercent})";

        #endregion

        #region Augmentation

        private void InitAugmentationCollections()
        {
            GeometryAugmentations.Clear();
            ColorAugmentations.Clear();
            NoiseAugmentations.Clear();
            AdvancedAugmentations.Clear();

            GeometryAugmentations.Add(new AugmentationOption("Rotate 90° CW", true, 1, 0, 1, 1.0, 0.0, 1.0, true));
            GeometryAugmentations.Add(new AugmentationOption("Rotate 180°", true, 1, 0, 1, 1.0, 0.0, 1.0, true));
            GeometryAugmentations.Add(new AugmentationOption("Rotate 270° CW", true, 1, 0, 1, 1.0, 0.0, 1.0, true));
            GeometryAugmentations.Add(new AugmentationOption("Flip Horizontal", true, 1, 0, 1, 1.0, 0.0, 1.0, true));
            GeometryAugmentations.Add(new AugmentationOption("Flip Vertical", false, 1, 0, 1, 1.0, 0.0, 1.0, true));
            GeometryAugmentations.Add(new AugmentationOption("Shear X", false, 0, -20, 20, -6, 6, 0.5, 0.0, 1.0));
            GeometryAugmentations.Add(new AugmentationOption("Shear Y", false, 0, -20, 20, -6, 6, 0.5, 0.0, 1.0));
            GeometryAugmentations.Add(new AugmentationOption("Scale", false, 1.0, 0.5, 1.5, 0.9, 1.1, 0.5, 0.0, 1.0));
            GeometryAugmentations.Add(new AugmentationOption("Translate X", false, 0.0, -0.2, 0.2, -0.08, 0.08, 0.5, 0.0, 1.0));
            GeometryAugmentations.Add(new AugmentationOption("Translate Y", false, 0.0, -0.2, 0.2, -0.08, 0.08, 0.5, 0.0, 1.0));
            GeometryAugmentations.Add(new AugmentationOption("Perspective", false, 0.0, 0.0, 0.002, 0.0002, 0.0008, 0.3, 0.0, 1.0));
            GeometryAugmentations.Add(new AugmentationOption("Random Crop", false, 1.0, 0.7, 1.0, 0.82, 0.95, 0.5, 0.0, 1.0));

            ColorAugmentations.Add(new AugmentationOption("Hue", false, 0.0, -0.5, 0.5, -0.03, 0.03, 0.7, 0.0, 1.0));
            ColorAugmentations.Add(new AugmentationOption("Saturation", false, 1.0, 0.0, 2.0, 0.85, 1.2, 0.7, 0.0, 1.0));
            ColorAugmentations.Add(new AugmentationOption("Value (Brightness)", false, 1.0, 0.0, 2.0, 0.9, 1.15, 0.7, 0.0, 1.0));
            ColorAugmentations.Add(new AugmentationOption("Contrast", false, 1.0, 0.5, 1.5, 0.9, 1.15, 0.5, 0.0, 1.0));
            ColorAugmentations.Add(new AugmentationOption("CLAHE", false, 2.0, 1.0, 6.0, 1.5, 3.0, 0.3, 0.0, 1.0));
            ColorAugmentations.Add(new AugmentationOption("Auto Contrast", false, 0, 0, 10, 0, 4, 0.3, 0.0, 1.0));
            ColorAugmentations.Add(new AugmentationOption("Grayscale", false, 0.0, 0.0, 1.0, 0.0, 0.0, 1.0, showValueSlider: false));
            ColorAugmentations.Add(new AugmentationOption("Solarize", false, 128, 0, 255, 96, 160, 0.2, 0.0, 1.0));
            ColorAugmentations.Add(new AugmentationOption("Posterize", false, 8, 1, 8, 5, 8, 0.2, 0.0, 1.0));
            ColorAugmentations.Add(new AugmentationOption("Equalize", false, 1, 0, 1, 0.3, 0.0, 1.0, true, showValueSlider: false));

            NoiseAugmentations.Add(new AugmentationOption("Gaussian Blur", false, 1, 1, 11, 1, 5, 0.3, 0.0, 1.0));
            NoiseAugmentations.Add(new AugmentationOption("Median Blur", false, 1, 1, 11, 1, 5, 0.3, 0.0, 1.0));
            NoiseAugmentations.Add(new AugmentationOption("Gaussian Noise", false, 0, 0, 50, 4, 16, 0.3, 0.0, 1.0));
            NoiseAugmentations.Add(new AugmentationOption("Salt & Pepper", false, 0.0, 0.0, 0.1, 0.005, 0.025, 0.3, 0.0, 1.0));
            NoiseAugmentations.Add(new AugmentationOption("Random Shadow", false, 1, 0, 1, 0.3, 0.0, 1.0, true, showValueSlider: false));
            NoiseAugmentations.Add(new AugmentationOption("Rain / Fog", false, 0.0, 0.0, 1.0, 0.1, 0.35, 0.3, 0.0, 1.0));

            AdvancedAugmentations.Add(new AugmentationOption("Mosaic (4 img)", false, 1, 0, 1, 0.3, 0.0, 1.0, true, showValueSlider: false));
            AdvancedAugmentations.Add(new AugmentationOption("MixUp", false, 0.0, 0.0, 1.0, 0.15, 0.35, 0.1, 0.0, 1.0));
            AdvancedAugmentations.Add(new AugmentationOption("Copy-Paste", false, 0, 0, 5, 1, 3, 0.2, 0.0, 1.0));
            AdvancedAugmentations.Add(new AugmentationOption("CutOut", false, 0.0, 0.0, 0.5, 0.08, 0.2, 0.3, 0.0, 1.0));
            AdvancedAugmentations.Add(new AugmentationOption("Erasing", false, 0.0, 0.0, 1.0, 0.3, 0.0, 1.0, showValueSlider: false));

            foreach (var option in AllAugmentations())
            {
                option.PropertyChanged += (_, __) =>
                {
                    RecalculateEstimatedStatistics();
                    RefreshEnabledPreviewOperations();
                };
            }

            RefreshEnabledPreviewOperations();
        }

        private IEnumerable<AugmentationOption> AllAugmentations()
        {
            return GeometryAugmentations
                .Concat(ColorAugmentations)
                .Concat(NoiseAugmentations)
                .Concat(AdvancedAugmentations);
        }

        private bool HasAnyAugmentationEnabled()
        {
            return AllAugmentations().Any(a => a.IsEnabled);
        }

        private void ResetAugmentation(AugmentationOption option)
        {
            if (option == null)
                return;

            option.Value = option.DefaultValue;
            option.ValueMin = option.DefaultValueMin;
            option.ValueMax = option.DefaultValueMax;
            option.Probability = option.DefaultProbability;
            option.IsEnabled = false;
        }

        private void RecalculateEstimatedStatistics()
        {
            var deterministicCopies = 1;
            var enabledDeterministic = GeometryAugmentations.Count(a => a.IsEnabled && (a.Name.StartsWith("Rotate") || a.Name.StartsWith("Flip")));
            if (enabledDeterministic > 0)
                deterministicCopies = Math.Min(8, 1 + enabledDeterministic);

            var stochastic = AllAugmentations().Count(a => a.IsEnabled && !a.IsDeterministicOnly);
            DatasetMultiplier = Math.Max(1, deterministicCopies + (stochastic > 0 ? 1 : 0));
            EstimatedTotalAfterAugmentation = EstimatedSourceTiles * DatasetMultiplier;

            var estimatedSeconds = EstimatedTotalAfterAugmentation / 20.0;
            EstimatedGenerationTime = TimeSpan.FromSeconds(estimatedSeconds).ToString(@"hh\:mm\:ss");

            DatasetSizeWarning = EstimatedTotalAfterAugmentation > 100000
                ? "WARN: Estimated dataset size exceeds 100,000 images."
                : string.Empty;
        }

        #endregion

        #region Presets

        private void ApplyBuiltInPreset(string presetName)
        {
            if (string.IsNullOrWhiteSpace(presetName))
                return;

            Tools.Logger.Log($"INFO: Apply preset requested: {presetName}");

            if (string.Equals(presetName, "None", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var a in AllAugmentations())
                {
                    a.IsEnabled = false;
                    a.Value = a.DefaultValue;
                    a.ValueMin = a.DefaultValueMin;
                    a.ValueMax = a.DefaultValueMax;
                    a.Probability = a.DefaultProbability;
                }
            }
            else if (string.Equals(presetName, "Light", StringComparison.OrdinalIgnoreCase))
            {
                EnableByName("Rotate 90° CW", true);
                EnableByName("Rotate 180°", true);
                EnableByName("Rotate 270° CW", true);
                EnableByName("Flip Horizontal", true);
                EnableByName("Hue", true, null, null, -0.02, 0.02, 0.7);
                EnableByName("Saturation", true, null, null, 0.9, 1.1, 0.7);
                EnableByName("Value (Brightness)", true, null, null, 0.92, 1.1, 0.7);
                EnableByName("Contrast", true, null, null, 0.92, 1.08, 0.4);
            }
            else if (string.Equals(presetName, "Standard", StringComparison.OrdinalIgnoreCase))
            {
                ApplyBuiltInPreset("Light");
                EnableByName("Scale", true, null, null, 0.9, 1.12, 0.5);
                EnableByName("Translate X", true, null, null, -0.08, 0.08, 0.5);
                EnableByName("Translate Y", true, null, null, -0.08, 0.08, 0.5);
                EnableByName("Perspective", true, null, null, 0.0002, 0.001, 0.3);
                EnableByName("Gaussian Blur", true, null, null, 1, 5, 0.3);
                EnableByName("Gaussian Noise", true, null, null, 4, 14, 0.3);
            }
            else if (string.Equals(presetName, "Aggressive", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var a in AllAugmentations())
                {
                    a.IsEnabled = true;
                    a.ValueMin = a.MinValue;
                    a.ValueMax = a.MaxValue;
                    a.Probability = Math.Max(0.5, a.Probability);
                }
            }
            else if (string.Equals(presetName, "Outdoor Aerial", StringComparison.OrdinalIgnoreCase))
            {
                ApplyBuiltInPreset("Standard");
                EnableByName("Rain / Fog", true, null, null, 0.12, 0.35, 0.4);
                EnableByName("Random Shadow", true, 1, 0.3);
                EnableByName("CLAHE", true, null, null, 1.8, 3.2, 0.4);
                EnableByName("Auto Contrast", true, null, null, 0, 5, 0.25);
            }

            RecalculateEstimatedStatistics();
        }

        private void EnableByName(string name, bool enabled, double? value = null, double? probability = null, double? valueMin = null, double? valueMax = null, double? rangeProbability = null)
        {
            var target = AllAugmentations().FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
            if (target == null)
                return;

            target.IsEnabled = enabled;
            if (value.HasValue)
                target.Value = value.Value;
            if (probability.HasValue)
                target.Probability = probability.Value;
            if (valueMin.HasValue)
                target.ValueMin = valueMin.Value;
            if (valueMax.HasValue)
                target.ValueMax = valueMax.Value;
            if (rangeProbability.HasValue)
                target.Probability = rangeProbability.Value;

            if (!value.HasValue && valueMin.HasValue && valueMax.HasValue)
                target.Value = (target.ValueMin + target.ValueMax) * 0.5;
        }

        private void SavePresetToJson()
        {
            try
            {
                var dlg = new SaveFileDialog
                {
                    Filter = "JSON (*.json)|*.json",
                    Title = "Save augmentation preset",
                    FileName = "augmentation_preset.json"
                };

                if (dlg.ShowDialog() != true)
                    return;

                var preset = BuildPresetDto();
                var json = JsonSerializer.Serialize(preset, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(dlg.FileName, json, Encoding.UTF8);
                AppendProgressLine($"Preset saved: {dlg.FileName}");
            }
            catch (Exception ex)
            {
                Tools.Logger.Log($"ERROR: Save preset failed: {ex}");
                AppendProgressLine("ERROR: Failed to save preset.");
            }
        }

        private void LoadPresetFromJson()
        {
            try
            {
                var dlg = new OpenFileDialog
                {
                    Filter = "JSON (*.json)|*.json",
                    Title = "Load augmentation preset"
                };

                if (dlg.ShowDialog() != true)
                    return;

                var json = File.ReadAllText(dlg.FileName, Encoding.UTF8);
                var preset = JsonSerializer.Deserialize<AugmentationPresetDto>(json);
                if (preset == null)
                    return;

                ApplyPresetDto(preset);
                AppendProgressLine($"Preset loaded: {dlg.FileName}");
            }
            catch (Exception ex)
            {
                Tools.Logger.Log($"ERROR: Load preset failed: {ex}");
                AppendProgressLine("ERROR: Failed to load preset.");
            }
        }

        private void ExportHypYaml()
        {
            try
            {
                var dlg = new SaveFileDialog
                {
                    Filter = "YAML (*.yaml)|*.yaml",
                    Title = "Export Ultralytics hyp.yaml",
                    FileName = "hyp.yaml"
                };

                if (dlg.ShowDialog() != true)
                    return;

                File.WriteAllText(dlg.FileName, BuildHypYaml(), Encoding.UTF8);
                AppendProgressLine($"hyp.yaml exported: {dlg.FileName}");
            }
            catch (Exception ex)
            {
                Tools.Logger.Log($"ERROR: Export hyp.yaml failed: {ex}");
                AppendProgressLine("ERROR: Failed to export hyp.yaml.");
            }
        }

        private void ApplyPresetDto(AugmentationPresetDto preset)
        {
            if (preset?.Items == null)
                return;

            var byName = AllAugmentations().ToDictionary(x => x.Name, x => x, StringComparer.OrdinalIgnoreCase);
            foreach (var item in preset.Items)
            {
                if (!byName.TryGetValue(item.Name ?? string.Empty, out var target))
                    continue;

                target.IsEnabled = item.IsEnabled;
                if (item.ValueMin.HasValue)
                    target.ValueMin = item.ValueMin.Value;
                if (item.ValueMax.HasValue)
                    target.ValueMax = item.ValueMax.Value;

                target.Value = item.Value;
                target.Probability = item.Probability;
            }

            ApplyToVal = preset.ApplyToVal;
            ApplyToTest = preset.ApplyToTest;
            Seed = preset.Seed;
            RecalculateEstimatedStatistics();
        }

        private AugmentationPresetDto BuildPresetDto()
        {
            return new AugmentationPresetDto
            {
                Seed = Seed,
                ApplyToVal = ApplyToVal,
                ApplyToTest = ApplyToTest,
                Items = AllAugmentations().Select(a => new AugmentationPresetItemDto
                {
                    Name = a.Name,
                    IsEnabled = a.IsEnabled,
                    Value = a.Value,
                    ValueMin = a.ValueMin,
                    ValueMax = a.ValueMax,
                    Probability = a.Probability
                }).ToList()
            };
        }

        #endregion

        #region Actions

        private void BrowsePreviewTile()
        {
            try
            {
                var dlg = new OpenFileDialog
                {
                    Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff|All files|*.*",
                    Title = "Select preview tile image"
                };

                if (dlg.ShowDialog() == true)
                {
                    PreviewTile = dlg.FileName;
                    SetCurrentPreviewImage(dlg.FileName);
                }
            }
            catch (Exception ex)
            {
                Tools.Logger.Log($"ERROR: Browse preview tile failed: {ex}");
            }
        }

        private void BrowsePreviewTilesFolder()
        {
            try
            {
                var dlg = new OpenFolderDialog
                {
                    Title = "Select preview tiles folder"
                };

                if (dlg.ShowDialog() == true)
                    PreviewTilesFolder = dlg.FolderName;
            }
            catch (Exception ex)
            {
                Tools.Logger.Log($"ERROR: Browse preview folder failed: {ex}");
            }
        }

        private void MovePreviewImage(int delta)
        {
            if (_previewImagePaths == null || _previewImagePaths.Count == 0)
            {
                PreviewImagePosition = "0/0";
                return;
            }

            _previewImageIndex += delta;
            if (_previewImageIndex < 0)
                _previewImageIndex = _previewImagePaths.Count - 1;
            if (_previewImageIndex >= _previewImagePaths.Count)
                _previewImageIndex = 0;

            SetCurrentPreviewImage(_previewImagePaths[_previewImageIndex]);
        }

        private void SetCurrentPreviewImage(string imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
                return;

            PreviewTile = imagePath;
            OriginalPreviewImagePath = imagePath;
            AugmentedPreviewImagePath = string.Empty;
            PreviewAppliedTransforms = "No preview generated.";
            _previewOperationIndex = -1;

            if (_previewImagePaths != null && _previewImagePaths.Count > 0)
            {
                var idx = _previewImagePaths.FindIndex(p => string.Equals(p, imagePath, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0)
                    _previewImageIndex = idx;
                PreviewImagePosition = $"{Math.Max(1, _previewImageIndex + 1)}/{_previewImagePaths.Count}";
            }
            else
            {
                _previewImageIndex = 0;
                PreviewImagePosition = "1/1";
            }
        }

        private void LoadPreviewImagesFromFolder(string folderPath)
        {
            try
            {
                _previewImagePaths = new List<string>();
                _previewImageIndex = -1;
                _previewOperationIndex = -1;

                if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
                {
                    PreviewTile = string.Empty;
                    OriginalPreviewImagePath = string.Empty;
                    AugmentedPreviewImagePath = string.Empty;
                    PreviewAppliedTransforms = "No preview generated.";
                    PreviewImagePosition = "0/0";
                    return;
                }

                var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff" };
                _previewImagePaths = Directory.EnumerateFiles(folderPath, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(p => exts.Contains(Path.GetExtension(p)))
                    .OrderBy(p => p)
                    .ToList();

                if (_previewImagePaths.Count == 0)
                {
                    PreviewTile = string.Empty;
                    OriginalPreviewImagePath = string.Empty;
                    AugmentedPreviewImagePath = string.Empty;
                    PreviewAppliedTransforms = "No preview generated.";
                    PreviewImagePosition = "0/0";
                    return;
                }

                _previewImageIndex = 0;
                SetCurrentPreviewImage(_previewImagePaths[0]);
            }
            catch (Exception ex)
            {
                Tools.Logger.Log($"ERROR: Load preview images from folder failed: {ex}");
                PreviewImagePosition = "0/0";
            }
        }

        private async Task ApplyRandomPreviewAsync()
        {
            await ApplyPreviewAsync(null, true);
        }

        private async Task ApplySelectedPreviewOperationAsync(PreviewOperationItem operation)
        {
            var key = operation?.Key;
            if (string.IsNullOrWhiteSpace(key))
                return;

            await ApplyPreviewAsync(key, false);

            try
            {
                _isUpdatingPreviewOperationSelection = true;
                SelectedPreviewOperation = null;
            }
            finally
            {
                _isUpdatingPreviewOperationSelection = false;
            }
        }

        private async Task ApplyPreviewAsync(string forcedPreviewOp, bool advanceRoundRobin)
        {
            if (IsPreviewApplying)
                return;

            var previewImage = ResolvePreviewTilePath();
            if (string.IsNullOrWhiteSpace(previewImage) || !File.Exists(previewImage))
            {
                PreviewAppliedTransforms = "Preview tile not found. Укажите путь к тайлу или имя существующего тайла.";
                return;
            }

            try
            {
                IsPreviewApplying = true;
                PreviewAppliedTransforms = "Applying transform... Пожалуйста, подождите.";

                var active = AllAugmentations().Where(a => a.IsEnabled).ToList();
                if (active.Count == 0)
                {
                    PreviewAppliedTransforms = "No active augmentations.";
                    OriginalPreviewImagePath = previewImage;
                    AugmentedPreviewImagePath = previewImage;
                    return;
                }

                var scriptPath = ResolveAugmentationScriptPath();
                if (string.IsNullOrWhiteSpace(scriptPath) || !File.Exists(scriptPath))
                {
                    PreviewAppliedTransforms = "augmentation_module.py not found.";
                    return;
                }

                var tempRoot = Path.Combine(Path.GetTempPath(), "ArcGisProYoloPreview");
                Directory.CreateDirectory(tempRoot);
                var token = Guid.NewGuid().ToString("N");
                var configPath = Path.Combine(tempRoot, $"preview_config_{token}.yaml");
                var outPath = Path.Combine(tempRoot, $"preview_out_{token}.jpg");
                var summaryPath = Path.Combine(tempRoot, $"preview_summary_{token}.json");

                File.WriteAllText(configPath, BuildAugmentationConfigYaml(), Encoding.UTF8);

                var orderedOps = GetOrderedPreviewOperations();
                string selectedOp = forcedPreviewOp;
                if (string.IsNullOrWhiteSpace(selectedOp) && advanceRoundRobin && orderedOps.Count > 0)
                {
                    _previewOperationIndex = (_previewOperationIndex + 1) % orderedOps.Count;
                    selectedOp = orderedOps[_previewOperationIndex];
                }

                var args = $"--config \"{configPath}\" --preview-image \"{previewImage}\" --preview-output \"{outPath}\" --preview-summary \"{summaryPath}\"";
                if (!string.IsNullOrWhiteSpace(selectedOp))
                    args += $" --preview-op \"{selectedOp}\"";
                if (Seed != 0)
                    args += $" --seed {Seed}";

                var stdout = new StringBuilder();
                var stderr = new StringBuilder();
                var exit = await Tools.PythonRunner.RunPythonScriptAsync(
                    null,
                    scriptPath,
                    args,
                    s => { if (!string.IsNullOrWhiteSpace(s)) stdout.AppendLine(s); },
                    e => { if (!string.IsNullOrWhiteSpace(e)) stderr.AppendLine(e); });

                if (exit != 0 || !File.Exists(outPath))
                {
                    PreviewAppliedTransforms = "Preview generation failed.";
                    if (stderr.Length > 0)
                        AppendProgressLine($"WARN: Preview stderr: {stderr.ToString().Trim()}");
                    return;
                }

                OriginalPreviewImagePath = previewImage;
                AugmentedPreviewImagePath = outPath;

                if (File.Exists(summaryPath))
                {
                    var text = File.ReadAllText(summaryPath, Encoding.UTF8);
                    using var doc = JsonDocument.Parse(text);
                    var parts = new List<string>();

                    if (doc.RootElement.TryGetProperty("applied_steps", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    {
                        var steps = arr.EnumerateArray()
                            .Where(x => x.ValueKind == JsonValueKind.String)
                            .Select(x => x.GetString())
                            .Where(x => !string.IsNullOrWhiteSpace(x))
                            .ToList();
                        if (steps.Count > 0)
                            parts.Add("Applied: " + string.Join(" → ", steps));
                    }

                    PreviewAppliedTransforms = parts.Count > 0 ? string.Join(" | ", parts) : "Preview generated.";
                }
                else
                {
                    PreviewAppliedTransforms = "Preview generated.";
                }
            }
            catch (Exception ex)
            {
                Tools.Logger.Log($"ERROR: Apply preview failed: {ex}");
                PreviewAppliedTransforms = "Preview generation failed.";
            }
            finally
            {
                IsPreviewApplying = false;
            }
        }

        private void RefreshEnabledPreviewOperations()
        {
            try
            {
                var selectedKey = SelectedPreviewOperation?.Key;
                var items = GetOrderedPreviewOperationItems();

                _isUpdatingPreviewOperationSelection = true;
                EnabledPreviewOperations.Clear();
                foreach (var item in items)
                    EnabledPreviewOperations.Add(item);

                if (!string.IsNullOrWhiteSpace(selectedKey))
                    SelectedPreviewOperation = EnabledPreviewOperations.FirstOrDefault(x => string.Equals(x.Key, selectedKey, StringComparison.OrdinalIgnoreCase));
                else
                    SelectedPreviewOperation = null;
            }
            finally
            {
                _isUpdatingPreviewOperationSelection = false;
            }
        }

        private string ResolvePreviewTilePath()
        {
            try
            {
                var input = (PreviewTile ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(input))
                    return string.Empty;

                if (File.Exists(input))
                    return input;

                var projectUri = Project.Current?.URI;
                if (string.IsNullOrWhiteSpace(projectUri) || string.IsNullOrWhiteSpace(SelectedOrtho))
                    return string.Empty;

                var projectDir = Path.GetDirectoryName(projectUri);
                var orthoFolder = Path.Combine(projectDir ?? string.Empty, "OrthoMapping", SelectedOrtho);
                var imagesFolder = Path.Combine(orthoFolder, "Tiles", $"{TileSize}px", "Images");
                if (!Directory.Exists(imagesFolder))
                    return string.Empty;

                var hasExt = Path.HasExtension(input);
                if (hasExt)
                {
                    var direct = Path.Combine(imagesFolder, input);
                    if (File.Exists(direct))
                        return direct;
                }
                else
                {
                    var exts = new[] { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff" };
                    foreach (var ext in exts)
                    {
                        var candidate = Path.Combine(imagesFolder, input + ext);
                        if (File.Exists(candidate))
                            return candidate;
                    }
                }

                return string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private List<string> GetOrderedPreviewOperations()
        {
            return GetOrderedPreviewOperationItems().Select(x => x.Key).ToList();
        }

        private List<PreviewOperationItem> GetOrderedPreviewOperationItems()
        {
            var ops = new List<PreviewOperationItem>();

            void AddIfEnabled(string category, string displayName)
            {
                var opt = AllAugmentations().FirstOrDefault(a => string.Equals(a.Name, displayName, StringComparison.OrdinalIgnoreCase));
                if (opt?.IsEnabled == true)
                    ops.Add(new PreviewOperationItem
                    {
                        Category = category,
                        DisplayName = displayName,
                        Key = NormalizeYamlKey(displayName)
                    });
            }

            AddIfEnabled("Geometry", "Rotate 90° CW");
            AddIfEnabled("Geometry", "Rotate 180°");
            AddIfEnabled("Geometry", "Rotate 270° CW");
            AddIfEnabled("Geometry", "Flip Horizontal");
            AddIfEnabled("Geometry", "Flip Vertical");
            AddIfEnabled("Geometry", "Shear X");
            AddIfEnabled("Geometry", "Shear Y");
            AddIfEnabled("Geometry", "Scale");
            AddIfEnabled("Geometry", "Translate X");
            AddIfEnabled("Geometry", "Translate Y");
            AddIfEnabled("Geometry", "Perspective");
            AddIfEnabled("Geometry", "Random Crop");

            AddIfEnabled("Color", "Hue");
            AddIfEnabled("Color", "Saturation");
            AddIfEnabled("Color", "Value (Brightness)");
            AddIfEnabled("Color", "Contrast");
            AddIfEnabled("Color", "CLAHE");
            AddIfEnabled("Color", "Auto Contrast");
            AddIfEnabled("Color", "Grayscale");
            AddIfEnabled("Color", "Solarize");
            AddIfEnabled("Color", "Posterize");
            AddIfEnabled("Color", "Equalize");

            AddIfEnabled("Noise & Blur", "Gaussian Blur");
            AddIfEnabled("Noise & Blur", "Median Blur");
            AddIfEnabled("Noise & Blur", "Gaussian Noise");
            AddIfEnabled("Noise & Blur", "Salt & Pepper");
            AddIfEnabled("Noise & Blur", "Random Shadow");
            AddIfEnabled("Noise & Blur", "Rain / Fog");

            AddIfEnabled("Advanced", "CutOut");
            AddIfEnabled("Advanced", "Erasing");

            return ops;
        }

        private async Task RunCreateDatasetAsync()
        {
            if (IsRunning)
                return;

            IsRunning = true;
            try
            {
                ProgressText = string.Empty;
                UpdateValidationWarnings();
                if (!string.IsNullOrWhiteSpace(ValidationWarning))
                {
                    AppendProgressLine($"WARN: {ValidationWarning}");
                }

                if (TrainPercent + ValPercent + TestPercent != 100)
                {
                    AppendProgressLine("ERROR: Split sum must be 100.");
                    return;
                }

                if (!AnnotationLayers.Any(a => a.IsSelected))
                {
                    AppendProgressLine("ERROR: Не выбран ни один слой аннотаций объектов.");
                    return;
                }

                if (string.IsNullOrWhiteSpace(SelectedOrtho))
                {
                    AppendProgressLine("ERROR: Ortho is not selected.");
                    return;
                }

                var projectUri = Project.Current?.URI;
                if (string.IsNullOrWhiteSpace(projectUri))
                {
                    AppendProgressLine("ERROR: ArcGIS project is not available.");
                    return;
                }

                var projectDir = Path.GetDirectoryName(projectUri);
                var orthoFolder = Path.Combine(projectDir ?? string.Empty, "OrthoMapping", SelectedOrtho);
                if (!Directory.Exists(orthoFolder))
                {
                    AppendProgressLine($"ERROR: Ortho folder not found: {orthoFolder}");
                    return;
                }

                var hasAugmentation = HasAnyAugmentationEnabled();
                var experimentName = BuildExperimentName(hasAugmentation);
                var dataSetRoot = Path.Combine(orthoFolder, "DataSet");
                var experimentFolder = Path.Combine(dataSetRoot, experimentName);

                AppendProgressLine($"INFO: Creating experiment folder: {experimentFolder}");
                Directory.CreateDirectory(experimentFolder);

                CreateSplitDirectories(experimentFolder);
                if (DebugMode)
                    Directory.CreateDirectory(Path.Combine(experimentFolder, "debug"));
                if (hasAugmentation)
                    Directory.CreateDirectory(Path.Combine(experimentFolder, "aug_source"));

                var tilesFolder = Tools.TileGenerator.PrepareTilesFolderInEomw(orthoFolder, TileSize);
                var orthoImagePath = ResolveOrthoImagePath(orthoFolder);
                if (string.IsNullOrWhiteSpace(orthoImagePath) || !File.Exists(orthoImagePath))
                {
                    AppendProgressLine("ERROR: Source ortho image not found in Products/Orthos.");
                    return;
                }

                var useExistingTiles = false;
                var imagesFolder = Path.Combine(tilesFolder, "Images");
                if (HasExistingTiles(imagesFolder))
                {
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
                        AppendProgressLine("INFO: Using existing tiles.");
                    }
                    else
                    {
                        try
                        {
                            Directory.Delete(tilesFolder, true);
                            tilesFolder = Tools.TileGenerator.PrepareTilesFolderInEomw(orthoFolder, TileSize);
                            AppendProgressLine("INFO: Existing tiles removed. Regenerating...");
                        }
                        catch (Exception ex)
                        {
                            AppendProgressLine($"ERROR: Failed to recreate tiles folder: {ex.Message}");
                            return;
                        }
                    }
                }

                if (!useExistingTiles)
                {
                    AppendProgressLine("INFO: Generating tiles...");
                    var overlapPercent = Math.Max(0, Math.Min(90, (int)Math.Round(Overlap * 100.0)));
                    var tilesOk = await Tools.TileGenerator.GenerateTilesAsync(orthoImagePath, tilesFolder, TileSize, overlapPercent, CancellationToken.None);
                    if (!tilesOk)
                    {
                        AppendProgressLine("ERROR: Tile generation failed.");
                        return;
                    }
                }

                AppendProgressLine("INFO: Building train/valid/test from tiles and annotation layers...");
                var buildSummary = await BuildDatasetFromTilesAsync(experimentFolder, tilesFolder);
                EstimatedSourceTiles = buildSummary.images;
                RecalculateEstimatedStatistics();
                AppendProgressLine($"INFO: Dataset base prepared. Images={buildSummary.images}, Labels={buildSummary.labels}");

                if (Seed == 0)
                {
                    Seed = BuildDeterministicSeed(experimentName);
                    AppendProgressLine($"INFO: Seed auto-generated: {Seed}");
                }

                var dataYamlPath = Path.Combine(experimentFolder, "data.yaml");
                File.WriteAllText(dataYamlPath, BuildDataYamlContent(), Encoding.UTF8);
                AppendProgressLine("INFO: data.yaml generated.");

                if (hasAugmentation)
                {
                    var augmentationConfigPath = Path.Combine(experimentFolder, "augmentation_config.yaml");
                    File.WriteAllText(augmentationConfigPath, BuildAugmentationConfigYaml(), Encoding.UTF8);
                    AppendProgressLine("INFO: augmentation_config.yaml generated.");
                }

                var reportPath = Path.Combine(experimentFolder, "dataset_report.txt");
                File.WriteAllText(reportPath, BuildDatasetReport(experimentName, orthoFolder, experimentFolder, hasAugmentation), Encoding.UTF8);
                AppendProgressLine("INFO: dataset_report.txt generated.");

                if (hasAugmentation)
                {
                    var hypPath = Path.Combine(experimentFolder, "hyp.yaml");
                    File.WriteAllText(hypPath, BuildHypYaml(), Encoding.UTF8);
                    AppendProgressLine("INFO: hyp.yaml generated.");

                    await RunAugmentationPipelineAsync(experimentFolder);
                }

                if (EstimatedTotalAfterAugmentation > 100000)
                    AppendProgressLine("WARN: Estimated dataset size exceeds 100,000 images.");

                if (TrainPercent < 50)
                    AppendProgressLine("WARN: Train split is below 50%.");

                AppendProgressLine("INFO: Dataset folder structure and configuration files created.");
                Tools.Logger.Log($"INFO: Create Dataset completed. Experiment folder: {experimentFolder}");
                await Task.CompletedTask;
                SaveUserSettings();
            }
            catch (Exception ex)
            {
                Tools.Logger.Log($"ERROR: RunCreateDatasetAsync failed: {ex}");
                AppendProgressLine("ERROR: Dataset generation failed.");
            }
            finally
            {
                IsRunning = false;
            }
        }

        #endregion

        #region Validation/State

        private void UpdateValidationWarnings()
        {
            var warnings = new List<string>();

            if (TileSize < 64 || TileSize > 2048)
                warnings.Add("Tile Size should be in range [64..2048].");

            if (Overlap < 0 || Overlap > 0.9)
                warnings.Add("Overlap should be in range [0..0.9].");

            if (!AnnotationLayers.Any(a => a.IsSelected))
                warnings.Add("Select at least one annotation layer.");

            if (TrainPercent < 50)
                SplitWarning = "WARN: Train split is below 50%.";
            else
                SplitWarning = string.Empty;

            ValidationWarning = string.Join(" ", warnings);
            NotifyPropertyChanged(nameof(SplitSummary));
            NotifyPropertyChanged(nameof(IsRunEnabled));
            NotifyPropertyChanged(nameof(SelectedAnnotationLayersSummary));
        }

        private void LoadProjectInfo()
        {
            try
            {
                var previous = SelectedOrtho;
                OrthoList.Clear();

                var projectUri = Project.Current?.URI;
                if (string.IsNullOrWhiteSpace(projectUri))
                {
                    NotifyPropertyChanged(nameof(IsRunEnabled));
                    return;
                }

                var projectDir = Path.GetDirectoryName(projectUri);
                var orthoRoot = Path.Combine(projectDir ?? string.Empty, "OrthoMapping");
                if (Directory.Exists(orthoRoot))
                {
                    var dirs = Directory.GetDirectories(orthoRoot);
                    foreach (var d in dirs)
                        OrthoList.Add(Path.GetFileName(d));
                }

                if (!string.IsNullOrWhiteSpace(previous) && OrthoList.Contains(previous))
                    SelectedOrtho = previous;
                else if (OrthoList.Count > 0)
                    SelectedOrtho = OrthoList[0];
                else
                    SelectedOrtho = string.Empty;

                NotifyPropertyChanged(nameof(IsRunEnabled));
                _ = RefreshAnnotationLayersAsync();
            }
            catch (Exception ex)
            {
                Tools.Logger.Log($"ERROR: LoadProjectInfo(CreateDataset) failed: {ex}");
            }
        }

        private async Task RefreshAnnotationLayersAsync()
        {
            try
            {
                var selectedNames = AnnotationLayers
                    .Where(x => x.IsSelected)
                    .Select(x => x.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var layerNames = await QueuedTask.Run(() =>
                {
                    var map = MapView.Active?.Map;
                    if (map == null)
                        return new List<string>();

                    return map.GetLayersAsFlattenedList()
                        .OfType<FeatureLayer>()
                        .Where(l =>
                        {
                            var name = l?.Name ?? string.Empty;
                            return !string.IsNullOrWhiteSpace(name)
                                && name.IndexOf("train", StringComparison.OrdinalIgnoreCase) >= 0;
                        })
                        .Select(l => l.Name)
                        .Where(n => !string.IsNullOrWhiteSpace(n))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(n => n)
                        .ToList();
                });

                AnnotationLayers.Clear();
                foreach (var name in layerNames)
                {
                    var item = new AnnotationLayerSelection
                    {
                        Name = name,
                        IsSelected = selectedNames.Contains(name)
                    };
                    item.PropertyChanged += (_, __) =>
                    {
                        NotifyPropertyChanged(nameof(IsRunEnabled));
                        NotifyPropertyChanged(nameof(SelectedAnnotationLayersSummary));
                    };
                    AnnotationLayers.Add(item);
                }

                if (AnnotationLayers.Count > 0 && !AnnotationLayers.Any(a => a.IsSelected))
                    AnnotationLayers[0].IsSelected = true;

                Tools.Logger.Log($"INFO: Annotation layers loaded for Create Dataset: {AnnotationLayers.Count}");

                NotifyPropertyChanged(nameof(IsRunEnabled));
                NotifyPropertyChanged(nameof(SelectedAnnotationLayersSummary));
                UpdateValidationWarnings();
            }
            catch (Exception ex)
            {
                Tools.Logger.Log($"ERROR: RefreshAnnotationLayersAsync failed: {ex}");
            }
        }

        private void AppendProgressLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return;

            var prefix = string.IsNullOrWhiteSpace(ProgressText) ? string.Empty : Environment.NewLine;
            ProgressText = ProgressText + prefix + line;
        }

        private async Task RunAugmentationPipelineAsync(string experimentFolder)
        {
            try
            {
                var scriptPath = ResolveAugmentationScriptPath();
                if (string.IsNullOrWhiteSpace(scriptPath) || !File.Exists(scriptPath))
                {
                    AppendProgressLine("WARN: augmentation_module.py not found, augmentation run skipped.");
                    Tools.Logger.Log($"WARN: augmentation_module.py not found. Resolved path: {scriptPath}");
                    return;
                }

                var stdout = new StringBuilder();
                var stderr = new StringBuilder();
                var args = $"--dataset-root \"{experimentFolder}\" --config \"{Path.Combine(experimentFolder, "augmentation_config.yaml")}\" --max-per-image {Math.Max(1, DatasetMultiplier - 1)}";
                if (ApplyToVal)
                    args += " --apply-to-val";
                if (ApplyToTest)
                    args += " --apply-to-test";
                if (DebugMode)
                {
                    var debugDir = Path.Combine(experimentFolder, "debug");
                    args += $" --debug --debug-dir \"{debugDir}\"";
                }

                AppendProgressLine("INFO: Running augmentation pipeline...");
                Tools.Logger.Log($"INFO: Starting augmentation_module.py: {scriptPath} {args}");

                var exit = await Tools.PythonRunner.RunPythonScriptAsync(
                    null,
                    scriptPath,
                    args,
                    s =>
                    {
                        stdout.AppendLine(s);
                        if (!string.IsNullOrWhiteSpace(s))
                            AppendProgressLine(s);
                    },
                    e =>
                    {
                        stderr.AppendLine(e);
                        if (!string.IsNullOrWhiteSpace(e))
                            AppendProgressLine(e);
                    });

                var outLog = Path.Combine(experimentFolder, "augmentation_stdout.log");
                var errLog = Path.Combine(experimentFolder, "augmentation_stderr.log");
                File.WriteAllText(outLog, stdout.ToString());
                File.WriteAllText(errLog, stderr.ToString());

                if (exit == 0)
                {
                    AppendProgressLine("INFO: Augmentation completed successfully.");
                    Tools.Logger.Log($"INFO: augmentation_module.py finished successfully. Logs: {outLog}, {errLog}");
                }
                else
                {
                    AppendProgressLine($"ERROR: Augmentation failed with exit code {exit}. See augmentation_stderr.log");
                    Tools.Logger.Log($"ERROR: augmentation_module.py failed with exit code {exit}");
                }
            }
            catch (Exception ex)
            {
                Tools.Logger.Log($"ERROR: RunAugmentationPipelineAsync failed: {ex}");
                AppendProgressLine("ERROR: Augmentation pipeline execution failed.");
            }
        }

        private static string ResolveAugmentationScriptPath()
        {
            try
            {
                var projectRoot = Path.GetDirectoryName(Project.Current?.URI ?? string.Empty);
                var projectScript = Path.Combine(projectRoot ?? string.Empty, "opp_yolo_tool", "augmentation_module.py");
                if (File.Exists(projectScript))
                    return projectScript;

                var appDataScript = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ESRI", "ArcGISPro", "opp_yolo_tool", "augmentation_module.py");
                if (File.Exists(appDataScript))
                    return appDataScript;

                var asmFolder = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                var fallback = Path.GetFullPath(Path.Combine(asmFolder ?? string.Empty, "..", "..", "opp_yolo_tool", "augmentation_module.py"));
                return fallback;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string ResolveCreateDatasetScriptPath()
        {
            try
            {
                var projectRoot = Path.GetDirectoryName(Project.Current?.URI ?? string.Empty);
                var projectScript = Path.Combine(projectRoot ?? string.Empty, "opp_yolo_tool", "create_dataset_module.py");
                if (File.Exists(projectScript))
                    return projectScript;

                var asmFolder = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                var fallbackScript = Path.GetFullPath(Path.Combine(asmFolder ?? string.Empty, "..", "..", "opp_yolo_tool", "create_dataset_module.py"));
                if (File.Exists(fallbackScript))
                    return fallbackScript;

                var appDataScript = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ESRI", "ArcGISPro", "opp_yolo_tool", "create_dataset_module.py");
                if (File.Exists(appDataScript))
                    return appDataScript;

                return fallbackScript;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string ResolveOrthoImagePath(string orthoFolder)
        {
            try
            {
                var orthosFolder = Path.Combine(orthoFolder, "Products", "Orthos");
                if (!Directory.Exists(orthosFolder))
                    return string.Empty;

                var exts = new[] { "*.tif", "*.tiff", "*.img", "*.vrt" };
                foreach (var ext in exts)
                {
                    var file = Directory.GetFiles(orthosFolder, ext).OrderBy(x => x).FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(file))
                        return file;
                }

                return string.Empty;
            }
            catch
            {
                return string.Empty;
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

        private async Task<(int images, int labels)> BuildDatasetFromTilesAsync(string experimentFolder, string tilesFolder)
        {
            var scriptPath = ResolveCreateDatasetScriptPath();
            if (string.IsNullOrWhiteSpace(scriptPath) || !File.Exists(scriptPath))
            {
                AppendProgressLine("ERROR: create_dataset_module.py not found.");
                return (0, 0);
            }
            AppendProgressLine($"INFO: create_dataset_module.py path: {scriptPath}");
            Tools.Logger.Log($"INFO: create_dataset_module.py path: {scriptPath}");

            var layerNames = AnnotationLayers.Where(a => a.IsSelected).Select(a => a.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
            if (layerNames.Count == 0)
            {
                AppendProgressLine("ERROR: No selected annotation layers to build dataset.");
                return (0, 0);
            }

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            var layersArg = string.Join("|", layerNames);
            var projectUri = Project.Current?.URI ?? string.Empty;
            var args = $"--tiles-folder \"{tilesFolder}\" --dataset-root \"{experimentFolder}\" --train {TrainPercent} --val {ValPercent} --test {TestPercent} --seed {Seed} --layers \"{layersArg}\" --dataset-type \"{SelectedDatasetType}\" --aprx \"{projectUri}\"";
            if (DebugMode)
            {
                var debugDir = Path.Combine(experimentFolder, "debug");
                args += $" --debug --debug-dir \"{debugDir}\"";
                AppendProgressLine($"INFO: Debug annotation mode enabled. Output: {debugDir}");
            }

            var exit = await Tools.PythonRunner.RunPythonScriptAsync(
                null,
                scriptPath,
                args,
                s => { stdout.AppendLine(s); if (!string.IsNullOrWhiteSpace(s)) AppendProgressLine(s); },
                e => { stderr.AppendLine(e); if (!string.IsNullOrWhiteSpace(e)) AppendProgressLine(e); });

            File.WriteAllText(Path.Combine(experimentFolder, "create_dataset_stdout.log"), stdout.ToString());
            File.WriteAllText(Path.Combine(experimentFolder, "create_dataset_stderr.log"), stderr.ToString());

            if (exit != 0)
            {
                AppendProgressLine($"ERROR: create_dataset_module.py failed with exit code {exit}");
                return (0, 0);
            }

            var summaryPath = Path.Combine(experimentFolder, "dataset_build_summary.json");
            if (!File.Exists(summaryPath))
                return CountCurrentDatasetFiles(experimentFolder);

            try
            {
                var json = File.ReadAllText(summaryPath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var images = root.TryGetProperty("total_images", out var imagesNode) ? imagesNode.GetInt32() : 0;
                var labels = root.TryGetProperty("total_labels", out var labelsNode) ? labelsNode.GetInt32() : 0;
                return (images, labels);
            }
            catch
            {
                return CountCurrentDatasetFiles(experimentFolder);
            }
        }

        private static (int images, int labels) CountCurrentDatasetFiles(string experimentFolder)
        {
            try
            {
                var splits = new[] { "train", "valid", "test" };
                var imgExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff" };
                var images = 0;
                var labels = 0;
                foreach (var split in splits)
                {
                    var imagesDir = Path.Combine(experimentFolder, split, "images");
                    var labelsDir = Path.Combine(experimentFolder, split, "labels");
                    if (Directory.Exists(imagesDir))
                        images += Directory.GetFiles(imagesDir).Count(f => imgExt.Contains(Path.GetExtension(f)));
                    if (Directory.Exists(labelsDir))
                        labels += Directory.GetFiles(labelsDir, "*.txt").Length;
                }
                return (images, labels);
            }
            catch
            {
                return (0, 0);
            }
        }

        #endregion

        #region Files (used in next implementation steps)

        private string BuildExperimentName(bool hasAugmentation)
        {
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            var datasetTypeTag = (SelectedDatasetType ?? "dataset").Trim().ToLowerInvariant();
            var baseName = $"{timestamp}_{TileSize}_{datasetTypeTag}";

            var suffix = string.Empty;
            if (hasAugmentation)
                suffix += "_aug";
            if (DebugMode)
                suffix += "_debug";

            return baseName + suffix;
        }

        private int BuildDeterministicSeed(string experimentName)
        {
            unchecked
            {
                var hash = 17;
                foreach (var ch in experimentName ?? string.Empty)
                    hash = hash * 31 + ch;
                return Math.Abs(hash == int.MinValue ? 123456789 : hash);
            }
        }

        private void CreateSplitDirectories(string experimentFolder)
        {
            CreateSplitRoot(experimentFolder, "train");
            CreateSplitRoot(experimentFolder, "valid");
            CreateSplitRoot(experimentFolder, "test");
        }

        private static void CreateSplitRoot(string experimentFolder, string splitName)
        {
            var splitRoot = Path.Combine(experimentFolder, splitName);
            Directory.CreateDirectory(splitRoot);
            Directory.CreateDirectory(Path.Combine(splitRoot, "images"));
            Directory.CreateDirectory(Path.Combine(splitRoot, "labels"));
        }

        private string BuildDataYamlContent()
        {
            var sb = new StringBuilder();
            sb.AppendLine("train: ./train/images");
            sb.AppendLine("val: ./valid/images");
            if (TestPercent > 0)
                sb.AppendLine("test: ./test/images");
            sb.AppendLine();
            sb.AppendLine("nc: 0");
            sb.AppendLine("names: []");
            return sb.ToString();
        }

        private string BuildAugmentationConfigYaml()
        {
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine($"seed: {Seed}");
            sb.AppendLine("geometry:");
            AppendAugmentationGroupYaml(sb, GeometryAugmentations, ci);
            sb.AppendLine("color:");
            AppendAugmentationGroupYaml(sb, ColorAugmentations, ci);
            sb.AppendLine("noise:");
            AppendAugmentationGroupYaml(sb, NoiseAugmentations, ci);
            sb.AppendLine("advanced:");
            AppendAugmentationGroupYaml(sb, AdvancedAugmentations, ci);
            sb.AppendLine($"apply_to_val: {ApplyToVal.ToString().ToLowerInvariant()}");
            sb.AppendLine($"apply_to_test: {ApplyToTest.ToString().ToLowerInvariant()}");
            return sb.ToString();
        }

        private static void AppendAugmentationGroupYaml(StringBuilder sb, IEnumerable<AugmentationOption> options, CultureInfo ci)
        {
            foreach (var option in options)
            {
                var key = NormalizeYamlKey(option.Name);
                if (option.IsDeterministicOnly)
                {
                    sb.AppendLine($"  {key}: {option.IsEnabled.ToString().ToLowerInvariant()}");
                    continue;
                }

                sb.AppendLine($"  {key}: {{value: {option.Value.ToString("0.######", ci)}, value_min: {option.ValueMin.ToString("0.######", ci)}, value_max: {option.ValueMax.ToString("0.######", ci)}, prob: {option.Probability.ToString("0.######", ci)}, enabled: {option.IsEnabled.ToString().ToLowerInvariant()}}}");
            }
        }

        private static string NormalizeYamlKey(string name)
        {
            return (name ?? string.Empty)
                .Trim()
                .ToLowerInvariant()
                .Replace("°", string.Empty)
                .Replace("(", string.Empty)
                .Replace(")", string.Empty)
                .Replace("/", "_")
                .Replace("&", "and")
                .Replace("-", "_")
                .Replace(" ", "_");
        }

        private string BuildDatasetReport(string experimentName, string orthoFolder, string experimentFolder, bool hasAugmentation)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Dataset Report");
            sb.AppendLine("==============");
            sb.AppendLine($"Date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Source ortho folder: {orthoFolder}");
            sb.AppendLine($"Tile size: {TileSize}");
            sb.AppendLine($"Overlap: {Overlap.ToString("0.###", CultureInfo.InvariantCulture)}");
            sb.AppendLine($"Dataset type: {SelectedDatasetType}");
            sb.AppendLine($"Experiment: {experimentName}");
            sb.AppendLine();

            sb.AppendLine("Modes:");
            sb.AppendLine($"- Debug: {(DebugMode ? "enabled" : "disabled")}");
            sb.AppendLine($"- Augmentation: {(hasAugmentation ? "enabled" : "disabled")}");
            sb.AppendLine();

            sb.AppendLine("Split:");
            sb.AppendLine($"- Train: {TrainPercent}%");
            sb.AppendLine($"- Val: {ValPercent}%");
            sb.AppendLine($"- Test: {TestPercent}%");
            sb.AppendLine($"- Background limit: {BackgroundLimit}{(BackgroundLimitIsPercent ? "%" : " images")}");
            sb.AppendLine($"- Class balancing: {(EnableClassBalancing ? "enabled" : "disabled")} ({SelectedBalanceMethod})");
            sb.AppendLine();

            sb.AppendLine("Augmentation:");
            sb.AppendLine($"- Seed: {Seed}");
            sb.AppendLine($"- Apply to Val: {ApplyToVal}");
            sb.AppendLine($"- Apply to Test: {ApplyToTest}");
            sb.AppendLine($"- Dataset multiplier: x{DatasetMultiplier}");
            sb.AppendLine($"- Estimated source images: {EstimatedSourceTiles}");
            sb.AppendLine($"- Estimated images after augmentation: {EstimatedTotalAfterAugmentation}");
            sb.AppendLine();

            sb.AppendLine("Enabled augmentations:");
            foreach (var aug in AllAugmentations().Where(a => a.IsEnabled))
            {
                sb.AppendLine($"- {aug.Name}: range=[{aug.ValueMin:0.###}..{aug.ValueMax:0.###}], prob={aug.Probability:0.###}");
            }

            sb.AppendLine();
            sb.AppendLine("Per-split annotation statistics:");
            foreach (var split in new[] { "train", "valid", "test" })
            {
                var stats = AnalyzeSplitAnnotations(experimentFolder, split);
                if (!stats.Exists)
                {
                    sb.AppendLine($"- {split}: not found");
                    continue;
                }

                sb.AppendLine($"- {split}:");
                sb.AppendLine($"  images: {stats.ImageCount}");
                sb.AppendLine($"  label files: {stats.LabelFileCount}");
                sb.AppendLine($"  missing label files: {stats.MissingLabelCount}");
                sb.AppendLine($"  empty annotations: {stats.EmptyAnnotationCount}");
                sb.AppendLine($"  images with objects: {stats.ImagesWithObjects}");
                sb.AppendLine($"  total objects: {stats.TotalObjects}");

                if (stats.TotalObjects <= 0 || stats.ClassObjectCounts.Count == 0)
                {
                    sb.AppendLine("  class balance: no objects");
                }
                else
                {
                    sb.AppendLine("  class balance:");
                    foreach (var kv in stats.ClassObjectCounts.OrderBy(x => x.Key))
                    {
                        var pct = kv.Value * 100.0 / stats.TotalObjects;
                        sb.AppendLine($"    class {kv.Key}: {kv.Value} ({pct:0.##}%)");
                    }
                }
            }

            sb.AppendLine();
            sb.AppendLine("Warnings:");
            if (!string.IsNullOrWhiteSpace(ValidationWarning))
                sb.AppendLine($"- {ValidationWarning}");
            if (!string.IsNullOrWhiteSpace(SplitWarning))
                sb.AppendLine($"- {SplitWarning}");
            if (EstimatedTotalAfterAugmentation > 100000)
                sb.AppendLine("- Estimated dataset size exceeds 100,000 images.");
            if (TrainPercent < 50)
                sb.AppendLine("- Train split is below 50%.");

            return sb.ToString();
        }

        private sealed class SplitAnnotationStats
        {
            public bool Exists { get; set; }
            public int ImageCount { get; set; }
            public int LabelFileCount { get; set; }
            public int MissingLabelCount { get; set; }
            public int EmptyAnnotationCount { get; set; }
            public int ImagesWithObjects { get; set; }
            public int TotalObjects { get; set; }
            public Dictionary<int, int> ClassObjectCounts { get; } = new Dictionary<int, int>();
        }

        private static SplitAnnotationStats AnalyzeSplitAnnotations(string experimentFolder, string splitName)
        {
            var stats = new SplitAnnotationStats();
            try
            {
                var splitRoot = Path.Combine(experimentFolder, splitName);
                var imagesDir = Path.Combine(splitRoot, "images");
                var labelsDir = Path.Combine(splitRoot, "labels");
                if (!Directory.Exists(imagesDir) || !Directory.Exists(labelsDir))
                    return stats;

                stats.Exists = true;
                var imgExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff" };

                var imageFiles = Directory.EnumerateFiles(imagesDir)
                    .Where(f => imgExt.Contains(Path.GetExtension(f)))
                    .ToList();
                stats.ImageCount = imageFiles.Count;

                stats.LabelFileCount = Directory.EnumerateFiles(labelsDir, "*.txt", SearchOption.TopDirectoryOnly).Count();

                foreach (var imagePath in imageFiles)
                {
                    var stem = Path.GetFileNameWithoutExtension(imagePath);
                    var labelPath = Path.Combine(labelsDir, stem + ".txt");
                    if (!File.Exists(labelPath))
                    {
                        stats.MissingLabelCount++;
                        stats.EmptyAnnotationCount++;
                        continue;
                    }

                    var lineCount = 0;
                    foreach (var raw in File.ReadLines(labelPath))
                    {
                        var line = raw?.Trim();
                        if (string.IsNullOrWhiteSpace(line))
                            continue;

                        var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 2)
                            continue;

                        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var clsId)
                            && !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.CurrentCulture, out clsId)
                            && !(double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var clsDouble) && (clsId = (int)Math.Round(clsDouble)) >= 0))
                        {
                            continue;
                        }

                        lineCount++;
                        stats.TotalObjects++;
                        if (!stats.ClassObjectCounts.ContainsKey(clsId))
                            stats.ClassObjectCounts[clsId] = 0;
                        stats.ClassObjectCounts[clsId]++;
                    }

                    if (lineCount == 0)
                        stats.EmptyAnnotationCount++;
                    else
                        stats.ImagesWithObjects++;
                }

                return stats;
            }
            catch
            {
                return stats;
            }
        }

        private string BuildHypYaml()
        {
            var hsvH = GetOptionValue("Hue", 0.0);
            var hsvS = GetOptionValue("Saturation", 1.0);
            var hsvV = GetOptionValue("Value (Brightness)", 1.0);
            var degrees = Math.Max(Math.Abs(GetOptionValue("Shear X", 0.0)), Math.Abs(GetOptionValue("Shear Y", 0.0)));
            var translate = Math.Max(Math.Abs(GetOptionValue("Translate X", 0.0)), Math.Abs(GetOptionValue("Translate Y", 0.0)));
            var scale = Math.Abs(GetOptionValue("Scale", 1.0) - 1.0);
            var shear = Math.Max(Math.Abs(GetOptionValue("Shear X", 0.0)), Math.Abs(GetOptionValue("Shear Y", 0.0)));
            var perspective = Math.Abs(GetOptionValue("Perspective", 0.0));
            var flipud = IsEnabled("Flip Vertical") ? 1.0 : 0.0;
            var fliplr = IsEnabled("Flip Horizontal") ? 1.0 : 0.0;
            var mosaic = IsEnabled("Mosaic (4 img)") ? GetOptionProbability("Mosaic (4 img)", 0.0) : 0.0;
            var mixup = IsEnabled("MixUp") ? GetOptionProbability("MixUp", 0.0) : 0.0;
            var copyPaste = IsEnabled("Copy-Paste") ? GetOptionProbability("Copy-Paste", 0.0) : 0.0;

            var sb = new StringBuilder();
            sb.AppendLine($"hsv_h: {hsvH:0.####}");
            sb.AppendLine($"hsv_s: {hsvS:0.####}");
            sb.AppendLine($"hsv_v: {hsvV:0.####}");
            sb.AppendLine($"degrees: {degrees:0.####}");
            sb.AppendLine($"translate: {translate:0.####}");
            sb.AppendLine($"scale: {scale:0.####}");
            sb.AppendLine($"shear: {shear:0.####}");
            sb.AppendLine($"perspective: {perspective:0.######}");
            sb.AppendLine($"flipud: {flipud:0.####}");
            sb.AppendLine($"fliplr: {fliplr:0.####}");
            sb.AppendLine($"mosaic: {mosaic:0.####}");
            sb.AppendLine($"mixup: {mixup:0.####}");
            sb.AppendLine($"copy_paste: {copyPaste:0.####}");
            return sb.ToString();
        }

        private bool IsEnabled(string name)
        {
            return AllAugmentations().Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase) && x.IsEnabled);
        }

        private double GetOptionValue(string name, double fallback)
        {
            var found = AllAugmentations().FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            return found?.Value ?? fallback;
        }

        private double GetOptionProbability(string name, double fallback)
        {
            var found = AllAugmentations().FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            return found?.Probability ?? fallback;
        }

        #endregion

        #region Settings

        private sealed class UserSettingsData
        {
            public GlobalSettings Global { get; set; } = new GlobalSettings();
            public Dictionary<string, ProjectSettings> Projects { get; set; } = new Dictionary<string, ProjectSettings>(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class GlobalSettings
        {
            public int TileSize { get; set; } = 640;
            public double Overlap { get; set; } = 0.2;
            public int TrainPercent { get; set; } = 70;
            public int ValPercent { get; set; } = 20;
            public int TestPercent { get; set; } = 10;
            public int BackgroundLimit { get; set; } = 20;
            public bool BackgroundLimitIsPercent { get; set; } = true;
            public bool EnableClassBalancing { get; set; } = true;
            public string SelectedBalanceMethod { get; set; } = "Median";
            public bool DebugMode { get; set; }
            public bool ApplyToVal { get; set; }
            public bool ApplyToTest { get; set; }
            public int Seed { get; set; }
            public string DatasetType { get; set; } = "Detection";
            public string PreviewTilesFolder { get; set; } = string.Empty;
            public List<string> PresetHistory { get; set; } = new List<string>();
            public AugmentationPresetDto LastAugmentations { get; set; } = new AugmentationPresetDto();
        }

        private sealed class ProjectSettings
        {
            public string SelectedOrtho { get; set; } = string.Empty;
        }

        private string GetCurrentProjectKey()
        {
            try
            {
                var uri = Project.Current?.URI;
                if (string.IsNullOrWhiteSpace(uri))
                    return string.Empty;

                var dir = Path.GetDirectoryName(uri);
                return string.IsNullOrWhiteSpace(dir)
                    ? string.Empty
                    : Path.GetFullPath(dir).ToLowerInvariant();
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

                var g = _settingsData.Global ?? new GlobalSettings();
                _settingsData.Global = g;

                TileSize = g.TileSize;
                Overlap = g.Overlap;
                _trainPercent = ClampPercent(g.TrainPercent);
                _valPercent = ClampPercent(g.ValPercent);
                _testPercent = ClampPercent(g.TestPercent);
                NotifyPropertyChanged(nameof(TrainPercent));
                NotifyPropertyChanged(nameof(ValPercent));
                NotifyPropertyChanged(nameof(TestPercent));
                RebalanceSplit(SplitField.Train);

                BackgroundLimit = g.BackgroundLimit;
                BackgroundLimitIsPercent = g.BackgroundLimitIsPercent;
                EnableClassBalancing = g.EnableClassBalancing;
                SelectedBalanceMethod = string.IsNullOrWhiteSpace(g.SelectedBalanceMethod) ? "Median" : g.SelectedBalanceMethod;
                DebugMode = g.DebugMode;
                ApplyToVal = g.ApplyToVal;
                ApplyToTest = g.ApplyToTest;
                Seed = g.Seed;
                SelectedDatasetType = string.IsNullOrWhiteSpace(g.DatasetType) ? "Detection" : g.DatasetType;

                if (!string.IsNullOrWhiteSpace(g.PreviewTilesFolder) && Directory.Exists(g.PreviewTilesFolder))
                    PreviewTilesFolder = g.PreviewTilesFolder;

                if (g.LastAugmentations != null)
                    ApplyPresetDto(g.LastAugmentations);

                var projectKey = GetCurrentProjectKey();
                if (!string.IsNullOrWhiteSpace(projectKey)
                    && _settingsData.Projects != null
                    && _settingsData.Projects.TryGetValue(projectKey, out var projectSettings)
                    && !string.IsNullOrWhiteSpace(projectSettings?.SelectedOrtho)
                    && OrthoList.Contains(projectSettings.SelectedOrtho))
                {
                    SelectedOrtho = projectSettings.SelectedOrtho;
                }

                UpdateValidationWarnings();
            }
            catch (Exception ex)
            {
                Tools.Logger.Log($"ERROR: LoadUserSettings(CreateDataset) failed: {ex}");
            }
        }

        private void SaveUserSettings()
        {
            try
            {
                _settingsData ??= new UserSettingsData();
                _settingsData.Global ??= new GlobalSettings();
                _settingsData.Projects ??= new Dictionary<string, ProjectSettings>(StringComparer.OrdinalIgnoreCase);

                var g = _settingsData.Global;
                g.TileSize = TileSize;
                g.Overlap = Overlap;
                g.TrainPercent = TrainPercent;
                g.ValPercent = ValPercent;
                g.TestPercent = TestPercent;
                g.BackgroundLimit = BackgroundLimit;
                g.BackgroundLimitIsPercent = BackgroundLimitIsPercent;
                g.EnableClassBalancing = EnableClassBalancing;
                g.SelectedBalanceMethod = SelectedBalanceMethod;
                g.DebugMode = DebugMode;
                g.ApplyToVal = ApplyToVal;
                g.ApplyToTest = ApplyToTest;
                g.Seed = Seed;
                g.DatasetType = SelectedDatasetType;
                g.PreviewTilesFolder = PreviewTilesFolder ?? string.Empty;
                g.LastAugmentations = BuildPresetDto();

                if (g.PresetHistory == null)
                    g.PresetHistory = new List<string>();
                while (g.PresetHistory.Count > MaxPresetHistoryItems)
                    g.PresetHistory.RemoveAt(g.PresetHistory.Count - 1);

                var projectKey = GetCurrentProjectKey();
                if (!string.IsNullOrWhiteSpace(projectKey))
                {
                    if (!_settingsData.Projects.TryGetValue(projectKey, out var projectSettings) || projectSettings == null)
                    {
                        projectSettings = new ProjectSettings();
                        _settingsData.Projects[projectKey] = projectSettings;
                    }

                    projectSettings.SelectedOrtho = SelectedOrtho ?? string.Empty;
                }

                var dir = Path.GetDirectoryName(_settingsPath);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);

                var json = JsonSerializer.Serialize(_settingsData, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_settingsPath, json);
            }
            catch (Exception ex)
            {
                Tools.Logger.Log($"ERROR: SaveUserSettings(CreateDataset) failed: {ex}");
            }
        }

        #endregion

        #region DTOs

        private sealed class AugmentationPresetDto
        {
            public int Seed { get; set; }
            public bool ApplyToVal { get; set; }
            public bool ApplyToTest { get; set; }
            public List<AugmentationPresetItemDto> Items { get; set; } = new List<AugmentationPresetItemDto>();
        }

        private sealed class AugmentationPresetItemDto
        {
            public string Name { get; set; } = string.Empty;
            public bool IsEnabled { get; set; }
            public double Value { get; set; }
            public double? ValueMin { get; set; }
            public double? ValueMax { get; set; }
            public double Probability { get; set; }
        }

        internal sealed class AugmentationOption : PropertyChangedBase
        {
            private bool _isEnabled;
            private double _value;
            private double _valueMin;
            private double _valueMax;
            private double _probability;

            public AugmentationOption(
                string name,
                bool isEnabled,
                double defaultValue,
                double minValue,
                double maxValue,
                double defaultProbability,
                double minProbability,
                double maxProbability,
                bool isDeterministicOnly = false,
                bool? showValueSlider = null,
                bool? showProbabilitySlider = null,
                string description = null,
                string recommendation = null)
                : this(
                    name,
                    isEnabled,
                    defaultValue,
                    minValue,
                    maxValue,
                    defaultValue,
                    defaultValue,
                    defaultProbability,
                    minProbability,
                    maxProbability,
                    isDeterministicOnly,
                    showValueSlider,
                    showProbabilitySlider,
                    description,
                    recommendation)
            {
            }

            public AugmentationOption(
                string name,
                bool isEnabled,
                double defaultValue,
                double minValue,
                double maxValue,
                double defaultValueMin,
                double defaultValueMax,
                double defaultProbability,
                double minProbability,
                double maxProbability,
                bool isDeterministicOnly = false,
                bool? showValueSlider = null,
                bool? showProbabilitySlider = null,
                string description = null,
                string recommendation = null)
            {
                Name = name;
                _isEnabled = isEnabled;
                _value = defaultValue;
                _valueMin = defaultValueMin;
                _valueMax = defaultValueMax;
                _probability = defaultProbability;
                DefaultValue = defaultValue;
                MinValue = minValue;
                MaxValue = maxValue;
                DefaultValueMin = Math.Max(minValue, Math.Min(defaultValueMin, maxValue));
                DefaultValueMax = Math.Max(minValue, Math.Min(defaultValueMax, maxValue));
                if (DefaultValueMin > DefaultValueMax)
                {
                    var tmp = DefaultValueMin;
                    DefaultValueMin = DefaultValueMax;
                    DefaultValueMax = tmp;
                }
                DefaultProbability = defaultProbability;
                MinProbability = minProbability;
                MaxProbability = maxProbability;
                IsDeterministicOnly = isDeterministicOnly;

                var defaultShowValueSlider = !IsRotationOrFlip(name);
                var defaultShowProbabilitySlider = !IsRotationOrFlip(name);
                ShowValueSlider = showValueSlider ?? defaultShowValueSlider;
                ShowProbabilitySlider = showProbabilitySlider ?? defaultShowProbabilitySlider;

                var guidance = BuildGuidance(name);
                Description = string.IsNullOrWhiteSpace(description) ? guidance.description : description;
                Recommendation = string.IsNullOrWhiteSpace(recommendation) ? guidance.recommendation : recommendation;

                ApplyRangeClamp();
            }

            public string Name { get; }
            public bool IsDeterministicOnly { get; }
            public double DefaultValue { get; }
            public double MinValue { get; }
            public double MaxValue { get; }
            public double DefaultValueMin { get; }
            public double DefaultValueMax { get; }
            public double DefaultProbability { get; }
            public double MinProbability { get; }
            public double MaxProbability { get; }
            public bool ShowValueSlider { get; }
            public bool ShowProbabilitySlider { get; }
            public string Description { get; }
            public string Recommendation { get; }
            public int Precision => ComputePrecision();
            public double SpinStep => ComputeSpinStep();

            public bool IsEnabled
            {
                get => _isEnabled;
                set => SetProperty(ref _isEnabled, value, () => IsEnabled);
            }

            public double Value
            {
                get => _value;
                set
                {
                    var clamped = value;
                    if (clamped < MinValue) clamped = MinValue;
                    if (clamped > MaxValue) clamped = MaxValue;
                    clamped = Math.Round(clamped, Precision, MidpointRounding.AwayFromZero);
                    if (SetProperty(ref _value, clamped, () => Value))
                    {
                        NotifyPropertyChanged(nameof(ValueText));
                        if (_valueMin > _value)
                            SetProperty(ref _valueMin, _value, () => ValueMin);
                        if (_valueMax < _value)
                            SetProperty(ref _valueMax, _value, () => ValueMax);
                    }
                }
            }

            public double ValueMin
            {
                get => _valueMin;
                set
                {
                    var clamped = value;
                    if (clamped < MinValue) clamped = MinValue;
                    if (clamped > MaxValue) clamped = MaxValue;
                    if (clamped > ValueMax)
                        clamped = ValueMax;
                    clamped = Math.Round(clamped, Precision, MidpointRounding.AwayFromZero);
                    if (SetProperty(ref _valueMin, clamped, () => ValueMin))
                    {
                        NotifyPropertyChanged(nameof(ValueMinText));
                        if (Value < _valueMin)
                            SetProperty(ref _value, _valueMin, () => Value);
                    }
                }
            }

            public double ValueMax
            {
                get => _valueMax;
                set
                {
                    var clamped = value;
                    if (clamped < MinValue) clamped = MinValue;
                    if (clamped > MaxValue) clamped = MaxValue;
                    if (clamped < ValueMin)
                        clamped = ValueMin;
                    clamped = Math.Round(clamped, Precision, MidpointRounding.AwayFromZero);
                    if (SetProperty(ref _valueMax, clamped, () => ValueMax))
                    {
                        NotifyPropertyChanged(nameof(ValueMaxText));
                        if (Value > _valueMax)
                            SetProperty(ref _value, _valueMax, () => Value);
                    }
                }
            }

            public double Probability
            {
                get => _probability;
                set
                {
                    var clamped = value;
                    if (clamped < MinProbability) clamped = MinProbability;
                    if (clamped > MaxProbability) clamped = MaxProbability;
                    clamped = Math.Round(clamped, 2, MidpointRounding.AwayFromZero);
                    if (SetProperty(ref _probability, clamped, () => Probability))
                        NotifyPropertyChanged(nameof(ProbabilityText));
                }
            }

            public string ValueText
            {
                get => Value.ToString($"F{Precision}", CultureInfo.CurrentCulture);
                set
                {
                    if (TryParseDouble(value, out var parsed))
                        Value = parsed;
                    NotifyPropertyChanged(nameof(ValueText));
                }
            }

            public string ValueMinText
            {
                get => ValueMin.ToString($"F{Precision}", CultureInfo.CurrentCulture);
                set
                {
                    if (TryParseDouble(value, out var parsed))
                        ValueMin = parsed;
                    NotifyPropertyChanged(nameof(ValueMinText));
                }
            }

            public string ValueMaxText
            {
                get => ValueMax.ToString($"F{Precision}", CultureInfo.CurrentCulture);
                set
                {
                    if (TryParseDouble(value, out var parsed))
                        ValueMax = parsed;
                    NotifyPropertyChanged(nameof(ValueMaxText));
                }
            }

            public string ProbabilityText
            {
                get => Probability.ToString("F2", CultureInfo.CurrentCulture);
                set
                {
                    if (TryParseDouble(value, out var parsed))
                        Probability = parsed;
                    NotifyPropertyChanged(nameof(ProbabilityText));
                }
            }

            private static bool IsRotationOrFlip(string name)
            {
                return !string.IsNullOrWhiteSpace(name)
                    && (name.StartsWith("Rotate", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith("Flip", StringComparison.OrdinalIgnoreCase));
            }

            private void ApplyRangeClamp()
            {
                ValueMin = _valueMin;
                ValueMax = _valueMax;
                Value = _value;
            }

            private int ComputePrecision()
            {
                var step = ComputeSpinStep();
                if (step >= 1.0)
                    return 0;
                if (step >= 0.1)
                    return 1;
                if (step >= 0.01)
                    return 2;
                if (step >= 0.001)
                    return 3;
                return 4;
            }

            private double ComputeSpinStep()
            {
                var span = Math.Abs(MaxValue - MinValue);
                if (span <= 0.01)
                    return 0.0001;
                if (span <= 0.1)
                    return 0.001;
                if (span <= 1.0)
                    return 0.001;
                if (span <= 10.0)
                    return 0.1;
                return 1.0;
            }

            private static bool TryParseDouble(string text, out double value)
            {
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
                    return true;
                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
            }

            public ICommand IncrementValueMinCommand => new RelayCommand(_ => ValueMin += SpinStep);
            public ICommand DecrementValueMinCommand => new RelayCommand(_ => ValueMin -= SpinStep);
            public ICommand IncrementValueMaxCommand => new RelayCommand(_ => ValueMax += SpinStep);
            public ICommand DecrementValueMaxCommand => new RelayCommand(_ => ValueMax -= SpinStep);
            public ICommand IncrementProbabilityCommand => new RelayCommand(_ => Probability += 0.01);
            public ICommand DecrementProbabilityCommand => new RelayCommand(_ => Probability -= 0.01);

            private static (string description, string recommendation) BuildGuidance(string name)
            {
                switch (name)
                {
                    case "Rotate 90° CW":
                        return (
                            "Создаёт детерминированную копию, поворачивая тайл на 90° по часовой стрелке.",
                            "Полезно, если объекты могут встречаться в любой ориентации по оси карты.");
                    case "Rotate 180°":
                        return (
                            "Создаёт детерминированную копию с переворотом на 180°.",
                            "Усиливает устойчивость модели к инверсной ориентации сцены.");
                    case "Rotate 270° CW":
                        return (
                            "Создаёт детерминированную копию, поворачивая тайл на 270° по часовой стрелке.",
                            "Добавляйте вместе с другими поворотами для полного покрытия ориентаций.");
                    case "Flip Horizontal":
                        return (
                            "Создаёт зеркальную копию по горизонтали.",
                            "Повышает устойчивость к левосторонним/правосторонним вариациям объектов.");
                    case "Flip Vertical":
                        return (
                            "Создаёт зеркальную копию по вертикали.",
                            "Включайте осторожно: при аэро-данных часто полезно, но иногда вносит нереалистичные сцены.");
                    case "Shear X":
                        return (
                            "Наклоняет изображение по оси X, имитируя сдвиг перспективы.",
                            "Умеренные значения (до 5–10°) обычно достаточны, большие могут искажать объекты.");
                    case "Shear Y":
                        return (
                            "Наклоняет изображение по оси Y.",
                            "Используйте совместно с Shear X для устойчивости к наклонным ракурсам.");
                    case "Scale":
                        return (
                            "Масштабирует сцену, изменяя относительный размер объектов.",
                            "Диапазон около 0.8–1.2 обычно даёт реалистичный эффект без сильной деградации.");
                    case "Translate X":
                        return (
                            "Сдвигает изображение по горизонтали.",
                            "Полезно для тренировки на смещённых объектах; слишком большой сдвиг увеличивает долю обрезаний.");
                    case "Translate Y":
                        return (
                            "Сдвигает изображение по вертикали.",
                            "Держите значение умеренным, чтобы не терять аннотации у края кадра.");
                    case "Perspective":
                        return (
                            "Добавляет перспективное искажение для имитации изменения точки наблюдения.",
                            "Начинайте с малых значений (0.0005–0.001), чтобы избежать сильной деформации.");
                    case "Random Crop":
                        return (
                            "Случайно кадрирует изображение, оставляя выбранную долю исходного кадра.",
                            "Снижение параметра усиливает разнообразие, но может увеличить число пустых тайлов.");
                    case "Hue":
                        return (
                            "Смещает оттенок в HSV, меняя цветовой тон объектов и фона.",
                            "Полезно при разных условиях освещения и сезонности.");
                    case "Saturation":
                        return (
                            "Изменяет насыщенность цветов.",
                            "Избегайте экстремумов, чтобы не получить неестественные текстуры.");
                    case "Value (Brightness)":
                        return (
                            "Регулирует яркость изображения.",
                            "Небольшие колебания помогают модели переноситься между светлыми и тёмными сценами.");
                    case "Contrast":
                        return (
                            "Увеличивает или уменьшает контраст между объектом и фоном.",
                            "Умеренный контраст полезен для устойчивости к разным сенсорам/экспозициям.");
                    case "CLAHE":
                        return (
                            "Локально выравнивает контраст, усиливая детали в тёмных и светлых участках.",
                            "Используйте умеренно, чтобы не переусилить шум.");
                    case "Auto Contrast":
                        return (
                            "Автоматически растягивает гистограмму с отсечением крайних значений.",
                            "Хорошо работает для разнородных снимков с нестабильной экспозицией.");
                    case "Grayscale":
                        return (
                            "Переводит изображение в оттенки серого по вероятности.",
                            "Помогает уменьшить зависимость модели от цвета, если важна форма/текстура.");
                    case "Solarize":
                        return (
                            "Инвертирует пиксели выше порога, создавая контрастный эффект.",
                            "Используйте редко и с низкой вероятностью как регуляризацию.");
                    case "Posterize":
                        return (
                            "Уменьшает число бит на канал, упрощая цветовые градации.",
                            "Подходит для повышения устойчивости к квантованию/сжатию.");
                    case "Equalize":
                        return (
                            "Глобально выравнивает гистограмму яркости.",
                            "Полезно для выравнивания контраста между разными сценами.");
                    case "Gaussian Blur":
                        return (
                            "Размывает изображение гауссовым фильтром.",
                            "Имитирует дефокус/смаз; большие ядра применяйте осторожно.");
                    case "Median Blur":
                        return (
                            "Медианное размытие подавляет импульсный шум, сохраняя границы.",
                            "Хорошо работает при шумных изображениях без сильной потери контуров.");
                    case "Gaussian Noise":
                        return (
                            "Добавляет гауссов шум к пикселям.",
                            "Повышает устойчивость к шумным сенсорам, но избыток ухудшает качество меток.");
                    case "Salt & Pepper":
                        return (
                            "Добавляет импульсный шум (белые/чёрные точки).",
                            "Используйте малую плотность для имитации артефактов передачи/сжатия.");
                    case "Random Shadow":
                        return (
                            "Накладывает искусственные тени.",
                            "Полезно для аэрофото с переменным освещением; не завышайте долю применения.");
                    case "Rain / Fog":
                        return (
                            "Имитирует атмосферные эффекты дождя/тумана.",
                            "Умеренная интенсивность улучшает переносимость модели на плохую погоду.");
                    case "Mosaic (4 img)":
                        return (
                            "Комбинирует 4 изображения в одно для увеличения плотности объектов.",
                            "Сильная аугментация для train; включайте с осторожностью для стабильной валидации.");
                    case "MixUp":
                        return (
                            "Линейно смешивает два изображения и их аннотации.",
                            "Низкие значения alpha обычно дают лучший баланс между регуляризацией и читаемостью.");
                    case "Copy-Paste":
                        return (
                            "Копирует объекты между изображениями для увеличения вариативности классов.",
                            "Полезно при дисбалансе классов и редких объектах.");
                    case "CutOut":
                        return (
                            "Случайно скрывает фрагменты изображения прямоугольными вырезами.",
                            "Помогает устойчивости к окклюзиям, но большие области могут удалить важные признаки.");
                    case "Erasing":
                        return (
                            "Вероятностно стирает случайные области изображения.",
                            "Используйте низкую/среднюю вероятность для умеренной регуляризации.");
                    default:
                        return (
                            "Преобразование изменяет внешний вид обучающих изображений.",
                            "Подбирайте параметры постепенно и проверяйте эффект в Preview.");
                }
            }
        }

        internal sealed class AnnotationLayerSelection : PropertyChangedBase
        {
            private string _name = string.Empty;
            private bool _isSelected;

            public string Name
            {
                get => _name;
                set => SetProperty(ref _name, value, () => Name);
            }

            public bool IsSelected
            {
                get => _isSelected;
                set => SetProperty(ref _isSelected, value, () => IsSelected);
            }
        }

        internal sealed class PreviewOperationItem
        {
            public string Category { get; set; } = string.Empty;
            public string DisplayName { get; set; } = string.Empty;
            public string Key { get; set; } = string.Empty;
        }

        #endregion
    }
}
