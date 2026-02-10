using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace WWiseToolsWPF.Classes.AppClasses
{
    public class Logger : IDisposable
    {
        private readonly ConcurrentQueue<(string Text, System.Drawing.Color? Color)> _logQueue = new();
        private readonly DispatcherTimer _logTimer;
        private readonly RichTextBox _output;

        public Logger(RichTextBox output)
        {
            _output = output;

            _logTimer = new DispatcherTimer(
                TimeSpan.FromMilliseconds(50),
                DispatcherPriority.Background,
                (_, _) => Flush(),
                output.Dispatcher
            );

            _logTimer.Start();
        }

        public void Enqueue(string text, System.Drawing.Color? color = null)
        {
            _logQueue.Enqueue((text, color));
        }

        private void Flush()
        {
            if (_logQueue.IsEmpty) return;

            var entries = new List<(string Text, System.Drawing.Color? Color)>();
            while (_logQueue.TryDequeue(out var e))
                entries.Add(e);

            foreach (var e in entries)
                Append(e.Text, e.Color);

            _output.ScrollToEnd();
        }

        private void Append(string text, System.Drawing.Color? color)
        {
            var paragraph = new Paragraph { Margin = new Thickness(0) };
            var run = new Run(text);

            if (color.HasValue)
                run.Foreground = new SolidColorBrush(Convert(color.Value));

            paragraph.Inlines.Add(run);
            _output.Document.Blocks.Add(paragraph);
        }

        private static Color Convert(System.Drawing.Color c)
            => Color.FromArgb(c.A, c.R, c.G, c.B);

        public void Dispose()
        {
            _logTimer.Stop();
        }
    }

}
