using System;
using System.IO;
using System.Numerics;
using System.Linq;
using System.Collections.Generic;
using YamaEngine.DCC;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class DccTests
    {
        public static void TestDccConnectorCatalogKeepsExternalToolsOutOfCore()
        {
            var catalog = DccConnectorCatalog.CreateDefault();

            AssertTrue(catalog.Connectors.Any(c => c.Application == DCCApplication.Blender51), "Connector catalog should include Blender 5.1.");
            AssertTrue(catalog.Connectors.Any(c => c.Application == DCCApplication.Max2025), "Connector catalog should include 3ds Max.");
            AssertTrue(catalog.Connectors.Any(c => c.Application == DCCApplication.Maya2025), "Connector catalog should include Maya.");
            AssertTrue(catalog.Connectors.All(c => c.Boundary == DccIntegrationBoundary.ExternalTool), "DCC tools must stay outside the self-developed engine core.");
            AssertTrue(catalog.CorePolicy.RequiresSelfDevelopedRuntimeCore, "Core policy should require self-developed runtime core.");
        }
    }
}
