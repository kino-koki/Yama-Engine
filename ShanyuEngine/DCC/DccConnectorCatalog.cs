namespace ShanyuEngine.DCC
{
    public sealed class DccConnectorCatalog
    {
        public IReadOnlyList<DccConnectorDescriptor> Connectors { get; init; } = Array.Empty<DccConnectorDescriptor>();
        public DccCorePolicy CorePolicy { get; init; } = new();

        public static DccConnectorCatalog CreateDefault()
        {
            return new DccConnectorCatalog
            {
                CorePolicy = new DccCorePolicy
                {
                    RequiresSelfDevelopedRuntimeCore = true,
                    AllowsExternalAuthoringTools = true,
                    PolicyName = "External DCC authoring, self-developed runtime core"
                },
                Connectors = new List<DccConnectorDescriptor>
                {
                    new(DCCApplication.Blender51, "Blender 5.1", DccIntegrationBoundary.ExternalTool),
                    new(DCCApplication.Blender42, "Blender 4.2", DccIntegrationBoundary.ExternalTool),
                    new(DCCApplication.Maya2025, "Autodesk Maya 2025", DccIntegrationBoundary.ExternalTool),
                    new(DCCApplication.Max2025, "Autodesk 3ds Max 2025", DccIntegrationBoundary.ExternalTool),
                    new(DCCApplication.Houdini20, "SideFX Houdini 20", DccIntegrationBoundary.ExternalTool),
                    new(DCCApplication.SubstancePainter, "Substance Painter", DccIntegrationBoundary.ExternalTool),
                    new(DCCApplication.RizomUV, "RizomUV", DccIntegrationBoundary.ExternalTool),
                    new(DCCApplication.Zbrush2025, "Maxon ZBrush 2025", DccIntegrationBoundary.ExternalTool)
                }
            };
        }
    }

    public sealed record DccConnectorDescriptor(
        DCCApplication Application,
        string DisplayName,
        DccIntegrationBoundary Boundary);

    public sealed class DccCorePolicy
    {
        public string PolicyName { get; set; } = string.Empty;
        public bool RequiresSelfDevelopedRuntimeCore { get; set; }
        public bool AllowsExternalAuthoringTools { get; set; }
    }

    public enum DccIntegrationBoundary
    {
        ExternalTool,
        RuntimeCore
    }
}
