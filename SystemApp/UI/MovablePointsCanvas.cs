using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using KinectCalibrationWPF.Models;

namespace KinectCalibrationWPF.UI
{
    public class MovablePointsCanvas : Canvas, INotifyPropertyChanged
    {
        private ObservableCollection<CalibrationPoint> calibrationPoints;
        private CalibrationPoint draggedPoint;
        private bool isDragging = false;
        private Point lastMousePosition;
        private Dictionary<CalibrationPoint, Ellipse> pointEllipseMap = new Dictionary<CalibrationPoint, Ellipse>();
        private Dictionary<CalibrationPoint, TextBlock> pointLabelMap = new Dictionary<CalibrationPoint, TextBlock>();
        private UIElement overlayElement;
        
        public event PropertyChangedEventHandler PropertyChanged;
        public event EventHandler<CalibrationPointsChangedEventArgs> PointsChanged;
        public event EventHandler PlaneRecalculated;
        public event EventHandler AreaRecalculated;
        public event EventHandler<StatusChangedEventArgs> StatusChanged;
        public event EventHandler<PlaneCalculatedEventArgs> PlaneCalculated; // includes normal and distance
        
        public int MaxPoints { get; set; }
        public double PointSize { get; set; }
        public Brush PointColor { get; set; }
        public Brush DraggingColor { get; set; }
        public Brush SelectedColor { get; set; }
        
        public ObservableCollection<CalibrationPoint> CalibrationPoints
        {
            get { return calibrationPoints; }
            set
            {
                calibrationPoints = value;
                OnPropertyChanged("CalibrationPoints");
            }
        }
        
        public int PointCount 
        { 
            get 
            { 
                return CalibrationPoints != null ? CalibrationPoints.Count : 0; 
            } 
        }
        public bool IsComplete 
        { 
            get 
            { 
                return PointCount >= MaxPoints; 
            } 
        }
        
        public MovablePointsCanvas()
        {
            MaxPoints = 2;
            PointSize = 20;
            PointColor = Brushes.Red;
            DraggingColor = Brushes.Cyan;
            SelectedColor = Brushes.Yellow;
            
            CalibrationPoints = new ObservableCollection<CalibrationPoint>();
            CalibrationPoints.CollectionChanged += CalibrationPoints_CollectionChanged;
            
            // Setup mouse events for the three-question logic
            this.MouseLeftButtonDown += MovablePointsCanvas_MouseLeftButtonDown;
            this.MouseMove += MovablePointsCanvas_MouseMove;
            this.MouseLeftButtonUp += MovablePointsCanvas_MouseLeftButtonUp;
            
            // Enable mouse capture for smooth dragging
            this.Focusable = true;
        }
        
        private void CalibrationPoints_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            RefreshVisualPoints();
            OnPropertyChanged("PointCount");
            OnPropertyChanged("IsComplete");
        }
        
        // THE THREE QUESTIONS - Question 1: "Did the user just BEGIN a click?" (MouseDown)
        private void MovablePointsCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            Point mousePosition = e.GetPosition(this);
            lastMousePosition = mousePosition;
            
            // Check if clicking ON an existing point
            CalibrationPoint clickedPoint = FindPointAtPosition(mousePosition);
            
            if (clickedPoint != null)
            {
                // The brain "grabs" that point
                draggedPoint = clickedPoint;
                draggedPoint.IsDragging = true;
                isDragging = true;
                
                this.CaptureMouse();
                this.Focus();
                
                // Visual feedback
                RefreshVisualPoints();
                
                // Notify status update
                OnStatusUpdate(string.Format("Grabbed point {0}", draggedPoint.Index + 1), DraggingColor);
                
                e.Handled = true;
                return;
            }
            
