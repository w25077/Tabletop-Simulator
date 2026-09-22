using Godot;

namespace TabletopSimulator.Data;

/// <summary>
/// 血量 / 计数器组件的数据（M5.5 P4）。
///
/// <b>"60/60"不是常量，而是新建时的默认值。</b>用户实测反馈的原话是
/// 「血量不一定是 60/60，也有可能是 xxx/xxx」—— 所以 <see cref="Current"/> 与
/// <see cref="Max"/> 都是普通整数，各自独立可改，也不要求 <c>Current &lt;= Max</c>
/// （"超载""临时溢出"是桌游里常见的状态，没理由在数据层禁掉它）。
///
/// <b>它是"对局中会变的状态"，不是"卡牌的定义"</b> —— 这正是它做成独立物件
/// 而不是卡牌字段可视化的理由：卡牌字段描述的是"这张卡是什么"，
/// 而血量描述的是"它现在还剩多少"。两者生命周期完全不同。
/// </summary>
public sealed class StatData
{
	/// <summary>标签（"血量"/"护甲"/"能量"…）。空字符串表示只显示数字。</summary>
	public string Label { get; set; } = "血量";

	/// <summary>当前值。<b>可以不是 0..Max</b>：负数与超过上限都是合法的桌游状态。</summary>
	public int Current { get; set; } = 60;

	/// <summary>上限。只用于显示 <c>当前/上限</c>。</summary>
	public int Max { get; set; } = 60;

	public StatData Clone() => new()
	{
		Label = Label,
		Current = Current,
		Max = Max,
	};

	/// <summary>显示文本，形如 <c>血量 60/60</c>（没有标签时只有 <c>60/60</c>）。</summary>
	public string DisplayText => string.IsNullOrWhiteSpace(Label)
		? $"{Current}/{Max}"
		: $"{Label} {Current}/{Max}";

	/// <summary>
	/// 显示的数值色：正常 / 告急 / 溢出。
	///
	/// 做成三档而不是只画黑字，是因为"血量"这类东西的价值全在<b>一眼看得出危险</b>：
	/// 桌上摆着五个计数器时，读数字比读颜色慢得多。
	/// </summary>
	public Color ValueColor
	{
		get
		{
			if (Max > 0 && Current <= 0)
				return new Color("#bf616a");       // 归零：告急红

			if (Max > 0 && Current * 100 <= Max * 25)
				return new Color("#d08770");       // 四分之一以下：橙

			if (Max > 0 && Current > Max)
				return new Color("#b48ead");       // 超过上限：紫（"溢出"）

			return new Color("#2e3440");           // 正常：深灰
		}
	}
}
