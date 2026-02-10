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
using WWiseToolsWPF.Classes.AppClasses;
using WWiseToolsWPF.Classes.BankClasses;
using WWiseToolsWPF.Classes.BankClasses.Chunks;
using WWiseToolsWPF.Classes.PackageClasses;
using static WWiseToolsWPF.Classes.AppClasses.FNVHash;

namespace WWiseToolsWPF.Views
{
    public partial class FNVHasher : UserControl
    {
        // Logging and concurrency
        private Logger _logger;

        // Data
        private HashSet<ulong> knownHashes = new HashSet<ulong>();
        private HashSet<ulong> targetHashes = new HashSet<ulong>();

        public FNVHasher()
        {
            InitializeComponent();

            _logger = new Logger(OutputTextBox);
            Unloaded += (_, _) => _logger?.Dispose();

            LoadKnownHashes(@"Libs\known_hashes.txt");
            LoadTargetHashes(@"Libs\target_hashes.txt");
        }

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
                                _logger.Enqueue("MATCH - " + hash.ToString("d") + "\t" + UserText, System.Drawing.Color.LimeGreen);
                            }
                            else
                            {
                                _logger.Enqueue("MATCH - " + hash.ToString("x8") + "\t" + UserText, System.Drawing.Color.LimeGreen);
                            }
                        }

                        if (inKnown)
                        {
                            if (LegacyCheckBox.IsChecked == true)
                            {
                                _logger.Enqueue("MATCH KNOWN - " + hash.ToString("d") + "\t" + UserText, System.Drawing.Color.DeepSkyBlue);
                            }
                            else
                            {
                                _logger.Enqueue("MATCH KNOWN - " + hash.ToString("x8") + "\t" + UserText, System.Drawing.Color.DeepSkyBlue);
                            }
                        }
                    }
                    else
                    {
                        if (LegacyCheckBox.IsChecked == true)
                        {
                            _logger.Enqueue(hash.ToString("d") + "\t" + UserText);
                        }
                        else
                        {
                            _logger.Enqueue(hash.ToString("x8") + "\t" + UserText);
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
                                _logger.Enqueue("MATCH - " + hash.ToString("d") + "\t" + UserText, System.Drawing.Color.LimeGreen);
                            }
                            else
                            {
                                _logger.Enqueue("MATCH - " + hash.ToString("x16") + "\t" + UserText, System.Drawing.Color.LimeGreen);
                            }
                        }

                        if (inKnown)
                        {
                            if (LegacyCheckBox.IsChecked == true)
                            {
                                _logger.Enqueue("MATCH KNOWN - " + hash.ToString("d") + "\t" + UserText, System.Drawing.Color.DeepSkyBlue);
                            }
                            else
                            {
                                _logger.Enqueue("MATCH KNOWN - " + hash.ToString("x16") + "\t" + UserText, System.Drawing.Color.DeepSkyBlue);
                            }
                        }
                    }
                    else
                    {
                        if (LegacyCheckBox.IsChecked == true)
                        {
                            _logger.Enqueue(hash.ToString("d") + "\t" + UserText);
                        }
                        else
                        {
                            _logger.Enqueue(hash.ToString("x16") + "\t" + UserText);
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