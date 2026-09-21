using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using TabletopSimulator.Core;
using TabletopSimulator.Core.Objects;
using TabletopSimulator.Data;

namespace TabletopSimulator.Dev;

/// <summary>
/// M3 区域系统的端到端断言：把「洗牌 → 抽 5 张 → 打出 → 弃牌」这条真实循环跑一遍。
///
/// 和 M2 的 <see cref="DevSequenceSim"/> 同一个立场 —— 单项功能各自测过，
/// 不代表<b>连起来</b>还对。区域系统尤其如此：它的状态分散在
/// 「物件自称属于谁」（<c>ZoneId</c>）与「区域认为自己有哪些成员」（<c>Members</c>）
/// 两处，中间还夹着自由堆的互斥关系。只在单项测试里看，两边永远是一致的。
///
/// 两条硬规矩（M2 用血换来的）：
/// <list type="number">
/// <item><b>不写死帧数</b> —— 帧率随环境变化（本机实测 ~165 FPS）。一律轮询直到条件成立。</item>
/// <item><b>不写死屏幕坐标</b> —— 桌面内容一变，写死的点就可能压在别的东西上。一律运行时探测。</item>
/// </list>
/// </summary>
internal static class DevZoneSim
{
	/// <summary>
	/// 记录"这次断言写了哪些键"，聚合判定直接遍历这张表。
	///
	/// 存在的理由很具体：M2 曾经把子项写成 <c>dup_ok</c>、聚合读成 <c>duplicate_ok</c>，
	/// 于是五项子断言全绿、聚合却报失败，差点去改本来正确的产品代码。
	/// 把"写键"和"读键"合成一次动作，这种漂移就不可能再发生。
	/// </summary>
	private sealed class Checks
	{
		private readonly Godot.Collections.Dictionary _dict;
		private readonly List<string> _keys = new();

		internal Checks(Godot.Collections.Dictionary dict) => _dict = dict;

		internal void Put(string key, bool value)
		{
			_dict[key] = value;
			_keys.Add(key);
		}

		internal void Put(string key, Variant value)
		{
			_dict[key] = value;
			_keys.Add(key);
		}

		internal bool AllPass()
		{
			if (_keys.Count == 0)
				return false;

			foreach (string k in _keys)
			{
				if (!_dict[k].AsBool())
					return false;
			}

			return true;
		}
	}

	internal static async Task<Godot.Collections.Dictionary> Probe(
		Node host, BoardCamera cam, ObjectManager objects, ZoneManager zones)
	{
		var r = new Godot.Collections.Dictionary();
		var c = new Checks(r);

		Zone? deck = zones.Find(DemoContent.DeckZoneId);
		Zone? hand = zones.Find(DemoContent.HandZoneId);
		Zone? discard = zones.Find(DemoContent.DiscardZoneId);
		Zone? play = zones.Find(DemoContent.PlayZoneId);

		if (deck is null || hand is null || discard is null || play is null)
		{
			r["skipped"] = "演示区域不全（需要 牌库 / 手牌 / 弃牌堆 / 出牌区）";
			r["pass"] = false;
			return r;
		}

		// 六条不变量在<b>任何操作发生之前</b>先跑一遍。这样后面某一步把它弄坏时，
		// 对比"一开始是绿的"就能确定是那一步干的。
		//
		// 注意传的是 Main 节点，不是场景树根（Window）—— ZoneProbe 会在<b>传进来的节点下</b>
		// 找 Objects / Zones。传错了不会报错，只会静默地返回一份 found=false 的空报告。
		Node main = host.GetTree().Root.GetNodeOrNull("Main") ?? host;
		r["invariants_before"] = DevReport.ZoneProbe(main);

		int deckStart = deck.Count;
		r["deck_start"] = deckStart;

		await ShuffleTwice(host, deck, c, r);
		await DrawFiveByDoubleClick(host, cam, objects, deck, hand, c, r);
		await CheckHandRowLayout(host, hand, c, r);
		await DragHandCardToPlay(host, cam, objects, hand, play, c, r);
		await DragPlayCardToDiscard(host, cam, objects, play, discard, c, r);
		await DragDiscardCardBackToDeck(host, cam, objects, discard, deck, c, r);
		await DragDeckCardOutToTable(host, cam, objects, zones, deck, c, r);
		await RejectWhenHandIsFull(host, cam, objects, deck, hand, play, c, r);

		// 放在这里是因为它会把整只手牌一次拖空 —— 前面几步都要用手牌。
		// 此时手牌正好是满的（上一步填到了 MaxCards），于是顺便测了一组更大的。
		await MultiSelectDragToTable(host, cam, objects, zones, hand, c, r);
		await LooseSelectionDragOnlyMoves(host, cam, objects, zones, c, r);

		await DeckRoundTripClearsBadge(host, cam, objects, zones, deck, c, r);
		await BackToBackPairFlip(host, cam, objects, zones, c, r);
		await StackFlipAndShuffle(host, cam, objects, zones, deck, c, r);

		// 收尾：所有操作做完之后，六条不变量必须仍然是绿的
		Godot.Collections.Dictionary after = DevReport.ZoneProbe(main);
		r["invariants_after"] = after;
		c.Put("invariants_hold_after_play", after["pass"].AsBool());

		r["pass"] = c.AllPass();
		return r;
	}

	// ------------------------------------------------------------------ 1. 洗牌

	private static async Task ShuffleTwice(Node host, Zone deck, Checks c, Godot.Collections.Dictionary r)
	{
		List<string> before = Uids(deck);
		var beforeSet = new HashSet<string>(before);

		deck.Shuffle();
		await DevInputSim.Frame(host);
		List<string> afterOne = Uids(deck);

		r["shuffle_seed_1"] = deck.LastShuffleSeed;
		r["shuffle_before"] = ToArray(before);
		r["shuffle_after_1"] = ToArray(afterOne);

		deck.Shuffle();
		await DevInputSim.Frame(host);
		List<string> afterTwo = Uids(deck);

		r["shuffle_seed_2"] = deck.LastShuffleSeed;
		r["shuffle_after_2"] = ToArray(afterTwo);

		// 洗牌是排列，不是筛选：成员集合必须一模一样
		c.Put("shuffle_keeps_members", SameSet(before, afterOne) && SameSet(before, afterTwo));

		// 而且必须真的变过至少一次 —— 否则"洗牌"其实什么都没做，
		// 但成员集合断言照样是绿的（这正是"测试说谎"的典型形状）。
		c.Put("shuffle_changed_order", !SameOrder(before, afterOne) || !SameOrder(afterOne, afterTwo));

		// 种子必须被记录，否则"这把牌为什么这么离谱"就复现不了
		c.Put("shuffle_seed_recorded", deck.LastShuffleSeed != 0 && beforeSet.Count == afterOne.Count);
	}

	// ------------------------------------------------------------------ 2. 双击抽 5 张

	private static async Task DrawFiveByDoubleClick(
		Node host, BoardCamera cam, ObjectManager objects, Zone deck, Zone hand, Checks c, Godot.Collections.Dictionary r)
	{
		int deckBefore = deck.Count;
		int handBefore = hand.Count;

		// 双击前先记下顶牌 —— 抽出来的必须<b>正是</b>它，而不只是"张数对上了"
		TabletopObject? expectedTop = deck.Top;
		string expectedUid = expectedTop?.Uid ?? "";
		r["draw_expected_top"] = expectedUid;

		Vector2 deckScreen = cam.WorldToScreen(deck.StackAnchor);

		// 第一次：单步验证"双击一次 = 抽 1 张"
		await DevInputSim.PushDoubleClick(host, deckScreen);
		await DevInputSim.Frame(host);

		r["draw_deck_after_first"] = deck.Count;
		r["draw_hand_after_first"] = hand.Count;
		c.Put("double_click_draws_one", deck.Count == deckBefore - 1 && hand.Count == handBefore + 1);

		// 抽出来的必须是原顶牌，且在手牌区里是<b>正面朝上</b>的
		TabletopObject? drawn = hand.Top;
		r["draw_got_uid"] = drawn?.Uid ?? "";
		r["draw_got_zone"] = drawn?.ZoneId ?? "";
		r["draw_got_face_down"] = drawn?.IsFaceDown ?? true;

		c.Put("double_click_draws_top_card", drawn is not null && drawn.Uid == expectedUid);
		c.Put("drawn_card_joined_hand", drawn is not null && drawn.ZoneId == hand.Id);

		// 手牌区 FaceOnEnter = FaceUp：牌库抽出来的盖牌必须被翻开，
		// 否则手里拿的是一堆卡背 —— 这是"隐藏信息"策略最容易漏的一半
		c.Put("hand_forced_face_up", drawn is not null && !drawn.IsFaceDown);

		// 再抽 4 张凑满 5（走同一条真实路径，不是直接调 API）
		for (int i = 0; i < 4; i++)
		{
			await DevInputSim.PushDoubleClick(host, deckScreen);
			await DevInputSim.Frame(host);
		}

		r["draw_deck_after_five"] = deck.Count;
		r["draw_hand_after_five"] = hand.Count;

		c.Put("drew_five_total",
			hand.Count == handBefore + 5 && deck.Count == deckBefore - 5);
		c.Put("deck_face_down_kept",
			AllFaceDown(deck));

		_ = objects;
	}

