using System;
using System.Windows;
using System.Windows.Input;
using KinectCalibrationWPF.Services;
using KinectCalibrationWPF.Models;

namespace KinectCalibrationWPF
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private KinectManager.KinectManager kinectManager;
        
        public MainWindow()
        {
            InitializeComponent();
            // Initialize Kinect asynchronously to keep UI responsive
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var mgr = new KinectManager.KinectManager();
                    // marshal to UI
                    this.Dispatcher.Invoke(() =>
                    {
                        kinectManager = mgr;
                        UpdateKinectStatus();
                    });
                }
                catch (System.Exception ex)
                {
                    this.Dispatcher.Invoke(() =>
                    {
                        KinectStatusText.Text = "Kinect: Not Connected (Test Mode)";
                        KinectStatusText.Foreground = System.Windows.Media.Brushes.Orange;
                        System.Diagnostics.Debug.WriteLine(string.Format("Kinect init async failed: {0}", ex.Message));
                    });
                }
            });
            SetupKeyboardShortcuts();
            // Load calibration if exists
            try
            {
                var cfg = CalibrationStorage.Load();
                if (cfg != null)
                {
                    System.Diagnostics.Debug.WriteLine("Calibration loaded at startup.");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Could not load calibration at startup: {ex.Message}");
                // This is not critical - the app can run without existing calibration
            }
        }

        protected override void OnClosed(System.EventArgs e)
        {
            if (kinectManager != null)
            {
                kinectManager.Dispose();
                kinectManager = null;
            }
            base.OnClosed(e);
        }
        
        private void InitializeKinect()
        {
            try
            {
                kinectManager = new KinectManager.KinectManager();
                UpdateKinectStatus();
            }
            catch (System.Exception ex)
            {
                KinectStatusText.Text = "Kinect not available (Test Mode)";
                KinectStatusText.Foreground = System.Windows.Media.Brushes.Orange;
                System.Diagnostics.Debug.WriteLine(string.Format("Kinect initialization failed: {0}", ex.Message));
            }
        }
        
        private void UpdateKinectStatus()
        {
            if (kinectManager != null && kinectManager.IsInitialized)
            {
                KinectStatusText.Text = "Kinect: Connected and Ready";
                KinectStatusText.Foreground = System.Windows.Media.Brushes.Green;
            }
            else
            {
                KinectStatusText.Text = "Kinect: Not Connected (Test Mode)";
                KinectStatusText.Foreground = System.Windows.Media.Brushes.Orange;
            }
        }
        
        private void SetupKeyboardShortcuts()
        {
            this.KeyDown += MainWindow_KeyDown;
        }
        
        private void MainWindow_KeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.F1:
                    StartCalibrationWizard();
                    break;
                case Key.F2:
                    StartTouchTest();
                    break;
                case Key.F3:
                    ShowSettings();
                    break;
                case Key.Escape:
                    this.Close();
                    break;
            }
        }
        
        private void StartCalibrationButton_Click(object sender, RoutedEventArgs e)
        {
            StartCalibrationWizard();
        }
        
        private void TouchTestButton_Click(object sender, RoutedEventArgs e)
        {
            StartTouchTest();
        }
        
        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            ShowSettings();
        }
        
        private void StartCalibrationWizard()
        {
            try
            {
                var wizardWindow = new CalibrationWizard.CalibrationWizardWindow(kinectManager);
                wizardWindow.ShowDialog();
            }
            catch (System.Exception ex)
            {
                MessageBox.Show(string.Format("Failed to start calibration wizard: {0}", ex.Message), 
                              "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        
        private void StartTouchTest()
        {
            MessageBox.Show("Touch Test functionality will be implemented in the next phase.", 
                          "Info", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        
        private void ShowSettings()
        {
            MessageBox.Show("Settings functionality will be implemented in the next phase.", 
                          "Info", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
