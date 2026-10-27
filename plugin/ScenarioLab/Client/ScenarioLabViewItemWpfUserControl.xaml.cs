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
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VideoOS.Platform;
using VideoOS.Platform.Client;
using VideoOS.Platform.Messaging;
using VideoOS.Platform.UI.Controls;
using ScenarioLab.Hafnia;
// Aliases: WPF and Windows Forms both have types called Image; we name the drawing ones explicitly.
using Bitmap = System.Drawing.Bitmap;
using ImageFormat = System.Drawing.Imaging.ImageFormat;
using Message = VideoOS.Platform.Messaging.Message;

namespace ScenarioLab.Client
{
    /// <summary>
    /// Code for the Scenario Lab panel (layout in the .xaml file). Two pages:
    ///
    /// Page 1, "Snapshot &amp; scenarios":
    ///   1. lists XProtect's cameras;
    ///   2. live view of the selected camera; a slider and frame-step buttons pick a recorded frame (original
    ///      resolution, read from XProtect's recordings), saved as the snapshot;
    ///   3. loads the scenario suggestions prepared from that snapshot (C:\ScenarioLab\suggestions\&lt;camera&gt;_scenarios.json),
    ///      shows them as a checklist, lets the user add their own, and saves the ticked ones to
    ///      &lt;camera&gt;_scenarios_user.json: the input for video generation.
    /// Page 2, "Generated clips &amp; Hafnia test":
    ///   4. each saved scenario next to the XProtect camera that plays its generated clip (found by name), plus a
    ///      control row (the snapshot camera at the snapshot moment); clicking a row shows that camera live;
    ///   5. one detection question tested with Hafnia VLM on every clip (one request per clip), expected vs. got;
    ///      a "detected" answer also raises the XProtect alarm through the gateway rule.
    ///
    /// Hafnia requests: only from the Run buttons, one request per clip, capped below the daily 500.
    /// XProtect calls follow Milestone's samples: "Smart Client Workspace" (camera list), "Video Preview"
    /// (ImageViewerWpfControl), "Video Replay" and "RGB Video Enhancement" (BitmapVideoSource, see RecordedFrameFetcher).
    /// </summary>
    public partial class ScenarioLabViewItemWpfUserControl : ViewItemWpfUserControl
    {
        private static readonly string SnapshotFolder = Path.Combine(ScenarioLabDefinition.DataFolder, "snapshots");

        private ImageViewerWpfControl _imageViewer;   // page 1: XProtect's live video control
        private bool _viewerConnected;
        private Item _selectedCamera;                 // an XProtect configuration item of kind Camera
        private List<Item> _allCameras = new List<Item>();

        private ImageViewerWpfControl _clipViewer;    // page 2: live view of the selected scenario's camera
        private bool _clipViewerConnected;

        private RecordedFrameFetcher _fetcher;        // reads recorded frames on a background thread
        private RecordedFrame _shownFrame;            // the recorded frame currently shown (we own its Bitmap)
        private DateTime _anchorUtc;                  // slider value 0 = this moment
        private bool _movingSliderFromCode;           // true while code (not the user) moves the slider

        // Static = shared by the whole Smart Client process, so switching tabs (which recreates this panel) keeps
        // the snapshot, the scenarios, the gateway session and the results. Memory only: gone when Smart Client closes.
        private static int _page = 1;
        private static string _lastSnapshotPath;
        private static DateTime _lastSnapshotFrameUtc;
        private static string _lastSnapshotCamera;
        private static string _suggestionsPath;                                        // file the checklist came from
        private static readonly ObservableCollection<Scenario> _suggestions = new ObservableCollection<Scenario>();
        private static ScenarioSelection _selection;                                   // what page 2 shows
        private static string _selectionPath;
        private static readonly ObservableCollection<Scenario> _rows = new ObservableCollection<Scenario>();

        // ---- 5. Test with Hafnia ----
        private const string PromptTitle = "Scenario Lab";   // saved prompt on the gateway (hafnia/README.md)
        private const string KeywordInstruction =
            "First describe in one or two sentences what happens in the clip. Then, on the last line, write exactly one word: "
            + "SCENARIO_DETECTED if this happens in the clip, SCENARIO_NOT_DETECTED if it does not.";
        private const int DailyRequestCap = 450;              // refuse to send above this (the allowance is 500 a day)
        private const int RecordingLagSeconds = 5;            // the clip window ends at least this long ago (already recorded)
        private static readonly string RunLogPath = Path.Combine(ScenarioLabDefinition.DataFolder, "runs.jsonl");

        private static HafniaGatewayClient _gateway;          // the password itself is never kept
        private static string _promptId;
        private static bool _testRunning;
        private bool _closed;
        private object _themeChangedReceiver;

        // Milestone design-system controls that take their items from code (see the .xaml file).
        private VideoOSTabControlPrimaryItem _tab1, _tab2;
        private bool _settingFromCode;          // true while code (not the user) changes a tab or drop-down
        private Scenario _shownRow;             // the page-2 row whose clip and answer are on screen

