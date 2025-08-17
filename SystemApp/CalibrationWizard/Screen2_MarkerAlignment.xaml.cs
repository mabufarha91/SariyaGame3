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

namespace KinectCalibrationWPF.CalibrationWizard
{
	public partial class Screen2_MarkerAlignment : Window
	{
		private KinectManager.KinectManager kinectManager;
		private DispatcherTimer cameraUpdateTimer;
		private bool markersDetected = false;
		private ProjectorWindow projectorWindow;

		public Screen2_MarkerAlignment(KinectManager.KinectManager manager)
		{
			InitializeComponent();
			kinectManager = manager;
			Loaded += Screen2_MarkerAlignment_Loaded;
			cameraUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
			cameraUpdateTimer.Tick += (s, e) =>
			{
				var bmp = kinectManager != null ? kinectManager.GetColorBitmap() : null;
				if (bmp != null) CameraFeedImage.Source = bmp;
				CameraStatusText.Text = (kinectManager != null && kinectManager.IsInitialized) ? "Camera: Connected" : "Camera: Not Available (Test Mode)";
			};
			cameraUpdateTimer.Start();
		}

		private void Screen2_MarkerAlignment_Loaded(object sender, RoutedEventArgs e)
		{
			// Create and display projector window on secondary monitor if available
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
			}
			projectorWindow.Show();

			// Initialize marker positions
			ApplyMarkerPositions();
			ApplyMarkerScale();
		}

		private void ApplyMarkerPositions()
		{
			if (projectorWindow == null) return;
			projectorWindow.SetMarkerPosition(0, M1X.Value, M1Y.Value);
			projectorWindow.SetMarkerPosition(1, M2X.Value, M2Y.Value);
			projectorWindow.SetMarkerPosition(2, M3X.Value, M3Y.Value);
			projectorWindow.SetMarkerPosition(3, M4X.Value, M4Y.Value);
		}

		private void ApplyMarkerScale()
		{
			if (projectorWindow == null) return;
			projectorWindow.SetAllMarkersScale(MarkerSizeSlider.Value);
		}

		private void MarkerPositionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
		{
			ApplyMarkerPositions();
		}

		private void MarkerSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
		{
			ApplyMarkerScale();
		}

		private void HideCameraButton_Click(object sender, RoutedEventArgs e)
		{
			CameraFeedImage.Visibility = Visibility.Collapsed;
		}

		private void FindMarkersButton_Click(object sender, RoutedEventArgs e)
		{
			MarkersOverlay.Children.Clear();
			try
			{
				if (CameraFeedImage.Source == null)
				{
					StatusText.Text = "Status: No camera frame";
					StatusText.Foreground = Brushes.Orange;
					return;
				}
				// Encode current frame to PNG bytes
				var encoder = new PngBitmapEncoder();
				encoder.Frames.Add(BitmapFrame.Create((BitmapSource)CameraFeedImage.Source));
				byte[] pngBytes;
				using (var ms = new MemoryStream())
				{
					encoder.Save(ms);
					pngBytes = ms.ToArray();
				}

				int threshold = (int)Math.Max(0, Math.Min(255, CameraExposureSlider.Value * 255.0));
				int detected = TryDetectQrWithOpenCv(pngBytes, threshold);
				if (detected >= 4)
				{
					CalibrateButton.IsEnabled = true;
					StatusText.Text = string.Format("Status: Detected {0} markers", detected);
					StatusText.Foreground = Brushes.Green;
					markersDetected = true;
				}
				else
				{
					CalibrateButton.IsEnabled = false;
					StatusText.Text = string.Format("Status: Detected {0} markers", detected);
					StatusText.Foreground = Brushes.Orange;
					markersDetected = false;
				}
			}
			catch (Exception ex)
			{
				StatusText.Text = "Status: Detection error";
				StatusText.Foreground = Brushes.OrangeRed;
				System.Diagnostics.Debug.WriteLine("FindMarkers error: " + ex.Message);
			}
		}

