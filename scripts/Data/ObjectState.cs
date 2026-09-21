using System.Collections.Generic;
using Godot;

namespace TabletopSimulator.Data;

/// <summary>桌面物件的三种类型。</summary>
public enum ObjectKind
{
	Card,
	Token,
	Dice,
}

/// <summary>
/// 一个物件的完整可序列化状态。
///
/// 这是 <b>M4 存档</b>与<b>撤销系统</b>的公共载体：
/// 撤销不是「反向操作」，而是「把快照写回去」；存档就是把一堆快照存成 JSON。
/// 因此它必须能完整描述一个物件，且与节点实例解耦（节点可以随时重建）。
/// </summary>
public sealed class ObjectState
{
	// ---- 身份 ----
	/// <summary>稳定唯一 id。存档、堆成员引用、撤销记录都靠它，绝不复用。</summary>
	public string Uid { get; set; } = "";

	public ObjectKind Kind { get; set; } = ObjectKind.Card;

	/// <summary>指向 CardDefinition.Id / TokenDefinition.Id。骰子留空。</summary>
	public string DefinitionId { get; set; } = "";

	// ---- 变换 ----
	public Vector2 Position { get; set; }

	/// <summary>旋转角度（度）。存度不存弧度，因为 JSON 要给人看。</summary>
	public float RotationDegrees { get; set; }

	/// <summary>是否盖放（卡牌显示卡背）。</summary>
	public bool FaceDown { get; set; }

	// ---- 堆叠 ----
	/// <summary>所属堆的 id；0 = 不在堆里。</summary>
	public int PileId { get; set; }

	/// <summary>在堆中的次序，0 = 最底。区域叠放时也表示在区域里的次序（见 <see cref="ZoneId"/>）。</summary>
	public int PileIndex { get; set; }

	// ---- 区域（M3）----
	/// <summary>
	/// 所属区域的 id；空字符串 = 不属于任何区域。指向 <c>ZoneDefinition.Id</c>。
	///
	/// 与 <see cref="PileId"/> 互斥：区域成员恒有 <c>PileId == 0</c>。
	/// 注意这是<b>声明</b>——读档时还要由加载器把它和 <c>Zone.Members</c> 对上，
	/// 两边只对一半就是自检里 <c>member_counts_match</c> 要抓的 bug。
	/// </summary>
	public string ZoneId { get; set; } = "";

	// ---- 卡牌专属 ----
	/// <summary>实例级字段覆盖：只改这一张卡，不污染定义。</summary>
	public Dictionary<string, string> FieldOverrides { get; set; } = new();

	// ---- 骰子专属 ----
	public int DiceSides { get; set; } = 6;
	public int DiceCount { get; set; } = 1;
	public List<int> DiceValues { get; set; } = new();

	/// <summary>骰子随机数种子。存下来是为了让一次掷骰可复现（调试与联机同步都靠它）。</summary>
	public int DiceSeed { get; set; }

	// ---- Token 专属 ----
	/// <summary>Token 的实例级文字覆盖（比如同一份定义打出多个不同数值的指示物）。</summary>
	public string TokenTextOverride { get; set; } = "";

	public ObjectState Clone() => new()
	{
		Uid = Uid,
		Kind = Kind,
		DefinitionId = DefinitionId,
		Position = Position,
		RotationDegrees = RotationDegrees,
		FaceDown = FaceDown,
		PileId = PileId,
		PileIndex = PileIndex,
		ZoneId = ZoneId,
		FieldOverrides = new Dictionary<string, string>(FieldOverrides),
		DiceSides = DiceSides,
		DiceCount = DiceCount,
		DiceValues = new List<int>(DiceValues),
		TokenTextOverride = TokenTextOverride,
	};

	/// <summary>
	/// 两份状态是否<b>内容相同</b>（不是引用相同）。
	///
	/// 用途是撤销系统判断"这次操作到底改了什么"：
	/// 点一下没拖动、拖回原位、往已经满了的区域再拖一次（被拒）——
	/// 这些都不该进历史，否则用户按 <c>Ctrl+Z</c> 会觉得"按了没反应"。
	///
	/// 位置用<b>严格相等</b>：快照写回是"把值抄回去"，不引入误差；
	/// 用近似比较会把"其实动了 0.2 像素"当成没动，那条改动就永远撤不掉了。
	/// </summary>
	public bool SameAs(ObjectState other)
	{
		if (ReferenceEquals(this, other))
			return true;

		if (Uid != other.Uid || Kind != other.Kind || DefinitionId != other.DefinitionId)
			return false;

		if (Position != other.Position || RotationDegrees != other.RotationDegrees)
			return false;

		if (FaceDown != other.FaceDown)
			return false;

		if (PileId != other.PileId || PileIndex != other.PileIndex || ZoneId != other.ZoneId)
			return false;

		if (DiceSides != other.DiceSides || DiceCount != other.DiceCount)
			return false;

		if (TokenTextOverride != other.TokenTextOverride)
			return false;

		if (FieldOverrides.Count != other.FieldOverrides.Count)
			return false;

		foreach (KeyValuePair<string, string> kv in FieldOverrides)
		{
			if (!other.FieldOverrides.TryGetValue(kv.Key, out string? v) || v != kv.Value)
				return false;
		}

		if (DiceValues.Count != other.DiceValues.Count)
			return false;

		for (int i = 0; i < DiceValues.Count; i++)
		{
			if (DiceValues[i] != other.DiceValues[i])
				return false;
		}

		return true;
	}
}
