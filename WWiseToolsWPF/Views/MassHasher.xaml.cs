using Microsoft.Win32;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using WWise_Audio_Tools.Classes.AppClasses;
using static WWise_Audio_Tools.Classes.AppClasses.FNVHash;

namespace WWiseToolsWPF.Views
{
    public partial class MassHasher : UserControl
    {
        // Process management
        private readonly ConcurrentDictionary<int, Process> _runningProcesses = new();
        private CancellationTokenSource? _abortCts;

        // Logging and concurrency
        private readonly ConcurrentQueue<(string Text, System.Drawing.Color? Color)> _logQueue = new();
        private readonly DispatcherTimer _logTimer;

        private HashSet<ulong> knownHashes = new HashSet<ulong>();
        private HashSet<ulong> targetHashes = new HashSet<ulong>();

        private List<string> fileContents = new List<string>();

        //bools
        private bool inputFileSelected = false;
        private bool outputDirSelected = false;


        public MassHasher()
        {
            InitializeComponent();

            // Keep the same default texts as the original
            InputTextBox.Text = "No Input File Selected.";
            OutputDirectoryTextBox.Text = Properties.Settings.Default.OutputDirectory ?? "No Output Directory Selected.";

            // Log flush timer (non-blocking)
            _logTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _logTimer.Tick += (_, __) => FlushLogsToUI();
            _logTimer.Start();

            LoadKnownHashes(@"Libs\known_hashes.txt");
            LoadTargetHashes(@"Libs\target_hashes.txt");
        }

        #region GUI Elements

        private void InputButton_Click(object sender, EventArgs e)
        {
            var ofd = new OpenFileDialog
            {
                Multiselect = false,
                Filter = "Parsed Filenames|*.txt"
            };
            bool? result = ofd.ShowDialog();
            if (result == true)
            {
                string sFileName = ofd.FileName;
                InputTextBox.Text = sFileName;

                var fileArray = File.ReadAllLines(sFileName);
                fileContents = fileArray.ToList();
                fileContents.Sort();

                inputFileSelected = true;
            }
            EnqueueLog("Successfully loaded parsed filenames.");
        }

        private void OutputDirectoryButton_Click(object sender, EventArgs e)
        {
            var fbd = new OpenFolderDialog();
            bool? res = fbd.ShowDialog();
            if (res == true)
            {
                AppVariables.OutputDirectory = fbd.FolderName;
                OutputDirectoryTextBox.Text = fbd.FolderName;

                EnqueueLog($"Output set as: {AppVariables.OutputDirectory}");

                outputDirSelected = true;
            }
        }

        private async void RunButton_Click(object sender, EventArgs e)
        {
            if (!inputFileSelected || !outputDirSelected)
            {
                EnqueueLog(
                    "Please make sure to load a valid file and set an output before running.",
                    System.Drawing.Color.Red
                );
                return;
            }

            RunButton.IsEnabled = false;

            try
            {
                await Task.Run(() => ProcessFile());
            }
            finally
            {
                RunButton.IsEnabled = true;
            }
        }

        #endregion

        #region File Loading
        private void LoadTargetHashes(string filename)
        {
            targetHashes.Clear();

            var hashes = File.ReadAllLines(filename);

            foreach (var hash in hashes)
            {
                // Might want to notify about the failed parses
                if (ulong.TryParse(hash, NumberStyles.HexNumber, null, out var value))
                    targetHashes.Add(value);
            }
        }

        private void LoadKnownHashes(string filename)
        {
            knownHashes.Clear();

            var hashes = File.ReadAllLines(filename);

            foreach (var hash in hashes)
            {
                // Might want to notify about the failed parses
                if (ulong.TryParse(hash, NumberStyles.HexNumber, null, out var value))
                    knownHashes.Add(value);
            }
        }

        #endregion

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

        #endregion

        #region Run

        private void ProcessFile()
        {
            var fileOutputList = new List<string>();

            foreach (var line in fileContents)
            {
                ulong hash = Fnv64.ComputeLowerCase(line);

                if (!knownHashes.Contains(hash))
                {
                    string matchStatus = hash.ToString("x16") + "\t" + line;

                    lock (fileOutputList)
                    {
                        fileOutputList.Add(matchStatus);
                    }
                }
                if (targetHashes.Contains(hash))
                {
                    string matchStatus = $"MATCH: {hash:x16}\t{line}";

                    EnqueueLog(matchStatus);
                }
                /*  if (!targetHashes.Contains(hash) && knownHashes.Contains(hash))
                  {
                      string matchStatus = $"MATCH KNOWN: {hash:x16}\t{line}";

                      WriteStatus(matchStatus);
                  } */
            }

            File.WriteAllLines(Path.Join(AppVariables.OutputDirectory, "GeneratedOutput.txt"), fileOutputList);
            EnqueueLog("Processing completed.");
        }

        #endregion

    }
}