using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Windows.Controls;
using WWiseToolsWPF.Classes.AppClasses;

namespace WWiseToolsWPF.Views
{
    public partial class Downloads : UserControl
    {
        // Networking
        private static readonly HttpClient _httpClient = new HttpClient();

        // Logging and concurrency
        private Logger _logger;

        public Downloads()
        {
            InitializeComponent();
            _logger = new Logger(StatusTextBox);
            Unloaded += (_, _) => _logger?.Dispose();
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
                _logger.Enqueue($"Starting download of {friendlyName}...");

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
                            _logger.Enqueue(
                                $"Downloading latest {friendlyName}: {percent}% completed.");
                        }
                    }
                }

                _logger.Enqueue("Download completed.", System.Drawing.Color.LimeGreen);

                await Task.Run(() =>
                    ZipFile.ExtractToDirectory(zipPath, extractPath, overwriteFiles: true));

                File.Delete(zipPath);

                _logger.Enqueue(
                    $"{friendlyName} extracted to {extractPath}",
                    System.Drawing.Color.LimeGreen);
            }
            catch (Exception ex)
            {
                _logger.Enqueue(ex.ToString(), System.Drawing.Color.Red);
            }
        }
    }
}