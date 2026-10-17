using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using VideoOS.Platform;
using VideoOS.Platform.Client;
using ScenarioLab.Hafnia;
// Aliases: WPF and Windows Forms both have types called Image; we name the drawing ones explicitly.
using Bitmap = System.Drawing.Bitmap;
using ImageFormat = System.Drawing.Imaging.ImageFormat;

namespace ScenarioLab.Client
{
    /// <summary>
    /// Code for the Scenario Lab panel (layout in the .xaml file).
    ///
    /// What works in this build:
    ///   1. lists XProtect's cameras;
    ///   2. shows live video of the selected camera; a slider and frame-step buttons pick a recorded frame (original
    ///      resolution, read from XProtect's recordings), which is saved as the snapshot;
    ///   3. saves a scenario text next to the snapshot;
    ///   4. lists and plays local clip files from C:\ScenarioLab\clips;
    ///   5. tests a detection question with Hafnia VLM on the picked moment (one request per click) and shows
    ///      expected vs. got; a "detected" answer also raises the XProtect alarm through the gateway rule.
    /// Placeholder: scenario suggestions.
    ///
    /// Hafnia requests: only from the "Run test" button, one per click, never in a loop, capped below the daily 500.
    /// XProtect calls follow Milestone's samples: "Smart Client Workspace" (camera list), "Video Preview"
    /// (ImageViewerWpfControl), "Video Replay" and "RGB Video Enhancement" (BitmapVideoSource, see RecordedFrameFetcher).
    /// </summary>
    public partial class ScenarioLabViewItemWpfUserControl : ViewItemWpfUserControl
    {
        private static readonly string SnapshotFolder = Path.Combine(ScenarioLabDefinition.DataFolder, "snapshots");
        private static readonly string ClipFolder = Path.Combine(ScenarioLabDefinition.DataFolder, "clips");
        private static readonly string[] ClipExtensions = { ".mp4", ".mov", ".avi", ".wmv", ".mkv" };

        private ImageViewerWpfControl _imageViewer;   // XProtect's live video control
        private bool _viewerConnected;
        private Item _selectedCamera;                 // an XProtect configuration item of kind Camera

        private RecordedFrameFetcher _fetcher;        // reads recorded frames on a background thread
        private RecordedFrame _shownFrame;            // the recorded frame currently shown (we own its Bitmap)
        private DateTime _anchorUtc;                  // slider value 0 = this moment
        private bool _movingSliderFromCode;           // true while code (not the user) moves the slider

        private string _lastSnapshotPath;
        private DateTime _lastSnapshotFrameUtc;

        // ---- 5. Test with Hafnia ----
        private const string PromptTitle = "Scenario Lab";   // saved prompt on the gateway (hafnia/README.md)
        private const string KeywordInstruction =
            "First describe in one or two sentences what happens in the clip. Then, on the last line, write exactly one word: "
            + "SCENARIO_DETECTED if this happens in the clip, SCENARIO_NOT_DETECTED if it does not.";
        private const int DailyRequestCap = 450;              // refuse to send above this (the allowance is 500 a day)
        private const int ClipSeconds = 30;
        private static readonly string RunLogPath = Path.Combine(ScenarioLabDefinition.DataFolder, "runs.jsonl");

        // Static = shared by the whole Smart Client process, so switching tabs keeps the gateway session and the
        // test table. Memory only: gone when Smart Client closes. The password itself is never kept.
        private static HafniaGatewayClient _gateway;
        private static string _promptId;
        private static readonly ObservableCollection<TestRun> _runs = new ObservableCollection<TestRun>();
        private static bool _testRunning;
        private bool _closed;

        public ScenarioLabViewItemWpfUserControl()
        {
            InitializeComponent();   // builds the controls described in the .xaml file
            ClientControl.Instance.RegisterUIControlForAutoTheming(this);   // follow Smart Client's dark/light theme
        }

