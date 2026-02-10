using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using WWise_Audio_Tools.Classes.AppClasses;
using WWise_Audio_Tools.Classes.BankClasses;
using WWise_Audio_Tools.Classes.BankClasses.Chunks;
using WWise_Audio_Tools.Classes.PackageClasses;

namespace WWiseToolsWPF.Views
{
    public partial class AudioExtractor : UserControl
    {
        // Process management
        private readonly ConcurrentDictionary<int, Process> _runningProcesses = new();
        private CancellationTokenSource? _abortCts;

        // Logging and concurrency
        private readonly ConcurrentQueue<(string Text, System.Drawing.Color? Color)> _logQueue = new();
        private readonly DispatcherTimer _logTimer;
        private readonly SemaphoreSlim _conversionSemaphore = new SemaphoreSlim(Math.Max(4, Environment.ProcessorCount));
        private readonly SemaphoreSlim _fileProcessingSemaphore = new SemaphoreSlim(1);

        // Checksum index used to skip already-processed files
        private ConcurrentDictionary<string, (string Hash, string Date)>? _checksumIndex;

        // State flags
        private bool doUpdateFormatSettings = false;
        private bool isBusy = false;
        private bool isAborted = false;
        private bool outputDirSelected = false;

        // Data
        public Dictionary<string, string> KnownFilenames { get; } = new();
        public Dictionary<string, string> KnownEvents { get; } = new();

        public AudioExtractor()
        {
            InitializeComponent();

            // Keep the same default texts as the original
            InputFilesTextBox.Text = "No Input Files Selected.";
            OutputDirectoryTextBox.Text = Properties.Settings.Default.OutputDirectory ?? "No Output Directory Selected.";
            KnownFilenamesTextBox.Text = "No Known_Filenames TSV Selected.";
            KnownEventsTextBox.Text = "No Known_Events TSV Selected.";

            // Log flush timer (non-blocking)
            _logTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _logTimer.Tick += (_, __) => FlushLogsToUI();
            _logTimer.Start();

            // restore saved radio state where possible
            WEMExportRadioButton.IsChecked = Properties.Settings.Default.ExportWem;
            WAVExportRadioButton.IsChecked = Properties.Settings.Default.ExportWav;
            OGGExportRadioButton.IsChecked = Properties.Settings.Default.ExportOgg;

            doUpdateFormatSettings = true;
            UpdateCanExportStatus();
        }

        #region Logging (thread-safe)

        private void EnqueueLog(string text, System.Drawing.Color? color = null)
        {
            _logQueue.Enqueue((text, color));
        }

