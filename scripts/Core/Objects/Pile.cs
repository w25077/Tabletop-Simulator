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

	/// <summary>上一次洗牌用的随机种子。存下来是为了让"这把为什么是这个顺序"可复现。</summary>
	public int LastShuffleSeed { get; private set; }

	/// <summary>堆的逻辑锚点。成员实际位置 = 锚点 + 阶梯偏移。</summary>
	public Vector2 Anchor { get; set; }

	public int Count => Members.Count;

	public TabletopObject? Top => Members.Count > 0 ? Members[^1] : null;

	public TabletopObject? Bottom => Members.Count > 0 ? Members[0] : null;

	public bool Contains(TabletopObject obj) => Members.Contains(obj);

	/// <summary>
	/// 洗牌：把成员随机重排（Fisher-Yates）。
	///
	/// 记种子是为了复现 —— 和 M2 骰子存 <c>DiceSeed</c>、叠放区域存
	/// <c>LastShuffleSeed</c> 同一个理由：出现"这把牌怎么这么离谱"的时候能重现它。
	/// </summary>
	/// <param name="seed">0 = 取当前时间。非 0 则用它，结果可复现。</param>
	/// <returns>实际使用的种子。</returns>
	public int Shuffle(int seed = 0)
	{
		LastShuffleSeed = seed != 0 ? seed : (int)(Time.GetTicksUsec() & 0x7FFFFFFF);
		var rng = new System.Random(LastShuffleSeed);

		for (int i = Members.Count - 1; i > 0; i--)
		{
			int j = rng.Next(i + 1);
			(Members[i], Members[j]) = (Members[j], Members[i]);
		}

		return LastShuffleSeed;
	}

	/// <summary>某个层级的视觉阶梯偏移。限制最大层数，免得一叠 60 张牌拖出长尾。</summary>
	public static Vector2 OffsetFor(int index)
		=> GameConfig.PileStepOffset * Mathf.Min(index, GameConfig.PileStepLimit);
}
