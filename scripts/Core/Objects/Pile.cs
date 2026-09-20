using System.Collections.Generic;
using Godot;

namespace TabletopSimulator.Core.Objects;

/// <summary>
/// 一堆叠在一起的物件。
///
/// <b>刻意不新增节点</b>：堆只是逻辑分组 —— 成员仍是 <c>Objects</c> 的直接子节点，
/// 只是位置被对齐、绘制次序被排成连续。这样「拆堆」「并入」都是纯数据操作，
/// 没有节点增删，也就没有生命周期与释放顺序的坑。
/// </summary>
public sealed class Pile
{
	public Pile(int id)
	{
		Id = id;
	}

	/// <summary>堆 id。物件的 <see cref="TabletopObject.PileId"/> 指向它；0 表示不在堆里。</summary>
	public int Id { get; }

	/// <summary>成员，<b>从底到顶</b>。</summary>
	public List<TabletopObject> Members { get; } = new();

	/// <summary>堆的逻辑锚点。成员实际位置 = 锚点 + 阶梯偏移。</summary>
	public Vector2 Anchor { get; set; }

	public int Count => Members.Count;

	public TabletopObject? Top => Members.Count > 0 ? Members[^1] : null;

	public TabletopObject? Bottom => Members.Count > 0 ? Members[0] : null;

	public bool Contains(TabletopObject obj) => Members.Contains(obj);

	/// <summary>某个层级的视觉阶梯偏移。限制最大层数，免得一叠 60 张牌拖出长尾。</summary>
	public static Vector2 OffsetFor(int index)
		=> GameConfig.PileStepOffset * Mathf.Min(index, GameConfig.PileStepLimit);
}
