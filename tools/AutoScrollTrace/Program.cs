using Smb3Editor.Core;

if (args.Length != 2) throw new ArgumentException("Usage: AutoScrollTrace <source.nes> <output.nes>");
var source = RomImage.Load(args[0]);
if (!source.IsSuccess) throw new InvalidOperationException(string.Join(Environment.NewLine, source.Diagnostics));
var sourceImage = source.Value!;
var w12 = Smb3LevelCodec.Decode(sourceImage, sourceImage.Profile.Levels["W1-2"]);
if (!w12.IsSuccess) throw new InvalidOperationException(string.Join(Environment.NewLine, w12.Diagnostics));

var controller = w12.Value!.Enemies[0] with { Id = 211, X = 0x03, Y = 0x16 };
var project = ProjectDocumentV2.Create(sourceImage).WithArea(w12.Value with
{
    Enemies = w12.Value.Enemies.Select(enemy => enemy.Index == controller.Index ? controller : enemy).ToArray()
}) with
{
    Patches = new PatchSettings(ContinuousAutoScroll: new PatchSetting(LevelOverrides: new Dictionary<string, bool> { ["W1-2"] = true }))
};
var direct = new DirectLevelTestBuilder().Build(project, sourceImage, sourceImage.Profile.Levels["W1-2"]);
if (!direct.IsSuccess) throw new InvalidOperationException(string.Join(Environment.NewLine, direct.Diagnostics));
File.WriteAllBytes(args[1], direct.Value!.RomBytes);