	// ------------------------------------------------------------------ 3. 手牌排版

	private static async Task CheckHandRowLayout(Node host, Zone hand, Checks c, Godot.Collections.Dictionary r)
	{
		await DevInputSim.Frame(host);

		float worstOutside = 0f;
		int visibleCount = 0;
		int overlappingPairs = 0;
		int rowCount = 0;
		float lastY = float.NaN;

		for (int i = 0; i < hand.Count; i++)
		{
			TabletopObject m = hand.Members[i];

			if (m.Visible)
				visibleCount++;

			// 整张牌都要在手牌区矩形里，不只是中心点
			Rect2 aabb = m.GetWorldAabb();
			worstOutside = Mathf.Max(worstOutside, OutsideDistance(aabb, hand.Definition.Rect));

			if (float.IsNaN(lastY) || Mathf.Abs(m.Position.Y - lastY) > 1f)
			{
				rowCount++;
				lastY = m.Position.Y;
			}
		}

		// 重叠判定用<b>包围盒两两相交</b>，不是"比较相邻两张的 X 差"。
		// 后者在换行时会假失败：第 5 张换到第二行、X 又回到最左边，
		// 于是和第一张的 X 差算出来是负数 —— 明明不重叠却报重叠。
		for (int i = 0; i < hand.Count; i++)
		{
			for (int j = i + 1; j < hand.Count; j++)
			{
				if (hand.Members[i].GetWorldAabb().Intersects(hand.Members[j].GetWorldAabb()))
					overlappingPairs++;
			}
		}

		r["hand_layout_members"] = hand.Count;
		r["hand_layout_visible"] = visibleCount;
		r["hand_layout_worst_outside_px"] = worstOutside;
		r["hand_layout_rows"] = rowCount;
		r["hand_layout_overlapping_pairs"] = overlappingPairs;

		c.Put("hand_members_all_visible", visibleCount == hand.Count && hand.Count > 0);
		c.Put("hand_cards_inside_zone", worstOutside < 0.5f);

		// 只有横排才要求互不重叠 —— 扇形排版是<b>故意</b>叠压的（省地方、像真手牌），
		// 拿同一条断言去卡它就成了"测试在说谎"。
		if (hand.Definition.SortMode == ZoneSortMode.Row)
			c.Put("hand_cards_not_overlapping", overlappingPairs == 0);
	}

	// ------------------------------------------------------------------ 4. 多选拖到桌面必须成摞

	/// <summary>
	/// 用户实测提出的行为要求：<b>手牌全选 N 张、拖到桌面上，桌面上应该是一摞牌，
	/// 右上角写着 N。</b>
	///
	/// 修改前的行为是：多张落在空地时 <c>TryMergeAfterDrop</c> <b>什么都不做</b> ——
	/// N 张牌各躺各的、永远不成堆，于是永远没有张数徽章。
	/// 而用户明明是把它们当成<b>一把</b>拿起来放下的，一次手势拿起 N 张、
	/// 放下就该是 N 张一摞。
	///
	/// 这条断言把三件事一起钉住：整组离开手牌、<b>恰好合成一堆</b>、
	/// 且徽章只画在最上面那一张上（多画一张就是错位）。
	/// </summary>
	private static async Task MultiSelectDragToTable(
		Node host, BoardCamera cam, ObjectManager objects, ZoneManager zones, Zone hand, Checks c, Godot.Collections.Dictionary r)
	{
		int n = hand.Count;
		if (n < 2)
		{
			c.Put("multi_setup_ok", false);
			return;
		}

		var picked = new List<TabletopObject>(hand.Members);

		// ---- 按下前的观察点 ----
		// 这一段是防"假断言"的关键：如果手牌本来就已经叠在一起（同一点、同一个 PileId），
		// 那"拖出去之后成了一堆"就是本来就成立的，断言再绿也说明不了任何事。
		// 必须先证明起点是<b>摊开的、各自独立的</b>。
		var idsBefore = new HashSet<int>();
		float spanBefore = 0f;
		int badgeBefore = 0;

		foreach (TabletopObject o in picked)
		{
			idsBefore.Add(o.PileId);
			spanBefore = Mathf.Max(spanBefore, o.Position.DistanceTo(picked[0].Position));

			if (o.PileCount >= 2 && o.PileIndex == o.PileCount - 1)
				badgeBefore++;
		}

		r["multi_span_before_px"] = spanBefore;
		r["multi_distinct_pile_ids_before"] = idsBefore.Count;
		r["multi_badges_before"] = badgeBefore;

		objects.SelectOnly(picked);
		await DevInputSim.Frame(host);
		await DevInputSim.Frame(host);

		(Vector2 screen, float clearance) = DevInputSim.FindEmptiestScreenPoint(cam, objects, zones);
		Vector2 dropWorld = cam.ScreenToWorld(screen);

		// 抓最靠右那张（横排下彼此不重叠，每个位置都能被独立拾取）
		// 刻意拆成"按下→拖动→(观察)→松手"两段：成堆发生在松手那一瞬间，
		// 松手之后所有牌都被排版到锚点上，就再也看不出它们原先落在哪了。
		TabletopObject grabbed = picked[^1];
		Vector2 startScreen = cam.WorldToScreen(grabbed.Position);
		Vector2 deltaScreen = screen - startScreen;

		r["multi_grabbed_start_world"] = new Godot.Collections.Array { grabbed.Position.X, grabbed.Position.Y };
		r["multi_p0_start_world"] = new Godot.Collections.Array { picked[0].Position.X, picked[0].Position.Y };
		r["multi_start_screen"] = new Godot.Collections.Array { startScreen.X, startScreen.Y };
		r["multi_target_screen"] = new Godot.Collections.Array { screen.X, screen.Y };
		r["multi_zoom"] = cam.TargetZoom;

		var firstStep = new Vector2(Mathf.Sign(deltaScreen.X) * 20f, 0f);
		if (Mathf.Abs(deltaScreen.X) < 25f)
			firstStep = deltaScreen * 0.5f;

		DevInputSim.PushButton(startScreen, MouseButton.Left, true);
		await DevInputSim.Frame(host);
		DevInputSim.PushMotion(startScreen + firstStep, firstStep);
		DevInputSim.PushMotion(screen, deltaScreen - firstStep);
		await DevInputSim.Frame(host);

		// ---- 松手前的观察点 ----
		// 整组在拖动过程中必须<b>保持原形</b>。这一条专门盯一个很难看的 bug：
		// 从横排区域里一次拖走多张时，如果"逐个移除成员、逐个重排"，
		// 每一张都会先被重排到槽位 0，于是整组塌成一点 ——
		// 看起来像"叠成了一堆"（用户就是这么描述的），实际却并没有成堆，
		// 于是既没有徽章、又跟不上光标。
		float spanAfterDrag = SpanOf(picked);
		r["multi_span_after_drag_px"] = spanAfterDrag;
		c.Put("multi_kept_shape_during_drag",
			spanBefore > 0f && Mathf.Abs(spanAfterDrag - spanBefore) < 1f);

		r["multi_grabbed_state_world"] = new Godot.Collections.Array { grabbed.Position.X, grabbed.Position.Y };
		r["multi_p0_state_world"] = new Godot.Collections.Array { picked[0].Position.X, picked[0].Position.Y };
		r["multi_expected_world"] = new Godot.Collections.Array { dropWorld.X, dropWorld.Y };

		float grabbedOffset = grabbed.Position.DistanceTo(dropWorld);
		float groupSpan = spanAfterDrag;

		r["multi_grabbed_uid"] = grabbed.Uid;
		r["multi_grabbed_offset_px"] = grabbedOffset;
		r["multi_group_span_px"] = groupSpan;

		// 被抓的那张必须停在鼠标落点上（误差容一个像素级的换算误差）。
		// 它就是"你手里捏着的那张"，跟不上光标是最直观的坏体验。
		c.Put("multi_grabbed_follows_cursor", grabbedOffset < 5f);
		r["multi_drop_world"] = new Godot.Collections.Array { dropWorld.X, dropWorld.Y };

		DevInputSim.PushButton(screen, MouseButton.Left, false);
		await DevInputSim.Frame(host);
		await DevInputSim.Frame(host);

		int inZone = 0;
		int topCardCount = 0;
		int minCount = int.MaxValue;
		int maxCount = 0;
		var pileIds = new HashSet<int>();
		TabletopObject? topCard = null;

		foreach (TabletopObject o in picked)
		{
			if (!string.IsNullOrEmpty(o.ZoneId))
				inZone++;

			pileIds.Add(o.PileId);
			minCount = Mathf.Min(minCount, o.PileCount);
			maxCount = Mathf.Max(maxCount, o.PileCount);

			// 徽章的绘制条件：PileCount >= 2 且自己就是最后一个
			if (o.PileCount >= 2 && o.PileIndex == o.PileCount - 1)
			{
				topCardCount++;
				topCard = o;
			}
		}

		float anchorOffsetTopCard = topCard is not null ? topCard.Position.DistanceTo(dropWorld) : -1f;

		r["multi_dragged"] = n;
		r["multi_hand_after"] = hand.Count;
		r["multi_clearance_px"] = clearance;
		r["multi_distinct_pile_ids"] = pileIds.Count;
		r["multi_min_pile_count"] = minCount == int.MaxValue ? 0 : minCount;
		r["multi_max_pile_count"] = maxCount;
		r["multi_cards_drawing_badge"] = topCardCount;
		r["multi_top_card_uid"] = topCard?.Uid ?? "";
		r["multi_top_card_offset_px"] = anchorOffsetTopCard;

		c.Put("multi_setup_ok", true);
		c.Put("multi_left_hand", hand.Count == 0 && inZone == 0);
		c.Put("multi_formed_one_pile", pileIds.Count == 1 && !pileIds.Contains(0));
		c.Put("multi_pile_count_is_group_size", minCount == n && maxCount == n);

		// 徽章只该画一张。画 0 张 = 用户报的问题；画多张 = 每张都以为自己是最上面那张。
		c.Put("multi_exactly_one_badge", topCardCount == 1);

		// 一摞牌要成形在你放手的地方。注意量的是<b>堆的锚点</b>，不是堆顶那张 ——
		// 堆顶带着阶梯偏移（OffsetFor(9) = (12,-12)，约 17px），量它会永远差这 17px。
		TabletopObject? topOfPile = topCard;
		Vector2 pileAnchor = topOfPile is not null && objects.Piles.TryGetValue(topOfPile.PileId, out Pile? formedPile)
			? formedPile.Anchor
			: new Vector2(float.NaN, float.NaN);

		float anchorOffset = topOfPile is not null ? pileAnchor.DistanceTo(dropWorld) : -1f;

		r["multi_anchor_world"] = new Godot.Collections.Array { pileAnchor.X, pileAnchor.Y };
		r["multi_anchor_vs_top_card_px"] = topOfPile is not null
			? topOfPile.Position.DistanceTo(pileAnchor)
			: -1f;

		c.Put("multi_pile_forms_at_drop_point",
			topOfPile is not null && anchorOffset >= 0f && anchorOffset < 5f);
	}

