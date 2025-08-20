# Kinect Calibration System - WPF Edition

A professional Windows Presentation Foundation (WPF) application for calibrating Kinect v2 sensors for interactive wall applications.

## Quick Start (TL;DR)

- Build: run `build_wpf.bat` in `SariyaGame3/SystemApp` (restores NuGet automatically if needed)
- Run: `bin/Debug/KinectCalibrationWPF.exe`
- Screen 2 markers: place four PNGs in the app folder:
  - Debug: `bin/Debug/Markers/aruco_7x7_250_id0.png` … `aruco_7x7_250_id3.png`
  - Release: `bin/Release/Markers/aruco_7x7_250_id0.png` … `aruco_7x7_250_id3.png`
- Make them BIG using `QrSizeSlider` on Screen 2
- Click “Find Markers”; check Status text and diagnostics
- Diagnostics (after clicking Find):
  - `%USERPROFILE%/Pictures/KinectCalibrationDiagnostics/` and `bin/Debug/Diagnostics/`

## 🎯 **Why WPF Instead of Unity?**

We migrated from Unity to WPF for several strategic reasons:

### **Direct Event-Driven Architecture**
- **Unity**: Complex game loop with Update() cycles, component dependencies, and potential timing issues
- **WPF**: Direct event-driven model where button clicks immediately trigger specific events

### **UI Control Reliability**
- **Unity**: UI controls can have connection issues with underlying logic
- **WPF**: Native Windows controls with guaranteed event handling and data binding

### **Simplified Development**
- **Unity**: Requires understanding of game engine concepts, prefabs, and scene management
- **WPF**: Standard Windows application development with familiar C# patterns

### **Better Suited for Utility Applications**
- **Unity**: Designed for games and interactive experiences
- **WPF**: Purpose-built for desktop applications and utilities

## 🚀 **Features**

### **✅ Implemented (Screen 1)**
- **Three-Question Logic System**: Implements the exact step-by-step logic for movable points
- **MovablePointsCanvas**: Clean, focused implementation following fundamental programming principles
- **Real-time Camera Feed**: Live Kinect color stream with test mode fallback
- **Interactive Point Placement**: Click to place, drag to adjust calibration points
- **Visual Feedback**: Color changes during dragging, point numbering, real-time coordinates
- **Automatic Plane Calculation**: Points automatically recalculate when moved
- **Professional UI**: Modern Material Design-inspired interface

### **Implemented (Screen 2 & 3 — Sensor Alignment)**
- Live color feed (unprocessed) in `CameraFeedImage`
- Real-time HSV mask for visualization in `FilteredCameraFeedImage`
- Projected ArUco markers (7x7_250) on the secondary display via `ProjectorWindow`
- Multi-pass ArUco detection (robust under projector lighting):
  - Grayscale 7x7 with tuned defaults
  - Grayscale 7x7 with stronger thresholds
  - Inverted grayscale 7x7
  - Fallback 6x6 (strong) if assets are 6x6
  - Last-resort on HSV mask
- Visual overlays: red centers + lime outlines for detected quads
- Diagnostics saved for troubleshooting (color/gray/HSV-mask snapshots)

### **🔄 Planned Features**
- **Screen 4**: Touch Detection Test & Tuning
- **3D Coordinate Mapping**: Convert 2D screen points to 3D world coordinates
- **Calibration Data Persistence**: Save/load calibration settings
- **Advanced Touch Detection**: Real-time depth-based touch detection

## 🛠 **Technical Architecture**

### **Core Components**

#### **1. KinectManager** (`KinectManager/KinectManager.cs`)
- Handles Kinect sensor initialization and management
- Provides color and depth frame access
- Includes test mode for development without Kinect hardware
- Implements proper resource disposal

#### **2. MovablePointsCanvas** (`UI/MovablePointsCanvas.cs`)
- **Implements the Three-Question Logic**:
  - **Question 1**: "Did the user just BEGIN a click?" (MouseDown)
  - **Question 2**: "Is the user CURRENTLY dragging?" (MouseHeldDown)  
  - **Question 3**: "Did the user just END a click?" (MouseUp)
