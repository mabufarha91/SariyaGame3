using System;
using System.Collections.Generic;
using System.Windows;

namespace Microsoft.Kinect
{
    // Comprehensive mock classes for building without Kinect SDK
    public class KinectSensor
    {
        public static KinectSensor GetDefault() => new KinectSensor();
        public bool IsAvailable => false;
        public bool IsOpen => false;
        public void Open() { }
        public void Close() { }
        public DepthFrameSource DepthFrameSource => new DepthFrameSource();
        public ColorFrameSource ColorFrameSource => new ColorFrameSource();
        public InfraredFrameSource InfraredFrameSource => new InfraredFrameSource();
        public BodyFrameSource BodyFrameSource => new BodyFrameSource();
        public CoordinateMapper CoordinateMapper => new CoordinateMapper();
        public event EventHandler<IsAvailableChangedEventArgs> IsAvailableChanged;
    }

    public class DepthFrameSource
    {
        public FrameDescription FrameDescription => new FrameDescription();
        public DepthFrameReader OpenReader() => new DepthFrameReader();
    }

    public class ColorFrameSource
    {
        public FrameDescription FrameDescription => new FrameDescription();
        public ColorFrameReader OpenReader() => new ColorFrameReader();
        public FrameDescription CreateFrameDescription(ColorImageFormat format) => new FrameDescription();
    }

    public class InfraredFrameSource
    {
        public FrameDescription FrameDescription => new FrameDescription();
        public InfraredFrameReader OpenReader() => new InfraredFrameReader();
    }

    public class BodyFrameSource
    {
        public BodyFrameReader OpenReader() => new BodyFrameReader();
        public Body[] Bodies => new Body[0];
        public int BodyCount => 0;
    }

    public class FrameDescription
    {
        public int Width => 512;
        public int Height => 424;
        public float DiagonalFieldOfView => 70.6f;
        public int LengthInPixels => Width * Height;
    }

    public class DepthFrame : IDisposable
    {
        public ushort[] GetPixelData() => new ushort[512 * 424];
        public FrameDescription FrameDescription => new FrameDescription();
        public DepthFrameSource DepthFrameSource => new DepthFrameSource();
        public void CopyFrameDataToArray(ushort[] frameData) { }
        public void Dispose() { }
    }

    public class ColorFrame : IDisposable
    {
        public byte[] GetRawColorFrameData() => new byte[1920 * 1080 * 4];
        public FrameDescription FrameDescription => new FrameDescription();
        public ColorFrameSource ColorFrameSource => new ColorFrameSource();
        public void CopyConvertedFrameDataToArray(byte[] frameData, ColorImageFormat format) { }
        public void Dispose() { }
    }

    public class InfraredFrame : IDisposable
    {
        public ushort[] GetPixelData() => new ushort[512 * 424];
        public FrameDescription FrameDescription => new FrameDescription();
        public InfraredFrameSource InfraredFrameSource => new InfraredFrameSource();
        public void CopyFrameDataToArray(ushort[] frameData) { }
        public void Dispose() { }
    }

    public class BodyFrame : IDisposable
    {
        public Body[] Bodies => new Body[0];
        public BodyFrameSource BodyFrameSource => new BodyFrameSource();
        public void GetAndRefreshBodyData(Body[] bodies) { }
        public void Dispose() { }
    }

    public class Body
    {
        public bool IsTracked => false;
        public Joint GetJoint(JointType jointType) => new Joint();
        public Dictionary<JointType, Joint> Joints => new Dictionary<JointType, Joint>();
    }

    public struct Joint
    {
        public JointType JointType;
        public CameraSpacePoint Position;
        public TrackingState TrackingState;
    }

    public enum JointType
    {
        Head = 0, Neck = 1, SpineShoulder = 2, SpineMid = 3, SpineBase = 4,
        ShoulderRight = 5, ElbowRight = 6, WristRight = 7, HandRight = 8,
        ShoulderLeft = 9, ElbowLeft = 10, WristLeft = 11, HandLeft = 12,
        HipRight = 13, KneeRight = 14, AnkleRight = 15, FootRight = 16,
        HipLeft = 17, KneeLeft = 18, AnkleLeft = 19, FootLeft = 20,
        HandTipLeft = 21, ThumbLeft = 22, HandTipRight = 23, ThumbRight = 24
    }

    public enum TrackingState
    {
        NotTracked = 0, Inferred = 1, Tracked = 2
    }

    public enum ColorImageFormat
    {
        None = 0, Rgba = 1, Yuv = 2, Bgra = 3, Bayer = 4, Yuy2 = 5
    }

