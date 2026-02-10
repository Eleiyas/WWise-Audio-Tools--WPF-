using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace WWiseToolsWPF
{
    public partial class MainWindow : Window
    {
        private const double ExpandedWidth = 200;
        private const double CollapsedWidth = 56;
        private readonly TimeSpan AnimationDuration = TimeSpan.FromMilliseconds(220);

        public MainWindow()
        {
            InitializeComponent();

            // Ensure menu is left-anchored so it "sticks" to the left margin
            MenuBorder.HorizontalAlignment = HorizontalAlignment.Left;
            MenuBorder.Margin = new Thickness(0);

            // Show init screen on startup (make sure Views.InitScreen exists)
            MainContent.Content = new Views.InitScreen();

            // Optionally start expanded
            MenuBorder.Width = ExpandedWidth;
            HamburgerButton.IsChecked = true;
        }

        private void AnimateMenu(double toWidth)
        {
            var anim = new DoubleAnimation(toWidth, new Duration(AnimationDuration))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            MenuBorder.BeginAnimation(WidthProperty, anim);
        }

        private void HamburgerButton_Checked(object sender, RoutedEventArgs e)
        {
            AnimateMenu(ExpandedWidth);
        }

        private void HamburgerButton_Unchecked(object sender, RoutedEventArgs e)
        {
            AnimateMenu(CollapsedWidth);
        }

        private void AudioExtractorButton_Click(object sender, RoutedEventArgs e)
        {
            MainContent.Content = new Views.AudioExtractor();
        }

        private void FNVHasherButton_Click(object sender, RoutedEventArgs e)
        {
            MainContent.Content = new Views.FNVHasher();
        }

        private void CollatorButton_Click(object sender, RoutedEventArgs e)
        {
            //MainContent.Content = new Views.Collator();
        }

        private void MassHasherButton_Click(object sender, RoutedEventArgs e)
        {
            //MainContent.Content = new Views.MassHasher();
        }

        private void DownloadsButton_Click(object sender, RoutedEventArgs e)
        {
            MainContent.Content = new Views.Downloads();
        }

        private void HelpButton_Click(object sender, RoutedEventArgs e)
        {
            //MainContent.Content = new Views.Help();
        }

        private void CreditsButton_Click(object sender, RoutedEventArgs e)
        {
            //MainContent.Content = new Views.Credits();
        }

        // Add other navigation handlers...
    }
}