        /// <summary>Called by Smart Client right after the panel is shown.</summary>
        public override void Init()
        {
            base.Init();
            try
            {
                Directory.CreateDirectory(SnapshotFolder);
                Directory.CreateDirectory(ClipFolder);

                // As in Milestone's Video Preview sample: pass this view item's WindowInformation so the viewer
                // follows Smart Client's live/playback mode.
                _imageViewer = new ImageViewerWpfControl(WindowInformation)
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    MinWidth = 100
                };
                _liveViewHost.Children.Add(_imageViewer);

                _fetcher = new RecordedFrameFetcher();
                _fetcher.FrameReady += OnFrameReadyFromFetcher;
                _fetcher.Problem += OnProblemFromFetcher;

                _closed = false;
                _runsGrid.ItemsSource = _runs;
                if (_gateway != null && _gateway.IsLoggedIn)
                {
                    _hafniaState.Text = "Connected to the Hafnia gateway.";
                }

                LoadCameras();
                LoadClips();
                UpdateHafniaControls();
                SetStatus("Ready. Data folder: " + ScenarioLabDefinition.DataFolder);
            }
            catch (Exception ex)
            {
                ReportError("Init", ex);
            }
        }

        /// <summary>Called when the panel is no longer shown (tab closed, logout). Free everything here.</summary>
        public override void Close()
        {
            _closed = true;   // stops a running test from updating this panel (the request itself continues)
            try
            {
                _player.Stop();
                _player.Close();

                if (_fetcher != null)
                {
                    _fetcher.FrameReady -= OnFrameReadyFromFetcher;
                    _fetcher.Problem -= OnProblemFromFetcher;
                    _fetcher.Dispose();
                    _fetcher = null;
                }
                ReplaceShownFrame(null);

                if (_imageViewer != null)
                {
                    if (_viewerConnected)
                    {
                        _imageViewer.Disconnect();
                        _viewerConnected = false;
                    }
                    // Milestone's reference: call Close() and Dispose() once completely done with the viewer.
                    _imageViewer.Close();
                    _imageViewer.Dispose();
                    _imageViewer = null;
                }
            }
            catch (Exception ex)
            {
                ReportError("Close", ex);
            }
            base.Close();
        }

        // No sliding camera toolbar at the bottom of our panel.
        public override bool ShowToolbar => false;

        private void OnMouseLeftUp(object sender, MouseButtonEventArgs e)
        {
            FireClickEvent();   // tells Smart Client this view item was clicked (selects it), as in the samples
        }

        #region 1. Cameras

        private void OnRefreshCameras(object sender, RoutedEventArgs e)
        {
            LoadCameras();
        }

        private void LoadCameras()
        {
            var cameras = new List<Item>();
            FindAllCameras(Configuration.Instance.GetItemsByKind(Kind.Camera), cameras);
            // LINQ (Language-Integrated Query) works like Python's sorted(..., key=...).
            _cameraList.ItemsSource = cameras.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            SetStatus(cameras.Count + " cameras found.");
        }

        /// <summary>
        /// XProtect returns cameras inside a tree of folders (groups). Walk it and collect each camera once.
        /// Same logic as FindAllCameras in Milestone's Workspace sample.
        /// </summary>
        private static void FindAllCameras(List<Item> items, List<Item> found)
        {
            foreach (Item item in items)
            {
                if (item.FQID.Kind == Kind.Camera && item.FQID.FolderType == FolderType.No)
                {
                    if (!found.Any(f => f.FQID.Equals(item.FQID)))
                    {
                        found.Add(item);
                    }
                }
                else
                {
                    FindAllCameras(item.GetChildren(), found);
                }
            }
        }

        private void OnCameraSelected(object sender, SelectionChangedEventArgs e)
        {
            var camera = _cameraList.SelectedItem as Item;   // "as" = cast, or null if it is not an Item
            if (camera == null || _imageViewer == null)
            {
                return;
            }
            try
            {
                if (_viewerConnected)
                {
                    _imageViewer.Disconnect();
                    _viewerConnected = false;
                }
                _selectedCamera = camera;
                _liveViewPlaceholder.Visibility = Visibility.Collapsed;

                // Order from Milestone's reference: set CameraFQID, then Initialize(), then Connect().
                _imageViewer.CameraFQID = camera.FQID;
                _imageViewer.EnableVisibleHeader = true;
                _imageViewer.MaintainImageAspectRatio = true;
                _imageViewer.Initialize();
                _imageViewer.Connect();
                _viewerConnected = true;

                // A different camera: forget the previous camera's frame, and show this one's latest recording.
                ReplaceShownFrame(null);
                _frameImage.Source = null;
                _framePlaceholder.Visibility = Visibility.Visible;
                SetFrameControlsEnabled(true);
                JumpToNow();
                UpdateHafniaControls();

                SetStatus("Showing " + camera.Name + ".");
            }
            catch (Exception ex)
            {
                ReportError("Connect to " + camera.Name, ex);
            }
        }

