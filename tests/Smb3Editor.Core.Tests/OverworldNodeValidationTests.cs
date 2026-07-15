using Smb3Editor.Core;

namespace Smb3Editor.Core.Tests;

public sealed class OverworldNodeValidationTests
{
    [Fact]
    public void ReportsEveryInvalidVanillaNodeFieldWithItsStoredValue()
    {
        var world = new OverworldDocument(0, 0, 0, 0, 0, 0, false, new byte[OverworldDocument.ScreenWidth * OverworldDocument.ScreenHeight], [], []);
        var node = new OverworldLevelPointer(12, 1, 16, 1, 16, 0x9000, 0xE000, 0, 0, 0, 0);

        var error = Smb3OverworldSerializer.GetNodeValidationError(world, node);

        Assert.Equal("Map position is outside World 1: screen 2 (World 1 has screens 1-1), column 17 (valid columns are 1-16), row 2 (valid rows are 3-11). Drag the red node onto the map. Destination object set is not usable: object set 16 (valid sets are 0-15). Switch to Nodes, click the red node, then choose its destination again.", error);
    }

    [Fact]
    public void AcceptsAnEncodableVanillaNode()
    {
        var world = new OverworldDocument(0, 0, 0, 0, 0, 0, false, new byte[OverworldDocument.ScreenWidth * OverworldDocument.ScreenHeight], [], []);
        var node = new OverworldLevelPointer(0, 0, 15, 10, 15, 0xBFFF, 0xDFFF, 0, 0, 0, 0);

        Assert.Null(Smb3OverworldSerializer.GetNodeValidationError(world, node));
    }

    [Fact]
    public void OptionalUserSuppliedPrg1RomHasNoInvalidMapNodes()
    {
        var path = Environment.GetEnvironmentVariable("SMB3_TEST_ROM");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

        var rom = RomImage.Load(path);
        Assert.True(rom.IsSuccess, string.Join(Environment.NewLine, rom.Diagnostics));
        if (rom.Value!.Profile.Id != "us-prg1") return;

        var maps = Smb3OverworldParser.Parse(rom.Value);
        Assert.True(maps.IsSuccess, string.Join(Environment.NewLine, maps.Diagnostics));
        foreach (var map in maps.Value!)
        foreach (var node in map.LevelPointers)
            Assert.Null(Smb3OverworldSerializer.GetNodeValidationError(map, node));
    }
}
