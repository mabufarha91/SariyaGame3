using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Kinect;
using KinectCalibrationWPF.Models;
using KinectCalibrationWPF.Services;
using System.Windows.Controls;

namespace KinectCalibrationWPF.CalibrationWizard
{
	public partial class Screen3_TouchTest : Window
	{
		private KinectManager.KinectManager kinectManager;
		private DispatcherTimer updateTimer;
		private CalibrationConfig calibration;

		public Screen3_TouchTest(KinectManager.KinectManager manager, CalibrationConfig config)
		{
			InitializeComponent();
			kinectManager = manager;
			calibration = config ?? new CalibrationConfig();
			PlaneThresholdSlider.Value = calibration.PlaneThresholdMeters;

			updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
			updateTimer.Tick += UpdateTimer_Tick;
			updateTimer.Start();
		}

		private void UpdateTimer_Tick(object sender, EventArgs e)
		{
			// Depth visualization
			var bmp = kinectManager != null ? kinectManager.GetDepthBitmap() : null;
			if (bmp != null) DepthImage.Source = bmp;
			OverlayCanvas.Children.Clear();

			// Hand to plane check
			if (kinectManager == null || !kinectManager.IsInitialized) { StatusText.Text = "Test Mode"; return; }
			CameraSpacePoint hand;
			if (kinectManager.TryGetTrackedHand(out hand))
			{
				double dist = KinectManager.KinectManager.DistancePointToPlaneMeters(hand, calibration.Plane.Nx, calibration.Plane.Ny, calibration.Plane.Nz, calibration.Plane.D);
				StatusText.Text = string.Format("Hand Z: {0:F3}m, Dist to plane: {1:F3}m", hand.Z, dist);
				if (dist < PlaneThresholdSlider.Value)
				{
					// Project hand to color then to canvas and draw red square
					var csp = kinectManager.CoordinateMapper.MapCameraPointToColorSpace(hand);
					if (!(float.IsNaN(csp.X) || float.IsNaN(csp.Y)))
					{
						var rect = new Rectangle { Width = 30, Height = 30, Stroke = Brushes.Red, StrokeThickness = 3 };
						var canvasPt = ColorToCanvas(csp);
						Canvas.SetLeft(rect, canvasPt.X - 15);
						Canvas.SetTop(rect, canvasPt.Y - 15);
						OverlayCanvas.Children.Add(rect);
					}
				}
			}
		}

		private Point ColorToCanvas(ColorSpacePoint p)
		{
			if (DepthImage.Source == null)
				return new Point(0, 0);
			double containerW = OverlayCanvas.ActualWidth > 0 ? OverlayCanvas.ActualWidth : OverlayCanvas.Width;
			double containerH = OverlayCanvas.ActualHeight > 0 ? OverlayCanvas.ActualHeight : OverlayCanvas.Height;
			if (containerW <= 0 || containerH <= 0) { containerW = 640; containerH = 480; }
			int colorW = (kinectManager != null && kinectManager.ColorWidth > 0) ? kinectManager.ColorWidth : 1920;
			int colorH = (kinectManager != null && kinectManager.ColorHeight > 0) ? kinectManager.ColorHeight : 1080;
			double sx = containerW / colorW;
			double sy = containerH / colorH;
			double scale = Math.Min(sx, sy);
			double displayedW = colorW * scale;
			double displayedH = colorH * scale;
			double offsetX = (containerW - displayedW) / 2.0;
			double offsetY = (containerH - displayedH) / 2.0;
			double x = p.X * scale + offsetX;
			double y = p.Y * scale + offsetY;
			return new Point(x, y);
		}

		private void FinishButton_Click(object sender, RoutedEventArgs e)
		{
			// Save threshold and config
			calibration.PlaneThresholdMeters = PlaneThresholdSlider.Value;
			CalibrationStorage.Save(calibration);
			MessageBox.Show("Calibration saved.", "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
			this.DialogResult = true;
		}

		private void CancelButton_Click(object sender, RoutedEventArgs e)
		{
			this.Close();
		}
	}
}
