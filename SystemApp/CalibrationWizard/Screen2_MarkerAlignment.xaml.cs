using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Kinect;
using System.Windows.Controls;
using WF = System.Windows.Forms;
using System.Windows.Media.Imaging;
using KinectCalibrationWPF.Services;
using KinectCalibrationWPF.Models;
using System.Collections.Generic;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using OpenCvSharp.Aruco;

namespace KinectCalibrationWPF.CalibrationWizard
{
	public partial class Screen2_MarkerAlignment : System.Windows.Window
	{
		private KinectManager.KinectManager kinectManager;
		private DispatcherTimer cameraUpdateTimer;
		private bool markersDetected = false;
		private ProjectorWindow projectorWindow;
		private List<System.Windows.Point> lastDetectedCentersColor = new List<System.Windows.Point>();
		private const double NudgeStep = 5.0;
		private double[] markerX = new double[4];
		private double[] markerY = new double[4];
		private BitmapSource qrBitmapSource;
		private const int QrBaseSize = 120;
		private double _currentContrast = 1.2;
		private double _currentBrightnessThreshold = 200.0;
		private int _hueMin = 0;
		private int _satMin = 0;
		private int _valMin = 200;

		public Screen2_MarkerAlignment(KinectManager.KinectManager manager)
		{
			InitializeComponent();
			kinectManager = manager;
			Loaded += Screen2_MarkerAlignment_Loaded;
			cameraUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
			cameraUpdateTimer.Tick += cameraUpdateTimer_Tick;
			cameraUpdateTimer.Start();
		}

		private void cameraUpdateTimer_Tick(object sender, EventArgs e)
		{
			try
			{
				var src = kinectManager != null ? kinectManager.GetColorBitmap() : null;
				if (src != null)
				{
					// Always show the unprocessed color feed
					CameraFeedImage.Source = src;

					// Also produce and show the real-time HSV mask in black & white
					int hueMin = HueSlider != null ? (int)HueSlider.Value : _hueMin;
					int satMin = SaturationSlider != null ? (int)SaturationSlider.Value : _satMin;
					int valMin = ValueSlider != null ? (int)ValueSlider.Value : _valMin;

					using (var bgra = BitmapSourceConverter.ToMat(src))
					using (var bgr = new Mat())
					using (var hsv = new Mat())
					using (var mask = new Mat())
					{
						if (!bgra.Empty())
						{
							Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);
							Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
							Cv2.InRange(hsv,
								new Scalar(hueMin, satMin, valMin),
								new Scalar(179, 255, 255),
								mask);
							var wbMask = mask.ToWriteableBitmap();
							wbMask.Freeze();
							FilteredCameraFeedImage.Source = wbMask;
						}
					}
				}
			}
			catch { }
			CameraStatusText.Text = (kinectManager != null && kinectManager.IsInitialized) ? "Camera: Connected" : "Camera: Not Available (Test Mode)";
		}

		private void Screen2_MarkerAlignment_Loaded(object sender, RoutedEventArgs e)
		{
			projectorWindow = new ProjectorWindow();
			var screens = WF.Screen.AllScreens;
			if (screens.Length > 1)
			{
				var second = screens[1].Bounds;
				projectorWindow.WindowStartupLocation = WindowStartupLocation.Manual;
				projectorWindow.Left = second.Left;
				projectorWindow.Top = second.Top;
				projectorWindow.Width = second.Width;
				projectorWindow.Height = second.Height;
			}
			projectorWindow.Show();
			this.Activate();
			// Generate and display 4 unique ArUco markers on projector
			for (int i = 0; i < 4; i++)
			{
				var bmp = GenerateArucoMarkerBitmap(i, QrBaseSize);
				projectorWindow.SetMarkerSource(i, bmp);
			}
			// Default positions: four corners inset by margin
			InitializeDefaultMarkerPositions();
			ApplyMarkerPositions();
			ApplyMarkerScale();
		}

