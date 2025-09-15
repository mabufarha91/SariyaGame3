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
				LogToFile(GetDiagnosticPath(), "UI elements initialized after Loaded event");
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in Screen3_TouchTest_Loaded: {ex.Message}");
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
				
			// TouchSizeSlider removed in simplified UI
				
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
				
				// TouchArea offset sliders removed in simplified UI
				
				// Update display values
				UpdateThresholdDisplay();
				// UpdateTouchSizeDisplay(); // Removed in simplified UI
				// UpdateTouchAreaOffsetDisplay(); // Removed in simplified UI
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
				updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) }; // ~30 FPS
			updateTimer.Tick += UpdateTimer_Tick;
			updateTimer.Start();
				
				LogToFile(GetDiagnosticPath(), "Update timer initialized successfully");
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
				
				for (int i = 0; i < depthData.Length; i++)
				{
					ushort depth = depthData[i];
					byte intensity = 0;
					
					if (depth != 0)
					{
						double depthInMeters = depth / 1000.0;
						
						// Normalize depth to 0-255 range centered on wall
						if (depthInMeters < minDepth)
							intensity = 255; // Very close = white
						else if (depthInMeters > maxDepth)
							intensity = 0; // Far = black
						else
						{
							// Linear interpolation
							double normalized = (depthInMeters - minDepth) / (maxDepth - minDepth);
							intensity = (byte)(255 * (1.0 - normalized));
						}
						
						// Highlight wall distance with special color
						if (Math.Abs(depthInMeters - wallDistance) < 0.02) // Within 2cm of wall
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
		
		private void DetectTouchesInDepthData(ushort[] depthData, int width, int height)
		{
			try
			{
				var threshold = PlaneThresholdSlider.Value;
				
				// DEBUG: Log the actual slider value
				LogToFile(GetDiagnosticPath(), $"DEBUG: Slider value = {threshold:F3}m, Slider object = {(PlaneThresholdSlider != null ? "OK" : "NULL")}");
				
				// BACKGROUND SUBTRACTION: Capture background if not done yet
				if (!backgroundCaptured || (DateTime.Now - lastBackgroundCapture).TotalSeconds > 30)
				{
					CaptureBackgroundFrame(depthData, width, height);
					return; // Skip detection on first frame
				}
			
				// Clear detected pixels if visualization is enabled
				if (showTouchDetection)
				{
					detectedTouchPixels.Clear();
				}
			
				// CRITICAL: Get calibrated wall distance
				double wallDistance = 0;
				if (calibration != null && calibration.KinectToSurfaceDistanceMeters > 0)
				{
					wallDistance = calibration.KinectToSurfaceDistanceMeters;
					LogToFile(GetDiagnosticPath(), $"Using calibrated wall: {wallDistance:F3}m");
				}
				else
				{
					// Auto-detect wall as most common depth
					wallDistance = AutoDetectWallDistance(depthData, width, height);
					LogToFile(GetDiagnosticPath(), $"Auto-detected wall: {wallDistance:F3}m");
				}
				
				// Calculate touch detection range - PROPER RANGE
				// Use the threshold as the detection distance from the wall
				double touchRangeMin = wallDistance - threshold; // Start detecting at threshold distance
				double touchRangeMax = wallDistance - 0.002; // Stop 2mm from wall
				
				// Log the detection range for debugging
				LogToFile(GetDiagnosticPath(), $"Touch range: {touchRangeMin:F3}m to {touchRangeMax:F3}m (slider threshold: {threshold:F3}m, range: {(touchRangeMax-touchRangeMin)*1000:F1}mm)");
				
				// Get ROI from TouchArea
				int roiLeft = 0, roiTop = 0, roiRight = width - 1, roiBottom = height - 1;
				
				if (calibration?.TouchArea != null && calibration.TouchArea.Width > 0)
				{
					// Simple scaling from color to depth space
					double scaleX = (double)width / 1920.0;
					double scaleY = (double)height / 1080.0;
					
					roiLeft = (int)(calibration.TouchArea.X * scaleX);
					roiTop = (int)(calibration.TouchArea.Y * scaleY);
					roiRight = (int)(calibration.TouchArea.Right * scaleX);
					roiBottom = (int)(calibration.TouchArea.Bottom * scaleY);
					
					// Add margin and clamp
					roiLeft = Math.Max(0, roiLeft - 10);
					roiTop = Math.Max(0, roiTop - 10);
					roiRight = Math.Min(width - 1, roiRight + 10);
					roiBottom = Math.Min(height - 1, roiBottom + 10);
				}
				
				// IMPROVED TOUCH DETECTION with coordinate mapping and background subtraction
				var touchPixels = new List<Point>();
				int sampleStep = 4; // Sample every 4th pixel for better accuracy
				int maxPixelsToDetect = 100; // Lower safety limit for better performance
				
				for (int y = roiTop; y <= roiBottom && touchPixels.Count < maxPixelsToDetect; y += sampleStep)
				{
					for (int x = roiLeft; x <= roiRight && touchPixels.Count < maxPixelsToDetect; x += sampleStep)
					{
						int idx = y * width + x;
						if (idx >= 0 && idx < depthData.Length && idx < backgroundDepthFrame.Length)
						{
							ushort currentDepth = depthData[idx];
							ushort backgroundDepth = backgroundDepthFrame[idx];
							
							// STEP 1: Check if point is in TouchArea using proper coordinate mapping
							if (!IsDepthPointInTouchArea(x, y, currentDepth))
								continue;
							
							// STEP 2: Check for significant depth difference (background subtraction)
							if (!IsSignificantDepthDifference(currentDepth, backgroundDepth, threshold))
								continue;
							
							// STEP 3: Additional depth range check
							double depth = currentDepth / 1000.0;
							if (depth > 0 && depth >= touchRangeMin && depth <= touchRangeMax)
							{
								touchPixels.Add(new Point(x, y));
								
								if (showTouchDetection)
								{
									detectedTouchPixels.Add(new Point(x, y));
								}
							}
						}
					}
				}
				
				// Log if we hit the safety limit
				if (touchPixels.Count >= maxPixelsToDetect)
				{
					LogToFile(GetDiagnosticPath(), $"WARNING: Hit safety limit of {maxPixelsToDetect} pixels - touch range may be too wide!");
				}
			
				// Simple clustering
				var blobs = SimpleCluster(touchPixels, 20);
				
				// Update active touches
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
							Depth = wallDistance - threshold/2 // Approximate
						});
					}
				}
				
				activeTouches = newTouches;
				
				// Log status
				if (activeTouches.Count > 0)
				{
					LogToFile(GetDiagnosticPath(), $"Detected {activeTouches.Count} touches from {touchPixels.Count} pixels");
				}
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in DetectTouchesInDepthData: {ex.Message}");
			}
		}

		// BACKGROUND SUBTRACTION METHODS
		private void CaptureBackgroundFrame(ushort[] depthData, int width, int height)
		{
			try
			{
				if (backgroundDepthFrame == null || backgroundDepthFrame.Length != depthData.Length)
				{
					backgroundDepthFrame = new ushort[depthData.Length];
				}
				
				Array.Copy(depthData, backgroundDepthFrame, depthData.Length);
				backgroundCaptured = true;
				lastBackgroundCapture = DateTime.Now;
				
				LogToFile(GetDiagnosticPath(), $"Background frame captured: {width}x{height}, {depthData.Length} pixels");
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in CaptureBackgroundFrame: {ex.Message}");
			}
		}
		
		private bool IsSignificantDepthDifference(ushort currentDepth, ushort backgroundDepth, double thresholdMeters = 0.02)
		{
			try
			{
				if (currentDepth == 0 || backgroundDepth == 0)
					return false;
				
				double currentMeters = currentDepth / 1000.0;
				double backgroundMeters = backgroundDepth / 1000.0;
				double difference = Math.Abs(currentMeters - backgroundMeters);
				
				// Significant difference if current depth is closer than background by threshold
				return difference >= thresholdMeters && currentMeters < backgroundMeters;
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in IsSignificantDepthDifference: {ex.Message}");
				return false;
			}
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
						
						// Check distance to any point in cluster
						foreach (var cp in cluster)
						{
							double dist = Math.Sqrt(Math.Pow(points[j].X - cp.X, 2) + 
												  Math.Pow(points[j].Y - cp.Y, 2));
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
				
				// Calculate center
				if (cluster.Count > 0)
				{
					double avgX = cluster.Average(p => p.X);
					double avgY = cluster.Average(p => p.Y);
					
					blobs.Add(new Blob 
					{ 
						Center = new Point(avgX, avgY), 
						Area = cluster.Count 
					});
				}
			}
			
			return blobs;
		}
		
		private bool IsPointWithinTouchThreshold(int x, int y, double depth, double threshold)
		{
			try
			{
				// WALL TOUCH DETECTION: Check if something is touching the wall in the TouchArea
				if (depth <= 0)
				{
					return false; // No depth data
				}
				
				// STEP 1: Check if this point is within the TouchArea (on the wall)
				// CRITICAL FIX: Check TouchArea data properly
				if (calibration?.TouchArea != null && 
					calibration.TouchArea.Width > 0 && calibration.TouchArea.Height > 0 &&
					calibration.TouchArea.X >= 0 && calibration.TouchArea.Y >= 0)
				{
					// CRITICAL: Convert depth coordinates to color coordinates to check against TouchArea
					// The TouchArea is defined in COLOR CAMERA coordinates (1920x1080)
					// We need to convert the depth point (x,y) to color coordinates for comparison
					var colorPoint = DepthToColorCoordinates(new Point(x, y));
					
					// Apply X/Y offset compensation for camera position differences
					// The offset sliders allow manual adjustment of the TouchArea position in depth space
					var adjustedTouchAreaX = calibration.TouchArea.X - (touchAreaXOffset / (512.0 / 1920.0));
					var adjustedTouchAreaY = calibration.TouchArea.Y - (touchAreaYOffset / (424.0 / 1080.0));
					var adjustedTouchAreaRight = calibration.TouchArea.Right - (touchAreaXOffset / (512.0 / 1920.0));
					var adjustedTouchAreaBottom = calibration.TouchArea.Bottom - (touchAreaYOffset / (424.0 / 1080.0));
					
					// Check if the depth point (converted to color coordinates) is within the TouchArea
					bool withinTouchArea = (colorPoint.X >= adjustedTouchAreaX && 
										   colorPoint.X <= adjustedTouchAreaRight &&
										   colorPoint.Y >= adjustedTouchAreaY && 
										   colorPoint.Y <= adjustedTouchAreaBottom);
					
					// Log TouchArea boundary checking occasionally
					if (DateTime.Now.Millisecond % 300 == 0 && depth < 3.0)
					{
						LogToFile(GetDiagnosticPath(), $"TOUCHAREA CHECK: depth({x},{y}) -> color({colorPoint.X:F1},{colorPoint.Y:F1}) vs TouchArea({adjustedTouchAreaX:F1},{adjustedTouchAreaY:F1})-({adjustedTouchAreaRight:F1},{adjustedTouchAreaBottom:F1}) = {withinTouchArea}");
					}
					
					if (!withinTouchArea)
					{
						return false; // Point is outside the TouchArea on the wall
					}
					
					// STEP 2: Check if something is touching the wall (closer than the wall plane)
					if (isPlaneValid)
					{
						try
						{
							// Use signed, normalized plane distance from cached camera-space frame
							CameraSpacePoint[] cps; int w, h;
							if (kinectManager.TryGetCameraSpaceFrame(out cps, out w, out h) && x >= 0 && y >= 0 && x < w && y < h)
							{
								var cp = cps[y * w + x];
								if (!float.IsInfinity(cp.X) && !float.IsInfinity(cp.Y) && !float.IsInfinity(cp.Z) &&
									!float.IsNaN(cp.X) && !float.IsNaN(cp.Y) && !float.IsNaN(cp.Z))
								{
									double distSigned = KinectManager.KinectManager.DistancePointToPlaneSignedNormalized(cp, planeNx, planeNy, planeNz, planeD);
									var isTouchingWall = distSigned >= 0 && distSigned <= threshold;
							// Log wall touch detection
							if (isTouchingWall && DateTime.Now.Millisecond % 100 == 0)
							{
										LogToFile(GetDiagnosticPath(), $"WALL TOUCH: ({x},{y}) depth={depth:F3}m, plane_dist_signed={distSigned:F3}m, threshold={threshold:F3}m, TOUCHING={isTouchingWall}");
							}
							return isTouchingWall;
								}
							}
							return false;
						}
						catch (Exception ex)
						{
							LogToFile(GetDiagnosticPath(), $"ERROR in plane calculation: {ex.Message}");
							// Fallback: use simple depth check for wall touching
							double wallDistance = calibration.KinectToSurfaceDistanceMeters > 0 ? calibration.KinectToSurfaceDistanceMeters : 2.5;
							double delta = wallDistance - depth;
							return depth > 0 && delta >= 0 && delta <= threshold;
						}
					}
					else
					{
						// No plane data, use CALIBRATED wall distance from Screen 2!
						double wallDistance = calibration.KinectToSurfaceDistanceMeters > 0 ? calibration.KinectToSurfaceDistanceMeters : 2.5; // Use calibrated distance or fallback
						double delta = wallDistance - depth;
						var isTouchingWall = depth > 0 && delta >= 0 && delta <= threshold;
						
						// Log fallback detection
						if (DateTime.Now.Millisecond % 500 == 0 && depth < 4.0)
						{
							LogToFile(GetDiagnosticPath(), $"FALLBACK WALL TOUCH: ({x},{y}) depth={depth:F3}m, CALIBRATED_WALL={wallDistance:F3}m, threshold={threshold:F3}m, TOUCHING={isTouchingWall}");
						}
						
						return isTouchingWall;
					}
				}
				else
				{
					// No TouchArea data, use fallback detection
					// Log the actual TouchArea values for debugging
					if (calibration?.TouchArea != null)
					{
						LogToFile(GetDiagnosticPath(), $"TouchArea data: X={calibration.TouchArea.X}, Y={calibration.TouchArea.Y}, W={calibration.TouchArea.Width}, H={calibration.TouchArea.Height}");
					}
					else
					{
						LogToFile(GetDiagnosticPath(), "TouchArea is NULL");
					}
					
					// USE ACTUAL WALL DISTANCE FROM SCREEN 2 CALIBRATION!
					var wallDistance = calibration.KinectToSurfaceDistanceMeters > 0 ? calibration.KinectToSurfaceDistanceMeters : 2.5; // Use calibrated distance or fallback
					var touchThreshold = calibration.TouchDetectionThresholdMeters > 0 ? calibration.TouchDetectionThresholdMeters : 0.05; // Use calibrated threshold or 5cm
					var isWallTouch = depth > 0.3 && depth < (wallDistance - touchThreshold);
					
					// Log fallback detection occasionally
					if (DateTime.Now.Millisecond % 500 == 0 && depth < 4.0)
					{
						LogToFile(GetDiagnosticPath(), $"FALLBACK: depth={depth:F3}m, CALIBRATED_WALL={wallDistance:F3}m, CALIBRATED_THRESHOLD={touchThreshold:F3}m, touch={isWallTouch}");
					}
					
					return isWallTouch;
				}
				
				/* ORIGINAL COMPLEX LOGIC - COMMENTED OUT FOR DEBUGGING
				// CRITICAL FIX: Convert depth coordinates to color coordinates first
				var colorPoint = DepthToColorCoordinates(new Point(x, y));
				
				// Check if the point is within the calibrated touch area (in color space)
				if (calibration?.TouchArea != null && calibration.TouchArea.Width > 0 && calibration.TouchArea.Height > 0)
				{
					// Convert depth coordinates back to color coordinates for comparison
					// Apply inverse offset to get the original TouchArea bounds
					var adjustedTouchAreaX = calibration.TouchArea.X - (touchAreaXOffset / (512.0 / 1920.0));
					var adjustedTouchAreaY = calibration.TouchArea.Y - (touchAreaYOffset / (424.0 / 1080.0));
					var adjustedTouchAreaRight = calibration.TouchArea.Right - (touchAreaXOffset / (512.0 / 1920.0));
					var adjustedTouchAreaBottom = calibration.TouchArea.Bottom - (touchAreaYOffset / (424.0 / 1080.0));
					
					// TouchArea is in color camera coordinates
					if (colorPoint.X < adjustedTouchAreaX || 
						colorPoint.X > adjustedTouchAreaRight ||
						colorPoint.Y < adjustedTouchAreaY || 
						colorPoint.Y > adjustedTouchAreaBottom)
					{
						return false; // Point is outside touch area
					}
				}
				else
				{
					LogToFile(GetDiagnosticPath(), "WARNING: TouchArea is null or invalid in IsPointWithinTouchThreshold");
					// IMPROVED FALLBACK: Use more restrictive depth-based detection
					// Only detect objects that are significantly closer than the wall
					var wallDistance = 2.3; // Approximate wall distance from Screen 1
					var touchThreshold = 0.05; // 5cm closer than wall
					return depth > 0.5 && depth < (wallDistance - touchThreshold);
				}
				
				// Check if depth is within threshold of the calibrated plane
				if (calibration?.Plane != null && 
					Math.Abs(calibration.Plane.Nx) > 0.001 && 
					Math.Abs(calibration.Plane.Ny) > 0.001 && 
					Math.Abs(calibration.Plane.Nz) > 0.001)
				{
					try
					{
						// Convert to camera space point
						var cameraPoint = kinectManager.CoordinateMapper.MapDepthPointToCameraSpace(
							new DepthSpacePoint { X = x, Y = y }, 
							(ushort)(depth * 1000));
						
						var distanceToPlane = Math.Abs(KinectManager.KinectManager.DistancePointToPlaneMeters(
							cameraPoint, calibration.Plane.Nx, calibration.Plane.Ny, calibration.Plane.Nz, calibration.Plane.D));
						
						var withinThreshold = distanceToPlane < threshold;
						
						// Log calibrated detection for debugging (occasionally)
						if (withinThreshold && DateTime.Now.Millisecond % 100 == 0)
						{
							LogToFile(GetDiagnosticPath(), $"CALIBRATED: Touch detected at ({x},{y}) depth={depth:F3}m, plane_dist={distanceToPlane:F3}m, threshold={threshold:F3}m");
						}
						
						return withinThreshold;
					}
					catch (Exception ex)
					{
						LogToFile(GetDiagnosticPath(), $"ERROR in plane calculation: {ex.Message}");
						// Fallback to simple depth check
						var wallDistance = 2.3;
						var touchThreshold = 0.05;
						return depth > 0.5 && depth < (wallDistance - touchThreshold);
					}
				}
				else
				{
					// Plane data is invalid, use fallback detection
					var wallDistance = 2.3;
					var touchThreshold = 0.05;
					return depth > 0.5 && depth < (wallDistance - touchThreshold);
				}
				*/
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in IsPointWithinTouchThreshold: {ex.Message}");
				return false;
			}
		}
		
		// Add this method to show touch area boundaries
		private void DrawTouchAreaOverlay()
		{
			try
			{
				if (OverlayCanvas == null || calibration?.TouchArea == null || 
					calibration.TouchArea.Width <= 0 || calibration.TouchArea.Height <= 0)
					return;
					
				// Convert color coordinates to depth coordinates for display
				var colorTopLeft = new Point(calibration.TouchArea.X, calibration.TouchArea.Y);
				var colorTopRight = new Point(calibration.TouchArea.Right, calibration.TouchArea.Y);
				var colorBottomLeft = new Point(calibration.TouchArea.X, calibration.TouchArea.Bottom);
				var colorBottomRight = new Point(calibration.TouchArea.Right, calibration.TouchArea.Bottom);
				
				// Log color coordinates for debugging
				LogToFile(GetDiagnosticPath(), $"TouchArea Color Coords: TL=({colorTopLeft.X:F1},{colorTopLeft.Y:F1}), TR=({colorTopRight.X:F1},{colorTopRight.Y:F1}), BL=({colorBottomLeft.X:F1},{colorBottomLeft.Y:F1}), BR=({colorBottomRight.X:F1},{colorBottomRight.Y:F1})");
				
				// Convert to depth coordinates using proper coordinate mapping
				var depthTopLeft = ColorToDepthCoordinates(colorTopLeft);
				var depthTopRight = ColorToDepthCoordinates(colorTopRight);
				var depthBottomLeft = ColorToDepthCoordinates(colorBottomLeft);
				var depthBottomRight = ColorToDepthCoordinates(colorBottomRight);
				
				// Log depth coordinates for debugging
				LogToFile(GetDiagnosticPath(), $"TouchArea Depth Coords: TL=({depthTopLeft.X:F1},{depthTopLeft.Y:F1}), TR=({depthTopRight.X:F1},{depthTopRight.Y:F1}), BL=({depthBottomLeft.X:F1},{depthBottomLeft.Y:F1}), BR=({depthBottomRight.X:F1},{depthBottomRight.Y:F1})");
				
				// Calculate rectangle dimensions
				var rectWidth = Math.Abs(depthBottomRight.X - depthTopLeft.X);
				var rectHeight = Math.Abs(depthBottomRight.Y - depthTopLeft.Y);
				var rectLeft = Math.Min(depthTopLeft.X, depthBottomRight.X);
				var rectTop = Math.Min(depthTopLeft.Y, depthBottomRight.Y);
				
				// Apply X and Y offsets
				rectLeft += touchAreaXOffset;
				rectTop += touchAreaYOffset;
				
				// Log rectangle dimensions with offsets
				LogToFile(GetDiagnosticPath(), $"TouchArea Rectangle: X={rectLeft:F1}, Y={rectTop:F1}, W={rectWidth:F1}, H={rectHeight:F1} [Offsets: X={touchAreaXOffset:F1}, Y={touchAreaYOffset:F1}]");
				
				// Draw touch area boundary
				var touchAreaRect = new Rectangle
				{
					Width = rectWidth,
					Height = rectHeight,
					Stroke = Brushes.Yellow,
					StrokeThickness = 3, // Increased thickness for better visibility
					Fill = new SolidColorBrush(Color.FromArgb(30, 255, 255, 0)) // Slightly more opaque yellow
				};
				
				Canvas.SetLeft(touchAreaRect, rectLeft);
				Canvas.SetTop(touchAreaRect, rectTop);
				OverlayCanvas.Children.Add(touchAreaRect);
				
				// Add corner markers for debugging
				AddCornerMarkers(depthTopLeft, depthTopRight, depthBottomLeft, depthBottomRight);
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in DrawTouchAreaOverlay: {ex.Message}");
			}
		}
		
		// Helper method to add corner markers for debugging
		private void AddCornerMarkers(Point tl, Point tr, Point bl, Point br)
		{
			try
			{
				var cornerSize = 8.0;
				var cornerColor = Brushes.Cyan;
				
				// Apply offsets to corner positions
				var offsetTl = new Point(tl.X + touchAreaXOffset, tl.Y + touchAreaYOffset);
				var offsetTr = new Point(tr.X + touchAreaXOffset, tr.Y + touchAreaYOffset);
				var offsetBl = new Point(bl.X + touchAreaXOffset, bl.Y + touchAreaYOffset);
				var offsetBr = new Point(br.X + touchAreaXOffset, br.Y + touchAreaYOffset);
				
				// Top-left corner
				var tlMarker = new Ellipse { Width = cornerSize, Height = cornerSize, Fill = cornerColor };
				Canvas.SetLeft(tlMarker, offsetTl.X - cornerSize/2);
				Canvas.SetTop(tlMarker, offsetTl.Y - cornerSize/2);
				OverlayCanvas.Children.Add(tlMarker);
				
				// Top-right corner
				var trMarker = new Ellipse { Width = cornerSize, Height = cornerSize, Fill = cornerColor };
				Canvas.SetLeft(trMarker, offsetTr.X - cornerSize/2);
				Canvas.SetTop(trMarker, offsetTr.Y - cornerSize/2);
				OverlayCanvas.Children.Add(trMarker);
				
				// Bottom-left corner
				var blMarker = new Ellipse { Width = cornerSize, Height = cornerSize, Fill = cornerColor };
				Canvas.SetLeft(blMarker, offsetBl.X - cornerSize/2);
				Canvas.SetTop(blMarker, offsetBl.Y - cornerSize/2);
				OverlayCanvas.Children.Add(blMarker);
				
				// Bottom-right corner
				var brMarker = new Ellipse { Width = cornerSize, Height = cornerSize, Fill = cornerColor };
				Canvas.SetLeft(brMarker, offsetBr.X - cornerSize/2);
				Canvas.SetTop(brMarker, offsetBr.Y - cornerSize/2);
				OverlayCanvas.Children.Add(brMarker);
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in AddCornerMarkers: {ex.Message}");
			}
		}
		
		private void UpdateVisualFeedback()
		{
			try
			{
				if (OverlayCanvas == null) return;
				
				// Clear overlay
				OverlayCanvas.Children.Clear();
				
				// Draw TouchArea boundary (if available)
				if (calibration?.TouchArea != null && calibration.TouchArea.Width > 0)
				{
					DrawTouchAreaBoundary();
				}
				
				// Draw detected pixels if enabled
				if (showTouchDetection && detectedTouchPixels.Count > 0)
				{
					// Limit the number of dots to prevent performance issues
					int maxDots = Math.Min(200, detectedTouchPixels.Count);
					for (int i = 0; i < maxDots; i++)
					{
						var pixel = detectedTouchPixels[i];
						var dot = new Ellipse
						{
							Width = 2,
							Height = 2,
							Fill = Brushes.Cyan,
							Stroke = Brushes.White,
							StrokeThickness = 1
						};
						Canvas.SetLeft(dot, pixel.X - 1);
						Canvas.SetTop(dot, pixel.Y - 1);
						OverlayCanvas.Children.Add(dot);
					}
					
					// Add a count indicator
					var countText = new TextBlock
					{
						Text = $"Cyan dots: {maxDots}/{detectedTouchPixels.Count}",
						Foreground = Brushes.Cyan,
						FontSize = 10,
						FontWeight = FontWeights.Bold,
						Background = new SolidColorBrush(Color.FromArgb(128, 0, 0, 0))
					};
					Canvas.SetLeft(countText, 10);
					Canvas.SetTop(countText, 10);
					OverlayCanvas.Children.Add(countText);
				}
				
				// Draw active touches
				foreach (var touch in activeTouches)
				{
					// Red square for each touch
					var rect = new Rectangle
					{
						Width = 30,
						Height = 30,
						Stroke = Brushes.Red,
						StrokeThickness = 3,
						Fill = new SolidColorBrush(Color.FromArgb(100, 255, 0, 0))
					};
					
					Canvas.SetLeft(rect, touch.Position.X - 15);
					Canvas.SetTop(rect, touch.Position.Y - 15);
					OverlayCanvas.Children.Add(rect);
					
					// Add touch info text
					var text = new TextBlock
					{
						Text = $"Touch {touch.Area}pts",
						Foreground = Brushes.Yellow,
						FontSize = 10,
						FontWeight = FontWeights.Bold
					};
					Canvas.SetLeft(text, touch.Position.X - 15);
					Canvas.SetTop(text, touch.Position.Y - 25);
					OverlayCanvas.Children.Add(text);
				}
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in UpdateVisualFeedback: {ex.Message}");
			}
		}

		// Helper: Draw TouchArea boundary
		private void DrawTouchAreaBoundary()
		{
			try
			{
				if (calibration?.TouchArea == null) return;
				
				// Scale from color to depth space
				double scaleX = 512.0 / 1920.0;
				double scaleY = 424.0 / 1080.0;
				
				var rect = new Rectangle
				{
					Width = calibration.TouchArea.Width * scaleX,
					Height = calibration.TouchArea.Height * scaleY,
					Stroke = Brushes.Yellow,
					StrokeThickness = 2,
					StrokeDashArray = new DoubleCollection { 5, 5 },
					Fill = Brushes.Transparent
				};
				
				Canvas.SetLeft(rect, calibration.TouchArea.X * scaleX);
				Canvas.SetTop(rect, calibration.TouchArea.Y * scaleY);
				OverlayCanvas.Children.Add(rect);
				
				// Add label
				var label = new TextBlock
				{
					Text = "Touch Area",
					Foreground = Brushes.Yellow,
					FontSize = 12,
					FontWeight = FontWeights.Bold
				};
				Canvas.SetLeft(label, calibration.TouchArea.X * scaleX + 5);
				Canvas.SetTop(label, calibration.TouchArea.Y * scaleY + 5);
				OverlayCanvas.Children.Add(label);
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in DrawTouchAreaBoundary: {ex.Message}");
			}
		}
		
		private void DrawDetectedTouchPixels()
		{
			try
			{
				if (detectedTouchPixels == null || detectedTouchPixels.Count == 0) return;
				
				// Draw each detected pixel as a small dot
				foreach (var pixel in detectedTouchPixels)
				{
					// Convert depth coordinates to canvas coordinates
					var canvasPoint = DepthToCanvas(pixel);
					
					// Create a small dot for each detected pixel
					var dot = new Ellipse
					{
						Width = 3,
						Height = 3,
						Fill = Brushes.Cyan, // Bright cyan for visibility
						Stroke = Brushes.White,
						StrokeThickness = 1
					};
					
					Canvas.SetLeft(dot, canvasPoint.X - 1.5);
					Canvas.SetTop(dot, canvasPoint.Y - 1.5);
					OverlayCanvas.Children.Add(dot);
				}
				
				// Add a legend
				var legendText = new TextBlock
				{
					Text = $"Touch Detection: {detectedTouchPixels.Count} pixels",
					Foreground = Brushes.White,
					FontSize = 12,
					FontWeight = FontWeights.Bold,
					Background = new SolidColorBrush(Color.FromArgb(128, 0, 0, 0))
				};
				Canvas.SetLeft(legendText, 10);
				Canvas.SetTop(legendText, 10);
				OverlayCanvas.Children.Add(legendText);
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in DrawDetectedTouchPixels: {ex.Message}");
			}
		}
		
		private void UpdateStatusInformation()
		{
			try
			{
				if (DetectionStatusText != null)
				{
					DetectionStatusText.Text = activeTouches.Count > 0 ? 
						$"Touches detected: {activeTouches.Count}" : 
						"No touches detected";
				}
				
				if (TouchCountText != null)
				{
					TouchCountText.Text = $"Active touches: {activeTouches.Count}";
				}
				
				if (DepthInfoText != null)
				{
					if (activeTouches.Count > 0)
					{
						var avgDepth = activeTouches.Average(t => t.Depth);
						DepthInfoText.Text = $"Avg depth: {avgDepth:F3}m";
					}
					else
					{
						DepthInfoText.Text = "Depth: --";
					}
				}
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in UpdateStatusInformation: {ex.Message}");
			}
		}

		private Point DepthToCanvas(Point depthPoint)
		{
			try
			{
				if (DepthImage?.Source == null || OverlayCanvas == null)
					return new Point(0, 0);
				
				double containerW = OverlayCanvas.ActualWidth > 0 ? OverlayCanvas.ActualWidth : OverlayCanvas.Width;
				double containerH = OverlayCanvas.ActualHeight > 0 ? OverlayCanvas.ActualHeight : OverlayCanvas.Height;
				if (containerW <= 0 || containerH <= 0) { containerW = 640; containerH = 480; }
				
				// Use depth resolution (512x424) for scaling
				int depthW = 512;
				int depthH = 424;
				
				double sx = containerW / depthW;
				double sy = containerH / depthH;
				double scale = Math.Min(sx, sy);
				double displayedW = depthW * scale;
				double displayedH = depthH * scale;
				double offsetX = (containerW - displayedW) / 2.0;
				double offsetY = (containerH - displayedH) / 2.0;
				
				double x = depthPoint.X * scale + offsetX;
				double y = depthPoint.Y * scale + offsetY;
				return new Point(x, y);
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in DepthToCanvas: {ex.Message}");
				return new Point(0, 0);
			}
		}

		private Point ColorToCanvas(ColorSpacePoint p)
		{
			try
			{
				if (DepthImage?.Source == null || OverlayCanvas == null)
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
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in ColorToCanvas: {ex.Message}");
				return new Point(0, 0);
			}
		}
		
		// CRITICAL: Proper coordinate mapping using Kinect SDK
		private bool IsDepthPointInTouchArea(int depthX, int depthY, ushort depthValue)
		{
			try
			{
				if (calibration?.TouchArea == null || kinectManager?.CoordinateMapper == null)
					return false;
				
				// Convert depth point to color space using Kinect's coordinate mapper
				var depthSpacePoint = new DepthSpacePoint { X = depthX, Y = depthY };
				var colorSpacePoint = kinectManager.CoordinateMapper.MapDepthPointToColorSpace(depthSpacePoint, depthValue);
				
				// Check if mapping was successful
				if (float.IsInfinity(colorSpacePoint.X) || float.IsInfinity(colorSpacePoint.Y) ||
					float.IsNaN(colorSpacePoint.X) || float.IsNaN(colorSpacePoint.Y))
					return false;
				
				// Check if the color point is within the TouchArea
				bool inTouchArea = (colorSpacePoint.X >= calibration.TouchArea.X && 
								   colorSpacePoint.X <= calibration.TouchArea.Right &&
								   colorSpacePoint.Y >= calibration.TouchArea.Y && 
								   colorSpacePoint.Y <= calibration.TouchArea.Bottom);
				
				// Log occasionally for debugging
				if (DateTime.Now.Millisecond % 500 == 0 && depthValue > 0)
				{
					LogToFile(GetDiagnosticPath(), $"COORDINATE CHECK: depth({depthX},{depthY}) -> color({colorSpacePoint.X:F1},{colorSpacePoint.Y:F1}) in TouchArea={inTouchArea}");
				}
				
				return inTouchArea;
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in IsDepthPointInTouchArea: {ex.Message}");
				return false;
			}
		}

		// Add this method to handle depth-to-color coordinate conversion
		private Point DepthToColorCoordinates(Point depthPoint)
		{
			try
			{
				if (kinectManager == null || !kinectManager.IsInitialized)
					return depthPoint;
					
				// Convert depth pixel to depth space point
				var depthSpacePoint = new DepthSpacePoint { X = (float)depthPoint.X, Y = (float)depthPoint.Y };
				
				// Get depth value at this point
				ushort[] depthData;
				int width, height;
				if (kinectManager.TryGetDepthFrameRaw(out depthData, out width, out height))
				{
					int index = (int)depthPoint.Y * width + (int)depthPoint.X;
					if (index >= 0 && index < depthData.Length)
					{
						ushort depthValue = depthData[index];
						if (depthValue > 0)
						{
							// Map depth point to color space
							var colorPoint = kinectManager.CoordinateMapper.MapDepthPointToColorSpace(depthSpacePoint, depthValue);
							return new Point(colorPoint.X, colorPoint.Y);
						}
					}
				}
				
				return depthPoint; // Fallback
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in DepthToColorCoordinates: {ex.Message}");
				return depthPoint;
			}
		}
		
		// Helper method to convert color coordinates to depth coordinates
		private Point ColorToDepthCoordinates(Point colorPoint)
		{
			try
			{
				if (kinectManager == null || !kinectManager.IsInitialized || kinectManager.CoordinateMapper == null)
				{
					// Fallback to simple scaling if Kinect is not available
					double scaleX = 512.0 / 1920.0;
					double scaleY = 424.0 / 1080.0;
					return new Point(colorPoint.X * scaleX, colorPoint.Y * scaleY);
				}
				
				// CRITICAL: Use Kinect's built-in coordinate mapping for ACCURATE conversion
				// This accounts for camera position differences, FOV differences, and lens differences
				try
				{
					// Use the existing KinectManager method to map color pixel to camera space
					CameraSpacePoint cameraPoint;
					if (kinectManager.TryMapColorPixelToCameraSpace((int)colorPoint.X, (int)colorPoint.Y, out cameraPoint))
					{
						// Convert camera space point to depth space
						// This is a more accurate approach than manual scaling
						var depthSpacePoint = kinectManager.CoordinateMapper.MapCameraPointToDepthSpace(cameraPoint);
						
						// Check if mapping was successful
						if (!float.IsInfinity(depthSpacePoint.X) && !float.IsInfinity(depthSpacePoint.Y) &&
							!float.IsNaN(depthSpacePoint.X) && !float.IsNaN(depthSpacePoint.Y))
						{
							LogToFile(GetDiagnosticPath(), $"ACCURATE ColorToDepth: ({colorPoint.X:F1},{colorPoint.Y:F1}) -> ({depthSpacePoint.X:F1},{depthSpacePoint.Y:F1}) [Kinect mapping]");
							return new Point(depthSpacePoint.X, depthSpacePoint.Y);
						}
						else
						{
							LogToFile(GetDiagnosticPath(), $"WARNING: Kinect mapping failed for ({colorPoint.X:F1},{colorPoint.Y:F1}) - using fallback");
						}
					}
					else
					{
						LogToFile(GetDiagnosticPath(), $"WARNING: Could not map color pixel ({colorPoint.X:F1},{colorPoint.Y:F1}) to camera space - using fallback");
					}
				}
				catch (Exception ex)
				{
					LogToFile(GetDiagnosticPath(), $"ERROR in Kinect coordinate mapping: {ex.Message}");
				}
				
				// FALLBACK: Enhanced coordinate mapping with field of view compensation
				// The Kinect color and depth cameras have different fields of view:
				// Color: ~62.0° × 48.6° (1920×1080)
				// Depth: ~58.5° × 46.6° (512×424)
				
				// Calculate the field of view scaling factors
				double colorFovX = 62.0; // degrees
				double colorFovY = 48.6; // degrees
				double depthFovX = 58.5; // degrees
				double depthFovY = 46.6; // degrees
				
				// Convert to radians
				double colorFovXRad = colorFovX * Math.PI / 180.0;
				double colorFovYRad = colorFovY * Math.PI / 180.0;
				double depthFovXRad = depthFovX * Math.PI / 180.0;
				double depthFovYRad = depthFovY * Math.PI / 180.0;
				
				// Calculate the scaling factors accounting for FOV differences
				double fovScaleX = Math.Tan(depthFovXRad / 2.0) / Math.Tan(colorFovXRad / 2.0);
				double fovScaleY = Math.Tan(depthFovYRad / 2.0) / Math.Tan(colorFovYRad / 2.0);
				
				// Apply FOV-compensated scaling
				double finalScaleX = (512.0 / 1920.0) * fovScaleX;
				double finalScaleY = (424.0 / 1080.0) * fovScaleY;
				
				// Apply scaling with center offset
				double depthX = (colorPoint.X - 960.0) * finalScaleX + 256.0; // Center at 960, scale, then center at 256
				double depthY = (colorPoint.Y - 540.0) * finalScaleY + 212.0; // Center at 540, scale, then center at 212
				
				LogToFile(GetDiagnosticPath(), $"FALLBACK ColorToDepth: ({colorPoint.X:F1},{colorPoint.Y:F1}) -> ({depthX:F1},{depthY:F1}) [FOV scales: {fovScaleX:F3},{fovScaleY:F3}]");
				
				return new Point(depthX, depthY);
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in ColorToDepthCoordinates: {ex.Message}");
				// Final fallback to simple scaling
				double scaleX = 512.0 / 1920.0;
				double scaleY = 424.0 / 1080.0;
				return new Point(colorPoint.X * scaleX, colorPoint.Y * scaleY);
			}
		}
		
		private void UpdateThresholdDisplay()
		{
			try
			{
				if (ThresholdValueText != null && PlaneThresholdSlider != null)
				{
					ThresholdValueText.Text = $"{PlaneThresholdSlider.Value:F3} m";
				}
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in UpdateThresholdDisplay: {ex.Message}");
			}
		}
		
		// UpdateTouchSizeDisplay and UpdateTouchAreaOffsetDisplay removed in simplified UI
		
		private void PlaneThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
		{
			try
			{
				UpdateThresholdDisplay();
				
				// Log the change for debugging
				LogToFile(GetDiagnosticPath(), $"SLIDER CHANGED: {e.NewValue:F3}m");
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in PlaneThresholdSlider_ValueChanged: {ex.Message}");
			}
		}
		
		// TouchSizeSlider and TouchArea offset sliders removed in simplified UI
		
		// ShowWallProjectionButton removed from simplified UI
		
		private void ShowTouchDetectionButton_Click(object sender, RoutedEventArgs e)
		{
			try
			{
				showTouchDetection = !showTouchDetection;
				
				if (showTouchDetection)
				{
					ShowTouchDetectionButton.Content = "Hide Touch Detection";
					ShowTouchDetectionButton.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xCC, 0xBC)); // Light orange
					LogToFile(GetDiagnosticPath(), "Touch detection visualization ENABLED - showing real-time touch detection");
				}
				else
				{
					ShowTouchDetectionButton.Content = "Show Touch Detection";
					ShowTouchDetectionButton.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x57, 0x22)); // Orange
					LogToFile(GetDiagnosticPath(), "Touch detection visualization DISABLED");
					detectedTouchPixels.Clear();
				}
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
				// Force background capture
				backgroundCaptured = false;
				LogToFile(GetDiagnosticPath(), "Background capture requested by user");
				
				MessageBox.Show("Background captured! Now touch the wall to test detection.", 
					"Background Captured", MessageBoxButton.OK, MessageBoxImage.Information);
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in CaptureBackgroundButton_Click: {ex.Message}");
			}
		}
		
		private void ProjectTouchAreaOnWall()
		{
			try
			{
				if (calibration?.TouchArea == null || calibration.TouchArea.Width <= 0 || calibration.TouchArea.Height <= 0)
				{
					LogToFile(GetDiagnosticPath(), "ERROR: Cannot project TouchArea - invalid TouchArea data");
					return;
				}
				
				// Create a new window to show the TouchArea boundaries
				var touchAreaWindow = new Window
				{
					Title = "Touch Area Boundaries - Project This on Wall",
					Width = 1920,
					Height = 1080,
					WindowState = WindowState.Maximized,
					WindowStyle = WindowStyle.None,
					Background = Brushes.Black,
					Topmost = true
				};
				
				// Create a visual representation of the TouchArea
				var canvas = new Canvas
				{
					Width = 1920,
					Height = 1080,
					Background = Brushes.Black
				};
				
				// Draw the TouchArea rectangle with bright borders
				var touchAreaRect = new Rectangle
				{
					Width = calibration.TouchArea.Width,
					Height = calibration.TouchArea.Height,
					Stroke = Brushes.Lime, // Bright green for visibility
					StrokeThickness = 8,
					Fill = new SolidColorBrush(Color.FromArgb(50, 0, 255, 0)) // Semi-transparent green
				};
				
				Canvas.SetLeft(touchAreaRect, calibration.TouchArea.X);
				Canvas.SetTop(touchAreaRect, calibration.TouchArea.Y);
				canvas.Children.Add(touchAreaRect);
				
				// Add corner markers
				var cornerSize = 20.0;
				var cornerColor = Brushes.Yellow;
				
				// Top-left corner
				var tlMarker = new Ellipse { Width = cornerSize, Height = cornerSize, Fill = cornerColor };
				Canvas.SetLeft(tlMarker, calibration.TouchArea.X - cornerSize/2);
				Canvas.SetTop(tlMarker, calibration.TouchArea.Y - cornerSize/2);
				canvas.Children.Add(tlMarker);
				
				// Top-right corner
				var trMarker = new Ellipse { Width = cornerSize, Height = cornerSize, Fill = cornerColor };
				Canvas.SetLeft(trMarker, calibration.TouchArea.Right - cornerSize/2);
				Canvas.SetTop(trMarker, calibration.TouchArea.Y - cornerSize/2);
				canvas.Children.Add(trMarker);
				
				// Bottom-left corner
				var blMarker = new Ellipse { Width = cornerSize, Height = cornerSize, Fill = cornerColor };
				Canvas.SetLeft(blMarker, calibration.TouchArea.X - cornerSize/2);
				Canvas.SetTop(blMarker, calibration.TouchArea.Bottom - cornerSize/2);
				canvas.Children.Add(blMarker);
				
				// Bottom-right corner
				var brMarker = new Ellipse { Width = cornerSize, Height = cornerSize, Fill = cornerColor };
				Canvas.SetLeft(brMarker, calibration.TouchArea.Right - cornerSize/2);
				Canvas.SetTop(brMarker, calibration.TouchArea.Bottom - cornerSize/2);
				canvas.Children.Add(brMarker);
				
				// Add text labels
				var titleText = new TextBlock
				{
					Text = "TOUCH AREA BOUNDARIES",
					Foreground = Brushes.White,
					FontSize = 48,
					FontWeight = FontWeights.Bold,
					HorizontalAlignment = HorizontalAlignment.Center
				};
				Canvas.SetLeft(titleText, calibration.TouchArea.X + calibration.TouchArea.Width/2 - 300);
				Canvas.SetTop(titleText, calibration.TouchArea.Y - 80);
				canvas.Children.Add(titleText);
				
				var instructionText = new TextBlock
				{
					Text = "Align the yellow rectangle in Screen 3 with this green area",
					Foreground = Brushes.White,
					FontSize = 24,
					HorizontalAlignment = HorizontalAlignment.Center
				};
				Canvas.SetLeft(instructionText, calibration.TouchArea.X + calibration.TouchArea.Width/2 - 400);
				Canvas.SetTop(instructionText, calibration.TouchArea.Bottom + 20);
				canvas.Children.Add(instructionText);
				
				// Add close instruction
				var closeText = new TextBlock
				{
					Text = "Press ESC to close this window",
					Foreground = Brushes.White,
					FontSize = 20,
					HorizontalAlignment = HorizontalAlignment.Center
				};
				Canvas.SetLeft(closeText, 20);
				Canvas.SetTop(closeText, 20);
				canvas.Children.Add(closeText);
				
				// Set the canvas as the window content
				touchAreaWindow.Content = canvas;
				
				// Add ESC key handler to close the window
				touchAreaWindow.KeyDown += (s, args) =>
				{
					if (args.Key == System.Windows.Input.Key.Escape)
					{
						touchAreaWindow.Close();
					}
				};
				
				touchAreaWindow.Show();
				
				LogToFile(GetDiagnosticPath(), $"TouchArea projected on wall: X={calibration.TouchArea.X:F1}, Y={calibration.TouchArea.Y:F1}, W={calibration.TouchArea.Width:F1}, H={calibration.TouchArea.Height:F1}");
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in ProjectTouchAreaOnWall: {ex.Message}");
			}
		}
		
		private void HideWallProjection()
		{
			try
			{
				// Close any existing touch area projection windows
				foreach (Window window in Application.Current.Windows)
				{
					if (window.Title == "Touch Area Boundaries - Project This on Wall")
					{
						window.Close();
					}
				}
				LogToFile(GetDiagnosticPath(), "Wall projection hidden");
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in HideWallProjection: {ex.Message}");
			}
		}

		private void FinishButton_Click(object sender, RoutedEventArgs e)
		{
			try
			{
				// Save threshold and touch size settings
			calibration.PlaneThresholdMeters = PlaneThresholdSlider.Value;
				
				// Add touch size to calibration config if not already present
				if (calibration.TouchDetectionSettings == null)
				{
					calibration.TouchDetectionSettings = new Dictionary<string, object>();
				}
				calibration.TouchDetectionSettings["TouchSizePixels"] = currentTouchSize;
				calibration.TouchDetectionSettings["TouchAreaXOffset"] = touchAreaXOffset;
				calibration.TouchDetectionSettings["TouchAreaYOffset"] = touchAreaYOffset;
				
			CalibrationStorage.Save(calibration);
				MessageBox.Show("Touch detection settings saved successfully!", "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
			this.DialogResult = true;
			}
			catch (Exception ex)
			{
				MessageBox.Show($"Error saving settings: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
			}
		}

		private void CancelButton_Click(object sender, RoutedEventArgs e)
		{
			this.Close();
		}
		
		// DEPTH CAMERA VIEW CONTROLS (like in video reference)
		// DepthCamera offset sliders removed in simplified UI
		
		private void DepthCameraZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
		{
			try
			{
				depthCameraZoom = e.NewValue;
				DepthCameraZoomValueText.Text = $"{depthCameraZoom:F1}x";
				LogToFile(GetDiagnosticPath(), $"Depth Camera Zoom: {depthCameraZoom:F1}x");
				UpdateUnifiedViewTransform();
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in DepthCameraZoomSlider_ValueChanged: {ex.Message}");
			}
		}
		
		private void ResetViewButton_Click(object sender, RoutedEventArgs e)
		{
			try
			{
				depthCameraXOffset = 0; depthCameraYOffset = 0; depthCameraZoom = 1.0;
				// Offset sliders removed in simplified UI
				if (DepthCameraZoomSlider != null) DepthCameraZoomSlider.Value = 1.0;
				UpdateUnifiedViewTransform();
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in ResetViewButton_Click: {ex.Message}");
			}
		}
		
		private void DiagnosticButton_Click(object sender, RoutedEventArgs e)
		{
			try
			{
				// Generate comprehensive diagnostics
				LogTouchDetectionDiagnostic();
				
				// Run depth data test
				TestDepthData();
				
				// Show user where the diagnostic file is located
				var diagnosticPath = GetDiagnosticPath();
				var diagnosticDir = System.IO.Path.GetDirectoryName(diagnosticPath);
				
				MessageBox.Show($"Diagnostics generated successfully!\n\nLocation: {diagnosticDir}\n\nFile: screen3_diagnostic.txt", 
					"Diagnostics Generated", MessageBoxButton.OK, MessageBoxImage.Information);
			}
			catch (Exception ex)
			{
				MessageBox.Show($"Error generating diagnostics: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
			}
		}
		
		private void TestDepthButton_Click(object sender, RoutedEventArgs e)
		{
			try
			{
				TestDepthData();
				MessageBox.Show("Depth data test completed! Check the diagnostic file for results.", 
					"Test Complete", MessageBoxButton.OK, MessageBoxImage.Information);
			}
			catch (Exception ex)
			{
				MessageBox.Show($"Error running depth test: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
			}
		}
		
		// Quick test method to verify depth data is working
		private void TestDepthData()
		{
			try
			{
				if (kinectManager != null && kinectManager.TryGetDepthFrameRaw(out ushort[] depthData, out int width, out int height))
				{
					var validDepths = depthData.Where(d => d > 0).ToArray();
					if (validDepths.Length > 0)
					{
						var minDepth = validDepths.Min() / 1000.0;
						var maxDepth = validDepths.Max() / 1000.0;
						var avgDepth = validDepths.Average(d => d / 1000.0);
						
						LogToFile(GetDiagnosticPath(), $"DEPTH TEST: Min={minDepth:F3}m, Max={maxDepth:F3}m, Avg={avgDepth:F3}m");
						LogToFile(GetDiagnosticPath(), $"Valid pixels: {validDepths.Length}/{depthData.Length}");
						
						// Test fallback detection with a very high threshold
						var testTouches = 0;
						
						for (int y = 0; y < height; y += 10) // Sample every 10th pixel
						{
							for (int x = 0; x < width; x += 10)
							{
								var index = y * width + x;
								if (index < depthData.Length)
								{
									var depth = depthData[index] / 1000.0;
									if (depth > 0.3 && depth < 2.0) // Fallback range
									{
										testTouches++;
									}
								}
							}
						}
						
						LogToFile(GetDiagnosticPath(), $"FALLBACK TEST: {testTouches} potential touches found with 0.3-2.0m range");
					}
					else
					{
						LogToFile(GetDiagnosticPath(), "DEPTH TEST: No valid depth data found");
					}
				}
				else
				{
					LogToFile(GetDiagnosticPath(), "DEPTH TEST: Could not get depth data");
				}
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"DEPTH TEST ERROR: {ex.Message}");
			}
		}
		
		// ===== COMPREHENSIVE DIAGNOSTIC METHODS =====
		
		private string GetDiagnosticPath()
		{
			try
			{
				string baseDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "KinectCalibrationDiagnostics");
				if (!System.IO.Directory.Exists(baseDir))
				{
					System.IO.Directory.CreateDirectory(baseDir);
				}
				// Try to use the most recent timestamped directory (yyyyMMdd_HHmmss) used by Screen 1/2
				string[] subDirs = System.IO.Directory.GetDirectories(baseDir);
				string chosenDir = null;
				if (subDirs != null && subDirs.Length > 0)
				{
					// pick latest folder name that matches timestamp pattern
					var ordered = subDirs
						.Select(d => new { Path = d, Name = System.IO.Path.GetFileName(d) })
						.Where(x => x.Name.Length == 15 && x.Name[8] == '_' && x.Name.All(ch => char.IsDigit(ch) || ch == '_'))
						.OrderByDescending(x => x.Name)
						.ToList();
					if (ordered.Count > 0)
					{
						chosenDir = ordered[0].Path;
					}
				}
				if (string.IsNullOrEmpty(chosenDir))
				{
					string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
					chosenDir = System.IO.Path.Combine(baseDir, timestamp);
					System.IO.Directory.CreateDirectory(chosenDir);
				}
				return System.IO.Path.Combine(chosenDir, "screen3_diagnostic.txt");
			}
			catch
			{
				// Fallback to temp
				string fallback = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "screen3_diagnostic.txt");
				return fallback;
			}
		}
		
		private void LogToFile(string path, string message)
		{
			try
			{
				var directory = System.IO.Path.GetDirectoryName(path);
				if (!string.IsNullOrEmpty(directory) && !System.IO.Directory.Exists(directory))
				{
					System.IO.Directory.CreateDirectory(directory);
				}
				System.IO.File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss.fff}] {message}" + Environment.NewLine);
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"LogToFile failed: {ex.Message}");
			}
		}
		
		private void InitializeScreen3Diagnostics()
		{
			var diagnosticPath = GetDiagnosticPath();
			LogToFile(diagnosticPath, "=== SCREEN 3 TOUCH TEST DIAGNOSTIC ===");
			LogToFile(diagnosticPath, $"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
			LogToFile(diagnosticPath, $"Diagnostic Directory: {System.IO.Path.GetDirectoryName(diagnosticPath)}");
			LogToFile(diagnosticPath, "");
			
			// Log system information
			LogToFile(diagnosticPath, "=== SYSTEM INFORMATION ===");
			LogToFile(diagnosticPath, $"OS Version: {Environment.OSVersion}");
			LogToFile(diagnosticPath, $"Machine Name: {Environment.MachineName}");
			LogToFile(diagnosticPath, $"User Name: {Environment.UserName}");
			LogToFile(diagnosticPath, $"Working Directory: {Environment.CurrentDirectory}");
			LogToFile(diagnosticPath, "");
		}
		
		private void ValidateCalibrationData()
		{
			var diagnosticPath = GetDiagnosticPath();
			LogToFile(diagnosticPath, "=== CALIBRATION DATA VALIDATION ===");
			
			try
			{
				// Check KinectManager
				if (kinectManager == null)
				{
					LogToFile(diagnosticPath, "ERROR: KinectManager is null");
					throw new InvalidOperationException("KinectManager is null");
				}
				LogToFile(diagnosticPath, $"KinectManager: OK (Initialized: {kinectManager.IsInitialized})");
				
				// Check CalibrationConfig
				if (calibration == null)
				{
					LogToFile(diagnosticPath, "ERROR: CalibrationConfig is null");
					throw new InvalidOperationException("CalibrationConfig is null");
				}
				LogToFile(diagnosticPath, "CalibrationConfig: OK");
				
				// Check TouchArea
				if (calibration.TouchArea == null)
				{
					LogToFile(diagnosticPath, "WARNING: TouchArea is null - creating default");
					calibration.TouchArea = new TouchAreaDefinition();
				}
				else
				{
					LogToFile(diagnosticPath, $"TouchArea: X={calibration.TouchArea.X:F1}, Y={calibration.TouchArea.Y:F1}, W={calibration.TouchArea.Width:F1}, H={calibration.TouchArea.Height:F1}");
				}
				
				// Check Plane
				if (calibration.Plane == null)
				{
					LogToFile(diagnosticPath, "WARNING: Plane is null - creating default");
					calibration.Plane = new PlaneDefinition();
				}
				else
				{
					LogToFile(diagnosticPath, $"Plane: Nx={calibration.Plane.Nx:F3}, Ny={calibration.Plane.Ny:F3}, Nz={calibration.Plane.Nz:F3}, D={calibration.Plane.D:F3}");
				}
				
				// Check PlaneThresholdMeters
				if (calibration.PlaneThresholdMeters <= 0)
				{
					LogToFile(diagnosticPath, "WARNING: PlaneThresholdMeters is invalid - setting default");
					calibration.PlaneThresholdMeters = 0.01;
				}
				LogToFile(diagnosticPath, $"PlaneThresholdMeters: {calibration.PlaneThresholdMeters:F3}");
				
				// Check TouchDetectionSettings
				if (calibration.TouchDetectionSettings == null)
				{
					LogToFile(diagnosticPath, "INFO: TouchDetectionSettings is null - creating new");
					calibration.TouchDetectionSettings = new Dictionary<string, object>();
				}
				else
				{
					LogToFile(diagnosticPath, $"TouchDetectionSettings: {calibration.TouchDetectionSettings.Count} settings");
				}
				
				LogToFile(diagnosticPath, "Calibration data validation: PASSED");
				LogToFile(diagnosticPath, "");
			}
			catch (Exception ex)
			{
				LogToFile(diagnosticPath, $"ERROR in ValidateCalibrationData: {ex.Message}");
				LogToFile(diagnosticPath, $"Stack Trace: {ex.StackTrace}");
				throw;
			}
		}
		
		private void LogTouchDetectionDiagnostic()
		{
			var diagnosticPath = GetDiagnosticPath();
			LogToFile(diagnosticPath, "=== TOUCH DETECTION DIAGNOSTIC ===");
			
			try
			{
				// Log current settings
				LogToFile(diagnosticPath, $"Current Threshold: {PlaneThresholdSlider?.Value ?? 0:F3}m");
				LogToFile(diagnosticPath, $"Current Touch Size: {currentTouchSize:F0}px");
				LogToFile(diagnosticPath, $"Min Blob Area: {minBlobAreaPoints} points");
				LogToFile(diagnosticPath, $"Smoothing Alpha: {smoothingAlpha:0.00}");
				LogToFile(diagnosticPath, $"Active Touches: {activeTouches.Count}");
				
				// Log Kinect status
				if (kinectManager != null)
				{
					LogToFile(diagnosticPath, $"Kinect Initialized: {kinectManager.IsInitialized}");
					LogToFile(diagnosticPath, $"Depth Stream Active: {kinectManager.IsDepthStreamActive()}");
					LogToFile(diagnosticPath, $"Color Stream Active: {kinectManager.IsColorStreamActive()}");
				}
				else
				{
					LogToFile(diagnosticPath, "ERROR: KinectManager is null");
				}
				
				// Log depth data statistics
				if (kinectManager != null && kinectManager.TryGetDepthFrameRaw(out ushort[] depthData, out int width, out int height))
				{
					LogToFile(diagnosticPath, $"Depth data: {width}x{height}, {depthData.Length} pixels");
					
					var validDepths = depthData.Where(d => d > 0).ToArray();
					if (validDepths.Length > 0)
					{
						var minDepth = validDepths.Min() / 1000.0; // Convert to meters
						var maxDepth = validDepths.Max() / 1000.0;
						var avgDepth = validDepths.Average(d => d / 1000.0);
						LogToFile(diagnosticPath, $"Depth range: {minDepth:F3}m - {maxDepth:F3}m, avg: {avgDepth:F3}m");
						LogToFile(diagnosticPath, $"Valid depth pixels: {validDepths.Length}/{depthData.Length} ({validDepths.Length * 100.0 / depthData.Length:F1}%)");
						
						// Log depth distribution
						var closeDepths = validDepths.Count(d => d / 1000.0 < 0.5); // Within 50cm
						var mediumDepths = validDepths.Count(d => d / 1000.0 >= 0.5 && d / 1000.0 < 1.0); // 50cm-1m
						var farDepths = validDepths.Count(d => d / 1000.0 >= 1.0); // Beyond 1m
						
						LogToFile(diagnosticPath, $"Depth distribution: Close(<0.5m): {closeDepths}, Medium(0.5-1m): {mediumDepths}, Far(>1m): {farDepths}");
					}
					else
					{
						LogToFile(diagnosticPath, "WARNING: No valid depth data found");
					}
				}
				else
				{
					LogToFile(diagnosticPath, "ERROR: Could not get depth data");
				}
				
				// Log calibration information
				LogToFile(diagnosticPath, $"Calibration Mode: {(calibration?.TouchArea != null && calibration?.Plane != null ? "CALIBRATED" : "FALLBACK")}");
				
				// Log coordinate mapping and ROI diagnostic
				LogToFile(diagnosticPath, "=== COORDINATE MAPPING DIAGNOSTIC ===");
				LogToFile(diagnosticPath, $"Color Camera Resolution: {kinectManager?.ColorWidth ?? 0}x{kinectManager?.ColorHeight ?? 0}");
				LogToFile(diagnosticPath, $"Depth Camera Resolution: {kinectManager?.DepthWidth ?? 0}x{kinectManager?.DepthHeight ?? 0}");
				LogToFile(diagnosticPath, $"TouchArea (Color Space): X={calibration?.TouchArea?.X ?? 0:F1}, Y={calibration?.TouchArea?.Y ?? 0:F1}, W={calibration?.TouchArea?.Width ?? 0:F1}, H={calibration?.TouchArea?.Height ?? 0:F1}");
				LogToFile(diagnosticPath, $"TouchArea (Depth Space): X={calibration?.TouchArea?.X * 512.0/1920.0 ?? 0:F1}, Y={calibration?.TouchArea?.Y * 424.0/1080.0 ?? 0:F1}");
				int roiMinX, roiMinY, roiMaxX, roiMaxY; ComputeDepthRoiBounds(kinectManager?.DepthWidth ?? 0, kinectManager?.DepthHeight ?? 0, out roiMinX, out roiMinY, out roiMaxX, out roiMaxY);
				LogToFile(diagnosticPath, $"ROI (Depth Space): [{roiMinX},{roiMinY}] - [{roiMaxX},{roiMaxY}] (w={Math.Max(0,roiMaxX-roiMinX+1)}, h={Math.Max(0,roiMaxY-roiMinY+1)})");
				
				// Log touch area information
				if (calibration?.TouchArea != null)
				{
					LogToFile(diagnosticPath, $"Touch Area: {calibration.TouchArea.X:F1},{calibration.TouchArea.Y:F1} - {calibration.TouchArea.Width:F1}x{calibration.TouchArea.Height:F1}");
				}
				else
				{
					LogToFile(diagnosticPath, "WARNING: TouchArea is null - using fallback detection");
				}
				
				// Log plane information
				if (calibration?.Plane != null)
				{
					double nrm = Math.Sqrt(calibration.Plane.Nx*calibration.Plane.Nx+calibration.Plane.Ny*calibration.Plane.Ny+calibration.Plane.Nz*calibration.Plane.Nz);
					LogToFile(diagnosticPath, $"Plane (raw): N=({calibration.Plane.Nx:F6},{calibration.Plane.Ny:F6},{calibration.Plane.Nz:F6}), |N|={nrm:F6}, D={calibration.Plane.D:F6}");
					LogToFile(diagnosticPath, $"Plane (normalized used): N'=({planeNx:F6},{planeNy:F6},{planeNz:F6}), D'={planeD:F6}, valid={isPlaneValid}");
				}
				else
				{
					LogToFile(diagnosticPath, "WARNING: Plane is null - using fallback detection");
				}

				// Signed plane distance sampling at ROI center
				CameraSpacePoint[] cps; int w, h;
				if (kinectManager.TryGetCameraSpaceFrame(out cps, out w, out h) && w>0 && h>0)
				{
					int cx = w/2, cy = h/2;
					if (cx>=0 && cy>=0 && cx<w && cy<h)
					{
						var cp = cps[cy*w+cx];
						if (isPlaneValid && !(float.IsNaN(cp.X)||float.IsNaN(cp.Y)||float.IsNaN(cp.Z)||float.IsInfinity(cp.X)||float.IsInfinity(cp.Y)||float.IsInfinity(cp.Z)))
						{
							double ds = KinectManager.KinectManager.DistancePointToPlaneSignedNormalized(cp, planeNx, planeNy, planeNz, planeD);
							LogToFile(diagnosticPath, $"ROI Center signed plane distance: {ds:F4} m");
						}
					}
				}
				
				// Log active touches
				if (activeTouches.Count > 0)
				{
					LogToFile(diagnosticPath, $"Active Touches ({activeTouches.Count}):");
					for (int i = 0; i < activeTouches.Count; i++)
					{
						var touch = activeTouches[i];
						LogToFile(diagnosticPath, $"  Touch {i}: Pos=({touch.Position.X:F1},{touch.Position.Y:F1}), Depth={touch.Depth:F3}m, Area={touch.Area}, Age={(DateTime.Now - touch.LastSeen).TotalMilliseconds:F0}ms");
					}
				}
				else
				{
					LogToFile(diagnosticPath, "No active touches detected");
				}
				
				LogToFile(diagnosticPath, "");
			}
			catch (Exception ex)
			{
				LogToFile(diagnosticPath, $"ERROR in LogTouchDetectionDiagnostic: {ex.Message}");
			}
		}

		private void NormalizeAndOrientPlane()
		{
			try
			{
				isPlaneValid = false;
				if (calibration == null || calibration.Plane == null) return;
				double nx = calibration.Plane.Nx, ny = calibration.Plane.Ny, nz = calibration.Plane.Nz, d = calibration.Plane.D;
				double norm = Math.Sqrt(nx * nx + ny * ny + nz * nz);
				if (norm < 1e-6) return;
				nx /= norm; ny /= norm; nz /= norm; d /= norm;
				// Orient toward camera origin (0,0,0)
				var p0x = -d * nx; var p0y = -d * ny; var p0z = -d * nz;
				double dot = nx * (-p0x) + ny * (-p0y) + nz * (-p0z);
				if (dot < 0) { nx = -nx; ny = -ny; nz = -nz; d = -d; }
				planeNx = nx; planeNy = ny; planeNz = nz; planeD = d; isPlaneValid = true;
				LogToFile(GetDiagnosticPath(), $"PLANE NORMALIZED: n=({planeNx:F4},{planeNy:F4},{planeNz:F4}), d={planeD:F4}");
			}
			catch (Exception ex)
			{
				isPlaneValid = false;
				LogToFile(GetDiagnosticPath(), $"ERROR in NormalizeAndOrientPlane: {ex.Message}");
			}
		}

		private void ComputeDepthRoiBounds(int depthWidth, int depthHeight, out int minX, out int minY, out int maxX, out int maxY)
		{
			minX = 0; minY = 0; maxX = depthWidth - 1; maxY = depthHeight - 1;
			try
			{
				if (calibration == null || calibration.TouchArea == null || calibration.TouchArea.Width <= 0 || calibration.TouchArea.Height <= 0)
					return;

				var colorTopLeft = new Point(calibration.TouchArea.X, calibration.TouchArea.Y);
				var colorTopRight = new Point(calibration.TouchArea.Right, calibration.TouchArea.Y);
				var colorBottomLeft = new Point(calibration.TouchArea.X, calibration.TouchArea.Bottom);
				var colorBottomRight = new Point(calibration.TouchArea.Right, calibration.TouchArea.Bottom);

				var depthTopLeft = ColorToDepthCoordinates(colorTopLeft);
				var depthTopRight = ColorToDepthCoordinates(colorTopRight);
				var depthBottomLeft = ColorToDepthCoordinates(colorBottomLeft);
				var depthBottomRight = ColorToDepthCoordinates(colorBottomRight);

				var xs = new double[] { depthTopLeft.X, depthTopRight.X, depthBottomLeft.X, depthBottomRight.X };
				var ys = new double[] { depthTopLeft.Y, depthTopRight.Y, depthBottomLeft.Y, depthBottomRight.Y };

				double left = xs.Min() + touchAreaXOffset;
				double right = xs.Max() + touchAreaXOffset;
				double top = ys.Min() + touchAreaYOffset;
				double bottom = ys.Max() + touchAreaYOffset;

				minX = (int)Math.Floor(left) - 2;
				maxX = (int)Math.Ceiling(right) + 2;
				minY = (int)Math.Floor(top) - 2;
				maxY = (int)Math.Ceiling(bottom) + 2;
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in ComputeDepthRoiBounds: {ex.Message}");
			}
		}


		private void MinBlobAreaSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
		{
			try
			{
				minBlobAreaPoints = (int)Math.Round(e.NewValue);
				if (MinBlobAreaValueText != null) MinBlobAreaValueText.Text = $"{minBlobAreaPoints} pts";
			}
			catch (Exception) { }
		}

		private void SmoothingAlphaSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
		{
			try
			{
				smoothingAlpha = e.NewValue;
				// SmoothingAlphaValueText removed in simplified UI
			}
			catch (Exception) { }
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

		// FIX: Proper camera feed positioning
		private void FixDepthCameraDisplay()
		{
			try
			{
				// Ensure the depth view container is properly sized
				if (DepthViewContainer != null)
				{
					DepthViewContainer.Width = 640;  // Set explicit width
					DepthViewContainer.Height = 480; // Set explicit height
					DepthViewContainer.Stretch = Stretch.Uniform;
					DepthViewContainer.StretchDirection = StretchDirection.Both;
					DepthViewContainer.HorizontalAlignment = HorizontalAlignment.Center;
					DepthViewContainer.VerticalAlignment = VerticalAlignment.Center;
				}
				
				// Ensure the overlay canvas matches
				if (OverlayCanvas != null)
				{
					OverlayCanvas.Width = 512;
					OverlayCanvas.Height = 424;
				}
				
				LogToFile(GetDiagnosticPath(), "Depth camera display fixed - Viewbox sized to 640x480");
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in FixDepthCameraDisplay: {ex.Message}");
			}
		}

		private void VerifyCalibrationButton_Click(object sender, RoutedEventArgs e)
		{
			try
			{
				string report = "=== CALIBRATION VERIFICATION ===\n\n";
				
				// Check Screen 1 data (Wall plane)
				report += "SCREEN 1 (Wall Plane):\n";
				if (calibration?.Plane != null)
				{
					report += $"✓ Plane: ({calibration.Plane.Nx:F3}, {calibration.Plane.Ny:F3}, {calibration.Plane.Nz:F3})\n";
					report += $"✓ Distance: {calibration.Plane.D:F3}\n";
				}
				else
				{
					report += "✗ No plane data!\n";
				}
				
				report += $"✓ Wall Distance: {calibration?.KinectToSurfaceDistanceMeters:F3}m\n\n";
				
				// Check Screen 2 data (TouchArea)
				report += "SCREEN 2 (TouchArea):\n";
				if (calibration?.TouchArea != null && calibration.TouchArea.Width > 0)
				{
					report += $"✓ Position: ({calibration.TouchArea.X}, {calibration.TouchArea.Y})\n";
					report += $"✓ Size: {calibration.TouchArea.Width} x {calibration.TouchArea.Height}\n";
				}
				else
				{
					report += "✗ No TouchArea data!\n";
				}
				
				// Check current settings
				report += "\nCURRENT SETTINGS:\n";
				report += $"✓ Sensitivity: {PlaneThresholdSlider?.Value:F3}m\n";
				report += $"✓ Min Touch Size: {MinBlobAreaSlider?.Value} points\n";
				report += $"✓ Touch Detection: {(showTouchDetection ? "ON" : "OFF")}\n";
				
				MessageBox.Show(report, "Calibration Status", MessageBoxButton.OK, MessageBoxImage.Information);
			}
			catch (Exception ex)
			{
				MessageBox.Show($"Error verifying calibration: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
			}
		}
	}
}
