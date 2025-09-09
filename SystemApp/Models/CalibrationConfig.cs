using System;
using System.Collections.Generic;
using System.Windows;
using Microsoft.Kinect;
using System.Runtime.Serialization;

namespace KinectCalibrationWPF.Models
{
	[DataContract]
	public class CalibrationConfig
	{
		[DataMember]
		public PlaneDefinition Plane { get; set; }
		[DataMember]
		public List<Point> CornerPointsNormalized { get; set; } // order: TL, TR, BR, BL in normalized [0..1]
		[DataMember]
		public List<CameraSpacePoint> CornerPointsCamera { get; set; } // mapped 3D points corresponding to normals
		[DataMember]
		public double PlaneThresholdMeters { get; set; }
		[DataMember]
		public double[,] SensorToWorldTransform { get; set; } // 4x4 row-major
		[DataMember]
		public DateTime SavedUtc { get; set; }

		// Screen 2 additions: projector alignment and detection settings
		[DataMember]
		public List<Point> ProjectorMarkerPositions { get; set; } // projector canvas pixels for 4 markers
		[DataMember]
		public double ProjectorMarkerScale { get; set; }
		[DataMember]
		public double DetectionExposure { get; set; } // 0..1
		[DataMember]
		public List<Point> DetectedMarkerCentersColor { get; set; } // detected centers in color-space pixels
		[DataMember]
		public int HsvHueMin { get; set; }
		[DataMember]
		public int HsvSatMin { get; set; }
		[DataMember]
		public int HsvValMin { get; set; }
		[DataMember]
		public List<Point> ProjectorMarkerCenters { get; set; } // projector centers for 4 markers
		[DataMember]
		public List<Point> CameraMarkerCornersTL { get; set; } // top-left corners sorted by ID
		[DataMember]
		public double[,] PerspectiveTransform3x3 { get; set; } // 3x3 row-major

		// Screen 2 Touch Area Detection
		[DataMember]
		public TouchAreaDefinition TouchArea { get; set; } // Touch detection area calculated from ArUco markers
		[DataMember]
		public List<Point> ArUcoMarkerCenters { get; set; } // Center points of detected ArUco markers (IDs 0,1,2,3)
		[DataMember]
		public List<int> ArUcoMarkerIds { get; set; } // IDs of detected ArUco markers
		[DataMember]
		public DateTime TouchAreaCalculatedUtc { get; set; } // When touch area was calculated

		public CalibrationConfig()
		{
			CornerPointsNormalized = new List<Point>();
			CornerPointsCamera = new List<CameraSpacePoint>();
			Plane = new PlaneDefinition();
			PlaneThresholdMeters = 0.03; // default 3 cm
			SensorToWorldTransform = CreateIdentity4x4();
			SavedUtc = DateTime.UtcNow;
			ProjectorMarkerPositions = new List<Point>();
			DetectedMarkerCentersColor = new List<Point>();
			ProjectorMarkerScale = 1.0;
			DetectionExposure = 0.5;
			HsvHueMin = 0;
			HsvSatMin = 0;
			HsvValMin = 200;
			TouchArea = new TouchAreaDefinition();
			ArUcoMarkerCenters = new List<Point>();
			ArUcoMarkerIds = new List<int>();
			TouchAreaCalculatedUtc = DateTime.MinValue;
			ProjectorMarkerCenters = new List<Point>();
			CameraMarkerCornersTL = new List<Point>();
			PerspectiveTransform3x3 = new double[3,3]
			{
				{1,0,0},
				{0,1,0},
				{0,0,1}
			};
		}

		private static double[,] CreateIdentity4x4()
		{
			return new double[4,4]
			{
				{1,0,0,0},
				{0,1,0,0},
				{0,0,1,0},
				{0,0,0,1}
			};
		}
	}

	[DataContract]
	public class PlaneDefinition
	{
		[DataMember]
		public double Nx { get; set; }
		[DataMember]
		public double Ny { get; set; }
		[DataMember]
		public double Nz { get; set; }
		[DataMember]
		public double D { get; set; } // plane equation: n.x * X + n.y * Y + n.z * Z + D = 0
	}

	[DataContract]
	public class TouchAreaDefinition
	{
		[DataMember]
		public double X { get; set; } // Left edge in camera coordinates
		[DataMember]
		public double Y { get; set; } // Top edge in camera coordinates
		[DataMember]
		public double Width { get; set; } // Width in camera coordinates
		[DataMember]
		public double Height { get; set; } // Height in camera coordinates
		[DataMember]
		public double Left { get; set; } // Left edge
		[DataMember]
		public double Top { get; set; } // Top edge
		[DataMember]
		public double Right { get; set; } // Right edge
		[DataMember]
		public double Bottom { get; set; } // Bottom edge
		[DataMember]
		public int CameraWidth { get; set; } // Camera resolution width
		[DataMember]
		public int CameraHeight { get; set; } // Camera resolution height

		public TouchAreaDefinition()
		{
			X = Y = Width = Height = Left = Top = Right = Bottom = 0;
			CameraWidth = 1920;
			CameraHeight = 1080;
		}

		public TouchAreaDefinition(double x, double y, double width, double height, int cameraWidth = 1920, int cameraHeight = 1080)
		{
			X = x;
			Y = y;
			Width = width;
			Height = height;
			Left = x;
			Top = y;
			Right = x + width;
			Bottom = y + height;
			CameraWidth = cameraWidth;
			CameraHeight = cameraHeight;
		}
	}
}