		private void InitializeDefaultMarkerPositions()
		{
			double margin = 50.0;
			double w = projectorWindow != null && projectorWindow.Width > 0 ? projectorWindow.Width : 1920.0;
			double h = projectorWindow != null && projectorWindow.Height > 0 ? projectorWindow.Height : 1080.0;
			double size = QrBaseSize * (QrSizeSlider != null ? QrSizeSlider.Value : 1.0);
			// Top-left
			markerX[0] = margin; markerY[0] = margin;
			// Top-right
			markerX[1] = Math.Max(margin, w - size - margin); markerY[1] = margin;
			// Bottom-right
			markerX[2] = Math.Max(margin, w - size - margin); markerY[2] = Math.Max(margin, h - size - margin);
			// Bottom-left
			markerX[3] = margin; markerY[3] = Math.Max(margin, h - size - margin);
		}

		private void ApplyMarkerPositions()
		{
			if (projectorWindow == null) return;
			projectorWindow.SetMarkerPosition(0, markerX[0], markerY[0]);
			projectorWindow.SetMarkerPosition(1, markerX[1], markerY[1]);
			projectorWindow.SetMarkerPosition(2, markerX[2], markerY[2]);
			projectorWindow.SetMarkerPosition(3, markerX[3], markerY[3]);
			// No overlays on camera view; markers shown only on projector
		}

		private void ApplyMarkerScale()
		{
			if (projectorWindow == null) return;
			projectorWindow.SetAllMarkersScale(QrSizeSlider.Value);
			// No overlays on camera view to scale
		}

		private void MarkerPositionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { }

		private void QrSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
		{
			ApplyMarkerScale();
			// Also push immediately to projector
			if (projectorWindow != null)
			{
				projectorWindow.SetAllMarkersScale(e.NewValue);
			}
		}

		private void BrightnessThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
		{
			_currentBrightnessThreshold = e.NewValue;
		}

		private void ContrastSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
		{
			_currentContrast = e.NewValue;
		}

		private void HideCameraButton_Click(object sender, RoutedEventArgs e)
		{
			if (CameraFeedImage.Visibility == Visibility.Visible)
			{
				CameraFeedImage.Visibility = Visibility.Collapsed;
				HideCameraButton.Content = "Show Camera View";
			}
			else
			{
				CameraFeedImage.Visibility = Visibility.Visible;
				HideCameraButton.Content = "Hide Camera View";
			}
		}

