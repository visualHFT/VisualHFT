using Newtonsoft.Json;
using VisualHFT.Studies.VPIN.Model;
using BinanceSettings = MarketConnectors.Binance.Model.PlugInSettings;

namespace VisualHFT.Desktop.Tests;

public class SettingsAndVpinTests
{
    [Fact]
    public void BinanceSettings_MissingFuturesSymbols_StillDeserializes()
    {
        const string legacy = """{"ApiKey":"k","Symbols":["BTCUSDT"],"DepthLevels":20,"UpdateIntervalMs":100}""";
        var settings = JsonConvert.DeserializeObject<BinanceSettings>(legacy);

        Assert.NotNull(settings);
        Assert.Null(settings.FuturesSymbols);
        Assert.Equal("BTCUSDT", Assert.Single(settings.Symbols));

        settings.FuturesSymbols = new List<string> { "ETHUSDT" };
        var roundTrip = JsonConvert.DeserializeObject<BinanceSettings>(JsonConvert.SerializeObject(settings));
        Assert.NotNull(roundTrip);
        Assert.Equal("ETHUSDT", Assert.Single(roundTrip.FuturesSymbols!));
        Assert.Equal("BTCUSDT", Assert.Single(roundTrip.Symbols));
    }

    [Fact]
    public void VolumeFloor_UsesConfiguredSize_UntilTheSampleIsFull()
    {
        var floor = new VpinVolumeFloor();
        floor.Add(4m);
        Assert.False(floor.IsReady);
        Assert.Equal(1m, floor.Recommend(1m));
        Assert.Equal(0m, floor.Recommend(0m));
    }

    [Fact]
    public void VolumeFloor_RaisesABucketThatCannotHoldEnoughPrints()
    {
        var floor = new VpinVolumeFloor();
        for (var i = 0; i < VpinVolumeFloor.SampleCount; i++)
            floor.Add(i % 2 == 0 ? 1m : 3m);

        Assert.True(floor.IsReady);
        Assert.Equal(2m, floor.MedianPrint);
        Assert.Equal(VpinVolumeFloor.PrintsPerBucket * 2m, floor.Recommend(1m));
        Assert.Equal(80m, floor.Recommend(80m));
    }

    [Fact]
    public void VolumeFloor_IgnoresNonPositivePrints_AndFurtherSamplesAfterReady()
    {
        var floor = new VpinVolumeFloor();
        floor.Add(0m);
        floor.Add(-1m);
        Assert.False(floor.IsReady);

        for (var i = 0; i < VpinVolumeFloor.SampleCount; i++)
            floor.Add(2m);
        floor.Add(100m);

        Assert.Equal(2m, floor.MedianPrint);
        Assert.Equal(40m, VpinVolumeFloor.Effective(1m, floor.MedianPrint));
    }

    [Fact]
    public void VolumeFloor_MedianOfAnEmptyList_IsZero()
    {
        Assert.Equal(0m, VpinVolumeFloor.Median(null!));
        Assert.Equal(0m, VpinVolumeFloor.Median(Array.Empty<decimal>()));
        Assert.Equal(2.5m, VpinVolumeFloor.Median(new[] { 1m, 2m, 3m, 4m }));
    }
}
