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
		private bool _usingRealMarkers = false;
		private const int QrBaseSize = 300;
		private Mat _grayMat;
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
					// Show the unprocessed color feed only; exposure is controlled by hardware
					CameraFeed.Source = src;

					// Build grayscale and adaptive threshold preview
					using (var bgra = BitmapSourceConverter.ToMat(src))
					using (var bgr = new Mat())
					{
						if (!bgra.Empty())
						{
							Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);
							if (_grayMat == null) _grayMat = new Mat();
							Cv2.CvtColor(bgr, _grayMat, ColorConversionCodes.BGR2GRAY);
							if (_thresholdMat == null) _thresholdMat = new Mat();
							int blockSize = ThresholdSlider != null ? (int)ThresholdSlider.Value : 15;
							if (blockSize % 2 == 0) blockSize++;
							if (blockSize <= 1) blockSize = 3;
							Cv2.AdaptiveThreshold(_grayMat, _thresholdMat, 255, AdaptiveThresholdTypes.GaussianC, ThresholdTypes.Binary, blockSize, 2);
							var prev = BitmapSourceConverter.ToBitmapSource(_thresholdMat);
							prev.Freeze();
							FilteredPreview.Source = prev;
						}
					}
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
			MarkersOverlay.Children.Clear();
			lastDetectedCentersColor.Clear();
			try
			{
				try { System.IO.Directory.CreateDirectory(@"C:\\Temp"); }
				catch (Exception ex) { MessageBox.Show($"Could not create Temp directory at C:\\Temp. Please create it manually. Error: {ex.Message}"); return; }
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
				AppendStatus("Camera frame acquired.");

				int detected = 0;
				using (var bgra = BitmapSourceConverter.ToMat(src))
				using (var bgr = new Mat())
				{
					System.IO.File.AppendAllText(@"C:\\Temp\\KinectDebugLog.txt", "--- FindMarkers_Click Initiated ---" + Environment.NewLine);
					if (bgra.Empty()) { StatusText.Text = "Image conversion failed (empty image)."; StatusText.Foreground = Brushes.Orange; System.IO.File.AppendAllText(@"C:\\Temp\\KinectDebugLog.txt", "[ERROR] Converted BGRA mat is empty." + Environment.NewLine); return; }
					Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);
					System.IO.File.AppendAllText(@"C:\\Temp\\KinectDebugLog.txt", $"[INFO] Frame valid. Size: {bgr.Width}x{bgr.Height}, Channels: {bgr.Channels()}" + Environment.NewLine);
					try
					{
						string folder = @"C:\\Temp";
						try { if (!Directory.Exists(folder)) Directory.CreateDirectory(folder); } catch {}
						string debugImagePath = System.IO.Path.Combine(folder, "KinectDebugFrame.png");
						Cv2.ImWrite(debugImagePath, bgr);
						System.IO.File.AppendAllText(@"C:\\Temp\\KinectDebugLog.txt", $"[SUCCESS] Saved current frame for debugging at: {debugImagePath}" + Environment.NewLine);
					}
					catch (Exception ex)
					{
						System.IO.File.AppendAllText(@"C:\\Temp\\KinectDebugLog.txt", $"[ERROR] Failed to save debug image: {ex.Message}" + Environment.NewLine);
					}
					System.IO.File.AppendAllText(@"C:\\Temp\\KinectDebugLog.txt", "[INFO] Creating ArUco dictionary: Dict4X4_50" + Environment.NewLine);
					var dict = CvAruco.GetPredefinedDictionary(PredefinedDictionaryName.Dict4X4_50);
					System.IO.File.AppendAllText(@"C:\\Temp\\KinectDebugLog.txt", "[INFO] Calling CvAruco.DetectMarkers on threshold image..." + Environment.NewLine);
					Point2f[][] corners; int[] ids; var parameters = new DetectorParameters(); Point2f[][] rejected;
					CvAruco.DetectMarkers(_thresholdMat ?? bgr, dict, out corners, out ids, parameters, out rejected);
					System.IO.File.AppendAllText(@"C:\\Temp\\KinectDebugLog.txt", "[INFO] Detection call finished." + Environment.NewLine);
					_detectedCorners = corners; _detectedIds = ids;
					detected = (ids != null) ? ids.Length : 0;
					if (detected > 0)
					{
						System.IO.File.AppendAllText(@"C:\\Temp\\KinectDebugLog.txt", $"[SUCCESS] Found {ids.Length} markers!" + Environment.NewLine);
						for (int i = 0; i < ids.Length; i++)
						{
							var tl = corners[i] != null && corners[i].Length > 0 ? corners[i][0].ToString() : "<null>";
							System.IO.File.AppendAllText(@"C:\\Temp\\KinectDebugLog.txt", $"  - Marker ID: {ids[i]} found at corner[0]: {tl}" + Environment.NewLine);
						}
						using (var imageWithMarkers = bgr.Clone())
						{
							CvAruco.DrawDetectedMarkers(imageWithMarkers, corners, ids);
							var bmp = BitmapSourceConverter.ToBitmapSource(imageWithMarkers);
							bmp.Freeze();
							CameraFeed.Source = bmp;
						}
					}
					else
					{
						System.IO.File.AppendAllText(@"C:\\Temp\\KinectDebugLog.txt", "[FAILURE] No markers were found in the image." + Environment.NewLine);
					}
					System.IO.File.AppendAllText(@"C:\\Temp\\KinectDebugLog.txt", "--- FindMarkers_Click Finished ---" + Environment.NewLine);
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
				CameraFeed.Visibility = Visibility.Visible;
				HideCameraButton.Content = "Hide Camera View";
			}
			catch
			{
				StatusText.Text = "Status: Detection failed. Please adjust exposure and try again.";
				StatusText.Foreground = Brushes.OrangeRed;
			}
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
