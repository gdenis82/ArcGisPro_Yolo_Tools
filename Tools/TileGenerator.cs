using ArcGIS.Desktop.Core;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ArcGisProAppYolo.Tools
{
    public static class TileGenerator
    {
        /// <summary>
        /// Подготавливает структуру папок для тайлов: Tiles/Images и Tiles/shapes
        /// Возвращает путь к папки Tiles.
        /// </summary>
        public static string PrepareTilesFolder(string projectUri, string orthoName)
        {
            if (string.IsNullOrEmpty(projectUri)) throw new ArgumentNullException(nameof(projectUri));
            if (string.IsNullOrEmpty(orthoName)) throw new ArgumentNullException(nameof(orthoName));

            var orthoFolder = Path.Combine(projectUri, "OrthoMapping", orthoName);
            if (!Directory.Exists(orthoFolder)) throw new DirectoryNotFoundException($"Ortho folder not found: {orthoFolder}");

            var tilesRoot = Path.Combine(orthoFolder, "Tiles");
            var images = Path.Combine(tilesRoot, "Images");
            var shapes = Path.Combine(tilesRoot, "shapes");

            Directory.CreateDirectory(tilesRoot);
            Directory.CreateDirectory(images);
            Directory.CreateDirectory(shapes);

            // create a manifest file to help later processing
            var manifest = Path.Combine(tilesRoot, "manifest.txt");
            File.WriteAllText(manifest, $"Tiles prepared: {DateTime.UtcNow:O}\nTileSize default: 640\nOverlapPercent default: 30\n");

            return tilesRoot;
        }

        /// <summary>
        /// Подготавливает структуру папок для тайлов внутри .eomw папки: Tiles/Images и Tiles/shapes
        /// Возвращает путь к папке Tiles.
        /// </summary>
        public static string PrepareTilesFolderInEomw(string eomwFolder, int tileSize)
        {
            if (string.IsNullOrEmpty(eomwFolder)) throw new ArgumentNullException(nameof(eomwFolder));

            var tilesRoot = Path.Combine(eomwFolder, "Tiles");
            var tileSetFolder = Path.Combine(tilesRoot, $"{tileSize}px");
            var images = Path.Combine(tileSetFolder, "Images");
            var shapes = Path.Combine(tileSetFolder, "shapes");

            Directory.CreateDirectory(tilesRoot);
            Directory.CreateDirectory(tileSetFolder);
            Directory.CreateDirectory(images);
            Directory.CreateDirectory(shapes);

            // create a manifest file to help later processing
            var manifest = Path.Combine(tilesRoot, "manifest.txt");
            File.WriteAllText(manifest, $"Tiles prepared: {DateTime.UtcNow:O}\nTileSize default: 640\nOverlapPercent default: 30\n");

            return tileSetFolder;
        }

        public static string PrepareTilesFolderInEomw(string eomwFolder)
        {
            return PrepareTilesFolderInEomw(eomwFolder, 640);
        }

        /// <summary>
        /// Генерация тайлов из ортоизображения. Реализовано через вызов Python-скрипта tile_generator.py в папке opp_yolo_tool внутри проекта.
        /// Скрипт использует rasterio (или arcpy если доступен) для сохранения геопривязанных тайлов и создания сетки .shp.
        /// </summary>
        public static async Task<bool> GenerateTilesAsync(string orthoImagePath, string tilesFolder, int tileSize, int overlapPercent, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(orthoImagePath)) throw new ArgumentNullException(nameof(orthoImagePath));
            if (string.IsNullOrEmpty(tilesFolder)) throw new ArgumentNullException(nameof(tilesFolder));

            cancellationToken.ThrowIfCancellationRequested();

            // locate python script - приоритет: проект, затем AppData, затем add-in папка
            string scriptPath = null;
            var projectUri = Project.Current?.URI ?? string.Empty;
            if (!string.IsNullOrEmpty(projectUri))
            {
                var projectDir = Path.GetDirectoryName(projectUri);
                var projectScript = Path.Combine(projectDir, "opp_yolo_tool", "tile_generator.py");
                if (File.Exists(projectScript))
                {
                    scriptPath = projectScript;
                    Logger.Log($"Using tile_generator.py from project: {scriptPath}");
                }
            }

            if (string.IsNullOrEmpty(scriptPath))
            {
                var appDataPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ESRI", "ArcGISPro", "opp_yolo_tool", "tile_generator.py");
                if (File.Exists(appDataPath))
                {
                    scriptPath = appDataPath;
                    Logger.Log($"Using tile_generator.py from AppData: {scriptPath}");
                }
            }

            if (string.IsNullOrEmpty(scriptPath))
            {
                // try relative to add-in installation folder
                var asmFolder = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                scriptPath = Path.Combine(asmFolder ?? string.Empty, "..", "..", "opp_yolo_tool", "tile_generator.py");
                scriptPath = Path.GetFullPath(scriptPath);
            }

            var logsRoot = tilesFolder;
            var folderName = Path.GetFileName(tilesFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.IsNullOrWhiteSpace(folderName) && folderName.EndsWith("px", StringComparison.OrdinalIgnoreCase))
            {
                logsRoot = Directory.GetParent(tilesFolder)?.FullName ?? tilesFolder;
            }

            if (!File.Exists(scriptPath))
            {
                // cannot find script
                File.WriteAllText(Path.Combine(logsRoot, "tiles_error.txt"), $"tile_generator.py not found. Expected at {scriptPath}");
                return false;
            }

            var args = $"--ortho-image \"{orthoImagePath}\" --tiles-folder \"{tilesFolder}\" --tile-size {tileSize} --overlap {overlapPercent}";

            var logs = new System.Text.StringBuilder();
            var errors = new System.Text.StringBuilder();

            // prefer running script via detected propy.bat; PythonRunner will detect propy automatically when pythonExe==null
            var exit = await PythonRunner.RunPythonScriptAsync(null, scriptPath, args, (s) => logs.AppendLine(s), (e) => errors.AppendLine(e), cancellationToken);

            // write logs
            try
            {
                File.WriteAllText(Path.Combine(logsRoot, "tiles_stdout.log"), logs.ToString());
                File.WriteAllText(Path.Combine(logsRoot, "tiles_stderr.log"), errors.ToString());
            }
            catch { }

            return exit == 0;
        }
    }
}