	// ------------------------------------------------------------------ 5. 拖手牌到出牌区

	private static async Task DragHandCardToPlay(
		Node host, BoardCamera cam, ObjectManager objects, Zone hand, Zone play, Checks c, Godot.Collections.Dictionary r)
	{
		TabletopObject? card = hand.Top;
		if (card is null)
		{
			c.Put("hand_to_play_moved", false);
			return;
		}

		string uid = card.Uid;
		int handBefore = hand.Count;
		int playBefore = play.Count;

		await DragTo(host, cam, card, play.Definition.Center);

		r["hand_to_play_uid"] = uid;
		r["hand_to_play_hand"] = hand.Count;
		r["hand_to_play_play"] = play.Count;
		r["hand_to_play_zone"] = card.ZoneId;

		c.Put("hand_to_play_moved",
			hand.Count == handBefore - 1 &&
			play.Count == playBefore + 1 &&
			card.ZoneId == play.Id);

		// 出牌区是 Free 排版：位置不该被区域改写，牌应该停在鼠标松手的地方
		float drift = card.Position.DistanceTo(play.Definition.Center);
		r["hand_to_play_drift_px"] = drift;
		c.Put("play_zone_keeps_drop_position", drift < 2f);

		_ = objects;
	}

	// ------------------------------------------------------------------ 6. 拖到弃牌堆

	private static async Task DragPlayCardToDiscard(
		Node host, BoardCamera cam, ObjectManager objects, Zone play, Zone discard, Checks c, Godot.Collections.Dictionary r)
	{
		TabletopObject? card = play.Top;
		if (card is null)
		{
			c.Put("play_to_discard_moved", false);
			return;
		}

		int playBefore = play.Count;
		int discardBefore = discard.Count;

		await DragTo(host, cam, card, discard.StackAnchor);

		r["play_to_discard_play"] = play.Count;
		r["play_to_discard_discard"] = discard.Count;
		r["play_to_discard_zone"] = card.ZoneId;
		r["play_to_discard_face_down"] = card.IsFaceDown;

		c.Put("play_to_discard_moved",
			play.Count == playBefore - 1 &&
			discard.Count == discardBefore + 1 &&
			card.ZoneId == discard.Id);

		// 弃牌堆 FaceOnEnter = FaceUp：打出去的牌必须摊开给人看
		c.Put("discard_forced_face_up", !card.IsFaceDown);

		// 弃牌堆是 Stack 排版：牌应该被吸到堆的锚点附近
		float off = card.Position.DistanceTo(discard.StackAnchor);
		r["play_to_discard_offset_px"] = off;
		c.Put("stack_zone_snapped_to_anchor", off <= (GameConfig.PileStepOffset.Length() * GameConfig.PileStepLimit) + 0.5f);

		_ = objects;
	}

	// ------------------------------------------------------------------ 7. 拖回牌库

	private static async Task DragDiscardCardBackToDeck(
		Node host, BoardCamera cam, ObjectManager objects, Zone discard, Zone deck, Checks c, Godot.Collections.Dictionary r)
	{
		TabletopObject? card = discard.Top;
		if (card is null)
		{
			c.Put("discard_to_deck_moved", false);
			return;
		}

		int discardBefore = discard.Count;
		int deckBefore = deck.Count;

		await DragTo(host, cam, card, deck.StackAnchor);

		r["discard_to_deck_discard"] = discard.Count;
		r["discard_to_deck_deck"] = deck.Count;
		r["discard_to_deck_zone"] = card.ZoneId;
		r["discard_to_deck_face_down"] = card.IsFaceDown;

		c.Put("discard_to_deck_moved",
			discard.Count == discardBefore - 1 &&
			deck.Count == deckBefore + 1 &&
			card.ZoneId == deck.Id);

		// 牌库 FaceOnEnter = FaceDown：进牌库必须盖回去
		c.Put("deck_forced_face_down", card.IsFaceDown);

		_ = objects;
	}

	// ------------------------------------------------------------------ 8. 从牌库拖到桌面空白

	private static async Task DragDeckCardOutToTable(
		Node host, BoardCamera cam, ObjectManager objects, ZoneManager zones, Zone deck, Checks c, Godot.Collections.Dictionary r)
	{
		TabletopObject? card = deck.Top;
		if (card is null)
		{
			c.Put("deck_to_table_detached", false);
			return;
		}

		int deckBefore = deck.Count;

		// 落点必须是<b>真空</b>：既没有物件、也不在任何区域矩形内。
		// 否则区域会把它接回去，这条断言就变成了在测别的东西。
		(Vector2 emptyScreen, float clearance) = DevInputSim.FindEmptiestScreenPoint(cam, objects, zones);
		r["deck_to_table_clearance_px"] = clearance;

		await DragScreenTo(host, cam, card, emptyScreen);

		r["deck_to_table_deck"] = deck.Count;
		r["deck_to_table_zone"] = card.ZoneId;
		r["deck_to_table_visible"] = card.Visible;

		c.Put("deck_to_table_detached", deck.Count == deckBefore - 1 && card.ZoneId == "");

		// 这条是最容易漏的一处：叠放区域把除最上面 3 张之外的成员设成 Visible=false，
		// 拖出来时若不恢复，卡就"消失"了 —— 数据都在，只是永远不画。
		c.Put("detached_card_visible_again", card.Visible);

		// 而且不该顺手和桌上的牌粘成一堆
		c.Put("detached_card_not_in_pile", card.PileId == 0);
	}

