using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KinectCalibrationWPF.KinectManager;
using Microsoft.Kinect;
using System.Collections.Generic;
using KinectCalibrationWPF.Models;
using KinectCalibrationWPF.Services;
using System.Linq;

namespace KinectCalibrationWPF.CalibrationWizard
{
	public partial class CalibrationWizardWindow : Window
	{
		private KinectManager.KinectManager kinectManager;
		private DispatcherTimer cameraUpdateTimer;
		private CameraSpacePoint? planeP1;
		private CameraSpacePoint? planeP2;
		private KinectCalibrationWPF.UI.MovablePointsCanvas.Vector3D? planeNormal;
		private double planeDistance;
		private System.Collections.Generic.List<CameraSpacePoint> finalCameraCorners;
		private CalibrationConfig calibrationConfig = new CalibrationConfig();
		
		public CalibrationWizardWindow(KinectManager.KinectManager kinectManager)
		{
			InitializeComponent();
			this.kinectManager = kinectManager;
			
			// Initialize Screen 1 diagnostics
			InitializeScreen1Diagnostics();
			
			InitializeCameraFeed();
			UpdateCameraStatus();

			// Configure movable points for Screen 1
			MovablePoints.SetMaxPoints(4);
			MovablePoints.PointsChanged += MovablePoints_PointsChanged;
			MovablePoints.PlaneRecalculated += MovablePoints_PlaneRecalculated;
			MovablePoints.StatusChanged += MovablePoints_StatusChanged;
			MovablePoints.PlaneCalculated += MovablePoints_PlaneCalculated;
			MovablePoints.SizeChanged += MovablePoints_SizeChanged;
			MovablePoints.ValidatePoint = ValidateClickTo3D;
			// When the whole window or image area resizes, re-draw points from normalized coords
			this.SizeChanged += Window_SizeChanged;
			UpdateButtonStates();
		}
		
		private void InitializeCameraFeed()
		{
			// Setup camera update timer
			cameraUpdateTimer = new DispatcherTimer();
			cameraUpdateTimer.Interval = TimeSpan.FromMilliseconds(33); // ~30 FPS
			cameraUpdateTimer.Tick += CameraUpdateTimer_Tick;
			cameraUpdateTimer.Start();
		}
		
		private void CameraUpdateTimer_Tick(object sender, EventArgs e)
		{
			try
			{
				if (kinectManager != null)
				{
					var colorBitmap = kinectManager.GetColorBitmap();
					if (colorBitmap != null)
					{
						CameraFeedImage.Source = colorBitmap;
					}
					UpdateCameraStatus();
					// Update frame counters on UI
					FrameCountersText.Text = string.Format("Color: {0} | Depth: {1}", kinectManager.colorFramesReceived, kinectManager.depthFramesReceived);
				}
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine(string.Format("Camera update error: {0}", ex.Message));
			}
		}
		
		private void UpdateCameraStatus()
		{
			if (kinectManager != null && kinectManager.IsInitialized)
			{
				var colorActive = kinectManager.IsColorStreamActive();
				var depthActive = kinectManager.IsDepthStreamActive();
				CameraStatusText.Text = colorActive && depthActive ? "Camera: Connected" : (colorActive ? "Camera: Color OK, Depth Pending" : "Camera: Waiting for frames...");
				CameraStatusText.Foreground = Brushes.Green;
			}
			else
			{
				CameraStatusText.Text = "Camera: Not Available (Test Mode)";
				CameraStatusText.Foreground = Brushes.Orange;
			}
		}
		
		protected override void OnClosed(EventArgs e)
		{
			if (cameraUpdateTimer != null)
			{
				cameraUpdateTimer.Stop();
			}
			base.OnClosed(e);
		}
		
		private void CalculatePlaneButton_Click(object sender, RoutedEventArgs e)
		{
			// Trigger explicit finalization using the current 4 points only
			MovablePoints.SetOverlay(null); // hide Stage A connector
			FinalizeAreaFromFourPoints();
		}
		
		private void ResetPointsButton_Click(object sender, RoutedEventArgs e)
		{
			MovablePoints.ClearPoints();
			UpdateButtonStates();
		}
		
		private void NextButton_Click(object sender, RoutedEventArgs e)
		{
			// Log depth data diagnostic before proceeding
			LogDepthDataDiagnostic();
			
			// Save screen 1 results into calibration config (use finalized data if available)
			if (planeNormal.HasValue)
			{
				calibrationConfig.Plane = new PlaneDefinition { Nx = planeNormal.Value.X, Ny = planeNormal.Value.Y, Nz = planeNormal.Value.Z, D = planeDistance };
			}
			var pts = MovablePoints.GetPointPositions();
			calibrationConfig.CornerPointsNormalized.Clear();
			foreach (var cp in MovablePoints.CalibrationPoints)
			{
				calibrationConfig.CornerPointsNormalized.Add(new System.Windows.Point(cp.Position.X, cp.Position.Y));
			}
			calibrationConfig.CornerPointsCamera.Clear();
			if (finalCameraCorners != null)
			{
				calibrationConfig.CornerPointsCamera.AddRange(finalCameraCorners.Select(p => new SerializableCameraSpacePoint(p)));
			}
			else if (kinectManager != null && kinectManager.IsInitialized && pts != null)
			{
				for (int i = 0; i < Math.Min(pts.Count, 4); i++)
				{
					var colorPt = CanvasToColor(pts[i]);
					CameraSpacePoint camPt;
					if (kinectManager.TryMapColorPixelToCameraSpace((int)Math.Round(colorPt.X), (int)Math.Round(colorPt.Y), out camPt))
					{
						calibrationConfig.CornerPointsCamera.Add(new SerializableCameraSpacePoint(camPt));
					}
				}
			}
			
			// Log calibration save diagnostic
			LogCalibrationSaveDiagnostic();
			
			// Persist immediately when proceeding to next screen
			try { CalibrationStorage.Save(calibrationConfig); } catch { }
			// Go to Screen 2
			var screen2 = new Screen2_MarkerAlignment(kinectManager, calibrationConfig);
			var res2 = screen2.ShowDialog();
			// Proceed to Screen 3 regardless of calibration transform for now
			var screen3 = new Screen3_TouchTest(kinectManager, calibrationConfig);
			var res3 = screen3.ShowDialog();
			this.Close();
		}
		
