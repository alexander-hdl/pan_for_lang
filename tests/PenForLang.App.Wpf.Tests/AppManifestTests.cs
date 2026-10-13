using System.IO;
using System.Xml.Linq;

namespace PenForLang.App.Wpf.Tests;

public class AppManifestTests
{
    [Fact]
    public void Manifest_declares_per_monitor_v2_dpi_awareness()
    {
        // ARCHITECTURE.md §7.3 (A12): без PerMonitorV2 координаты пера на мониторах с разным DPI неверны.
        var doc = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "app.manifest"));

        XElement? dpiAwareness = doc.Descendants()
            .SingleOrDefault(e => e.Name.LocalName == "dpiAwareness");

        Assert.NotNull(dpiAwareness);
        Assert.Equal("PerMonitorV2", dpiAwareness.Value.Trim());
    }
}