	// ------------------------------------------------------------------ 9. 满容量拒绝

	private static async Task RejectWhenHandIsFull(
		Node host, BoardCamera cam, ObjectManager objects, Zone deck, Zone hand, Zone play, Checks c, Godot.Collections.Dictionary r)
	{
		int max = hand.Definition.MaxCards;
		r["full_max_cards"] = max;

		if (max <= 0)
		{
			c.Put("hand_has_capacity_limit", false);
			return;
		}

		c.Put("hand_has_capacity_limit", true);

		// 先从手里挪一张到出牌区，留出"等一下要塞进去的那张"
		TabletopObject? toPlay = hand.Top;
		if (toPlay is null)
		{
			c.Put("full_setup_ok", false);
			return;
		}

		await DragTo(host, cam, toPlay, play.Definition.Center);
		c.Put("full_setup_ok", play.Count > 0 && toPlay.ZoneId == play.Id);

		// 把手牌抽到刚好满
		Vector2 deckScreen = cam.WorldToScreen(deck.StackAnchor);
		int guard = 0;
		while (hand.Count < max && guard < 32)
		{
			await DevInputSim.PushDoubleClick(host, deckScreen);
			await DevInputSim.Frame(host);
			guard++;
		}

		r["full_hand_count"] = hand.Count;
		r["full_draw_attempts"] = guard;
		c.Put("hand_filled_to_limit", hand.Count == max);

		// 现在再往里拖一张 —— 必须被整体拒绝
		int handBefore = hand.Count;
		int playBefore = play.Count;
		TabletopObject target = toPlay;

		await DragTo(host, cam, target, hand.Definition.Center);

		r["full_hand_after"] = hand.Count;
		r["full_play_after"] = play.Count;
		r["full_card_zone"] = target.ZoneId;

		c.Put("full_hand_rejected",
			hand.Count == handBefore &&
			play.Count == playBefore &&
			target.ZoneId == play.Id);

		// 再验证"锁定"这条：锁住手牌区之后，连空位也进不去
		TabletopObject? locked = play.Top;
		if (locked is not null)
		{
			hand.Definition.MaxCards = 0;   // 先腾开容量，把变量单独隔离出来
			hand.Definition.Enabled = false;
			hand.QueueRedraw();

			int lockedHandBefore = hand.Count;

			await DragTo(host, cam, locked, hand.Definition.Center);

			r["locked_hand_after"] = hand.Count;
			r["locked_card_zone"] = locked.ZoneId;
			c.Put("locked_zone_rejects_drop",
				hand.Count == lockedHandBefore && locked.ZoneId == play.Id);

			hand.Definition.MaxCards = max;
			hand.Definition.Enabled = true;
			hand.QueueRedraw();
		}
		else
		{
			c.Put("locked_zone_rejects_drop", false);
		}

		_ = objects;

		// 把双击出的牌清回去，别让后面的探针看到一桌乱账
		await DevInputSim.Frame(host);
	}

	// ------------------------------------------------------------------ 10. 进出牌库不留残留

	/// <summary>
	/// 用户实测报告的 bug：<b>把一张牌放进牌库、再拖出来，它右上角仍挂着牌库的张数徽章。</b>
	///
	/// 根因：<c>Zone.RemoveMember</c> 只清了 <c>ZoneId</c>，没清 <c>PileIndex</c> /
	/// <c>PileCount</c>。而徽章的绘制条件恰好是「<c>PileCount &gt;= 2</c> 且
	/// <c>PileIndex</c> 是最后一个」—— 拖进来的那张牌会成为牌库<b>最上面</b>一张，
	/// 两个字段正好满足条件，于是离开之后徽章还留着。
	///
	/// 这条断言刻意分两步，并且第二步以前一步成立为前提：
	/// 先确认它<b>真的拿到了</b>徽章语境，再确认拖出来之后清干净了。
	/// 只查第二步会得到一个永远绿的假断言（一个本来就干净的散件当然"没有残留"）。
	/// </summary>
	private static async Task DeckRoundTripClearsBadge(
		Node host, BoardCamera cam, ObjectManager objects, ZoneManager zones, Zone deck, Checks c, Godot.Collections.Dictionary r)
	{
		TabletopObject? card = FindLooseCard(objects, cam);
		if (card is null)
		{
			c.Put("roundtrip_setup_ok", false);
			return;
		}

		c.Put("roundtrip_setup_ok", true);

		string uid = card.Uid;
		int deckBefore = deck.Count;

		await DragTo(host, cam, card, deck.StackAnchor);

		r["roundtrip_uid"] = uid;
		r["roundtrip_zone_in"] = card.ZoneId;
		r["roundtrip_index_in"] = card.PileIndex;
		r["roundtrip_count_in"] = card.PileCount;
		r["roundtrip_deck_after_in"] = deck.Count;

		bool gainedBadgeContext = card.ZoneId == deck.Id
			&& deck.Count == deckBefore + 1
			&& card.PileCount == deck.Count
			&& card.PileIndex == deck.Count - 1;

		c.Put("roundtrip_gained_badge_context", gainedBadgeContext);

		if (!gainedBadgeContext)
			return;   // 前置不成立 → 后面的断言没有意义，宁可不写，也不写一条骗人的

		(Vector2 emptyScreen, float clearance) = DevInputSim.FindEmptiestScreenPoint(cam, objects, zones);
		r["roundtrip_out_clearance_px"] = clearance;

		await DragScreenTo(host, cam, card, emptyScreen);

		r["roundtrip_zone_out"] = card.ZoneId;
		r["roundtrip_index_out"] = card.PileIndex;
		r["roundtrip_count_out"] = card.PileCount;
		r["roundtrip_visible_out"] = card.Visible;
		r["roundtrip_deck_after_out"] = deck.Count;

		c.Put("roundtrip_left_deck", card.ZoneId == "" && deck.Count == deckBefore);

		// 这就是用户看到的那条：徽章画不画，只取决于这两项
		c.Put("roundtrip_badge_cleared", card.PileIndex == 0 && card.PileCount == 0);
		c.Put("roundtrip_visible_restored", card.Visible);
	}

	// ------------------------------------------------------------------ 拆桌检查（破坏性，必须最后跑）

	/// <summary>
	/// 清空所有区域之后，桌面上的物件必须干干净净：没有孤儿声明、没有幽灵徽章。
	///
	/// 守的是 M4 换存档那条路：清空区域时如果只把成员表清掉、不清物件上的
	/// <c>ZoneId</c> 与叠放残留，桌面上就会留下一批"孤儿声明"和幽灵徽章。
	///
	/// <b>这是破坏性的</b>（跑完就没有区域了），所以它刻意<b>不</b>放在
	/// <see cref="Probe"/> 里面 —— 试过：一放进去，后面的
	/// <c>object_simulation</c> / <c>sequence_simulation</c> 就没了区域可测，
	/// 顺序断言直接变红。探针之间不该互相拆台。
	/// 改由 <c>DevCapture</c> 在所有模拟跑完之后单独调一次。
	/// </summary>
	internal static async Task<Godot.Collections.Dictionary> VerifyTeardown(
		Node host, ObjectManager objects, ZoneManager zones)
	{
		var r = new Godot.Collections.Dictionary();

		int zonesBefore = zones.ZoneCount;
		int membersBefore = 0;
		foreach (Zone z in zones.AllZones)
			membersBefore += z.Count;

		zones.ClearAll();
		await DevInputSim.Frame(host);

		int orphanClaims = 0;
		int phantomBadges = 0;

		foreach (TabletopObject obj in objects.AllObjects)
		{
			if (!string.IsNullOrEmpty(obj.ZoneId))
				orphanClaims++;

			// 自由堆成员的 PileCount 是合法的，只有"既不在堆、也不在区域"才算残留
			if (obj.PileId == 0 && obj.PileCount > 0)
				phantomBadges++;
		}

		r["zones_before"] = zonesBefore;
		r["members_before"] = membersBefore;
		r["zones_after"] = zones.ZoneCount;
		r["orphan_claims"] = orphanClaims;
		r["phantom_badges"] = phantomBadges;

		bool removed = zonesBefore > 0 && zones.ZoneCount == 0;
		bool clean = orphanClaims == 0 && phantomBadges == 0;

		r["removes_zones"] = removed;
		r["leaves_no_orphans"] = orphanClaims == 0;
		r["leaves_no_badges"] = phantomBadges == 0;
		r["pass"] = removed && clean;
		return r;
	}

	// ------------------------------------------------------------------ 13. 不变量探针自身（防"空转的绿灯"）