        #endregion

        #region 2. Pick a recorded frame

        private void SetFrameControlsEnabled(bool enabled)
        {
            _timeSlider.IsEnabled = enabled;
            _prevFrameButton.IsEnabled = enabled;
            _nextFrameButton.IsEnabled = enabled;
            _nowButton.IsEnabled = enabled;
        }

        private int SelectedRangeSeconds
        {
            get
            {
                var item = _rangeBox.SelectedItem as ComboBoxItem;
                int seconds;
                return item != null && int.TryParse(item.Tag as string, out seconds) ? seconds : 600;
            }
        }

        /// <summary>Re-anchors the slider at the current time and asks for the latest recorded frame.</summary>
        private void JumpToNow()
        {
            _anchorUtc = DateTime.UtcNow;
            MoveSlider(0);
            RequestFrame(_anchorUtc, FrameStep.AtOrBefore);
        }

        private void OnNow(object sender, RoutedEventArgs e)
        {
            JumpToNow();
        }

        private void OnRangeChanged(object sender, SelectionChangedEventArgs e)
        {
            // Fires once while the XAML is still being built (the default item is selected): ignore that one.
            if (_timeSlider == null)
            {
                return;
            }
            _timeSlider.Minimum = -SelectedRangeSeconds;
            if (_selectedCamera != null)
            {
                JumpToNow();
            }
        }

