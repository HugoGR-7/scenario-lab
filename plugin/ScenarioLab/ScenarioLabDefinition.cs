using System;
using System.Collections.Generic;
using System.Drawing;
using ScenarioLab.Client;
using VideoOS.Platform;
using VideoOS.Platform.Client;

namespace ScenarioLab
{
    /// <summary>
    /// Entry point of the plug-in. Smart Client finds this class (it derives from PluginDefinition) when it loads
    /// ScenarioLab.dll, and asks it which parts the plug-in adds: here one workspace (a tab) and one view item
    /// (the panel shown inside that tab). Structure copied from Milestone's "Smart Client Workspace" sample.
    /// </summary>
    public class ScenarioLabDefinition : PluginDefinition
    {
        // Our own IDs. Each part of a plug-in needs a GUID (globally unique identifier) that no other plug-in uses.
        // Generated fresh for this project; never copy IDs from a sample.
        internal static readonly Guid PluginId = new Guid("4988b709-b8b4-4c5b-8eb6-e7efe5426b3d");
        internal static readonly Guid WorkSpaceId = new Guid("b9dfa669-5a66-4f95-a096-446311e74813");
        internal static readonly Guid ViewItemId = new Guid("63cf953e-84d6-4a7b-8b8f-96b3771819eb");

        internal const string ProductName = "Scenario Lab";

        // Where the plug-in keeps its files on the machine (C:, not D:, which is wiped when the machine stops).
        internal const string DataFolder = @"C:\ScenarioLab";

        // Milestone's note: plug-ins are constructed when the application starts, so constructors must only touch
        // our own DLL. Real work happens in Init(), after the user has logged in.
        private readonly List<WorkSpacePlugin> _workSpacePlugins = new List<WorkSpacePlugin>();
        private readonly List<ViewItemPlugin> _viewItemPlugins = new List<ViewItemPlugin>();

        public override void Init()
        {
            if (EnvironmentManager.Instance.EnvironmentType == EnvironmentType.SmartClient)
            {
                _workSpacePlugins.Add(new ScenarioLabWorkSpacePlugin());
                _viewItemPlugins.Add(new ScenarioLabViewItemPlugin());
            }
        }

        public override void Close()
        {
            _workSpacePlugins.Clear();
            _viewItemPlugins.Clear();
        }

        // "=>" defines a read-only property in one line (C#): like a Python @property returning a constant.
        public override Guid Id => PluginId;
        public override string Name => ProductName;
        public override string Manufacturer => "Hugo González Romo";
        public override string VersionString => "0.1.0";

        // Milestone's generic plug-in icon, as in their samples.
        public override Image Icon => VideoOS.Platform.UI.Util.ImageList.Images[VideoOS.Platform.UI.Util.PluginIx];

        public override List<WorkSpacePlugin> WorkSpacePlugins => _workSpacePlugins;
        public override List<ViewItemPlugin> ViewItemPlugins => _viewItemPlugins;
    }
}