	/// <summary>把失败项列表转成可进报告的数组。空数组 = 全绿。</summary>
	private static Godot.Collections.Array FailuresArray(ZoneInvariantReport report)
	{
		var arr = new Godot.Collections.Array();
		foreach (string name in report.Failures())
			arr.Add(name);

		return arr;
	}

	/// <summary>
	/// 证明那八条不变量<b>真的会吹哨</b>，而且能指名道姓说出是哪一条。
	///
	/// 为什么必须有这一节：M4 起八条不变量被抽成公共的 <see cref="ZoneInvariants.Check"/>，
	/// 撤销与读档写回快照之后要复查它。若这份实现其实是空转的（永远返回绿），
	/// 那么"撤销之后一致性仍然全绿"这句话<b>毫无价值</b> —— 而它恰恰是 M4 最依赖的一条保证。
	///
	/// 做法：先在正常局面上确认全绿（前置），再依次注入 5 种真实发生过的损坏，
	/// 每种都要求"指定那一条恰好变红"，然后逐个还原。
	///
	/// 注入的是<b>真实 bug 的样子</b>，不是随便改个字段：
	/// 幽灵声明与残留徽章正是 M3 用户实测抓到的两个。
	/// </summary>
	internal static Godot.Collections.Dictionary VerifyInvariants(ObjectManager objects, ZoneManager zones)
	{
		var r = new Godot.Collections.Dictionary();
		var c = new Checks(r);

		ZoneInvariantReport clean = ZoneInvariants.Check(objects, zones);
		c.Put("baseline_is_green", clean.All);
		r["baseline_failures"] = FailuresArray(clean);

		// ---- 注入 1：成员"不认"自己所属的区域（ZoneId 被清掉）----
		Zone? host = null;
		TabletopObject? victim = null;
		foreach (Zone z in zones.AllZones)
		{
			if (z.Count <= 0)
				continue;

			host = z;
			victim = z.Members[0];
			break;
		}

		if (host is null || victim is null)
		{
			c.Put("has_a_member_to_break", false);
			r["pass"] = c.AllPass();
			r["note"] = "区域里没有成员，无法注入损坏（近景截图下正常）";
			return r;
		}

		c.Put("has_a_member_to_break", true);

		string savedZoneId = victim.ZoneId;
		victim.ZoneId = "";
		ZoneInvariantReport broken = ZoneInvariants.Check(objects, zones);
		c.Put("detects_member_without_zone_id",
			!broken.All && !broken.ZoneIdMatchesMembership);
		r["zone_id_break_failures"] = FailuresArray(broken);
		victim.ZoneId = savedZoneId;

		// ---- 注入 2：半截账本（区域忘了这个成员，成员还认着区域）----
		//
		// 走 Zone.Members 而不是 LeaveCurrentZone：后者的职责就是"干净地离开"，
		// 它会把 ZoneId 一并清掉、根本不是 bug。半截账本的真实形态是
		// <b>只清了一边</b> —— M3 就是在这里出的事。
		//
		// 注意它触发的<b>不是</b> no_orphan_zone_claims：那个名字听起来正对，
		// 但它问的是"这个区域还在不在"。区域明明还在、只是账本少了一行，
		// 所以响的是 member_counts_match。（我第一版就断言错了这一条，
		// 是自检把这个错误纠正过来的。）
		host.Members.RemoveAt(0);                // 只动区域的账本，不动物件的声明
		ZoneInvariantReport halfBook = ZoneInvariants.Check(objects, zones);
		c.Put("detects_half_bookkeeping", !halfBook.All && !halfBook.MemberCountsMatch);
		r["half_bookkeeping_failures"] = FailuresArray(halfBook);
		host.Members.Insert(0, victim);          // 还原：账本补齐，声明本来就在

		// ---- 注入 3：孤儿声明（物件自称属于一个<b>根本不存在</b>的区域）----
		// 这才是 no_orphan_zone_claims 负责的场景：删掉区域、或换了存档，
		// 物件的 ZoneId 还指着旧 id。每次换档都会遇到，必须能抓住。
		string savedIdForOrphan = victim.ZoneId;
		victim.ZoneId = "nope.gone";
		ZoneInvariantReport orphan = ZoneInvariants.Check(objects, zones);
		c.Put("detects_orphan_claim", !orphan.All && !orphan.NoOrphanZoneClaims);
		r["orphan_break_failures"] = FailuresArray(orphan);
		victim.ZoneId = savedIdForOrphan;

		// ---- 注入 4：PileId 与 ZoneId 同时非零（两套生命周期打架）----
		TabletopObject? inZone = host.Count > 0 ? host.Members[0] : null;
		if (inZone is not null)
		{
			int savedPileId = inZone.PileId;
			inZone.PileId = 4242;
			ZoneInvariantReport overlap = ZoneInvariants.Check(objects, zones);
			c.Put("detects_pile_zone_overlap",
				!overlap.All && overlap.PileZoneOverlapCount > 0);
			inZone.PileId = savedPileId;
		}
		else
		{
			c.Put("detects_pile_zone_overlap", false);
		}

		// ---- 注入 5：残留徽章（PileCount 挂着，却不在任何堆/叠放区域里）----
		TabletopObject? badgeVictim = objects.AllObjects.Count > 0 ? objects.AllObjects[0] : null;
		if (badgeVictim is not null)
		{
			int savedCount = badgeVictim.PileCount;
			badgeVictim.PileCount = 7;
			ZoneInvariantReport badge = ZoneInvariants.Check(objects, zones);
			c.Put("detects_phantom_badge", !badge.All && badge.PhantomBadgeCount > 0);

			// 诊断必须能指到具体是哪一张 —— M3 修这个 bug 时靠的就是这个字段
			bool named = false;
			foreach (string d in badge.PhantomBadgeDetail)
			{
				if (d.Contains(badgeVictim.Uid, System.StringComparison.Ordinal))
					named = true;
			}

			c.Put("phantom_badge_names_the_culprit", named);
			badgeVictim.PileCount = savedCount;
		}
		else
		{
			c.Put("detects_phantom_badge", false);
			c.Put("phantom_badge_names_the_culprit", false);
		}

		// ---- 收尾：还原之后必须重新全绿 ----
		c.Put("restored_is_green_again", ZoneInvariants.Check(objects, zones).All);

		r["pass"] = c.AllPass();
		return r;
	}

	// ------------------------------------------------------------------ 12. 桌面散牌框选拖拽 = 仅移动
	/// <summary>
	/// 用户实测：<b>桌面上已排好位置的几张牌，框选后拖拽，结果被自动合并了；用户要的只是移动。</b>
	///
	/// 这与上一条（手牌全选拖到桌面 → 应当叠成一摞）<b>在动作上完全一样</b>，
	/// 只能靠"从哪来"区分：从区域里抓出来的是一把牌，桌面上的几张只是要换个地方摆。
	/// 规则：
	/// <list type="bullet">
	/// <item>整组<b>全部刚从区域里取出</b> + 落空地 → 叠成一摞；</item>
	/// <item>否则 → <b>仅移动</b>，保持相对排列，一概不并堆（压在别的牌上也不并）。</item>
	/// </list>
	///
	/// 这条断言同时钉住两件事：<b>没成堆</b>（PileId 仍为 0）与<b>排列没被打乱</b>
	/// （每张的位移一致 —— 少查后一条的话，"整组塌到一点"也能算"移动了"）。
	/// </summary>
	private static async Task LooseSelectionDragOnlyMoves(
		Node host, BoardCamera cam, ObjectManager objects, ZoneManager zones, Checks c, Godot.Collections.Dictionary r)
	{
		(List<CardObject> loose, _) = LooseFaceUpCards(objects, cam);

		if (loose.Count < 3)
		{
			c.Put("loose_drag_setup_ok", false);
			return;
		}

		c.Put("loose_drag_setup_ok", true);

		List<CardObject> picked = loose.GetRange(0, 3);
		var before = new Dictionary<string, Vector2>();
		foreach (CardObject card in picked)
			before[card.Uid] = card.Position;

		int pilesBefore = objects.Piles.Count;

		var selection = new List<TabletopObject>();
		foreach (CardObject card in picked)
			selection.Add(card);

		objects.SelectOnly(selection);
		await DevInputSim.Frame(host);

		Vector2 dropScreen = DevInputSim.FindEmptiestScreenPoint(cam, objects, zones).Screen;
		await DragScreenTo(host, cam, picked[^1], dropScreen);

		Vector2 grabbedDelta = picked[^1].Position - before[picked[^1].Uid];

		int piledNow = 0;
		float worstDrift = 0f;
		float moved = 0f;

		foreach (CardObject card in picked)
		{
			if (card.PileId != 0)
				piledNow++;

			Vector2 delta = card.Position - before[card.Uid];
			moved = Mathf.Max(moved, delta.Length());
			worstDrift = Mathf.Max(worstDrift, delta.DistanceTo(grabbedDelta));
		}

		r["loose_drag_moved_px"] = moved;
		r["loose_drag_worst_drift_px"] = worstDrift;
		r["loose_drag_cards_in_pile"] = piledNow;
		r["loose_drag_piles_before"] = pilesBefore;
		r["loose_drag_piles_after"] = objects.Piles.Count;

		c.Put("loose_drag_actually_moved", moved > 50f);
		c.Put("loose_drag_did_not_merge", piledNow == 0 && objects.Piles.Count == pilesBefore);

		// 排列必须原样搬过去：每张的位移都相同（组内相对位置不变）
		c.Put("loose_drag_keeps_arrangement", worstDrift < 1.5f);

		// 收尾：把这三张放回原处，别让后面的探针面对的桌子跟开门时不一样
		foreach (CardObject card in picked)
			card.Position = before[card.Uid];

		objects.ClearSelection();
		await DevInputSim.Frame(host);
	}

