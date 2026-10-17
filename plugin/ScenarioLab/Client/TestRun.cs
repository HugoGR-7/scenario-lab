using System;

namespace ScenarioLab.Client
{
    /// <summary>
    /// One row of the test table: one detection question asked about one clip.
    /// Public with public properties because WPF's DataGrid reads them by name ({Binding Time}, ...).
    /// </summary>
    public class TestRun
    {
        public string Time { get; set; }          // local time the test was started, for the table
        public string Camera { get; set; }
        public string Clip { get; set; }          // the clip's time window, UTC
        public string Expected { get; set; }      // "detect" or "no detect"
        public string Got { get; set; }           // "detected", "not detected", "unclear", "failed", ...
        public string Result { get; set; }        // "PASS", "FAIL" or "?"

        // Not shown as columns; shown below the table when the row is selected, and written to runs.jsonl.
        public string Question { get; set; }
        public string Answer { get; set; }
        public string RequestId { get; set; }
        public DateTime StartUtc { get; set; }
        public DateTime EndUtc { get; set; }
    }
}
