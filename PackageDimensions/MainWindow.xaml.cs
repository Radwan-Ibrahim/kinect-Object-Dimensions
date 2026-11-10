using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Kinect;

namespace PackageDimensions
{
    /// <summary>
    /// Main window of the Package Dimensions application.
    /// Reads depth frames from a Kinect v2 sensor, compares them with a calibrated
    /// background frame (the empty surface) and estimates the width, height and depth
    /// of the object placed inside the calibration area.
    /// </summary>
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        #region Constants

        /// <summary>
        /// Divisor that maps a depth value (0–8000 mm) into a grey-scale byte (0–255).
        /// </summary>
        private const int MapDepthToByte = 8000 / 256;

        #endregion

        #region Sensor fields

        /// <summary>
        /// Active Kinect sensor.
        /// </summary>
        private KinectSensor kinectSensor = null;

        /// <summary>
        /// Reader for depth frames.
        /// </summary>
        private DepthFrameReader depthFrameReader = null;

        /// <summary>
        /// Description of the data contained in the depth frame (512 x 424 for Kinect v2).
        /// </summary>
        private FrameDescription depthFrameDescription = null;

        #endregion

        #region Display buffers

        /// <summary>
        /// Bitmaps bound to the UI: live scene, calibrated background and object difference.
        /// </summary>
        private WriteableBitmap PackagedepthBitmap = null;
        private WriteableBitmap CalibrationdepthBitmap = null;
        private WriteableBitmap DifferencedepthBitmap = null;

        /// <summary>
        /// Grey-scale pixel buffers written into the bitmaps above.
        /// </summary>
        private byte[] CalibrationdepthPixels = null;
        private byte[] PackagedepthPixels = null;
        private byte[] DiffernecedepthPixels = null;

        #endregion

        #region Depth data and results

        /// <summary>
        /// Raw filtered depth (mm) of the current frame.
        /// </summary>
        ushort[] PackageFrameData;

        /// <summary>
        /// Raw filtered depth (mm) of the background captured during calibration.
        /// </summary>
        ushort[] CalibrationFrameData;

        /// <summary>
        /// Per-pixel height above the background (mm): background minus current depth.
        /// </summary>
        ushort[] DifferenceFrame;

        /// <summary>
        /// Measured object dimensions in centimetres.
        /// </summary>
        int PackageDepth;
        int PackageWidth;
        int PackageHeight;

        /// <summary>
        /// Current status text to display.
        /// </summary>
        private string statusText = null;

        #endregion

        #region Calibration settings

        /// <summary>
        /// Size (in depth pixels) of the centred region of interest used for measuring.
        /// </summary>
        int CalibrationWidth = 512;
        int CalibrationHeight = 424;

        /// <summary>
        /// When true, the next frame is stored as the calibration (background) frame.
        /// </summary>
        bool IsCalibrate;

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of the MainWindow class: opens the sensor,
        /// allocates the frame buffers and wires the depth frame handler.
        /// </summary>
        public MainWindow()
        {
            // get the kinectSensor object
            this.kinectSensor = KinectSensor.GetDefault();

            // open the reader for the depth frames
            this.depthFrameReader = this.kinectSensor.DepthFrameSource.OpenReader();

            // wire handler for frame arrival
            this.depthFrameReader.FrameArrived += this.Reader_FrameArrived;

            // get FrameDescription from DepthFrameSource
            this.depthFrameDescription = this.kinectSensor.DepthFrameSource.FrameDescription;

            // allocate space to put the pixels being received and converted
            this.PackagedepthPixels = new byte[this.depthFrameDescription.Width * this.depthFrameDescription.Height];
            this.CalibrationdepthPixels = new byte[this.depthFrameDescription.Width * this.depthFrameDescription.Height];
            this.DiffernecedepthPixels = new byte[this.depthFrameDescription.Width * this.depthFrameDescription.Height];

            // create the bitmap to display
            this.PackagedepthBitmap = new WriteableBitmap(this.depthFrameDescription.Width, this.depthFrameDescription.Height, 96.0, 96.0, PixelFormats.Gray8, null);
            this.CalibrationdepthBitmap = new WriteableBitmap(this.depthFrameDescription.Width, this.depthFrameDescription.Height, 96.0, 96.0, PixelFormats.Gray8, null);
            this.DifferencedepthBitmap = new WriteableBitmap(this.depthFrameDescription.Width, this.depthFrameDescription.Height, 96.0, 96.0, PixelFormats.Gray8, null);

            // set IsAvailableChanged event notifier
            this.kinectSensor.IsAvailableChanged += this.Sensor_IsAvailableChanged;

            // open the sensor
            this.kinectSensor.Open();

            // set the status text
            this.StatusText = this.kinectSensor.IsAvailable ? Properties.Resources.RunningStatusText
                                                            : Properties.Resources.NoSensorStatusText;

            // use the window object as the view model in this simple example
            this.DataContext = this;

            // initialize the components (controls) of the window
            this.InitializeComponent();
        }

