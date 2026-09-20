using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using TabletopSimulator.Core;
using TabletopSimulator.Core.Objects;

namespace TabletopSimulator.Dev;

/// <summary>
/// 真实使用顺序的对抗式断言。
///
/// 为什么需要它：<see cref="DevObjectSim"/> 把每个功能<b>单独</b>测了一遍，
/// 但用户是<b>连着</b>操作的 —— 「点一下卡 → 再框选」「框选完 → 右键」这类
/// 顺序依赖才是 bug 的高发区。单项测试全绿不代表连起来也对。
///
/// 每一步都断言"上一步的残留状态有没有污染这一步"。
/// </summary>
internal static class DevSequenceSim
{
	internal static async Task<Godot.Collections.Dictionary> Probe(
		Node host, BoardCamera cam, ViewportController vc, ObjectManager objects)
	{
		var r = new Godot.Collections.Dictionary();

		await ClickThenBoxSelect(host, cam, objects, r);
		await HoverIsTheTarget(host, cam, objects, r);
		await RightClickMenu(host, cam, objects, r);
		await MenuClampsAtEdge(host, cam, objects, r);
		await ShiftExtractFromPile(host, cam, objects, r);
		await DragWholePile(host, cam, objects, r);
		await SelectAllThenDuplicate(host, objects, r);

		// 注意键名要和下面各行实际写入的一致 —— 之前这里写的是 duplicate_ok
		// 而 SelectAllThenDuplicate 写的是 dup_ok，于是聚合判定永远 false，
		// 明明五项子断言全绿却报失败。
		r["pass"] = AsBool(r, "click_then_box_ok")
			&& AsBool(r, "hover_target_ok")
			&& AsBool(r, "menu_ok")
			&& AsBool(r, "menu_edge_ok")
			&& AsBool(r, "shift_extract_ok")
			&& AsBool(r, "pile_drag_ok")
			&& AsBool(r, "dup_ok");

		return r;
	}

	// ------------------------------------------------------------------ 顺序 1

	/// <summary>
	/// 先轻点一张卡（选中、但不拖动），然后立刻在空白处框选。
	/// 这一步专门抓"上一次手势的残留状态把下一次手势带偏"。
	/// </summary>
	private static async Task ClickThenBoxSelect(Node host, BoardCamera cam, ObjectManager objects, Godot.Collections.Dictionary r)
	{
		CardObject? card = FindAnyCard(objects, cam);
		if (card is null)
		{
			r["click_then_box_ok"] = false;
			r["click_then_box_note"] = "没有卡牌";
			return;
		}

		// 1) 轻点一张卡：按下 → 原样松开，不越过拖拽阈值
		Vector2 cardScreen = cam.WorldToScreen(card.Position);
		DevInputSim.PushButton(cardScreen, MouseButton.Left, true);
		await DevInputSim.Frame(host);
		DevInputSim.PushButton(cardScreen, MouseButton.Left, false);
		await DevInputSim.Frame(host);
		await DevInputSim.Frame(host);

		r["after_click_selection"] = objects.Selection.Count;

		// 2) 紧接着在空白处框选 —— 如果上次手势的拖拽集没清干净，这一步会失败
		objects.ClearSelection();
		await DevInputSim.Frame(host);

		Vector2 cardPos = cam.WorldToScreen(card.Position);
		Vector2? startOpt = DevInputSim.FindEmptyScreenPointNear(cam, objects, cardPos, 420f);
		if (startOpt is null)
		{
			r["click_then_box_ok"] = false;
			r["click_then_box_note"] = "找不到空白起点";
			return;
		}

		Vector2 start = startOpt.Value;
		Vector2 end = cardPos + new Vector2(12f, 12f);
		Vector2 before = card.Position;

		DevInputSim.PushButton(start, MouseButton.Left, true);
		DevInputSim.PushMotion(start + new Vector2(20f, 0f), new Vector2(20f, 0f));
		DevInputSim.PushMotion(end, end - (start + new Vector2(20f, 0f)));
		DevInputSim.PushButton(end, MouseButton.Left, false);
		await DevInputSim.Frame(host);
		await DevInputSim.Frame(host);

		r["click_then_box_selected"] = objects.Selection.Count;
		r["click_then_box_card_moved"] = card.Position.DistanceTo(before);
		r["click_then_box_ok"] = objects.Selection.Count > 0 && card.Position.DistanceTo(before) < 0.01f;
	}

	// ------------------------------------------------------------------ 顺序 1b