	// ------------------------------------------------------------------ 11. 背靠背的一对牌

	/// <summary>
	/// 用户实测：<b>把两张卡背靠背合成一摞（从上看见牌面、从下也是牌面），按 F 之后却显示卡背。</b>
	///
	/// 先把"背靠背"说清楚，它是能机械判定的：
	/// <list type="bullet">
	/// <item>底牌 <c>IsFaceDown = true</c>（正面朝下）—— 从下往上看是牌面；</item>
	/// <item>顶牌 <c>IsFaceDown = false</c>（正面朝上）—— 从上往下看是牌面。</item>
	/// </list>
	/// 于是"整摞翻过来"必须<b>保持背靠背</b>：次序反转后原来的底牌成了顶牌、
	/// 且它的朝向也跟着翻了（正面朝下 → 正面朝上），所以顶上<b>仍然显示牌面</b>。
	/// 这既是用户的预期，也是物理事实。
	///
	/// 这一节专门盯一个很容易被忽略的分支：<b>目标只解析到了一张牌</b>。
	/// 那样会走"逐张翻面"那条路 —— 只把顶上那张翻过去，盖着的一摞立刻显示卡背，
	/// 而<b>次序根本没有错位</b>，所以光看"次序有没有反转"是查不出来的。
	///
	/// <b>为什么这条路径特别容易踩到</b>：把 B 拖到 A 上之后 B 仍是选中状态
	/// （M2 刻意保留，方便拖完接着旋转）。而"悬停对象在选中集里 → 目标是选中集"
	/// 这条规则会把目标缩小成 B 一张。用户并没有"故意只选一张"，
	/// 但系统无从区分"刻意选中"与"拖拽的残留" —— 所以这里刻意<b>不清理选中集</b>。
	/// </summary>
	private static async Task BackToBackPairFlip(
		Node host, BoardCamera cam, ObjectManager objects, ZoneManager zones, Checks c, Godot.Collections.Dictionary r)
	{
		(List<CardObject> loose, _) = LooseFaceUpCards(objects, cam);

		if (loose.Count < 2)
		{
			c.Put("pair_setup_ok", false);
			return;
		}

		CardObject bottom = loose[0];
		CardObject top = loose[1];

		// 摆成背靠背：先把底下那张翻成盖放（用 API 摆局，这一步本身不是被测对象）
		if (!bottom.IsFaceDown)
			objects.FlipObjects(new[] { (TabletopObject)bottom });

		await DevInputSim.Frame(host);

		// 把 top 拖到 bottom 上 → 合成一摞。
		// 这次拖拽会<b>让 top 保持选中</b> —— 这正是用户操作完之后的真实状态。
		await DragTo(host, cam, top, bottom.Position);

		int pileId = top.PileId;
		if (pileId == 0 || !objects.Piles.TryGetValue(pileId, out Pile? pair) || pair.Count != 2)
		{
			c.Put("pair_setup_ok", false);
			r["pair_note"] = $"两张牌没能合成一堆（pileId={pileId}）";
			return;
		}

		c.Put("pair_setup_ok", true);

		r["pair_bottom_uid"] = pair.Bottom?.Uid ?? "";
		r["pair_top_uid"] = pair.Top?.Uid ?? "";
		r["pair_bottom_face_down"] = pair.Bottom?.IsFaceDown ?? false;
		r["pair_top_face_down"] = pair.Top?.IsFaceDown ?? false;
		r["pair_selection_after_drag"] = objects.Selection.Count;

		// 起点必须是"背靠背"：底盖顶开 → 上下看都是牌面
		c.Put("pair_is_back_to_back",
			pair.Bottom is not null && pair.Top is not null
			&& pair.Bottom.IsFaceDown
			&& !pair.Top.IsFaceDown);

		// ---- 按 F（刻意不清理选中集，复现用户的真实状态）----
		string topBefore = pair.Top!.Uid;
		bool topFaceBefore = pair.Top.IsFaceDown;

		await Hover(host, cam, pair.Top);

		DevInputSim.PushKey(Key.F);
		await DevInputSim.Frame(host);
		await DevInputSim.Frame(host);

		r["pair_top_after_flip"] = pair.Top?.Uid ?? "";
		r["pair_top_face_before"] = topFaceBefore;
		r["pair_top_face_after"] = pair.Top?.IsFaceDown ?? true;
		r["pair_bottom_face_after"] = pair.Bottom?.IsFaceDown ?? true;

		// 整摞翻转 = 顶牌换人（次序反转）
		c.Put("pair_flip_reversed_order", pair.Top?.Uid != topBefore);

		// 顶上仍然显示牌面 —— 这就是用户报的那一条
		c.Put("pair_flip_still_shows_face", pair.Top is not null && !pair.Top.IsFaceDown);

		// 翻完仍然是背靠背
		c.Put("pair_flip_keeps_back_to_back",
			pair.Bottom is not null && pair.Top is not null
			&& pair.Bottom.IsFaceDown
			&& !pair.Top.IsFaceDown);

		// 收尾：把这摞拆开、并把两张牌恢复成正面朝上。
		// <b>探针弄乱的东西必须自己还原</b> —— 否则后面几节会因为
		// "屏幕上已经没有足够的两张正面散件"而莫名其妙地红，
		// 而那种红看起来跟本节毫无关系。
		var touched = new List<TabletopObject>(pair.Members);
		foreach (TabletopObject m in touched)
			objects.ReleaseFromPile(m);

		foreach (TabletopObject m in touched)
		{
			m.IsFaceDown = false;
			m.QueueRedraw();
		}

		objects.ClearSelection();
		await DevInputSim.Frame(host);

		_ = zones;
	}

	/// <summary>挑出"散件、正面朝上、在屏幕内"的卡（用于摆局）。</summary>
	private static (List<CardObject> Loose, List<CardObject> Piled) LooseFaceUpCards(ObjectManager objects, BoardCamera cam)
	{
		var loose = new List<CardObject>();
		var piled = new List<CardObject>();

		foreach (TabletopObject obj in objects.AllObjects)
		{
			if (obj is not CardObject card || card.ZoneId != "" || !card.Visible || card.IsFaceDown)
				continue;

			if (card.PileId == 0 && DevInputSim.IsOnScreen(cam, card.Position))
				loose.Add(card);
			else if (card.PileId != 0)
				piled.Add(card);
		}

		return (loose, piled);
	}

	// ------------------------------------------------------------------ 12. 整摞翻转 / 洗牌

