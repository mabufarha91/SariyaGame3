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
		public List<SerializableCameraSpacePoint> CornerPointsCamera { get; set; } // mapped 3D points corresponding to normals
		[DataMember]
		public double PlaneThresholdMeters { get; set; }
		[DataMember]
		public double[] SensorToWorldTransform { get; set; } // 4x4 row-major as 1D array
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
		public double[] PerspectiveTransform3x3 { get; set; } // 3x3 row-major

		// Screen 2 Touch Area Detection
		[DataMember]
		public TouchAreaDefinition TouchArea { get; set; } // Touch detection area calculated from ArUco markers
		[DataMember]
		public List<Point> ArUcoMarkerCenters { get; set; } // Center points of detected ArUco markers (IDs 0,1,2,3)
		[DataMember]
		public List<int> ArUcoMarkerIds { get; set; } // IDs of detected ArUco markers
		[DataMember]
		public DateTime TouchAreaCalculatedUtc { get; set; } // When touch area was calculated
		
		// Enhanced Kinect Distance and Touch Detection Parameters
		[DataMember]
		public double KinectToSurfaceDistanceMeters { get; set; } // Distance from Kinect sensor to touch surface
		[DataMember]
		public double TouchDetectionThresholdMeters { get; set; } // How close to surface to detect touch
		[DataMember]
		public double TouchAreaWidthMeters { get; set; } // Physical width of touch area in meters
		[DataMember]
		public double TouchAreaHeightMeters { get; set; } // Physical height of touch area in meters
		[DataMember]
		public List<SerializableCameraSpacePoint> TouchAreaCorners3D { get; set; } // 3D coordinates of touch area corners
		[DataMember]
		public double AverageMarkerSizePixels { get; set; } // Average size of detected markers in pixels
		[DataMember]
		public double CalibrationAccuracyScore { get; set; } // Quality score of calibration (0-1)
		
		// Screen 3 Touch Detection Settings
		[DataMember]
		public Dictionary<string, object> TouchDetectionSettings { get; set; } // Additional touch detection parameters
		
		// Distance Gradient for Angle Problem Solution
		[DataMember]
		public Dictionary<string, double> DistanceGradientMap { get; set; } // Sparse distance map: "x,y" -> distance
		[DataMember]
		public int DistanceGradientSamplingRate { get; set; } // How often we sample (e.g., every 5th pixel)
		[DataMember]
		public double DistanceGradientMinDistance { get; set; } // Minimum distance in the touch area
		[DataMember]
		public double DistanceGradientMaxDistance { get; set; } // Maximum distance in the touch area
		[DataMember]
		public double DistanceGradientAverageDistance { get; set; } // Average distance across touch area

		public CalibrationConfig()
		{
			CornerPointsNormalized = new List<Point>();
			CornerPointsCamera = new List<SerializableCameraSpacePoint>();
			Plane = new PlaneDefinition();
			PlaneThresholdMeters = 0.03; // default 3 cm
			SensorToWorldTransform = CreateIdentity4x4As1D();
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
			TouchAreaCalculatedUtc = DateTime.UtcNow;
			ProjectorMarkerCenters = new List<Point>();
			CameraMarkerCornersTL = new List<Point>();
			PerspectiveTransform3x3 = new double[9]
			{
				1,0,0,
				0,1,0,
				0,0,1
			};
			
			// Initialize enhanced Kinect parameters
			KinectToSurfaceDistanceMeters = 0.0;
			TouchDetectionThresholdMeters = 0.05; // Default 5cm threshold
			TouchAreaWidthMeters = 0.0;
			TouchAreaHeightMeters = 0.0;
			TouchAreaCorners3D = new List<SerializableCameraSpacePoint>();
			AverageMarkerSizePixels = 0.0;
			CalibrationAccuracyScore = 0.0;
			TouchDetectionSettings = new Dictionary<string, object>();
			
			// Initialize distance gradient properties
			DistanceGradientMap = new Dictionary<string, double>();
			DistanceGradientSamplingRate = 5; // Sample every 5th pixel
			DistanceGradientMinDistance = 0.0;
			DistanceGradientMaxDistance = 0.0;
			DistanceGradientAverageDistance = 0.0;
		}

		private static double[] CreateIdentity4x4As1D()
		{
			return new double[16]
			{
				1,0,0,0,
				0,1,0,0,
				0,0,1,0,
				0,0,0,1
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
	
	[DataContract]
	public class SerializableCameraSpacePoint
	{
		[DataMember]
		public float X { get; set; }
		[DataMember]
		public float Y { get; set; }
		[DataMember]
		public float Z { get; set; }
		
		public SerializableCameraSpacePoint() { }
		
		public SerializableCameraSpacePoint(float x, float y, float z)
		{
			X = x;
			Y = y;
			Z = z;
		}
		
		public SerializableCameraSpacePoint(CameraSpacePoint point)
		{
			X = point.X;
			Y = point.Y;
			Z = point.Z;
		}
		
		public CameraSpacePoint ToCameraSpacePoint()
		{
			return new CameraSpacePoint { X = X, Y = Y, Z = Z };
		}
	}
}
