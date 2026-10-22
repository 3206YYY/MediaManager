using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MediaManager.Core.Editing;

namespace MediaManager.App.Editing;

/// <summary>画布上的文字项（坐标为「可见图片」的归一化坐标 0~1）。</summary>
public sealed class TextOverlayItem
{
    public string Text { get; set; } = "双击输入文字";
    public string FontFamily { get; set; } = "Microsoft YaHei";
    public double FontSize { get; set; } = 48;   // 图片像素
    public string ColorHex { get; set; } = "#FFFFFF";
    public bool Bold { get; set; } = true;
    public bool Italic { get; set; }
    public double Opacity { get; set; } = 1.0;
    public double X { get; set; } = 0.1;
    public double Y { get; set; } = 0.1;
    /// <summary>上次测量到的归一化宽高（旋转重映射时用）。</summary>
    public double W { get; set; } = 0.3;
    public double H { get; set; } = 0.08;

    /// <summary>文字旋转角度（度，正值顺时针，绕文字中心旋转）。</summary>
    public double Angle { get; set; }

    /// <summary>文字底色，null = 透明。</summary>
    public string? BackgroundHex { get; set; }
    /// <summary>描边颜色，null = 不描边（白字压在浅色背景上会看不清）。</summary>
    public string? OutlineHex { get; set; }
    /// <summary>描边粗细（相对字号的倍数）。</summary>
    public double OutlineWidth { get; set; } = 0.07;

    public TextOverlayItem Clone() => (TextOverlayItem)MemberwiseClone();
}

/// <summary>一笔涂鸦（点坐标为归一化坐标，粗细为图片像素）。</summary>
public sealed class StrokeOverlayItem
{
    public List<Point> Points { get; set; } = new();
    public string ColorHex { get; set; } = "#FF3B3B";
    /// <summary>线宽，按可见图宽度的比例计（0.004 ≈ 800px 宽时 3px）。</summary>
    public double Thickness { get; set; } = 0.004;

    public StrokeOverlayItem Clone()
    {
        return new StrokeOverlayItem
        {
            Points = new List<Point>(Points),
            ColorHex = ColorHex,
            Thickness = Thickness,
        };
    }
}

/// <summary>马赛克块（归一化矩形，旧的矩形打码）。</summary>
public sealed class MosaicOverlayItem
{
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }

    /// <summary>马赛克颗粒占画布宽度的比例（越大方块越粗）。</summary>
    public double BlockRatio { get; set; } = 1.0 / 60;

    public MosaicOverlayItem Clone() => (MosaicOverlayItem)MemberwiseClone();
}

/// <summary>标注形状：矩形框 / 箭头 / 椭圆。</summary>
public enum ShapeKind
{
    Rect,
    Arrow,
    Ellipse,
}

/// <summary>
/// 一条形状标注。坐标为归一化（0~1），由两个对角点决定；
/// 线宽按画布宽度归一化，裁剪/旋转后仍然保持屏幕上的粗细。
/// </summary>
public sealed class ShapeOverlayItem
{
    public ShapeKind Kind { get; set; }
    public double X1 { get; set; }
    public double Y1 { get; set; }
    public double X2 { get; set; }
    public double Y2 { get; set; }
    public string ColorHex { get; set; } = "#FF3B3B";
    public double Thickness { get; set; } = 0.004;

    public ShapeOverlayItem Clone()
    {
        return new ShapeOverlayItem
        {
            Kind = Kind,
            X1 = X1,
            Y1 = Y1,
            X2 = X2,
            Y2 = Y2,
            ColorHex = ColorHex,
            Thickness = Thickness,
        };
    }
}

/// <summary>隐私打码方式。</summary>
public enum PrivacyMode
{
    Mosaic,
    Blur,
}

/// <summary>画笔式打码：沿轨迹涂抹，区域变成马赛克或高斯模糊。</summary>
public sealed class PrivacyOverlayItem
{
    public List<Point> Points { get; set; } = new();
    /// <summary>笔宽，按可见图宽度的比例计。</summary>
    public double Thickness { get; set; } = 0.05;
    /// <summary>马赛克颗粒占画布宽度的比例（越大越粗；模糊模式下同时决定模糊半径）。</summary>
    public double BlockRatio { get; set; } = 1.0 / 60;
    public PrivacyMode Mode { get; set; } = PrivacyMode.Mosaic;

