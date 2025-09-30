using System;
using System.Threading;
using System.Windows.Media.Imaging;
using Microsoft.Kinect;

namespace KinectCalibrationWPF.KinectManager
{
    public class KinectManager : IDisposable
    {
        private KinectSensor kinectSensor;
        private ColorFrameReader colorFrameReader;
        private DepthFrameReader depthFrameReader;
        private InfraredFrameReader infraredFrameReader;
        private BodyFrameReader bodyFrameReader;
        private CoordinateMapper coordinateMapper;
        private readonly object depthDataLock = new object();
        private ushort[] latestDepthData;
        private readonly object infraredDataLock = new object();
        private ushort[] latestInfraredData;
        private int depthWidth;
        private int depthHeight;
        private int infraredWidth;
        private int infraredHeight;
        private int colorWidth;
        private int colorHeight;
        private WriteableBitmap colorBitmap;
        private WriteableBitmap infraredBitmap;
        private byte[] latestColorData;
        private readonly object colorDataLock = new object();
        private DateTime lastColorFrameTimeUtc;
        private DateTime lastDepthFrameTimeUtc;
        private DateTime lastInfraredFrameTimeUtc;
        private ColorSpacePoint[] depthToColorPoints;
        private CameraSpacePoint[] latestCameraSpacePoints;
        private Body[] latestBodies;
        private readonly object bodyDataLock = new object();
        private double infraredExposureGain = 1.0;
        
        public bool IsInitialized { get; private set; }
        public bool IsTestMode { get; private set; }
        public CoordinateMapper CoordinateMapper { get { return coordinateMapper; } }
        public int ColorWidth { get { return colorWidth; } }
        public int ColorHeight { get { return colorHeight; } }
        public int DepthWidth { get { return depthWidth; } }
        public int DepthHeight { get { return depthHeight; } }
        public int InfraredWidth { get { return infraredWidth; } }
        public int InfraredHeight { get { return infraredHeight; } }
        
        public event EventHandler<ColorFrameArrivedEventArgs> ColorFrameArrived;
        public event EventHandler<DepthFrameArrivedEventArgs> DepthFrameArrived;
        public event EventHandler<InfraredFrameArrivedEventArgs> InfraredFrameArrived;
        public event EventHandler<BodyFrameArrivedEventArgs> BodyFrameArrived;
        
        // Debug counters to verify frame events
        public int colorFramesReceived = 0;
        public int depthFramesReceived = 0;
        public int infraredFramesReceived = 0;
        
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
                infraredFrameReader = kinectSensor.InfraredFrameSource.OpenReader();
                bodyFrameReader = kinectSensor.BodyFrameSource.OpenReader();
                
                // Get coordinate mapper
                coordinateMapper = kinectSensor.CoordinateMapper;
                
                // Cache frame dimensions
                var colorDesc = kinectSensor.ColorFrameSource.CreateFrameDescription(ColorImageFormat.Bgra);
                colorWidth = colorDesc.Width;
                colorHeight = colorDesc.Height;
                latestColorData = new byte[colorDesc.LengthInPixels * colorDesc.BytesPerPixel];
                colorBitmap = null; // create on UI thread lazily in GetColorBitmap
                var depthDesc = kinectSensor.DepthFrameSource.FrameDescription;
                depthWidth = depthDesc.Width;
                depthHeight = depthDesc.Height;
                var infraredDesc = kinectSensor.InfraredFrameSource.FrameDescription;
                infraredWidth = infraredDesc.Width;
                infraredHeight = infraredDesc.Height;
                latestInfraredData = new ushort[infraredDesc.LengthInPixels];
                depthToColorPoints = new ColorSpacePoint[depthWidth * depthHeight];
                latestBodies = new Body[kinectSensor.BodyFrameSource.BodyCount];
                
