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
using System.Diagnostics;
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
		private bool _usingRealMarkers = false;
		private const int QrBaseSize = 300;
		private Mat _grayMat;
		private Mat _otsuMat;
		private Mat _thresholdMat;
		private Point2f[][] _detectedCorners;
		private int[] _detectedIds;
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
			this.KeyDown += Screen2_KeyDown;
			this.Focusable = true;
		}

		private void cameraUpdateTimer_Tick(object sender, EventArgs e)
		{
			try
			{
				var src = kinectManager != null ? kinectManager.GetColorBitmap() : null;
				if (src != null)
				{
					// Show the unprocessed color feed only
					CameraFeed.Source = src;
				}
			}
			catch { }
			CameraStatusText.Text = (kinectManager != null && kinectManager.IsInitialized) ? "Camera: Connected" : "Camera: Not Available (Test Mode)";
		}

		private void ExposureSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
		{
			// Map slider to exposure time ~ +/- (10 steps) around ~0 baseline, e.g., -10..10 -> -333ms..+333ms offset from 0
			// Here we use step as 1/30s per unit as suggested
			long ticks = (long)(e.NewValue * (1.0 / 30.0) * 10000000.0);
			var exposure = TimeSpan.FromTicks(ticks);
			try { kinectManager?.SetColorCameraExposure(exposure); } catch { }
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
				projectorWindow.WindowStyle = WindowStyle.None;
				projectorWindow.WindowState = WindowState.Maximized;
			}
			projectorWindow.Show();
			this.Activate();
			// Load real markers from disk if available; otherwise let ProjectorWindow load embedded assets
			for (int i = 0; i < 4; i++)
			{
				var bmp = GenerateArucoMarkerBitmap(i, QrBaseSize);
				if (_usingRealMarkers && bmp != null)
				{
					projectorWindow.SetMarkerSource(i, bmp);
				}
			}
			if (_usingRealMarkers)
			{
				AppendStatus("Projector: Using real ArUco marker PNGs (7x7_250).");
			}
			else
			{
				AppendStatus("WARNING: Projector markers are placeholders (not real ArUco). Place 7x7_250 PNGs in bin/Debug/Markers or bin/Release/Markers.");
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
			if (CameraFeed.Visibility == Visibility.Visible)
			{
				CameraFeed.Visibility = Visibility.Collapsed;
				HideCameraButton.Content = "Show Camera View";
			}
			else
			{
				CameraFeed.Visibility = Visibility.Visible;
				HideCameraButton.Content = "Hide Camera View";
			}
		}

		private void FindMarkersButton_Click(object sender, RoutedEventArgs e)
		{
			string diagPath = @"C:\\Temp\\ArucoDebug";
			try { Directory.CreateDirectory(diagPath); } catch { }
			string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
			string logPath = System.IO.Path.Combine(diagPath, $"detection_log_{timestamp}.txt");

			LogToFile(logPath, "--- Starting Enhanced Aruco Diagnostic ---");

			// Acquire latest color frame as source mat
			var src = kinectManager != null ? kinectManager.GetColorBitmap() : null;
			if (src == null || src.PixelWidth <= 0 || src.PixelHeight <= 0)
			{
				LogToFile(logPath, "[ERROR] Camera frame not ready.");
				return;
			}

			using (var bgra = BitmapSourceConverter.ToMat(src))
			using (var color = new Mat())
			{
				if (bgra.Empty()) { LogToFile(logPath, "[ERROR] BGRA mat empty."); return; }
				Cv2.CvtColor(bgra, color, ColorConversionCodes.BGRA2BGR);
				color.SaveImage(System.IO.Path.Combine(diagPath, $"00_Original_{timestamp}.png"));

				// Build processed strategies
				var processingStrategies = new Dictionary<string, Mat>();
				using (var gray = new Mat())
				{
					Cv2.CvtColor(color, gray, ColorConversionCodes.BGR2GRAY);
					processingStrategies.Add("01_Grayscale", gray.Clone());
					using (var blurred = new Mat())
					{
						Cv2.GaussianBlur(gray, blurred, new OpenCvSharp.Size(5, 5), 0);
						processingStrategies.Add("02_Grayscale_Blurred", blurred.Clone());
					}
					using (var flipped = new Mat())
					{
						Cv2.Flip(gray, flipped, FlipMode.Y);
						processingStrategies.Add("03_Grayscale_Flipped", flipped.Clone());
					}
				}

				foreach (var kv in processingStrategies)
				{
					kv.Value.SaveImage(System.IO.Path.Combine(diagPath, $"{kv.Key}_{timestamp}.png"));
				}
				LogToFile(logPath, $"Saved {processingStrategies.Count} processed image versions.");

				bool anyFound = false;
				foreach (var kv in processingStrategies)
				{
					LogToFile(logPath, $"\n--- Testing Strategy: {kv.Key} ---");
					if (TryDetect(kv.Value, logPath, diagPath, timestamp, kv.Key, false)) anyFound = true;
					if (TryDetect(kv.Value, logPath, diagPath, timestamp, kv.Key, true)) anyFound = true;
				}

				foreach (var kv in processingStrategies) { kv.Value.Dispose(); }

				LogToFile(logPath, anyFound ? "\n--- DIAGNOSTIC COMPLETE: SUCCESS! At least one marker was found. ---" : "\n--- DIAGNOSTIC COMPLETE: FAILURE. No markers found in any configuration. ---");
				try { Process.Start("explorer.exe", diagPath); } catch { }
			}
		}

		private bool TryDetect(Mat imageToTest, string logPath, string diagPath, string timestamp, string strategyName, bool useAggressiveParams)
		{
			string paramType = useAggressiveParams ? "Aggressive" : "Default";
			LogToFile(logPath, $"--> Attempting detection with {paramType} parameters...");

			DetectorParameters parameters;
			parameters = new DetectorParameters();
			if (useAggressiveParams)
			{
				parameters.MinMarkerPerimeterRate = 0.01;
				parameters.PolygonalApproxAccuracyRate = 0.08;
				try { parameters.CornerRefinementMethod = CornerRefineMethod.Subpix; } catch { }
			}

			var dict = CvAruco.GetPredefinedDictionary(PredefinedDictionaryName.Dict7X7_250);
			Point2f[][] corners; int[] ids; Point2f[][] rejected;
			CvAruco.DetectMarkers(imageToTest, dict, out corners, out ids, parameters, out rejected);

			if (ids != null && ids.Length > 0)
			{
				LogToFile(logPath, $"    [SUCCESS] FOUND {ids.Length} MARKERS!");
				using (var resultImage = imageToTest.Channels() == 1 ? imageToTest.CvtColor(ColorConversionCodes.GRAY2BGR) : imageToTest.Clone())
				{
					CvAruco.DrawDetectedMarkers(resultImage, corners, ids, Scalar.LimeGreen);
					string successFileName = $"DETECTED_{strategyName}_{paramType}_{timestamp}.png";
					resultImage.SaveImage(System.IO.Path.Combine(diagPath, successFileName));
					LogToFile(logPath, $"    Saved successful detection image to {successFileName}");
				}
				return true;
			}
			else
			{
				LogToFile(logPath, "    [FAILURE] No markers found.");
				return false;
			}
		}

		private void LogToFile(string path, string message)
		{
			try { File.AppendAllText(path, message + Environment.NewLine); } catch { }
		}

		private void AppendStatus(string message)
		{
			try
			{
				StatusText.Text += "\n" + message;
			}
			catch { }
		}

		private void SaveDiagnostics(Mat bgr, Mat gray, Mat bw)
		{
			string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
			var destinations = new List<string>();
			try
			{
				string picsDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "KinectCalibrationDiagnostics");
				if (!Directory.Exists(picsDir)) Directory.CreateDirectory(picsDir);
				Cv2.ImWrite(System.IO.Path.Combine(picsDir, $"color_{ts}.png"), bgr);
				Cv2.ImWrite(System.IO.Path.Combine(picsDir, $"gray_{ts}.png"), gray);
				Cv2.ImWrite(System.IO.Path.Combine(picsDir, $"hsvmask_{ts}.png"), bw);
				destinations.Add(picsDir);
			}
			catch (Exception ex)
			{
				AppendStatus($"Diagnostics save to Pictures failed: {ex.Message}");
			}
			try
			{
				string appDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Diagnostics");
				if (!Directory.Exists(appDir)) Directory.CreateDirectory(appDir);
				Cv2.ImWrite(System.IO.Path.Combine(appDir, $"color_{ts}.png"), bgr);
				Cv2.ImWrite(System.IO.Path.Combine(appDir, $"gray_{ts}.png"), gray);
				Cv2.ImWrite(System.IO.Path.Combine(appDir, $"hsvmask_{ts}.png"), bw);
				destinations.Add(appDir);
			}
			catch (Exception ex)
			{
				AppendStatus($"Diagnostics save to App folder failed: {ex.Message}");
			}
			if (destinations.Count > 0)
			{
				AppendStatus("Saved diagnostics to:" + string.Join(", ", destinations));
			}
		}

		// Keyboard: select and nudge projector markers
		private int _selectedMarkerIndex = 0;
		private void Screen2_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
		{
			if (projectorWindow == null) return;
			if (e.Key == System.Windows.Input.Key.Tab)
			{
				_selectedMarkerIndex = (_selectedMarkerIndex + 1) % 4;
				projectorWindow.HighlightMarker(_selectedMarkerIndex);
				AppendStatus($"Selected marker: {_selectedMarkerIndex}");
				e.Handled = true;
				return;
			}
			double step = 5.0;
			if (e.Key == System.Windows.Input.Key.Up) { projectorWindow.HighlightMarker(_selectedMarkerIndex); projectorWindow.NudgeSelected(0, -step); e.Handled = true; }
			else if (e.Key == System.Windows.Input.Key.Down) { projectorWindow.HighlightMarker(_selectedMarkerIndex); projectorWindow.NudgeSelected(0, step); e.Handled = true; }
			else if (e.Key == System.Windows.Input.Key.Left) { projectorWindow.HighlightMarker(_selectedMarkerIndex); projectorWindow.NudgeSelected(-step, 0); e.Handled = true; }
			else if (e.Key == System.Windows.Input.Key.Right) { projectorWindow.HighlightMarker(_selectedMarkerIndex); projectorWindow.NudgeSelected(step, 0); e.Handled = true; }
		}

		private int DetectArucoOnImageWithLogging(Mat imageSingleChannel, Mat hsvMask)
		{
			AppendStatus("Pass 1: 7x7 default params on grayscale...");
			if (TryDetect(imageSingleChannel, PredefinedDictionaryName.Dict7X7_250, out var firstCorners))
			{
				AppendStatus("Pass 1 success.");
				return AddDetections(firstCorners);
			}

			AppendStatus("Pass 2: 7x7 stronger thresholds...");
			if (TryDetect(imageSingleChannel, PredefinedDictionaryName.Dict7X7_250, out var secondCorners, strong: true))
			{
				AppendStatus("Pass 2 success.");
				return AddDetections(secondCorners);
			}

			// Pass 3: inverted image
			using (var inverted = new Mat())
			{
				Cv2.BitwiseNot(imageSingleChannel, inverted);
				AppendStatus("Pass 3: 7x7 on inverted grayscale...");
				if (TryDetect(inverted, PredefinedDictionaryName.Dict7X7_250, out var invCorners, strong: true))
				{
					AppendStatus("Pass 3 success.");
					return AddDetections(invCorners);
				}
			}

			AppendStatus("Pass 4: fallback 6x6 with stronger thresholds...");
			if (TryDetect(imageSingleChannel, PredefinedDictionaryName.Dict6X6_250, out var sixCorners, strong: true))
			{
				AppendStatus("Pass 4 success.");
				return AddDetections(sixCorners);
			}

			// Pass 5: last resort on HSV mask (if any)
			if (hsvMask != null && !hsvMask.Empty())
			{
				AppendStatus("Pass 5: 7x7 on HSV mask...");
				if (TryDetect(hsvMask, PredefinedDictionaryName.Dict7X7_250, out var hsvCorners, strong: true))
				{
					AppendStatus("Pass 5 success.");
					return AddDetections(hsvCorners);
				}
			}

			AppendStatus("No markers detected in all passes.");
			return 0;
		}

		private bool TryDetect(Mat gray, PredefinedDictionaryName dictName, out Point2f[][] corners, bool strong = false)
		{
			var dict = CvAruco.GetPredefinedDictionary(dictName);
			int[] ids;
			var parameters = new DetectorParameters();
			parameters.CornerRefinementMethod = CornerRefineMethod.Subpix;
			parameters.CornerRefinementWinSize = 7;
			parameters.CornerRefinementMaxIterations = 50;
			parameters.CornerRefinementMinAccuracy = 0.01;
			parameters.AdaptiveThreshWinSizeMin = strong ? 3 : 5;
			parameters.AdaptiveThreshWinSizeMax = strong ? 35 : 25;
			parameters.AdaptiveThreshWinSizeStep = strong ? 2 : 5;
			parameters.AdaptiveThreshConstant = strong ? 5 : 7;
			parameters.MinMarkerPerimeterRate = strong ? 0.01 : 0.02;
			parameters.MaxMarkerPerimeterRate = 4.0;
			parameters.MaxErroneousBitsInBorderRate = strong ? 0.5 : 0.35;
			parameters.MinCornerDistanceRate = 0.02;
			parameters.PerspectiveRemoveIgnoredMarginPerCell = 0.13;
			parameters.MarkerBorderBits = 1;
			parameters.MinDistanceToBorder = 1;
			parameters.MinOtsuStdDev = 5.0;
			try { parameters.DetectInvertedMarker = true; } catch { }
			CvAruco.DetectMarkers(gray, dict, out corners, out ids, parameters, out _);
			return corners != null && corners.Length > 0;
		}

		private int AddDetections(Point2f[][] corners)
		{
			int found = 0;
			for (int i = 0; i < corners.Length; i++)
			{
				var quad = corners[i];
				if (quad != null && quad.Length >= 4)
				{
					AddMarkerFromQuad(quad);
					// Outline the detected marker for visual confirmation
					var pts = quad.Select(p => new System.Windows.Point(p.X, p.Y)).ToArray();
					for (int e = 0; e < 4; e++)
					{
						var a = ColorToCanvas(new System.Windows.Point(pts[e].X, pts[e].Y));
						var b = ColorToCanvas(new System.Windows.Point(pts[(e + 1) % 4].X, pts[(e + 1) % 4].Y));
						var line = new System.Windows.Shapes.Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, StrokeThickness = 2, Stroke = Brushes.Lime }; 
						MarkersOverlay.Children.Add(line);
					}
					found++;
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
			// Preconditions: we need detected corners and IDs (preferably IDs 0..3)
			if (_detectedIds == null || _detectedCorners == null || _detectedIds.Length < 4)
			{
				StatusText.Text = "Status: Need 4 detected markers before calibrating";
				StatusText.Foreground = Brushes.Orange;
				return;
			}

			// Build sorted projector centers by marker index 0..3
			var projectorPts = new Point2f[4];
			for (int i = 0; i < 4; i++)
			{
				var pc = projectorWindow != null ? projectorWindow.GetMarkerCenter(i) : new System.Windows.Point(double.NaN, double.NaN);
				if (double.IsNaN(pc.X) || double.IsNaN(pc.Y))
				{
					StatusText.Text = "Status: Projector marker centers not available";
					StatusText.Foreground = Brushes.Orange;
					return;
				}
				projectorPts[i] = new Point2f((float)pc.X, (float)pc.Y);
			}

			// Build sorted camera TL corners by marker ID 0..3
			var cameraPts = new Point2f[4];
			var cameraPtsListForSave = new List<System.Windows.Point>();
			for (int id = 0; id < 4; id++)
			{
				int idx = Array.IndexOf(_detectedIds, id);
				if (idx < 0 || _detectedCorners[idx] == null || _detectedCorners[idx].Length < 4)
				{
					StatusText.Text = $"Status: Missing detected marker with ID {id}";
					StatusText.Foreground = Brushes.Orange;
					return;
				}
				var tl = _detectedCorners[idx][0];
				cameraPts[id] = new Point2f(tl.X, tl.Y);
				cameraPtsListForSave.Add(new System.Windows.Point(tl.X, tl.Y));
			}

			// Compute perspective transform: camera -> projector
			var H = Cv2.GetPerspectiveTransform(cameraPts, projectorPts);
			double[,] H33 = new double[3,3];
			for (int r = 0; r < 3; r++)
				for (int c = 0; c < 3; c++)
					H33[r, c] = H.Get<double>(r, c);

			var cfg = CalibrationStorage.Load() ?? new CalibrationConfig();
			cfg.ProjectorMarkerPositions.Clear();
			cfg.ProjectorMarkerPositions.Add(new System.Windows.Point(markerX[0], markerY[0]));
			cfg.ProjectorMarkerPositions.Add(new System.Windows.Point(markerX[1], markerY[1]));
			cfg.ProjectorMarkerPositions.Add(new System.Windows.Point(markerX[2], markerY[2]));
			cfg.ProjectorMarkerPositions.Add(new System.Windows.Point(markerX[3], markerY[3]));
			cfg.ProjectorMarkerScale = QrSizeSlider.Value;
			cfg.DetectionExposure = 0; // exposure handled via hardware UI now
			cfg.DetectedMarkerCentersColor = new List<System.Windows.Point>(lastDetectedCentersColor);
			cfg.HsvHueMin = 0;
			cfg.HsvSatMin = 0;
			cfg.HsvValMin = 0;
			cfg.ProjectorMarkerCenters = projectorPts.Select(p => new System.Windows.Point(p.X, p.Y)).ToList();
			cfg.CameraMarkerCornersTL = cameraPtsListForSave;
			cfg.PerspectiveTransform3x3 = H33;
			cfg.SavedUtc = DateTime.UtcNow;

			try
			{
				CalibrationStorage.Save(cfg);
				MessageBox.Show("Calibration successful!");
				StatusText.Text = "Status: CALIBRATED";
				StatusText.Foreground = Brushes.Green;
				NextButton.IsEnabled = true;
				projectorWindow?.Close();
			}
			catch (Exception ex)
			{
				StatusText.Text = $"Status: Failed to save calibration ({ex.Message})";
				StatusText.Foreground = Brushes.OrangeRed;
			}
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
			try
			{
				// Try to load from disk if available: .\\Markers\\aruco_6x6_250_id{ID}.png
				string markersDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Markers");
				string file = System.IO.Path.Combine(markersDir, $"aruco_7x7_250_id{id}.png");
				if (System.IO.File.Exists(file))
				{
					var bmp = new BitmapImage();
					bmp.BeginInit();
					bmp.CacheOption = BitmapCacheOption.OnLoad;
					bmp.UriSource = new Uri(file, UriKind.Absolute);
					bmp.EndInit();
					bmp.Freeze();
					_usingRealMarkers = true;
					return bmp;
				}
			}
			catch { }
			// No fallback drawing; return null to allow embedded resources to be used
			return null;
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
