using Microsoft.Win32;
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
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WWise_Audio_Tools.Classes.AppClasses;
using WWise_Audio_Tools.Classes.BankClasses;
using WWise_Audio_Tools.Classes.BankClasses.Chunks;
using WWise_Audio_Tools.Classes.PackageClasses;
using static WWise_Audio_Tools.Classes.AppClasses.FNVHash;

namespace WWiseToolsWPF.Views
{
    public partial class FNVHasher : UserControl
    {
        // Logging and concurrency
        private readonly ConcurrentQueue<(string Text, System.Drawing.Color? Color)> _logQueue = new();
        private readonly DispatcherTimer _logTimer;

        // Data
        private HashSet<ulong> knownHashes = new HashSet<ulong>();
        private HashSet<ulong> targetHashes = new HashSet<ulong>();

        public FNVHasher()
        {
            InitializeComponent();

            // Log flush timer (non-blocking)
            _logTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _logTimer.Tick += (_, __) => FlushLogsToUI();
            _logTimer.Start();

            LoadKnownHashes(@"Libs\known_hashes.txt");
            LoadTargetHashes(@"Libs\target_hashes.txt");
        }

        #region Logging
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
                    AppendOutputText(e.Text, e.Color);
                }

                // Defer scrolling until AFTER layout/render
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    OutputTextBox.ScrollToEnd();
                }));
            }));
        }

        private void AppendOutputText(string text, System.Drawing.Color? color = null)
        {
            var paragraph = new Paragraph { Margin = new Thickness(0) };
            var run = new Run(text);
            if (color.HasValue)
            {
                run.Foreground = new SolidColorBrush(ConvertDrawingColor(color.Value));
            }
            paragraph.Inlines.Add(run);
            OutputTextBox.Document.Blocks.Add(paragraph);
        }

        private static System.Windows.Media.Color ConvertDrawingColor(System.Drawing.Color c)
        {
            return System.Windows.Media.Color.FromArgb(c.A, c.R, c.G, c.B);
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

        #region Hashing Logic

        private void OnKeyDownHandler(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                //if FNV32 is selected
                if (FNV32RadioButton.IsChecked == true)
                {
                    TextBox InputTextBox = (TextBox)sender;
                    string UserText = InputTextBox.Text;

                    uint hash = Fnv32.ComputeLowerCase(UserText);

                    var inTarget = targetHashes.Contains(hash);
                    var inKnown = knownHashes.Contains(hash);

                    if (inTarget || inKnown)
                    {
                        if (inTarget)
                        {
                            if (LegacyCheckBox.IsChecked == true)
                            {
                                EnqueueLog("MATCH - " + hash.ToString("d") + "\t" + UserText, System.Drawing.Color.LimeGreen);
                            }
                            else
                            {
                                EnqueueLog("MATCH - " + hash.ToString("x8") + "\t" + UserText, System.Drawing.Color.LimeGreen);
                            }
                        }

                        if (inKnown)
                        {
                            if (LegacyCheckBox.IsChecked == true)
                            {
                                EnqueueLog("MATCH KNOWN - " + hash.ToString("d") + "\t" + UserText, System.Drawing.Color.DeepSkyBlue);
                            }
                            else
                            {
                                EnqueueLog("MATCH KNOWN - " + hash.ToString("x8") + "\t" + UserText, System.Drawing.Color.DeepSkyBlue);
                            }
                        }
                    }
                    else
                    {
                        if (LegacyCheckBox.IsChecked == true)
                        {
                            EnqueueLog(hash.ToString("d") + "\t" + UserText);
                        }
                        else
                        {
                            EnqueueLog(hash.ToString("x8") + "\t" + UserText);
                        }
                    }
                    e.Handled = true;
                    //e.SuppressKeyPress = true;
                }

                //if FNV64 is selected
                if (FNV64RadioButton.IsChecked == true)
                {
                    TextBox InputTextBox = (TextBox)sender;
                    string UserText = InputTextBox.Text;

                    ulong hash = Fnv64.ComputeLowerCase(UserText);

                    var inTarget = targetHashes.Contains(hash);
                    var inKnown = knownHashes.Contains(hash);

                    if (inTarget || inKnown)
                    {
                        if (inTarget)
                        {
                            if (LegacyCheckBox.IsChecked == true)
                            {
                                EnqueueLog("MATCH - " + hash.ToString("d") + "\t" + UserText, System.Drawing.Color.LimeGreen);
                            }
                            else
                            {
                                EnqueueLog("MATCH - " + hash.ToString("x16") + "\t" + UserText, System.Drawing.Color.LimeGreen);
                            }
                        }

                        if (inKnown)
                        {
                            if (LegacyCheckBox.IsChecked == true)
                            {
                                EnqueueLog("MATCH KNOWN - " + hash.ToString("d") + "\t" + UserText, System.Drawing.Color.DeepSkyBlue);
                            }
                            else
                            {
                                EnqueueLog("MATCH KNOWN - " + hash.ToString("x16") + "\t" + UserText, System.Drawing.Color.DeepSkyBlue);
                            }
                        }
                    }
                    else
                    {
                        if (LegacyCheckBox.IsChecked == true)
                        {
                            EnqueueLog(hash.ToString("d") + "\t" + UserText);
                        }
                        else
                        {
                            EnqueueLog(hash.ToString("x16") + "\t" + UserText);
                        }
                    }
                    e.Handled = true;
                    //e.SuppressKeyPress = true;
                }
            }
        }
        #endregion
    }
}