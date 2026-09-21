using Godot;
using TabletopSimulator.Core;
using TabletopSimulator.Core.Objects;

namespace TabletopSimulator.Dev;

/// <summary>
/// 「一路撤销到底会发生什么」的自检（M4 撤销系统的回归断言）。
///
/// 用户实测报的 bug：<b>执行若干动作之后连续按 Ctrl+Z，桌面内容整个消失，
/// 而且 Ctrl+Y 再也拿不回来。</b>这一节把它钉成断言，并如实记下复现出来的数值。
///
/// 与 <see cref="DevHistorySim"/> 的分工：那边测"一次操作记几条、能不能退一步、
/// 重做对不对"，而它开头就 <c>Reset()</c> 了一次历史 —— 于是
/// <b>"历史的起点到底是什么"这件事永远测不到</b>。这一节刻意<b>不 Reset</b>：
/// 它必须从"这一局真实的起步状态"往下走，因为那个 bug 的根子就在起步状态上。
///
/// <b>所以本探针必须排在所有其它探针之前跑</b>（见 <c>DevCapture</c>）。
/// 只要有一个探针先动过手 —— 哪怕只是点一下空白 ——
/// <c>UndoSystem._current</c> 就会在"没有产生变化的空手势"里被重新基准化
/// （见 <c>EndGesture</c> 的"没变化"分支），于是这一节会在一个
/// <b>已经不成立的场景</b>里全绿，而玩家的桌面上照样一点 Ctrl+Z 就清空。
/// 测验场景错了比测验失败更坏。
///
/// 动作覆盖面按用户报的三条路径来：<b>拖拽手势</b>（Begin/EndGesture 那对边）、
/// <b>区域抽牌 / 洗牌</b>（ZoneManager 那条路）、<b>复制与删除</b>（要删物件的那条路）。
/// 最后一条是关键：只有"撤销之后物件变少"的步骤才会走到写回里的删除分支。
/// </summary>
internal static class DevUndoFlowSim
{
	/// <summary>撤销 / 重做循环的步数上限。正常历史远小于它；超了说明链子自己转圈了。</summary>
	private const int MaxSteps = 500;

