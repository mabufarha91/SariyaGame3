using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
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
		// Performance guard for diagnostics
		private static readonly bool ENABLE_VERBOSE_DIAGNOSTICS = true; // Keep enabled for debugging
		
		// Throttling variables for performance optimization
		private int frameCounter = 0;
		private const int LOG_EVERY_N_FRAMES = 5; // Log every 5th frame to reduce I/O
		
		// Frame timing monitoring
		private DateTime lastFrameTime = DateTime.Now;
		private int frameTimingWarnings = 0;
		private double averageFrameTime = 33.0;
		
		// Performance monitoring
		private readonly System.Diagnostics.Stopwatch perfTimer = new System.Diagnostics.Stopwatch();
		private double avgDetectionTime = 0;
		
		// Adaptive noise floor
		private int[] deltaHist; // 64 bins for histogram
		
		private KinectManager.KinectManager kinectManager;
		private DispatcherTimer updateTimer;
		private CalibrationConfig calibration;
		private List<TouchPoint> activeTouches = new List<TouchPoint>();
		
		
		// TOUCH DETECTION CACHING for performance
		private Rect cachedDepthTouchArea = Rect.Empty;
		private bool coordinateMappingRefreshed = false;
		private bool singleTouchMode = false;
		
		
		// CONSTANT SCALING FACTORS
		private const double SCALE_X = 512.0 / 1920.0;  // 0.267
		private const double SCALE_Y = 424.0 / 1080.0;  // 0.393
		
		
		
		
		
		
		// PERFORMANCE OPTIMIZATION
		private WriteableBitmap depthBitmap; // Reuse depth bitmap
		private byte[] depthPixels; // PERFORMANCE FIX: Add this class member

		// Touch candidate buffers (reused each frame)
		private bool[] candidateMask;
		private byte[] neighborCount;
        private byte[] temporalCount;

		// Fast-tap (single-frame) burst override (feature flag)
		private static readonly bool ENABLE_FAST_TAP_BURST = true; // set false to disable
		// Store burst-eligible points for the current frame (encoded as long: (y<<32)|x)
		private readonly System.Collections.Generic.HashSet<long> tapBurstPoints = new System.Collections.Generic.HashSet<long>();

		// Flood detection and baseline correction
		private int floodFrameCounter = 0;
		private const int FLOOD_FRAME_TRIGGER = 5;
        private const double BASELINE_CORRECTION_THRESHOLD = 0.008; // 8 mm
        // Early flood gate (pre-density) uses its own counter
        private int earlyFloodCounter = 0;
        private const int EARLY_FLOOD_TRIGGER = 3;

		// Diagnostic logging counters
		private static int sampleCount = 0;
		private static int blobIndex = 0;
		
		
		
		private class TouchPoint
		{
			public Point Position { get; set; }
			public DateTime LastSeen { get; set; }
			public double Depth { get; set; }
			public int Area { get; set; }
			public int SeenCount { get; set; } // frames observed
		}

		
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

		private int warmupFrames = 10;
		private int framesSinceStart = 0;
		
		// Post-release guard fields for preventing ghost touches
		private bool hadTouchLastFrame = false;
		private DateTime guardUntil = DateTime.MinValue;

		
        // VARIABLE TOUCH SIZE DETECTION for different interaction types
        private int minBlobAreaPoints = 30;   // Minimum touch size (points)
        private int maxBlobAreaPoints = 1000; // Maximum touch size for large objects

        // Cached path for Screen 3 diagnostic text in Pictures (per session)
        private string screen3DiagnosticPath;
        private float[] deltaFiltered;

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
				
				DebugCalibrationLoad();
				
				// Add TouchAreaDefinition debugging
				if (calibration?.TouchArea != null)
				{
					LogToFile(GetDiagnosticPath(), $"ORIGINAL TOUCH AREA: X={calibration.TouchArea.X:F1}, Y={calibration.TouchArea.Y:F1}, W={calibration.TouchArea.Width:F1}, H={calibration.TouchArea.Height:F1}");
				}
				else
				{
					LogToFile(GetDiagnosticPath(), "ERROR: TouchArea is NULL - this will cause search area problems!");
				}
				
				// Initialize cachedDepthTouchArea immediately instead of waiting for first depth frame
				if (calibration?.TouchArea != null)
				{
					// Calculate the depth area immediately
					var touchArea = calibration.TouchArea;
					var depthArea = ConvertColorAreaToDepthArea(touchArea);
					cachedDepthTouchArea = depthArea;

					LogToFile(GetDiagnosticPath(), $"INITIALIZED TOUCH AREA CACHE: X={cachedDepthTouchArea.X:F1}, Y={cachedDepthTouchArea.Y:F1}, W={cachedDepthTouchArea.Width:F1}, H={cachedDepthTouchArea.Height:F1}");
					
					// Add this line after line 204 in InitializeTouchAreaCache:
					PrecomputeCoordinateMapping();
				}
				else
				{
					LogToFile(GetDiagnosticPath(), "ERROR: Cannot initialize touch area cache - calibration.TouchArea is null");
				}
				
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
				
				// Call this after loading calibration data
				NormalizeAndOrientPlane();
				
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
					// Max Touch Distance (I'_max): 8-15 mm (tight detection window)
					PlaneThresholdSlider.Minimum = 8;
					PlaneThresholdSlider.Maximum = 15;
					if (PlaneThresholdSlider.Value < PlaneThresholdSlider.Minimum || PlaneThresholdSlider.Value > PlaneThresholdSlider.Maximum)
					{
						PlaneThresholdSlider.Value = 15; // default 15mm (tighter detection, avoids wall bias)
					}
					
					LogToFile(GetDiagnosticPath(), $"Threshold slider: {PlaneThresholdSlider.Value}mm (range: {PlaneThresholdSlider.Minimum}-{PlaneThresholdSlider.Maximum}mm)");
				}
				
                if (MinBlobAreaSlider != null)
                {
                    // Min Object Size: 10-60 pixels
                    MinBlobAreaSlider.Minimum = 10;
                    MinBlobAreaSlider.Maximum = 60;
                    if (MinBlobAreaSlider.Value < MinBlobAreaSlider.Minimum || MinBlobAreaSlider.Value > MinBlobAreaSlider.Maximum)
                    {
                        MinBlobAreaSlider.Value = 30; // default 30 pts
                    }
                    minBlobAreaPoints = (int)Math.Round(MinBlobAreaSlider.Value);
                    if (MinBlobAreaValueText != null)
                    {
                        MinBlobAreaValueText.Text = $"{minBlobAreaPoints} pts";
                    }
                    LogToFile(GetDiagnosticPath(), $"Min Blob Area slider: {minBlobAreaPoints} pixels (range: {MinBlobAreaSlider.Minimum}-{MinBlobAreaSlider.Maximum} pixels)");
                }

				if (MaxBlobAreaSlider != null)
				{
					// Max Object Size: large enough for palms/balls (officer recommendation)
					// Values are now set in XAML, just log the current state
					maxBlobAreaPoints = (int)MaxBlobAreaSlider.Value;
					LogToFile(GetDiagnosticPath(), $"Max Blob Area slider: {MaxBlobAreaSlider.Value} pixels (range: {MaxBlobAreaSlider.Minimum}-{MaxBlobAreaSlider.Maximum} pixels)");
				}

				if (PlaneToleranceSlider != null)
				{
					PlaneToleranceSlider.Minimum = 1;
					PlaneToleranceSlider.Maximum = 5;
					PlaneToleranceSlider.TickFrequency = 0.5;
					if (PlaneToleranceSlider.Value < PlaneToleranceSlider.Minimum || PlaneToleranceSlider.Value > PlaneToleranceSlider.Maximum)
					{
						PlaneToleranceSlider.Value = 2.5; // More stable
					}
					LogToFile(GetDiagnosticPath(), $"Plane Tolerance slider: {PlaneToleranceSlider.Value}mm (range: {PlaneToleranceSlider.Minimum}-{PlaneToleranceSlider.Maximum}mm)");
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
				
				
				// Update display values
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
				// Frame timing monitoring
				var now = DateTime.Now;
				var elapsed = (now - lastFrameTime).TotalMilliseconds;
				lastFrameTime = now;
				averageFrameTime = averageFrameTime * 0.95 + elapsed * 0.05;
				if (elapsed > 40 && frameTimingWarnings++ < 5) 
					LogToFile(GetDiagnosticPath(), $"PERF WARN: Frame {elapsed:F1}ms (target 33ms)");

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
				
				// PERFORMANCE FIX: Reuse the pixel buffer instead of creating a new one every frame.
				if (depthPixels == null || depthPixels.Length != width * height * 4)
				{
					depthPixels = new byte[width * height * 4];
				}
				
				// Delta-to-plane visualization with narrow window (0-30mm in front of wall)
				CameraSpacePoint[] cameraSpacePoints;
				if (!kinectManager.TryGetCameraSpaceFrame(out cameraSpacePoints, out int cspWidth, out int cspHeight))
				{
					StatusText.Text = "No camera space data available";
					return;
				}

				// Create plane for delta calculation
				var plane = new Plane { Nx = (float)planeNx, Ny = (float)planeNy, Nz = (float)planeNz, D = (float)planeD };
				
				// Delta visualization parameters (use sliders directly)
				float maxDelta = (float)((PlaneThresholdSlider?.Value ?? 15.0) * 0.001f);
			float minDelta = (float)((PlaneToleranceSlider?.Value ?? 2.5) * 0.001f);
			// Clamp visualization minimum to 8mm so the view matches detection floor
			if (minDelta < 0.008f) minDelta = 0.008f;
				if (minDelta < 0f) minDelta = 0f;
				if (minDelta > maxDelta) minDelta = Math.Max(0f, maxDelta * 0.5f);
				
				// Process pixels for delta visualization
				for (int i = 0; i < depthData.Length; i++)
				{
					ushort depth = depthData[i];
					byte intensity = 0; // Default to black
					
					if (depth != 0 && i < cameraSpacePoints.Length)
					{
						var p = cameraSpacePoints[i];
						if (!float.IsInfinity(p.X) && !float.IsInfinity(p.Y) && !float.IsInfinity(p.Z))
						{
							double r = Math.Sqrt(p.X * p.X + p.Y * p.Y + p.Z * p.Z);
							if (r > 0.2 && r < 5.0)
							{
								float invR = (float)(1.0 / r);
								float dx = p.X * invR, dy = p.Y * invR, dz = p.Z * invR;
								float denom = (float)(plane.Nx * dx + plane.Ny * dy + plane.Nz * dz);
								
							if (Math.Abs(denom) >= 0.20f) // Match detection threshold (tightened)
								{
									float tExp = (float)(-plane.D / denom);
									if (tExp > 0 && tExp <= 8.0f)
									{
										float delta = (float)(tExp - r); // >0 means in front of plane
										
										// Only show pixels 0-30mm in front of wall
										if (delta >= minDelta && delta <= maxDelta)
										{
											// Map delta to intensity (closer to wall = brighter)
											float normalized = delta / maxDelta;
											intensity = (byte)(255 * (1.0f - normalized)); // Inverted so closer = brighter
										}
									}
								}
							}
						}
					}
					
					// Set grayscale pixel (BGR format)
					depthPixels[i * 4] = intensity;     // Blue
					depthPixels[i * 4 + 1] = intensity; // Green
					depthPixels[i * 4 + 2] = intensity; // Red
					depthPixels[i * 4 + 3] = 255;       // Alpha
				}
				
				// Update pixels in existing bitmap
				depthBitmap.WritePixels(new Int32Rect(0, 0, width, height), depthPixels, width * 4, 0);
				
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
			perfTimer.Restart();
			
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
				
				// Refresh coordinate mapping when depth data becomes available
				RefreshCoordinateMappingIfNeeded();
				
			// CRITICAL FIX: More aggressive touch cleanup with visual element removal
				var currentTime = DateTime.Now;
				var removedCount = activeTouches.RemoveAll(touch => 
				{
					bool shouldRemove = (currentTime - touch.LastSeen).TotalMilliseconds > 200; // unified TTL for consistency
					
					
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
					if (ENABLE_VERBOSE_DIAGNOSTICS) LogToFile(GetDiagnosticPath(), $"Depth data: {width}x{height}, {depthData?.Length ?? 0} pixels");
					DetectTouchesInDepthData(depthData, width, height);
				}
				else
				{
					if (ENABLE_VERBOSE_DIAGNOSTICS) LogToFile(GetDiagnosticPath(), "WARNING: Failed to get depth frame data");
				}
				
				// Performance monitoring
				perfTimer.Stop();
				var ms = perfTimer.Elapsed.TotalMilliseconds;
				avgDetectionTime = avgDetectionTime * 0.95 + ms * 0.05;
				if (ms > 25) LogToFile(GetDiagnosticPath(), $"SLOW DETECTION: {ms:F1}ms (avg {avgDetectionTime:F1}ms)");
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
            // Reset per-frame fast-tap cache
            if (ENABLE_FAST_TAP_BURST) tapBurstPoints.Clear();
			// Get depth-to-color map snapshot once per frame (performance fix)
			ColorSpacePoint[] d2c;
			int dw, dh;
			bool haveMap = kinectManager.TryGetDepthToColorMapSnapshot(out d2c, out dw, out dh);

			framesSinceStart++;
			if (framesSinceStart <= warmupFrames)
			{
				UpdateTouchTracking(new List<Point>());
				UpdateTouchVisuals(new List<Point>());
				DetectionStatusText.Text = $"Warming up ({framesSinceStart}/{warmupFrames})";
				return;
			}

			// Clear all borders every frame first (prevents "stuck" borders)
			var oldBorders = OverlayCanvas.Children
				.OfType<System.Windows.Shapes.Polygon>()
				.Where(p => p.Tag?.ToString() == "ContourBorder")
				.ToList();
			foreach (var poly in oldBorders) OverlayCanvas.Children.Remove(poly);

			// Reset diagnostic counters for this frame
			sampleCount = 0;
			blobIndex = 0;

			// Validate prerequisites (allow DEBUG-only fallback plane for testing)
			if (!isPlaneValid || calibration?.TouchArea == null)
			{
#if DEBUG
				bool hadFallback = false;
				if (!isPlaneValid)
				{
					planeNx = 0; planeNy = 0; planeNz = -1; planeD = 1.25; // approx 1.25m away
					isPlaneValid = true;
					hadFallback = true;
					LogToFile(GetDiagnosticPath(), "Warning: Using fallback plane for testing (DEBUG)");
				}
				if (calibration?.TouchArea == null)
				{
					LogToFile(GetDiagnosticPath(), "WARNING: TouchArea missing; cannot proceed without ROI");
					return;
				}
				// if we reached here and plane is now valid and ROI exists, continue
#else
				LogToFile(GetDiagnosticPath(), "WARNING: Invalid plane or touch area");
				return;
#endif
			}

			// Enhanced plane validation logging
			if (!isPlaneValid) {
				LogToFile(GetDiagnosticPath(), "CRITICAL: Plane is invalid - check Screen 1 calibration");
				LogToFile(GetDiagnosticPath(), $"Plane data: N=({planeNx:F6}, {planeNy:F6}, {planeNz:F6}), D={planeD:F6}");
			}
			if (calibration?.TouchArea == null) {
				LogToFile(GetDiagnosticPath(), "CRITICAL: TouchArea is null - check Screen 2 calibration");
			}

			// Camera space for this frame
				CameraSpacePoint[] cameraSpacePoints;
				int depthWidth, depthHeight;
				if (!kinectManager.TryGetCameraSpaceFrame(out cameraSpacePoints, out depthWidth, out depthHeight))
				{
				if (ENABLE_VERBOSE_DIAGNOSTICS) LogToFile(GetDiagnosticPath(), "WARNING: Camera space frame failed, skipping detection");
					return;
				}

			// Add safety check for buffer size mismatch
			if (cameraSpacePoints.Length != width * height)
				{
				LogToFile(GetDiagnosticPath(), $"WARNING: Buffer size mismatch - skipping frame");
						return;
					}

			// Allocate reusable buffers
			int len = width * height;
			if (candidateMask == null || candidateMask.Length != len)
			{
				candidateMask = new bool[len];
				neighborCount = new byte[len];
                temporalCount = new byte[len];
			}

			// Slider values (mm â†’ m)
			double objMm = PlaneThresholdSlider != null ? PlaneThresholdSlider.Value : 15.0; // 3..30mm
			double tolMm = PlaneToleranceSlider != null ? PlaneToleranceSlider.Value : 2.5; // 1..5mm

			float thrM = (float)(objMm * 0.001);  // Î´_max

		var now = DateTime.Now;

		float baseMinPos = (float)Math.Max(0.008f, tolMm * 0.001f);
		// brief guard adds ~1.0mm, capped to keep below contactOn
		float guardMinPos = baseMinPos;

			// Contact detection threshold (must be > guardMinPos)
			float contactOn = thrM; // Use full threshold, no cap
			if (now <= guardUntil)
			{
				guardMinPos = Math.Min(contactOn - 0.0006f, baseMinPos + 0.0010f);
			}
			// Add safety to enforce guardMinPos < contactOn once per frame
			if (guardMinPos >= contactOn) guardMinPos = Math.Max(baseMinPos, contactOn - 0.0006f);

			// Work area (robust fallback if cached area is invalid or too small)
			Rect area;
			if (cachedDepthTouchArea.IsEmpty || cachedDepthTouchArea.Width < 40 || cachedDepthTouchArea.Height < 40)
			{
				area = new Rect(0, 0, width, height); // fallback to full frame
				if (ENABLE_VERBOSE_DIAGNOSTICS) LogToFile(GetDiagnosticPath(), "ROI fallback: using full frame");
							}
							else
							{
				area = InflateRect(cachedDepthTouchArea, 12, 12, width, height); // slightly wider margin
			}

			// Add ROI debugging as requested by Officer 2 (throttled for performance)
			if (ENABLE_VERBOSE_DIAGNOSTICS && frameCounter % LOG_EVERY_N_FRAMES == 0) 
			{
				LogToFile(GetDiagnosticPath(), $"ROI Search Area: {area.X},{area.Y},{area.Width}x{area.Height} (depth coordinates)");
				LogToFile(GetDiagnosticPath(), $"ColorDepth Mapping: TouchArea({calibration.TouchArea.X},{calibration.TouchArea.Y},{calibration.TouchArea.Width}x{calibration.TouchArea.Height}) + ROI({area.X},{area.Y},{area.Width}x{area.Height})");
				LogToFile(GetDiagnosticPath(), $"BLOB SIZE LIMITS: Min={MinBlobAreaSlider?.Value ?? 45}, Max={MaxBlobAreaSlider?.Value ?? 200}");
				LogToFile(GetDiagnosticPath(), $"THRESHOLD SETTINGS: ObjectHeight={PlaneThresholdSlider?.Value ?? 15}mm, Tolerance={PlaneToleranceSlider?.Value ?? 2.5}mm");
				LogToFile(GetDiagnosticPath(), $"BLOB LIMITS (effective): Min={minBlobAreaPoints}, Max={maxBlobAreaPoints}");
			}

			// Add detection pipeline debugging as requested (throttled for performance)
			if (ENABLE_VERBOSE_DIAGNOSTICS && frameCounter % LOG_EVERY_N_FRAMES == 0) {
				LogToFile(GetDiagnosticPath(), $"DETECTION START: ROI={area.X},{area.Y},{area.Width}x{area.Height}, Thresholds: minDelta={guardMinPos*1000:F1}mm, maxDelta={thrM*1000:F1}mm, contactOn={contactOn*1000:F1}mm");
				LogToFile(GetDiagnosticPath(), $"PLANE VALID: {isPlaneValid}, TOUCH AREA: {(calibration?.TouchArea != null ? "Valid" : "NULL")}");
			}
			frameCounter++;

			int ax = Math.Max(0, (int)area.X);
			int ay = Math.Max(0, (int)area.Y);
			int bx = Math.Min(width, (int)area.Right);
			int by = Math.Min(height, (int)area.Bottom);
			
			// Add camera space logging after coordinates are calculated
			if (ENABLE_VERBOSE_DIAGNOSTICS && frameCounter % LOG_EVERY_N_FRAMES == 0) {
				LogToFile(GetDiagnosticPath(), $"CAMERA SPACE: {cameraSpacePoints?.Length ?? 0} points, ROI pixels: {(bx-ax)*(by-ay)}");
			}

			int deltaFilterCount = 0; // Track pixels that pass delta filter

			// Clear masks in the region only
			for (int y = ay; y < by; y++)
			{
				int row = y * width;
				for (int x = ax; x < bx; x++)
				{
					int i = row + x;
					candidateMask[i] = false;
					neighborCount[i] = 0;
				}
			}

			// Plane (normalized & oriented earlier)
			var plane = new Plane { Nx = (float)planeNx, Ny = (float)planeNy, Nz = (float)planeNz, D = (float)planeD };

			// Capture wall baseline
			double wallMedian = ComputeNormalDistanceMedian(cameraSpacePoints, width, height, ax, ay, bx, by, plane);

			// COMPREHENSIVE DIAGNOSTIC: Ray-based algorithm validation
			if (ENABLE_VERBOSE_DIAGNOSTICS) LogToFile(GetDiagnosticPath(), $"=== RAY-BASED ALGORITHM VALIDATION ===");
			if (ENABLE_VERBOSE_DIAGNOSTICS) LogToFile(GetDiagnosticPath(), $"Timestamp: {DateTime.Now:HH:mm:ss.fff}");
			if (ENABLE_VERBOSE_DIAGNOSTICS) LogToFile(GetDiagnosticPath(), $"Plane: N=({plane.Nx:F6}, {plane.Ny:F6}, {plane.Nz:F6}), D={plane.D:F6}");
			if (ENABLE_VERBOSE_DIAGNOSTICS) LogToFile(GetDiagnosticPath(), $"Thresholds: minDelta={guardMinPos*1000:F1}mm, maxDelta={thrM*1000:F1}mm");
			if (ENABLE_VERBOSE_DIAGNOSTICS) LogToFile(GetDiagnosticPath(), $"Detection Area: X={ax}, Y={ay}, W={bx-ax}, H={by-ay}");
			if (ENABLE_VERBOSE_DIAGNOSTICS) LogToFile(GetDiagnosticPath(), $"Algorithm: Along-ray residual calculation with 3Ã—3 density filtering");

			// 1) Build candidate mask using along-ray residual and color TouchArea gating
			for (int y = ay; y < by; y++)
			{
				int row = y * width;
				for (int x = ax; x < bx; x++)
				{
					int i = row + x;
					ushort depth = depthData[i];
					if (depth == 0) continue;

					var p = cameraSpacePoints[i];
					if (float.IsInfinity(p.X) || float.IsInfinity(p.Y) || float.IsInfinity(p.Z)) continue;

					// Perpendicular-to-plane distance cap (max 10cm)
					float signedDistance = (float)(planeNx * p.X + planeNy * p.Y + planeNz * p.Z + planeD);
					if (signedDistance > 0.10f) continue; // Reject touches > 10cm from wall

					double r = Math.Sqrt(p.X * p.X + p.Y * p.Y + p.Z * p.Z);
					if (r < 0.2 || r > 5.0) continue;

					float invR = (float)(1.0 / r);
					float dx = p.X * invR, dy = p.Y * invR, dz = p.Z * invR;
					float denom = (float)(plane.Nx * dx + plane.Ny * dy + plane.Nz * dz);
					if (Math.Abs(denom) < 0.20f) continue;            // avoid grazing rays (tightened)
					float tExp = (float)(-plane.D / denom);
					if (tExp <= 0 || tExp > 8.0f) continue;          // must hit plane in front of camera

					float delta = (float)(tExp - r);                  // >0 means in front of plane

					// Temporal smoothing
					if (deltaFiltered == null || deltaFiltered.Length != len)
						deltaFiltered = new float[len];
					float a = 0.33f; // smoothing alpha (EMA)
					float prev = deltaFiltered[i];
					deltaFiltered[i] = (prev == 0 || float.IsNaN(prev) || float.IsInfinity(prev)) ? delta : (a * delta + (1 - a) * prev);

					// Adaptive local min threshold
					float tLocal = tExp;
					const float tBase = 1.2f;
					float adapt = Math.Max(0f, (tLocal - tBase)) * 0.003f;
					float localMin = guardMinPos + adapt;

					if (deltaFiltered[i] < localMin || deltaFiltered[i] > thrM) continue;
					if (ENABLE_VERBOSE_DIAGNOSTICS && delta >= guardMinPos && delta <= thrM) deltaFilterCount++;

					// Replace expensive IsPointInTouchArea() call with direct array lookup
					if (haveMap)
					{
						var cp = d2c[i];
						if (float.IsInfinity(cp.X) || float.IsInfinity(cp.Y) || float.IsNaN(cp.X) || float.IsNaN(cp.Y)) continue;
						var ta = calibration.TouchArea;
						if (!(cp.X >= ta.X && cp.X <= ta.Right && cp.Y >= ta.Y && cp.Y <= ta.Bottom)) continue;
							}
							else
							{
						// Fallback to original method if map not available
						if (!IsPointInTouchArea(x, y, depth)) continue;
					}

					candidateMask[i] = true;

					// ENHANCED SAMPLE POINT LOGGING: First 10 candidates with full ray analysis
					if (sampleCount < 10)
					{
						if (ENABLE_VERBOSE_DIAGNOSTICS && frameCounter % LOG_EVERY_N_FRAMES == 0 && sampleCount < 3)
						{
							LogToFile(GetDiagnosticPath(), $"=== SAMPLE CANDIDATE {sampleCount + 1} ===");
							LogToFile(GetDiagnosticPath(), $"  Position: ({x}, {y}), Depth: {depth}");
							LogToFile(GetDiagnosticPath(), $"  CameraSpace: ({p.X:F3}, {p.Y:F3}, {p.Z:F3})");
							LogToFile(GetDiagnosticPath(), $"  Range: {r:F3}m");
							LogToFile(GetDiagnosticPath(), $"  Ray Direction: ({dx:F3}, {dy:F3}, {dz:F3})");
							LogToFile(GetDiagnosticPath(), $"  Plane Intersection: denom={denom:F3}, t_exp={tExp:F3}m");
							LogToFile(GetDiagnosticPath(), $"  Along-Ray Residual: Î´={delta*1000:F1}mm");
							LogToFile(GetDiagnosticPath(), $"  Threshold Check: {delta*1000:F1}mm in range [{guardMinPos*1000:F1}, {thrM*1000:F1}]mm");
							bool inArea = haveMap ? 
								(!float.IsInfinity(d2c[i].X) && !float.IsInfinity(d2c[i].Y) && 
								 d2c[i].X >= calibration.TouchArea.X && d2c[i].X <= calibration.TouchArea.Right &&
								 d2c[i].Y >= calibration.TouchArea.Y && d2c[i].Y <= calibration.TouchArea.Bottom) :
								IsPointInTouchArea(x, y, depth);
							LogToFile(GetDiagnosticPath(), $"  TouchArea Validation: {inArea}");
						}
						sampleCount++;
					}
				}
			}

			// 2) 3x3 density filter to remove speckle
            // Early flood gate: detect systematic offset using along-ray residuals before density filtering
            if (frameCounter % 10 == 0)
            {
                int candidateCount = 0;
                int roiSize = Math.Max(1, (bx - ax) * (by - ay));
                for (int y = ay; y < by; y++)
                {
                    int row = y * width;
                    for (int x = ax; x < bx; x++)
                    {
                        if (candidateMask[row + x]) candidateCount++;
                    }
                }

                double candidateFraction = candidateCount / (double)roiSize;
                if (candidateFraction > 0.40)
                {
                    earlyFloodCounter++;
                    if (earlyFloodCounter >= EARLY_FLOOD_TRIGGER)
                    {
                        var deltas = new List<float>(candidateCount);
                        for (int y = ay; y < by; y++)
                        {
                            int row = y * width;
                            for (int x = ax; x < bx; x++)
                            {
                                int i = row + x;
                                if (candidateMask[i])
                                {
                                    float d = (deltaFiltered != null && i < deltaFiltered.Length) ? deltaFiltered[i] : 0f;
                                    if (d > 0) deltas.Add(d);
                                }
                            }
                        }

                        if (deltas.Count > 1000)
                        {
                            deltas.Sort();
                            float medianOffset = deltas[deltas.Count / 2];
                            if (medianOffset > 0.008f)
                            {
                                double correction = medianOffset * 0.8; // conservative 80% correction
                                planeD -= correction; // move plane towards camera
                                plane.D = (float)planeD; // apply in-frame
                                if (deltaFiltered != null) Array.Clear(deltaFiltered, 0, deltaFiltered.Length);
                                if (temporalCount != null) Array.Clear(temporalCount, 0, temporalCount.Length);
                                LogToFile(GetDiagnosticPath(), $"EARLY AUTO-BASELINE CORRECTION: Adjusted plane D by {correction*1000:F1}mm (median offset={medianOffset*1000:F1}mm)");
                                LogToFile(GetDiagnosticPath(), $"New plane D: {planeD:F6}m");
                            }
                        }

                        earlyFloodCounter = 0; // reset after attempt
                    }
                }
                else
                {
                    earlyFloodCounter = 0;
                }
            }
			int minNeighbors = ENABLE_FAST_TAP_BURST ? 1 : 2; // relaxed to 1 when fast-tap burst is enabled
			for (int y = ay + 1; y < by - 1; y++)
			{
				int row = y * width;
				for (int x = ax + 1; x < bx - 1; x++)
				{
					int i = row + x;
					if (!candidateMask[i]) continue;
					int count = 0;
					for (int yy = -1; yy <= 1; yy++)
					{
						int nrow = (y + yy) * width;
						for (int xx = -1; xx <= 1; xx++)
						{
							if (xx == 0 && yy == 0) continue;
							if (candidateMask[nrow + (x + xx)]) count++;
						}
					}
					if (count >= minNeighbors) neighborCount[i] = (byte)count; else candidateMask[i] = false;
				}
			}

			// DENSITY FILTER ANALYSIS
			// Build histogram of deltaFiltered values
			int bins = 64;
			if (deltaHist == null || deltaHist.Length != bins) deltaHist = new int[bins];
			int totalCount = 0;
			Array.Clear(deltaHist, 0, bins);

			// Accumulate histogram during candidate pass
			for (int y = ay; y < by; y++)
			{
				int row = y * width;
				for (int x = ax; x < bx; x++)
				{
					int i = row + x;
					if (candidateMask[i])
					{
						float v = Math.Max(0f, Math.Min(contactOn, deltaFiltered[i]));
						int b = (int)(v / Math.Max(1e-6f, contactOn) * (bins - 1));
						deltaHist[b]++;
						totalCount++;
					}
				}
			}

			// Find P05 percentile and adapt minimum threshold
			int target = (int)(totalCount * 0.05);
			int cum = 0;
			float p05 = 0f;
			for (int bi = 0; bi < bins; bi++)
			{
				cum += deltaHist[bi];
				if (cum >= target)
				{
					p05 = (bi / (float)(bins - 1)) * contactOn;
					break;
				}
			}
			float adaptMin = Math.Max(guardMinPos, p05 + 0.0007f);

			// Reapply threshold with adaptive minimum
			for (int y = ay; y < by; y++)
			{
				int row = y * width;
				for (int x = ax; x < bx; x++)
				{
					int i = row + x;
					if (candidateMask[i] && deltaFiltered[i] < adaptMin)
						candidateMask[i] = false;
				}
			}

			int densityFilterCandidates = 0;
			int densityFilterSurvivors = 0;
			for (int y = ay; y < by; y++)
			{
				int row = y * width;
				for (int x = ax; x < bx; x++)
				{
					if (candidateMask[row + x]) densityFilterSurvivors++;
					if (neighborCount[row + x] > 0) densityFilterCandidates++;
				}
			}
			if (ENABLE_VERBOSE_DIAGNOSTICS) LogToFile(GetDiagnosticPath(), $"DENSITY FILTER: {densityFilterCandidates} candidates, {densityFilterSurvivors} survivors (minNeighbors={minNeighbors})");

			// Stronger denoise: 3Ã—3 neighbor + morphological open + close
			Open3x3(candidateMask, width, height, ax, ay, bx, by);
			Close3x3(candidateMask, width, height, ax, ay, bx, by);

			// Local-contrast gate: keep only pixels that protrude above local neighborhood (~1.0mm for testing)
			ApplyLocalContrastGate(candidateMask, deltaFiltered, width, height, ax, ay, bx, by, bumpMm: 1.0f);

			// Temporal K-of-N to suppress single-frame sparkles (relaxed to 1 when fast-tap burst enabled)
			if (temporalCount != null && temporalCount.Length == width * height)
			{
				byte requireCount = ENABLE_FAST_TAP_BURST ? (byte)1 : (byte)2;
				for (int i = 0; i < temporalCount.Length; i++)
				{
					if (candidateMask[i]) temporalCount[i] = (byte)Math.Min(3, temporalCount[i] + 1);
					else                  temporalCount[i] = (byte)Math.Max(0, temporalCount[i] - 1);
					if (temporalCount[i] < requireCount) candidateMask[i] = false;
				}
			}

			// 3) Collect surviving pixels and group into blobs
			var survivors = new List<Point>();
			for (int y = ay; y < by; y++)
			{
				int row = y * width;
				for (int x = ax; x < bx; x++)
				{
					if (candidateMask[row + x]) survivors.Add(new Point(x, y));
				}
			}

			int roiPx = (bx - ax) * (by - ay);
			if (activeTouches.Count == 0 && survivors.Count > roiPx * 0.14)
				floodFrameCounter++;
			else
				floodFrameCounter = 0;

			int survivorsAfterMorphology = survivors.Count;

			if (survivorsAfterMorphology > roiPx * 0.14)
			{
				// Strengthen density filter for noisy frames
				Open3x3(candidateMask, width, height, ax, ay, bx, by);
			}

			if (survivorsAfterMorphology > roiPx * 0.2)
			{
				// Emergency noise suppression - raise threshold by 0.4mm
				float emergencyMin = Math.Max(guardMinPos, adaptMin + 0.0004f);
				for (int y = ay; y < by; y++)
				{
					int row = y * width;
					for (int x = ax; x < bx; x++)
					{
						int i = row + x;
						if (candidateMask[i] && deltaFiltered[i] < emergencyMin)
							candidateMask[i] = false;
					}
				}
			}

			if (floodFrameCounter >= FLOOD_FRAME_TRIGGER && activeTouches.Count == 0 && !double.IsNaN(wallMedian))
			{
				if (Math.Abs(wallMedian) > BASELINE_CORRECTION_THRESHOLD)
				{
					double correction = wallMedian * 0.8;
					planeD -= correction;
					plane.D = (float)planeD;
					floodFrameCounter = 0;
					if (deltaFiltered != null) Array.Clear(deltaFiltered, 0, deltaFiltered.Length);
					if (temporalCount != null) Array.Clear(temporalCount, 0, temporalCount.Length);
					LogToFile(GetDiagnosticPath(), $"PLANE BASELINE AUTO-CORRECT: Adjusted by {correction * 1000:F1}mm (wall median={wallMedian*1000:F1}mm) -> new D={planeD:F4}");
				}
			}

			var blobs = FindBlobs(survivors);
			blobs = MergeCloseBlobs(blobs, 40); // px radius to merge clusters (was 28)
			var touchPoints = new List<Point>();

				foreach (var blob in blobs)
				{
				if (blob.Count < minBlobAreaPoints || blob.Count > maxBlobAreaPoints) continue;

				// Ball detection - aspect ratio filter
				var blobMinX = blob.Min(p => p.X);
				var blobMaxX = blob.Max(p => p.X);
				var blobMinY = blob.Min(p => p.Y);
				var blobMaxY = blob.Max(p => p.Y);
				double bboxWidth = blobMaxX - blobMinX;
				double bboxHeight = blobMaxY - blobMinY;
				double aspectRatio = Math.Max(bboxWidth, bboxHeight) / Math.Max(1, Math.Min(bboxWidth, bboxHeight));
				if (aspectRatio > 5.0) continue; // Reject elongated noise artifacts

				// Î´â€'weighted centroid
				double sumW = 0, sumX = 0, sumY = 0;
				float minDelta = float.MaxValue;
				Point minDeltaPt = default(Point);

				foreach (var pt in blob)
				{
					int idx = ((int)pt.Y) * width + ((int)pt.X);
					float d = deltaFiltered[idx];
					if (d < minDelta) { minDelta = d; minDeltaPt = pt; }
					double w = 1.0 / Math.Max(0.001, deltaFiltered[idx]); // Use smoothed residual for optimal centroid
					sumW += w;
					sumX += pt.X * w;
					sumY += pt.Y * w;
				}

				if (sumW <= 0) continue;

				// core = only pixels at or below contactOn
				var core = new List<Point>();
				foreach (var pt in blob){
					int bi=((int)pt.Y)*width+(int)pt.X;
					if (deltaFiltered[bi] <= contactOn) core.Add(pt);
				}

				// Dynamic contact fraction based on blob size
				float contactFracMin;
				if (blob.Count <= 60) contactFracMin = 0.05f;      // 5% for tiny blobs
				else if (blob.Count <= 150) contactFracMin = 0.08f; // 8% for small blobs
				else contactFracMin = 0.12f;                        // 12% for large blobs
				
                int contactPixels = 0;
                foreach (var pt in blob) {
                    int bi = ((int)pt.Y) * width + (int)pt.X;
                    if (deltaFiltered[bi] <= contactOn) contactPixels++;
                }
				if (contactPixels / (float)blob.Count < contactFracMin) continue;

				// mean Î´ of core â‰¤ 60% of contact threshold (max 12mm)
                double meanCore = core.Count > 0 ? core.Average(pt => (double)deltaFiltered[((int)pt.Y) * width + ((int)pt.X)]) : double.MaxValue;
                // Slightly relaxed cap for tiny blobs; still capped at 95% of contactOn
                double sizeFactor = Math.Min(1.0, blob.Count / 1200.0); // 0..1 across typical ranges
                double maxMean = Math.Min(contactOn * (0.7 + 0.30 * sizeFactor), contactOn * 0.95f);
                if (meanCore > maxMean) continue;
                // Fast-tap (single-frame) burst override: record burst-eligible points for this frame
                if (ENABLE_FAST_TAP_BURST)
                {
                    bool burst = (meanCore <= 0.0065) && (blob.Count >= Math.Max(1, (int)(minBlobAreaPoints * 0.5)));
                    if (burst)
                    {
                        // Use current best candidate proxy (minDeltaPt) before final best snap
                        long key = ((long)Math.Max(0, Math.Min(height - 1, (int)minDeltaPt.Y)) << 32) | (uint)Math.Max(0, Math.Min(width - 1, (int)minDeltaPt.X));
                        tapBurstPoints.Add(key);
                    }
                }
				// reject ultra-thin blobs
				int minX=(int)blob.Min(p=>p.X), maxX=(int)blob.Max(p=>p.X);
				int minY=(int)blob.Min(p=>p.Y), maxY=(int)blob.Max(p=>p.Y);
				if ((maxX-minX)<5 || (maxY-minY)<5) continue; // Changed from 8

				// Aspect ratio rejection for elongated artifacts
				double blobWidth = maxX - minX, blobHeight = maxY - minY;
				double ar = Math.Max(blobWidth, blobHeight) / Math.Max(1.0, Math.Min(blobWidth, blobHeight));
				if (ar > 5.0) continue; // Reject elongated noise

				if (core.Count >= 3){
					var hull = ConvexHull(core);
					DrawContourBorder(hull, System.Windows.Media.Brushes.Red); // Tag = "ContourBorder"
				}

				// ADD SAFETY CHECK:
				if (Math.Abs(sumW) < 1e-9) continue; // Prevent division by zero

				// Refine to the Î´-minimum's local area for stability
				int cx = (int)Math.Round(sumX / sumW);
				int cy = (int)Math.Round(sumY / sumW);

				// Optional local min 3Ã—3 snap
				Point best = minDeltaPt;
				float bestDelta = minDelta;
				for (int yy = -1; yy <= 1; yy++)
				{
					int y0 = cy + yy; if (y0 < ay || y0 >= by) continue;
					for (int xx = -1; xx <= 1; xx++)
					{
						int x0 = cx + xx; if (x0 < ax || x0 >= bx) continue;
						int i = y0 * width + x0;
						if (!candidateMask[i]) continue;
						float d = deltaFiltered[i];
						if (d < bestDelta) { bestDelta = d; best = new Point(x0, y0); }
					}
				}

				// ENHANCED BLOB ANALYSIS: Detailed centroid calculation
				if (ENABLE_VERBOSE_DIAGNOSTICS) LogToFile(GetDiagnosticPath(), $"=== BLOB ANALYSIS {blobIndex + 1} ===");
				if (ENABLE_VERBOSE_DIAGNOSTICS) LogToFile(GetDiagnosticPath(), $"  Area: {blob.Count} pixels");
				if (ENABLE_VERBOSE_DIAGNOSTICS) LogToFile(GetDiagnosticPath(), $"  MinDelta: {minDelta*1000:F1}mm at ({minDeltaPt.X}, {minDeltaPt.Y})");
				if (ENABLE_VERBOSE_DIAGNOSTICS) LogToFile(GetDiagnosticPath(), $"  Weighted Centroid: sumW={sumW:F3}, sumX={sumX:F1}, sumY={sumY:F1}");
				if (ENABLE_VERBOSE_DIAGNOSTICS) LogToFile(GetDiagnosticPath(), $"  Calculated Centroid: ({cx}, {cy})");
				if (ENABLE_VERBOSE_DIAGNOSTICS) LogToFile(GetDiagnosticPath(), $"  Final Position: ({best.X}, {best.Y})");
				if (ENABLE_VERBOSE_DIAGNOSTICS) LogToFile(GetDiagnosticPath(), $"  Safety Check: sumW={sumW:F6} (passed: {Math.Abs(sumW) >= 1e-9})");
				blobIndex++;

				touchPoints.Add(best);
			}

			// Suppress frames with broad residuals but no accepted contact; hard reset state
			int roiPixels = (bx-ax)*(by-ay);
			double fill = survivors.Count / (double)Math.Max(1, roiPixels);
			if (fill > 0.25){ // 25% (was 12%) - less aggressive suppression
				activeTouches.Clear();
				hadTouchLastFrame = false;
				DetectionStatusText.Text = $"Suppressed (fill {fill*100:F1}%)";
				return;
			}

			// Hold touches persisting under 1.5mm even if blob breaks
			const float holdOff = 0.008f; // 8mm to match min residual floor
			var addedFromHold = 0;
			foreach (var t in activeTouches)
			{
				int ix = (int)Math.Round(t.Position.X);
				int iy = (int)Math.Round(t.Position.Y);
				if (ix < ax || ix >= bx || iy < ay || iy >= by) continue;
				int idx = iy * width + ix;

				// get current frame residual at the previous touch position
				float d = (idx >= 0 && idx < (deltaFiltered?.Length ?? 0) && candidateMask[idx]) ? deltaFiltered[idx] : float.MaxValue;
				if (d == float.MaxValue)
				{
					var p = cameraSpacePoints[idx];
					if (!(float.IsInfinity(p.X) || float.IsInfinity(p.Y) || float.IsInfinity(p.Z)))
					{
						double r = Math.Sqrt(p.X * p.X + p.Y * p.Y + p.Z * p.Z);
						if (r > 0.2 && r < 5.0)
						{
							float invR = (float)(1.0 / r);
							float ddx = p.X * invR, ddy = p.Y * invR, ddz = p.Z * invR;
							float denom2 = (float)(plane.Nx * ddx + plane.Ny * ddy + plane.Nz * ddz);
							if (Math.Abs(denom2) > 0.20f) // Match detection threshold (tightened)
							{
								float tExp2 = (float)(-plane.D / denom2);
								d = (float)(tExp2 - r);
							}
						}
					}
				}

				if (d > 0 && d <= holdOff)
				{
					bool exists = touchPoints.Any(p => Math.Abs(p.X - ix) < 8 && Math.Abs(p.Y - iy) < 8);
					if (!exists)
					{
						touchPoints.Add(new Point(ix, iy));
						addedFromHold++;
					}
				}
			}
			if (ENABLE_VERBOSE_DIAGNOSTICS) LogToFile(GetDiagnosticPath(), $"HOLD ADDITIONS: {addedFromHold}, Final Touches (pre-guard): {touchPoints.Count}");

			// Update guard state to prevent immediate re-trigger after touch ends
			if (touchPoints.Count == 0 && hadTouchLastFrame)
			{
				// 200ms guard after a touch ends to prevent immediate re-trigger by residual noise
				guardUntil = now.AddMilliseconds(200);
			}
			hadTouchLastFrame = touchPoints.Count > 0;

			// Post-filter: de-duplicate nearby touches (safest approach)
			if (singleTouchMode && touchPoints.Count > 1)
			{
			touchPoints = DeDuplicateByProximity(touchPoints, width, deltaFiltered, 45);
			}

			UpdateTouchTracking(touchPoints);
			UpdateTouchVisuals(touchPoints);

			DetectionStatusText.Text = $"Touches: {activeTouches.Count} ({thrM * 1000:F0}mm)";
			StatusText.Text = $"Detection active (ray-based)";

			// COMPREHENSIVE FRAME SUMMARY: Complete algorithm analysis
			if (ENABLE_VERBOSE_DIAGNOSTICS) LogToFile(GetDiagnosticPath(), $"=== DETECTION FRAME SUMMARY ===");
			if (ENABLE_VERBOSE_DIAGNOSTICS) LogToFile(GetDiagnosticPath(), $"Timestamp: {DateTime.Now:HH:mm:ss.fff}");
			if (ENABLE_VERBOSE_DIAGNOSTICS) LogToFile(GetDiagnosticPath(), $"Algorithm: Ray-based with along-ray residual calculation");
			if (ENABLE_VERBOSE_DIAGNOSTICS) LogToFile(GetDiagnosticPath(), $"Detection Area: X={ax}, Y={ay}, W={bx-ax}, H={by-ay}");
			if (ENABLE_VERBOSE_DIAGNOSTICS) LogToFile(GetDiagnosticPath(), $"Thresholds: minDelta={guardMinPos*1000:F1}mm, maxDelta={thrM*1000:F1}mm");
			if (ENABLE_VERBOSE_DIAGNOSTICS) LogToFile(GetDiagnosticPath(), $"Plane: N=({plane.Nx:F3}, {plane.Ny:F3}, {plane.Nz:F3}), D={plane.D:F3}");

			// Count total candidates and survivors
			int totalCandidates = 0;
			int survivorsAfterDensity = 0;
			if (ENABLE_VERBOSE_DIAGNOSTICS) LogToFile(GetDiagnosticPath(), $"SAMPLE COUNT: {deltaFilterCount} pixels passed delta filter");
			for (int y = ay; y < by; y++)
			{
				int row = y * width;
				for (int x = ax; x < bx; x++)
				{
					if (candidateMask[row + x]) totalCandidates++;
				}
			}

			for (int i = 0; i < len; i++)
			{
				if (candidateMask[i]) survivorsAfterDensity++;
			}

			if (ENABLE_VERBOSE_DIAGNOSTICS) LogToFile(GetDiagnosticPath(), $"Detection Stats: Candidates={totalCandidates}, AfterDensityFilter={survivorsAfterDensity}");
			if (ENABLE_VERBOSE_DIAGNOSTICS) LogToFile(GetDiagnosticPath(), $"Blobs Found: {blobs.Count}, Final Touches: {touchPoints.Count}");
			if (ENABLE_VERBOSE_DIAGNOSTICS) LogToFile(GetDiagnosticPath(), $"Active Touches: {activeTouches.Count}");
			if (ENABLE_VERBOSE_DIAGNOSTICS) LogToFile(GetDiagnosticPath(), $"Algorithm Performance: Ray-based detection with 3Ã—3 density filtering");
			if (ENABLE_VERBOSE_DIAGNOSTICS) LogToFile(GetDiagnosticPath(), $"=================================");
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



		private void UpdateStatusText()
		{
			if (calibration == null)
			{
				StatusText.Text = "No calibration loaded";
				return;
			}

			var status = new StringBuilder();
			status.AppendLine($"Calibration: Loaded");
			status.AppendLine($"Plane: Valid (N=({planeNx:F3}, {planeNy:F3}, {planeNz:F3}), D={planeD:F3})");
			status.AppendLine($"Touch Area: Active ({cachedDepthTouchArea.Width:F0}x{cachedDepthTouchArea.Height:F0} pixels)");
			status.AppendLine($"Detection: Ray-based with density filtering");
			status.AppendLine($"Sensitivity: {PlaneThresholdSlider?.Value ?? 30:F1}mm (slider-controlled)");
			status.AppendLine($"Min Blob Area: {MinBlobAreaSlider?.Value ?? 20:F0} pixels");
			status.AppendLine($"Active Touches: {activeTouches.Count}");
			status.AppendLine($"Performance: Optimized (standard detection)");
			status.AppendLine($"Frame Time: {averageFrameTime:F1}ms");
			status.AppendLine($"Detection: {avgDetectionTime:F1}ms");
			status.AppendLine($"FPS: {(1000.0 / averageFrameTime):F1}");

			StatusText.Text = status.ToString();
		}

		// EVENT HANDLERS
		private async void PlaneThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
		{
			if (PlaneThresholdSlider == null) return;
			
			// Ensure UI updates happen on UI thread
			Dispatcher.BeginInvoke(new Action(() =>
			{
				double thresholdMm = PlaneThresholdSlider.Value;
				
				// Update text immediately
				if (ThresholdValueText != null)
				{
					ThresholdValueText.Text = $"{thresholdMm:F0}mm";
					ThresholdValueText.Visibility = Visibility.Visible; // Ensure visibility
				}
				
				// Add small delay before status update to prevent conflicts
				_ = Task.Run(async () =>
				{
					await Task.Delay(50);
					Dispatcher.BeginInvoke(new Action(() => UpdateStatusText()));
				});
			}));
			
			// Log on background thread to avoid UI blocking
			if (ENABLE_VERBOSE_DIAGNOSTICS) LogToFile(GetDiagnosticPath(), $"Plane threshold changed to: {PlaneThresholdSlider.Value:F1}mm");
		}
		
        private void MinBlobAreaSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            try
            {
                minBlobAreaPoints = (int)Math.Round(e.NewValue);
                if (MinBlobAreaValueText != null)
                {
                    MinBlobAreaValueText.Text = $"{minBlobAreaPoints} pts";
                }
                LogToFile(GetDiagnosticPath(), $"Min blob area changed to: {minBlobAreaPoints} pixels");
            }
            catch (Exception ex)
            {
                LogToFile(GetDiagnosticPath(), $"ERROR in MinBlobAreaSlider_ValueChanged: {ex.Message}");
            }
        }
		
		private void MaxBlobAreaSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
		{
			try
			{
				maxBlobAreaPoints = (int)e.NewValue;
				
				// Update display text to show current value
				if (MaxBlobAreaValueText != null)
				{
					MaxBlobAreaValueText.Text = $"{maxBlobAreaPoints} pts";
				}
				
				LogToFile(GetDiagnosticPath(), $"Max blob area changed to: {maxBlobAreaPoints} pixels");
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in MaxBlobAreaSlider_ValueChanged: {ex.Message}");
			}
		}
		
		private void PlaneToleranceSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
		{
			try
			{
				// Ensure UI updates happen on UI thread
				Dispatcher.BeginInvoke(new Action(() =>
				{
					if (PlaneToleranceValueText != null)
					{
						PlaneToleranceValueText.Text = $"{e.NewValue:F1}mm";
						PlaneToleranceValueText.Visibility = Visibility.Visible; // Ensure visibility
				}
				}));
				
				// Log on background thread to avoid UI blocking
				if (ENABLE_VERBOSE_DIAGNOSTICS) LogToFile(GetDiagnosticPath(), $"Plane tolerance changed to: {e.NewValue:F1}mm");
			}
			catch (Exception ex)
			{
				if (ENABLE_VERBOSE_DIAGNOSTICS) LogToFile(GetDiagnosticPath(), $"ERROR in PlaneToleranceSlider_ValueChanged: {ex.Message}");
			}
		}
		
		
        private void ResetViewButton_Click(object sender, RoutedEventArgs e)
		{
			try
			{
				UpdateUnifiedViewTransform();
				
				LogToFile(GetDiagnosticPath(), "View reset to default");
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

		private void UpdateVisualFeedback()
		{
			try
			{
				if (OverlayCanvas == null) return;
				
				// TouchVisual system removed
				
				
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
					var scaled = ConvertColorAreaToDepthArea(calibration.TouchArea);
					Rect depthArea;
					ushort[] dd; int dw, dh;
					if (kinectManager.TryGetDepthFrameRaw(out dd, out dw, out dh))
					{
						var mapped = FindDepthAreaForColorArea(calibration.TouchArea, dd, dw, dh);
						// accept mapped only if not drastically smaller (<70%) than scaled
						if (mapped.HasValue && mapped.Value.Width >= 0.7 * scaled.Width && mapped.Value.Height >= 0.7 * scaled.Height)
							depthArea = mapped.Value;
						else
							depthArea = scaled;
					}
					else
					{
						depthArea = scaled;
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
                // Ensure markers render above boundaries
                Panel.SetZIndex(rect, 10);
					
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
				if (ENABLE_VERBOSE_DIAGNOSTICS) LogToFile(GetDiagnosticPath(), $"CONVERT INPUT: ColorArea=({colorArea.X:F1}, {colorArea.Y:F1}, {colorArea.Width:F1}, {colorArea.Height:F1})");
				
				// Get current depth data for accurate mapping
				if (kinectManager.TryGetDepthFrameRaw(out ushort[] depthData, out int depthWidth, out int depthHeight))
				{
					var mappedArea = FindDepthAreaForColorArea(colorArea, depthData, depthWidth, depthHeight);
					if (mappedArea.HasValue)
					{
						if (ENABLE_VERBOSE_DIAGNOSTICS) LogToFile(GetDiagnosticPath(), $"CONVERT OUTPUT: DepthArea=({mappedArea.Value.X:F1}, {mappedArea.Value.Y:F1}, {mappedArea.Value.Width:F1}, {mappedArea.Value.Height:F1})");
						return mappedArea.Value;
					}
				}
				
				// Fallback to linear scaling if mapper fails
				if (ENABLE_VERBOSE_DIAGNOSTICS) LogToFile(GetDiagnosticPath(), "ROI MAPPING: Using fallback linear scaling (mapper unavailable)");
				double centerX = colorArea.X + colorArea.Width / 2;
				double depthCenterX = (centerX / 1920.0) * 512.0;
				double depthCenterY = ((colorArea.Y + colorArea.Height / 2) / 1080.0) * 424.0;
				double mappedWidth = (colorArea.Width / 1920.0) * 512.0;
				double mappedHeight = (colorArea.Height / 1080.0) * 424.0;
				
				return new Rect(depthCenterX - mappedWidth/2, depthCenterY - mappedHeight/2, mappedWidth, mappedHeight);
			}
			catch (Exception ex)
			{
				if (ENABLE_VERBOSE_DIAGNOSTICS) LogToFile(GetDiagnosticPath(), $"ERROR in ConvertColorAreaToDepthArea: {ex.Message}");
				// Final fallback
				return new Rect(colorArea.X * SCALE_X, colorArea.Y * SCALE_Y, colorArea.Width * SCALE_X, colorArea.Height * SCALE_Y);
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
			double t = -planeD / denom; // distance from camera origin along the pixel ray to the wall plane
			return (t > 0) ? t : double.NaN;
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

						// Ensure the plane normal points toward the camera to make along-ray distances positive and stable
						try
						{
							if (calibration.TouchAreaCorners3D != null && calibration.TouchAreaCorners3D.Count >= 4)
							{
								// Use the touch area center as a point on the wall
								var cx = calibration.TouchAreaCorners3D.Average(p => p.X);
								var cy = calibration.TouchAreaCorners3D.Average(p => p.Y);
								var cz = calibration.TouchAreaCorners3D.Average(p => p.Z);
								// Vector from wall toward camera origin (0,0,0)
								double vx = -cx, vy = -cy, vz = -cz;
								double dot = planeNx * vx + planeNy * vy + planeNz * vz;
								if (dot < 0)
								{
									// Flip orientation
									planeNx = -planeNx; planeNy = -planeNy; planeNz = -planeNz; planeD = -planeD;
								}
							}
							else
							{
								// Fallback: prefer camera to be on the positive side of the plane
								// If camera origin (0,0,0) evaluates negative, flip.
								if (planeD < 0) { planeNx = -planeNx; planeNy = -planeNy; planeNz = -planeNz; planeD = -planeD; }
							}
						}
						catch { }

						isPlaneValid = true;
						LogToFile(GetDiagnosticPath(), $"Plane normalized & oriented: N=({planeNx:F6}, {planeNy:F6}, {planeNz:F6}), D={planeD:F6}");

						// COMPREHENSIVE PLANE DIAGNOSTICS
						LogToFile(GetDiagnosticPath(), $"PLANE CALCULATION: Original Normal=({planeNx:F6}, {planeNy:F6}, {planeNz:F6}), D={planeD:F6}");
						LogToFile(GetDiagnosticPath(), $"PLANE MAGNITUDE: {Math.Sqrt(planeNx*planeNx + planeNy*planeNy + planeNz*planeNz):F6}");
						LogToFile(GetDiagnosticPath(), $"PLANE VALIDATION: Nx={Math.Abs(planeNx):F6}, Ny={Math.Abs(planeNy):F6}, Nz={Math.Abs(planeNz):F6}");

						// Test plane with camera origin
						double cameraOriginTest = planeNx * 0 + planeNy * 0 + planeNz * 0 + planeD;
						LogToFile(GetDiagnosticPath(), $"CAMERA ORIGIN TEST: planeD={planeD:F6}, CameraOriginEvaluation={cameraOriginTest:F6}");

						// Test plane with sample wall points
						string orientation = cameraOriginTest > 0 ? "TOWARDS" : "AWAY FROM";
						LogToFile(GetDiagnosticPath(), $"PLANE ORIENTATION: Normal points {orientation} camera");
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


		/// <summary>
		/// A simple blob detection algorithm (Connected-component labeling).
		/// This method groups adjacent pixels into lists (blobs).
		/// </summary>
		private List<List<Point>> FindBlobs(List<Point> pixels)
		{
			var blobs = new List<List<Point>>();
			var toVisit = new HashSet<Point>(pixels);

			while (toVisit.Any())
			{
				var currentBlob = new List<Point>();
				var queue = new Queue<Point>();
				
				var startPixel = toVisit.First();
				queue.Enqueue(startPixel);
				toVisit.Remove(startPixel);

				while (queue.Any())
				{
					var currentPixel = queue.Dequeue();
					currentBlob.Add(currentPixel);

					// Check neighbors (up, down, left, right) - 8-way connectivity is better for fingers.
					for (int y = -1; y <= 1; y++)
					{
						for (int x = -1; x <= 1; x++)
						{
							if (x == 0 && y == 0) continue;

							var neighbor = new Point(currentPixel.X + x, currentPixel.Y + y);
							if (toVisit.Contains(neighbor))
							{
								toVisit.Remove(neighbor);
								queue.Enqueue(neighbor);
							}
						}
					}
				}
				blobs.Add(currentBlob);
			}
			return blobs;
		}

		private void UpdateTouchTracking(List<Point> newTouches)
		{
			var now = DateTime.Now;
			var updated = new List<TouchPoint>();

			foreach (var p in newTouches)
			{
				var existing = activeTouches.FirstOrDefault(t =>
					Math.Abs(t.Position.X - p.X) < 28 &&
					Math.Abs(t.Position.Y - p.Y) < 28);

				if (existing != null)
				{
					// Compute distance and set adaptive alpha
					double dx = p.X - existing.Position.X;
					double dy = p.Y - existing.Position.Y;
					double distance = Math.Sqrt(dx * dx + dy * dy);
					double alpha = distance > 10 ? 0.5 : 0.2; // Fast motion = more responsive

					existing.Position = new Point(
						existing.Position.X * (1 - alpha) + p.X * alpha,
						existing.Position.Y * (1 - alpha) + p.Y * alpha);
					existing.LastSeen = now;
					existing.SeenCount++;
					updated.Add(existing);
				}
				else
				{
					int ix = Math.Max(0, Math.Min((int)Math.Round(p.X), (int)(OverlayCanvas?.Width > 0 ? OverlayCanvas.Width - 1 : 511)));
					int iy = Math.Max(0, Math.Min((int)Math.Round(p.Y), (int)(OverlayCanvas?.Height > 0 ? OverlayCanvas.Height - 1 : 423)));
					bool burstHit = false;
					if (ENABLE_FAST_TAP_BURST && tapBurstPoints.Count > 0)
					{
						// exact or 3x3 neighborhood match
						for (int dy = -1; dy <= 1 && !burstHit; dy++)
						{
							for (int dx = -1; dx <= 1 && !burstHit; dx++)
							{
								int x0 = Math.Max(0, Math.Min(ix + dx, 511));
								int y0 = Math.Max(0, Math.Min(iy + dy, 423));
								long key = ((long)y0 << 32) | (uint)x0;
								if (tapBurstPoints.Contains(key)) burstHit = true;
							}
						}
					}
					updated.Add(new TouchPoint
					{
						Position = p,
						LastSeen = now,
						Area = 1,
						Depth = 0,
						SeenCount = burstHit ? 2 : 1
					});
				}
			}

			// TTL and persistence
			// Keep touches alive briefly; show only after 2 frames to reduce flicker
			var alive = updated.Where(t => 
				(now - t.LastSeen).TotalMilliseconds <= 200 && 
				t.SeenCount >= 2  // Anti-flicker: must persist 2+ frames
			).ToList();
			activeTouches = alive;
		}

		private void UpdateTouchVisuals(List<Point> touches)
		{
			// Clear existing touch markers first
			var existingMarkers = OverlayCanvas.Children.OfType<Rectangle>()
				.Where(rect => rect.Tag?.ToString() == "TouchVisual").ToList();
			
			foreach (var marker in existingMarkers)
			{
				OverlayCanvas.Children.Remove(marker);
			}
			
			// Draw markers for all touches
			foreach (var touch in activeTouches)
			{
				DrawTouchMarker(touch.Position, System.Windows.Media.Brushes.Red);
			}
		}

		private void DrawTouchMarker(Point position, System.Windows.Media.Brush brush)
		{
			try
			{
				if (OverlayCanvas == null) return;
				
				var rect = new Rectangle
				{
					Width = 12,
					Height = 12,
					Fill = brush,
					Stroke = System.Windows.Media.Brushes.DarkRed,
					StrokeThickness = 1,
					Tag = "TouchVisual"
				};
				
				Canvas.SetLeft(rect, position.X - 6);
				Canvas.SetTop(rect, position.Y - 6);
				OverlayCanvas.Children.Add(rect);
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in DrawTouchMarker: {ex.Message}");
			}
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
				
				// Touch area configuration updated
				LogToFile(GetDiagnosticPath(), $"Touch area configured: {depthArea}");
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
				// Cache the mapping lookup to avoid repeated O(512Ã—424) scans
				if (!coordinateMappingRefreshed)
				{
					// Use fast linear scaling if coordinate mapping not refreshed yet
					double depthXEarly = (colorPoint.X / 1920.0) * 512.0;
					double depthYEarly = (colorPoint.Y / 1080.0) * 424.0;
					return new Point(depthXEarly, depthYEarly);
				}
				
				// Only do expensive lookup after coordinate mapping is refreshed
				if (kinectManager.TryGetDepthToColorMapSnapshot(out ColorSpacePoint[] depthToColorMap, out int depthWidth, out int depthHeight))
				{
					// Find the depth point that maps closest to this color point
					float minDistance = float.MaxValue;
					int bestDepthIndex = -1;
					
					for (int i = 0; i < depthToColorMap.Length; i++)
					{
						var mappedColor = depthToColorMap[i];
						if (!float.IsInfinity(mappedColor.X) && !float.IsInfinity(mappedColor.Y))
						{
							float distance = (float)Math.Sqrt(Math.Pow(mappedColor.X - colorPoint.X, 2) + Math.Pow(mappedColor.Y - colorPoint.Y, 2));
							if (distance < minDistance)
							{
								minDistance = distance;
								bestDepthIndex = i;
							}
						}
					}
					
					if (bestDepthIndex >= 0)
					{
						int mappedDepthX = bestDepthIndex % depthWidth;
						int mappedDepthY = bestDepthIndex / depthWidth;
						return new Point(mappedDepthX, mappedDepthY);
					}
				}
				
				// Fallback to linear scaling
				double depthX = (colorPoint.X / 1920.0) * 512.0;
				double depthY = (colorPoint.Y / 1080.0) * 424.0;
				return new Point(depthX, depthY);
			}
			catch (Exception ex)
			{
				if (ENABLE_VERBOSE_DIAGNOSTICS) LogToFile(GetDiagnosticPath(), $"ERROR in ConvertColorPointToDepthPoint: {ex.Message}");
				return new Point(colorPoint.X * SCALE_X, colorPoint.Y * SCALE_Y);
			}
		}

		private void RefreshCoordinateMappingIfNeeded()
		{
			if (!coordinateMappingRefreshed && kinectManager.TryGetDepthFrameRaw(out ushort[] depthData, out int width, out int height))
			{
				if (ENABLE_VERBOSE_DIAGNOSTICS) LogToFile(GetDiagnosticPath(), "REFRESHING: Updating coordinate mapping with actual depth data");
				PrecomputeCoordinateMapping();
				coordinateMappingRefreshed = true;
				if (ENABLE_VERBOSE_DIAGNOSTICS) LogToFile(GetDiagnosticPath(), $"REFRESHED: cachedDepthTouchArea=({cachedDepthTouchArea.X:F1}, {cachedDepthTouchArea.Y:F1}, {cachedDepthTouchArea.Width:F1}, {cachedDepthTouchArea.Height:F1})");
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
				status.AppendLine($"Screen 1 (Plane): {(isPlaneValid ? "âœ“ Valid" : "âœ— Invalid")}");
				
				// Screen 2 status
				bool touchAreaValid = calibration?.TouchArea != null && 
									 calibration.TouchArea.Width > 0 && 
									 calibration.TouchArea.Height > 0;
				status.AppendLine($"Screen 2 (Touch Area): {(touchAreaValid ? "âœ“ Valid" : "âœ— Invalid")}");
				
				// Kinect status
				status.AppendLine($"Kinect: {(kinectManager?.IsInitialized == true ? "âœ“ Connected" : "âœ— Disconnected")}");
				
				
				// Performance status
				status.AppendLine($"Performance: âœ“ Optimized (robust detection)");
				
				DepthInfoText.Text = status.ToString();
			}
			catch (Exception ex)
			{
				LogToFile(GetDiagnosticPath(), $"ERROR in UpdateCalibrationStatus: {ex.Message}");
			}
		}
		
		// ENHANCED: Automatic diagnostic access on performance issues
		private void LogTouchDetectionDiagnostic() 
		{
			try
			{
				// Performance optimization status
				LogToFile(GetDiagnosticPath(), $"Performance: Robust detection active");
					LogToFile(GetDiagnosticPath(), $"TOUCH DETECTION: {activeTouches?.Count ?? 0} active touches detected");
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
				LogToFile(GetDiagnosticPath(), "Touch area configuration updated");
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
				
				// View transform status
				LogToFile(diagnosticPath, $"Depth View: optional vertical flip for display; no horizontal mirroring in detection");
				LogToFile(diagnosticPath, $"Robust Threshold: Enabled (8-15mm range with density filtering)");
				
				// Touch size detection status
				LogToFile(diagnosticPath, $"Max Touch Size: {maxBlobAreaPoints} pixels");
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
            try
            {
                if (string.IsNullOrEmpty(screen3DiagnosticPath))
                {
                    var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    var pics = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                    var dir = System.IO.Path.Combine(pics, "KinectCalibrationDiagnostics", $"Screen3_TouchTest_{timestamp}");
                    System.IO.Directory.CreateDirectory(dir);
                    screen3DiagnosticPath = System.IO.Path.Combine(dir, "screen3_diagnostic.txt");
                }
                return screen3DiagnosticPath;
            }
            catch
            {
                // Fallback to temp if anything goes wrong
                try
                {
                    var temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "KinectCalibrationDiagnostics", "Screen3_Fallback");
                    System.IO.Directory.CreateDirectory(temp);
                    screen3DiagnosticPath = System.IO.Path.Combine(temp, "screen3_diagnostic.txt");
                    return screen3DiagnosticPath;
                }
                catch
                {
                    // Last resort: alongside executable
                    var appDir = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                    screen3DiagnosticPath = System.IO.Path.Combine(appDir ?? ".", "screen3_diagnostic.txt");
                    return screen3DiagnosticPath;
                }
            }
        }


		
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

		// DEBUGGING: Strategic breakpoint targets for Screen 3
		private void DebugCalibrationLoad()
		{
			// BREAKPOINT TARGET: Set breakpoint here to inspect loaded calibration data
			var planeValid = isPlaneValid;
			var planeData = new { Nx = planeNx, Ny = planeNy, Nz = planeNz, D = planeD };
			var touchArea = calibration?.TouchArea != null ? new { X = calibration.TouchArea.X, Y = calibration.TouchArea.Y, Width = calibration.TouchArea.Width, Height = calibration.TouchArea.Height } : null;
			System.Diagnostics.Debug.WriteLine($"LOADED DATA: PlaneValid={planeValid}, Plane={planeData}, TouchArea={touchArea}, Robust Detection=Enabled");
		}

		private void DebugTouchDetection(int checkedPixels, int detectedPixels, List<Point> touchPixels, List<TouchPoint> activeTouches)
		{
			// BREAKPOINT TARGET: Set breakpoint here to inspect touch detection
			var detectionRate = checkedPixels > 0 ? (double)detectedPixels / checkedPixels * 100 : 0;
			var touchPixelCount = touchPixels.Count;
			var activeTouchCount = activeTouches.Count;
			var threshold = PlaneThresholdSlider?.Value ?? 0;
			
			System.Diagnostics.Debug.WriteLine($"DETECTION: Checked={checkedPixels}, Detected={detectedPixels} ({detectionRate:F1}%), TouchPixels={touchPixelCount}, ActiveTouches={activeTouchCount}, Threshold={threshold}mm");
		}

		private void DebugPlaneDistance(CameraSpacePoint point, double planeDistance)
		{
			// BREAKPOINT TARGET: Set breakpoint here to inspect plane distance calculations
			var point3D = new { X = point.X, Y = point.Y, Z = point.Z };
			var distance = planeDistance;
			var threshold = PlaneThresholdSlider?.Value * 0.001 ?? 0;
			var isTouch = distance > 0.010 && distance < threshold;
			
			System.Diagnostics.Debug.WriteLine($"PLANE DIST: Point={point3D}, Distance={distance:F3}m, Threshold={threshold:F3}m, IsTouch={isTouch}");
		}

		private void DebugTouchTracking(List<TouchPoint> newTouches, List<TouchPoint> updatedTouches)
		{
			// BREAKPOINT TARGET: Set breakpoint here to inspect touch tracking
			var newCount = newTouches.Count;
			var updatedCount = updatedTouches.Count;
			var totalActive = activeTouches.Count;
			
			System.Diagnostics.Debug.WriteLine($"TRACKING: New={newCount}, Updated={updatedCount}, TotalActive={totalActive}");
		}

		// DEBUGGING: Conditional breakpoint helpers
		private void DebugIfNoDetection()
		{
			// BREAKPOINT TARGET: Set conditional breakpoint here (when activeTouches.Count == 0)
			if (activeTouches.Count == 0)
			{
				var planeValid = isPlaneValid;
				var threshold = PlaneThresholdSlider?.Value ?? 0;
				var searchArea = cachedDepthTouchArea;
				
				System.Diagnostics.Debug.WriteLine($"NO DETECTION: PlaneValid={planeValid}, Threshold={threshold}mm, SearchArea={searchArea}");
			}
		}

		private void DebugIfFalsePositives()
		{
			// BREAKPOINT TARGET: Set conditional breakpoint here (when activeTouches.Count > 5)
			if (activeTouches.Count > 5)
			{
				var touchCount = activeTouches.Count;
				var threshold = PlaneThresholdSlider?.Value ?? 0;
				
				System.Diagnostics.Debug.WriteLine($"FALSE POSITIVES: TouchCount={touchCount}, Threshold={threshold}mm");
			}
		}

		// DEBUGGING: Real-time monitoring helper
		private void LogRealTimeDebug(string location, object data = null)
		{
			try
			{
				var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
				var message = $"[{timestamp}] {location}";
				
				if (data != null)
				{
					message += $" - {data}";
				}
				
				// Output to debug console for live monitoring
				System.Diagnostics.Debug.WriteLine(message);
				
				// Also log to file for persistent record
				LogToFile(GetDiagnosticPath(), message);
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"LogRealTimeDebug Error: {ex.Message}");
			}
		}


		private List<List<Point>> FindContours(bool[] mask, int width, int height)
		{
			var contours = new List<List<Point>>();
			var visited = new bool[width * height];
			
			for (int y = 0; y < height; y++)
			{
				for (int x = 0; x < width; x++)
				{
					int i = y * width + x;
					if (mask[i] && !visited[i])
					{
						var contour = new List<Point>();
						TraceContour(mask, visited, width, height, x, y, contour);
						if (contour.Count > 4) // Only keep meaningful contours
						{
							contours.Add(contour);
						}
					}
				}
			}
			return contours;
		}

		private void TraceContour(bool[] mask, bool[] visited, int width, int height, int startX, int startY, List<Point> contour)
		{
			var stack = new Stack<Point>();
			stack.Push(new Point(startX, startY));
			
			while (stack.Count > 0)
			{
				var p = stack.Pop();
				int x = (int)p.X;
				int y = (int)p.Y;
				int i = y * width + x;
				
				if (x < 0 || x >= width || y < 0 || y >= height || visited[i] || !mask[i])
					continue;
				
				visited[i] = true;
				contour.Add(p);
				
				// Add 8-connected neighbors
				for (int dy = -1; dy <= 1; dy++)
				{
					for (int dx = -1; dx <= 1; dx++)
					{
						if (dx == 0 && dy == 0) continue;
						stack.Push(new Point(x + dx, y + dy));
					}
				}
			}
		}

		private void DrawContourBorder(List<Point> contour, System.Windows.Media.Brush brush)
		{
			if (contour.Count < 3) return;
			
			// Create polygon for contour
			var polygon = new System.Windows.Shapes.Polygon
			{
				Stroke = brush,
				StrokeThickness = 2,
				Fill = System.Windows.Media.Brushes.Transparent,
				Tag = "ContourBorder"
			};
			
			var points = new PointCollection();
			foreach (var pt in contour)
			{
				points.Add(pt);
			}
			polygon.Points = points;
			
			OverlayCanvas.Children.Add(polygon);
		}


		private double Cross(Point a, Point b, Point c){return (b.X-a.X)*(c.Y-a.Y)-(b.Y-a.Y)*(c.X-a.X);}
		private List<Point> ConvexHull(List<Point> pts){
			if(pts==null||pts.Count<3) return pts?.ToList()??new List<Point>();
			var p=pts.OrderBy(v=>v.X).ThenBy(v=>v.Y).ToList(); var lo=new List<Point>(); foreach(var v in p){while(lo.Count>=2&&Cross(lo[lo.Count-2],lo[lo.Count-1],v)<=0) lo.RemoveAt(lo.Count-1); lo.Add(v);} var up=new List<Point>(); for(int i=p.Count-1;i>=0;i--){var v=p[i]; while(up.Count>=2&&Cross(up[up.Count-2],up[up.Count-1],v)<=0) up.RemoveAt(up.Count-1); up.Add(v);} lo.RemoveAt(lo.Count-1); up.RemoveAt(up.Count-1); lo.AddRange(up); return lo;
		}


		private void Open3x3(bool[] mask, int width, int height, int ax, int ay, int bx, int by)
		{
			// Use a temporary buffer for the operation
			var tmp = new bool[mask.Length];
			Array.Copy(mask, tmp, mask.Length);
			
			// Erode
			for (int y = ay + 1; y < by - 1; y++)
			{
				int row = y * width;
				for (int x = ax + 1; x < bx - 1; x++)
				{
					int i = row + x; 
					if (!tmp[i]) { mask[i] = false; continue; }
					
					int count = 0;
					for (int yy = -1; yy <= 1; yy++)
					{
						int nrow = (y + yy) * width;
						for (int xx = -1; xx <= 1; xx++) 
						{
							if (xx != 0 || yy != 0) 
								if (tmp[nrow + (x + xx)]) count++;
						}
					}
					mask[i] = count >= 4; // Directly modify the original array
				}
			}
			
			// Dilate
			// Use the eroded result as the new source for dilation
			Array.Copy(mask, tmp, mask.Length);
			for (int y = ay + 1; y < by - 1; y++)
			{
				int row = y * width;
				for (int x = ax + 1; x < bx - 1; x++)
				{
					int i = row + x; 
					bool any = false;
					for (int yy = -1; yy <= 1 && !any; yy++)
					{
						int nrow = (y + yy) * width;
						for (int xx = -1; xx <= 1; xx++) 
						{
							if (tmp[nrow + (x + xx)]) { any = true; break; }
						}
					}
					mask[i] = any; // Directly modify the original array
				}
			}
		}

    private void Close3x3(bool[] mask, int width, int height, int ax, int ay, int bx, int by)
    {
			// Use a temporary buffer for the operation
			var tmp = new bool[mask.Length];
			Array.Copy(mask, tmp, mask.Length);
			
            // Dilate first
            for (int y = ay + 1; y < by - 1; y++)
            {
                int row = y * width;
                for (int x = ax + 1; x < bx - 1; x++)
                {
                    int i = row + x; 
                    bool any = false;
                    for (int yy = -1; yy <= 1 && !any; yy++)
                    {
                        int nrow = (y + yy) * width;
                        for (int xx = -1; xx <= 1; xx++) 
                        {
                            if (tmp[nrow + (x + xx)]) { any = true; break; }
                        }
                    }
                    mask[i] = any; // Directly modify the original array
                }
            }
			
			// Then erode
			Array.Copy(mask, tmp, mask.Length);
			for (int y = ay + 1; y < by - 1; y++)
			{
				int row = y * width;
				for (int x = ax + 1; x < bx - 1; x++)
				{
					int i = row + x; 
					if (!mask[i]) { tmp[i] = false; continue; }
					
					int count = 0;
					for (int yy = -1; yy <= 1; yy++)
					{
						int nrow = (y + yy) * width;
						for (int xx = -1; xx <= 1; xx++) 
						{
							if (xx != 0 || yy != 0) 
								if (mask[nrow + (x + xx)]) count++;
						}
					}
					tmp[i] = count >= 4;
				}
			}
			
			// Copy results back to original array
			Array.Copy(tmp, mask, mask.Length);
		}

    // Local-contrast gate: keep only pixels protruding above their 3x3 neighborhood by a small bump threshold
    private void ApplyLocalContrastGate(bool[] mask, float[] delta, int width, int height, int ax, int ay, int bx, int by, float bumpMm = 1.5f)
    {
        try
        {
            if (mask == null || delta == null) return;
            float bump = bumpMm / 1000f; // meters
            int ax1 = Math.Max(ax + 1, 1);
            int ay1 = Math.Max(ay + 1, 1);
            int bx1 = Math.Min(bx - 1, width - 1);
            int by1 = Math.Min(by - 1, height - 1);

            for (int y = ay1; y < by1; y++)
            {
                int row = y * width;
                for (int x = ax1; x < bx1; x++)
                {
                    int i = row + x;
                    if (!mask[i]) continue;

                    float sum = 0f; int cnt = 0;
                    for (int yy = -1; yy <= 1; yy++)
                    {
                        int r2 = (y + yy) * width;
                        for (int xx = -1; xx <= 1; xx++)
                        {
                            int j = r2 + (x + xx);
                            float v = (j >= 0 && j < delta.Length) ? delta[j] : 0f;
                            if (v > 0 && !float.IsNaN(v) && !float.IsInfinity(v)) { sum += v; cnt++; }
                        }
                    }
                    float mean = (cnt > 0) ? sum / cnt : 0f;
                    if (delta[i] - mean < bump) mask[i] = false;
                }
            }
        }
        catch { }
    }

		private List<List<Point>> MergeCloseBlobs(List<List<Point>> blobs,int mergeDist){
			if(blobs.Count<=1) return blobs;
			var centers = blobs.Select(b=> new Point(b.Average(p=>p.X), b.Average(p=>p.Y))).ToList();
			int n=blobs.Count; var used=new bool[n];
			var outList=new List<List<Point>>();
			for(int i=0;i<n;i++){ if(used[i]) continue;
				var acc=new List<Point>(blobs[i]); used[i]=true;
				for(int j=i+1;j<n;j++){ if(used[j]) continue;
					double dx=centers[i].X - centers[j].X, dy=centers[i].Y - centers[j].Y;
					if(dx*dx+dy*dy <= mergeDist*mergeDist){ acc.AddRange(blobs[j]); used[j]=true; }
				}
				outList.Add(acc);
			}
			return outList;
		}

		private List<Point> DeDuplicateByProximity(List<Point> pts, int width, float[] delta, int radiusPx)
		{
			var r2 = radiusPx * radiusPx;
			var scored = pts.Select(p => new { P = p, D = delta[(int)p.Y * width + (int)p.X] })
						   .OrderBy(x => x.D).ToList();
			var kept = new List<Point>();
			foreach (var s in scored)
			{
				bool near = kept.Any(k => {
					var dx = s.P.X - k.X;
					var dy = s.P.Y - k.Y;
					return dx * dx + dy * dy <= r2;
				});
				if (!near) kept.Add(s.P);
			}
			return kept;
		}

		private double ComputeNormalDistanceMedian(CameraSpacePoint[] csp, int width, int height, int ax, int ay, int bx, int by, Plane plane)
		{
			if (csp == null || csp.Length == 0) return double.NaN;

			int stepX = Math.Max(1, (bx - ax) / 40);
			int stepY = Math.Max(1, (by - ay) / 40);
			var samples = new List<double>();

			for (int y = ay; y < by; y += stepY)
			{
				int row = y * width;
				for (int x = ax; x < bx; x += stepX)
				{
					int idx = row + x;
					if (idx < 0 || idx >= csp.Length) continue;

					var p = csp[idx];
					if (float.IsInfinity(p.X) || float.IsInfinity(p.Y) || float.IsInfinity(p.Z)) continue;

					double nd = plane.Nx * p.X + plane.Ny * p.Y + plane.Nz * p.Z + plane.D;
					if (double.IsNaN(nd) || double.IsInfinity(nd)) continue;

					samples.Add(nd);
				}
			}

			if (samples.Count == 0) return double.NaN;

			samples.Sort();
			int mid = samples.Count / 2;
			if (samples.Count % 2 == 0)
				return 0.5 * (samples[mid - 1] + samples[mid]);
			return samples[mid];
		}
	}
}
