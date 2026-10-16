using System;
using VideoOS.Platform.Client;
using VideoOS.Platform.UI.Controls;

namespace ScenarioLab.Client
{
    /// <summary>
    /// Describes our view item type to Smart Client: its ID, name, icon, and how to create it.
    /// One plug-in object exists per type; Smart Client calls GenerateViewItemManager() for every instance it shows.
    /// </summary>
    public class ScenarioLabViewItemPlugin : ViewItemPlugin
    {
        private static VideoOSIconSourceBase _icon;

        public ScenarioLabViewItemPlugin()
        {
            // "pack://" addresses a resource compiled into our own DLL (Resources\ScenarioLab.png).
            _icon = new VideoOSIconUriSource { Uri = new Uri("pack://application:,,,/ScenarioLab;component/Resources/ScenarioLab.png") };
        }

        public override Guid Id => ScenarioLabDefinition.ViewItemId;

        public override string Name => ScenarioLabDefinition.ProductName;

        public override VideoOSIconSourceBase IconSource => _icon;

        // Hidden from Smart Client's Setup list: the panel only makes sense inside our own tab.
        public override bool HideSetupItem => true;

        public override ViewItemManager GenerateViewItemManager()
        {
            return new ScenarioLabViewItemManager();
        }

        public override void Init()
        {
        }

        public override void Close()
        {
        }
    }
}
