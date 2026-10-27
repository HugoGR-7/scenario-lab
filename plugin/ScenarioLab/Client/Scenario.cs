using System;
using System.ComponentModel;
using VideoOS.Platform;

namespace ScenarioLab.Client
{
    /// <summary>
    /// One scenario: a row of the checklist on page 1 and a row of the test table on page 2.
    ///
    /// Public with public properties because WPF reads them by name ({Binding Title}, ...).
    /// INotifyPropertyChanged = WPF's way of hearing "this value changed, redraw it" (like an observer/callback in
    /// Python). Without it the table would only update after a full refresh.
    /// </summary>
    public class Scenario : INotifyPropertyChanged
    {
        // The two values of Expected, also the choices of the "Expected" drop-down in the test table.
        public const string Detect = "detect";
        public const string NoDetect = "no detect";
        public static readonly string[] ExpectedChoices = { Detect, NoDetect };

        // Source values.
        public const string FromSuggestion = "suggested";
        public const string FromUser = "user";
        public const string Control = "control";   // the original camera's real recording, from the snapshot moment

        // ---- Saved in <camera>_scenarios_user.json (the hand-off to video generation) ----
        public string Id { get; set; }          // e.g. vandalism_s1 (suggestion 1) or vandalism_u1 (the user's first own scenario)
        public string Title { get; set; }
        public string Category { get; set; }
        public string Where { get; set; }
        public string Source { get; set; }

        private string _expected = Detect;
        public string Expected
        {
            get { return _expected; }
            set { _expected = value; Changed("Expected"); }
        }

        // ---- Page 1: ticked or not ----
        private bool _isSelected;
        public bool IsSelected
        {
            get { return _isSelected; }
            set { _isSelected = value; Changed("IsSelected"); }
        }

        /// <summary>Second line of the checklist row: "vandalism · on the brick wall near the windows".</summary>
        public string Details
        {
            get
            {
                string details = Source == FromUser ? "your own" : (Category ?? "");
                if (!string.IsNullOrEmpty(Where))
                {
                    details += " · " + Where;
                }
                return details + "   (" + (Expected == NoDetect ? "should NOT trigger" : "should trigger") + ")";
            }
        }

        // ---- Page 2: the XProtect camera playing this scenario's clip, and the latest test ----
        private Item _camera;                   // null when no camera with a matching name exists yet
        public Item Camera
        {
            get { return _camera; }
            set { _camera = value; Changed("Camera"); Changed("CameraLabel"); }
        }

        public string CameraLabel
        {
            get { return Camera != null ? Camera.Name : "missing: " + Id; }
        }

        // Control row only: the clip window starts at the snapshot moment.
        public DateTime? StartAtUtc { get; set; }

        private string _got = "";
        public string Got
        {
            get { return _got; }
            set { _got = value; Changed("Got"); }
        }

        private string _result = "";
        public string Result
        {
            get { return _result; }
            set { _result = value; Changed("Result"); }
        }

        // Not shown as columns: shown below the table for the selected row, and written to runs.jsonl.
        public string Answer { get; set; }
        public string RequestId { get; set; }
        public string Question { get; set; }
        public DateTime StartUtc { get; set; }
        public DateTime EndUtc { get; set; }

        public void ClearResult()
        {
            Got = "";
            Result = "";
            Answer = null;
            RequestId = null;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void Changed(string property)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
            if (property == "Expected")
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Details"));
            }
        }
    }
}
