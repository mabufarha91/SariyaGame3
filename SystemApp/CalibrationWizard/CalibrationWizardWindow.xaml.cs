using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KinectCalibrationWPF.KinectManager;
using Microsoft.Kinect;

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
        
        public CalibrationWizardWindow(KinectManager.KinectManager kinectManager)
        {
            InitializeComponent();
            this.kinectManager = kinectManager;
            
            InitializeCameraFeed();
            UpdateCameraStatus();

            // Configure movable points for Screen 1
            MovablePoints.SetMaxPoints(2);
            MovablePoints.PointsChanged += MovablePoints_PointsChanged;
            MovablePoints.PlaneRecalculated += MovablePoints_PlaneRecalculated;
            MovablePoints.StatusChanged += MovablePoints_StatusChanged;
            MovablePoints.PlaneCalculated += MovablePoints_PlaneCalculated;
            MovablePoints.SizeChanged += MovablePoints_SizeChanged;
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
                CameraStatusText.Text = "Camera: Connected";
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
            // Trigger recalculation explicitly
            MovablePoints.RecalculatePlane();
        }
        
        private void ResetPointsButton_Click(object sender, RoutedEventArgs e)
        {
            MovablePoints.ClearPoints();
            UpdateButtonStates();
        }
        
        private void NextButton_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Next Screen functionality will be implemented in the next phase.", 
                          "Info", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        
        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void MovablePoints_PointsChanged(object sender, UI.CalibrationPointsChangedEventArgs e)
        {
            PointCountText.Text = string.Format("Points: {0}/2", e.Points.Count);
            PointStatusText.Text = e.Points.Count == 0 ? "Click to place first point" : (e.Points.Count == 1 ? "Click to place second point" : "Drag to adjust points or calculate plane");
            UpdateButtonStates();
        }

        private void MovablePoints_PlaneRecalculated(object sender, EventArgs e)
        {
            StatusText.Text = "Plane recalculated!";
            StatusText.Foreground = Brushes.Green;
            UpdateButtonStates(planeCalculated:true);
            DrawPlaneOverlay();
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
            CalculatePlaneButton.IsEnabled = (count == 2);
            ResetPointsButton.IsEnabled = (count > 0);
            NextButton.IsEnabled = planeCalculated && count == 2;
        }

        private void MovablePoints_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (planeNormal.HasValue)
            {
                DrawPlaneOverlay();
            }
        }

        private void TryComputePlaneFromPoints()
        {
            if (kinectManager == null || !kinectManager.IsInitialized)
            {
                return;
            }
            var points = MovablePoints.GetPointPositions();
            if (points == null || points.Count != 2)
            {
                return;
            }

            // Convert canvas-space points to color pixel coords, then map to 3D camera space
            var colorPt1 = CanvasToColor(points[0]);
            var colorPt2 = CanvasToColor(points[1]);
            CameraSpacePoint p1, p2;
            bool ok1 = kinectManager.TryMapColorPixelToCameraSpace((int)Math.Round(colorPt1.X), (int)Math.Round(colorPt1.Y), out p1);
            bool ok2 = kinectManager.TryMapColorPixelToCameraSpace((int)Math.Round(colorPt2.X), (int)Math.Round(colorPt2.Y), out p2);
            if (!ok1 || !ok2)
            {
                StatusText.Text = "Failed to map points to 3D. Ensure depth is available.";
                StatusText.Foreground = Brushes.Orange;
                return;
            }

            planeP1 = p1;
            planeP2 = p2;

            // Compute plane normal consistent with spec: use world up to form a plane with the segment
            var rightVec = new KinectCalibrationWPF.UI.MovablePointsCanvas.Vector3D(p2.X - p1.X, p2.Y - p1.Y, p2.Z - p1.Z);
            rightVec = Normalize(rightVec);
            var worldUp = new KinectCalibrationWPF.UI.MovablePointsCanvas.Vector3D(0, 1, 0);
            var n = Cross(rightVec, worldUp); // normal ≈ perpendicular to right and up
            if (Length(n) < 1e-6)
            {
                // Fallback if segment parallel to up; use camera forward
                var cameraForward = new KinectCalibrationWPF.UI.MovablePointsCanvas.Vector3D(0, 0, 1);
                n = Cross(rightVec, cameraForward);
            }
            n = Normalize(n);
            planeNormal = n;
            planeDistance = -(n.X * p1.X + n.Y * p1.Y + n.Z * p1.Z);

            StatusText.Text = "3D Plane estimated";
            StatusText.Foreground = Brushes.Green;
            DrawPlaneOverlay();
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

            // Define a large rectangle in 3D on the plane using right and up vectors on the plane
            var p1Cam = planeP1.Value;
            var p2Cam = planeP2.Value;
            var planeN = planeNormal.Value;

            var rightVec = new KinectCalibrationWPF.UI.MovablePointsCanvas.Vector3D(p2Cam.X - p1Cam.X, p2Cam.Y - p1Cam.Y, p2Cam.Z - p1Cam.Z);
            rightVec = Normalize(rightVec);
            // Per spec: upDirection = cross(planeNormal, rightDirection)
            var upVec = Cross(planeN, rightVec);
            upVec = Normalize(upVec);

            // Center at midpoint of the two points
            var center = new KinectCalibrationWPF.UI.MovablePointsCanvas.Vector3D(
                (p1Cam.X + p2Cam.X) / 2.0,
                (p1Cam.Y + p2Cam.Y) / 2.0,
                (p1Cam.Z + p2Cam.Z) / 2.0);

            // FIXED rectangle size in meters (as requested for debugging)
            double rectWidth = 2.0;
            double rectHeight = 1.5;
            double halfWidth = rectWidth / 2.0;
            double halfHeight = rectHeight / 2.0;

            // 3D corners in camera space (TopRight, TopLeft, BottomLeft, BottomRight)
            var c1 = Add(Add(center, Scale(rightVec, halfWidth)), Scale(upVec, halfHeight));
            var c2 = Add(Add(center, Scale(rightVec, -halfWidth)), Scale(upVec, halfHeight));
            var c3 = Add(Add(center, Scale(rightVec, -halfWidth)), Scale(upVec, -halfHeight));
            var c4 = Add(Add(center, Scale(rightVec, halfWidth)), Scale(upVec, -halfHeight));

            // Project 3D corners to 2D color space
            var mapper = kinectManager.CoordinateMapper;
            var p2d1 = mapper.MapCameraPointToColorSpace(new CameraSpacePoint { X = (float)c1.X, Y = (float)c1.Y, Z = (float)c1.Z });
            var p2d2 = mapper.MapCameraPointToColorSpace(new CameraSpacePoint { X = (float)c2.X, Y = (float)c2.Y, Z = (float)c2.Z });
            var p2d3 = mapper.MapCameraPointToColorSpace(new CameraSpacePoint { X = (float)c3.X, Y = (float)c3.Y, Z = (float)c3.Z });
            var p2d4 = mapper.MapCameraPointToColorSpace(new CameraSpacePoint { X = (float)c4.X, Y = (float)c4.Y, Z = (float)c4.Z });

            // If any corner projects invalid, draw 2D fallback so users still see feedback
            if (!(IsValidColorSpacePoint(p2d1) && IsValidColorSpacePoint(p2d2) && IsValidColorSpacePoint(p2d3) && IsValidColorSpacePoint(p2d4)))
            {
                StatusText.Text = "3D projection unavailable; showing 2D overlay.";
                StatusText.Foreground = Brushes.Orange;
                Draw2DOverlayFallback();
                return;
            }

            // Convert color-space coords to canvas-space for drawing
            var a = ColorToCanvas(p2d1);
            var b = ColorToCanvas(p2d2);
            var c = ColorToCanvas(p2d3);
            var d = ColorToCanvas(p2d4);

            // Build overlay polygon and set on MovablePoints so points stay interactive on top
            var polygon = new System.Windows.Shapes.Polygon();
            polygon.Fill = new SolidColorBrush(Color.FromArgb(90, 76, 175, 80)); // semi-transparent green
            polygon.Points = new PointCollection { a, b, c, d };
            MovablePoints.SetOverlay(polygon);
            UpdateButtonStates(true);

            // Debug corner markers to verify order and shape
            AddCornerDebugMarker(a, "1");
            AddCornerDebugMarker(b, "2");
            AddCornerDebugMarker(c, "3");
            AddCornerDebugMarker(d, "4");
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
            if (pts == null || pts.Count != 2)
            {
                return;
            }

            // Create an oriented rectangle centered between points, aligned with the line connecting them
            var p1 = pts[0];
            var p2 = pts[1];
            double dx = p2.X - p1.X;
            double dy = p2.Y - p1.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-6)
                return;

            double ux = dx / len;
            double uy = dy / len;
            double px = -uy; // perpendicular
            double py = ux;

            // Dimensions relative to the distance between the points (keeps the overlay close and not oversized)
            double centerX = (p1.X + p2.X) / 2.0;
            double centerY = (p1.Y + p2.Y) / 2.0;
            // Use the segment length as base; extend a bit to show "wall" extent
            double halfLength = Math.Max(40, len * 1.2);  // total length ~ 2.4x distance between points
            double halfThickness = Math.Max(12, len * 0.35); // total thickness ~ 0.7x distance between points

            var a = new Point(centerX - ux * halfLength - px * halfThickness, centerY - uy * halfLength - py * halfThickness);
            var b = new Point(centerX + ux * halfLength - px * halfThickness, centerY + uy * halfLength - py * halfThickness);
            var c = new Point(centerX + ux * halfLength + px * halfThickness, centerY + uy * halfLength + py * halfThickness);
            var d = new Point(centerX - ux * halfLength + px * halfThickness, centerY - uy * halfLength + py * halfThickness);

            var polygon = new System.Windows.Shapes.Polygon();
            polygon.Fill = new SolidColorBrush(Color.FromArgb(80, 76, 175, 80));
            polygon.Points = new PointCollection { a, b, c, d };
            MovablePoints.SetOverlay(polygon);
            StatusText.Text = "Showing 2D overlay (test mode).";
            StatusText.Foreground = Brushes.Orange;
            UpdateButtonStates(true);
        }
        private static KinectCalibrationWPF.UI.MovablePointsCanvas.Vector3D Add(KinectCalibrationWPF.UI.MovablePointsCanvas.Vector3D a, KinectCalibrationWPF.UI.MovablePointsCanvas.Vector3D b)
        {
            return new KinectCalibrationWPF.UI.MovablePointsCanvas.Vector3D(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        }
        private static KinectCalibrationWPF.UI.MovablePointsCanvas.Vector3D Scale(KinectCalibrationWPF.UI.MovablePointsCanvas.Vector3D a, double s)
        {
            return new KinectCalibrationWPF.UI.MovablePointsCanvas.Vector3D(a.X * s, a.Y * s, a.Z * s);
        }
    }
}