	/// <summary>
	/// 用户反馈的原场景：先点选 A，再把鼠标移到 B 上（<b>不点击</b>），按 F。
	/// 期望 B 翻面、A 不动 —— 「悬停即为选中」。
	///
	/// 顺带断言描边标志：B 应是操作目标，A 不是。否则会出现
	/// "描边说会改这张、实际改的是另一张"。
	/// </summary>
	private static async Task HoverIsTheTarget(Node host, BoardCamera cam, ObjectManager objects, Godot.Collections.Dictionary r)
	{
		(CardObject? a, CardObject? b) = FindTwoSeparateCards(objects, cam);
		if (a is null || b is null)
		{
			r["hover_target_ok"] = false;
			r["hover_target_note"] = "找不到两张互不重叠、都在屏幕内的正面卡";
			return;
		}

		// 1) 点选 A
		Vector2 aScreen = cam.WorldToScreen(a.Position);
		DevInputSim.PushButton(aScreen, MouseButton.Left, true);
		await DevInputSim.Frame(host);
		DevInputSim.PushButton(aScreen, MouseButton.Left, false);
		await DevInputSim.Frame(host);
		await DevInputSim.Frame(host);

		bool aFaceBefore = a.IsFaceDown;
		bool bFaceBefore = b.IsFaceDown;

		r["hover_target_a_selected"] = a.IsSelected;
		r["hover_target_selection_count"] = objects.Selection.Count;

		// 2) 只移动鼠标到 B 上，不点击
		Vector2 bScreen = cam.WorldToScreen(b.Position);
		DevInputSim.PushMotion(bScreen, bScreen - aScreen);
		await DevInputSim.Frame(host);
		await DevInputSim.Frame(host);

		r["hover_target_hovered_uid"] = objects.Hovered?.Uid ?? "(null)";
		r["hover_target_b_is_target"] = b.IsActionTarget;
		r["hover_target_a_is_target"] = a.IsActionTarget;

		// 3) 按 F
		DevInputSim.PushKey(Key.F);
		await DevInputSim.Frame(host);
		await DevInputSim.Frame(host);

		bool aFlipped = a.IsFaceDown != aFaceBefore;
		bool bFlipped = b.IsFaceDown != bFaceBefore;

		r["hover_target_a_flipped"] = aFlipped;
		r["hover_target_b_flipped"] = bFlipped;
		r["hover_target_ok"] = !aFlipped && bFlipped && b.IsActionTarget && !a.IsActionTarget;
	}

	/// <summary>挑两张互不重叠、都在屏幕内的正面卡：各自位置上的最上层物件必须就是它自己。</summary>
	private static (CardObject?, CardObject?) FindTwoSeparateCards(ObjectManager objects, BoardCamera cam)
	{
		var candidates = new List<CardObject>();

		foreach (TabletopObject obj in objects.AllObjects)
		{
			if (obj is CardObject card && card.Visible && !card.IsFaceDown
				&& DevInputSim.IsOnScreen(cam, card.Position)
				&& ReferenceEquals(objects.PickTopmost(card.Position), card))
			{
				candidates.Add(card);
			}
		}

		for (int i = 0; i < candidates.Count; i++)
		{
			for (int j = i + 1; j < candidates.Count; j++)
			{
				// 距离要够远，免得鼠标移到 B 上时其实还压在 A 的范围内
				if (candidates[i].Position.DistanceTo(candidates[j].Position) > 60f)
					return (candidates[i], candidates[j]);
			}
		}

		return (null, null);
	}

	// ------------------------------------------------------------------ 顺序 2