		private int TryDetectQrWithOpenCv(byte[] pngBytes, int threshold)
		{
			// Use reflection to avoid hard compile dependency if OpenCvSharp assemblies are not present
			var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "OpenCvSharp")
					  ?? TryLoadAssembly("OpenCvSharp");
			var asmExt = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "OpenCvSharp.Extensions")
					  ?? TryLoadAssembly("OpenCvSharp.Extensions");
			if (asm == null)
			{
				StatusText.Text = "Status: OpenCvSharp not available";
				StatusText.Foreground = Brushes.Orange;
				return 0;
			}

			// Types
			Type cv2Type = asm.GetType("OpenCvSharp.Cv2");
			Type matType = asm.GetType("OpenCvSharp.Mat");
			Type qrdType = asm.GetType("OpenCvSharp.QRCodeDetector");
			if (cv2Type == null || matType == null || qrdType == null)
			{
				return 0;
			}

			// Mat src = Cv2.ImDecode(pngBytes, 1)
			var imDecode = cv2Type.GetMethod("ImDecode", new Type[] { typeof(byte[]), typeof(int) });
			object srcMat = imDecode.Invoke(null, new object[] { pngBytes, 1 });
			// gray = new Mat(); bw = new Mat();
			object grayMat = Activator.CreateInstance(matType);
			object bwMat = Activator.CreateInstance(matType);
			// Cv2.CvtColor(src, gray, 6) // BGR2GRAY
			var cvtColor = cv2Type.GetMethod("CvtColor", new Type[] { matType, matType, typeof(int) });
			cvtColor.Invoke(null, new object[] { srcMat, grayMat, 6 });
			// Cv2.Threshold(gray, bw, threshold, 255, 0) // Binary
			var thresholdMi = cv2Type.GetMethod("Threshold", new Type[] { matType, matType, typeof(double), typeof(double), typeof(int) });
			thresholdMi.Invoke(null, new object[] { grayMat, bwMat, (double)threshold, 255.0, 0 });

			// detector = new QRCodeDetector();
			object detector = Activator.CreateInstance(qrdType);
			int found = 0;
			// Try DetectMulti first: bool DetectMulti(Mat img, out Point2f[][] points)
			var detectMulti = qrdType.GetMethod("DetectMulti", new Type[] { matType, null });
			if (detectMulti != null)
			{
				// Build out param: Point2f[][]
				Type p2fType = asm.GetType("OpenCvSharp.Point2f");
				Type p2fArrayType = p2fType.MakeArrayType();
				Type p2fJaggedType = p2fArrayType.MakeArrayType();
				var parms = detectMulti.GetParameters();
				object[] args = new object[] { bwMat, null };
				bool ok = (bool)detectMulti.Invoke(detector, args);
				if (ok && args[1] != null)
				{
					var jagged = (Array)args[1];
					for (int i = 0; i < jagged.Length; i++)
					{
						var quad = (Array)jagged.GetValue(i);
						if (quad != null && quad.Length >= 4)
						{
							DrawMarkerCenterFromPoint2fArray(quad, asm);
							found++;
						}
					}
					return found;
				}
			}
			// Fallback: Detect single
			var detect = qrdType.GetMethod("Detect", new Type[] { matType, null });
			if (detect != null)
			{
				Type p2fType = asm.GetType("OpenCvSharp.Point2f");
				Type p2fArrayType = p2fType.MakeArrayType();
				object[] args = new object[] { bwMat, null };
				bool ok = (bool)detect.Invoke(detector, args);
				if (ok && args[1] != null)
				{
					var quad = (Array)args[1];
					if (quad != null && quad.Length >= 4)
					{
						DrawMarkerCenterFromPoint2fArray(quad, asm);
						found = 1;
					}
				}
			}
			return found;
		}

		private Assembly TryLoadAssembly(string name)
		{
			try { return Assembly.Load(name); } catch { return null; }
		}

		private void DrawMarkerCenterFromPoint2fArray(Array pointArray, Assembly opencvAsm)
		{
			// Compute centroid in color space and map to overlay coordinates
			double cx = 0, cy = 0;
			int n = Math.Min(4, pointArray.Length);
			for (int i = 0; i < n; i++)
			{
				object p = pointArray.GetValue(i);
				float px = (float)p.GetType().GetField("X").GetValue(p);
				float py = (float)p.GetType().GetField("Y").GetValue(p);
				cx += px; cy += py;
			}
			cx /= n; cy /= n;
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

		private void CalibrateButton_Click(object sender, RoutedEventArgs e)
		{
			if (!markersDetected)
			{
				StatusText.Text = "Status: No markers detected";
				StatusText.Foreground = Brushes.Orange;
				return;
			}
			StatusText.Text = "Status: CALIBRATED";
			StatusText.Foreground = Brushes.Green;
			NextButton.IsEnabled = true;
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
	}
}
