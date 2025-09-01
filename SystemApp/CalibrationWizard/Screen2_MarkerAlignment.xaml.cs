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
			// Task 1: Acquire perfect, lossless image from WriteableBitmap/byte[]
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
			StatusText.Text = "Status: Detecting...";
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
						try { LogToFile(logPath, $"OpenCV: {Cv2.GetVersionString()}"); } catch { }
					}

					// Quick minimal detection pass: grayscale with relaxed params across likely dictionaries
					Point2f[][] quickCorners; int[] quickIds; string quickDict;
					if (TryMinimalDictionarySweep(matBGR, w, h, out quickCorners, out quickIds, out quickDict, verbose ? logPath : null, verbose ? diagDir : null))
					{
						// Accept quick result immediately
						var pairsQuick = new List<(int id, Point2f[] quad)>();
						for (int i = 0; i < Math.Min(quickIds.Length, quickCorners.Length); i++)
						{
							pairsQuick.Add((quickIds[i], quickCorners[i]));
						}
						pairsQuick.Sort((a,b) => a.id.CompareTo(b.id));
						_detectedIds = pairsQuick.Select(p => p.id).ToArray();
						_detectedCorners = pairsQuick.Select(p => p.quad).ToArray();
						StatusText.Text = $"Status: Found {quickIds.Length} marker(s) using Minimal+{quickDict}";
						StatusText.Foreground = Brushes.Green;
						CalibrateButton.IsEnabled = HasAllMarkerIds(quickIds);
						NextButton.IsEnabled = HasAllMarkerIds(quickIds);
						MarkersOverlay.Children.Clear();
						AddDetections(quickCorners);
						return;
					}

					// Task 2: Build processing strategies
					var strategyImages = BuildProcessingStrategies(matBGR);
					if (verbose)
					{
						foreach (var kv in strategyImages)
						{
							try { Cv2.ImWrite(System.IO.Path.Combine(diagDir, $"strategy_{kv.Key.Replace(' ', '_')}.png"), kv.Value); } catch { }
						}
					}

					// Task 3: Detector parameter sets (default + aggressive)
					var parameterSets = BuildDetectorParameterSets();

					// Task 4: Multi-strategy detection loop
					Point2f[][] bestCorners = null; int[] bestIds = null; string bestKey = null; string bestDictName = null; Point2f[][] bestRejected = null;
					var dicts = BuildDictionaries();
					await Task.Run(() =>
					{
						var totalStart = Stopwatch.StartNew();
						int rejectedSaved = 0;
						foreach (var kv in strategyImages)
						{
							if (totalStart.ElapsedMilliseconds > 2500) break;
							foreach (var p in parameterSets)
							{
								if (totalStart.ElapsedMilliseconds > 2500) break;
								foreach (var dictName in dicts)
								{
									if (totalStart.ElapsedMilliseconds > 2500) break;
									Point2f[][] corners; int[] ids; Point2f[][] rejected;
									var dict = CvAruco.GetPredefinedDictionary(dictName);
									CvAruco.DetectMarkers(kv.Value, dict, out corners, out ids, p, out rejected);
									if (ids != null && ids.Length > 0)
									{
										// Rescale detected corners back to original color frame size if strategy image size differs
										double sx = (double)w / kv.Value.Width;
										double sy = (double)h / kv.Value.Height;
										if (Math.Abs(sx - 1.0) > 1e-6 || Math.Abs(sy - 1.0) > 1e-6)
										{
											for (int ci = 0; ci < corners.Length; ci++)
											{
												for (int pi = 0; pi < corners[ci].Length; pi++)
												{
													corners[ci][pi].X = (float)(corners[ci][pi].X * sx);
													corners[ci][pi].Y = (float)(corners[ci][pi].Y * sy);
												}
											}
										}
										Point2f[][] filteredCorners; int[] filteredIds;
										FilterDetections(corners, ids, w, h, out filteredCorners, out filteredIds, dictName, null);
										if (filteredIds != null && filteredIds.Length > 0)
										{
											bestCorners = filteredCorners; bestIds = filteredIds; bestKey = kv.Key; bestDictName = dictName.ToString(); return;
										}
									}
								}
								if (bestIds != null) break;
							}
						}
					});

					foreach (var kv in strategyImages) kv.Value.Dispose();

					if (bestIds != null && bestIds.Length > 0)
					{
						// Sort detections by ID to make UI overlays stable and mapping consistent
						var pairs = new List<(int id, Point2f[] quad)>();
						for (int i = 0; i < Math.Min(bestIds.Length, bestCorners.Length); i++)
						{
							pairs.Add((bestIds[i], bestCorners[i]));
						}
						pairs.Sort((a,b) => a.id.CompareTo(b.id));
						_detectedIds = pairs.Select(p => p.id).ToArray();
						_detectedCorners = pairs.Select(p => p.quad).ToArray();
						StatusText.Text = $"Status: Found {bestIds.Length}/4 expected marker(s) [{string.Join(",", bestIds)}] using {bestKey}, dict={bestDictName}";
						StatusText.Foreground = Brushes.Green;
						CalibrateButton.IsEnabled = HasAllMarkerIds(bestIds);
						NextButton.IsEnabled = HasAllMarkerIds(bestIds);
						// Draw overlay outlines
						MarkersOverlay.Children.Clear();
						AddDetections(bestCorners);
						if (verbose)
						{
							try
							{
								using (var vis = matBGR.Clone())
								{
									CvAruco.DrawDetectedMarkers(vis, bestCorners, bestIds, Scalar.LimeGreen);
									Cv2.ImWrite(System.IO.Path.Combine(diagDir, "zz_detected.png"), vis);
								}
							}
							catch { }
						}
						// Mark if calibration-ready (IDs 0..3 present)
						if (!HasAllMarkerIds(bestIds))
						{
							StatusText.Text += " (IDs 0..3 not all present)";
						}
					}
					else
					{
						StatusText.Text = "Status: No markers found. Try adjusting projector/camera.";
						StatusText.Foreground = Brushes.OrangeRed;
						if (verbose) LogToFile(logPath, "RESULT: No markers found in any strategy.");
					}
				}
			}
			finally
			{
				if (handle.IsAllocated) handle.Free();
				FindMarkersButton.IsEnabled = true;
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
			// Try both 6x6 and 7x7 first (most likely), then smaller fallbacks
			return new List<PredefinedDictionaryName>
			{
				PredefinedDictionaryName.Dict6X6_250,
				PredefinedDictionaryName.Dict7X7_250,
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

		private bool TryMinimalDictionarySweep(Mat bgr, int w, int h, out Point2f[][] outCorners, out int[] outIds, out string outDict, string logPath, string saveDir)
		{
			outCorners = null; outIds = null; outDict = string.Empty;
			using (var gray = new Mat())
			{
				Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
				var dicts = new[]
				{
					PredefinedDictionaryName.Dict6X6_250,
					PredefinedDictionaryName.Dict7X7_250,
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
						}
						if (ids != null && ids.Length > 0)
						{
							// No scaling needed; using original size
							Point2f[][] filtered; int[] fids;
							FilterDetections(corners, ids, w, h, out filtered, out fids, d, logPath);
							if (fids != null && fids.Length > 0)
							{
								outCorners = filtered; outIds = fids; outDict = d.ToString();
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
					}
					catch { }
				}
			}
			return false;
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

		private void LogToFile(string path, string message)
		{
			try { File.AppendAllText(path, message + Environment.NewLine); } catch { }
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
					// Geometry checks: within bounds and reasonable size
					double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
					for (int k = 0; k < 4; k++)
					{
						if (double.IsNaN(quad[k].X) || double.IsNaN(quad[k].Y)) { minX = double.MaxValue; break; }
						if (quad[k].X < minX) minX = quad[k].X;
						if (quad[k].Y < minY) minY = quad[k].Y;
						if (quad[k].X > maxX) maxX = quad[k].X;
						if (quad[k].Y > maxY) maxY = quad[k].Y;
					}
					if (minX == double.MaxValue) continue;
					if (minX < 0 || minY < 0 || maxX >= width || maxY >= height) continue;
					double wpx = maxX - minX, hpx = maxY - minY;
					double area = wpx * hpx;
					if (area < (width * height) * 0.00002) continue; // too small
					if (area > (width * height) * 0.25) continue; // too large
					// Keep only minimal geometry checks to avoid rejecting true markers under heavy perspective
					double d01 = Distance(quad[0], quad[1]);
					double d12 = Distance(quad[1], quad[2]);
					double d23 = Distance(quad[2], quad[3]);
					double d30 = Distance(quad[3], quad[0]);
					double avg = (d01 + d12 + d23 + d30) / 4.0;
					if (avg < 3.0) continue;
					int id = ids[i];
					// Accept any ID for detection; calibration still requires 0..3
					listCorners.Add(quad);
					listIds.Add(id);
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
				// Try to load from disk if available. Prefer 6x6_250 markers, then fall back to 7x7_250
				string markersDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Markers");
				string[] candidates = new[]
				{
					System.IO.Path.Combine(markersDir, $"aruco_6x6_250_id{id}.png"),
					System.IO.Path.Combine(markersDir, $"aruco_6x6_250_id_{id}.png"),
					System.IO.Path.Combine(markersDir, $"aruco_7x7_250_id{id}.png"),
					System.IO.Path.Combine(markersDir, $"aruco_7x7_250_id_{id}.png")
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
			// Fallback: generate a 6x6_250 marker programmatically to guarantee correct dictionary
			try
			{
				using (var marker = new Mat())
				{
					var dict = CvAruco.GetPredefinedDictionary(PredefinedDictionaryName.Dict6X6_250);
					CvAruco.DrawMarker(dict, id, size, marker, 1);
					var bmp = marker.ToWriteableBitmap();
					_usingRealMarkers = true;
					return bmp;
				}
			}
			catch { }
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
