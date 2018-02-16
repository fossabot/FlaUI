﻿#if (!NET35 && !NET40)
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FlaUI.Core.Logging;
using FlaUI.Core.Tools;

namespace FlaUI.Core.Capturing
{
    /// <summary>
    /// A video recorder which records the captured images into a video file.
    /// </summary>
    public class VideoRecorder : IDisposable
    {
        private readonly uint _frameRate;
        private readonly uint _quality;
        private readonly string _ffmpegExePath;
        private readonly Func<CaptureImage> _captureMethod;
        private readonly CancellationTokenSource _recordTaskCancellation = new CancellationTokenSource();
        private readonly BlockingCollection<ImageData> _frames;
        private Task _recordTask;
        private Task _writeTask;

        /// <summary>
        /// Creates the video recorder and starts recording.
        /// </summary>
        /// <param name="frameRate">The wanted framerate of the recording.</param>
        /// <param name="quality">The quality of the recording. Should be 0 (lossless) to 51 (lowest quality).</param>
        /// <param name="ffmpegExePath">The full path to the executable of ffmpeg.</param>
        /// <param name="targetVideoPath">The path to the target video file.</param>
        /// <param name="captureMethod">The method used for capturing the image which is recorder.</param>
        public VideoRecorder(uint frameRate, uint quality, string ffmpegExePath, string targetVideoPath, Func<CaptureImage> captureMethod)
        {
            _frameRate = frameRate;
            _quality = quality;
            _ffmpegExePath = ffmpegExePath;
            TargetVideoPath = targetVideoPath;
            _captureMethod = captureMethod;
            _frames = new BlockingCollection<ImageData>();
            Start();
        }

        /// <summary>
        /// The path of the video file.
        /// </summary>
        public string TargetVideoPath { get; }

        private async Task RecordLoop(CancellationToken ct)
        {
            var frameInterval = TimeSpan.FromSeconds(1.0 / _frameRate);
            var sw = Stopwatch.StartNew();
            var frameCount = 0;
            ImageData lastImage = null;
            while (!ct.IsCancellationRequested)
            {
                var timestamp = DateTime.UtcNow;

                if (lastImage != null)
                {
                    var requiredFrames = (int)Math.Floor(sw.Elapsed.TotalSeconds * _frameRate);
                    var diff = requiredFrames - frameCount;
                    if (diff > 0)
                    {
                        Logger.Default.Warn($"Adding {diff} missing frame(s) to \"{Path.GetFileName(TargetVideoPath)}\"");
                    }
                    for (var i = 0; i < diff; ++i)
                    {
                        _frames.Add(lastImage, ct);
                        ++frameCount;
                    }
                }

                using (var img = _captureMethod())
                {
                    var imgData = BitmapToByteArray(img.Bitmap);
                    var image = new ImageData
                    {
                        Data = imgData,
                        Width = img.Bitmap.Width,
                        Height = img.Bitmap.Height
                    };
                    _frames.Add(image, ct);
                    ++frameCount;
                    lastImage = image;
                }

                var timeTillNextFrame = timestamp + frameInterval - DateTime.UtcNow;
                if (timeTillNextFrame > TimeSpan.Zero)
                {
                    await Task.Delay(timeTillNextFrame, ct);
                }
            }
        }

        private void WriteLoop()
        {
            var videoPipeName = $"flaui-capture-{Guid.NewGuid()}";
            var ffmpegIn = new NamedPipeServerStream(videoPipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 10000, 10000);
            const string pipePrefix = @"\\.\pipe\";
            Process ffmpegProcess = null;

            var isFirstFrame = true;
            while (!_frames.IsCompleted)
            {
                _frames.TryTake(out var img, -1);
                if (img == null)
                {
                    // Happens when the queue is marked as completed
                    continue;
                }
                if (isFirstFrame)
                {
                    isFirstFrame = false;
                    Directory.CreateDirectory(new FileInfo(TargetVideoPath).Directory.FullName);
                    var videoInArgs = $"-framerate {_frameRate} -f rawvideo -pix_fmt rgb32 -video_size {img.Width}x{img.Height} -i {pipePrefix}{videoPipeName}";
                    var videoOutArgs = $"-vcodec libx264 -crf {_quality} -pix_fmt yuv420p -preset ultrafast -r {_frameRate} -vf \"scale={img.Width.Even()}:{img.Height.Even()}\"";
                    ffmpegProcess = StartFFMpeg(_ffmpegExePath, $"-y -hide_banner -loglevel warning {videoInArgs} {videoOutArgs} \"{TargetVideoPath}\"");
                    ffmpegIn.WaitForConnection();
                }
                ffmpegIn.WriteAsync(img.Data, 0, img.Data.Length);
            }

            ffmpegIn.Flush();
            ffmpegIn.Close();
            ffmpegIn.Dispose();
            ffmpegProcess?.WaitForExit();
        }