        #endregion

        #region Bindable properties

        /// <summary>
        /// INotifyPropertyChanged event to allow window controls to bind to changeable data.
        /// </summary>
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// Gets the live (filtered) depth image of the scene.
        /// </summary>
        public ImageSource PackageImageSource
        {
            get
            {
                return this.PackagedepthBitmap;
            }
        }

        /// <summary>
        /// Gets the calibrated background depth image.
        /// </summary>
        public ImageSource CalibrationImageSource
        {
            get
            {
                return this.CalibrationdepthBitmap;
            }
        }

        /// <summary>
        /// Gets the difference image (object only) with the detected bounding box.
        /// </summary>
        public ImageSource DifferenceImageSource
        {
            get
            {
                return this.DifferencedepthBitmap;
            }
        }

        /// <summary>
        /// Gets or sets the current status text to display
        /// </summary>
        public string StatusText
        {
            get
            {
                return this.statusText;
            }

            set
            {
                if (this.statusText != value)
                {
                    this.statusText = value;

                    // notify any bound elements that the text has changed
                    if (this.PropertyChanged != null)
                    {
                        this.PropertyChanged(this, new PropertyChangedEventArgs("StatusText"));
                    }
                }
            }
        }

        #endregion

        #region Window lifecycle

        /// <summary>
        /// Execute shutdown tasks: release the frame reader and close the sensor.
        /// </summary>
        /// <param name="sender">object sending the event</param>
        /// <param name="e">event arguments</param>
        private void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            if (this.depthFrameReader != null)
            {
                // DepthFrameReader is IDisposable
                this.depthFrameReader.Dispose();
                this.depthFrameReader = null;
            }

