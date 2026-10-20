using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MediaManager.App.Editing;

/// <summary>简单滤镜（预览与导出保持同一效果）。</summary>
public enum EditorFilter
{
    None,
    Gray,
    Warm,
    Retro,
}

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

/// <summary>马赛克块（归一化矩形）。</summary>
public sealed class MosaicOverlayItem
{
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }

    public MosaicOverlayItem Clone() => (MosaicOverlayItem)MemberwiseClone();
}

/// <summary>编辑状态快照：撤销/重做以快照为单位，覆盖所有操作。</summary>
public sealed class EditorSnapshot
{
    public Int32Rect Crop { get; init; }
    public int Rotation { get; init; }
    public EditorFilter Filter { get; init; }
    public List<TextOverlayItem> Texts { get; init; } = new();
    public List<StrokeOverlayItem> Strokes { get; init; } = new();
    public List<MosaicOverlayItem> Mosaics { get; init; } = new();

    public static EditorSnapshot Capture(EditorDocument doc)
    {
        return new EditorSnapshot
        {
            Crop = doc.Crop,
            Rotation = doc.Rotation,
            Filter = doc.Filter,
            Texts = doc.Texts.Select(t => t.Clone()).ToList(),
            Strokes = doc.Strokes.Select(s => s.Clone()).ToList(),
            Mosaics = doc.Mosaics.Select(m => m.Clone()).ToList(),
        };
    }

