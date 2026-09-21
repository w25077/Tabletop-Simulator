using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using TabletopSimulator.Core;
using TabletopSimulator.Core.Objects;

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
		c.Put("undo_restores_exactly",
			beforeLast is not null
			&& SceneSnapshot.SameContent(beforeLast, SceneSnapshot.Capture(objects, zones)));

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
	}
}