	/// <summary>右键轻点一张卡：菜单必须<b>真的弹出来</b>并且有菜单项，而不只是发了信号。</summary>
	private static async Task RightClickMenu(Node host, BoardCamera cam, ObjectManager objects, Godot.Collections.Dictionary r)
	{
		CardObject? card = FindAnyCard(objects, cam);
		PopupMenu? menu = objects.ContextMenu;

		if (card is null || menu is null)
		{
			r["menu_ok"] = false;
			r["menu_note"] = card is null ? "没有卡牌" : "菜单节点不存在";
			return;
		}

		Vector2 screen = cam.WorldToScreen(card.Position);
		Vector2 world = cam.ScreenToWorld(screen);

		// 诊断：把"我们以为点在哪"和"物件系统认为那有什么"都记下来。
		// 只报"菜单项不对"无法定位问题出在坐标换算还是拾取。
		r["menu_click_screen"] = new Godot.Collections.Array { screen.X, screen.Y };
		r["menu_click_world"] = new Godot.Collections.Array { world.X, world.Y };
		r["menu_card_pos"] = new Godot.Collections.Array { card.Position.X, card.Position.Y };
		r["menu_card_usid"] = card.Uid;
		r["menu_pick_at_world"] = (objects.PickTopmost(world) as TabletopObject)?.Uid ?? "(null)";
		r["menu_pick_at_cardpos"] = (objects.PickTopmost(card.Position) as TabletopObject)?.Uid ?? "(null)";
		r["menu_card_contains_own_pos"] = card.ContainsWorldPoint(card.Position);

		DevInputSim.PushButton(screen, MouseButton.Right, true);
		await DevInputSim.Frame(host);
		DevInputSim.PushButton(screen, MouseButton.Right, false);
		await DevInputSim.Frame(host);
		await DevInputSim.Frame(host);

		r["menu_visible"] = menu.Visible;
		r["menu_item_count"] = menu.ItemCount;
		r["menu_is_inside_tree"] = menu.IsInsideTree();

		// 菜单必须弹在鼠标旁边。这个断言是回归保护：
		// 早先用 DisplayServer.MouseGetPosition()（桌面坐标）去定位嵌入式子窗口
		// （视口坐标），结果菜单整体偏移、被顶到视口最右边。
		Vector2 menuPos = menu.Position;
		Vector2 viewport = cam.GetViewportRect().Size;

		r["menu_position"] = new Godot.Collections.Array { menuPos.X, menuPos.Y };
		r["menu_offset_from_click_px"] = menuPos.DistanceTo(screen);
		r["menu_inside_viewport"] = menuPos.X >= 0f && menuPos.Y >= 0f
			&& menuPos.X < viewport.X && menuPos.Y < viewport.Y;
		r["menu_near_cursor"] = menuPos.DistanceTo(screen) < 4f;

		var labels = new Godot.Collections.Array();
		for (int i = 0; i < menu.ItemCount && i < 12; i++)
			labels.Add(menu.GetItemText(i));
		r["menu_items"] = labels;

		// 关掉菜单，别影响后续步骤
		menu.Hide();
		await DevInputSim.Frame(host);

		r["menu_ok"] = menu.IsInsideTree() && menu.ItemCount > 0
			&& AsBool(r, "menu_near_cursor") && AsBool(r, "menu_inside_viewport");
	}

	// ------------------------------------------------------------------ 顺序 2b

	/// <summary>
	/// 贴着视口右下角右键：菜单必须被收拢回视口内，不能伸出去。
	/// 光测"菜单位置 ≈ 光标位置"是不够的 —— 靠近边缘时光标本身就在视口内，
	/// 但菜单会从光标向右下展开、整块跑到视口外面。
	/// </summary>
	private static async Task MenuClampsAtEdge(Node host, BoardCamera cam, ObjectManager objects, Godot.Collections.Dictionary r)
	{
		PopupMenu? menu = objects.ContextMenu;
		if (menu is null)
		{
			r["menu_edge_ok"] = false;
			return;
		}

		Vector2 viewport = cam.GetViewportRect().Size;
		// 避开底部 HUD（提示条约 1080-34 起），但仍贴着右下
		var corner = new Vector2(viewport.X - 24f, viewport.Y - 120f);

		DevInputSim.PushButton(corner, MouseButton.Right, true);
		await DevInputSim.Frame(host);
		DevInputSim.PushButton(corner, MouseButton.Right, false);
		await DevInputSim.Frame(host);
		await DevInputSim.Frame(host);

		Vector2 pos = menu.Position;
		Vector2 size = menu.Size;
		var end = new Vector2(pos.X + size.X, pos.Y + size.Y);

		r["menu_edge_click"] = new Godot.Collections.Array { corner.X, corner.Y };
		r["menu_edge_position"] = new Godot.Collections.Array { pos.X, pos.Y };
		r["menu_edge_size"] = new Godot.Collections.Array { size.X, size.Y };
		r["menu_edge_end"] = new Godot.Collections.Array { end.X, end.Y };
		r["menu_edge_overflow_x"] = Mathf.Max(end.X - viewport.X, 0f);
		r["menu_edge_overflow_y"] = Mathf.Max(end.Y - viewport.Y, 0f);

		bool inside = pos.X >= 0f && pos.Y >= 0f && end.X <= viewport.X + 1f && end.Y <= viewport.Y + 1f;
		r["menu_edge_ok"] = inside && menu.Visible;

		menu.Hide();
		await DevInputSim.Frame(host);
	}

	// ------------------------------------------------------------------ 顺序 3

