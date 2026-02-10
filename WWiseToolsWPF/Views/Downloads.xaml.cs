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
using System.Windows.Media;
using System.Windows.Threading;
using WWise_Audio_Tools.Classes.AppClasses;
using WWise_Audio_Tools.Classes.BankClasses;
using WWise_Audio_Tools.Classes.BankClasses.Chunks;
using WWise_Audio_Tools.Classes.PackageClasses;

namespace WWiseToolsWPF.Views
{
    public partial class Downloads : UserControl
    {
        // Networking
        private static readonly HttpClient _httpClient = new HttpClient();

        // Logging and concurrency
        private readonly ConcurrentQueue<(string Text, System.Drawing.Color? Color)> _logQueue = new();
        private readonly DispatcherTimer _logTimer;

        public Downloads()
        {
            InitializeComponent();

            // Log flush timer (non-blocking)
            _logTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _logTimer.Tick += (_, __) => FlushLogsToUI();
            _logTimer.Start();
        }

        private async void VGMStreamDownloadButton_Click(object sender, EventArgs e)
        {
            Directory.CreateDirectory("Tools");
            string url = "https://github.com/vgmstream/vgmstream-releases/releases/download/nightly/vgmstream-win64.zip";
            string zipPath = @"Tools\vgmstream-win.zip";
            string extractPath = @"Tools\vgmstream-win";

            await DownloadAndExtractAsync(url, zipPath, extractPath, "VGMStream");
        }

        private async void FFmpegDownloadButton_Click(object sender, EventArgs e)
        {
            Directory.CreateDirectory("Tools");
            string url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl-shared.zip";
            string zipPath = @"Tools\ffmpeg-master.zip";
            string extractPath = @"Tools\";

            await DownloadAndExtractAsync(url, zipPath, extractPath, "FFmpeg");
        }

        private async Task DownloadAndExtractAsync(
            string url,
            string zipPath,
            string extractPath,
            string friendlyName)
        {
            try
            {
                EnqueueLog($"Starting download of {friendlyName}...");

                var lastPercentage = -1;

                using var response = await _httpClient.GetAsync(
                    url,
                    HttpCompletionOption.ResponseHeadersRead);

                response.EnsureSuccessStatusCode();

                var total = response.Content.Headers.ContentLength ?? -1L;
                var canReport = total > 0;

                await using var contentStream = await response.Content.ReadAsStreamAsync();
                Directory.CreateDirectory(Path.GetDirectoryName(zipPath) ?? "");

                await using (var fileStream = new FileStream(
                    zipPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    useAsync: true))
                {
                    var buffer = new byte[81920];
                    long totalRead = 0;
                    int read;

                    while ((read = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length))) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, read));
                        totalRead += read;

                        if (!canReport) continue;

                        var percent = (int)((totalRead * 100) / total);
                        if (percent == lastPercentage) continue;

                        lastPercentage = percent;
                        CurrentProgressBar.Value = percent;

                        if (percent < 100)
                        {
                            EnqueueLog(
                                $"Downloading latest {friendlyName}: {percent}% completed.");
                        }
                    }
                }

                EnqueueLog("Download completed.", System.Drawing.Color.LimeGreen);

                await Task.Run(() =>
                    ZipFile.ExtractToDirectory(zipPath, extractPath, overwriteFiles: true));

                File.Delete(zipPath);

                EnqueueLog(
                    $"{friendlyName} extracted to {extractPath}",
                    System.Drawing.Color.LimeGreen);
            }
            catch (Exception ex)
            {
                EnqueueLog(ex.ToString(), System.Drawing.Color.Red);
            }
        }

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
    }
}