		private void CancelButton_Click(object sender, RoutedEventArgs e)
		{
			this.Close();
		}

		private void MovablePoints_PointsChanged(object sender, UI.CalibrationPointsChangedEventArgs e)
		{
			PointCountText.Text = string.Format("Points: {0}/4", e.Points.Count);
			PointStatusText.Text = e.Points.Count == 0 ? "Click to place first corner" :
				(e.Points.Count == 1 ? "Click to place second corner" :
				(e.Points.Count == 2 ? "Click to place third corner" :
				(e.Points.Count == 3 ? "Click to place fourth corner" : "Click 'Calculate Plane' to finalize")));
			// Any change invalidates the previously finalized polygon
			finalCameraCorners = null;
			MeasuresText.Text = string.Empty;
			StatusText.Text = e.Points.Count >= 4 ? "Ready to calculate plane" : StatusText.Text;
			StatusText.Foreground = e.Points.Count >= 4 ? Brushes.SteelBlue : StatusText.Foreground;
			UpdateButtonStates();
			// Live overlay: as soon as we have 4 points, draw/update green quad; else show 2-point connector; otherwise clear
			if (finalCameraCorners == null && e.Points.Count >= 4)
			{
				Draw2DQuadOverlay(e.Points);
			}
			else if (e.Points.Count == 2)
			{
				Draw2DConnectorOverlay(e.Points[0], e.Points[1]);
			}
			else
			{
				MovablePoints.SetOverlay(null);
			}
			
			// Log point placement diagnostic
			if (e.Points != null && e.Points.Count > 0)
			{
				var lastPoint = e.Points[e.Points.Count - 1];
				LogPointPlacementDiagnostic(lastPoint, e.Points.Count - 1);
			}
		}

		private void MovablePoints_PlaneRecalculated(object sender, EventArgs e)
		{
			// Only update status here; drawing will be handled after explicit Calculate
			StatusText.Text = "Plane recalculated!";
			StatusText.Foreground = Brushes.Green;
			UpdateButtonStates(planeCalculated:true);
			if (finalCameraCorners != null)
			{
				DrawFinalPolygonFromCameraPoints();
			}
			
			// Note: Plane calculation diagnostic is logged in FinalizeAreaFromFourPoints()
			// where the actual plane calculation happens
		}

		private void MovablePoints_StatusChanged(object sender, UI.StatusChangedEventArgs e)
		{
			StatusText.Text = e.Message;
			StatusText.Foreground = e.Color;
		}

		private void MovablePoints_PlaneCalculated(object sender, UI.MovablePointsCanvas.PlaneCalculatedEventArgs e)
		{
			// Host performs real 3D mapping and sets overlay
			TryComputePlaneFromPoints();
		}

		private void UpdateButtonStates(bool planeCalculated = false)
		{
			int count = MovablePoints.PointCount;
			CalculatePlaneButton.IsEnabled = (count >= 3);
			ResetPointsButton.IsEnabled = (count > 0);
			NextButton.IsEnabled = planeCalculated && count >= 4;
		}
		
		private void MovablePoints_SizeChanged(object sender, SizeChangedEventArgs e)
		{
			if (finalCameraCorners != null) { DrawFinalPolygonFromCameraPoints(); }
			MovablePoints.InvalidatePointsVisuals();
			if (finalCameraCorners == null && MovablePoints.PointCount == 2)
			{
				var pts = MovablePoints.GetPointPositions();
				Draw2DConnectorOverlay(pts[0], pts[1]);
			}
		}

		private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
		{
			if (finalCameraCorners != null) { DrawFinalPolygonFromCameraPoints(); }
			MovablePoints.InvalidatePointsVisuals();
			if (finalCameraCorners == null && MovablePoints.PointCount == 2)
			{
				var pts = MovablePoints.GetPointPositions();
				Draw2DConnectorOverlay(pts[0], pts[1]);
			}
		}

		private void Draw2DConnectorOverlay(Point s1, Point s2)
		{
			// Thin 2D rectangle between two screen points
			double dx = s2.X - s1.X, dy = s2.Y - s1.Y;
			double segLen = Math.Sqrt(dx * dx + dy * dy);
			if (segLen < 1e-6)
			{
				if (MovablePoints != null) MovablePoints.SetOverlay(null);
				return;
			}
			double ux = dx / segLen, uy = dy / segLen;
			double px = -uy, py = ux;
			double thickness = 6.0; // thin guide
			double halfT = thickness / 2.0;
			var a = new Point(s1.X + px * (-halfT), s1.Y + py * (-halfT));
			var b = new Point(s1.X + px * (halfT), s1.Y + py * (halfT));
			var c = new Point(s2.X + px * (halfT), s2.Y + py * (halfT));
			var d = new Point(s2.X + px * (-halfT), s2.Y + py * (-halfT));
			var polygon = new System.Windows.Shapes.Polygon();
			polygon.Fill = new SolidColorBrush(Color.FromArgb(120, 76, 175, 80));
			polygon.Points = new PointCollection { a, b, c, d };
			SetOverlaySafe(polygon);
		}

		private void Draw2DQuadOverlay(System.Collections.Generic.List<Point> pts)
		{
			if (pts == null || pts.Count < 4) { return; }
			var polygon = new System.Windows.Shapes.Polygon();
			polygon.Fill = new SolidColorBrush(Color.FromArgb(90, 76, 175, 80));
			polygon.Points = new PointCollection { pts[0], pts[1], pts[2], pts[3] };
			SetOverlaySafe(polygon);
		}

