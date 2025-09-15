using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Kinect;
using KinectCalibrationWPF.Models;
using KinectCalibrationWPF.Services;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace KinectCalibrationWPF.CalibrationWizard
{
	public partial class Screen3_TouchTest : Window
	{
		private KinectManager.KinectManager kinectManager;
		private DispatcherTimer updateTimer;
		private CalibrationConfig calibration;
		private List<TouchPoint> activeTouches = new List<TouchPoint>();
		private double currentTouchSize = 30;
		private double touchAreaXOffset = 0;
		private double touchAreaYOffset = 0;
		private bool showWallProjection = false;
		private bool showTouchDetection = false;
		private List<Point> detectedTouchPixels = new List<Point>();
		
		// BACKGROUND SUBTRACTION for robust touch detection
		private ushort[] backgroundDepthFrame = null;
		private bool backgroundCaptured = false;
		private DateTime lastBackgroundCapture = DateTime.MinValue;
		
		// DEPTH CAMERA VIEW CONTROLS (like in video reference)
		private double depthCameraXOffset = 0.0; // Move depth camera view left/right
		private double depthCameraYOffset = 0.0; // Move depth camera view up/down
		private double depthCameraZoom = 1.0; // Zoom in/out depth camera view
		
		private class TouchPoint
		{
			public Point Position { get; set; }
			public DateTime LastSeen { get; set; }
			public double Depth { get; set; }
			public Rectangle VisualElement { get; set; }
			public int Area { get; set; }
		}

		private class Blob { public Point Center; public int Area; }

		// Plane cache (normalized and oriented toward camera)
		private bool isPlaneValid = false;
		private double planeNx = 0, planeNy = 0, planeNz = 0, planeD = 0;

		// Unified view transform for depth image and overlay
		private TransformGroup depthViewTransformGroup;
		private ScaleTransform depthScaleTransform;
		private ScaleTransform depthFlipTransform;
		private TranslateTransform depthTranslateTransform;

		private int minBlobAreaPoints = 40;
		private double smoothingAlpha = 0.30;

		public Screen3_TouchTest(KinectManager.KinectManager manager, CalibrationConfig config)
		{
			try
			{
				// Step 1: Initialize XAML components first
				InitializeComponent();
				
				// Step 1.5: Subscribe to Loaded event to ensure UI is ready
				this.Loaded += Screen3_TouchTest_Loaded;
				
				// Step 1.6: Fix depth camera display positioning
				FixDepthCameraDisplay();
				
				// Slider initialization moved to Loaded event handler
				
				// Step 2: Set basic properties
				kinectManager = manager;
				
				// CRITICAL FIX: Load latest calibration from disk
				try
				{
					var latestCalibration = CalibrationStorage.Load();
					if (latestCalibration != null && latestCalibration.TouchArea != null && 
						latestCalibration.TouchArea.Width > 0 && latestCalibration.TouchArea.Height > 0)
					{
						calibration = latestCalibration;
						LogToFile(GetDiagnosticPath(), $"SUCCESS: Loaded TouchArea from disk: X={calibration.TouchArea.X:F1}, Y={calibration.TouchArea.Y:F1}, W={calibration.TouchArea.Width:F1}, H={calibration.TouchArea.Height:F1}");
						
						// ENHANCED: Validate plane data
						if (calibration.Plane != null && 
							Math.Abs(calibration.Plane.Nx) > 0.001 && 
							Math.Abs(calibration.Plane.Ny) > 0.001 && 
							Math.Abs(calibration.Plane.Nz) > 0.001)
						{
							LogToFile(GetDiagnosticPath(), $"SUCCESS: Valid plane data loaded: N=({calibration.Plane.Nx:F6}, {calibration.Plane.Ny:F6}, {calibration.Plane.Nz:F6}), D={calibration.Plane.D:F6}");
						}
						else
						{
							LogToFile(GetDiagnosticPath(), "WARNING: Plane data is invalid or missing - will use fallback detection");
						}
					}
					else
					{
						calibration = config ?? new CalibrationConfig();
						LogToFile(GetDiagnosticPath(), "WARNING: No valid TouchArea found, using provided config");
					}
				}
				catch (Exception ex)
				{
					LogToFile(GetDiagnosticPath(), $"ERROR loading calibration: {ex.Message}");
					calibration = config ?? new CalibrationConfig();
				}
				
				// Step 3: Initialize diagnostics (but don't access UI elements yet)
				InitializeScreen3Diagnostics();
				
				// Step 4: Validate calibration data
				ValidateCalibrationData();
				
				// Step 5: Initialize UI elements safely (moved to Loaded event)
				// InitializeUIElements(); // Moved to Loaded event
				
				// Step 6: Start the update timer
				InitializeUpdateTimer();
				// Initialize unified view transform and normalize/orient plane
				InitializeUnifiedViewTransform();
				NormalizeAndOrientPlane();
				
				LogToFile(GetDiagnosticPath(), "Screen 3 initialized successfully");
			}
			catch (Exception ex)
			{
				// Log the error and show user-friendly message
				var errorMsg = $"Screen 3 initialization failed: {ex.Message}";
				System.Diagnostics.Debug.WriteLine(errorMsg);
				System.Diagnostics.Debug.WriteLine($"Stack Trace: {ex.StackTrace}");
				
				// Try to log to file if possible
				try
				{
					LogToFile(GetDiagnosticPath(), errorMsg);
					LogToFile(GetDiagnosticPath(), $"Stack Trace: {ex.StackTrace}");
				}
				catch { }
				
				MessageBox.Show(errorMsg, "Initialization Error", MessageBoxButton.OK, MessageBoxImage.Error);
				throw;
			}
		}
		
		private void Screen3_TouchTest_Loaded(object sender, RoutedEventArgs e)
		{
			try
			{
				// Initialize UI elements after the window is fully loaded
				InitializeUIElements();
				
				// Initialize sliders after UI is fully loaded
				InitializeSliders();
				
				LogToFile(GetDiagnosticPath(), "UI elements and sliders initialized after Loaded event");
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in Screen3_TouchTest_Loaded: {ex.Message}");
			}
		}
		
		private void InitializeSliders()
		{
			try
			{
				if (PlaneThresholdSlider != null)
				{
					PlaneThresholdSlider.Minimum = 5;
					PlaneThresholdSlider.Maximum = 150;
					PlaneThresholdSlider.Value = 10;
					UpdateThresholdDisplay();
				}
				
				if (MinBlobAreaSlider != null)
				{
					MinBlobAreaSlider.Minimum = 5;
					MinBlobAreaSlider.Maximum = 100;
					MinBlobAreaSlider.Value = 50;
				}
				
				if (TouchAreaXOffsetSlider != null)
				{
					TouchAreaXOffsetSlider.Minimum = -100;
					TouchAreaXOffsetSlider.Maximum = 100;
					TouchAreaXOffsetSlider.Value = 0;
				}
				
				if (TouchAreaYOffsetSlider != null)
				{
					TouchAreaYOffsetSlider.Minimum = -100;
					TouchAreaYOffsetSlider.Maximum = 100;
					TouchAreaYOffsetSlider.Value = 0;
				}
				
				if (DepthCameraZoomSlider != null)
				{
					DepthCameraZoomSlider.Minimum = 0.5;
					DepthCameraZoomSlider.Maximum = 2.0;
					DepthCameraZoomSlider.Value = 1.0;
				}
				
				LogToFile(GetDiagnosticPath(), "Sliders initialized successfully");
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in InitializeSliders: {ex.Message}");
			}
		}
		
		private void InitializeUIElements()
		{
			try
			{
				// Check if UI elements exist before accessing them
				if (PlaneThresholdSlider != null)
				{
					PlaneThresholdSlider.Value = calibration?.PlaneThresholdMeters ?? 0.01;
				}
				
				// Load offset values from calibration if available
				if (calibration?.TouchDetectionSettings != null)
				{
					if (calibration.TouchDetectionSettings.ContainsKey("TouchAreaXOffset"))
					{
						touchAreaXOffset = Convert.ToDouble(calibration.TouchDetectionSettings["TouchAreaXOffset"]);
					}
					if (calibration.TouchDetectionSettings.ContainsKey("TouchAreaYOffset"))
					{
						touchAreaYOffset = Convert.ToDouble(calibration.TouchDetectionSettings["TouchAreaYOffset"]);
					}
				}
				
				// Update display values
				UpdateThresholdDisplay();
				NormalizeAndOrientPlane();
				
				LogToFile(GetDiagnosticPath(), "UI elements initialized successfully");
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in InitializeUIElements: {ex.Message}");
				throw;
			}
		}
		
		private void InitializeUpdateTimer()
		{
			try
			{
				updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) }; // ~60 FPS for real-time feel
			updateTimer.Tick += UpdateTimer_Tick;
			updateTimer.Start();
				
				LogToFile(GetDiagnosticPath(), "Update timer initialized successfully at 60 FPS");
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in InitializeUpdateTimer: {ex.Message}");
				throw;
			}
		}

		private void UpdateTimer_Tick(object sender, EventArgs e)
		{
			try
			{
				// Update depth visualization
				UpdateDepthVisualization();
				
				// Perform advanced touch detection
				PerformTouchDetection();
				
				// Update visual feedback
				UpdateVisualFeedback();
				
				// Update status information
				UpdateStatusInformation();
				
				// Log periodic diagnostics (every 5 seconds)
				if (DateTime.Now.Second % 5 == 0 && DateTime.Now.Millisecond < 100)
				{
					LogTouchDetectionDiagnostic();
				}
			}
			catch (Exception ex)
			{
				var errorMsg = $"UpdateTimer_Tick Error: {ex.Message}";
				StatusText.Text = errorMsg;
				LogToFile(GetDiagnosticPath(), errorMsg);
				LogToFile(GetDiagnosticPath(), $"Stack Trace: {ex.StackTrace}");
			}
		}
		
		private void UpdateDepthVisualization()
		{
			try
			{
				// Get depth frame
				ushort[] depthData;
				int width, height;
				if (!kinectManager.TryGetDepthFrameRaw(out depthData, out width, out height))
				{
					StatusText.Text = "No depth data available";
					return;
				}

				// Create depth bitmap with proper visualization
				var depthBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
				var pixels = new byte[width * height * 4];
				
				// Get calibrated wall distance for proper visualization
				double wallDistance = calibration?.KinectToSurfaceDistanceMeters ?? 2.5;
				double minDepth = Math.Max(0.5, wallDistance - 0.5); // 50cm before wall
				double maxDepth = Math.Min(8.0, wallDistance + 0.5); // 50cm after wall
				
				// Pre-calculate thresholds for faster processing
				ushort minDepthRaw = (ushort)(minDepth * 1000);
				ushort maxDepthRaw = (ushort)(maxDepth * 1000);
				ushort wallDepthMin = (ushort)((wallDistance - 0.02) * 1000);
				ushort wallDepthMax = (ushort)((wallDistance + 0.02) * 1000);
				
				// Optimized pixel processing with reduced calculations
				for (int i = 0; i < depthData.Length; i++)
				{
					ushort depth = depthData[i];
					byte intensity = 0;
					
					if (depth != 0)
					{
						// Fast depth range check
						if (depth < minDepthRaw)
							intensity = 255; // Very close = white
						else if (depth > maxDepthRaw)
							intensity = 0; // Far = black
						else
						{
							// Simplified intensity calculation
							double depthInMeters = depth / 1000.0;
							double normalized = (depthInMeters - minDepth) / (maxDepth - minDepth);
							intensity = (byte)(255 * (1.0 - normalized));
						}
						
						// Fast wall highlight check
						if (depth >= wallDepthMin && depth <= wallDepthMax)
						{
							pixels[i * 4] = 0;     // Blue
							pixels[i * 4 + 1] = intensity; // Green
							pixels[i * 4 + 2] = 255;   // Red (makes it purple-ish)
							pixels[i * 4 + 3] = 255;
						}
						else
						{
							// Proper grayscale mapping (BGR format)
							pixels[i * 4] = intensity;     // Blue
							pixels[i * 4 + 1] = intensity; // Green
							pixels[i * 4 + 2] = intensity; // Red
							pixels[i * 4 + 3] = 255;       // Alpha
						}
					}
				}
				
				depthBitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
				
				// Update the image
				DepthImage.Source = depthBitmap;
				
				// FIX: Ensure proper canvas sizing
				if (OverlayCanvas != null)
				{
					OverlayCanvas.Width = width;
					OverlayCanvas.Height = height;
				}
				
				StatusText.Text = $"Depth feed active (Wall: {wallDistance:F2}m)";
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in UpdateDepthVisualization: {ex.Message}");
				StatusText.Text = "Error updating depth feed";
			}
		}
		
		private void InitializeUnifiedViewTransform()
		{
			try
			{
				depthViewTransformGroup = new TransformGroup();
				depthScaleTransform = new ScaleTransform(1.0, 1.0);
				depthFlipTransform = new ScaleTransform(1.0, -1.0); // Flip vertically by default
				depthTranslateTransform = new TranslateTransform(0, 0);
				depthViewTransformGroup.Children.Add(depthScaleTransform);
				depthViewTransformGroup.Children.Add(depthFlipTransform);
				depthViewTransformGroup.Children.Add(depthTranslateTransform);
				if (DepthViewContainer != null)
				{
					DepthViewContainer.RenderTransform = depthViewTransformGroup;
				}
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in InitializeUnifiedViewTransform: {ex.Message}");
			}
		}

		private void UpdateUnifiedViewTransform()
		{
			try
			{
				if (depthScaleTransform != null)
				{
					depthScaleTransform.ScaleX = depthCameraZoom;
					depthScaleTransform.ScaleY = depthCameraZoom;
				}
				if (depthFlipTransform != null)
				{
					bool flipEnabled = FlipDepthVerticallyCheckBox?.IsChecked == true;
					depthFlipTransform.ScaleY = flipEnabled ? -1.0 : 1.0;
				}
				if (depthTranslateTransform != null)
				{
					depthTranslateTransform.X = depthCameraXOffset;
					depthTranslateTransform.Y = depthCameraYOffset;
				}
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in UpdateUnifiedViewTransform: {ex.Message}");
			}
		}
		
		private void PerformTouchDetection()
		{
			try
			{
				if (kinectManager == null)
				{
					DetectionStatusText.Text = "KinectManager is null";
					LogToFile(GetDiagnosticPath(), "ERROR: KinectManager is null in PerformTouchDetection");
					return;
				}
				
				if (!kinectManager.IsInitialized)
				{
					DetectionStatusText.Text = "Kinect not initialized";
					LogToFile(GetDiagnosticPath(), "WARNING: Kinect not initialized in PerformTouchDetection");
					return;
				}
				
				// Clear old touches that are no longer active
				var currentTime = DateTime.Now;
				var removedCount = activeTouches.RemoveAll(touch => (currentTime - touch.LastSeen).TotalMilliseconds > 200);
				if (removedCount > 0)
				{
					LogToFile(GetDiagnosticPath(), $"Removed {removedCount} expired touches");
				}
				
				// Get depth data for touch detection
				ushort[] depthData;
				int width, height;
				if (kinectManager.TryGetDepthFrameRaw(out depthData, out width, out height))
				{
					LogToFile(GetDiagnosticPath(), $"Depth data: {width}x{height}, {depthData?.Length ?? 0} pixels");
					DetectTouchesInDepthData(depthData, width, height);
				}
				else
				{
					LogToFile(GetDiagnosticPath(), "WARNING: Failed to get depth frame data");
				}
			}
			catch (Exception ex)
			{
				var errorMsg = $"PerformTouchDetection Error: {ex.Message}";
				DetectionStatusText.Text = errorMsg;
				LogToFile(GetDiagnosticPath(), errorMsg);
				LogToFile(GetDiagnosticPath(), $"Stack Trace: {ex.StackTrace}");
			}
		}
		
		// OPTIMIZED TOUCH DETECTION METHOD
		private void DetectTouchesInDepthData(ushort[] depthData, int width, int height)
		{
			try
			{
				var threshold = PlaneThresholdSlider.Value * 0.001; // Convert from slider units to meters
				var wallDistance = calibration.KinectToSurfaceDistanceMeters;

				if (wallDistance <= 0)
				{
					// Try to auto-detect if not calibrated
					wallDistance = AutoDetectWallDistance(depthData, width, height);
					if (wallDistance <= 0)
					{
						StatusText.Text = "Wall distance not calibrated!";
						return;
					}
				}

				var touchPixels = new List<Point>();
				const int sampleStep = 3; // Only check every 3rd pixel for performance
				const int maxPixelsToCheck = 5000; // Limit total pixels checked per frame
				int pixelsChecked = 0;

				// Pre-calculate depth thresholds for faster comparison
				ushort wallDepthMin = (ushort)((wallDistance - threshold) * 1000);
				ushort wallDepthMax = (ushort)(wallDistance * 1000);

				for (int y = 0; y < height && pixelsChecked < maxPixelsToCheck; y += sampleStep)
				{
					for (int x = 0; x < width && pixelsChecked < maxPixelsToCheck; x += sampleStep)
					{
						pixelsChecked++;
						int index = y * width + x;
						ushort depth = depthData[index];

						if (depth > 0 && depth >= wallDepthMin && depth <= wallDepthMax)
						{
							// Quick depth check passed, now check touch area
							if (IsPointInTouchArea(x, y, depth))
							{
								touchPixels.Add(new Point(x, y));
								
								// Early exit if we find too many touch pixels (performance limit)
								if (touchPixels.Count > 200)
								{
									break;
								}
							}
						}
					}
				}

				var blobs = SimpleCluster(touchPixels, 20);
				var now = DateTime.Now;
				var newTouches = new List<TouchPoint>();

				foreach (var blob in blobs)
				{
					if (blob.Area >= minBlobAreaPoints)
					{
						newTouches.Add(new TouchPoint
						{
							Position = blob.Center,
							LastSeen = now,
							Area = blob.Area,
							Depth = wallDistance - (threshold / 2.0)
						});
					}
				}
				activeTouches = newTouches;
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in DetectTouchesInDepthData: {ex.Message}");
			}
		}
		
		// CORRECTED COORDINATE MAPPING METHOD
		private bool IsPointInTouchArea(int x, int y, ushort depth)
		{
			// Map the depth point to color space to check against the TouchArea
			var depthPoint = new DepthSpacePoint { X = x, Y = y };
			var colorPoint = kinectManager.CoordinateMapper.MapDepthPointToColorSpace(depthPoint, depth);

			if (!float.IsInfinity(colorPoint.X) && !float.IsInfinity(colorPoint.Y))
			{
				// Check if the point is within the calibrated touch area with offset adjustments
				var touchArea = calibration.TouchArea;
				var adjustedX = colorPoint.X - touchAreaXOffset;
				var adjustedY = colorPoint.Y - touchAreaYOffset;
				
				return (adjustedX >= touchArea.X && adjustedX <= touchArea.Right &&
						adjustedY >= touchArea.Y && adjustedY <= touchArea.Bottom);
			}
			return false;
		}

		// Helper: Auto-detect wall distance
		private double AutoDetectWallDistance(ushort[] depthData, int width, int height)
		{
			try
			{
				// Build histogram of depths
				var histogram = new Dictionary<int, int>();
				
				for (int i = 0; i < depthData.Length; i += 10) // Sample every 10th pixel
				{
					if (depthData[i] > 500 && depthData[i] < 4000) // 0.5m to 4m range
					{
						int bin = depthData[i] / 50; // 5cm bins
						if (!histogram.ContainsKey(bin))
							histogram[bin] = 0;
						histogram[bin]++;
					}
				}
				
				if (histogram.Count > 0)
				{
					// Find the most common depth (likely the wall)
					var mostCommon = histogram.OrderByDescending(kvp => kvp.Value).First();
					return (mostCommon.Key * 50) / 1000.0; // Convert back to meters
				}
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in AutoDetectWallDistance: {ex.Message}");
			}
			
			return 2.5; // Default fallback
		}

		// Simple clustering algorithm
		private List<Blob> SimpleCluster(List<Point> points, double maxDist)
		{
			var blobs = new List<Blob>();
			if (points.Count == 0) return blobs;
			
			var used = new bool[points.Count];
			
			for (int i = 0; i < points.Count; i++)
			{
				if (used[i]) continue;
				
				var cluster = new List<Point> { points[i] };
				used[i] = true;
				
				// Find all nearby points
				bool foundNew = true;
				while (foundNew)
				{
					foundNew = false;
					for (int j = 0; j < points.Count; j++)
					{
						if (used[j]) continue;
						
						// Check if this point is close to any point in the cluster
						foreach (var clusterPoint in cluster)
						{
							double dist = Math.Sqrt(Math.Pow(points[j].X - clusterPoint.X, 2) + 
												   Math.Pow(points[j].Y - clusterPoint.Y, 2));
							if (dist <= maxDist)
							{
								cluster.Add(points[j]);
								used[j] = true;
								foundNew = true;
								break;
							}
						}
					}
				}
				
				// Calculate center and area
				double avgX = cluster.Average(p => p.X);
				double avgY = cluster.Average(p => p.Y);
				
				blobs.Add(new Blob 
				{ 
					Center = new Point(avgX, avgY), 
					Area = cluster.Count 
				});
			}
			
			return blobs;
		}

		// EVENT HANDLERS
		private void PlaneThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
		{
			try
		{
			UpdateThresholdDisplay();
				LogToFile(GetDiagnosticPath(), $"SLIDER CHANGED: {(e.NewValue * 0.001):F3}m");
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in PlaneThresholdSlider_ValueChanged: {ex.Message}");
			}
		}
		
		private void ShowTouchDetectionButton_Click(object sender, RoutedEventArgs e)
		{
			try
			{
				showTouchDetection = !showTouchDetection;
				if (ShowTouchDetectionButton != null)
				{
					ShowTouchDetectionButton.Content = showTouchDetection ? "👁️ HIDE TOUCH DETECTION" : "👁️ SHOW TOUCH DETECTION";
					ShowTouchDetectionButton.Background = showTouchDetection ? new SolidColorBrush(Colors.Green) : new SolidColorBrush(Colors.OrangeRed);
				}
				LogToFile(GetDiagnosticPath(), $"Show touch detection: {showTouchDetection}");
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in ShowTouchDetectionButton_Click: {ex.Message}");
			}
		}
		
		private void CaptureBackgroundButton_Click(object sender, RoutedEventArgs e)
		{
			try
			{
				// Force background capture on next frame
				backgroundCaptured = false;
				LogToFile(GetDiagnosticPath(), "Background capture requested by user - will capture on next frame");
				
				MessageBox.Show("Background will be captured on the next frame. Make sure the wall is clear of any objects.", 
					"Background Capture", MessageBoxButton.OK, MessageBoxImage.Information);
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in CaptureBackgroundButton_Click: {ex.Message}");
			}
		}
		
		private void ResetViewButton_Click(object sender, RoutedEventArgs e)
		{
			try
			{
				depthCameraXOffset = 0;
				depthCameraYOffset = 0;
				depthCameraZoom = 1.0;
				UpdateUnifiedViewTransform();
				LogToFile(GetDiagnosticPath(), "View reset to default");
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in ResetViewButton_Click: {ex.Message}");
			}
		}

		private void DepthCameraZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
		{
			try
			{
				depthCameraZoom = e.NewValue;
				UpdateUnifiedViewTransform();
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in DepthCameraZoomSlider_ValueChanged: {ex.Message}");
			}
		}

		private void FlipDepthVerticallyCheckBox_Changed(object sender, RoutedEventArgs e)
		{
			try
			{
				UpdateUnifiedViewTransform();
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in FlipDepthVerticallyCheckBox_Changed: {ex.Message}");
			}
		}
		
		private void MinBlobAreaSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
		{
			try
			{
				minBlobAreaPoints = (int)e.NewValue;
				if (MinBlobAreaValueText != null)
				{
					MinBlobAreaValueText.Text = $"{minBlobAreaPoints} pts";
				}
				LogToFile(GetDiagnosticPath(), $"Min blob area changed to: {minBlobAreaPoints}");
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in MinBlobAreaSlider_ValueChanged: {ex.Message}");
			}
		}
		
		private void TouchAreaXOffsetSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
		{
			try
			{
				touchAreaXOffset = e.NewValue;
				if (TouchAreaXOffsetValueText != null)
				{
					TouchAreaXOffsetValueText.Text = $"{touchAreaXOffset:F0} px";
				}
				LogToFile(GetDiagnosticPath(), $"Touch area X offset changed to: {touchAreaXOffset}");
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in TouchAreaXOffsetSlider_ValueChanged: {ex.Message}");
			}
		}
		
		private void TouchAreaYOffsetSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
		{
			try
			{
				touchAreaYOffset = e.NewValue;
				if (TouchAreaYOffsetValueText != null)
				{
					TouchAreaYOffsetValueText.Text = $"{touchAreaYOffset:F0} px";
				}
				LogToFile(GetDiagnosticPath(), $"Touch area Y offset changed to: {touchAreaYOffset}");
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in TouchAreaYOffsetSlider_ValueChanged: {ex.Message}");
			}
		}
		
		private void DiagnosticButton_Click(object sender, RoutedEventArgs e)
		{
			try
			{
				var path = GetDiagnosticPath();
				if (System.IO.File.Exists(path))
				{
					System.Diagnostics.Process.Start("notepad.exe", path);
					}
					else
					{
					MessageBox.Show("Diagnostic file not found.", "Diagnostic", MessageBoxButton.OK, MessageBoxImage.Information);
				}
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in DiagnosticButton_Click: {ex.Message}");
			}
		}

		private void VerifyCalibrationButton_Click(object sender, RoutedEventArgs e)
		{
			try
			{
				var message = $"Calibration Status:\n\n";
				message += $"TouchArea: {(calibration?.TouchArea != null ? $"X={calibration.TouchArea.X:F1}, Y={calibration.TouchArea.Y:F1}, W={calibration.TouchArea.Width:F1}, H={calibration.TouchArea.Height:F1}" : "Not set")}\n";
				message += $"Wall Distance: {(calibration?.KinectToSurfaceDistanceMeters > 0 ? $"{calibration.KinectToSurfaceDistanceMeters:F3}m" : "Not calibrated")}\n";
				message += $"Plane: {(calibration?.Plane != null ? "Valid" : "Not set")}\n";
				
				MessageBox.Show(message, "Calibration Verification", MessageBoxButton.OK, MessageBoxImage.Information);
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in VerifyCalibrationButton_Click: {ex.Message}");
			}
		}

		private void FinishButton_Click(object sender, RoutedEventArgs e)
		{
			try
			{
				MessageBox.Show("Touch detection test completed!", "Finish", MessageBoxButton.OK, MessageBoxImage.Information);
				this.Close();
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in FinishButton_Click: {ex.Message}");
			}
		}

		private void CancelButton_Click(object sender, RoutedEventArgs e)
		{
			try
			{
				this.Close();
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in CancelButton_Click: {ex.Message}");
			}
		}

		// PLACEHOLDER METHODS - These need to be implemented
		private void FixDepthCameraDisplay() { }
		private void InitializeScreen3Diagnostics() { }
		private void ValidateCalibrationData() { }
		private void NormalizeAndOrientPlane() { }
		private void UpdateThresholdDisplay()
		{
			try
			{
				if (ThresholdValueText != null && PlaneThresholdSlider != null)
				{
					ThresholdValueText.Text = $"{(PlaneThresholdSlider.Value * 0.001):F3} m";
				}
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in UpdateThresholdDisplay: {ex.Message}");
			}
		}
		private void UpdateVisualFeedback()
		{
			try
			{
				if (OverlayCanvas == null) return;
				
				// Clear previous overlays
				OverlayCanvas.Children.Clear();
				
				if (!showTouchDetection) return;
				
				// Draw touch area boundary
				DrawTouchAreaBoundary();
				
				// Draw detected touch pixels (cyan dots)
				if (detectedTouchPixels != null && detectedTouchPixels.Count > 0)
				{
					int maxDots = Math.Min(200, detectedTouchPixels.Count);
					for (int i = 0; i < maxDots; i++)
					{
						var point = detectedTouchPixels[i];
						var dot = new Ellipse
						{
							Width = 2,
							Height = 2,
							Fill = new SolidColorBrush(Colors.Cyan)
						};
						Canvas.SetLeft(dot, point.X - 1);
						Canvas.SetTop(dot, point.Y - 1);
						OverlayCanvas.Children.Add(dot);
					}
					
					// Add count indicator
					var countText = new TextBlock
					{
						Text = $"Cyan dots: {maxDots}/{detectedTouchPixels.Count}",
						Foreground = new SolidColorBrush(Colors.White),
						Background = new SolidColorBrush(Colors.Black),
						FontSize = 12
					};
					Canvas.SetLeft(countText, 10);
					Canvas.SetTop(countText, 10);
					OverlayCanvas.Children.Add(countText);
				}
				
				// Draw active touches (red squares)
				if (activeTouches != null && activeTouches.Count > 0)
				{
					foreach (var touch in activeTouches)
					{
						var square = new Rectangle
						{
							Width = 20,
							Height = 20,
							Fill = new SolidColorBrush(Colors.Red),
							Stroke = new SolidColorBrush(Colors.White),
							StrokeThickness = 2
						};
						Canvas.SetLeft(square, touch.Position.X - 10);
						Canvas.SetTop(square, touch.Position.Y - 10);
						OverlayCanvas.Children.Add(square);
						
						// Add touch info text
						var touchText = new TextBlock
						{
							Text = $"T{activeTouches.IndexOf(touch) + 1}",
							Foreground = new SolidColorBrush(Colors.White),
							Background = new SolidColorBrush(Colors.Red),
							FontSize = 10,
							FontWeight = FontWeights.Bold
						};
						Canvas.SetLeft(touchText, touch.Position.X - 5);
						Canvas.SetTop(touchText, touch.Position.Y - 25);
						OverlayCanvas.Children.Add(touchText);
					}
				}
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in UpdateVisualFeedback: {ex.Message}");
			}
		}
		
		private void DrawTouchAreaBoundary()
		{
			try
			{
				if (calibration?.TouchArea == null || OverlayCanvas == null) return;
				
				var touchArea = calibration.TouchArea;
				
				// Convert color space coordinates to depth space for visualization
				// This is a simplified conversion - you might need to adjust based on your setup
				var rect = new Rectangle
				{
					Width = touchArea.Width * 0.27, // Approximate scale factor from color to depth
					Height = touchArea.Height * 0.39, // Approximate scale factor from color to depth
					Stroke = new SolidColorBrush(Colors.Yellow),
					StrokeThickness = 2,
					StrokeDashArray = new DoubleCollection { 5, 5 },
					Fill = new SolidColorBrush(Color.FromArgb(30, 255, 255, 0)) // Semi-transparent yellow
				};
				
				Canvas.SetLeft(rect, touchArea.X * 0.27);
				Canvas.SetTop(rect, touchArea.Y * 0.39);
				OverlayCanvas.Children.Add(rect);
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in DrawTouchAreaBoundary: {ex.Message}");
			}
		}
		private void UpdateStatusInformation() { }
		private void LogTouchDetectionDiagnostic() { }
		private string GetDiagnosticPath() 
		{ 
			var appDir = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
			return System.IO.Path.Combine(appDir, "..", "..", "screen3_diagnostic.txt");
		}
		private void LogToFile(string path, string message) { System.IO.File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} - {message}\n"); }
	}
}
