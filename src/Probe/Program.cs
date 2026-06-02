using System.Diagnostics;
using Tiger;

string game = @"A:\Steam\steamapps\common\Marathon";
string pkgDir = Path.Combine(game, "packages");
string oodle = Path.Combine(game, "bin", "x64", "oo2core_9_win64.dll");
string vgmExe = @"C:\Users\Casey\Desktop\MarathonAudio\tools\vgmstream\vgmstream-cli.exe";
string temp = Path.Combine(Path.GetTempPath(), "mara_probe");

var sw = Stopwatch.StartNew();
var mgr = new PackageManager(pkgDir, oodle);
mgr.Index((p, msg) => Console.Write($"\r{p:P0} {msg}                              "));
Console.WriteLine();
Console.WriteLine($"Indexed {mgr.Sounds.Count} sounds in {mgr.PackageCount} packages ({sw.ElapsedMilliseconds} ms)");

// take a sound, read it, parse header, decode with vgmstream
var s = mgr.Sounds[100];
var hdr = mgr.LoadHeader(s);
Console.WriteLine($"Sample: tag={s.TagId} pkg={s.PackageName} size={s.Size} " +
    $"codec={hdr.CodecName} ch={hdr.Channels} rate={hdr.SampleRate}");

string wem = mgr.ExtractWemToTemp(s, temp);
var vgm = new Vgmstream(vgmExe);
Console.WriteLine($"vgmstream available: {vgm.Available}");
var meta = vgm.GetMetadata(wem);
if (meta != null)
    Console.WriteLine($"vgmstream meta: {meta.Channels}ch {meta.SampleRate}Hz {meta.Seconds:F2}s [{meta.Encoding}]");
string wav = Path.Combine(temp, s.TagId + ".wav");
bool ok = vgm.DecodeToWav(wem, wav);
Console.WriteLine($"Decode -> {wav}: {ok} ({(ok ? new FileInfo(wav).Length : 0)} bytes)");

// soundbank build timing
var sw2 = Stopwatch.StartNew();
mgr.BuildSoundbanks((p, msg) => Console.Write($"\r{p:P0} {msg}                    "));
Console.WriteLine();
int grouped = mgr.Sounds.Count(x => x.SoundbankTag != 0);
Console.WriteLine($"Soundbanks: {mgr.Soundbanks.Count} banks, {grouped}/{mgr.Sounds.Count} sounds grouped ({sw2.ElapsedMilliseconds} ms)");
foreach (var b in mgr.Soundbanks.Take(5))
    Console.WriteLine($"  {b.Display}: {b.Count} sounds");