            // If click was NOT on existing point, check if we can add a new point
            if (CalibrationPoints.Count < MaxPoints)
            {
                // Create a NEW point
                CalibrationPoint newPoint = new CalibrationPoint(mousePosition, CalibrationPoints.Count);
                CalibrationPoints.Add(newPoint);
                
                // Start dragging the new point immediately
                draggedPoint = newPoint;
                draggedPoint.IsDragging = true;
                isDragging = true;
                
                this.CaptureMouse();
                this.Focus();
                
                // Notify system of point change
                OnPointsChanged(new CalibrationPointsChangedEventArgs(CalibrationPoints.Select(p => p.Position).ToList()));
                OnStatusUpdate(string.Format("Added point {0}/{1}", CalibrationPoints.Count, MaxPoints), Brushes.White);
                
                e.Handled = true;
            }
        }
        
        // THE THREE QUESTIONS - Question 2: "Is the user CURRENTLY dragging?" (MouseHeldDown)
        private void MovablePointsCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (isDragging && draggedPoint != null)
            {
                Point currentMousePosition = e.GetPosition(this);
                
                // Update the position of the point we're holding
                Point newPosition = currentMousePosition;
                
                // Constrain to canvas bounds
                newPosition.X = Math.Max(PointSize / 2, Math.Min(newPosition.X, this.ActualWidth - PointSize / 2));
                newPosition.Y = Math.Max(PointSize / 2, Math.Min(newPosition.Y, this.ActualHeight - PointSize / 2));
                
                draggedPoint.Position = newPosition;
                
                // Show real-time feedback
                OnStatusUpdate(string.Format("Dragging point {0}: ({1:F1}, {2:F1})", draggedPoint.Index + 1, newPosition.X, newPosition.Y), DraggingColor);
                
                // Update visual representation
                UpdatePointVisual(draggedPoint);
                
                e.Handled = true;
            }
        }
        
        // THE THREE QUESTIONS - Question 3: "Did the user just END a click?" (MouseUp)
        private void MovablePointsCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (isDragging && draggedPoint != null)
            {
                // The brain "lets go" of the point
                draggedPoint.IsDragging = false;
                isDragging = false;
                
                this.ReleaseMouseCapture();
                
                // THE FEEDBACK LOOP - Shout to recalculate everything
                OnPointsChanged(new CalibrationPointsChangedEventArgs(CalibrationPoints.Select(p => p.Position).ToList()));
                
                // Recalculate based on screen type
                if (MaxPoints == 2)
                {
                    // Screen 1: Recalculate plane
                    OnPlaneRecalculated();
                    OnStatusUpdate("Plane recalculated!", Brushes.Green);
                }
                else
                {
                    // Screen 2: Recalculate area
                    OnAreaRecalculated();
                    OnStatusUpdate("Area recalculated!", Brushes.Green);
                }
                
                // Update visual representation
                RefreshVisualPoints();
                
                draggedPoint = null;
                e.Handled = true;
            }
        }
        
        private CalibrationPoint FindPointAtPosition(Point position)
        {
            double hitRadius = PointSize / 2;
            
            return CalibrationPoints.FirstOrDefault(point =>
                Math.Sqrt(Math.Pow(point.Position.X - position.X, 2) + 
                         Math.Pow(point.Position.Y - position.Y, 2)) <= hitRadius);
        }
        
        private void RefreshVisualPoints()
        {
            // Clear existing visual elements
            this.Children.Clear();
            pointEllipseMap.Clear();
            pointLabelMap.Clear();
            
            // Add overlay first so points draw on top
            if (overlayElement != null)
            {
                this.Children.Add(overlayElement);
            }
            
            // Add visual representation for each point
            foreach (var point in CalibrationPoints)
            {
                AddPointVisual(point);
            }
        }
        
        private void AddPointVisual(CalibrationPoint point)
        {
            // Create circle for the point
            Ellipse ellipse = new Ellipse
            {
                Width = PointSize,
                Height = PointSize,
                Fill = point.IsDragging ? DraggingColor : PointColor,
                Stroke = Brushes.White,
                StrokeThickness = 2
            };
            
            // Position the ellipse
            Canvas.SetLeft(ellipse, point.Position.X - PointSize / 2);
            Canvas.SetTop(ellipse, point.Position.Y - PointSize / 2);
            
            // Add point number
            TextBlock numberText = new TextBlock
            {
                Text = (point.Index + 1).ToString(),
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            
            // Position the text
            Canvas.SetLeft(numberText, point.Position.X - 10);
            Canvas.SetTop(numberText, point.Position.Y - 8);
            
            // Add to canvas
            this.Children.Add(ellipse);
            this.Children.Add(numberText);

            // Track visuals for precise updates
            pointEllipseMap[point] = ellipse;
            pointLabelMap[point] = numberText;
        }
        
        private void UpdatePointVisual(CalibrationPoint point)
        {
            Ellipse ellipse;
            TextBlock textBlock;
            if (!pointEllipseMap.TryGetValue(point, out ellipse) || !pointLabelMap.TryGetValue(point, out textBlock))
            {
                // Fallback if visuals were rebuilt
                RefreshVisualPoints();
                if (!pointEllipseMap.TryGetValue(point, out ellipse) || !pointLabelMap.TryGetValue(point, out textBlock))
                {
                    return;
                }
            }

            // Update position
            Canvas.SetLeft(ellipse, point.Position.X - PointSize / 2);
            Canvas.SetTop(ellipse, point.Position.Y - PointSize / 2);

            // Update color
            ellipse.Fill = point.IsDragging ? DraggingColor : PointColor;

            // Update text position
            Canvas.SetLeft(textBlock, point.Position.X - 10);
            Canvas.SetTop(textBlock, point.Position.Y - 8);
        }
        
        // Public methods for external control
        public void ClearPoints()
        {
            CalibrationPoints.Clear();
            OnStatusUpdate("Points cleared", Brushes.Yellow);
        }
        
        public List<Point> GetPointPositions()
        {
            return CalibrationPoints.Select(p => p.Position).ToList();
        }
        
        public void SetMaxPoints(int max)
        {
            MaxPoints = max;
            OnPropertyChanged("IsComplete");
        }

        public void RecalculatePlane()
        {
            // Explicit recalc triggered by UI button
            OnPointsChanged(new CalibrationPointsChangedEventArgs(CalibrationPoints.Select(p => p.Position).ToList()));
            if (MaxPoints == 2 && CalibrationPoints.Count == 2)
            {
                // Notify simple recalculated
                OnPlaneRecalculated();
                // Raise detailed plane calculated with placeholder; actual 3D mapping done by host window
                if (PlaneCalculated != null)
                {
                    PlaneCalculated(this, new PlaneCalculatedEventArgs(new Vector3D(0, 0, 1), 0));
                }
                OnStatusUpdate("Plane recalculated!", Brushes.Green);
            }
            else if (MaxPoints != 2 && CalibrationPoints.Count >= MaxPoints)
            {
                OnAreaRecalculated();
                OnStatusUpdate("Area recalculated!", Brushes.Green);
            }
            else
            {
                OnStatusUpdate("Not enough points to recalculate", Brushes.Orange);
            }
        }

        public void SetOverlay(UIElement overlay)
        {
            overlayElement = overlay;
            RefreshVisualPoints();
        }

        public struct Vector3D
        {
            public double X;
            public double Y;
            public double Z;
            public Vector3D(double x, double y, double z) { X = x; Y = y; Z = z; }
        }

        public class PlaneCalculatedEventArgs : EventArgs
        {
            public Vector3D Normal { get; private set; }
            public double Distance { get; private set; }
            public PlaneCalculatedEventArgs(Vector3D normal, double distance)
            {
                Normal = normal; Distance = distance;
            }
        }
        
        // Event handlers
        protected virtual void OnPointsChanged(CalibrationPointsChangedEventArgs e)
        {
            if (PointsChanged != null) { PointsChanged(this, e); }
        }
        
        protected virtual void OnPlaneRecalculated()
        {
            if (PlaneRecalculated != null) { PlaneRecalculated(this, EventArgs.Empty); }
        }
        
        protected virtual void OnAreaRecalculated()
        {
            if (AreaRecalculated != null) { AreaRecalculated(this, EventArgs.Empty); }
        }
        
        protected virtual void OnStatusUpdate(string message, Brush color)
        {
            if (StatusChanged != null)
            {
                StatusChanged(this, new StatusChangedEventArgs(message, color));
            }
        }
        
        protected virtual void OnPropertyChanged(string propertyName)
        {
            if (PropertyChanged != null) { PropertyChanged(this, new PropertyChangedEventArgs(propertyName)); }
        }
    }
    
    public class CalibrationPointsChangedEventArgs : EventArgs
    {
        public List<Point> Points { get; private set; }
        
        public CalibrationPointsChangedEventArgs(List<Point> points)
        {
            Points = points;
        }
    }

    public class StatusChangedEventArgs : EventArgs
    {
        public string Message { get; private set; }
        public Brush Color { get; private set; }

        public StatusChangedEventArgs(string message, Brush color)
        {
            Message = message;
            Color = color;
        }
    }
}
