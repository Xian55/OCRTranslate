using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Runtime.InteropServices;
using System.Text;
using Tesseract;

// Notes:
//  - The Canvas must have a non-null background to make it generate mouse events.

namespace Protonox
{
    /// <summary>
    /// Global hotkeys
    /// </summary>
    internal static class Win32
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("kernel32", SetLastError = true)]
        public static extern short GlobalAddAtom(string lpString);

        [DllImport("kernel32", SetLastError = true)]
        public static extern short GlobalDeleteAtom(short nAtom);

        public const int MOD_ALT = 1;
        public const int MOD_CONTROL = 2;
        public const int MOD_SHIFT = 4;
        public const int MOD_WIN = 8;

        public const uint VK_KEY_C = 0x43;
        public const uint VK_SPACE = 0x20;

        public const int WM_HOTKEY = 0x312;
    }

    /// <summary>
    /// Get an area of the screen as a Bitmap
    /// </summary>
    public static class ScreenShot
    {
        public static System.Drawing.Bitmap GetBitmap(System.Drawing.Point SourcePoint, System.Drawing.Point DestinationPoint, System.Drawing.Rectangle SelectionRectangle)
        {
            System.Drawing.Bitmap bitmap = new System.Drawing.Bitmap(SelectionRectangle.Width, SelectionRectangle.Height);
            System.Drawing.Graphics graphics = System.Drawing.Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(SourcePoint, DestinationPoint, SelectionRectangle.Size);

            return bitmap;
        }
    }

    /// <summary>
    /// Interaction logic for Window1.xaml
    /// http://csharphelper.com/blog/2014/12/let-user-move-resize-rectangle-wpf-c/
    /// </summary>
    public partial class Window1 : Window, INotifyPropertyChanged
    {
        #region Variables

        // Represenet the application status (max/min)
        private bool overlayStatus = true;

        private Point _minRectSize = new Point(5, 5);

        private readonly double _screenWidth = SystemParameters.PrimaryScreenWidth;
        private readonly double _screenHeight = SystemParameters.PrimaryScreenHeight;

        private Point _point;
        public Point RectPoint
        {
            get { return this._point; }
            private set
            {
                this._point = value;
                this.OnPropertyChanged("RectPoint");
            }
        }

        public double AreaWidth { get; private set; }
        public double AreaHeight { get; private set; }

        // True if a drag is in progress.
        private bool _isDragging = false;
        private bool _isRectDrawn = false;
        private bool _isLeftClickDown = false;

        // The drag's last point.
        private Point _lastMousePoint;

        // The part of the rectangle under the mouse.
        private HitType _mouseHitType = HitType.None;


        // Hotkeys
        private HwndSource hk_hWndSource;
        private short hk_atom;

        private Thread _workerThread;

        const string sourceLang = "en";
        const string targetLang = "hu";

        #endregion


        public Window1()
        {
            InitializeComponent();
            this.DataContext = this;

            this.PreviewKeyDown += new KeyEventHandler(HandleKeyDown_window);
            ResetRectangle();
        }

        public void SetOverlayStatus(bool status)
        {
            if (status)
                WindowState = WindowState.Maximized;
            else
                WindowState = WindowState.Minimized;

            overlayStatus = status;
        }


        #region Interface Inotify

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #endregion


        #region Rectangle Editor

        // The part of the rectangle the mouse is over.
        private enum HitType
        {
            None, Body, UL, UR, LR, LL, L, R, T, B
        };

        // Return a HitType value to indicate what is at the point.
        private HitType SetHitType( Point point )
        {
            double left = Canvas.GetLeft( RecodBorderSizeObject );
            double top = Canvas.GetTop( RecodBorderSizeObject );
            double right = left + RecodBorderSizeObject.Width;
            double bottom = top + RecodBorderSizeObject.Height;
            if ( point.X < left ) return HitType.None;
            if ( point.X > right ) return HitType.None;
            if ( point.Y < top ) return HitType.None;
            if ( point.Y > bottom ) return HitType.None;

            const double GAP = 10;
            if ( point.X - left < GAP )
            {
                // Left edge.
                if ( point.Y - top < GAP ) return HitType.UL;
                if ( bottom - point.Y < GAP ) return HitType.LL;
                return HitType.L;
            }
            if ( right - point.X < GAP )
            {
                // Right edge.
                if ( point.Y - top < GAP ) return HitType.UR;
                if ( bottom - point.Y < GAP ) return HitType.LR;
                return HitType.R;
            }
            if ( point.Y - top < GAP ) return HitType.T;
            if ( bottom - point.Y < GAP ) return HitType.B;
            return HitType.Body;
        }

        // Set a mouse cursor appropriate for the current hit type.
        private void SetMouseCursor()
        {
            // See what cursor we should display.
            Cursor desired_cursor = Cursors.Arrow;
            switch ( _mouseHitType )
            {
                case HitType.None:
                    desired_cursor = Cursors.Arrow;
                    break;
                case HitType.Body:
                    desired_cursor = Cursors.ScrollAll;
                    break;
                case HitType.UL:
                case HitType.LR:
                    desired_cursor = Cursors.SizeNWSE;
                    break;
                case HitType.LL:
                case HitType.UR:
                    desired_cursor = Cursors.SizeNESW;
                    break;
                case HitType.T:
                case HitType.B:
                    desired_cursor = Cursors.SizeNS;
                    break;
                case HitType.L:
                case HitType.R:
                    desired_cursor = Cursors.SizeWE;
                    break;
            }

            // Display the desired cursor.
            if ( Cursor != desired_cursor ) Cursor = desired_cursor;
        }

        private void ResetRectangle()
        {
            Canvas.SetLeft(RecodBorderSizeObject, 0);
            Canvas.SetTop(RecodBorderSizeObject, 0);

            RecodBorderSizeObject.Width = 0;
            RecodBorderSizeObject.Height = 0;

            this.RectPoint = new Point(0, 0);
            this.AreaWidth = 0;
            this.AreaHeight = 0;

            Cursor = Cursors.Arrow;
        }

        private bool IsValidRectangle()
        {
            return (AreaWidth > 0 && AreaHeight > 0);
        }

        #endregion


        #region Global Keybinding

        private void RegisterHotkeys()
        {
            WindowInteropHelper wih = new WindowInteropHelper(this);
            hk_hWndSource = HwndSource.FromHwnd(wih.Handle);
            hk_hWndSource.AddHook(MainWindowProc);

            // create an atom for registering the hotkey
            hk_atom = Win32.GlobalAddAtom("ProtonoxTrans");

            if (hk_atom == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());

            // register the CTRL+SHIFT+Space hotkey
            //if (!Win32.RegisterHotKey(wih.Handle, atom, Win32.MOD_CONTROL | Win32.MOD_SHIFT, Win32.VK_SPACE))
            if (!Win32.RegisterHotKey(wih.Handle, hk_atom, Win32.MOD_CONTROL, Win32.VK_SPACE))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        private void ReleaseHotkeys()
        {
            if (hk_atom != 0)
                Win32.UnregisterHotKey(hk_hWndSource.Handle, hk_atom);
        }

        private IntPtr MainWindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            switch (msg)
            {
                case Win32.WM_HOTKEY:
                    SetOverlayStatus(!overlayStatus);
                    handled = true;
                    break;
            }

            return IntPtr.Zero;
        }

        #endregion


        #region Tesseract Commands

        private void StartWorkerThread()
        {
            if (_workerThread != null)
                _workerThread.Abort();

            int th = (int)RecodBorderSizeObject.BorderThickness.Left;

            _workerThread = new Thread(ExtractTextFromBitmap);
            _workerThread.Start(th);
        }

        public void ExtractTextFromBitmap(object pth)
        {
            int th = Convert.ToInt32(pth);

            int x = (int)RectPoint.X;
            int y = (int)RectPoint.Y;
            int w = (int)AreaWidth;
            int h = (int)AreaHeight;

            System.Drawing.Point startPoint = new System.Drawing.Point(x + th, y + th);
            System.Drawing.Rectangle rectangle = new System.Drawing.Rectangle(startPoint.X, startPoint.Y, w - 2*th, h - 2*th);

            System.Drawing.Bitmap bitmap = ScreenShot.GetBitmap(startPoint, System.Drawing.Point.Empty, rectangle);
            //bitmap.Save("‪text.png", System.Drawing.Imaging.ImageFormat.Png);

            var ocr = new TesseractEngine(@".\tessdata", "eng", EngineMode.TesseractAndCube);
            var page = ocr.Process(bitmap);

            string recText = page.GetText();
            ProcessText(ref recText);

            ExecuteNodeJS(sourceLang, targetLang, recText);
        }


        private void ProcessText(ref string recText)
        {
            //text = text.Replace(". *", " ");
            recText = recText.Replace("L ", "1. ");
            recText = recText.Replace("\nl. ", "\n1. ");
            recText = recText.Replace("\n1.", "\n\n1.");
            recText = recText.Replace("*", " ");
            //text = text.Replace(". \n", ". ");
            recText = recText.Replace('[', ' ');
            recText = recText.Replace(']', ' ');
            recText = recText.Replace('»', '.');
            recText = recText.Replace("\n\n", "\n");
            recText = recText.Replace("  ", " ");
            //text = text.Replace(" \n", "\n");

            recText = recText.Trim();

            /*
            string pattern = "([.][*])|([!][*])|([?][*])";
            string replacement = "\n";
            Regex rgx = new Regex(pattern);
            string result = rgx.Replace(text, replacement);
            */
        }

        //Update GUI element from an another Thread
        public delegate void UpdateTextCallback(string message);
        private void UpdateOuputText(string message)
        {
            outputText.Content = message;
        }

        #endregion


        private void ExecuteNodeJS(string srcLang, string targetLang, string text)
        {
            try
            {
                Process process = new Process();
                process.StartInfo.FileName = @"C:\Program Files\nodejs\node.exe";
                string argument = string.Format("main.js --srclang=\"{0}\" --targetlang=\"{1}\" --text=\"{2}\"", srcLang, targetLang, text);
                process.StartInfo.Arguments = @argument;

                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;

                process.StartInfo.UseShellExecute = false;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;

                process.StartInfo.StandardOutputEncoding = Encoding.UTF8;
                process.OutputDataReceived += new DataReceivedEventHandler(OutputHandler);
                process.ErrorDataReceived += new DataReceivedEventHandler(OutputHandler);

                _stdOutput = string.Empty;

                process.Start();

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                process.WaitForExit();

                outputText.Dispatcher.Invoke(new UpdateTextCallback(this.UpdateOuputText), new object[] { _stdOutput.Trim() });
            }
            catch(Exception exception)
            {
                _stdOutput = "NodeJs not installed!";
                outputText.Dispatcher.Invoke(new UpdateTextCallback(this.UpdateOuputText), new object[] { _stdOutput.Trim() + "\n" + exception.Message });
            }

            //Stop the Thread
            _workerThread.Abort();
            _workerThread = null;
        }

        private static string _stdOutput;
        private static void OutputHandler(object sendingProcess, DataReceivedEventArgs outLine)
        {
            _stdOutput += outLine.Data + "\n";
        }


        #region Window Events

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            RegisterHotkeys();
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            ReleaseHotkeys();
        }

        private void HandleOnResize(object sender, SizeChangedEventArgs e)
        {
            if (WindowState == WindowState.Maximized)
                overlayStatus = true;
            else
                overlayStatus = false;
        }

        #endregion


        #region Keyboard & Mouse Events

        private void HandleKeyDown_window(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
            else if (e.Key == Key.D1)
            {
                outputText.SetValue(Grid.RowProperty, 0);
                outputText.SetValue(Label.VerticalAlignmentProperty, VerticalAlignment.Top);
                outputText.SetValue(Grid.RowSpanProperty, 1);
            }
            else if (e.Key == Key.D2)
            {
                outputText.SetValue(Grid.RowProperty, 1);
                outputText.SetValue(Label.VerticalAlignmentProperty, VerticalAlignment.Bottom);
                outputText.SetValue(Grid.RowSpanProperty, 1);
            }
            else if (e.Key == Key.D3)
            {
                outputText.SetValue(Grid.RowProperty, 2);
                outputText.SetValue(Label.VerticalAlignmentProperty, VerticalAlignment.Bottom);
                outputText.SetValue(Grid.RowSpanProperty, 1);
            }
        }

        private void HandleMouseDown_canvas1(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                _isLeftClickDown = true;

                if (IsValidRectangle() && e.ClickCount == 2)
                {
                    StartWorkerThread();
                    return;
                }

                _mouseHitType = SetHitType(Mouse.GetPosition(CanvasMain));
                SetMouseCursor();
                if (_isRectDrawn && _mouseHitType == HitType.None)
                {
                    ResetRectangle();
                    _isRectDrawn = false;
                }

                _lastMousePoint = Mouse.GetPosition(CanvasMain);

                if (_isRectDrawn)
                    _isDragging = true;
                else
                {
                    //Set rect top left corner to cursor 
                    this.RectPoint = new Point(_lastMousePoint.X, _lastMousePoint.Y);

                    Canvas.SetLeft(RecodBorderSizeObject, _lastMousePoint.X);
                    Canvas.SetTop(RecodBorderSizeObject, _lastMousePoint.Y);
                }
            }
            else if (e.ChangedButton == MouseButton.Right)
            {
                SetOverlayStatus(false);
            }
        }

        private void HandleMouseUp_canvas1(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                _isDragging = false;
                _isLeftClickDown = false;

                if (IsValidRectangle())
                    _isRectDrawn = true;

                SetMouseCursor();
            }
        }

        private void HandleMouseMove_canvas1(object sender, MouseEventArgs e)
        {
            if (!_isLeftClickDown)
            {
                _mouseHitType = SetHitType(Mouse.GetPosition(CanvasMain));
                SetMouseCursor();
                return;
            }

            if (!_isRectDrawn)
            {
                // See how much the mouse has moved.
                Point mousePoint = Mouse.GetPosition(CanvasMain);
                double deltaX = mousePoint.X - _lastMousePoint.X;
                double deltaY = mousePoint.Y - _lastMousePoint.Y;

                if (deltaX > _minRectSize.X && deltaY > _minRectSize.Y)
                {
                    RecodBorderSizeObject.Width = deltaX;
                    RecodBorderSizeObject.Height = deltaY;

                    this.AreaWidth = deltaX;
                    this.AreaHeight = deltaY;
                }

                _mouseHitType = SetHitType(Mouse.GetPosition(CanvasMain));
                SetMouseCursor();

                return;
            }

            if (!_isDragging)
            {
                _mouseHitType = SetHitType(Mouse.GetPosition(CanvasMain));
                SetMouseCursor();
            }
            else
            {
                // See how much the mouse has moved.
                Point mousePoint = Mouse.GetPosition(CanvasMain);
                double deltaX = mousePoint.X - _lastMousePoint.X;
                double deltaY = mousePoint.Y - _lastMousePoint.Y;

                // Get the rectangle's current position.
                double newX = Canvas.GetLeft(RecodBorderSizeObject);
                double newY = Canvas.GetTop(RecodBorderSizeObject);
                double newWidth = RecodBorderSizeObject.Width;
                double newHeight = RecodBorderSizeObject.Height;

                // Bound checks
                if (newX <= 0)
                    newX = 0;

                if (newY <= 0)
                    newY = 0;

                if (newWidth > _screenWidth)
                    newWidth = _screenWidth;

                if (newHeight > _screenHeight)
                    newHeight = _screenHeight;

                if (newX + newWidth >= _screenWidth)
                    newX -= newX + newWidth - _screenWidth;

                if (newY + newHeight >= _screenHeight)
                    newY -= newY + newHeight - _screenHeight;

                // Update the rectangle.
                switch (_mouseHitType)
                {
                    case HitType.Body:
                        newX += deltaX;
                        newY += deltaY;
                        break;
                    case HitType.UL:
                        newX += deltaX;
                        newY += deltaY;
                        newWidth -= deltaX;
                        newHeight -= deltaY;
                        break;
                    case HitType.UR:
                        newY += deltaY;
                        newWidth += deltaX;
                        newHeight -= deltaY;
                        break;
                    case HitType.LR:
                        newWidth += deltaX;
                        newHeight += deltaY;
                        break;
                    case HitType.LL:
                        newX += deltaX;
                        newWidth -= deltaX;
                        newHeight += deltaY;
                        break;
                    case HitType.L:
                        newX += deltaX;
                        newWidth -= deltaX;
                        break;
                    case HitType.R:
                        newWidth += deltaX;
                        break;
                    case HitType.B:
                        newHeight += deltaY;
                        break;
                    case HitType.T:
                        newY += deltaY;
                        newHeight -= deltaY;
                        break;
                }

                // Don't use negative width or height.
                if ((newWidth > _minRectSize.X) && (newHeight > _minRectSize.Y))
                {
                    // Update the rectangle.
                    Canvas.SetLeft(RecodBorderSizeObject, newX);
                    Canvas.SetTop(RecodBorderSizeObject, newY);

                    RecodBorderSizeObject.Width = newWidth;
                    RecodBorderSizeObject.Height = newHeight;

                    this.RectPoint = new Point(newX, newY);
                    this.AreaWidth = newWidth;
                    this.AreaHeight = newHeight;

                    // Save the mouse's new location.
                    _lastMousePoint = mousePoint;
                }
            }
        }


        #endregion


        #region Button Click Events

        private void HandleOnClick_ButtonExit(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        private void HandleOnClick_ButtonTrans(object sender, RoutedEventArgs e)
        {
            if (IsValidRectangle())
                StartWorkerThread();
        }

        private void HandleOnClick_ButtonToggle(object sender, RoutedEventArgs e)
        {
            SetOverlayStatus(!overlayStatus);
        }

        #endregion

    }
}
