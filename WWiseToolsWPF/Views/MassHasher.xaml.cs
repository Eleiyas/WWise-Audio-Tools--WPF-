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
using WWiseToolsWPF.Classes.AppClasses;
using static WWiseToolsWPF.Classes.AppClasses.FNVHash;

namespace WWiseToolsWPF.Views
{
    public partial class MassHasher : UserControl
    {
        // Process management
        private readonly ConcurrentDictionary<int, Process> _runningProcesses = new();
        private CancellationTokenSource? _abortCts;

        // Logging and concurrency
        private Logger _logger;

        private HashSet<ulong> knownHashes = new HashSet<ulong>();
        private HashSet<ulong> targetHashes = new HashSet<ulong>();

        private List<string> fileContents = new List<string>();

        //bools
        private bool inputFileSelected = false;
        private bool outputDirSelected = false;


        public MassHasher()
        {
            InitializeComponent();

            _logger = new Logger(StatusTextBox);
            Unloaded += (_, _) => _logger?.Dispose();

            // Keep the same default texts as the original
            InputTextBox.Text = "No Input File Selected.";
            OutputDirectoryTextBox.Text = Properties.Settings.Default.OutputDirectory ?? "No Output Directory Selected.";

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
            _logger.Enqueue("Successfully loaded parsed filenames.");
        }

        private void OutputDirectoryButton_Click(object sender, EventArgs e)
        {
            var fbd = new OpenFolderDialog();
            bool? res = fbd.ShowDialog();
            if (res == true)
            {
                AppVariables.OutputDirectory = fbd.FolderName;
                OutputDirectoryTextBox.Text = fbd.FolderName;

                _logger.Enqueue($"Output set as: {AppVariables.OutputDirectory}");

                outputDirSelected = true;
            }
        }

        private async void RunButton_Click(object sender, EventArgs e)
        {
            if (!inputFileSelected || !outputDirSelected)
            {
                _logger.Enqueue(
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

                    _logger.Enqueue(matchStatus);
                }
                /*  if (!targetHashes.Contains(hash) && knownHashes.Contains(hash))
                  {
                      string matchStatus = $"MATCH KNOWN: {hash:x16}\t{line}";

                      WriteStatus(matchStatus);
                  } */
            }

            File.WriteAllLines(Path.Join(AppVariables.OutputDirectory, "GeneratedOutput.txt"), fileOutputList);
            _logger.Enqueue("Processing completed.");
        }

        #endregion

    }
}