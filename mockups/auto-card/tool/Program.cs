// Mockup renderer: draws an SVG with Svg.Skia (as Sloperator's own SVG viewer does, SvgFile.Open) to a PNG.
//   render fonts <dir>               writes the Inter TTFs bundled in Avalonia.Fonts.Inter to <dir>
//   render <in.svg> <out.png> [scale] draws the SVG; Inter is given to Svg.Skia from those files when the
//                                     SVG's own @font-face isn't taken
using System.Reflection;
using SkiaSharp;
using Svg.Skia;

if (args.Length >= 2 && args[0] == "fonts")
{
    var asm = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "Avalonia.Fonts.Inter.dll"));
    using var res = asm.GetManifestResourceStream("!AvaloniaResources") ?? throw new Exception("no resources");
    var reader = new BinaryReader(res);
    var indexLength = reader.ReadInt32();
    var version = reader.ReadInt32();
    var count = reader.ReadInt32();
    var entries = new List<(string Path, int Offset, int Size)>();
    for (var i = 0; i < count; i++)
        entries.Add((reader.ReadString(), reader.ReadInt32(), reader.ReadInt32()));
    Directory.CreateDirectory(args[1]);
    foreach (var (path, offset, size) in entries)
    {
        res.Position = 4 + indexLength + offset;
        var bytes = reader.ReadBytes(size);
        var file = Path.Combine(args[1], Path.GetFileName(path));
        File.WriteAllBytes(file, bytes);
        Console.WriteLine($"{path} {size} (index v{version})");
    }
    return;
}

if (args.Length >= 4 && args[0] == "mock")
{
    Mock.Run(args[1], args[2], args[3]);
    return;
}

var input = args[0];
var output = args[1];
var scale = args.Length > 2 ? float.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 1f;
var fontDir = Environment.GetEnvironmentVariable("MOCK_FONTS");
using var svg = new SKSvg();
if (fontDir is not null && Directory.Exists(fontDir))
    svg.Settings.TypefaceProviders?.Insert(0, new InterProvider(fontDir));
var parameters = new Svg.Model.SvgParameters(null, null, null, new Svg.SvgDocumentLoadOptions { ExternalResources = Svg.SvgExternalResourcePolicy.SameOrigin });
using var stream = File.OpenRead(input);
var picture = svg.Load(stream, parameters, new Uri(Path.GetFullPath(input))) ?? throw new Exception("no picture");
var b = picture.CullRect;
var bitmap = new SKBitmap(new SKImageInfo((int)Math.Round(b.Width * scale), (int)Math.Round(b.Height * scale), SKColorType.Bgra8888, SKAlphaType.Premul));
using (var canvas = new SKCanvas(bitmap))
{
    canvas.Clear(SKColors.Transparent);
    canvas.Scale(scale);
    canvas.Translate(-b.Left, -b.Top);
    canvas.DrawPicture(picture);
}
using (var file = File.Create(output))
    bitmap.Encode(file, SKEncodedImageFormat.Png, 100);
Console.WriteLine($"{output} {bitmap.Width}x{bitmap.Height}");

/// <summary>Inter by weight from the TTFs written by "fonts"; any other family is left to the next provider.</summary>
sealed class InterProvider(string dir) : Svg.Skia.TypefaceProviders.ITypefaceProvider
{
    private readonly Dictionary<string, SKTypeface> _cache = new();

    public SKTypeface? FromFamilyName(string fontFamily, SKFontStyleWeight fontWeight, SKFontStyleWidth fontWidth, SKFontStyleSlant fontStyle)
    {
        foreach (var family in fontFamily.Split(','))
        {
            var name = family.Trim().Trim('\'', '"');
            if (!name.Equals("Inter", StringComparison.OrdinalIgnoreCase))
                continue;
            var w = (int)fontWeight;
            var file = w >= 700 ? "Inter-Bold.ttf" : w >= 600 ? "Inter-SemiBold.ttf" : w >= 500 ? "Inter-Medium.ttf" : "Inter-Regular.ttf";
            var path = Path.Combine(dir, file);
            if (!File.Exists(path))
                path = Directory.GetFiles(dir, "Inter*.ttf").First();
            if (!_cache.TryGetValue(path, out var tf))
                _cache[path] = tf = SKTypeface.FromFile(path);
            return tf;
        }
        return null;
    }
}
