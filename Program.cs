using System.Text.Encodings.Web;
using System.Text.Json;
using Windows.Foundation;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;

const string Usage = "用法: WinOCR <圖片路徑> [語言代碼，例如 zh-Hant-TW / en-US] [--format json|text]";

var format = "json";
var positional = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--format")
    {
        if (i + 1 >= args.Length)
        {
            Console.Error.WriteLine(Usage);
            return 1;
        }
        format = args[++i].ToLowerInvariant();
    }
    else if (args[i].StartsWith("--format=", StringComparison.Ordinal))
    {
        format = args[i]["--format=".Length..].ToLowerInvariant();
    }
    else
    {
        positional.Add(args[i]);
    }
}

if (positional.Count < 1 || format is not ("json" or "text"))
{
    Console.Error.WriteLine(Usage);
    return 1;
}

var imagePath = Path.GetFullPath(positional[0]);
if (!File.Exists(imagePath))
{
    Console.Error.WriteLine($"找不到檔案: {imagePath}");
    return 1;
}

OcrEngine? engine;
if (positional.Count >= 2)
{
    var language = new Language(positional[1]);
    if (!OcrEngine.IsLanguageSupported(language))
    {
        Console.Error.WriteLine($"不支援的 OCR 語言: {positional[1]}");
        Console.Error.WriteLine("可用語言: " + string.Join(", ", OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag)));
        return 1;
    }
    engine = OcrEngine.TryCreateFromLanguage(language);
}
else
{
    engine = OcrEngine.TryCreateFromUserProfileLanguages();
}

if (engine is null)
{
    Console.Error.WriteLine("無法建立 OCR 引擎，請確認已安裝對應語言的 OCR 元件。");
    return 1;
}

var createdDateTime = DateTime.UtcNow;
var file = await StorageFile.GetFileFromPathAsync(imagePath);
using var stream = await file.OpenAsync(FileAccessMode.Read);
var decoder = await BitmapDecoder.CreateAsync(stream);

// Upscale small text for better accuracy, but keep within the OCR engine's size limit.
const double PreferredScale = 2.0;
var longestSide = Math.Max(decoder.OrientedPixelWidth, decoder.OrientedPixelHeight);
var scale = Math.Min(PreferredScale, (double)OcrEngine.MaxImageDimension / longestSide);
var transform = new BitmapTransform
{
    ScaledWidth = (uint)(decoder.PixelWidth * scale),
    ScaledHeight = (uint)(decoder.PixelHeight * scale),
    InterpolationMode = BitmapInterpolationMode.Fant,
};
using var bitmap = await decoder.GetSoftwareBitmapAsync(
    BitmapPixelFormat.Bgra8,
    BitmapAlphaMode.Premultiplied,
    transform,
    ExifOrientationMode.RespectExifOrientation,
    ColorManagementMode.DoNotColorManage);

var result = await engine.RecognizeAsync(bitmap);

var lines = result.Lines
    .Where(l => l.Words.Count > 0)
    .Select(l =>
    {
        var left = l.Words.Min(w => w.BoundingRect.X);
        var top = l.Words.Min(w => w.BoundingRect.Y);
        var right = l.Words.Max(w => w.BoundingRect.X + w.BoundingRect.Width);
        var bottom = l.Words.Max(w => w.BoundingRect.Y + w.BoundingRect.Height);
        return new
        {
            boundingBox = ToBox(new Rect(left, top, right - left, bottom - top), scale),
            text = JoinWords(l.Words),
            words = l.Words.Select(w => new { boundingBox = ToBox(w.BoundingRect, scale), text = w.Text }),
        };
    })
    .OrderBy(l => l.boundingBox[1])
    .ThenBy(l => l.boundingBox[0])
    .ToList();

if (format == "text")
{
    // Group lines that overlap vertically into the same visual row, so labels stay next to their values.
    var rows = new List<(int Top, int Bottom, List<(int Left, string Text)> Items)>();
    foreach (var line in lines)
    {
        int top = line.boundingBox[1], bottom = line.boundingBox[5];
        var index = rows.FindIndex(r =>
            Math.Min(r.Bottom, bottom) - Math.Max(r.Top, top) > 0.5 * Math.Min(r.Bottom - r.Top, bottom - top));
        if (index < 0)
        {
            rows.Add((top, bottom, [(line.boundingBox[0], line.text)]));
        }
        else
        {
            var row = rows[index];
            row.Items.Add((line.boundingBox[0], line.text));
            rows[index] = (Math.Min(row.Top, top), Math.Max(row.Bottom, bottom), row.Items);
        }
    }

    foreach (var row in rows.OrderBy(r => r.Top))
    {
        Console.WriteLine(string.Join("\t", row.Items.OrderBy(i => i.Left).Select(i => i.Text)));
    }
    return 0;
}

var output = new
{
    status = "succeeded",
    createdDateTime = createdDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
    lastUpdatedDateTime = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
    analyzeResult = new
    {
        version = "1.0",
        modelVersion = "Windows.Media.Ocr",
        language = engine.RecognizerLanguage.LanguageTag,
        readResults = new[]
        {
            new
            {
                page = 1,
                // Adding 0.0 normalizes -0 to 0.
                angle = (result.TextAngle ?? 0) + 0.0,
                width = decoder.OrientedPixelWidth,
                height = decoder.OrientedPixelHeight,
                unit = "pixel",
                lines,
            },
        },
    },
};

Console.WriteLine(JsonSerializer.Serialize(output, new JsonSerializerOptions
{
    WriteIndented = true,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
}));

return 0;

// Map a rect on the scaled bitmap back to original image pixels as 4 clockwise corner points.
static int[] ToBox(Rect r, double scale)
{
    int x1 = (int)Math.Round(r.X / scale), y1 = (int)Math.Round(r.Y / scale);
    int x2 = (int)Math.Round((r.X + r.Width) / scale), y2 = (int)Math.Round((r.Y + r.Height) / scale);
    return [x1, y1, x2, y1, x2, y2, x1, y2];
}

// Windows OCR may split text into fragments; insert a space only when the visual gap looks like a real word break.
static string JoinWords(IReadOnlyList<OcrWord> words)
{
    var sb = new System.Text.StringBuilder(words[0].Text);
    for (var i = 1; i < words.Count; i++)
    {
        var prev = words[i - 1].BoundingRect;
        var curr = words[i].BoundingRect;
        var gap = curr.X - (prev.X + prev.Width);
        var height = Math.Max(prev.Height, curr.Height);
        if (!IsCjk(sb[^1]) && !IsCjk(words[i].Text[0]) && gap > height * 0.15)
        {
            sb.Append(' ');
        }
        sb.Append(words[i].Text);
    }
    return sb.ToString();
}

static bool IsCjk(char c) =>
    c is >= '\u2E80' and <= '\u9FFF'
    or >= '\uF900' and <= '\uFAFF'
    or >= '\uFF00' and <= '\uFFEF';
