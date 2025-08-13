using System.Windows;

namespace KinectCalibrationWPF.Models
{
    public class CalibrationPoint
    {
        public Point Position { get; set; }
        public int Index { get; set; }
        public bool IsDragging { get; set; }
        public bool IsSelected { get; set; }
        
        public CalibrationPoint(Point position, int index)
        {
            Position = position;
            Index = index;
            IsDragging = false;
            IsSelected = false;
        }
        
        public CalibrationPoint(double x, double y, int index) : this(new Point(x, y), index)
        {
        }
        
        public override string ToString()
        {
            return string.Format("Point {0}: ({1:F1}, {2:F1})", Index + 1, Position.X, Position.Y);
        }
    }
}
