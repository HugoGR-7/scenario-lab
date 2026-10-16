using VideoOS.Platform.Client;

namespace ScenarioLab.Client
{
    /// <summary>
    /// One manager per shown instance of our view item. It holds that instance's saved properties and creates
    /// the WPF (Windows Presentation Foundation) control that draws it.
    /// </summary>
    public class ScenarioLabViewItemManager : ViewItemManager
    {
        public ScenarioLabViewItemManager() : base("ScenarioLabViewItemManager")
        {
        }

        public override ViewItemWpfUserControl GenerateViewItemWpfUserControl()
        {
            return new ScenarioLabViewItemWpfUserControl();
        }
    }
}
