using System.Windows;

namespace KinectCalibrationWPF
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            
            // Set up global exception handling
            this.DispatcherUnhandledException += App_DispatcherUnhandledException;
        }
        
        private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            MessageBox.Show(string.Format("An error occurred: {0}", e.Exception.Message), "Error", 
                          MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        }
    }
}
