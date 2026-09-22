using System.Collections.Generic;
using Godot;

namespace TabletopSimulator.Data;

/// <summary>桌面物件的四种类型。</summary>
public enum ObjectKind
{
	Card,
	Token,
	Dice,

	/// <summary>
	/// 血量 / 计数器组件（M5.5 P4）：桌面上一个可拖的"数值牌"，显示形如 <c>60/60</c>。
	///
	/// <b>为什么是第四种物件、而不是卡牌字段的可视化</b>（用户拍板）：
	/// 血量是<b>对局中会变的状态</b>，卡牌字段是<b>卡的定义</b> ——
	/// 前者要能独立于任何一张卡存在（"这局我 60 血"），后者改一次整副牌一起变。
	/// 项目里已经有现成的分类法（<see cref="ObjectKind"/> + <see cref="ObjectState"/>），
	/// 加一个成员是顺着结构长，不是另起一套。
	/// </summary>
	Stat,
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

	// ---- 血量 / 计数器专属（M5.5 P4）----
	/// <summary>
	/// 当前值。<b>它必须在这里</b> —— M4 那条规矩："加新字段时先问它进没进
	/// <see cref="ObjectState"/>"。不进就写不回来，而症状是"改了血量却 <c>Ctrl+Z</c> 撤不掉"，
	/// 骰子点数当年就是这么栽的（同一个坑，见 <see cref="SameAs"/> 里那段说明）。
	/// </summary>
	public int StatCurrent { get; set; } = 60;

	/// <summary>上限。只影响显示，但同样要进快照 —— 否则改上限也撤不掉。</summary>
	public int StatMax { get; set; } = 60;

	/// <summary>标签（"血量"/"护甲"…）。</summary>
	public string StatLabel { get; set; } = "血量";

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
		DiceSeed = DiceSeed,
		TokenTextOverride = TokenTextOverride,
		StatCurrent = StatCurrent,
		StatMax = StatMax,
		StatLabel = StatLabel,
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

		// <b>点数与种子也算内容</b>（M4 第 4 步补的）。
		//
		// 少了这两句，"掷骰"就不是一次状态变化：快照写回之后骰子会被还原成
		// 掷之前的点数，而 `Record` 又认为"什么都没变"、连历史都不记 ——
		// 用户看到的是「点了骰子、数字跳了、<b>但 Ctrl+Z 撤不掉它</b>」。
		//
		// 种子一起进比对：同一次掷骰的两个分身（撤销再重做）必须被认成相同，
		// 而"重掷"即使点数碰巧一样也换了种子，那是两次不同的操作。
		if (DiceValues.Count != other.DiceValues.Count)
			return false;

		for (int i = 0; i < DiceValues.Count; i++)
		{
			if (DiceValues[i] != other.DiceValues[i])
				return false;
		}

		if (DiceSeed != other.DiceSeed)
			return false;

		if (TokenTextOverride != other.TokenTextOverride)
			return false;

		// <b>血量三项也算内容</b>（M5.5 P4）。
		//
		// 与上面骰子点数那两句是同一个理由，而且是同一条用户要求：
		// "改血量必须能被 Ctrl+Z 退回去"。少了这三句，一次"+1 血量"会被
		// <c>UndoSystem.Record</c> 判成"什么都没变"、连历史都不记 ——
		// 用户看到数字跳了、而撤销按下去毫无反应。
		if (StatCurrent != other.StatCurrent || StatMax != other.StatMax || StatLabel != other.StatLabel)
			return false;

		if (FieldOverrides.Count != other.FieldOverrides.Count)
			return false;

		foreach (KeyValuePair<string, string> kv in FieldOverrides)
		{
			if (!other.FieldOverrides.TryGetValue(kv.Key, out string? v) || v != kv.Value)
				return false;
		}

		return true;
	}
}
