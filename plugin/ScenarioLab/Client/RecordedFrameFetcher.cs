using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Media.Imaging;
using VideoOS.Platform;
using VideoOS.Platform.Data;
using Bitmap = System.Drawing.Bitmap;
using ImageFormat = System.Drawing.Imaging.ImageFormat;

namespace ScenarioLab.Client
{
    internal enum FrameStep
    {
        AtOrBefore,   // the recorded frame at the requested time, or the closest one before it
        Previous,     // the frame just before the requested time
        Next          // the frame just after the requested time
    }

    /// <summary>One recorded frame, at the camera's original resolution.</summary>
    internal sealed class RecordedFrame
    {
        public Item Camera;
        public DateTime TimeUtc;
        public Bitmap Bitmap;          // full-resolution copy, owned by whoever receives it (must Dispose)
        public BitmapSource Preview;   // frozen WPF copy for display
    }

    /// <summary>
    /// Fetches recorded frames from XProtect on its own background thread.
    ///
    /// Milestone's reference for BitmapVideoSource: it is not thread-safe (all calls from one thread), and Get() can
    /// be slow, so not on the UI thread. So one dedicated thread owns the source, as in Milestone's
    /// "RGB Video Enhancement" plug-in sample. Requests coalesce: while dragging the slider only the newest request
    /// is fetched, older ones are dropped.
    ///
    /// Reads recordings only. No events, no Hafnia requests.
    /// </summary>
    internal sealed class RecordedFrameFetcher : IDisposable
    {
        private sealed class FrameRequest
        {
            public Item Camera;
            public DateTime TimeUtc;
            public FrameStep Step;
        }

        /// <summary>Raised on the background thread. The receiver owns the frame's Bitmap.</summary>
        public event Action<RecordedFrame> FrameReady;

        /// <summary>Raised on the background thread with a message for the status line.</summary>
        public event Action<string> Problem;

        private readonly object _lock = new object();
        private readonly AutoResetEvent _signal = new AutoResetEvent(false);
        private readonly Thread _thread;
        private FrameRequest _pending;
        private volatile bool _stopping;

        public RecordedFrameFetcher()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "ScenarioLab frame fetcher" };
            _thread.Start();
        }

        public void Request(Item camera, DateTime timeUtc, FrameStep step)
        {
            lock (_lock)
            {
                _pending = new FrameRequest { Camera = camera, TimeUtc = timeUtc, Step = step };
            }
            _signal.Set();
        }

        public void Dispose()
        {
            _stopping = true;
            _signal.Set();
            // Wait for a fetch in progress to finish. Only free the signal once the thread has really ended.
            if (_thread.Join(3000))
            {
                _signal.Dispose();
            }
        }

        private void Run()
        {
            // An unhandled exception on a background thread would close Smart Client: catch everything here.
            try
            {
                RunLoop();
            }
            catch (Exception ex)
            {
                EnvironmentManager.Instance.Log(true, "ScenarioLab", "Frame fetcher stopped: " + ex);
            }
        }

        private void RunLoop()
        {
            BitmapVideoSource source = null;
            Item sourceCamera = null;

            while (!_stopping)
            {
                _signal.WaitOne();
                FrameRequest request;
                lock (_lock)
                {
                    request = _pending;
                    _pending = null;
                }
                if (request == null || _stopping)
                {
                    continue;
                }

                try
                {
                    if (source == null || !sourceCamera.FQID.Equals(request.Camera.FQID))
                    {
                        if (source != null)
                        {
                            source.Close();
                        }
                        source = new BitmapVideoSource(request.Camera);
                        // Width and height 0 = the camera's original resolution (Milestone's reference).
                        source.Init(0, 0, BitmapFormat.BGR24);
                        sourceCamera = request.Camera;
                    }

                    BitmapData data = Fetch(source, request);
                    if (data == null)
                    {
                        RaiseProblem(DescribeMiss(request));
                        continue;
                    }
                    try
                    {
                        RaiseFrame(request.Camera, data);
                    }
                    finally
                    {
                        data.Dispose();   // releases the frame's unmanaged memory
                    }
                }
                catch (Exception ex)
                {
                    RaiseProblem("Could not read the recording: " + ex.Message);
                    EnvironmentManager.Instance.Log(true, "ScenarioLab", "Frame fetch failed: " + ex);
                    if (source != null)
                    {
                        try { source.Close(); } catch { }
                    }
                    source = null;   // reconnect on the next request
                }
            }

            if (source != null)
            {
                try { source.Close(); } catch { }
            }
        }

        private static BitmapData Fetch(BitmapVideoSource source, FrameRequest request)
        {
            switch (request.Step)
            {
                case FrameStep.Previous:
                    return source.GetAtOrBefore(request.TimeUtc.AddMilliseconds(-1)) as BitmapData;
                case FrameStep.Next:
                    // Get(start, max span, max count): frames from 'start' on. We want just the first one.
                    var after = source.Get(request.TimeUtc.AddMilliseconds(1), TimeSpan.FromSeconds(30), 1);
                    return after == null ? null : after.OfType<BitmapData>().FirstOrDefault();
                default:
                    return source.GetAtOrBefore(request.TimeUtc) as BitmapData;
            }
        }

        private void RaiseFrame(Item camera, BitmapData data)
        {
            // GetBitmap() points into the frame's own memory, so copy it before data.Dispose().
            Bitmap copy;
            using (Bitmap raw = data.GetBitmap())
            {
                copy = new Bitmap(raw);
            }

            var frame = new RecordedFrame
            {
                Camera = camera,
                TimeUtc = data.DateTime.ToUniversalTime(),
                Bitmap = copy,
                Preview = ToFrozenWpfImage(copy)
            };

            var handler = FrameReady;
            if (handler != null)
            {
                handler(frame);
            }
            else
            {
                copy.Dispose();
            }
        }

        private void RaiseProblem(string message)
        {
            var handler = Problem;
            if (handler != null)
            {
                handler(message);
            }
        }

        private static string DescribeMiss(FrameRequest request)
        {
            switch (request.Step)
            {
                case FrameStep.Next:
                    return "No later frame recorded.";
                case FrameStep.Previous:
                    return "No earlier frame recorded.";
                default:
                    return "No recording at or before " + request.TimeUtc.ToString("HH:mm:ss") + " UTC (recordings are kept 24 hours, and these cameras record on motion).";
            }
        }

        /// <summary>
        /// WPF images belong to the thread that made them unless frozen. Frozen = read-only and usable from any thread,
        /// so the UI thread can show it. Same conversion as Milestone's Video Replay sample.
        /// </summary>
        private static BitmapSource ToFrozenWpfImage(Bitmap bitmap)
        {
            using (var stream = new MemoryStream())
            {
                bitmap.Save(stream, ImageFormat.Bmp);
                stream.Position = 0;
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                return image;
            }
        }
    }
}
