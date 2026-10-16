using System;
using System.Collections.Generic;
using System.Drawing;
using VideoOS.Platform.Client;

namespace ScenarioLab.Client
{
    /// <summary>
    /// The "Scenario Lab" tab in Smart Client. A workspace owns one view (a layout of rectangles); each rectangle
    /// holds a view item. Ours has a single rectangle filling the whole tab, holding our own panel.
    /// Based on Milestone's "Smart Client Workspace" sample, without its demo timer, shuffling and camera grid.
    /// </summary>
    public class ScenarioLabWorkSpacePlugin : WorkSpacePlugin
    {
        public override Guid Id => ScenarioLabDefinition.WorkSpaceId;

        public override string Name => ScenarioLabDefinition.ProductName;

        // No Setup button on our tab: the layout is fixed by code.
        public override bool IsSetupStateSupported => false;

        public override void Init()
        {
            // Layout coordinates run from 0 to 1000 in both directions (the sample uses the same space),
            // so this one rectangle covers the whole tab.
            ViewAndLayoutItem.Layout = new[] { new Rectangle(0, 0, 1000, 1000) };
            ViewAndLayoutItem.Name = Name;

            // Put our panel (view item) into rectangle 0. The dictionary holds view item properties: none yet.
            ViewAndLayoutItem.InsertViewItemPlugin(0, new ScenarioLabViewItemPlugin(), new Dictionary<string, string>());
        }

        public override void Close()
        {
        }
    }
}
