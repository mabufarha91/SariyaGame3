using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Media;

namespace KinectCalibrationWPF.CalibrationWizard
{
	public partial class ProjectorWindow : Window
	{
		private Image[] markers;
		private ScaleTransform[] scales;

		public ProjectorWindow()
		{
			InitializeComponent();
			markers = new[] { Marker0, Marker1, Marker2, Marker3 };
			scales = new[] { (ScaleTransform)Scale0, (ScaleTransform)Scale1, (ScaleTransform)Scale2, (ScaleTransform)Scale3 };
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
			}
		}

		public void SetMarkerSource(BitmapSource source)
		{
			foreach (var img in markers)
			{
				img.Source = source;
			}
		}
	}
}
