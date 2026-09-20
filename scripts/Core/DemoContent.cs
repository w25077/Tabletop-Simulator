using Godot;
using TabletopSimulator.Core.Objects;
using TabletopSimulator.Data;

namespace TabletopSimulator.Core;

/// <summary>
/// 演示内容：几张示例卡、两个 Token、两颗骰子。
///
/// 存在的意义：M4 的存档与 M5 的编辑器还没做，但 M2 必须<b>立刻能看出对不对</b>。
/// 没有内容就没法验证卡面渲染、拖拽、堆叠、掷骰 —— 所以先用代码造一批。
/// M5 做完之后，这份数据就退化成"新建存档时的示例模板"。
/// </summary>
public static class DemoContent
{
	/// <summary>把示例内容放进管理器，并按桌面中心铺开。</summary>
	public static void Populate(ObjectManager manager, Vector2 boardCenter)
	{
		CardDefinition fireball = RegisterCard(manager, new CardDefinition
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
		});

		CardDefinition goblin = RegisterCard(manager, new CardDefinition
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
		});

		CardDefinition potion = RegisterCard(manager, new CardDefinition
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
		});

		CardDefinition dragon = RegisterCard(manager, new CardDefinition
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
		});

		// 一张刻意不带任何字段的卡 —— 验证「没定义 Title 时用卡名顶替」这条路
		CardDefinition placeholder = RegisterCard(manager, new CardDefinition
		{
			Id = "demo.placeholder",
			DisplayName = "占位卡",
			FaceTint = new Color("#4c566a"),
			BorderColor = new Color("#d8dee9"),
		});

		TokenDefinition damage = RegisterToken(manager, new TokenDefinition
		{
			Id = "demo.token.damage",
			DisplayName = "伤害指示物",
			Shape = TokenShape.Circle,
			Fill = new Color("#bf616a"),
			Text = "1",
			Size = 150f,
		});

		TokenDefinition shield = RegisterToken(manager, new TokenDefinition
		{
			Id = "demo.token.shield",
			DisplayName = "护盾指示物",
			Shape = TokenShape.Hexagon,
			Fill = new Color("#5e81ac"),
			Text = "盾",
			FontSize = 38,
			Size = 150f,
		});

		// ---- 摆到桌面上 ----
		float cardW = GameConfig.DefaultCardSize.X;
		float cardH = GameConfig.DefaultCardSize.Y;
		float gap = 34f;

		float rowY = boardCenter.Y - (cardH * 0.55f);
		float row1 = boardCenter.X - (2f * (cardW + gap));

		manager.SpawnCard(fireball, new Vector2(row1, rowY));
		manager.SpawnCard(goblin, new Vector2(row1 + cardW + gap, rowY));
		manager.SpawnCard(potion, new Vector2(row1 + (2f * (cardW + gap)), rowY));
		manager.SpawnCard(dragon, new Vector2(row1 + (3f * (cardW + gap)), rowY));
		manager.SpawnCard(placeholder, new Vector2(row1 + (4f * (cardW + gap)), rowY));

		// 盖放的一排 —— 验证卡背渲染
		float backY = boardCenter.Y + (cardH * 0.75f);
		for (int i = 0; i < 4; i++)
		{
			manager.SpawnCard(
				i % 2 == 0 ? goblin : potion,
				new Vector2(row1 + (i * (cardW + gap)), backY),
				faceDown: true);
		}

		// 已经被摆成一小堆的三张卡 —— 验证阶梯偏移与堆叠数量徽章。
		// 注意：生成本身不会自动成堆（只有拖放/显式调用才会），所以要真的建堆。
		var pileBase = new Vector2(boardCenter.X + (2.2f * (cardW + gap)), backY);
		var piled = new System.Collections.Generic.List<Objects.TabletopObject>
		{
			manager.SpawnCard(goblin, pileBase),
			manager.SpawnCard(goblin, pileBase),
			manager.SpawnCard(dragon, pileBase),
		};
		manager.GroupIntoPile(piled);

		// Token 与骰子
		manager.SpawnToken(damage, new Vector2(boardCenter.X + (3.6f * (cardW + gap)), backY));
		manager.SpawnToken(shield, new Vector2(boardCenter.X + (3.6f * (cardW + gap)) + 180f, backY));

		manager.SpawnDice(20, 1, new Vector2(boardCenter.X - (3.6f * (cardW + gap)), backY));
		manager.SpawnDice(6, 3, new Vector2(boardCenter.X - (3.6f * (cardW + gap)) + 300f, backY));
	}

	private static CardDefinition RegisterCard(ObjectManager manager, CardDefinition def)
	{
		manager.CardDefinitions[def.Id] = def;
		return def;
	}

	private static TokenDefinition RegisterToken(ObjectManager manager, TokenDefinition def)
	{
		manager.TokenDefinitions[def.Id] = def;
		return def;
	}
}