		private void FindMarkersButton_Click(object sender, RoutedEventArgs e)
		{
			MarkersOverlay.Children.Clear();
			lastDetectedCentersColor.Clear();
			try
			{
				StatusText.Text = "Starting marker detection...";
				StatusText.Foreground = Brushes.SteelBlue;
				// Get a fresh color frame and process it now; do not use the displayed image
				var src = kinectManager != null ? kinectManager.GetColorBitmap() : null;
				if (src == null || src.PixelWidth <= 0 || src.PixelHeight <= 0)
				{
					StatusText.Text = "Status: Camera frame not ready, please try again";
					StatusText.Foreground = Brushes.Orange;
					return;
				}
				StatusText.Text = "Camera frame acquired.";

				int detected = 0;
				using (var bgra = BitmapSourceConverter.ToMat(src))
				using (var bgr = new Mat())
				using (var hsv = new Mat())
				using (var bw = new Mat())
				{
					if (bgra.Empty()) { StatusText.Text = "Image conversion failed (empty image)."; StatusText.Foreground = Brushes.Orange; return; }
					StatusText.Text = "Image converted for processing.";
					Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);
					Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
					int hueMin = HueSlider != null ? (int)HueSlider.Value : _hueMin;
					int satMin = SaturationSlider != null ? (int)SaturationSlider.Value : _satMin;
					int valMin = ValueSlider != null ? (int)ValueSlider.Value : _valMin;
					Cv2.InRange(hsv,
						new Scalar(hueMin, satMin, valMin),
						new Scalar(179, 255, 255),
						bw);
					StatusText.Text = "Image processed successfully.";
					detected = DetectArucoOnBinary(bw);
				}

				markersDetected = detected >= 4;
				CalibrateButton.IsEnabled = markersDetected;
				if (detected == 0)
				{
					StatusText.Text = "No markers found. Adjust HSV and try again.";
					StatusText.Foreground = Brushes.Orange;
				}
				else if (detected < 4)
				{
					StatusText.Text = $"Found {detected}/4 markers.";
					StatusText.Foreground = Brushes.Orange;
				}
				else
				{
					StatusText.Text = "Success! 4 markers found.";
					StatusText.Foreground = Brushes.Green;
				}

				// Reveal camera view again (if hidden) and show red dots only
				CameraFeedImage.Visibility = Visibility.Visible;
				HideCameraButton.Content = "Hide Camera View";
			}
			catch
			{
				StatusText.Text = "Status: Detection failed. Please adjust exposure and try again.";
				StatusText.Foreground = Brushes.OrangeRed;
			}
		}

		private int DetectArucoOnBinary(Mat bw)
		{
			var dict = CvAruco.GetPredefinedDictionary(PredefinedDictionaryName.Dict6X6_250);
			Point2f[][] corners;
			int[] ids;
			var parameters = new DetectorParameters();
			CvAruco.DetectMarkers(bw, dict, out corners, out ids, parameters, out _);
			int found = 0;
			if (corners != null)
			{
				for (int i = 0; i < corners.Length; i++)
				{
					var quad = corners[i];
					if (quad != null && quad.Length >= 4)
					{
						AddMarkerFromQuad(quad);
						found++;
					}
				}
			}
			return found;
		}

		// removed legacy QR helper

		private void AddMarkerFromQuad(Point2f[] quad)
		{
			double cx = 0, cy = 0;
			for (int i = 0; i < 4; i++) { cx += quad[i].X; cy += quad[i].Y; }
			cx /= 4.0; cy /= 4.0;
			lastDetectedCentersColor.Add(new System.Windows.Point(cx, cy));
			var canvasPt = ColorToCanvas(new System.Windows.Point(cx, cy));
			var dot = new Ellipse { Width = 18, Height = 18, Fill = Brushes.Red, Stroke = Brushes.White, StrokeThickness = 2 };
			Canvas.SetLeft(dot, canvasPt.X - 9);
			Canvas.SetTop(dot, canvasPt.Y - 9);
			MarkersOverlay.Children.Add(dot);
		}

		private System.Windows.Point ColorToCanvas(System.Windows.Point colorPoint)
		{
			double containerW = MarkersOverlay.ActualWidth;
			double containerH = MarkersOverlay.ActualHeight;
			int colorW = (kinectManager != null && kinectManager.ColorWidth > 0) ? kinectManager.ColorWidth : 1920;
			int colorH = (kinectManager != null && kinectManager.ColorHeight > 0) ? kinectManager.ColorHeight : 1080;
			if (containerW <= 0 || containerH <= 0) { containerW = 640; containerH = 480; }
			double sx = containerW / colorW;
			double sy = containerH / colorH;
			double scale = Math.Min(sx, sy);
			double displayedW = colorW * scale;
			double displayedH = colorH * scale;
			double offsetX = (containerW - displayedW) / 2.0;
			double offsetY = (containerH - displayedH) / 2.0;
			double x = colorPoint.X * scale + offsetX;
			double y = colorPoint.Y * scale + offsetY;
			return new System.Windows.Point(x, y);
		}

		private void NudgeM1_Up(object sender, RoutedEventArgs e) { markerY[0] = Math.Max(0, markerY[0] - NudgeStep); ApplyMarkerPositions(); }
		private void NudgeM1_Down(object sender, RoutedEventArgs e) { markerY[0] = Math.Min(1080, markerY[0] + NudgeStep); ApplyMarkerPositions(); }
		private void NudgeM1_Left(object sender, RoutedEventArgs e) { markerX[0] = Math.Max(0, markerX[0] - NudgeStep); ApplyMarkerPositions(); }
		private void NudgeM1_Right(object sender, RoutedEventArgs e) { markerX[0] = Math.Min(1920, markerX[0] + NudgeStep); ApplyMarkerPositions(); }
		private void NudgeM2_Up(object sender, RoutedEventArgs e) { markerY[1] = Math.Max(0, markerY[1] - NudgeStep); ApplyMarkerPositions(); }
		private void NudgeM2_Down(object sender, RoutedEventArgs e) { markerY[1] = Math.Min(1080, markerY[1] + NudgeStep); ApplyMarkerPositions(); }
		private void NudgeM2_Left(object sender, RoutedEventArgs e) { markerX[1] = Math.Max(0, markerX[1] - NudgeStep); ApplyMarkerPositions(); }
		private void NudgeM2_Right(object sender, RoutedEventArgs e) { markerX[1] = Math.Min(1920, markerX[1] + NudgeStep); ApplyMarkerPositions(); }
		private void NudgeM3_Up(object sender, RoutedEventArgs e) { markerY[2] = Math.Max(0, markerY[2] - NudgeStep); ApplyMarkerPositions(); }
		private void NudgeM3_Down(object sender, RoutedEventArgs e) { markerY[2] = Math.Min(1080, markerY[2] + NudgeStep); ApplyMarkerPositions(); }
		private void NudgeM3_Left(object sender, RoutedEventArgs e) { markerX[2] = Math.Max(0, markerX[2] - NudgeStep); ApplyMarkerPositions(); }
		private void NudgeM3_Right(object sender, RoutedEventArgs e) { markerX[2] = Math.Min(1920, markerX[2] + NudgeStep); ApplyMarkerPositions(); }
		private void NudgeM4_Up(object sender, RoutedEventArgs e) { markerY[3] = Math.Max(0, markerY[3] - NudgeStep); ApplyMarkerPositions(); }
		private void NudgeM4_Down(object sender, RoutedEventArgs e) { markerY[3] = Math.Min(1080, markerY[3] + NudgeStep); ApplyMarkerPositions(); }
		private void NudgeM4_Left(object sender, RoutedEventArgs e) { markerX[3] = Math.Max(0, markerX[3] - NudgeStep); ApplyMarkerPositions(); }
		private void NudgeM4_Right(object sender, RoutedEventArgs e) { markerX[3] = Math.Min(1920, markerX[3] + NudgeStep); ApplyMarkerPositions(); }

		private void CalibrateButton_Click(object sender, RoutedEventArgs e)
		{
			if (!markersDetected)
			{
				StatusText.Text = "Status: No QRs detected";
				StatusText.Foreground = Brushes.Orange;
				return;
			}
			StatusText.Text = "Status: CALIBRATED";
			StatusText.Foreground = Brushes.Green;
			NextButton.IsEnabled = true;

			var cfg = CalibrationStorage.Load() ?? new CalibrationConfig();
			cfg.ProjectorMarkerPositions.Clear();
			cfg.ProjectorMarkerPositions.Add(new System.Windows.Point(markerX[0], markerY[0]));
			cfg.ProjectorMarkerPositions.Add(new System.Windows.Point(markerX[1], markerY[1]));
			cfg.ProjectorMarkerPositions.Add(new System.Windows.Point(markerX[2], markerY[2]));
			cfg.ProjectorMarkerPositions.Add(new System.Windows.Point(markerX[3], markerY[3]));
			cfg.ProjectorMarkerScale = QrSizeSlider.Value;
			cfg.DetectionExposure = ValueSlider != null ? ValueSlider.Value : _currentBrightnessThreshold;
			cfg.DetectedMarkerCentersColor = new List<System.Windows.Point>(lastDetectedCentersColor);
			cfg.SavedUtc = DateTime.UtcNow;
			try { CalibrationStorage.Save(cfg); } catch { }
		}

		private void NextButton_Click(object sender, RoutedEventArgs e)
		{
			this.DialogResult = true;
			projectorWindow?.Close();
		}

		private void CancelButton_Click(object sender, RoutedEventArgs e)
		{
			projectorWindow?.Close();
			this.Close();
		}

		private BitmapSource GenerateArucoMarkerBitmap(int id, int size)
		{
			var dict = CvAruco.GetPredefinedDictionary(PredefinedDictionaryName.Dict6X6_250);
			using (var marker = CvAruco.GenerateImageMarker(dict, id, size, 1))
			using (var bgra = new Mat())
			{
				Cv2.CvtColor(marker, bgra, ColorConversionCodes.GRAY2BGRA);
				var wb = bgra.ToWriteableBitmap();
				wb.Freeze();
				return wb;
			}
		}

		private void HueSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
		{
			_hueMin = (int)e.NewValue;
		}

		private void SaturationSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
		{
			_satMin = (int)e.NewValue;
		}

		private void ValueSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
		{
			_valMin = (int)e.NewValue;
		}
	}
}