    public PrivacyOverlayItem Clone()
    {
        return new PrivacyOverlayItem
        {
            Points = new List<Point>(Points),
            Thickness = Thickness,
            BlockRatio = BlockRatio,
            Mode = Mode,
        };
    }
}

/// <summary>图片水印（PNG 透明 Logo），坐标为归一化矩形。</summary>
public sealed class ImageWatermarkItem
{
    public BitmapSource Image { get; set; } = null!;
    public string Name { get; set; } = "水印";
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; } = 0.3;
    public double H { get; set; } = 0.15;
    public double Opacity { get; set; } = 0.8;

    /// <summary>原始宽高比（宽 / 高），用于裁剪后保持形状。</summary>
    public double Aspect => Image == null || Image.PixelHeight <= 0
        ? 1
        : (double)Image.PixelWidth / Image.PixelHeight;

    public ImageWatermarkItem Clone() => (ImageWatermarkItem)MemberwiseClone();
}

/// <summary>
/// 平铺文字水印：把一段文字按固定间距、固定角度重复铺满整张图。
/// 字号按「可见图宽度」归一化，旋转/裁剪后像素大小保持一致。
/// </summary>
public sealed class TileWatermarkItem
{
    public string Text { get; set; } = "© 你的署名";
    public string FontFamily { get; set; } = "Microsoft YaHei";
    /// <summary>字号占可见图宽度的比例（0.045 ≈ 宽 1000px 时 45px）。</summary>
    public double FontSizeRatio { get; set; } = 0.045;
    public string ColorHex { get; set; } = "#FFFFFF";
    public double Opacity { get; set; } = 0.18;
    /// <summary>斜放角度（度，正值顺时针）。</summary>
    public double Angle { get; set; } = -30;
    /// <summary>行列间距，按字号倍数计（越大越稀疏）。</summary>
    public double GapRatio { get; set; } = 1.6;
    public bool Bold { get; set; } = true;

    public TileWatermarkItem Clone() => (TileWatermarkItem)MemberwiseClone();
}

/// <summary>编辑状态快照：撤销/重做以快照为单位，覆盖所有操作。</summary>
public sealed class EditorSnapshot
{
    public Int32Rect Crop { get; init; }
    public int Rotation { get; init; }
    public bool FlipHorizontal { get; init; }
    public double StraightenAngle { get; init; }
    public EditorFilter Filter { get; init; }
    public int FilterStrength { get; init; } = 100;
    public ColorAdjustments Adjustments { get; init; }
    public int? OutputWidth { get; init; }
    public int? OutputHeight { get; init; }
    public int? OutputMaxEdge { get; init; }
    public List<TextOverlayItem> Texts { get; init; } = new();
    public List<StrokeOverlayItem> Strokes { get; init; } = new();
    public List<MosaicOverlayItem> Mosaics { get; init; } = new();
    public List<PrivacyOverlayItem> Privacies { get; init; } = new();
    public List<ShapeOverlayItem> Shapes { get; init; } = new();
    public List<ImageWatermarkItem> Watermarks { get; init; } = new();
    public TileWatermarkItem? TileWatermark { get; init; }

    public static EditorSnapshot Capture(EditorDocument doc)
    {
        return new EditorSnapshot
        {
            Crop = doc.Crop,
            Rotation = doc.Rotation,
            FlipHorizontal = doc.FlipHorizontal,
            StraightenAngle = doc.StraightenAngle,
            Filter = doc.Filter,
            FilterStrength = doc.FilterStrength,
            Adjustments = doc.Adjustments,
            OutputWidth = doc.OutputWidth,
            OutputHeight = doc.OutputHeight,
            OutputMaxEdge = doc.OutputMaxEdge,
            Texts = doc.Texts.Select(t => t.Clone()).ToList(),
            Strokes = doc.Strokes.Select(s => s.Clone()).ToList(),
            Mosaics = doc.Mosaics.Select(m => m.Clone()).ToList(),
            Privacies = doc.Privacies.Select(p => p.Clone()).ToList(),
            Shapes = doc.Shapes.Select(s => s.Clone()).ToList(),
            Watermarks = doc.Watermarks.Select(w => w.Clone()).ToList(),
            TileWatermark = doc.TileWatermark?.Clone(),
        };
    }

