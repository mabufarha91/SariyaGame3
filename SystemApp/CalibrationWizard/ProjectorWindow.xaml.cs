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
					// Try preferred aruco_7x7_250 naming
					var uris = new[]
					{
						$"pack://application:,,,/Assets/aruco_7x7_250_id{i}.png",
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
				MessageBox.Show($"Error loading embedded marker images: {ex.Message}\n\n" +
					"Ensure images exist in 'Assets' and their Build Action is set to 'Resource'.",
					"Resource Error", MessageBoxButton.OK, MessageBoxImage.Error);
			}
			// No popup if nothing loaded and external code will supply sources via SetMarkerSource
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