        /// <summary>
        /// Starts recording.
        /// </summary>
        private void Start()
        {
            _recordTask = Task.Factory.StartNew(async () => await RecordLoop(_recordTaskCancellation.Token), _recordTaskCancellation.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            _writeTask = Task.Factory.StartNew(WriteLoop, TaskCreationOptions.LongRunning);
        }

        /// <summary>
        /// Stops recording and finishes the video file.
        /// </summary>
        public void Stop()
        {
            if (_recordTask != null)
            {
                _recordTaskCancellation.Cancel();
                _recordTask.Wait();
                _recordTask = null;
            }
            _frames.CompleteAdding();
            if (_writeTask != null)
            {
                try
                {
                    _writeTask.Wait();
                    _writeTask = null;
                }
                catch (Exception ex)
                {
                    Logger.Default.Warn(ex.Message, ex);
                }
            }
        }

        public void Dispose()
        {
            Stop();
        }

        private Process StartFFMpeg(string exePath, string arguments)
        {
            var process = new Process
            {
                StartInfo =
                    {
                        FileName = exePath,
                        Arguments = arguments,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                    },
                EnableRaisingEvents = true
            };

            process.OutputDataReceived += OnProcessDataReceived;
            process.ErrorDataReceived += OnProcessDataReceived;
            process.Start();
            process.BeginErrorReadLine();
            return process;
        }

        private void OnProcessDataReceived(object s, DataReceivedEventArgs e)
        {
            if (!String.IsNullOrWhiteSpace(e.Data))
            {
                Logger.Default.Info(e.Data);
            }
        }

        private byte[] BitmapToByteArray(Bitmap bitmap)
        {
            BitmapData bmpdata = null;
            try
            {
                bmpdata = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                var numbytes = Math.Abs(bmpdata.Stride) * bitmap.Height;
                var bytedata = new byte[numbytes];
                var ptr = bmpdata.Scan0;
                Marshal.Copy(ptr, bytedata, 0, numbytes);
                return bytedata;
            }
            finally
            {
                if (bmpdata != null)
                    bitmap.UnlockBits(bmpdata);
            }
        }

        public static async Task<string> DownloadFFMpeg(string targetFolder)
        {
            var bits = Environment.Is64BitOperatingSystem ? 64 : 32;
            var uri = new Uri($"http://ffmpeg.zeranoe.com/builds/win{bits}/static/ffmpeg-latest-win{bits}-static.zip");
            var archivePath = Path.Combine(Path.GetTempPath(), "ffmpeg.zip");
            var destPath = Path.Combine(targetFolder, "ffmpeg.exe");
            if (!File.Exists(destPath))
            {
                // Download
                using (var webClient = new WebClient())
                {
                    await webClient.DownloadFileTaskAsync(uri, archivePath);
                }
                // Extract
                Directory.CreateDirectory(targetFolder);
                await Task.Run(() =>
                {
                    using (var archive = ZipFile.OpenRead(archivePath))
                    {
                        var exeEntry = archive.Entries.First(x => x.Name == "ffmpeg.exe");
                        exeEntry.ExtractToFile(destPath, true);
                    }
                });
                File.Delete(archivePath);
            }
            return destPath;
        }

        private class ImageData
        {
            public int Width { get; set; }
            public int Height { get; set; }
            public byte[] Data { get; set; }
        }
    }
}
#endif
