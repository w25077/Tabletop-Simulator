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

	/// <summary>在堆中的次序，0 = 最底。</summary>
	public int PileIndex { get; set; }

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
		FieldOverrides = new Dictionary<string, string>(FieldOverrides),
		DiceSides = DiceSides,
		DiceCount = DiceCount,
		DiceValues = new List<int>(DiceValues),
		TokenTextOverride = TokenTextOverride,
	};
}
