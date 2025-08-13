using System;
using System.Windows.Media.Imaging;
using Microsoft.Kinect;

namespace KinectCalibrationWPF.KinectManager
{
    public class KinectManager : IDisposable
    {
        private KinectSensor kinectSensor;
        private ColorFrameReader colorFrameReader;
        private DepthFrameReader depthFrameReader;
        private BodyFrameReader bodyFrameReader;
        private CoordinateMapper coordinateMapper;
        private readonly object depthDataLock = new object();
        private ushort[] latestDepthData;
        private int depthWidth;
        private int depthHeight;
        private int colorWidth;
        private int colorHeight;
        
        public bool IsInitialized { get; private set; }
        public bool IsTestMode { get; private set; }
        public CoordinateMapper CoordinateMapper { get { return coordinateMapper; } }
        public int ColorWidth { get { return colorWidth; } }
        public int ColorHeight { get { return colorHeight; } }
        public int DepthWidth { get { return depthWidth; } }
        public int DepthHeight { get { return depthHeight; } }
        
        public event EventHandler<ColorFrameArrivedEventArgs> ColorFrameArrived;
        public event EventHandler<DepthFrameArrivedEventArgs> DepthFrameArrived;
        public event EventHandler<BodyFrameArrivedEventArgs> BodyFrameArrived;
        
        public KinectManager()
        {
            InitializeKinect();
        }
        
        private void InitializeKinect()
        {
            try
            {
                kinectSensor = KinectSensor.GetDefault();
                
                if (kinectSensor == null)
                {
                    IsTestMode = true;
                    IsInitialized = false;
                    return;
                }
                
                // Initialize readers
                colorFrameReader = kinectSensor.ColorFrameSource.OpenReader();
                depthFrameReader = kinectSensor.DepthFrameSource.OpenReader();
                bodyFrameReader = kinectSensor.BodyFrameSource.OpenReader();
                
                // Get coordinate mapper
                coordinateMapper = kinectSensor.CoordinateMapper;
                
                // Cache frame dimensions
                var colorDesc = kinectSensor.ColorFrameSource.CreateFrameDescription(ColorImageFormat.Bgra);
                colorWidth = colorDesc.Width;
                colorHeight = colorDesc.Height;
                var depthDesc = kinectSensor.DepthFrameSource.FrameDescription;
                depthWidth = depthDesc.Width;
                depthHeight = depthDesc.Height;
                
                // Subscribe to events
                colorFrameReader.FrameArrived += ColorFrameReader_FrameArrived;
                depthFrameReader.FrameArrived += DepthFrameReader_FrameArrived;
                bodyFrameReader.FrameArrived += BodyFrameReader_FrameArrived;
                
                // Start the sensor
                kinectSensor.Open();
                
                IsInitialized = true;
                IsTestMode = false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(string.Format("Kinect initialization failed: {0}", ex.Message));
                IsTestMode = true;
                IsInitialized = false;
            }
        }
        
        private void ColorFrameReader_FrameArrived(object sender, ColorFrameArrivedEventArgs e)
        {
            if (ColorFrameArrived != null) { ColorFrameArrived(this, e); }
        }
        
