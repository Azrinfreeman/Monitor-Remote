using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

static class DesktopCaptureWorker {
    public static async Task Run() {
        if (!OperatingSystem.IsWindows()) return;
        DesktopDisplayCatalog.InitializeDpi();
        var name = Environment.GetEnvironmentVariable("MONITOR_CAPTURE_PIPE");
        var secret = Environment.GetEnvironmentVariable("MONITOR_CAPTURE_SECRET");
        if (name == null || secret == null) return;
        try {
            using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var startup = new CancellationTokenSource(5000);
            await pipe.ConnectAsync(startup.Token);
            while (true) {
                var request = await ManagementProtocol.Read<CaptureRequest>(pipe, CancellationToken.None);
                if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(request.Secret))) return;
                var frame = Capture(request);
                await RemoteProtocol.WritePacket(pipe, RemoteProtocol.Encode(frame), CancellationToken.None);
            }
        } catch (Exception e) when (e is IOException or OperationCanceledException or System.Text.Json.JsonException or InvalidOperationException or System.ComponentModel.Win32Exception) {
            // The parent reports capture failure. Never log desktop content or credentials.
        }
    }
    [SupportedOSPlatform("windows")]
    static CapturedFrame Capture(CaptureRequest request) {
        if (!DesktopDisplayCatalog.Interactive()) throw new InvalidOperationException("Windows desktop unavailable.");
        var snapshot = DesktopDisplayCatalog.Snapshot();
        var display = snapshot.Displays.FirstOrDefault(d => d.Id == request.DisplayId);
        if (display == null || snapshot.Revision != request.Revision) throw new InvalidOperationException("Display layout changed.");
        var (maxSize, quality) = request.Quality switch { "low" => (960, 55L), "clear" => (1600, 85L), "balanced" => (1280, 70L), _ => throw new InvalidOperationException("Invalid quality.") };
        // Bound allocations even if a driver reports unusual dimensions.
        if (display.Width > 16384 || display.Height > 16384 || (long)display.Width * display.Height > 64000000) throw new InvalidOperationException("Display dimensions exceed capture limits.");
        using var native = new Bitmap(display.Width, display.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(native)) {
            if (DesktopDisplayCatalog.Demo) {
                graphics.Clear(Color.FromArgb(16, 24, 39));
                using var font = new Font("Segoe UI", 28);
                using var small = new Font("Segoe UI", 16);
                graphics.FillRectangle(Brushes.SteelBlue, 36, 36, display.Width - 72, 10);
                graphics.DrawString("Monitor Remote · Demo desktop", font, Brushes.White, 36, 72);
                graphics.DrawString("Synthetic screen — no PC capture or real mouse/keyboard input", small, Brushes.LightSkyBlue, 36, 140);
                graphics.DrawString(DateTime.Now.ToString("HH:mm:ss"), font, Brushes.LightGreen, 36, 205);
                graphics.DrawRectangle(Pens.LightSkyBlue, 36, 300, Math.Min(640, display.Width - 72), 220);
                graphics.DrawString("Try the touchpad, direct touch and keyboard.", small, Brushes.White, 60, 330);
            } else graphics.CopyFromScreen(display.X, display.Y, 0, 0, native.Size, CopyPixelOperation.SourceCopy);
        }
        var scale = Math.Min(1d, (double)maxSize / Math.Max(display.Width, display.Height));
        var width = Math.Max(1, (int)Math.Round(display.Width * scale));
        var height = Math.Max(1, (int)Math.Round(display.Height * scale));
        using var scaled = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(scaled)) {
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            graphics.DrawImage(native, 0, 0, width, height);
        }
        using var stream = new MemoryStream();
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
        scaled.Save(stream, ImageCodecInfo.GetImageEncoders().Single(c => c.FormatID == ImageFormat.Jpeg.Guid), parameters);
        var cursor = new Point(display.X + display.Width / 2, display.Y + display.Height / 2);
        var visible = DesktopDisplayCatalog.Demo;
        if (!DesktopDisplayCatalog.Demo) { visible = GetCursorPos(out cursor) && cursor.X >= display.X && cursor.Y >= display.Y && cursor.X < display.X + display.Width && cursor.Y < display.Y + display.Height; }
        return new(new(1, 0, display.Id, snapshot.Revision, display.X, display.Y, display.Width, display.Height, width, height, cursor.X, cursor.Y, visible), stream.ToArray());
    }
    [DllImport("user32.dll")] static extern bool GetCursorPos(out Point point);
}