        public ScenarioLabViewItemWpfUserControl()
        {
            InitializeComponent();   // builds the controls described in the .xaml file
            ClientControl.Instance.RegisterUIControlForAutoTheming(this);   // follow Smart Client's dark/light theme
            SetUpDesignSystemControls();
        }

        /// <summary>
        /// Items and events of Milestone's tab strip, drop-downs and tables. Their events are hooked up here, in code,
        /// with lambdas, so the code does not depend on each event's exact argument type.
        /// </summary>
        private void SetUpDesignSystemControls()
        {
            _settingFromCode = true;
            _tab1 = new VideoOSTabControlPrimaryItem { Text = "Snapshot & scenarios" };
            _tab2 = new VideoOSTabControlPrimaryItem { Text = "Generated clips & Hafnia test" };
            _tabs.ItemsSource = new List<VideoOSTabControlPrimaryItem> { _tab1, _tab2 };
            _tabs.SelectedItem = _tab1;

            _rangeBox.ItemsSource = DropDownItems("Last 2 minutes", 120, "Last 10 minutes", 600, "Last hour", 3600, "Last 24 hours", 86400);
            _rangeBox.SelectedItem = FindDropDownItem(_rangeBox, 600);
            _ownExpectedBox.ItemsSource = DropDownItems("Should trigger the alarm", Scenario.Detect,
                                                        "Should NOT trigger the alarm (harmless)", Scenario.NoDetect);
            _ownExpectedBox.SelectedItem = FindDropDownItem(_ownExpectedBox, Scenario.Detect);
            _rowExpectedBox.ItemsSource = DropDownItems("Should trigger the alarm (detect)", Scenario.Detect,
                                                        "Should NOT trigger the alarm (no detect)", Scenario.NoDetect);
            _windowBox.ItemsSource = DropDownItems("10 seconds", 10, "20 seconds", 20, "30 seconds", 30);
            _windowBox.SelectedItem = FindDropDownItem(_windowBox, 10);
            _settingFromCode = false;

            // Table columns (the WPF markup compiler cannot add several items to this ICollection<T> in XAML).
            var star = new DataGridLength(1, DataGridLengthUnitType.Star);
            AddColumn(_cameraList, "CAMERA", "Name", star);
            AddColumn(_scenarioGrid, "SCENARIO", "Title", star);
            AddColumn(_scenarioGrid, "CAMERA", "CameraLabel", DataGridLength.Auto);
            AddColumn(_scenarioGrid, "EXPECTED", "Expected", DataGridLength.Auto);
            AddColumn(_scenarioGrid, "GOT", "Got", DataGridLength.Auto);
            AddColumn(_scenarioGrid, "RESULT", "Result", DataGridLength.Auto);

            _tabs.SelectedItemChanged += (s, e) =>
            {
                if (!_settingFromCode)
                {
                    ShowPage(_tabs.SelectedItem == _tab2 ? 2 : 1);
                }
            };
            _rangeBox.SelectedItemChanged += (s, e) => OnRangeChanged();
            _rowExpectedBox.SelectedItemChanged += (s, e) => OnRowExpectedChanged();
            _cameraList.SelectionChanged += (s, e) => OnCameraSelected();
            _scenarioGrid.SelectionChanged += (s, e) => ShowRow(_scenarioGrid.SelectedItem as Scenario);
        }

        /// <summary>Adds a text column showing the row object's property <paramref name="property"/>.</summary>
        private static void AddColumn(VideoOSTable table, string header, string property, DataGridLength width)
        {
            var columns = table.Columns;
            if (columns == null)
            {
                // Not created by the control itself: give it a list (only if the property can be set).
                var prop = typeof(VideoOSTable).GetProperty("Columns");
                if (prop == null || !prop.CanWrite)
                {
                    return;
                }
                columns = new List<VideoOSTableColumnBase>();
                prop.SetValue(table, columns);
            }
            columns.Add(new VideoOSTableTextColumn
            {
                Header = header,
                ItemTextPropertyName = property,
                Width = width,
                IsSortable = false
            });
        }

        /// <summary>DropDownItems("text", tag, "text", tag, ...): the shown text in Data, our value in Tag.</summary>
        private static List<VideoOSDropDownItem> DropDownItems(params object[] textAndTag)
        {
            var items = new List<VideoOSDropDownItem>();
            for (int i = 0; i + 1 < textAndTag.Length; i += 2)
            {
                items.Add(new VideoOSDropDownItem { Data = textAndTag[i], Tag = textAndTag[i + 1] });
            }
            return items;
        }

        private static VideoOSDropDownItem FindDropDownItem(VideoOSDropDownMedium box, object tag)
        {
            var items = box.ItemsSource as IEnumerable<VideoOSDropDownItem>;
            return items == null ? null : items.FirstOrDefault(i => Equals(i.Tag, tag));
        }

