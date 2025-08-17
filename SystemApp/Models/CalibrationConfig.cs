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

		public CalibrationConfig()
		{
			CornerPointsNormalized = new List<Point>();
			CornerPointsCamera = new List<CameraSpacePoint>();
			Plane = new PlaneDefinition();
			PlaneThresholdMeters = 0.03; // default 3 cm
			SensorToWorldTransform = CreateIdentity4x4();
			SavedUtc = DateTime.UtcNow;
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
}