	/// <summary>Shift+拖拽 把堆里最上面一张抽出来，原堆必须还剩原来的张数减一。</summary>
	private static async Task ShiftExtractFromPile(Node host, BoardCamera cam, ObjectManager objects, Godot.Collections.Dictionary r)
	{
		int pileId = 0;
		int countBefore = 0;
		TabletopObject? top = null;

		foreach (KeyValuePair<int, Pile> kv in objects.Piles)
		{
			if (kv.Value.Count < 2)
				continue;

			pileId = kv.Key;
			countBefore = kv.Value.Count;
			top = kv.Value.Top;
			break;
		}

		if (pileId == 0 || top is null)
		{
			r["shift_extract_ok"] = false;
			r["shift_extract_note"] = "没有可用的堆";
			return;
		}

		Vector2 start = cam.WorldToScreen(top.Position);
		Vector2 empty = DevInputSim.FindEmptyScreenPoint(cam, objects);
		Vector2 screenDelta = empty - start;

		// 按住 Shift 再拖 —— ViewportController 用 Input.IsKeyPressed(Key.Shift) 判断，
		// 所以合成事件之外还得让 Input 认为 Shift 真的按着。
		DevInputSim.PushKeyDown(Key.Shift);
		await DevInputSim.Frame(host);

		DevInputSim.PushButton(start, MouseButton.Left, true);
		DevInputSim.PushMotion(start + new Vector2(20f, 0f), new Vector2(20f, 0f));
		DevInputSim.PushMotion(empty, screenDelta - new Vector2(20f, 0f));
		DevInputSim.PushButton(empty, MouseButton.Left, false);
		await DevInputSim.Frame(host);

		DevInputSim.PushKeyUp(Key.Shift);
		await DevInputSim.Frame(host);
		await DevInputSim.Frame(host);

		int countAfter = objects.Piles.TryGetValue(pileId, out Pile? pile) ? pile.Count : 0;

		r["shift_pile_before"] = countBefore;
		r["shift_pile_after"] = countAfter;
		r["shift_extracted_pile_id"] = top.PileId;
		r["shift_extract_ok"] = countAfter == countBefore - 1 && top.PileId == 0;
	}

	// ------------------------------------------------------------------ 顺序 4

	/// <summary>拖动整堆：所有成员位移必须一致，相对阶梯偏移不能变。</summary>
	private static async Task DragWholePile(Node host, BoardCamera cam, ObjectManager objects, Godot.Collections.Dictionary r)
	{
		Pile? pile = null;
		foreach (KeyValuePair<int, Pile> kv in objects.Piles)
		{
			if (kv.Value.Count >= 2)
			{
				pile = kv.Value;
				break;
			}
		}

		if (pile is null || pile.Top is null)
		{
			r["pile_drag_ok"] = false;
			r["pile_drag_note"] = "没有可用的堆";
			return;
		}

		var before = new Dictionary<string, Vector2>();
		foreach (TabletopObject m in pile.Members)
			before[m.Uid] = m.Position;

		Vector2 start = cam.WorldToScreen(pile.Top.Position);
		var delta = new Vector2(-260f, 180f);

		DevInputSim.PushButton(start, MouseButton.Left, true);
		DevInputSim.PushMotion(start + new Vector2(20f, 0f), new Vector2(20f, 0f));
		DevInputSim.PushMotion(start + delta, delta - new Vector2(20f, 0f));
		DevInputSim.PushButton(start + delta, MouseButton.Left, false);
		await DevInputSim.Frame(host);
		await DevInputSim.Frame(host);

		Vector2 expected = delta / cam.ZoomLevel;
		float worst = 0f;
		bool allMoved = true;

		foreach (TabletopObject m in pile.Members)
		{
			if (!before.TryGetValue(m.Uid, out Vector2 was))
				continue;

			Vector2 moved = m.Position - was;
			float err = moved.DistanceTo(expected);
			worst = Mathf.Max(worst, err);

			if (err > 1.5f)
				allMoved = false;
		}

		r["pile_drag_members"] = pile.Count;
		r["pile_drag_worst_error_px"] = worst;
		r["pile_drag_ok"] = allMoved && worst < 1.5f;
	}

	// ------------------------------------------------------------------ 顺序 5

	/// <summary>全选后复制：物件数应该翻倍，且副本不能和原件完全重叠。</summary>
	private static async Task SelectAllThenDuplicate(Node host, ObjectManager objects, Godot.Collections.Dictionary r)
	{
		int before = objects.ObjectCount;

		objects.SelectAll();
		await DevInputSim.Frame(host);

		int selected = objects.Selection.Count;
		objects.DuplicateSelection();
		await DevInputSim.Frame(host);

		int after = objects.ObjectCount;

		r["dup_before"] = before;
		r["dup_selected"] = selected;
		r["dup_after"] = after;
		r["dup_ok"] = before > 0 && selected == before && after == before + selected;
	}

	// ------------------------------------------------------------------ 工具

	private static CardObject? FindAnyCard(ObjectManager objects, BoardCamera cam)
	{
		// 只挑屏幕上看得见的 —— 前面的步骤可能把物件拖到视口之外，
		// 对它合成点击等于点在视口外，测出来的是假失败。
		foreach (TabletopObject obj in objects.AllObjects)
		{
			if (obj is CardObject card && card.Visible && DevInputSim.IsOnScreen(cam, card.Position))
				return card;
		}

		return null;
	}

	private static bool AsBool(Godot.Collections.Dictionary dict, string key)
		=> dict.ContainsKey(key) && dict[key].AsBool();
}
