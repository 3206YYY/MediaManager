namespace MediaManager.Core.Models;

/// <summary>
/// 导入的文件「不在任何媒体文件夹里」时怎么办。
///
/// 这个选择必须由用户做，不能默默替他决定：
/// 直接把 D:\随便一个目录 收成媒体文件夹，等于往侧栏塞了一条莫名其妙的路径，
/// 而且下次扫描会把整个目录吸进库里；反过来强行复制文件，又会动用户的原件。
/// </summary>
public enum UnknownFolderPolicy
{
    /// <summary>把文件所在的这一层目录加入媒体库（文件原地不动）。</summary>
    AddParentFolder = 0,

    /// <summary>把文件复制到用户的「导入文件夹」，并把那个文件夹加入媒体库。</summary>
    CopyToImportFolder = 1,
}

/// <summary>单文件导入的选项。</summary>
public sealed class MediaImportOptions
{
    /// <summary>不在库内的文件怎么处理。默认原地加父目录（老行为，测试与后台调用用）。</summary>
    public UnknownFolderPolicy UnknownFolderPolicy { get; init; } = UnknownFolderPolicy.AddParentFolder;

    /// <summary>「复制到导入文件夹」模式下的目标目录；为空时用 <see cref="DefaultImportFolder"/>。</summary>
    public string? ImportFolder { get; init; }

    /// <summary>
    /// 设置里没配过导入文件夹时的默认位置：
    /// 用户「图片」目录下的 <c>MediaManager</c>（Windows 上通常就是 %USERPROFILE%\Pictures）。
    /// </summary>
    public static string DefaultImportFolder
    {
        get
        {
            var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (string.IsNullOrWhiteSpace(pictures))
                pictures = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            return System.IO.Path.Combine(pictures, "MediaManager");
        }
    }
}