		private void TryComputePlaneFromPoints()
		{
			if (kinectManager == null || !kinectManager.IsInitialized)
			{
				return;
			}
			var points = MovablePoints.GetPointPositions();
			if (points == null || points.Count < 3)
			{
				return;
			}

			// Convert canvas-space points to color pixel coords, then map to 3D camera space
			var colorPt1 = CanvasToColor(points[0]);
			var colorPt2 = CanvasToColor(points[1]);
			var colorPt3 = CanvasToColor(points[2]);
			CameraSpacePoint p1, p2, p3;
			bool ok1 = kinectManager.TryMapColorPixelToCameraSpace((int)Math.Round(colorPt1.X), (int)Math.Round(colorPt1.Y), out p1);
			bool ok2 = kinectManager.TryMapColorPixelToCameraSpace((int)Math.Round(colorPt2.X), (int)Math.Round(colorPt2.Y), out p2);
			bool ok3 = kinectManager.TryMapColorPixelToCameraSpace((int)Math.Round(colorPt3.X), (int)Math.Round(colorPt3.Y), out p3);
			if (!ok1 || !ok2 || !ok3)
			{
				StatusText.Text = "Failed to map points to 3D. Ensure depth is available.";
				StatusText.Foreground = Brushes.Orange;
				return;
			}

			planeP1 = p1;
			planeP2 = p2;
			
			// Compute plane normal from three points
			var v1 = new KinectCalibrationWPF.UI.MovablePointsCanvas.Vector3D(p2.X - p1.X, p2.Y - p1.Y, p2.Z - p1.Z);
			var v2 = new KinectCalibrationWPF.UI.MovablePointsCanvas.Vector3D(p3.X - p1.X, p3.Y - p1.Y, p3.Z - p1.Z);
			var n = Cross(v1, v2);
			n = Normalize(n);
			planeNormal = n;
			planeDistance = -(n.X * p1.X + n.Y * p1.Y + n.Z * p1.Z);

			StatusText.Text = "3D Plane estimated";
			StatusText.Foreground = Brushes.Green;
			// Do not draw here; drawing is done after explicit Calculate
		}

		private bool ValidateClickTo3D(Point canvasPoint)
		{
			if (kinectManager == null || !kinectManager.IsInitialized)
			{
				return true; // allow in test mode
			}
			var colorPt = CanvasToColor(canvasPoint);
			CameraSpacePoint p;
			System.Diagnostics.Debug.WriteLine(string.Format("Attempting to map color point at: {0},{1}", (int)Math.Round(colorPt.X), (int)Math.Round(colorPt.Y)));
			bool ok = kinectManager.TryMapColorPixelToCameraSpace((int)Math.Round(colorPt.X), (int)Math.Round(colorPt.Y), out p);
			if (ok)
			{
				System.Diagnostics.Debug.WriteLine(string.Format("Mapped Point Depth (Z): {0:F3} at color ({1},{2})", p.Z, (int)Math.Round(colorPt.X), (int)Math.Round(colorPt.Y)));
				// show on UI briefly
				StatusText.Text = string.Format("Depth Z: {0:F3} m", p.Z);
				StatusText.Foreground = Brushes.Green;
			}
			else
			{
				System.Diagnostics.Debug.WriteLine(string.Format("Mapping failed at color ({0},{1})", (int)Math.Round(colorPt.X), (int)Math.Round(colorPt.Y)));
				StatusText.Text = "Mapping failed: no depth at click";
				StatusText.Foreground = Brushes.Orange;
			}
			return ok && !(float.IsNaN(p.X) || float.IsNaN(p.Y) || float.IsNaN(p.Z)) && p.Z > 0;
		}
		
		private static KinectCalibrationWPF.UI.MovablePointsCanvas.Vector3D Cross(KinectCalibrationWPF.UI.MovablePointsCanvas.Vector3D a, KinectCalibrationWPF.UI.MovablePointsCanvas.Vector3D b)
		{
			return new KinectCalibrationWPF.UI.MovablePointsCanvas.Vector3D(
				a.Y * b.Z - a.Z * b.Y,
				a.Z * b.X - a.X * b.Z,
				a.X * b.Y - a.Y * b.X
			);
		}
		
		private static double Length(KinectCalibrationWPF.UI.MovablePointsCanvas.Vector3D v)
		{
			return Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
		}
		
		private static KinectCalibrationWPF.UI.MovablePointsCanvas.Vector3D Normalize(KinectCalibrationWPF.UI.MovablePointsCanvas.Vector3D v)
		{
			double len = Length(v);
			if (len < 1e-9) return new KinectCalibrationWPF.UI.MovablePointsCanvas.Vector3D(0, 0, 1);
			return new KinectCalibrationWPF.UI.MovablePointsCanvas.Vector3D(v.X / len, v.Y / len, v.Z / len);
		}
		
		private void DrawPlaneOverlay()
		{
			// Requires a computed plane and Kinect initialization
			if (!planeNormal.HasValue || !planeP1.HasValue || !planeP2.HasValue)
			{
				return;
			}
			if (kinectManager == null || !kinectManager.IsInitialized || kinectManager.CoordinateMapper == null)
			{
				return;
			}
			
			// 3D rectangle based on plane normal and right/up vectors on the plane
			var mapper = kinectManager.CoordinateMapper;
			var p1Cam = planeP1.Value; var p2Cam = planeP2.Value;
			var planeN = planeNormal.HasValue ? planeNormal.Value : new KinectCalibrationWPF.UI.MovablePointsCanvas.Vector3D(0,0,1);
			// Basis on plane
			var worldUp = new KinectCalibrationWPF.UI.MovablePointsCanvas.Vector3D(0, 1, 0);
			var rightVec = Cross(planeN, worldUp);
			if (Length(rightVec) < 1e-6)
			{
				// Fallback if plane normal parallel to world up: use camera forward
				var cameraForward = new KinectCalibrationWPF.UI.MovablePointsCanvas.Vector3D(0, 0, 1);
				rightVec = Cross(planeN, cameraForward);
			}
			rightVec = Normalize(rightVec);
			var upVec = worldUp; // simple up
			var center = new KinectCalibrationWPF.UI.MovablePointsCanvas.Vector3D(
				(p1Cam.X + p2Cam.X) / 2.0,
				(p1Cam.Y + p2Cam.Y) / 2.0,
				(p1Cam.Z + p2Cam.Z) / 2.0);
			double rectWidth = 2.0; // meters
			double rectHeight = 1.5; // meters
			double hw = rectWidth / 2.0, hh = rectHeight / 2.0;
			var c1 = Add(Add(center, Scale(rightVec, hw)), Scale(upVec, hh));
			var c2 = Add(Add(center, Scale(rightVec, -hw)), Scale(upVec, hh));
			var c3 = Add(Add(center, Scale(rightVec, -hw)), Scale(upVec, -hh));
			var c4 = Add(Add(center, Scale(rightVec, hw)), Scale(upVec, -hh));
			var sp1 = mapper.MapCameraPointToColorSpace(new Microsoft.Kinect.CameraSpacePoint{ X=(float)c1.X, Y=(float)c1.Y, Z=(float)c1.Z });
			var sp2 = mapper.MapCameraPointToColorSpace(new Microsoft.Kinect.CameraSpacePoint{ X=(float)c2.X, Y=(float)c2.Y, Z=(float)c2.Z });
			var sp3 = mapper.MapCameraPointToColorSpace(new Microsoft.Kinect.CameraSpacePoint{ X=(float)c3.X, Y=(float)c3.Y, Z=(float)c3.Z });
			var sp4 = mapper.MapCameraPointToColorSpace(new Microsoft.Kinect.CameraSpacePoint{ X=(float)c4.X, Y=(float)c4.Y, Z=(float)c4.Z });
			// Validate all four corners strictly; if any invalid, do not draw to avoid distortion
			if (!IsValidColorSpacePoint(sp1) || !IsValidColorSpacePoint(sp2) || !IsValidColorSpacePoint(sp3) || !IsValidColorSpacePoint(sp4))
			{
				return;
			}
			var a = ColorToCanvas(sp1);
			var b = ColorToCanvas(sp2);
			var c = ColorToCanvas(sp3);
			var d = ColorToCanvas(sp4);
			var polygon = new System.Windows.Shapes.Polygon();
			polygon.Fill = new SolidColorBrush(Color.FromArgb(90, 76, 175, 80));
			polygon.Points = new PointCollection { a, b, c, d };
			SetOverlaySafe(polygon);
			UpdateButtonStates(true);
		}
		
