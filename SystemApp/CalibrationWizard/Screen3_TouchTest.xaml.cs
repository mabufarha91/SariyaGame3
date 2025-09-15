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
using System.Windows.Media.Media3D;

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
		
		// WALL PROFILE for improved touch detection
		private ushort[] wallProfile = null;
		
		// TOUCH AREA MASK for performance optimization
		private bool[] touchAreaMask = null;
		private WriteableBitmap touchAreaBitmap;
		private Image touchAreaImage; // Single image control for the touch area
		
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
		
		// OPTIMIZED CALIBRATED PLANE TOUCH DETECTION METHOD (using Screen 1 & 2 data)
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

				var threshold = PlaneThresholdSlider.Value;
				var touchPixels = new List<Point>();

				// Get the raw camera space points from the Kinect Manager
				CameraSpacePoint[] cameraSpacePoints;
				int depthWidth, depthHeight;
				if (!kinectManager.TryGetCameraSpaceFrame(out cameraSpacePoints, out depthWidth, out depthHeight))
				{
					StatusText.Text = "No camera space data available";
					return; // No data, nothing to do
				}

				// This is the key optimization:
				// We create a "mask" of the touchable pixels once, so we don't have to do it on every frame.
				if (touchAreaMask == null)
				{
					CreateTouchAreaMask(depthData, width, height);
				}

				// Now, we only check the pixels that are inside the touch area
				for (int i = 0; i < cameraSpacePoints.Length; i++)
				{
					if (touchAreaMask[i])
					{
						CameraSpacePoint csp = cameraSpacePoints[i];

						if (float.IsInfinity(csp.X) || float.IsInfinity(csp.Y) || float.IsInfinity(csp.Z))
						{
							continue;
						}

						double distanceToWall = (planeNx * csp.X + planeNy * csp.Y + planeNz * csp.Z + planeD);

						if (distanceToWall > 0 && distanceToWall < threshold)
						{
							int x = i % width;
							int y = i / width;
							touchPixels.Add(new Point(x, y));
						}
					}
				}

				// Cluster the touch points into blobs
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
							Depth = 0 // Depth value is less important with this method
						});
					}
				}
				activeTouches = newTouches;
				
				// Update status
				DetectionStatusText.Text = $"Touches detected: {activeTouches.Count}";
				StatusText.Text = "Optimized calibrated detection active";
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in DetectTouchesInDepthData: {ex.Message}");
				DetectionStatusText.Text = "Error in touch detection";
			}
		}
		
		// CORRECTED COORDINATE MAPPING METHOD
		private bool IsPointInTouchArea(int x, int y, ushort depth)
		{
			var depthPoint = new DepthSpacePoint { X = x, Y = y };
			var colorPoint = kinectManager.CoordinateMapper.MapDepthPointToColorSpace(depthPoint, depth);

			if (!float.IsInfinity(colorPoint.X) && !float.IsInfinity(colorPoint.Y))
			{
				var touchArea = calibration.TouchArea;
				return (colorPoint.X >= touchArea.X && colorPoint.X <= touchArea.Right &&
						colorPoint.Y >= touchArea.Y && colorPoint.Y <= touchArea.Bottom);
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
		
		
		private void ResetViewButton_Click(object sender, RoutedEventArgs e)
		{
			try
			{
				depthCameraXOffset = 0;
				depthCameraYOffset = 0;
				depthCameraZoom = 1.0;
				UpdateUnifiedViewTransform();
				
				// Force recreation of touch area mask and bitmap
				touchAreaMask = null;
				touchAreaBitmap = null;
				touchAreaImage = null;
				LogToFile(GetDiagnosticPath(), "View reset to default and touch area mask/bitmap/image invalidated");
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
		private void ValidateCalibrationData() 
		{
			try
			{
				// Validate plane data from Screen 1
				if (calibration?.Plane != null)
				{
					planeNx = calibration.Plane.Nx;
					planeNy = calibration.Plane.Ny;
					planeNz = calibration.Plane.Nz;
					planeD = calibration.Plane.D;
					
					// Check if plane data is valid
					if (Math.Abs(planeNx) > 0.001 && Math.Abs(planeNy) > 0.001 && Math.Abs(planeNz) > 0.001)
					{
						isPlaneValid = true;
						LogToFile(GetDiagnosticPath(), $"SUCCESS: Valid plane data loaded from Screen 1: N=({planeNx:F6}, {planeNy:F6}, {planeNz:F6}), D={planeD:F6}");
					}
					else
					{
						isPlaneValid = false;
						LogToFile(GetDiagnosticPath(), "WARNING: Plane data from Screen 1 is invalid");
					}
				}
				else
				{
					isPlaneValid = false;
					LogToFile(GetDiagnosticPath(), "WARNING: No plane data found from Screen 1");
				}
				
				// Validate TouchArea from Screen 2
				if (calibration?.TouchArea != null && 
					calibration.TouchArea.Width > 0 && calibration.TouchArea.Height > 0)
				{
					LogToFile(GetDiagnosticPath(), $"SUCCESS: Valid TouchArea loaded from Screen 2: X={calibration.TouchArea.X:F1}, Y={calibration.TouchArea.Y:F1}, W={calibration.TouchArea.Width:F1}, H={calibration.TouchArea.Height:F1}");
					
					// Invalidate the touch area mask and bitmap since calibration data has changed
					touchAreaMask = null;
					touchAreaBitmap = null;
					touchAreaImage = null;
					LogToFile(GetDiagnosticPath(), "Touch area mask, bitmap, and image invalidated - will be recreated on next frame");
				}
				else
				{
					LogToFile(GetDiagnosticPath(), "WARNING: No valid TouchArea found from Screen 2");
					touchAreaMask = null;
					touchAreaBitmap = null;
					touchAreaImage = null;
				}
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in ValidateCalibrationData: {ex.Message}");
				isPlaneValid = false;
				touchAreaMask = null;
				touchAreaBitmap = null;
				touchAreaImage = null;
			}
		}
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
				if (OverlayCanvas == null) return;

				// If the bitmap hasn't been created yet, create it from the mask
				if (touchAreaBitmap == null && touchAreaMask != null)
				{
					int width = 512;
					int height = 424;
					touchAreaBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
					var pixels = new byte[width * height * 4];

					for (int i = 0; i < touchAreaMask.Length; i++)
					{
						if (touchAreaMask[i])
						{
							int index = i * 4;
							pixels[index] = 0;       // Blue
							pixels[index + 1] = 255; // Green
							pixels[index + 2] = 255; // Red
							pixels[index + 3] = 100; // Alpha (semi-transparent)
						}
					}
					touchAreaBitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
				}

				// Create the image control only once and reuse it
				if (touchAreaBitmap != null && touchAreaImage == null)
				{
					touchAreaImage = new Image { Source = touchAreaBitmap };
					OverlayCanvas.Children.Add(touchAreaImage);
				}
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in DrawTouchAreaBoundary: {ex.Message}");
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
				
				if (DepthInfoText != null)
				{
					if (isPlaneValid && calibration?.TouchArea != null)
					{
						if (touchAreaMask != null)
						{
							int maskPixels = touchAreaMask.Count(m => m);
							DepthInfoText.Text = $"Optimized detection: Ready ✓ ({maskPixels} pixels)";
						}
						else
						{
							DepthInfoText.Text = "Optimized detection: Creating mask...";
						}
					}
					else if (!isPlaneValid)
					{
						DepthInfoText.Text = "Screen 1 calibration: Missing ⚠️";
					}
					else if (calibration?.TouchArea == null)
					{
						DepthInfoText.Text = "Screen 2 calibration: Missing ⚠️";
					}
					else
					{
						DepthInfoText.Text = "Calibration: Incomplete ⚠️";
					}
				}
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in UpdateStatusInformation: {ex.Message}");
			}
		}
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
				}
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in LogTouchDetectionDiagnostic: {ex.Message}");
			}
		}

		private void RunInitialDiagnostics()
		{
			var diagnosticPath = GetDiagnosticPath();
			LogToFile(diagnosticPath, "=== SCREEN 3 INITIAL DIAGNOSTIC ===");
			LogToFile(diagnosticPath, $"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");

			// Log calibration data
			if (calibration != null)
			{
				LogToFile(diagnosticPath, "--- Calibration Data ---");
				LogToFile(diagnosticPath, $"TouchArea: X={calibration.TouchArea.X}, Y={calibration.TouchArea.Y}, W={calibration.TouchArea.Width}, H={calibration.TouchArea.Height}");
				LogToFile(diagnosticPath, $"Plane: Nx={calibration.Plane.Nx}, Ny={calibration.Plane.Ny}, Nz={calibration.Plane.Nz}, D={calibration.Plane.D}");
				LogToFile(diagnosticPath, $"Kinect to Surface Distance: {calibration.KinectToSurfaceDistanceMeters}m");
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
			LogToFile(diagnosticPath, "=== END INITIAL DIAGNOSTIC ===");
		}
		private string GetDiagnosticPath() 
		{ 
			var appDir = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
			return System.IO.Path.Combine(appDir, "..", "..", "screen3_diagnostic.txt");
		}
		private void LogToFile(string path, string message) { System.IO.File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} - {message}\n"); }
		
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
		
		// Create touch area mask for performance optimization
		private void CreateTouchAreaMask(ushort[] depthData, int width, int height)
		{
			try
			{
				touchAreaMask = new bool[depthData.Length];
				var touchArea = calibration.TouchArea;

				for (int i = 0; i < depthData.Length; i++)
				{
					ushort depth = depthData[i];
					if (depth > 0)
					{
						int x = i % width;
						int y = i / width;

						var depthPoint = new DepthSpacePoint { X = x, Y = y };
						var colorPoint = kinectManager.CoordinateMapper.MapDepthPointToColorSpace(depthPoint, depth);

						if (!float.IsInfinity(colorPoint.X) && !float.IsInfinity(colorPoint.Y))
						{
							if (colorPoint.X >= touchArea.X && colorPoint.X <= touchArea.Right &&
								colorPoint.Y >= touchArea.Y && colorPoint.Y <= touchArea.Bottom)
							{
								touchAreaMask[i] = true;
							}
						}
					}
				}
				LogToFile(GetDiagnosticPath(), "Touch area mask created.");
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
