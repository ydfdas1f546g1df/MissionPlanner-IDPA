using MissionPlanner.Plugin;

namespace ImageOverlayPlugin
{
    internal class ImageOverlayPlugin : Plugin
    {
        public override string Name { get; } = "ImageOverlayPlugin";
        public override string Version { get; } = "0.1";
        public override string Author { get; } = "IDPA";
        public override bool Init() { return true; }
        public override bool Loaded()
        {
            return true;
        }
        public override bool Exit() { return true; }
    }
}
