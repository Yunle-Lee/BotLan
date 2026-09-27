using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace IslandUI.Controls;

/// <summary>Two textured leaves with a real perspective hinge. Textures are captured once,
/// never per frame; the live WebView is restored unchanged when the transition ends.</summary>
internal sealed class ChatBookFold : Canvas
{
    private readonly double _width, _height, _half, _capsuleWidth;
    private readonly Border _capsule;
    private readonly Canvas _docks;
    private readonly GradientStop _closedDark = new(Color.FromRgb(216, 220, 226), 0);
    private readonly GradientStop _closedLight = new(Color.FromRgb(247, 248, 250), 1);
    private readonly MeshGeometry3D _left = new(), _right = new();
    private readonly GeometryModel3D _rightPage;
    private readonly Material _rightMaterial;
    private readonly SolidColorBrush _leftShade = new(Colors.Transparent), _rightShade = new(Colors.Transparent);
    private readonly Viewport3D _book;
    public double AngleDegrees { get; private set; }
    public double BookWidth { get; private set; }
    public double HingeX { get; private set; }
    public double LeafWidth => _half;
    public double CapsuleOpacity => _docks.Opacity;

    public ChatBookFold(BitmapSource snapshot, double width, double height, double dock)
    {
        _width = width; _height = height; _capsuleWidth = dock * 2;
        // The hinge bisects the WHOLE Chat, including both tool docks.
        _half = width / 2;
        Width = width; Height = height;
        IsHitTestVisible = false;
        BitmapSource Crop(double x, double w, double y = 0, double? h = null)
        {
            int l = (int)Math.Round(x / width * snapshot.PixelWidth);
            int r = (int)Math.Round((x + w) / width * snapshot.PixelWidth);
            int top = (int)Math.Round(y / height * snapshot.PixelHeight);
            int bottom = (int)Math.Round((y + (h ?? height)) / height * snapshot.PixelHeight);
            var image = new CroppedBitmap(snapshot, new Int32Rect(l, top, Math.Max(1, r - l), Math.Max(1, bottom - top)));
            image.Freeze(); return image;
        }
        _book = new Viewport3D { Width = width, Height = height + 220, ClipToBounds = false };
        SetTop(_book, -110);
        double cameraZ = width * 2.2;
        _book.Camera = new PerspectiveCamera(new Point3D(0, 0, cameraZ), new Vector3D(0, 0, -1),
            new Vector3D(0, 1, 0), 2 * Math.Atan(width / (2 * cameraZ)) * 180 / Math.PI);
        var scene = new Model3DGroup();
        scene.Children.Add(new AmbientLight(Colors.White));
        // Static right page, then the left leaf turning over it (correct depth ordering).
        _rightMaterial = Texture(Crop(_half, _half), _rightShade);
        _rightPage = new GeometryModel3D(_right, _rightMaterial);
        scene.Children.Add(_rightPage);
        var back = new DrawingGroup();
        using (var dc = back.Open())
        {
            // The outside corners turn with the left sheet; the hinge stays square.
            // Back-face UVs are reversed on screen, so the rounded edge is UV-left.
            var shape = new StreamGeometry();
            using (var path = shape.Open())
            {
                path.BeginFigure(new Point(_half, 1.1), true, true);
                path.LineTo(new Point(44, 1.1), true, false);
                path.ArcTo(new Point(1.1, 44), new Size(43, 43), 0, false, SweepDirection.Counterclockwise, true, false);
                path.LineTo(new Point(1.1, height - 44), true, false);
                path.ArcTo(new Point(44, height - 1.1), new Size(43, 43), 0, false, SweepDirection.Counterclockwise, true, false);
                path.LineTo(new Point(_half, height - 1.1), true, false);
            }
            dc.DrawGeometry(new LinearGradientBrush(Color.FromRgb(247, 248, 250), Color.FromRgb(216, 220, 226), 0),
                new Pen(ComicTheme.Current.Outline, 2.2), shape);
        }
        scene.Children.Add(new GeometryModel3D(_left, Texture(Crop(0, _half), _leftShade))
        { BackMaterial = new DiffuseMaterial(new DrawingBrush(back)) });
        _book.Children.Add(new ModelVisual3D { Content = scene });
        Children.Add(_book);
        // These controls appear only AFTER the full sheet has finished turning.
        // They are never independent strips travelling beside a rotating centre.
        _docks = new Canvas { Width = _capsuleWidth, Height = height, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(-2.2),
            Clip = new RectangleGeometry(new Rect(0, 0, _capsuleWidth, height), dock, dock) };
        var leftDock = new Image { Source = Crop(14, dock - 14, 46, height - 60), Width = dock - 14, Height = height - 60, Stretch = Stretch.Fill };
        var rightDock = new Image { Source = Crop(width - dock, dock - 14, 46, height - 60), Width = dock - 14, Height = height - 60, Stretch = Stretch.Fill };
        SetLeft(leftDock, 14); SetLeft(rightDock, dock);
        SetTop(leftDock, 46); SetTop(rightDock, 46);
        _docks.Children.Add(leftDock); _docks.Children.Add(rightDock);
        var expand = new TextBlock { Text = "↗", Width = _capsuleWidth, TextAlignment = TextAlignment.Center,
            FontSize = 17, Foreground = ComicTheme.Current.Muted };
        SetTop(expand, 14); _docks.Children.Add(expand);
        var closedFill = new LinearGradientBrush { StartPoint = new Point(0, .5), EndPoint = new Point(1, .5) };
        closedFill.GradientStops.Add(_closedDark); closedFill.GradientStops.Add(_closedLight);
        _capsule = new Border { Width = _capsuleWidth, Height = height, CornerRadius = new CornerRadius(dock),
            Background = closedFill, BorderBrush = ComicTheme.Current.Outline, BorderThickness = new Thickness(2.2),
            Child = _docks, Opacity = 0 };
        SetLeft(_capsule, width - _capsuleWidth);
        Children.Add(_capsule);
        SetProgress(0);
    }

