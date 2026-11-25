namespace MediaManager.Core.Enums;

/// <summary>
/// 内容区的展示方式。
/// <para>
/// 只影响「怎么排」——排序、筛选、分页、选择、拖拽全部共用同一套逻辑，
/// 所以别在这里加任何与排序或数据来源有关的成员。
/// </para>
/// </summary>
public enum ViewMode
{
    /// <summary>缩略图网格。</summary>
    Grid,

    /// <summary>
    /// 紧凑列表：一行一个文件（缩略图 + 文件名 + 元信息），
    /// 行宽由换行面板按窗口宽度均分，宽窗口下并排 2~4 列，不强制单列。
    /// </summary>
    List,
}