    public void ApplyTo(EditorDocument doc)
    {
        doc.Crop = Crop;
        doc.Rotation = Rotation;
        doc.FlipHorizontal = FlipHorizontal;
        doc.StraightenAngle = StraightenAngle;
        doc.Filter = Filter;
        doc.FilterStrength = FilterStrength;
        doc.Adjustments = Adjustments;
        doc.OutputWidth = OutputWidth;
        doc.OutputHeight = OutputHeight;
        doc.OutputMaxEdge = OutputMaxEdge;
        doc.Texts.Clear();
        doc.Texts.AddRange(Texts.Select(t => t.Clone()));
        doc.Strokes.Clear();
        doc.Strokes.AddRange(Strokes.Select(s => s.Clone()));
        doc.Mosaics.Clear();
        doc.Mosaics.AddRange(Mosaics.Select(m => m.Clone()));
        doc.Privacies.Clear();
        doc.Privacies.AddRange(Privacies.Select(p => p.Clone()));
        doc.Shapes.Clear();
        doc.Shapes.AddRange(Shapes.Select(s => s.Clone()));
        doc.Watermarks.Clear();
        doc.Watermarks.AddRange(Watermarks.Select(w => w.Clone()));
        doc.TileWatermark = TileWatermark?.Clone();
    }
}

/// <summary>编辑命令（命令模式）。</summary>
public interface IEditorCommand
{
    string Name { get; }
    void Undo();
    void Redo();
}

/// <summary>基于快照的命令：一次操作 = 前State → 后State。</summary>
public sealed class SnapshotCommand : IEditorCommand
{
    private readonly EditorDocument _doc;
    private readonly EditorSnapshot _before;
    private readonly EditorSnapshot _after;

    public SnapshotCommand(EditorDocument doc, EditorSnapshot before, EditorSnapshot after, string name)
    {
        _doc = doc;
        _before = before;
        _after = after;
        Name = name;
    }

    public string Name { get; }

    public void Undo() => _doc.RestoreSnapshot(_before);

    public void Redo() => _doc.RestoreSnapshot(_after);
}

/// <summary>撤销/重做栈。</summary>
public sealed class EditorHistory
{
    private readonly List<IEditorCommand> _undo = new();
    private readonly List<IEditorCommand> _redo = new();

    public event EventHandler? Changed;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public int UndoCount => _undo.Count;

    public string? UndoName => _undo.Count > 0 ? _undo[^1].Name : null;
    public string? RedoName => _redo.Count > 0 ? _redo[^1].Name : null;