    public class CoordinateMapper
    {
        public Point MapDepthPointToColorSpace(DepthSpacePoint depthPoint) => new Point(0, 0);
        public Point MapDepthPointToColorSpace(float x, float y) => new Point(0, 0);
        public CameraSpacePoint MapDepthPointToCameraSpace(DepthSpacePoint depthPoint) => new CameraSpacePoint();
        public CameraSpacePoint MapDepthPointToCameraSpace(float x, float y) => new CameraSpacePoint();
        public DepthSpacePoint MapCameraPointToDepthSpace(CameraSpacePoint cameraPoint) => new DepthSpacePoint();
        public Point MapCameraPointToColorSpace(CameraSpacePoint cameraPoint) => new Point(0, 0);
        public ColorSpacePoint[] MapDepthFrameToColorSpace(DepthFrame depthFrame) => new ColorSpacePoint[512 * 424];
        public CameraSpacePoint[] MapDepthFrameToCameraSpace(DepthFrame depthFrame) => new CameraSpacePoint[512 * 424];
        public ColorSpacePoint[] MapDepthFrameToColorSpace(DepthFrame depthFrame, ColorSpacePoint[] colorSpacePoints) => new ColorSpacePoint[512 * 424];
        public CameraSpacePoint[] MapDepthFrameToCameraSpace(DepthFrame depthFrame, CameraSpacePoint[] cameraSpacePoints) => new CameraSpacePoint[512 * 424];
    }

    public struct DepthSpacePoint
    {
        public float X;
        public float Y;
    }

    public struct CameraSpacePoint
    {
        public float X;
        public float Y;
        public float Z;
    }

    public struct ColorSpacePoint
    {
        public float X;
        public float Y;
    }

    // Frame Readers
    public class ColorFrameReader
    {
        public event EventHandler<ColorFrameArrivedEventArgs> FrameArrived;
        public ColorFrame AcquireLatestFrame() => new ColorFrame();
        public void Dispose() { }
    }

    public class DepthFrameReader
    {
        public event EventHandler<DepthFrameArrivedEventArgs> FrameArrived;
        public DepthFrame AcquireLatestFrame() => new DepthFrame();
        public void Dispose() { }
    }

    public class InfraredFrameReader
    {
        public event EventHandler<InfraredFrameArrivedEventArgs> FrameArrived;
        public InfraredFrame AcquireLatestFrame() => new InfraredFrame();
        public void Dispose() { }
    }

    public class BodyFrameReader
    {
        public event EventHandler<BodyFrameArrivedEventArgs> FrameArrived;
        public BodyFrame AcquireLatestFrame() => new BodyFrame();
        public void Dispose() { }
    }

    // Event Args
    public class ColorFrameArrivedEventArgs : EventArgs
    {
        public ColorFrameReference FrameReference => new ColorFrameReference();
    }

    public class DepthFrameArrivedEventArgs : EventArgs
    {
        public DepthFrameReference FrameReference => new DepthFrameReference();
    }

    public class InfraredFrameArrivedEventArgs : EventArgs
    {
        public InfraredFrameReference FrameReference => new InfraredFrameReference();
    }

    public class BodyFrameArrivedEventArgs : EventArgs
    {
        public BodyFrameReference FrameReference => new BodyFrameReference();
    }

    public class IsAvailableChangedEventArgs : EventArgs
    {
        public bool IsAvailable => false;
    }

    // Frame References
    public class ColorFrameReference
    {
        public ColorFrame AcquireFrame() => new ColorFrame();
        public void Dispose() { }
    }

    public class DepthFrameReference
    {
        public DepthFrame AcquireFrame() => new DepthFrame();
        public void Dispose() { }
    }

    public class InfraredFrameReference
    {
        public InfraredFrame AcquireFrame() => new InfraredFrame();
        public void Dispose() { }
    }

    public class BodyFrameReference
    {
        public BodyFrame AcquireFrame() => new BodyFrame();
        public void Dispose() { }
    }

    // Legacy support
    public class MultiSourceFrameReader
    {
        public event EventHandler<MultiSourceFrameArrivedEventArgs> MultiSourceFrameArrived;
        public void Dispose() { }
    }

    public class MultiSourceFrameArrivedEventArgs : EventArgs
    {
        public MultiSourceFrameReference FrameReference => new MultiSourceFrameReference();
    }

    public class MultiSourceFrameReference
    {
        public DepthFrameReference DepthFrameReference => new DepthFrameReference();
        public ColorFrameReference ColorFrameReference => new ColorFrameReference();
        public void Dispose() { }
    }
}
