using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System;

namespace KinectCalibrationWPF.CalibrationWizard
{
	public partial class ProjectorWindow : Window
	{
		private Image[] markers;
		private ScaleTransform[] scales;
		private double[] baseWidths;
		private double[] baseHeights;
		private int selectedIndex = -1;

		public ProjectorWindow()
		{
			InitializeComponent();
			markers = new[] { Marker0, Marker1, Marker2, Marker3 };
			scales = new[] { (ScaleTransform)Scale0, (ScaleTransform)Scale1, (ScaleTransform)Scale2, (ScaleTransform)Scale3 };
			baseWidths = new double[markers.Length];
			baseHeights = new double[markers.Length];
			for (int i = 0; i < markers.Length; i++)
			{
				baseWidths[i] = markers[i].Width;
				baseHeights[i] = markers[i].Height;
			}
			// Attempt to populate markers from embedded resources when the window is ready
			Loaded += ProjectorWindow_Loaded;
		}

		private void ProjectorWindow_Loaded(object sender, RoutedEventArgs e)
		{
			LoadEmbeddedMarkerImagesIfMissing();
		}

		public void SetMarkerPosition(int index, double x, double y)
		{
			if (index < 0 || index >= markers.Length) return;
			Canvas.SetLeft(markers[index], x);
			Canvas.SetTop(markers[index], y);
		}

		public void SetAllMarkersScale(double scale)
		{
			for (int i = 0; i < scales.Length; i++)
			{
				scales[i].ScaleX = scale;
				scales[i].ScaleY = scale;
				markers[i].Width = baseWidths[i] * scale;
				markers[i].Height = baseHeights[i] * scale;
			}
		}

		public void SetMarkerSource(BitmapSource source)
		{
			foreach (var img in markers)
			{
				img.Source = source;
			}
		}

		public void SetMarkerSource(int index, BitmapSource source)
		{
			if (index < 0 || index >= markers.Length) return;
			markers[index].Source = source;
		}

		private void LoadEmbeddedMarkerImagesIfMissing()
		{
			bool anyLoaded = false;
			try
			{
				for (int i = 0; i < markers.Length; i++)
				{
					if (markers[i].Source != null) continue;
					
					// Load existing 7x7 markers (detection will handle them with ultra-permissive parameters)
					var uris = new[]
					{
						$"pack://application:,,,/Assets/aruco_7x7_250_id{i}.png",
						$"pack://application:,,,/Assets/aruco_4x4_50_id{i}.png",
						$"pack://application:,,,/Assets/marker_{i}.png"
					};
					foreach (var uri in uris)
					{
						try
						{
							var img = new BitmapImage(new Uri(uri, UriKind.Absolute));
							markers[i].Source = img;
							anyLoaded = true;
							break;
						}
						catch { /* try next naming */ }
					}
				}
			}
			catch (Exception ex)
			{
				MessageBox.Show($"Error loading marker images: {ex.Message}\n\n" +
					"Ensure images exist in 'Assets' and their Build Action is set to 'Resource'.",
					"Resource Error", MessageBoxButton.OK, MessageBoxImage.Error);
			}
		}

		public Point GetMarkerCenter(int index)
		{
			if (index < 0 || index >= markers.Length) return new Point(double.NaN, double.NaN);
			double x = Canvas.GetLeft(markers[index]);
			double y = Canvas.GetTop(markers[index]);
			double w = markers[index].ActualWidth > 0 ? markers[index].ActualWidth : markers[index].Width;
			double h = markers[index].ActualHeight > 0 ? markers[index].ActualHeight : markers[index].Height;
			return new Point(x + w / 2.0, y + h / 2.0);
		}