        private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_movingSliderFromCode || _selectedCamera == null)
            {
                return;
            }
            RequestFrame(_anchorUtc.AddSeconds(_timeSlider.Value), FrameStep.AtOrBefore);
        }

        private void OnPreviousFrame(object sender, RoutedEventArgs e)
        {
            RequestFrame(CurrentFrameTimeUtc(), FrameStep.Previous);
        }

        private void OnNextFrame(object sender, RoutedEventArgs e)
        {
            RequestFrame(CurrentFrameTimeUtc(), FrameStep.Next);
        }

        /// <summary>Time of the frame on screen, or the slider's time if no frame is shown yet.</summary>
        private DateTime CurrentFrameTimeUtc()
        {
            return _shownFrame != null ? _shownFrame.TimeUtc : _anchorUtc.AddSeconds(_timeSlider.Value);
        }

        private void RequestFrame(DateTime timeUtc, FrameStep step)
        {
            if (_selectedCamera == null || _fetcher == null)
            {
                return;
            }
            _frameTimeText.Text = "Loading " + timeUtc.ToString("HH:mm:ss.fff") + " UTC ...";
            _fetcher.Request(_selectedCamera, timeUtc, step);
        }

        /// <summary>Moves the slider without triggering a new request.</summary>
        private void MoveSlider(double value)
        {
            _movingSliderFromCode = true;
            _timeSlider.Value = Math.Max(_timeSlider.Minimum, Math.Min(_timeSlider.Maximum, value));
            _movingSliderFromCode = false;
        }

        // Runs on the fetcher's background thread: hand the frame to the UI thread.
        private void OnFrameReadyFromFetcher(RecordedFrame frame)
        {
            Dispatcher.BeginInvoke(new Action(() => ShowFrame(frame)));
        }

        // Runs on the fetcher's background thread.
        private void OnProblemFromFetcher(string message)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _frameTimeText.Text = message;
                SetStatus(message);
            }));
        }

        private void ShowFrame(RecordedFrame frame)
        {
            // The answer may arrive after the user switched camera or closed the panel: then drop it.
            if (_fetcher == null || _selectedCamera == null || !frame.Camera.FQID.Equals(_selectedCamera.FQID))
            {
                frame.Bitmap.Dispose();
                return;
            }
            ReplaceShownFrame(frame);
            _frameImage.Source = frame.Preview;
            _framePlaceholder.Visibility = Visibility.Collapsed;
            _snapshotButton.IsEnabled = true;

            double secondsFromAnchor = (frame.TimeUtc - _anchorUtc).TotalSeconds;
            MoveSlider(secondsFromAnchor);
            _frameTimeText.Text = frame.TimeUtc.ToString("yyyy-MM-dd HH:mm:ss.fff") + " UTC   ("
                + FormatAgo(-secondsFromAnchor) + ")   " + frame.Bitmap.Width + " × " + frame.Bitmap.Height + " px";
        }

        private void ReplaceShownFrame(RecordedFrame frame)
        {
            if (_shownFrame != null && _shownFrame != frame)
            {
                _shownFrame.Bitmap.Dispose();
            }
            _shownFrame = frame;
        }

        private static string FormatAgo(double seconds)
        {
            if (seconds < 1)
            {
                return "latest";
            }
            var span = TimeSpan.FromSeconds(seconds);
            return span.TotalHours >= 1
                ? string.Format("{0}h {1:00}m ago", (int)span.TotalHours, span.Minutes)
                : string.Format("{0}m {1:00}.{2:0}s ago", (int)span.TotalMinutes, span.Seconds, span.Milliseconds / 100);
        }

        /// <summary>
        /// Saves the recorded frame on screen at its original resolution, as PNG (lossless: no extra compression
        /// artefacts in the picture we hand to the video model). The file name holds the frame's own time.
        /// </summary>
        private void OnTakeSnapshot(object sender, RoutedEventArgs e)
        {
            if (_shownFrame == null)
            {
                return;
            }
            try
            {
                string fileName = SafeFileName(_shownFrame.Camera.Name) + "_" + _shownFrame.TimeUtc.ToString("yyyyMMdd'T'HHmmss.fff'Z'") + ".png";
                string path = Path.Combine(SnapshotFolder, fileName);
                _shownFrame.Bitmap.Save(path, ImageFormat.Png);

                _lastSnapshotPath = path;
                _lastSnapshotFrameUtc = _shownFrame.TimeUtc;
                _snapshotInfo.Text = "Saved " + _shownFrame.Bitmap.Width + " × " + _shownFrame.Bitmap.Height + " px   " + path;
                _saveScenarioButton.IsEnabled = true;
                SetStatus("Snapshot saved: " + path);
            }
            catch (Exception ex)
            {
                ReportError("Snapshot", ex);
            }
        }

        private void OnOpenSnapshotFolder(object sender, RoutedEventArgs e)
        {
            OpenInExplorer(SnapshotFolder);
        }

        #endregion

        #region 3. Scenario

        /// <summary>
        /// Writes the scenario as a text file next to the snapshot (same name, ".scenario.txt"), so the snapshot and
        /// its scenario can be handed to video generation together.
        /// </summary>
        private void OnSaveScenario(object sender, RoutedEventArgs e)
        {
            if (_lastSnapshotPath == null)
            {
                return;
            }
            try
            {
                string text = _scenarioText.Text.Trim();
                if (text.Length == 0)
                {
                    SetStatus("Write a scenario first.");
                    return;
                }
                string path = Path.ChangeExtension(_lastSnapshotPath, ".scenario.txt");
                var content = new StringBuilder()
                    .AppendLine("camera: " + _selectedCamera.Name)
                    .AppendLine("snapshot: " + Path.GetFileName(_lastSnapshotPath))
                    .AppendLine("frame_utc: " + _lastSnapshotFrameUtc.ToString("o"))
                    .AppendLine("saved_utc: " + DateTime.UtcNow.ToString("o"))
                    .AppendLine("scenario:")
                    .AppendLine(text)
                    .ToString();
                File.WriteAllText(path, content, Encoding.UTF8);
                SetStatus("Scenario saved: " + path);
            }
            catch (Exception ex)
            {
                ReportError("Save scenario", ex);
            }
        }

        #endregion

        #region 4. Generated clips (local files for now)

        private void OnRefreshClips(object sender, RoutedEventArgs e)
        {
            LoadClips();
        }

        private void LoadClips()
        {
            var clips = new DirectoryInfo(ClipFolder).GetFiles()
                .Where(f => ClipExtensions.Contains(f.Extension.ToLowerInvariant()))
                .OrderBy(f => f.Name)
                .ToList();
            _clipList.ItemsSource = clips;
        }

        private FileInfo SelectedClip => _clipList.SelectedItem as FileInfo;

        private void OnClipSelected(object sender, SelectionChangedEventArgs e)
        {
            bool hasClip = SelectedClip != null;
            _playButton.IsEnabled = hasClip;
            _stopButton.IsEnabled = hasClip;
            _openExternalButton.IsEnabled = hasClip;
            if (hasClip)
            {
                _player.Stop();
                _player.Source = new Uri(SelectedClip.FullName);
            }
        }

        private void OnPlay(object sender, RoutedEventArgs e)
        {
            _player.Play();
        }

        private void OnStop(object sender, RoutedEventArgs e)
        {
            _player.Stop();
        }

        private void OnPlayerEnded(object sender, RoutedEventArgs e)
        {
            // Loop the clip, like XProtect's test cameras do.
            _player.Position = TimeSpan.Zero;
            _player.Play();
        }

        private void OnPlayerFailed(object sender, ExceptionRoutedEventArgs e)
        {
            // WPF's player relies on Windows media components, which Windows Server may not have.
            SetStatus("Cannot play this clip inside Smart Client (" + e.ErrorException.Message + "). Use 'Open in player'.");
            EnvironmentManager.Instance.Log(true, "ScenarioLab", "MediaFailed: " + e.ErrorException);
        }

        private void OnOpenExternal(object sender, RoutedEventArgs e)
        {
            if (SelectedClip != null)
            {
                try
                {
                    Process.Start(SelectedClip.FullName);   // opens with the default Windows app for that file type
                }
                catch (Exception ex)
                {
                    ReportError("Open clip", ex);
                }
            }
        }

        private void OnOpenClipFolder(object sender, RoutedEventArgs e)
        {
            OpenInExplorer(ClipFolder);
        }

        #endregion

        #region 5. Test with Hafnia

        private void UpdateHafniaControls()
        {
            bool connected = _gateway != null && _gateway.IsLoggedIn && _promptId != null;
            _runTestButton.IsEnabled = connected && _selectedCamera != null && !_testRunning;
        }

        /// <summary>Logs in to the gateway (password used once, then forgotten), finds the saved prompt, shows the allowance.</summary>
        private async void OnConnectHafnia(object sender, RoutedEventArgs e)
        {
            // "async void" is only allowed for event handlers: an exception escaping it would crash Smart Client,
            // so everything is inside try/catch.
            string password = _gatewayPassword.Password;
            _gatewayPassword.Clear();
            if (password.Length == 0)
            {
                _hafniaState.Text = "Type the gateway password first.";
                return;
            }
            _connectButton.IsEnabled = false;
            _hafniaState.Text = "Connecting to " + HafniaGatewayClient.Address + " ...";
            try
            {
                if (_gateway == null)
                {
                    _gateway = new HafniaGatewayClient();
                }
                await _gateway.LoginAsync("administrator", password);
                password = null;

                _promptId = await _gateway.FindPromptIdAsync(PromptTitle);
                if (_promptId == null)
                {
                    _hafniaState.Text = "Connected, but the gateway has no saved prompt titled '" + PromptTitle + "'. See hafnia/README.md.";
                    return;
                }
                _hafniaState.Text = "Connected. " + DescribeQuota(await _gateway.GetQuotaAsync());
            }
            catch (Exception ex)
            {
                _hafniaState.Text = "Connection failed: " + ex.Message;
                EnvironmentManager.Instance.Log(true, "ScenarioLab", "Hafnia connect failed: " + ex);
            }
            finally
            {
                _connectButton.IsEnabled = true;
                UpdateHafniaControls();
            }
        }

        /// <summary>
        /// Sends ONE Hafnia request: the detection question (+ keyword instruction) about 30 seconds of the selected
        /// camera around the picked frame, then reads the answer every 3 seconds (reading is free) until it is done.
        /// </summary>
        private async void OnRunTest(object sender, RoutedEventArgs e)
        {
            if (_testRunning || _selectedCamera == null || _gateway == null || _promptId == null)
            {
                return;
            }
            string question = _detectionQuestion.Text.Trim();
            if (question.Length == 0)
            {
                _runProgress.Text = "Write a detection question first.";
                return;
            }

            // Clip window: 30 s centred on the picked frame, ending at least 5 s ago (it must already be recorded).
            DateTime center = _shownFrame != null ? _shownFrame.TimeUtc : DateTime.UtcNow;
            DateTime startUtc = center.AddSeconds(-ClipSeconds / 2);
            DateTime endUtc = center.AddSeconds(ClipSeconds / 2);
            DateTime latest = DateTime.UtcNow.AddSeconds(-5);
            if (endUtc > latest)
            {
                endUtc = latest;
                startUtc = endUtc.AddSeconds(-ClipSeconds);
            }

            var expectedItem = _expectedBox.SelectedItem as ComboBoxItem;
            var run = new TestRun
            {
                Time = DateTime.Now.ToString("HH:mm:ss"),
                Camera = _selectedCamera.Name,
                Clip = startUtc.ToString("HH:mm:ss") + "–" + endUtc.ToString("HH:mm:ss") + " UTC",
                Expected = expectedItem != null ? (string)expectedItem.Tag : "detect",
                Got = "sending…",
                Result = "",
                Question = question,
                StartUtc = startUtc,
                EndUtc = endUtc
            };
            // XProtect camera ID = the camera item's ObjectId (the same ID the REST API and MilestonePSTools show).
            string cameraId = _selectedCamera.FQID.ObjectId.ToString("D").ToUpperInvariant();

            _testRunning = true;
            UpdateHafniaControls();
            _runs.Insert(0, run);
            _runsGrid.SelectedItem = run;

            try
            {
                // Quota guard: read today's count (free) and refuse above the cap.
                Quota quota = await _gateway.GetQuotaAsync();
                if (quota != null && quota.Used >= DailyRequestCap)
                {
                    FinishRun(run, "not sent", "", "Not sent: " + quota.Used + " of " + quota.Limit + " requests used today (cap " + DailyRequestCap + ").");
                    return;
                }

                Summary summary = await _gateway.CreateSummaryAsync(_promptId, question + "\n" + KeywordInstruction, cameraId, startUtc, endUtc);
                run.RequestId = summary.Id;

                DateTime deadline = DateTime.UtcNow.AddMinutes(10);   // kit: stop waiting after 10 minutes
                while (!summary.IsFinished && DateTime.UtcNow < deadline)
                {
                    if (_closed)
                    {
                        return;   // panel closed: the request finishes on the gateway; its answer is in History
                    }
                    run.Got = Describe(summary.Progress);
                    _runProgress.Text = run.Camera + ", " + run.Clip + ": " + run.Got;
                    _runsGrid.Items.Refresh();
                    await Task.Delay(TimeSpan.FromSeconds(3));
                    summary = await _gateway.GetSummaryAsync(summary.Id);
                }

                if (summary.Progress == "Completed")
                {
                    string got = Verdict(summary.Response);
                    string result = got == "unclear" ? "?" : (got == "detected") == (run.Expected == "detect") ? "PASS" : "FAIL";
                    FinishRun(run, got, result, summary.Response);
                }
                else if (summary.Progress == "Failed")
                {
                    FinishRun(run, "failed", "?", "The request failed: " + summary.LastError);
                }
                else
                {
                    FinishRun(run, "no answer yet", "?", "Still running after 10 minutes: see Summarization, History in Smart Client.");
                }
                _hafniaState.Text = "Connected. " + DescribeQuota(await _gateway.GetQuotaAsync());
            }
            catch (Exception ex)
            {
                FinishRun(run, "error", "?", ex.Message);
                if (!_gateway.IsLoggedIn)
                {
                    _hafniaState.Text = "Session ended: press Connect again.";
                }
                EnvironmentManager.Instance.Log(true, "ScenarioLab", "Hafnia test failed: " + ex);
            }
            finally
            {
                _testRunning = false;
                if (!_closed)
                {
                    UpdateHafniaControls();
                }
            }
        }

        /// <summary>Writes the outcome into the row, shows it, and appends it to C:\ScenarioLab\runs.jsonl.</summary>
        private void FinishRun(TestRun run, string got, string result, string answer)
        {
            run.Got = got;
            run.Result = result;
            run.Answer = answer;
            if (!_closed)
            {
                _runsGrid.Items.Refresh();
                _runsGrid.SelectedItem = run;
                _hafniaAnswer.Text = answer;
                _runProgress.Text = run.Camera + ", " + run.Clip + ": " + got + (result.Length > 0 ? "  →  " + result : "");
            }
            try
            {
                var line = new JavaScriptSerializer().Serialize(new Dictionary<string, object>
                {
                    { "local_time", run.Time }, { "camera", run.Camera },
                    { "start_utc", run.StartUtc.ToString("o") }, { "end_utc", run.EndUtc.ToString("o") },
                    { "question", run.Question }, { "expected", run.Expected }, { "got", got }, { "result", result },
                    { "request_id", run.RequestId }, { "answer", answer }
                });
                File.AppendAllText(RunLogPath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                EnvironmentManager.Instance.Log(true, "ScenarioLab", "Could not write " + RunLogPath + ": " + ex.Message);
            }
        }

        private void OnRunSelected(object sender, SelectionChangedEventArgs e)
        {
            var run = _runsGrid.SelectedItem as TestRun;
            if (run != null)
            {
                _hafniaAnswer.Text = run.Answer ?? ("Question: " + run.Question + "\nClip: " + run.Camera + ", " + run.Clip);
            }
        }

        /// <summary>
        /// Reads the keyword the way the gateway rule does: a whole word, ignoring case. "_" is part of a word, so
        /// SCENARIO_NOT_DETECTED never counts as SCENARIO_DETECTED.
        /// </summary>
        private static string Verdict(string answer)
        {
            if (string.IsNullOrEmpty(answer))
            {
                return "unclear";
            }
            if (Regex.IsMatch(answer, @"\bSCENARIO_DETECTED\b", RegexOptions.IgnoreCase))
            {
                return "detected";
            }
            if (Regex.IsMatch(answer, @"\bSCENARIO_NOT_DETECTED\b", RegexOptions.IgnoreCase))
            {
                return "not detected";
            }
            return "unclear";
        }

        private static string Describe(string progress)
        {
            switch (progress)
            {
                case "NotStarted": return "queued";
                case "FetchingVideo": return "exporting the clip from XProtect";
                case "TransferringAssets": return "uploading the clip";
                case "RequestingSummary": return "waiting for Hafnia VLM";
                default: return progress ?? "…";
            }
        }

        private static string DescribeQuota(Quota quota)
        {
            return quota == null
                ? "Today's allowance is not available right now."
                : quota.Used + " of " + quota.Limit + " Hafnia requests used today (resets 00:00 UTC).";
        }

        #endregion

        #region Helpers

        private void SetStatus(string text)
        {
            _status.Text = DateTime.Now.ToString("HH:mm:ss") + "  " + text;
        }

        /// <summary>Shows the error in the status line and writes it to Smart Client's MIP log.</summary>
        private void ReportError(string what, Exception ex)
        {
            SetStatus(what + " failed: " + ex.Message);
            EnvironmentManager.Instance.Log(true, "ScenarioLab", what + " failed: " + ex);
        }

        private void OpenInExplorer(string folder)
        {
            try
            {
                Directory.CreateDirectory(folder);
                Process.Start("explorer.exe", folder);
            }
            catch (Exception ex)
            {
                ReportError("Open folder", ex);
            }
        }

        private static string SafeFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }
            return name.Replace(' ', '_');
        }

        #endregion
    }
}
