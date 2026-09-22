using System.Collections.Generic;
using Godot;
using TabletopSimulator.Core.Objects;
using TabletopSimulator.Data;

namespace TabletopSimulator.Core;

/// <summary>
/// 演示内容：几个区域、一副 20 张的牌库、几张示例卡、两个 Token、两颗骰子。
///
/// 存在的意义：M4 的存档与 M5 的编辑器还没做，但每个里程碑都必须<b>立刻能看出对不对</b>。
/// 没有内容就没法验证卡面渲染、拖拽、堆叠、掷骰、洗牌抽牌 —— 所以先用代码造一批。
/// M5 做完之后，这份数据就退化成"新建存档时的示例模板"。
///
/// <b>版面是刻意排成两带的</b>（见下方 <c>Layout</c> 常量）：上带与下带放区域，
/// 中间横带放"散件道具"。这不只是为了好看 —— M2 的回归断言（拖拽 / 堆叠 / 框选）
/// 挑选目标时只认"不属于任何区域的散件"，所以那些道具<b>必须完全落在所有区域矩形之外</b>，
/// 否则一条 M2 断言就会开始在别人的里程碑里随机地红。
/// </summary>
public static class DemoContent
{
	/// <summary>
	/// 版面常量。改这里就能整桌挪动，不必逐个数坐标。
	///
	/// <code>
	/// y   60 .. 520   上带：牌库 / 弃牌堆 / 出牌区 / 公共区
	/// y  560 ..1400   中带：散件道具（M2 回归用），刻意避开所有区域矩形
	/// y 1440 ..1920   下带：手牌区
	/// </code>
	/// </summary>
	private static class Layout
	{
		internal const float TopY = 60f;
		internal const float MiddleRow1Y = 780f;
		internal const float MiddleRow2Y = 1200f;

		internal const float PropsRow1X = 300f;
		internal const float PropsRow2X = 300f;
		internal const float PropsStepX = 334f;

		internal const float PileX = 2100f;
		internal const float TokenX = 2100f;
		internal const float DiceX = 2600f;

		/// <summary>计数器摆在下带，与骰子同一行、更靠右（那一带最空）。</summary>
		internal const float StatX = 2500f;
		internal const float StatY = 1560f;
	}

	/// <summary>
	/// 垃圾桶的落点（右下角）。
	///
	/// 与牌库（左上）离得最远 —— 拖牌去垃圾桶的路上不会经过别的区域，
	/// 于是"顺手丢一个"不会变成"顺手放进手牌"。
	/// </summary>
	private static readonly Vector2 TrashTopLeft = new(2860f, 1560f);

	public const string DeckZoneId = "demo.zone.deck";
	public const string HandZoneId = "demo.zone.hand";
	public const string DiscardZoneId = "demo.zone.discard";
	public const string PlayZoneId = "demo.zone.play";
	public const string PublicZoneId = "demo.zone.public";

	/// <summary>垃圾桶区域的 id（M5.5 P4）。</summary>
	public const string TrashZoneId = "demo.zone.trash";

	/// <summary>示例卡组的 id（"新建存档"与示例桌面共用同一份）。</summary>
	public const string StarterDeckId = "demo.deck.starter";

	/// <summary>牌库初始张数。抽牌 / 洗牌的自检都按它推期望值。</summary>
	public const int DeckSize = 20;