        private static object SelectedTag(VideoOSDropDownMedium box)
        {
            var item = box.SelectedItem as VideoOSDropDownItem;
            return item != null ? item.Tag : null;
        }

        /// <summary>Called by Smart Client right after the panel is shown.</summary>
        public override void Init()
        {
            base.Init();
            try
            {
                Directory.CreateDirectory(SnapshotFolder);
                Directory.CreateDirectory(ScenarioFiles.SuggestionsFolder);

                // As in Milestone's Video Preview sample: pass this view item's WindowInformation so the viewer
                // follows Smart Client's live/playback mode. One viewer per page.
                _imageViewer = NewViewer();
                _liveViewHost.Children.Add(_imageViewer);
                _clipViewer = NewViewer();
                _clipViewHost.Children.Add(_clipViewer);

                _fetcher = new RecordedFrameFetcher();
                _fetcher.FrameReady += OnFrameReadyFromFetcher;
                _fetcher.Problem += OnProblemFromFetcher;

                _closed = false;
                ApplyThemeTextColor();
                // Re-apply when the user switches between dark and light theme (Milestone: "Introduction to Smart
                // Client themes", MessageId.SmartClient.ThemeChangedIndication).
                _themeChangedReceiver = EnvironmentManager.Instance.RegisterReceiver(OnThemeChanged,
                    new MessageIdFilter(MessageId.SmartClient.ThemeChangedIndication));

                _suggestionList.ItemsSource = _suggestions;
                _scenarioGrid.ItemsSource = _rows;
                if (_gateway != null && _gateway.IsLoggedIn)
                {
                    _hafniaState.Text = "Connected to the Hafnia gateway.";
                }

                LoadCameras();
                RestorePage1State();
                if (_selection == null)
                {
                    TryLoadLatestSelection();   // e.g. after restarting Smart Client: page 2 works straight away
                }
                else
                {
                    ShowSelection();
                }
                ShowPage(_page);
                UpdateHafniaControls();
                SetStatus("Ready. Data folder: " + ScenarioLabDefinition.DataFolder);
            }
            catch (Exception ex)
            {
                ReportError("Init", ex);
            }
        }

        private ImageViewerWpfControl NewViewer()
        {
            return new ImageViewerWpfControl(WindowInformation)
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                MinWidth = 100
            };
        }

