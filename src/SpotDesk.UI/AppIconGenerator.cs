using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace SpotDesk.UI;

/// <summary>
/// Generates the SpotDesk window icon at runtime using Avalonia's rendering pipeline.
/// Avoids the need for a bundled .ico / .png asset file.
/// </summary>
public static class AppIconGenerator
{
    public static WindowIcon Create() => new(CreateBitmap());

    public static Bitmap CreateBitmap()
    {
        var canvas = new Canvas { Width = 64, Height = 64 };

        // Dark rounded background
        var bg = new Rectangle { Width = 64, Height = 64,
            Fill = new SolidColorBrush(Color.Parse("#171B26")),
            RadiusX = 13, RadiusY = 13 };
        canvas.Children.Add(bg);

        // Monitor bezel — accent blue
        var bezel = new Rectangle { Width = 46, Height = 32,
            Fill = new SolidColorBrush(Color.Parse("#3B82F6")),
            RadiusX = 4, RadiusY = 4 };
        Canvas.SetLeft(bezel, 9); Canvas.SetTop(bezel, 9);
        canvas.Children.Add(bezel);

        // Screen surface — dark inner
        var screen = new Rectangle { Width = 40, Height = 25,
            Fill = new SolidColorBrush(Color.Parse("#0F1117")),
            RadiusX = 2, RadiusY = 2 };
        Canvas.SetLeft(screen, 12); Canvas.SetTop(screen, 12);
        canvas.Children.Add(screen);

        // Fake terminal lines on screen (light blue, 2px tall each)
        for (int i = 0; i < 3; i++)
        {
            var line = new Rectangle { Width = 28 - i * 6, Height = 2,
                Fill = new SolidColorBrush(Color.Parse("#93C5FD")),
                RadiusX = 1, RadiusY = 1 };
            Canvas.SetLeft(line, 16); Canvas.SetTop(line, 16 + i * 5);
            canvas.Children.Add(line);
        }

        // Monitor stand (stem)
        var stem = new Rectangle { Width = 4, Height = 7,
            Fill = new SolidColorBrush(Color.Parse("#3B82F6")) };
        Canvas.SetLeft(stem, 30); Canvas.SetTop(stem, 41);
        canvas.Children.Add(stem);

        // Monitor base
        var @base = new Rectangle { Width = 18, Height = 4,
            Fill = new SolidColorBrush(Color.Parse("#3B82F6")),
            RadiusX = 2, RadiusY = 2 };
        Canvas.SetLeft(@base, 23); Canvas.SetTop(@base, 48);
        canvas.Children.Add(@base);

        // Measure + arrange so Avalonia has layout info before render
        canvas.Measure(new Size(64, 64));
        canvas.Arrange(new Rect(0, 0, 64, 64));

        var rtb = new RenderTargetBitmap(new PixelSize(64, 64), new Vector(96, 96));
        rtb.Render(canvas);
        return rtb;
    }
}