	/// <summary>
	/// 示例卡牌定义（不含桌面上任何物件）。
	///
	/// 抽出来是为了"新建存档"能复用：一个连卡牌定义都没有的空存档让人无从下手，
	/// 而 M5 的运行时编辑器正是从"有卡可改"开始工作。
	/// 这份与 <see cref="Populate"/> 用的必须是<b>同一份</b>——
	/// 两份各自维护的话，演示桌上的卡与新建存档里的卡会慢慢长得不一样。
	/// </summary>
	public static List<CardDefinition> CreateDemoCardDefinitions()
	{
		return new List<CardDefinition>
		{
			new()
			{
				Id = "demo.fireball",
				DisplayName = "火球术",
				FaceTint = new Color("#5e3a3a"),
				BorderColor = new Color("#d08770"),
				Fields =
				{
					new CardField { Key = "type", Value = "法术 · 火焰", Slot = FieldSlot.TypeLine },
					new CardField { Key = "cost", Label = "费用", Value = "3", Slot = FieldSlot.TopRight, ShowLabel = true },
					new CardField
					{
						Key = "effect", Value = "对一个目标造成 6 点伤害。若目标已被点燃，改为 9 点。",
						Slot = FieldSlot.Description,
					},
				},
			},
			new()
			{
				Id = "demo.goblin",
				DisplayName = "哥布林斥候",
				FaceTint = new Color("#3f5240"),
				BorderColor = new Color("#a3be8c"),
				Fields =
				{
					new CardField { Key = "type", Value = "生物 · 哥布林", Slot = FieldSlot.TypeLine },
					new CardField { Key = "cost", Value = "1", Slot = FieldSlot.TopLeft },
					new CardField { Key = "atk", Value = "2", Slot = FieldSlot.BottomLeft },
					new CardField { Key = "hp", Value = "1", Slot = FieldSlot.BottomRight },
					new CardField { Key = "rule", Value = "突袭。", Slot = FieldSlot.Description },
				},
			},
			new()
			{
				Id = "demo.potion",
				DisplayName = "治疗药水",
				FaceTint = new Color("#3a4a5e"),
				BorderColor = new Color("#81a1c1"),
				Fields =
				{
					new CardField { Key = "type", Value = "物品 · 消耗品", Slot = FieldSlot.TypeLine },
					new CardField { Key = "cost", Value = "1", Slot = FieldSlot.TopRight },
					new CardField { Key = "heal", Label = "回复", Value = "5", Slot = FieldSlot.Center, ShowLabel = true },
					new CardField { Key = "rule", Value = "只能在你的主要阶段使用。", Slot = FieldSlot.Description },
				},
			},
			new()
			{
				Id = "demo.dragonkin",
				DisplayName = "龙裔战士",
				FaceTint = new Color("#4a3a52"),
				BorderColor = new Color("#b48ead"),
				Fields =
				{
					new CardField { Key = "type", Value = "生物 · 龙裔", Slot = FieldSlot.TypeLine },
					new CardField { Key = "cost", Value = "5", Slot = FieldSlot.TopLeft },
					new CardField { Key = "atk", Value = "5", Slot = FieldSlot.BottomLeft },
					new CardField { Key = "hp", Value = "7", Slot = FieldSlot.BottomRight },
					new CardField
					{
						Key = "rule", Value = "嘲讽。受到伤害时，若伤害小于 3 则减半（向下取整）。",
						Slot = FieldSlot.Description,
					},
				},
			},

			// 一张刻意不带任何字段的卡 —— 验证「没定义 Title 时用卡名顶替」这条路
			new()
			{
				Id = "demo.placeholder",
				DisplayName = "占位卡",
				FaceTint = new Color("#4c566a"),
				BorderColor = new Color("#d8dee9"),
			},
		};
	}

	/// <summary>示例 Token 定义（同样抽出来给"新建存档"复用）。</summary>
	public static List<TokenDefinition> CreateDemoTokenDefinitions()
	{
		return new List<TokenDefinition>
		{
			new()
			{
				Id = "demo.token.damage",
				DisplayName = "伤害指示物",
				Shape = TokenShape.Circle,
				Fill = new Color("#bf616a"),
				Text = "1",
				Size = 150f,
			},
			new()
			{
				Id = "demo.token.shield",
				DisplayName = "护盾指示物",
				Shape = TokenShape.Hexagon,
				Fill = new Color("#5e81ac"),
				Text = "盾",
				FontSize = 38,
				Size = 150f,
			},
		};
	}

	/// <summary>
	/// 示例卡组：<b>20 张 = 5/5/4/4/2</b>。
	///
	/// <b>这个配方原先写死在 <see cref="BuildDeck"/> 的代码里，M5 把它收成了数据。</b>
	/// 理由见 <see cref="CardDeck"/> 的说明：验玩法最常做的事就是改张数，
	/// 而写死在代码里意味着"改一次 = 重新编译 + 重启 + 重新摆桌"。
	///
	/// 张数固定（不是"每个 4 张"那种凑数）是有用的：自检直接按
	/// <see cref="DeckSize"/> 推期望值，牌库剩几张、抽了几张都能算。
	/// </summary>
	public static CardDeck CreateStarterDeck(IReadOnlyList<CardDefinition> cards)
	{
		var deck = new CardDeck { Id = StarterDeckId, DisplayName = "示例卡组" };

		// 次序 = 发牌次序。按定义池的次序取前 5 个，张数 5/5/4/4/2。
		int[] counts = { 5, 5, 4, 4, 2 };
		for (int i = 0; i < cards.Count && i < counts.Length; i++)
			deck.Cards.Add(new CardStack { CardId = cards[i].Id, Count = counts[i] });

		return deck;
	}