        /// <summary>Called when the panel is no longer shown (tab closed, logout). Free everything here.</summary>
        public override void Close()
        {
            _closed = true;   // stops a running test from updating this panel (the request itself continues)
            if (_themeChangedReceiver != null)
            {
                EnvironmentManager.Instance.UnRegisterReceiver(_themeChangedReceiver);
                _themeChangedReceiver = null;
            }
            try
            {
                if (_fetcher != null)
                {
                    _fetcher.FrameReady -= OnFrameReadyFromFetcher;
                    _fetcher.Problem -= OnProblemFromFetcher;
                    _fetcher.Dispose();
                    _fetcher = null;
                }
                ReplaceShownFrame(null);

                DisconnectLiveView();
                DisconnectClipView();
                // Milestone's reference: call Close() and Dispose() once completely done with a viewer.
                foreach (var viewer in new[] { _imageViewer, _clipViewer })
                {
                    if (viewer != null)
                    {
                        viewer.Close();
                        viewer.Dispose();
                    }
                }
                _imageViewer = null;
                _clipViewer = null;
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

        #region Pages

        private void OnShowPage1(object sender, RoutedEventArgs e)
        {
            ShowPage(1);
        }

        private void OnShowPage2(object sender, RoutedEventArgs e)
        {
            ShowPage(2);
        }

        /// <summary>
        /// Shows one page and stops the other page's live video: the machine has 4 CPU cores, so we never decode
        /// video nobody can see.
        /// </summary>
        private void ShowPage(int page)
        {
            _page = page;
            _page1.Visibility = page == 1 ? Visibility.Visible : Visibility.Collapsed;
            _page2.Visibility = page == 2 ? Visibility.Visible : Visibility.Collapsed;
            _settingFromCode = true;
            _tabs.SelectedItem = page == 2 ? _tab2 : _tab1;
            _settingFromCode = false;

            if (page == 1)
            {
                DisconnectClipView();
                if (_selectedCamera != null)
                {
                    ConnectLiveView(_selectedCamera);
                }
            }
            else
            {
                DisconnectLiveView();
                if (_selection == null)
                {
                    TryLoadLatestSelection();
                }
                MatchCameras();
                var row = _scenarioGrid.SelectedItem as Scenario;
                if (row != null)
                {
                    _shownRow = null;   // reconnect its clip view: it was disconnected while page 1 was shown
                    ShowRow(row);
                }
                else if (_rows.Count > 0)
                {
                    SelectRow(_rows[0]);   // shows the first clip right away
                }
            }
        }

        #endregion

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
            _allCameras = cameras.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            _cameraList.ItemsSource = _allCameras;
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

        private void OnCameraSelected()
        {
            var camera = _cameraList.SelectedItem as Item;   // "as" = cast, or null if it is not an Item
            if (camera == null || _imageViewer == null)
            {
                return;
            }
            try
            {
                _selectedCamera = camera;
                ConnectLiveView(camera);

                // A different camera: forget the previous camera's frame, and show this one's latest recording.
                ReplaceShownFrame(null);
                _frameImage.Source = null;
                _framePlaceholder.Visibility = Visibility.Visible;
                SetFrameControlsEnabled(true);
                JumpToNow();

                SetStatus("Showing " + camera.Name + ".");
            }
            catch (Exception ex)
            {
                ReportError("Connect to " + camera.Name, ex);
            }
        }

        /// <summary>Order from Milestone's reference: set CameraFQID, then Initialize(), then Connect().</summary>
        private void ConnectLiveView(Item camera)
        {
            if (_imageViewer == null)
            {
                return;
            }
            DisconnectLiveView();
            _liveViewPlaceholder.Visibility = Visibility.Collapsed;
            _imageViewer.CameraFQID = camera.FQID;
            _imageViewer.EnableVisibleHeader = true;
            _imageViewer.MaintainImageAspectRatio = true;
            _imageViewer.Initialize();
            _imageViewer.Connect();
            _viewerConnected = true;
        }

        private void DisconnectLiveView()
        {
            if (_imageViewer != null && _viewerConnected)
            {
                _imageViewer.Disconnect();
                _viewerConnected = false;
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
                object tag = SelectedTag(_rangeBox);
                return tag is int ? (int)tag : 600;
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

        private void OnRangeChanged()
        {
            if (_settingFromCode)
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
                _lastSnapshotCamera = _shownFrame.Camera.Name;
                _snapshotInfo.Text = "Saved " + _shownFrame.Bitmap.Width + " × " + _shownFrame.Bitmap.Height + " px   " + path;

                // A new snapshot starts a new list of scenarios.
                _suggestions.Clear();
                _suggestionsPath = null;
                _nextPageButton.IsEnabled = false;
                RestorePage1State();
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

        #region 3. Scenarios (page 1)

        /// <summary>Puts page 1's step 3 back the way it was (the panel is recreated when the user switches tabs).</summary>
        private void RestorePage1State()
        {
            bool hasSnapshot = _lastSnapshotPath != null;
            bool hasList = _suggestions.Count > 0;
            _loadSuggestionsButton.IsEnabled = hasSnapshot;
            _ownScenarioText.IsEnabled = hasList;
            _ownExpectedBox.IsEnabled = hasList;
            _addOwnButton.IsEnabled = hasList;
            _saveSelectionButton.IsEnabled = hasList;
            _nextPageButton.IsEnabled = _selection != null;

            if (hasSnapshot && _snapshotInfo.Text == "No snapshot yet.")
            {
                _snapshotInfo.Text = "Snapshot: " + _lastSnapshotPath;
            }
            if (hasList)
            {
                _suggestionsInfo.Text = SuggestionsSummary();
            }
            else if (hasSnapshot)
            {
                string expected = ScenarioFiles.SuggestionsPathFor(_lastSnapshotCamera);
                _suggestionsInfo.Text = File.Exists(expected)
                    ? "Suggestions for this view are ready (" + Path.GetFileName(expected) + "). Press Load suggestions."
                    : "No " + Path.GetFileName(expected) + " yet. Run the scene-analysis step on the snapshot, copy its JSON into the suggestions folder, then press Load suggestions.";
            }
        }

        private string SuggestionsSummary()
        {
            int suggested = _suggestions.Count(s => s.Source == Scenario.FromSuggestion);
            int own = _suggestions.Count(s => s.Source == Scenario.FromUser);
            return suggested + " scenarios suggested for this view by the scene-analysis model (" + Path.GetFileName(_suggestionsPath) + ")"
                + (own > 0 ? ", " + own + " of your own" : "") + ". Tick the ones to generate.";
        }

        /// <summary>
        /// Loads C:\ScenarioLab\suggestions\&lt;camera&gt;_scenarios.json for the snapshot's camera. If it is not
        /// there, lets the user pick the file.
        /// </summary>
        private void OnLoadSuggestions(object sender, RoutedEventArgs e)
        {
            if (_lastSnapshotPath == null)
            {
                return;
            }
            try
            {
                string path = ScenarioFiles.SuggestionsPathFor(_lastSnapshotCamera);
                if (!File.Exists(path))
                {
                    var dialog = new Microsoft.Win32.OpenFileDialog
                    {
                        Title = "Scenario suggestions for " + _lastSnapshotCamera,
                        InitialDirectory = ScenarioFiles.SuggestionsFolder,
                        Filter = "Scenario suggestions (*_scenarios.json)|*_scenarios.json|JSON files (*.json)|*.json"
                    };
                    if (dialog.ShowDialog() != true)
                    {
                        return;
                    }
                    path = dialog.FileName;
                }

                List<Scenario> loaded = ScenarioFiles.LoadSuggestions(path);
                _suggestions.Clear();
                foreach (Scenario s in loaded)
                {
                    _suggestions.Add(s);
                }
                _suggestionsPath = path;
                RestorePage1State();
                SetStatus("Loaded " + loaded.Count + " suggestions from " + path);
            }
            catch (Exception ex)
            {
                ReportError("Load suggestions", ex);
            }
        }

        private void OnOpenSuggestionsFolder(object sender, RoutedEventArgs e)
        {
            OpenInExplorer(ScenarioFiles.SuggestionsFolder);
        }

        /// <summary>Adds the user's own scenario as a new, ticked entry with ID &lt;prefix&gt;_u1, _u2, ...</summary>
        private void OnAddOwnScenario(object sender, RoutedEventArgs e)
        {
            string text = _ownScenarioText.Text.Trim();
            if (text.Length == 0 || _suggestionsPath == null)
            {
                SetStatus("Write your scenario in the box first.");
                return;
            }
            string expected = SelectedTag(_ownExpectedBox) as string ?? Scenario.Detect;
            int number = _suggestions.Count(s => s.Source == Scenario.FromUser) + 1;
            var own = new Scenario
            {
                Id = ScenarioFiles.PrefixFromFile(_suggestionsPath) + "_u" + number,
                Title = text,
                Category = "user",
                Where = "",
                Source = Scenario.FromUser,
                Expected = expected,
                IsSelected = true
            };
            _suggestions.Add(own);
            _ownScenarioText.Clear();
            _suggestionsInfo.Text = SuggestionsSummary();
            SetStatus("Added your scenario " + own.Id + ".");
        }

        /// <summary>
        /// Writes the ticked scenarios to &lt;prefix&gt;_scenarios_user.json (the input for video generation) and loads
        /// them into page 2.
        /// </summary>
        private void OnSaveSelection(object sender, RoutedEventArgs e)
        {
            if (_suggestionsPath == null)
            {
                return;
            }
            var ticked = _suggestions.Where(s => s.IsSelected).ToList();
            if (ticked.Count == 0)
            {
                SetStatus("Tick at least one scenario first.");
                return;
            }
            try
            {
                var selection = new ScenarioSelection
                {
                    Camera = _lastSnapshotCamera,
                    Snapshot = _lastSnapshotPath,
                    SnapshotUtc = _lastSnapshotFrameUtc,
                    SuggestionsFile = Path.GetFileName(_suggestionsPath),
                    SavedUtc = DateTime.UtcNow,
                    Scenarios = ticked
                };
                string path = ScenarioFiles.SelectionPathFor(_suggestionsPath);
                ScenarioFiles.SaveSelection(path, selection);

                // Page 2 works on its own copy (read back from the file), so later ticks on page 1 don't change it.
                ShowSelection(ScenarioFiles.LoadSelection(path), path);
                _nextPageButton.IsEnabled = true;
                _selectionInfo.Text = "Saved " + ticked.Count + " scenarios to " + path + ": the input for video generation.";
                SetStatus("Selection saved: " + path);
            }
            catch (Exception ex)
            {
                ReportError("Save selection", ex);
            }
        }

        #endregion

        #region 4. Generated clips in XProtect (page 2)

        private void OnReloadSelection(object sender, RoutedEventArgs e)
        {
            if (!TryLoadLatestSelection())
            {
                SetStatus("No *_scenarios_user.json in " + ScenarioFiles.SuggestionsFolder + " yet: save a selection on page 1.");
            }
        }

        private bool TryLoadLatestSelection()
        {
            try
            {
                string path = ScenarioFiles.LatestSelectionPath();
                if (path == null)
                {
                    return false;
                }
                ShowSelection(ScenarioFiles.LoadSelection(path), path);
                SetStatus("Loaded selection " + path);
                return true;
            }
            catch (Exception ex)
            {
                ReportError("Load selection", ex);
                return false;
            }
        }

        /// <summary>Fills the test table: one row per chosen scenario, then the control row.</summary>
        private void ShowSelection(ScenarioSelection selection, string path)
        {
            _selection = selection;
            _selectionPath = path;
            _rows.Clear();
            foreach (Scenario s in selection.Scenarios)
            {
                _rows.Add(s);
            }
            // The original camera's own (real) recording, from the snapshot moment: the question must also behave
            // right on real footage. Expected defaults to "detect" because the demo camera, vandalism, loops a real
            // incident (a man throws a stone through a window every ~10 s); change it in the row for a quiet camera.
            _rows.Add(new Scenario
            {
                Id = selection.Camera,
                Title = "Original camera: real recording of " + selection.Camera + " from the snapshot moment",
                Category = "original",
                Source = Scenario.Control,
                Expected = Scenario.Detect,
                StartAtUtc = selection.SnapshotUtc
            });
            ShowSelection();
            MatchCameras();
        }

        /// <summary>Shows the loaded selection's snapshot and summary (also after the panel is recreated).</summary>
        private void ShowSelection()
        {
            if (_selection == null)
            {
                return;
            }
            _selectionSummary.Text = "Snapshot of " + _selection.Camera + " at " + _selection.SnapshotUtc.ToString("yyyy-MM-dd HH:mm:ss") + " UTC\n"
                + _selection.Scenarios.Count + " scenarios chosen (" + Path.GetFileName(_selectionPath) + ")";
            _snapshotThumb.Source = LoadImage(_selection.Snapshot);
        }

        private static BitmapImage LoadImage(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    return null;
                }
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;   // read now, so the file is not kept locked
                image.UriSource = new Uri(path);
                image.DecodePixelWidth = 336;
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void OnMatchCameras(object sender, RoutedEventArgs e)
        {
            LoadCameras();
            MatchCameras();
        }

        /// <summary>
        /// Finds each scenario's XProtect camera by name: a camera named exactly like the scenario ID, or starting with
        /// the ID and "_" or " " (vandalism_s1 matches "vandalism_s1_rock_window", not "vandalism_s10"). The control
        /// row uses the snapshot's camera, by exact name.
        /// </summary>
        private void MatchCameras()
        {
            foreach (Scenario row in _rows)
            {
                row.Camera = row.Source == Scenario.Control
                    ? _allCameras.FirstOrDefault(c => string.Equals(c.Name, row.Id, StringComparison.OrdinalIgnoreCase))
                    : FindCameraFor(row.Id);
            }
            int found = _rows.Count(r => r.Camera != null);
            if (_rows.Count > 0)
            {
                SetStatus(found + " of " + _rows.Count + " cameras found."
                    + (found < _rows.Count ? " Missing ones: install their clips (xprotect-setup\\README.md), then press Find cameras again." : ""));
            }
            UpdateHafniaControls();
        }

        private Item FindCameraFor(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }
            return _allCameras.FirstOrDefault(c =>
                string.Equals(c.Name, id, StringComparison.OrdinalIgnoreCase)
                || c.Name.StartsWith(id + "_", StringComparison.OrdinalIgnoreCase)
                || c.Name.StartsWith(id + " ", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Selects a row from code and shows it (does not rely on the table raising its event).</summary>
        private void SelectRow(Scenario row)
        {
            if (_scenarioGrid.SelectedItem != row)
            {
                _scenarioGrid.SelectedItem = row;
            }
            ShowRow(row);
        }

        /// <summary>Shows a row: its clip live on the right, its answer, its expected result in the drop-down.</summary>
        private void ShowRow(Scenario row)
        {
            if (row == null || row == _shownRow)
            {
                UpdateHafniaControls();
                return;
            }
            _shownRow = row;
            _hafniaAnswer.Text = row.Answer ?? (row.Camera == null
                ? "No XProtect camera for this scenario yet. Expected name: " + row.Id + " (or " + row.Id + "_something)."
                : "Not tested yet.");
            _settingFromCode = true;
            _rowExpectedBox.SelectedItem = FindDropDownItem(_rowExpectedBox, row.Expected);
            _settingFromCode = false;
            if (_page == 2)
            {
                ConnectClipView(row.Camera);
            }
            UpdateHafniaControls();
        }

        /// <summary>The user changed "Expected for the selected clip": store it and recompute PASS / FAIL.</summary>
        private void OnRowExpectedChanged()
        {
            var row = _scenarioGrid.SelectedItem as Scenario;
            string expected = SelectedTag(_rowExpectedBox) as string;
            if (_settingFromCode || row == null || expected == null || row.Expected == expected)
            {
                return;
            }
            row.Expected = expected;
            if (row.Got == "detected" || row.Got == "not detected")
            {
                row.Result = (row.Got == "detected") == (expected == Scenario.Detect) ? "PASS" : "FAIL";
            }
        }

        private void ConnectClipView(Item camera)
        {
            if (_clipViewer == null)
            {
                return;
            }
            try
            {
                DisconnectClipView();
                if (camera == null)
                {
                    _clipViewPlaceholder.Text = "This scenario has no XProtect camera yet.";
                    _clipViewPlaceholder.Visibility = Visibility.Visible;
                    _clipViewer.Visibility = Visibility.Collapsed;
                    return;
                }
                _clipViewer.Visibility = Visibility.Visible;
                _clipViewPlaceholder.Visibility = Visibility.Collapsed;
                _clipViewer.CameraFQID = camera.FQID;
                _clipViewer.EnableVisibleHeader = true;
                _clipViewer.MaintainImageAspectRatio = true;
                _clipViewer.Initialize();
                _clipViewer.Connect();
                _clipViewerConnected = true;
            }
            catch (Exception ex)
            {
                ReportError("Show " + camera.Name, ex);
            }
        }

        private void DisconnectClipView()
        {
            if (_clipViewer != null && _clipViewerConnected)
            {
                _clipViewer.Disconnect();
                _clipViewerConnected = false;
            }
        }

        #endregion

        #region 5. Test with Hafnia

        private void UpdateHafniaControls()
        {
            bool connected = _gateway != null && _gateway.IsLoggedIn && _promptId != null;
            var row = _scenarioGrid.SelectedItem as Scenario;
            int runnable = _rows.Count(r => r.Camera != null);
            _runSelectedButton.IsEnabled = connected && !_testRunning && row != null && row.Camera != null;
            _runAllButton.IsEnabled = connected && !_testRunning && runnable > 0;
            _runAllButton.Content = "Run all (" + runnable + (runnable == 1 ? " request)" : " requests)");
            _rowExpectedBox.IsEnabled = row != null && !_testRunning;
        }

        private int WindowSeconds
        {
            get
            {
                object tag = SelectedTag(_windowBox);
                return tag is int ? (int)tag : 10;
            }
        }

        /// <summary>Logs in to the gateway (password used once, then forgotten), finds the saved prompt, shows the allowance.</summary>
        private async void OnConnectHafnia(object sender, RoutedEventArgs e)
        {
            // "async void" is only allowed for event handlers: an exception escaping it would crash Smart Client,
            // so everything is inside try/catch.
            // Milestone's password box: read once here; the text stays masked in the box until the panel closes.
            string password = _gatewayPassword.Password ?? string.Empty;
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

        private async void OnRunSelected(object sender, RoutedEventArgs e)
        {
            var row = _scenarioGrid.SelectedItem as Scenario;
            if (row == null || row.Camera == null)
            {
                return;
            }
            await RunRowsAsync(new List<Scenario> { row });
        }

        private async void OnRunAll(object sender, RoutedEventArgs e)
        {
            await RunRowsAsync(_rows.Where(r => r.Camera != null).ToList());
        }

        /// <summary>One Hafnia request per row, one after the other. Stops early if the daily cap is reached.</summary>
        private async Task RunRowsAsync(List<Scenario> rows)
        {
            if (_testRunning || _gateway == null || _promptId == null || rows.Count == 0)
            {
                return;
            }
            string question = _detectionQuestion.Text.Trim();
            if (question.Length == 0)
            {
                _runProgress.Text = "Write a detection question first.";
                return;
            }
            int seconds = WindowSeconds;

            _testRunning = true;
            UpdateHafniaControls();
            try
            {
                foreach (Scenario row in rows)
                {
                    row.ClearResult();
                }
                for (int i = 0; i < rows.Count && !_closed; i++)
                {
                    string prefix = rows.Count > 1 ? "Clip " + (i + 1) + " of " + rows.Count + ", " : "";
                    bool goOn = await RunOneAsync(rows[i], question, seconds, prefix);
                    if (!goOn)
                    {
                        break;
                    }
                }
                if (!_closed)
                {
                    int pass = rows.Count(r => r.Result == "PASS");
                    if (rows.Count > 1)
                    {
                        _runProgress.Text = "Done: " + pass + " of " + rows.Count + " PASS.";
                    }
                    _hafniaState.Text = "Connected. " + DescribeQuota(await _gateway.GetQuotaAsync());
                }
            }
            catch (Exception ex)
            {
                EnvironmentManager.Instance.Log(true, "ScenarioLab", "Hafnia run failed: " + ex);
                if (!_closed)
                {
                    _runProgress.Text = "Stopped: " + ex.Message;
                }
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

        /// <summary>
        /// Sends ONE Hafnia request for one row, then reads the answer every 3 seconds (reading is free) until it is
        /// done. Clip window: the control row starts at the snapshot moment; a generated clip loops forever on its
        /// camera, so it uses the latest recorded seconds. Returns false when the run of rows should stop.
        /// </summary>
        private async Task<bool> RunOneAsync(Scenario row, string question, int seconds, string progressPrefix)
        {
            DateTime latest = DateTime.UtcNow.AddSeconds(-RecordingLagSeconds);
            DateTime startUtc = row.StartAtUtc.HasValue ? row.StartAtUtc.Value : latest.AddSeconds(-seconds);
            DateTime endUtc = startUtc.AddSeconds(seconds);
            if (endUtc > latest)
            {
                endUtc = latest;
                startUtc = endUtc.AddSeconds(-seconds);
            }

            row.Question = question;
            row.StartUtc = startUtc;
            row.EndUtc = endUtc;
            row.Got = "sending…";
            SelectRow(row);
            string where = progressPrefix + row.CameraLabel + ", " + startUtc.ToString("HH:mm:ss") + "–" + endUtc.ToString("HH:mm:ss") + " UTC: ";

            try
            {
                // Quota guard: read today's count (free) and refuse above the cap.
                Quota quota = await _gateway.GetQuotaAsync();
                if (quota != null && quota.Used >= DailyRequestCap)
                {
                    FinishRow(row, "not sent", "", "Not sent: " + quota.Used + " of " + quota.Limit + " requests used today (cap " + DailyRequestCap + ").", where);
                    return false;
                }

                // XProtect camera ID = the camera item's ObjectId (the same ID the REST API and MilestonePSTools show).
                string cameraId = row.Camera.FQID.ObjectId.ToString("D").ToUpperInvariant();
                Summary summary = await _gateway.CreateSummaryAsync(_promptId, question + "\n" + KeywordInstruction, cameraId, startUtc, endUtc);
                row.RequestId = summary.Id;

                DateTime deadline = DateTime.UtcNow.AddMinutes(10);   // kit: stop waiting after 10 minutes
                while (!summary.IsFinished && DateTime.UtcNow < deadline)
                {
                    if (_closed)
                    {
                        return false;   // panel closed: the request finishes on the gateway; its answer is in History
                    }
                    row.Got = Describe(summary.Progress);
                    _runProgress.Text = where + row.Got;
                    await Task.Delay(TimeSpan.FromSeconds(3));
                    summary = await _gateway.GetSummaryAsync(summary.Id);
                }

                if (summary.Progress == "Completed")
                {
                    string got = Verdict(summary.Response);
                    string result = got == "unclear" ? "?" : (got == "detected") == (row.Expected == Scenario.Detect) ? "PASS" : "FAIL";
                    FinishRow(row, got, result, summary.Response, where);
                }
                else if (summary.Progress == "Failed")
                {
                    FinishRow(row, "failed", "?", "The request failed: " + summary.LastError, where);
                }
                else
                {
                    FinishRow(row, "no answer yet", "?", "Still running after 10 minutes: see Summarization, History in Smart Client.", where);
                }
                return true;
            }
            catch (Exception ex)
            {
                FinishRow(row, "error", "?", ex.Message, where);
                EnvironmentManager.Instance.Log(true, "ScenarioLab", "Hafnia test failed: " + ex);
                if (!_gateway.IsLoggedIn)
                {
                    _hafniaState.Text = "Session ended: press Connect again.";
                    return false;
                }
                return true;
            }
        }

        /// <summary>Writes the outcome into the row, shows it, and appends it to C:\ScenarioLab\runs.jsonl.</summary>
        private void FinishRow(Scenario row, string got, string result, string answer, string where)
        {
            row.Got = got;
            row.Result = result;
            row.Answer = answer;
            if (!_closed)
            {
                if (_scenarioGrid.SelectedItem == row)
                {
                    _hafniaAnswer.Text = answer;
                }
                _runProgress.Text = where + got + (result.Length > 0 ? "  →  " + result : "");
            }
            try
            {
                var line = new JavaScriptSerializer().Serialize(new Dictionary<string, object>
                {
                    { "local_time", DateTime.Now.ToString("s") }, { "scenario_id", row.Id }, { "scenario", row.Title },
                    { "camera", row.CameraLabel }, { "start_utc", row.StartUtc.ToString("o") }, { "end_utc", row.EndUtc.ToString("o") },
                    { "question", row.Question }, { "expected", row.Expected }, { "got", got }, { "result", result },
                    { "request_id", row.RequestId }, { "answer", answer }
                });
                File.AppendAllText(RunLogPath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                EnvironmentManager.Instance.Log(true, "ScenarioLab", "Could not write " + RunLogPath + ": " + ex.Message);
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

        /// <summary>
        /// Smart Client themes WPF *controls* automatically (Button, TextBox, ...), but plain text (TextBlock) is not a
        /// control, so it stayed black on the dark background. TextBlocks inherit the panel's Foreground, so set it to
        /// the theme's TextColor (ClientControl.Instance.Theme, MIP reference "Theme Class").
        /// </summary>
        private void ApplyThemeTextColor()
        {
            try
            {
                // The reference lists TextColor as "Color" without saying which framework's Color type it is.
                // Both System.Drawing.Color and WPF's Color have A, R, G, B bytes, so read them dynamically.
                dynamic color = ClientControl.Instance.Theme.TextColor;
                byte a = (byte)color.A, r = (byte)color.R, g = (byte)color.G, b = (byte)color.B;
                if (a > 0)
                {
                    Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(a, r, g, b));
                }
            }
            catch (Exception ex)
            {
                EnvironmentManager.Instance.Log(true, "ScenarioLab", "Theme text color not applied: " + ex.Message);
            }
        }

        private object OnThemeChanged(Message message, FQID destination, FQID sender)
        {
            Dispatcher.BeginInvoke(new Action(ApplyThemeTextColor));
            return null;
        }

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
