using System.Collections.Generic;
using Godot;
using TabletopSimulator.Core.Objects;
using TabletopSimulator.Data;

namespace TabletopSimulator.Core;

/// <summary>一次发牌的结果（给 UI 说清楚"到底发了几张"）。</summary>
public readonly struct DealResult
{
	public bool Ok { get; init; }

	/// <summary>实际造出来的卡张数。</summary>
	public int Spawned { get; init; }

	/// <summary>因为引用不到定义而被跳过的张数。<b>它非零时必须说出来</b>，
	/// 否则症状是"牌库少了几张，而没有任何东西说明为什么"。</summary>
	public int Skipped { get; init; }

	public string TargetLabel { get; init; }

	public string Error { get; init; }
}

/// <summary>
/// 卡组的增删改与发牌。
///
/// <b>发牌刻意走 <see cref="ZoneManager.MoveInto"/> 这条正常游戏路径</b>
/// （与 <c>DemoContent.BuildDeck</c> 同一个理由）：盖放策略、容量上限、重排
/// 全都由和实战同一份代码处理。发牌器如果自己往 <c>Zone.Members</c> 里塞，
/// 它会成为唯一一处"绕过正常路径也能成功"的地方 —— 而那种通道的存在
/// 会让正常路径上的 bug 一直藏到某天有人真的拖一张牌进去。
/// </summary>
public static class CardDeckService
{
	/// <summary>新建一副空卡组。</summary>
	public static CardDeck Create(ObjectManager objects, string displayName)
	{
		CardDeck deck = new()
		{
			Id = UniqueId(objects.Decks.Keys),
			DisplayName = string.IsNullOrWhiteSpace(displayName) ? "新卡组" : displayName.Trim(),
		};

		objects.Decks[deck.Id] = deck;
		return deck;
	}

	/// <summary>往卡组里加一张卡；已经有这一行就把张数加上去（不重复出行）。</summary>
	public static void AddCard(CardDeck deck, string cardId, int count)
	{
		foreach (CardStack stack in deck.Cards)
		{
			if (stack.CardId == cardId)
			{
				stack.Count += count;
				return;
			}
		}

		deck.Cards.Add(new CardStack { CardId = cardId, Count = count });
	}

	/// <summary>
	/// 按这副牌发一副，放进 <paramref name="targetZoneId"/> 指的区域
	/// （空串 = 直接落在桌面当散件）。
	/// </summary>
	public static DealResult DealIntoZone(
		ObjectManager objects, ZoneManager zones, Board board, CardDeck deck, string targetZoneId)
	{
		if (deck.TotalCards <= 0)
		{
			return new DealResult
			{
				Ok = false,
				Error = $"「{deck.DisplayName}」是空的（总张数 0）",
			};
		}

		Zone? zone = string.IsNullOrEmpty(targetZoneId) ? null : zones.Find(targetZoneId);

		// 牌先都造在同一个点上：进区域时区域会按自己的排版规则摆它们，
		// 而散件那条路下面会自己错开。造的时候就散开反而会打乱"谁是顶牌"。
		Vector2 spawnAt = zone is not null ? zone.StackAnchor : SpawnOrigin(board);

		var created = new List<TabletopObject>(deck.TotalCards);
		int skipped = 0;

		foreach (string cardId in deck.Expand())
		{
			if (!objects.CardDefinitions.TryGetValue(cardId, out CardDefinition? def))
			{
				skipped++;
				continue;
			}

			created.Add(objects.SpawnCard(def, spawnAt));
		}

		if (skipped > 0)
		{
			GD.PushWarning($"[CardDeckService] 卡组「{deck.Id}」有 {skipped} 张引用了不存在的卡牌定义，已跳过");
		}

		if (created.Count == 0)
		{
			return new DealResult
			{
				Ok = false,
				Skipped = skipped,
				Error = $"这副牌里的 {skipped} 张全都引用不到卡牌定义",
			};
		}

		if (zone is not null)
		{
			// MoveInto 返回的是"整次接受还是整次拒绝"（超限时不会塞进去一部分），
			// 所以张数要按 created.Count 报，不能按返回值报。
			bool accepted = zones.MoveInto(zone, created);

			return new DealResult
			{
				Ok = accepted,
				Spawned = accepted ? created.Count : 0,
				Skipped = skipped,
				TargetLabel = $"「{zone.Definition.Name}」",
				Error = accepted ? "" : $"「{zone.Definition.Name}」拒收了这 {created.Count} 张：{zones.LastRejectReason}",
			};
		}

		Scatter(created, board);
		return new DealResult
		{
			Ok = true,
			Spawned = created.Count,
			Skipped = skipped,
			TargetLabel = "桌面",
		};
	}

	/// <summary>把一副牌散在桌面上（不进任何区域）。</summary>
	public static int ScatterOnTable(ObjectManager objects, Board board, CardDeck deck)
	{
		var created = new List<TabletopObject>(deck.TotalCards);

		foreach (string cardId in deck.Expand())
		{
			if (objects.CardDefinitions.TryGetValue(cardId, out CardDefinition? def))
				created.Add(objects.SpawnCard(def, SpawnOrigin(board)));
		}

		Scatter(created, board);
		return created.Count;
	}

	/// <summary>
	/// 把一堆刚造出来的卡按网格错开，别叠成一个点。
	///
	/// <b>注意只是"错开位置"，不成堆</b> —— 与 M2 那条教训一致：
	/// 生成不会自动成堆（<c>MergeInto</c> 不会因生成而成堆）。散件就是散件，
	/// 想成堆得靠拖放或显式 <c>GroupIntoPile</c>。
	/// </summary>
	private static void Scatter(IReadOnlyList<TabletopObject> cards, Board board)
	{
		if (cards.Count == 0)
			return;

		Vector2 origin = SpawnOrigin(board);
		float step = GameConfig.DefaultCardSize.X * 0.55f;
		int perRow = Mathf.Clamp(Mathf.FloorToInt(1800f / step), 1, 12);

		for (int i = 0; i < cards.Count; i++)
		{
			int col = i % perRow;
			int row = i / perRow;
			cards[i].Position = origin + new Vector2(col * step, row * step * 0.6f);
		}
	}

	/// <summary>
	/// 新造物件落在哪 —— <b>桌面中带靠左的一块空地</b>。
	///
	/// 挑这里是<b>有具体理由的</b>，而且理由来自现有版面：
	/// 示例内容把中间那条横带留给了"散件道具"（M2 的回归断言只认
	/// <b>不属于任何区域</b>的散件），区域都压在上带与下带。
	/// 落在这儿既不进任何区域，也不和那排道具抢地方。
	///
	/// 跟着 <see cref="Board.BoardRect"/> 走而不是写死坐标：桌面尺寸是编辑器能改的
	/// （「桌面」页），写死的落点在改小桌面之后会跑到桌子外面 ——
	/// 表现为"点了一下发牌，什么都没出现"。
	/// </summary>
	public static Vector2 SpawnOrigin(Board board)
	{
		Rect2 rect = board.BoardRect;
		return rect.Position + new Vector2(rect.Size.X * 0.28f, rect.Size.Y * 0.48f);
	}

	private static string UniqueId(IEnumerable<string> existing)
	{
		var taken = new HashSet<string>(existing);
		for (int i = 1; i < 100000; i++)
		{
			string candidate = $"deck.{System.DateTime.Now:HHmmss}.{i}";
			if (!taken.Contains(candidate))
				return candidate;
		}

		return $"deck.{System.Guid.NewGuid():N}";
	}
}