	/// <summary>
	/// 用户实测提出的两条要求：
	/// <list type="number">
	/// <item><b>指着一摞牌按 F，应当把整摞反过来</b> —— 成员次序反转（底牌变顶牌）
	///   <b>并且</b>每张牌正反面翻转。而不是"只翻恰好被指到的那一张"。</item>
	/// <item><b>一摞牌要能洗</b>，快捷键 <c>R</c>。</item>
	/// </list>
	///
	/// 同时钉住"整摞"与"单张"两条路都还在：
	/// 悬停（未选中）→ 整摞；<b>先点一下</b>（进入选中集）→ 只有那一张。
	/// 少任何一条都是退化 —— 前者少了就回到"只翻顶牌"，后者少了就再也没法单独翻一张。
	/// </summary>
	private static async Task StackFlipAndShuffle(
		Node host, BoardCamera cam, ObjectManager objects, ZoneManager zones, Zone deck, Checks c, Godot.Collections.Dictionary r)
	{
		// ---- A. 桌面自由堆：整摞翻转 ----
		Pile? pile = null;
		foreach (KeyValuePair<int, Pile> kv in objects.Piles)
		{
			if (kv.Value.Count >= 2 && kv.Value.Top is not null)
			{
				pile = kv.Value;
				break;
			}
		}

		if (pile?.Top is not TabletopObject pileTop)
		{
			c.Put("stack_flip_setup_ok", false);
			return;
		}

		c.Put("stack_flip_setup_ok", true);

		List<string> pileBefore = Uids(pile);
		List<bool> pileFacesBefore = Faces(pile);

		await Hover(host, cam, pileTop);
		DevInputSim.PushKey(Key.F);
		await DevInputSim.Frame(host);
		await DevInputSim.Frame(host);

		List<string> pileAfter = Uids(pile);
		List<bool> pileFacesAfter = Faces(pile);

		r["stack_pile_id"] = pile.Id;
		r["stack_pile_count"] = pile.Count;
		r["stack_pile_before"] = ToArray(pileBefore);
		r["stack_pile_after"] = ToArray(pileAfter);
		r["stack_pile_faces_before"] = ToArray(pileFacesBefore);
		r["stack_pile_faces_after"] = ToArray(pileFacesAfter);

		// 次序正好反转（底牌变顶牌）
		c.Put("pile_flip_reverses_order", Reversed(pileBefore, pileAfter));
		// 每张牌的正反面都翻了（整摞转了 180°）
		c.Put("pile_flip_toggles_faces", FacesFlipped(pileFacesBefore, pileFacesAfter));
		// 成员一个不多一个不少
		c.Put("pile_flip_keeps_members",
			pileAfter.Count == pileBefore.Count && new HashSet<string>(pileBefore).SetEquals(pileAfter));

		// 再翻一次应当还原 —— 这是个对合操作，做不到就说明次序与正反面没有同步
		await Hover(host, cam, pile.Top!);
		DevInputSim.PushKey(Key.F);
		await DevInputSim.Frame(host);
		await DevInputSim.Frame(host);

		c.Put("pile_flip_is_involutive",
			SameOrder(Uids(pile), pileBefore) && FacesEqual(Faces(pile), pileFacesBefore));

		// ---- B. 桌面自由堆：整摞洗牌（R） ----
		List<string> shuffleBefore = Uids(pile);

		await Hover(host, cam, pile.Top!);
		DevInputSim.PushKey(Key.R);
		await DevInputSim.Frame(host);
		await DevInputSim.Frame(host);
		List<string> shuffleAfterOne = Uids(pile);

		await Hover(host, cam, pile.Top!);
		DevInputSim.PushKey(Key.R);
		await DevInputSim.Frame(host);
		await DevInputSim.Frame(host);
		List<string> shuffleAfterTwo = Uids(pile);

		r["pile_shuffle_seed"] = pile.LastShuffleSeed;
		r["pile_shuffle_before"] = ToArray(shuffleBefore);
		r["pile_shuffle_after"] = ToArray(shuffleAfterTwo);

		c.Put("pile_shuffle_keeps_members",
			SameSet(shuffleBefore, shuffleAfterOne) && SameSet(shuffleBefore, shuffleAfterTwo));
		// 洗牌必须真的动过至少一次 —— 只查成员集合的话，"什么都没做"也能通过
		c.Put("pile_shuffle_changed_order",
			!SameOrder(shuffleBefore, shuffleAfterOne) || !SameOrder(shuffleAfterOne, shuffleAfterTwo));
		c.Put("pile_shuffle_seed_recorded", pile.LastShuffleSeed != 0);

		// ---- C. 叠放区域（牌库）：整摞翻转 + 洗牌 ----
		List<string> deckBefore = Uids(deck);
		List<bool> deckFacesBefore = Faces(deck);

		if (deck.Top is TabletopObject deckTop)
		{
			await Hover(host, cam, deckTop);
			DevInputSim.PushKey(Key.F);
			await DevInputSim.Frame(host);
			await DevInputSim.Frame(host);
		}

		List<string> deckAfter = Uids(deck);
		List<bool> deckFacesAfter = Faces(deck);

		r["stack_deck_before"] = ToArray(deckBefore);
		r["stack_deck_after"] = ToArray(deckAfter);
		r["stack_deck_faces_before"] = ToArray(deckFacesBefore);
		r["stack_deck_faces_after"] = ToArray(deckFacesAfter);
		r["stack_deck_count"] = deck.Count;

		c.Put("deck_flip_reverses_order", Reversed(deckBefore, deckAfter));
		c.Put("deck_flip_toggles_faces", FacesFlipped(deckFacesBefore, deckFacesAfter));
		c.Put("deck_flip_keeps_members",
			deckAfter.Count == deckBefore.Count && new HashSet<string>(deckBefore).SetEquals(deckAfter));

		// 翻回来，别给后面的探针留一个正面朝上的牌库
		if (deck.Top is TabletopObject deckTopAgain)
		{
			await Hover(host, cam, deckTopAgain);
			DevInputSim.PushKey(Key.F);
			await DevInputSim.Frame(host);
			await DevInputSim.Frame(host);
		}

		c.Put("deck_flip_is_involutive",
			SameOrder(Uids(deck), deckBefore) && FacesEqual(Faces(deck), deckFacesBefore));

		// 区域洗牌（R）
		List<string> deckShuffleBefore = Uids(deck);
		int seedBefore = deck.LastShuffleSeed;

		await Hover(host, cam, deck.Top!);
		DevInputSim.PushKey(Key.R);
		await DevInputSim.Frame(host);
		await DevInputSim.Frame(host);
		List<string> deckShuffleAfter = Uids(deck);

		r["deck_shuffle_seed"] = deck.LastShuffleSeed;
		c.Put("deck_shuffle_keeps_members", SameSet(deckShuffleBefore, deckShuffleAfter));
		c.Put("deck_shuffle_changed_order", !SameOrder(deckShuffleBefore, deckShuffleAfter));
		c.Put("deck_shuffle_seed_recorded",
			deck.LastShuffleSeed != 0 && deck.LastShuffleSeed != seedBefore);

		// ---- 次序同步：屏幕上谁压谁，必须和成员次序（底→顶）一致 ----
		//
		// 这一条是被一条"时红时绿"的断言逼出来的：它先是在某次运行里红了、
		// 前一次却是绿的。查下去发现是真 bug —— 洗牌改了 Members 次序却没同步
		// _drawOrder。后果很实际：**双击抽牌会抽走一张不在顶上的牌**，
		// 而画面上完全看不出异样。
		//
		// 判据刻意用"整摞的完整绘制次序"而不是"屏幕最上面那张是谁"：
		// 后者只是一个采样点，随机洗牌有约 1/N 的概率碰巧对上，
		// 于是断言会时红时绿 —— 那正是这次踩到的坑。
		List<string> visualOrder = VisualOrder(objects, deck);

		r["deck_member_order"] = ToArray(Uids(deck));
		r["deck_visual_order"] = ToArray(visualOrder);
		r["deck_member_top"] = deck.Top?.Uid ?? "";

		c.Put("stack_draw_order_matches_members", SameOrder(visualOrder, Uids(deck)));

		// ---- D. 目标解析规则：整摞 vs 选中集 ----
		//
		// 这条规则用一个不改变状态的方式观察：ObjectManager 会把解析出的目标集
		// 打到每个物件的 IsActionTarget 上（描边就靠它）。于是"当前目标是谁"
		// 可以直接读出来，不必真按一次 F 再猜。
		TabletopObject? probe = deck.Top;
		if (probe is not null)
		{
			// D1. 没有选中集 + 悬停牌库 → 整摞都是目标
			objects.ClearSelection();
			await Hover(host, cam, probe);
			int targetsWhenLone = CountTargetsIn(deck, c, r, "targets_no_selection");
			r["targets_deck_count"] = deck.Count;

			c.Put("hover_stack_targets_whole_deck",
				targetsWhenLone == deck.Count && deck.Count >= 2);

			// D2. 只选中一张（= 拖拽/点击的残留）+ 悬停同一张 → 仍然整摞。
			// 这一条就是用户那个 bug 的规则级回归：
			// 曾经"单张选中"会把目标缩成一张，于是整摞翻转退化成逐张翻面。
			objects.SelectOnly(probe);
			await DevInputSim.Frame(host);
			await Hover(host, cam, probe);
			int targetsWhenLoneSelected = CountTargetsIn(deck, c, r, "targets_lone_selection");

			c.Put("lone_selection_does_not_shrink_stack",
				targetsWhenLoneSelected == deck.Count);

			// D3. 刻意多选（一摞里的两张）+ 悬停<其中一张> → 目标就是那两张，整摞规则让位。
			// "选一组再操作"必须还在，否则框选 + F 就废了。
			//
			// 悬停的那张必须<b>本身是屏幕上最上面那张</b>：一摞牌里只有顶牌在自己的位置上
			// 能被拾取到，悬停底下的成员实际命中到的仍是顶牌 ——
			// 那样 _hovered 就不在选中集里，走的是"指着一摞"那条路（这是对的，只是没测到 D3）。
			if (deck.Count >= 3)
			{
				TabletopObject topMember = deck.Members[^1];
				var two = new List<TabletopObject> { deck.Members[^2], topMember };

				objects.SelectOnly(two);
				await DevInputSim.Frame(host);
				await Hover(host, cam, topMember);
				int targetsWhenTwo = CountTargetsIn(deck, c, r, "targets_multi_selection");
				r["targets_hovered_in_selection"] = topMember.IsActionTarget;

				c.Put("multi_selection_still_wins",
					targetsWhenTwo == 2 && topMember.IsActionTarget);
			}
			else
			{
				c.Put("multi_selection_still_wins", false);
			}

			objects.ClearSelection();
			await DevInputSim.Frame(host);
		}
		else
		{
			c.Put("hover_stack_targets_whole_deck", false);
			c.Put("lone_selection_does_not_shrink_stack", false);
			c.Put("multi_selection_still_wins", false);
		}
	}