	/// <summary>把区域与示例内容铺到桌面上。</summary>
	public static void Populate(ObjectManager manager, ZoneManager zones, Vector2 boardCenter)
	{
		_ = boardCenter;

		// ---- 卡牌与 Token 定义 ----
		List<CardDefinition> cards = CreateDemoCardDefinitions();
		foreach (CardDefinition def in cards)
			manager.CardDefinitions[def.Id] = def;

		List<TokenDefinition> tokens = CreateDemoTokenDefinitions();
		foreach (TokenDefinition def in tokens)
			manager.TokenDefinitions[def.Id] = def;

		// 卡组也要进池子（M5）：桌面上的牌库是从它铺出来的，
		// 而"编辑器里看不到这 20 张是按什么配方来的"会让人以为卡组功能没接上。
		CardDeck starter = CreateStarterDeck(cards);
		manager.Decks[starter.Id] = starter;

		CardDefinition fireball = cards[0];
		CardDefinition goblin = cards[1];
		CardDefinition potion = cards[2];
		CardDefinition dragon = cards[3];
		CardDefinition placeholder = cards[4];
		TokenDefinition damage = tokens[0];
		TokenDefinition shield = tokens[1];

		// ---- 区域 ----
		BuildZones(zones);

		// ---- 计数器（M5.5 P4）：两个，其中一个不是 60/60 ----
		BuildStats(manager);

		// ---- 牌库：20 张，盖着放进去。配方来自示例卡组（M5 收口）----
		BuildDeck(manager, zones, starter);

		// ---- 中带的散件道具（M2 回归的靶子，刻意全在区域之外）----
		BuildLooseProps(manager, fireball, goblin, potion, dragon, placeholder, damage, shield);
	}

	// ------------------------------------------------------------------ 区域

	private static void BuildZones(ZoneManager zones)
	{
		float top = Layout.TopY;

		ZoneDefinition deck = ZoneDefaults.Create(ZoneKind.Deck, DeckZoneId, "牌库", new Vector2(80f, top));
		deck.DrawTargetId = HandZoneId;
		deck.DrawCount = 1;
		zones.AddZone(deck);

		zones.AddZone(ZoneDefaults.Create(ZoneKind.Discard, DiscardZoneId, "弃牌堆", new Vector2(440f, top)));
		zones.AddZone(ZoneDefaults.Create(ZoneKind.Play, PlayZoneId, "出牌区", new Vector2(820f, top)));
		zones.AddZone(ZoneDefaults.Create(ZoneKind.Public, PublicZoneId, "公共区", new Vector2(1800f, top)));

		// 手牌区放在下带：它是唯一会"长满"的区域（横排 10 张），
		// 放中间会和散件道具抢地方。宽度用满整张桌面，才能让 10 张卡排成一行不换行。
		zones.AddZone(ZoneDefaults.Create(ZoneKind.Hand, HandZoneId, "手牌", new Vector2(16f, 1440f)));

		// ---- 垃圾桶（M5.5 P4）----
		//
		// 出厂就摆在桌上，理由是<b>可发现性</b>：用户实测反馈里那条"没有垃圾桶组件"
		// 说的是"这桌上没有这么个东西"，而不是"我没有创建它的入口"。
		// 摆在右下角 —— 与牌库（左上）离得最远，拖过去的路上不会误碰别的区域。
		zones.AddZone(ZoneDefaults.Create(ZoneKind.Trash, TrashZoneId, "垃圾桶", TrashTopLeft));
	}

	// ------------------------------------------------------------------ 计数器（M5.5 P4）