                // Subscribe to events
                colorFrameReader.FrameArrived += ColorFrameReader_FrameArrived;
                depthFrameReader.FrameArrived += DepthFrameReader_FrameArrived;
                infraredFrameReader.FrameArrived += InfraredFrameReader_FrameArrived;
                bodyFrameReader.FrameArrived += BodyFrameReader_FrameArrived;
                kinectSensor.IsAvailableChanged += KinectSensor_IsAvailableChanged;
                
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
            if (kinectSensor == null || !kinectSensor.IsAvailable || colorFrameReader == null) return;
            if (ColorFrameArrived != null) { ColorFrameArrived(this, e); }
            try
            {
                using (var colorFrame = e.FrameReference.AcquireFrame())
                {
                    if (colorFrame == null) return;
                    Interlocked.Increment(ref colorFramesReceived);
                    var colorFrameDescription = colorFrame.ColorFrameSource.CreateFrameDescription(ColorImageFormat.Bgra);
                    if (latestColorData == null || latestColorData.Length != colorFrameDescription.LengthInPixels * colorFrameDescription.BytesPerPixel)
                    {
                        latestColorData = new byte[colorFrameDescription.LengthInPixels * colorFrameDescription.BytesPerPixel];
                    }
                    colorFrame.CopyConvertedFrameDataToArray(latestColorData, ColorImageFormat.Bgra);
                    lock (colorDataLock)
                    {
                        lastColorFrameTimeUtc = DateTime.UtcNow;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(string.Format("Color frame update failed: {0}", ex.Message));
            }
        }
        
        private void InfraredFrameReader_FrameArrived(object sender, InfraredFrameArrivedEventArgs e)
        {
            if (kinectSensor == null || !kinectSensor.IsAvailable || infraredFrameReader == null) return;
            if (InfraredFrameArrived != null) { InfraredFrameArrived(this, e); }
            try
            {
                using (var irFrame = e.FrameReference.AcquireFrame())
                {
                    if (irFrame == null) return;
                    Interlocked.Increment(ref infraredFramesReceived);
                    var frameDesc = irFrame.InfraredFrameSource.FrameDescription;
                    if (latestInfraredData == null || latestInfraredData.Length != frameDesc.LengthInPixels)
                    {
                        latestInfraredData = new ushort[frameDesc.LengthInPixels];
                    }
                    irFrame.CopyFrameDataToArray(latestInfraredData);
                    lock (infraredDataLock)
                    {
                        lastInfraredFrameTimeUtc = DateTime.UtcNow;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(string.Format("Infrared frame update failed: {0}", ex.Message));
            }
        }

        public void SetInfraredExposureGain(double gain)
        {
            if (gain < 0.1) gain = 0.1;
            if (gain > 10.0) gain = 10.0;
            infraredExposureGain = gain;
        }
        
        private void DepthFrameReader_FrameArrived(object sender, DepthFrameArrivedEventArgs e)
        {
            if (kinectSensor == null || !kinectSensor.IsAvailable || depthFrameReader == null) return;
            if (DepthFrameArrived != null) { DepthFrameArrived(this, e); }
            try
            {
                using (var depthFrame = e.FrameReference.AcquireFrame())
                {
                    if (depthFrame == null) return;
                    Interlocked.Increment(ref depthFramesReceived);
                    var frameDesc = depthFrame.DepthFrameSource.FrameDescription;
                    if (latestDepthData == null || latestDepthData.Length != frameDesc.LengthInPixels)
                    {
                        latestDepthData = new ushort[frameDesc.LengthInPixels];
                    }
                    depthFrame.CopyFrameDataToArray(latestDepthData);
                    try
                    {
                        if (depthToColorPoints == null || depthToColorPoints.Length != frameDesc.LengthInPixels)
                        {
                            depthToColorPoints = new ColorSpacePoint[frameDesc.LengthInPixels];
                        }
                        coordinateMapper.MapDepthFrameToColorSpace(latestDepthData, depthToColorPoints);
                        // Also map to camera space for signed plane distance calculations
                        if (latestCameraSpacePoints == null || latestCameraSpacePoints.Length != frameDesc.LengthInPixels)
                        {
                            latestCameraSpacePoints = new CameraSpacePoint[frameDesc.LengthInPixels];
                        }
                        coordinateMapper.MapDepthFrameToCameraSpace(latestDepthData, latestCameraSpacePoints);
                    }
                    catch (Exception mapEx)
                    {
                        System.Diagnostics.Debug.WriteLine(string.Format("MapDepthFrameToColorSpace failed: {0}", mapEx.Message));
                    }
                    lastDepthFrameTimeUtc = DateTime.UtcNow;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(string.Format("Depth frame copy failed: {0}", ex.Message));
            }
        }

        private void KinectSensor_IsAvailableChanged(object sender, IsAvailableChangedEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine(string.Format("Kinect availability changed: {0}", e.IsAvailable));
            // Do not crash; UI will reflect frozen counters if unavailable
        }
        
        private void BodyFrameReader_FrameArrived(object sender, BodyFrameArrivedEventArgs e)
        {
            if (BodyFrameArrived != null) { BodyFrameArrived(this, e); }
            try
            {
                using (var bodyFrame = e.FrameReference.AcquireFrame())
                {
                    if (bodyFrame == null) return;
                    var bodies = new Body[bodyFrame.BodyFrameSource.BodyCount];
                    bodyFrame.GetAndRefreshBodyData(bodies);
                    lock (bodyDataLock)
                    {
                        latestBodies = bodies;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(string.Format("Body frame update failed: {0}", ex.Message));
            }
        }
        
        public BitmapSource GetColorBitmap()
        {
            if (!IsInitialized || kinectSensor == null)
                return CreateTestColorBitmap();

            // Create the bitmap lazily on the UI thread
            if (colorBitmap == null && colorWidth > 0 && colorHeight > 0)
            {
                colorBitmap = new WriteableBitmap(colorWidth, colorHeight, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
            }

            // Update pixels from latestColorData buffer
            if (colorBitmap != null && latestColorData != null)
            {
                try
                {
                    colorBitmap.WritePixels(
                        new System.Windows.Int32Rect(0, 0, colorWidth, colorHeight),
                        latestColorData,
                        colorWidth * 4,
                        0);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(string.Format("Color bitmap WritePixels failed: {0}", ex.Message));
                }
            }

            // Return a non-frozen reference; do not Freeze() here to allow continuous updates
            return colorBitmap;
        }

        public BitmapSource GetInfraredBitmap()
        {
            if (!IsInitialized || kinectSensor == null)
                return CreateTestDepthBitmap();

            using (var irFrame = infraredFrameReader != null ? infraredFrameReader.AcquireLatestFrame() : null)
            {
                if (irFrame == null)
                {
                    // Fallback to last buffered data if available
                    lock (infraredDataLock)
                    {
                        if (latestInfraredData == null || latestInfraredData.Length == 0)
                            return CreateTestDepthBitmap();
                    }
                }
                else
                {
                    var frameDesc = irFrame.InfraredFrameSource.FrameDescription;
                    if (latestInfraredData == null || latestInfraredData.Length != frameDesc.LengthInPixels)
                    {
                        latestInfraredData = new ushort[frameDesc.LengthInPixels];
                    }
                    irFrame.CopyFrameDataToArray(latestInfraredData);
                    infraredWidth = frameDesc.Width;
                    infraredHeight = frameDesc.Height;
                }

                // Convert IR data to BGRA bitmap with exposure gain
                int width = infraredWidth > 0 ? infraredWidth : 512;
                int height = infraredHeight > 0 ? infraredHeight : 424;
                var pixels = new byte[width * height * 4];
                ushort[] irSnapshot;
                lock (infraredDataLock)
                {
                    if (latestInfraredData == null)
                        return CreateTestDepthBitmap();
                    irSnapshot = new ushort[latestInfraredData.Length];
                    Array.Copy(latestInfraredData, irSnapshot, latestInfraredData.Length);
                }
                for (int i = 0; i < Math.Min(irSnapshot.Length, width * height); i++)
                {
                    double v = irSnapshot[i] * infraredExposureGain;
                    if (v > ushort.MaxValue) v = ushort.MaxValue;
                    byte intensity = (byte)(v / 256.0); // map 16-bit to 8-bit (approx)
                    int idx = i * 4;
                    pixels[idx] = intensity;
                    pixels[idx + 1] = intensity;
                    pixels[idx + 2] = intensity;
                    pixels[idx + 3] = 255;
                }
                var bitmap = new WriteableBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
                bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, width, height), pixels, width * 4, 0);
                return bitmap;
            }
        }
        
        public bool TryGetColorFrameRaw(out byte[] bgraBytes, out int width, out int height, out int stride)
        {
            bgraBytes = null;
            width = colorWidth;
            height = colorHeight;
            stride = (colorWidth > 0 ? colorWidth : 0) * 4;
            if (!IsInitialized || kinectSensor == null || colorWidth <= 0 || colorHeight <= 0)
                return false;
            lock (colorDataLock)
            {
                if (latestColorData == null || latestColorData.Length == 0)
                {
                    return false;
                }
                bgraBytes = new byte[latestColorData.Length];
                Array.Copy(latestColorData, bgraBytes, latestColorData.Length);
                return true;
            }
        }
        
        public bool TryGetDepthFrameRaw(out ushort[] depthData, out int width, out int height)
        {
            depthData = null;
            width = depthWidth;
            height = depthHeight;
            if (!IsInitialized || kinectSensor == null || depthWidth <= 0 || depthHeight <= 0)
                return false;
            lock (depthDataLock)
            {
                if (latestDepthData == null || latestDepthData.Length == 0)
                {
                    return false;
                }
                depthData = new ushort[latestDepthData.Length];
                Array.Copy(latestDepthData, depthData, latestDepthData.Length);
                return true;
            }
        }

        public bool IsColorStreamActive()
        {
            return (DateTime.UtcNow - lastColorFrameTimeUtc).TotalSeconds < 1.0;
        }

        public bool IsDepthStreamActive()
        {
            return (DateTime.UtcNow - lastDepthFrameTimeUtc).TotalSeconds < 1.0;
        }

        public Body[] GetLatestBodiesSnapshot()
        {
            if (!IsInitialized) return null;
            lock (bodyDataLock)
            {
                if (latestBodies == null) return null;
                var clone = new Body[latestBodies.Length];
                Array.Copy(latestBodies, clone, latestBodies.Length);
                return clone;
            }
        }
        
        public ushort[] GetLatestDepthDataSnapshot()
        {
            if (!IsInitialized) return null;
            lock (depthDataLock)
            {
                if (latestDepthData == null) return null;
                var clone = new ushort[latestDepthData.Length];
                Array.Copy(latestDepthData, clone, latestDepthData.Length);
                return clone;
            }
        }

        public bool TryGetTrackedHand(out CameraSpacePoint handPoint, bool preferRightHand = true)
        {
            handPoint = new CameraSpacePoint { X = float.NaN, Y = float.NaN, Z = float.NaN };
            var bodies = GetLatestBodiesSnapshot();
            if (bodies == null) return false;
            foreach (var body in bodies)
            {
                if (body == null || !body.IsTracked) continue;
                JointType jt = preferRightHand ? JointType.HandRight : JointType.HandLeft;
                var joint = body.Joints[jt];
                if (joint.TrackingState == TrackingState.Tracked || joint.TrackingState == TrackingState.Inferred)
                {
                    handPoint = joint.Position;
                    return true;
                }
            }
            return false;
        }

        public static double DistancePointToPlaneMeters(CameraSpacePoint p, double nx, double ny, double nz, double d)
        {
            // plane normalized? assume n is unit
            double value = nx * p.X + ny * p.Y + nz * p.Z + d;
            return Math.Abs(value); // meters when n is unit length
        }

        // Returns signed, normalized distance from point to plane. Positive values indicate the point is on the
        // side the normal points toward (expected to be toward the camera after orientation). Units: meters.
        public static double DistancePointToPlaneSignedNormalized(CameraSpacePoint p, double nx, double ny, double nz, double d)
        {
            double norm = System.Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (norm < 1e-9) return double.NaN;
            double inv = 1.0 / norm;
            double value = (nx * p.X + ny * p.Y + nz * p.Z + d) * inv;
            return value;
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

        // Produces a normalized depth visualization centered around a given distance with a window (meters).
        // Example: center=wall distance, window=0.6 => near=center-0.3, far=center+0.3, mapped to 255..0.
        public BitmapSource GetDepthBitmapNormalized(double centerMeters, double windowMeters = 0.6, bool colorRamp = false)
        {
            if (!IsInitialized || kinectSensor == null)
                return CreateTestDepthBitmap();

            using (var depthFrame = depthFrameReader.AcquireLatestFrame())
            {
                if (depthFrame == null)
                    return CreateTestDepthBitmap();

                var desc = depthFrame.DepthFrameSource.FrameDescription;
                var depthFrameData = new ushort[desc.LengthInPixels];
                depthFrame.CopyFrameDataToArray(depthFrameData);

                lock (depthDataLock)
                {
                    if (latestDepthData == null || latestDepthData.Length != depthFrameData.Length)
                    {
                        latestDepthData = new ushort[depthFrameData.Length];
                    }
                    System.Array.Copy(depthFrameData, latestDepthData, depthFrameData.Length);
                    depthWidth = desc.Width;
                    depthHeight = desc.Height;
                }

                double half = System.Math.Max(0.05, windowMeters * 0.5);
                double near = System.Math.Max(0.2, centerMeters - half);
                double far = System.Math.Max(near + 0.01, centerMeters + half);
                double range = far - near;

                var pixels = new byte[desc.LengthInPixels * 4];
                for (int i = 0; i < depthFrameData.Length; i++)
                {
                    double dm = depthFrameData[i] * 0.001;
                    double t = (far - dm) / range; // nearer -> higher intensity
                    if (double.IsNaN(t)) t = 0;
                    if (t < 0) t = 0; if (t > 1) t = 1;
                    byte r, g, b;
                    if (!colorRamp)
                    {
                        byte intensity = (byte)(t * 255.0);
                        r = g = b = intensity;
                    }
                    else
                    {
                        // Simple blue->cyan->green->yellow->red ramp
                        double v = t;
                        r = (byte)(System.Math.Min(1.0, System.Math.Max(0.0, 1.5 * v - 0.5)) * 255);
                        g = (byte)(System.Math.Min(1.0, System.Math.Max(0.0, 1.5 * v)) * 255);
                        b = (byte)(System.Math.Min(1.0, System.Math.Max(0.0, 1.0 - 1.5 * v)) * 255);
                    }
                    int idx = i * 4;
                    pixels[idx] = b;
                    pixels[idx + 1] = g;
                    pixels[idx + 2] = r;
                    pixels[idx + 3] = 255;
                }

                var bitmap = new WriteableBitmap(desc.Width, desc.Height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
                bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, desc.Width, desc.Height), pixels, desc.Width * 4, 0);
                return bitmap;
            }
        }

        public bool TryGetCameraSpaceFrame(out CameraSpacePoint[] cameraPoints, out int width, out int height)
        {
            cameraPoints = null;
            width = depthWidth;
            height = depthHeight;
            if (!IsInitialized || kinectSensor == null || depthWidth <= 0 || depthHeight <= 0)
                return false;
            lock (depthDataLock)
            {
                if (latestCameraSpacePoints == null || latestCameraSpacePoints.Length == 0)
                {
                    return false;
                }
                cameraPoints = new CameraSpacePoint[latestCameraSpacePoints.Length];
                System.Array.Copy(latestCameraSpacePoints, cameraPoints, latestCameraSpacePoints.Length);
                return true;
            }
        }

        public bool TryGetDepthToColorMapSnapshot(out ColorSpacePoint[] map, out int width, out int height)
        {
            map = null;
            width = depthWidth;
            height = depthHeight;
            if (!IsInitialized || kinectSensor == null || depthWidth <= 0 || depthHeight <= 0)
                return false;
            lock (depthDataLock)
            {
                if (depthToColorPoints == null || depthToColorPoints.Length == 0)
                    return false;
                map = new ColorSpacePoint[depthToColorPoints.Length];
                Array.Copy(depthToColorPoints, map, depthToColorPoints.Length);
                return true;
            }
        }

        public bool TryMapColorPixelToCameraSpace(int colorX, int colorY, out CameraSpacePoint cameraPoint)
        {
            cameraPoint = new CameraSpacePoint { X = float.NaN, Y = float.NaN, Z = float.NaN };
            if (!IsInitialized || coordinateMapper == null)
                return false;

            ushort[] depthDataSnapshot = null;
            ColorSpacePoint[] depthToColorSnapshot = null;
            int localDepthWidth, localDepthHeight, localColorWidth, localColorHeight;
            lock (depthDataLock)
            {
                if (latestDepthData == null || depthToColorPoints == null)
                {
                    return false;
                }
                depthDataSnapshot = new ushort[latestDepthData.Length];
                Array.Copy(latestDepthData, depthDataSnapshot, latestDepthData.Length);
                depthToColorSnapshot = new ColorSpacePoint[depthToColorPoints.Length];
                Array.Copy(depthToColorPoints, depthToColorSnapshot, depthToColorPoints.Length);
                localDepthWidth = depthWidth;
                localDepthHeight = depthHeight;
                localColorWidth = colorWidth > 0 ? colorWidth : 1920;
                localColorHeight = colorHeight > 0 ? colorHeight : 1080;
            }

            if (colorX < 0 || colorY < 0 || colorX >= localColorWidth || colorY >= localColorHeight)
                return false;

            try
            {
                System.Diagnostics.Debug.WriteLine(string.Format("Attempting to map color point at: {0},{1}", colorX, colorY));
                // Reverse mapping: find the nearest depth pixel whose mapped color coordinate is close to (colorX,colorY)
                int bestDx = -1, bestDy = -1;
                double bestDistSq = double.MaxValue;
                for (int dy = 0; dy < localDepthHeight; dy++)
                {
                    int row = dy * localDepthWidth;
                    for (int dx = 0; dx < localDepthWidth; dx++)
                    {
                        int di = row + dx;
                        var csp = depthToColorSnapshot[di];
                        if (float.IsNaN(csp.X) || float.IsNaN(csp.Y)) continue;
                        int cx = (int)Math.Round(csp.X);
                        int cy = (int)Math.Round(csp.Y);
                        if (cx < 0 || cy < 0 || cx >= localColorWidth || cy >= localColorHeight) continue;
                        // measure distance in color space
                        int ddx = cx - colorX;
                        int ddy = cy - colorY;
                        double distSq = (double)ddx * ddx + (double)ddy * ddy;
                        if (distSq < bestDistSq)
                        {
                            bestDistSq = distSq;
                            bestDx = dx;
                            bestDy = dy;
                            if (bestDistSq == 0) break;
                        }
                    }
                    if (bestDistSq == 0) break;
                }
                if (bestDx >= 0 && bestDy >= 0)
                {
                    ushort depth = depthDataSnapshot[bestDy * localDepthWidth + bestDx];
                    if (depth != 0)
                    {
                        var dsp = new DepthSpacePoint { X = bestDx, Y = bestDy };
                        var mapped = coordinateMapper.MapDepthPointToCameraSpace(dsp, depth);
                        if (!(float.IsInfinity(mapped.X) || float.IsInfinity(mapped.Y) || float.IsInfinity(mapped.Z) ||
                              float.IsNaN(mapped.X) || float.IsNaN(mapped.Y) || float.IsNaN(mapped.Z)))
                        {
                            cameraPoint = mapped;
                            return true;
                        }
                    }
                }
                return false;
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

            if (infraredFrameReader != null)
            {
                infraredFrameReader.Dispose();
                infraredFrameReader = null;
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

        // Attempts to set color camera exposure via ColorCameraSettings if available
        public bool SetColorCameraExposure(TimeSpan exposure)
        {
            // Kinect v2 SDK exposes ColorCameraSettings as read-only; there is no public setter.
            // Return false to indicate hardware exposure could not be applied.
            return false;
        }
    }
}
