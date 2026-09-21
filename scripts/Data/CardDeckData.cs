using System.Collections.Generic;

namespace TabletopSimulator.Data;

/// <summary>
/// 卡组里的一行：<b>哪种卡、几张</b>。
///
/// 它与 <see cref="CardDefinition"/> 的关系是"引用"而不是"包含"：
/// 只存 <see cref="CardId"/>，改卡面数值时整副牌组跟着变 ——
/// 而验玩法时你改的正是"那张卡的费用"，改完还要立刻看到整副牌都变了。
/// </summary>
public sealed class CardStack
{
	/// <summary>引用的 <see cref="CardDefinition.Id"/>。</summary>
	public string CardId { get; set; } = "";

	/// <summary>张数。允许 0（保留一行但暂时不发），负数按 0 处理。</summary>
	public int Count { get; set; }

	public CardStack Clone() => new() { CardId = CardId, Count = Count };
}

/// <summary>
/// 卡组（一副牌的配方）：一组"哪种卡几张"。
///
/// <b>为什么它必须是一份独立的数据，而不是一句代码：</b>
/// M5 之前，牌库那 20 张牌的张数配方写死在 <c>DemoContent.BuildDeck</c> 里
/// （5/5/4/4/2），于是"换一副牌试试"这件事只能改代码、重新编译、重启。
/// 而验玩法最常做的事恰恰是"把火球术从 5 张改成 3 张，看看会怎样"。
/// 收成数据之后：编辑器改它、JSON 手改它、存档带走它，三件事同时成立。
///
/// 一个存档可以有多副卡组（主牌堆 / 备牌 / 遭遇牌堆……），
/// 但**发牌时只认你点的那一副** —— 不搞"自动合并全部卡组"那种隐式行为。
/// </summary>
public sealed class CardDeck
{
	/// <summary>存档内唯一 id。发牌与存档引用都靠它。</summary>
	public string Id { get; set; } = "";

	public string DisplayName { get; set; } = "";

	/// <summary>
	/// 组成这副牌的各行。<b>次序就是发牌次序</b>（先发第一行，再发第二行），
	/// 于是不洗牌时"牌库顶"是可预期的 —— 自检要靠这一点推期望值。
	/// </summary>
	public List<CardStack> Cards { get; set; } = new();

	/// <summary>
	/// 整副牌的总张数（负数与 0 不计）。
	///
	/// <b><c>[JsonIgnore]</c> 是必须的</b>：它是从 <see cref="Cards"/> 算出来的，
	/// 而 <c>System.Text.Json</c> 默认会把只有 getter 的属性<b>写出去</b>、
	/// 读的时候又静默丢弃 —— 于是 <c>project.json</c> 里白多一行
	/// <c>"totalCards": 20</c>，而且它会与 <c>cards</c> 漂移：
	/// 手改文件的人把 <c>count</c> 从 5 改成 3，那一行却还写着 20。
	/// 同一个坑在 <see cref="ZoneDefinition.Center"/> 与
	/// <see cref="BoardTheme.BoardRect"/> 上各踩过一次，
	/// 这次是自检的 <c>deck_total_is_not_serialized</c> 当场抓住的。
	/// </summary>
	[System.Text.Json.Serialization.JsonIgnore]
	public int TotalCards
	{
		get
		{
			int total = 0;
			foreach (CardStack stack in Cards)
			{
				if (stack.Count > 0)
					total += stack.Count;
			}

			return total;
		}
	}

	/// <summary>
	/// 按次序铺开成"逐个卡牌 id"的序列 —— 发牌器直接吃它。
	///
	/// 摊开而不是让调用方自己双层循环，是为了让"次序"只有一个定义处：
	/// 自检与产品代码读的是同一个序列，否则"发出来的牌序对不对"这件事
	/// 会有两套各自成立的说法。
	/// </summary>
	public List<string> Expand()
	{
		var ids = new List<string>(TotalCards);
		foreach (CardStack stack in Cards)
		{
			for (int i = 0; i < stack.Count; i++)
				ids.Add(stack.CardId);
		}

		return ids;
	}

	public CardDeck Clone()
	{
		var copy = new CardDeck { Id = Id, DisplayName = DisplayName };
		foreach (CardStack stack in Cards)
			copy.Cards.Add(stack.Clone());

		return copy;
	}
}