        private void DepthFrameReader_FrameArrived(object sender, DepthFrameArrivedEventArgs e)
        {
            if (DepthFrameArrived != null) { DepthFrameArrived(this, e); }
            try
            {
                using (var depthFrame = e.FrameReference.AcquireFrame())
                {
                    if (depthFrame == null) return;
                    var frameDesc = depthFrame.DepthFrameSource.FrameDescription;
                    if (latestDepthData == null || latestDepthData.Length != frameDesc.LengthInPixels)
                    {
                        latestDepthData = new ushort[frameDesc.LengthInPixels];
                    }
                    depthFrame.CopyFrameDataToArray(latestDepthData);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(string.Format("Depth frame copy failed: {0}", ex.Message));
            }
        }
        
        private void BodyFrameReader_FrameArrived(object sender, BodyFrameArrivedEventArgs e)
        {
            if (BodyFrameArrived != null) { BodyFrameArrived(this, e); }
        }
        
        public BitmapSource GetColorBitmap()
        {
            if (!IsInitialized || kinectSensor == null)
                return CreateTestColorBitmap();
                
            using (var colorFrame = colorFrameReader.AcquireLatestFrame())
            {
                if (colorFrame == null)
                    return CreateTestColorBitmap();
                    
                var colorFrameDescription = colorFrame.ColorFrameSource.CreateFrameDescription(ColorImageFormat.Bgra);
                var colorFrameData = new byte[colorFrameDescription.LengthInPixels * colorFrameDescription.BytesPerPixel];
                
                colorFrame.CopyConvertedFrameDataToArray(colorFrameData, ColorImageFormat.Bgra);
                
                var bitmap = new WriteableBitmap(
                    colorFrameDescription.Width,
                    colorFrameDescription.Height,
                    96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
                    
                bitmap.WritePixels(
                    new System.Windows.Int32Rect(0, 0, colorFrameDescription.Width, colorFrameDescription.Height),
                    colorFrameData,
                    (int)(colorFrameDescription.Width * colorFrameDescription.BytesPerPixel),
                    0);
                    
                return bitmap;
            }
        }
        
        public BitmapSource GetDepthBitmap()
        {
            if (!IsInitialized || kinectSensor == null)
                return CreateTestDepthBitmap();
                
            using (var depthFrame = depthFrameReader.AcquireLatestFrame())
            {
                if (depthFrame == null)
                    return CreateTestDepthBitmap();
                    
                var depthFrameDescription = depthFrame.DepthFrameSource.FrameDescription;
                var depthFrameData = new ushort[depthFrameDescription.LengthInPixels];
                
                depthFrame.CopyFrameDataToArray(depthFrameData);
                // Also cache latest depth for mapping
                lock (depthDataLock)
                {
                    if (latestDepthData == null || latestDepthData.Length != depthFrameData.Length)
                    {
                        latestDepthData = new ushort[depthFrameData.Length];
                    }
                    Array.Copy(depthFrameData, latestDepthData, depthFrameData.Length);
                    depthWidth = depthFrameDescription.Width;
                    depthHeight = depthFrameDescription.Height;
                }
                
                var colorFrameData = new byte[depthFrameDescription.LengthInPixels * 4];
                
                for (int i = 0; i < depthFrameData.Length; i++)
                {
                    var depth = depthFrameData[i];
                    var intensity = (byte)(depth >= 450 && depth <= 1090 ? depth : 0);
                    
                    colorFrameData[i * 4] = intensity;     // Blue
                    colorFrameData[i * 4 + 1] = intensity; // Green
                    colorFrameData[i * 4 + 2] = intensity; // Red
                    colorFrameData[i * 4 + 3] = 255;       // Alpha
                }
                
                var bitmap = new WriteableBitmap(
                    depthFrameDescription.Width,
                    depthFrameDescription.Height,
                    96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
                    
                bitmap.WritePixels(
                    new System.Windows.Int32Rect(0, 0, depthFrameDescription.Width, depthFrameDescription.Height),
                    colorFrameData,
                    (int)((int)(depthFrameDescription.Width * 4)),
                    0);
                    
                return bitmap;
            }
        }

        public bool TryMapColorPixelToCameraSpace(int colorX, int colorY, out CameraSpacePoint cameraPoint)
        {
            cameraPoint = new CameraSpacePoint { X = float.NaN, Y = float.NaN, Z = float.NaN };
            if (!IsInitialized || coordinateMapper == null)
                return false;

            ushort[] depthDataSnapshot = null;
            int localDepthWidth, localDepthHeight, localColorWidth, localColorHeight;
            lock (depthDataLock)
            {
                if (latestDepthData == null)
                {
                    return false;
                }
                depthDataSnapshot = new ushort[latestDepthData.Length];
                Array.Copy(latestDepthData, depthDataSnapshot, latestDepthData.Length);
                localDepthWidth = depthWidth;
                localDepthHeight = depthHeight;
                localColorWidth = colorWidth > 0 ? colorWidth : 1920;
                localColorHeight = colorHeight > 0 ? colorHeight : 1080;
            }

            if (colorX < 0 || colorY < 0 || colorX >= localColorWidth || colorY >= localColorHeight)
                return false;

            try
            {
                var depthSpacePoints = new DepthSpacePoint[localColorWidth * localColorHeight];
                coordinateMapper.MapColorFrameToDepthSpace(depthDataSnapshot, depthSpacePoints);

                int idx = colorY * localColorWidth + colorX;
                var dsp = depthSpacePoints[idx];
                if (float.IsInfinity(dsp.X) || float.IsInfinity(dsp.Y) || float.IsNaN(dsp.X) || float.IsNaN(dsp.Y))
                    return false;

                int dx = (int)Math.Round(dsp.X);
                int dy = (int)Math.Round(dsp.Y);
                if (dx < 0 || dy < 0 || dx >= localDepthWidth || dy >= localDepthHeight)
                    return false;

                ushort depth = depthDataSnapshot[dy * localDepthWidth + dx];
                if (depth == 0)
                    return false;

                cameraPoint = coordinateMapper.MapDepthPointToCameraSpace(dsp, depth);
                if (float.IsInfinity(cameraPoint.X) || float.IsInfinity(cameraPoint.Y) || float.IsInfinity(cameraPoint.Z))
                    return false;
                if (float.IsNaN(cameraPoint.X) || float.IsNaN(cameraPoint.Y) || float.IsNaN(cameraPoint.Z))
                    return false;
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(string.Format("Map color pixel to camera space failed: {0}", ex.Message));
                return false;
            }
        }
        
        private BitmapSource CreateTestColorBitmap()
        {
            var width = 640;
            var height = 480;
            var pixels = new byte[width * height * 4];
            
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    var index = (y * width + x) * 4;
                    pixels[index] = (byte)((float)x / width * 255);     // Blue
                    pixels[index + 1] = (byte)((float)y / height * 255); // Green
                    pixels[index + 2] = 128;                             // Red
                    pixels[index + 3] = 255;                             // Alpha
                }
            }
            
            var bitmap = new WriteableBitmap(width, height, 96, 96, 
                                           System.Windows.Media.PixelFormats.Bgra32, null);
            bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, width, height), 
                              pixels, width * 4, 0);
            return bitmap;
        }
        
        private BitmapSource CreateTestDepthBitmap()
        {
            var width = 512;
            var height = 424;
            var pixels = new byte[width * height * 4];
            
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    var index = (y * width + x) * 4;
                    var intensity = (byte)((float)x / width * 255);
                    pixels[index] = intensity;     // Blue
                    pixels[index + 1] = intensity; // Green
                    pixels[index + 2] = intensity; // Red
                    pixels[index + 3] = 255;       // Alpha
                }
            }
            
            var bitmap = new WriteableBitmap(width, height, 96, 96, 
                                           System.Windows.Media.PixelFormats.Bgra32, null);
            bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, width, height), 
                              pixels, width * 4, 0);
            return bitmap;
        }
        
        public void Dispose()
        {
            if (colorFrameReader != null)
            {
                colorFrameReader.Dispose();
                colorFrameReader = null;
            }
            
            if (depthFrameReader != null)
            {
                depthFrameReader.Dispose();
                depthFrameReader = null;
            }
            
            if (bodyFrameReader != null)
            {
                bodyFrameReader.Dispose();
                bodyFrameReader = null;
            }
            
            if (kinectSensor != null && kinectSensor.IsOpen)
            {
                kinectSensor.Close();
                kinectSensor = null;
            }
        }
    }
}
