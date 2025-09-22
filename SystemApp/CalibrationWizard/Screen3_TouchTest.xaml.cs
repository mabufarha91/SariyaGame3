using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Kinect;
using KinectCalibrationWPF.Models;
using KinectCalibrationWPF.Services;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace KinectCalibrationWPF.CalibrationWizard
{
	public partial class Screen3_TouchTest : Window
	{
		private KinectManager.KinectManager kinectManager;
		private DispatcherTimer updateTimer;
		private CalibrationConfig calibration;
		private List<TouchPoint> activeTouches = new List<TouchPoint>();
		private bool showTouchDetection = true; // Always show touch detection
		private List<Point> detectedTouchPixels = new List<Point>();
		
		
		// TOUCH DETECTION CACHING for performance
		private Rect cachedDepthTouchArea = Rect.Empty;
		private TouchAreaDefinition lastTouchAreaUpdate = null;
		
		
		// CONSTANT SCALING FACTORS
		private const double SCALE_X = 512.0 / 1920.0;  // 0.267
		private const double SCALE_Y = 424.0 / 1080.0;  // 0.393
		
		
		// TOUCH AREA MASK for performance optimization
		private bool[] touchAreaMask = null;
		
		// COORDINATE MAPPING CACHE for performance
		private Dictionary<Point, Point> coordinateMappingCache = new Dictionary<Point, Point>();
		
		// DISTANCE GRADIENT for solving Kinect angle problem
		private Dictionary<string, double> distanceGradientMap = null;
		private bool distanceGradientAvailable = false;
		private double distanceGradientMinDistance = 0.0;
		private double distanceGradientMaxDistance = 0.0;
		
		// PERFORMANCE OPTIMIZATION
		private WriteableBitmap depthBitmap; // Reuse depth bitmap
		
		// Add counter for debugging
		private static int gradientLookupCounter = 0;
		private int negativeDistances = 0; // Track how many objects are closer than expected
		
		
		
		private class TouchPoint
		{
			public Point Position { get; set; }
			public DateTime LastSeen { get; set; }
			public double Depth { get; set; }
			public Rectangle VisualElement { get; set; }
			public int Area { get; set; }
		}

		private class Blob { public Point Center; public int Area; }
		
		public struct Plane
		{
			public double Nx, Ny, Nz, D;
		}

		// Plane cache (normalized and oriented toward camera)
		private bool isPlaneValid = false;
		private double planeNx = 0, planeNy = 0, planeNz = 0, planeD = 0;

		// Unified view transform for depth image and overlay
		private TransformGroup depthViewTransformGroup;
		private ScaleTransform depthScaleTransform;
		private ScaleTransform depthFlipTransform;
		private TranslateTransform depthTranslateTransform;

		private int minBlobAreaPoints = 100;
		private double smoothingAlpha = 0.30;
		
		// VARIABLE TOUCH SIZE DETECTION for different interaction types
		private int maxBlobAreaPoints = 1000; // Maximum touch size for large objects
		private string currentTouchMode = "Hand"; // "Hand", "Ball", "Custom"
		private Dictionary<string, int> touchModeSettings = new Dictionary<string, int>
		{
			{ "Hand", 100 },      // Realistic touches for hand detection
			{ "Ball", 200 },     // Medium touches for balls
			{ "Custom", 50 }     // Custom setting
		};

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
							
							// Load distance gradient from calibration
							if (calibration?.DistanceGradientMap != null && calibration.DistanceGradientMap.Count > 0)
							{
								distanceGradientMap = calibration.DistanceGradientMap;
								distanceGradientAvailable = true;
								distanceGradientMinDistance = calibration.DistanceGradientMinDistance;
								distanceGradientMaxDistance = calibration.DistanceGradientMaxDistance;
								
								LogToFile(GetDiagnosticPath(), $"Distance gradient loaded: {distanceGradientAvailable}");
								LogToFile(GetDiagnosticPath(), $"GRADIENT LOADED: {distanceGradientMap?.Count ?? 0} points, Available: {distanceGradientAvailable}");
								LogToFile(GetDiagnosticPath(), $"GRADIENT RANGE: {distanceGradientMinDistance:F3}m to {distanceGradientMaxDistance:F3}m");
							}
							else
							{
								LogToFile(GetDiagnosticPath(), "WARNING: No distance gradient available - using single threshold");
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
				
				// Run initial diagnostics
				RunInitialDiagnostics();
				
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
					// Sensitivity in millimeters (UI): keep value set by calibration if available
					PlaneThresholdSlider.Minimum = 5;     // 5mm min
					PlaneThresholdSlider.Maximum = 80;    // 80mm max
					if (PlaneThresholdSlider.Value < PlaneThresholdSlider.Minimum || PlaneThresholdSlider.Value > PlaneThresholdSlider.Maximum)
					{
						PlaneThresholdSlider.Value = 30; // default 30mm
					}
					UpdateThresholdDisplay();
					
					LogToFile(GetDiagnosticPath(), $"Threshold slider: {PlaneThresholdSlider.Value}mm (range: {PlaneThresholdSlider.Minimum}-{PlaneThresholdSlider.Maximum}mm)");
				}
				
				if (MinBlobAreaSlider != null)
				{
					MinBlobAreaSlider.Minimum = 5;
					MinBlobAreaSlider.Maximum = 1000; // Increased for large objects like balls
					MinBlobAreaSlider.Value = touchModeSettings[currentTouchMode];
					UpdateTouchSizeDisplay();
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
					// Slider expects mm; config stored in meters
					if (calibration != null && calibration.PlaneThresholdMeters > 0)
					{
						var mm = calibration.PlaneThresholdMeters * 1000.0;
						PlaneThresholdSlider.Value = Math.Max(PlaneThresholdSlider.Minimum, Math.Min(PlaneThresholdSlider.Maximum, mm));
					}
				}
				
				// Load offset values from calibration if available
				if (calibration?.TouchDetectionSettings != null)
				{
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
				// FIXED: Real-time performance - 30 FPS for smooth depth feed
				updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) }; // 30 FPS
			updateTimer.Tick += UpdateTimer_Tick;
			updateTimer.Start();
				
				LogToFile(GetDiagnosticPath(), "Update timer initialized successfully at 30 FPS");
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
				// FIXED: Process every frame for real-time performance
				UpdateDepthVisualization();
				PerformTouchDetection();
				UpdateVisualFeedback();
				UpdateStatusInformation();
			}
			catch (Exception ex)
			{
				var errorMsg = $"UpdateTimer_Tick Error: {ex.Message}";
				if (StatusText != null) StatusText.Text = errorMsg;
				if (DetectionStatusText != null) DetectionStatusText.Text = "Error in processing";
				LogToFile(GetDiagnosticPath(), errorMsg);
				LogToFile(GetDiagnosticPath(), $"Stack Trace: {ex.StackTrace}");
				
				// Reset error state after a delay
				var resetTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
				resetTimer.Tick += (s, args) => {
					resetTimer.Stop();
					if (StatusText != null) StatusText.Text = "Ready";
					if (DetectionStatusText != null) DetectionStatusText.Text = "Processing...";
				};
				resetTimer.Start();
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

				// Reuse bitmap instead of creating new one
				if (depthBitmap == null)
				{
					depthBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
				}
				
				var pixels = new byte[width * height * 4];
				
				// Use fixed depth range for visualization (no longer dependent on wall distance)
				double minDepth = 0.5; // 50cm minimum
				double maxDepth = 4.0; // 4m maximum
				
				// Pre-calculate thresholds for faster processing
				ushort minDepthRaw = (ushort)(minDepth * 1000);
				ushort maxDepthRaw = (ushort)(maxDepth * 1000);
				
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
						
							// Proper grayscale mapping (BGR format)
							pixels[i * 4] = intensity;     // Blue
							pixels[i * 4 + 1] = intensity; // Green
							pixels[i * 4 + 2] = intensity; // Red
							pixels[i * 4 + 3] = 255;       // Alpha
					}
				}
				
				// Update pixels in existing bitmap
				depthBitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
				
				// Update the image
				DepthImage.Source = depthBitmap;
				
				// FIX: Ensure proper canvas sizing
				if (OverlayCanvas != null)
				{
					OverlayCanvas.Width = width;
					OverlayCanvas.Height = height;
				}
				
				StatusText.Text = "Depth feed active";
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
					depthScaleTransform.ScaleX = 1.0; // Fixed zoom
					depthScaleTransform.ScaleY = 1.0; // Fixed zoom
				}
				if (depthFlipTransform != null)
				{
					bool flipEnabled = FlipDepthVerticallyCheckBox?.IsChecked == true;
					depthFlipTransform.ScaleY = flipEnabled ? -1.0 : 1.0;
				}
				if (depthTranslateTransform != null)
				{
					depthTranslateTransform.X = 0; // Fixed offset
					depthTranslateTransform.Y = 0; // Fixed offset
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
				
			// CRITICAL FIX: More aggressive touch cleanup with visual element removal
				var currentTime = DateTime.Now;
				var removedCount = activeTouches.RemoveAll(touch => 
				{
					bool shouldRemove = (currentTime - touch.LastSeen).TotalMilliseconds > 300; // was 100
					
					if (shouldRemove && touch.VisualElement != null)
					{
						// Remove visual element from overlay
						try
						{
							OverlayCanvas?.Children?.Remove(touch.VisualElement);
							LogToFile(GetDiagnosticPath(), $"Removed visual element for touch at ({touch.Position.X:F1}, {touch.Position.Y:F1})");
						}
						catch (Exception ex)
						{
							LogToFile(GetDiagnosticPath(), $"ERROR removing visual element: {ex.Message}");
						}
					}
					
					return shouldRemove;
				});
				
				if (removedCount > 0)
				{
					LogToFile(GetDiagnosticPath(), $"Removed {removedCount} expired touches and their visual elements");
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
		
		// ENHANCED: Keep existing sophisticated distance logic but add debugging and fix thresholds
		private void DetectTouchesInDepthData(ushort[] depthData, int width, int height)
		{
			try
			{
				if (!isPlaneValid)
				{
					StatusText.Text = "Wall plane is not valid. Please recalibrate from Screen 1.";
					DetectionStatusText.Text = "Wall plane not calibrated";
						return;
				}

				if (calibration?.TouchArea == null || 
					calibration.TouchArea.Width <= 0 || 
					calibration.TouchArea.Height <= 0)
				{
					LogToFile(GetDiagnosticPath(), "ERROR: No valid touch area from Screen 2");
					DetectionStatusText.Text = "No touch area defined";
					return;
				}
				
				// KEEP existing sophisticated threshold logic but add debugging
				var threshold = PlaneThresholdSlider.Value * 0.001; // Convert to meters
				var touchPixels = new List<Point>();
				
				LogToFile(GetDiagnosticPath(), $"DETECTION START: Using threshold {threshold*1000:F1}mm, gradient available: {distanceGradientAvailable}");

				// Get camera space points
				CameraSpacePoint[] cameraSpacePoints;
				int depthWidth, depthHeight;
				if (!kinectManager.TryGetCameraSpaceFrame(out cameraSpacePoints, out depthWidth, out depthHeight))
				{
					LogToFile(GetDiagnosticPath(), "WARNING: Camera space frame failed, using fallback");
					DetectTouchesUsingDepthData(depthData, width, height);
					return;
				}

				// Cache converted touch area only when needed
				if (cachedDepthTouchArea.IsEmpty || lastTouchAreaUpdate != calibration.TouchArea)
				{
					cachedDepthTouchArea = ConvertColorAreaToDepthArea(calibration.TouchArea);
					lastTouchAreaUpdate = calibration.TouchArea;
					LogToFile(GetDiagnosticPath(), $"CACHED touch area: {cachedDepthTouchArea.Width:F0}x{cachedDepthTouchArea.Height:F0}");
				}

				// Create mask if needed
				if (touchAreaMask == null)
				{
					PrecomputeCoordinateMapping();
					LogToFile(GetDiagnosticPath(), "Touch area mask recreated");
				}
				
				// Expand a bit to avoid edge misses due to mapping approximations
				var searchArea = InflateRect(cachedDepthTouchArea, 12, 12, width, height);
				
				// DEBUGGING: Track detection statistics
				int pixelsChecked = 0;
				int validCameraPoints = 0;
				int gradientLookups = 0;
				int detectedPixels = 0;
				int closerPixels = 0;
				negativeDistances = 0; // CRITICAL FIX: Reset counter each frame
				
				// KEEP existing sampling but add debugging
				for (int y = (int)searchArea.Y; y < (int)searchArea.Bottom; y += 4)
				{
					for (int x = (int)searchArea.X; x < (int)searchArea.Right; x += 4)
					{
						pixelsChecked++;
						int index = y * width + x;
						
						if (index >= 0 && index < cameraSpacePoints.Length)
						{
							CameraSpacePoint csp = cameraSpacePoints[index];
							if (float.IsInfinity(csp.X) || float.IsInfinity(csp.Y) || float.IsInfinity(csp.Z))
								continue;

							// Map this depth pixel to color-space for gradient lookup and area check
							ushort depthVal = depthData[index];
							var colorPt = kinectManager.CoordinateMapper.MapDepthPointToColorSpace(
								new DepthSpacePoint { X = x, Y = y }, depthVal);

							if (float.IsInfinity(colorPt.X) || float.IsInfinity(colorPt.Y)) continue;
							int cx = (int)Math.Round(colorPt.X);
							int cy = (int)Math.Round(colorPt.Y);
							if (cx < 0 || cy < 0 || cx >= 1920 || cy >= 1080) continue;

							// Enforce inclusion by Screen 2 color-space touch area
							var ta = calibration.TouchArea;
							if (cx < ta.Left || cx > ta.Right || cy < ta.Top || cy > ta.Bottom) continue;

							validCameraPoints++;

							double expectedDistance = GetExpectedDistanceFromGradient(cx, cy);
							double depthInMeters = csp.Z;

							bool isTouch = false;

							if (distanceGradientAvailable && expectedDistance > 0)
							{
								gradientLookups++;
								double distanceFromWall = expectedDistance - depthInMeters;
								if (gradientLookups <= 5)
								{
									LogToFile(GetDiagnosticPath(),
										$"SAMPLE {gradientLookups}: Expected={expectedDistance:F3}m, Actual={depthInMeters:F3}m, DistanceFromWall={distanceFromWall:F3}m, Threshold={threshold:F3}m");
								}
								if (distanceFromWall > 0) closerPixels++;
								if (distanceFromWall > 0.010 && distanceFromWall < threshold &&
									depthInMeters > 0.3 && depthInMeters < 4.0)
								{
									isTouch = true;
								}
							}

							// Plane-along-ray fallback (robust to gradient errors)
							if (!isTouch)
							{
								double expectedAlong = ExpectedDistanceToPlaneAlongRay(csp);
								if (!double.IsNaN(expectedAlong))
								{
									double actualAlong = Math.Sqrt(csp.X * csp.X + csp.Y * csp.Y + csp.Z * csp.Z);
									double deltaRay = expectedAlong - actualAlong;
									if (gradientLookups <= 5)
									{
										LogToFile(GetDiagnosticPath(), $"ALONG-RAY: Expected={expectedAlong:F3}m, Actual={actualAlong:F3}m, Delta={deltaRay:F3}m");
									}
									if (deltaRay > 0.010 && deltaRay < (threshold * 1.5) && depthInMeters > 0.3 && depthInMeters < 4.0)
									{
										isTouch = true;
									}
								}
							}

							if (isTouch)
							{
								touchPixels.Add(new Point(x, y));
								detectedPixels++;
							}
						}
					}
				}

				// ENHANCED: Detailed debugging output
				LogToFile(GetDiagnosticPath(), 
					$"DETECTION STATS: Checked={pixelsChecked}, Valid={validCameraPoints}, GradientLookups={gradientLookups}, Detected={detectedPixels}");
				
				// PROPER TOUCH TRACKING: Update existing touches instead of replacing them
				var blobs = SimpleCluster(touchPixels, 20);
				var now = DateTime.Now;
				var newTouches = new List<TouchPoint>();

				// Create new touches from detected blobs
				foreach (var blob in blobs)
				{
					if (blob.Area >= minBlobAreaPoints && blob.Area <= maxBlobAreaPoints)
					{
						newTouches.Add(new TouchPoint
						{
							Position = blob.Center,
							LastSeen = now,
							Area = blob.Area,
							Depth = 0
						});
					}
				}

				// CRITICAL FIX: Proper touch tracking instead of replacement
				var updatedTouches = new List<TouchPoint>();

				// Process each newly detected touch
				foreach (var newTouch in newTouches)
				{
					// Find existing touch near this position (within 30 pixels)
					var existingTouch = activeTouches.FirstOrDefault(t => 
						Math.Abs(t.Position.X - newTouch.Position.X) < 30 && 
						Math.Abs(t.Position.Y - newTouch.Position.Y) < 30);
					
					if (existingTouch != null)
					{
						// Update existing touch position and timestamp
						existingTouch.Position = newTouch.Position;
						existingTouch.LastSeen = now;
						existingTouch.Area = newTouch.Area;
						updatedTouches.Add(existingTouch);
						
						LogToFile(GetDiagnosticPath(), $"Updated existing touch at ({newTouch.Position.X:F1}, {newTouch.Position.Y:F1})");
					}
					else
					{
						// Add completely new touch
						newTouch.LastSeen = now;
						updatedTouches.Add(newTouch);
						
						LogToFile(GetDiagnosticPath(), $"Added new touch at ({newTouch.Position.X:F1}, {newTouch.Position.Y:F1})");
					}
				}

				// Keep existing touches that weren't updated this frame (for timeout cleanup)
				foreach (var existingTouch in activeTouches)
				{
					if (!updatedTouches.Any(t => Math.Abs(t.Position.X - existingTouch.Position.X) < 30 && 
											Math.Abs(t.Position.Y - existingTouch.Position.Y) < 30))
					{
						// Touch wasn't detected this frame but still exists - keep it for cleanup
						updatedTouches.Add(existingTouch);
					}
				}

				activeTouches = updatedTouches;
				detectedTouchPixels = touchPixels;
				
				// ENHANCED status with debugging info
				string detectionMode = distanceGradientAvailable ? "Gradient" : "Plane";
				DetectionStatusText.Text = $"Touches: {activeTouches.Count} ({detectionMode}, {threshold*1000:F1}mm, {closerPixels} closer, Area: {cachedDepthTouchArea.Width:F0}x{cachedDepthTouchArea.Height:F0})";
				StatusText.Text = $"Detection active - {detectedPixels} pixels detected";
				
				// Log significant detection events
				if (activeTouches.Count > 0)
				{
					LogToFile(GetDiagnosticPath(), 
						$"TOUCHES DETECTED: {activeTouches.Count} touches from {touchPixels.Count} pixels using {detectionMode} method");
				}
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in DetectTouchesInDepthData: {ex.Message}");
				DetectionStatusText.Text = "Error in touch detection";
				// AutoOpenDiagnosticFile(); // DISABLED
			}
		}
		
		// CORRECTED COORDINATE MAPPING METHOD
		// FIXED: Proper coordinate validation with error handling
		private bool IsPointInTouchArea(int x, int y, ushort depth)
		{
			try
			{
				// Convert depth point to color space
			var depthPoint = new DepthSpacePoint { X = x, Y = y };
			var colorPoint = kinectManager.CoordinateMapper.MapDepthPointToColorSpace(depthPoint, depth);

			if (!float.IsInfinity(colorPoint.X) && !float.IsInfinity(colorPoint.Y))
			{
				var touchArea = calibration.TouchArea;
					return (colorPoint.X >= touchArea.X && colorPoint.X <= touchArea.Right &&
							colorPoint.Y >= touchArea.Y && colorPoint.Y <= touchArea.Bottom);
				}
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in IsPointInTouchArea: {ex.Message}");
			}
			return false;
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
		
		
		
		
		private void ResetViewButton_Click(object sender, RoutedEventArgs e)
		{
			try
			{
				UpdateUnifiedViewTransform();
				
				// Force recreation of touch area mask
				touchAreaMask = null;
				LogToFile(GetDiagnosticPath(), "View reset to default and touch area mask invalidated");
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in ResetViewButton_Click: {ex.Message}");
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
				
				// Update touch mode settings when slider changes
				if (touchModeSettings.ContainsKey(currentTouchMode))
				{
					touchModeSettings[currentTouchMode] = minBlobAreaPoints;
				}
				
				UpdateTouchSizeDisplay();
				LogToFile(GetDiagnosticPath(), $"Min blob area changed to: {minBlobAreaPoints} ({currentTouchMode} mode)");
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in MinBlobAreaSlider_ValueChanged: {ex.Message}");
			}
		}
		
		
		// Touch mode selection for different interaction types
		private void UpdateTouchMode(string mode)
		{
			if (touchModeSettings.ContainsKey(mode))
			{
				currentTouchMode = mode;
				minBlobAreaPoints = touchModeSettings[mode];
				
				if (MinBlobAreaSlider != null)
				{
					MinBlobAreaSlider.Value = minBlobAreaPoints;
				}
				
				UpdateTouchSizeDisplay();
				LogToFile(GetDiagnosticPath(), $"Touch mode changed to: {mode} (min size: {minBlobAreaPoints} pixels)");
			}
		}
		
		private void UpdateTouchSizeDisplay()
		{
			if (MinBlobAreaValueText != null)
			{
				MinBlobAreaValueText.Text = $"{minBlobAreaPoints} pts ({currentTouchMode})";
			}
		}
		
		// Touch mode event handlers
		private void TouchMode_Changed(object sender, RoutedEventArgs e)
		{
			try
			{
				if (sender == HandTouchMode && HandTouchMode.IsChecked == true)
				{
					UpdateTouchMode("Hand");
				}
				else if (sender == BallTouchMode && BallTouchMode.IsChecked == true)
				{
					UpdateTouchMode("Ball");
				}
				else if (sender == CustomTouchMode && CustomTouchMode.IsChecked == true)
				{
					UpdateTouchMode("Custom");
				}
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in TouchMode_Changed: {ex.Message}");
			}
		}
		

		private void FinishButton_Click(object sender, RoutedEventArgs e)
		{
			try
			{
				MessageBox.Show("Touch detection test completed!", "Finish", MessageBoxButton.OK, MessageBoxImage.Information);
				// Persist the chosen sensitivity in meters
				if (calibration != null && PlaneThresholdSlider != null)
				{
					calibration.PlaneThresholdMeters = PlaneThresholdSlider.Value * 0.001;
					CalibrationStorage.Save(calibration);
				}
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

		// IMPLEMENTED METHODS - Critical functionality
		private void FixDepthCameraDisplay() 
		{
			try
			{
				// Ensure proper depth camera positioning and scaling
				if (DepthViewContainer != null)
				{
					DepthViewContainer.Stretch = Stretch.Uniform;
					DepthViewContainer.HorizontalAlignment = HorizontalAlignment.Center;
					DepthViewContainer.VerticalAlignment = VerticalAlignment.Center;
				}
				
				LogToFile(GetDiagnosticPath(), "Depth camera display fixed and positioned");
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in FixDepthCameraDisplay: {ex.Message}");
			}
		}

		private void InitializeScreen3Diagnostics() 
		{
			try
			{
				var diagnosticPath = GetDiagnosticPath();
				LogToFile(diagnosticPath, "=== SCREEN 3 DIAGNOSTIC STARTED ===");
				LogToFile(diagnosticPath, $"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
				LogToFile(diagnosticPath, "Screen 3 diagnostics initialized successfully");
				
				// AUTOMATIC: Open diagnostic file automatically for easy access
				// AutoOpenDiagnosticFile(); // DISABLED
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in InitializeScreen3Diagnostics: {ex.Message}");
			}
		}

		private void NormalizeAndOrientPlane() 
		{
			try
			{
				if (calibration?.Plane != null)
				{
					// Normalize plane normal vector
					double norm = Math.Sqrt(calibration.Plane.Nx * calibration.Plane.Nx + 
										   calibration.Plane.Ny * calibration.Plane.Ny + 
										   calibration.Plane.Nz * calibration.Plane.Nz);
					
					if (norm > 0.001)
					{
						planeNx = calibration.Plane.Nx / norm;
						planeNy = calibration.Plane.Ny / norm;
						planeNz = calibration.Plane.Nz / norm;
						planeD = calibration.Plane.D / norm;
						isPlaneValid = true;
						
						LogToFile(GetDiagnosticPath(), $"Plane normalized: N=({planeNx:F6}, {planeNy:F6}, {planeNz:F6}), D={planeD:F6}");
				}
				else
				{
						isPlaneValid = false;
						LogToFile(GetDiagnosticPath(), "ERROR: Plane normal vector has zero magnitude");
					}
				}
				else
				{
					isPlaneValid = false;
					LogToFile(GetDiagnosticPath(), "ERROR: No plane data available for normalization");
				}
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in NormalizeAndOrientPlane: {ex.Message}");
				isPlaneValid = false;
			}
		}
		private void UpdateThresholdDisplay()
		{
			try
			{
				if (ThresholdValueText != null && PlaneThresholdSlider != null)
				{
					double threshold = PlaneThresholdSlider.Value * 0.001;
					string status = distanceGradientAvailable ? "Adaptive" : "Fixed";
					ThresholdValueText.Text = $"{threshold:F3} m ({status})";
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
				
				// Remove old touch visual elements
				var elementsToRemove = OverlayCanvas.Children
					.OfType<Rectangle>()
					.Where(r => r.Tag?.ToString() == "TouchVisual")
					.ToList();
					
				foreach (var element in elementsToRemove)
				{
					OverlayCanvas.Children.Remove(element);
				}
				
				// Add visual elements for current active touches
					foreach (var touch in activeTouches)
					{
					var rect = new Rectangle
						{
							Width = 20,
							Height = 20,
							Fill = new SolidColorBrush(Colors.Red),
						Stroke = new SolidColorBrush(Colors.DarkRed),
						StrokeThickness = 2,
						Tag = "TouchVisual"
					};
					
					Canvas.SetLeft(rect, touch.Position.X - 10);
					Canvas.SetTop(rect, touch.Position.Y - 10);
					
					OverlayCanvas.Children.Add(rect);
					
					// Store reference for cleanup
					touch.VisualElement = rect;
				}
				
				// Draw touch area boundary (keep existing logic)
				DrawTouchAreaBoundary();
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
				if (OverlayCanvas == null || calibration?.TouchArea == null) return;
				
				// FIXED: Only create rectangle once, reuse it
				var existingRect = OverlayCanvas.Children.OfType<Rectangle>()
					.FirstOrDefault(r => r.Tag?.ToString() == "TouchAreaBoundary");
				
				if (existingRect == null)
				{
					// Prefer real mapping via current depth frame; fallback to proportional mapping
					Rect depthArea;
					ushort[] dd; int dw, dh;
					if (kinectManager.TryGetDepthFrameRaw(out dd, out dw, out dh))
					{
						var mapped = FindDepthAreaForColorArea(calibration.TouchArea, dd, dw, dh);
						depthArea = mapped ?? ConvertColorAreaToDepthArea(calibration.TouchArea);
					}
					else
					{
						depthArea = ConvertColorAreaToDepthArea(calibration.TouchArea);
					}

					var rect = new Rectangle
					{
						Width = Math.Max(1, depthArea.Width),
						Height = Math.Max(1, depthArea.Height),
						Stroke = new SolidColorBrush(Colors.Yellow),
						StrokeThickness = 2,
						Fill = new SolidColorBrush(Color.FromArgb(50, 255, 255, 0)),
						Tag = "TouchAreaBoundary"
					};
					
					Canvas.SetLeft(rect, depthArea.X);
					Canvas.SetTop(rect, depthArea.Y);
					
					OverlayCanvas.Children.Add(rect);
					
					LogToFile(GetDiagnosticPath(), $"Touch area boundary drawn: X={depthArea.X:F1}, Y={depthArea.Y:F1}, W={depthArea.Width:F1}, H={depthArea.Height:F1}");
				}
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in DrawTouchAreaBoundary: {ex.Message}");
			}
		}

		private Rect ConvertColorAreaToDepthArea(TouchAreaDefinition colorArea)
		{
			try
			{
				// FIX: Don't scale down the area - keep it the same relative size
				// Map the touch area to cover a reasonable portion of the depth frame
				
				// Calculate center position in depth frame
				double depthWidth = 512.0;
				double depthHeight = 424.0;
				
				// Map color area center to depth area center
				double colorCenterX = colorArea.X + colorArea.Width / 2;
				double colorCenterY = colorArea.Y + colorArea.Height / 2;
				
				// Convert center to depth coordinates (simple proportional mapping)
				double depthCenterX = (colorCenterX / 1920.0) * depthWidth;
				double depthCenterY = (colorCenterY / 1080.0) * depthHeight;
				
				// Keep the SAME relative size (don't shrink it!)
				double depthWidth_mapped = (colorArea.Width / 1920.0) * depthWidth;
				double depthHeight_mapped = (colorArea.Height / 1080.0) * depthHeight;
				
				// Position the area
				double x = Math.Max(0, depthCenterX - depthWidth_mapped / 2);
				double y = Math.Max(0, depthCenterY - depthHeight_mapped / 2);
				
				// Ensure it fits within bounds
				x = Math.Min(x, depthWidth - depthWidth_mapped);
				y = Math.Min(y, depthHeight - depthHeight_mapped);
				
				var mappedArea = new Rect(x, y, depthWidth_mapped, depthHeight_mapped);
				
				LogToFile(GetDiagnosticPath(), $"FIXED MAPPING: Color {colorArea.Width:F0}x{colorArea.Height:F0} -> Depth {mappedArea.Width:F0}x{mappedArea.Height:F0} at ({mappedArea.X:F1}, {mappedArea.Y:F1})");
				return mappedArea;
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in ConvertColorAreaToDepthArea: {ex.Message}");
				return new Rect(100, 100, 200, 200); // Fallback area
			}
		}

		// Helper: inflate rect with bounds clamping
		private Rect InflateRect(Rect r, int dx, int dy, int maxW, int maxH)
		{
			double x = Math.Max(0, r.X - dx);
			double y = Math.Max(0, r.Y - dy);
			double right = Math.Min(maxW, r.Right + dx);
			double bottom = Math.Min(maxH, r.Bottom + dy);
			return new Rect(x, y, Math.Max(0, right - x), Math.Max(0, bottom - y));
		}

		private double ExpectedDistanceToPlaneAlongRay(CameraSpacePoint p)
		{
			double len = Math.Sqrt(p.X * p.X + p.Y * p.Y + p.Z * p.Z);
			if (len < 1e-6) return double.NaN;
			double dx = p.X / len, dy = p.Y / len, dz = p.Z / len;
			double denom = planeNx * dx + planeNy * dy + planeNz * dz;
			if (Math.Abs(denom) < 1e-6) return double.NaN;
			double t = -planeD / denom; // intersection distance along ray
			if (t <= 0) return double.NaN;
			return t;
		}

		// Helper method to find depth area corresponding to color area using reverse mapping
		private Rect? FindDepthAreaForColorArea(TouchAreaDefinition colorArea, ushort[] depthData, int depthWidth, int depthHeight)
		{
			try
			{
				var colorPoints = new List<ColorSpacePoint>();
				var correspondingDepthPoints = new List<DepthSpacePoint>();
				
				// Sample points in the color area
				int sampleStep = 10; // Sample every 10th pixel for performance
				for (int y = (int)colorArea.Y; y < colorArea.Bottom; y += sampleStep)
				{
					for (int x = (int)colorArea.X; x < colorArea.Right; x += sampleStep)
					{
						colorPoints.Add(new ColorSpacePoint { X = x, Y = y });
					}
				}
				
				// Find depth points that map to these color points
				for (int dy = 0; dy < depthHeight; dy += sampleStep)
				{
					for (int dx = 0; dx < depthWidth; dx += sampleStep)
							{
								int depthIndex = dy * depthWidth + dx;
								if (depthIndex < depthData.Length && depthData[depthIndex] > 0)
								{
									var depthPoint = new DepthSpacePoint { X = dx, Y = dy };
							var mappedColorPoint = kinectManager.CoordinateMapper.MapDepthPointToColorSpace(depthPoint, depthData[depthIndex]);
							
							// Check if this depth point maps to our color area
							if (mappedColorPoint.X >= colorArea.X && mappedColorPoint.X <= colorArea.Right &&
								mappedColorPoint.Y >= colorArea.Y && mappedColorPoint.Y <= colorArea.Bottom)
							{
								correspondingDepthPoints.Add(depthPoint);
							}
						}
					}
				}
				
				if (correspondingDepthPoints.Count > 0)
				{
					// Calculate bounding box of corresponding depth points
					var minX = correspondingDepthPoints.Min(p => p.X);
					var maxX = correspondingDepthPoints.Max(p => p.X);
					var minY = correspondingDepthPoints.Min(p => p.Y);
					var maxY = correspondingDepthPoints.Max(p => p.Y);
					
					return new Rect(minX, minY, maxX - minX, maxY - minY);
				}
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in FindDepthAreaForColorArea: {ex.Message}");
			}
			
			return null;
		}

		// FIXED: Pre-compute coordinate mapping using fast scaling (no expensive search)
		private void PrecomputeCoordinateMapping()
		{
			try
			{
				// CRITICAL FIX: Check bounds AFTER setting cachedDepthTouchArea, not before
				if (calibration?.TouchArea == null) return;
				
				// Set the cached area first using the corrected mapping
				var touchArea = calibration.TouchArea;
				var depthArea = ConvertColorAreaToDepthArea(touchArea); // Use the corrected method
				
				// Store the depth area bounds
				cachedDepthTouchArea = depthArea;
				
				// NOW check bounds after setting the area
				if (cachedDepthTouchArea.Width <= 0 || cachedDepthTouchArea.Height <= 0)
				{
					LogToFile(GetDiagnosticPath(), $"ERROR: Invalid touch area dimensions: {cachedDepthTouchArea.Width}x{cachedDepthTouchArea.Height}");
					return;
				}
				
				coordinateMappingCache.Clear();
				
				// Pre-compute coordinate mapping cache using 1:1 mapping
				for (int y = (int)touchArea.Y; y < (int)touchArea.Bottom; y += 5)
				{
					for (int x = (int)touchArea.X; x < (int)touchArea.Right; x += 5)
					{
						var colorPoint = new Point(x, y);
						var depthPoint = new Point(x, y); // 1:1 mapping
						coordinateMappingCache[colorPoint] = depthPoint;
					}
				}
				
				// Create mask based on pre-computed bounds
				CreateTouchAreaMask(depthArea);
				
				LogToFile(GetDiagnosticPath(), $"Pre-computed coordinate mapping for {coordinateMappingCache.Count} points using 1:1 mapping: {depthArea}");
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in PrecomputeCoordinateMapping: {ex.Message}");
			}
		}

		
		// FIXED: Use cached coordinate mapping for instant lookup
		private Point ConvertColorPointToDepthPoint(Point colorPoint)
		{
			try
			{
				// First check cache for instant lookup
				if (coordinateMappingCache.ContainsKey(colorPoint))
				{
					return coordinateMappingCache[colorPoint];
				}
				
				// If not in cache, use simple scaling (much faster than search)
				var scaledPoint = new Point(colorPoint.X * SCALE_X, colorPoint.Y * SCALE_Y);
				
				// Cache the result for future lookups
				coordinateMappingCache[colorPoint] = scaledPoint;
				
				return scaledPoint;
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in ConvertColorPointToDepthPoint: {ex.Message}");
			return new Point(colorPoint.X * SCALE_X, colorPoint.Y * SCALE_Y);
			}
		}


		private void UpdateStatusInformation() 
		{
			try
			{
				if (TouchCountText != null)
				{
					TouchCountText.Text = $"Active touches: {activeTouches?.Count ?? 0}";
				}
				
				UpdateCalibrationStatus();
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in UpdateStatusInformation: {ex.Message}");
			}
		}

		private void UpdateCalibrationStatus()
		{
			try
			{
				if (DepthInfoText == null) return;

				var status = new StringBuilder();
				
				// Screen 1 status
				status.AppendLine($"Screen 1 (Plane): {(isPlaneValid ? "✓ Valid" : "✗ Invalid")}");
				
				// Screen 2 status
				bool touchAreaValid = calibration?.TouchArea != null && 
									 calibration.TouchArea.Width > 0 && 
									 calibration.TouchArea.Height > 0;
				status.AppendLine($"Screen 2 (Touch Area): {(touchAreaValid ? "✓ Valid" : "✗ Invalid")}");
				
				// Kinect status
				status.AppendLine($"Kinect: {(kinectManager?.IsInitialized == true ? "✓ Connected" : "✗ Disconnected")}");
				
				
				// Performance status
				if (touchAreaMask != null)
				{
					int maskPixels = touchAreaMask.Count(m => m);
					status.AppendLine($"Performance: ✓ Optimized ({maskPixels} pixels)");
					}
					else
					{
					status.AppendLine($"Performance: ⚠ Creating mask...");
					}
				
				DepthInfoText.Text = status.ToString();
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in UpdateCalibrationStatus: {ex.Message}");
			}
		}
		// ENHANCED: Distance gradient debugging
		private double GetExpectedDistanceFromGradient(int x, int y)
		{
			if (!distanceGradientAvailable || distanceGradientMap == null)
			{
				return calibration?.KinectToSurfaceDistanceMeters ?? 2.0;
			}
			
			// DEBUGGING: Log coordinate lookups for troubleshooting
			string key = $"{x},{y}";
			if (distanceGradientMap.ContainsKey(key))
			{
				double distance = distanceGradientMap[key];
				// Log occasional lookups for debugging (every 100th lookup)
				if (System.Threading.Interlocked.Increment(ref gradientLookupCounter) % 100 == 1)
				{
					LogToFile(GetDiagnosticPath(), $"GRADIENT LOOKUP: ({x},{y}) -> {distance:F3}m");
				}
				return distance;
			}
			
			// ENHANCED interpolation with debugging
			var interpolated = InterpolateDistanceFromNearbyPoints(x, y);
			if (gradientLookupCounter % 100 == 1)
				{
					LogToFile(GetDiagnosticPath(), $"INTERPOLATED: ({x},{y}) -> {interpolated:F3}m");
				}
			
			return interpolated;
		}
		
		private double InterpolateDistanceFromNearbyPoints(int x, int y)
		{
			// Find the 4 nearest sample points for bilinear interpolation
			var nearbyPoints = new List<(int x, int y, double distance)>();
			
			// Search in a 10-pixel radius for sample points
			for (int dy = -10; dy <= 10; dy += 5)
			{
				for (int dx = -10; dx <= 10; dx += 5)
				{
					string key = $"{x + dx},{y + dy}";
					if (distanceGradientMap.ContainsKey(key))
					{
						nearbyPoints.Add((x + dx, y + dy, distanceGradientMap[key]));
					}
				}
			}
			
			if (nearbyPoints.Count == 0)
			{
				// No nearby points found, use average distance
				return (distanceGradientMinDistance + distanceGradientMaxDistance) / 2.0;
			}
			
			// Use the closest point
			var closest = nearbyPoints.OrderBy(p => Math.Abs(p.x - x) + Math.Abs(p.y - y)).First();
			return closest.distance;
		}
		
		// ENHANCED: Automatic diagnostic access on performance issues
		private void LogTouchDetectionDiagnostic() 
		{
			try
			{
				if (touchAreaMask != null)
				{
					int maskPixels = touchAreaMask.Count(m => m);
					int totalPixels = touchAreaMask.Length;
					double efficiency = (double)maskPixels / totalPixels * 100;
					
					LogToFile(GetDiagnosticPath(), $"PERFORMANCE: Processing {maskPixels}/{totalPixels} pixels ({efficiency:F1}% efficiency)");
					LogToFile(GetDiagnosticPath(), $"TOUCH DETECTION: {activeTouches?.Count ?? 0} active touches detected");
					
					// AUTOMATIC: Open diagnostic file if performance is poor
					if (efficiency < 10.0) // Less than 10% efficiency
					{
						// AutoOpenDiagnosticFile(); // DISABLED
						LogToFile(GetDiagnosticPath(), "AUTOMATIC: Diagnostic file opened due to poor performance");
					}
				}
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in LogTouchDetectionDiagnostic: {ex.Message}");
			}
		}


		// Validation and Error Handling
		private bool ValidateTouchDetectionSetup()
		{
			bool isValid = true;
			
			// Validate plane data
			if (!isPlaneValid || calibration?.Plane == null)
			{
				LogToFile(GetDiagnosticPath(), "ERROR: Invalid plane data");
				isValid = false;
			}
			
			// Validate touch area
			if (calibration?.TouchArea == null || 
				calibration.TouchArea.Width <= 0 || 
				calibration.TouchArea.Height <= 0)
			{
				LogToFile(GetDiagnosticPath(), "ERROR: Invalid touch area");
				isValid = false;
			}
			
			// Validate Kinect connection
			if (kinectManager?.IsInitialized != true)
			{
				LogToFile(GetDiagnosticPath(), "ERROR: Kinect not initialized");
				isValid = false;
			}
			
			return isValid;
		}

		// COMPREHENSIVE CALIBRATION VALIDATION
		private bool ValidateCalibrationData()
		{
			bool isValid = true;
			
			// Check Screen 1 data with improved validation
			if (calibration?.Plane == null)
			{
				LogToFile(GetDiagnosticPath(), "ERROR: No plane data from Screen 1");
				isValid = false;
				isPlaneValid = false;
			}
			else
			{
				// Check if plane normal vector has reasonable magnitude (not too small)
				double norm = Math.Sqrt(calibration.Plane.Nx * calibration.Plane.Nx + 
									   calibration.Plane.Ny * calibration.Plane.Ny + 
									   calibration.Plane.Nz * calibration.Plane.Nz);
				
				if (norm < 0.1) // More reasonable threshold
				{
					LogToFile(GetDiagnosticPath(), $"ERROR: Plane normal vector too small: {norm:F6}");
					isValid = false;
					isPlaneValid = false;
				}
				else
				{
					// Normalize and cache the plane data for performance
					planeNx = calibration.Plane.Nx / norm;
					planeNy = calibration.Plane.Ny / norm;
					planeNz = calibration.Plane.Nz / norm;
					planeD = calibration.Plane.D / norm;
					isPlaneValid = true;
					LogToFile(GetDiagnosticPath(), $"SUCCESS: Valid plane data loaded from Screen 1: N=({planeNx:F6}, {planeNy:F6}, {planeNz:F6}), D={planeD:F6}");
				}
			}
			
			// Check Screen 2 data with enhanced validation
			if (calibration?.TouchArea == null ||
				calibration.TouchArea.Width <= 0 ||
				calibration.TouchArea.Height <= 0)
			{
				LogToFile(GetDiagnosticPath(), "ERROR: Invalid touch area from Screen 2");
				isValid = false;
			}
			else
			{
				// CRITICAL FIX: Validate touch area dimensions match Screen 2
				if (calibration.TouchArea.Width < 50 || calibration.TouchArea.Height < 50)
				{
					LogToFile(GetDiagnosticPath(), "ERROR: Touch area too small - may cause detection issues");
					isValid = false;
				}
				
				// Log touch area consistency check
				LogToFile(GetDiagnosticPath(), $"Touch area consistency check: {calibration.TouchArea.Width:F0}x{calibration.TouchArea.Height:F0} pixels");
				
				// Check if touch area is within reasonable bounds
				if (calibration.TouchArea.X < 0 || calibration.TouchArea.Y < 0 ||
					calibration.TouchArea.Right > 1920 || calibration.TouchArea.Bottom > 1080)
				{
					LogToFile(GetDiagnosticPath(), "WARNING: Touch area extends beyond camera bounds");
				}
				
				// Check if touch area has reasonable size
				if (calibration.TouchArea.Width < 100 || calibration.TouchArea.Height < 100)
				{
					LogToFile(GetDiagnosticPath(), "WARNING: Touch area is very small");
				}
				
				LogToFile(GetDiagnosticPath(), $"SUCCESS: Valid TouchArea loaded from Screen 2: X={calibration.TouchArea.X:F1}, Y={calibration.TouchArea.Y:F1}, W={calibration.TouchArea.Width:F1}, H={calibration.TouchArea.Height:F1}");
			}
			
			// Check Kinect connection
			if (kinectManager == null || !kinectManager.IsInitialized)
			{
				LogToFile(GetDiagnosticPath(), "ERROR: Kinect not initialized");
				isValid = false;
			}
			
			// Validate coordinate systems
			if (!ValidateCoordinateSystems())
			{
				LogToFile(GetDiagnosticPath(), "WARNING: Coordinate system validation failed");
			}
			
			// Invalidate touch area components when calibration changes
			if (isValid)
			{
				touchAreaMask = null;
				LogToFile(GetDiagnosticPath(), "Touch area mask invalidated - will be recreated on next frame");
			}
			else
			{
				// AUTOMATIC: Open diagnostic file when validation fails
				// AutoOpenDiagnosticFile(); // DISABLED
				LogToFile(GetDiagnosticPath(), "AUTOMATIC: Diagnostic file opened due to validation failure");
			}
			
			return isValid;
		}

		// Add coordinate system validation
		private bool ValidateCoordinateSystems()
		{
			try
			{
				if (calibration?.TouchArea == null)
				{
					LogToFile(GetDiagnosticPath(), "ERROR: TouchArea is null");
					return false;
				}
				
				// Validate TouchArea coordinates are in reasonable color camera range
				var touchArea = calibration.TouchArea;
				if (touchArea.X >= 0 && touchArea.X <= 1920 &&
					touchArea.Y >= 0 && touchArea.Y <= 1080 &&
					touchArea.Width > 0 && touchArea.Width <= 1920 &&
					touchArea.Height > 0 && touchArea.Height <= 1080)
				{
					LogToFile(GetDiagnosticPath(), $"SUCCESS: TouchArea in color coordinates: X={touchArea.X:F1}, Y={touchArea.Y:F1}, W={touchArea.Width:F1}, H={touchArea.Height:F1}");
					
					// Test coordinate conversion
					var testPoint = new Point(touchArea.X, touchArea.Y);
					var convertedPoint = ConvertColorPointToDepthPoint(testPoint);
					LogToFile(GetDiagnosticPath(), $"Coordinate conversion test: Color({testPoint.X:F1}, {testPoint.Y:F1}) -> Depth({convertedPoint.X:F1}, {convertedPoint.Y:F1})");
					
					return true;
				}
				else
				{
					LogToFile(GetDiagnosticPath(), $"ERROR: TouchArea coordinates out of range: X={touchArea.X}, Y={touchArea.Y}, W={touchArea.Width}, H={touchArea.Height}");
					return false;
				}
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in ValidateCoordinateSystems: {ex.Message}");
				return false;
			}
		}

		private void RunInitialDiagnostics()
		{
			var diagnosticPath = GetDiagnosticPath();
			LogToFile(diagnosticPath, "=== SCREEN 3 COMPREHENSIVE DIAGNOSTIC ===");
			LogToFile(diagnosticPath, $"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");

			// Log calibration data
			if (calibration != null)
			{
				LogToFile(diagnosticPath, "--- Calibration Data ---");
				LogToFile(diagnosticPath, $"TouchArea: X={calibration.TouchArea.X}, Y={calibration.TouchArea.Y}, W={calibration.TouchArea.Width}, H={calibration.TouchArea.Height}");
				LogToFile(diagnosticPath, $"Plane: Nx={calibration.Plane.Nx}, Ny={calibration.Plane.Ny}, Nz={calibration.Plane.Nz}, D={calibration.Plane.D}");
				LogToFile(diagnosticPath, $"Kinect to Surface Distance: {calibration.KinectToSurfaceDistanceMeters}m");
				
				// Distance gradient status
				LogToFile(diagnosticPath, $"Distance Gradient Status: {(distanceGradientAvailable ? "Available" : "Not Available")}");
				if (distanceGradientAvailable)
				{
					LogToFile(diagnosticPath, $"Gradient Points: {distanceGradientMap.Count}");
					LogToFile(diagnosticPath, $"Distance Range: {distanceGradientMinDistance:F3} - {distanceGradientMaxDistance:F3} meters");
				}
				
				// View transform status
				LogToFile(diagnosticPath, $"Depth View: optional vertical flip for display; no horizontal mirroring in detection");
				LogToFile(diagnosticPath, $"Adaptive Threshold: {(distanceGradientAvailable ? "Enabled" : "Disabled")}");
				
				// Touch size detection status
				LogToFile(diagnosticPath, $"Touch Mode: {currentTouchMode}");
				LogToFile(diagnosticPath, $"Min Touch Size: {minBlobAreaPoints} pixels");
				LogToFile(diagnosticPath, $"Max Touch Size: {maxBlobAreaPoints} pixels");
				LogToFile(diagnosticPath, $"Touch Mode Settings: {string.Join(", ", touchModeSettings.Select(kv => $"{kv.Key}={kv.Value}"))}");
			}
			else
			{
				LogToFile(diagnosticPath, "!!! CRITICAL: Calibration data is NULL. !!!");
			}

			// Log Kinect status
			if (kinectManager != null)
			{
				LogToFile(diagnosticPath, "--- Kinect Status ---");
				LogToFile(diagnosticPath, $"Is Initialized: {kinectManager.IsInitialized}");
				LogToFile(diagnosticPath, $"Color Stream Active: {kinectManager.IsColorStreamActive()}");
				LogToFile(diagnosticPath, $"Depth Stream Active: {kinectManager.IsDepthStreamActive()}");
			}
			else
			{
				LogToFile(diagnosticPath, "!!! CRITICAL: KinectManager is NULL. !!!");
			}

			// Log performance settings
			LogToFile(diagnosticPath, "--- Performance Settings ---");
			LogToFile(diagnosticPath, $"Frame Rate: 30 FPS (33ms interval)");
			LogToFile(diagnosticPath, $"Frame Skipping: None (real-time processing)");
			LogToFile(diagnosticPath, $"Depth Visualization: Every frame");
			LogToFile(diagnosticPath, $"Touch Detection: Every frame");

			// Log validation results
			LogToFile(diagnosticPath, "--- Validation Results ---");
			bool validationResult = ValidateCalibrationData();
			LogToFile(diagnosticPath, $"Calibration Valid: {validationResult}");
			
			// Add touch detection setup validation
			bool touchSetupValid = ValidateTouchDetectionSetup();
			LogToFile(diagnosticPath, $"Touch Detection Setup Valid: {touchSetupValid}");


			LogToFile(diagnosticPath, "=== END COMPREHENSIVE DIAGNOSTIC ===");
		}
		private string GetDiagnosticPath() 
		{ 
			var appDir = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
			var root = System.IO.Path.GetFullPath(System.IO.Path.Combine(appDir, "..", "..", ".."));
			return System.IO.Path.Combine(root, "diag", "screen3_diagnostic.txt");
		}
		
		// NEW: Automatic diagnostic file access
		// COMMENTED OUT: AutoOpenDiagnosticFile method disabled
		/*
		private void AutoOpenDiagnosticFile()
		{
			try
			{
				var path = GetDiagnosticPath();
				if (System.IO.File.Exists(path))
				{
					// Open diagnostic file automatically in background
					System.Diagnostics.Process.Start("notepad.exe", path);
					// LogToFile(path, "AUTOMATIC: Diagnostic file opened automatically for monitoring");
				}
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in AutoOpenDiagnosticFile: {ex.Message}");
			}
		}
		*/
		// ENHANCED: Automatic diagnostic access on errors
		private void LogToFile(string path, string message) 
		{ 
			try
			{
				System.IO.File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} - {message}\n");
				
				// AUTOMATIC: Open diagnostic file on critical errors
				if (message.Contains("CRITICAL") || message.Contains("ERROR") || message.Contains("FAILED"))
				{
					// AutoOpenDiagnosticFile(); // DISABLED
				}
			}
			catch (Exception ex)
			{
				// Fallback logging
				System.Diagnostics.Debug.WriteLine($"LogToFile Error: {ex.Message}");
			}
		}
		
		// Real-time plane detection helper methods
		private Plane? CalculateWallPlane(ushort[] depthData, int width, int height)
		{
			var touchArea = calibration.TouchArea;
			var points = new List<CameraSpacePoint>();

			// We're going to sample points from the TouchArea to find the wall
			for (int y = (int)touchArea.Y; y < (int)touchArea.Bottom; y += 20)
			{
				for (int x = (int)touchArea.X; x < (int)touchArea.Right; x += 20)
				{
					var depthPoint = new DepthSpacePoint { X = x, Y = y };
					int depthIndex = (int)(depthPoint.Y * width + depthPoint.X);

					if (depthIndex >= 0 && depthIndex < depthData.Length)
					{
						ushort depth = depthData[depthIndex];
						if (depth > 0)
						{
							var cameraPoint = kinectManager.CoordinateMapper.MapDepthPointToCameraSpace(depthPoint, depth);
							if (!float.IsInfinity(cameraPoint.X) && !float.IsInfinity(cameraPoint.Y) && !float.IsInfinity(cameraPoint.Z))
							{
								points.Add(cameraPoint);
							}
						}
					}
				}
			}

			if (points.Count < 10) // We need a good number of points to get an accurate plane
			{
				return null;
			}

			// Standard algorithm to find the best-fit plane for a set of points
			var centroid = new CameraSpacePoint
			{
				X = points.Average(p => p.X),
				Y = points.Average(p => p.Y),
				Z = points.Average(p => p.Z)
			};

			double xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;

			foreach (var p in points)
			{
				xx += (p.X - centroid.X) * (p.X - centroid.X);
				xy += (p.X - centroid.X) * (p.Y - centroid.Y);
				xz += (p.X - centroid.X) * (p.Z - centroid.Z);
				yy += (p.Y - centroid.Y) * (p.Y - centroid.Y);
				yz += (p.Y - centroid.Y) * (p.Z - centroid.Z);
				zz += (p.Z - centroid.Z) * (p.Z - centroid.Z);
			}

			var V = new double[3, 3]
			{
				{ xx, xy, xz },
				{ xy, yy, yz },
				{ xz, yz, zz }
			};

			var evecs = new double[3, 3];
			var evals = new double[3];
			Jacobi(V, evals, evecs);

			var normal = new Vector3D(evecs[0, 0], evecs[1, 0], evecs[2, 0]);
			var d = -(normal.X * centroid.X + normal.Y * centroid.Y + normal.Z * centroid.Z);

			return new Plane { Nx = normal.X, Ny = normal.Y, Nz = normal.Z, D = d };
		}

		private CameraSpacePoint? DepthToCameraSpace(int x, int y, ushort depth)
		{
			var depthPoint = new DepthSpacePoint { X = x, Y = y };
			var cameraPoint = kinectManager.CoordinateMapper.MapDepthPointToCameraSpace(depthPoint, depth);

			if (!float.IsInfinity(cameraPoint.X) && !float.IsInfinity(cameraPoint.Y) && !float.IsInfinity(cameraPoint.Z))
			{
				return cameraPoint;
			}
			return null;
		}

		private double DistanceToPlane(CameraSpacePoint point, Plane plane)
		{
			return point.X * plane.Nx + point.Y * plane.Ny + point.Z * plane.Nz + plane.D;
		}
		
		// ENHANCED: Better fallback detection with debugging
		private void DetectTouchesUsingDepthData(ushort[] depthData, int width, int height)
		{
			try
			{
				var threshold = PlaneThresholdSlider.Value * 0.001;
				var touchPixels = new List<Point>();
				
				LogToFile(GetDiagnosticPath(), $"FALLBACK DETECTION: Using depth data directly with {threshold*1000:F1}mm threshold");
				
				int checkedPixels = 0;
				int gradientSuccesses = 0;
				
				for (int y = 0; y < height; y += 5)
				{
					for (int x = 0; x < width; x += 5)
					{
						int index = y * width + x;
						if (index < depthData.Length)
						{
							checkedPixels++;
							ushort depth = depthData[index];
							if (depth > 0)
							{
								double depthInMeters = depth / 1000.0;

								// Map depth -> color for gradient and area check
								var colorPt = kinectManager.CoordinateMapper.MapDepthPointToColorSpace(
									new DepthSpacePoint { X = x, Y = y }, depth);
								if (float.IsInfinity(colorPt.X) || float.IsInfinity(colorPt.Y)) continue;
								int cx = (int)Math.Round(colorPt.X);
								int cy = (int)Math.Round(colorPt.Y);
								if (cx < 0 || cy < 0 || cx >= 1920 || cy >= 1080) continue;

								var ta = calibration.TouchArea;
								if (cx < ta.Left || cx > ta.Right || cy < ta.Top || cy > ta.Bottom) continue;

								double expectedDistance = GetExpectedDistanceFromGradient(cx, cy);

								if (distanceGradientAvailable && expectedDistance > 0)
								{
									gradientSuccesses++;
									double distanceFromWall = expectedDistance - depthInMeters;

									if (distanceFromWall > 0.010 &&
										distanceFromWall < threshold &&
										depthInMeters > 0.3 &&
										Math.Abs(distanceFromWall) < 1.000)
									{
										touchPixels.Add(new Point(x, y)); // depth coords
									}
								}
							}
						}
					}
				}
				
				LogToFile(GetDiagnosticPath(), 
					$"FALLBACK STATS: Checked={checkedPixels}, GradientHits={gradientSuccesses}, Detected={touchPixels.Count}");
				
				// PROPER TOUCH TRACKING: Update existing touches instead of replacing them
				var blobs = SimpleCluster(touchPixels, 20);
				var now = DateTime.Now;
				var newTouches = new List<TouchPoint>();

				// Create new touches from detected blobs
				foreach (var blob in blobs)
				{
					if (blob.Area >= minBlobAreaPoints && blob.Area <= maxBlobAreaPoints)
					{
						newTouches.Add(new TouchPoint
						{
							Position = blob.Center,
							LastSeen = now,
							Area = blob.Area,
							Depth = 0
						});
					}
				}

				// CRITICAL FIX: Proper touch tracking for fallback method too
				var updatedTouches = new List<TouchPoint>();

				// Process each newly detected touch
				foreach (var newTouch in newTouches)
				{
					// Find existing touch near this position (within 30 pixels)
					var existingTouch = activeTouches.FirstOrDefault(t => 
						Math.Abs(t.Position.X - newTouch.Position.X) < 30 && 
						Math.Abs(t.Position.Y - newTouch.Position.Y) < 30);
					
					if (existingTouch != null)
					{
						// Update existing touch position and timestamp
						existingTouch.Position = newTouch.Position;
						existingTouch.LastSeen = now;
						existingTouch.Area = newTouch.Area;
						updatedTouches.Add(existingTouch);
					}
					else
					{
						// Add completely new touch
						newTouch.LastSeen = now;
						updatedTouches.Add(newTouch);
					}
				}

				// Keep existing touches that weren't updated this frame (for timeout cleanup)
				foreach (var existingTouch in activeTouches)
				{
					if (!updatedTouches.Any(t => Math.Abs(t.Position.X - existingTouch.Position.X) < 30 && 
											Math.Abs(t.Position.Y - existingTouch.Position.Y) < 30))
					{
						// Touch wasn't detected this frame but still exists - keep it for cleanup
						updatedTouches.Add(existingTouch);
					}
				}

				activeTouches = updatedTouches;
				DetectionStatusText.Text = $"Touches (fallback): {activeTouches.Count}";
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in DetectTouchesUsingDepthData: {ex.Message}");
				DetectionStatusText.Text = "Error in fallback detection";
			}
		}
		
		// FIXED: Efficient touch area mask creation using pre-computed bounds
		private void CreateTouchAreaMask(Rect depthArea)
		{
			try
			{
				// Get depth data dimensions
				ushort[] depthData;
				int width, height;
				if (!kinectManager.TryGetDepthFrameRaw(out depthData, out width, out height))
				{
					LogToFile(GetDiagnosticPath(), "ERROR: Could not get depth data for mask creation");
					return;
				}
				
				// Create mask based on converted touch area using 1:1 mapping
				touchAreaMask = new bool[depthData.Length];
				LogToFile(GetDiagnosticPath(), $"Creating touch area mask: {depthArea.Width:F0}x{depthArea.Height:F0} at ({depthArea.X:F1}, {depthArea.Y:F1})");
				
				for (int y = (int)depthArea.Y; y < (int)depthArea.Bottom; y++)
				{
					for (int x = (int)depthArea.X; x < (int)depthArea.Right; x++)
					{
						int index = y * width + x;
						if (index >= 0 && index < depthData.Length)
						{
							touchAreaMask[index] = true;
						}
					}
				}
				
				int maskPixels = touchAreaMask.Count(m => m);
				LogToFile(GetDiagnosticPath(), $"Touch area mask created: {maskPixels} pixels enabled");
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in CreateTouchAreaMask: {ex.Message}");
				touchAreaMask = null;
			}
		}

		// Helper method to convert color coordinates to depth coordinates for visualization
		private Point ColorToDepthCoordinates(Point colorPoint)
		{
			try
			{
				// Use the KinectManager's method to convert color pixel to camera space
				CameraSpacePoint cameraPoint;
				if (kinectManager.TryMapColorPixelToCameraSpace((int)colorPoint.X, (int)colorPoint.Y, out cameraPoint))
				{
					if (!float.IsInfinity(cameraPoint.X) && !float.IsInfinity(cameraPoint.Y) && !float.IsInfinity(cameraPoint.Z))
					{
						// Convert camera space back to depth space
						var depthPoint = kinectManager.CoordinateMapper.MapCameraPointToDepthSpace(cameraPoint);
						
						if (!float.IsInfinity(depthPoint.X) && !float.IsInfinity(depthPoint.Y))
						{
							return new Point(depthPoint.X, depthPoint.Y);
						}
					}
				}
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in ColorToDepthCoordinates: {ex.Message}");
			}
			
			// Fallback: use approximate scaling if conversion fails
			return new Point(colorPoint.X * 0.27, colorPoint.Y * 0.39);
		}

		// Jacobi eigenvalue algorithm for plane fitting
		private void Jacobi(double[,] V, double[] evals, double[,] evecs)
		{
			int n = 3;
			for (int i = 0; i < n; i++)
			{
				for (int j = 0; j < n; j++)
				{
					evecs[i, j] = (i == j) ? 1.0 : 0.0;
				}
			}
			
			for (int i = 0; i < n; i++)
			{
				evals[i] = V[i, i];
			}
			
			double[] b = new double[n];
			double[] z = new double[n];
			
			for (int i = 0; i < n; i++)
			{
				b[i] = evals[i];
				z[i] = 0.0;
			}
			
			for (int iter = 0; iter < 50; iter++)
			{
				double sm = 0.0;
				for (int p = 0; p < n - 1; p++)
				{
					for (int q = p + 1; q < n; q++)
					{
						sm += Math.Abs(V[p, q]);
					}
				}
				
				if (sm == 0.0) break;
				
				double tresh = (iter < 3) ? 0.2 * sm / (n * n) : 0.0;
				
				for (int p = 0; p < n - 1; p++)
				{
					for (int q = p + 1; q < n; q++)
					{
						double g = 100.0 * Math.Abs(V[p, q]);
						
						if (iter > 3 && Math.Abs(evals[p]) + g == Math.Abs(evals[p]) && Math.Abs(evals[q]) + g == Math.Abs(evals[q]))
						{
							V[p, q] = 0.0;
						}
						else if (Math.Abs(V[p, q]) > tresh)
						{
							double h = evals[q] - evals[p];
							double t;
							
							if (Math.Abs(h) + g == Math.Abs(h))
							{
								t = V[p, q] / h;
							}
							else
							{
								double theta = 0.5 * h / V[p, q];
								t = 1.0 / (Math.Abs(theta) + Math.Sqrt(1.0 + theta * theta));
								if (theta < 0.0) t = -t;
							}
							
							double c = 1.0 / Math.Sqrt(1.0 + t * t);
							double s = t * c;
							double tau = s / (1.0 + c);
							h = t * V[p, q];
							z[p] -= h;
							z[q] += h;
							evals[p] -= h;
							evals[q] += h;
							V[p, q] = 0.0;
							
							for (int j = 0; j < p; j++)
							{
								g = V[j, p];
								h = V[j, q];
								V[j, p] = g - s * (h + g * tau);
								V[j, q] = h + s * (g - h * tau);
							}
							
							for (int j = p + 1; j < q; j++)
							{
								g = V[p, j];
								h = V[j, q];
								V[p, j] = g - s * (h + g * tau);
								V[j, q] = h + s * (g - h * tau);
							}
							
							for (int j = q + 1; j < n; j++)
							{
								g = V[p, j];
								h = V[q, j];
								V[p, j] = g - s * (h + g * tau);
								V[q, j] = h + s * (g - h * tau);
							}
							
							for (int j = 0; j < n; j++)
							{
								g = evecs[j, p];
								h = evecs[j, q];
								evecs[j, p] = g - s * (h + g * tau);
								evecs[j, q] = h + s * (g - h * tau);
							}
						}
					}
				}
				
				for (int p = 0; p < n; p++)
				{
					b[p] += z[p];
					evals[p] = b[p];
					z[p] = 0.0;
				}
			}
		}
	}
}

