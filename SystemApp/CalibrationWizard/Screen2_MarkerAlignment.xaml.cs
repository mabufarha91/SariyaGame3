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
using System.Runtime.InteropServices;
using System.Threading.Tasks;

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
			// Quick check: ensure OpenCV native runtime is present; if not, warn so on-the-fly drawing won't work
			VerifyOpenCvRuntimePresent();
			// Load real markers from disk if available; otherwise generate on-the-fly; otherwise ProjectorWindow loads embedded assets
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
				AppendStatus("Projector: Using real ArUco markers (disk or generated).");
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

		private async void FindMarkersButton_Click(object sender, RoutedEventArgs e)
		{
			bool verbose = (EnableVerboseLogging != null && EnableVerboseLogging.IsChecked == true);
			string baseDiag = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "KinectCalibrationDiagnostics");
			string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
			string diagDir = System.IO.Path.Combine(baseDiag, ts);
			string logPath = System.IO.Path.Combine(diagDir, "detection_log.txt");
			if (verbose) { try { Directory.CreateDirectory(diagDir); } catch { } }
			
			if (kinectManager == null)
			{
				AppendStatus("Camera not initialized.");
				if (verbose) LogToFile(logPath, "ERROR: Kinect manager null.");
				return;
			}
			
			byte[] bgra;
			int w, h, stride;
			if (!kinectManager.TryGetColorFrameRaw(out bgra, out w, out h, out stride) || bgra == null)
			{
				AppendStatus("No color frame available.");
				if (verbose) LogToFile(logPath, "ERROR: No color frame available.");
				return;
			}
			
			FindMarkersButton.IsEnabled = false;
			
			// CRITICAL: Hide camera feed during detection (like reference video 0:44-1:55)
			// This prevents detecting ArUco markers from the camera display itself
			bool cameraWasVisible = CameraFeed.Visibility == Visibility.Visible;
			if (cameraWasVisible)
			{
				CameraFeed.Visibility = Visibility.Collapsed;
				if (verbose) LogToFile(logPath, "✅ Camera feed hidden to prevent detection interference (ref video method)");
			}
			
			StatusText.Text = "Status: Professional ArUco Detection (Camera Hidden)...";
			
			try
			{
				// Run detection on background thread to prevent UI freezing
				var result = await Task.Run(() =>
				{
					var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
					try
					{
						using (var matBGRA = Mat.FromPixelData(h, w, MatType.CV_8UC4, handle.AddrOfPinnedObject(), stride))
						using (var matBGR = new Mat())
						{
							Cv2.CvtColor(matBGRA, matBGR, ColorConversionCodes.BGRA2BGR);
							if (verbose)
							{
								try { Cv2.ImWrite(System.IO.Path.Combine(diagDir, "00_bgr.png"), matBGR); } catch { }
								try { LogToFile(logPath, $"SRC: {w}x{h}, stride={stride}"); } catch { }
								try { LogToFile(logPath, "STRATEGY: Fast ArUco detection (non-blocking)"); } catch { }
							}

							// COMPREHENSIVE DIAGNOSTICS FIRST
							if (verbose)
							{
								try { LogToFile(logPath, "Running comprehensive diagnostics..."); } catch { }
								
								// 1. OpenCvSharp4 functionality diagnostics
								bool opencvOk = RunOpenCvSharp4Diagnostics(diagDir, logPath);
								if (!opencvOk)
								{
									LogToFile(logPath, "⚠️  OpenCV issues detected - may affect detection");
								}
								
								// 2. Projector window diagnostics - RUN ON MAIN THREAD
								try
								{
									var projectorWindow = GetProjectorWindow();
									if (projectorWindow != null)
									{
										// Schedule projector diagnostics on UI thread to avoid threading error
										System.Windows.Application.Current.Dispatcher.Invoke(() => 
										{
											try
											{
												projectorWindow.SaveProjectorDiagnostics(diagDir);
											}
											catch (Exception projEx)
											{
												LogToFile(logPath, $"⚠️  Projector diagnostics error: {projEx.Message}");
											}
										});
										LogToFile(logPath, "✅ Projector window diagnostics completed");
									}
									else
									{
										LogToFile(logPath, "⚠️  Projector window not available for diagnostics");
									}
								}
								catch (Exception ex)
								{
									LogToFile(logPath, $"⚠️  Projector diagnostics setup error: {ex.Message}");
								}
							}

							// ArUco detection with full diagnostics
							Point2f[][] corners; int[] ids;
							if (TryFastArucoDetection(matBGR, w, h, out corners, out ids, verbose ? logPath : null, verbose ? diagDir : null))
							{
								return new { Success = true, Corners = corners, Ids = ids };
							}
							else
							{
								if (verbose) LogToFile(logPath, "RESULT: Fast ArUco detection failed after full diagnostics.");
								return new { Success = false, Corners = (Point2f[][])null, Ids = (int[])null };
							}
						}
					}
					finally
					{
						if (handle.IsAllocated) handle.Free();
					}
				});

				// Update UI on main thread
				if (result.Success)
				{
					_detectedCorners = result.Corners;
					_detectedIds = result.Ids;
					StatusText.Text = $"Status: Found {result.Ids.Length} ArUco marker(s)";
					StatusText.Foreground = Brushes.Green;
					CalibrateButton.IsEnabled = HasAllMarkerIds(result.Ids);
					NextButton.IsEnabled = HasAllMarkerIds(result.Ids);
					MarkersOverlay.Children.Clear();
					lastDetectedCentersColor.Clear();
					
					// Enhanced coordinate logging for detected markers
					if (verbose)
					{
						LogToFile(logPath, "\n=== COORDINATE TRANSFORMATION ANALYSIS ===");
						LogToFile(logPath, $"Container (MarkersOverlay): {MarkersOverlay.ActualWidth:F1}x{MarkersOverlay.ActualHeight:F1}");
						LogToFile(logPath, $"Camera resolution: 1920x1080");
						LogToFile(logPath, $"Detected {result.Ids.Length} markers with IDs: [{string.Join(",", result.Ids)}]");
						
						LogToFile(logPath, "\n=== EXPECTED PROJECTOR MARKER POSITIONS ===");
						LogToFile(logPath, "Projector markers should be at these CAMERA coordinates:");
						LogToFile(logPath, "  - Marker 0: Should be around camera coordinates corresponding to projector position (50, 50)");
						LogToFile(logPath, "  - Marker 1: Should be around camera coordinates corresponding to projector position (1331, 50)");
						LogToFile(logPath, "  - Marker 2: Should be around camera coordinates corresponding to projector position (1331, 669)");
						LogToFile(logPath, "  - Marker 3: Should be around camera coordinates corresponding to projector position (40, 669)");
					}
					
					// Process each detected marker with enhanced coordinate logging
					for (int i = 0; i < result.Corners.Length; i++)
					{
						int markerId = i < result.Ids.Length ? result.Ids[i] : -1;
						AddMarkerFromQuad(result.Corners[i], markerId, verbose ? logPath : null);
					}
					
					if (verbose)
					{
						LogToFile(logPath, "\n⚠️  NOTE: If red dots are misplaced, the camera-to-projector coordinate mapping needs calibration!");
					}
				}
				else
				{
					StatusText.Text = "Status: No ArUco markers detected. Check marker display.";
					StatusText.Foreground = Brushes.OrangeRed;
				}
			}
			catch (Exception ex)
			{
				StatusText.Text = $"Status: Detection error - {ex.Message}";
				StatusText.Foreground = Brushes.Red;
				if (verbose) LogToFile(logPath, $"ERROR: {ex.Message}");
			}
			finally
			{
				FindMarkersButton.IsEnabled = true;
				
				// Restore camera feed visibility if it was visible before
				if (cameraWasVisible)
				{
					CameraFeed.Visibility = Visibility.Visible;
					if (verbose) LogToFile(logPath, "✅ Camera feed restored after detection");
				}
			}
		}

		private Dictionary<string, Mat> BuildProcessingStrategies(Mat bgr)
		{
			var dict = new Dictionary<string, Mat>();
			// Grayscale
			var gray = new Mat();
			Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
			dict["Grayscale"] = gray;
			// Enhanced Contrast (alpha=1.5, beta=20)
			var enhanced = new Mat();
			gray.ConvertTo(enhanced, MatType.CV_8UC1, 1.5, 20);
			dict["Enhanced Contrast"] = enhanced;
			// CLAHE
			var claheMat = new Mat();
			using (var clahe = Cv2.CreateCLAHE(2.0, new OpenCvSharp.Size(8, 8)))
			{
				clahe.Apply(gray, claheMat);
			}
			dict["CLAHE"] = claheMat;
			// Additional strategies for projector glare/noise
			var blur3 = new Mat();
			Cv2.GaussianBlur(gray, blur3, new OpenCvSharp.Size(3, 3), 0);
			dict["Gray_Gauss3"] = blur3;
			var median3 = new Mat();
			Cv2.MedianBlur(gray, median3, 3);
			dict["Gray_Median3"] = median3;
			var bilateral = new Mat();
			Cv2.BilateralFilter(gray, bilateral, 7, 60, 7);
			dict["Gray_Bilateral7"] = bilateral;
			var unsharp = new Mat();
			using (var g5 = new Mat())
			{
				Cv2.GaussianBlur(gray, g5, new OpenCvSharp.Size(5, 5), 0);
				Cv2.AddWeighted(gray, 1.5, g5, -0.5, 0, unsharp);
			}
			dict["Gray_Unsharp"] = unsharp;
			// Gamma correction variants
			dict["Gray_Gamma0_7"] = ApplyGamma(gray, 0.7);
			dict["Gray_Gamma1_3"] = ApplyGamma(gray, 1.3);
			// Adaptive thresholds
			var atMean = new Mat();
			Cv2.AdaptiveThreshold(gray, atMean, 255, AdaptiveThresholdTypes.MeanC, ThresholdTypes.Binary, 15, 7);
			dict["AT_Mean_15_7"] = atMean;
			var atGauss = new Mat();
			Cv2.AdaptiveThreshold(gray, atGauss, 255, AdaptiveThresholdTypes.GaussianC, ThresholdTypes.Binary, 25, 5);
			dict["AT_Gauss_25_5"] = atGauss;
			// Inverted variants
			var invGray = new Mat();
			Cv2.BitwiseNot(gray, invGray);
			dict["Gray_Inverted"] = invGray;
			var invClahe = new Mat();
			Cv2.BitwiseNot(claheMat, invClahe);
			dict["CLAHE_Inverted"] = invClahe;
			// Binary threshold sweeps on CLAHE + morphology open/close
			int[] thresholds = new int[] { 60, 100, 140, 180, 220 };
			foreach (var t in thresholds)
			{
				var th = new Mat();
				Cv2.Threshold(claheMat, th, t, 255, ThresholdTypes.Binary);
				dict[$"CLAHE_Thresh_{t}"] = th;
				var kernel3 = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(3, 3));
				var opened = new Mat();
				Cv2.MorphologyEx(th, opened, MorphTypes.Open, kernel3);
				dict[$"CLAHE_Thresh_{t}_Open3"] = opened;
				var closed = new Mat();
				Cv2.MorphologyEx(th, closed, MorphTypes.Close, kernel3);
				dict[$"CLAHE_Thresh_{t}_Close3"] = closed;
			}
			// Aggressive CLAHE + Gamma to expand contrast for projected markers
			var clahe2 = new Mat();
			using (var c2 = Cv2.CreateCLAHE(4.0, new OpenCvSharp.Size(8, 8))) { c2.Apply(gray, clahe2); }
			var clahe2Gamma = ApplyGamma(clahe2, 0.6);
			dict["CLAHE_Aggressive"] = clahe2Gamma;
			// Otsu on CLAHE
			var otsu = new Mat();
			Cv2.Threshold(claheMat, otsu, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
			dict["CLAHE_Otsu"] = otsu;
			// Multi-scale variants (down/upscale) on key bases
			double[] scales = new double[] { 0.5, 0.75, 1.25 };
			foreach (var s in scales)
			{
				if (Math.Abs(s - 1.0) < 1e-6) continue;
				var sz = new OpenCvSharp.Size((int)Math.Round(gray.Cols * s), (int)Math.Round(gray.Rows * s));
				if (sz.Width < 64 || sz.Height < 64) continue;
				var rGray = new Mat(); Cv2.Resize(gray, rGray, sz, 0, 0, InterpolationFlags.Area);
				dict[$"Gray_Scale_{s:0.00}"] = rGray;
				var rClahe = new Mat(); Cv2.Resize(claheMat, rClahe, sz, 0, 0, InterpolationFlags.Area);
				dict[$"CLAHE_Scale_{s:0.00}"] = rClahe;
				var rOtsu = new Mat(); Cv2.Resize(otsu, rOtsu, sz, 0, 0, InterpolationFlags.Nearest);
				dict[$"Otsu_Scale_{s:0.00}"] = rOtsu;
			}
			return dict;
		}

		private List<DetectorParameters> BuildDetectorParameterSets()
		{
			var list = new List<DetectorParameters>();
			// Default
			var def = new DetectorParameters();
			try { def.CornerRefinementMethod = CornerRefineMethod.Subpix; } catch { }
			try { def.DetectInvertedMarker = true; } catch { }
			list.Add(def);
			// Aggressive
			var ag = new DetectorParameters();
			ag.AdaptiveThreshWinSizeMin = 3;
			ag.AdaptiveThreshWinSizeMax = 35;
			ag.AdaptiveThreshWinSizeStep = 2;
			ag.MinMarkerPerimeterRate = 0.01;
			ag.MaxErroneousBitsInBorderRate = 0.8f;
			try { ag.CornerRefinementMethod = CornerRefineMethod.Subpix; } catch { }
			try { ag.DetectInvertedMarker = true; } catch { }
			list.Add(ag);
			// Very aggressive
			var vag = new DetectorParameters();
			vag.AdaptiveThreshWinSizeMin = 3;
			vag.AdaptiveThreshWinSizeMax = 51;
			vag.AdaptiveThreshWinSizeStep = 2;
			vag.AdaptiveThreshConstant = 1;
			vag.MinMarkerPerimeterRate = 0.005;
			vag.MaxMarkerPerimeterRate = 6.0;
			vag.MaxErroneousBitsInBorderRate = 0.95f;
			vag.MinCornerDistanceRate = 0.01;
			vag.MinDistanceToBorder = 0;
			vag.MarkerBorderBits = 1;
			try { vag.CornerRefinementMethod = CornerRefineMethod.Subpix; } catch { }
			try { vag.DetectInvertedMarker = true; } catch { }
			list.Add(vag);
			return list;
		}

		private List<PredefinedDictionaryName> BuildDictionaries()
		{
			// Try both 7x7 and 6x6 first (most likely), then smaller fallbacks
			return new List<PredefinedDictionaryName>
			{
				PredefinedDictionaryName.Dict7X7_250,
				PredefinedDictionaryName.Dict6X6_250,
				PredefinedDictionaryName.Dict4X4_50,
				PredefinedDictionaryName.Dict5X5_100
			};
		}

		private Mat ApplyGamma(Mat srcGray, double gamma)
		{
			var lut = new Mat(1, 256, MatType.CV_8UC1);
			var idx = lut.GetGenericIndexer<byte>();
			for (int i = 0; i < 256; i++)
			{
				double normalized = i / 255.0;
				double corrected = Math.Pow(normalized, gamma);
				int val = (int)Math.Round(corrected * 255.0);
				if (val < 0) val = 0; else if (val > 255) val = 255;
				idx[0, i] = (byte)val;
			}
			var dst = new Mat();
			Cv2.LUT(srcGray, lut, dst);
			lut.Dispose();
			return dst;
		}

		private DetectorParameters CreateRelaxedDetectorParameters()
		{
			var p = new DetectorParameters();
			// Very relaxed parameters specifically for projector-displayed markers
			p.AdaptiveThreshWinSizeMin = 3;
			p.AdaptiveThreshWinSizeMax = 51; // Increased from 35
			p.AdaptiveThreshWinSizeStep = 2;
			p.AdaptiveThreshConstant = 5; // Reduced from 7 for better contrast adaptation
			p.MinMarkerPerimeterRate = 0.005; // Much lower minimum (was 0.01)
			p.MaxMarkerPerimeterRate = 8.0; // Increased maximum (was 4.0)
			p.MaxErroneousBitsInBorderRate = 0.8f; // More permissive (was 0.5)
			p.MinCornerDistanceRate = 0.01; // Reduced from 0.02
			p.MinDistanceToBorder = 0; // Allow markers at the very edge (was 1)
			p.MarkerBorderBits = 1;
			p.MinOtsuStdDev = 2.0; // Reduced from 5.0 for lower contrast tolerance
			try { p.CornerRefinementMethod = CornerRefineMethod.Subpix; } catch { }
			try { p.DetectInvertedMarker = true; } catch { }
			return p;
		}

		private bool TryMinimalDictionarySweep(Mat bgr, int w, int h, out Point2f[][] outCorners, out int[] outIds, out string outDict, string logPath, string saveDir)
		{
			outCorners = null; outIds = null; outDict = string.Empty;
			using (var gray = new Mat())
			{
				Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
				var dicts = new[]
				{
					PredefinedDictionaryName.Dict7X7_250,
					PredefinedDictionaryName.Dict6X6_250,
					PredefinedDictionaryName.Dict5X5_100,
					PredefinedDictionaryName.Dict4X4_50
				};
				var p = CreateRelaxedDetectorParameters();
				foreach (var d in dicts)
				{
					try
					{
						Point2f[][] corners; int[] ids; Point2f[][] rejected;
						var dict = CvAruco.GetPredefinedDictionary(d);
						CvAruco.DetectMarkers(gray, dict, out corners, out ids, p, out rejected);
						if (logPath != null)
						{
							LogToFile(logPath, $"Minimal sweep: Dict={d}, Found={(ids?.Length ?? 0)}, Rejected={(rejected?.Length ?? 0)}");
							// Log details of rejected markers if verbose
							if (rejected != null && rejected.Length > 0)
							{
								LogToFile(logPath, $"  First few rejected marker details:");
								for (int ri = 0; ri < Math.Min(5, rejected.Length); ri++)
								{
									var r = rejected[ri];
									if (r != null && r.Length >= 4)
									{
										var minX = r.Min(pt => pt.X);
										var maxX = r.Max(pt => pt.X);
										var minY = r.Min(pt => pt.Y);
										var maxY = r.Max(pt => pt.Y);
										var area = (maxX - minX) * (maxY - minY);
										LogToFile(logPath, $"    Rejected {ri}: bounds=({minX:F1},{minY:F1})-({maxX:F1},{maxY:F1}), area={area:F0}");
									}
								}
							}
						}
						if (ids != null && ids.Length > 0)
						{
							if (logPath != null)
							{
								LogToFile(logPath, $"  Before filtering: Found {ids.Length} markers with IDs: [{string.Join(",", ids)}]");
							}
							
							// BYPASS FILTERING IN MINIMAL SWEEP - Accept ANY markers found by ArUco
							outCorners = corners; outIds = ids; outDict = d.ToString();
							if (logPath != null)
							{
								LogToFile(logPath, $"  BYPASSED filtering - accepting all {ids.Length} markers");
							}
							
							if (saveDir != null)
							{
								try
								{
									using (var vis = bgr.Clone())
									{
										CvAruco.DrawDetectedMarkers(vis, outCorners, outIds, Scalar.LimeGreen);
										Cv2.ImWrite(System.IO.Path.Combine(saveDir, $"detected_minimal_{outDict}.png"), vis);
									}
								}
								catch { }
							}
							return true;
						}
					}
					catch { }
				}
			}
			return false;
		}

		private bool TryFastArucoDetection(Mat bgr, int width, int height, out Point2f[][] outCorners, out int[] outIds, string logPath, string saveDir)
		{
			outCorners = null;
			outIds = null;
			
			try
			{
				// PROJECTED MARKER OPTIMIZED PARAMETERS - Very permissive for low contrast projected markers
				var params1 = new DetectorParameters();
				
				// CRITICAL: Adaptive thresholding for projected markers (low contrast)
				params1.AdaptiveThreshWinSizeMin = 3;
				params1.AdaptiveThreshWinSizeMax = 51;  // Larger windows for projected markers
				params1.AdaptiveThreshWinSizeStep = 2;
				params1.AdaptiveThreshConstant = 3;     // Lower threshold constant for low contrast
				
				// CRITICAL: Very permissive size constraints for projected markers
				params1.MinMarkerPerimeterRate = 0.005;  // Much lower minimum
				params1.MaxMarkerPerimeterRate = 8.0;    // Higher maximum  
				params1.MinCornerDistanceRate = 0.005;   // Very low corner distance
				params1.MinDistanceToBorder = 0;         // Allow edge markers
				
				// CRITICAL: ULTRA-PERMISSIVE for projected markers (perspective/lighting issues)
				params1.MaxErroneousBitsInBorderRate = 0.95f; // 95% error tolerance!  
				params1.MinOtsuStdDev = 0.1;                  // Almost no contrast requirement
				
				// CRITICAL: Enhanced corner detection for projected markers
				try { params1.CornerRefinementMethod = CornerRefineMethod.Subpix; } catch { }
				try { params1.DetectInvertedMarker = true; } catch { }  // Check both orientations
				
				if (logPath != null) LogToFile(logPath, "USING PROJECTED MARKER OPTIMIZED PARAMETERS");
				
				// CRITICAL: Back to comprehensive dictionary testing - find what works!
				var dicts = new[] {
					PredefinedDictionaryName.Dict7X7_250,  // What projector actually shows (PRIORITY!)
					PredefinedDictionaryName.Dict6X6_250,  // Backup for projected markers
					PredefinedDictionaryName.Dict4X4_50,   // Environmental markers - detect but filter spatially
					PredefinedDictionaryName.Dict5X5_50    // Environmental markers - detect but filter spatially
				};
				
				using (var gray = new Mat())
				{
					Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
					
					// CRITICAL: Image enhancement for projected markers
					using (var enhanced = new Mat())
					{
						// Apply CLAHE (Contrast Limited Adaptive Histogram Equalization) for projected markers
						using (var clahe = Cv2.CreateCLAHE(3.0, new OpenCvSharp.Size(8, 8)))
						{
							clahe.Apply(gray, enhanced);
						}
						
						if (saveDir != null)
						{
							try { Cv2.ImWrite(System.IO.Path.Combine(saveDir, "01_original_gray.png"), gray); } catch { }
							try { Cv2.ImWrite(System.IO.Path.Combine(saveDir, "02_enhanced_clahe.png"), enhanced); } catch { }
						}
						
						// Try detection on both original and enhanced images
						var images = new[] { 
							("Original", gray),
							("CLAHE_Enhanced", enhanced)
						};
						
						foreach (var (imageName, testImage) in images)
						{
							if (logPath != null) LogToFile(logPath, $"Testing image: {imageName}");
							
							foreach (var dictName in dicts)
							{
								if (logPath != null) LogToFile(logPath, $"  Testing dictionary: {dictName}");
								
								try
								{
									var dict = CvAruco.GetPredefinedDictionary(dictName);
									Point2f[][] corners; int[] ids; Point2f[][] rejected;
									
									CvAruco.DetectMarkers(testImage, dict, out corners, out ids, params1, out rejected);
									
									if (logPath != null)
									{
										LogToFile(logPath, $"    Raw result: Found={ids?.Length ?? 0}, Rejected={rejected?.Length ?? 0}");
									}
									
									if (ids != null && ids.Length > 0)
									{
										// SMART FILTERING: Remove environmental markers, keep projected ones
										Point2f[][] filteredCorners;
										int[] filteredIds;
										
										if (FilterEnvironmentalMarkers(corners, ids, width, height, out filteredCorners, out filteredIds, logPath))
										{
											outCorners = filteredCorners;
											outIds = filteredIds;
											
											if (logPath != null)
											{
												LogToFile(logPath, $"SUCCESS: {imageName} + {dictName} found {filteredIds.Length} PROJECTED markers: [{string.Join(",", filteredIds)}]");
												LogToFile(logPath, $"  (Filtered from {ids.Length} total detections: [{string.Join(",", ids)}])");
											}
											
											if (saveDir != null)
											{
												try
												{
													using (var vis = bgr.Clone())
													{
														CvAruco.DrawDetectedMarkers(vis, filteredCorners, filteredIds, Scalar.LimeGreen);
														Cv2.ImWrite(System.IO.Path.Combine(saveDir, $"03_detected_{imageName}_{dictName}.png"), vis);
													}
												}
												catch { }
											}
											
											return true;
										}
										else
										{
											if (logPath != null)
											{
												LogToFile(logPath, $"FILTERED: {imageName} + {dictName} detected {ids.Length} markers [{string.Join(",", ids)}] but ALL were environmental markers");
											}
										}
									}
								}
								catch (Exception ex)
								{
									if (logPath != null) LogToFile(logPath, $"    Exception with {dictName}: {ex.Message}");
								}
							}
						}
					}
				}
				
				if (logPath != null) LogToFile(logPath, "PROJECTED MARKER DETECTION: All attempts failed");
				return false;
			}
			catch (Exception ex)
			{
				if (logPath != null) LogToFile(logPath, $"Projected marker detection error: {ex.Message}");
				return false;
			}
		}

		private void VerifyOpenCvRuntimePresent()
		{
			try
			{
				// Attempt to call a trivial OpenCv function to verify native load works; if it fails, warn user
				var ver = Cv2.GetVersionString();
				if (string.IsNullOrWhiteSpace(ver)) { AppendStatus("WARNING: OpenCV runtime not found."); }
			}
			catch
			{
				AppendStatus("WARNING: OpenCV runtime DLLs missing. Place OpenCvSharpExtern.dll and opencv_* DLLs next to the EXE for on-the-fly marker generation.");
			}
		}

		private bool TryGeometricMarkerDetection(Mat bgr, int width, int height, out Point2f[][] outQuads, out int[] outIds, string logPath, string saveDir)
		{
			outQuads = null;
			outIds = null;
			var detectedQuads = new List<Point2f[]>();
			var assignedIds = new List<int>();
			
			try
			{
				using (var gray = new Mat())
				{
					Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
					if (saveDir != null)
					{
						try { Cv2.ImWrite(System.IO.Path.Combine(saveDir, "geometric_gray.png"), gray); } catch { }
					}
					
					// Apply CLAHE for better contrast
					using (var clahe = Cv2.CreateCLAHE(3.0, new OpenCvSharp.Size(8, 8)))
					using (var enhanced = new Mat())
					{
						clahe.Apply(gray, enhanced);
						if (saveDir != null)
						{
							try { Cv2.ImWrite(System.IO.Path.Combine(saveDir, "geometric_clahe.png"), enhanced); } catch { }
						}
						
						// Binary threshold
						using (var binary = new Mat())
						{
							Cv2.Threshold(enhanced, binary, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
							if (saveDir != null)
							{
								try { Cv2.ImWrite(System.IO.Path.Combine(saveDir, "geometric_binary.png"), binary); } catch { }
							}
							
							// Find contours
							using (var contours = new Mat())
							{
								OpenCvSharp.Point[][] allContours;
								HierarchyIndex[] hierarchy;
								Cv2.FindContours(binary, out allContours, out hierarchy, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
								
								if (logPath != null)
								{
									LogToFile(logPath, $"Found {allContours.Length} contours");
								}
								
								// Look for rectangular contours
								foreach (var contour in allContours)
								{
									if (contour.Length < 4) continue;
									
									// Approximate contour to polygon
									var epsilon = 0.02 * Cv2.ArcLength(contour, true);
									var approx = Cv2.ApproxPolyDP(contour, epsilon, true);
									
									// Must be a quadrilateral
									if (approx.Length != 4) continue;
									
									// Check if it's convex
									if (!Cv2.IsContourConvex(approx)) continue;
									
									// Check area
									var area = Math.Abs(Cv2.ContourArea(approx));
									var minArea = (width * height) * 0.001; // At least 0.1% of image
									var maxArea = (width * height) * 0.15;  // At most 15% of image
									
									if (area < minArea || area > maxArea) continue;
									
									// Check aspect ratio (should be roughly square)
									var rect = Cv2.BoundingRect(approx);
									var aspectRatio = (double)rect.Width / rect.Height;
									if (aspectRatio < 0.3 || aspectRatio > 3.0) continue;
									
									// Check if corners are reasonable distances apart
									var corners = approx.Select(p => new Point2f(p.X, p.Y)).ToArray();
									var distances = new[]
									{
										Distance(corners[0], corners[1]),
										Distance(corners[1], corners[2]),
										Distance(corners[2], corners[3]),
										Distance(corners[3], corners[0])
									};
									var avgDistance = distances.Average();
									if (avgDistance < 20 || avgDistance > 500) continue;
									
									// Order corners consistently (clockwise from top-left)
									var orderedCorners = OrderCornersClockwise(corners);
									detectedQuads.Add(orderedCorners);
									
									if (logPath != null)
									{
										LogToFile(logPath, $"Accepted quad: area={area:F0}, avgDist={avgDistance:F1}, aspect={aspectRatio:F2}");
									}
								}
							}
						}
					}
				}
				
				if (detectedQuads.Count == 0)
				{
					if (logPath != null) LogToFile(logPath, "No valid rectangular contours found");
					return false;
				}
				
				// Sort by position and assign IDs (0=top-left, 1=top-right, 2=bottom-right, 3=bottom-left)
				var sorted = detectedQuads
					.Select(quad => new { 
						Quad = quad, 
						Center = new Point2f(quad.Average(p => p.X), quad.Average(p => p.Y)) 
					})
					.OrderBy(x => x.Center.Y) // Top to bottom first
					.ThenBy(x => x.Center.X)  // Then left to right
					.ToArray();
				
				// Assign IDs based on expected positions
				detectedQuads.Clear();
				assignedIds.Clear();
				for (int i = 0; i < Math.Min(4, sorted.Length); i++)
				{
					detectedQuads.Add(sorted[i].Quad);
					assignedIds.Add(i);
				}
				
				outQuads = detectedQuads.Take(4).ToArray();
				outIds = assignedIds.Take(4).ToArray();
				
				if (logPath != null)
				{
					LogToFile(logPath, $"Geometric detection: Found {outQuads.Length} markers with IDs [{string.Join(",", outIds)}]");
				}
				
				return outQuads.Length > 0;
			}
			catch (Exception ex)
			{
				if (logPath != null) LogToFile(logPath, $"Geometric detection error: {ex.Message}");
				return false;
			}
		}
		
		private Point2f[] OrderCornersClockwise(Point2f[] corners)
		{
			// Find the center point
			var centerX = corners.Average(p => p.X);
			var centerY = corners.Average(p => p.Y);
			var center = new Point2f(centerX, centerY);
			
			// Sort by angle from center
			var sorted = corners
				.Select(p => new { 
					Point = p, 
					Angle = Math.Atan2(p.Y - center.Y, p.X - center.X) 
				})
				.OrderBy(x => x.Angle)
				.Select(x => x.Point)
				.ToArray();
			
			return sorted;
		}

		private void LogToFile(string path, string message)
		{
			try { File.AppendAllText(path, message + Environment.NewLine); } catch { }
		}

		private ProjectorWindow GetProjectorWindow()
		{
			// Return the projector window instance managed by this screen
			return projectorWindow;
		}

		private bool FilterEnvironmentalMarkers(Point2f[][] corners, int[] ids, int imageWidth, int imageHeight, out Point2f[][] filteredCorners, out int[] filteredIds, string logPath)
		{
			var validCorners = new List<Point2f[]>();
			var validIds = new List<int>();
			
			try
			{
				if (logPath != null) LogToFile(logPath, "\n--- ENVIRONMENTAL MARKER FILTERING ---");
				
				// SMART CLUSTERING: Look for markers that are grouped together (projected markers)
				// vs isolated markers scattered around the room (environmental markers)
				
				var markerCenters = new List<(Point2f center, int index, int id)>();
				for (int i = 0; i < Math.Min(corners.Length, ids.Length); i++)
				{
					var corner = corners[i];
					var id = ids[i];
					
					if (corner != null && corner.Length >= 4)
					{
						// Calculate marker center
						var centerX = corner.Average(p => p.X);
						var centerY = corner.Average(p => p.Y);
						markerCenters.Add((new Point2f(centerX, centerY), i, id));
						
						if (logPath != null) LogToFile(logPath, $"Marker {id} at ({centerX:F1},{centerY:F1})");
					}
				}
				
				if (markerCenters.Count == 0)
				{
					if (logPath != null) LogToFile(logPath, "No valid markers found for clustering analysis");
				}
				else
				{
					// Find the largest cluster of markers (likely the projected ones)
					var clusteredMarkers = FindLargestMarkerCluster(markerCenters, imageWidth, imageHeight, logPath);
					
					if (clusteredMarkers.Count > 0)
					{
						if (logPath != null) LogToFile(logPath, $"Found cluster of {clusteredMarkers.Count} markers - likely projected markers");
					}
					else
					{
						if (logPath != null) LogToFile(logPath, "No clustered markers found - all appear to be environmental/scattered");
					}
					
					// Use clustered markers as the filtered result
					for (int i = 0; i < clusteredMarkers.Count; i++)
					{
						var marker = clusteredMarkers[i];
						var originalIndex = marker.index;
						
						if (logPath != null) LogToFile(logPath, $"ACCEPTED: Marker {marker.id} at ({marker.center.X:F1},{marker.center.Y:F1}) - part of projection cluster");
					}
					
					validCorners.AddRange(clusteredMarkers.Select(m => corners[m.index]));
					validIds.AddRange(clusteredMarkers.Select(m => m.id));
				}
				
				if (logPath != null) LogToFile(logPath, $"CLUSTERING RESULT: {validIds.Count} clustered markers from {ids.Length} detected");
				
			}
			catch (Exception ex)
			{
				if (logPath != null) LogToFile(logPath, $"Filtering error: {ex.Message}");
			}
			
			filteredCorners = validCorners.ToArray();
			filteredIds = validIds.ToArray();
			
			return filteredCorners.Length > 0;
		}

		private List<(Point2f center, int index, int id)> FindLargestMarkerCluster(List<(Point2f center, int index, int id)> markers, int imageWidth, int imageHeight, string logPath)
		{
			if (markers.Count <= 1) return markers;
			
			// Simple clustering: find markers that are close to each other
			var clusters = new List<List<(Point2f center, int index, int id)>>();
			var used = new bool[markers.Count];
			
			double clusterRadius = Math.Min(imageWidth, imageHeight) * 0.25; // 25% of image size
			if (logPath != null) LogToFile(logPath, $"Clustering with radius: {clusterRadius:F0} pixels");
			
			for (int i = 0; i < markers.Count; i++)
			{
				if (used[i]) continue;
				
				var cluster = new List<(Point2f center, int index, int id)> { markers[i] };
				used[i] = true;
				
				// Find all markers within cluster radius
				for (int j = i + 1; j < markers.Count; j++)
				{
					if (used[j]) continue;
					
					double distance = Math.Sqrt(
						Math.Pow(markers[i].center.X - markers[j].center.X, 2) + 
						Math.Pow(markers[i].center.Y - markers[j].center.Y, 2)
					);
					
					if (distance <= clusterRadius)
					{
						cluster.Add(markers[j]);
						used[j] = true;
					}
				}
				
				clusters.Add(cluster);
				if (logPath != null) LogToFile(logPath, $"Cluster {clusters.Count}: {cluster.Count} markers around ({markers[i].center.X:F0},{markers[i].center.Y:F0})");
			}
			
			// Return the largest cluster (most likely the projected markers)
			var largestCluster = clusters.OrderByDescending(c => c.Count).First();
			if (logPath != null) LogToFile(logPath, $"Selected largest cluster with {largestCluster.Count} markers");
			
			// Only return cluster if it has 2+ markers (projected markers should be grouped)
			return largestCluster.Count >= 2 ? largestCluster : new List<(Point2f center, int index, int id)>();
		}

		private bool RunOpenCvSharp4Diagnostics(string diagDir, string logPath)
		{
			try
			{
				LogToFile(logPath, "\n=== OPENCVSHARP4 DIAGNOSTICS ===");
				
				// Test 1: Basic OpenCV functionality
				try
				{
					using (var testMat = new Mat(100, 100, MatType.CV_8UC1, Scalar.All(128)))
					{
						LogToFile(logPath, $"✅ Basic Mat creation: SUCCESS ({testMat.Width}x{testMat.Height})");
					}
				}
				catch (Exception ex)
				{
					LogToFile(logPath, $"❌ Basic Mat creation: FAILED - {ex.Message}");
					return false;
				}
				
				// Test 2: OpenCV Version
				try
				{
					var version = Cv2.GetVersionString();
					LogToFile(logPath, $"✅ OpenCV Version: {version}");
				}
				catch (Exception ex)
				{
					LogToFile(logPath, $"❌ OpenCV Version check: FAILED - {ex.Message}");
				}
				
				// Test 3: ArUco dictionary loading
				bool arucoWorking = true;
				var testDicts = new[] {
					PredefinedDictionaryName.Dict4X4_50,
					PredefinedDictionaryName.Dict5X5_50,
					PredefinedDictionaryName.Dict6X6_250,
					PredefinedDictionaryName.Dict7X7_250
				};
				
				foreach (var dictName in testDicts)
				{
					try
					{
						using (var dict = CvAruco.GetPredefinedDictionary(dictName))
						{
							LogToFile(logPath, $"✅ Dictionary {dictName}: SUCCESS (size={dict.MarkerSize})");
						}
					}
					catch (Exception ex)
					{
						LogToFile(logPath, $"❌ Dictionary {dictName}: FAILED - {ex.Message}");
						arucoWorking = false;
					}
				}
				
				// Test 4: Basic detection functionality test  
				try
				{
					LogToFile(logPath, "\n--- BASIC DETECTION TEST ---");
					
					// Create a simple test image with white background
					using (var testImage = new Mat(200, 200, MatType.CV_8UC3, Scalar.All(255)))
					using (var dict = CvAruco.GetPredefinedDictionary(PredefinedDictionaryName.Dict4X4_50))
					{
						// Test basic detection on white image (should find nothing)
						using (var grayImage = new Mat())
						{
							Cv2.CvtColor(testImage, grayImage, ColorConversionCodes.BGR2GRAY);
							
							var params1 = new DetectorParameters();
							Point2f[][] corners; int[] ids; Point2f[][] rejected;
							
							CvAruco.DetectMarkers(grayImage, dict, out corners, out ids, params1, out rejected);
							
							LogToFile(logPath, $"✅ Basic detection test: SUCCESS - Found {ids?.Length ?? 0} markers (expected 0 on white image)");
							
							// Save test image
							var testPath = System.IO.Path.Combine(diagDir, "opencv_detection_test.png");
							Cv2.ImWrite(testPath, testImage);
							LogToFile(logPath, "Basic test image saved: opencv_detection_test.png");
						}
					}
				}
				catch (Exception ex)
				{
					LogToFile(logPath, $"❌ Basic detection test: FAILED - {ex.Message}");
					arucoWorking = false;
				}
				
				// Test 5: CLAHE functionality
				try
				{
					using (var testImg = new Mat(100, 100, MatType.CV_8UC1, Scalar.All(128)))
					using (var clahe = Cv2.CreateCLAHE(3.0, new OpenCvSharp.Size(8, 8)))
					using (var enhanced = new Mat())
					{
						clahe.Apply(testImg, enhanced);
						LogToFile(logPath, "✅ CLAHE enhancement: SUCCESS");
					}
				}
				catch (Exception ex)
				{
					LogToFile(logPath, $"❌ CLAHE enhancement: FAILED - {ex.Message}");
				}
				
				// Test 6: Color conversion
				try
				{
					using (var colorImg = new Mat(50, 50, MatType.CV_8UC3, Scalar.All(128)))
					using (var grayImg = new Mat())
					{
						Cv2.CvtColor(colorImg, grayImg, ColorConversionCodes.BGR2GRAY);
						LogToFile(logPath, "✅ Color conversion: SUCCESS");
					}
				}
				catch (Exception ex)
				{
					LogToFile(logPath, $"❌ Color conversion: FAILED - {ex.Message}");
				}
				
				LogToFile(logPath, $"\n=== OPENCVSHARP4 DIAGNOSTICS RESULT ===");
				LogToFile(logPath, arucoWorking ? "✅ OPENCV STATUS: FULLY FUNCTIONAL" : "❌ OPENCV STATUS: ISSUES DETECTED");
				
				return arucoWorking;
			}
			catch (Exception ex)
			{
				LogToFile(logPath, $"❌ OpenCvSharp4 diagnostics failed: {ex.Message}");
				return false;
			}
		}

		private string DescribeParams(DetectorParameters p)
		{
			try
			{
				return string.Format("WinMin={0},WinMax={1},Step={2},MinPerim={3},MaxErrBits={4},CornerRef={5},Invert={6}",
					p.AdaptiveThreshWinSizeMin, p.AdaptiveThreshWinSizeMax, p.AdaptiveThreshWinSizeStep,
					p.MinMarkerPerimeterRate, p.MaxErroneousBitsInBorderRate,
					p.CornerRefinementMethod, SafeGetDetectInverted(p));
			}
			catch { return "<params>"; }
		}

		private bool SafeGetDetectInverted(DetectorParameters p)
		{
			try { return p.DetectInvertedMarker; } catch { return false; }
		}

		private bool HasAllMarkerIds(int[] ids)
		{
			if (ids == null) return false;
			for (int i = 0; i < 4; i++)
			{
				if (Array.IndexOf(ids, i) < 0) return false;
			}
			return true;
		}

		private void FilterDetections(Point2f[][] corners, int[] ids, int width, int height, out Point2f[][] outCorners, out int[] outIds, PredefinedDictionaryName dictName, string logPath)
		{
			var listCorners = new List<Point2f[]>();
			var listIds = new List<int>();
			if (corners != null && ids != null)
			{
				for (int i = 0; i < Math.Min(corners.Length, ids.Length); i++)
				{
					var quad = corners[i];
					if (quad == null || quad.Length < 4) continue;
					
					// Basic sanity checks only - be more permissive for projector markers
					bool validQuad = true;
					double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
					for (int k = 0; k < 4; k++)
					{
						if (double.IsNaN(quad[k].X) || double.IsNaN(quad[k].Y)) { validQuad = false; break; }
						if (quad[k].X < minX) minX = quad[k].X;
						if (quad[k].Y < minY) minY = quad[k].Y;
						if (quad[k].X > maxX) maxX = quad[k].X;
						if (quad[k].Y > maxY) maxY = quad[k].Y;
					}
					if (!validQuad) continue;
					
					// More permissive bounds check - allow markers that extend slightly outside frame
					double margin = 50.0; // pixels
					if (maxX < -margin || minX > width + margin || maxY < -margin || minY > height + margin) 
					{
						if (logPath != null) LogToFile(logPath, $"Filter: rejected marker {ids[i]} - completely outside bounds");
						continue;
					}
					
					// More permissive size checks for projector markers
					double wpx = maxX - minX, hpx = maxY - minY;
					double area = wpx * hpx;
					double minArea = (width * height) * 0.00001; // Smaller minimum (was 0.00002)
					double maxArea = (width * height) * 0.5; // Larger maximum (was 0.25)
					if (area < minArea) 
					{
						if (logPath != null) LogToFile(logPath, $"Filter: rejected marker {ids[i]} - too small (area={area:F0}, min={minArea:F0})");
						continue;
					}
					if (area > maxArea) 
					{
						if (logPath != null) LogToFile(logPath, $"Filter: rejected marker {ids[i]} - too large (area={area:F0}, max={maxArea:F0})");
						continue;
					}
					
					// Very permissive perimeter check
					double d01 = Distance(quad[0], quad[1]);
					double d12 = Distance(quad[1], quad[2]);
					double d23 = Distance(quad[2], quad[3]);
					double d30 = Distance(quad[3], quad[0]);
					double avg = (d01 + d12 + d23 + d30) / 4.0;
					if (avg < 2.0) // Reduced from 3.0 to 2.0
					{
						if (logPath != null) LogToFile(logPath, $"Filter: rejected marker {ids[i]} - perimeter too small (avg={avg:F1})");
						continue;
					}
					
					int id = ids[i];
					listCorners.Add(quad);
					listIds.Add(id);
					if (logPath != null) LogToFile(logPath, $"Filter: accepted marker {id} - area={area:F0}, avg_side={avg:F1}");
				}
			}
			outCorners = listCorners.Count > 0 ? listCorners.ToArray() : null;
			outIds = listIds.Count > 0 ? listIds.ToArray() : null;
			if (logPath != null)
			{
				LogToFile(logPath, string.Format("Filter: kept={0}, ids=[{1}]", listIds.Count, listIds.Count > 0 ? string.Join(",", listIds) : ""));
			}
		}

		private double Distance(Point2f a, Point2f b)
		{
			double dx = a.X - b.X; double dy = a.Y - b.Y; return Math.Sqrt(dx * dx + dy * dy);
		}

		private double AngleDeg(Point2f prev, Point2f vertex, Point2f next)
		{
			double v1x = prev.X - vertex.X, v1y = prev.Y - vertex.Y;
			double v2x = next.X - vertex.X, v2y = next.Y - vertex.Y;
			double dot = v1x * v2x + v1y * v2y;
			double n1 = Math.Sqrt(v1x * v1x + v1y * v1y);
			double n2 = Math.Sqrt(v2x * v2x + v2y * v2y);
			if (n1 < 1e-5 || n2 < 1e-5) return 0.0;
			double cos = dot / (n1 * n2);
			if (cos < -1) cos = -1; else if (cos > 1) cos = 1;
			return Math.Acos(cos) * (180.0 / Math.PI);
		}

		private bool IsRightish(double angleDeg, double tolerance)
		{
			return Math.Abs(angleDeg - 90.0) <= tolerance;
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
					AddMarkerFromQuad(quad, -1, null); // Legacy call - no logging for now
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

		private void AddMarkerFromQuad(Point2f[] quad, int markerId, string logPath)
		{
			// Calculate center of detected ArUco marker
			double cx = 0, cy = 0;
			for (int i = 0; i < 4; i++) { cx += quad[i].X; cy += quad[i].Y; }
			cx /= 4.0; cy /= 4.0;
			
			// Store color space coordinate
			lastDetectedCentersColor.Add(new System.Windows.Point(cx, cy));
			
			// Transform to canvas coordinates with detailed logging
			var colorPt = new System.Windows.Point(cx, cy);
			var canvasPt = ColorToCanvas(colorPt, logPath, markerId);
			
			// Create red dot overlay with larger size for better visibility
			var dot = new Ellipse { Width = 24, Height = 24, Fill = Brushes.Red, Stroke = Brushes.Yellow, StrokeThickness = 3 };
			Canvas.SetLeft(dot, canvasPt.X - 12);  // Center the 24px dot
			Canvas.SetTop(dot, canvasPt.Y - 12);
			MarkersOverlay.Children.Add(dot);
			
			// Add corner dots for better visualization with detailed logging
			if (logPath != null)
			{
				LogToFile(logPath, $"  Marker {markerId} corners in camera space:");
				for (int i = 0; i < 4; i++)
				{
					LogToFile(logPath, $"    Corner {i}: ({quad[i].X:F1}, {quad[i].Y:F1})");
				}
			}
			
			for (int i = 0; i < 4; i++)
			{
				var cornerColorPt = new System.Windows.Point(quad[i].X, quad[i].Y);
									var cornerCanvasPt = ColorToCanvas(cornerColorPt); // Don't log each corner individually
				var cornerDot = new Ellipse { Width = 8, Height = 8, Fill = Brushes.LimeGreen, Stroke = Brushes.Black, StrokeThickness = 1 };
				Canvas.SetLeft(cornerDot, cornerCanvasPt.X - 4);
				Canvas.SetTop(cornerDot, cornerCanvasPt.Y - 4);
				MarkersOverlay.Children.Add(cornerDot);
			}
		}

		private System.Windows.Point ColorToCanvas(System.Windows.Point colorPoint, string logPath = null, int markerId = -1)
		{
			// Get actual dimensions
			double containerW = MarkersOverlay.ActualWidth;
			double containerH = MarkersOverlay.ActualHeight;
			int colorW = (kinectManager != null && kinectManager.ColorWidth > 0) ? kinectManager.ColorWidth : 1920;
			int colorH = (kinectManager != null && kinectManager.ColorHeight > 0) ? kinectManager.ColorHeight : 1080;
			
			// Log container dimensions for debugging
			if (logPath != null && markerId >= 0)
			{
				LogToFile(logPath, $"Marker {markerId} coordinate transformation:");
				LogToFile(logPath, $"  Container (MarkersOverlay): {containerW:F1}x{containerH:F1}");
				LogToFile(logPath, $"  Camera resolution: {colorW}x{colorH}");
			}
			
			// Critical: Check if container is properly sized - FORCE LARGER SIZE
			if (containerW < 400 || containerH < 300) 
			{ 
				// Use camera feed size if available
				if (CameraFeed.ActualWidth > 400 && CameraFeed.ActualHeight > 300)
				{
					containerW = CameraFeed.ActualWidth; 
					containerH = CameraFeed.ActualHeight;
				}
				else
				{
					containerW = 800; 
					containerH = 600;
				}
				if (logPath != null && markerId >= 0)
				{
					LogToFile(logPath, $"  ⚠️  FIXED: Container too small ({MarkersOverlay.ActualWidth:F1}x{MarkersOverlay.ActualHeight:F1}), using {containerW}x{containerH}");
				}
			}
			
			// Calculate scaling to fit color image in container (maintaining aspect ratio)
			double sx = containerW / colorW;
			double sy = containerH / colorH;
			double scale = Math.Min(sx, sy);  // Uniform scaling to fit
			
			// Calculate displayed image dimensions and centering offsets
			double displayedW = colorW * scale;
			double displayedH = colorH * scale;
			double offsetX = (containerW - displayedW) / 2.0;
			double offsetY = (containerH - displayedH) / 2.0;
			
			// Transform color coordinates to canvas coordinates
			double x = colorPoint.X * scale + offsetX;
			double y = colorPoint.Y * scale + offsetY;
			
			// Detailed coordinate logging to file
			if (logPath != null && markerId >= 0)
			{
				LogToFile(logPath, $"  Camera coordinates: ({colorPoint.X:F1}, {colorPoint.Y:F1})");
				LogToFile(logPath, $"  Scale factor: {scale:F4} (sx={sx:F4}, sy={sy:F4})");
				LogToFile(logPath, $"  Displayed image: {displayedW:F1}x{displayedH:F1}");
				LogToFile(logPath, $"  Canvas offset: ({offsetX:F1}, {offsetY:F1})");
				LogToFile(logPath, $"  Final canvas coordinates: ({x:F1}, {y:F1})");
			}
			
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
				// Try to load from disk if available. Prefer 7x7_250 markers, then fall back to 6x6_250
				string markersDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Markers");
				string[] candidates = new[]
				{
					System.IO.Path.Combine(markersDir, $"aruco_7x7_250_id{id}.png"),
					System.IO.Path.Combine(markersDir, $"aruco_7x7_250_id_{id}.png"),
					System.IO.Path.Combine(markersDir, $"aruco_6x6_250_id{id}.png"),
					System.IO.Path.Combine(markersDir, $"aruco_6x6_250_id_{id}.png")
				};
				foreach (var file in candidates)
				{
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
			}
			catch { }
			// Fallback generation disabled - real markers should be available in Assets and Markers folders
			// If you need programmatic generation, install a compatible version of OpenCvSharp with ArUco support
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
