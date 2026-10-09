using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SysFloat.UI
{
    /// <summary>
    /// Reusable premultiplied-alpha DIB used by UpdateLayeredWindow.
    /// The GDI+ bitmap wraps the DIB memory directly; presenting does not copy pixels.
    /// </summary>
    internal sealed class LayeredWidgetSurface : IDisposable
    {
        private const uint DibRgbColors = 0;
        private const uint BiRgb = 0;
        private const uint UlwAlpha = 0x00000002;
        private const byte AcSrcOver = 0;
        private const byte AcSrcAlpha = 1;
        private const long MaxSurfaceBytes = 64L * 1024 * 1024;

        private IntPtr _memoryDc;
        private IntPtr _dibHandle;
        private IntPtr _previousBitmap;
        private IntPtr _bits;
        private Bitmap _bitmap;
        private Graphics _graphics;
        private bool _disposed;
        internal long PresentCount { get; private set; }
        internal bool LastPresentSucceeded { get; private set; }

        public int Width { get; }
        public int Height { get; }
        internal Graphics DrawingGraphics => _graphics;

        public LayeredWidgetSurface(int width, int height)
        {
            long byteCount = (long)width * height * 4;
            if (width <= 0 || height <= 0 || byteCount <= 0 || byteCount > MaxSurfaceBytes)
                throw new ArgumentOutOfRangeException(nameof(width), "Layered surface dimensions are outside the safe allocation limit.");

            Width = width;
            Height = height;

            try
            {
                _memoryDc = CreateCompatibleDC(IntPtr.Zero);
                if (_memoryDc == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());

                var info = new BitmapInfo
                {
                    Header = new BitmapInfoHeader
                    {
                        Size = (uint)Marshal.SizeOf(typeof(BitmapInfoHeader)),
                        Width = width,
                        Height = -height,
                        Planes = 1,
                        BitCount = 32,
                        Compression = BiRgb,
                        SizeImage = (uint)byteCount
                    }
                };

                _dibHandle = CreateDIBSection(_memoryDc, ref info, DibRgbColors, out _bits, IntPtr.Zero, 0);
                if (_dibHandle == IntPtr.Zero || _bits == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error());

                _previousBitmap = SelectObject(_memoryDc, _dibHandle);
                if (_previousBitmap == IntPtr.Zero || _previousBitmap == new IntPtr(-1))
                    throw new Win32Exception(Marshal.GetLastWin32Error());

                _bitmap = new Bitmap(width, height, checked(width * 4), PixelFormat.Format32bppPArgb, _bits);
                _graphics = Graphics.FromImage(_bitmap);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public bool Present(IntPtr windowHandle, Point screenLocation)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(LayeredWidgetSurface));
            if (windowHandle == IntPtr.Zero || _memoryDc == IntPtr.Zero) return false;

            var destination = new NativePoint { X = screenLocation.X, Y = screenLocation.Y };
            var source = new NativePoint { X = 0, Y = 0 };
            var size = new NativeSize { Width = Width, Height = Height };
            var blend = new BlendFunction
            {
                BlendOp = AcSrcOver,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = AcSrcAlpha
            };

            // hdcDst may be NULL; Windows uses its default palette for color matching.
            _graphics.Flush(System.Drawing.Drawing2D.FlushIntention.Sync);
            LastPresentSucceeded = UpdateLayeredWindow(windowHandle, IntPtr.Zero, ref destination, ref size,
                _memoryDc, ref source, 0, ref blend, UlwAlpha);
            if (LastPresentSucceeded) PresentCount++;
            return LastPresentSucceeded;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _graphics?.Dispose();
            _graphics = null;
            _bitmap?.Dispose();
            _bitmap = null;

            if (_memoryDc != IntPtr.Zero && _previousBitmap != IntPtr.Zero && _previousBitmap != new IntPtr(-1))
            {
                SelectObject(_memoryDc, _previousBitmap);
                _previousBitmap = IntPtr.Zero;
            }
            if (_dibHandle != IntPtr.Zero)
            {
                DeleteObject(_dibHandle);
                _dibHandle = IntPtr.Zero;
            }
            if (_memoryDc != IntPtr.Zero)
            {
                DeleteDC(_memoryDc);
                _memoryDc = IntPtr.Zero;
            }
            _bits = IntPtr.Zero;
        }

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool DeleteObject(IntPtr obj);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfo bitmapInfo, uint usage,
            out IntPtr bits, IntPtr section, uint offset);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref NativePoint pptDst,
            ref NativeSize psize, IntPtr hdcSrc, ref NativePoint pptSrc, uint colorKey,
            ref BlendFunction blend, uint flags);

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfoHeader
        {
            public uint Size;
            public int Width;
            public int Height;
            public ushort Planes;
            public ushort BitCount;
            public uint Compression;
            public uint SizeImage;
            public int XPelsPerMeter;
            public int YPelsPerMeter;
            public uint ClrUsed;
            public uint ClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfo
        {
            public BitmapInfoHeader Header;
            public uint Colors;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeSize
        {
            public int Width;
            public int Height;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BlendFunction
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }
    }
}
