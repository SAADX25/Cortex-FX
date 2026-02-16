using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace CortexFX
{
    public class WatermarkProcessor
    {
        private readonly string _ffmpegPath;

        public WatermarkProcessor()
        {
            // Relative path to ffmpeg.exe (Resources/ffmpeg.exe)
            string resourcesPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources");
            _ffmpegPath = Path.Combine(resourcesPath, "ffmpeg.exe");
        }

        public async Task RemoveWatermarkAsync(string inputFile, string outputFile, List<RegionModel> regions, double videoWidth, double videoHeight, double containerWidth, double containerHeight)
        {
            if (!File.Exists(_ffmpegPath))
                throw new FileNotFoundException($"FFmpeg not found at: {_ffmpegPath}");

            if (!File.Exists(inputFile))
                throw new FileNotFoundException($"Input file not found: {inputFile}");

            // 1. Calculate Displayed Dimensions & Offsets (Uniform Stretch Logic)
            double videoAspectRatio = videoWidth / videoHeight;
            double containerAspectRatio = containerWidth / containerHeight;

            double displayedWidth, displayedHeight;
            double offsetX = 0, offsetY = 0;

            if (videoAspectRatio > containerAspectRatio)
            {
                // Video is wider than container -> Fills Width, Letterbox Top/Bottom
                displayedWidth = containerWidth;
                displayedHeight = containerWidth / videoAspectRatio;
                offsetY = (containerHeight - displayedHeight) / 2;
            }
            else
            {
                // Video is taller than container -> Fills Height, Letterbox Left/Right
                displayedHeight = containerHeight;
                displayedWidth = containerHeight * videoAspectRatio;
                offsetX = (containerWidth - displayedWidth) / 2;
            }

            // 2. Build Filter Chain
            var filters = new List<string>();
            
            foreach (var region in regions)
            {
                // Map Coordinates: RealX = (SelectionX - OffsetX) * (RealWidth / DisplayedWidth)
                double scaleX = videoWidth / displayedWidth;
                double scaleY = videoHeight / displayedHeight;

                double realX = (region.X - offsetX) * scaleX;
                double realY = (region.Y - offsetY) * scaleY;
                double realW = region.Width * scaleX;
                double realH = region.Height * scaleY;

                // Ensure boundaries and valid integers
                int x = Math.Max(0, (int)Math.Round(realX));
                int y = Math.Max(0, (int)Math.Round(realY));
                int w = (int)Math.Round(realW);
                int h = (int)Math.Round(realH);

                // Validation: Prevent filter failure if region is outside
                if (x + w > videoWidth) w = (int)videoWidth - x;
                if (y + h > videoHeight) h = (int)videoHeight - y;

                if (w > 0 && h > 0)
                {
                    // delogo filter syntax: delogo=x=...:y=...:w=...:h=...
                    filters.Add($"delogo=x={x}:y={y}:w={w}:h={h}");
                }
            }

            if (filters.Count == 0)
                throw new InvalidOperationException("No valid regions to process.");

            // Chain filters with comma
            string filterGraph = string.Join(",", filters);

            // 3. Construct Command
            // -c:a copy to copy audio without re-encoding (Fast)
            // Use quotes for paths
            string arguments = $"-i \"{inputFile}\" -vf \"{filterGraph}\" -c:a copy -y \"{outputFile}\"";

            // 4. Execute
            await RunFFmpegAsync(arguments);
        }

        private Task RunFFmpegAsync(string arguments)
        {
            var tcs = new TaskCompletionSource<bool>();

            var psi = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            var process = new Process { StartInfo = psi };
            process.EnableRaisingEvents = true;

            process.OutputDataReceived += (s, e) => { };
            process.ErrorDataReceived += (s, e) => { };

            process.Exited += (s, e) =>
            {
                if (process.ExitCode == 0)
                    tcs.TrySetResult(true);
                else
                {
                    // In a real app, capture stderr for details
                    tcs.TrySetException(new Exception($"FFmpeg exited with code {process.ExitCode}"));
                }
                process.Dispose();
            };

            try
            {
                process.Start();
                // Read streams to prevent deadlock
                process.BeginErrorReadLine();
                process.BeginOutputReadLine();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }

            return tcs.Task;
        }
    }
}
