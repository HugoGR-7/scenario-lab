using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using VideoOS.Platform;
using VideoOS.Platform.Client;
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
    ///   2. shows live video of the selected camera and saves a snapshot of it through XProtect;
    ///   3. saves a scenario text next to the snapshot;
    ///   4. lists and plays local clip files from C:\ScenarioLab\clips.
    /// Placeholders: LLM scenario suggestions, Hafnia results.
    ///
    /// This build sends NO Hafnia requests and raises NO XProtect events.
    /// XProtect calls are taken from Milestone's samples: "Smart Client Workspace" (camera list) and
    /// "Video Preview" (ImageViewerWpfControl, GetCurrentDisplayedImageAsBitmap).
    /// </summary>
    public partial class ScenarioLabViewItemWpfUserControl : ViewItemWpfUserControl
    {
        private static readonly string SnapshotFolder = Path.Combine(ScenarioLabDefinition.DataFolder, "snapshots");
        private static readonly string ClipFolder = Path.Combine(ScenarioLabDefinition.DataFolder, "clips");
        private static readonly string[] ClipExtensions = { ".mp4", ".mov", ".avi", ".wmv", ".mkv" };

        private ImageViewerWpfControl _imageViewer;   // XProtect's video control
        private bool _viewerConnected;
        private Item _selectedCamera;                 // an XProtect configuration item of kind Camera
        private string _lastSnapshotPath;

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

                LoadCameras();
                LoadClips();
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
            try
            {
                _player.Stop();
                _player.Close();
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

                _snapshotButton.IsEnabled = true;
                SetStatus("Showing " + camera.Name + ".");
            }
            catch (Exception ex)
            {
                ReportError("Connect to " + camera.Name, ex);
            }
        }

        #endregion

        #region 2. Snapshot

        private void OnTakeSnapshot(object sender, RoutedEventArgs e)
        {
            if (_selectedCamera == null || _imageViewer == null)
            {
                return;
            }
            try
            {
                // The frame XProtect is currently displaying (with any privacy mask applied). Null until the first
                // image has arrived from the recording server.
                Bitmap displayed = _imageViewer.GetCurrentDisplayedImageAsBitmap();
                if (displayed == null)
                {
                    SetStatus("No image received yet from " + _selectedCamera.Name + ". Wait a second and try again.");
                    return;
                }

                // The viewer keeps its own reference to that bitmap, so save a copy ("using" disposes it afterwards,
                // like a Python "with" block).
                using (var copy = new Bitmap(displayed))
                {
                    string fileName = SafeFileName(_selectedCamera.Name) + "_" + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'") + ".jpg";
                    string path = Path.Combine(SnapshotFolder, fileName);
                    copy.Save(path, ImageFormat.Jpeg);

                    _lastSnapshotPath = path;
                    _snapshotImage.Source = LoadImageWithoutLocking(path);
                    _snapshotInfo.Text = copy.Width + " × " + copy.Height + " px   " + path;
                    _saveScenarioButton.IsEnabled = true;
                    SetStatus("Snapshot saved: " + path);
                }
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

        /// <summary>Loads an image fully into memory so the file is not kept open (and can be moved or deleted).</summary>
        private static BitmapImage LoadImageWithoutLocking(string path)
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }

        #endregion
    }
}
