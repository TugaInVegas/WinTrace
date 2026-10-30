using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace WinTrace.Appium
{
    /// <summary>
    /// Makes screenshots smaller before they are stored: scale down to a maximum width and/or re-encode as JPEG.
    /// Set it on <see cref="AppiumTraceSource.Compression"/>. Runs on Windows; elsewhere screenshots are kept as they are.
    /// </summary>
    public sealed class ScreenshotCompression
    {
        ScreenshotCompression(bool jpeg, int quality, int? maxWidth)
        {
            if (quality < 1 || quality > 100) throw new ArgumentOutOfRangeException(nameof(quality), "Use 1-100.");
            if (maxWidth is int w && w < 16) throw new ArgumentOutOfRangeException(nameof(maxWidth), "Use at least 16 pixels.");
            Jpeg = jpeg;
            Quality = quality;
            MaxWidth = maxWidth;
        }

        public bool Jpeg { get; }
        public int Quality { get; }
        public int? MaxWidth { get; }

        /// <summary>JPEG at <paramref name="quality"/> (1-100), scaled down to <paramref name="maxWidth"/> pixels if wider.</summary>
        public static ScreenshotCompression ToJpeg(int quality = 80, int? maxWidth = 1600) => new ScreenshotCompression(true, quality, maxWidth);

        /// <summary>Keep PNG (sharp text, often small for UI), only scaled down to <paramref name="maxWidth"/> pixels if wider.</summary>
        public static ScreenshotCompression ScaleDown(int maxWidth) => new ScreenshotCompression(false, 100, maxWidth);

        /// <summary>Returns the compressed image, or <paramref name="png"/> unchanged when it cannot be processed.</summary>
        public byte[] Apply(byte[] png)
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) return png;
            try { return ApplyOnWindows(png); }
            catch (Exception ex) when (ex is ArgumentException || ex is ExternalException || ex is OutOfMemoryException || ex is TypeInitializationException)
            {
                return png;
            }
        }

        [SupportedOSPlatform("windows6.1")]
        byte[] ApplyOnWindows(byte[] png)
        {
            using var input = new MemoryStream(png);
            using var source = Image.FromStream(input);
            var scale = MaxWidth is int max && source.Width > max ? (double)max / source.Width : 1.0;
            if (scale == 1.0 && !Jpeg) return png;

            using var target = new Bitmap(Math.Max(1, (int)Math.Round(source.Width * scale)), Math.Max(1, (int)Math.Round(source.Height * scale)));
            using (var g = Graphics.FromImage(target))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(source, 0, 0, target.Width, target.Height);
            }

            using var output = new MemoryStream();
            if (Jpeg)
            {
                var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                using var parameters = new EncoderParameters(1);
                parameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)Quality);
                target.Save(output, codec, parameters);
            }
            else target.Save(output, ImageFormat.Png);
            // Never make things worse: keep the original when re-encoding does not help.
            return output.Length < png.Length ? output.ToArray() : png;
        }
    }
}