    private static Material Texture(BitmapSource image, Brush shade)
    {
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            dc.DrawImage(image, new Rect(0, 0, 1, 1));
            // Shading must respect the card's transparent rounded corners.
            dc.PushOpacityMask(new ImageBrush(image));
            dc.DrawRectangle(shade, null, new Rect(0, 0, 1, 1));
            dc.Pop();
        }
        return new DiffuseMaterial(new DrawingBrush(drawing));
    }

    public static double Smooth(double from, double to, double progress)
    {
        double t = Math.Clamp((progress - from) / (to - from), 0, 1);
        return t * t * (3 - 2 * t);
    }

    public void SetProgress(double progress)
    {
        double p = Math.Clamp(progress, 0, 1);
        // Keep the crease exactly at the full window's centre for the entire turn.
        // Only after the 180-degree fold do we narrow the closed outline into a capsule.
        AngleDegrees = 180 * Smooth(0, .72, p);
        // Once shut, the right page is fully occluded; remove it to avoid depth
        // precision making its controls bleed through the nearly coplanar back.
        _rightPage.Material = AngleDegrees >= 179.9 ? null : _rightMaterial;
        double angle = AngleDegrees * Math.PI / 180;
        double length = _half + (_capsuleWidth - _half) * Smooth(.76, 1, p);
        BookWidth = length * (1 + Math.Max(0, Math.Cos(angle)));
        double hinge = HingeX = _width - length;
        double edge = hinge - length * Math.Cos(angle);
        double depth = length * Math.Sin(angle);
        Quad(_right, hinge, 0, _width, 0);
        double lift = .5 * Smooth(.05, .2, p);
        Quad(_left, edge, depth + lift, hinge, lift);
        _leftShade.Color = Color.FromArgb((byte)(90 * Math.Sin(angle)), 20, 24, 30);
        _rightShade.Color = Color.FromArgb((byte)(105 * Math.Sin(angle)), 20, 24, 30);
        // A single continuous outline rounds into the capsule after the fold.
        // Avoid crossfading two differently sized borders (which creates ghost edges).
        double compact = Smooth(.76, 1, p);
        _book.Opacity = p < .76 ? 1 : 0;
        _capsule.Opacity = p >= .76 ? 1 : 0;
        _capsule.Width = length;
        SetLeft(_capsule, _width - length);
        _capsule.CornerRadius = new CornerRadius(_capsuleWidth / 2 * compact,
            44 + (_capsuleWidth / 2 - 44) * compact, 44 + (_capsuleWidth / 2 - 44) * compact,
            _capsuleWidth / 2 * compact);
        _docks.Opacity = Smooth(.85, 1, p);
        Color card = ((SolidColorBrush)ComicTheme.Current.Card).Color;
        Color Blend(Color from) => Color.FromRgb((byte)(from.R + (card.R - from.R) * compact),
            (byte)(from.G + (card.G - from.G) * compact), (byte)(from.B + (card.B - from.B) * compact));
        _closedDark.Color = Blend(Color.FromRgb(216, 220, 226));
        _closedLight.Color = Blend(Color.FromRgb(247, 248, 250));
    }

    private void Quad(MeshGeometry3D mesh, double left, double zLeft, double right, double zRight)
    {
        mesh.Positions = new Point3DCollection {
            new(left - _width / 2, _height / 2, zLeft), new(right - _width / 2, _height / 2, zRight),
            new(left - _width / 2, -_height / 2, zLeft), new(right - _width / 2, -_height / 2, zRight) };
        if (mesh.TriangleIndices.Count != 0) return;
        mesh.TriangleIndices = new Int32Collection { 0, 2, 1, 1, 2, 3 };
        mesh.TextureCoordinates = new PointCollection { new(0, 0), new(1, 0), new(0, 1), new(1, 1) };
    }
}