    public void Push(IEditorCommand command)
    {
        _undo.Add(command);
        // 每步快照都会克隆全部叠加层，栈太深会一直吃内存；保留最近 60 步足够日常编辑
        while (_undo.Count > MaxUndoDepth) _undo.RemoveAt(0);
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>撤销栈上限（超出后丢弃最早的一步）。</summary>
    public const int MaxUndoDepth = 60;

    public string? Undo()
    {
        if (_undo.Count == 0) return null;
        var cmd = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        cmd.Undo();
        _redo.Add(cmd);
        Changed?.Invoke(this, EventArgs.Empty);
        return cmd.Name;
    }

    public string? Redo()
    {
        if (_redo.Count == 0) return null;
        var cmd = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        cmd.Redo();
        _undo.Add(cmd);
        Changed?.Invoke(this, EventArgs.Empty);
        return cmd.Name;
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
/// 编辑文档：持有原图与所有编辑状态。
/// 预览与导出都从同一份状态计算，保证「所见即所得」。
/// 颜色/滤镜由 ImageSharp 烘焙（预览降采样、导出原尺寸），这里只保存参数。
/// </summary>
public sealed class EditorDocument
{
    public EditorDocument(BitmapSource bitmap, string sourcePath)
    {
        Bitmap = bitmap;
        SourcePath = sourcePath;
        SourceWidth = bitmap.PixelWidth;
        SourceHeight = bitmap.PixelHeight;
        Crop = new Int32Rect(0, 0, SourceWidth, SourceHeight);
    }

    public BitmapSource Bitmap { get; }
    public string SourcePath { get; }
    public int SourceWidth { get; }
    public int SourceHeight { get; }

    public Int32Rect Crop { get; set; }
    public int Rotation { get; set; }
    /// <summary>显示层是否水平镜像（垂直翻转通过「旋转 180° + 保持/取消镜像」表达）。</summary>
    public bool FlipHorizontal { get; set; }
    /// <summary>拉直角度（-15 ~ +15，正值顺时针）；旋转后自动裁掉四角。</summary>
    public double StraightenAngle { get; set; }
    public EditorFilter Filter { get; set; } = EditorFilter.None;
    /// <summary>滤镜强度 0~100（与原图混合），100 = 完整效果。</summary>
    public int FilterStrength { get; set; } = 100;
    public ColorAdjustments Adjustments { get; set; } = ColorAdjustments.None;

    /// <summary>自定义输出宽高（保存在副本上的最终像素尺寸）；null = 跟随原图。</summary>
    public int? OutputWidth { get; set; }
    public int? OutputHeight { get; set; }
    /// <summary>按长边压缩（旧的「压缩」功能）；自定义宽高优先。</summary>
    public int? OutputMaxEdge { get; set; }

    public List<TextOverlayItem> Texts { get; } = new();
    public List<StrokeOverlayItem> Strokes { get; } = new();
    public List<MosaicOverlayItem> Mosaics { get; } = new();
    public List<PrivacyOverlayItem> Privacies { get; } = new();
    public List<ShapeOverlayItem> Shapes { get; } = new();
    public List<ImageWatermarkItem> Watermarks { get; } = new();
    /// <summary>平铺文字水印（同一时间最多一个，铺满整张图）。</summary>
    public TileWatermarkItem? TileWatermark { get; set; }

    public EditorHistory History { get; } = new();

    /// <summary>叠加层内容变化（文字/涂鸦/马赛克/水印/打码）→ 重建覆盖层。</summary>
    public event EventHandler? OverlaysChanged;

    /// <summary>底图变化（裁剪/旋转/翻转/滤镜/调色）→ 重建预览位图。</summary>
    public event EventHandler? BaseChanged;

    public int VisibleWidth => Rotation % 180 == 0 ? Crop.Width : Crop.Height;
    public int VisibleHeight => Rotation % 180 == 0 ? Crop.Height : Crop.Width;

    /// <summary>拉直并自动裁边后的「源画面」尺寸（裁剪矩形就在这个坐标系里）。</summary>
    public int StraightWidth => StraightenMath.StraightenedSize(SourceWidth, SourceHeight, StraightenAngle).Width;
    public int StraightHeight => StraightenMath.StraightenedSize(SourceWidth, SourceHeight, StraightenAngle).Height;

    /// <summary>当前裁剪区域在源图中的归一化位置（导出时换算到原图真实尺寸）。</summary>
    public Rect NormalizedCrop
    {
        get
        {
            var sw = StraightWidth;
            var sh = StraightHeight;
            if (sw <= 0 || sh <= 0)
                return new Rect(0, 0, 1, 1);

            var x = Math.Clamp((double)Crop.X / sw, 0, 1);
            var y = Math.Clamp((double)Crop.Y / sh, 0, 1);
            var w = Math.Clamp((double)Crop.Width / sw, 0, 1 - x);
            var h = Math.Clamp((double)Crop.Height / sh, 0, 1 - y);
            return new Rect(x, y, Math.Max(1e-6, w), Math.Max(1e-6, h));
        }
    }

    /// <summary>上次保存时的撤销栈深度：用于判断「还有没有未保存修改」。</summary>
    public int SavedUndoCount { get; set; }

    public bool IsDirty => History.UndoCount != SavedUndoCount;

    public bool HasBaseEdits =>
        Rotation != 0 || FlipHorizontal || Math.Abs(StraightenAngle) > 0.01
        || Filter != EditorFilter.None || !Adjustments.IsIdentity;

    private BitmapSource? _baseCache;
    private EditorSnapshot? _pending;

    /// <summary>开始一次可撤销操作（配合 Commit 使用）。</summary>
    public void BeginEdit() => _pending = EditorSnapshot.Capture(this);

    /// <summary>当前是否已开启一次待提交操作。</summary>
    public bool IsEditing => _pending != null;

    /// <summary>提交本次操作到撤销栈。</summary>
    public void Commit(string name)
    {
        if (_pending == null)
        {
            NotifyAll();
            return;
        }

        History.Push(new SnapshotCommand(this, _pending, EditorSnapshot.Capture(this), name));
        _pending = null;
        NotifyAll();
    }

    /// <summary>执行一个可撤销操作。</summary>
    public void Mutate(string name, Action change)
    {
        BeginEdit();
        change();
        Commit(name);
    }

    public void CancelEdit() => _pending = null;

    /// <summary>撤销/重做后由命令回调，恢复状态并刷新界面。</summary>
    public void RestoreSnapshot(EditorSnapshot snapshot)
    {
        snapshot.ApplyTo(this);
        InvalidateBase();
        NotifyAll();
    }

    public void InvalidateBase()
    {
        _baseCache = null;
        BaseChanged?.Invoke(this, EventArgs.Empty);
    }

    public void NotifyAll()
    {
        BaseChanged?.Invoke(this, EventArgs.Empty);
        OverlaysChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>只需刷新叠加层时调用（文字/涂鸦/打码/水印）。</summary>
    public void NotifyOverlays() => OverlaysChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// 设置拉直角度。拉直框始终居中，所以「保持同一块画面」只需把裁剪矩形平移同样的距离；
    /// 角度变化后若裁剪超出画面，向画面内收。
    /// </summary>
    public void SetStraighten(double angle)
    {
        var next = StraightenMath.ClampAngle(angle);
        if (Math.Abs(next - StraightenAngle) < 1e-6) return;

        var kOld = StraightenMath.FrameScale(SourceWidth, SourceHeight, StraightenAngle);
        var kNew = StraightenMath.FrameScale(SourceWidth, SourceHeight, next);

        StraightenAngle = next;

        var shiftX = (int)Math.Round((kNew - kOld) * SourceWidth / 2.0);
        var shiftY = (int)Math.Round((kNew - kOld) * SourceHeight / 2.0);
        Crop = ClampCropToCanvas(new Int32Rect(Crop.X + shiftX, Crop.Y + shiftY, Crop.Width, Crop.Height));
    }

    private Int32Rect ClampCropToCanvas(Int32Rect rect)
    {
        var maxW = StraightWidth;
        var maxH = StraightHeight;
        var w = Math.Clamp(rect.Width, 1, maxW);
        var h = Math.Clamp(rect.Height, 1, maxH);
        return new Int32Rect(
            Math.Clamp(rect.X, 0, Math.Max(0, maxW - w)),
            Math.Clamp(rect.Y, 0, Math.Max(0, maxH - h)),
            w, h);
    }

    /// <summary>
    /// 生成预览底图：拉直（自动裁边）→ 裁剪 → 旋转 → 水平镜像。
    /// 颜色与滤镜由 ImageSharp 处理，这里返回「原始颜色」的底图，
    /// 用于兜底预览、马赛克取样和尺寸计算。
    /// </summary>
    public BitmapSource GetBaseBitmap()
    {
        if (_baseCache != null) return _baseCache;

        BitmapSource current = Bitmap;

        if (Math.Abs(StraightenAngle) > 0.01)
            current = ApplyStraighten(current);

        if (Crop.Width > 0 && Crop.Height > 0
            && (Crop.X != 0 || Crop.Y != 0 || Crop.Width != current.PixelWidth || Crop.Height != current.PixelHeight))
        {
            current = new CroppedBitmap(current, Crop);
        }

        if (Rotation % 360 != 0)
        {
            current = new TransformedBitmap(current, new RotateTransform(Rotation % 360));
        }

        if (FlipHorizontal)
        {
            current = MirrorHorizontal(current);
        }

        if (current.CanFreeze) current.Freeze();
        _baseCache = current;
        return current;
    }

    /// <summary>按拉直角度旋转并裁掉四角（WPF 侧预览用，与导出的几何系数一致）。</summary>
    private BitmapSource ApplyStraighten(BitmapSource source)
    {
        var w = source.PixelWidth;
        var h = source.PixelHeight;
        if (w <= 0 || h <= 0) return source;

        var (ew, eh) = StraightenMath.ExpandedSize(w, h, StraightenAngle);
        var k = StraightenMath.FrameScale(w, h, StraightenAngle);
        var cw = Math.Max(1, Math.Min(ew, (int)Math.Round(w * k)));
        var ch = Math.Max(1, Math.Min(eh, (int)Math.Round(h * k)));

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new RotateTransform(StraightenAngle, ew / 2.0, eh / 2.0));
            dc.DrawImage(source, new Rect((ew - w) / 2.0, (eh - h) / 2.0, w, h));
            dc.Pop();
        }

        var rtb = new RenderTargetBitmap(ew, eh, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();

        var cropped = new CroppedBitmap(rtb, new Int32Rect(
            Math.Max(0, (ew - cw) / 2), Math.Max(0, (eh - ch) / 2), cw, ch));
        if (cropped.CanFreeze) cropped.Freeze();
        return cropped;
    }

    private static BitmapSource MirrorHorizontal(BitmapSource source)
    {
        var w = source.PixelWidth;
        var h = source.PixelHeight;
        if (w <= 0 || h <= 0) return source;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(-1, 1, w / 2.0, 0));
            dc.DrawImage(source, new Rect(0, 0, w, h));
            dc.Pop();
        }

        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>旋转显示层（+90 = 顺时针）。叠加层坐标同步旋转。</summary>
    public void Rotate(int degrees)
    {
        var d = ((degrees % 360) + 360) % 360;
        if (d == 0) return;

        RemapOverlaysForRotation(d);

        // 显示 = 镜像 ∘ 旋转：有镜像时，屏幕顺时针旋转 90° 对应内部旋转角 -90°
        Rotation = ((Rotation + (FlipHorizontal ? -d : d)) % 360 + 360) % 360;
    }

    /// <summary>翻转：镜像内容，叠加层跟着镜像位置（文字本身不镜像）。</summary>
    public void Flip(bool horizontal)
    {
        if (horizontal)
        {
            foreach (var t in Texts)
            {
                t.X = 1 - t.X - t.W;
                t.Angle = -t.Angle;   // 文字不镜像，只把倾斜方向反过来
            }
            foreach (var s in Strokes) s.Points = s.Points.Select(p => new Point(1 - p.X, p.Y)).ToList();
            foreach (var m in Mosaics) m.X = 1 - m.X - m.W;
            foreach (var p in Privacies) p.Points = p.Points.Select(pt => new Point(1 - pt.X, pt.Y)).ToList();
            foreach (var sh in Shapes) (sh.X1, sh.X2) = (1 - sh.X1, 1 - sh.X2);
            foreach (var w in Watermarks) w.X = 1 - w.X - w.W;
            if (TileWatermark != null) TileWatermark.Angle = NormalizeAngle(-TileWatermark.Angle);

            FlipHorizontal = !FlipHorizontal;
        }
        else
        {
            foreach (var t in Texts) t.Y = 1 - t.Y - t.H;
            foreach (var s in Strokes) s.Points = s.Points.Select(p => new Point(p.X, 1 - p.Y)).ToList();
            foreach (var m in Mosaics) m.Y = 1 - m.Y - m.H;
            foreach (var p in Privacies) p.Points = p.Points.Select(pt => new Point(pt.X, 1 - pt.Y)).ToList();
            foreach (var sh in Shapes) (sh.Y1, sh.Y2) = (1 - sh.Y1, 1 - sh.Y2);
            foreach (var w in Watermarks) w.Y = 1 - w.Y - w.H;

            // 垂直翻转 = 旋转 180°（镜像标志保持不变）
            Rotation = (Rotation + 180) % 360;
        }
    }

    /// <summary>在当前可见图上按归一化矩形裁剪（像素矩形，来源坐标）。</summary>
    public void ApplyCropNormalized(Rect normalized)
    {
        var vw = (double)VisibleWidth;
        var vh = (double)VisibleHeight;

        var left = Math.Clamp(normalized.X, 0, 1) * vw;
        var top = Math.Clamp(normalized.Y, 0, 1) * vh;
        var w = Math.Clamp(normalized.Width, 0, 1) * vw;
        var h = Math.Clamp(normalized.Height, 0, 1) * vh;

        var rect = new Int32Rect(
            (int)Math.Round(left), (int)Math.Round(top),
            Math.Max(1, (int)Math.Round(w)), Math.Max(1, (int)Math.Round(h)));

        // 先把「屏幕坐标」还原成「旋转后的坐标系」（镜像发生在旋转之后）
        if (FlipHorizontal)
        {
            rect = new Int32Rect(
                Math.Max(0, (int)Math.Round(vw) - rect.X - rect.Width),
                rect.Y, rect.Width, rect.Height);
        }

        var sourceRect = MapVisibleRectToSource(rect);

        RemapOverlaysForCrop(normalized);
        Crop = sourceRect;
    }

    private Int32Rect MapVisibleRectToSource(Int32Rect r)
    {
        int cw = Crop.Width, ch = Crop.Height;
        var x = r.X;
        var y = r.Y;
        var w = r.Width;
        var h = r.Height;

        switch (Rotation % 360)
        {
            case 90:
                // 可见图顺时针旋转 90°：visible(u,v) ↔ source(v, ch - u)
                return new Int32Rect(Crop.X + y, Crop.Y + ch - x - w, h, w);
            case 180:
                return new Int32Rect(Crop.X + cw - x - w, Crop.Y + ch - y - h, w, h);
            case 270:
                // 270°：visible(u,v) ↔ source(cw - v, u)
                return new Int32Rect(Crop.X + cw - y - h, Crop.Y + x, h, w);
            default:
                return new Int32Rect(Crop.X + x, Crop.Y + y, w, h);
        }
    }

    /// <summary>裁剪后把文字/涂鸦/马赛克/水印/打码的归一化坐标映射到新画布。</summary>
    private void RemapOverlaysForCrop(Rect crop)
    {
        if (crop.Width <= 0 || crop.Height <= 0) return;

        var oldW = Math.Max(1, VisibleWidth);
        var oldH = Math.Max(1, VisibleHeight);
        var newW = Math.Max(1, oldW * crop.Width);
        var newH = Math.Max(1, oldH * crop.Height);

        foreach (var t in Texts)
        {
            t.X = (t.X - crop.X) / crop.Width;
            t.Y = (t.Y - crop.Y) / crop.Height;
            t.W /= crop.Width;
            t.H /= crop.Height;
        }
        Texts.RemoveAll(t => t.X > 1 || t.Y > 1 || t.X + t.W < 0 || t.Y + t.H < 0);

        foreach (var s in Strokes)
        {
            s.Points = s.Points
                .Select(p => new Point((p.X - crop.X) / crop.Width, (p.Y - crop.Y) / crop.Height))
                .ToList();
            // 线宽按宽度归一化，裁剪后要除以裁剪比例，才是「原图上一样的像素粗细」
            s.Thickness /= crop.Width;
        }
        Strokes.RemoveAll(s => s.Points.Count == 0 || s.Points.All(p => p.X < -0.05 || p.X > 1.05 || p.Y < -0.05 || p.Y > 1.05));

        foreach (var m in Mosaics)
        {
            m.X = (m.X - crop.X) / crop.Width;
            m.Y = (m.Y - crop.Y) / crop.Height;
            m.W /= crop.Width;
            m.H /= crop.Height;
            m.BlockRatio /= crop.Width;   // 颗粒大小按画布宽度归一化，裁剪后保持像素粗细
        }
        Mosaics.RemoveAll(m => m.X > 1 || m.Y > 1 || m.X + m.W < 0 || m.Y + m.H < 0);

        foreach (var p in Privacies)
        {
            p.Points = p.Points
                .Select(pt => new Point((pt.X - crop.X) / crop.Width, (pt.Y - crop.Y) / crop.Height))
                .ToList();
            // 同上：笔迹粗细按宽度归一化，裁剪后必须除以裁剪比例
            p.Thickness /= crop.Width;
            p.BlockRatio /= crop.Width;
        }
        Privacies.RemoveAll(p => p.Points.Count == 0 || p.Points.All(pt => pt.X < -0.05 || pt.X > 1.05 || pt.Y < -0.05 || pt.Y > 1.05));

        foreach (var sh in Shapes)
        {
            sh.X1 = (sh.X1 - crop.X) / crop.Width;
            sh.Y1 = (sh.Y1 - crop.Y) / crop.Height;
            sh.X2 = (sh.X2 - crop.X) / crop.Width;
            sh.Y2 = (sh.Y2 - crop.Y) / crop.Height;
            sh.Thickness /= crop.Width;
        }
        Shapes.RemoveAll(sh =>
            Math.Max(sh.X1, sh.X2) < -0.05 || Math.Min(sh.X1, sh.X2) > 1.05 ||
            Math.Max(sh.Y1, sh.Y2) < -0.05 || Math.Min(sh.Y1, sh.Y2) > 1.05);

        foreach (var wm in Watermarks)
        {
            wm.X = (wm.X - crop.X) / crop.Width;
            wm.Y = (wm.Y - crop.Y) / crop.Height;
            wm.W /= crop.Width;
            // 保持图片原始宽高比（显示宽 = W*画布宽，显示高 = H*画布高），避免 Logo 被拉扁
            wm.H = wm.W * newW / (wm.Aspect * newH);
        }
        Watermarks.RemoveAll(wm => wm.X > 1 || wm.Y > 1 || wm.X + wm.W < 0 || wm.Y + wm.H < 0);
    }

    /// <summary>旋转 90° 后重映射叠加层坐标，让标注跟着画面一起转。</summary>
    public void RemapOverlaysForRotation(int delta)
    {
        delta = ((delta % 360) + 360) % 360;
        if (delta == 0) return;

        // 线宽、马赛克颗粒都按「画布宽度」归一化：旋转 90° 后宽高互换，
        // 不换算的话横图转竖图时笔迹与颗粒会按 高/宽 的比例变粗或变细。
        var oldWidth = VisibleWidth;
        var oldHeight = VisibleHeight;

        for (int turn = 0; turn < delta / 90; turn++)
        {
            var scale = oldHeight > 0 ? (double)oldWidth / oldHeight : 1;

            foreach (var t in Texts)
            {
                var nx = 1 - (t.Y + t.H);
                var ny = t.X;
                (t.X, t.Y) = (nx, ny);
                (t.W, t.H) = (t.H, t.W);
                t.Angle = NormalizeAngle(t.Angle + 90);
            }

            foreach (var s in Strokes)
            {
                s.Points = s.Points.Select(p => new Point(1 - p.Y, p.X)).ToList();
                s.Thickness *= scale;
            }

            foreach (var m in Mosaics)
            {
                var nx = 1 - (m.Y + m.H);
                var ny = m.X;
                (m.X, m.Y) = (nx, ny);
                (m.W, m.H) = (m.H, m.W);
                m.BlockRatio *= scale;
            }

            foreach (var p in Privacies)
            {
                p.Points = p.Points.Select(pt => new Point(1 - pt.Y, pt.X)).ToList();
                p.BlockRatio *= scale;
            }

            foreach (var sh in Shapes)
            {
                var nx1 = 1 - sh.Y1;
                var ny1 = sh.X1;
                var nx2 = 1 - sh.Y2;
                var ny2 = sh.X2;
                (sh.X1, sh.Y1) = (nx1, ny1);
                (sh.X2, sh.Y2) = (nx2, ny2);
                sh.Thickness *= scale;
            }

            foreach (var wm in Watermarks)
            {
                var nx = 1 - (wm.Y + wm.H);
                var ny = wm.X;
                (wm.X, wm.Y) = (nx, ny);

                // W 按宽度归一化、H 按高度归一化：宽高互换后要按新旧画布换算，
                // 直接交换 W/H 会让 Logo 的像素尺寸和宽高比一起跑偏
                if (oldWidth > 0 && oldHeight > 0)
                {
                    var newW = wm.W * oldWidth / oldHeight;
                    var newH = wm.H * oldHeight / oldWidth;
                    (wm.W, wm.H) = (newW, newH);
                }
                else
                {
                    (wm.W, wm.H) = (wm.H, wm.W);
                }
            }

            if (TileWatermark != null)
            {
                // 字号按宽度归一化：宽高互换后按新旧画布换算，铺满的视觉大小才不变
                TileWatermark.FontSizeRatio *= scale;
                TileWatermark.Angle = NormalizeAngle(TileWatermark.Angle + 90);
            }

            (oldWidth, oldHeight) = (oldHeight, oldWidth);
        }
    }

    /// <summary>把角度规整到 (-180, 180]，避免旋转几次后数值一直变大。</summary>
    private static double NormalizeAngle(double angle)
    {
        if (!double.IsFinite(angle)) return 0;
        var a = angle % 360;
        if (a > 180) a -= 360;
        if (a <= -180) a += 360;
        return a;
    }
}
