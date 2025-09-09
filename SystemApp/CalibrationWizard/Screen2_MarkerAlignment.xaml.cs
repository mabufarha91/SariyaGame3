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

		private CalibrationConfig calibrationConfig;

		public Screen2_MarkerAlignment(KinectManager.KinectManager manager, CalibrationConfig config = null)
		{
			InitializeComponent();
			kinectManager = manager;
			calibrationConfig = config ?? new CalibrationConfig();
			Loaded += Screen2_MarkerAlignment_Loaded;
			SizeChanged += Screen2_MarkerAlignment_SizeChanged;
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
					// Synchronize overlay with camera feed dimensions
					SynchronizeOverlayWithCameraFeed();
				}
			}
			catch { }
			CameraStatusText.Text = (kinectManager != null && kinectManager.IsInitialized) ? "Camera: Connected" : "Camera: Not Available (Test Mode)";
		}
		
		private void SynchronizeOverlayWithCameraFeed()
		{
			try
			{
				// CRITICAL FIX: The MarkersOverlay must match the actual rendered camera feed size
				// NOT the parent container size, to ensure proper coordinate mapping
				
				// Force the camera feed to update its layout first
				CameraFeed.UpdateLayout();
				
				// Use the camera feed's actual rendered size
				double feedWidth = CameraFeed.ActualWidth;
				double feedHeight = CameraFeed.ActualHeight;
				
				// If camera feed is not properly sized, calculate based on aspect ratio
				if (feedWidth <= 0 || feedHeight <= 0)
				{
					// Get the parent container size
					var parent = CameraFeed.Parent as FrameworkElement;
					if (parent != null)
					{
						double containerWidth = parent.ActualWidth;
						double containerHeight = parent.ActualHeight;
						
						// Calculate the actual rendered size based on camera aspect ratio
						int colorW = (kinectManager != null && kinectManager.ColorWidth > 0) ? kinectManager.ColorWidth : 1920;
						int colorH = (kinectManager != null && kinectManager.ColorHeight > 0) ? kinectManager.ColorHeight : 1080;
						double aspectRatio = (double)colorW / colorH;
						
						// Calculate the actual rendered size with Stretch="Uniform"
						if (containerWidth / containerHeight > aspectRatio)
						{
							// Container is wider than aspect ratio, height is the limiting factor
							feedHeight = containerHeight;
							feedWidth = containerHeight * aspectRatio;
						}
						else
						{
							// Container is taller than aspect ratio, width is the limiting factor
							feedWidth = containerWidth;
							feedHeight = containerWidth / aspectRatio;
						}
					}
				}
				
				// Set canvas size to match the actual camera feed rendered size
				MarkersOverlay.Width = feedWidth;
				MarkersOverlay.Height = feedHeight;
				
				// Position the canvas at the same location as the camera feed
				Canvas.SetLeft(MarkersOverlay, 0);
				Canvas.SetTop(MarkersOverlay, 0);
				
				// Force the canvas to update its layout
				MarkersOverlay.UpdateLayout();
				
				System.Diagnostics.Debug.WriteLine($"Overlay synchronized: {MarkersOverlay.Width}x{MarkersOverlay.Height} (feed: {CameraFeed.ActualWidth}x{CameraFeed.ActualHeight})");
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"Overlay synchronization error: {ex.Message}");
			}
		}
		
		private void Screen2_MarkerAlignment_SizeChanged(object sender, SizeChangedEventArgs e)
		{
			// CRITICAL: When window is resized or maximized, resynchronize the overlay and update marker positions
			Dispatcher.BeginInvoke(new Action(() => {
				SynchronizeOverlayWithCameraFeed();
				UpdateMarkerPositionsOnResize();
			}), DispatcherPriority.Loaded);
		}
		
		private void UpdateMarkerPositionsOnResize()
		{
			try
			{
				// Only update if we have detected markers
				if (_detectedCorners != null && _detectedCorners.Length > 0 && lastDetectedCentersColor.Count > 0)
				{
					// Clear current overlay
					MarkersOverlay.Children.Clear();
					
					// Re-add test blue dot at center
					var testDot = new Ellipse { Width = 20, Height = 20, Fill = Brushes.Blue, Stroke = Brushes.White, StrokeThickness = 2 };
					
					// Position the test dot at the center of the MarkersOverlay (which represents the camera feed area)
					double centerX = MarkersOverlay.ActualWidth / 2.0;
					double centerY = MarkersOverlay.ActualHeight / 2.0;
					
					Canvas.SetLeft(testDot, centerX - 10);  // Center the 20px dot
					Canvas.SetTop(testDot, centerY - 10);   // Center the 20px dot
					MarkersOverlay.Children.Add(testDot);
					
					// Re-process all detected markers with new coordinates
					for (int i = 0; i < _detectedCorners.Length; i++)
					{
						int markerId = i < _detectedIds.Length ? _detectedIds[i] : -1;
						AddMarkerFromQuad(_detectedCorners[i], markerId, null);
					}
					
					// Re-draw touch area rectangle if all 4 markers are present
					if (_detectedIds != null && _detectedIds.Length == 4 && HasAllMarkerIds(_detectedIds))
					{
						CalculateAndSaveTouchArea(_detectedCorners, _detectedIds, null);
					}
					
					// Draw rectangle connecting the red dots if we have 4 markers
					if (lastDetectedCentersColor.Count == 4)
					{
						DrawRectangleFromRedDots();
					}
				}
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"Error updating marker positions on resize: {ex.Message}");
			}
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
			// Synchronize overlay with camera feed after a short delay to ensure UI is fully loaded
			Dispatcher.BeginInvoke(new Action(() => SynchronizeOverlayWithCameraFeed()), DispatcherPriority.Loaded);
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
			string markerDiagPath = System.IO.Path.Combine(diagDir, "red_markers_diagnostic.txt");
			if (verbose) { try { Directory.CreateDirectory(diagDir); } catch { } }
			
			// COMPREHENSIVE RED MARKERS DIAGNOSTIC
			LogToFile(markerDiagPath, "=== COMPREHENSIVE RED MARKERS DIAGNOSTIC ===");
			LogToFile(markerDiagPath, $"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
			LogToFile(markerDiagPath, $"Window State: {this.WindowState}");
			LogToFile(markerDiagPath, $"Window Size: {this.ActualWidth}x{this.ActualHeight}");
			LogToFile(markerDiagPath, $"Camera Feed Visibility: {CameraFeed.Visibility}");
			LogToFile(markerDiagPath, $"Camera Feed Size: {CameraFeed.ActualWidth}x{CameraFeed.ActualHeight}");
			LogToFile(markerDiagPath, $"MarkersOverlay Size: {MarkersOverlay.ActualWidth}x{MarkersOverlay.ActualHeight}");
			LogToFile(markerDiagPath, $"MarkersOverlay Children Count: {MarkersOverlay.Children.Count}");
			LogToFile(markerDiagPath, $"MarkersOverlay Background: {MarkersOverlay.Background}");
			LogToFile(markerDiagPath, $"MarkersOverlay ClipToBounds: {MarkersOverlay.ClipToBounds}");
			LogToFile(markerDiagPath, "");
			
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
					// COMPREHENSIVE DETECTION RESULT LOGGING
					LogToFile(markerDiagPath, "=== DETECTION RESULT ANALYSIS ===");
					LogToFile(markerDiagPath, $"Detection Success: {result.Success}");
					LogToFile(markerDiagPath, $"Detected Markers Count: {result.Ids.Length}");
					LogToFile(markerDiagPath, $"Detected Marker IDs: [{string.Join(", ", result.Ids)}]");
					LogToFile(markerDiagPath, $"Corners Array Length: {result.Corners.Length}");
					LogToFile(markerDiagPath, "");
					
					// Log each detected marker's corners
					for (int i = 0; i < result.Corners.Length; i++)
					{
						LogToFile(markerDiagPath, $"Marker {i} (ID: {result.Ids[i]}):");
						for (int j = 0; j < result.Corners[i].Length; j++)
						{
							LogToFile(markerDiagPath, $"  Corner {j}: ({result.Corners[i][j].X:F2}, {result.Corners[i][j].Y:F2})");
						}
						LogToFile(markerDiagPath, "");
					}
					
					_detectedCorners = result.Corners;
					_detectedIds = result.Ids;
					StatusText.Text = $"Status: Found {result.Ids.Length} ArUco marker(s)";
						StatusText.Foreground = Brushes.Green;
					CalibrateButton.IsEnabled = HasAllMarkerIds(result.Ids);
					NextButton.IsEnabled = HasAllMarkerIds(result.Ids);
					
					// COMPREHENSIVE OVERLAY CLEARING LOGGING
					LogToFile(markerDiagPath, "=== OVERLAY CLEARING PROCESS ===");
					LogToFile(markerDiagPath, $"Before Clear - MarkersOverlay Children Count: {MarkersOverlay.Children.Count}");
						MarkersOverlay.Children.Clear();
					lastDetectedCentersColor.Clear();
					LogToFile(markerDiagPath, $"After Clear - MarkersOverlay Children Count: {MarkersOverlay.Children.Count}");
					LogToFile(markerDiagPath, "");
					
					// DEBUG: Add a test marker to verify canvas is working
					LogToFile(markerDiagPath, "=== TEST MARKER CREATION ===");
					var testDot = new Ellipse { Width = 20, Height = 20, Fill = Brushes.Blue, Stroke = Brushes.White, StrokeThickness = 2 };
					
					// Position the test dot at the center of the MarkersOverlay (which represents the camera feed area)
					double centerX = MarkersOverlay.ActualWidth / 2.0;
					double centerY = MarkersOverlay.ActualHeight / 2.0;
					
					Canvas.SetLeft(testDot, centerX - 10);  // Center the 20px dot
					Canvas.SetTop(testDot, centerY - 10);   // Center the 20px dot
					MarkersOverlay.Children.Add(testDot);
					LogToFile(markerDiagPath, $"Test Blue Dot Created at center ({centerX:F1},{centerY:F1})");
					LogToFile(markerDiagPath, $"After Test Dot - MarkersOverlay Children Count: {MarkersOverlay.Children.Count}");
					LogToFile(markerDiagPath, $"Test Dot Properties: Width={testDot.Width}, Height={testDot.Height}, Fill={testDot.Fill}");
					LogToFile(markerDiagPath, "");
					System.Diagnostics.Debug.WriteLine("Added test blue dot at (50,50)");
					
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
					LogToFile(markerDiagPath, "=== MARKER PROCESSING PHASE ===");
					LogToFile(markerDiagPath, $"Processing {result.Corners.Length} detected markers");
					System.Diagnostics.Debug.WriteLine($"Processing {result.Corners.Length} detected markers");
					
					for (int i = 0; i < result.Corners.Length; i++)
					{
						int markerId = i < result.Ids.Length ? result.Ids[i] : -1;
						LogToFile(markerDiagPath, $"--- Processing Marker {i} (ID: {markerId}) ---");
						LogToFile(markerDiagPath, $"Before Processing - MarkersOverlay Children Count: {MarkersOverlay.Children.Count}");
						System.Diagnostics.Debug.WriteLine($"Processing marker {i}, ID: {markerId}");
						
						AddMarkerFromQuad(result.Corners[i], markerId, markerDiagPath);
						
						LogToFile(markerDiagPath, $"After Processing - MarkersOverlay Children Count: {MarkersOverlay.Children.Count}");
						LogToFile(markerDiagPath, "");
					}
					
					// Draw rectangle connecting the red dots if we have 4 markers
					if (lastDetectedCentersColor.Count == 4)
					{
						LogToFile(markerDiagPath, "=== DRAWING RECTANGLE FROM RED DOTS ===");
						LogToFile(markerDiagPath, $"Red dots count: {lastDetectedCentersColor.Count}");
						DrawRectangleFromRedDots();
						LogToFile(markerDiagPath, $"After rectangle - MarkersOverlay Children Count: {MarkersOverlay.Children.Count}");
					}
					
					// Calculate and save touch area if all 4 markers are detected
					LogToFile(markerDiagPath, "=== TOUCH AREA CALCULATION ===");
					LogToFile(markerDiagPath, $"Detected markers count: {result.Ids.Length}");
					LogToFile(markerDiagPath, $"Has all marker IDs: {HasAllMarkerIds(result.Ids)}");
					
					if (result.Ids.Length == 4 && HasAllMarkerIds(result.Ids))
					{
						LogToFile(markerDiagPath, "All 4 markers detected - calculating touch area...");
						CalculateAndSaveTouchArea(result.Corners, result.Ids, markerDiagPath);
					}
					else
					{
						LogToFile(markerDiagPath, "Not all 4 markers detected - skipping touch area calculation");
					}
					LogToFile(markerDiagPath, "");
					
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
					// CRITICAL: Synchronize overlay after camera feed is restored
					Dispatcher.BeginInvoke(new Action(() => SynchronizeOverlayWithCameraFeed()), DispatcherPriority.Loaded);
					if (verbose) LogToFile(logPath, "✅ Camera feed restored after detection");
				}
				
				// FINAL COMPREHENSIVE DIAGNOSTIC SUMMARY
				LogToFile(markerDiagPath, "=== FINAL DIAGNOSTIC SUMMARY ===");
				LogToFile(markerDiagPath, $"Final MarkersOverlay Children Count: {MarkersOverlay.Children.Count}");
				LogToFile(markerDiagPath, $"Final MarkersOverlay Size: {MarkersOverlay.ActualWidth}x{MarkersOverlay.ActualHeight}");
				LogToFile(markerDiagPath, $"Final Camera Feed Size: {CameraFeed.ActualWidth}x{CameraFeed.ActualHeight}");
				LogToFile(markerDiagPath, $"Final Camera Feed Visibility: {CameraFeed.Visibility}");
				LogToFile(markerDiagPath, $"Final Status Text: {StatusText.Text}");
				LogToFile(markerDiagPath, "");
				LogToFile(markerDiagPath, "=== END OF COMPREHENSIVE RED MARKERS DIAGNOSTIC ===");
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
				// VERTICAL FLIP ONLY: The Kinect camera mirrors the wall, so vertical flip makes detection match camera view
				using (var flipped = new Mat())
				{
					Cv2.Flip(bgr, flipped, FlipMode.Y); // Vertical flip only
					
					if (saveDir != null)
					{
						try { Cv2.ImWrite(System.IO.Path.Combine(saveDir, "00_vertically_flipped_bgr.png"), flipped); } catch { }
					}
					
					// Use the vertically flipped image for detection
					return TryFastArucoDetectionInternal(flipped, width, height, out outCorners, out outIds, logPath, saveDir);
				}
			}
			catch (Exception ex)
			{
				if (logPath != null) LogToFile(logPath, $"Projected marker detection error: {ex.Message}");
				return false;
			}
		}

		private bool TryFastArucoDetectionInternal(Mat bgr, int width, int height, out Point2f[][] outCorners, out int[] outIds, string logPath, string saveDir)
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
				if (logPath != null) LogToFile(logPath, "\n--- STRICT ID FILTERING (ONLY IDs 0,1,2,3) ---");
				
				// CRITICAL: Only accept markers with IDs 0, 1, 2, 3 (our projected markers)
				var allowedIds = new int[] { 0, 1, 2, 3 };
				
				if (logPath != null) LogToFile(logPath, $"Raw detection: {ids.Length} markers with IDs: [{string.Join(", ", ids)}]");
				
				for (int i = 0; i < Math.Min(corners.Length, ids.Length); i++)
				{
					var corner = corners[i];
					var id = ids[i];
					
					if (corner != null && corner.Length >= 4)
					{
						// Calculate marker center for logging
						var centerX = corner.Average(p => p.X);
						var centerY = corner.Average(p => p.Y);
						
						if (allowedIds.Contains(id))
						{
							validCorners.Add(corner);
							validIds.Add(id);
							if (logPath != null) LogToFile(logPath, $"✅ ACCEPTED: Marker ID {id} at ({centerX:F1},{centerY:F1})");
				}
				else
				{
							if (logPath != null) LogToFile(logPath, $"❌ REJECTED: Marker ID {id} at ({centerX:F1},{centerY:F1}) - not in allowed range [0,1,2,3]");
						}
					}
				}
				
				if (logPath != null) LogToFile(logPath, $"STRICT FILTERING RESULT: {validIds.Count} valid markers with IDs: [{string.Join(", ", validIds)}] from {ids.Length} total detected");
				
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
			// COMPREHENSIVE MARKER PROCESSING LOGGING
			LogToFile(logPath, $"=== ADDING MARKER {markerId} ===");
			LogToFile(logPath, $"Input Quad Corners:");
			for (int i = 0; i < quad.Length; i++)
			{
				LogToFile(logPath, $"  Corner {i}: ({quad[i].X:F2}, {quad[i].Y:F2})");
			}
			
			// Calculate center of detected ArUco marker
			double cx = 0, cy = 0;
			for (int i = 0; i < 4; i++) { cx += quad[i].X; cy += quad[i].Y; }
			cx /= 4.0; cy /= 4.0;
			LogToFile(logPath, $"Calculated Center: ({cx:F2}, {cy:F2})");
			
			// Store color space coordinate
			lastDetectedCentersColor.Add(new System.Windows.Point(cx, cy));
			LogToFile(logPath, $"Stored in lastDetectedCentersColor. Count: {lastDetectedCentersColor.Count}");
			
			// CRITICAL: Ensure overlay is synchronized before drawing
			LogToFile(logPath, "Synchronizing overlay with camera feed...");
			SynchronizeOverlayWithCameraFeed();
			LogToFile(logPath, $"After sync - MarkersOverlay: {MarkersOverlay.ActualWidth}x{MarkersOverlay.ActualHeight}");
			
			// Transform to canvas coordinates with detailed logging
			var colorPt = new System.Windows.Point(cx, cy);
			var canvasPt = ColorToCanvas(colorPt, logPath, markerId);
			LogToFile(logPath, $"Coordinate Transformation: Camera({cx:F2},{cy:F2}) -> Canvas({canvasPt.X:F2},{canvasPt.Y:F2})");
			
			// Debug: Log canvas dimensions and marker position
			System.Diagnostics.Debug.WriteLine($"Drawing marker {markerId}: Camera({cx:F1},{cy:F1}) -> Canvas({canvasPt.X:F1},{canvasPt.Y:F1})");
			System.Diagnostics.Debug.WriteLine($"Canvas size: {MarkersOverlay.ActualWidth}x{MarkersOverlay.ActualHeight}");
			
			// Create red dot overlay with larger size for better visibility
			LogToFile(logPath, "Creating red dot ellipse...");
			var dot = new Ellipse { Width = 24, Height = 24, Fill = Brushes.Red, Stroke = Brushes.Yellow, StrokeThickness = 3 };
			
			// Ensure red dot is positioned within canvas bounds (accounting for dot size)
			// Use the coordinates directly from ColorToCanvas which already has proper bounds checking
			double dotLeft = canvasPt.X - 12;  // Center the 24px dot
			double dotTop = canvasPt.Y - 12;   // Center the 24px dot
			
			Canvas.SetLeft(dot, dotLeft);  // Center the 24px dot within bounds
			Canvas.SetTop(dot, dotTop);
			LogToFile(logPath, $"Red dot properties: Width={dot.Width}, Height={dot.Height}, Fill={dot.Fill}, Stroke={dot.Stroke}");
			LogToFile(logPath, $"Red dot position: Left={Canvas.GetLeft(dot):F2}, Top={Canvas.GetTop(dot):F2}");
			
			MarkersOverlay.Children.Add(dot);
			LogToFile(logPath, $"Red dot added to MarkersOverlay. Children count: {MarkersOverlay.Children.Count}");
			
			// Debug: Log that red dot was added
			System.Diagnostics.Debug.WriteLine($"Added red dot for marker {markerId} at canvas position ({canvasPt.X:F1},{canvasPt.Y:F1})");
			System.Diagnostics.Debug.WriteLine($"Canvas now has {MarkersOverlay.Children.Count} children");
			
			// Force the canvas to update
			LogToFile(logPath, "Forcing canvas layout update...");
			MarkersOverlay.UpdateLayout();
			LogToFile(logPath, "Canvas layout updated.");
			LogToFile(logPath, "");
			
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
			// CRITICAL FIX: The detected coordinates are from a vertically flipped image
			// Detection uses FlipMode.Y which ONLY flips Y coordinates: Y=0 becomes Y=height, Y=height becomes Y=0
			// The detection was designed to match the camera view, so X coordinates should be used directly
			// Canvas coordinate system: Y=0 at top, Y=height at bottom
			// So we need to flip Y back: Y_canvas = height - Y_detected
			// X coordinates should be used directly: X_canvas = X_detected (NO horizontal flip)
			int colorW = (kinectManager != null && kinectManager.ColorWidth > 0) ? kinectManager.ColorWidth : 1920;
			int colorH = (kinectManager != null && kinectManager.ColorHeight > 0) ? kinectManager.ColorHeight : 1080;
			
			// FIXED: Apply both horizontal and vertical flips to red dots to align with Aruco markers
			// 1. Flip X coordinate horizontally to correct rightward shift
			// 2. Flip Y coordinate vertically to correct downward shift (red dots below are for upper Aruco)
			System.Windows.Point cameraPoint = new System.Windows.Point(colorW - colorPoint.X, colorPoint.Y);
			
			// CRITICAL FIX: Use the MarkersOverlay size as the reference
			// Since CameraFeed and MarkersOverlay are in the same Grid cell, they should have the same size
			// The MarkersOverlay represents the actual coordinate space we need to map to
			double cameraFeedW = MarkersOverlay.ActualWidth;
			double cameraFeedH = MarkersOverlay.ActualHeight;
			
			// Log both sizes for debugging
			if (logPath != null && markerId >= 0)
			{
				LogToFile(logPath, $"  Camera Feed (actual): {CameraFeed.ActualWidth:F1}x{CameraFeed.ActualHeight:F1}");
				LogToFile(logPath, $"  MarkersOverlay (using): {cameraFeedW:F1}x{cameraFeedH:F1}");
				if (CameraFeed.ActualWidth <= 0 || CameraFeed.ActualHeight <= 0)
				{
					LogToFile(logPath, $"  ⚠️  NOTE: CameraFeed not sized, using MarkersOverlay as reference");
				}
			}
			
			// Log container dimensions for debugging
			if (logPath != null && markerId >= 0)
			{
				LogToFile(logPath, $"Marker {markerId} coordinate transformation:");
				LogToFile(logPath, $"  Original detected coordinates: ({colorPoint.X:F1}, {colorPoint.Y:F1})");
				LogToFile(logPath, $"  X-coordinate flip: {colorPoint.X:F1} -> {colorW - colorPoint.X:F1} (flipped horizontally for red dots)");
				LogToFile(logPath, $"  Y-coordinate (direct): {colorPoint.Y:F1} (NO vertical flip for red dots)");
				LogToFile(logPath, $"  Camera coordinates (X-flipped only): ({cameraPoint.X:F1}, {cameraPoint.Y:F1})");
				LogToFile(logPath, $"  Camera Feed (actual): {CameraFeed.ActualWidth:F1}x{CameraFeed.ActualHeight:F1}");
				LogToFile(logPath, $"  Camera Feed (using): {cameraFeedW:F1}x{cameraFeedH:F1}");
				LogToFile(logPath, $"  MarkersOverlay: {MarkersOverlay.ActualWidth:F1}x{MarkersOverlay.ActualHeight:F1}");
				LogToFile(logPath, $"  Camera resolution: {colorW}x{colorH}");
			}
			
			// Calculate uniform scaling to fit color image in camera feed (maintaining aspect ratio)
			double scale = Math.Min(cameraFeedW / colorW, cameraFeedH / colorH);
			
			// Calculate displayed image dimensions and centering offsets within camera feed
			double displayedW = colorW * scale;
			double displayedH = colorH * scale;
			double offsetX = (cameraFeedW - displayedW) / 2.0;
			double offsetY = (cameraFeedH - displayedH) / 2.0;
			
			// Transform camera coordinates to camera feed coordinates using uniform scaling
			double x = cameraPoint.X * scale + offsetX;
			double y = cameraPoint.Y * scale + offsetY;
			
			// NO FINE-TUNING: The coordinate transformation should be mathematically correct
			// The detected coordinates should map directly to the correct canvas positions
			
			// CRITICAL: Ensure coordinates are within the camera feed boundaries
			// Allow some margin for the 24px dot size
			x = Math.Max(12, Math.Min(cameraFeedW - 12, x));
			y = Math.Max(12, Math.Min(cameraFeedH - 12, y));
			
			// Detailed coordinate logging to file
			if (logPath != null && markerId >= 0)
			{
				LogToFile(logPath, $"  Scale factor: {scale:F4} (uniform scaling)");
				LogToFile(logPath, $"  Displayed image: {displayedW:F1}x{displayedH:F1}");
				LogToFile(logPath, $"  Camera feed offset: ({offsetX:F1}, {offsetY:F1})");
				LogToFile(logPath, $"  No fine-tuning - using pure coordinate transformation");
				LogToFile(logPath, $"  Final canvas coordinates: ({x:F1}, {y:F1})");
				LogToFile(logPath, $"  Camera feed bounds: {cameraFeedW:F1}x{cameraFeedH:F1}");
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
		
		private void TestMarker0Button_Click(object sender, RoutedEventArgs e)
		{
			try
			{
				TestMarker0Button.IsEnabled = false;
				TestMarker0Button.Content = "🔍 Testing...";
				
				// Run the test in a background thread to avoid blocking UI
				Task.Run(() => {
					try
					{
						PerformComprehensiveMarker0Test();
					}
					catch (Exception ex)
					{
						Dispatcher.Invoke(() => {
							StatusText.Text = $"Test Error: {ex.Message}";
							StatusText.Foreground = new SolidColorBrush(Colors.Red);
						});
					}
					finally
					{
						Dispatcher.Invoke(() => {
							TestMarker0Button.IsEnabled = true;
							TestMarker0Button.Content = "🔍 Test Marker ID 0 Only";
						});
					}
				});
			}
			catch (Exception ex)
			{
				StatusText.Text = $"Test Error: {ex.Message}";
				StatusText.Foreground = new SolidColorBrush(Colors.Red);
				TestMarker0Button.IsEnabled = true;
				TestMarker0Button.Content = "🔍 Test Marker ID 0 Only";
			}
		}
		
		private void PerformComprehensiveMarker0Test()
		{
			try
			{
				Dispatcher.Invoke(() => {
					StatusText.Text = "🔍 Starting comprehensive Marker ID 0 test...";
					StatusText.Foreground = new SolidColorBrush(Colors.Orange);
				});
				
				// Create diagnostic directory
				var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
				var diagnosticDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "KinectCalibrationDiagnostics", $"Marker0Test_{timestamp}");
				Directory.CreateDirectory(diagnosticDir);
				
				var logPath = System.IO.Path.Combine(diagnosticDir, "marker0_test_log.txt");
				
				LogToFile(logPath, "=== COMPREHENSIVE MARKER ID 0 TEST ===");
				LogToFile(logPath, $"Test started at: {DateTime.Now}");
				LogToFile(logPath, $"Test directory: {diagnosticDir}");
				LogToFile(logPath, "");
				
				// Step 1: Test the marker file directly
				LogToFile(logPath, "=== STEP 1: TESTING MARKER FILE DIRECTLY ===");
				TestMarkerFileDirectly(logPath, diagnosticDir);
				
				// Step 2: Test OpenCV dictionary capabilities
				LogToFile(logPath, "\n=== STEP 2: TESTING OPENCV DICTIONARY CAPABILITIES ===");
				TestOpenCVDictionaryCapabilities(logPath);
				
				// Step 3: Capture camera image and test detection
				LogToFile(logPath, "\n=== STEP 3: TESTING CAMERA CAPTURE AND DETECTION ===");
				TestCameraCaptureAndDetection(logPath, diagnosticDir);
				
				LogToFile(logPath, "\n=== TEST COMPLETED ===");
				LogToFile(logPath, $"Test completed at: {DateTime.Now}");
				
				Dispatcher.Invoke(() => {
					StatusText.Text = $"✅ Marker ID 0 test completed! Check: {diagnosticDir}";
					StatusText.Foreground = new SolidColorBrush(Colors.Green);
				});
			}
			catch (Exception ex)
			{
				LogToFile(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "KinectCalibrationDiagnostics", $"Marker0Test_{DateTime.Now:yyyyMMdd_HHmmss}", "marker0_test_log.txt"), $"CRITICAL ERROR: {ex.Message}\n{ex.StackTrace}");
				throw;
			}
		}
		
		private void TestMarkerFileDirectly(string logPath, string diagnosticDir)
		{
			try
			{
				LogToFile(logPath, "Testing marker file: Assets/my/aruco_4x4_50_id_0.png");
				
				var markerPath = "pack://application:,,,/Assets/my/aruco_4x4_50_id_0.png";
				var uri = new Uri(markerPath, UriKind.Absolute);
				var bitmap = new BitmapImage(uri);
				
				LogToFile(logPath, $"Marker file loaded: {bitmap.Width}x{bitmap.Height}, DPI: {bitmap.DpiX}x{bitmap.DpiY}");
				
				// Convert to byte array
				var encoder = new PngBitmapEncoder();
				encoder.Frames.Add(BitmapFrame.Create(bitmap));
				byte[] imageBytes;
				using (var stream = new MemoryStream())
				{
					encoder.Save(stream);
					imageBytes = stream.ToArray();
				}
				
				LogToFile(logPath, $"Marker file size: {imageBytes.Length} bytes");
				
				// Load with OpenCV
				using (var markerMat = Cv2.ImDecode(imageBytes, ImreadModes.Color))
				{
					LogToFile(logPath, $"OpenCV loaded: {markerMat.Width}x{markerMat.Height}, channels: {markerMat.Channels()}, type: {markerMat.Type()}");
					
					// Save the loaded image for inspection
					var loadedImagePath = System.IO.Path.Combine(diagnosticDir, "00_loaded_marker.png");
					Cv2.ImWrite(loadedImagePath, markerMat);
					LogToFile(logPath, $"Saved loaded marker to: {loadedImagePath}");
					
					// Test with Dict4X4_50
					using (var dict = CvAruco.GetPredefinedDictionary(PredefinedDictionaryName.Dict4X4_50))
					using (var gray = new Mat())
					{
						Cv2.CvtColor(markerMat, gray, ColorConversionCodes.BGR2GRAY);
						
						var parameters = new DetectorParameters();
						parameters.AdaptiveThreshWinSizeMin = 3;
						parameters.AdaptiveThreshWinSizeMax = 23;
						parameters.AdaptiveThreshWinSizeStep = 10;
						parameters.AdaptiveThreshConstant = 7;
						parameters.MinMarkerPerimeterRate = 0.01;
						parameters.MaxMarkerPerimeterRate = 8.0;
						parameters.PolygonalApproxAccuracyRate = 0.05;
						parameters.MinCornerDistanceRate = 0.01;
						parameters.MinDistanceToBorder = 1;
						parameters.MinMarkerDistanceRate = 0.01;
						parameters.CornerRefinementMethod = CornerRefineMethod.Subpix;
						parameters.CornerRefinementWinSize = 5;
						parameters.CornerRefinementMaxIterations = 30;
						parameters.CornerRefinementMinAccuracy = 0.1;
						parameters.MarkerBorderBits = 1;
						parameters.PerspectiveRemovePixelPerCell = 4;
						parameters.PerspectiveRemoveIgnoredMarginPerCell = 0.13;
						parameters.MaxErroneousBitsInBorderRate = 0.5;
						parameters.MinOtsuStdDev = 2.0;
						parameters.ErrorCorrectionRate = 0.3;
						
						Point2f[][] corners; int[] ids; Point2f[][] rejected;
						CvAruco.DetectMarkers(gray, dict, out corners, out ids, parameters, out rejected);
						
						if (ids != null && ids.Length > 0)
						{
							LogToFile(logPath, $"✅ DETECTED: ID {ids[0]} (rejected: {rejected?.Length ?? 0})");
							
							// Save detection result
							using (var result = markerMat.Clone())
							{
								CvAruco.DrawDetectedMarkers(result, corners, ids);
								var resultPath = System.IO.Path.Combine(diagnosticDir, "detected_marker_0.png");
								Cv2.ImWrite(resultPath, result);
								LogToFile(logPath, $"Saved detection result to: {resultPath}");
							}
						}
						else
						{
							LogToFile(logPath, $"❌ NOT DETECTED (rejected: {rejected?.Length ?? 0})");
						}
					}
				}
			}
			catch (Exception ex)
			{
				LogToFile(logPath, $"ERROR in TestMarkerFileDirectly: {ex.Message}");
			}
		}
		
		private void TestOpenCVDictionaryCapabilities(string logPath)
		{
			try
			{
				LogToFile(logPath, "Testing OpenCV ArUco dictionary capabilities...");
				
				// Test all available dictionaries
				var allDictionaries = new[]
				{
					PredefinedDictionaryName.Dict4X4_50,
					PredefinedDictionaryName.Dict4X4_100,
					PredefinedDictionaryName.Dict4X4_250,
					PredefinedDictionaryName.Dict4X4_1000,
					PredefinedDictionaryName.Dict5X5_50,
					PredefinedDictionaryName.Dict5X5_100,
					PredefinedDictionaryName.Dict5X5_250,
					PredefinedDictionaryName.Dict5X5_1000,
					PredefinedDictionaryName.Dict6X6_50,
					PredefinedDictionaryName.Dict6X6_100,
					PredefinedDictionaryName.Dict6X6_250,
					PredefinedDictionaryName.Dict6X6_1000,
					PredefinedDictionaryName.Dict7X7_50,
					PredefinedDictionaryName.Dict7X7_100,
					PredefinedDictionaryName.Dict7X7_250,
					PredefinedDictionaryName.Dict7X7_1000
				};
				
				foreach (var dictName in allDictionaries)
				{
					try
					{
						using (var dict = CvAruco.GetPredefinedDictionary(dictName))
						{
							LogToFile(logPath, $"✅ {dictName}: Available (size: {dict.MarkerSize})");
						}
					}
					catch (Exception ex)
					{
						LogToFile(logPath, $"❌ {dictName}: Error - {ex.Message}");
					}
				}
			}
			catch (Exception ex)
			{
				LogToFile(logPath, $"ERROR in TestOpenCVDictionaryCapabilities: {ex.Message}");
			}
		}
		
		private void TestCameraCaptureAndDetection(string logPath, string diagnosticDir)
		{
			try
			{
				LogToFile(logPath, "Testing camera capture and detection...");
				
				if (kinectManager == null)
				{
					LogToFile(logPath, "❌ KinectManager is null - cannot capture camera image");
					return;
				}
				
				// Capture current camera frame
				byte[] colorData;
				int width, height, stride;
				if (!kinectManager.TryGetColorFrameRaw(out colorData, out width, out height, out stride))
				{
					LogToFile(logPath, "❌ No color frame available from Kinect");
					return;
				}
				
				LogToFile(logPath, $"Captured color frame: {width}x{height}, stride: {stride}");
				
				// Convert to OpenCV Mat using a simpler approach
				using (var bgr = new Mat(height, width, MatType.CV_8UC3))
				{
					// Copy data manually
					for (int y = 0; y < height; y++)
					{
						for (int x = 0; x < width; x++)
						{
							int srcIndex = y * stride + x * 4; // BGRA format
							int dstIndex = y * width * 3 + x * 3; // BGR format
							
							if (srcIndex + 2 < colorData.Length && dstIndex + 2 < bgr.Total() * 3)
							{
								bgr.Set(y, x, new Vec3b(colorData[srcIndex], colorData[srcIndex + 1], colorData[srcIndex + 2]));
							}
						}
					}
					
					// Save original image
					var originalPath = System.IO.Path.Combine(diagnosticDir, "01_camera_original.png");
					Cv2.ImWrite(originalPath, bgr);
					LogToFile(logPath, $"Saved original camera image to: {originalPath}");
					
					// CRITICAL FIX: Mirror the image horizontally to match projected markers
					using (var mirrored = new Mat())
					{
						Cv2.Flip(bgr, mirrored, FlipMode.X); // Horizontal flip (mirror)
						
						// Save mirrored image
						var mirroredPath = System.IO.Path.Combine(diagnosticDir, "01_camera_mirrored.png");
						Cv2.ImWrite(mirroredPath, mirrored);
						LogToFile(logPath, $"Saved mirrored camera image to: {mirroredPath}");
						
						// Test detection on mirrored image (this should work!)
						TestDetectionOnImage(mirrored, "Mirrored Camera Image", logPath, diagnosticDir);
					}
					
					// Also test original for comparison
					TestDetectionOnImage(bgr, "Original Camera Image", logPath, diagnosticDir);
				}
			}
			catch (Exception ex)
			{
				LogToFile(logPath, $"ERROR in TestCameraCaptureAndDetection: {ex.Message}");
			}
		}
		
		private void TestDetectionOnImage(Mat image, string imageName, string logPath, string diagnosticDir)
		{
			try
			{
				LogToFile(logPath, $"\n--- Testing detection on {imageName} ---");
				LogToFile(logPath, $"Image: {image.Width}x{image.Height}, channels: {image.Channels()}, type: {image.Type()}");
				
				using (var gray = new Mat())
				{
					Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY);
					
					// Test with Dict4X4_50 only (our target)
					using (var dict = CvAruco.GetPredefinedDictionary(PredefinedDictionaryName.Dict4X4_50))
					{
						var parameters = new DetectorParameters();
						parameters.AdaptiveThreshWinSizeMin = 3;
						parameters.AdaptiveThreshWinSizeMax = 23;
						parameters.AdaptiveThreshWinSizeStep = 10;
						parameters.AdaptiveThreshConstant = 7;
						parameters.MinMarkerPerimeterRate = 0.01;
						parameters.MaxMarkerPerimeterRate = 8.0;
						parameters.PolygonalApproxAccuracyRate = 0.05;
						parameters.MinCornerDistanceRate = 0.01;
						parameters.MinDistanceToBorder = 1;
						parameters.MinMarkerDistanceRate = 0.01;
						parameters.CornerRefinementMethod = CornerRefineMethod.Subpix;
						parameters.CornerRefinementWinSize = 5;
						parameters.CornerRefinementMaxIterations = 30;
						parameters.CornerRefinementMinAccuracy = 0.1;
						parameters.MarkerBorderBits = 1;
						parameters.PerspectiveRemovePixelPerCell = 4;
						parameters.PerspectiveRemoveIgnoredMarginPerCell = 0.13;
						parameters.MaxErroneousBitsInBorderRate = 0.5;
						parameters.MinOtsuStdDev = 2.0;
						parameters.ErrorCorrectionRate = 0.3;
						
						Point2f[][] corners; int[] ids; Point2f[][] rejected;
						CvAruco.DetectMarkers(gray, dict, out corners, out ids, parameters, out rejected);
						
						LogToFile(logPath, $"Detection results:");
						LogToFile(logPath, $"  Found: {ids?.Length ?? 0} markers");
						LogToFile(logPath, $"  Rejected: {rejected?.Length ?? 0} candidates");
						
						if (ids != null && ids.Length > 0)
						{
							LogToFile(logPath, $"  Detected IDs: [{string.Join(", ", ids)}]");
							
							// Check if ID 0 is detected
							bool foundId0 = ids.Contains(0);
							LogToFile(logPath, $"  Found ID 0: {(foundId0 ? "✅ YES" : "❌ NO")}");
							
							// Save detection result
							using (var result = image.Clone())
							{
								CvAruco.DrawDetectedMarkers(result, corners, ids);
								var resultPath = System.IO.Path.Combine(diagnosticDir, $"02_detection_{imageName.Replace(" ", "_")}.png");
								Cv2.ImWrite(resultPath, result);
								LogToFile(logPath, $"  Saved detection result to: {resultPath}");
							}
						}
						else
						{
							LogToFile(logPath, "  ❌ No markers detected");
						}
					}
				}
			}
			catch (Exception ex)
			{
				LogToFile(logPath, $"ERROR in TestDetectionOnImage: {ex.Message}");
			}
		}
		
		private void CalculateAndSaveTouchArea(Point2f[][] corners, int[] ids, string logPath)
		{
			try
			{
				if (logPath != null) LogToFile(logPath, "\n=== CALCULATING TOUCH DETECTION AREA ===");
				
				// Create a dictionary to map marker IDs to their centers
				var markerCenters = new Dictionary<int, System.Windows.Point>();
				
				for (int i = 0; i < corners.Length && i < ids.Length; i++)
				{
					// Calculate center of this marker
					double cx = 0, cy = 0;
					for (int j = 0; j < 4; j++) 
					{ 
						cx += corners[i][j].X; 
						cy += corners[i][j].Y; 
					}
					cx /= 4.0; 
					cy /= 4.0;
					
					markerCenters[ids[i]] = new System.Windows.Point(cx, cy);
					
					if (logPath != null) LogToFile(logPath, $"Marker ID {ids[i]} center: ({cx:F1}, {cy:F1})");
				}
				
				// Ensure we have all 4 markers
				if (markerCenters.Count == 4 && markerCenters.ContainsKey(0) && markerCenters.ContainsKey(1) && 
				    markerCenters.ContainsKey(2) && markerCenters.ContainsKey(3))
				{
					// Get the 4 corner points (assuming standard layout: 0=top-left, 1=top-right, 2=bottom-right, 3=bottom-left)
					var topLeft = markerCenters[0];
					var topRight = markerCenters[1];
					var bottomRight = markerCenters[2];
					var bottomLeft = markerCenters[3];
					
					// Calculate the bounding rectangle
					double minX = Math.Min(Math.Min(topLeft.X, topRight.X), Math.Min(bottomLeft.X, bottomRight.X));
					double maxX = Math.Max(Math.Max(topLeft.X, topRight.X), Math.Max(bottomLeft.X, bottomRight.X));
					double minY = Math.Min(Math.Min(topLeft.Y, topRight.Y), Math.Min(bottomLeft.Y, bottomRight.Y));
					double maxY = Math.Max(Math.Max(topLeft.Y, topRight.Y), Math.Max(bottomLeft.Y, bottomRight.Y));
					
					// Create the touch area rectangle
					var touchArea = new System.Windows.Rect(minX, minY, maxX - minX, maxY - minY);
					
					if (logPath != null)
					{
						LogToFile(logPath, $"Touch area rectangle: X={touchArea.X:F1}, Y={touchArea.Y:F1}, Width={touchArea.Width:F1}, Height={touchArea.Height:F1}");
						LogToFile(logPath, $"Touch area corners:");
						LogToFile(logPath, $"  Top-Left: ({touchArea.Left:F1}, {touchArea.Top:F1})");
						LogToFile(logPath, $"  Top-Right: ({touchArea.Right:F1}, {touchArea.Top:F1})");
						LogToFile(logPath, $"  Bottom-Left: ({touchArea.Left:F1}, {touchArea.Bottom:F1})");
						LogToFile(logPath, $"  Bottom-Right: ({touchArea.Right:F1}, {touchArea.Bottom:F1})");
					}
					
					// Save the touch area to calibration config
					SaveTouchAreaToCalibrationConfig(touchArea, markerCenters, ids, logPath);
					
					// Draw the touch area rectangle on the overlay
					DrawTouchAreaRectangle(touchArea, logPath);
					
					// Update status
					Dispatcher.Invoke(() => {
						StatusText.Text = $"Status: Touch area calculated and saved to system! Area: {touchArea.Width:F0}x{touchArea.Height:F0} pixels";
						StatusText.Foreground = Brushes.Green;
					});
				}
				else
				{
					if (logPath != null) LogToFile(logPath, "❌ Cannot calculate touch area - missing some marker IDs");
				}
			}
			catch (Exception ex)
			{
				if (logPath != null) LogToFile(logPath, $"ERROR calculating touch area: {ex.Message}");
			}
		}
		
		private void SaveTouchAreaToCalibrationConfig(System.Windows.Rect touchArea, Dictionary<int, System.Windows.Point> markerCenters, int[] ids, string logPath)
		{
			try
			{
				// Create touch area definition
				var touchAreaDef = new TouchAreaDefinition(
					touchArea.X, 
					touchArea.Y, 
					touchArea.Width, 
					touchArea.Height,
					kinectManager?.ColorWidth ?? 1920,
					kinectManager?.ColorHeight ?? 1080
				);
				
				// Update calibration config
				calibrationConfig.TouchArea = touchAreaDef;
				calibrationConfig.TouchAreaCalculatedUtc = DateTime.UtcNow;
				
				// Store ArUco marker centers and IDs
				calibrationConfig.ArUcoMarkerCenters.Clear();
				calibrationConfig.ArUcoMarkerIds.Clear();
				
				foreach (var kvp in markerCenters.OrderBy(x => x.Key))
				{
					calibrationConfig.ArUcoMarkerCenters.Add(kvp.Value);
					calibrationConfig.ArUcoMarkerIds.Add(kvp.Key);
				}
				
				// Save to system calibration file
				CalibrationStorage.Save(calibrationConfig);
				
				if (logPath != null) 
				{
					LogToFile(logPath, $"✅ Touch area saved to calibration config:");
					LogToFile(logPath, $"  - Touch Area: X={touchAreaDef.X:F1}, Y={touchAreaDef.Y:F1}, W={touchAreaDef.Width:F1}, H={touchAreaDef.Height:F1}");
					LogToFile(logPath, $"  - ArUco Markers: {string.Join(", ", calibrationConfig.ArUcoMarkerIds)}");
					LogToFile(logPath, $"  - Camera Resolution: {touchAreaDef.CameraWidth}x{touchAreaDef.CameraHeight}");
					LogToFile(logPath, $"  - Saved to: {System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "calibration.json")}");
				}
			}
			catch (Exception ex)
			{
				if (logPath != null) LogToFile(logPath, $"ERROR saving touch area to calibration config: {ex.Message}");
			}
		}
		
		private void DrawTouchAreaRectangle(System.Windows.Rect touchArea, string logPath)
		{
			try
			{
				// COMPREHENSIVE RECTANGLE DRAWING LOGGING
				LogToFile(logPath, "=== DRAWING TOUCH AREA RECTANGLE ===");
				LogToFile(logPath, $"Input touch area: X={touchArea.X:F2}, Y={touchArea.Y:F2}, Width={touchArea.Width:F2}, Height={touchArea.Height:F2}");
				LogToFile(logPath, $"Before rectangle - MarkersOverlay Children Count: {MarkersOverlay.Children.Count}");
				
				// Convert camera coordinates to canvas coordinates
				var topLeftCanvas = ColorToCanvas(new System.Windows.Point(touchArea.Left, touchArea.Top));
				var topRightCanvas = ColorToCanvas(new System.Windows.Point(touchArea.Right, touchArea.Top));
				var bottomLeftCanvas = ColorToCanvas(new System.Windows.Point(touchArea.Left, touchArea.Bottom));
				var bottomRightCanvas = ColorToCanvas(new System.Windows.Point(touchArea.Right, touchArea.Bottom));
				
				LogToFile(logPath, $"Canvas coordinates:");
				LogToFile(logPath, $"  Top-Left: ({topLeftCanvas.X:F2}, {topLeftCanvas.Y:F2})");
				LogToFile(logPath, $"  Top-Right: ({topRightCanvas.X:F2}, {topRightCanvas.Y:F2})");
				LogToFile(logPath, $"  Bottom-Left: ({bottomLeftCanvas.X:F2}, {bottomLeftCanvas.Y:F2})");
				LogToFile(logPath, $"  Bottom-Right: ({bottomRightCanvas.X:F2}, {bottomRightCanvas.Y:F2})");
				
				// Create rectangle outline
				var rectangle = new System.Windows.Shapes.Rectangle
				{
					Width = topRightCanvas.X - topLeftCanvas.X,
					Height = bottomLeftCanvas.Y - topLeftCanvas.Y,
					Stroke = Brushes.Cyan,
					StrokeThickness = 3,
					Fill = Brushes.Transparent,
					StrokeDashArray = new DoubleCollection { 5, 5 } // Dashed line
				};
				
				Canvas.SetLeft(rectangle, topLeftCanvas.X);
				Canvas.SetTop(rectangle, topLeftCanvas.Y);
				
				LogToFile(logPath, $"Rectangle properties:");
				LogToFile(logPath, $"  Width: {rectangle.Width:F2}");
				LogToFile(logPath, $"  Height: {rectangle.Height:F2}");
				LogToFile(logPath, $"  Position: Left={Canvas.GetLeft(rectangle):F2}, Top={Canvas.GetTop(rectangle):F2}");
				LogToFile(logPath, $"  Stroke: {rectangle.Stroke}");
				LogToFile(logPath, $"  Fill: {rectangle.Fill}");
				
				// Add to overlay
				MarkersOverlay.Children.Add(rectangle);
				LogToFile(logPath, $"Rectangle added to MarkersOverlay. Children count: {MarkersOverlay.Children.Count}");
				
				// Add corner markers
				var cornerSize = 12;
				var corners = new[]
				{
					new { Point = topLeftCanvas, Label = "TL" },
					new { Point = topRightCanvas, Label = "TR" },
					new { Point = bottomLeftCanvas, Label = "BL" },
					new { Point = bottomRightCanvas, Label = "BR" }
				};
				
				foreach (var corner in corners)
				{
					var cornerDot = new Ellipse
					{
						Width = cornerSize,
						Height = cornerSize,
						Fill = Brushes.Cyan,
						Stroke = Brushes.Black,
						StrokeThickness = 2
					};
					
					Canvas.SetLeft(cornerDot, corner.Point.X - cornerSize / 2);
					Canvas.SetTop(cornerDot, corner.Point.Y - cornerSize / 2);
					MarkersOverlay.Children.Add(cornerDot);
					
					// Add label
					var label = new TextBlock
					{
						Text = corner.Label,
						Foreground = Brushes.Cyan,
						FontWeight = FontWeights.Bold,
						FontSize = 12
					};
					
					Canvas.SetLeft(label, corner.Point.X + cornerSize / 2 + 2);
					Canvas.SetTop(label, corner.Point.Y - cornerSize / 2);
					MarkersOverlay.Children.Add(label);
				}
			}
			catch (Exception ex)
			{
				// Log error but don't crash
				System.Diagnostics.Debug.WriteLine($"Error drawing touch area rectangle: {ex.Message}");
			}
		}
		
		private void DrawRectangleFromRedDots()
		{
			try
			{
				System.Diagnostics.Debug.WriteLine($"DrawRectangleFromRedDots called - lastDetectedCentersColor.Count: {lastDetectedCentersColor.Count}");
				
				if (lastDetectedCentersColor.Count != 4) 
				{
					System.Diagnostics.Debug.WriteLine("Not drawing rectangle - need exactly 4 red dots");
					return;
				}
				
				// Convert the red dot positions to canvas coordinates
				var canvasPoints = new System.Windows.Point[4];
				for (int i = 0; i < 4; i++)
				{
					canvasPoints[i] = ColorToCanvas(lastDetectedCentersColor[i]);
					System.Diagnostics.Debug.WriteLine($"Red dot {i}: Camera({lastDetectedCentersColor[i].X:F1},{lastDetectedCentersColor[i].Y:F1}) -> Canvas({canvasPoints[i].X:F1},{canvasPoints[i].Y:F1})");
				}
				
				// Create lines connecting the red dots to form a rectangle
				for (int i = 0; i < 4; i++)
				{
					var start = canvasPoints[i];
					var end = canvasPoints[(i + 1) % 4];
					
					var line = new System.Windows.Shapes.Line
					{
						X1 = start.X,
						Y1 = start.Y,
						X2 = end.X,
						Y2 = end.Y,
						Stroke = Brushes.Cyan,
						StrokeThickness = 5, // Make it thicker for visibility
						StrokeDashArray = new DoubleCollection { 8, 4 } // Make dashes more visible
					};
					
					MarkersOverlay.Children.Add(line);
					System.Diagnostics.Debug.WriteLine($"Added line {i}: ({start.X:F1},{start.Y:F1}) -> ({end.X:F1},{end.Y:F1})");
				}
				
				System.Diagnostics.Debug.WriteLine($"Rectangle drawn connecting red dots - Total children: {MarkersOverlay.Children.Count}");
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"Error drawing rectangle from red dots: {ex.Message}");
			}
		}
	}
}
