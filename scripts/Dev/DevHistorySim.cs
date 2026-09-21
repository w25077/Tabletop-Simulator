using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using TabletopSimulator.Core;
using TabletopSimulator.Core.Objects;
using TabletopSimulator.Data;

namespace TabletopSimulator.Dev;

/// <summary>
/// 撤销历史的端到端断言（M4 第 3 步）。
///
/// 与 <see cref="DevUndoSim"/> 的分工：那边测"快照写回"这个<b>机制</b>
/// （整表恢复 + 逐字段比对），这边测<b>历史本身</b> ——
/// 一次操作记几条、能不能一步步退回去、重做对不对、连击会不会合并。
///
/// <b>尽量走真实的合成输入</b>，而不是直接调 <c>UndoSystem</c> 的方法 ——
/// 那样测的只是自己刚写的代码，而不是"用户按 Ctrl+Z 会发生什么"。
/// 两个例外都要标明：菜单项是程序化触发的（<c>PopupMenu</c> 是 <c>Window</c>，
/// 合成点击会被它抢走，见 M2 踩过的坑），时间旅行也还没有 UI（第 6 步才做面板）。
/// </summary>
internal static class DevHistorySim
{
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

		internal void Data(string key, Variant value) => _dict[key] = value;

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
		Node host, BoardCamera cam, ObjectManager objects, ZoneManager zones, UndoSystem undo)
	{
		var r = new Godot.Collections.Dictionary();
		var c = new Checks(r);

		// 历史从头量起，免得受前面探针的影响
		undo.Reset();

		// ---------------------------------------------------------- 1. 拖拽 = 1 条历史
		CardObject? card = FindLooseCard(objects, cam);
		if (card is null)
		{
			r["skipped"] = "屏幕上没有散牌，没法测拖拽历史（近景截图下正常）";
			r["pass"] = false;
			return r;
		}

		SceneSnapshot beforeDrag = SceneSnapshot.Capture(objects, zones);
		Vector2 cardStart = card.Position;

		int countBefore = undo.Count;
		(Vector2 dropScreen, _) = DevInputSim.FindEmptiestScreenPoint(cam, objects, zones);
		await DevInputSim.DragToScreen(host, cam, card, dropScreen);

		// 再多等一帧：合成输入下"松手"被输入路由处理、进而触发
		// PrimaryReleased → EndGesture（历史在这一刻才产生），
		// 比 DragToScreen 内部的等待晚一步。不等这一帧就会读到"还没记"的状态
		// —— 这个坑我踩过一次：断言在手势未结束时就调 Undo()，
		// 结果把一次撤销记成了拖拽的结果。
		await DevInputSim.Frame(host);

		int dragEntries = undo.Count - countBefore;

		c.Put("drag_records_exactly_one_entry", dragEntries == 1);
		c.Put("drag_actually_moved", card.Position.DistanceTo(cardStart) > 1f);
		c.Put("drag_label_has_chinese", HasChinese(undo.UndoLabel));
		c.Put("drag_label_is_not_a_class_name",
			!undo.UndoLabel.Contains("Command") && !undo.UndoLabel.Contains("null"));

		r["drag_entries"] = dragEntries;
		r["drag_label"] = undo.UndoLabel;
		r["drag_moved_px"] = card.Position.DistanceTo(cardStart);

		SceneSnapshot afterDrag = SceneSnapshot.Capture(objects, zones);
		c.Put("history_changed_something", !SceneSnapshot.SameContent(beforeDrag, afterDrag));

		// ---------------------------------------------------------- 2. 每个入口都记一条
		//
		// "某个入口忘了接历史"是最容易漏的缝 —— 症状是"这个操作撤不掉"，
		// 用户会以为撤销坏了。所以逐个触发一遍，每个都必须产出 ≥1 条。
		//
		// 菜单项走<b>程序化触发</b>：PopupMenu 是 Window，弹出来会抢走后续合成鼠标事件
		// （M2 为此栽过一次：同一个二进制跑两次结果不同）。
		// 这里验证的是"菜单那条分支有没有接历史"，不是"点击能不能命中菜单项"。
		var entries = new Godot.Collections.Dictionary();
		int missing = 0;

		foreach (MenuAction action in new[]
		{
			MenuAction.Flip, MenuAction.RotateCw, MenuAction.Duplicate, MenuAction.Delete,
		})
		{
			CardObject? target = FindLooseCard(objects, cam);
			if (target is null)
			{
				entries[action.ToString()] = "找不到靶子";
				missing++;
				continue;
			}

			int n = undo.Count;
			bool fired = await FireMenu(host, cam, objects, target, action);
			int gained = undo.Count - n;

			entries[action.ToString()] = new Godot.Collections.Dictionary
			{
				["menu_found"] = fired,
				["entries"] = gained,
				["count_before"] = n,
				["count_after"] = undo.Count,
				["label"] = undo.UndoLabel,

			};

			if (!fired || gained < 1)
				missing++;
		}

		// 键盘那条路也各来一次
		foreach (Key key in new[] { Key.F, Key.Bracketright, Key.Bracketleft })
		{
			CardObject? target = FindLooseCard(objects, cam);
			if (target is null)
			{
				entries[key.ToString()] = "找不到靶子";
				missing++;
				continue;
			}

			// 键盘是"悬停即为目标"，所以先把鼠标移到靶子上
			Vector2 pos = cam.WorldToScreen(target.Position);
			DevInputSim.PushMotion(pos, Vector2.Zero);
			await DevInputSim.Frame(host);

			int n = undo.Count;
			float rotBefore = target.RotationDeg;
			DevInputSim.PushKey(key);
			await DevInputSim.Frame(host);
			int gained = undo.Count - n;

			entries[$"键 {key}"] = new Godot.Collections.Dictionary
			{
				["entries"] = gained,
				["uid"] = target.Uid,
				["rot_before"] = rotBefore,
				["rot_after"] = target.RotationDeg,
				["label"] = undo.UndoLabel,
			};

			if (gained < 1)
				missing++;
		}

		c.Put("every_entry_records_history", missing == 0);
		r["entry_records"] = entries;
		r["entries_missing"] = missing;

		objects.HideAllMenus();
		await DevInputSim.Frame(host);

		// ---------------------------------------------------------- 2b. 区域侧的动作也记一条
		//
		// 区域有自己的动作（抽牌 / 洗牌 / 排版 / 锁定），它们不走物件系统那条路。
		// 漏接的症状同样是"这个操作撤不掉"，所以单独验一遍。
		var zoneRecords = new Godot.Collections.Dictionary();
		int zoneMissing = 0;

		Zone? deck = zones.Find(DemoContent.DeckZoneId);
		if (deck is not null)
		{
			Vector2 deckScreen = cam.WorldToScreen(deck.Definition.Center);

			// 双击牌库 → 抽牌
			if (deck.Count > 0)
			{
				int n = undo.Count;
				await DevInputSim.PushDoubleClick(host, deckScreen);
				await DevInputSim.Frame(host);
				int gained = undo.Count - n;
				zoneRecords["双击抽牌"] = gained;

				if (gained < 1)
					zoneMissing++;
			}

			// 鼠标悬停在牌库上按 R → 洗牌
			if (deck.Count >= 2)
			{
				DevInputSim.PushMotion(deckScreen, Vector2.Zero);
				await DevInputSim.Frame(host);

				int n = undo.Count;
				DevInputSim.PushKey(Key.R);
				await DevInputSim.Frame(host);
				int gained = undo.Count - n;
				zoneRecords["R 洗牌"] = gained;

				if (gained < 1)
					zoneMissing++;
			}
		}
		else
		{
			zoneRecords["牌库"] = "找不到 demo.zone.deck";
			zoneMissing += 2;
		}

		c.Put("zone_actions_all_record", zoneMissing == 0);
		r["zone_records"] = zoneRecords;
		r["zone_missing"] = zoneMissing;

		objects.HideAllMenus();
		await DevInputSim.Frame(host);

		// ---------------------------------------------------------- 2c. 剩下的入口（M4 第 4 步）
		await ProbeEntryPoints(host, cam, objects, zones, undo, c, r);

		// ---------------------------------------------------------- 3. 撤销 / 重做（现在没有手势在跑）
		//
		// <b>必须排在所有拖拽之后</b>：手势进行中 Undo() 会被挡住
		// （见 UndoSystem.Undo 的说明）。第一版把这段排在拖拽后面紧接着，
		// 而合成的松手事件晚一帧才被处理 —— 于是 Undo() 正好落在手势中间、
		// 把一次撤销记成了拖拽的结果。这个顺序不是随便排的。
		SceneSnapshot? beforeLast = undo.PeekBeforeForTest;
		SceneSnapshot afterLast = SceneSnapshot.Capture(objects, zones);

		c.Put("gesture_not_in_progress", !undo.GestureInProgress);
		c.Put("has_a_before_snapshot_to_compare", beforeLast is not null);
		c.Put("undo_is_available", undo.CanUndo);

		bool undone = undo.Undo();
		c.Put("undo_one_step_succeeded", undone);

		SceneSnapshot sceneAfterUndo = SceneSnapshot.Capture(objects, zones);
		c.Put("undo_restores_exactly",
			beforeLast is not null && SceneSnapshot.SameContent(beforeLast, sceneAfterUndo));

		// 不成立时把"哪里不一样"写进报告 —— 少了这一行，失败信息只有"false"，
		// 而撤销的差异可能是几十个字段里的任意一个（位置 / 序号 / 正反面 / 骰子点数）。
		if (beforeLast is not null && !SceneSnapshot.SameContent(beforeLast, sceneAfterUndo))
			r["undo_restore_diff"] = FirstFieldDiff(beforeLast, sceneAfterUndo);

		c.Put("redo_is_available", undo.CanRedo);

		bool redone = undo.Redo();
		c.Put("redo_one_step_succeeded", redone);
		c.Put("redo_restores_exactly",
			SceneSnapshot.SameContent(afterLast, SceneSnapshot.Capture(objects, zones)));

		// 再退一步、进两步：连着用也不能错位
		undo.Undo();
		bool undoneAgain = undo.Undo();
		bool redoneAgain = undo.Redo();
		c.Put("repeat_undo_redo_is_stable", undoneAgain && redoneAgain);

		// ---------------------------------------------------------- 4. 手势进行中撤销要被挡住
		//
		// 合成输入：按下鼠标但不松开 → 手势开始 → 此时 Undo / TravelTo 都必须拒绝。
		// 这条断言背后是一个真 bug（见 UndoSystem.Undo 的说明）。
		CardObject? dragTarget = FindLooseCard(objects, cam);
		if (dragTarget is not null)
		{
			Vector2 start = cam.WorldToScreen(dragTarget.Position);
			DevInputSim.PushButton(start, MouseButton.Left, true);
			await DevInputSim.Frame(host);

			// 越过拖动阈值，让它真的进入"拖拽中"
			DevInputSim.PushMotion(start + new Vector2(40f, 0f), new Vector2(40f, 0f));
			await DevInputSim.Frame(host);

			c.Put("gesture_detected_while_dragging", undo.GestureInProgress);
			c.Put("undo_blocked_during_gesture", !undo.Undo() && undo.Count >= 0);
			c.Put("travel_blocked_during_gesture", undo.TravelTo(0) == -1);

			// 松手收尾
			DevInputSim.PushButton(start + new Vector2(40f, 0f), MouseButton.Left, false);
			await DevInputSim.Frame(host);
			await DevInputSim.Frame(host);
			c.Put("gesture_cleared_after_release", !undo.GestureInProgress);
		}
		else
		{
			c.Put("gesture_detected_while_dragging", false);
			c.Put("undo_blocked_during_gesture", false);
			c.Put("travel_blocked_during_gesture", false);
			c.Put("gesture_cleared_after_release", false);
		}

		// ---------------------------------------------------------- 5. 连击合并
		//
		// 连按两次 [ 应当合成 1 条"旋转 30°"（撤销一次就转回去），
		// 而不是 2 条 —— 否则用户要按两次 Ctrl+Z 才转回原位。
		CardObject? spinner = FindLooseCard(objects, cam);
		if (spinner is not null)
		{
			Vector2 pos = cam.WorldToScreen(spinner.Position);
			DevInputSim.PushMotion(pos, Vector2.Zero);
			await DevInputSim.Frame(host);

			int n = undo.Count;
			DevInputSim.PushKey(Key.Bracketright);
			await DevInputSim.Frame(host);
			DevInputSim.PushKey(Key.Bracketright);
			await DevInputSim.Frame(host);

			int spinEntries = undo.Count - n;
			string spinLabel = undo.UndoLabel;

			c.Put("rapid_rotate_merges_into_one", spinEntries == 1);
			c.Put("rapid_rotate_label_adds_up", spinLabel.Contains("30"));
			r["rotate_entries"] = spinEntries;
			r["rotate_label"] = spinLabel;

			// 合并成一条之后，撤销一次就该转回原始角度
			bool hadRotation = Mathf.Abs(spinner.RotationDeg) > 0.01f;
			undo.Undo();
			c.Put("merged_rotate_undoes_in_one_step",
				hadRotation && Mathf.Abs(spinner.RotationDeg) < 0.01f);
		}
		else
		{
			c.Put("rapid_rotate_merges_into_one", false);
			c.Put("rapid_rotate_label_adds_up", false);
			c.Put("merged_rotate_undoes_in_one_step", false);
		}

		// ---------------------------------------------------------- 6. 时间旅行
		//
		// 日志面板还没有 UI（第 6 步才做），这里直接调 ——
		// 面板做好之后这一段会改成"点面板上的第 N 行"。
		int total = undo.Count;
		int cursorBeforeTravel = undo.Cursor;
		int toStart = undo.TravelTo(0);

		c.Put("travel_to_start_reaches_start", undo.Cursor == 0);
		c.Put("travel_to_start_disables_undo", !undo.CanUndo);
		c.Put("travel_to_start_enables_redo", undo.CanRedo);

		// <b>比的必须是"走了几步"与"出发时的游标"，不是与历史总条数。</b>
		//
		// 两者只在"时间线正好停在末端"时才相等，而这一段前面刚好做过一次撤销
		// （连击合并那条断言末尾要退一步、看有没有转回原位），
		// 于是游标后面天然留着一条"重做"。原先写成 `toStart == total`，
		// 在"写回会偷偷多记历史"的年代<b>恰好</b>成立 —— 那些多出来的脏条目
		// 把游标顶回了末端，断言于是被喂饱了。那个 bug 修掉之后它才露馅。
		//
		// 这与本节开头那条"两次 TravelTo 步数不一定相等"的注释是同一类教训：
		// 断言写错量，就会在 bug 存在时显绿、在 bug 修好之后显红。
		c.Put("travel_to_start_walked_all_steps", toStart == cursorBeforeTravel);

		int backToEnd = undo.TravelTo(undo.Count);
		c.Put("travel_to_end_reaches_end", !undo.CanRedo);

		// 注意：两次 TravelTo 的步数不一定相等 —— 中间那段（连击合并的断言）
		// 可能又撤销了一步，于是"回到末端"要多走一步。比的是"走到了没有"，
		// 不是"走了多远"。第一版写成相等，是断言自己算错了。
		c.Put("travel_to_end_walked_forward", backToEnd >= 0);

		r["travel_steps"] = toStart;
		r["cursor_before_travel"] = cursorBeforeTravel;
		r["entries_total"] = total;

		// ---------------------------------------------------------- 7. 容量上限
		//
		// 这里只核对"上限确实存在且是个正整数"。
		// 超限之后旧条目被丢掉这件事<b>没有写断言</b>，如实记下原因：
		// 要触发它得在运行时把 300 条历史填满（约 300 次操作，每次一份全表快照），
		// 代价远大于它保护的东西 —— 而那段逻辑（Add 之后 RemoveAt(0)）
		// 短到可以直接看明白。**不写假断言，写清楚为什么没写。**
		r["capacity"] = UndoSystem.Capacity;
		c.Put("capacity_is_a_positive_integer", UndoSystem.Capacity > 0);

		// 收尾：回到末端、收干净菜单与选中，不给后面的探针留一张动过的桌子
		undo.TravelTo(undo.Count);
		objects.HideAllMenus();
		objects.ClearSelection();
		await DevInputSim.Frame(host);

		r["entries_at_end"] = undo.Count;
		r["pass"] = c.AllPass();
		return r;
	}

	/// <summary>右键弹出物件菜单，然后程序化触发指定的那一项。</summary>
	private static async Task<bool> FireMenu(
		Node host, BoardCamera cam, ObjectManager objects, TabletopObject target, MenuAction action)
	{
		await DevInputSim.RightClick(host, cam, target);

		PopupMenu? menu = objects.ContextMenu;
		if (menu is null || !menu.Visible)
			return false;

		// 菜单项是按<b>文本</b>加的（"翻面 (F)" / "顺时针 90°" / …），
		// 所以用文本反查 id —— 硬编码 id 会在菜单增删项之后悄悄指错。
		int id = FindItemId(menu, action);
		if (id < 0)
			return false;

		// C# 里 IdPressed 是事件，不能直接调 —— 用 Godot 的信号名发。
		menu.EmitSignal(PopupMenu.SignalName.IdPressed, id);
		objects.HideAllMenus();
		await DevInputSim.Frame(host);
		return true;
	}

	private static int FindItemId(PopupMenu menu, MenuAction action)
	{
		string needle = action switch
		{
			MenuAction.Flip => "翻面",
			MenuAction.RotateCw => "顺时针",
			MenuAction.RotateCcw => "逆时针",
			MenuAction.ResetRotation => "重置旋转",
			MenuAction.Duplicate => "复制",
			MenuAction.Delete => "删除",
			MenuAction.PullFromPile => "从堆中取出",
			MenuAction.DissolvePile => "拆散这堆",
			MenuAction.RollDice => "掷！",
			_ => "",
		};

		if (needle.Length == 0)
			return -1;

		for (int i = 0; i < menu.ItemCount; i++)
		{
			if (menu.GetItemText(i).Contains(needle, System.StringComparison.Ordinal))
				return menu.GetItemId(i);
		}

		return -1;
	}

	// ------------------------------------------------------------------ M4 第 4 步：剩下的入口

	/// <summary>
	/// 骰子 / 堆 / 区域菜单那几个入口<b>各自恰好记一条</b>。
	///
	/// 与上面两段的区别：那两段只数"有没有记"，这一段还数"记了几条" ——
	/// 少了后半句，一个入口记两条（用户要按两次 <c>Ctrl+Z</c>）和记零条一样能蒙过去。
	/// 而 <c>ObjectManager.OnPrimaryReleased</c> 里"轻点骰子自己记一条 + 收尾再记一条"
	/// 正是这么一种真实写法：看起来两条都"有记录"，实际是重复。
	/// </summary>
	private static async Task ProbeEntryPoints(
		Node host, BoardCamera cam, ObjectManager objects, ZoneManager zones,
		UndoSystem undo, Checks c, Godot.Collections.Dictionary r)
	{
		var records = new Godot.Collections.Dictionary();
		int wrong = 0;
		var diagnostics = new Godot.Collections.Dictionary();

		void Note(string key, int entries, string label)
		{
			records[key] = new Godot.Collections.Dictionary
			{
				["entries"] = entries,
				["label"] = label,
			};

			if (entries != 1)
				wrong++;
		}

		// 把这一段完整的时间线单独留一份：整份 trace 里还混着别的探针，
		// 而排查时真正要看的是"从进入本探针到结束"这一截完整的顺序。
		undo.TraceMark("===== 入口探针开始 =====");
		int traceStart = undo.PushTrace.Count - 1;

		// ---- 1. 轻点骰子即掷 ----
		DiceObject? die = FindLooseDice(objects, cam);
		undo.TraceMark($"进入 tap 探针（die={(die is null ? "无" : die.Uid)}）");
		if (die is not null)
		{
			var before = new List<int>(die.Values);
			int n = undo.Count;
			int cursorBeforeRoll = undo.Cursor;
			SceneSnapshot snapBefore = SceneSnapshot.Capture(objects, zones);
			await DevInputSim.ClickAt(host, cam.WorldToScreen(die.Position));

			// Roll() 是同步算出结果的（动画只是滚动显示），所以这里读到的就是最终点数。
			int gained = undo.Count - n;
			bool changed = !SameValues(before, die.Values);

			// `current_matches_scene` 是关键的那一问：撤销系统的 `_current`
			// 是不是还等于现场。它一旦漂了，"这一次操作改了什么"就是拿一份陈旧状态在算，
			// 而症状（历史里出现一条描述与动作对不上的条目）极难往回追。
			SceneSnapshot sceneNow = SceneSnapshot.Capture(objects, zones);
			records["轻点骰子·现场"] = new Godot.Collections.Dictionary
			{
				["count_before"] = n,
				["count_after"] = undo.Count,
				["cursor_before"] = cursorBeforeRoll,
				["cursor_after"] = undo.Cursor,
				["current_matches_scene"] = SceneSnapshot.SameContent(undo.PeekCurrentForTest, sceneNow),

				// 快照里到底哪里变了 —— 掷骰这条路的"变化"全在骰子身上，
				// 而骰子点数曾经**不在** `ObjectState.SameAs` 的比对范围里
				// （于是"掷骰"不是一次状态变化、撤不掉）。这一行让那件事随时可查。
				["snapshot_field_diff"] = FirstFieldDiff(snapBefore, sceneNow),
			};

			// <b>"点数变了"与"记了一条"必须成对。</b>
			// 只验其中一条就会出现这种假绿：点数压根没变（于是"记了一条"测的是别的东西），
			// 或者记了两条（点数确实变了、但用户要按两次 Ctrl+Z）。
			c.Put("dice_tap_records_exactly_one_entry", gained == 1 && changed);
			Note("轻点骰子", gained, undo.UndoLabel);
			c.Put("current_snapshot_matches_scene", SceneSnapshot.SameContent(undo.PeekCurrentForTest, sceneNow));

			// 撤销必须把点数一起还原 —— 这条是"骰子结果进快照"那个改动的意义所在。
			// 少了它，产品的行为是"数字跳了但撤不掉"，而其它断言全都绿。
			// （Settle 里就包含 Undo，见它的说明。）
			undo.Undo();
			c.Put("dice_roll_undo_restores_values", SameValues(before, die.Values));
			undo.DiscardRedo();
			await DevInputSim.Frame(host);

			// 撤销之后重掷：随机性必须还在（设计文档 §9 决定 2 的落地）。
			// 写成循环而不是"掷一次就不等"：单次相等的概率是 1/面数，
			// 真撞上会让断言偶发变红 —— 那种红会把人引向"撤销把 RNG 冻住了"这个错误方向。
			DiceObject? redie = FindLooseDice(objects, cam);
			if (redie is not null)
			{
				var first = new List<int>(redie.Values);
				bool different = false;
				int attempts = 0;

				for (; attempts < 8 && !different; attempts++)
				{
					int n2 = undo.Count;
					await DevInputSim.ClickAt(host, cam.WorldToScreen(redie.Position));
					if (undo.Count - n2 != 1)
						break;

					different = !SameValues(first, redie.Values);

					// 不论结果如何都退掉这一次：不然骰子会带着一个新点数进入下一段，
					// 而"点数"是快照的一部分，后面所有逐字段比对都会因此错位。
					await Settle(host, undo);
				}

				// 诚实记下试了几次：报告里能看到是"一次就不同"还是"撞了几次"。
				records["重掷尝试次数"] = attempts;
				c.Put("dice_reroll_is_still_random", different);
			}
			else
			{
				c.Put("dice_reroll_is_still_random", false);
			}
		}
		else
		{
			c.Put("dice_tap_records_exactly_one_entry", false);
			c.Put("dice_roll_undo_restores_values", false);
			c.Put("dice_reroll_is_still_random", false);
			c.Put("current_snapshot_matches_scene", false);
			records["轻点骰子"] = "屏幕上没有可点的骰子";
			wrong += 3;
		}

		objects.HideAllMenus();
		await DevInputSim.Frame(host);

		// ---- 2. 右键「掷！」菜单那条路 ----
		//
		// 这条与"轻点"是同一个动作的两个入口。分开测是有意的：
		// 轻点那条要跟 <c>EndGesture</c> 抢"谁记这一条"，菜单这条不经过手势 ——
		// 一处写对不代表另一处也对。
		//
		// <b>必须先选中。</b>「掷！」作用于选中集（与其余物件菜单项一致），
		// 而这里要是"恰好刚才点过它"，那验的就是别的探针留下的选中状态 ——
		// 一旦前面某条改了行为，这条会跟着一起假红或假绿。
		DiceObject? die2 = FindLooseDice(objects, cam);
		if (die2 is not null)
		{
			Vector2 dieScreen = cam.WorldToScreen(die2.Position);
			await DevInputSim.ClickAt(host, dieScreen);
			await Settle(host, undo);   // 上面这一次点击本身也是一掷

			var before = new List<int>(die2.Values);
			int n = undo.Count;
			bool fired = await FireMenu(host, cam, objects, die2, MenuAction.RollDice);

			int gained = undo.Count - n;
			Note("骰子菜单「掷！」", fired ? gained : -1, undo.UndoLabel);

			c.Put("dice_menu_roll_records_once",
				fired && gained == 1 && !SameValues(before, die2.Values));

			await Settle(host, undo);   // 收尾：这一掷退掉，免得它留在后面的比对基准里
		}
		else
		{
			c.Put("dice_menu_roll_records_once", false);
			records["骰子菜单「掷！」"] = "屏幕上没有可点的骰子";
			wrong++;
		}

		objects.HideAllMenus();
		await DevInputSim.Frame(host);

		// ---- 3. 自由堆：从堆中取出 / 拆散这堆 ----
		//
		// 这两项原来**完全没接历史**（M4 第 4 步补的）。漏接的症状是
		// "拆错了堆想退回，Ctrl+Z 却没反应" —— 而那一刻正是最想撤销的时刻。
		TabletopObject? pileMember = FindPileMember(objects, cam);
		if (pileMember is not null)
		{
			int pileId = pileMember.PileId;
			Pile? pile = objects.Piles.TryGetValue(pileId, out Pile? p) ? p : null;
			int membersBefore = pile?.Count ?? 0;

			// 「从堆中取出」：先把整摞选中（菜单项作用于选中集）
			await DevInputSim.ClickAt(host, cam.WorldToScreen(pileMember.Position));
			await DevInputSim.Frame(host);

			int n = undo.Count;
			bool fired = await FireMenu(host, cam, objects, pileMember, MenuAction.PullFromPile);
			Note("从堆中取出", fired ? undo.Count - n : -1, undo.UndoLabel);

			// 断言写成"这一摞少了人"，而不是"刚才那张牌的 PileId 变成 0" ——
			// 后者在写回快照之后会指向一个已经释放的节点：读它的字段**不报错、
			// 只是给出无意义的值**，于是断言会在"撤销成功"之后莫名其妙地变红。
			// 比"堆的张数"则两边都是当时抓下来的整数，怎么折腾都不会失效。
			int membersAfter = pile is not null && objects.Piles.ContainsKey(pileId)
				? objects.Piles[pileId].Count
				: 0;
			c.Put("pull_from_pile_actually_left", membersBefore > 0 && membersAfter == membersBefore - 1);

			// 再撤回来，让"拆散这堆"能在同一个堆上继续测；并**把时间线归位**（见 Settle）。
			await Settle(host, undo);

			c.Put("pull_from_pile_is_undoable",
				membersBefore > 0 && objects.Piles.TryGetValue(pileId, out Pile? restored)
				&& restored.Count == membersBefore);

			// 「拆散这堆」：同样先选中再走菜单
			TabletopObject? again = FindPileMember(objects, cam);
			if (again is not null)
			{
				int pileId2 = again.PileId;
				int before2 = objects.Piles.TryGetValue(pileId2, out Pile? p2) ? p2.Count : 0;

				await DevInputSim.ClickAt(host, cam.WorldToScreen(again.Position));
				await DevInputSim.Frame(host);

				int n2 = undo.Count;
				bool fired2 = await FireMenu(host, cam, objects, again, MenuAction.DissolvePile);
				Note("拆散这堆", fired2 ? undo.Count - n2 : -1, undo.UndoLabel);
				c.Put("dissolve_pile_actually_dissolved",
					before2 > 0 && !objects.Piles.ContainsKey(pileId2));

				await Settle(host, undo);
			}
			else
			{
				c.Put("dissolve_pile_actually_dissolved", false);
				records["拆散这堆"] = "撤回之后找不到堆成员了";
				wrong++;
			}
		}
		else
		{
			c.Put("pull_from_pile_actually_left", false);
			c.Put("dissolve_pile_actually_dissolved", false);
			c.Put("pull_from_pile_is_undoable", false);
			records["从堆中取出"] = "屏幕上没有成堆的牌";
			wrong += 2;
		}

		objects.HideAllMenus();
		await DevInputSim.Frame(host);

		// ---- 4. 区域菜单：全部翻开 / 锁定 / 查看区域（摊开） ----
		//
		// 这一段末尾会把历史**退回到出发点**（TravelTo），于是摊开这种
		// 破坏性动作不会给后面的探针留下一张摊了一桌的牌库。
		int entryCursor = undo.Cursor;

		Zone? deck = zones.Find(DemoContent.DeckZoneId);
		if (deck is not null && deck.Count >= 2 && deck.Top is TabletopObject deckTop)
		{
			// 「全部翻开」——区域菜单项作用于<b>区域</b>，不需要先选中物件
			int n = undo.Count;
			bool fired = await FireZoneMenu(host, cam, zones, deckTop, "全部翻开");
			Note("区域菜单「全部翻开」", fired ? undo.Count - n : -1, undo.UndoLabel);

			// 「锁死 / 解锁」——改的是区域定义的 Enabled 字段。
			// 这类"只改一个字段"的动作最容易被漏掉：快照差异看起来几乎没有，
			// 而它确实改变了玩法（锁上之后牌就拖不进来了）。
			int n2 = undo.Count;
			bool fired2 = await FireZoneMenu(host, cam, zones, deckTop, "锁定此区域");
			Note("区域菜单「锁定此区域」", fired2 ? undo.Count - n2 : -1, undo.UndoLabel);
			c.Put("zone_lock_actually_locked", !deck.Definition.Enabled);

			// 「查看区域（摊开 N 张）」——区域会被清空、牌摊到桌上
			int n3 = undo.Count;
			bool fired3 = await FireZoneMenu(host, cam, zones, deckTop, "查看区域");
			Note("区域菜单「查看区域」", fired3 ? undo.Count - n3 : -1, undo.UndoLabel);
			c.Put("zone_spread_actually_emptied_deck", deck.Count == 0);
		}
		else
		{
			c.Put("zone_lock_actually_locked", false);
			c.Put("zone_spread_actually_emptied_deck", false);
			records["区域菜单"] = "牌库里不足 2 张，无法测区域菜单";
			wrong += 3;
		}

		objects.HideAllMenus();
		await DevInputSim.Frame(host);

		// 退回出发点：把上面这几个破坏性动作（摊开、翻面、锁定）一并收回去，
		// 交给后面的探针一张干净的桌子。顺带再一次验了时间旅行本身。
		int traveled = undo.TravelTo(entryCursor);
		c.Put("entry_points_restored_by_travel", traveled >= 0 && undo.Cursor == entryCursor);

		objects.HideAllMenus();
		await DevInputSim.Frame(host);

		r["entry_point_records"] = records;
		r["entry_point_wrong"] = wrong;
		r["entry_point_diagnostics"] = diagnostics;
		undo.TraceMark($"入口探针结束（wrong={wrong}）");
		r["undo_counters"] = new Godot.Collections.Dictionary
		{
			["attempts"] = undo.RecordAttempts,
			["suppressed_applying"] = undo.RecordSuppressedApplying,
			["suppressed_unchanged"] = undo.RecordSuppressedUnchanged,
			["pushes"] = undo.RecordPushes,
			["appends"] = undo.AppendCount,
			["merges"] = undo.MergeCount,
			["truncated_redo"] = undo.TruncatedRedoCount,
			["entries"] = undo.Count,
		};

		var labelList = new Godot.Collections.Array();
		foreach (string s in undo.EntryLabelsForTest)
			labelList.Add(s);

		r["undo_labels"] = labelList;

		var traceList = new Godot.Collections.Array();
		for (int i = traceStart; i < undo.PushTrace.Count && i >= 0; i++)
			traceList.Add(undo.PushTrace[i]);

		r["undo_trace"] = traceList;
		r["undo_trace_start_index"] = traceStart;
		r["undo_trace_total"] = undo.PushTrace.Count;
		c.Put("entry_points_record_exactly_once", wrong == 0);
	}

	/// <summary>
	/// 探针的收尾：把测试造成的改动退掉，<b>并把时间线归位</b>。
	///
	/// 两步缺一不可：
	/// <list type="number">
	/// <item><see cref="UndoSystem.Undo"/> —— 把场景退回去（这一步是"实验做完要擦桌子"）；</item>
	/// <item><see cref="UndoSystem.DiscardRedo"/> —— 丢掉由此产生的"可重做"尾巴。
	///   少了它，下一个探针写入历史时会走"在时间线中间做新操作 → 截断掉后面的"那条路：
	///   条数<b>原地不动</b>，于是"这次操作记了几条"被数成 0。</item>
	/// </list>
	///
	/// 这个坑查了很久，因为症状是<b>自相矛盾</b>的：报告里每条入口的标签都对
	/// （说明确实记进去了）、条数却全是 0。第一版据此误判成"产品漏记了历史"，
	/// 真相是"探针自己没把手擦干净"。教训写下来：
	/// <b>诊断与探针不许改变被测对象的可观测状态 —— 时间线游标也是可观测状态。</b>
	/// </summary>
	private static async Task Settle(Node host, UndoSystem undo)
	{
		undo.Undo();
		undo.DiscardRedo();
		await DevInputSim.Frame(host);
	}

	/// <summary>右键弹出<b>区域</b>菜单，然后程序化触发包含指定文本的那一项。</summary>
	private static async Task<bool> FireZoneMenu(
		Node host, BoardCamera cam, ZoneManager zones, TabletopObject onZone, string needle)
	{
		// 右键落在区域里的物件上 → 由 ZoneManager 优先接手（叠放区域优先于物件）
		await DevInputSim.RightClick(host, cam, onZone);

		PopupMenu? menu = zones.ContextMenu;
		if (menu is null || !menu.Visible)
			return false;

		int id = -1;
		for (int i = 0; i < menu.ItemCount; i++)
		{
			if (menu.GetItemText(i).Contains(needle, System.StringComparison.Ordinal))
			{
				id = menu.GetItemId(i);
				break;
			}
		}

		if (id < 0)
		{
			zones.HideMenu();
			return false;
		}

		menu.EmitSignal(PopupMenu.SignalName.IdPressed, id);
		zones.HideMenu();
		await DevInputSim.Frame(host);
		return true;
	}

	/// <summary>
	/// 两份快照里第一个不同的字段名（诊断用）。
	///
	/// <c>SameContent</c> 只回答"一样不一样"；排查时真正想知道的是<b>哪里不一样</b>，
	/// 而拿两份 <c>SceneSnapshot</c> 去人肉 diff 是不可能的（没有打印形式）。
	/// 这一句把答案变成报告里的一行字。
	/// </summary>
	private static string FirstFieldDiff(SceneSnapshot a, SceneSnapshot b)
	{
		if (a.Objects.Count != b.Objects.Count)
			return $"物件数 {a.Objects.Count} != {b.Objects.Count}";

		for (int i = 0; i < a.Objects.Count; i++)
		{
			ObjectState x = a.Objects[i];
			ObjectState y = b.Objects[i];

			if (x.Uid != y.Uid)
				return $"#{i} uid {x.Uid} != {y.Uid}";
			if (x.Position != y.Position)
				return $"{x.Uid} pos";
			if (x.RotationDegrees != y.RotationDegrees)
				return $"{x.Uid} rot";
			if (x.FaceDown != y.FaceDown)
				return $"{x.Uid} faceDown";
			if (x.PileId != y.PileId || x.PileIndex != y.PileIndex)
				return $"{x.Uid} pile {x.PileId}/{x.PileIndex} != {y.PileId}/{y.PileIndex}";
			if (x.ZoneId != y.ZoneId)
				return $"{x.Uid} zone '{x.ZoneId}' != '{y.ZoneId}'";
			if (x.DiceSides != y.DiceSides || x.DiceCount != y.DiceCount)
				return $"{x.Uid} dice {x.DiceSides}x{x.DiceCount}";
			if (x.DiceSeed != y.DiceSeed)
				return $"{x.Uid} diceSeed {x.DiceSeed} != {y.DiceSeed}";

			if (x.DiceValues.Count != y.DiceValues.Count)
				return $"{x.Uid} diceValues 个数 {x.DiceValues.Count} != {y.DiceValues.Count}";

			for (int k = 0; k < x.DiceValues.Count; k++)
			{
				if (x.DiceValues[k] != y.DiceValues[k])
					return $"{x.Uid} diceValues[{k}] {x.DiceValues[k]} != {y.DiceValues[k]}";
			}

			if (x.FieldOverrides.Count != y.FieldOverrides.Count)
				return $"{x.Uid} overrides 个数";

			foreach (KeyValuePair<string, string> kv in x.FieldOverrides)
			{
				if (!y.FieldOverrides.TryGetValue(kv.Key, out string? v) || v != kv.Value)
					return $"{x.Uid} override[{kv.Key}]";
			}

			if (x.TokenTextOverride != y.TokenTextOverride)
				return $"{x.Uid} tokenText";
		}

		if (a.NextUidSeq != b.NextUidSeq)
			return $"nextUidSeq {a.NextUidSeq} != {b.NextUidSeq}";

		if (a.Zones.Count != b.Zones.Count)
			return $"区域数 {a.Zones.Count} != {b.Zones.Count}";

		for (int i = 0; i < a.Zones.Count; i++)
		{
			if (SaveJson.Serialize(a.Zones[i]) != SaveJson.Serialize(b.Zones[i]))
				return $"区域定义 {a.Zones[i].Id}";
		}

		if (a.ZoneMembers.Count != b.ZoneMembers.Count)
			return "区域成员表的键数不同";

		foreach (KeyValuePair<string, List<string>> kv in a.ZoneMembers)
		{
			if (!b.ZoneMembers.TryGetValue(kv.Key, out List<string>? other) ||
				kv.Value.Count != other.Count)
			{
				return $"区域 {kv.Key} 成员数";
			}

			for (int i = 0; i < kv.Value.Count; i++)
			{
				if (kv.Value[i] != other[i])
					return $"区域 {kv.Key} 成员次序[{i}] {kv.Value[i]} != {other[i]}";
			}
		}

		return "逐字段都没查出差异（那就是 SameContent 自己的比较口径与这里不一致）";
	}

	/// <summary>屏幕上可见、属于某个自由堆的物件（堆菜单那两项的靶子）。</summary>
	private static TabletopObject? FindPileMember(ObjectManager objects, BoardCamera cam)
	{
		foreach (TabletopObject obj in objects.AllObjects)
		{
			if (!obj.Visible || obj.PileId == 0)
				continue;

			if (!DevInputSim.IsOnScreen(cam, obj.Position))
				continue;

			return obj;
		}

		return null;
	}

	/// <summary>屏幕上可见、不在任何堆与区域里的骰子（轻点即掷的靶子）。</summary>
	private static DiceObject? FindLooseDice(ObjectManager objects, BoardCamera cam)
	{
		foreach (TabletopObject obj in objects.AllObjects)
		{
			if (obj is not DiceObject die || !obj.Visible)
				continue;

			if (obj.PileId != 0 || obj.ZoneId.Length != 0)
				continue;

			if (!DevInputSim.IsOnScreen(cam, obj.Position))
				continue;

			return die;
		}

		return null;
	}

	/// <summary>两组点数是否逐项相同（比的是"掷出了什么"，不是随机种子）。</summary>
	private static bool SameValues(IReadOnlyList<int> a, IReadOnlyList<int> b)
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

	/// <summary>屏幕上可见、不属于任何堆与区域的散牌。</summary>
	private static CardObject? FindLooseCard(ObjectManager objects, BoardCamera cam)
	{
		foreach (TabletopObject obj in objects.AllObjects)
		{
			if (obj is not CardObject card || !obj.Visible)
				continue;

			if (obj.PileId != 0 || obj.ZoneId.Length != 0)
				continue;

			if (!DevInputSim.IsOnScreen(cam, obj.Position))
				continue;

			return card;
		}

		return null;
	}

	/// <summary>描述里有没有中文 —— 日志是给人看的，退化成类名或英文就该被发现。</summary>
	private static bool HasChinese(string text)
	{
		foreach (char ch in text)
		{
			if (ch >= 0x4E00 && ch <= 0x9FFF)
				return true;
		}

		return false;
	}

	private enum MenuAction
	{
		Flip,
		RotateCw,
		RotateCcw,
		ResetRotation,
		Duplicate,
		Delete,
		PullFromPile,
		DissolvePile,
		RollDice,
	}
}