	internal static Godot.Collections.Dictionary Probe(
		ObjectManager objects, ZoneManager zones, UndoSystem undo)
	{
		var r = new Godot.Collections.Dictionary();

		// ---------------------------------------------------------- 0. 开局的桌子
		//
		// 这就是玩家眼里的"桌面全部内容"：一副牌库、几个区域、一排散件。
		// 下面所有断言都以它为准 —— 撤销到底必须回到这里，而不是回到一张空桌子。
		SceneSnapshot opening = SceneSnapshot.Capture(objects, zones);

		r["objects_at_start"] = opening.Count;
		r["zones_at_start"] = opening.Zones.Count;
		r["entries_at_start"] = undo.Count;
		r["opening_table_has_content"] = opening.Count > 0 && opening.Zones.Count > 0;

		CardObject? card = FindLooseCard(objects);
		if (card is null)
		{
			r["skipped"] = "桌面上没有散牌，造不出历史";
			r["pass"] = false;
			return r;
		}

		var labels = new Godot.Collections.Array();

		// ---------------------------------------------------------- 1a. 区域：洗牌 + 抽牌
		//
		// 这两条是区域系统自己的入口（键盘 R / 双击牌库最终也会走到它们），
		// 与物件系统那条路各记各的历史。
		int beforeZoneActions = undo.Count;
		Zone? deck = zones.Find(DemoContent.DeckZoneId);

		if (deck is not null && deck.Count >= 2)
		{
			zones.ShuffleZone(deck);
			labels.Add(undo.UndoLabel);

			zones.DrawFrom(deck, 3);
			labels.Add(undo.UndoLabel);
		}

		r["zone_actions_recorded"] = undo.Count - beforeZoneActions;
		r["zone_actions_all_recorded"] = (int)r["zone_actions_recorded"] >= 2;

		// ---------------------------------------------------------- 1b. 拖拽手势
		//
		// 鼠标拖动一张牌时被调的就是这一对边（ViewportController.PrimaryDragStarted /
		// PrimaryReleased → UndoSystem.BeginGesture / EndGesture）。
		// 这条路的特点：它的"之前"是在<b>按下鼠标那一刻</b>抓的，不是记历史那一刻抓的。
		int beforeGesture = undo.Count;
		Vector2 cardStart = card.Position;

		undo.BeginGesture();
		card.Position = cardStart + new Vector2(140f, 90f);
		card.QueueRedraw();
		undo.EndGesture();

		r["gesture_recorded_one"] = undo.Count - beforeGesture == 1;
		labels.Add(undo.UndoLabel);

		// ---------------------------------------------------------- 1c. 复制 / 翻面 / 旋转 / 删除
		//
		// 走的是<b>真实入口</b>：这几个方法正是右键菜单与键盘最终调的那几个，
		// 所以"这一串动作该产出几条历史"和玩家手点出来的完全一致。
		//
		// 顺序是刻意排的：<b>复制排在最前面</b>，因为"撤销复制"要删掉副本 ——
		// 那正是踩雷的地方（写回路径里去调了会记历史的删除）。
		card = FindLooseCard(objects);
		if (card is not null)
		{
			objects.SelectOnly(card);

			objects.DuplicateSelection();
			labels.Add(undo.UndoLabel);

			objects.FlipSelection();
			labels.Add(undo.UndoLabel);

			objects.RotateSelection(15f);
			labels.Add(undo.UndoLabel);

			objects.DeleteSelection();
			labels.Add(undo.UndoLabel);
		}

		SceneSnapshot afterActions = SceneSnapshot.Capture(objects, zones);
		int entriesAtTop = undo.Count;

		r["action_labels"] = labels;
		r["entries_after_actions"] = entriesAtTop;
		r["objects_after_actions"] = afterActions.Count;
		r["actions_all_recorded"] = entriesAtTop >= 6;

		// ---------------------------------------------------------- 2. 一步一步撤销到底
		//
		// 每一步都查两件事：
		//   (a) 这一步本身不许产生新历史 —— 写回路径是"把已知状态抄回去"，
		//       它去记历史就等于"撤销这个动作"又变成了一个动作；
		//   (b) 走完之后历史条数不许变 —— 少了说明写回把 redo 那一截吃掉了
		//       （PushInternal 里"在时间线中间做新操作 → 丢掉后面的重做"那条规则）。
		int undoSteps = 0;
		int stepsThatAddedHistory = 0;
		int firstBadStep = -1;

		while (undo.CanUndo && undoSteps < MaxSteps)
		{
			if (!undo.Undo())
				break;

			undoSteps++;

			if (undo.Count != entriesAtTop)
			{
				stepsThatAddedHistory++;
				if (firstBadStep < 0)
					firstBadStep = undoSteps;
			}
		}

		SceneSnapshot afterUndoAll = SceneSnapshot.Capture(objects, zones);
		bool redoAvailableAfterUndoAll = undo.CanRedo;

		r["undo_steps"] = undoSteps;
		r["steps_that_added_history"] = stepsThatAddedHistory;
		r["first_bad_undo_step"] = firstBadStep;
		r["history_intact_after_undo_all"] = undo.Count == entriesAtTop;
		r["undo_steps_match_entries"] = undoSteps == entriesAtTop;
		r["can_undo_after_undo_all"] = undo.CanUndo;
		r["objects_after_undo_all"] = afterUndoAll.Count;
		r["zones_after_undo_all"] = afterUndoAll.Zones.Count;

		// 用户的原话是"撤销会删除桌面全部内容" —— 这两条盯的就是它
		r["undo_all_kept_the_table"] = afterUndoAll.Count > 0 && afterUndoAll.Zones.Count > 0;
		r["undo_all_returned_to_opening_table"] = SceneSnapshot.SameContent(opening, afterUndoAll);

		// "重做不回来" —— 撤销到底之后重做必须还有得做
		r["redo_available_after_undo_all"] = redoAvailableAfterUndoAll;

		// ---------------------------------------------------------- 3. 再一路重做回去
		int redoSteps = 0;
		while (undo.CanRedo && redoSteps < MaxSteps)
		{
			if (!undo.Redo())
				break;

			redoSteps++;
		}

		SceneSnapshot afterRedoAll = SceneSnapshot.Capture(objects, zones);

		r["redo_steps"] = redoSteps;
		r["objects_after_redo_all"] = afterRedoAll.Count;
		r["redo_all_returned_to_post_action_state"] = SceneSnapshot.SameContent(afterActions, afterRedoAll);
		r["cursor_at_end_after_redo_all"] = undo.Cursor == undo.Count;

		// 走完这么多步之后"正在写回"的标志必须是干净的 ——
		// 它若卡在 true，撤销系统就永久瘫痪，而症状看起来像"历史空了"。
		r["not_stuck_applying"] = !undo.IsApplying;

		// 收尾：把桌子恢复成开局那样，历史清干净 ——
		// 后面的探针要在一块没被动过的桌子上跑。
		// 这里直接走快照写回（与撤销同一条机制），<b>不依赖被断言的历史</b>：
		// 否则这一段在 bug 现场（历史已经乱了）就恢复不了，后面的探针会集体假红。
		opening.Restore(objects, zones);
		undo.Reset();

		// 「一步都没多记」单独做成一条布尔，读报告时不必去比数字
		r["steps_that_added_history_is_zero"] = stepsThatAddedHistory == 0;

		r["pass"] = AllPass(r, new[]
		{
			"opening_table_has_content",
			"zone_actions_all_recorded",
			"gesture_recorded_one",
			"actions_all_recorded",
			"steps_that_added_history_is_zero",
			"history_intact_after_undo_all",
			"undo_steps_match_entries",
			"undo_all_kept_the_table",
			"undo_all_returned_to_opening_table",
			"redo_available_after_undo_all",
			"redo_all_returned_to_post_action_state",
			"cursor_at_end_after_redo_all",
			"not_stuck_applying",
		});

		return r;
	}

	/// <summary>屏幕上/桌上可自由操作的一张散牌（不属于任何堆与区域）。</summary>
	private static CardObject? FindLooseCard(ObjectManager objects)
	{
		foreach (TabletopObject obj in objects.AllObjects)
		{
			if (obj is CardObject card && obj.Visible && obj.PileId == 0 && obj.ZoneId.Length == 0)
				return card;
		}

		return null;
	}

	private static bool AllPass(Godot.Collections.Dictionary r, string[] keys)
	{
		foreach (string key in keys)
		{
			if (!r.TryGetValue(key, out Variant v) || !v.AsBool())
				return false;
		}

		return true;
	}
}