	/// <summary>
	/// 摆两个血量 / 计数器：一个默认的 <c>60/60</c>，一个<b>不是 60/60</b> 的。
	///
	/// 第二个是有意摆的 —— 用户实测反馈明确说过
	/// 「血量不一定是 60/60，也有可能是 xxx/xxx」。
	/// 桌上摆一个 <c>12/20</c>，这件事就不再只是代码里的一句注释。
	/// </summary>
	private static void BuildStats(ObjectManager manager)
	{
		manager.SpawnStat(new StatData { Label = "血量", Current = 60, Max = 60 }, new Vector2(Layout.StatX, Layout.StatY));
		manager.SpawnStat(new StatData { Label = "护甲", Current = 12, Max = 20 }, new Vector2(Layout.StatX + 280f, Layout.StatY));
	}

	// ------------------------------------------------------------------ 牌库

	/// <summary>
	/// 造一副牌放进牌库。
	///
	/// 刻意走 <see cref="ZoneManager.MoveInto"/> 这条<b>正常游戏路径</b>，
	/// 而不是直接往 <c>Zone.Members</c> 里塞 —— 这样"盖放策略、容量、重排"全都由
	/// 和实战同一份代码处理。示例内容如果是"特殊通道造出来的"，
	/// 它就会在别人改坏正常路径时依然显示正常，等于一块遮羞布。
	///
	/// <b>张数与次序来自 <see cref="CreateStarterDeck"/>，不是这里的字面量。</b>
	/// 编辑器改完卡组之后，示例桌面与"新建存档"必须给出同一副牌 ——
	/// 两份各自维护的配方迟早会长得不一样。
	/// </summary>
	private static void BuildDeck(ObjectManager manager, ZoneManager zones, CardDeck deck)
	{
		Zone? zone = zones.Find(DeckZoneId);
		if (zone is null)
			return;

		var cards = new List<TabletopObject>(DeckSize);
		Vector2 spawnAt = zone.StackAnchor;

		foreach (string cardId in deck.Expand())
		{
			if (!manager.CardDefinitions.TryGetValue(cardId, out CardDefinition? def))
			{
				GD.PushWarning($"[DemoContent] 卡组「{deck.Id}」引用了不存在的卡牌定义 {cardId}，已跳过");
				continue;
			}

			cards.Add(manager.SpawnCard(def, spawnAt));
		}

		zones.MoveInto(zone, cards);
	}

	// ------------------------------------------------------------------ 散件道具

	private static void BuildLooseProps(
		ObjectManager manager,
		CardDefinition fireball, CardDefinition goblin, CardDefinition potion,
		CardDefinition dragon, CardDefinition placeholder,
		TokenDefinition damage, TokenDefinition shield)
	{
		CardDefinition[] faces = { fireball, goblin, potion, dragon, placeholder };

		// 正面朝上的一排 —— 框选 / 悬停 / 目标落点这几条断言都靠它们
		for (int i = 0; i < faces.Length; i++)
		{
			manager.SpawnCard(
				faces[i],
				new Vector2(Layout.PropsRow1X + (i * Layout.PropsStepX), Layout.MiddleRow1Y));
		}

		// 盖放的一排 —— 验证卡背渲染
		for (int i = 0; i < 4; i++)
		{
			manager.SpawnCard(
				i % 2 == 0 ? goblin : potion,
				new Vector2(Layout.PropsRow2X + (i * Layout.PropsStepX), Layout.MiddleRow2Y),
				faceDown: true);
		}

		// 已经被摆成一小堆的三张卡 —— 验证阶梯偏移与堆叠数量徽章。
		// 注意：生成本身不会自动成堆（只有拖放/显式调用才会），所以要真的建堆。
		var pileBase = new Vector2(Layout.PileX, Layout.MiddleRow1Y);
		var piled = new List<TabletopObject>
		{
			manager.SpawnCard(goblin, pileBase),
			manager.SpawnCard(goblin, pileBase),
			manager.SpawnCard(dragon, pileBase),
		};
		manager.GroupIntoPile(piled);

		// Token 与骰子
		manager.SpawnToken(damage, new Vector2(Layout.TokenX, Layout.MiddleRow2Y));
		manager.SpawnToken(shield, new Vector2(Layout.TokenX + 220f, Layout.MiddleRow2Y));

		manager.SpawnDice(20, 1, new Vector2(Layout.DiceX, Layout.MiddleRow1Y));
		manager.SpawnDice(6, 3, new Vector2(Layout.DiceX + 250f, Layout.MiddleRow1Y));
	}

	// ------------------------------------------------------------------ 注册
}