		public void SaveProjectorDiagnostics(string diagDir)
		{
			try
			{
				System.IO.Directory.CreateDirectory(diagDir);
				var logPath = System.IO.Path.Combine(diagDir, "projector_diagnostics.txt");
				
				// Log projector window state
				LogToFile(logPath, "=== PROJECTOR WINDOW DIAGNOSTICS ===");
				LogToFile(logPath, $"Window Size: {this.ActualWidth}x{this.ActualHeight}");
				LogToFile(logPath, $"Canvas Size: {MarkerCanvas.ActualWidth}x{MarkerCanvas.ActualHeight}");
				LogToFile(logPath, $"Window State: {this.WindowState}");
				LogToFile(logPath, $"Visibility: {this.Visibility}");
				
				// Capture individual marker diagnostics
				for (int i = 0; i < markers.Length; i++)
				{
					LogToFile(logPath, $"\n--- MARKER {i} ---");
					
					var marker = markers[i];
					var x = Canvas.GetLeft(marker);
					var y = Canvas.GetTop(marker);
					var w = marker.ActualWidth > 0 ? marker.ActualWidth : marker.Width;
					var h = marker.ActualHeight > 0 ? marker.ActualHeight : marker.Height;
					
					LogToFile(logPath, $"Position: ({x:F1}, {y:F1})");
					LogToFile(logPath, $"Size: {w:F1}x{h:F1}");
					LogToFile(logPath, $"Scale: {scales[i].ScaleX:F2}x{scales[i].ScaleY:F2}");
					LogToFile(logPath, $"Visibility: {marker.Visibility}");
					LogToFile(logPath, $"Source: {(marker.Source != null ? "Loaded" : "NULL")}");
					
					if (marker.Source != null)
					{
						var bitmapSource = marker.Source as System.Windows.Media.Imaging.BitmapSource;
						if (bitmapSource != null)
						{
							LogToFile(logPath, $"Source Size: {bitmapSource.PixelWidth}x{bitmapSource.PixelHeight}");
							LogToFile(logPath, $"Source DPI: {bitmapSource.DpiX:F1}x{bitmapSource.DpiY:F1}");
							
							// Save individual marker image
							try
							{
								var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
								encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmapSource));
								var markerPath = System.IO.Path.Combine(diagDir, $"projector_marker_{i}.png");
								using (var stream = new System.IO.FileStream(markerPath, System.IO.FileMode.Create))
								{
									encoder.Save(stream);
								}
								LogToFile(logPath, $"Individual marker saved: projector_marker_{i}.png");
							}
							catch (Exception ex)
							{
								LogToFile(logPath, $"Failed to save marker {i}: {ex.Message}");
							}
						}
						else
						{
							LogToFile(logPath, "Source is not a BitmapSource - cannot get pixel dimensions");
						}
					}
				}
				
				// Capture full projector canvas screenshot
				try
				{
					var canvasScreenshot = CaptureCanvasScreenshot();
					if (canvasScreenshot != null)
					{
						var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
						encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(canvasScreenshot));
						var canvasPath = System.IO.Path.Combine(diagDir, "projector_canvas_full.png");
						using (var stream = new System.IO.FileStream(canvasPath, System.IO.FileMode.Create))
						{
							encoder.Save(stream);
						}
						LogToFile(logPath, "Full canvas screenshot saved: projector_canvas_full.png");
					}
				}
				catch (Exception ex)
				{
					LogToFile(logPath, $"Failed to capture canvas screenshot: {ex.Message}");
				}
				
				LogToFile(logPath, "\n=== PROJECTOR DIAGNOSTICS COMPLETE ===");
			}
			catch (Exception ex)
			{
				System.Windows.MessageBox.Show($"Projector diagnostics error: {ex.Message}");
			}
		}
		
		private System.Windows.Media.Imaging.BitmapSource CaptureCanvasScreenshot()
		{
			try
			{
				var canvas = MarkerCanvas;
				var width = (int)canvas.ActualWidth;
				var height = (int)canvas.ActualHeight;
				
				if (width <= 0 || height <= 0) return null;
				
				var renderTarget = new System.Windows.Media.Imaging.RenderTargetBitmap(
					width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
				renderTarget.Render(canvas);
				
				return renderTarget;
			}
			catch
			{
				return null;
			}
		}
		
		private void LogToFile(string path, string message)
		{
			try 
			{ 
				System.IO.File.AppendAllText(path, message + System.Environment.NewLine); 
			} 
			catch { }
		}

		public void HighlightMarker(int index)
		{
			for (int i = 0; i < markers.Length; i++)
			{
				markers[i].Opacity = (i == index) ? 0.7 : 1.0;
			}
			selectedIndex = index;
		}

		public void NudgeSelected(double dx, double dy)
		{
			if (selectedIndex < 0 || selectedIndex >= markers.Length) return;
			var img = markers[selectedIndex];
			double x = Canvas.GetLeft(img);
			double y = Canvas.GetTop(img);
			Canvas.SetLeft(img, x + dx);
			Canvas.SetTop(img, y + dy);
		}
	}
}