    public void ApplyTo(EditorDocument doc)
    {
        doc.Crop = Crop;
        doc.Rotation = Rotation;
        doc.Filter = Filter;
        doc.Texts.Clear();
        doc.Texts.AddRange(Texts.Select(t => t.Clone()));
        doc.Strokes.Clear();
        doc.Strokes.AddRange(Strokes.Select(s => s.Clone()));
        doc.Mosaics.Clear();
        doc.Mosaics.AddRange(Mosaics.Select(m => m.Clone()));
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
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

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
/// 编辑文档：持有原图与所有编辑状态（裁剪、旋转、滤镜、文字、涂鸦、马赛克）。
/// 预览与导出都从同一份状态计算，保证「所见即所得」。
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
    public EditorFilter Filter { get; set; } = EditorFilter.None;

    public List<TextOverlayItem> Texts { get; } = new();
    public List<StrokeOverlayItem> Strokes { get; } = new();
    public List<MosaicOverlayItem> Mosaics { get; } = new();

    public EditorHistory History { get; } = new();

    /// <summary>叠加层内容变化（文字/涂鸦/马赛克）→ 重建覆盖层。</summary>
    public event EventHandler? OverlaysChanged;

    /// <summary>底图变化（裁剪/旋转/滤镜）→ 重建预览位图。</summary>
    public event EventHandler? BaseChanged;

    public int VisibleWidth => Rotation % 180 == 0 ? Crop.Width : Crop.Height;
    public int VisibleHeight => Rotation % 180 == 0 ? Crop.Height : Crop.Width;

    /// <summary>上次保存时的撤销栈深度：用于判断「还有没有未保存修改」。</summary>
    public int SavedUndoCount { get; set; }

    public bool IsDirty => History.UndoCount != SavedUndoCount;

    private BitmapSource? _baseCache;
    private EditorSnapshot? _pending;

    /// <summary>开始一次可撤销操作（配合 Commit 使用）。</summary>
    public void BeginEdit() => _pending = EditorSnapshot.Capture(this);

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

    /// <summary>
    /// 生成预览底图：裁剪 → 旋转 → 黑白滤镜。
    /// 暖色/复古只是叠加色，由界面上的半透明矩形负责（导出时同样叠加）。
    /// </summary>
    public BitmapSource GetBaseBitmap()
    {
        if (_baseCache != null) return _baseCache;

        BitmapSource current = Bitmap;

        if (Crop.Width > 0 && Crop.Height > 0
            && (Crop.X != 0 || Crop.Y != 0 || Crop.Width != SourceWidth || Crop.Height != SourceHeight))
        {
            current = new CroppedBitmap(current, Crop);
        }

        if (Rotation % 360 != 0)
        {
            current = new TransformedBitmap(current, new RotateTransform(Rotation % 360));
        }

        if (Filter == EditorFilter.Gray)
        {
            var gray = new FormatConvertedBitmap();
            gray.BeginInit();
            gray.Source = current;
            gray.DestinationFormat = PixelFormats.Gray8;
            gray.EndInit();
            current = gray;
        }

        if (current.CanFreeze) current.Freeze();
        _baseCache = current;
        return current;
    }

    /// <summary>暖色/复古滤镜的叠加色（无滤镜返回 null）。</summary>
    public (Color Color, double Opacity)? GetTint()
    {
        return Filter switch
        {
            EditorFilter.Warm => (Color.FromRgb(0xFF, 0x8A, 0x3C), 0.20),
            EditorFilter.Retro => (Color.FromRgb(0xC9, 0xA2, 0x6B), 0.30),
            _ => null,
        };
    }

    /// <summary>在当前可见图上按归一化矩形裁剪（像素矩形，来源坐标）。</summary>
    public void ApplyCropNormalized(Rect normalized)
    {
        // 归一化坐标 → 当前可见图坐标 → 源图坐标（逆旋转）
        var vw = (double)VisibleWidth;
        var vh = (double)VisibleHeight;

        var left = Math.Clamp(normalized.X, 0, 1) * vw;
        var top = Math.Clamp(normalized.Y, 0, 1) * vh;
        var w = Math.Clamp(normalized.Width, 0, 1) * vw;
        var h = Math.Clamp(normalized.Height, 0, 1) * vh;

        var rect = new Int32Rect(
            (int)Math.Round(left), (int)Math.Round(top),
            Math.Max(1, (int)Math.Round(w)), Math.Max(1, (int)Math.Round(h)));

        // 逆旋转到裁剪前坐标
        var sourceRect = MapVisibleRectToSource(rect);

        // 叠加层坐标重映射到新的可见区域
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

    /// <summary>裁剪后把文字/涂鸦/马赛克的归一化坐标映射到新画布，超出范围的丢弃。</summary>
    private void RemapOverlaysForCrop(Rect crop)
    {
        if (crop.Width <= 0 || crop.Height <= 0) return;

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
            // 线宽按宽度归一化，裁剪后按比例放大，保证屏幕上看起来一样粗
            s.Thickness *= crop.Width;
        }
        Strokes.RemoveAll(s => s.Points.Count == 0 || s.Points.All(p => p.X < -0.05 || p.X > 1.05 || p.Y < -0.05 || p.Y > 1.05));

        foreach (var m in Mosaics)
        {
            m.X = (m.X - crop.X) / crop.Width;
            m.Y = (m.Y - crop.Y) / crop.Height;
            m.W /= crop.Width;
            m.H /= crop.Height;
        }
        Mosaics.RemoveAll(m => m.X > 1 || m.Y > 1 || m.X + m.W < 0 || m.Y + m.H < 0);
    }

    /// <summary>旋转 90° 后重映射叠加层坐标，让标注跟着画面一起转。</summary>
    public void RemapOverlaysForRotation(int delta)
    {
        delta = ((delta % 360) + 360) % 360;
        if (delta == 0) return;

        for (int turn = 0; turn < delta / 90; turn++)
        {
            foreach (var t in Texts)
            {
                var nx = 1 - (t.Y + t.H);
                var ny = t.X;
                (t.X, t.Y) = (nx, ny);
                (t.W, t.H) = (t.H, t.W);
            }

            foreach (var s in Strokes)
            {
                s.Points = s.Points.Select(p => new Point(1 - p.Y, p.X)).ToList();
            }

            foreach (var m in Mosaics)
            {
                var nx = 1 - (m.Y + m.H);
                var ny = m.X;
                (m.X, m.Y) = (nx, ny);
                (m.W, m.H) = (m.H, m.W);
            }
        }
    }
}