        private void FlushLogsToUI()
        {
            if (_logQueue.IsEmpty) return;

            var entries = new List<(string Text, System.Drawing.Color? Color)>();
            while (_logQueue.TryDequeue(out var e))
                entries.Add(e);

            if (entries.Count == 0) return;

            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                foreach (var e in entries)
                {
                    AppendStatusText(e.Text, e.Color);
                }

                // Defer scrolling until AFTER layout/render
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    StatusTextBox.ScrollToEnd();
                }));
            }));
        }

        private void AppendStatusText(string text, System.Drawing.Color? color = null)
        {
            var paragraph = new Paragraph { Margin = new Thickness(0) };
            var run = new Run(text);
            if (color.HasValue)
            {
                run.Foreground = new SolidColorBrush(ConvertDrawingColor(color.Value));
            }
            paragraph.Inlines.Add(run);
            StatusTextBox.Document.Blocks.Add(paragraph);
        }

        private static System.Windows.Media.Color ConvertDrawingColor(System.Drawing.Color c)
        {
            return System.Windows.Media.Color.FromArgb(c.A, c.R, c.G, c.B);
        }

        #endregion

        #region File/Folder Browsers

        private void InputFilesBrowse_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog
            {
                Multiselect = true,
                Filter = "PCK Files|*.pck|BNK Files|*.bnk|CHK Files|*.chk|WEM Files|*.wem|All Files|*.*"
            };

            bool? result = ofd.ShowDialog();
            if (result == true)
            {
                AppVariables.InputFiles.Clear();
                AppVariables.InputFiles = ofd.FileNames.ToList();

                foreach (var filePath in AppVariables.InputFiles)
                {
                    var fileName = Path.GetFileName(filePath);
                    var folderName = Path.GetFileName(Path.GetDirectoryName(filePath) ?? string.Empty);
                    var outputLine = folderName + "\\" + fileName;

                    InputFilesTextBox.Text = $"Selected {AppVariables.InputFiles.Count} WWise file{(AppVariables.InputFiles.Count > 1 ? "s" : "")} to process.";
                    EnqueueLog($"Successfully loaded {outputLine}", System.Drawing.Color.Green);
                }
            }
            UpdateCanExportStatus();
        }

        // replaced WinForms-style dialog usage errors with explicit WinForms aliasing;
        // this avoids ambiguity and works in WPF projects that reference System.Windows.Forms.
        private void OutputDirectoryBrowse_Click(object sender, RoutedEventArgs e)
        {
            var fbd = new OpenFolderDialog();
            bool? res = fbd.ShowDialog();
            if (res == true)
            {
                AppVariables.OutputDirectory = fbd.FolderName;
                OutputDirectoryTextBox.Text = fbd.FolderName;

                AppVariables.OutputDirectoryWem = Path.Combine(AppVariables.OutputDirectory, "Wem");
                AppVariables.OutputDirectoryWav = Path.Combine(AppVariables.OutputDirectory, "Wav");
                AppVariables.OutputDirectoryOgg = Path.Combine(AppVariables.OutputDirectory, "Ogg");

                outputDirSelected = true;

                EnqueueLog($"Output set as: {AppVariables.OutputDirectory}", System.Drawing.Color.Green);
            }
            UpdateCanExportStatus();
        }

        private void KnownFilenamesBrowse_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog { Multiselect = false, Filter = "Known_Filenames TSV|*.tsv|All Files|*.*" };
            bool? res = ofd.ShowDialog();
            if (res == true)
            {
                var sFileName = ofd.FileName;
                KnownFilenamesTextBox.Text = sFileName;

                var lines = File.ReadAllLines(sFileName);
                KnownFilenames.Clear();
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var entry = line.Split('\t');
                    if (entry.Length >= 2) KnownFilenames[entry[0]] = entry[1];
                }

                EnqueueLog("Successfully loaded Known_Filenames.tsv", System.Drawing.Color.Green);
            }
            UpdateCanExportStatus();
        }

        private void KnownEventsBrowse_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog { Multiselect = false, Filter = "Known_Events TSV|*.tsv|All Files|*.*" };
            bool? res = ofd.ShowDialog();
            if (res == true)
            {
                var sFileName = ofd.FileName;
                KnownEventsTextBox.Text = sFileName;

                var lines = File.ReadAllLines(sFileName);
                KnownEvents.Clear();
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var entry = line.Split('\t');
                    if (entry.Length >= 2) KnownEvents[entry[0]] = entry[1];
                }

                EnqueueLog("Successfully loaded Known_Events.tsv", System.Drawing.Color.Green);
            }
            UpdateCanExportStatus();
        }

        #endregion

        #region Radio / Check handlers

        private void WEMExportRadioButton_Checked(object sender, RoutedEventArgs e)
        {
            if (outputDirSelected == false)
            {
                EnqueueLog("Please select an output directory first.", System.Drawing.Color.Red);
                WEMExportRadioButton.IsChecked = false;
                return;
            }
            EnqueueLog("Selected 'Export to WEM'.", System.Drawing.Color.Green);
            EnqueueLog($"{AppVariables.OutputDirectoryWem}", System.Drawing.Color.Gray);
            UpdateCanExportStatus();
        }

        private void WAVExportRadioButton_Checked(object sender, RoutedEventArgs e)
        {
            if (outputDirSelected == false)
            {
                EnqueueLog("Please select an output directory first.", System.Drawing.Color.Red);
                WAVExportRadioButton.IsChecked = false;
                return;
            }
            EnqueueLog("Selected 'Export to WAV'.", System.Drawing.Color.Green);
            EnqueueLog($"{AppVariables.OutputDirectoryWav}", System.Drawing.Color.Gray);
            UpdateCanExportStatus();
        }

        private void OGGExportRadioButton_Checked(object sender, RoutedEventArgs e)
        {
            if (outputDirSelected == false)
            {
                EnqueueLog("Please select an output directory first.", System.Drawing.Color.Red);
                OGGExportRadioButton.IsChecked = false;
                return;
            }
            EnqueueLog("Selected 'Export to OGG'.", System.Drawing.Color.Green);
            EnqueueLog($"{AppVariables.OutputDirectoryOgg}", System.Drawing.Color.Gray);
            UpdateCanExportStatus();
        }

        private void SplitOutputCheckBox_Checked(object sender, RoutedEventArgs e) =>
            EnqueueLog("Selected 'Split Output'.", System.Drawing.Color.Green);

        private void SplitOutputCheckBox_UnChecked(object sender, RoutedEventArgs e) =>
            EnqueueLog("Deselected 'Split Output'.", System.Drawing.Color.Gray);

        private void BankedOutputCheckBox_Checked(object sender, RoutedEventArgs e) =>
            EnqueueLog("Selected 'Banked Output'.", System.Drawing.Color.Gray);

        private void BankedOutputCheckBox_UnChecked(object sender, RoutedEventArgs e) =>
            EnqueueLog("Deselected 'Banked Output'.", System.Drawing.Color.Gray);

        private void LegacyCheckBox_Checked(object sender, RoutedEventArgs e) =>
            EnqueueLog("Selected 'Legacy Output'.", System.Drawing.Color.Gray);

        private void LegacyCheckBox_UnChecked(object sender, RoutedEventArgs e) =>
            EnqueueLog("Deselected 'Legacy Output'.", System.Drawing.Color.Gray);

        private void NoLangCheckBox_Checked(object sender, RoutedEventArgs e) =>
            EnqueueLog("Selected 'NoLang'.", System.Drawing.Color.Gray);

        private void NoLangCheckBox_UnChecked(object sender, RoutedEventArgs e) =>
            EnqueueLog("Deselected 'NoLang'.", System.Drawing.Color.Gray);

        private void SpreadsheetOutputCheckBox_Checked(object sender, RoutedEventArgs e) =>
            EnqueueLog("Selected 'Spreadsheet Output'.", System.Drawing.Color.Green);

        private void SpreadsheetOutputCheckBox_UnChecked(object sender, RoutedEventArgs e) =>
            EnqueueLog("Deselected 'Spreadsheet Output'.", System.Drawing.Color.Gray);

        #endregion

        #region Export / Pipeline (ported core helpers)

        // Run external process and capture stdout/stderr. Registers process to allow abort.
        private Task<int> RunProcessAsync(string fileName, string arguments, Action<string>? onOutput = null, Action<string>? onError = null)
        {
            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                var psi = new ProcessStartInfo(fileName, arguments)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = onOutput != null,
                    RedirectStandardError = onError != null,
                    CreateNoWindow = true
                };

                var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

                if (onOutput != null)
                {
                    proc.OutputDataReceived += (s, e) => { if (e.Data != null) onOutput(e.Data); };
                }
                if (onError != null)
                {
                    proc.ErrorDataReceived += (s, e) => { if (e.Data != null) onError(e.Data); };
                }

                proc.Exited += (s, e) =>
                {
                    try { tcs.TrySetResult(proc.ExitCode); }
                    catch { }
                    _runningProcesses.TryRemove(proc.Id, out _);
                    proc.Dispose();
                };

                proc.Start();

                try { _runningProcesses.TryAdd(proc.Id, proc); } catch { }

                // Ensure kill on cancellation
                try
                {
                    if (_abortCts is not null)
                    {
                        var token = _abortCts.Token;
                        token.Register(() =>
                        {
                            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                        });
                    }
                }
                catch { }

                if (onOutput != null) proc.BeginOutputReadLine();
                if (onError != null) proc.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            return tcs.Task;
        }

        private async Task ConvertWemBytesToWavFileAsync(byte[] wemData, string wavOutputPath)
        {
            await _conversionSemaphore.WaitAsync();
            try
            {
                if (!Directory.Exists("Processing")) Directory.CreateDirectory("Processing");
                string tempWem = Path.Combine("Processing", Guid.NewGuid().ToString() + ".wem");
                string tempWav = Path.Combine("Processing", Guid.NewGuid().ToString() + ".wav");
                await File.WriteAllBytesAsync(tempWem, wemData);

                string vgmPath = Path.Combine("Tools", "vgmstream-win", "vgmstream-cli.exe");
                var args = $"-o \"{tempWav}\" \"{tempWem}\"";

                var code = await RunProcessAsync(vgmPath, args, null, s =>
                {
                    if (string.IsNullOrWhiteSpace(s)) return;
                    var line = s.Trim();
                    if (IsImportantProcessLine(line)) EnqueueLog(line, System.Drawing.Color.Red);
                });
                if (code != 0) throw new InvalidOperationException($"vgmstream exited with {code}");

                EnsureParentDirectory(wavOutputPath);
                try
                {
                    if (File.Exists(wavOutputPath)) File.Delete(wavOutputPath);
                    File.Move(tempWav, wavOutputPath);
                }
                catch
                {
                    try
                    {
                        File.Copy(tempWav, wavOutputPath, overwrite: true);
                        File.Delete(tempWav);
                    }
                    catch (Exception inner)
                    {
                        EnqueueLog($"Failed to move/copy temp WAV to destination: {inner.Message}", System.Drawing.Color.Red);
                        throw;
                    }
                }
                try { if (File.Exists(tempWem)) File.Delete(tempWem); } catch { }
            }
            finally
            {
                _conversionSemaphore.Release();
            }
        }

        private async Task ConvertWavFileToOggAsync(string wavPath, string oggOutputPath)
        {
            await _conversionSemaphore.WaitAsync();
            try
            {
                string ffmpegPath = Path.Combine("Tools", "ffmpeg-master-latest-win64-gpl-shared", "bin", "ffmpeg.exe");
                EnsureParentDirectory(oggOutputPath);
                var args = $"-y -i \"{wavPath}\" -c:a libvorbis -qscale:a 10 \"{oggOutputPath}\"";

                var code = await RunProcessAsync(ffmpegPath, args, null, s =>
                {
                    if (string.IsNullOrWhiteSpace(s)) return;
                    var line = s.Trim();
                    if (IsImportantProcessLine(line)) EnqueueLog(line, System.Drawing.Color.Red);
                });
                if (code != 0) throw new InvalidOperationException($"ffmpeg exited with {code}");
                EnqueueLog($"{Path.GetFileName(oggOutputPath)} - DONE", System.Drawing.Color.Green);
            }
            finally
            {
                _conversionSemaphore.Release();
            }
        }

        private static bool IsImportantProcessLine(string line)
        {
            var lower = line.ToLowerInvariant();
            if (lower.Contains("frame=") || lower.Contains("fps=") || lower.Contains("size=") || lower.Contains("time=") || lower.Contains("bitrate=") || lower.Contains("speed=") || lower.Contains("progress")) return false;
            if (lower.Contains("active code page")) return false;
            if (lower.Contains("error") || lower.Contains("failed") || lower.Contains("unsupported")) return true;
            if (line.StartsWith("WARNING", StringComparison.OrdinalIgnoreCase)) return true;
            if (line.StartsWith("Error", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public async Task ProcessWemAsync(byte[] data, string path)
        {
            EnsureParentDirectory(path);

            var rel = Path.GetRelativePath(AppVariables.OutputDirectoryWem, path).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.IsNullOrEmpty(rel)) rel = Path.GetFileName(path);

            var md = GetHashFromBytes(data);
            var nowDate = DateTime.Now.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

            if (_checksumIndex is not null && _checksumIndex.TryGetValue(rel, out var existing))
            {
                if (!string.IsNullOrEmpty(existing.Hash) && existing.Hash == md)
                {
                    EnqueueLog($"{Path.GetFileName(rel)} - SKIPPED (already processed)", System.Drawing.Color.Gray);
                    return;
                }
            }

            await File.WriteAllBytesAsync(path, data);

            try
            {
                _checksumIndex ??= new ConcurrentDictionary<string, (string, string)>();
                _checksumIndex[rel] = (md, nowDate);
            }
            catch { }

            if (AppVariables.ExportOgg || AppVariables.ExportWav)
            {
                var wavPath = path.Replace(AppVariables.OutputDirectoryWem, AppVariables.OutputDirectoryWav).Replace(".wem", ".wav");
                await ConvertWemBytesToWavFileAsync(data, wavPath);

                if (AppVariables.ExportOgg)
                {
                    var oggPath = wavPath.Replace(AppVariables.OutputDirectoryWav, AppVariables.OutputDirectoryOgg).Replace(".wav", ".ogg");
                    await ConvertWavFileToOggAsync(wavPath, oggPath);
                }
            }
        }

        // Helper used by multiple processing functions
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

        private static string GetHashFromBytes(byte[] data)
        {
            using var md5 = MD5.Create();
            var checksumBytes = md5.ComputeHash(data);
            return BitConverter.ToString(checksumBytes).Replace("-", "").ToLowerInvariant();
        }

        private string GetFileHash(string filePath)
        {
            using var md5 = MD5.Create();
            using var stream = File.OpenRead(filePath);
            var checksumBytes = md5.ComputeHash(stream);
            return BitConverter.ToString(checksumBytes).Replace("-", "").ToLowerInvariant();
        }

        // Major file processing pipeline entry used by ExportButton_Click
        private async Task ConvertInputFileAsync(string filepath)
        {
            await _fileProcessingSemaphore.WaitAsync();
            try
            {
                var data = await File.ReadAllBytesAsync(filepath);
                switch (data.DetermineFileExtension())
                {
                    case ".pck":
                        await ProcessPckAsync(data, filepath);
                        break;
                    case ".bnk":
                        await ProcessBnkAsync(data, filepath);
                        break;
                    case ".chk":
                        await ProcessCHKAsync(data, filepath);
                        break;
                    case ".wem":
                        var outPath = filepath.Replace(AppVariables.OutputDirectory, AppVariables.OutputDirectoryWem);
                        EnsureParentDirectory(outPath);
                        await ProcessWemAsync(data, outPath);
                        break;
                    default:
                        break;
                }
            }
            finally
            {
                _fileProcessingSemaphore.Release();
            }
        }

        public async Task ProcessPckAsync(byte[] data, string filepath)
        {
            var pck = new Package(data);
            var tasks = new List<Task>();

            foreach (var entry in pck.BanksTable.Files)
            {
                var bnkData = pck.GetBytes(entry);
                tasks.Add(ProcessPckBnkAsync(bnkData, entry, filepath));
            }
            foreach (var entry in pck.ExternalsTable.Files)
            {
                var extData = pck.GetBytes(entry);
                tasks.Add(ProcessPckExternalAsync(extData, entry, filepath));
            }
            foreach (var entry in pck.StreamsTable.Files)
            {
                var streamData = pck.GetBytes(entry);
                tasks.Add(ProcessPckStreamAsync(streamData, entry, filepath));
            }

            await Task.WhenAll(tasks);
        }

        public async Task ProcessPckBnkAsync(byte[] data, FileTable.FileEntry entry, string filepath)
        {
            var bnk = new Bank(data);
            bnk.Package = entry.Parent.Parent;
            bnk.Language = entry.GetLanguage();

            if (bnk.DIDXChunk is not null && bnk.DATAChunk is not null)
            {
                var tasks = new List<Task>();
                foreach (var fileEntryList in bnk.DIDXChunk.Files.Values)
                {
                    foreach (var file in fileEntryList)
                    {
                        var wemData = bnk.DATAChunk.GetFile(file);
                        tasks.Add(ProcessPckBnkWemAsync(wemData, file, bnk.Header, filepath));
                    }
                }
                await Task.WhenAll(tasks);
            }
        }

        public async Task ProcessPckBnkWemAsync(byte[] data, DataIndexChunk.FileEntry entry, BankHeader header, string filepath)
        {
            var ext = data.DetermineFileExtension();
            var wemPath = GetPckBnkWemOutputPath(entry, header, OutputFormat.Wem);
            EnsureParentDirectory(wemPath);
            if (ext == ".wem")
            {
                await ProcessWemAsync(data, wemPath);
            }
            else if (ext == ".wav")
            {
                var wavPath = GetPckBnkWemOutputPath(entry, header, OutputFormat.Wav);
                EnsureParentDirectory(wavPath);
                await File.WriteAllBytesAsync(wavPath, data);
                if (AppVariables.ExportOgg)
                {
                    var oggPath = wavPath.Replace(AppVariables.OutputDirectoryWav, AppVariables.OutputDirectoryOgg).Replace(".wav", ".ogg");
                    await ConvertWavFileToOggAsync(wavPath, oggPath);
                }
            }
            else
            {
                await File.WriteAllBytesAsync(wemPath, data);
            }
        }

        public async Task ProcessPckExternalAsync(byte[] data, FileTable.FileEntry entry, string filepath)
        {
            var ext = data.DetermineFileExtension();
            if (ext == ".wem")
            {
                var wemPath = GetPckExternalOutputPath(filepath, entry, OutputFormat.Wem);
                EnsureParentDirectory(wemPath);
                await ProcessWemAsync(data, wemPath);
                if (AppVariables.ExportWav || AppVariables.ExportOgg)
                {
                    var wavPath = wemPath.Replace(AppVariables.OutputDirectoryWem, AppVariables.OutputDirectoryWav).Replace(".wem", ".wav");
                    await ConvertWemBytesToWavFileAsync(data, wavPath);
                    if (AppVariables.ExportOgg)
                    {
                        var oggPath = wavPath.Replace(AppVariables.OutputDirectoryWav, AppVariables.OutputDirectoryOgg).Replace(".wav", ".ogg");
                        await ConvertWavFileToOggAsync(wavPath, oggPath);
                    }
                }
            }
            else if (ext == ".wav")
            {
                var wavPath = GetPckExternalOutputPath(filepath, entry, OutputFormat.Wav);
                EnsureParentDirectory(wavPath);
                await File.WriteAllBytesAsync(wavPath, data);
                if (AppVariables.ExportOgg)
                {
                    var oggPath = wavPath.Replace(AppVariables.OutputDirectoryWav, AppVariables.OutputDirectoryOgg).Replace(".wav", ".ogg");
                    await ConvertWavFileToOggAsync(wavPath, oggPath);
                }
            }
            else
            {
                var path = GetPckExternalOutputPath(filepath, entry, OutputFormat.Wem);
                EnsureParentDirectory(path);
                await File.WriteAllBytesAsync(path, data);
            }
        }

        public async Task ProcessPckStreamAsync(byte[] data, FileTable.FileEntry entry, string filepath)
        {
            var ext = data.DetermineFileExtension();
            if (ext == ".wem")
            {
                var wemPath = GetPckStreamOutputPath(entry, OutputFormat.Wem);
                EnsureParentDirectory(wemPath);
                await ProcessWemAsync(data, wemPath);
                if (AppVariables.ExportWav || AppVariables.ExportOgg)
                {
                    var wavPath = wemPath.Replace(AppVariables.OutputDirectoryWem, AppVariables.OutputDirectoryWav).Replace(".wem", ".wav");
                    await ConvertWemBytesToWavFileAsync(data, wavPath);
                    if (AppVariables.ExportOgg)
                    {
                        var oggPath = wavPath.Replace(AppVariables.OutputDirectoryWav, AppVariables.OutputDirectoryOgg).Replace(".wav", ".ogg");
                        await ConvertWavFileToOggAsync(wavPath, oggPath);
                    }
                }
            }
            else if (ext == ".wav")
            {
                var wavPath = GetPckStreamOutputPath(entry, OutputFormat.Wav);
                EnsureParentDirectory(wavPath);
                await File.WriteAllBytesAsync(wavPath, data);
                if (AppVariables.ExportOgg)
                {
                    var oggPath = wavPath.Replace(AppVariables.OutputDirectoryWav, AppVariables.OutputDirectoryOgg).Replace(".wav", ".ogg");
                    await ConvertWavFileToOggAsync(wavPath, oggPath);
                }
            }
            else
            {
                var path = GetPckStreamOutputPath(entry, OutputFormat.Wem);
                EnsureParentDirectory(path);
                await File.WriteAllBytesAsync(path, data);
            }
        }

        public async Task ProcessBnkAsync(byte[] data, string filepath)
        {
            var bnk = new Bank(data);

            if (bnk.DIDXChunk is not null && bnk.DATAChunk is not null)
            {
                var tasks = new List<Task>();
                foreach (var fileEntryList in bnk.DIDXChunk.Files.Values)
                {
                    foreach (var file in fileEntryList)
                    {
                        var wemData = bnk.DATAChunk.GetFile(file);
                        tasks.Add(ProcessPckBnkWemAsync(wemData, file, bnk.Header, filepath));
                    }
                }
                await Task.WhenAll(tasks);
            }
        }

        public async Task ProcessCHKAsync(byte[] data, string filepath)
        {
            static uint Key(uint seed)
            {
                uint temp = unchecked(((seed & 0xFF) ^ 0x9C5A0B29) * 81861667);
                temp = unchecked((temp ^ (seed >> 8) & 0xFF) * 81861667);
                temp = unchecked((temp ^ (seed >> 16) & 0xFF) * 81861667);
                temp = unchecked((temp ^ (seed >> 24) & 0xFF) * 81861667);
                return temp;
            }

            static void Decrypt(byte[] data, int offset, int count, uint seed)
            {
                uint keySeed = seed;

                int dataIndex = offset;
                int remaining = count;

                // body
                int nBlocks = remaining / 4;
                for (int i = 0; i < nBlocks; i++)
                {
                    uint keyValue = Key(keySeed);

                    uint dataValue =
                        (uint)(data[dataIndex]
                        | (data[dataIndex + 1] << 8)
                        | (data[dataIndex + 2] << 16)
                        | (data[dataIndex + 3] << 24));

                    dataValue ^= keyValue;

                    data[dataIndex] = (byte)(dataValue & 0xFF);
                    data[dataIndex + 1] = (byte)((dataValue >> 8) & 0xFF);
                    data[dataIndex + 2] = (byte)((dataValue >> 16) & 0xFF);
                    data[dataIndex + 3] = (byte)((dataValue >> 24) & 0xFF);

                    dataIndex += 4;
                    keySeed++;
                }

                // tail
                int trailing = remaining & 3;
                if (trailing > 0)
                {
                    uint keyValue = Key(keySeed);
                    for (int i = 0; i < trailing; i++)
                    {
                        data[dataIndex] ^= (byte)((keyValue >> (i * 8)) & 0xFF);
                        dataIndex++;
                    }
                }
            }

            void DecryptHeader(byte[] header, int headerSize) => Decrypt(header, 12, headerSize - 4, (uint)headerSize);
            void DecryptWem(byte[] wemData, uint wemId) => Decrypt(wemData, 0, wemData.Length, wemId);

            using var ms = new MemoryStream(data);
            using var br = new BinaryReader(ms);

            ms.Seek(4, SeekOrigin.Begin);
            int headerSize = br.ReadInt32();
            EnqueueLog($"{headerSize}");

            ms.Seek(0, SeekOrigin.Begin);
            byte[] header = br.ReadBytes(headerSize+8);
            DecryptHeader(header, headerSize);

            var decData = new List<byte>();
            decData.AddRange(header);
            decData.AddRange(data.Skip(headerSize + 8));

            var decArray = decData.ToArray();
            decArray[0] = (byte)'A';
            decArray[1] = (byte)'K';
            decArray[2] = (byte)'P';
            decArray[3] = (byte)'K';
            BitConverter.GetBytes(1).CopyTo(decArray, 8);

            var pck = new Package(data);
            var tasks = new List<Task>();

            foreach (var entry in pck.BanksTable.Files)
            {
                var bnkData = pck.GetBytes(entry);
                tasks.Add(ProcessPckBnkAsync(bnkData, entry, filepath));
            }
            foreach (var entry in pck.ExternalsTable.Files)
            {
                var extData = pck.GetBytes(entry);
                DecryptWem(extData, (uint)entry.FileId); // doesn't work
                tasks.Add(ProcessPckExternalAsync(extData, entry, filepath));
            }
            foreach (var entry in pck.StreamsTable.Files)
            {
                var streamData = pck.GetBytes(entry);
                DecryptWem(streamData, (uint)entry.FileId); // doesn't work
                tasks.Add(ProcessPckStreamAsync(streamData, entry, filepath));
            }

            await Task.WhenAll(tasks);
        }

        private async Task ProcessCHKWemAsync(byte[] data, uint wemId, string sourceFilePath)
        {
            var wemPath = Path.Combine(AppVariables.OutputDirectoryWem, "vfs", Path.GetFileNameWithoutExtension(sourceFilePath), $"{wemId}.wem");

            EnsureParentDirectory(wemPath);

            await ProcessWemAsync(data, wemPath);

            if (AppVariables.ExportWav || AppVariables.ExportOgg)
            {
                var wavPath = wemPath
                    .Replace(AppVariables.OutputDirectoryWem, AppVariables.OutputDirectoryWav)
                    .Replace(".wem", ".wav");

                await ConvertWemBytesToWavFileAsync(data, wavPath);

                if (AppVariables.ExportOgg)
                {
                    var oggPath = wavPath
                        .Replace(AppVariables.OutputDirectoryWav, AppVariables.OutputDirectoryOgg)
                        .Replace(".wav", ".ogg");

                    await ConvertWavFileToOggAsync(wavPath, oggPath);
                }
            }
        }

        #endregion

        #region Path builders & misc helpers (ported names)

        public enum OutputFormat { Wem, Wav, Ogg }

        // Thread-safe checkbox read helper
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

        public string GetPckExternalName(ulong fileId, out bool hasLanguage)
        {
            var name = fileId.ToString("x16");
            hasLanguage = KnownFilenames.TryGetValue(name, out var full);
            if (hasLanguage)
                return Path.Join(Path.GetDirectoryName(full) ?? string.Empty, Path.GetFileNameWithoutExtension(full));
            return name;
        }

        public string GetPckStreamName(ulong fileId, out bool hasLanguage)
        {
            hasLanguage = false;
            if (IsCheckedSafe(LegacyCheckBox))
                return fileId.ToString("d");
            return fileId.ToString("x8");
        }

        public string GetPckBnkWemName(ulong fileId, out bool hasLanguage)
        {
            var name = fileId.ToString("x8");
            if (IsCheckedSafe(LegacyCheckBox))
            {
                name = fileId.ToString("d");
                hasLanguage = KnownEvents.TryGetValue(name, out var full);
                if (hasLanguage)
                    return Path.Join(Path.GetDirectoryName(full) ?? string.Empty, Path.GetFileNameWithoutExtension(full));
            }
            else
            {
                hasLanguage = false;
            }
            return name;
        }

        public string GetPckExternalOutputPath(string filepath, FileTable.FileEntry entry, OutputFormat format)
        {
            var outputPath = AppVariables.OutputDirectoryWem;
            if (IsCheckedSafe(SplitOutputCheckBox))
            {
                var pckName = Path.GetFileNameWithoutExtension(filepath);
                outputPath = Path.Join(outputPath, pckName);
            }

            var name = GetPckExternalName(entry.FileId, out var hasLanguage);

            if (!hasLanguage && (IsCheckedSafe(NoLangCheckBox) != true || entry.Parent.Parent.LanguagesMap.Languages.Count > 1))
            {
                var language = entry.GetLanguage();
                outputPath = Path.Join(outputPath, language);
            }
            return Path.Join(outputPath, name + GetFileExtension(format));
        }

        public string GetPckStreamOutputPath(FileTable.FileEntry entry, OutputFormat format)
        {
            var outputPath = AppVariables.OutputDirectoryWem;
            if (IsCheckedSafe(SplitOutputCheckBox))
            {
                foreach (var filepath in AppVariables.InputFiles)
                {
                    var pckName = Path.GetFileNameWithoutExtension(filepath);
                    outputPath = Path.Join(outputPath, pckName);
                }
            }

            var name = GetPckStreamName(entry.FileId, out var hasLanguage);

            if (!hasLanguage && (IsCheckedSafe(NoLangCheckBox) != true || entry.Parent.Parent.LanguagesMap.Languages.Count > 1))
            {
                var language = entry.GetLanguage();
                outputPath = Path.Join(outputPath, language);
            }

            return Path.Join(outputPath, name + GetFileExtension(format));
        }

        public string GetPckBnkWemOutputPath(DataIndexChunk.FileEntry entry, BankHeader header, OutputFormat format)
        {
            var outputPath = AppVariables.OutputDirectoryWem;
            if (IsCheckedSafe(SplitOutputCheckBox))
            {
                foreach (var filepath in AppVariables.InputFiles)
                {
                    var pckName = Path.GetFileNameWithoutExtension(filepath);
                    outputPath = Path.Join(outputPath, pckName);
                }
            }

            if (IsCheckedSafe(BankedOutputCheckBox))
            {
                outputPath = Path.Join(outputPath, header.SoundBankId.ToString());
            }

            var name = GetPckBnkWemName(entry.Id, out var hasLanguage);

            if (!hasLanguage && (IsCheckedSafe(NoLangCheckBox) != true || header.Parent.Package.LanguagesMap.Languages.Count > 1))
            {
                var language = header.Parent.Language;
                outputPath = Path.Join(outputPath, language);
            }

            return Path.Join(outputPath, name + GetFileExtension(format));
        }

        public string GetFileExtension(OutputFormat format)
        {
            return format switch
            {
                OutputFormat.Ogg => ".ogg",
                OutputFormat.Wav => ".wav",
                OutputFormat.Wem => ".wem",
                _ => ".bin"
            };
        }

        #endregion

        #region Checksum index persistence & postprocessing

        private void LoadChecksumIndex()
        {
            _checksumIndex = new ConcurrentDictionary<string, (string Hash, string Date)>();
            string directoryName = Path.GetFileName(AppVariables.OutputDirectory);
            string processedFilesFilePath = Path.Combine("Logging\\", directoryName + "-WEM_Checksums.csv");
            if (!File.Exists(processedFilesFilePath)) return;
            foreach (var line in File.ReadLines(processedFilesFilePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split(',');
                if (parts.Length >= 3)
                {
                    var hash = parts[1];
                    var date = parts[^1];
                    _checksumIndex[parts[0]] = (hash, date);
                }
            }
        }

        private void SaveChecksumIndex()
        {
            if (_checksumIndex is null) return;
            string directoryName = Path.GetFileName(AppVariables.OutputDirectory);
            string processedFilesFilePath = Path.Combine("Logging\\", directoryName + "-WEM_Checksums.csv");
            var lines = _checksumIndex.Select(kv => kv.Key + "," + kv.Value.Hash + "," + kv.Value.Date);
            Directory.CreateDirectory("Logging\\");
            File.WriteAllLines(processedFilesFilePath, lines.OrderBy(l => l));
        }

        private void GenerateMD5Checksums()
        {
            string directoryName = Path.GetFileName(AppVariables.OutputDirectory);
            string processedFilesFilePath = Path.Combine("Logging\\", directoryName + "-WEM_Checksums.csv");

            var processedFileHashes = new ConcurrentDictionary<string, (string Hash, string Date)>();
            bool anyChange = false;

            if (!Directory.Exists("Logging\\")) Directory.CreateDirectory("Logging\\");

            if (File.Exists(processedFilesFilePath))
            {
                foreach (var line in File.ReadLines(processedFilesFilePath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var parts = line.Split(',');
                    if (parts.Length >= 3)
                    {
                        var date = parts[^1];
                        var hash = parts[1];
                        processedFileHashes[parts[0]] = (hash, date);
                    }
                }
            }

            _checksumIndex ??= new ConcurrentDictionary<string, (string, string)>();
            foreach (var kv in processedFileHashes)
            {
                _checksumIndex[kv.Key] = kv.Value;
            }

            var files = Directory.EnumerateFiles(AppVariables.OutputDirectoryWem, "*", SearchOption.AllDirectories);
            var po = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) };

            Parallel.ForEach(files, po, file =>
            {
                try
                {
                    string fileName = Path.GetFileName(file);
                    string folderPath = Path.GetDirectoryName(file) ?? string.Empty;
                    string folderName = folderPath.Length > AppVariables.OutputDirectoryWem.Length ? folderPath.Substring(AppVariables.OutputDirectoryWem.Length) : string.Empty;
                    if (folderName.StartsWith(Path.DirectorySeparatorChar.ToString()) || folderName.StartsWith(Path.AltDirectorySeparatorChar.ToString()))
                        folderName = folderName.Substring(1);
                    string concatenatedFolders = string.Join("\\", folderName.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Where(f => !string.IsNullOrEmpty(f)));
                    string outputLine = string.IsNullOrEmpty(concatenatedFolders) ? fileName : (concatenatedFolders + "\\" + fileName);

                    var fileHash = GetFileHash(file);
                    if (processedFileHashes.TryGetValue(outputLine, out var existing))
                    {
                        if (existing.Hash != fileHash)
                        {
                            processedFileHashes[outputLine] = (fileHash, DateTime.Now.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture));
                            EnqueueLog("> MD5-Checksum Changed: " + outputLine, System.Drawing.Color.Orange);
                            anyChange = true;
                        }
                        else
                        {
                            File.Delete(file);
                            EnqueueLog("> File Deleted (MD5-Checksum Matched): " + outputLine, System.Drawing.Color.Red);
                        }
                    }
                    else
                    {
                        processedFileHashes[outputLine] = (fileHash, DateTime.Now.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture));
                        EnqueueLog("> New MD5-Checksum Generated: " + outputLine, System.Drawing.Color.Green);
                        anyChange = true;
                    }
                }
                catch (Exception ex)
                {
                    EnqueueLog("Error hashing file: " + ex.Message, System.Drawing.Color.Red);
                }
            });

            if (anyChange)
            {
                var lines = processedFileHashes.Select(kv => kv.Key + "," + kv.Value.Hash + "," + kv.Value.Date);
                File.WriteAllLines(processedFilesFilePath, lines.OrderBy(l => l));
            }
        }

        #endregion

        #region Export orchestration (wired to Export button)

        private async void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            if (isBusy)
            {
                // Request abort and stop running processes
                isAborted = true;
                ExportButton.Content = "Aborting...";
                ExportButton.IsEnabled = false;
                AbortAll();
                return;
            }

            isBusy = true;
            _abortCts?.Dispose();
            _abortCts = new CancellationTokenSource();
            ExportButton.Content = "Abort";
            ParametersGroupBox.IsEnabled = false;

            TotalProgressBar.Value = 0;
            CurrentProgressBar.Value = 0;

            Directory.CreateDirectory(AppVariables.OutputDirectoryWem);

            try
            {
                int itemCount = Math.Max(1, AppVariables.InputFiles.Count);
                CurrentProgressBar.Maximum = itemCount;
                TotalProgressBar.Maximum = itemCount;

                EnqueueLog($"Exporting {AppVariables.InputFiles.Count} files", System.Drawing.Color.Purple);

                try
                {
                    LoadChecksumIndex();
                }
                catch (Exception ex)
                {
                    EnqueueLog("Failed to load checksum index: " + ex.Message, System.Drawing.Color.Red);
                    _checksumIndex = new ConcurrentDictionary<string, (string, string)>();
                }

                var queue = new ConcurrentQueue<string>(AppVariables.InputFiles);
                int completed = 0;
                int workerCount = Math.Max(1, Environment.ProcessorCount / 2);
                var workers = new List<Task>();

                for (int i = 0; i < workerCount; i++)
                {
                    workers.Add(Task.Run(async () =>
                    {
                        while (!_abortCts!.Token.IsCancellationRequested && queue.TryDequeue(out var input))
                        {
                            try
                            {
                                await ConvertInputFileAsync(input);
                                EnqueueLog($"Exported: {input}", System.Drawing.Color.Green);
                            }
                            catch (Exception ex)
                            {
                                EnqueueLog($"Error processing {input}: {ex.Message}", System.Drawing.Color.Red);
                            }
                            finally
                            {
                                var val = Interlocked.Increment(ref completed);
                                Dispatcher.Invoke(() =>
                                {
                                    CurrentProgressBar.Value = Math.Min(CurrentProgressBar.Maximum, val);
                                    TotalProgressBar.Value = Math.Min(TotalProgressBar.Maximum, val);
                                });
                            }
                        }
                    }, _abortCts.Token));
                }

                await Task.WhenAll(workers);

                if (AppVariables.ExportWem && SplitOutputCheckBox.IsChecked != true && NoLangCheckBox.IsChecked != true)
                {
                    GenerateMD5Checksums();
                }

                if (AppVariables.ExportWav || AppVariables.ExportOgg)
                {
                    Directory.CreateDirectory(AppVariables.OutputDirectoryWav);
                    if (AppVariables.ExportOgg) Directory.CreateDirectory(AppVariables.OutputDirectoryOgg);
                    GenerateMD5Checksums();
                }

                try
                {
                    SaveChecksumIndex();
                }
                catch (Exception ex)
                {
                    EnqueueLog("Failed to save checksum index: " + ex.Message, System.Drawing.Color.Red);
                }

                Cleanup();
                await ProcessOutputAsync();

                StatusTextBox.Focus();
                OnExportEnded(isAborted);
            }
            finally
            {
                // restore UI in OnExportEnded and finally guard as safe
                isBusy = false;
                isAborted = false;
                ExportButton.Content = "Export";
                ExportButton.IsEnabled = true;
                ParametersGroupBox.IsEnabled = true;
            }
        }

        private void AbortAll()
        {
            isAborted = true;
            _abortCts?.Cancel();
            foreach (var kv in _runningProcesses)
            {
                try { if (!kv.Value.HasExited) kv.Value.Kill(entireProcessTree: true); } catch { }
            }
            EnqueueLog("Abort requested.", System.Drawing.Color.Orange);
        }

        private void OnExportEnded(bool aborted)
        {
            ExportButton.IsEnabled = true;
            ExportButton.Content = "Export";
            ParametersGroupBox.IsEnabled = true;
            isAborted = false;
            isBusy = false;
        }

        private async Task ProcessOutputAsync()
        {
            await Task.Run(() => ProcessOutput());
            EnqueueLog("Processing Output Completed.", System.Drawing.Color.Green);
        }

        private void ProcessOutput()
        {
            // Capture UI state on the UI thread to avoid cross-thread access later.
            bool spreadsheetEnabled = false;
            Dispatcher.Invoke(() => { spreadsheetEnabled = SpreadsheetOutputCheckBox.IsChecked == true; });

            string directoryName = Path.GetFileName(AppVariables.OutputDirectory);
            string processedFilesFilePath = Path.Combine("Logging\\", directoryName + "-OutputFiles.txt");
            var processedDict = new ConcurrentDictionary<string, byte>();
            var newFiles = new ConcurrentBag<string>();

            if (File.Exists(processedFilesFilePath))
            {
                foreach (var line in File.ReadLines(processedFilesFilePath))
                {
                    if (!string.IsNullOrWhiteSpace(line)) processedDict.TryAdd(line, 0);
                }
            }

            var filesEnum = Directory.EnumerateFiles(AppVariables.OutputDirectory, "*", SearchOption.AllDirectories);
            Parallel.ForEach(filesEnum, file =>
            {
                try
                {
                    string fileName = Path.GetFileName(file);
                    string folderPath = Path.GetDirectoryName(file) ?? string.Empty;
                    string folderName = folderPath.Length > AppVariables.OutputDirectory.Length ? folderPath.Substring(AppVariables.OutputDirectory.Length) : string.Empty;
                    if (folderName.StartsWith(Path.DirectorySeparatorChar.ToString()) || folderName.StartsWith(Path.AltDirectorySeparatorChar.ToString()))
                        folderName = folderName.Substring(1);
                    string[] folders = folderName.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    string concatenatedFolders = string.Join("\\", folders.Where(f => !string.IsNullOrEmpty(f)).ToArray());
                    string outputLine = string.IsNullOrEmpty(concatenatedFolders) ? fileName : (concatenatedFolders + "\\" + fileName);

                    if (processedDict.TryAdd(outputLine, 0))
                    {
                        newFiles.Add(outputLine);
                        EnqueueLog("> New file detected: " + outputLine, System.Drawing.Color.Green);
                    }
                }
                catch (Exception ex)
                {
                    EnqueueLog("Error while scanning files: " + ex.Message, System.Drawing.Color.Red);
                }
            });

            if (!newFiles.IsEmpty)
            {
                var toAppend = newFiles.ToArray();
                File.AppendAllLines(processedFilesFilePath, toAppend);
                EnqueueLog($"{toAppend.Length} new audio files have been exported.", System.Drawing.Color.Purple);
            }

            // Use the captured UI state rather than reading the checkbox from the background thread.
            if (spreadsheetEnabled)
            {
                List<string> OutputFiles = new();
                foreach (string file in Directory.GetFiles(AppVariables.OutputDirectoryWem, "*", SearchOption.AllDirectories))
                {
                    if (!OutputFiles.Contains(file)) OutputFiles.Add(file);
                }

                string outputFilePath = Path.Combine("Logging\\", "SpreadsheetOutput.txt");
                List<string> outputLines = new();
                foreach (string file in OutputFiles)
                {
                    string fileName = Path.GetFileNameWithoutExtension(file);
                    string folderName = Path.GetFileName(Path.GetDirectoryName(file) ?? string.Empty);
                    string outputLine = folderName + "\t" + fileName;
                    outputLines.Add(outputLine);
                }

                File.WriteAllLines(outputFilePath, outputLines);
                Dispatcher.Invoke(() =>
                {
                    AppendStatusText("> " + "Output files saved to " + outputFilePath, System.Drawing.Color.Green);
                });
            }
        }

        private void Cleanup()
        {
            if (AppVariables.ExportWav)
            {
                foreach (string dirPath in Directory.GetDirectories(AppVariables.OutputDirectoryWem))
                {
                    Directory.Delete(dirPath, true);
                    EnqueueLog($"Deleting: {dirPath}", System.Drawing.Color.Red);
                }
            }
            if (AppVariables.ExportOgg)
            {
                foreach (string dirPath in Directory.GetDirectories(AppVariables.OutputDirectoryWem))
                {
                    Directory.Delete(dirPath, true);
                    EnqueueLog($"Deleting: {dirPath}", System.Drawing.Color.Red);
                }
                foreach (string dirPath in Directory.GetDirectories(AppVariables.OutputDirectoryWav))
                {
                    Directory.Delete(dirPath, true);
                    EnqueueLog($"Deleting: {dirPath}", System.Drawing.Color.Red);
                }
            }

            var dlg = MessageBox.Show("Delete all input files?", "Confirmation", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (dlg == MessageBoxResult.Yes)
            {
                foreach (var filePath in AppVariables.InputFiles)
                {
                    try
                    {
                        File.Delete(filePath);
                        EnqueueLog($"Deleting: {filePath}", System.Drawing.Color.Red);
                    }
                    catch (Exception ex)
                    {
                        EnqueueLog($"Failed to delete {filePath}: {ex.Message}", System.Drawing.Color.Red);
                    }
                }
            }
        }

        #endregion

        #region Utility / small stubs

        private void UpdateCanExportStatus()
        {
            bool canExport = true;
            AppVariables.ExportWem = WEMExportRadioButton.IsChecked == true;
            AppVariables.ExportWav = WAVExportRadioButton.IsChecked == true;
            AppVariables.ExportOgg = OGGExportRadioButton.IsChecked == true;

            if (!AppVariables.ExportWem && !AppVariables.ExportWav && !AppVariables.ExportOgg)
                canExport = false;

            if (AppVariables.InputFiles.Count == 0 || !Directory.Exists(OutputDirectoryTextBox.Text))
                canExport = false;

            if (!Directory.Exists(Path.Combine("Tools", "vgmstream-win")))
            {
                canExport = false;
                EnqueueLog("Please download VGMStream.", System.Drawing.Color.Red);
            }
            if (!Directory.Exists(Path.Combine("Tools", "ffmpeg-master-latest-win64-gpl-shared")))
            {
                canExport = false;
                EnqueueLog("Please download FFmpeg.", System.Drawing.Color.Red);
            }

            ExportButton.IsEnabled = canExport;

            if (canExport)
                EnqueueLog("Ready to Export", System.Drawing.Color.Green);
        }

        #endregion
    }
}