		private static bool IsValidColorSpacePoint(ColorSpacePoint p)
		{
			return !(float.IsNaN(p.X) || float.IsNaN(p.Y) || float.IsInfinity(p.X) || float.IsInfinity(p.Y));
		}
		
		private void ComputeScaleAndOffset(out double scale, out double offsetX, out double offsetY)
		{
			double containerW = MovablePoints.ActualWidth;
			double containerH = MovablePoints.ActualHeight;
			int colorW = (kinectManager != null && kinectManager.ColorWidth > 0) ? kinectManager.ColorWidth : 1920;
			int colorH = (kinectManager != null && kinectManager.ColorHeight > 0) ? kinectManager.ColorHeight : 1080;
			
			if (containerW <= 0 || containerH <= 0)
			{
				scale = 1.0;
				offsetX = 0.0;
				offsetY = 0.0;
				return;
			}
			
			double sx = containerW / colorW;
			double sy = containerH / colorH;
			scale = Math.Min(sx, sy);
			double displayedW = colorW * scale;
			double displayedH = colorH * scale;
			offsetX = (containerW - displayedW) / 2.0;
			offsetY = (containerH - displayedH) / 2.0;
		}
		
		private Point CanvasToColor(Point canvasPoint)
		{
			double scale; double offsetX; double offsetY;
			ComputeScaleAndOffset(out scale, out offsetX, out offsetY);
			double x = (canvasPoint.X - offsetX) / scale;
			double y = (canvasPoint.Y - offsetY) / scale;
			int colorW = (kinectManager != null && kinectManager.ColorWidth > 0) ? kinectManager.ColorWidth : 1920;
			int colorH = (kinectManager != null && kinectManager.ColorHeight > 0) ? kinectManager.ColorHeight : 1080;
			x = Math.Max(0, Math.Min(x, colorW - 1));
			y = Math.Max(0, Math.Min(y, colorH - 1));
			return new Point(x, y);
		}
		
		private Point ColorToCanvas(ColorSpacePoint colorPoint)
		{
			double scale; double offsetX; double offsetY;
			ComputeScaleAndOffset(out scale, out offsetX, out offsetY);
			double x = colorPoint.X * scale + offsetX;
			double y = colorPoint.Y * scale + offsetY;
			return new Point(x, y);
		}
		
		private void AddCornerDebugMarker(Point p, string label)
		{
			var dot = new System.Windows.Shapes.Ellipse();
			dot.Width = 6; dot.Height = 6; dot.Fill = Brushes.LimeGreen; dot.Stroke = Brushes.Black; dot.StrokeThickness = 1;
			System.Windows.Controls.Canvas.SetLeft(dot, p.X - 3);
			System.Windows.Controls.Canvas.SetTop(dot, p.Y - 3);
			var text = new System.Windows.Controls.TextBlock();
			text.Text = label; text.FontSize = 10; text.Foreground = Brushes.Black; text.FontWeight = FontWeights.Bold;
			System.Windows.Controls.Canvas.SetLeft(text, p.X + 4);
			System.Windows.Controls.Canvas.SetTop(text, p.Y - 8);
			MovablePoints.Children.Add(dot);
			MovablePoints.Children.Add(text);
		}
		