            if (this.kinectSensor != null)
            {
                this.kinectSensor.Close();
                this.kinectSensor = null;
            }
        }

        #endregion

        #region Depth frame processing

        /// <summary>
        /// Handles the depth frame data arriving from the sensor (~30 fps).
        /// Validates the buffer, processes it and refreshes the UI.
        /// </summary>
        /// <param name="sender">object sending the event</param>
        /// <param name="e">event arguments</param>
        private void Reader_FrameArrived(object sender, DepthFrameArrivedEventArgs e)
        {
            bool depthFrameProcessed = false;

            using (DepthFrame depthFrame = e.FrameReference.AcquireFrame())
            {
                if (depthFrame != null)
                {
                    // the fastest way to process the body index data is to directly access 
                    // the underlying buffer
                    using (Microsoft.Kinect.KinectBuffer depthBuffer = depthFrame.LockImageBuffer())
                    {
                        // verify data and write the color data to the display bitmap
                        if (((this.depthFrameDescription.Width * this.depthFrameDescription.Height) == (depthBuffer.Size / this.depthFrameDescription.BytesPerPixel)) &&
                            (this.depthFrameDescription.Width == this.CalibrationdepthBitmap.PixelWidth) && (this.depthFrameDescription.Height == this.CalibrationdepthBitmap.PixelHeight))
                        {
                            // Note: In order to see the full range of depth (including the less reliable far field depth)
                            // we are setting maxDepth to the extreme potential depth threshold
                            ushort maxDepth = ushort.MaxValue;

                            // If you wish to filter by reliable depth distance, uncomment the following line:
                            //// maxDepth = depthFrame.DepthMaxReliableDistance

                            this.ProcessDepthFrameData(depthBuffer.UnderlyingBuffer, depthBuffer.Size, depthFrame.DepthMinReliableDistance, maxDepth);
                            depthFrameProcessed = true;
                        }
                    }
                }
            }

            if (depthFrameProcessed)
                this.RenderDepthPixels();
        }

        /// <summary>
        /// Core measurement pipeline. Reads the raw 16-bit depth buffer and either:
        /// <list type="bullet">
        /// <item><description>Calibration mode: stores the frame as the background reference.</description></item>
        /// <item><description>Measurement mode: subtracts the frame from the background, locates the
        /// object's bounding box and converts it into real-world dimensions (cm).</description></item>
        /// </list>
        /// Requires the /unsafe compiler option because it reads native memory directly.
        /// </summary>
        /// <param name="depthFrameData">Pointer to the DepthFrame image data</param>
        /// <param name="depthFrameDataSize">Size of the DepthFrame image data</param>
        /// <param name="minDepth">The minimum reliable depth value for the frame</param>
        /// <param name="maxDepth">The maximum reliable depth value for the frame</param>
        private unsafe void ProcessDepthFrameData(IntPtr depthFrameData, uint depthFrameDataSize, ushort minDepth, ushort maxDepth)
        {
            // Region of interest (ROI): a CalibrationWidth x CalibrationHeight rectangle centred in the frame
            int X1 = (depthFrameDescription.Width - CalibrationWidth) / 2;
            int X2 = depthFrameDescription.Width - X1;
            int Y1 = (depthFrameDescription.Height - CalibrationHeight) / 2;
            int Y2 = depthFrameDescription.Height - Y1;

            if (IsCalibrate || CalibrationCheckBox.IsChecked.Value)
            {
                // ===== Step 1: Calibration - capture the background (empty surface) =====

                // depth frame data is a 16 bit value
                CalibrationFrameData = new ushort[(depthFrameDataSize / this.depthFrameDescription.BytesPerPixel)]; // (ushort*)depthFrameData;

                // convert depth to a visual representation
                for (int i = 0; i < (int)(depthFrameDataSize / this.depthFrameDescription.BytesPerPixel); ++i)
                {
                    // Convert the linear buffer index into (column, row) coordinates
                    int ColumnIdex = i % depthFrameDescription.Width;
                    int RowIndex = (i - ColumnIdex) / depthFrameDescription.Width;

                    // Keep only depths inside the 0.5 m - 4 m working range and inside the ROI
                    ushort depth = ((ushort*)depthFrameData)[i];
                    if (depth < 500 || depth > 4000 || ColumnIdex < X1 || ColumnIdex > X2 || RowIndex < Y1 || RowIndex > Y2)
                        depth = 0;

                    CalibrationFrameData[i] = depth;

                    // To convert to a byte, we're mapping the depth value to the byte range.
                    // Values outside the reliable depth range are mapped to 0 (black).
                    this.CalibrationdepthPixels[i] = (byte)(depth >= minDepth && depth <= maxDepth ? (depth / MapDepthToByte) : 0);
                }

                // One-shot calibration done (the check box keeps calibrating continuously)
                IsCalibrate = false;
            }
            else
            {
                // ===== Step 2: Measurement - subtract the current frame from the background =====

                // depth frame data is a 16 bit value
                PackageFrameData = new ushort[(depthFrameDataSize / this.depthFrameDescription.BytesPerPixel)];
                DifferenceFrame = new ushort[(depthFrameDataSize / this.depthFrameDescription.BytesPerPixel)];

                // convert depth to a visual representation
                for (int i = 0; i < (int)(depthFrameDataSize / this.depthFrameDescription.BytesPerPixel); ++i)
                {
                    // Convert the linear buffer index into (column, row) coordinates
                    int ColumnIdex = i % depthFrameDescription.Width;
                    int RowIndex = (i - ColumnIdex) / depthFrameDescription.Width;

                    // Keep only depths inside the 0.5 m - 4 m working range and inside the ROI
                    ushort depth = ((ushort*)depthFrameData)[i];
                    if (depth < 500 || depth > 4000 || ColumnIdex < X1 || ColumnIdex > X2 || RowIndex < Y1 || RowIndex > Y2)
                        depth = 0;
                    PackageFrameData[i] = depth;

                    // To convert to a byte, we're mapping the depth value to the byte range.
                    // Values outside the reliable depth range are mapped to 0 (black).
                    this.PackagedepthPixels[i] = (byte)(depth >= minDepth && depth <= maxDepth ? (depth / MapDepthToByte) : 0);

                    // Difference = how far (mm) this pixel rises above the calibrated background
                    if (PackageFrameData != null && CalibrationFrameData != null)
                    {
                        int Difference;
                        if (CalibrationFrameData[i] < 500 || PackageFrameData[i] < 500 || CalibrationFrameData[i] > 4000 || PackageFrameData[i] > 4000 || ColumnIdex < X1 || ColumnIdex > X2 || RowIndex < Y1 || RowIndex > Y2)
                            Difference = 0;
                        else
                            Difference = (CalibrationFrameData[i] - PackageFrameData[i]);

                        // remove difference smaller than 100 mm (sensor noise)
                        Difference = (ushort)((Difference > 100) ? Difference : 0);
                        // var LayerDifference = (ushort)(Difference + Difference % 10);
                        DifferenceFrame[i] = (ushort)Difference;

                        // Amplify x10 for visibility and clamp to the byte range
                        int differencePixel = (Difference / MapDepthToByte) * 10;
                        this.DiffernecedepthPixels[i] = (byte)((differencePixel > 255) ? 255 : differencePixel);
                    }
                }

                if (PackageFrameData != null && CalibrationFrameData != null)
                {
                    // ===== Step 3: Bounding box - first rows/columns with more than 10 object pixels =====
                    int TopY = 0;
                    int DownY = 0;
                    int RightX = 0;
                    int LeftX = 0;

                    // Scan rows from the top and bottom edges simultaneously
                    for (int RowIndex = 0; RowIndex < depthFrameDescription.Height; RowIndex++)
                    {
                        int Topcount = 0;
                        int DownCount = 0;
                        for (int ColumnIndex = 0; ColumnIndex < depthFrameDescription.Width; ColumnIndex++)
                        {
                            var TopIndex = ColumnIndex + (RowIndex * depthFrameDescription.Width);
                            var DownIndex = ColumnIndex + (((depthFrameDescription.Height - RowIndex) - 1) * depthFrameDescription.Width);

                            if (DifferenceFrame[TopIndex] > 0 && TopY == 0)
                                Topcount++;

                            if (DifferenceFrame[DownIndex] > 0 && DownY == 0)
                                DownCount++;
                        }

                        if (Topcount > 10 && TopY == 0)
                        {
                            TopY = RowIndex;
                            Topcount = 0;
                        }
                        if (DownCount > 10 && DownY == 0)
                        {
                            DownY = depthFrameDescription.Height - RowIndex - 1;
                            DownCount = 0;
                        }
                        if (DownY > 0 && TopY > 0)
                            break;
                    }

                    // Scan columns from the left and right edges simultaneously
                    for (int ColumnIndex = 0; ColumnIndex < depthFrameDescription.Width; ColumnIndex++)
                    {
                        int Leftcount = 0;
                        int RightCount = 0;
                        for (int RowIndex = 0; RowIndex < depthFrameDescription.Height; RowIndex++)
                        {
                            var LeftIndex = ColumnIndex + (RowIndex * depthFrameDescription.Width);
                            var RightIndex = (depthFrameDescription.Width - ColumnIndex - 1) + (RowIndex * depthFrameDescription.Width);

                            if (DifferenceFrame[LeftIndex] > 0 && LeftX == 0)
                                Leftcount++;

                            if (DifferenceFrame[RightIndex] > 0 && RightX == 0)
                                RightCount++;
                        }

                        if (Leftcount > 10 && LeftX == 0)
                        {
                            LeftX = ColumnIndex;
                            Leftcount = 0;
                        }
                        if (RightCount > 10 && RightX == 0)
                        {
                            RightX = depthFrameDescription.Width - ColumnIndex - 1;
                            RightCount = 0;
                        }
                        if (LeftX > 0 && RightX > 0)
                            break;
                    }

                    // ===== Step 4: Draw the bounding box (white) on the difference image =====
                    for (int ColumnIndex = LeftX; ColumnIndex < RightX; ColumnIndex++)
                    {
                        var TopRowIndex = ColumnIndex + (TopY * depthFrameDescription.Width);
                        var DownRowIndex = ColumnIndex + (DownY * depthFrameDescription.Width);
                        this.DiffernecedepthPixels[TopRowIndex] = 255;
                        this.DiffernecedepthPixels[DownRowIndex] = 255;
                    }

                    for (int RowIndex = TopY; RowIndex < DownY; RowIndex++)
                    {
                        var LeftIndex = LeftX + (RowIndex * depthFrameDescription.Width);
                        var RightIndex = RightX + (RowIndex * depthFrameDescription.Width);
                        this.DiffernecedepthPixels[LeftIndex] = 255;
                        this.DiffernecedepthPixels[RightIndex] = 255;
                    }

                    // ===== Step 5: Histograms inside the bounding box =====

                    // Histogram of object heights above the background (mm -> pixel count)
                    SortedDictionary<ushort, int> DifferentDepthWeight = new SortedDictionary<ushort, int>();
                    for (int RowIndex = TopY; RowIndex < DownY; RowIndex++)
                    {
                        for (int ColumnIndex = LeftX; ColumnIndex < RightX; ColumnIndex++)
                        {
                            var Index = ColumnIndex + (RowIndex * depthFrameDescription.Width);

                            if (DifferentDepthWeight.ContainsKey(DifferenceFrame[Index]))
                                DifferentDepthWeight[DifferenceFrame[Index]]++;
                            else
                                DifferentDepthWeight.Add(DifferenceFrame[Index], 1);
                        }
                    }

                    // Histogram of camera-to-surface distances, ignoring filtered (zero) pixels
                    SortedDictionary<ushort, int> DepthWeight = new SortedDictionary<ushort, int>();
                    for (int RowIndex = TopY; RowIndex < DownY; RowIndex++)
                    {
                        for (int ColumnIndex = LeftX; ColumnIndex < RightX; ColumnIndex++)
                        {
                            var Index = ColumnIndex + (RowIndex * depthFrameDescription.Width);

                            if (DepthWeight.ContainsKey(PackageFrameData[Index]))
                                DepthWeight[PackageFrameData[Index]]++;
                            else if (PackageFrameData[Index] != 0)
                                DepthWeight.Add(PackageFrameData[Index], 1);
                        }
                    }
                    // Most frequent distance = camera to the object's top surface (mm)
                    int DepthFromCamera = 0;
                    if (DepthWeight.Count > 0)
                        DepthFromCamera = DepthWeight.OrderBy(p => p.Value).Last().Key;

                    // ===== Step 6: Convert pixels to real-world dimensions (cm) =====
                    // Kinect has a depth image resolution of 512 x 424 pixels with a fov of 70.6 x 60 degrees resulting in an average of about 7 x 7 pixels per degree
                    // Real size = 2 * D * tan(FOV / 2) * (pixel span / image size)

                    // Depth: the largest height above background seen by more than 4 pixels
                    PackageDepth = (DifferentDepthWeight.LastOrDefault(p => p.Value > 4).Key) / 10;
                    PackageWidth = (int)(((2 * DepthFromCamera * Math.Tan(35.3 * Math.PI / 180) * (RightX - LeftX)) / depthFrameDescription.Width) / 10);
                    PackageHeight = (int)(((2 * DepthFromCamera * Math.Abs(Math.Tan(30 * Math.PI / 180)) * (DownY - TopY)) / depthFrameDescription.Height) / 10);
                }
            }
        }

        #endregion

        #region Rendering

        /// <summary>
        /// Writes the pixel buffers into the bound bitmaps and updates the dimension labels.
        /// </summary>
        private void RenderDepthPixels()
        {
            if (PackageFrameData != null)
            {
                this.PackagedepthBitmap.WritePixels(
                    new Int32Rect(0, 0, this.PackagedepthBitmap.PixelWidth, this.PackagedepthBitmap.PixelHeight),
                    PackagedepthPixels,
                    this.PackagedepthBitmap.PixelWidth,
                    0);

                this.DifferencedepthBitmap.WritePixels(
                  new Int32Rect(0, 0, this.DifferencedepthBitmap.PixelWidth, this.DifferencedepthBitmap.PixelHeight),
                  DiffernecedepthPixels,
                  this.DifferencedepthBitmap.PixelWidth,
                  0);

                Depth.Text = PackageDepth.ToString();
                Width.Text = PackageWidth.ToString();
                Height.Text = PackageHeight.ToString();
            }

            if (CalibrationFrameData != null)
            {
                this.CalibrationdepthBitmap.WritePixels(
                   new Int32Rect(0, 0, this.CalibrationdepthBitmap.PixelWidth, this.CalibrationdepthBitmap.PixelHeight),
                   CalibrationdepthPixels,
                   this.CalibrationdepthBitmap.PixelWidth,
                   0);
            }
        }

        #endregion

        #region Sensor and calibration UI handlers

        /// <summary>
        /// Handles the event which the sensor becomes unavailable (E.g. paused, closed, unplugged).
        /// </summary>
        /// <param name="sender">object sending the event</param>
        /// <param name="e">event arguments</param>
        private void Sensor_IsAvailableChanged(object sender, IsAvailableChangedEventArgs e)
        {
            // on failure, set the status text
            this.StatusText = this.kinectSensor.IsAvailable ? Properties.Resources.RunningStatusText
                                                            : Properties.Resources.SensorNotAvailableStatusText;
        }

        /// <summary>
        /// Resizes the ROI width and the white rectangle overlay on the calibration image.
        /// </summary>
        private void CalibrationWidth_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (CalibrationImage == null)
                return;
            CalibrationWidth = (int)e.NewValue;
            int RectWidth = (int)(CalibrationWidth * CalibrationImage.ActualWidth) / depthFrameDescription.Width;
            int Left = (int)(CalibrationImage.ActualWidth - RectWidth) / 2;
            int Right = Left;
            CalibrationRect.Margin = new Thickness(Left, CalibrationRect.Margin.Top, Right, CalibrationRect.Margin.Bottom);
        }

        /// <summary>
        /// Resizes the ROI height and the white rectangle overlay on the calibration image.
        /// </summary>
        private void CalibrationHeightChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (CalibrationImage == null)
                return;
            CalibrationHeight = (int)e.NewValue;
            int RectHeight = (int)(CalibrationHeight * CalibrationImage.ActualHeight) / depthFrameDescription.Height;
            int Top = (int)(CalibrationImage.ActualHeight - RectHeight) / 2;
            int Bottom = Top;
            CalibrationRect.Margin = new Thickness(CalibrationRect.Margin.Left, Top, CalibrationRect.Margin.Right, Bottom);
        }

        /// <summary>
        /// Requests a one-shot calibration using the next depth frame.
        /// </summary>
        private void Calibrate(object sender, RoutedEventArgs e)
        {
            IsCalibrate = true;
        }

        #endregion
    }
}
