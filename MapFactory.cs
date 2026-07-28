using BruTile.Predefined;
using BruTile.Web;
using Mapsui.Tiling.Layers;

namespace GPSet;

internal static class MapFactory
{
    private const string GoogleHybridUrl =
        "https://mt1.google.com/vt/lyrs=y&x={x}&y={y}&z={z}";

    public static Mapsui.Map CreateGoogleHybridMap()
    {
        var tileSource = new HttpTileSource(
            new GlobalSphericalMercator(),
            GoogleHybridUrl,
            name: "Google Hybrid");

        var map = new Mapsui.Map();
        map.Widgets.Clear();
        map.Layers.Add(new TileLayer(tileSource)
        {
            Name = "Google Hybrid"
        });
        return map;
    }
}
