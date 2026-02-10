using Microsoft.Win32;
using Newtonsoft.Json;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WWiseToolsWPF.Classes.AppClasses;

namespace WWiseToolsWPF.Views
{
    public partial class Collator : UserControl
    {
        // Process management
        private readonly ConcurrentDictionary<int, Process> _runningProcesses = new();

        // Logging and concurrency
        private Logger _logger;
        private readonly SemaphoreSlim _conversionSemaphore = new SemaphoreSlim(Math.Max(4, Environment.ProcessorCount));
        private readonly SemaphoreSlim _fileProcessingSemaphore = new SemaphoreSlim(1);

        private bool inputDirSelected = false;
        private bool outputDirSelected = false;

        private bool englishSelected = false;
        private bool chineseSelected = false;
        private bool japaneseSelected = false;
        private bool koreanSelected = false;

        private string InDir = Properties.Settings.Default.InputDirectory;
        private string OutDir = Properties.Settings.Default.OutputDirectory;

        public Collator()
        {
            InitializeComponent();

            _logger = new Logger(StatusTextBox);
            Unloaded += (_, _) => _logger?.Dispose();

            InputDirectoryTextBox.Text = InDir ?? "No Input Directory Selected.";
            OutputDirectoryTextBox.Text = OutDir ?? "No Output Directory Selected.";
        }

        #region GUI Elements

        private void InputDirectoryButton_Click(object sender, EventArgs e)
        {
            var fbd = new OpenFolderDialog();
            bool? res = fbd.ShowDialog();
            if (res == true)
            {
                InDir = fbd.FolderName;
                InputDirectoryTextBox.Text = fbd.FolderName;

                _logger.Enqueue($"Input set as: {InDir}");

                inputDirSelected = true;
            }
        }
        private void OutputDirectoryButton_Click(object sender, EventArgs e)
        {
            var fbd = new OpenFolderDialog();
            bool? res = fbd.ShowDialog();
            if (res == true)
            {
                OutDir = fbd.FolderName;
                OutputDirectoryTextBox.Text = fbd.FolderName;

                _logger.Enqueue($"Output set as: {OutDir}");

                outputDirSelected = true;
            }
        }

        private void EnglishCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            _logger.Enqueue("Selected 'English'.", System.Drawing.Color.Green);
            englishSelected = true;
        }

        private void EnglishCheckBox_UnChecked(object sender, RoutedEventArgs e) =>
            _logger.Enqueue("Deselected 'English'.", System.Drawing.Color.DimGray);

        private void ChineseCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            _logger.Enqueue("Selected 'Chinese'.", System.Drawing.Color.Green);
            chineseSelected = true;
        }

        private void ChineseCheckBox_UnChecked(object sender, RoutedEventArgs e) =>
            _logger.Enqueue("Deselected 'Chinese'.", System.Drawing.Color.DimGray);

        private void JapaneseCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            _logger.Enqueue("Selected 'Japanese'.", System.Drawing.Color.Green);
            japaneseSelected = true;
        }

        private void JapaneseCheckBox_UnChecked(object sender, RoutedEventArgs e) =>
            _logger.Enqueue("Deselected 'Japanese'.", System.Drawing.Color.DimGray);

        private void KoreanCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            _logger.Enqueue("Selected 'Korean'.", System.Drawing.Color.Green);
            koreanSelected = true;
        }

        private void KoreanCheckBox_UnChecked(object sender, RoutedEventArgs e) =>
            _logger.Enqueue("Deselected 'Korean'.", System.Drawing.Color.DimGray);

        private async void RunButton_Click(object sender, EventArgs e)
        {
            if (inputDirSelected == false || outputDirSelected == false)
            {
                _logger.Enqueue("Please select a valid Input & Output.", System.Drawing.Color.Red);
                return;
            }
            if (englishSelected == false && chineseSelected == false && japaneseSelected == false && koreanSelected == false)
            {
                _logger.Enqueue("Please select a language.", System.Drawing.Color.Red);
                return;
            }

            string outputFileName = Path.Join(OutDir, "VoiceItemsParsed.txt");

            string[] files = Directory.GetFiles(InDir);

            _logger.Enqueue($"Attempting to collate: {files.Length} files from Input directory.", System.Drawing.Color.Purple);

            var result = new ConcurrentBag<string>();

            string[] keyNames = { "_sourceNames", "SourceNames", "sourceNames", "OFEEIPOMNKD", "EIKJKDICKMJ", "DHMACMBAEHG", "FOLFEPNIKEC", "FONIFKPLDGC", "HLGDIILMGCB", "LPFADPAJNJE", "JKHGLBHOKIC", "JKDJFGBGOEB" };
            string[] fieldNames = { "sourceFileName", "CBGLAJNLFCB", "HLGOMILNFNK", "NCPBJNJNCEI", "HPAJGPIFDKB", "POLDPGADMOJ", "KEGGFHAFNBM", "AJHGGOIEIFN", "BJDAJEKPCFP", "DCIHFJLBLAP" };

            await Task.WhenAll(files.Select(async fileName =>
            {
                try
                {
                    var data = JsonConvert.DeserializeObject<Dictionary<string, dynamic>>(await File.ReadAllTextAsync(fileName));

                    foreach (var file in data!.Values)
                    {
                        foreach (var key in keyNames)
                        {
                            if (file.ContainsKey(key))
                            {
                                var sourceNames = file[key] as IEnumerable<dynamic>;
                                if (sourceNames == null) continue;

                                foreach (var src in sourceNames)
                                {
                                    string? srcFileName = null;

                                    foreach (var field in fieldNames)
                                    {
                                        if (src.ContainsKey(field))
                                        {
                                            srcFileName = src[field]?.ToString()?.ToLower();
                                            if (srcFileName != null) break;
                                        }
                                    }

                                    if (srcFileName == null) continue;

                                    if (englishSelected == true)
                                    {
                                        result.Add($"english(us)\\{srcFileName}");
                                    }
                                    if (chineseSelected == true)
                                    {
                                        result.Add($"chinese\\{srcFileName}");
                                    }
                                    if (japaneseSelected == true)
                                    {
                                        result.Add($"japanese\\{srcFileName}");
                                    }
                                    if (koreanSelected == true)
                                    {
                                        result.Add($"korean\\{srcFileName}");
                                    }
                                    else
                                    {
                                        result.Add($"{srcFileName}");
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    string errorMessage = $"An error occurred: {ex.Message}\n";
                    errorMessage += $"The file causing the error is: {fileName}";

                    _logger.Enqueue(errorMessage, System.Drawing.Color.Red);
                }
            }));

            var sortedResult = new HashSet<string>(result).OrderBy(r => r).ToList();

            _logger.Enqueue($"Found {result.Count} valid results.", System.Drawing.Color.Green);
            await File.WriteAllLinesAsync(outputFileName, sortedResult);
            _logger.Enqueue($"Run finished. {outputFileName} generated.", System.Drawing.Color.Green);
        }

        #endregion

        #region General Helpers
        private static void EnsureParentDirectory(string path)
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
            }
            catch { }
        }

        private bool IsCheckedSafe(CheckBox cb)
        {
            try
            {
                if (cb == null) return false;
                if (Dispatcher.CheckAccess()) return cb.IsChecked == true;
                return Dispatcher.Invoke(() => cb.IsChecked == true);
            }
            catch
            {
                return false;
            }
        }

        #endregion
    }
}