		private void Draw2DOverlayFallback()
		{
			var pts = MovablePoints.GetPointPositions();
			if (pts == null || pts.Count < 2)
			{
				return;
			}
			
			if (pts.Count == 2)
			{
				Draw2DConnectorOverlay(pts[0], pts[1]);
				StatusText.Text = "Showing 2D overlay (test mode).";
				StatusText.Foreground = Brushes.Orange;
				UpdateButtonStates(true);
				return;
			}
			
			if (pts.Count >= 4)
			{
				Draw2DQuadOverlay(pts);
				StatusText.Text = "Showing 2D area overlay (test mode).";
				StatusText.Foreground = Brushes.Orange;
				UpdateButtonStates(true);
			}
		}
		private static KinectCalibrationWPF.UI.MovablePointsCanvas.Vector3D Add(KinectCalibrationWPF.UI.MovablePointsCanvas.Vector3D a, KinectCalibrationWPF.UI.MovablePointsCanvas.Vector3D b)
		{
			return new KinectCalibrationWPF.UI.MovablePointsCanvas.Vector3D(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
		}
		private static KinectCalibrationWPF.UI.MovablePointsCanvas.Vector3D Scale(KinectCalibrationWPF.UI.MovablePointsCanvas.Vector3D a, double s)
		{
			return new KinectCalibrationWPF.UI.MovablePointsCanvas.Vector3D(a.X * s, a.Y * s, a.Z * s);
		}

		private void FinalizeAreaFromFourPoints()
		{
			var ptsCanvas = MovablePoints.GetPointPositions();
			if (ptsCanvas == null || ptsCanvas.Count < 4 || kinectManager == null || !kinectManager.IsInitialized)
			{
				StatusText.Text = "Need 4 points and active Kinect to calculate.";
				StatusText.Foreground = Brushes.Orange;
				return;
			}
			var camPts = new System.Collections.Generic.List<CameraSpacePoint>(4);
			for (int i = 0; i < 4; i++)
			{
				var colorPt = CanvasToColor(ptsCanvas[i]);
				CameraSpacePoint cam;
				if (!kinectManager.TryMapColorPixelToCameraSpace((int)System.Math.Round(colorPt.X), (int)System.Math.Round(colorPt.Y), out cam))
				{
					StatusText.Text = "Mapping failed; click different points or ensure depth is available.";
					StatusText.Foreground = Brushes.Orange;
					return;
				}
				camPts.Add(cam);
			}

			// Compute plane from first three
			var v1 = new KinectCalibrationWPF.UI.MovablePointsCanvas.Vector3D(camPts[1].X - camPts[0].X, camPts[1].Y - camPts[0].Y, camPts[1].Z - camPts[0].Z);
			var v2 = new KinectCalibrationWPF.UI.MovablePointsCanvas.Vector3D(camPts[2].X - camPts[0].X, camPts[2].Y - camPts[0].Y, camPts[2].Z - camPts[0].Z);
			var n = Normalize(Cross(v1, v2));
			planeNormal = n;
			planeDistance = -(n.X * camPts[0].X + n.Y * camPts[0].Y + n.Z * camPts[0].Z);

			// ADD THIS LINE: Call debugging method for plane calculation
			var plane = new PlaneDefinition { Nx = n.X, Ny = n.Y, Nz = n.Z, D = planeDistance };
			DebugPlaneCalculation(plane, camPts);

			// ADD THIS LINE: Call debugging method for corner points
			DebugTouchAreaCorners(camPts);

			// Persist finalized corners
			finalCameraCorners = camPts;
			calibrationConfig.CornerPointsCamera.Clear();
			calibrationConfig.CornerPointsCamera.AddRange(camPts.Select(p => new SerializableCameraSpacePoint(p)));
			calibrationConfig.Plane = new KinectCalibrationWPF.Models.PlaneDefinition { Nx = n.X, Ny = n.Y, Nz = n.Z, D = planeDistance };

			// Draw polygon strictly from 3D → color → canvas for the four corners (matches red dots order)
			DrawFinalPolygonFromCameraPoints();

			// Update status and measures
			StatusText.Text = "Plane recalculated!";
			StatusText.Foreground = Brushes.Green;
			UpdateMeasures(camPts, ptsCanvas);
			UpdateButtonStates(true);
			
			// Log plane calculation diagnostic with actual results
			LogPlaneCalculationDiagnostic(camPts, n, planeDistance);
		}

		private void DrawFinalPolygonFromCameraPoints()
		{
			if (finalCameraCorners == null || kinectManager == null || !kinectManager.IsInitialized)
				return;
			var mapper = kinectManager.CoordinateMapper;
			var points = new System.Windows.Media.PointCollection();
			for (int i = 0; i < finalCameraCorners.Count; i++)
			{
				var c = finalCameraCorners[i];
				var sp = mapper.MapCameraPointToColorSpace(new Microsoft.Kinect.CameraSpacePoint { X = c.X, Y = c.Y, Z = c.Z });
				if (!IsValidColorSpacePoint(sp)) return; // if any invalid, skip drawing
				points.Add(ColorToCanvas(sp));
			}
			var polygon = new System.Windows.Shapes.Polygon();
			polygon.Fill = new SolidColorBrush(Color.FromArgb(90, 76, 175, 80));
			polygon.Points = points;
			MovablePoints.SetOverlay(polygon);
		}

		private static double Distance3(CameraSpacePoint a, CameraSpacePoint b)
		{
			double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
			return System.Math.Sqrt(dx * dx + dy * dy + dz * dz);
		}

		private void UpdateMeasures(System.Collections.Generic.List<CameraSpacePoint> camPts, System.Collections.Generic.List<System.Windows.Point> canvasPts)
		{
			// Identify top/bottom by canvas Y, left/right by canvas X
			var indices = new int[] { 0, 1, 2, 3 };
			System.Array.Sort(indices, (i, j) => canvasPts[i].Y.CompareTo(canvasPts[j].Y));
			int top1 = indices[0], top2 = indices[1], bot1 = indices[2], bot2 = indices[3];
			double widthTop = Distance3(camPts[top1], camPts[top2]);
			double widthBot = Distance3(camPts[bot1], camPts[bot2]);
			double width = (widthTop + widthBot) / 2.0;
			System.Array.Sort(indices, (i, j) => canvasPts[i].X.CompareTo(canvasPts[j].X));
			int left1 = indices[0], left2 = indices[1], right1 = indices[2], right2 = indices[3];
			double heightLeft = Distance3(camPts[left1], camPts[left2]);
			double heightRight = Distance3(camPts[right1], camPts[right2]);
			double height = (heightLeft + heightRight) / 2.0;
			// Center distance
			double cx = 0, cy = 0, cz = 0;
			for (int i = 0; i < 4; i++) { cx += camPts[i].X; cy += camPts[i].Y; cz += camPts[i].Z; }
			cx /= 4.0; cy /= 4.0; cz /= 4.0;
			double dist = System.Math.Sqrt(cx * cx + cy * cy + cz * cz);
			MeasuresText.Text = string.Format("Measures: Area: {0:F2}m x {1:F2}m | Distance: {2:F2}m", width, height, dist);
		}

		private void SetOverlaySafe(System.Windows.UIElement overlay)
		{
			if (MovablePoints != null)
			{
				MovablePoints.SetOverlay(overlay);
			}
		}
		
		// ===== COMPREHENSIVE SCREEN 1 DIAGNOSTIC METHODS =====
		
		private string GetScreen1DiagnosticPath()
		{
			var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
			var diagnosticDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "KinectCalibrationDiagnostics", $"Screen1_PlaneCalibration_{timestamp}");
			System.IO.Directory.CreateDirectory(diagnosticDir);
			return System.IO.Path.Combine(diagnosticDir, "screen1_diagnostic.txt");
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
		
		private void InitializeScreen1Diagnostics()
		{
			var diagnosticPath = GetScreen1DiagnosticPath();
			LogToFile(diagnosticPath, "=== SCREEN 1 PLANE CALIBRATION DIAGNOSTIC ===");
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
			
			// Log Kinect status
			LogToFile(diagnosticPath, "=== KINECT STATUS DIAGNOSTIC ===");
			if (kinectManager != null)
			{
				LogToFile(diagnosticPath, $"Kinect Initialized: {kinectManager.IsInitialized}");
				LogToFile(diagnosticPath, $"Color Stream Active: {kinectManager.IsColorStreamActive()}");
				LogToFile(diagnosticPath, $"Depth Stream Active: {kinectManager.IsDepthStreamActive()}");
				LogToFile(diagnosticPath, $"Color Resolution: {kinectManager.ColorWidth}x{kinectManager.ColorHeight}");
				LogToFile(diagnosticPath, $"Depth Resolution: {kinectManager.DepthWidth}x{kinectManager.DepthHeight}");
				LogToFile(diagnosticPath, $"Color Frames Received: {kinectManager.colorFramesReceived}");
				LogToFile(diagnosticPath, $"Depth Frames Received: {kinectManager.depthFramesReceived}");
			}
			else
			{
				LogToFile(diagnosticPath, "ERROR: KinectManager is null");
			}
			LogToFile(diagnosticPath, "");
			
			// Log initial calibration state
			LogToFile(diagnosticPath, "=== INITIAL CALIBRATION STATE ===");
			LogToFile(diagnosticPath, $"Plane Normal: {planeNormal?.X ?? 0:F3}, {planeNormal?.Y ?? 0:F3}, {planeNormal?.Z ?? 0:F3}");
			LogToFile(diagnosticPath, $"Plane Distance: {planeDistance:F3}");
			LogToFile(diagnosticPath, $"Plane P1: {planeP1?.X ?? 0:F3}, {planeP1?.Y ?? 0:F3}, {planeP1?.Z ?? 0:F3}");
			LogToFile(diagnosticPath, $"Plane P2: {planeP2?.X ?? 0:F3}, {planeP2?.Y ?? 0:F3}, {planeP2?.Z ?? 0:F3}");
			LogToFile(diagnosticPath, $"Final Camera Corners Count: {finalCameraCorners?.Count ?? 0}");
			LogToFile(diagnosticPath, "");
			
			LogToFile(diagnosticPath, "Screen 1 diagnostics initialized successfully");
			LogToFile(diagnosticPath, "");
		}
		
		private void LogPointPlacementDiagnostic(System.Windows.Point canvasPoint, int pointIndex)
		{
			var diagnosticPath = GetScreen1DiagnosticPath();
			LogToFile(diagnosticPath, "=== POINT PLACEMENT DIAGNOSTIC ===");
			LogToFile(diagnosticPath, $"Point {pointIndex + 1} placed at canvas coordinates: ({canvasPoint.X:F1}, {canvasPoint.Y:F1})");
			
			try
			{
				// Convert to color coordinates
				var colorPoint = CanvasToColor(canvasPoint);
				LogToFile(diagnosticPath, $"Point {pointIndex + 1} color coordinates: ({colorPoint.X:F1}, {colorPoint.Y:F1})");
				
				// Try to map to 3D
				if (kinectManager != null && kinectManager.IsInitialized)
				{
					CameraSpacePoint cameraPoint;
					bool mappingSuccess = kinectManager.TryMapColorPixelToCameraSpace((int)Math.Round(colorPoint.X), (int)Math.Round(colorPoint.Y), out cameraPoint);
					
					if (mappingSuccess)
					{
						LogToFile(diagnosticPath, $"Point {pointIndex + 1} 3D coordinates: ({cameraPoint.X:F3}, {cameraPoint.Y:F3}, {cameraPoint.Z:F3})");
						LogToFile(diagnosticPath, $"Point {pointIndex + 1} distance from Kinect: {cameraPoint.Z:F3}m");
						
						// Check if depth is valid
						if (float.IsNaN(cameraPoint.Z) || cameraPoint.Z <= 0)
						{
							LogToFile(diagnosticPath, $"WARNING: Point {pointIndex + 1} has invalid depth data");
						}
						else if (cameraPoint.Z < 0.5 || cameraPoint.Z > 4.0)
						{
							LogToFile(diagnosticPath, $"WARNING: Point {pointIndex + 1} is outside recommended range (0.5m - 4.0m)");
						}
					}
					else
					{
						LogToFile(diagnosticPath, $"ERROR: Failed to map Point {pointIndex + 1} to 3D space");
					}
				}
				else
				{
					LogToFile(diagnosticPath, $"ERROR: Kinect not available for Point {pointIndex + 1} mapping");
				}
			}
			catch (Exception ex)
			{
				LogToFile(diagnosticPath, $"ERROR in Point {pointIndex + 1} processing: {ex.Message}");
			}
			
			LogToFile(diagnosticPath, "");
		}
		
		private void LogPlaneCalculationDiagnostic()
		{
			LogPlaneCalculationDiagnostic(null, null, 0);
		}
		
		private void LogPlaneCalculationDiagnostic(System.Collections.Generic.List<CameraSpacePoint> camPts, KinectCalibrationWPF.UI.MovablePointsCanvas.Vector3D? normal, double distance)
		{
			var diagnosticPath = GetScreen1DiagnosticPath();
			LogToFile(diagnosticPath, "=== PLANE CALCULATION DIAGNOSTIC ===");
			
			try
			{
				var points = MovablePoints.GetPointPositions();
				LogToFile(diagnosticPath, $"Total points placed: {points?.Count ?? 0}");
				
				if (points == null || points.Count < 3)
				{
					LogToFile(diagnosticPath, "ERROR: Not enough points for plane calculation (need at least 3)");
					return;
				}
				
				// Log all points
				for (int i = 0; i < points.Count; i++)
				{
					var canvasPoint = points[i];
					var colorPoint = CanvasToColor(canvasPoint);
					LogToFile(diagnosticPath, $"Point {i + 1}: Canvas=({canvasPoint.X:F1},{canvasPoint.Y:F1}), Color=({colorPoint.X:F1},{colorPoint.Y:F1})");
					
					// Try to get 3D coordinates
					if (kinectManager != null && kinectManager.IsInitialized)
					{
						CameraSpacePoint cameraPoint;
						if (kinectManager.TryMapColorPixelToCameraSpace((int)Math.Round(colorPoint.X), (int)Math.Round(colorPoint.Y), out cameraPoint))
						{
							LogToFile(diagnosticPath, $"Point {i + 1} 3D: ({cameraPoint.X:F3}, {cameraPoint.Y:F3}, {cameraPoint.Z:F3})");
						}
						else
						{
							LogToFile(diagnosticPath, $"Point {i + 1} 3D: MAPPING FAILED");
						}
					}
				}
				
				// Check if plane was calculated
				if (normal.HasValue)
				{
					LogToFile(diagnosticPath, "=== PLANE CALCULATION RESULTS ===");
					LogToFile(diagnosticPath, $"Plane Normal: ({normal.Value.X:F6}, {normal.Value.Y:F6}, {normal.Value.Z:F6})");
					LogToFile(diagnosticPath, $"Plane Distance: {distance:F6}");
					LogToFile(diagnosticPath, $"Plane Equation: {normal.Value.X:F6}*X + {normal.Value.Y:F6}*Y + {normal.Value.Z:F6}*Z + {distance:F6} = 0");
					
					// Validate plane normal
					double normalLength = Math.Sqrt(normal.Value.X * normal.Value.X + normal.Value.Y * normal.Value.Y + normal.Value.Z * normal.Value.Z);
					LogToFile(diagnosticPath, $"Plane Normal Length: {normalLength:F6} (should be close to 1.0)");
					
					if (Math.Abs(normalLength - 1.0) > 0.1)
					{
						LogToFile(diagnosticPath, "WARNING: Plane normal is not properly normalized");
					}
					
					// Check if plane is reasonable
					if (Math.Abs(normal.Value.X) < 0.001 && Math.Abs(normal.Value.Y) < 0.001 && Math.Abs(normal.Value.Z) < 0.001)
					{
						LogToFile(diagnosticPath, "ERROR: Plane normal is zero vector - calculation failed");
					}
					
					// Log plane quality metrics
					if (camPts != null && camPts.Count >= 3)
					{
						LogToFile(diagnosticPath, "=== PLANE QUALITY ANALYSIS ===");
						
						// Calculate distances from points to plane
						for (int i = 0; i < Math.Min(camPts.Count, 3); i++)
						{
							var point = camPts[i];
							double distanceToPlane = Math.Abs(normal.Value.X * point.X + normal.Value.Y * point.Y + normal.Value.Z * point.Z + distance);
							LogToFile(diagnosticPath, $"Point {i + 1} distance to plane: {distanceToPlane:F6}m");
						}
						
						// Calculate plane area (if we have 4 points)
						if (camPts.Count == 4)
						{
							var p1 = camPts[0];
							var p2 = camPts[1];
							var p3 = camPts[2];
							var p4 = camPts[3];
							
							// Calculate area using cross product
							var v1 = new KinectCalibrationWPF.UI.MovablePointsCanvas.Vector3D(p2.X - p1.X, p2.Y - p1.Y, p2.Z - p1.Z);
							var v2 = new KinectCalibrationWPF.UI.MovablePointsCanvas.Vector3D(p3.X - p1.X, p3.Y - p1.Y, p3.Z - p1.Z);
							var cross = Cross(v1, v2);
							double area = Math.Sqrt(cross.X * cross.X + cross.Y * cross.Y + cross.Z * cross.Z) / 2.0;
							LogToFile(diagnosticPath, $"Calculated plane area: {area:F6} square meters");
						}
					}
					else if (finalCameraCorners != null && finalCameraCorners.Count >= 3)
					{
						LogToFile(diagnosticPath, "=== PLANE QUALITY ANALYSIS ===");
						
						// Calculate distances from points to plane
						for (int i = 0; i < Math.Min(finalCameraCorners.Count, 3); i++)
						{
							var point = finalCameraCorners[i];
							double distanceToPlane = Math.Abs(planeNormal.Value.X * point.X + planeNormal.Value.Y * point.Y + planeNormal.Value.Z * point.Z + planeDistance);
							LogToFile(diagnosticPath, $"Point {i + 1} distance to plane: {distanceToPlane:F6}m");
						}
						
						// Calculate plane area (if we have 4 points)
						if (finalCameraCorners.Count == 4)
						{
							var p1 = finalCameraCorners[0];
							var p2 = finalCameraCorners[1];
							var p3 = finalCameraCorners[2];
							var p4 = finalCameraCorners[3];
							
							// Calculate area using cross product
							var v1 = new KinectCalibrationWPF.UI.MovablePointsCanvas.Vector3D(p2.X - p1.X, p2.Y - p1.Y, p2.Z - p1.Z);
							var v2 = new KinectCalibrationWPF.UI.MovablePointsCanvas.Vector3D(p3.X - p1.X, p3.Y - p1.Y, p3.Z - p1.Z);
							var cross = Cross(v1, v2);
							double area = Math.Sqrt(cross.X * cross.X + cross.Y * cross.Y + cross.Z * cross.Z) / 2.0;
							LogToFile(diagnosticPath, $"Calculated plane area: {area:F6} square meters");
						}
					}
				}
				else
				{
					LogToFile(diagnosticPath, "ERROR: Plane normal is null - calculation failed");
				}
			}
			catch (Exception ex)
			{
				LogToFile(diagnosticPath, $"ERROR in plane calculation diagnostic: {ex.Message}");
				LogToFile(diagnosticPath, $"Stack Trace: {ex.StackTrace}");
			}
			
			LogToFile(diagnosticPath, "");
		}
		
		private void LogCalibrationSaveDiagnostic()
		{
			var diagnosticPath = GetScreen1DiagnosticPath();
			LogToFile(diagnosticPath, "=== CALIBRATION SAVE DIAGNOSTIC ===");
			
			try
			{
				LogToFile(diagnosticPath, "Attempting to save calibration data...");
				
				// Log what we're about to save
				if (calibrationConfig != null)
				{
					LogToFile(diagnosticPath, $"CalibrationConfig is not null");
					
					if (calibrationConfig.Plane != null)
					{
						LogToFile(diagnosticPath, $"Plane to save: Nx={calibrationConfig.Plane.Nx:F6}, Ny={calibrationConfig.Plane.Ny:F6}, Nz={calibrationConfig.Plane.Nz:F6}, D={calibrationConfig.Plane.D:F6}");
					}
					else
					{
						LogToFile(diagnosticPath, "WARNING: Plane is null in CalibrationConfig");
					}
					
					LogToFile(diagnosticPath, $"CornerPointsNormalized count: {calibrationConfig.CornerPointsNormalized?.Count ?? 0}");
					LogToFile(diagnosticPath, $"CornerPointsCamera count: {calibrationConfig.CornerPointsCamera?.Count ?? 0}");
					
					// ADD THIS LINE: Call debugging method
					DebugCalibrationSave();
					
					// Try to save
					CalibrationStorage.Save(calibrationConfig);
					LogToFile(diagnosticPath, "Calibration saved successfully!");
					
					// Verify by loading back
					var loadedConfig = CalibrationStorage.Load();
					if (loadedConfig != null && loadedConfig.Plane != null)
					{
						LogToFile(diagnosticPath, $"Verification - Loaded plane: Nx={loadedConfig.Plane.Nx:F6}, Ny={loadedConfig.Plane.Ny:F6}, Nz={loadedConfig.Plane.Nz:F6}, D={loadedConfig.Plane.D:F6}");
					}
					else
					{
						LogToFile(diagnosticPath, "ERROR: Could not verify saved calibration");
					}
				}
				else
				{
					LogToFile(diagnosticPath, "ERROR: CalibrationConfig is null");
				}
			}
			catch (Exception ex)
			{
				LogToFile(diagnosticPath, $"ERROR saving calibration: {ex.Message}");
				LogToFile(diagnosticPath, $"Stack Trace: {ex.StackTrace}");
			}
			
			LogToFile(diagnosticPath, "");
		}
		
		private void LogDepthDataDiagnostic()
		{
			var diagnosticPath = GetScreen1DiagnosticPath();
			LogToFile(diagnosticPath, "=== DEPTH DATA DIAGNOSTIC ===");
			
			try
			{
				if (kinectManager != null && kinectManager.IsInitialized)
				{
					ushort[] depthData;
					int width, height;
					if (kinectManager.TryGetDepthFrameRaw(out depthData, out width, out height))
					{
						LogToFile(diagnosticPath, $"Depth data available: {width}x{height}, {depthData.Length} pixels");
						
						// Analyze depth data
						var validDepths = depthData.Where(d => d > 0).ToArray();
						if (validDepths.Length > 0)
						{
							var minDepth = validDepths.Min() / 1000.0; // Convert to meters
							var maxDepth = validDepths.Max() / 1000.0;
							var avgDepth = validDepths.Average(d => d / 1000.0);
							
							LogToFile(diagnosticPath, $"Depth range: {minDepth:F3}m - {maxDepth:F3}m");
							LogToFile(diagnosticPath, $"Average depth: {avgDepth:F3}m");
							LogToFile(diagnosticPath, $"Valid depth pixels: {validDepths.Length}/{depthData.Length} ({100.0 * validDepths.Length / depthData.Length:F1}%)");
						}
						else
						{
							LogToFile(diagnosticPath, "WARNING: No valid depth data found");
						}
					}
					else
					{
						LogToFile(diagnosticPath, "ERROR: Could not get depth frame data");
					}
				}
				else
				{
					LogToFile(diagnosticPath, "ERROR: Kinect not available for depth analysis");
				}
			}
			catch (Exception ex)
			{
				LogToFile(diagnosticPath, $"ERROR in depth data diagnostic: {ex.Message}");
			}
			
			LogToFile(diagnosticPath, "");
		}

		// DEBUGGING: Strategic breakpoint targets for Screen 1
		private void DebugPlaneCalculation(PlaneDefinition plane, List<CameraSpacePoint> points)
		{
			// BREAKPOINT TARGET: Set breakpoint here to inspect plane calculation
			var planeData = new { Nx = plane.Nx, Ny = plane.Ny, Nz = plane.Nz, D = plane.D };
			var pointCount = points.Count;
			var normalMagnitude = Math.Sqrt(plane.Nx * plane.Nx + plane.Ny * plane.Ny + plane.Nz * plane.Nz);
			
			// Test plane with first point
			var testDistance = points.Count > 0 ? plane.Nx * points[0].X + plane.Ny * points[0].Y + plane.Nz * points[0].Z + plane.D : 0;
			
			System.Diagnostics.Debug.WriteLine($"PLANE CALC: Normal={planeData}, Points={pointCount}, Magnitude={normalMagnitude:F6}, TestDist={testDistance:F6}m");
		}

		private void DebugTouchAreaCorners(List<CameraSpacePoint> corners)
		{
			// BREAKPOINT TARGET: Set breakpoint here to inspect corner points
			var cornerCount = corners.Count;
			var centerX = corners.Average(c => c.X);
			var centerY = corners.Average(c => c.Y);
			var centerZ = corners.Average(c => c.Z);
			
			System.Diagnostics.Debug.WriteLine($"CORNERS: Count={cornerCount}, Center=({centerX:F3}, {centerY:F3}, {centerZ:F3})");
		}

		private void DebugCalibrationSave()
		{
			// BREAKPOINT TARGET: Set breakpoint here to inspect what gets saved
			var planeData = new { Nx = calibrationConfig.Plane.Nx, Ny = calibrationConfig.Plane.Ny, Nz = calibrationConfig.Plane.Nz, D = calibrationConfig.Plane.D };
			var cornerCount = calibrationConfig.TouchAreaCorners3D?.Count ?? 0;
			var distance = calibrationConfig.KinectToSurfaceDistanceMeters;
			
			System.Diagnostics.Debug.WriteLine($"SAVING: Plane={planeData}, Corners={cornerCount}, Distance={distance:F3}m");
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
				LogToFile(GetScreen1DiagnosticPath(), message);
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"LogRealTimeDebug Error: {ex.Message}");
			}
		}
	}
}