- Custom WPF Canvas with drag-and-drop functionality
- Real-time visual feedback and coordinate display
- Automatic plane/area recalculation

#### **3. CalibrationWizardWindow** (`CalibrationWizard/CalibrationWizardWindow.xaml`)
- Screen 1 of the 4-screen calibration wizard
- Professional UI with camera feed and control panel
- Real-time status updates and button state management

#### **Screen 2: Sensor Alignment** (`CalibrationWizard/Screen2_MarkerAlignment.xaml[.cs]`)
- `CameraFeedImage`: always shows color feed
- `FilteredCameraFeedImage`: shows HSV mask in black & white
- `FindMarkersButton_Click`:
  - Gets a fresh frame from `KinectManager`
  - Builds HSV mask (for display), prepares grayscale (for detection)
  - Runs multi-pass ArUco detection
  - Draws overlays and updates status/logs
- `ProjectorWindow`: displays 4 markers (IDs 0–3), scaled by `QrSizeSlider`

#### **4. CalibrationPoint Model** (`Models/CalibrationPoint.cs`)
- Data model for calibration points
- Tracks position, index, and dragging state

## 📋 **Requirements**

### **System Requirements**
- Windows 10 or later
- .NET Framework 4.8
- DirectX 11 compatible graphics card

### **Kinect Requirements**
- Kinect v2 sensor
- Kinect for Windows SDK 2.0
- USB 3.0 port

### **Development Requirements**
- Visual Studio 2019 or later
- WPF development tools
- Kinect SDK installed

## 🚀 **Getting Started**

### **Building the Application**

Option A (recommended):

1. Open PowerShell in `SariyaGame3/SystemApp`
2. Run `./build_wpf.bat`

Option B (Visual Studio):

1. Open `KinectCalibrationWPF.csproj`
2. Restore NuGet packages
3. Build (Debug | x64)
4. Run

### **Running Without Kinect**

The application includes a test mode that works without Kinect hardware:
- Test color and depth patterns are generated
- All UI functionality works normally
- Perfect for development and testing

### **Using Screen 1**
### **Using Screen 2 (Sensor Alignment)**

1. Ensure the projector is configured as a second display
2. Place marker PNGs in the app folder:
   - Debug: `bin/Debug/Markers/aruco_7x7_250_id0.png` … `id3.png`
   - Release: `bin/Release/Markers/aruco_7x7_250_id0.png` … `id3.png`
3. Start the Calibration Wizard → Screen 2
4. Use `QrSizeSlider` to make markers large and clearly visible
5. Adjust HSV sliders to visualize the mask (detection uses grayscale)
6. Click “Find Markers”; watch Status text for the detection passes
7. If found, red dots appear at centers; lime outlines draw the quads

Notes:
- If Status shows a WARNING about placeholders, your projector is not using real ArUco PNGs
- Marker dictionary: `DICT_7X7_250` (IDs 0,1,2,3)
- Increase marker size and reduce motion blur for best results

### **Diagnostics and Logs**

- On each “Find Markers” click, the app logs detection passes and saves frames to:
  - `%USERPROFILE%/Pictures/KinectCalibrationDiagnostics/`
  - `bin/Debug/Diagnostics/` (or `bin/Release/Diagnostics/`)
- Files: `color_*.png`, `gray_*.png`, `hsvmask_*.png`
- Status text shows exact save locations and which detection pass succeeded

1. **Launch the application**
2. **Click "Start Calibration Wizard"**
3. **Click anywhere on the camera feed to place points**
4. **Drag points to adjust their positions**
5. **Watch real-time feedback and coordinate display**
6. **Click "Calculate Plane" when satisfied**
7. **Click "Next Screen" to proceed**

## 🎮 **Controls**

