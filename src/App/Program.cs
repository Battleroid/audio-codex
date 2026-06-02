using Avalonia;
using System;

namespace MarathonAudio.App;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--smoke")
        {
            SmokeTest.Run();
            return;
        }
        if (args.Length > 0 && args[0] == "--transcribe")
        {
            TranscribeCli.Run(args).GetAwaiter().GetResult();
            return;
        }
        if (args.Length > 0 && args[0] == "--corpus")
        {
            var st = Services.AppState.Instance;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            st.BuildIndex((p, m) => { });
            var corp = st.EnsureCorpus((p, m) => { });
            System.Console.WriteLine($"corpus: {corp.WordCount:N0} words, built in {sw.ElapsedMilliseconds} ms");
            foreach (var line in new[] {
                "indiscriminate force from the UESD is equally misguided however",
                "data integrity low data integrity low",
                "leave the airier",
                "and now we're going to be making a new video",
            })
            {
                var r = corp.Correct(line);
                System.Console.WriteLine($"\nASR : {line}\nFIX : {(r is { } h ? $"{h.text}  [{h.score:F3}]" : "(no match)")}");
            }
            return;
        }
        if (args.Length > 0 && args[0] == "--config")
        {
            var c = Services.AppState.Instance.Config;
            System.Console.WriteLine($"GameDir     = {c.GameDir}");
            System.Console.WriteLine($"ExportDir   = {c.ExportDir}");
            System.Console.WriteLine($"WordlistFile= {c.WordlistFile}");
            System.Console.WriteLine($"Volume      = {c.Volume}");
            System.Console.WriteLine($"HasExportDir= {Services.AppState.Instance.HasExportDir}");
            // round-trip test: set a wordlist, save, reload via a fresh JSON read
            return;
        }
        if (args.Length > 0 && args[0] == "--fontcheck")
        {
            BuildAvaloniaApp().SetupWithoutStarting();
            var mono = (Avalonia.Media.FontFamily)Avalonia.Application.Current!.Resources["MonoFont"]!;
            bool monoOk = Avalonia.Media.FontManager.Current.TryGetGlyphTypeface(
                new Avalonia.Media.Typeface(mono), out var gt);
            System.Console.WriteLine($"MonoFont resolves -> {monoOk}, family='{gt?.FamilyName}'");
            foreach (var f in new[] { "PPFraktionMono-Regular.otf", "PPFraktionMono-Medium.otf",
                                      "PPFraktionMono-Bold.otf", "MarathonShapiroWide65.otf" })
            {
                var uri = new System.Uri($"avares://MarathonAudio.App/Assets/Fonts/{f}");
                bool exists = Avalonia.Platform.AssetLoader.Exists(uri);
                long len = 0;
                if (exists) { using var s = Avalonia.Platform.AssetLoader.Open(uri); len = s.Length; }
                System.Console.WriteLine($"{f}: embedded={exists} bytes={len}");
            }
            return;
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
