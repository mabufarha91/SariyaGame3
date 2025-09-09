using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using KinectCalibrationWPF.Models;

namespace KinectCalibrationWPF.Services
{
	public static class CalibrationStorage
	{
		private const string DefaultFileName = "calibration.json";

		public static void Save(CalibrationConfig config, string filePath = null)
		{
			var path = string.IsNullOrWhiteSpace(filePath) ? GetDefaultPath() : filePath;
			var serializer = new DataContractJsonSerializer(typeof(CalibrationConfig));
			using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
			{
				serializer.WriteObject(fs, config);
			}
		}

		public static CalibrationConfig Load(string filePath = null)
		{
			var path = string.IsNullOrWhiteSpace(filePath) ? GetDefaultPath() : filePath;
			if (!File.Exists(path)) return null;
			var serializer = new DataContractJsonSerializer(typeof(CalibrationConfig));
			using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
			{
				return (CalibrationConfig)serializer.ReadObject(fs);
			}
		}

		private static string GetDefaultPath()
		{
			var baseDir = System.AppDomain.CurrentDomain.BaseDirectory;
			return System.IO.Path.Combine(baseDir, DefaultFileName);
		}
		
		public static void DeleteCorruptedFile()
		{
			try
			{
				var path = GetDefaultPath();
				if (System.IO.File.Exists(path))
				{
					System.IO.File.Delete(path);
					System.Diagnostics.Debug.WriteLine("Deleted corrupted calibration file");
				}
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"Could not delete corrupted calibration file: {ex.Message}");
			}
		}
	}
}