### **Keyboard Shortcuts**
- **F1**: Start calibration wizard
- **F2**: Touch detection test (coming soon)
- **F3**: Settings (coming soon)
- **ESC**: Return to main menu or exit

### **Mouse Controls**
- **Left Click**: Place new point or select existing point
- **Left Click + Drag**: Move selected point
- **Release**: Complete point placement/movement

## 🔧 **Development Notes**

### **The Three-Question Logic Implementation**

The movable points system follows the exact logic outlined in the original specification:

```csharp
// Question 1: "Did the user just BEGIN a click?" (MouseDown)
private void MovablePointsCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
{
    // Check if clicking ON an existing point
    // If yes: grab that point
    // If no: create new point (if under limit)
}

// Question 2: "Is the user CURRENTLY dragging?" (MouseHeldDown)
private void MovablePointsCanvas_MouseMove(object sender, MouseEventArgs e)
{
    // Update position of dragged point
    // Show real-time feedback
}

// Question 3: "Did the user just END a click?" (MouseUp)
private void MovablePointsCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
{
    // Release the point
    // Trigger recalculation
}
```

### **Event-Driven Architecture**

WPF's event-driven model ensures reliable UI control connections:

```csharp
// Direct event handling - no complex component dependencies
MovablePointsCanvas.PointsChanged += MovablePointsCanvas_PointsChanged;
MovablePointsCanvas.PlaneRecalculated += MovablePointsCanvas_PlaneRecalculated;

// Immediate UI updates
private void UpdateButtonStates()
{
    CalculatePlaneButton.IsEnabled = (pointCount == 2);
    NextButton.IsEnabled = planeCalculated;
}
```

## 📁 **Project Structure**

```
KinectCalibrationWPF/
├── App.xaml                          # Application entry point
├── App.xaml.cs                       # Application logic
├── MainWindow.xaml                   # Main application window
├── MainWindow.xaml.cs                # Main window logic
├── CalibrationWizard/
│   ├── CalibrationWizardWindow.xaml        # Screen 1 UI
│   ├── CalibrationWizardWindow.xaml.cs     # Screen 1 logic
│   ├── Screen2_MarkerAlignment.xaml        # Screen 2 UI
│   ├── Screen2_MarkerAlignment.xaml.cs     # Screen 2 logic (camera feeds + ArUco detection)
│   ├── ProjectorWindow.xaml                # Secondary display for markers
│   └── ProjectorWindow.xaml.cs             # Marker placement/scaling API
├── KinectManager/
│   └── KinectManager.cs              # Kinect sensor management
├── UI/
│   └── MovablePointsCanvas.cs        # Draggable points implementation
├── Models/
│   └── CalibrationPoint.cs           # Calibration point data model
└── README.md                         # This file
```

## 🎯 **Next Steps**

### **Immediate Development**
1. **Test Screen 1 functionality** with and without Kinect
2. **Implement Screen 2** (4 corner points for interactive area)
3. **Add 3D coordinate mapping** using Kinect's CoordinateMapper
4. **Implement calibration data persistence**

### **Future Enhancements**
1. **Screen 3**: Marker detection and sensor alignment
2. **Screen 4**: Touch detection testing and tuning
3. **Advanced touch detection algorithms**
4. **Multi-screen support**
5. **Calibration validation and quality metrics**

## 🤝 **Contributing**

This project demonstrates the successful migration from Unity to WPF for a utility application. The key insight is using the right tool for the job:

- **Unity**: Great for games and interactive experiences
- **WPF**: Perfect for desktop utilities and applications

The three-question logic system remains the same, but the implementation is now more reliable and maintainable.

## 📞 **Support**

For questions or issues with the WPF implementation, please refer to the project documentation or create an issue in the repository.

---

**Build Date**: 2025-08-20  
**Framework**: WPF (.NET Framework 4.8)  
**Status**: Screen 1, Screen 2/3 Alignment (robust ArUco) ✅  
**Next**: Screen 4 Touch Test 🔄
