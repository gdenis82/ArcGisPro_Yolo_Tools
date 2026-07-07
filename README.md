# ArcGIS Pro YOLO Detection Tool

![ArcGIS Pro](https://img.shields.io/badge/ArcGIS%20Pro-3.7-blue?logo=esri&logoColor=white)
![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)
![C#](https://img.shields.io/badge/C%23-12.0-239120?logo=csharp&logoColor=white)
![Python](https://img.shields.io/badge/Python-3.9+-3776AB?logo=python&logoColor=white)
![YOLO](https://img.shields.io/badge/YOLO-Ultralytics-00FFFF?logo=yolo&logoColor=black)
![License](https://img.shields.io/badge/License-MIT-green)

---

## 📖 Project Description

**ArcGisProAppYolo** is an **ArcGIS Pro 3.7** add-in that integrates **Ultralytics YOLO (You Only Look Once)** object detection directly into the ArcGIS Pro workspace.

The project provides a convenient UI for:
- ✂️ **Cutting orthophotos into tiles** with configurable size and overlap
- 🤖 **Running object detection** with YOLO + SAHI (sliced inference + NMS)
- 📊 **Automatically importing results** (points, masks, bounding boxes, OBB) as shapefiles
- 🗺️ **Integrating results** into the ArcGIS Pro project structure
- 🧪 **Creating YOLO training datasets** (train/valid/test) with Detection / Segmentation / OBB support
- 🔁 **Augmenting datasets** with final set post-processing, including background limits and class balancing

### 🎯 Project Goal

The goal is to simplify object detection workflows for GIS specialists working with orthophotos, allowing the full process, from tile generation to result visualization, to run inside one ArcGIS Pro environment.

---

![](assets/Screenshot_2.png)

## 🛠️ Technology Stack

### Frontend (ArcGIS Pro Add-in)

- **ArcGIS Pro SDK**: 3.7.0.1901
- **.NET**: 10.0
- **C#**: 12.0
- **WPF**: UI with the MVVM pattern
- **XAML**: declarative UI markup

### Backend (Python Processing)

- **Python**: 3.9+ (ArcGIS Pro Python environment)
- **Ultralytics YOLO**: object detection
- **SAHI**: sliced prediction and post-processing (NMS)
- **PyTorch**: neural network backend
- **ArcPy**: geospatial data processing

### Development Tools

- **Visual Studio 2026** (18.4.1+)
- **ArcGIS Pro 3.7**
- **PyCharm 2026**
- **Git/GitHub**: version control
- **NuGet**: .NET dependency management

---

## 📁 Project Structure

```text
ArcGisProAppYolo/
├── 📄 Config.daml                    # Add-in configuration (DAML)
├── 📄 Module1.cs                     # Main add-in module
│
├── 📂 Controls/                      # UI controls
│   └── ShowYoloDock.cs            # Button that opens the dock pane
│
├── 📂 DockPanes/                     # Tool panels (MVVM)
│   ├── YoloDockPane.cs            # ViewModel (business logic)
│   ├── YoloDockPaneView.xaml      # View (UI markup)
│   └── YoloDockPaneView.xaml.cs   # View code-behind
│
├── 📂 Infrastructure/                # Helper classes
│   └── RelayCommand.cs            # ICommand implementation
│
├── 📂 Tools/                         # Business logic
│   ├── Logger.cs                  # Logging
│   ├── TileGenerator.cs           # Tile generation
│   ├── PythonRunner.cs            # Python script runner
│   └── ResultImporter.cs          # Result import
│
├── 📂 opp_yolo_tool/                 # Python modules
│   ├── models.py                  # Model definitions
│   ├── utils.py                   # Helper functions
│   ├── predict_module.py          # YOLO detection
│   ├── tile_generator.py          # Tile generation (arcpy)
│   ├── create_dataset_module.py   # train/valid/test + label .txt generation
│   └── augmentation_module.py     # Image and annotation augmentation
│
└── 📂 Images/                        # Add-in icons
```

### 📦 Create Dataset Output Structure

```text
OrthoMapping/<OrthoName>/DataSet/<experiment_name>/
├── train/
│   ├── images/
│   └── labels/
├── valid/
│   ├── images/
│   └── labels/
├── test/
│   ├── images/
│   └── labels/
├── debug/                              # When DebugMode is enabled
│   ├── train/
│   ├── valid/
│   └── test/
├── data.yaml
├── hyp.yaml
├── augmentation_config.yaml
├── dataset_report.txt
├── dataset_build_summary.json
├── augmentation_run_summary.json
├── create_dataset_stdout.log
├── create_dataset_stderr.log
├── augmentation_stdout.log
└── augmentation_stderr.log
```

### 🧩 `augmentation_module.py`

This module augments an existing YOLO dataset (`train/valid/test`):

- deterministic transforms (`rot90`, `rot180`, `rot270`, `fliph`, `flipv`, `fliph_rot90`, `fliph_rot270`);
- random geometry, color, noise, and advanced augmentations (`mosaic`, `mixup`, `copy-paste`, `cutout`, `erasing`);
- synchronized `.txt` annotation updates for every generated image;
- debug visualization of annotations on augmented variants when `--debug` is enabled.

### 🧩 `create_dataset_module.py`

This module builds a base dataset from tiles and selected annotation layers:

- reads `Tile_Grid_Pixels.shp` and tile geometry;
- creates the `train/valid/test` split from the configured percentages and seed;
- supports annotation formats:
  - `Detection` (YOLO bbox),
  - `Segmentation` (YOLO polygon),
  - `OBB` (YOLO oriented box, 4 points);
- filters near-empty black/white tiles without annotations;
- creates debug annotation renders when debug mode is enabled;
- writes `dataset_build_summary.json` with final statistics.

### 🧠 Create Dataset Pipeline

1. Generate or reuse tiles from the ArcGIS Pro panel.
2. Build the base dataset with `create_dataset_module.py`.
3. Run augmentation with `augmentation_module.py`.
4. Post-process the final train set after augmentation:
   - limit background share/count,
   - downsample classes by `Median / Average / Minimum`.
5. Generate `dataset_report.txt` with statistics:
   - `Base / before augmentation`,
   - `Final / after augmentation`.

---

## 🚀 Installation and Launch

### Requirements

- ✅ **ArcGIS Pro 3.7** installed and activated
- ✅ **Visual Studio 2026** (18.4.1+) with ArcGIS Pro SDK
- ✅ **Python 3.9+** bundled with ArcGIS Pro
- ✅ **Ultralytics YOLO** and **PyTorch** for detection

### 1️⃣ Install Python Dependencies

Open **Python Command Prompt** from ArcGIS Pro:

```bash
# Clone the default arcgispro-py3 environment to create an environment named arcgispro-py3-clone.
conda create --clone arcgispro-py3 --name arcgispro-py3-clone --pinned

# The --pinned flag from Esri copies the pinned file from the source environment
# into the cloned environment. Use it to preserve environment integrity when
# updating or installing packages.

# Activate the ArcGIS Pro environment
activate arcgispro-py3-clone

# Install Ultralytics YOLO
conda install ultralytics

# Install SAHI for sliced inference
conda install conda-forge::sahi
```

### Create a Dataset (ArcGIS Pro Python, requires `arcpy`)

```bash
python opp_yolo_tool/create_dataset_module.py \
  --tiles-folder "C:\MyProject\OrthoMapping\Ortho_2024_01\Tiles\640px" \
  --dataset-root "C:\MyProject\OrthoMapping\Ortho_2024_01\DataSet\exp_001" \
  --train 70 --val 20 --test 10 \
  --seed 12345 \
  --layers "buildings_train|roads_train" \
  --dataset-type "Segmentation" \
  --aprx "C:\MyProject\MyProject.aprx" \
  --debug --debug-dir "C:\MyProject\OrthoMapping\Ortho_2024_01\DataSet\exp_001\debug"
```

### Augment a Dataset

```bash
python opp_yolo_tool/augmentation_module.py \
  --dataset-root "C:\MyProject\OrthoMapping\Ortho_2024_01\DataSet\exp_001" \
  --config "C:\MyProject\OrthoMapping\Ortho_2024_01\DataSet\exp_001\augmentation_config.yaml" \
  --max-per-image 4 \
  --post-background-limit 20 \
  --post-background-limit-is-percent \
  --post-class-balance \
  --post-balance-method median \
  --apply-to-val \
  --debug --debug-dir "C:\MyProject\OrthoMapping\Ortho_2024_01\DataSet\exp_001\debug"
```

📚 **ArcGIS Pro Python documentation:**
- [Install Python for ArcGIS Pro](https://pro.arcgis.com/ru/pro-app/latest/arcpy/get-started/installing-python-for-arcgis-pro.htm)
- [Use Conda with ArcGIS Pro](https://pro.arcgis.com/ru/pro-app/latest/arcpy/get-started/using-conda-with-arcgis-pro.htm)
- [Package Manager](https://doc.esri.com/en/arcgis-pro/latest/arcpy/get-started/what-is-conda.html)
- [Search Conda and Python packages](https://anaconda.org/)

### 2️⃣ Build the Add-in

In Visual Studio:

1. Open `ArcGisProAppYolo.sln`
2. Select **Build -> Clean Solution**
3. Select **Build -> Rebuild Solution**
4. Verify there are **0 errors**

### 3️⃣ Clear the ArcGIS Pro Cache (Required)

Before launching after each rebuild:

```powershell
Remove-Item "$env:LOCALAPPDATA\ESRI\ArcGISPro\AssemblyCache\{a79ff6b9-f9a2-4dc3-8cdb-820811bb9ad8}" -Recurse -Force
```

### 4️⃣ Copy Python Files

Copy the Python files from `opp_yolo_tool` to:

```text
C:\Users\<User_Name>\AppData\Local\ESRI\ArcGISPro\opp_yolo_tool
```

### 5️⃣ Start Debugging

1. Press **F5** in Visual Studio.
2. ArcGIS Pro starts automatically.
3. Open or create a project.
4. On the **Add-In** tab, find the **YOLO Tool** button.
5. The panel opens on the right.

---

## 🧩 Add to ArcGIS Pro

Download the release archive [ArcGisProAppYolo.1.2.0-windows.rar](https://github.com/gdenis82/ArcGisPro_Yolo_Tools/releases).

```text
Extract ArcGisProAppYolo.1.2.0-windows.rar.
Double-click ArcGisProAppYolo.esriAddinX inside the ArcGisProAppYolo.1.2.0-windows folder.
Click Install Add-In in the installer window.
Restart ArcGIS Pro.
The tool appears on the ribbon in the corresponding tab.
Place the opp_yolo_tool folder under ArcGISPro: C:\Users\<USERNAME>\AppData\Local\ESRI\ArcGISPro\opp_yolo_tool
```

### Method 1: Easiest Option (Double-Click)

1. Close ArcGIS Pro if it is open.
2. Go to the plugin folder.
3. Find `ArcGisProAppYolo.esriAddinX` and double-click it.
4. The ArcGIS Pro Add-In installer opens. Click **Install Add-In**.

### Method 2: Through ArcGIS Pro

1. Open ArcGIS Pro.
2. Go to **Settings -> Add-In Manager**.
3. Select **Options**.
4. Add the folder path that contains the `.esriAddinX` file.
5. Enable **Load all Add-Ins without restrictions (Least Secure)**.

---

## 📋 Usage

### Step 1: Prepare Data

Create the following structure in your ArcGIS Pro project:

```text
MyProject/
├── MyProject.aprx
└── OrthoMapping/
    ├── Ortho_2024_01/
    │   └── ortho.tif          # Orthophoto
    └── Ortho_2024_02/
        └── ortho.tif          # Another orthophoto
```

### Step 2: Configure Parameters

In the **YOLO Tool** panel:

1. **Ortho Selection**: select an orthophoto from the list.
2. **Model Configuration**: specify the path to the YOLO model (`.pt` file).
   - Previously selected models are available in the ComboBox history.
3. **Tile Settings**:
   - Tile Size: `640` (recommended size for YOLO)
   - Overlap %: `30` (overlap between tiles)
4. **Detection Settings**:
   - Confidence: `0.5` (confidence threshold)
   - Output Points: enabled (object centroids)
   - Output Masks: enabled (mask polygons)
   - Output BBoxes: enabled (rectangles)
   - Output OBB: enabled (oriented rectangles)
   - Mask Mode:
     - `Largest`: 1 mask = 1 object (largest contour)
     - `Union`: union all contours into one geometry

### Step 3: Run Detection

1. Click **Run Detection**.
   - While processing is running, the button changes to **Cancel**.
2. The process:
   - ✂️ Generate tiles (`OrthoMapping/<OrthoName>/Tiles/Images/`)
   - 🤖 Run YOLO detection
   - 📊 Create shapefiles
   - 📁 Write results to `Detection_Results/<experiment_name>/`

### Step 4: View Results

Results are saved to:

```text
OrthoMapping/<OrthoName>/
├── Tiles/
│   └── <TileSize>px/
│       ├── Images/            # Image tiles
│       └── shapes/            # Tile grid (shapefile)
└── Detection_Results/
    └── <experiment_name>/
        ├── all_detections_sahi.json
        ├── Detected_Points.shp    # Object centroids
        ├── Detected_Masks.shp     # Mask polygons
        ├── Detected_BBoxes.shp    # Bounding boxes
        ├── Detected_OBB.shp       # Oriented bounding boxes
        ├── predict_stdout.log
        └── predict_stderr.log
```

---

## 🐞 Debugging and Logging

### Logs in Visual Studio

- **View -> Output** -> select **Debug**
- All operations are logged with timestamps.

### Log File

The log is saved to:

```text
%TEMP%\ArcGisProAppYolo_YYYYMMDD.log
```

When the panel opens, the path is printed to the Output Window:

```text
Log file location: C:\Users\...\Temp\ArcGisProAppYolo_20260622.log
```

### 🛠️ Common Issues

#### 🔴 Torchvision and Torch Package Compatibility Warning

```text
WARNING torchvision==0.25 is incompatible with torch==2.9.
Run 'pip install torchvision==0.24' to fix torchvision or 'pip install -U torch torchvision' to update both.
For a full compatibility table see https://github.com/pytorch/vision#installation
```

One possible solution:

```bash
conda uninstall pytorch torchvision -y
pip install torch torchvision --index-url https://download.pytorch.org/whl/cu128
pip install ultralytics-thop opencv-python
```

#### 🔴 Empty Panel

**Solution:** Clear the cache and rebuild the project.

#### 🔴 Orthophotos Not Found

**Solution:** Check the `OrthoMapping/` folder structure in the project root.

#### 🔴 Python Script Does Not Start

**Solution:** Check the Python path and installed dependencies.

---

## 🧪 Manual Python Module Run

For testing without ArcGIS Pro:

### Tile Generation

```bash
python opp_yolo_tool/tile_generator.py \
  --ortho-image "C:\MyProject\OrthoMapping\Ortho_2024_01\ortho.tif" \
  --tiles-folder "C:\MyProject\OrthoMapping\Ortho_2024_01\Tiles" \
  --tile-size 640 \
  --overlap 30
```

### Prediction

```bash
python opp_yolo_tool/predict_module.py \
  --tiles-dir "C:\MyProject\OrthoMapping\Ortho_2024_01\Tiles" \
  --model "C:\Models\yolo11x-seg.pt" \
  --confidence 0.5 \
  --outputs point,bbox,mask,obb \
  --mask-mode largest
```

---

## 📚 Documentation

- 📗 [ArcGIS Pro SDK Wiki](https://github.com/Esri/arcgis-pro-sdk/wiki)
- 📕 [ProGuide: Dockpanes](https://github.com/Esri/arcgis-pro-sdk/wiki/ProGuide-Dockpanes)
- 📙 [API Reference](https://pro.arcgis.com/en/pro-app/latest/sdk/api-reference)

---

## 🤝 Contributing

Pull requests are welcome. For major changes, open an issue first to discuss the proposed update.

---

## 📄 License

MIT License

---

## 👨‍💻 Author

**gdenis82** - [GitHub](https://github.com/gdenis82)

---

## ⭐ Acknowledgements

- [Esri ArcGIS Pro SDK](https://github.com/Esri/arcgis-pro-sdk)
- [Ultralytics YOLO](https://github.com/ultralytics/ultralytics)

---