	/// <summary>数一数某个区域的成员里有多少个是当前的操作目标（读 IsActionTarget，不改状态）。</summary>
	private static int CountTargetsIn(Zone zone, Checks c, Godot.Collections.Dictionary r, string key)
	{
		int n = 0;
		foreach (TabletopObject m in zone.Members)
		{
			if (m.IsActionTarget)
				n++;
		}

		r[key] = n;
		_ = c;
		return n;
	}

	/// <summary>
	/// 一摞牌在实际绘制次序里"从底到顶"的 uid 序列。
	/// 与 <c>Uids(zone)</c>（成员次序）比对，就能判断"屏幕上谁压谁"与"谁是顶牌"是否一致。
	/// </summary>
	private static List<string> VisualOrder(ObjectManager objects, Zone zone)
	{
		var members = new List<TabletopObject>(zone.Members);
		members.Sort((a, b) => objects.DrawIndexOf(a).CompareTo(objects.DrawIndexOf(b)));

		var list = new List<string>(members.Count);
		foreach (TabletopObject m in members)
			list.Add(m.Uid);

		return list;
	}

	/// <summary>把鼠标移到某个物件上（只移动、不点击）—— 建立"悬停"。</summary>
	private static async Task Hover(Node host, BoardCamera cam, TabletopObject obj)
	{
		Vector2 target = cam.WorldToScreen(obj.Position);
		DevInputSim.PushMotion(target, Vector2.Zero);
		await DevInputSim.Frame(host);
	}

	/// <summary>物件列表的 uid 序列（底 → 顶）。</summary>
	private static List<string> Uids(Pile pile)
	{
		var list = new List<string>(pile.Count);
		foreach (TabletopObject m in pile.Members)
			list.Add(m.Uid);

		return list;
	}

	private static List<bool> Faces(Pile pile)
	{
		var list = new List<bool>(pile.Count);
		foreach (TabletopObject m in pile.Members)
			list.Add(m.IsFaceDown);

		return list;
	}

	private static List<bool> Faces(Zone zone)
	{
		var list = new List<bool>(zone.Count);
		foreach (TabletopObject m in zone.Members)
			list.Add(m.IsFaceDown);

		return list;
	}

	/// <summary>b 是否正好是 a 的反序。</summary>
	private static bool Reversed(List<string> a, List<string> b)
	{
		if (a.Count != b.Count)
			return false;

		for (int i = 0; i < a.Count; i++)
		{
			if (a[i] != b[a.Count - 1 - i])
				return false;
		}

		return true;
	}

	/// <summary>b 是否正好是 a 逐项取反。</summary>
	private static bool FacesFlipped(List<bool> a, List<bool> b)
	{
		if (a.Count != b.Count)
			return false;

		for (int i = 0; i < a.Count; i++)
		{
			if (b[i] == a[i])
				return false;
		}

		return true;
	}

	private static bool FacesEqual(List<bool> a, List<bool> b)
	{
		if (a.Count != b.Count)
			return false;

		for (int i = 0; i < a.Count; i++)
		{
			if (a[i] != b[i])
				return false;
		}

		return true;
	}

	private static Godot.Collections.Array ToArray(List<bool> values)
	{
		var arr = new Godot.Collections.Array();
		foreach (bool v in values)
			arr.Add(v);

		return arr;
	}

	// ------------------------------------------------------------------ 工具

	/// <summary>挑一张能自由拖动的散件卡（不属于任何区域、不在自由堆里、正面朝上、在屏幕内）。</summary>
	private static TabletopObject? FindLooseCard(ObjectManager objects, BoardCamera cam)
	{
		foreach (TabletopObject obj in objects.AllObjects)
		{
			if (obj is CardObject card && card.ZoneId == "" && card.PileId == 0
				&& card.Visible && !card.IsFaceDown
				&& DevInputSim.IsOnScreen(cam, card.Position))
				return card;
		}

		return null;
	}

	/// <summary>把物件拖到某个<b>世界坐标</b>（内部换算成屏幕坐标）。</summary>
	private static async Task DragTo(Node host, BoardCamera cam, TabletopObject obj, Vector2 worldTarget)
		=> await DragScreenTo(host, cam, obj, cam.WorldToScreen(worldTarget));

	/// <summary>
	/// 合成一次完整拖拽。
	/// 第一段必须越过 <see cref="GameConfig.DragThresholdPixels"/>，才会从"待定"变成"拖拽中"
	/// —— 否则这次手势会被当成"点击"，区域根本收不到落点。
	/// </summary>
	private static async Task DragScreenTo(Node host, BoardCamera cam, TabletopObject obj, Vector2 screenTarget)
	{
		Vector2 start = cam.WorldToScreen(obj.Position);
		Vector2 delta = screenTarget - start;

		var first = new Vector2(Mathf.Sign(delta.X) * 20f, 0f);
		if (Mathf.Abs(delta.X) < 25f)
			first = delta * 0.5f;

		Vector2 second = delta - first;

		DevInputSim.PushButton(start, MouseButton.Left, true);
		await DevInputSim.Frame(host);
		DevInputSim.PushMotion(start + first, first);
		DevInputSim.PushMotion(screenTarget, second);
		DevInputSim.PushButton(screenTarget, MouseButton.Left, false);

		await DevInputSim.Frame(host);
		await DevInputSim.Frame(host);
	}

	private static List<string> Uids(Zone zone)
	{
		var list = new List<string>(zone.Count);
		foreach (TabletopObject m in zone.Members)
			list.Add(m.Uid);

		return list;
	}

	private static Godot.Collections.Array ToArray(List<string> values)
	{
		var arr = new Godot.Collections.Array();
		foreach (string v in values)
			arr.Add(v);

		return arr;
	}

	private static bool SameSet(List<string> a, List<string> b)
	{
		if (a.Count != b.Count)
			return false;

		return new HashSet<string>(a).SetEquals(b);
	}

	private static bool SameOrder(List<string> a, List<string> b)
	{
		if (a.Count != b.Count)
			return false;

		for (int i = 0; i < a.Count; i++)
		{
			if (a[i] != b[i])
				return false;
		}

		return true;
	}

	/// <summary>一组物件里最远两点之间的距离（诊断用：跨度突然变 0 就说明整组被塌到了一点）。</summary>
	private static float SpanOf(IReadOnlyList<TabletopObject> objects)
	{
		float span = 0f;
		for (int i = 1; i < objects.Count; i++)
			span = Mathf.Max(span, objects[i].Position.DistanceTo(objects[0].Position));

		return span;
	}

	private static bool AllFaceDown(Zone zone)	{
		foreach (TabletopObject m in zone.Members)
		{
			if (!m.IsFaceDown)
				return false;
		}

		return true;
	}

	/// <summary>矩形完全落在容器内时返回 0，否则返回越界的最大距离（世界单位）。</summary>
	private static float OutsideDistance(Rect2 inner, Rect2 outer)
	{
		float d = 0f;
		d = Mathf.Max(d, outer.Position.X - inner.Position.X);
		d = Mathf.Max(d, outer.Position.Y - inner.Position.Y);
		d = Mathf.Max(d, inner.End.X - outer.End.X);
		d = Mathf.Max(d, inner.End.Y - outer.End.Y);
		return Mathf.Max(d, 0f);
	}
}
