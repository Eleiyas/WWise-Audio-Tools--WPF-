using Microsoft.Win32;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Controls;
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

        // Settings
        private string InDir = Properties.Settings.Default.InputDirectory;
        private string OutDir = Properties.Settings.Default.OutputDirectory;

        public MassHasher()
        {
            InitializeComponent();

            _logger = new Logger(StatusTextBox);
            Unloaded += (_, _) => _logger?.Dispose();

            InputTextBox.Text = Properties.Settings.Default.InputDirectory ?? "No Input File Selected.";
            OutputDirectoryTextBox.Text = Properties.Settings.Default.OutputDirectory ?? "No Output Directory Selected.";

            LoadHashes(@"Libs\known_hashes.txt", knownHashes);
            LoadHashes(@"Libs\target_hashes.txt", targetHashes);
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
                OutDir = fbd.FolderName;
                OutputDirectoryTextBox.Text = fbd.FolderName;

                _logger.Enqueue($"Output set as: {OutDir}");

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
        private void LoadHashes(string filename, HashSet<ulong> hashSet)
        {
            hashSet.Clear();

            if (!File.Exists(filename))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(filename)!);
                File.WriteAllText(filename, string.Empty);
            }

            foreach (var line in File.ReadAllLines(filename))
            {
                var hash = line.ToString().Trim();
                if (string.IsNullOrEmpty(hash))
                    continue;
                // Might want to notify about the failed parses
                if (ulong.TryParse(line, NumberStyles.HexNumber, null, out var value))
                    hashSet.Add(value);
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

            File.WriteAllLines(Path.Join(OutDir, "GeneratedOutput.txt"), fileOutputList);
            _logger.Enqueue("Processing completed.");
        }

        #endregion

    }
}